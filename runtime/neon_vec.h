/* (NEON generator, 2026-11-16) RECOMP_NEON_VEC (build_opt.py, default off: -DRECOMP_NEON_VEC in the game's units): the hot NEON helpers of
 * neon.h as whole-register vector code, included at the end of neon.h; without the define neon.h is exactly as before.
 *
 * Why (ROLLBACK_STATO 2.12): the lifted f_710389a390 (164 Arm instructions) ran ~2280 cycles where SSE / FMA vector code
 * runs ~225. The build's LLVM pipeline has no vectorizer: every lane of a NEON op was scalar code (FMLA four rt_fmaf, each a double
 * computation, a check and a library call on the cold side), TBL a byte loop with a bound check per byte and 8 / 16 byte stores, INS a
 * 4-byte store into the register; and a later 16-byte read of a register written by narrower stores cannot be forwarded (in LLVM or
 * in the CPU: a store-forwarding stall). The first vector try (8ead1b4, rejected) mixed vector loads with the scalar lane stores of
 * every other helper, behind a branch in every helper.
 *
 * Rules here:
 *  - a register is read lane by lane (four 32-bit loads: LLVM forwards each from whatever stored it, and a vector built from the
 *    lanes of one vector is that vector) and written with ONE 16-byte store (a later lane or vector read is forwarded from it);
 *  - single-precision FADD / FSUB / FMUL / FDIV / FMLA / FMLS / FRECPS / FRSQRTS / FCMxx / FADDP (vector and by element), TBL / TBX,
 *    EXT, DUP, INS (vset), BSL / BIT / BIF, UZP / TRN / ZIP (32 / 64-bit), FABS / FNEG / FCMxx #0 / SCVTF / FSQRT are whole-vector ops;
 *    everything else (double precision, FMAX / FMIN, estimates, conversions to integer, the integer ops) is neon.h's code as before;
 *  - results are Arm's (FPCR = 0: no flush to zero, round to nearest, DN = 0) bit for bit, NaNs included: a lane that is a NaN, or
 *    that needs the slow way, sends the whole operation to a cold per-lane function that follows the Arm pseudocode (FPProcessNaNs /
 *    FPMulAdd / FPRecipStepFused / FPRSqrtStepFused) with an exact software fused multiply-add (no C library);
 *  - fused multiply-adds: with FMA3 (rt_cpu_fma, set by game_bind from CPUID + XGETBV) one vfmadd231ps (IEEE fma = Arm FPMulAdd for
 *    every non-NaN input); without it two lanes at a time in double (x * y is exact in a double; the sum rounded once; (float) of it is
 *    the fused result unless it falls on a float halfway point or is not a normal float: rt_fmaf's test, vectorised) and the cold
 *    function for the rest. TBL with PSHUFB when rt_cpu_ssse3, neon.h's byte loop otherwise. The CPU-specific instructions are inline
 *    assembly, so the DLL stays generic x86-64 and both ways are in it; the two ways give the same bits (tools/lifter/neon_vec_test.c).
 * Differences from neon.h's code (which they fix): FRECPS / FRSQRTS of infinity x zero give 2.0 / 1.5 (Arm) instead of a NaN; FADDP's
 * generated NaN is Arm's default NaN (neon.h: x86's 0xffc00000); NaN operands follow Arm's order (SNaN first, then the first operand),
 * not the C library's / the compiler's operand order; a propagated NaN 0xffc00000 stays 0xffc00000 (fnan32 turned it into 0x7fc00000);
 * the slow fused cases never go to the C library (whose zero signs differed between CPUs with and without FMA3). */
#ifndef RECOMP_NEON_VEC_H
#define RECOMP_NEON_VEC_H
#include <emmintrin.h>

typedef float nv_f4 __attribute__((vector_size(16)));
typedef uint32_t nv_u4 __attribute__((vector_size(16)));
typedef int32_t nv_i4 __attribute__((vector_size(16)));
typedef uint64_t nv_u2 __attribute__((vector_size(16)));
typedef double nv_d2 __attribute__((vector_size(16)));
typedef uint8_t nv_b16 __attribute__((vector_size(16)));

extern __declspec(dllexport) int rt_cpu_fma, rt_cpu_ssse3;       /* (game_shim.c game_bind) 1 = the CPU (and the OS) has FMA3 / SSSE3 */

static inline nv_u4 nv_ld(const V128 *p) { nv_u4 r = { p->w[0], p->w[1], p->w[2], p->w[3] }; return r; }
static inline nv_u4 nv_ld2(const V128 *p) { nv_u4 r = { p->w[0], p->w[1], 0, 0 }; return r; }       /* 64-bit form: upper lanes 0 */
static inline void nv_st(V128 *p, nv_u4 v) { memcpy(p, &v, 16); }
static inline nv_f4 nv_f(nv_u4 v) { return (nv_f4)v; }
static inline nv_u4 nv_u(nv_f4 v) { return (nv_u4)v; }
static inline int nv_nanmask(nv_f4 r) { return _mm_movemask_ps((__m128)(r != r)); }

/* FMA3 / SSSE3 instructions without target attributes (the lifted code is built for generic x86-64; these run only when the
 * flag says the CPU has them). acc + a * b, one rounding. */
static inline nv_f4 nv_vfma(nv_f4 acc, nv_f4 a, nv_f4 b) { __asm__("vfmadd231ps %2, %1, %0" : "+x"(acc) : "x"(a), "x"(b)); return acc; }
static inline __m128i nv_pshufb(__m128i t, __m128i i) { __asm__("pshufb %1, %0" : "+x"(t) : "x"(i)); return t; }

