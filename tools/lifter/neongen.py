"""(NEON generator 2, 2026-11-16) Rules of the generator applied to each lifted function of the game's units before it is compiled
(tu_prepare.py calls rewrite() on the function's lines). The generated files are not touched: the rewritten functions go into the
parts tu_prepare.py writes, so a build without the switches compiles exactly the code of before. Only with RECOMP_NEON_VEC (the
rewritten statements use runtime/neon_vec.h's whole-register helpers); build_opt.py passes the switches.

RECOMP_NEON_CONST=1  read-only constants folded at build time. main.elf's read-only data segment (R--, 0x39c8000..0x4f15f60 from the
    image base 0x7100000000; no relocation lands in it, and the runtime makes it read-only once the image is relocated: hostrt.c)
    holds the game's constant vectors: the index vectors of TBL and float constants, loaded through adrp + ldr. Inside one
    straight piece of a function (no jump lands in between, no call), x registers set to a constant are followed; a vector load
    from a constant address inside that segment becomes the constant itself (`NVK_LDK`: the same 16 bytes, no memory access), and
    a TBL / TBX whose index register holds a constant built this way (or by MOVI) becomes a fixed shuffle of whole 32-bit words
    when every index byte group picks an aligned word or lies outside the table (all the game's TBL constants seen: transposes /
    shuffles of float lanes): a pshufd / shufps / unpck the compiler picks, on every x86-64 CPU (no SSSE3, no CPU flag). Other index
    patterns keep the TBL helper. The constants come from the file this build lifts (RECOMP_BIN\\main.elf).
RECOMP_NEON_BITS=1  whole-register writes for what recomp.py writes as narrower pieces: AND / BIC / ORR / ORN / EOR of vectors (two
    64-bit halves), MOV of a vector (a 16-byte copy: a later 16-byte read of a register written by narrower stores stalls), MOVI
    (two 64-bit stores) and the scalar FP / int-to-vector writes `vzero(&v); v.f[0] = t` (three stores) become ONE 16-byte store of
    the same bytes (neon_vec.h nv_st), and a register is read lane by lane (nv_ld). The bits are those of before.

RECOMP_NEON_CHECK (test DLL): NVK_LDK also loads the memory and counts any difference, NVK_TBL also runs neon.h's v_tbl and counts any
difference, NVK_BITS also runs the 64-bit code (neon_vec.h rt_nchk[4..6]).
"""
import os
import re
import struct
import threading

RO_LO, RO_HI = 0x7100000000 + 0x39c8000, 0x7100000000 + 0x4f15f60       # main.elf R-- segment (checked when the image is read)
_img = None


def _image():
    """main.elf's R segment bytes (read once): (start address, bytes); None when the file or the segment is not as expected"""
    global _img
    if _img is None:
        _img = False
        b = os.environ.get('RECOMP_BIN')
        p = os.path.join(b, 'main.elf') if b else None
        if p and os.path.exists(p):
            with open(p, 'rb') as f:
                d = f.read()
            phoff, = struct.unpack_from('<Q', d, 0x20)
            phes, phn = struct.unpack_from('<HH', d, 0x36)
            for i in range(phn):
                t, fl, off, va, pa, fsz, msz, al = struct.unpack_from('<IIQQQQQQ', d, phoff + i * phes)
                # the one read-only, not executable load segment, exactly where it was when these rules were written
                if t == 1 and fl == 4 and 0x7100000000 + va == RO_LO and 0x7100000000 + va + fsz == RO_HI:
                    _img = (RO_LO, d[off:off + fsz])
    return _img or None


def _ro(addr, n):
    img = _image()
    if not img or addr < RO_LO or addr + n > RO_HI:
        return None
    o = addr - img[0]
    return img[1][o:o + n]


# set by build_opt.py (RECOMP_NEONGEN = the rules on: "const", "bits"; only with RECOMP_NEON_VEC)
ON_CONST = 'const' in (os.environ.get('RECOMP_NEONGEN') or '').split(',')
ON_BITS = 'bits' in (os.environ.get('RECOMP_NEONGEN') or '').split(',')
STATS = {}
_lock = threading.Lock()


def report():
    """one line for the build log"""
    if not (ON_CONST or ON_BITS):
        return 'NEON generator 2 rules: off'
    s = dict(STATS)
    return ('NEON generator 2 rules (%s): %d folded constant loads, %d TBL as fixed word shuffles (%d left: byte patterns), %d vector bit '
            'ops, %d vector moves, %d MOVI, %d scalar writes as one 16-byte store; main.elf read-only segment %s' %
            (os.environ.get('RECOMP_NEONGEN'), s.get('ldk', 0), s.get('tblk', 0), s.get('tbl_bytes', 0), s.get('bits', 0), s.get('vmov', 0),
             s.get('movi', 0), s.get('scal', 0), 'read' if _image() else 'NOT FOUND (no folding)'))

