#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""The stage a match state file (.ssbv) recorded, checked against its own fighters: every frame a fighter stands on the
ground (situation 0), a recorded floor line that is there at that frame must be under its feet.

    python tools/replay/check_ground.py <file.ssbv> [more.ssbv ...]

Lines move (Smashville's platform) and come and go (Town and City): the per-frame positions and the lines-on mask of the
file are used, so this also checks that moving and appearing platforms follow the game frame by frame.
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ssbv  # noqa: E402


def main():
    for path in sys.argv[1:]:
        v = ssbv.Ssbv(open(path, "rb").read())
        cur = [list(l[:4]) for l in v.lines]
        first = [(l[4] >> 8) for l in v.lines]
        floors = [i for i, l in enumerate(v.lines) if l[4] & 1]
        n = ok = 0
        bad = {}
        for i in range(min(len(v.lines), 64)):           # (as the launcher: the start scan's lines were there before it ended)
            if first[i] == 0:
                on = next((j for j, fr in enumerate(v.frames) if (fr["valid"] >> i) & 1), 0)
                for j in range(on):
                    v.frames[j]["valid"] |= 1 << i
        for fr in v.frames:
            for m in fr["moved"]:
                cur[m[0]] = list(m[1:5])
            for k, s in fr["f"].items():
                if s["situation"] != 0 or abs(s["x"]) > 300:
                    continue
                n += 1
                hit = False
                for i in floors:
                    if fr["frame"] < first[i] or not (fr["valid"] >> i) & 1:
                        continue
                    x1, y1, x2, y2 = cur[i]
                    lo, hi = min(x1, x2), max(x1, x2)
                    if lo - 1.0 <= s["x"] <= hi + 1.0:
                        t = 0 if hi == lo else (s["x"] - x1) / (x2 - x1)
                        y = y1 + (y2 - y1) * max(0, min(1, t))
                        if abs(y - s["y"]) < 1.0:
                            hit = True
                            break
                if hit:
                    ok += 1
                else:
                    key = (round(s["y"], 1))
                    bad.setdefault(key, []).append((fr["frame"], k, round(s["x"], 2)))
        print("%s: %d fighter-frames on the ground, %d over a recorded floor line, %d not" % (os.path.basename(path), n, ok, n - ok))
        for y, lst in sorted(bad.items()):
            print("   at y %.1f: %d frames, e.g. %s" % (y, len(lst), lst[:4]))


if __name__ == "__main__":
    main()