/* ---- the Arm rules, one lane (cold) ---- */
static inline int nv_isnan(uint32_t x) { return (x & 0x7fffffffu) > 0x7f800000u; }
static inline int nv_issnan(uint32_t x) { return nv_isnan(x) && !(x & 0x00400000u); }
static inline int nv_isinf(uint32_t x) { return (x & 0x7fffffffu) == 0x7f800000u; }
static inline int nv_iszero(uint32_t x) { return (x & 0x7fffffffu) == 0; }
static inline float nv_bf(uint32_t u) { float f; memcpy(&f, &u, 4); return f; }
static inline uint32_t nv_fb(float f) { uint32_t u; memcpy(&u, &f, 4); return u; }
static inline uint64_t nv_db(double d) { uint64_t u; memcpy(&u, &d, 8); return u; }
#define NV_DNAN 0x7fc00000u
/* FPProcessNaNs / FPProcessNaNs3: the first signalling NaN (quietened), else the first quiet NaN, in operand order */
static inline int nv_pn2(uint32_t a, uint32_t b, uint32_t *r) {
    if (nv_issnan(a)) { *r = a | 0x00400000u; return 1; }
    if (nv_issnan(b)) { *r = b | 0x00400000u; return 1; }
    if (nv_isnan(a)) { *r = a; return 1; }
    if (nv_isnan(b)) { *r = b; return 1; }
    return 0;
}
static inline int nv_pn3(uint32_t a, uint32_t b, uint32_t c, uint32_t *r) {
    if (nv_issnan(a)) { *r = a | 0x00400000u; return 1; }
    if (nv_issnan(b)) { *r = b | 0x00400000u; return 1; }
    if (nv_issnan(c)) { *r = c | 0x00400000u; return 1; }
    if (nv_isnan(a)) { *r = a; return 1; }
    if (nv_isnan(b)) { *r = b; return 1; }
    if (nv_isnan(c)) { *r = c; return 1; }
    return 0;
}
/* Exact (z + x * y) * scale rounded once to float, for finite x, y, z and scale 1 or 0.5, without FMA hardware or the C library:
 * p = x * y is exact in a double; r = p + z rounded to double, err = (p + z) - r exactly (TwoSum: no overflow, |p| <= 2^256, and
 * every value is a multiple of 2^-298, far above the double's subnormals); halving both is exact. (float)r is the correctly rounded
 * result unless r is exactly halfway between two floats and err != 0 (rounding is monotonic and every halfway point, subnormal
 * ones and the one above FLT_MAX included, is a double): then the true value lies on err's side of r. */
static float nv_swfma(float x, float y, float z, double scale) {
    double p = (double)x * (double)y, zd = z, r = p + zd, bb = r - p, err = (p - (r - bb)) + (zd - bb);
    r *= scale; err *= scale;
    float f = (float)r;
    if (err == 0) return f;
    uint32_t fu = nv_fb(f);
    if ((fu & 0x7fffffffu) == 0x7f800000u) {                    /* rounded to infinity: only r == FLT_MAX + half ulp can be wrong */
        if (fabs(r) == 0x1.ffffffp127 && (err < 0) != (r < 0)) return nv_bf((fu & 0x80000000u) | 0x7f7fffffu);
        return f;
    }
    double fd = f;
    if (r == fd) return f;
    int up = r > fd;                                            /* the neighbour of f on r's side */
    uint32_t nu = (fu & 0x7fffffffu) == 0 ? (up ? 1u : 0x80000001u) : ((fu >> 31) == (uint32_t)!up ? fu + 1 : fu - 1);
    double nd = nv_bf(nu);
    if (r != (fd + nd) * 0.5) return f;                          /* not a halfway point: (float)r is right */
    return (err > 0) == up ? nv_bf(nu) : f;
}
/* FPMulAdd(acc, a, b) (Arm, FPCR = 0); for FMLS the caller passes a with its sign flipped (FPNeg, NaNs too) */
static __attribute__((noinline)) uint32_t nv_arm_fma(uint32_t acc, uint32_t a, uint32_t b) {
    uint32_t r;
    if (nv_isnan(acc) && !nv_issnan(acc) && ((nv_isinf(a) && nv_iszero(b)) || (nv_iszero(a) && nv_isinf(b)))) return NV_DNAN;
    if (nv_pn3(acc, a, b, &r)) return r;
    if (nv_isinf(acc) || nv_isinf(a) || nv_isinf(b)) {           /* exact in double: inf x 0 and inf - inf give a NaN */
        double v = (double)nv_bf(a) * (double)nv_bf(b) + (double)nv_bf(acc);
        return v != v ? NV_DNAN : nv_fb((float)v);
    }
    return nv_fb(nv_swfma(nv_bf(a), nv_bf(b), nv_bf(acc), 1.0));
}
/* FPRecipStepFused (rs 0: 2 + a * b) / FPRSqrtStepFused (rs 1: (3 + a * b) / 2); a is the first operand after the instruction's
 * FPNeg (the caller flips its sign, NaNs too, as for FMLS) */
