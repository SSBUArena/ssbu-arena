"""(22/11) SSBU Arena's ranked emblems as GAME TEXTURES for the Online select's GSP panel (runtime/online_mastery.c SSBU_RANK_EMBLEM).

The panel's rows show a fighter icon: ui/replace/chara/chara_0/chara_0_<ui_chara>_00.bntx (128 x 128), set by the row's icon setter
with a ui_chara id. Arena's tier emblems take the place of eight icons the Online select never shows (the bosses' costume 00): the
runtime gives the row the boss's ui_chara and the loose-file layer serves our texture at that boss icon's path. One emblem per tier
family (the division is in the row's text):

    0 Unranked  koopag      1 Bronze  masterhand    2 Silver   crazyhand    3 Gold         ganonboss
    4 Platinum  kiila       5 Diamond darz          6 Master   dracula      7 Grandmaster  galleom

The geometry is the launcher's and the site's emblem (tools/launcher/arena/RankEmblem.cs, viewBox 56 x 62: the hexagon, its inner
line, the diamond with its core; the Grandmaster's star), the colours the families' colours of public.ranked_tiers (--tiers: the
launcher's copy of the table, JSON rows {tier, position, family, color}; without it the copy of 20261121 below). Nothing of the game
is written: each output is the game's own BNTX of that boss icon (read from the player's data.arc) with its surface replaced by
our picture.
    python rank_emblems.py --arc <data.arc> --out <characters>/arena_rank_emblems [--tiers ranked_tiers.json]"""
import argparse
import json
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import menu_textures  # noqa: E402
import texture_surface  # noqa: E402

SLOTS = ['koopag', 'masterhand', 'crazyhand', 'ganonboss', 'kiila', 'darz', 'dracula', 'galleom']
FAMILIES = ['Unranked', 'Bronze', 'Silver', 'Gold', 'Platinum', 'Diamond', 'Master', 'Grandmaster']
FALLBACK = {'Unranked': '#8C9AAD', 'Bronze': '#C07A45', 'Silver': '#BFC9D4', 'Gold': '#E6B84A', 'Platinum': '#5FD3C7',
            'Diamond': '#65C7FF', 'Master': '#B38CFF', 'Grandmaster': '#FF5A6A'}
DEEP = (0x0D, 0x17, 0x24)
HEX = [(28, 1), (54, 16), (54, 46), (28, 61), (2, 46), (2, 16)]
HEX_IN = [(28, 7), (49, 19), (49, 43), (28, 55), (7, 43), (7, 19)]
GEM = [(28, 18), (39, 31), (28, 44), (17, 31)]
CORE = [(28, 26), (32.5, 31), (28, 36), (23.5, 31)]
STAR = [(28, 24), (30, 29), (35, 31), (30, 33), (28, 38), (26, 33), (21, 31), (26, 29)]


