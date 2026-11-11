"""Check every game address the runtime names against the main.elf of a build other than 13.0.4 (docs/VERSIONS.md).

    python tools/port/verify_build.py <main.elf> <functions.csv> [--build 13.0.2|13.0.5] [--ref <13.0.4 main.elf>]

The build is told by the size of the code (as hostrt.c load_layout does); --build makes a different size a failure.
The sources name each game address by its 13.0.4 value and pass it through GA() (runtime/hostrt.c), which gives the
address in the build that runs. This script finds those addresses in runtime\\ and fighter_sdk\\ and checks what GA()
answers on that build:
  * code (below 0x39c8000): the address must be in the build's table of hostrt.c (g_code_13_0_5, g_code_13_0_2; ga()
    looks for any other by its code), the table's delta must be the one at which the same-size functions of the
    decompilation list line up in the build's own unwind table (.eh_frame_hdr), on both sides of the address or for the
    address's own function, and when the address has a fingerprint (ga_sig_13_0_4.inc) the search by code that ga() does
    must find the table's place
  * read-only data: 13.0.5 one page (0x1000) later; 13.0.2 kept the segment's start but moved parts of it by different
    amounts, so each address must be in g_ro_13_0_2, the build's code must form the new address (ADRP + ADD/LDR/STR) and,
    with --ref, the 64 bytes there must be the 13.0.4 bytes at the old address
  * data and bss: one page (0x1000) later in both; the build's code must form the new address more often than the old one
It also checks the layout ga() assumes (segment starts and sizes) and that no 0x71xxxxxxxx literal in code bypasses GA()
unless it is listed below with its reason.
<functions.csv> is data/functions.csv of the matching decompilation (sbergeron42/ssbu-decomp): 13.0.4 function addresses
and sizes (names and addresses only, no code). Nothing of the game is stored here; all inputs come from outside.
Exit code 1 when anything fails."""
import bisect
import collections
import csv
import os
import re
import struct
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
BASE = 0x7100000000
RO4 = 0x39c8000          # 13.0.4: start of the read-only data segment; everything below is code
TEXT4 = 0x39c7e90        # 13.0.4: size of the code
DATA4 = 0x4f16000        # 13.0.4: start of the data segment
# what ga() assumes of each build: code size, read-only data (start, size), data (start, size in the file), the table of
# code addresses, and either the table of read-only data addresses or the one delta of that segment
BUILDS = {'13.0.5': dict(text=0x39c8440, ro=(0x39c9000, 0x154df60), data=(0x4f17000, 0x392570), code='g_code_13_0_5', ro_table=None, ro_delta=0x1000),
          '13.0.2': dict(text=0x39c7a90, ro=(0x39c8000, 0x154e0a0), data=(0x4f17000, 0x392570), code='g_code_13_0_2', ro_table='g_ro_13_0_2', ro_delta=None)}
# literals that are not passed through GA(), and why
RAW = {0x0: 'the image base',
       0x1000: 'a test value in the first page of code (cxxrt_selftest.h), the same in every build',
       0x4000000: 'a test value outside any function (cxxrt_selftest.h): read-only data in both builds',
       0x147f330: 'g_nm_path_fn[] (newmodule_host.c): GA() where the array is used',
       0x17df5f0: 'g_nm_path_fn[]', 0x17dfe50: 'g_nm_path_fn[]', 0x353e330: 'g_nm_path_fn[]',
       0x356d7a0: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x3575c60: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x3575fa0: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x35763c0: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x3593c40: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x35c13d0: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x36186d0: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x3618b80: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x3618ef0: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x3619080: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x361d040: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x361e7e0: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x3632850: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x36e6e00: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x36f2c00: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x36f2d40: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x36f7410: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x37857f0: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x3864700: 'g_rgl_h[] (r1_ring.c): GA() where the array is used',
       0x7500000: 'the upper bound of the "main+offset" log labels (r1_state.c), past the image in both builds',
       0x47d20: 'site[] (nnsdk.c host_fixed_clock): GA() where the array is used',
       0x138534: 'site[]', 0x366d1f0: 'site[]', 0x366d388: 'site[]',
       0x81980: 'big3[] (rb_learned.c): GA() where the array is used',
       0xa3fd8: 'big3[]', 0x3559e08: 'big3[]', 0x3760cf8: 'big3[]', 0x3816944: 'big3[]',
       0x530454c: 'k_rules[] (competitive.c): GA() where the array is used',
       0x5309924: 'k_rules[]', 0x530f49c: 'k_rules[]',
       0x1a028b0: 'the amiibo panel hooks (nnapplet.c): installed on 13.0.4 only',
       0x1a1dbf0: 'the amiibo panel hooks', 0x1a1fa30: 'the amiibo panel hooks',
       0x52c41b0: 'through ga() itself (rb_net.c declares it and does not include hostrt.h, where GA() is)',
       0x22df110: 'g_rbg_skipf_def (rb_rollback.c): a string, GA() where it is read', 0x22dec30: 'g_rbg_skipf_def', 0x22dc3d0: 'g_rbg_skipf_def', 0x1357e20: 'g_rbg_skipf_def', 0x1358ef0: 'g_rbg_skipf_def', 0x375ea70: 'g_rbg_skipf_def',
       0x80000000: 'a free host range for a test stack (rb_rollback.c, SSBU_RB_NET_TEST_STK), not an address of the image'}