static __attribute__((noinline)) uint32_t nv_arm_step(uint32_t a, uint32_t b, int rs) {
    uint32_t r;
    if (nv_pn2(a, b, &r)) return r;
    if ((nv_isinf(a) && nv_iszero(b)) || (nv_iszero(a) && nv_isinf(b))) return rs ? 0x3fc00000u : 0x40000000u;
    if (nv_isinf(a) || nv_isinf(b)) return ((a ^ b) & 0x80000000u) | 0x7f800000u;
    return nv_fb(nv_swfma(nv_bf(a), nv_bf(b), rs ? 3.0f : 2.0f, rs ? 0.5 : 1.0));
}
/* FPAdd / FPSub / FPMul / FPDiv: the NaN operands by Arm's order; a NaN the operation generates is the default NaN */
static __attribute__((noinline)) uint32_t nv_arm_2(int op, uint32_t a, uint32_t b) {
    uint32_t r;
    if (nv_pn2(a, b, &r)) return r;
    float x = nv_bf(a), y = nv_bf(b), v = op == F_ADD || op == F_ADDP ? x + y : op == F_SUB ? x - y : op == F_MUL ? x * y : x / y;
    return v != v ? NV_DNAN : nv_fb(v);
}
/* the lanes of `bad` (a bit per lane) by the rules above, the others kept from the vector result r (upper lanes of a 64-bit form: 0) */
static __attribute__((noinline, cold)) void nv_cold_2(V128 *d, nv_u4 a, nv_u4 b, int op, int n, nv_u4 r, int bad) {
#ifdef NV_COLD_HOOK
    NV_COLD_HOOK(10 + op, bad, a, a, b, r);
#endif
    for (int i = 0; i < 4; i++) r[i] = i >= n ? 0 : (bad >> i) & 1 ? nv_arm_2(op, a[i], b[i]) : r[i];
    nv_st(d, r);
}
/* kind 0: acc + a * b (a already negated for FMLS); 1 FRECPS; 2 FRSQRTS */
static __attribute__((noinline, cold)) void nv_cold_fma(V128 *d, nv_u4 acc, nv_u4 a, nv_u4 b, int kind, int n, nv_u4 r, int bad) {
#ifdef NV_COLD_HOOK
    NV_COLD_HOOK(kind, bad, acc, a, b, r);      /* (test builds only) */
#endif
    for (int i = 0; i < 4; i++) r[i] = i >= n ? 0 : !((bad >> i) & 1) ? r[i] : kind ? nv_arm_step(a[i], b[i], kind == 2) : nv_arm_fma(acc[i], a[i], b[i]);
    nv_st(d, r);
}

/* rt_fmaf's test on four doubles (two vectors), refined: bit i set = lane i may not be exactly its (float): a float halfway point that
 * the double sum reached by rounding (enl / enh: the sum's error, TwoSum, is not 0; an exact sum on a halfway point is rounded right
 * by (float), ties to even), or not a normal float (subnormal, infinite, NaN); an exact zero passes (exact: runtime.h rt_fmaf) */
static inline int nv_dbad(nv_d2 lo, nv_d2 hi, nv_d2 elo, nv_d2 ehi) {
    nv_i4 L = (nv_i4)__builtin_shufflevector((nv_i4)lo, (nv_i4)hi, 0, 2, 4, 6), H = (nv_i4)__builtin_shufflevector((nv_i4)lo, (nv_i4)hi, 1, 3, 5, 7);
    nv_i4 E = (nv_i4)__builtin_shufflevector((nv_i4)(elo != 0), (nv_i4)(ehi != 0), 0, 2, 4, 6);
    nv_i4 mid = ((L & 0x1fffffff) == 0x10000000) & E;
    nv_i4 e = (H >> 20) & 0x7ff;
    nv_i4 inr = (e > 0x380) & (e < 0x47f);
    nv_i4 zero = ((H & 0x7fffffff) | L) == 0;
    return _mm_movemask_ps((__m128)((mid | ~inr) & ~zero));
}
/* acc + a * b two lanes at a time in double (scale 0.5: halved, exact) into *out; returns the lanes that need the cold way */
static inline int nv_dfma(nv_f4 *out, nv_f4 acc, nv_f4 a, nv_f4 b, double scale, int n) {
    nv_d2 pl = __builtin_convertvector(__builtin_shufflevector(a, a, 0, 1), nv_d2) * __builtin_convertvector(__builtin_shufflevector(b, b, 0, 1), nv_d2);
    nv_d2 ph = __builtin_convertvector(__builtin_shufflevector(a, a, 2, 3), nv_d2) * __builtin_convertvector(__builtin_shufflevector(b, b, 2, 3), nv_d2);
    nv_d2 cl = __builtin_convertvector(__builtin_shufflevector(acc, acc, 0, 1), nv_d2), ch = __builtin_convertvector(__builtin_shufflevector(acc, acc, 2, 3), nv_d2);
    nv_d2 lo = pl + cl, hi = ph + ch, bl = lo - pl, bh = hi - ph;
    nv_d2 elo = (pl - (lo - bl)) + (cl - bl), ehi = (ph - (hi - bh)) + (ch - bh);          /* TwoSum: the sums' rounding errors */
    if (scale != 1.0) { lo *= scale; hi *= scale; }
    typedef float nv_f2 __attribute__((vector_size(8)));
    nv_f2 l = __builtin_convertvector(lo, nv_f2), h = __builtin_convertvector(hi, nv_f2);
    *out = __builtin_shufflevector(l, h, 0, 1, 2, 3);
    return nv_dbad(lo, hi, elo, ehi);
}

static inline nv_u4 nv_lomask(int n) { nv_u4 m = { ~0u, ~0u, n == 4 ? ~0u : 0, n == 4 ? ~0u : 0 }; return m; }

