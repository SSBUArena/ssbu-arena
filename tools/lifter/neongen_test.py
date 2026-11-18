"""tools/lifter/neongen_test.py (NEON generator 2, 16/11): exactness of the forms tools/lifter/neongen.py writes.

    python neongen_test.py <clang> <runtime dir> <cases> [seed]

Writes a C test (in the current folder: neongen_t.c / neongen_t.exe), compiles it with the runtime's headers (-DRECOMP_NEON_VEC) and
runs it. Each case: a random TBL / TBX (1..4 table registers, 64 / 128-bit, index vectors made of whole aligned words, words out of
the table, and some byte patterns that the generator must leave alone) put through neongen.rewrite as the generator sees it in a
function (an index register set by MOVI right before), and the statement it writes compared with neon.h's v_tbl on random registers
(every register of the context compared, 64 random contexts per case). Also: every vector bit op (AND / EOR / BIC / ORR / ORN, 64 /
128-bit), vector MOV, MOVI and scalar write the generator rewrites, against recomp.py's original statement (all of v0..v31 compared).
The folded loads (NVK_LDK) are checked in the game by the check DLL (RECOMP_NEON_CHECK: against the memory at run time).
"""
import os
import random
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import neongen  # noqa: E402

neongen.ON_CONST = neongen.ON_BITS = True


def tbl_case(rng):
    ln, q, tbx = rng.randint(1, 4), rng.randint(0, 1), rng.randint(0, 1)
    rd, rn, rm = rng.randrange(32), rng.randrange(32), rng.randrange(32)
    idx = []
    for j in range(4):
        k = rng.randrange(10)
        if k < 7:
            w = rng.randrange(4 * ln)
            idx += [4 * w, 4 * w + 1, 4 * w + 2, 4 * w + 3]
        elif k < 9:
            idx += [rng.choice([16 * ln, 16 * ln + 3, 0x40, 0x80, 0xff]) for _ in range(4)] if k == 7 else [0xff] * 4
        else:
            idx += [rng.randrange(256) for _ in range(4)]          # a byte pattern: left as v_tbl (or a word by chance)
    lo = sum(b << (8 * i) for i, b in enumerate(idx[:8]))
    hi = sum(b << (8 * i) for i, b in enumerate(idx[8:]))
    orig = ['L_1: { c->v[%d].d[0] = 0x%xULL; c->v[%d].d[1] = 0x%xULL; }' % (rm, lo, rm, hi),
            'L_2: v_tbl(c, %d, %d, %d, %d, %d, %d);' % (rd, rn, ln, rm, q, tbx)]
    return orig


def bit_case(rng):
    d, n, m, q = rng.randrange(32), rng.randrange(32), rng.randrange(32), rng.randint(0, 1)
    op = rng.choice(['a & b', 'a ^ b', 'a & ~b', 'a | b', 'a | ~b'])
    body = ''.join('{ uint64_t a = c->v[%d].d[%d], b = c->v[%d].d[%d]; t.d[%d] = %s; } ' % (n, i, m, i, i, op) for i in range(2 if q else 1))
    return ['L_1: { V128 t; t.d[1] = 0; %s c->v[%d] = t; }' % (body, d)]


def other_case(rng):
    d, n = rng.randrange(32), rng.randrange(32)
    k = rng.randrange(6)
    if k == 0:
        return ['L_1: { V128 t = c->v[%d];  c->v[%d] = t; }' % (n, d)]
    if k == 1:
        return ['L_1: { V128 t = c->v[%d]; t.d[1] = 0; c->v[%d] = t; }' % (n, d)]
    if k == 2:
        return ['L_1: { float t = fnan32(c->v[%d].f[0] * c->v[%d].f[0]); vzero(&c->v[%d]); c->v[%d].f[0] = t; }' % (n, d, d, d)]
    if k == 3:
        return ['L_1: { double t = fnan64(c->v[%d].df[0] + c->v[%d].df[0]); vzero(&c->v[%d]); c->v[%d].df[0] = t; }' % (n, d, d, d)]
    if k == 4:
        return ['L_1: { uint32_t t = (uint32_t)c->x[%d]; vzero(&c->v[%d]); c->v[%d].w[0] = t; }' % (n, d, d)]
    return ['L_1: { c->v[%d].d[0] = 0x%xULL; c->v[%d].d[1] = %s; }' % (d, rng.getrandbits(64), d, '0x%xULL' % rng.getrandbits(64) if rng.randrange(2) else '0')]


