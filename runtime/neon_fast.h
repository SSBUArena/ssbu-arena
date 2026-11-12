/* (rb-8ms 2026-11-12) Vector forms of the hottest NEON helpers of neon.h, for the game DLL (R1_MARK builds): the build's LLVM
 * pipeline has no vectorizer, so every NEON lane ran as scalar code (TBL: a byte loop with a bound check per byte; FMLA: four
 * rt_fmaf in a row). Smashville's stage animation (f_710389a390: 15 TBL, 35 FMLA, 16 FMUL per call) is ~15% of its re-run passes.
 * Same results bit for bit:
 *  - TBL / TBX: PSHUFB per 16-byte table register (SSSE3: every x86-64 CPU Windows 10/11 runs on has it; the function carries the
 *    target attribute, the rest of the DLL stays generic x86-64), indices out of range give 0 (TBL) or keep the byte (TBX);
 *  - FADD / FSUB / FMUL / FDIV on 2 or 4 floats: the same IEEE single operations (ADDPS..DIVPS = ADDSS..DIVSS per lane), then
 *    fnan32 per lane (x86's negative default NaN 0xffc00000 -> 0x7fc00000);
 *  - FMLA / FMLS on 2 or 4 floats: rt_fmaf's method two lanes at a time (the product of two floats is exact in a double, the sum
 *    rounded once, then to float), with rt_fmaf's check (halfway point / not a normal float) for each lane: when any lane fails
 *    it, the whole operation goes the scalar way (the C library's fmaf for that lane), as before.
 * r1_fastneon (game_shim_r1.c, set by the runtime from SSBU_FASTNEON): 0 = the scalar helpers as before. */
#ifndef RECOMP_NEON_FAST_H
#define RECOMP_NEON_FAST_H
#include <emmintrin.h>
#include <tmmintrin.h>

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

static inline __m128 nf_fnan(__m128 v) {                                                /* fnan32 on every lane */
    __m128i b = _mm_castps_si128(v), bad = _mm_cmpeq_epi32(b, _mm_set1_epi32((int)0xffc00000u));
    return _mm_castsi128_ps(_mm_or_si128(_mm_andnot_si128(bad, b), _mm_and_si128(bad, _mm_set1_epi32(0x7fc00000))));
}
/* rt_fmaf's test on one double: 1 = the double rounds to the correct float (not a halfway point, a normal float) */
static inline int nf_fma_ok(double r) {
    uint64_t u; memcpy(&u, &r, sizeof u);
    uint32_t e = (uint32_t)(u >> 52) & 0x7ff;
    return (u & 0x1fffffffull) != 0x10000000ull && e >= 0x3ff - 126 && e <= 0x3ff + 127;
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
#endif
