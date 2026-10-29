#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""SSBU Arena replays, the VPS side after the lobby: every .ssbr the lobby wrote (rb_lobby --replay-dir <dir>: <dir>/pending)
is compressed (raw deflate body, codec 1) and sent to the Supabase Edge Function replay-ingest with the shared ingest
secret; sent files move to <dir>/sent (kept SENT_DAYS days as a local copy), files the function refuses (400) to <dir>/bad.
Once a day it asks the function to prune replays older than --days (default 30) and beyond the newest --keep (5000).
No inbound port, no service key on the VPS: only the ingest secret (root-only file).

    replay_uploader.py --dir /var/lib/ssbu-rblobby/replays --secret-file /etc/ssbu-rblobby/replay_ingest_secret
                       [--url https://<project>.supabase.co/functions/v1/replay-ingest] [--once] [--days 30] [--keep 5000]
"""
import argparse
import json
import os
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ssbr  # noqa: E402

DEFAULT_URL = "https://gnufjvqwynxmudpgrded.supabase.co/functions/v1/replay-ingest"
SENT_DAYS = 7


def log(msg):
    print(time.strftime("%H:%M:%S ") + msg, flush=True)


def post(url, secret, body, ctype="application/octet-stream"):
    req = urllib.request.Request(url, data=body, method="POST", headers={"x-replay-secret": secret, "Content-Type": ctype})
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            return r.status, r.read().decode(errors="replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors="replace")
    except (urllib.error.URLError, OSError) as e:
        return 0, str(e)


def send_one(path, a, secret):
    name = os.path.basename(path)
    stem = name[:-5]
    try:
        packed = ssbr.load(path).packed()
    except (ValueError, OSError) as e:
        log("replay %s: unreadable (%s): moved to bad/" % (stem, e))
        os.replace(path, os.path.join(a.dir, "bad", name))
        return
    code, text = post(a.url + "?id=" + stem, secret, packed)
    if code == 200:
        os.replace(path, os.path.join(a.dir, "sent", name))
        log("replay %s: sent (%d bytes)" % (stem, len(packed)))
    elif code in (400, 413):
        os.replace(path, os.path.join(a.dir, "bad", name))
        log("replay %s: refused by the server (%d %s): moved to bad/" % (stem, code, text[:200]))
    else:
        log("replay %s: not sent now (%d %s): next try later" % (stem, code, text[:200]))


def cleanup(a):
    now = time.time()
    for sub, days in (("sent", SENT_DAYS), ("bad", 30)):
        d = os.path.join(a.dir, sub)
        for n in os.listdir(d):
            p = os.path.join(d, n)
            if os.path.isfile(p) and now - os.path.getmtime(p) > days * 86400:
                os.unlink(p)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--dir", required=True)
    ap.add_argument("--secret-file", required=True)
    ap.add_argument("--url", default=DEFAULT_URL)
    ap.add_argument("--once", action="store_true")
    ap.add_argument("--days", type=int, default=30)
    ap.add_argument("--keep", type=int, default=5000)
    ap.add_argument("--every", type=float, default=10.0)
    a = ap.parse_args()
    with open(a.secret_file) as f:
        secret = f.read().strip()
    for sub in ("pending", "sent", "bad"):
        os.makedirs(os.path.join(a.dir, sub), exist_ok=True)
    log("replay uploader: %s/pending -> %s" % (a.dir, a.url))
    last_prune = 0.0
    while True:
        pend = os.path.join(a.dir, "pending")
        for n in sorted(os.listdir(pend)):
            if n.endswith(".ssbr") and not n.startswith("."):
                send_one(os.path.join(pend, n), a, secret)
        if time.time() - last_prune > 86400:
            last_prune = time.time()
            code, text = post(a.url + "?action=prune&days=%d&keep=%d" % (a.days, a.keep), secret, b"{}", "application/json")
            log("prune: %d %s" % (code, text[:200]))
            cleanup(a)
        if a.once:
            return 0
        time.sleep(a.every)


if __name__ == "__main__":
    sys.exit(main())
