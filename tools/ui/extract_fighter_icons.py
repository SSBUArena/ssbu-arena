"""The launcher's profile pictures: each fighter's square portrait, taken from YOUR data.arc (game content: never in this
repository, never uploaded; an account stores only the fighter's name).

    python tools/ui/extract_fighter_icons.py --arc <data.arc> --out <build>/avatars

Writes <fighter>.png (ui/replace/chara/chara_4/chara_4_<fighter>_00.bntx, 162 x 162), icons.txt (<fighter>=<number of the fighter's
icon in the game's own profile icons, ui_profile_icon_db: what the launcher writes in the save, Core.ApplyProfileIcon). icons.txt is
written last: it also marks the work as done. more/<fighter>.png: the portraits of the fighters not offered as pictures (the ones from
the updates, the select screen's shared panels), for the replay list; more/done.txt marks them as done. (0.2.34) all/: every other
icon of the game's profile icons (fighters' other colours, spirits, series emblems), all_icons()."""
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

# the list's other names of a fighter: the panels that share one, what a match's record can hold
MORE = FIGHTERS + 'ice_climber element eflame_first elight_first eflame_only elight_only'.split()
PORTRAIT = ('ui/replace/chara/chara_4/chara_4_%s_00.bntx', 'ui/replace_patch/chara/chara_4/chara_4_%s_00.bntx')

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
        if t == 10:      # (0.2.34) a string: in the reference table
            o = struct.unpack_from('<I', d, pb + p + 1)[0]
            return d[base + o:d.index(b'\0', base + o)].decode('utf-8', 'replace')
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


SPIRIT = ('ui/replace/spirits/spirits_1/spirits_1_%s.bntx', 'ui/replace/spirits/spirits_2/spirits_2_%s.bntx', 'ui/replace/spirits/spirits_0/spirits_0_%s.bntx', 'ui/replace_patch/spirits/spirits_1/spirits_1_%s.bntx', 'ui/replace_patch/spirits/spirits_2/spirits_2_%s.bntx')
# (0.2.37) the Assist Trophies among the profile icons: rows ui_item_assist_<name> (ui_item_db), picture item_0_assist_<name>,
# English name nam_item_assist_<name> in msg_name (region 2, US English)
ASSIST = ('ui/replace/item/item_0/item_0_%s.bntx', 'ui/replace_patch/item/item_0/item_0_%s.bntx')
SERIES = ('ui/replace/series/series_0/series_0_%s.bntx', 'ui/replace/series/series_1/series_1_%s.bntx', 'ui/replace_patch/series/series_0/series_0_%s.bntx', 'ui/replace_patch/series/series_1/series_1_%s.bntx')


def decode_bc4(bntx, base):
    """a one-channel BC4 surface (the series' emblems) as white with that channel as its alpha, or None"""
    from PIL import Image
    from brawl.nutexb import swizzle      # (menu_recolor put its folder on the path)
    fmt = struct.unpack_from('<I', bntx, base + 0x1c)[0]
    if fmt not in (0x1d01, 0x1d02) or struct.unpack_from('<H', bntx, base + 0x16)[0] != 1:
        return None
    w, h = struct.unpack_from('<II', bntx, base + 0x24)
    size = struct.unpack_from('<I', bntx, base + 0x50)[0]
    offset = struct.unpack_from('<Q', bntx, struct.unpack_from('<Q', bntx, base + 0x70)[0])[0]
    bw, bh = (w + 3) // 4, (h + 3) // 4
    linear = swizzle(bytes(bntx[offset:offset + size]), bw, bh, bpp=8, to_linear=True)
    a = Image.frombytes('L', (bw * 4, bh * 4), linear[:bw * bh * 8], 'bcn', 4).crop((0, 0, w, h))
    im = Image.new('RGBA', (w, h), (238, 243, 247, 0))
    im.putalpha(a)
    return im


def first_image(arc, paths, name):
    for path in paths:
        try:
            bntx = bytearray(arc.get(path % name))
        except KeyError:
            continue
        for _, base in texture_slots(bntx).items():
            image = decode(bntx, base)
            if image:
                return image[0]
            return decode_bc4(bntx, base)
    return None