def rgb(h):
    h = h.lstrip('#')
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def mix(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def family_colours(tiers_file):
    cols = dict(FALLBACK)
    if tiers_file and Path(tiers_file).exists():
        try:
            rows = json.loads(Path(tiers_file).read_text(encoding='utf-8'))
            for r in sorted(rows, key=lambda r: r.get('position', 0)):
                fam, col = r.get('family') or r.get('tier'), r.get('color')
                if fam in cols and isinstance(col, str) and len(col.lstrip('#')) == 6:
                    cols[fam] = col
            print('rank emblems: the colours of %s' % tiers_file)
        except (OSError, ValueError) as e:
            print('rank emblems: %s not read (%s): the built-in colours' % (tiers_file, e))
    return cols


def emblem(family, colour, size=128, opaque=True):
    """the emblem on a transparent square (the icon slot), drawn 4x and scaled down"""
    k = 4 * size / 64.0
    W = 4 * size
    im = Image.new('RGBA', (W, W), (0, 0, 0, 0))
    if opaque:                                      # (the icon slot's own frame is white: a dark tile of the panel's colours under the emblem)
        bg = Image.new('RGBA', (W, W))
        bd = ImageDraw.Draw(bg)
        for y in range(W):
            bd.line([(0, y), (W, y)], fill=mix((0x24, 0x31, 0x42), (0x10, 0x18, 0x24), y / (W - 1)) + (255,))
        im = bg
    ox, oy = (W - 56 * k) / 2, (W - 62 * k) / 2
    pt = lambda p: [(ox + x * k, oy + y * k) for x, y in p]
    c = rgb(colour)
    unranked = family == 'Unranked'
    if not unranked and family in ('Master', 'Grandmaster'):      # (the launcher's glow: a soft shadow of the colour)
        glow = Image.new('RGBA', (W, W), (0, 0, 0, 0))
        ImageDraw.Draw(glow).polygon(pt(HEX), fill=c + (150,))
        im = Image.alpha_composite(im, glow.filter(ImageFilter.GaussianBlur(6 * k)))
    d = ImageDraw.Draw(im)
    top, bottom = (mix(DEEP, c, 0.28), DEEP) if not unranked else (rgb('#22344A'), DEEP)
    grad = Image.new('RGBA', (W, W))
    gd = ImageDraw.Draw(grad)
    for y in range(W):
        t = y / (W - 1)
        gd.line([(0, y), (W, y)], fill=mix(top, bottom, t) + (255,))
    mask = Image.new('L', (W, W), 0)
    ImageDraw.Draw(mask).polygon(pt(HEX), fill=255)
    im.paste(grad, (0, 0), mask)
    d = ImageDraw.Draw(im)
    edge = mix(c, (255, 255, 255), 0.35) if not unranked else rgb('#C3CEDA')
    d.line(pt(HEX) + pt(HEX[:1]), fill=edge + (255,), width=max(1, int(2 * k)), joint='curve')
    d.line(pt(HEX_IN) + pt(HEX_IN[:1]), fill=(c if not unranked else rgb('#B6D4E8')) + (85 if not unranked else 51,), width=max(1, int(k)))
    gem_line = c if not unranked else rgb('#AAB6C4')
    d.polygon(pt(GEM), fill=(c + (64,)) if not unranked else (255, 255, 255, 20))
    d.line(pt(GEM) + pt(GEM[:1]), fill=gem_line + (255,), width=max(1, int(2 * k)), joint='curve')
    core = mix(c, (255, 255, 255), 0.25) if not unranked else rgb('#7D8C9E')
    d.polygon(pt(STAR if family == 'Grandmaster' else CORE), fill=core + (255,))
    return im.resize((size, size), Image.LANCZOS)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--arc', required=True)
    ap.add_argument('--out', required=True, type=Path)
    ap.add_argument('--tiers')
    ap.add_argument('--png', type=Path, help='(tests) also the pictures as PNG in this folder')
    a = ap.parse_args()
    sys.path.insert(0, str(HERE.parent.parent / 'scripts'))
    from prepare_menu import Arc
    arc = Arc(a.arc)
    cols = family_colours(a.tiers)
    n = 0
    for i, (fam, slot) in enumerate(zip(FAMILIES, SLOTS)):
        path = 'ui/replace/chara/chara_0/chara_0_%s_00.bntx' % slot
        data = arc.get(path)
        slots = menu_textures.texture_slots(bytearray(data))
        imgs = {}
        for name, b in slots.items():
            w, h = struct.unpack_from('<II', data, b + 0x24)
            imgs[name] = emblem(fam, cols[fam], w).resize((w, h), Image.LANCZOS)
        dest = a.out / path
        dest.parent.mkdir(parents=True, exist_ok=True)
        tmp = dest.with_suffix('.tmp')
        tmp.write_bytes(texture_surface.replace_rgba(data, imgs))
        tmp.replace(dest)
        if a.png:
            a.png.mkdir(parents=True, exist_ok=True)
            next(iter(imgs.values())).save(a.png / ('%d_%s.png' % (i, fam)))
        n += 1
    print('rank emblems: %d textures in %s' % (n, a.out))


if __name__ == '__main__':
    main()