/* The single-precision part of v_f3v: n = 2 or 4 lanes */
static inline void nv_f3s(V128 *d, nv_u4 A, nv_u4 B, nv_u4 C, int op, int n, int fma) {
    nv_f4 a = nv_f(A), b = nv_f(B), r;
    switch (op) {
    case F_ADD: case F_SUB: case F_MUL: case F_DIV:
        r = op == F_ADD ? a + b : op == F_SUB ? a - b : op == F_MUL ? a * b : a / b;
        { int bad = nv_nanmask(r) & ((1 << n) - 1); if (__builtin_expect(bad, 0)) { nv_cold_2(d, A, B, op, n, nv_u(r), bad); return; } }
        nv_st(d, nv_u(r) & nv_lomask(n));
        return;
    case F_ADDP: {
        nv_u4 x = n == 4 ? __builtin_shufflevector(A, B, 0, 2, 4, 6) : __builtin_shufflevector(A, B, 0, 4, 2, 2);
        nv_u4 y = n == 4 ? __builtin_shufflevector(A, B, 1, 3, 5, 7) : __builtin_shufflevector(A, B, 1, 5, 3, 3);
        r = nv_f(x) + nv_f(y);
        { int bad = nv_nanmask(r) & ((1 << n) - 1); if (__builtin_expect(bad, 0)) { nv_cold_2(d, x, y, F_ADD, n, nv_u(r), bad); return; } }
        nv_st(d, nv_u(r) & nv_lomask(n));
        return;
    }
    case F_CMEQ: nv_st(d, (nv_u4)(a == b) & nv_lomask(n)); return;
    case F_CMGE: nv_st(d, (nv_u4)(a >= b) & nv_lomask(n)); return;
    case F_CMGT: nv_st(d, (nv_u4)(a > b) & nv_lomask(n)); return;
    case F_MLA: case F_MLS: case F_RECPS: case F_RSQRTS: {
        int kind = op == F_RECPS ? 1 : op == F_RSQRTS ? 2 : 0;
        nv_u4 An = op == F_MLA ? A : A ^ 0x80000000u;              /* FMLS, FRECPS, FRSQRTS: FPNeg of the first operand */
        nv_f4 acc = kind == 0 ? nv_f(C) : kind == 1 ? (nv_f4){ 2, 2, 2, 2 } : (nv_f4){ 3, 3, 3, 3 };
        int bad;
        if (__builtin_expect(fma, 1)) {
            r = nv_vfma(acc, nv_f(An), b);
            nv_u4 m = (nv_u4)(r != r);
            if (kind == 2) {                                        /* halved: exact unless the sum is infinite or below 2^-125 */
                nv_u4 ab = nv_u(r) & 0x7fffffffu;
                m |= (nv_u4)((ab >= 0x7f800000u) | ((ab != 0) & (ab < 0x01000000u)));
                r *= (nv_f4){ 0.5f, 0.5f, 0.5f, 0.5f };
            }
            bad = _mm_movemask_ps((__m128)m);
        } else bad = nv_dfma(&r, acc, nv_f(An), b, kind == 2 ? 0.5 : 1.0, n);
        bad &= (1 << n) - 1;
        if (__builtin_expect(bad, 0)) { nv_cold_fma(d, C, An, B, kind, n, nv_u(r), bad); return; }
        nv_st(d, nv_u(r) & nv_lomask(n));
        return;
    }
    }
}
static inline int nv_f3ok(int op) {
    return op == F_ADD || op == F_SUB || op == F_MUL || op == F_DIV || op == F_MLA || op == F_MLS || op == F_RECPS || op == F_RSQRTS ||
           op == F_CMEQ || op == F_CMGE || op == F_CMGT || op == F_ADDP;
}
static inline void nv_f3vw(V128 *d, const V128 *pn, const V128 *pm, int op, int dbl, int n, int fma) {
    if (dbl || !(n == 2 || n == 4) || !nv_f3ok(op)) { v_f3v(d, pn, pm, op, dbl, n); return; }
    nv_u4 A = n == 4 ? nv_ld(pn) : nv_ld2(pn), B = n == 4 ? nv_ld(pm) : nv_ld2(pm), C = op == F_MLA || op == F_MLS ? (n == 4 ? nv_ld(d) : nv_ld2(d)) : (nv_u4){ 0, 0, 0, 0 };
    nv_f3s(d, A, B, C, op, n, fma);
}
static inline void nv_f3ew(V128 *d, const V128 *pn, const V128 *pm, int idx, int op, int dbl, int n, int fma) {
    if (dbl || !(n == 2 || n == 4) || !nv_f3ok(op)) { v_f3e(d, pn, pm, idx, op, dbl, n); return; }
    uint32_t e = pm->w[idx];
    nv_u4 A = n == 4 ? nv_ld(pn) : nv_ld2(pn), B = { e, e, n == 4 ? e : 0, n == 4 ? e : 0 };
    nv_u4 C = op == F_MLA || op == F_MLS ? (n == 4 ? nv_ld(d) : nv_ld2(d)) : (nv_u4){ 0, 0, 0, 0 };
    nv_f3s(d, A, B, C, op, n, fma);
}
static inline void nv_f3v(V128 *d, const V128 *pn, const V128 *pm, int op, int dbl, int n) { nv_f3vw(d, pn, pm, op, dbl, n, rt_cpu_fma); }
static inline void nv_f3e(V128 *d, const V128 *pn, const V128 *pm, int idx, int op, int dbl, int n) { nv_f3ew(d, pn, pm, idx, op, dbl, n, rt_cpu_fma); }

/* FABS / FNEG (sign bit only, NaNs too: as Arm), FCMxx #0, SCVTF (cvtdq2ps: round to nearest, as Arm), FSQRT; the rest: neon.h */
static __attribute__((noinline, cold)) void nv_cold_sqrt(V128 *d, nv_u4 a, int n) {
    nv_u4 r = { 0, 0, 0, 0 };
    for (int i = 0; i < n; i++) {
        uint32_t x = a[i];
        if (nv_isnan(x)) r[i] = nv_issnan(x) ? x | 0x00400000u : x;
        else if ((x >> 31) && !nv_iszero(x)) r[i] = NV_DNAN;
        else r[i] = nv_fb(sqrtf(nv_bf(x)));
    }
    nv_st(d, r);
}
#ifdef RECOMP_NEON_EST
/* (NEON generator 2, 16/11; build_opt.py RECOMP_NEON_EST) FRSQRTE / FRECPE / FCVTZS of single-precision lanes as vector code: neon.h's way is
 * per lane (recip_sqrt_estimate: a square root and correction loops for every lane). Normal positive inputs (FRSQRTE), normal inputs
 * with an exponent up to 252 (FRECPE: the result stays normal) and |x| < 2^31 (FCVTZS: cvttps2dq is exact there): the exponent and
 * the top fraction bits as in fp_rsqrte_bits / fp_recpe_bits, the 8-bit estimate from rt_rsqrte_tab / rt_recpe_tab (game_shim.c fills
 * them from fpexact.h's own functions); any other lane (zero, subnormal, negative, infinite, NaN, out of range) sends the operation to
 * fpexact.h's per-lane code. Same bits as neon.h (tools/lifter/neon_vec_test.c: every float). */
