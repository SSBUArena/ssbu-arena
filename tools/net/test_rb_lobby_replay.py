#!/usr/bin/env python3
"""rb_lobby.c --replay-dir (SSBU Arena replays, rb_replay_rec.c): two signed-in games paired by the lobby's rollback search
play a match as the games do it on the wire (PROPOSE / ACCEPT, the host's match block, PADS with redundant copies, the
match tag and mode bytes, checksums); after the pads stop, one .ssbr file holds the agreed match, both players' pads of
every frame, the checksums, their names and account ids, and no internet address. A second match of the same pair is a
second file. Pads of an older match (another tag) and messages of other players are not taken.
    LOBBY_EXE=./rb_lobby python3 test_rb_lobby_replay.py"""
import glob
import os
import random
import struct
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import ssbu_lobby as L  # noqa: E402
import ssbr  # noqa: E402
from test_ssbu_lobby import Fake, free_port  # noqa: E402
from test_rb_lobby_ticket import ticket, join_raw  # noqa: E402

UID_A, UID_B = "11111111-2222-3333-4444-555555555555", "66666666-7777-8888-9999-000000000000"


def rb(f, dst, payload):
    f.tx(L.RB, struct.pack(">QI", f.cookie, dst) + payload)


def rbh(kind, nonce, echo, match):
    return struct.pack("<BBhIII", kind, 5, 0, 0x1234, nonce, echo) + match


def match_bytes(seed):
    m = bytearray(388)
    struct.pack_into("<QQ", m, 0, 0xC10000AABBCCDD01, 0xC10000AABBCCDD02)
    m[16], m[17] = 3, 5
    struct.pack_into("<QI", m, 0x40, 0x6900000011223344, 0)
    m[0x61], m[0x62] = 1, 3
    struct.pack_into("<Q", m, 0x68, seed)
    return bytes(m)