def main():
    clang, rt, ncase = sys.argv[1], sys.argv[2], int(sys.argv[3])
    rng = random.Random(int(sys.argv[4]) if len(sys.argv) > 4 else 1)
    fns, calls, kinds = [], [], {'tbl': 0, 'tbl_left': 0, 'bits': 0, 'other': 0}
    for i in range(ncase):
        k = i % 3
        orig = tbl_case(rng) if k == 0 else bit_case(rng) if k == 1 else other_case(rng)
        new = neongen.rewrite('t', ['void n_%d(Ctx *c) {' % i] + orig + ['}'])
        if k == 0:
            kinds['tbl' if 'NVK_TBL' in new[2] else 'tbl_left'] += 1
        else:
            kinds['bits' if k == 1 else 'other'] += 1
        fns.append('\n'.join(['static void o_%d(Ctx *c) {' % i] + orig + ['}']))
        fns.append('\n'.join(['static void ' + new[0][5:]] + new[1:]))
        calls.append('    bad += t1(o_%d, n_%d, %d);' % (i, i, i))
    src = r'''#include <stdio.h>
#include <stdlib.h>
#include "runtime.h"
int r1_fmazero = 1, r1_fastneon, rt_cpu_fma = 1, rt_cpu_ssse3 = 1;
uint8_t *r1_m1, *r1_m2; uint64_t r1_lo1, r1_n1, r1_n2; int r1_mcmp;
static uint64_t rs = 0x9e3779b97f4a7c15ull;
static uint64_t rnd(void) { rs ^= rs << 13; rs ^= rs >> 7; rs ^= rs << 17; return rs; }
#undef v_tbl
#define v_tbl v_tbl_old
static void v_tbl_old(Ctx *c, int rd, int rn, int len, int rm, int q, int tbx) {      /* neon.h's v_tbl (a copy: neon.h's own is renamed) */
    uint8_t table[64];
    for (int i = 0; i < len; i++) memcpy(table + 16 * i, c->v[(rn + i) & 31].b, 16);
    V128 idx = c->v[rm], r = c->v[rd];
    int n = q ? 16 : 8;
    for (int i = 0; i < n; i++) { if (idx.b[i] < 16 * len) r.b[i] = table[idx.b[i]]; else if (!tbx) r.b[i] = 0; }
    if (!q) r.d[1] = 0;
    c->v[rd] = r;
}
''' + '\n'.join(fns) + r'''
static long t1(void (*o)(Ctx *), void (*n)(Ctx *), int id) {
    static Ctx a, b;
    long bad = 0;
    for (int k = 0; k < 64; k++) {
        for (int i = 0; i < 32; i++) { a.x[i] = rnd(); a.v[i].d[0] = rnd(); a.v[i].d[1] = rnd(); if (k & 1) for (int j = 0; j < 4; j++) if (rnd() % 3 == 0) a.v[i].w[j] = (uint32_t)(rnd() % 7 == 0 ? 0x7fc00000u | (uint32_t)rnd() : 0xffc00000u); }
        b = a;
        o(&a); n(&b);
        if (memcmp(&a, &b, sizeof a)) { if (bad++ == 0) printf("case %d differs\n", id); }
    }
    return bad;
}
int main(void) {
    long bad = 0;
''' + '\n'.join(calls) + r'''
    printf("neongen forms: %ld runs differ\n", bad);
    return bad != 0;
}
'''
    with open('neongen_t.c', 'w', newline='\n') as f:
        f.write(src)
    r = subprocess.run([clang, '-O1', '-w', '-ffp-contract=off', '-I', rt, '-DRECOMP_NEON_VEC', '-DR1_GAMEDLL', 'neongen_t.c', '-o', 'neongen_t.exe'],
                       capture_output=True, text=True)
    if r.returncode:
        print(r.stderr[-3000:])
        return 2
    print('cases: %s' % kinds)
    r = subprocess.run([os.path.abspath('neongen_t.exe')], capture_output=True, text=True)
    print(r.stdout.strip())
    return r.returncode


if __name__ == '__main__':
    sys.exit(main())