extern __declspec(dllexport) uint8_t rt_rsqrte_tab[384], rt_recpe_tab[256];
static __attribute__((noinline, cold)) void nv_cold_est(V128 *d, nv_u4 a, int op, int n) {
    nv_u4 r = { 0, 0, 0, 0 };
    for (int i = 0; i < n; i++)
        r[i] = op == F1_RSQRTE ? (uint32_t)fp_rsqrte_bits(a[i], 0) : op == F1_RECPE ? (uint32_t)fp_recpe_bits(a[i], 0) : f2s32(trunc((double)nv_bf(a[i])));
    nv_st(d, r);
}
static inline void nv_est(V128 *d, nv_u4 A, int op, int n) {
    nv_u4 e = (A >> 23) & 0xffu, f = A & 0x7fffffu, r;
    nv_i4 ok;
    if (op == F1_CVTZS) {
        ok = (nv_i4)(e < 158u);
        r = (nv_u4)_mm_cvttps_epi32((__m128)A);
    } else {
        nv_u4 ix;
        if (op == F1_RSQRTE) {
            ok = (nv_i4)((A >> 31) == 0) & (nv_i4)(e != 0) & (nv_i4)(e != 0xffu);
            nv_u4 odd = (nv_u4)((e & 1) != 0);
            ix = (~odd & (128 + (f >> 15))) | (odd & (f >> 16));
        } else {
            ok = (nv_i4)(e != 0) & (nv_i4)(e <= 252u);
            ix = f >> 15;
        }
        ix &= (nv_u4)ok & 0x1ffu;                                     /* (a lane that goes the cold way reads entry 0) */
        const uint8_t *tab = op == F1_RSQRTE ? rt_rsqrte_tab : rt_recpe_tab;
        nv_u4 est = { tab[ix[0]], tab[ix[1]], tab[ix[2]], tab[ix[3]] };
        r = op == F1_RSQRTE ? (((380 - e) >> 1) << 23) | (est << 15) : (A & 0x80000000u) | ((253 - e) << 23) | (est << 15);
    }
    int bad = ~_mm_movemask_ps((__m128)ok) & ((1 << n) - 1);
    if (__builtin_expect(bad, 0)) { nv_cold_est(d, A, op, n); return; }
    nv_st(d, r & nv_lomask(n));
}
#define NV_EST_OP(op) ((op) == F1_RSQRTE || (op) == F1_RECPE || (op) == F1_CVTZS)
#else
#define NV_EST_OP(op) 0
#endif
static inline void nv_f1v(V128 *d, const V128 *pn, int op, int dbl, int n) {
#ifdef RECOMP_NEON_EST
    if (!dbl && (n == 2 || n == 4) && NV_EST_OP(op)) { nv_est(d, n == 4 ? nv_ld(pn) : nv_ld2(pn), op, n); return; }
#endif
    if (dbl || !(n == 2 || n == 4) || !(op == F1_ABS || op == F1_NEG || op == F1_SQRT || op == F1_SCVTF || (op >= F1_CMEQ0 && op <= F1_CMLE0))) {
        v_f1v(d, pn, op, dbl, n); return;
    }
    nv_u4 A = n == 4 ? nv_ld(pn) : nv_ld2(pn), r;
    nv_f4 a = nv_f(A), z = { 0, 0, 0, 0 };
    switch (op) {
    case F1_ABS: r = A & 0x7fffffffu; break;
    case F1_NEG: r = A ^ 0x80000000u; break;
    case F1_SCVTF: r = nv_u((nv_f4)_mm_cvtepi32_ps((__m128i)A)); break;
    case F1_SQRT: {
        nv_f4 s = (nv_f4)_mm_sqrt_ps((__m128)a);
        if (__builtin_expect(nv_nanmask(s) & ((1 << n) - 1), 0)) { nv_cold_sqrt(d, A, n); return; }
        r = nv_u(s);
        break;
    }
    case F1_CMEQ0: r = (nv_u4)(a == z); break;
    case F1_CMGT0: r = (nv_u4)(a > z); break;
    case F1_CMGE0: r = (nv_u4)(a >= z); break;
    case F1_CMLT0: r = (nv_u4)(a < z); break;
    default: r = (nv_u4)(a <= z); break;
    }
    nv_st(d, r & nv_lomask(n));
}

/* TBL / TBX: PSHUFB per table register (an index byte with its top bit set gives 0); without SSSE3, neon.h's byte loop */
static inline void nv_tblw(Ctx *c, int rd, int rn, int len, int rm, int q, int tbx, int ssse3) {
    if (!__builtin_expect(ssse3, 1)) { v_tbl(c, rd, rn, len, rm, q, tbx); return; }
    __m128i idx = (__m128i)nv_ld(&c->v[rm]), res = _mm_setzero_si128();
    RT_LANES(k, 0, len, {
        __m128i s = _mm_sub_epi8(idx, _mm_set1_epi8((char)(16 * k)));
        __m128i inr = _mm_cmpeq_epi8(_mm_min_epu8(s, _mm_set1_epi8(15)), s);             /* s < 16, unsigned */
        res = _mm_or_si128(res, nv_pshufb((__m128i)nv_ld(&c->v[(rn + k) & 31]), _mm_or_si128(s, _mm_andnot_si128(inr, _mm_set1_epi8(-128)))));
    })
    if (tbx) {
        __m128i lim = _mm_set1_epi8((char)(16 * len - 1)), any = _mm_cmpeq_epi8(_mm_min_epu8(idx, lim), idx);
        res = _mm_or_si128(_mm_and_si128(any, res), _mm_andnot_si128(any, (__m128i)nv_ld(&c->v[rd])));
    }
    if (!q) res = _mm_move_epi64(res);
    nv_st(&c->v[rd], (nv_u4)res);
}

