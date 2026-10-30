"""The launcher's profile pictures: each fighter's square portrait, taken from YOUR data.arc (game content: never in this
repository, never uploaded; an account stores only the fighter's name).

    python tools/ui/extract_fighter_icons.py --arc <data.arc> --out <build>/avatars

Writes <fighter>.png (ui/replace/chara/chara_4/chara_4_<fighter>_00.bntx, 162 x 162), icons.txt (<fighter>=<number of the fighter's
icon in the game's own profile icons, ui_profile_icon_db: what the launcher writes in the save, Core.ApplyProfileIcon). icons.txt is
written last: it also marks the work as done."""
import argparse
from pathlib import Path
import struct
import sys
import zlib

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from prepare_menu import Arc      # (also sets the path of tools/nro)
from menu_recolor import decode
from menu_textures import texture_slots

# the fighters of the select screen; the ones that share a panel appear once (Ice Climbers: popo, Pokemon Trainer: ptrainer)
FIGHTERS = '''mario donkey link samus samusd yoshi kirby fox pikachu luigi ness captain purin peach daisy koopa popo sheik zelda mariod
pichu falco marth lucina younglink ganon mewtwo roy chrom gamewatch metaknight pit pitb szerosuit wario snake ike ptrainer diddy
lucas sonic dedede pikmin lucario robot toonlink wolf murabito rockman wiifit rosetta littlemac gekkouga palutena pacman reflet
shulk koopajr duckhunt ryu ken cloud kamui bayonetta inkling ridley simon richter krool shizue gaogaen packun jack brave buddy
dolly master tantan pickel edge eflame elight demon trail miifighter miiswordsman miigunner'''.split()

# fields of ui_profile_icon_db (hashes: the file keeps no names): which picture, its colour slot, the icon's number
F_CHARA, F_SLOT, F_INDEX = 0xd8fe20293, 0x5665648e9, 0xf5f9446e1
M40 = 0xffffffffff


def hash40(s):
    return (len(s) << 32) | zlib.crc32(s.encode())


def prc_rows(d):
    """the list of a paracobn file that holds one table: each row as {field hash: value} (hash and integer fields only)"""
    hs, rs = struct.unpack_from('<II', d, 8)
    hashes = [struct.unpack_from('<Q', d, 16 + i * 8)[0] & M40 for i in range(hs // 8)]
    base, pb = 16 + hs, 16 + hs + rs

    def walk(p):
        t = d[pb + p]
        if t == 12:
            n, ref = struct.unpack_from('<II', d, pb + p + 1)
            pairs = [struct.unpack_from('<II', d, base + ref + i * 8) for i in range(n)]
            return {hashes[h]: walk(p + off) for h, off in pairs}
        if t == 11:
            n = struct.unpack_from('<I', d, pb + p + 1)[0]
            return [walk(p + struct.unpack_from('<I', d, pb + p + 5 + i * 4)[0]) for i in range(n)]
        if t == 9:
            return hashes[struct.unpack_from('<I', d, pb + p + 1)[0]]
        if t in (6, 7):
            return struct.unpack_from('<i', d, pb + p + 1)[0]
        if t in (4, 5):
            return struct.unpack_from('<h', d, pb + p + 1)[0]
        if t in (2, 3):
            return d[pb + p + 1]
        return None
    return next(v for v in walk(0).values() if isinstance(v, list))


def icon_numbers(arc):
    """<fighter>: the number of the icon with the fighter's default colour"""
    rows = prc_rows(arc.get('ui/param/database/ui_profile_icon_db.prc'))
    first = {}
    for r in rows:
        if r.get(F_SLOT) == 0:
            first.setdefault(r.get(F_CHARA), r.get(F_INDEX))
    return {f: first[hash40('ui_chara_' + f)] for f in FIGHTERS if hash40('ui_chara_' + f) in first}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--arc', type=Path, required=True)
    ap.add_argument('--out', type=Path, required=True)
    a = ap.parse_args()
    arc = Arc(str(a.arc))
    a.out.mkdir(parents=True, exist_ok=True)
    made = 0
    for f in FIGHTERS:
        try:
            bntx = bytearray(arc.get('ui/replace/chara/chara_4/chara_4_%s_00.bntx' % f))
        except KeyError:
            continue
        for _, base in texture_slots(bntx).items():
            image = decode(bntx, base)
            if image:
                image[0].save(a.out / (f + '.png'))
                made += 1
            break
    print('profile pictures: %d of %d fighters' % (made, len(FIGHTERS)))
    numbers = icon_numbers(arc)
    print('icons in the game: %d of %d fighters' % (len(numbers), len(FIGHTERS)))
    if made:
        (a.out / 'icons.txt').write_text(''.join('%s=%d\n' % kv for kv in numbers.items()))


if __name__ == '__main__':
    main()