LABEL = re.compile(r'^(L_[0-9a-f]+): ')
GOTO = re.compile(r'goto (L_[0-9a-f]+)')
XCONST = re.compile(r'^c->x\[(\d+)\] = \(uint64_t\)\((0x[0-9a-fA-F]+|\d+)U?LL\);$')
XADD = re.compile(r'^c->x\[(\d+)\] = \(uint64_t\)\(c->x\[(\d+)\] ([+-]) (0x[0-9a-fA-F]+|\d+)U?LL\);$')
XWR = re.compile(r'c->x\[(\d+)\]\s*(?:[-+|&^]|<<|>>)?=(?!=)')
VWR = re.compile(r'c->v\[(\d+)\](?:\.\w+\[\d+\])?\s*=(?!=)')
VFIRST = re.compile(r'\b(\w+)\(&c->v\[(\d+)\]')
CALLC = re.compile(r'\b(\w+)\((?:[^()]*, )?c[,)]')
SAFE_C = {'cond', 'fcmp', 'set_nzcv', 'adds32', 'adds64', 'adds32f', 'adds64f', 'logic32', 'logic64', 'logic32f', 'logic64f',
          'subs32', 'subs64', 'v_tbl'}
# {  uint64_t a = c->x[B] (+ K)?; c->v[D] = rdv(a, S); ...  }
RDV_BLOCK = re.compile(r'^\{\s+uint64_t a = c->x\[(\d+)\](?: \+ (?:(0x[0-9a-fA-F]+|\d+)ULL|\(uint64_t\)(-?\d+)LL))?;((?: c->v\[\d+\] = rdv\(a(?: \+ \d+)?, (?:4|8|16)\);)+)\s+\}$')
RDV_ONE = re.compile(r'c->v\[(\d+)\] = rdv\(a(?: \+ (\d+))?, (4|8|16)\);')
TBL = re.compile(r'^v_tbl\(c, (\d+), (\d+), (\d+), (\d+), (\d+), (\d+)\);$')
MOVI = re.compile(r'^\{ c->v\[(\d+)\]\.d\[0\] = 0x([0-9a-f]+)ULL; c->v\[\1\]\.d\[1\] = (?:0x([0-9a-f]+)ULL|0); \}$')
VMOV = re.compile(r'^\{ V128 t = c->v\[(\d+)\];\s*(t\.d\[1\] = 0;)?\s*c->v\[(\d+)\] = t; \}$')
BITW = re.compile(r'^\{ V128 t; t\.d\[1\] = 0; \{ uint64_t a = c->v\[(\d+)\]\.d\[0\], b = c->v\[(\d+)\]\.d\[0\]; t\.d\[0\] = (a & b|a \^ b|a & ~b|a \| b|a \| ~b); \} '
                  r'(\{ uint64_t a = c->v\[\1\]\.d\[1\], b = c->v\[\2\]\.d\[1\]; t\.d\[1\] = \3; \} )? c->v\[(\d+)\] = t; \}$')
BITOP = {'a & b': 0, 'a ^ b': 1, 'a & ~b': 2, 'a | b': 3, 'a | ~b': 4}
SCAL = re.compile(r'^\{ (float|double|uint32_t|uint64_t) t = (.*); vzero\(&c->v\[(\d+)\]\); c->v\[\3\]\.(f|df|w|d)\[0\] = t; \}$')
SCAL_FN = {('float', 'f'): 'nv_st1f', ('double', 'df'): 'nv_st1df', ('uint32_t', 'w'): 'nv_st1w', ('uint64_t', 'd'): 'nv_st1d'}