static inline void nv_tbl(Ctx *c, int rd, int rn, int len, int rm, int q, int tbx) { nv_tblw(c, rd, rn, len, rm, q, tbx, rt_cpu_ssse3); }

/* INS / MOV element (vset on a register): the register read, the lane replaced, one store */
static inline void nv_vset(V128 *v, int sz, int i, uint64_t x) {
    nv_u4 A = nv_ld(v);
    switch (sz) {
    case 0: { int s = 8 * (i & 3); A[i >> 2] = (A[i >> 2] & ~(0xffu << s)) | ((uint32_t)(uint8_t)x << s); break; }
    case 1: { int s = 16 * (i & 1); A[i >> 1] = (A[i >> 1] & ~(0xffffu << s)) | ((uint32_t)(uint16_t)x << s); break; }
    case 2: A[i] = (uint32_t)x; break;
    default: A[2 * i] = (uint32_t)x; A[2 * i + 1] = (uint32_t)(x >> 32); break;
    }
    nv_st(v, A);
}

static inline void nv_dup(V128 *d, uint64_t value, int sz, int n) {
    uint32_t w = sz == 0 ? (uint32_t)(uint8_t)value * 0x01010101u : sz == 1 ? (uint32_t)(uint16_t)value * 0x00010001u : (uint32_t)value;
    uint32_t h = sz == 3 ? (uint32_t)(value >> 32) : w;
    int full = (n << sz) == 16;
    nv_u4 r = { w, h, full ? w : 0, full ? h : 0 };
    nv_st(d, r);
}

#define NV_EXT16(I) case I: r = __builtin_shufflevector(a, b, I, I + 1, I + 2, I + 3, I + 4, I + 5, I + 6, I + 7, I + 8, I + 9, I + 10, I + 11, I + 12, I + 13, I + 14, I + 15); break;
static inline void nv_ext(V128 *d, const V128 *pn, const V128 *pm, int imm, int q) {
    if (!q) { v_ext(d, pn, pm, imm, q); return; }
    nv_b16 a = (nv_b16)nv_ld(pn), b = (nv_b16)nv_ld(pm), r;
    switch (imm & 15) {
    NV_EXT16(0) NV_EXT16(1) NV_EXT16(2) NV_EXT16(3) NV_EXT16(4) NV_EXT16(5) NV_EXT16(6) NV_EXT16(7)
    NV_EXT16(8) NV_EXT16(9) NV_EXT16(10) NV_EXT16(11) NV_EXT16(12) NV_EXT16(13) NV_EXT16(14) default: NV_EXT16(15)
    }
    nv_st(d, (nv_u4)r);
}

static inline void nv_bitsel(V128 *d, const V128 *pn, const V128 *pm, int kind, int q) {
    nv_u4 x = nv_ld(d), a = nv_ld(pn), m = nv_ld(pm), r;
    r = kind == 1 ? (x & a) | (~x & m) : kind == 2 ? (x & ~m) | (a & m) : (x & m) | (a & ~m);
    nv_st(d, r & nv_lomask(q ? 4 : 2));
}

/* UZP / TRN / ZIP of 32-bit (n 2 / 4) and 64-bit (n 2) elements; the rest: neon.h */
static inline void nv_perm(V128 *d, const V128 *pn, const V128 *pm, int opcode, int sz, int n) {
    int part = (opcode >> 2) & 1, kind = opcode & 3;
    if (!((sz == 2 && (n == 2 || n == 4)) || (sz == 3 && n == 2)) || kind == 0) { v_perm(d, pn, pm, opcode, sz, n); return; }
    nv_u4 a = nv_ld(pn), b = nv_ld(pm), r;
    if (sz == 3) r = part ? __builtin_shufflevector(a, b, 2, 3, 6, 7) : __builtin_shufflevector(a, b, 0, 1, 4, 5);
    else if (n == 2) { r = part ? __builtin_shufflevector(a, b, 1, 5, 2, 2) : __builtin_shufflevector(a, b, 0, 4, 2, 2); r &= nv_lomask(2); }
    else if (kind == 1) r = part ? __builtin_shufflevector(a, b, 1, 3, 5, 7) : __builtin_shufflevector(a, b, 0, 2, 4, 6);
    else if (kind == 2) r = part ? __builtin_shufflevector(a, b, 1, 5, 3, 7) : __builtin_shufflevector(a, b, 0, 4, 2, 6);
    else r = part ? __builtin_shufflevector(a, b, 2, 6, 3, 7) : __builtin_shufflevector(a, b, 0, 4, 1, 5);
    nv_st(d, r);
}

/* ---- (NEON generator 2, 16/11) the statements tools/lifter/neongen.py writes into the lifted code (RECOMP_NEON_CONST / RECOMP_NEON_BITS);
 * none of them is used by code generated without those switches ----
 * NVK_LDK(p, addr, n, w0..w3): a vector load from main's read-only data (addr, n bytes) whose bytes the generator read from main.elf:
 *     the register set to them (n < 16: the rest zero, as the load), no memory access.
 * NVK_TBL(c, rd, rn, len, rm, q, tbx, e): a TBL / TBX whose index vector the generator knows (folded load or MOVI in the same straight
 *     piece of code): e is the result as a fixed shuffle of whole words of the table registers (and zeros / the destination's words).
 * NVK_BITS(d, n, m, op, q): AND / EOR / BIC / ORR / ORN of vectors (op 0..4), one 16-byte write (recomp.py: two 64-bit halves).
 * nv_st1f / nv_st1df / nv_st1w / nv_st1d: a scalar result written with the rest of the register zero, one 16-byte write (recomp.py:
 *     vzero and a narrower store). */
