/* (rb-8ms 2026-11-12) Vector forms of neon.h's TBL / TBX and single-precision FADD / FSUB / FMUL / FDIV / FMLA / FMLS (vector and
 * by element), for the game DLL of the rollback build (-DR1_GAMEDLL, included at the end of neon.h). The build's LLVM pipeline
 * has no vectorizer: every NEON lane runs as scalar code (TBL a byte loop with a bound check per byte, FMLA four rt_fmaf with a
 * possible library call each: f_710389a390 = 6264 x86 instructions and 140 calls for ~165 guest instructions).
 *
 * Used ONLY by the copies tu_prepare.py makes of the functions listed in RECOMP_NF_FNS (f_<addr>ff: the same body with
 * v_tbl / v_f3v / v_f3e -> nf_v_tbl / nf_v_f3v / nf_v_f3e), entered when r1_fastneon (game_shim_r1.c, SSBU_FASTNEON) is set.
 * The first try (8ead1b4, the check in every neon.h helper) made the whole DLL ~2.5 ms slower per rollback tick and its
 * FMLA form rarely finished: a lane whose result is an exact zero (matrix compositions are full of them) sent the whole
 * operation to the scalar way. Here the other functions keep their code exactly, and a zero lane is exact (runtime.h rt_fmaf).
 *
 * Same results bit for bit (rb8ms nftest.c, 3.26 M float operations + 2 M TBL / TBX, 0 differences, for the first version):
 *  - TBL / TBX: PSHUFB per 16-byte table register; indices out of range give 0 (TBL) or keep the byte (TBX);
 *  - FADD / FSUB / FMUL / FDIV on 2 or 4 floats: the same IEEE single operations per lane, then fnan32 per lane;
 *  - FMLA / FMLS on 2 or 4 floats: rt_fmaf's method two lanes at a time, with rt_fmaf's check for each lane (and its exact
 *    zero when r1_fmazero); when any lane fails it, the whole operation goes the scalar way (v_f3v), as before. */
#ifndef RECOMP_NEON_FAST_H
#define RECOMP_NEON_FAST_H
#include <emmintrin.h>
#include <tmmintrin.h>
extern __declspec(dllexport) int r1_fastneon;

__attribute__((target("ssse3"))) static void nf_tbl(V128 *r, const V128 *tab, int len, const V128 *idx, int q, int tbx) {
    __m128i ix = _mm_loadu_si128((const __m128i *)idx->b), res = tbx ? _mm_loadu_si128((const __m128i *)r->b) : _mm_setzero_si128();
    const __m128i lowmask = _mm_set1_epi8(0x0f), fifteen = _mm_set1_epi8(15);
    for (int k = 0; k < len; k++) {
        __m128i sub = _mm_sub_epi8(ix, _mm_set1_epi8((char)(16 * k)));
        __m128i inr = _mm_cmpeq_epi8(_mm_min_epu8(sub, fifteen), sub);                  /* 0..15 (unsigned) */
        __m128i val = _mm_shuffle_epi8(_mm_loadu_si128((const __m128i *)tab[k].b), _mm_and_si128(sub, lowmask));
        res = _mm_or_si128(_mm_and_si128(inr, val), _mm_andnot_si128(inr, res));
    }
    if (!q) res = _mm_move_epi64(res);                                                  /* upper 64 bits 0 */
    _mm_storeu_si128((__m128i *)r->b, res);
}
static inline void nf_v_tbl(Ctx *c, int rd, int rn, int len, int rm, int q, int tbx) {
    V128 tab[4], idx = c->v[rm], r = c->v[rd];
    for (int i = 0; i < len; i++) tab[i] = c->v[(rn + i) & 31];
    nf_tbl(&r, tab, len, &idx, q, tbx);
    c->v[rd] = r;
}

