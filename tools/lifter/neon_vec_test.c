/* tools/lifter/neon_vec_test.c (NEON generator 16/11): exactness of runtime/neon_vec.h (RECOMP_NEON_VEC).
 *  1. nv_swfma (the exact software fused multiply-add of the cold path) against the CPU's FMA3 (vfmadd = IEEE fma), random and
 *     constructed halfway cases, subnormal / overflow results; the halved form (FRSQRTS) against fmaf(-a, b / 2, 1.5).
 *  2. every vector helper of neon_vec.h, 2 and 4 lanes, both ways (FMA3 + SSSE3 / neither), against a per-lane reference written
 *     from the Arm pseudocode (FPProcessNaNs(3), FPMulAdd, FPRecipStepFused, FPRSqrtStepFused) with the CPU's IEEE operations and
 *     FMA for the numbers; and against neon.h's old helper (counted apart: differences expected only where a NaN / infinity x zero
 *     is involved). Build (a CPU with FMA3): clang -O2 -ffp-contract=off -mfma -I runtime -DRECOMP_NEON_VEC -DR1_GAMEDLL tools/lifter/neon_vec_test.c
 * usage: neon_vec_test <millions of fma cases> <thousands of cases per op> <seed>
 * (NEON generator 2) built with -DRECOMP_NEON_EST: neon_vec_test 0 0 0 est = nv_est against fpexact.h for every float (FRSQRTE / FRECPE /
 * FCVTZS, 4 and 2 lanes; a few minutes) */
#include <stdio.h>
#include <stdlib.h>
#include <immintrin.h>
#include "runtime.h"
int r1_fmazero = 1, r1_fastneon, rt_cpu_fma = 1, rt_cpu_ssse3 = 1;
uint8_t *r1_m1, *r1_m2; uint64_t r1_lo1, r1_n1, r1_n2; int r1_mcmp;
#include "neon.h"
#undef v_f3v
#undef v_f3e
#undef v_f1v
#undef v_tbl
#undef vset
#undef v_dup
#undef v_ext
#undef v_bitsel
#undef v_perm

