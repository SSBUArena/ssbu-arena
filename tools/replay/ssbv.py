#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""SSBU Arena match state files (.ssbv: fighter_sdk/replay_state.c writes them, the launcher's Replays page draws them).

    python tools/replay/ssbv.py info <file.ssbv>                 header, stage lines, a summary
    python tools/replay/ssbv.py frames <file.ssbv> [from] [to]   one line per fighter per frame
    python tools/replay/ssbv.py ff <file.ssbv> <fight frame>     the frame whose fighter frame count is that (screenshots)
"""
import struct
import sys
import zlib

HEAD = 0x200


def cstr(b):
    return b.split(b"\0", 1)[0].decode("utf-8", "replace")


class Ssbv:
    def __init__(self, data):
        if data[:4] != b"SSBV":
            raise ValueError("not an .ssbv file")
        self.version, self.header_size, self.flags, self.nframes, self.unix = struct.unpack_from("<HHIIq", data, 4)
        self.body_size, self.body_raw = struct.unpack_from("<II", data, 0x18)
        self.codec, self.nf, self.rule_kind, self.stocks = struct.unpack_from("<BBBB", data, 0x20)
        self.ui_stage, self.stage_form, self.stage_id = struct.unpack_from("<QII", data, 0x28)
        self.dead = struct.unpack_from("<4f", data, 0x38)
        self.camera = struct.unpack_from("<4f", data, 0x48)
        self.nlines, self.game_build = struct.unpack_from("<II", data, 0x58)
        self.fighters = []
        for i in range(self.nf):
            o = 0x60 + 0x40 * i
            ui, kind, colour, typ, entry, slot = struct.unpack_from("<QiBBBB", data, o)
            end_stocks, end_damage = struct.unpack_from("<If", data, o + 48)
            self.fighters.append(dict(ui_chara=ui, kind=kind, colour=colour, type=typ, entry=entry, slot=slot, name=cstr(data[o + 16:o + 48]), end_stocks=end_stocks, end_damage=end_damage))
        self.build = cstr(data[0x160:0x180])
        self.local_player, self.match_number, self.seed, self.first_ff, self.ended_frame, self.winner, self.rec_us = struct.unpack_from("<IIQIIiI", data, 0x180)
        self.match_id, self.kind = struct.unpack_from("<IB", data, 0x1a4)
        self.local_name = cstr(data[0x1ac:0x1cc])
        body = data[self.header_size:self.header_size + self.body_size]
        if self.codec == 1:
            body = zlib.decompress(body, -15)
        self.lines = [struct.unpack_from("<4fI", body, 20 * i) for i in range(self.nlines)]
        self.frames = []
        o = 20 * self.nlines
        while o + 8 <= len(body):
            if self.version >= 2:
                fr, mask, moved, _, valid = struct.unpack_from("<IBBHQ", body, o)
                o += 16
            else:
                fr, mask, moved = struct.unpack_from("<IBB", body, o)
                valid = (1 << 64) - 1
                o += 8
            mv = []
            for _ in range(moved):
                li = struct.unpack_from("<H", body, o)[0]
                mv.append((li,) + struct.unpack_from("<4f", body, o + 4))
                o += 20
            fs = {}
            for k in range(4):
                if not mask & (1 << k):
                    continue
                x, y, dmg, mf, mot, st, lr, sit, stocks, nhit, nhurt, flags, _, shield, ff = struct.unpack_from("<4fQibBBBBBHfI", body, o)
                o += 44
                hits = []
                for _ in range(nhit):
                    hx, hy, hx2, hy2, r, hid, hfl, d10, ang, kbg, bkb = struct.unpack_from("<4hHBBHhHH", body, o)
                    hits.append(dict(x=hx / 16, y=hy / 16, x2=hx2 / 16, y2=hy2 / 16, r=r / 16, id=hid, dmg=d10 / 10, angle=ang, kbg=kbg, bkb=bkb))
                    o += 20
                hurts = []
                for _ in range(nhurt):
                    ux, uy, ux2, uy2, ur, part, ust = struct.unpack_from("<4hHBB", body, o)
                    hurts.append(dict(x=ux / 16, y=uy / 16, x2=ux2 / 16, y2=uy2 / 16, r=ur / 16, part=part, status=ust))
                    o += 12
                fs[k] = dict(x=x, y=y, dmg=dmg, mf=mf, motion=mot, status=st, lr=lr, situation=sit, stocks=stocks, shield=shield, ff=ff, hits=hits, hurts=hurts)
            self.frames.append(dict(frame=fr, moved=mv, valid=valid, f=fs))


def line(fr):
    out = ["%6d" % fr["frame"]]
    for k, s in sorted(fr["f"].items()):
        out.append("| k%d ff %5d x %8.3f y %8.3f %6.1f%% st %3x mot %010x mf %5.1f lr %2d sit %d stk %d sh %5.1f hit %d hurt %d" % (
            k, s["ff"], s["x"], s["y"], s["dmg"], s["status"], s["motion"] & 0xffffffffff, s["mf"], s["lr"], s["situation"], s["stocks"], s["shield"], len(s["hits"]), len(s["hurts"])))
        for h in s["hits"]:
            out.append("   [hit %d %.1f%% (%.1f,%.1f)-(%.1f,%.1f) r %.2f a %d kbg %d bkb %d]" % (h["id"], h["dmg"], h["x"], h["y"], h["x2"], h["y2"], h["r"], h["angle"], h["kbg"], h["bkb"]))
    return " ".join(out)


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return
    v = Ssbv(open(sys.argv[2], "rb").read())
    if sys.argv[1] == "info":
        print("version %d flags %x frames %d (%d in the body) codec %d build %s stage %x form %d id %x rules %d/%d" % (v.version, v.flags, v.nframes, len(v.frames), v.codec, v.build, v.ui_stage, v.stage_form, v.stage_id, v.rule_kind, v.stocks))
        print("blast zones (dead area) %s camera %s winner %d recorder %d us/line" % (v.dead, v.camera, v.winner, v.rec_us))
        print("match id %08x kind %d (0 local, 1 matchmaking, 2 LDN room, 3 other online) local player %d name %r" % (v.match_id, v.kind, v.local_player, v.local_name))
        for f in v.fighters:
            print("fighter %s" % f)
        for i, l in enumerate(v.lines):
            print("line %2d %8.2f %8.2f -> %8.2f %8.2f flags %d" % ((i,) + l))
        if v.frames:
            print("first frame %d, last %d" % (v.frames[0]["frame"], v.frames[-1]["frame"]))
            gaps = sum(1 for a, b in zip(v.frames, v.frames[1:]) if b["frame"] != a["frame"] + 1)
            print("frame number gaps: %d" % gaps)
    elif sys.argv[1] == "frames":
        a = int(sys.argv[3]) if len(sys.argv) > 3 else 0
        b = int(sys.argv[4]) if len(sys.argv) > 4 else 1 << 30
        for fr in v.frames:
            if a <= fr["frame"] <= b:
                print(line(fr))
    elif sys.argv[1] == "ff":
        want = int(sys.argv[3])
        for fr in v.frames:
            if any(s["ff"] == want for s in fr["f"].values()):
                print(line(fr))


if __name__ == "__main__":
    main()