static inline nv_u4 nv_bitop(nv_u4 a, nv_u4 b, int op) { return op == 0 ? a & b : op == 1 ? a ^ b : op == 2 ? a & ~b : op == 3 ? a | b : a | ~b; }
static inline void nv_bits(V128 *d, const V128 *pn, const V128 *pm, int op, int q) { nv_st(d, nv_bitop(nv_ld(pn), nv_ld(pm), op) & nv_lomask(q ? 4 : 2)); }
static inline void nv_st1w(V128 *d, uint32_t x) { nv_u4 r = { x, 0, 0, 0 }; nv_st(d, r); }
static inline void nv_st1f(V128 *d, float f) { nv_st1w(d, nv_fb(f)); }
static inline void nv_st1d(V128 *d, uint64_t x) { nv_u4 r = { (uint32_t)x, (uint32_t)(x >> 32), 0, 0 }; nv_st(d, r); }
static inline void nv_st1df(V128 *d, double f) { nv_st1d(d, nv_db(f)); }

#ifdef RECOMP_NEON_CHECK
/* (test DLL) the generator's forms checked: rt_nchk[4] folded loads (against the memory at run time), [5] fixed shuffles (against
 * neon.h's v_tbl with the index register), [6] vector bit operations (against the 64-bit halves); a difference counts in [3] (op 500 /
 * 501 / 502: must stay 0) */
extern __declspec(dllexport) volatile long long rt_nchk[8];
static __attribute__((noinline)) void nvc_bad(int kind, int op, const V128 *got, const V128 *other, const V128 *x, const V128 *y);
static __attribute__((noinline)) void nvk_ldk(V128 *p, uint64_t a, int n, uint32_t w0, uint32_t w1, uint32_t w2, uint32_t w3) {
    V128 m = rdv(a, n), k; k.w[0] = w0; k.w[1] = w1; k.w[2] = w2; k.w[3] = w3;
    rt_nchk[0]++; rt_nchk[4]++;
    if (memcmp(&m, &k, 16)) { V128 ad; ad.d[0] = a; ad.d[1] = (uint64_t)n; nvc_bad(3, 500, &k, &m, &ad, &ad); }
    *p = k;
}
static __attribute__((noinline)) void nvk_tbl(Ctx *c, int rd, int rn, int len, int rm, int q, int tbx, nv_u4 r) {
    V128 ix = c->v[rm], t0 = c->v[rn & 31], o, got;
    rt_nchk[0]++; rt_nchk[5]++;
    v_tbl(c, rd, rn, len, rm, q, tbx);                              /* neon.h's byte loop (the macro below is defined later) */
    o = c->v[rd];
    nv_st(&got, r);
    if (memcmp(&o, &got, 16)) nvc_bad(3, 501, &got, &o, &ix, &t0);
    c->v[rd] = got;
}
static __attribute__((noinline)) void nvk_bits(V128 *d, const V128 *pn, const V128 *pm, int op, int q) {
    V128 a = *pn, b = *pm, o, got;
    rt_nchk[0]++; rt_nchk[6]++;
    for (int i = 0; i < 2; i++) {
        uint64_t x = a.d[i], y = b.d[i];
        o.d[i] = i && !q ? 0 : op == 0 ? x & y : op == 1 ? x ^ y : op == 2 ? x & ~y : op == 3 ? x | y : x | ~y;
    }
    nv_bits(&got, &a, &b, op, q);
    if (memcmp(&o, &got, 16)) nvc_bad(3, 502, &got, &o, &a, &b);
    *d = got;
}
#define NVK_LDK(p, a, n, w0, w1, w2, w3) nvk_ldk(p, a, n, w0, w1, w2, w3)
#define NVK_TBL(c, rd, rn, len, rm, q, tbx, e) nvk_tbl(c, rd, rn, len, rm, q, tbx, e)
#define NVK_BITS(d, n, m, op, q) nvk_bits(d, n, m, op, q)
#else
#define NVK_LDK(p, a, n, w0, w1, w2, w3) nv_st(p, (nv_u4){ w0, w1, w2, w3 })
#define NVK_TBL(c, rd, rn, len, rm, q, tbx, e) nv_st(&(c)->v[rd], e)
#define NVK_BITS(d, n, m, op, q) nv_bits(d, n, m, op, q)
#endif

#ifdef RECOMP_NEON_CHECK
/* (test DLL only, build_opt.py RECOMP_NEON_CHECK) every NEON op of the game checked while it runs: the result of the way in use against
 * the other way (FMA3 + SSSE3 against the generic one, when the CPU has them) and against neon.h's old helper on the same inputs.
 * rt_nchk (game_shim.c, read by the exe: gamedll.c "NEONCHK" lines): [0] ops checked, [1] the two ways differ (must stay 0),
 * [2] differ from the old helper with a NaN or an infinity among the inputs or in the old result (the Arm fixes above), [3] differ
 * from the old helper otherwise (must stay 0); rt_nchk_op: [2] per op (f3v op, 100 + f3e op, 200 + f1v op); rt_nchk_log: the first 8 of [1], [3], [2]
 * (kind, op, 4 words of the result, of the other result, of the first input, of the second). */