# data addresses that are the bounds of a range, not of an object (no code forms them): only the one-page rule is checked
BOUNDS = {0x5000000: 'lower bound of the graphics descriptor range (r1_ring.c sd_clear), inside data / bss',
          0x5400000: 'upper bound of the graphics descriptor range (r1_ring.c sd_clear), inside data / bss'}


class Elf:
    def __init__(self, path):
        self.b = open(path, 'rb').read()
        phoff, = struct.unpack_from('<Q', self.b, 0x20)
        phnum, = struct.unpack_from('<H', self.b, 0x38)
        self.segs = []
        for i in range(phnum):
            t, _, off, va, _, fsz, msz, _ = struct.unpack_from('<IIQQQQQQ', self.b, phoff + i * 56)
            if t == 1:
                self.segs.append((va, off, fsz, msz))

    def rd(self, va, n):
        for v, o, s, _ in self.segs:
            if v <= va < v + s:
                return self.b[o + va - v:o + va - v + n]
        raise KeyError(hex(va))

    def u32(self, va):
        return struct.unpack('<I', self.rd(va, 4))[0]

    def s32(self, va):
        return struct.unpack('<i', self.rd(va, 4))[0]


def unwind_functions(e):
    """{start: size} from .eh_frame_hdr (found through MOD0)"""
    m = e.u32(4)
    assert e.rd(m, 4) == b'MOD0'
    eh = m + e.s32(m + 16)
    assert tuple(e.rd(eh, 4)[1:]) == (0x1b, 0x03, 0x3b), 'unexpected .eh_frame_hdr encoding'
    out = {}
    for i in range(e.u32(eh + 8)):
        loc = eh + e.s32(eh + 12 + i * 8)
        fde = eh + e.s32(eh + 16 + i * 8)
        out[loc] = e.u32(fde + 12)
    return out


