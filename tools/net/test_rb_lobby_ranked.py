#!/usr/bin/env python3
"""rb_lobby.c quick matches with a recorded result (rb_ranked.c, lobby 1.5), with fake games:
  - MATCH op 3 needs an account (a lobby without tickets answers state 7), pairs two DIFFERENT accounts, both get the same game id;
  - the same account twice is not paired; op 2 and op 3 searchers are not paired together;
  - both RESULTs agree -> "recorded" (VERDICT to both, one JSON line with the winner's account id and no internet address);
  - the RESULTs disagree -> no contest; the PADS checksums differ (seen by the lobby) -> no contest (desync);
  - one game stops sending PADS for 10 s while the other keeps playing -> forfeit, pushed to the one still playing;
  - one normal report and the other game gone -> recorded after 20 s; a RESULT for an unknown game -> void;
  - a match that never started (no PADS) and is cancelled is not written.
    LOBBY_EXE=./rb_lobby python3 test_rb_lobby_ranked.py      (about 1 minute)"""
import hashlib
import hmac
import json
import os
import struct
import subprocess
import sys
import tempfile
import time
import uuid

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ssbu_lobby as L  # noqa: E402
from test_ssbu_lobby import Fake, free_port  # noqa: E402
from test_rb_lobby_ticket import ticket  # noqa: E402

RESULT, VERDICT = 22, 23


class Player(Fake):
    def __init__(self, lobby, name, secret=None, uid=None, build=b"build-rk"):
        super().__init__(lobby, name, build=build, mods=bytes([0]), code=b"public")
        self.uid = uid or str(uuid.uuid4())
        self.tk = ticket(secret, name, uid=self.uid) if secret else None

    def join(self):
        if self.tk is None:
            return super().join()
        port = self.s.getsockname()[1]
        t = self.tk.encode()
        self.tx(L.JOIN, self.nonce + self.code.ljust(32, b"\0") + self.name.encode().ljust(32, b"\0") + self.build.ljust(40, b"\0")
                + struct.pack(">IHB", 0x7F000001, port, 1) + b"MS" + self.mods + b"TK" + struct.pack(">H", len(t)) + t)
        b = self.rx(L.WELCOME)
        self.ip, self.cookie = struct.unpack_from(">IQ", b, 8)
        return self.ip

    def state_gid(self, want, secs=3.0):
        end = time.time() + secs
        while time.time() < end:
            try:
                b = self.rx(L.MATCH_STATE, 0.3)
            except AssertionError:
                continue
            if b[4] == want:
                assert len(b) == 57, len(b)
                return b, struct.unpack_from(">Q", b, 49)[0]
        raise AssertionError("%s: no MATCH_STATE %d" % (self.name, want))

    def pads(self, peer_ip, player, newest, check_frame=0, check=0):
        body = struct.pack("<BBHiiiI", 1, player, 1, newest, 0, check_frame, check) + struct.pack("<Qhhhh", 0, 0, 0, 0, 0) + bytes([0x11, 2])
        self.tx(L.RB, struct.pack(">QI", self.cookie, peer_ip) + body)

    def result(self, gid, side, winner, end=1, frames=3000, agree_f=2990, agree_ck=0xABCD, desyncs=0):
        body = struct.pack(">QBQBBBBBHHIIIIH", self.cookie, 1, gid, side, winner, end, 2, 0, 455, 1020, frames, 0x1234, agree_f, agree_ck, desyncs)
        body += struct.pack(">QQQB", 0xC100000F00000001, 0xC100000F00000002, 0x6900000A00000003, 1)
        self.tx(RESULT, body)

    def verdict(self, gid, secs=3.0, final=True):
        end = time.time() + secs
        while time.time() < end:
            try:
                b = self.rx(VERDICT, 0.3)
            except AssertionError:
                continue
            g, ver, v, w, why = struct.unpack_from(">QBBBB", b)
            if g == gid and (v != 0 or not final):
                return v, w, b[12:76].rstrip(b"\0").decode()
        raise AssertionError("%s: no VERDICT for %x" % (self.name, gid))


def pair(lob, secret, a_name, b_name, sid):
    a, b = Player(lob, a_name, secret), Player(lob, b_name, secret)
    a.join(); b.join()
    a.match(3, sid); time.sleep(0.1); b.match(3, sid + 1)
    _, ga = a.state_gid(L.MS_MATCHED if hasattr(L, "MS_MATCHED") else 2)
    _, gb = b.state_gid(2)
    assert ga and ga == gb, (ga, gb)
    return a, b, ga


def play(a, b, frames=(1, 2, 3)):
    for f in frames:
        a.pads(b.ip, 0, f, f - 1 if f > 1 else 0, 0x100 + f)
        b.pads(a.ip, 1, f, f - 1 if f > 1 else 0, 0x100 + f)
        time.sleep(0.02)


def lines(path):
    with open(path) as f:
        return [json.loads(x) for x in f if x.strip()]


