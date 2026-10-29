#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Move data for the launcher's replay viewer, from Ultimate Frame Data (https://ultimateframedata.com, by MetalMusicMan;
Smash Ultimate hitboxes and frame data: Zeckemyro; the site credits further sources on its pages).

    python tools/replay/ufd_moves.py [--out tools/launcher/arena/Assets/ufd_moves.json] [--delay 1.5]

One page per fighter (https://ultimateframedata.com/<slug>), read politely (a pause between requests, a plain user agent).
Kept per move ONLY what the viewer shows next to the hitboxes the game itself reports (the replay's own data always wins):
the move's name, startup frame, active frames, total frames, landing lag and base damage, plus the URL of the page and of
the move's hitbox picture (a link: the pictures are NOT downloaded or stored). The site states no terms of use for its
Smash data (checked 2026-10-29: no license or terms text on /, /smash or the fighter pages; its only notice concerns another
game); the data are credited visibly in the launcher and in this file's output ("source", "credit").
Keys are the game's own fighter names (ui_chara_<name>), so the launcher finds a fighter by the ui_chara id of the replay.
"""
import argparse
import html
import json
import os
import re
import sys
import time
import urllib.request

BASE = "https://ultimateframedata.com/"
UA = "SSBU-Arena replay viewer data script (one request per fighter page)"
CREDIT = "Ultimate Frame Data / MetalMusicMan; Smash Ultimate hitboxes: Zeckemyro"

# ultimateframedata.com page -> the game's fighter name (ui_chara_<name>)
SLUGS = {
    "mario": "mario", "donkey_kong": "donkey", "link": "link", "samus": "samus", "dark_samus": "samusd", "yoshi": "yoshi",
    "kirby": "kirby", "fox": "fox", "pikachu": "pikachu", "luigi": "luigi", "ness": "ness", "captain_falcon": "captain",
    "jigglypuff": "purin", "peach": "peach", "daisy": "daisy", "bowser": "koopa", "ice_climbers": "ice_climber", "sheik": "sheik",
    "zelda": "zelda", "dr_mario": "mariod", "pichu": "pichu", "falco": "falco", "marth": "marth", "lucina": "lucina",
    "young_link": "younglink", "ganondorf": "ganon", "mewtwo": "mewtwo", "roy": "roy", "chrom": "chrom",
    "mr_game_and_watch": "gamewatch", "meta_knight": "metaknight", "pit": "pit", "dark_pit": "pitb", "zero_suit_samus": "szerosuit",
    "wario": "wario", "snake": "snake", "ike": "ike", "pt_squirtle": "pzenigame", "pt_ivysaur": "pfushigisou",
    "pt_charizard": "plizardon", "diddy_kong": "diddy", "lucas": "lucas", "sonic": "sonic", "king_dedede": "dedede",
    "olimar": "pikmin", "lucario": "lucario", "rob": "robot", "toon_link": "toonlink", "wolf": "wolf", "villager": "murabito",
    "mega_man": "rockman", "wii_fit_trainer": "wiifit", "rosalina_and_luma": "rosetta", "little_mac": "littlemac",
    "greninja": "gekkouga", "mii_brawler": "miifighter", "mii_swordfighter": "miiswordsman", "mii_gunner": "miigunner",
    "palutena": "palutena", "pac_man": "pacman", "robin": "reflet", "shulk": "shulk", "bowser_jr": "koopajr",
    "duck_hunt": "duckhunt", "ryu": "ryu", "ken": "ken", "cloud": "cloud", "corrin": "kamui", "bayonetta": "bayonetta",
    "inkling": "inkling", "ridley": "ridley", "simon": "simon", "richter": "richter", "king_k_rool": "krool",
    "isabelle": "shizue", "incineroar": "gaogaen", "piranha_plant": "packun", "joker": "jack", "hero": "brave",
    "banjo_and_kazooie": "buddy", "terry": "dolly", "byleth": "master", "minmin": "tantan", "steve": "pickel",
    "sephiroth": "edge", "pyra": "eflame", "mythra": "elight", "kazuya": "demon", "sora": "trail",
}
FIELDS = {"startup": "startup", "activeframes": "active", "totalframes": "total", "landinglag": "landing", "basedamage": "damage"}


def fetch(url):
    rq = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(rq, timeout=30) as r:
        return r.read().decode("utf-8", "replace")


def clean(t):
    t = html.unescape(re.sub(r"<[^>]+>", " ", t))
    t = t.replace("�", "-").replace("—", "-").replace("–", "-")
    return re.sub(r"\s+", " ", t).strip()


def parse(page, slug):
    moves = []
    for block in re.split(r'<div class="movecontainer[^"]*">', page)[1:]:
        name = re.search(r'class="movename">(.*?)</div>', block, re.S)
        if not name:
            continue
        m = {"name": clean(name.group(1))}
        for cls, key in FIELDS.items():
            v = re.search(r'class="%s">(.*?)</div>' % cls, block, re.S)
            if v:
                t = clean(v.group(1))
                if t and t != "--":
                    m[key] = t
        pics = re.findall(r'data-featherlight="([^"]+\.(?:gif|png|webp))"', block)
        if pics:
            m["pics"] = [BASE + p.lstrip("/") for p in dict.fromkeys(pics)]
        if len(m) > 1:
            moves.append(m)
    return moves


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "launcher", "arena", "Assets", "ufd_moves.json"))
    ap.add_argument("--delay", type=float, default=1.5)
    ap.add_argument("--only", nargs="*", help="UFD slugs (default: every fighter)")
    a = ap.parse_args()
    out = {"source": BASE + "smash", "credit": CREDIT, "fetched": time.strftime("%Y-%m-%d"), "fighters": {}}
    for slug, name in SLUGS.items():
        if a.only and slug not in a.only:
            continue
        url = BASE + slug
        try:
            moves = parse(fetch(url), slug)
        except Exception as e:                      # (a fighter the site does not have: kept out, reported)
            print("%-22s %s" % (slug, e), file=sys.stderr)
            continue
        out["fighters"][name] = {"url": url, "moves": moves}
        print("%-22s %3d moves" % (slug, len(moves)))
        time.sleep(a.delay)
    with open(a.out, "w", encoding="utf-8", newline="\n") as f:
        json.dump(out, f, ensure_ascii=False, separators=(",", ":"))
    print("%s: %d fighters, %d bytes" % (os.path.normpath(a.out), len(out["fighters"]), os.path.getsize(a.out)))


if __name__ == "__main__":
    main()
