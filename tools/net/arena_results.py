#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""arena_results.py - the VPS writer of SSBU Arena quick-match results: follows the lobby's results file (rb_lobby --results-file,
one JSON line per decided match, tools/net/rb_ranked.c) and writes each line to the accounts database (Supabase public.matches).
Runs as its own systemd service (deploy: tools/net/deploy_results.sh); the lobby never talks HTTP.

The write key is read from a root-only file (systemd LoadCredential), never from the repo or a client:
  - a Supabase service-role key (sb_secret_... or a JWT): POST /rest/v1/matches (the service role bypasses RLS), or
  - the results secret (64 hex characters, the same text as private.results_secret): POST /rest/v1/rpc/record_match with an
    HMAC-SHA256 signature of the payload (public.record_match checks it; without the secret nobody can write).
Idempotent: the match id is the primary key (duplicates ignored), so a restart may send a line twice. The position in the file
is kept in --state. Lines whose write is refused (4xx) are logged and skipped; network errors are retried every 10 s.
No internet address is in the results file or sent anywhere.

    arena_results.py --results /var/lib/ssbu-rblobby/results.jsonl --key-file $CREDENTIALS_DIRECTORY/key --state /var/lib/ssbu-arena-results/offset
    arena_results.py --self-test        (offline: payload, names, signature)"""
import argparse
import hashlib
import hmac
import json
import os
import sys
import time
import urllib.error
import urllib.request
import zlib

URL = "https://gnufjvqwynxmudpgrded.supabase.co"
PUBLISHABLE = "sb_publishable_v5DkzLiVBpTnDMy6BdpI7A_rqZSSQWz"       # (the clients' key: what it may do is decided by RLS)

FIGHTERS = {
    "mario": "Mario", "donkey": "Donkey Kong", "link": "Link", "samus": "Samus", "samusd": "Dark Samus", "yoshi": "Yoshi", "kirby": "Kirby",
    "fox": "Fox", "pikachu": "Pikachu", "luigi": "Luigi", "ness": "Ness", "captain": "Captain Falcon", "purin": "Jigglypuff", "peach": "Peach",
    "daisy": "Daisy", "koopa": "Bowser", "ice_climber": "Ice Climbers", "popo": "Ice Climbers", "nana": "Ice Climbers", "sheik": "Sheik",
    "zelda": "Zelda", "mariod": "Dr. Mario", "pichu": "Pichu", "falco": "Falco", "marth": "Marth", "lucina": "Lucina", "younglink": "Young Link",
    "ganon": "Ganondorf", "mewtwo": "Mewtwo", "roy": "Roy", "chrom": "Chrom", "gamewatch": "Mr. Game & Watch", "metaknight": "Meta Knight",
    "pit": "Pit", "pitb": "Dark Pit", "szerosuit": "Zero Suit Samus", "wario": "Wario", "snake": "Snake", "ike": "Ike",
    "ptrainer": "Pokemon Trainer", "ptrainer_low": "Pokemon Trainer", "pzenigame": "Squirtle", "pfushigisou": "Ivysaur", "plizardon": "Charizard",
    "diddy": "Diddy Kong", "lucas": "Lucas", "sonic": "Sonic", "dedede": "King Dedede", "pikmin": "Olimar", "lucario": "Lucario", "robot": "R.O.B.",
    "toonlink": "Toon Link", "wolf": "Wolf", "murabito": "Villager", "rockman": "Mega Man", "wiifit": "Wii Fit Trainer", "rosetta": "Rosalina & Luma",
    "littlemac": "Little Mac", "gekkouga": "Greninja", "palutena": "Palutena", "pacman": "Pac-Man", "reflet": "Robin", "shulk": "Shulk",
    "koopajr": "Bowser Jr.", "duckhunt": "Duck Hunt", "ryu": "Ryu", "ken": "Ken", "cloud": "Cloud", "kamui": "Corrin", "bayonetta": "Bayonetta",
    "inkling": "Inkling", "ridley": "Ridley", "simon": "Simon", "richter": "Richter", "krool": "King K. Rool", "shizue": "Isabelle",
    "gaogaen": "Incineroar", "packun": "Piranha Plant", "jack": "Joker", "brave": "Hero", "buddy": "Banjo & Kazooie", "dolly": "Terry",
    "master": "Byleth", "tantan": "Min Min", "pickel": "Steve", "edge": "Sephiroth", "eflame": "Pyra", "elight": "Mythra",
    "eflame_first": "Pyra", "elight_first": "Mythra", "eflame_only": "Pyra", "elight_only": "Mythra", "element": "Pyra / Mythra",
    "demon": "Kazuya", "trail": "Sora", "miifighter": "Mii Brawler", "miiswordsman": "Mii Swordfighter", "miigunner": "Mii Gunner",
    "random": "Random",
}
STAGES = {"battle_field": "Battlefield", "battle_field_s": "Small Battlefield", "end": "Final Destination", "poke_stadium2": "Pokemon Stadium 2",
          "animal_village": "Smashville", "animal_city": "Town and City", "random": "Random"}


def hash40(s):
    b = s.encode()
    return (len(b) << 32) | (zlib.crc32(b) & 0xFFFFFFFF)


NAME_OF = {}
for k, v in FIGHTERS.items():
    NAME_OF[hash40("ui_chara_" + k)] = v
for k, v in STAGES.items():
    NAME_OF[hash40("ui_stage_" + k)] = v


def label(hexid):
    """'c1000009...' (the game's ui id: 8-bit tag, 40-bit hash40) -> a name, else the hex id"""
    if not hexid:
        return None
    try:
        return NAME_OF.get(int(hexid, 16) & 0xFFFFFFFFFF, hexid)
    except ValueError:
        return None


def payload(line):
    """a results-file record -> the matches row (as JSON text, fixed key order)"""
    j = json.loads(line)
    row = {
        "id": int(j["id"]), "played_at": j["played_at"], "decided_at": j.get("decided_at"),
        "p1": j["p1"] or None, "p2": j["p2"] or None, "p1_name": j["p1_name"], "p2_name": j["p2_name"],
        "p1_fighter": label(j.get("p1_fighter")), "p2_fighter": label(j.get("p2_fighter")), "stage": label(j.get("stage")),
        "p1_stocks": j.get("p1_stocks"), "p2_stocks": j.get("p2_stocks"), "winner": j.get("winner"),
        "result": j["result"], "reason": j.get("reason"), "frames": j.get("frames"), "build": j.get("build"), "region": j.get("region", "eu"),
    }
    if row["id"] <= 0 or row["result"] not in ("recorded", "forfeit", "no_contest", "void"):
        raise ValueError("not a match record")
    return json.dumps(row, separators=(",", ":"), sort_keys=True)


def post(path, body, key, extra=None):
    rq = urllib.request.Request(URL + path, data=body.encode(), method="POST")
    rq.add_header("apikey", key)
    rq.add_header("Content-Type", "application/json")
    if key.startswith("eyJ"):
        rq.add_header("Authorization", "Bearer " + key)
    for k, v in (extra or {}).items():
        rq.add_header(k, v)
    with urllib.request.urlopen(rq, timeout=20) as r:
        return r.status


def write(row_json, secret):
    if secret.startswith("sb_secret_") or secret.startswith("eyJ"):             # the service role
        return post("/rest/v1/matches?on_conflict=id", "[" + row_json + "]", secret, {"Prefer": "resolution=ignore-duplicates,return=minimal"})
    sig = hmac.new(secret.encode(), row_json.encode(), hashlib.sha256).hexdigest()
    return post("/rest/v1/rpc/record_match", json.dumps({"payload": row_json, "sig": sig}), PUBLISHABLE)


def log(msg):
    print(time.strftime("%H:%M:%S"), msg, flush=True)


def run(a):
    with open(a.key_file) as f:
        secret = f.read().strip()
    if len(secret) < 32:
        log("the key file holds no key")
        return 2
    try:
        with open(a.state) as f:
            pos = int(f.read().strip() or 0)
    except (OSError, ValueError):
        pos = 0
    log("following %s from byte %d (%s)" % (a.results, pos, "service role" if secret.startswith(("sb_secret_", "eyJ")) else "signed rpc record_match"))
    while True:
        try:
            size = os.path.getsize(a.results)
        except OSError:
            size = -1
        if 0 <= size < pos:
            log("the results file is shorter than before (replaced): from the start")
            pos = 0
        if size > pos:
            with open(a.results, "rb") as f:
                f.seek(pos)
                data = f.read()
            done = 0
            for raw in data.split(b"\n")[:-1]:                 # complete lines only
                n = len(raw) + 1
                line = raw.decode("utf-8", "replace").strip()
                if line:
                    try:
                        row = payload(line)
                        write(row, secret)
                        log("match %s written" % json.loads(row)["id"])
                    except urllib.error.HTTPError as e:
                        if e.code >= 500 or e.code == 429:
                            log("the database answered %d: retry later" % e.code)
                            break
                        log("match refused (%d): %s" % (e.code, e.read()[:200]))
                    except (urllib.error.URLError, TimeoutError, OSError) as e:
                        log("cannot reach the database (%s): retry later" % e)
                        break
                    except (ValueError, KeyError) as e:
                        log("a line that is not a match record skipped (%s)" % e)
                done += n
            if done:
                pos += done
                tmp = a.state + ".tmp"
                with open(tmp, "w") as f:
                    f.write(str(pos))
                os.replace(tmp, a.state)
        if a.once:
            return 0
        time.sleep(10 if size > pos else 3)


def self_test():
    assert label("c1000000%08x" % 0) is not None
    m = hash40("ui_chara_mario")
    assert label("%016x" % (0xC100000000000000 | m)) == "Mario", label("%016x" % (0xC100000000000000 | m))
    assert label("%016x" % (0x6900000000000000 | hash40("ui_stage_end"))) == "Final Destination"
    line = json.dumps({"v": 1, "id": "12345", "played_at": "2026-10-24T10:00:00Z", "decided_at": "2026-10-24T10:05:00Z", "p1": "a", "p2": "b",
                       "p1_name": "A", "p2_name": "B", "p1_fighter": "%016x" % (0xC100000000000000 | m), "p2_fighter": "c1000000deadbeef",
                       "stage": "690000000000aaaa", "p1_stocks": 1, "p2_stocks": 0, "winner": "a", "result": "recorded", "reason": "A won",
                       "frames": 9000, "build": "x"})
    row = json.loads(payload(line))
    assert row["p1_fighter"] == "Mario" and row["p2_fighter"] == "c1000000deadbeef" and row["id"] == 12345 and "ip" not in line
    print("arena_results self-test: ok")
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", default="/var/lib/ssbu-rblobby/results.jsonl")
    ap.add_argument("--key-file")
    ap.add_argument("--state", default="/var/lib/ssbu-arena-results/offset")
    ap.add_argument("--once", action="store_true", help="send what is there, then exit")
    ap.add_argument("--self-test", action="store_true")
    a = ap.parse_args()
    if a.self_test:
        return self_test()
    if not a.key_file:
        a.key_file = os.path.join(os.environ.get("CREDENTIALS_DIRECTORY", "/etc/ssbu-arena-results"), "key")
    return run(a)


if __name__ == "__main__":
    sys.exit(main())