def main():
    exe = os.environ.get("LOBBY_EXE", "./rb_lobby")
    secret = os.urandom(32).hex()
    sf = tempfile.NamedTemporaryFile("w", suffix=".secret", delete=False); sf.write(secret + "\n"); sf.close()
    res = tempfile.mktemp(suffix=".jsonl")
    port, port2 = free_port(), free_port()
    log = tempfile.TemporaryFile()
    proc = subprocess.Popen([exe, "--port", str(port), "--bind", "127.0.0.1", "--ticket-secret-file", sf.name, "--results-file", res, "--stats", "0"],
                            stdout=log, stderr=subprocess.STDOUT)
    proc2 = subprocess.Popen([exe, "--port", str(port2), "--bind", "127.0.0.1", "--stats", "0"], stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)
    try:
        time.sleep(0.3)
        lob, lob2 = ("127.0.0.1", port), ("127.0.0.1", port2)
        # no accounts on this lobby: refused with state 7
        n = Player(lob2, "NoAccount"); n.join(); n.match(3, 7)
        b7, _ = n.state_gid(7)
        print("op 3 without an account: state 7")
        # the same account twice: not paired; op 2 with op 3: not paired
        uid = str(uuid.uuid4())
        s1, s2 = Player(lob, "Same1", secret, uid), Player(lob, "Same2", secret, uid)
        s1.join(); s2.join(); s1.match(3, 100); s2.match(3, 200)
        s1.state_gid(1); s2.state_gid(1)
        o2 = Player(lob, "Op2", secret); o2.join(); o2.match(2, 300)
        time.sleep(0.5)
        for p in (s1, s2):
            p.match(3, 100 if p is s1 else 200)
            b, _ = p.state_gid(1)
        s1.match(0, 101); s2.match(0, 201); o2.match(0, 301)
        print("same account twice / op 2 with op 3: not paired")
        # 1. agreed results -> recorded
        a, b, gid = pair(lob, secret, "Alice", "Bob", 1000)
        play(a, b)
        a.result(gid, 0, 1); v = a.verdict(gid, final=False); assert v[0] == 0, v
        b.result(gid, 1, 1)
        va, vb = a.verdict(gid), b.verdict(gid)
        assert va[0] == 1 and va[1] == 1 and vb == va, (va, vb)
        print("both agree: recorded,", va)
        # 2. disagree -> no contest
        a, b, gid2 = pair(lob, secret, "Carol", "Dave", 2000)
        play(a, b)
        a.result(gid2, 0, 0); b.result(gid2, 1, 1)
        assert a.verdict(gid2)[0] == 3 and b.verdict(gid2)[0] == 3
        print("disagree: no contest")
        # 3. desync seen by the lobby -> no contest
        a, b, gid3 = pair(lob, secret, "Erin", "Frank", 3000)
        play(a, b)
        a.pads(b.ip, 0, 10, 9, 0x5555); b.pads(a.ip, 1, 10, 9, 0x6666)
        a.result(gid3, 0, 0); b.result(gid3, 1, 0)
        v = a.verdict(gid3); assert v[0] == 3 and "desync" in v[2], v
        print("desync: no contest,", v)
        # 4. forfeit: A goes silent, B keeps playing
        a, b, gid4 = pair(lob, secret, "Gina", "Hank", 4000)
        play(a, b)
        t0 = time.time(); f = 4
        while time.time() - t0 < 14:
            b.pads(a.ip, 1, f, f - 1, 0x100 + f); f += 1; b.ping(); time.sleep(0.1)
            try:
                v = b.verdict(gid4, 0.05)
                break
            except AssertionError:
                v = None
        assert v and v[0] == 2 and v[1] == 1, v
        print("forfeit after %.1f s:" % (time.time() - t0), v)
        # 5. one normal report, the other game gone (no pings): recorded after 20 s
        a, b, gid5 = pair(lob, secret, "Ivy", "Jack", 5000)
        play(a, b)
        b.tx(L.LEAVE, struct.pack(">Q", b.cookie))
        a.result(gid5, 0, 0)
        t0 = time.time(); v = None
        while time.time() - t0 < 30 and not v:
            a.ping(); a.result(gid5, 0, 0)
            try:
                v = a.verdict(gid5, 1.0)
            except AssertionError:
                pass
        assert v and v[0] == 1 and v[1] == 0, v
        print("one report, the other game left: recorded after %.1f s" % (time.time() - t0))
        # 6. unknown game id -> void; 7. never started + cancel -> not written
        a.result(0x1234567, 0, 0); assert a.verdict(0x1234567)[0] == 4
        c, d, gid7 = pair(lob, secret, "Kim", "Lee", 7000)
        c.match(0, 7001)
        time.sleep(1.0)
        got = lines(res)
        ids = [x["id"] for x in got]
        assert str(gid) in ids and str(gid2) in ids and str(gid3) in ids and str(gid4) in ids and str(gid5) in ids and str(gid7) not in ids, ids
        r1 = next(x for x in got if x["id"] == str(gid))
        assert r1["result"] == "recorded" and r1["winner"] == r1["p2"] and r1["p1_name"] == "Alice" and r1["p1_fighter"] == "c100000f00000001", r1
        r4 = next(x for x in got if x["id"] == str(gid4))
        assert r4["result"] == "forfeit" and r4["winner"] == r4["p2"], r4
        text = open(res).read()
        assert "127.0.0.1" not in text, text
        log.seek(0); lt = log.read().decode(errors="replace")
        assert "127.0.0.1:" not in lt.replace("UDP 127.0.0.1:%d" % port, ""), lt
        print("results file: %d lines, no internet address" % len(got))
        print("rb_lobby quick matches: ok")
        return 0
    finally:
        for p in (proc, proc2):
            p.terminate(); p.wait(5)
        os.unlink(sf.name)
        if os.environ.get("KEEP_RESULTS"):
            print("results:", res)
        elif os.path.exists(res):
            os.unlink(res)


if __name__ == "__main__":
    sys.exit(main())
