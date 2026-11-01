#!/usr/bin/env python3
"""The lifted functions with a register branch (BR Xn), without a merge point at every instruction (rollback 8 ms).

    python gensplit.py <gen dir in> <gen dir out> <main.elf> [coverage list]

recomp_eh.py ends such a function with `L_dispatch: switch (jt)` over ALL its instructions, since the branch may lead anywhere in it: every
guest instruction is then a merge point and the compiler can keep nothing in registers from one to the next. Nearly all those branches are
tail calls or jump tables. Here (only for functions of the coverage list; the others stay as they are):
  - the function keeps in its switch only the targets of its jump tables (read from the binary at each BR: adrp + add ... br);
  - any other target inside the function goes to s_<addr>(c, jt): a copy of the function as the lifter wrote it, entered at the switch. All
    registers are in the Ctx at a BR (it is a jump out of the optimised code), so that is exact, only slower; a target outside the function is a
    tail call as before. So a missed jump table costs speed, never correctness.
Input and output are the lifter's C; funcs.h gets the declarations of the copies."""
import os, re, struct, sys

FN = re.compile(r'^(void ([fu])_([0-9a-f]+)\(Ctx \*c(, uint64_t rp)?\) \{)$')
BR = re.compile(r'^L_([0-9a-f]+): \{ jt = c->x\[(\d+)\]; goto L_dispatch; \}$')
BASE = 0x7100000000


def elf(path):
    f = open(path, 'rb').read()
    phoff = struct.unpack_from('<Q', f, 0x20)[0]; phes, phn = struct.unpack_from('<HH', f, 0x36)
    segs = []
    for i in range(phn):
        t, fl, off, va, pa, fs, ms = struct.unpack_from('<IIQQQQQ', f, phoff + i * phes)
        if t == 1: segs.append((va, off, fs))
    return f, segs


def rd(f, segs, a, n):
    v = a - BASE
    for va, off, fs in segs:
        if va <= v and v + n <= va + fs:
            return f[off + v - va:off + v - va + n]
    return None


def tables(f, segs, pc, lo, hi):
    """Targets in [lo, hi) of a jump table used by the BR at pc: 32-bit, 16-bit or 8-bit entries, found from the adrp + add before it"""
    out = set()
    regs = {}
    for k in range(64, 0, -1):
        w = rd(f, segs, pc - 4 * k, 4)
        if not w: continue
        w = struct.unpack('<I', w)[0]; rdn = w & 31; p = pc - 4 * k
        if (w & 0x9f000000) == 0x90000000:    # ADRP
            imm = ((w >> 29) & 3) | (((w >> 5) & 0x7ffff) << 2)
            if imm & (1 << 20): imm -= 1 << 21
            regs[rdn] = (p & ~0xfff) + (imm << 12)
        elif (w & 0x9f000000) == 0x10000000:  # ADR
            imm = ((w >> 29) & 3) | (((w >> 5) & 0x7ffff) << 2)
            if imm & (1 << 20): imm -= 1 << 21
            regs[rdn] = p + imm
        elif (w & 0xff800000) == 0x91000000:  # ADD Xd, Xn, #imm
            rn = (w >> 5) & 31; imm = (w >> 10) & 0xfff
            if (w >> 22) & 1: imm <<= 12
            if rn in regs: regs[rdn] = regs[rn] + imm
    addrs = set(regs.values())
    for t in addrs:
        if lo <= t < hi: continue
        for size, fmt in ((4, '<i'), (2, '<H'), (1, '<B')):
            n = 0
            while n < 4096:
                b = rd(f, segs, t + n * size, size)
                if not b: break
                o = struct.unpack(fmt, b)[0]
                ok = False
                if size == 4:
                    a = t + o
                    if lo <= a < hi and not a & 3: out.add(a); ok = True
                else:                         # relative to any anchor before the BR (adr): x4
                    for an in addrs:
                        a = an + o * 4
                        if lo <= a < hi: out.add(a); ok = True
                if not ok: break
                n += 1
            if size == 4 and n >= 2: break
    return out


def process(text, f, segs, cov, stats):
    lines = text.split('\n'); out = []; decl = []; i = 0
    while i < len(lines):
        m = FN.match(lines[i])
        if not m:
            out.append(lines[i]); i += 1; continue
        e = i
        while lines[e] != '}': e += 1
        body = lines[i:e + 1]
        addr = int(m.group(3), 16)
        sw = next((k for k, l in enumerate(body) if l == 'L_dispatch: switch (jt) {'), -1)
        if sw < 0 or (cov is not None and addr not in cov):
            out.extend(body); i = e + 1; stats['plain'] += 1; continue
        labs = [int(l[2:l.index(':')], 16) for l in body if l.startswith('L_') and l[2:3] != 'd']
        lo, hi = min(labs), max(labs) + 4
        keep = set()
        for l in body:
            b = BR.match(l)
            if b: keep |= tables(f, segs, int(b.group(1), 16), lo, hi)
        keep &= set(labs)
        stats['split'] += 1; stats['all'] += len(labs); stats['kept'] += len(keep)
        name = 's_%x' % addr
        decl.append('void %s(Ctx *c, uint64_t jt0);' % name)
        # the copy: as the lifter wrote it, entered at the switch
        cp = ['void %s(Ctx *c, uint64_t jt0) {' % name]
        rest = body[1:]
        cp.append('  uint64_t rp = 0;' if m.group(4) else '  ;')
        for l in rest:
            cp.append(l)
            if l == '  uint64_t jt = 0;': cp.append('  jt = jt0; goto L_dispatch;')
        # the fast one
        fast = body[:sw + 1]
        fast += ['  case 0x%xULL: goto L_%x;' % (a, a) for a in sorted(keep)]
        fast += ['  default: if (jt - 0x%xULL < 0x%xULL) { %s(c, jt); return; } call_indirect(c, jt); return; }' % (lo, hi - lo, name), '}']
        out.extend(cp); out.extend(fast)
        i = e + 1
    return '\n'.join(out), decl


def main():
    gi, go, ef = sys.argv[1:4]
    cov = None
    if len(sys.argv) > 4:
        cov = set(int(l, 16) for l in open(sys.argv[4]) if l.strip() and not l.startswith('#'))
    f, segs = elf(ef)
    os.makedirs(go, exist_ok=True)
    stats = dict(plain=0, split=0, all=0, kept=0); decls = []
    for n in sorted(os.listdir(gi)):
        p = os.path.join(gi, n)
        if n.startswith('c_') and n.endswith('.c'):
            t = open(p, encoding='latin-1', newline='').read()
            if '\r\n' in t: t = t.replace('\r\n', '\n')
            t2, d = process(t, f, segs, cov, stats); decls += d
            old = os.path.join(go, n)
            open(old, 'w', encoding='latin-1', newline='').write(t2)
        elif os.path.isfile(p) and not n.endswith('.obj'):
            d = open(p, 'rb').read()
            open(os.path.join(go, n), 'wb').write(d)
    with open(os.path.join(go, 'funcs.h'), 'a', encoding='latin-1', newline='') as o:
        o.write('\n'.join(decls) + '\n')
    print(stats, 'functions split; labels kept %.2f%%' % (100.0 * stats['kept'] / max(1, stats['all'])))


if __name__ == '__main__':
    main()
