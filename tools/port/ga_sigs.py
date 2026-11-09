"""Code fingerprints of the 13.0.4 code addresses the runtime names, for GA() on 13.0.5 and 13.0.2 (docs/VERSIONS.md).

    python tools/port/ga_sigs.py <13.0.4 main.elf>          writes runtime/ga_sig_13_0_4.inc

g_code_13_0_5 (runtime/hostrt.c) lists the 13.0.5 counterpart of each code address that was checked on a 13.0.5 dump. A
code address not in it used to stop the program on 13.0.5. For those, GA() now looks for the same code near the address
in the 13.0.5 image it runs: code moved by at most +-0x5b0 between the builds (docs/VERSIONS.md), so it hashes the
instructions at every word within +-0x1000 of the 13.0.4 address and takes the one place whose hash is the 13.0.4 one.

The hash is FNV-1a 64 over N instruction words from the address, with the parts that change when code moves masked out
(branch and literal offsets, ADRP pages and the low 12 bits that ADD / LDR / STR then add to an ADRP register); runtime/
hostrt.c ga_mask does the same. This script picks for each address the smallest N (and, when the code from the address repeats nearby,
some words before it too) whose hash is unique within +-0x1000 on 13.0.4 itself; an address with no such window gets no
fingerprint and still stops on 13.0.5. The g_code_13_0_5 entries get one too: SSBU_GA_CHECK=1 checks the table against
the search on a 13.0.5 image, which also shows whether the search finds the right places there.
The file holds addresses and hashes only, nothing of the game's code. Run it again after adding a game address."""
import glob
import os
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
TEXT4 = 0x39c7e90
SPAN = 0x1000
SIZES = (16, 24, 32, 48, 64)
BACKS = (0, 8, 16, 32)          # words hashed before the address too, when the code from it repeats nearby


def mask(w, adrp):
    if w & 0x9f000000 == 0x90000000:                     # ADRP
        adrp[0] |= 1 << (w & 31)
        return w & 0x9f00001f
    if w & 0x9f000000 == 0x10000000: return w & 0x9f00001f      # ADR
    if w & 0x7c000000 == 0x14000000: return w & 0xfc000000      # B, BL
    if w & 0xff000010 == 0x54000000: return w & 0xff00001f      # B.cond
    if w & 0x7e000000 == 0x34000000: return w & 0xff00001f      # CBZ, CBNZ
    if w & 0x7e000000 == 0x36000000: return w & 0xfff8001f      # TBZ, TBNZ
    if w & 0x3b000000 == 0x18000000: return w & 0xff00001f      # LDR (literal)
    if (adrp[0] >> ((w >> 5) & 31)) & 1:
        if w & 0x7f800000 == 0x11000000: return w & ~0x003ffc00 & 0xffffffff     # ADD (immediate) on an ADRP register
        if w & 0x3b000000 == 0x39000000: return w & ~0x003ffc00 & 0xffffffff     # LDR / STR (unsigned offset) on one
    return w


def fnv(words):
    h, adrp = 0xcbf29ce484222325, [0]
    for w in words:
        m = mask(w, adrp)
        for k in range(4):
            h = ((h ^ ((m >> (8 * k)) & 0xff)) * 0x100000001b3) & 0xffffffffffffffff
    return h


def code_of(elf):
    d = open(elf, 'rb').read()
    phoff, = struct.unpack_from('<Q', d, 0x20)
    phnum, = struct.unpack_from('<H', d, 0x38)
    for i in range(phnum):
        typ, _fl, off, va, _pa, fsz = struct.unpack_from('<IIQQQQ', d, phoff + 56 * i)
        if typ == 1 and va == 0:
            if fsz != TEXT4:
                sys.exit('%s: code size %#x, not 13.0.4 (%#x)' % (elf, fsz, TEXT4))
            return d[off:off + fsz]
    sys.exit('%s: no code segment' % elf)


def table_entries():
    """the 13.0.4 code offsets of every build's code table (g_code_13_0_5, g_code_13_0_2)"""
    s = open(os.path.join(ROOT, 'runtime', 'hostrt.c'), encoding='utf-8').read()
    out = set()
    for name in ('g_code_13_0_5', 'g_code_13_0_2'):
        t = s[s.index(name + '[][2] = {'):]
        t = t[:t.index('};')]
        out |= set(int(m, 16) for m in re.findall(r'\{0x([0-9a-f]+),\s*0x[0-9a-f]+\}', t))
    return out


def named_addresses():
    out = set()
    for top in ('runtime', 'fighter_sdk'):
        for f in glob.glob(os.path.join(ROOT, top, '**', '*'), recursive=True):
            if not re.search(r'\.(c|h|cpp|inc)$', f) or f.endswith('ga_sig_13_0_4.inc'):
                continue
            for m in re.finditer(r'0x(71[0-9a-fA-F]{8})', open(f, encoding='utf-8', errors='ignore').read()):
                a = int(m.group(1), 16) - 0x7100000000
                if 0x1000 < a < TEXT4 and a % 4 == 0:
                    out.add(a)
    return out


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    code = code_of(sys.argv[1])
    words = struct.unpack('<%dI' % (len(code) // 4), code[:len(code) // 4 * 4])
    known = table_entries()
    rows, none = [], []
    for a in sorted(named_addresses() | known):
        for back, n in [(b, n) for b in BACKS for n in SIZES]:
            s0 = a - 4 * back
            if s0 < 0 or s0 + 4 * n > TEXT4:
                continue
            h = fnv(words[s0 // 4:s0 // 4 + n])
            hits = [d for d in range(-SPAN, SPAN + 1, 4)
                    if 0 <= s0 + d and s0 + d + 4 * n <= TEXT4 and fnv(words[(s0 + d) // 4:(s0 + d) // 4 + n]) == h]
            if hits == [0]:
                rows.append((a, back, n, h))
                break
        else:
            none.append(a)
    p = os.path.join(ROOT, 'runtime', 'ga_sig_13_0_4.inc')
    with open(p, 'w', newline='\n') as f:
        f.write('/* made by tools/port/ga_sigs.py from a 13.0.4 main.elf: 13.0.4 code offset, words before it, words hashed, hash (hostrt.c ga_find) */\n')
        for a, back, n, h in rows:
            f.write('    {0x%07x, %d, %d, 0x%016xull},\n' % (a, back, n, h))
    print('%d fingerprints (%d for entries of the build tables: SSBU_GA_CHECK=1 checks those), %d without one: %s'
          % (len(rows), len(known & set(r[0] for r in rows)), len(none), ' '.join('%#x' % a for a in none) or '-'))


if __name__ == '__main__':
    main()