static inline __m128 nf_fnan(__m128 v) {                                                /* fnan32 on every lane */
    __m128i b = _mm_castps_si128(v), bad = _mm_cmpeq_epi32(b, _mm_set1_epi32((int)0xffc00000u));
    return _mm_castsi128_ps(_mm_or_si128(_mm_andnot_si128(bad, b), _mm_and_si128(bad, _mm_set1_epi32(0x7fc00000))));
}
/* rt_fmaf's test on one double: 1 = (float) of it is fmaf's result (not a halfway point and a normal float, or an exact zero) */
static inline int nf_fma_ok(double r) {
    uint64_t u; memcpy(&u, &r, sizeof u);
    uint32_t e = (uint32_t)(u >> 52) & 0x7ff;
    return ((u & 0x1fffffffull) != 0x10000000ull && e >= 0x3ff - 126 && e <= 0x3ff + 127) || ((u << 1) == 0 && r1_fmazero);
}
/* FMLA (neg 0) / FMLS (neg 1) of n (2 or 4) lanes: 1 = done into *out, 0 = a lane needs the library (caller goes the scalar way) */
static inline int nf_fma(V128 *out, const V128 *acc, const V128 *a, const V128 *b, int n, int neg) {
    __m128 av = _mm_loadu_ps(a->f), bv = _mm_loadu_ps(b->f), cv = _mm_loadu_ps(acc->f);
    __m128d a0 = _mm_cvtps_pd(av), b0 = _mm_cvtps_pd(bv), c0 = _mm_cvtps_pd(cv);
    if (neg) a0 = _mm_xor_pd(a0, _mm_set1_pd(-0.0));      /* -a: the sign bit (0 - a would turn -0 into +0) */
    __m128d r0 = _mm_add_pd(_mm_mul_pd(a0, b0), c0);
    double d0 = _mm_cvtsd_f64(r0), d1 = _mm_cvtsd_f64(_mm_unpackhi_pd(r0, r0)), d2 = 0.5, d3 = 0.5;
    __m128 lo = _mm_cvtpd_ps(r0), hi = _mm_setzero_ps();
    if (n == 4) {
        __m128d a1 = _mm_cvtps_pd(_mm_movehl_ps(av, av)), b1 = _mm_cvtps_pd(_mm_movehl_ps(bv, bv)), c1 = _mm_cvtps_pd(_mm_movehl_ps(cv, cv));
        if (neg) a1 = _mm_xor_pd(a1, _mm_set1_pd(-0.0));
        __m128d r1 = _mm_add_pd(_mm_mul_pd(a1, b1), c1);
        d2 = _mm_cvtsd_f64(r1); d3 = _mm_cvtsd_f64(_mm_unpackhi_pd(r1, r1));
        hi = _mm_cvtpd_ps(r1);
    }
    if (!(nf_fma_ok(d0) & nf_fma_ok(d1) & nf_fma_ok(d2) & nf_fma_ok(d3))) return 0;      /* (0.5 for the lanes not used: passes) */
    _mm_storeu_ps(out->f, _mm_movelh_ps(lo, hi));
    return 1;
}
/* v_f3v with the vector forms for single precision, 2 or 4 lanes, ADD / SUB / MUL / DIV / MLA / MLS; anything else: v_f3v */
static inline void nf_v_f3v(V128 *d, const V128 *pn, const V128 *pm, int op, int dbl, int n) {
    if (!dbl && (n == 4 || n == 2)) {
        V128 a = *pn, b = *pm, acc = *d, r;
        __m128 x = _mm_loadu_ps(a.f), y = _mm_loadu_ps(b.f), z; int done = 1;
        switch (op) {
        case F_ADD: z = nf_fnan(_mm_add_ps(x, y)); break;
        case F_SUB: z = nf_fnan(_mm_sub_ps(x, y)); break;
        case F_MUL: z = nf_fnan(_mm_mul_ps(x, y)); break;
        case F_DIV: z = nf_fnan(_mm_div_ps(x, y)); break;
        case F_MLA: case F_MLS: done = nf_fma(&r, &acc, &a, &b, n, op == F_MLS); z = _mm_loadu_ps(r.f); break;
        default: done = 0; z = x; break;
        }
        if (done) { if (n == 2) z = _mm_movelh_ps(z, _mm_setzero_ps()); _mm_storeu_ps(r.f, z); *d = r; return; }
    }
    v_f3v(d, pn, pm, op, dbl, n);
}
static inline void nf_v_f3e(V128 *d, const V128 *pn, const V128 *pm, int idx, int op, int dbl, int n) {
    V128 b;
    RT_LANES(i, 0, 4, { if (dbl) b.df[i & 1] = pm->df[idx]; else b.f[i] = pm->f[idx]; })
    nf_v_f3v(d, pn, &b, op, dbl, n);
}
#endif
