"""The masks of the launcher's complete save (tools/launcher/arena/BaseSave.cs, Complete): which save numbers of the game's
lists are base-game content, read from YOUR data.arc (nothing of the game is kept: only the bit masks it prints).

    python tools/save/complete_save_masks.py [--arc <data.arc>]

Prints, as hex (bit n = save_no n, little endian), the masks BaseSave.cs holds:
  BGM     ui_bgm_db rows with save_no >= 0 and not DLC (is_dlc 0)                  save +0x1d0 (owned), +0x5b45dc (heard)
  HATS    ui_mii_hat_db rows not added by a paid update (is_patch 0)                save +0x20
  BODIES  ui_mii_body_db rows not added by a paid update (is_patch 0)               save +0x54
  MOVIES  ui_movie_db rows not DLC                                                  save +0x30c
  HELP    the "how to play" pages a mode shows the first time (save_no >= 0)         save +0x310
  CHALLENGES the count of ui challenge rows ("clear_getter_*", the Challenges board) save +0x555bb8, 3 bits each
Generated 2026-11-10 from the 13.0.4 data.arc.
"""
import argparse
import os
import struct
import sys
import zlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'nro'))
from arc_extract import Arc     # noqa: E402

M40 = 0xffffffffff
DB = 'ui/param/database/'
# file names by their hash40 (the archive keeps no names): the challenges' list, the how-to-play pages
CHALLENGE_DB, HELP_DB = 0x1676e4c0c4, 0x13498650ff


def h40(s):
    b = s.lower().encode()
    return (len(b) << 32) | zlib.crc32(b)


def parse(d):
    hs, rs = struct.unpack_from('<II', d, 8)
    hashes = [struct.unpack_from('<Q', d, 16 + i * 8)[0] & M40 for i in range(hs // 8)]
    base, pb = 16 + hs, 16 + hs + rs
    fmt = {1: '<B', 2: '<b', 3: '<B', 4: '<h', 5: '<H', 6: '<i', 7: '<I', 8: '<f'}

    def walk(p):
        t = d[pb + p]
        if t == 12:
            n, ref = struct.unpack_from('<II', d, pb + p + 1)
            return {hashes[h]: walk(p + off) for h, off in (struct.unpack_from('<II', d, base + ref + i * 8) for i in range(n))}
        if t == 11:
            n = struct.unpack_from('<I', d, pb + p + 1)[0]
            return [walk(p + struct.unpack_from('<I', d, pb + p + 5 + i * 4)[0]) for i in range(n)]
        if t == 10:
            o = struct.unpack_from('<I', d, pb + p + 1)[0]
            return d[base + o:d.index(b'\0', base + o)].decode('utf-8', 'replace')
        if t == 9:
            return hashes[struct.unpack_from('<I', d, pb + p + 1)[0]]
        return struct.unpack_from(fmt[t], d, pb + p + 1)[0]
    return walk(0)


def rows(arc, name):
    key = h40(DB + name) if isinstance(name, str) else None
    if key is not None:
        d = arc.get(key)
    else:
        d = next(arc.read(r) for r in arc.list_ext('prc') if r['name'] == name and r['parent'] == h40(DB))
    return [r for r in parse(d)[h40('db_root')] if r[h40('save_no')] >= 0]


def mask(rs, keep):
    n = max(r[h40('save_no')] for r in rs) + 1
    b = bytearray((n + 7) // 8)
    for r in rs:
        if keep(r):
            s = r[h40('save_no')]
            b[s >> 3] |= 1 << (s & 7)
    return bytes(b)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--arc', default=os.environ.get('SSBU_ARC') or os.path.join(HERE, '..', '..', 'romfs', 'data.arc'))
    a = ap.parse_args()
    arc = Arc(a.arc)
    out = {
        'BGM': mask(rows(arc, 'ui_bgm_db.prc'), lambda r: not r.get(h40('is_dlc'))),
        'HATS': mask(rows(arc, 'ui_mii_hat_db.prc'), lambda r: not r.get(h40('is_patch'))),
        'BODIES': mask(rows(arc, 'ui_mii_body_db.prc'), lambda r: not r.get(h40('is_patch'))),
        'MOVIES': mask(rows(arc, 'ui_movie_db.prc'), lambda r: not r.get(h40('is_dlc'))),
        'HELP': mask(rows(arc, HELP_DB), lambda r: True),
    }
    for k, v in out.items():
        print('%-8s %3d bits of %4d: %s' % (k, sum(bin(x).count('1') for x in v), len(v) * 8, v.hex()))
    print('CHALLENGES %d' % len(rows(arc, CHALLENGE_DB)))


if __name__ == '__main__':
    main()
