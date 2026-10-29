#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""A match state file (.ssbv) against the rollback session's own final-frame log of the same match.

    python tools/replay/cmp_detail.py <file.ssbv> <SSBU_RB_NET_LOG file>

The game run with SSBU_RB_NET_LOG=<file> SSBU_RB_NET_DETAIL=1 SSBU_FIGHT_HASH=<file> writes one "d <frame> ..." line per FINAL
frame (runtime/rb_net.c detail_frame) with each fighter's status, motion, motion frame, position and damage as the frame
checksum saw them in that frame's last simulation. The recorder must hold exactly the same values for the same frames:
anything else would mean a frame from a guessed (rolled back) simulation was kept.
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ssbv  # noqa: E402


def main():
    v = ssbv.Ssbv(open(sys.argv[1], "rb").read())
    rec = {fr["frame"]: fr for fr in v.frames}
    pat = re.compile(r"\| e(-?\d+) st ([0-9a-f]+) mot ([0-9a-f]+) mf ([-\d.]+) x ([-\d.]+) y ([-\d.]+) dmg ([-\d.]+)")
    n = same = diff = missing = 0
    shown = 0
    for line in open(sys.argv[2], encoding="utf-8", errors="replace"):
        if not line.startswith("d "):
            continue
        f = int(line.split()[1])
        fighters = pat.findall(line)
        r = rec.get(f)
        if r is None:
            missing += 1
            continue
        recs = sorted(r["f"].items())
        for i, (e, st, mot, mf, x, y, dmg) in enumerate(fighters):
            if i >= len(recs):
                break
            s = recs[i][1]
            n += 1
            ok = (int(st, 16) == s["status"] and int(mot, 16) == s["motion"] and abs(float(mf) - s["mf"]) < 0.002 and abs(float(x) - s["x"]) < 0.0002
                  and abs(float(y) - s["y"]) < 0.0002 and abs(float(dmg) - s["dmg"]) < 0.002)
            if ok:
                same += 1
            else:
                diff += 1
                if shown < 10:
                    shown += 1
                    print("frame %d fighter line %d: log st %s mot %s mf %s x %s y %s dmg %s | recorded st %x mot %x mf %.3f x %.4f y %.4f dmg %.3f" % (
                        f, i, st, mot, mf, x, y, dmg, s["status"], s["motion"], s["mf"], s["x"], s["y"], s["dmg"]))
    print("%d fighter-frames compared: %d identical, %d different; %d final frames of the log not in the file; %d frames in the file" % (n, same, diff, missing, len(v.frames)))


if __name__ == "__main__":
    main()