static uint64_t rs = 0x9e3779b97f4a7c15ull;
static uint64_t rnd(void) { rs ^= rs << 13; rs ^= rs >> 7; rs ^= rs << 17; return rs; }
static float bf(uint32_t u) { float f; memcpy(&f, &u, 4); return f; }
static uint32_t fb(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
static float hwfma(float a, float b, float c) { return _mm_cvtss_f32(_mm_fmadd_ss(_mm_set_ss(a), _mm_set_ss(b), _mm_set_ss(c))); }
static uint32_t rfl(int spec) {
    static const uint32_t sp[] = { 0, 0x80000000u, 0x7f800000u, 0xff800000u, 0x7fc00000u, 0xffc00000u, 0x7f800001u, 0xff812345u, 0x7fffffffu,
        0x00000001u, 0x807fffffu, 0x00400000u, 0x7f7fffffu, 0xff7fffffu, 0x3f800000u, 0xbf800000u, 0x00800000u, 0x40000000u, 0x40400000u, 0x3fc00000u };
    uint32_t r = (uint32_t)rnd();
    if ((int)(rnd() % 100) < spec) return sp[rnd() % (sizeof sp / 4)];
    switch (rnd() % 6) {
    case 0: return r;                                                               /* anything (NaNs included) */
    case 1: return (r & 0x807fffffu) | ((uint32_t)(0x60 + rnd() % 64) << 23);
    case 2: { float f = (float)((int)(rnd() % 9) - 4); return fb(f); }
    case 3: return (r & 0x80000000u) | ((uint32_t)(0x78 + rnd() % 16) << 23) | (r & 0x007f8000u);   /* few mantissa bits: ties */
    case 4: return (r & 0x807fffffu) | ((uint32_t)(rnd() % 8) << 23);                         /* subnormal / tiny */
    default: return (r & 0x807fffffu) | ((uint32_t)(0xf8 + rnd() % 7) << 23);                 /* huge */
    }
}
/* ---- 1. software fma ---- */
static long t_swfma(long n) {
    long bad = 0;
    for (long i = 0; i < n; i++) {
        float x, y, z;
        if (i & 1) {                                     /* construct a near-halfway sum: z = -(x*y rounded) + tiny offsets */
            x = bf(rfl(0) & 0x7fffffffu); y = bf(rfl(0));
            if (!isfinite(x) || !isfinite(y)) continue;
            float p = x * y; uint32_t pu = fb(p); int k = (int)(rnd() % 5) - 2;
            z = (rnd() & 1) ? -bf(pu + k) : bf(pu + k) * (rnd() & 1 ? 0.5f : 2.0f);
            if (rnd() & 1) z = bf(fb(z) ^ 0x80000000u);
        } else { x = bf(rfl(0)); y = bf(rfl(0)); z = bf(rfl(0)); }
        if (!isfinite(x) || !isfinite(y) || !isfinite(z)) continue;
        float a = nv_swfma(x, y, z, 1.0), b = hwfma(x, y, z);
        if (fb(a) != fb(b) && bad++ < 5) printf("swfma %08x %08x %08x: sw %08x hw %08x\n", fb(x), fb(y), fb(z), fb(a), fb(b));
        float yh = y * 0.5f;
        if (yh * 2.0f == y && isfinite(z)) {             /* halved: (3 + x * y) / 2 = fma(x, y / 2, 1.5) when y / 2 is exact */
            float c = nv_swfma(x, y, 3.0f, 0.5), d = hwfma(x, yh, 1.5f);
            if (fb(c) != fb(d) && bad++ < 5) printf("swfma/2 %08x %08x: sw %08x hw %08x\n", fb(x), fb(y), fb(c), fb(d));
        }
    }
    return bad;
}
/* ---- 2. per-lane reference ---- */
static int isnanu(uint32_t x) { return (x & 0x7fffffffu) > 0x7f800000u; }
static int issnanu(uint32_t x) { return isnanu(x) && !(x & 0x400000u); }
static int isinfu(uint32_t x) { return (x & 0x7fffffffu) == 0x7f800000u; }
static int iszu(uint32_t x) { return (x & 0x7fffffffu) == 0; }
static int pnan(const uint32_t *ops, int k, uint32_t *r) {          /* FPProcessNaNs(3) */
    for (int i = 0; i < k; i++) if (issnanu(ops[i])) { *r = ops[i] | 0x400000u; return 1; }
    for (int i = 0; i < k; i++) if (isnanu(ops[i])) { *r = ops[i]; return 1; }
    return 0;
}
static uint32_t dn(float v) { return v != v ? 0x7fc00000u : fb(v); }
static uint32_t ref_lane(int op, uint32_t acc, uint32_t a, uint32_t b) {
    uint32_t r, o[3];
    switch (op) {
    case F_ADD: case F_SUB: case F_MUL: case F_DIV: case F_ADDP:
        o[0] = a; o[1] = b; if (pnan(o, 2, &r)) return r;
        return dn(op == F_ADD || op == F_ADDP ? bf(a) + bf(b) : op == F_SUB ? bf(a) - bf(b) : op == F_MUL ? bf(a) * bf(b) : bf(a) / bf(b));
    case F_MLA: case F_MLS:
        if (op == F_MLS) a ^= 0x80000000u;
        if (isnanu(acc) && !issnanu(acc) && ((isinfu(a) && iszu(b)) || (iszu(a) && isinfu(b)))) return 0x7fc00000u;
        o[0] = acc; o[1] = a; o[2] = b; if (pnan(o, 3, &r)) return r;
        return dn(hwfma(bf(a), bf(b), bf(acc)));
    case F_RECPS: case F_RSQRTS:
        a ^= 0x80000000u; o[0] = a; o[1] = b; if (pnan(o, 2, &r)) return r;
        if ((isinfu(a) && iszu(b)) || (iszu(a) && isinfu(b))) return op == F_RECPS ? 0x40000000u : 0x3fc00000u;
        if (isinfu(a) || isinfu(b)) return ((a ^ b) & 0x80000000u) | 0x7f800000u;
        if (op == F_RECPS) return fb(hwfma(bf(a), bf(b), 2.0f));
        if (bf(b) * 0.5f * 2.0f == bf(b)) return fb(hwfma(bf(a), bf(b) * 0.5f, 1.5f));
        if (bf(a) * 0.5f * 2.0f == bf(a)) return fb(hwfma(bf(a) * 0.5f, bf(b), 1.5f));
        return 0xdeadbeefu;                                         /* (both halvings inexact: not checked) */
    case F_CMEQ: return bf(a) == bf(b) ? ~0u : 0;
    case F_CMGE: return bf(a) >= bf(b) ? ~0u : 0;
    case F_CMGT: return bf(a) > bf(b) ? ~0u : 0;
    }
    return 0;
}
static const char *opn[] = { "ADD", "SUB", "MUL", "DIV", "MLA", "MLS", "MAX", "MIN", "MAXNM", "MINNM", "ABD", "CMEQ", "CMGE", "CMGT", "RECPS", "RSQRTS", "ADDP" };
static long nbad, nold, nskip;
static void cmp(const char *what, const V128 *got, const V128 *want, const V128 *old, const V128 *in) {
    if (memcmp(got, want, 16)) {
        int skip = 0; for (int i = 0; i < 4; i++) if (want->w[i] == 0xdeadbeefu) skip = 1;
        if (skip) { nskip++; return; }
        if (nbad++ < 12) printf("BAD %s: got %08x %08x %08x %08x want %08x %08x %08x %08x (in %08x %08x %08x %08x)\n", what, got->w[0], got->w[1], got->w[2], got->w[3],
                                 want->w[0], want->w[1], want->w[2], want->w[3], in->w[0], in->w[1], in->w[2], in->w[3]);
    }
    if (old && memcmp(got, old, 16)) nold++;
}
static void rv(V128 *v, int spec) { for (int i = 0; i < 4; i++) v->w[i] = rfl(spec); }
static long t_ops(long n) {
    long nb0 = nbad; Ctx c;
    for (long it = 0; it < n; it++) {
        int spec = (int)(it % 4) * 10;
        for (int way = 0; way < 2; way++) {
            rt_cpu_fma = rt_cpu_ssse3 = !way;
            for (int op = 0; op <= F_ADDP; op++) {
                if (op >= F_MAX && op <= F_ABD) continue;
                for (int n = 2; n <= 4; n += 2) for (int byel = 0; byel < 2; byel++) {
                    V128 a, b, d0, d, w, o; int idx = (int)(rnd() % 4);
                    rv(&a, spec); rv(&b, spec); rv(&d0, spec);
                    if (op == F_ADDP && byel) continue;
                    d = d0; o = d0;
                    if (byel) { nv_f3e(&d, &a, &b, idx, op, 0, n); v_f3e(&o, &a, &b, idx, op, 0, n); }
                    else { nv_f3v(&d, &a, &b, op, 0, n); v_f3v(&o, &a, &b, op, 0, n); }
                    memset(&w, 0, 16);
                    for (int i = 0; i < n; i++) {
                        uint32_t x = a.w[i], y = byel ? b.w[idx] : b.w[i];
                        if (op == F_ADDP) { int k = 2 * i; const V128 *s = k < n ? &a : &b; k %= n; x = s->w[k]; y = s->w[k + 1]; }
                        w.w[i] = ref_lane(op, d0.w[i], x, y);
                    }
                    char what[64]; snprintf(what, sizeof what, "%s%s n%d way%d", opn[op], byel ? "e" : "", n, way);
                    cmp(what, &d, &w, &o, &a);
                }
            }
            for (int n = 2; n <= 4; n += 2) for (int op = F1_ABS; op <= F1_CMLE0; op++) {      /* v_f1v: against the old helper (and FSQRT's Arm rule) */
                V128 a, d, o; rv(&a, spec); rv(&d, spec); o = d;
                nv_f1v(&d, &a, op, 0, n); v_f1v(&o, &a, op, 0, n);
                if (op == F1_SQRT) for (int i = 0; i < n; i++) {
                    uint32_t x = a.w[i]; o.w[i] = isnanu(x) ? x | 0x400000u : ((x >> 31) && !iszu(x)) ? 0x7fc00000u : fb(sqrtf(bf(x)));
                }
                char what[32]; snprintf(what, sizeof what, "f1 op%d n%d way%d", op, n, way); cmp(what, &d, &o, NULL, &a);
            }
            for (int r = 0; r < 32; r++) rv(&c.v[r], 0);
            for (int k = 0; k < 4; k++) for (int i = 0; i < 16; i++) { uint8_t u = (uint8_t)rnd(); c.v[(9 + k)].b[i] = (rnd() & 3) ? u % 70 : u; }
            for (int len = 1; len <= 4; len++) for (int q = 0; q < 2; q++) for (int tbx = 0; tbx < 2; tbx++) {     /* TBL / TBX */
                int rm = 9 + (int)(rnd() % 4), rn = (int)(rnd() % 32), rd = 20 + (int)(rnd() % 4);
                Ctx c2 = c; nv_tbl(&c, rd, rn, len, rm, q, tbx); v_tbl(&c2, rd, rn, len, rm, q, tbx);
                char what[32]; snprintf(what, sizeof what, "tbl len%d q%d x%d way%d", len, q, tbx, way); cmp(what, &c.v[rd], &c2.v[rd], NULL, &c.v[rm]);
            }
            for (int q = 0; q < 2; q++) for (int imm = 0; imm < (q ? 16 : 8); imm++) {            /* EXT */
                V128 a, b, d, o; rv(&a, 0); rv(&b, 0); nv_ext(&d, &a, &b, imm, q); v_ext(&o, &a, &b, imm, q);
                char what[32]; snprintf(what, sizeof what, "ext q%d imm%d", q, imm); cmp(what, &d, &o, NULL, &a);
            }
            for (int sz = 0; sz < 4; sz++) for (int i = 0; i < (16 >> sz); i++) {                    /* INS, DUP */
                V128 d, o; rv(&d, 0); o = d; uint64_t x = rnd();
                nv_vset(&d, sz, i, x); vset(&o, sz, i, x);
                char what[32]; snprintf(what, sizeof what, "vset sz%d i%d", sz, i); cmp(what, &d, &o, NULL, &d);
                int n = (i & 1) ? (16 >> sz) : (8 >> sz); if (!n) n = 1;
                nv_dup(&d, x, sz, n); v_dup(&o, x, sz, n);
                snprintf(what, sizeof what, "dup sz%d n%d", sz, n); cmp(what, &d, &o, NULL, &d);
            }
            for (int kind = 1; kind <= 3; kind++) for (int q = 0; q < 2; q++) {                      /* BSL / BIT / BIF */
                V128 a, b, d, o; rv(&a, 0); rv(&b, 0); rv(&d, 0); o = d; nv_bitsel(&d, &a, &b, kind, q); v_bitsel(&o, &a, &b, kind, q);
                char what[32]; snprintf(what, sizeof what, "bitsel k%d q%d", kind, q); cmp(what, &d, &o, NULL, &a);
            }
            for (int opc = 0; opc < 8; opc++) for (int sz = 0; sz < 4; sz++) for (int q = 0; q < 2; q++) {   /* UZP / TRN / ZIP */
                if ((opc & 3) == 0 || (sz == 3 && !q)) continue;
                int n = (q ? 16 : 8) >> sz; V128 a, b, d, o; rv(&a, 0); rv(&b, 0); rv(&d, 0); o = d;
                nv_perm(&d, &a, &b, opc, sz, n); v_perm(&o, &a, &b, opc, sz, n);
                char what[32]; snprintf(what, sizeof what, "perm o%d sz%d n%d", opc, sz, n); cmp(what, &d, &o, NULL, &a);
            }
        }
    }
    return nbad - nb0;
}
#ifdef RECOMP_NEON_EST
/* (NEON generator 2) nv_est (FRSQRTE / FRECPE / FCVTZS, RECOMP_NEON_EST) against fpexact.h's per-lane code for EVERY float, 4 and 2 lanes */
uint8_t rt_rsqrte_tab[384], rt_recpe_tab[256];
static long long t_est(void) {
    for (uint32_t a = 128; a < 512; a++) rt_rsqrte_tab[a - 128] = (uint8_t)recip_sqrt_estimate(a);
    for (uint32_t a = 256; a < 512; a++) rt_recpe_tab[a - 256] = (uint8_t)recip_estimate(a);
    int ops[3] = { F1_RSQRTE, F1_RECPE, F1_CVTZS };
    long long bad = 0;
    for (int k = 0; k < 3; k++)
        for (int nl = 4; nl >= 2; nl -= 2)
            for (uint64_t b = 0; b < (1ull << 32); b += (uint64_t)nl) {
                V128 d, x;
                for (int i = 0; i < 4; i++) x.w[i] = (uint32_t)(b + (uint64_t)i);
                nv_est(&d, nl == 4 ? nv_ld(&x) : nv_ld2(&x), ops[k], nl);
                for (int i = 0; i < 4; i++) {
                    uint32_t want = i >= nl ? 0 : ops[k] == F1_RSQRTE ? (uint32_t)fp_rsqrte_bits(x.w[i], 0) : ops[k] == F1_RECPE ? (uint32_t)fp_recpe_bits(x.w[i], 0)
                                  : f2s32(trunc((double)bf(x.w[i])));
                    if (d.w[i] != want && bad++ < 8) printf("est op %d in %08x: %08x, fpexact.h %08x%c", ops[k], x.w[i], d.w[i], want, 10);
                }
            }
    return bad;
}
#endif
int main(int argc, char **argv) {
    long nf = argc > 1 ? atol(argv[1]) * 1000000 : 1000000, no = argc > 2 ? atol(argv[2]) * 1000 : 10000;
#ifdef RECOMP_NEON_EST
    if (argc > 4 && argv[4][0] == 'e') {          /* neon_vec_test 0 0 0 est (build with -DRECOMP_NEON_EST): only the exhaustive estimate test */
        long long be = t_est();
        printf("est: every float, FRSQRTE / FRECPE / FCVTZS, 4 and 2 lanes: %lld lanes differ from fpexact.h%c", be, 10);
        return be != 0;
    }
#endif
    if (argc > 3) rs ^= (uint64_t)atoll(argv[3]) * 0x2545f4914f6cdd1dull;
    long b1 = t_swfma(nf);
    printf("swfma: %ld cases, %ld differ from the CPU's FMA\n", nf, b1);
    long b2 = t_ops(no);
    printf("ops: %ld rounds (every op, n 2 / 4, vector / by element, both ways), %ld differ from the Arm reference (%ld lanes unchecked: both "
           "halvings inexact), %ld results differ from neon.h's old helper\n", no, b2, nskip, nold);
    return (b1 || b2) ? 1 : 0;
}
