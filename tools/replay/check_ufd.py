#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""The hitboxes a match state file (.ssbv) recorded, against Ultimate Frame Data's numbers for the same moves.

    python tools/replay/check_ufd.py <file.ssbv> [more.ssbv ...]

For every use of a common move (the motion names the launcher knows: attack_11 = Jab 1, attack_air_f = Forward Air, ...):
the frames of the move (counted from the frame its motion began, 1 = the first) on which the game had hitboxes out, and the
damage of each hitbox, next to UFD's startup / active frames / base damage. A move that was cut short (hit, shield,
landing) shows fewer frames; a charged smash shows more damage (up to 1.4x). One line per use.
"""
import json
import os
import sys
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import ssbv  # noqa: E402

MOVES = {
    "attack_11": "Jab 1", "attack_12": "Jab 2", "attack_13": "Jab 3", "attack_dash": "Dash Attack",
    "attack_s3_s": "Forward Tilt", "attack_s3_hi": "Forward Tilt", "attack_s3_lw": "Forward Tilt", "attack_hi3": "Up Tilt", "attack_lw3": "Down Tilt",
    "attack_s4_s": "Forward Smash", "attack_s4_hi": "Forward Smash", "attack_s4_lw": "Forward Smash", "attack_hi4": "Up Smash", "attack_lw4": "Down Smash",
    "attack_air_n": "Neutral Air", "attack_air_f": "Forward Air", "attack_air_b": "Back Air", "attack_air_hi": "Up Air", "attack_air_lw": "Down Air",
    "catch": "Grab", "catch_dash": "Dash Grab", "catch_turn": "Pivot Grab",
}
FIGHTERS = ["mario", "donkey", "link", "samus", "samusd", "yoshi", "kirby", "fox", "pikachu", "luigi", "ness", "captain", "purin", "peach", "daisy",
            "koopa", "ice_climber", "sheik", "zelda", "mariod", "pichu", "falco", "marth", "lucina", "younglink", "ganon", "mewtwo", "roy", "chrom",
            "gamewatch", "metaknight", "pit", "pitb", "szerosuit", "wario", "snake", "ike", "ptrainer", "pzenigame", "pfushigisou", "plizardon", "diddy",
            "lucas", "sonic", "dedede", "pikmin", "lucario", "robot", "toonlink", "wolf", "murabito", "rockman", "wiifit", "rosetta", "littlemac",
            "gekkouga", "miifighter", "miiswordsman", "miigunner", "palutena", "pacman", "reflet", "shulk", "koopajr", "duckhunt", "ryu", "ken", "cloud",
            "kamui", "bayonetta", "inkling", "ridley", "simon", "richter", "krool", "shizue", "gaogaen", "packun", "jack", "brave", "buddy", "dolly",
            "master", "tantan", "pickel", "edge", "eflame", "elight", "demon", "trail"]


def h40(s):
    return (len(s) << 32) | zlib.crc32(s.encode())


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ufd = json.load(open(os.path.join(here, "..", "launcher", "arena", "Assets", "ufd_moves.json"), encoding="utf-8"))["fighters"]
    mot = {h40(k): k for k in MOVES}
    fname = {h40("ui_chara_" + f): f for f in FIGHTERS}
    for path in sys.argv[1:]:
        v = ssbv.Ssbv(open(path, "rb").read())
        print("== %s" % os.path.basename(path))
        slot_fighter = {}
        for f in v.fighters:
            slot_fighter[f["slot"]] = fname.get(f["ui_chara"] & 0xFFFFFFFFFF, "?")
        for k, who in slot_fighter.items():
            cur = None
            uses = []
            for fr in v.frames:
                s = fr["f"].get(k)
                if not s:
                    continue
                m = s["motion"] & 0xFFFFFFFFFF
                if cur is None or m != cur["m"] or s["mf"] < cur["last_mf"]:
                    if cur and cur["m"] in mot:
                        uses.append(cur)
                    cur = {"m": m, "start": fr["frame"], "hits": [], "last_mf": s["mf"]}
                cur["last_mf"] = s["mf"]
                if s["hits"]:
                    n = fr["frame"] - cur["start"] + 1
                    cur["hits"].append((n, round(s["mf"], 1), sorted({h["dmg"] for h in s["hits"]}), max(h["r"] for h in s["hits"])))
            if cur and cur["m"] in mot:
                uses.append(cur)
            for u in uses:
                name = MOVES[mot[u["m"]]]
                ref = next((x for x in ufd.get(who, {}).get("moves", []) if x["name"] == name), None)
                frames = [h[0] for h in u["hits"]]
                mfs = [h[1] for h in u["hits"]]
                dmg = sorted({d for h in u["hits"] for d in h[2]})
                print("  %-11s %-14s at frame %5d: hitbox frames %-22s (motion frame %s) damage %-18s | UFD startup %-6s active %-12s damage %s" % (
                    who, name, u["start"], ",".join(map(str, frames)) or "-", ",".join("%g" % x for x in mfs[:12]) or "-", "/".join("%g" % d for d in dmg) or "-",
                    ref.get("startup", "-") if ref else "?", ref.get("active", "-") if ref else "?", ref.get("damage", "-") if ref else "?"))


if __name__ == "__main__":
    main()