extern __declspec(dllexport) volatile long long rt_nchk[8];
extern __declspec(dllexport) volatile uint32_t rt_nchk_log[24][20];
extern __declspec(dllexport) volatile long long rt_nchk_op[512];
static int nvc_special(const V128 *v) { for (int i = 0; i < 4; i++) if ((v->w[i] & 0x7f800000u) == 0x7f800000u) return 1; return 0; }
static __attribute__((noinline)) void nvc_bad(int kind, int op, const V128 *got, const V128 *other, const V128 *x, const V128 *y) {
    long long k = __sync_fetch_and_add(&rt_nchk[kind], 1);
    if (k < 8) {
        volatile uint32_t *l = rt_nchk_log[(kind == 1 ? 0 : kind == 3 ? 8 : 16) + k];
        l[0] = (uint32_t)kind; l[1] = (uint32_t)op;
        for (int i = 0; i < 4; i++) { l[2 + i] = got->w[i]; l[6 + i] = other->w[i]; l[10 + i] = x->w[i]; l[14 + i] = y->w[i]; }
    }
}
static __attribute__((noinline)) void nvc_old(int op, const V128 *got, const V128 *old, const V128 *x, const V128 *y, const V128 *z, int fp) {
    if (!memcmp(got, old, 16)) return;
    if (fp && (nvc_special(x) || nvc_special(y) || nvc_special(z) || nvc_special(old))) { __sync_fetch_and_add(&rt_nchk_op[op & 511], 1); nvc_bad(2, op, got, old, x, y); return; }
    nvc_bad(3, op, got, old, x, y);
}
static inline void nvc_f3v(V128 *d, const V128 *pn, const V128 *pm, int op, int dbl, int n) {
    V128 a = *pn, b = *pm, d0 = *d, g = d0, o = d0;
    rt_nchk[0]++;
    nv_f3vw(d, &a, &b, op, dbl, n, rt_cpu_fma);
    if (rt_cpu_fma) { nv_f3vw(&g, &a, &b, op, dbl, n, 0); if (memcmp(d, &g, 16)) nvc_bad(1, op, d, &g, &a, &b); }
    v_f3v(&o, &a, &b, op, dbl, n);
    nvc_old(op, d, &o, &a, &b, &d0, 1);
}
static inline void nvc_f3e(V128 *d, const V128 *pn, const V128 *pm, int idx, int op, int dbl, int n) {
    V128 a = *pn, b = *pm, d0 = *d, g = d0, o = d0;
    rt_nchk[0]++;
    nv_f3ew(d, &a, &b, idx, op, dbl, n, rt_cpu_fma);
    if (rt_cpu_fma) { nv_f3ew(&g, &a, &b, idx, op, dbl, n, 0); if (memcmp(d, &g, 16)) nvc_bad(1, 100 + op, d, &g, &a, &b); }
    v_f3e(&o, &a, &b, idx, op, dbl, n);
    nvc_old(100 + op, d, &o, &a, &b, &d0, 1);
}
static inline void nvc_f1v(V128 *d, const V128 *pn, int op, int dbl, int n) {
    V128 a = *pn, d0 = *d, o = d0;
    rt_nchk[0]++;
    nv_f1v(d, &a, op, dbl, n);
    v_f1v(&o, &a, op, dbl, n);
    nvc_old(200 + op, d, &o, &a, &a, &d0, 1);
}
static inline void nvc_tbl(Ctx *c, int rd, int rn, int len, int rm, int q, int tbx) {
    V128 t[4], ix = c->v[rm], d0 = c->v[rd], g, o;
    for (int i = 0; i < len; i++) t[i] = c->v[(rn + i) & 31];
    rt_nchk[0]++;
    if (rt_cpu_ssse3) {
        nv_tblw(c, rd, rn, len, rm, q, tbx, 0); g = c->v[rd];                   /* the other way first, then the registers put back */
        c->v[rd] = d0; for (int i = 0; i < len; i++) c->v[(rn + i) & 31] = t[i]; c->v[rm] = ix;
    }
    nv_tblw(c, rd, rn, len, rm, q, tbx, rt_cpu_ssse3);
    if (rt_cpu_ssse3 && memcmp(&c->v[rd], &g, 16)) nvc_bad(1, 300 + len, &c->v[rd], &g, &ix, &t[0]);
}
#define NVC_BITS(call_new, call_old, opn, x, y) do { V128 o_ = *d; rt_nchk[0]++; call_old; o_ = *d; *d = d0_; call_new; nvc_old(opn, d, &o_, x, y, x, 0); } while (0)
static inline void nvc_vset(V128 *d, int sz, int i, uint64_t x) { V128 d0_ = *d; NVC_BITS(nv_vset(d, sz, i, x), vset(d, sz, i, x), 400 + sz, &d0_, &d0_); }
static inline void nvc_dup(V128 *d, uint64_t v, int sz, int n) { V128 d0_ = *d; NVC_BITS(nv_dup(d, v, sz, n), v_dup(d, v, sz, n), 410 + sz, &d0_, &d0_); }
static inline void nvc_ext(V128 *d, const V128 *pn, const V128 *pm, int imm, int q) {
    V128 a = *pn, b = *pm, d0_ = *d; NVC_BITS(nv_ext(d, &a, &b, imm, q), v_ext(d, &a, &b, imm, q), 420 + imm, &a, &b);
}
static inline void nvc_bitsel(V128 *d, const V128 *pn, const V128 *pm, int kind, int q) {
    V128 a = *pn, b = *pm, d0_ = *d; NVC_BITS(nv_bitsel(d, &a, &b, kind, q), v_bitsel(d, &a, &b, kind, q), 440 + kind, &a, &b);
}
static inline void nvc_perm(V128 *d, const V128 *pn, const V128 *pm, int opcode, int sz, int n) {
    V128 a = *pn, b = *pm, d0_ = *d; NVC_BITS(nv_perm(d, &a, &b, opcode, sz, n), v_perm(d, &a, &b, opcode, sz, n), 450 + opcode, &a, &b);
}
#define v_f3v nvc_f3v
#define v_f3e nvc_f3e
#define v_f1v nvc_f1v
#define v_tbl nvc_tbl
#define vset nvc_vset
#define v_dup nvc_dup
#define v_ext nvc_ext
#define v_bitsel nvc_bitsel
#define v_perm nvc_perm
#else
/* The generated code calls neon.h's names; from here on they are these (neon.h's own versions stay for the cases above) */
#define v_f3v nv_f3v
#define v_f3e nv_f3e
#define v_f1v nv_f1v
#define v_tbl nv_tbl
#define vset nv_vset
#define v_dup nv_dup
#define v_ext nv_ext
#define v_bitsel nv_bitsel
#define v_perm nv_perm
#endif
#endif