def square(im, size=128):
    """the picture in a square: scaled to fit, centred, transparent around it"""
    from PIL import Image
    im = im.convert('RGBA')
    im.thumbnail((size, size), Image.LANCZOS)
    out = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    out.paste(im, ((size - im.width) // 2, (size - im.height) // 2), im)
    return out


def message_names(arc, prefix):
    """{label without the prefix: English text} of ui/message/msg_name.msbt (the game's own words, read from data.arc)"""
    try:
        import msbt_patch
        names, chunks = msbt_patch.messages(arc.get('ui/message/msg_name.msbt', 2))
    except Exception:
        return {}
    out = {}
    for k, i in names.items():
        if k.startswith(prefix):
            t = chunks[i].decode('utf-16-le', 'replace').split('\x0e')[0].rstrip('\x00').strip()
            if t:
                out[k[len(prefix):]] = t
    return out


def all_icons(arc, out):
    """(0.2.34) every icon of the game's profile icons (ui_profile_icon_db: the fighters in some of their colours, spirits,
    the series' emblems, 0.2.37 the Assist Trophies): all/icon_<number>.png, all/icons.txt
    (icon_<number>=<number>,<fighter|spirit|series|assist>,<name>,<colour>), written last (it marks the work as done), then
    all/v2.txt (0.2.37: made again once for the Assist Trophies). Rows whose picture is not found are left out."""
    def strings_of(path, prefix):
        try:
            rows = prc_rows(arc.get(path))
        except KeyError:
            return {}
        names = {}
        for r in rows:
            for v in r.values():
                if isinstance(v, str) and v:
                    names[hash40(prefix + v)] = v
        return names
    fighters = {hash40('ui_chara_' + f): f for f in MORE}
    fighters.update(strings_of('ui/param/database/ui_chara_db.prc', 'ui_chara_'))
    spirits = strings_of('ui/param/database/ui_spirit_db.prc', '')
    series = strings_of('ui/param/database/ui_series_db.prc', 'ui_series_')
    assists = {h: v for h, v in strings_of('ui/param/database/ui_item_db.prc', 'ui_item_').items() if v.startswith('assist_')}
    assist_names = message_names(arc, 'nam_item_assist_')
    dst = out / 'all'
    dst.mkdir(parents=True, exist_ok=True)
    lines = []
    for r in prc_rows(arc.get('ui/param/database/ui_profile_icon_db.prc')):
        n, ch, slot = r.get(F_INDEX), r.get(F_CHARA), r.get(F_SLOT) or 0
        if n is None:
            continue
        if ch in fighters:
            kind, name, im = 'fighter', fighters[ch], None
            path = (PORTRAIT[0].replace('_00.', '_%02d.' % slot), PORTRAIT[1].replace('_00.', '_%02d.' % slot))
            im = first_image(arc, path, name)
        elif ch in series:
            kind, name = 'series', series[ch]
            im = first_image(arc, SERIES, name)
        elif ch in assists:
            kind, key = 'assist', assists[ch]
            name = assist_names.get(key[len('assist_'):], key[len('assist_'):]).replace(',', ' ')
            im = first_image(arc, ASSIST, key)
        elif ch in spirits:
            kind, name = 'spirit', spirits[ch]
            im = first_image(arc, SPIRIT, name)
        else:
            continue
        if im is None:
            continue
        square(im).save(dst / ('icon_%d.png' % n))
        lines.append('icon_%d=%d,%s,%s,%d\n' % (n, n, kind, name, slot))
    (dst / 'icons.txt').write_text(''.join(lines), encoding='utf-8')
    (dst / 'v2.txt').write_text('ok')
    return len(lines)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--arc', type=Path, required=True)
    ap.add_argument('--out', type=Path, required=True)
    a = ap.parse_args()
    arc = Arc(str(a.arc))
    a.out.mkdir(parents=True, exist_ok=True)
    made = 0
    for f in FIGHTERS:
        if (a.out / (f + '.png')).exists():
            made += 1
            continue
        try:
            bntx = bytearray(arc.get(PORTRAIT[0] % f))
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
    more = a.out / 'more'
    more.mkdir(exist_ok=True)
    for f in MORE:
        if (a.out / (f + '.png')).exists():
            continue
        for path in PORTRAIT:
            try:
                bntx = bytearray(arc.get(path % f))
            except KeyError:
                continue
            for _, base in texture_slots(bntx).items():
                image = decode(bntx, base)
                if image:
                    image[0].save(more / (f + '.png'))
                break
            break
    (more / 'done.txt').write_text('ok')
    if not (a.out / 'all' / 'icons.txt').exists() or not (a.out / 'all' / 'v2.txt').exists():
        print('all the profile icons: %d' % all_icons(arc, a.out))


if __name__ == '__main__':
    main()
