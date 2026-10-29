#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""SSBU Arena replay files (.ssbr, written by the lobby: tools/net/rb_replay_rec.c, which documents the layout; played by
runtime/rb_replay.c). Reading, (de)compressing the body, a summary.

    python ssbr.py info <file.ssbr>             header summary (no IPs exist in the file: names and account ids only)
    python ssbr.py raw <in.ssbr> <out.ssbr>     body inflated (codec 0: what the game reads)
    python ssbr.py pack <in.ssbr> <out.ssbr>    body deflated (codec 1: what the server stores)
    python ssbr.py pads <file.ssbr> [from] [to] per-frame pads
"""
import struct
import sys
import zlib

HDR = 0x1000
MATCH_OFF, MATCH_SIZE = 0x100, 388
BLOCK_OFF, BLOCK_SIZE = 0x300, 0xA48
FLAG_QUICK, FLAG_ROOM, FLAG_BLOCK, FLAG_DESYNC, FLAG_ENDED = 1, 2, 4, 8, 0x10


def _cstr(b):
    return b.split(b"\0", 1)[0].decode("utf-8", "replace")


class Replay:
    def __init__(self, data):
        if len(data) < HDR or data[:4] != b"SSBR":
            raise ValueError("not an SSBR replay")
        self.data = bytes(data)
        h = self.data
        self.version, self.header_size, self.flags = struct.unpack_from("<HHI", h, 4)
        if self.version != 1:
            raise ValueError("replay version %d (this reader knows 1)" % self.version)
        self.game_build, self.proto, self.codec = struct.unpack_from("<hBB", h, 0xC)
        self.match_id, self.frames, self.unix, self.duration_ms, self.body_size, self.body_raw, self.desync_frame = struct.unpack_from("<IIqIIII", h, 0x10)
        self.winner, self.end_reason, self.check_every = struct.unpack_from("<BBH", h, 0x30)
        self.build = _cstr(h[0x38:0x60])
        self.names = [_cstr(h[0x60:0x80]), _cstr(h[0x80:0xA0])]
        self.uids = [_cstr(h[0xA0:0xC8]), _cstr(h[0xC8:0xF0])]
        self.match = h[MATCH_OFF:MATCH_OFF + MATCH_SIZE]
        self.chara = list(struct.unpack_from("<QQ", self.match, 0))
        self.colour = list(self.match[16:18])
        self.stage = struct.unpack_from("<Q", self.match, 0x40)[0]
        self.stage_form = struct.unpack_from("<I", self.match, 0x48)[0]
        self.hazards, self.rule_kind, self.stock = self.match[0x60], self.match[0x61], self.match[0x62]
        self.seed = struct.unpack_from("<Q", self.match, 0x68)[0]
        if len(self.data) < self.header_size + self.body_size:
            raise ValueError("replay file cut short")

    def body(self):
        b = self.data[self.header_size:self.header_size + self.body_size]
        if self.codec == 1:
            b = zlib.decompress(b, -15)
        elif self.codec != 0:
            raise ValueError("unknown body codec %d" % self.codec)
        if len(b) != self.body_raw:
            raise ValueError("body size %d, header says %d" % (len(b), self.body_raw))
        return b

    def _with_body(self, codec, body):
        h = bytearray(self.data[:self.header_size])
        h[0xF] = codec
        struct.pack_into("<I", h, 0x24, len(body))
        return bytes(h) + body

    def packed(self):
        """the file with a raw-deflate body (codec 1)"""
        c = zlib.compressobj(9, zlib.DEFLATED, -15, 9)
        return self._with_body(1, c.compress(self.body()) + c.flush())

    def raw(self):
        return self._with_body(0, self.body())

    def pads(self):
        """[(p1 (buttons, lx, ly, rx, ry), p2 (...))] for frames 1..N"""
        b, n = self.body(), self.frames
        cols = []
        for p in range(2):
            base = p * 10 * n
            btn = struct.unpack_from("<%dH" % n, b, base)
            st = [struct.unpack_from("<%dh" % n, b, base + 2 * n * (k + 1)) for k in range(4)]
            cols.append([(btn[f], st[0][f], st[1][f], st[2][f], st[3][f]) for f in range(n)])
        return list(zip(cols[0], cols[1]))

    def checks(self):
        """{frame: 32-bit checksum} (every check_every frames, as the games sent them)"""
        b, n = self.body(), self.frames
        k = n // self.check_every + 1
        v = struct.unpack_from("<%dI" % k, b, 20 * n)
        return {i * self.check_every: c for i, c in enumerate(v) if i and c}

    def summary(self):
        import datetime
        t = datetime.datetime.fromtimestamp(self.unix, datetime.timezone.utc).strftime("%Y-%m-%d %H:%M:%S UTC")
        return ("match %08x %s: %s vs %s; ui_chara %016x / %016x colours %d / %d; stage %016x form %d; rule kind %d stocks %d; "
                "%d frames (%.1f s); build %s, game build %d, handshake %d; flags %x%s; codec %d, body %d bytes (raw %d)"
                % (self.match_id, t, self.names[0], self.names[1], self.chara[0], self.chara[1], self.colour[0], self.colour[1],
                   self.stage, self.stage_form, self.rule_kind, self.stock, self.frames, self.frames / 60.0, self.build,
                   self.game_build, self.proto, self.flags, (", live desync at frame %d" % self.desync_frame) if self.desync_frame else "",
                   self.codec, self.body_size, self.body_raw))


def load(path):
    with open(path, "rb") as f:
        return Replay(f.read())


def main(a):
    if len(a) < 2:
        print(__doc__)
        return 2
    r = load(a[1])
    if a[0] == "info":
        print(r.summary())
        print("checksums stored: %d" % len(r.checks()))
    elif a[0] in ("raw", "pack"):
        with open(a[2], "wb") as f:
            f.write(r.raw() if a[0] == "raw" else r.packed())
    elif a[0] == "pads":
        lo, hi = int(a[2]) if len(a) > 2 else 1, int(a[3]) if len(a) > 3 else 60
        for f, (p, q) in enumerate(r.pads(), 1):
            if lo <= f <= hi:
                print(f, p, q)
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