def adrp_targets(e, text_size):
    """how often code forms each address with ADRP + ADD / LDR / STR (unsigned offset) on the same register"""
    words = struct.unpack('<%dI' % (text_size // 4), e.rd(0, text_size // 4 * 4))
    refs = collections.Counter()
    for i, x in enumerate(words):
        if (x & 0x9f000000) != 0x90000000:
            continue
        rd = x & 31
        imm = (((x >> 5) & 0x7ffff) << 2) | ((x >> 29) & 3)
        if imm & (1 << 20):
            imm -= 1 << 21
        page = ((i * 4) & ~0xfff) + (imm << 12)
        for y in words[i + 1:i + 6]:
            if (y >> 5) & 31 != rd:
                continue
            if (y & 0xff800000) == 0x91000000:
                refs[page + ((y >> 10) & 0xfff)] += 1
            elif (y & 0x3b000000) == 0x39000000:
                refs[page + (((y >> 10) & 0xfff) << ((y >> 30) & 3))] += 1
            break
    return refs


def code_table(name='g_code_13_0_5'):
    """a table of runtime/hostrt.c (g_code_13_0_5, g_code_13_0_2, g_ro_13_0_2): {13.0.4 offset: offset in that build}"""
    s = open(os.path.join(ROOT, 'runtime', 'hostrt.c'), encoding='utf-8').read()
    block = s[s.index(name + '[][2] = {'):]
    block = block[:block.index('};')]
    pairs = [(int(a, 16), int(b, 16)) for a, b in re.findall(r'\{0x([0-9a-fA-F]+), 0x([0-9a-fA-F]+)\}', block)]
    assert [a for a, _ in pairs] == sorted(a for a, _ in pairs), '%s is not sorted (ga() searches it by halves)' % name
    assert len(set(a for a, _ in pairs)) == len(pairs), '%s has an address twice' % name
    return dict(pairs)


def fingerprints():
    """runtime/ga_sig_13_0_4.inc: {13.0.4 offset: (words before, words, hash)}"""
    s = open(os.path.join(ROOT, 'runtime', 'ga_sig_13_0_4.inc')).read()
    return {int(a, 16): (int(b), int(n), int(h, 16)) for a, b, n, h in re.findall(r'\{0x([0-9a-f]+), (\d+), (\d+), 0x([0-9a-f]+)ull\}', s)}


def search_by_code(words, text, a, sig, span=0x1000):
    """what hostrt.c ga_find answers: the one place within +-span whose fingerprint hash is the 13.0.4 one, or None"""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    import ga_sigs
    back, n, h = sig
    s0 = a - 4 * back
    hits = [d for d in range(-span, span + 1, 4) if 0 <= s0 + d and s0 + d + 4 * n <= text and ga_sigs.fnv(words[(s0 + d) // 4:(s0 + d) // 4 + n]) == h]
    return a + hits[0] if len(hits) == 1 else None


def source_addresses():
    """{13.0.4 offset: [where, ...]} of every 0x71xxxxxxxx literal in code (comments left out), and the literals not in GA()"""
    wrapped, raw = collections.defaultdict(list), collections.defaultdict(list)
    for top in ('runtime', 'fighter_sdk'):
        for d, dirs, files in os.walk(os.path.join(ROOT, top)):
            for f in files:
                if not f.endswith(('.c', '.h', '.inc')):
                    continue
                p = os.path.join(d, f)
                s = open(p, encoding='utf-8', errors='replace').read()
                s = re.sub(r'/\*.*?\*/', lambda m: '\n' * m.group(0).count('\n'), s, flags=re.S)
                s = re.sub(r'//[^\n]*', '', s)
                rel = os.path.relpath(p, ROOT).replace('\\', '/')
                for n, line in enumerate(s.split('\n'), 1):
                    for m in re.finditer(r'(GA\(\s*)?(?:0x71([0-9a-fA-F]{8})(?:ull|ULL)|GUEST_BASE \+ 0x([0-9a-fA-F]{4,})(?:ull|ULL)?)\b', line):
                        (wrapped if m.group(1) else raw)[int(m.group(2) or m.group(3), 16)].append('%s:%d' % (rel, n))
    return wrapped, raw


def main(argv=None):
    args = list(sys.argv[1:] if argv is None else argv)
    want = ref = None
    for opt in ('--build', '--ref'):
        if opt in args:
            k = args.index(opt)
            if k + 1 >= len(args):
                sys.exit(__doc__)
            v = args[k + 1]
            del args[k:k + 2]
            if opt == '--build':
                want = v
            else:
                ref = Elf(v)
    if len(args) != 2 or (want and want not in BUILDS):
        sys.exit(__doc__)
    e = Elf(args[0])
    bad = 0

    def report(ok, text):
        nonlocal bad
        bad += not ok
        print('%s %s' % ('ok  ' if ok else 'FAIL', text))

    text, ro, data = e.segs[0], e.segs[1], e.segs[2]
    build = want or next((b for b, x in BUILDS.items() if x['text'] == text[2]), None)
    if build is None:
        sys.exit('code size %#x: not a build this script knows (%s)' % (text[2], ', '.join('%s %#x' % (b, x['text']) for b, x in BUILDS.items())))
    B = BUILDS[build]
    print('build %s' % build)
    report(text[2] == B['text'], '%s code size %#x (hostrt.c load_layout knows %#x)' % (build, text[2], B['text']))
    report((ro[0], ro[2]) == B['ro'], 'read-only data at %#x, %#x bytes (ga() assumes %#x, %#x)' % (ro[0], ro[2], B['ro'][0], B['ro'][1]))
    report((data[0], data[2]) == B['data'] and data[0] == DATA4 + 0x1000,
           'data at %#x, %#x bytes in the file: 13.0.4 + 0x1000, same size (the rule of ga() for data and bss)' % (data[0], data[2]))
    if ref is not None:
        report(ref.segs[0][2] == TEXT4, '--ref is 13.0.4 (code size %#x)' % ref.segs[0][2])

    fde = unwind_functions(e)
    funcs = sorted((int(r['Address'], 16) - BASE, int(r['Size'])) for r in csv.DictReader(open(args[1])))
    funcs = [f for f in funcs if f[0] < RO4]
    starts = [f[0] for f in funcs]

    def delta_near(a, k=25):
        i = bisect.bisect_right(starts, a)
        res = []
        for side in (funcs[max(0, i - k):i], funcs[i:i + k]):
            best = max(range(-0x800, 0x801, 4), key=lambda d: (sum(fde.get(s + d) == z for s, z in side), -abs(d)))
            res.append((best, sum(fde.get(s + best) == z for s, z in side)))
        own = funcs[i - 1] if i and a < funcs[i - 1][0] + funcs[i - 1][1] else None
        return res, own

    table = code_table(B['code'])
    ro_table = code_table(B['ro_table']) if B['ro_table'] else {}
    sigs = fingerprints()
    code = e.rd(0, text[2])
    words = struct.unpack('<%dI' % (len(code) // 4), code[:len(code) // 4 * 4])
    wrapped, raw = source_addresses()
    for a, where in sorted(raw.items()):
        report(a in RAW, '0x71%08x not passed through GA() (%s): %s' % (a, ', '.join(where[:3]), RAW.get(a, 'NOT EXPLAINED')))
    names = set(wrapped) | {a for a in raw if a in table}
    for a in sorted((set(table) | set(ro_table)) - names):
        report(False, 'table entry 0x71%08x is not used by any source' % a)
    refs = adrp_targets(e, text[2])
    for old in sorted(names):
        where = ', '.join((wrapped.get(old) or raw.get(old))[:2])
        if old < RO4:
            if old not in table:
                report(False, '0x71%08x (%s): code address missing from %s' % (old, where, B['code']))
                continue
            new = table[old]
            d = new - old
            ((db, nb), (da, na)), own = delta_near(old)
            own_ok = own is not None and fde.get(own[0] + d) == own[1]
            ok = old < TEXT4 and (own_ok or (db == d and da == d) or (d in (db, da) and max(nb, na) >= 15))
            sig = ''
            if old in sigs:
                r = search_by_code(words, text[2], old, sigs[old])
                # a place inside a function that changed size: its own code says where it is, and the delta lies between
                # those of the functions on either side
                inside = r == new and old < TEXT4 and min(db, da) < d < max(db, da) and min(nb, na) >= 10
                ok = (ok or inside) and r == new
                sig = ', by its code %s%s' % ('the same' if r == new else ('0x71%08x' % r if r is not None else 'not found'),
                                              ' (inside a changed function: between its sides)' if inside else '')
            report(ok, '0x71%08x -> 0x71%08x  %+#7x  neighbours %+#x (%d/25) / %+#x (%d/25)%s%s  [%s]'
                   % (old, new, d, db, nb, da, na, ', own function same size' if own_ok else '', sig, where))
        elif old < DATA4 and old not in BOUNDS:
            if B['ro_table'] is None:
                new = old + B['ro_delta']
                report(refs[new] > refs[old], '0x71%08x -> 0x71%08x  %+#x  code references: new %d, old %d  [%s]' % (old, new, B['ro_delta'], refs[new], refs[old], where))
                continue
            if old not in ro_table:
                report(False, '0x71%08x (%s): read-only data address missing from %s' % (old, where, B['ro_table']))
                continue
            new = ro_table[old]
            ok = refs[new] > 0 and (new == old or refs[new] > refs[old])
            same = ''
            if ref is not None:
                same = ref.rd(old, 64) == e.rd(new, 64)
                ok = ok and same
                same = ', 64 bytes as 13.0.4 at the old address' if same else ', bytes DIFFER from 13.0.4'
            report(ok, '0x71%08x -> 0x71%08x  %+#x  code references: new %d, old %d%s  [%s]' % (old, new, new - old, refs[new], refs[old], same, where))
        else:
            new = old + 0x1000
            if old in BOUNDS:
                report(data[0] <= new < data[0] + data[3] + 0x1000, '0x71%08x -> 0x71%08x  +0x1000  %s  [%s]' % (old, new, BOUNDS[old], where))
                continue
            report(refs[new] > refs[old], '0x71%08x -> 0x71%08x  +0x1000  code references: new %d, old %d  [%s]' % (old, new, refs[new], refs[old], where))
    print('%d checks failed' % bad if bad else 'all checks passed')
    sys.exit(1 if bad else 0)


if __name__ == '__main__':
    main()