def pad(seed, player, f):
    r = random.Random(seed * 1000003 + player * 7919 + f // 5)
    return (r.randrange(0, 0x10000), r.choice([0, 0, 32767, -32767, 12000]), r.choice([0, -32767, 9000]), 0, r.choice([0, 20000]))


def pads_msg(player, newest, count, ack, cf, ck, tag, seed):
    b = struct.pack("<BBHiiiI", 1, player, count, newest, ack, cf, ck)
    for f in range(newest, newest - count, -1):
        p = pad(seed, player, f)
        b += struct.pack("<Qhhhh", p[0], p[1], p[2], p[3], p[4])
    return b + bytes([tag, 4])


def check(seed, f):
    return (f * 2654435761 ^ seed) & 0xFFFFFFFF or 1


def play(a, b, seed, frames, tag, desync_at=0):
    mid = (seed ^ (seed >> 32)) & 0xFFFFFFFF | 1
    m = match_bytes(seed)
    for _ in range(3):
        rb(a, b.ip, rbh(0x49, 0xA0A0 + seed % 1000, 0x5555, m))
        time.sleep(0.02)
    rb(b, a.ip, rbh(0x4A, 0x7777, 0xA0A0 + seed % 1000, m))
    rb(a, b.ip, rbh(0x49, 0xA0A0 + seed % 1000, 0x5555, m))      # (one more in flight after the ACCEPT)
    blk = bytes((i * 7) & 255 for i in range(0xA48))
    for off in range(0, 0xA48, 768):
        ln = min(768, 0xA48 - off)
        rb(a, b.ip, struct.pack("<BBhIIHHH", 0x4C, 5, 0, 0x1234, mid, off, ln, 0xA48) + blk[off:off + ln])
    rb(a, b.ip, pads_msg(0, 2000, 6, 1999, 1998, 77, tag ^ 0x55, seed))     # (the end of an older match, late: another tag)
    for f in range(1, frames + 1):
        for side, (src, dst) in enumerate(((a, b), (b, a))):
            cnt = min(f, 6)
            ck = check(seed, f - 2) if f > 2 else 0
            if side == 1 and desync_at and f - 2 >= desync_at:
                ck ^= 1
            if random.random() < 0.1:
                continue                                               # (a lost datagram: the next one repeats its pads)
            rb(src, dst.ip, pads_msg(side, f, cnt, f - 1, f - 2 if f > 2 else 0, ck, tag, seed))
        if f % 100 == 0:
            time.sleep(0.01)
    for side, (src, dst) in enumerate(((a, b), (b, a))):
        rb(src, dst.ip, pads_msg(side, frames, min(frames, 30), frames, frames, check(seed, frames), tag, seed))
    return mid, blk


def main():
    secret = os.urandom(32).hex()
    tmp = tempfile.mkdtemp()
    sf = os.path.join(tmp, "secret")
    with open(sf, "w") as f:
        f.write(secret + "\n")
    rdir = os.path.join(tmp, "replays")
    port = free_port()
    log = open(os.path.join(tmp, "lobby.log"), "w+")
    proc = subprocess.Popen([os.environ.get("LOBBY_EXE", "./rb_lobby"), "--port", str(port), "--bind", "127.0.0.1",
                             "--ticket-secret-file", sf, "--replay-dir", rdir], stdout=log, stderr=subprocess.STDOUT)
    try:
        time.sleep(0.3)
        lob = ("127.0.0.1", port)
        a, b = Fake(lob, "x", code=b"public"), Fake(lob, "y", code=b"public")
        assert join_raw(a, ticket(secret, "Alice", uid=UID_A))[0] == "welcome"
        assert join_raw(b, ticket(secret, "Bob", uid=UID_B))[0] == "welcome"
        # (join_raw consumed the WELCOME: read ip / cookie by joining again with the same nonce: the lobby answers the same)
        for f, n, u in ((a, "Alice", UID_A), (b, "Bob", UID_B)):
            f.got.clear()
            port_ = f.s.getsockname()[1]
            tk = ticket(secret, n, uid=u).encode()
            f.tx(L.JOIN, f.nonce + f.code.ljust(32, b"\0") + b"x".ljust(32, b"\0") + f.build.ljust(40, b"\0") + struct.pack(">IHB", 0x7F000001, port_, 1) + b"MS\0" + b"TK" + struct.pack(">H", len(tk)) + tk)
            w = f.rx(L.WELCOME)
            f.ip, f.cookie = struct.unpack_from(">IQ", w, 8)
        b.match(2, 1)
        time.sleep(0.2)
        a.match(2, 2)
        time.sleep(0.3)
        random.seed(5)
        seed1 = 0x1122334455667788
        mid1, blk = play(a, b, seed1, 2400, tag=0x33)
        time.sleep(4.5)                                                # (3 s without pads: written)
        seed2 = 0x0A0B0C0D01020304
        mid2, _ = play(a, b, seed2, 900, tag=0x44, desync_at=500)
        time.sleep(4.5)
        files = sorted(glob.glob(os.path.join(rdir, "pending", "*.ssbr")))
        log.seek(0)
        text = log.read()
        assert len(files) == 2, (files, text)
        for path, mid, seed, n, desync in ((files[0], mid1, seed1, 2400, 0), (files[1], mid2, seed2, 900, 500)):
            raw = open(path, "rb").read()
            r = ssbr.Replay(raw)
            print(r.summary())
            assert r.match_id == mid and r.frames == n, (hex(r.match_id), r.frames)
            assert r.names == ["Alice", "Bob"] and r.uids == [UID_A, UID_B], (r.names, r.uids)
            assert r.flags & ssbr.FLAG_QUICK and r.flags & ssbr.FLAG_BLOCK and r.flags & ssbr.FLAG_ENDED
            assert r.match == match_bytes(seed) and raw[ssbr.BLOCK_OFF:ssbr.BLOCK_OFF + ssbr.BLOCK_SIZE] == blk
            assert r.desync_frame == desync, r.desync_frame
            for f, (p, q) in enumerate(r.pads(), 1):
                assert p == pad(seed, 0, f) and q == pad(seed, 1, f), (f, p, q, pad(seed, 0, f))
            ck = r.checks()
            assert all(ck[f] == check(seed, f) or (desync and f >= desync and ck[f] == check(seed, f) ^ 1) for f in ck) and len(ck) >= n // 8 * 9 // 10, len(ck)
            assert b"127.0.0.1" not in raw and struct.pack(">I", 0x7F000001) not in raw[:0x300]
            pk = ssbr.Replay(r.packed())
            assert pk.codec == 1 and pk.body() == r.body() and pk.body_size < r.body_size
            print("  packed body %d -> %d bytes" % (r.body_size, pk.body_size))
        assert all(":%d" % f.s.getsockname()[1] not in text for f in (a, b))      # (the players' addresses are never logged)
        proc.terminate()
        proc.wait(5)
        print("rb_lobby replays: ok (2 matches recorded: pads of every frame, checksums, live desync frame, names + account ids, no address)")
        return 0
    finally:
        if proc.poll() is None:
            proc.terminate()
            proc.wait(5)
        log.seek(0)
        sys.stderr.write(log.read()[-1500:])


if __name__ == "__main__":
    sys.exit(main())