def _tbl_words(idx16, n_tab, q, tbx):
    """[(source, word)] for the 4 result words, source 't<k>' / 'z' / 'd', or None when the bytes are not whole aligned words"""
    out = []
    for j in range(4):
        if j >= 2 and not q:
            out.append(('z', 0))
            continue
        b = idx16[4 * j:4 * j + 4]
        if all(x >= 16 * n_tab for x in b):
            out.append(('d', j) if tbx else ('z', 0))
        elif b[0] % 4 == 0 and b[1] == b[0] + 1 and b[2] == b[0] + 2 and b[3] == b[0] + 3:
            out.append(('t%d' % (b[0] // 16), (b[0] % 16) // 4))
        else:
            return None
    return out


def _tbl_expr(words, rd, rn, n_tab):
    """C text: the result vector as one shuffle of two sources, or a vector literal of words"""
    srcs = []
    for s, _ in words:
        if s not in srcs:
            srcs.append(s)
    loads = []
    names = {}
    for s in srcs:
        if s == 'z':
            names[s] = 'nvk_z'
        elif s == 'd':
            names[s] = 'nvk_d'
            loads.append('nv_u4 nvk_d = nv_ld(&c->v[%d]);' % rd)
        else:
            k = int(s[1:])
            names[s] = 'nvk_t%d' % k
            loads.append('nv_u4 nvk_t%d = nv_ld(&c->v[%d]);' % (k, (rn + k) & 31))
    if 'z' in srcs:
        loads.append('nv_u4 nvk_z = { 0, 0, 0, 0 };')
    if len(srcs) == 1:
        srcs.append(srcs[0])
    if len(srcs) == 2:
        a, b = srcs
        sel = ', '.join(str((0 if s == a else 4) + w) for s, w in words)
        expr = '__builtin_shufflevector(%s, %s, %s)' % (names[a], names[b], sel)
    else:
        expr = '(nv_u4){ %s }' % ', '.join('0' if s == 'z' else '%s[%d]' % (names[s], w) for s, w in words)
    return ' '.join(loads), expr


def _kill(x, v, s):
    """forget what the statement s may write (conservative): a call that may write anything empties both"""
    for m in CALLC.finditer(s):
        if m.group(1) not in SAFE_C:
            x.clear()
            v.clear()
            return
    if 'memcpy' in s or 'memset' in s:
        x.clear()
        v.clear()
        return
    for m in XWR.finditer(s):
        x.pop(int(m.group(1)), None)
    for m in VWR.finditer(s):
        v.pop(int(m.group(1)), None)
    for m in VFIRST.finditer(s):
        if m.group(1) != 'vget' and not m.group(1).startswith('wr') and not m.group(1).startswith('rd'):
            v.pop(int(m.group(2)), None)
    m = TBL.match(s)
    if m:
        v.pop(int(m.group(1)), None)


def _u64(v):
    return v & 0xffffffffffffffff


def _step(x, v, s, stats):
    """the statement s with x / v (the registers known to hold a constant before it; changed in place to the state after it) ->
    its new text or None (stats: counters, or None while the states are being found)"""
    def count(k, n=1):
        if stats is not None:
            stats[k] = stats.get(k, 0) + n
    if ON_CONST:
        mc = XCONST.match(s)
        if mc:
            x[int(mc.group(1))] = int(mc.group(2), 0)
            return None
        ma = XADD.match(s)
        if ma and int(ma.group(2)) in x and int(ma.group(2)) != 31:
            b = x[int(ma.group(2))]
            k = int(ma.group(4), 0)
            x[int(ma.group(1))] = _u64(b + k if ma.group(3) == '+' else b - k)
            return None
        mb = RDV_BLOCK.match(s)
        if mb and int(mb.group(1)) in x and int(mb.group(1)) != 31:
            base = _u64(x[int(mb.group(1))] + (int(mb.group(2), 0) if mb.group(2) else int(mb.group(3)) if mb.group(3) else 0))
            parts, vals = [], []
            for r in RDV_ONE.finditer(mb.group(4)):
                d, off, n = int(r.group(1)), int(r.group(2) or 0), int(r.group(3))
                by = _ro(base + off, n)
                if by is None:
                    parts = None
                    break
                w = struct.unpack('<4I', by + bytes(16 - n))
                parts.append('NVK_LDK(&c->v[%d], 0x%xULL, %d, 0x%08xu, 0x%08xu, 0x%08xu, 0x%08xu);' % ((d, base + off, n) + w))
                vals.append((d, w))
            if parts:
                for d, w in vals:
                    v[d] = w
                count('ldk', len(parts))
                return '{ ' + ' '.join(parts) + ' }'
        mt = TBL.match(s)
        if mt and int(mt.group(4)) in v:
            rd, rn, ln, rm, q, tbx = (int(g) for g in mt.groups())
            words = _tbl_words(list(struct.pack('<4I', *v[rm])), ln, q, tbx)
            v.pop(rd, None)
            if words is None:
                count('tbl_bytes')
                return None
            loads, expr = _tbl_expr(words, rd, rn, ln)
            count('tblk')
            return '{ %s NVK_TBL(c, %d, %d, %d, %d, %d, %d, (%s)); }' % (loads, rd, rn, ln, rm, q, tbx, expr)
    mm = MOVI.match(s)
    if mm:
        lo, hi = int(mm.group(2), 16), int(mm.group(3), 16) if mm.group(3) else 0
        w = (lo & 0xffffffff, lo >> 32, hi & 0xffffffff, hi >> 32)
        v[int(mm.group(1))] = w
        if ON_BITS:
            count('movi')
            return 'nv_st(&c->v[%d], (nv_u4){ 0x%08xu, 0x%08xu, 0x%08xu, 0x%08xu });' % ((int(mm.group(1)),) + w)
        return None
    mv = VMOV.match(s)
    if mv:
        src, dst = int(mv.group(1)), int(mv.group(3))
        w = v.get(src)
        if w is not None:
            v[dst] = (w[0], w[1], 0, 0) if mv.group(2) else w
        else:
            v.pop(dst, None)
        if ON_BITS:
            count('vmov')
            return 'nv_st(&c->v[%d], nv_ld(&c->v[%d])%s);' % (dst, src, ' & nv_lomask(2)' if mv.group(2) else '')
        return None
    _kill(x, v, s)
    if ON_BITS:
        mw = BITW.match(s)
        if mw:
            count('bits')
            return 'NVK_BITS(&c->v[%d], &c->v[%d], &c->v[%d], %d, %d);' % (int(mw.group(5)), int(mw.group(1)), int(mw.group(2)), BITOP[mw.group(3)],
                                                                          1 if mw.group(4) else 0)
        msc = SCAL.match(s)
        if msc and (msc.group(1), msc.group(4)) in SCAL_FN:
            count('scal')
            return '{ %s t = %s; %s(&c->v[%d], t); }' % (msc.group(1), msc.group(2), SCAL_FN[(msc.group(1), msc.group(4))], int(msc.group(3)))
    return None


def _meet(a, b):
    return {k: w for k, w in a.items() if b.get(k) == w}


def rewrite(name, lines):
    """lines: the function (definition line ... closing brace) -> rewritten lines (counted in STATS).
    The registers known to hold a constant before each statement come from a forward analysis over the function's jumps (a
    statement's state is what all the ways into it agree on; the function's entry, the targets of its switches (EH resume points,
    indirect jumps) and any line without a label know nothing; a call forgets everything). Functions without TBL: one pass in order
    (a statement knows only what the one before it left, when that is its only way in)."""
    if not (ON_CONST or ON_BITS):
        return lines
    body = lines[1:]
    n = len(body)
    pre, stm, lab = [], [], []
    for line in body:
        m = LABEL.match(line)
        if m:
            lab.append(m.group(1))
            stm.append(line[m.end():])
            pre.append(line[:m.end()])
        else:
            lab.append(None)
            stm.append(line.strip())
            pre.append(line[:len(line) - len(line.lstrip())])
    where = {l: i for i, l in enumerate(lab) if l}
    unknown = {0}
    succ = []
    for i, s in enumerate(stm):
        if lab[i] is None:
            unknown.add(i)
        tg = [where[g] for g in GOTO.findall(s) if g in where]
        nxt = [i + 1] if i + 1 < n else []
        if 'case ' in s or 'switch' in s:
            unknown.update(tg)
            succ.append(nxt)
        elif s.startswith('if ('):
            succ.append(tg + nxt)
        elif 'goto ' in s:
            succ.append(tg)
        elif s == 'return;' or s.endswith(' return; }') or s.endswith('; return;'):
            succ.append([])
        else:
            succ.append(nxt)
    preds = [[] for _ in range(n)]
    for i, ss in enumerate(succ):
        for j in ss:
            preds[j].append(i)
    out_x, out_v = [None] * n, [None] * n
    in_x, in_v = [None] * n, [None] * n
    simple = not ON_CONST or not any('v_tbl(' in s for s in stm)
    for it in range(1 if simple else 12):
        changed = False
        for i in range(n):
            if i in unknown:
                x, v = {}, {}
            elif simple:
                p = i - 1
                if p >= 0 and preds[i] == [p]:
                    x, v = dict(out_x[p]), dict(out_v[p])
                else:
                    x, v = {}, {}
            else:
                x = v = None
                for p in preds[i]:
                    if out_x[p] is None:
                        continue
                    x = dict(out_x[p]) if x is None else _meet(x, out_x[p])
                    v = dict(out_v[p]) if v is None else _meet(v, out_v[p])
                if x is None:
                    x, v = {}, {}
            in_x[i], in_v[i] = dict(x), dict(v)
            _step(x, v, stm[i], None)
            if x != out_x[i] or v != out_v[i]:
                out_x[i], out_v[i] = x, v
                changed = True
        if not changed:
            break
    else:
        in_x = [{}] * n                                       # (not seen) no fixed point: nothing known anywhere
        in_v = [{}] * n
    stats = {}
    out = [lines[0]]
    for i in range(n):
        new = _step(dict(in_x[i]), dict(in_v[i]), stm[i], stats)
        out.append(pre[i] + new if new else body[i])
    with _lock:
        for k, c in stats.items():
            STATS[k] = STATS.get(k, 0) + c
    return out
