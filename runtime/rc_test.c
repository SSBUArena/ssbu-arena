/* (rb-8ms 2026-11-14, TEST ONLY) SSBU_RC=1 / 2 with a game DLL built with -DR1_RDCOUNT and RECOMP_RC_FNS (rb8ms/build_dll8.sh <name>
 * rc): can an exact memo of the model update pay in the re-run passes? Included by r1_state.c (after rb_rollback.c).
 * The DLL's RECOMP_RC_FNS functions call r1_rc_ent at their start and end; on main, while one of them runs, every guest load / store
 * of the lifted code calls r1_rc_acc (runtime.h RC_ACC). Each call of a unit (one of rc_units) gets a record: its reads (count, bytes,
 * distinct 64-byte lines, reads of bytes it wrote itself = internal), a hash of every read in order (address + value; stack
 * addresses relative to the SP at the call), a hash of its writes, a hash of its argument registers, and its time. The calls of a
 * real pass are kept by (frame, unit, index in the pass); a re-run pass of the same frame compares its calls with them: same
 * arguments + same reads in the same order = the call could have been taken from the real pass (its writes then must be the same:
 * checked). SSBU_RC=2: only the entries timed (no per-access hook: the units' own speed), =1: everything. Report at the end of the
 * fight (rc_report, called from the ring report). Units nested: an access counts for each unit running. Other threads ignored. */
#define RC_NU 4
static const uint64_t rc_units[RC_NU] = {0x71038d70f0ull, 0x71036906b0ull, 0x710368ab70ull, 0x710389a390ull};
typedef struct { uint64_t args, rh, wh; } RcKey;
typedef struct {
    int u; uint64_t x0, sp, t0, rseq0, wseq0, rh, wh, args; long long nr, nre, br, lines, nw, nstk;
} RcAct;
#define RC_KEEP 4096
static RcKey g_rc_real[16][RC_NU][RC_KEEP]; static long long g_rc_realf[16][RC_NU]; static int g_rc_realn[16][RC_NU];
static int g_rc_idx[RC_NU]; static long long g_rc_idx_frame = -1; static int g_rc_idx_kind = -1;
static RcAct g_rc_st[16]; static int g_rc_dep, g_rc_mode = -1;
#define g_rc_tid g_rgp_main                             /* main (r1_ring.c rgs_step) */
static volatile int *g_rc_on; static uint64_t g_rc_rseq, g_rc_wseq;
/* written granules (8 bytes) and read lines (64 bytes): address -> sequence number of the newest write / read; a slot older than the
 * outermost running call counts as empty (entries of the current call are never older, so a probe stops correctly at one) */
#define RC_HB 21
static uint64_t *g_rc_wa, *g_rc_ws, *g_rc_la, *g_rc_ls; static uint64_t g_rc_wbase, g_rc_lbase;
/* stats [unit][kind 0 real / 1 re-run] */
static long long g_rc_n[RC_NU][2], g_rc_nr[RC_NU][2], g_rc_nre[RC_NU][2], g_rc_br[RC_NU][2], g_rc_lines[RC_NU][2], g_rc_nw[RC_NU][2], g_rc_nstk[RC_NU][2];
static double g_rc_cyc[RC_NU][2];
static long long g_rc_cmp[RC_NU], g_rc_argeq[RC_NU], g_rc_same[RC_NU], g_rc_samew[RC_NU], g_rc_nomatch[RC_NU]; static double g_rc_cyc_same[RC_NU];
static long long g_rc_other_thr, g_rc_deep;
/* (second key) the real pass's calls by (frame, unit, x0, occurrence of that x0 in the pass): not hurt by calls shifting in order */
static inline size_t rc_slot(uint64_t k);
typedef struct { uint64_t key; long long fr; uint64_t rh, wh, args; } RcObj;
#define RC_OB 18
static RcObj *g_rc_ob[16]; static uint64_t g_rc_occ_k[1 << 16]; static long long g_rc_occ_g[1 << 16]; static int g_rc_occ_n[1 << 16]; static long long g_rc_pass_gen;
static long long g_rc_o_cmp[RC_NU], g_rc_o_miss[RC_NU], g_rc_o_rh[RC_NU], g_rc_o_rhw[RC_NU], g_rc_o_all[RC_NU]; static double g_rc_o_cyc[RC_NU];
static int rc_occ(uint64_t k) {      /* the how-many-th call with key k in this pass (0 first) */
    for (size_t i = rc_slot(k) >> (RC_HB - 16), n = 0; n < (1u << 16); i = (i + 1) & 0xffff, n++) {
        if (g_rc_occ_g[i] != g_rc_pass_gen) { g_rc_occ_g[i] = g_rc_pass_gen; g_rc_occ_k[i] = k; g_rc_occ_n[i] = 1; return 0; }
        if (g_rc_occ_k[i] == k) return g_rc_occ_n[i]++;
    }
    return -1;
}
static RcObj *rc_ob_find(int fs, uint64_t key, long long fr, int add) {
    if (!g_rc_ob[fs]) return NULL;
    size_t m = ((size_t)1 << RC_OB) - 1;
    for (size_t i = rc_slot(key) >> (RC_HB - RC_OB), n = 0; n <= m; i = (i + 1) & m, n++) {
        RcObj *o = &g_rc_ob[fs][i];
        if (o->fr != fr) { if (add) { o->fr = fr; o->key = key; return o; } return NULL; }
        if (o->key == key) return o;
    }
    return NULL;
}
static inline uint64_t rc_mix(uint64_t h, uint64_t x) { h ^= x + 0x9E3779B97F4A7C15ull + (h << 6) + (h >> 2); return h * 0xff51afd7ed558ccdull; }
static inline size_t rc_slot(uint64_t k) { return (size_t)((k * 0x9E3779B97F4A7C15ull) >> (64 - RC_HB)); }
/* the newest sequence number of key k (0 = none since base), and set it to s (if s) */
static uint64_t rc_tab(uint64_t *ka, uint64_t *ks, uint64_t base, uint64_t k, uint64_t s) {
    size_t m = ((size_t)1 << RC_HB) - 1;
    for (size_t i = rc_slot(k), n = 0; n <= m; i = (i + 1) & m, n++) {
        if (ks[i] < base || ks[i] == 0) { if (s) { ka[i] = k; ks[i] = s; } return 0; }
        if (ka[i] == k) { uint64_t o = ks[i]; if (s) ks[i] = s; return o; }
    }
    return 0;
}
static void rc_acc(uint64_t a, const void *v, int n, int w) {
    if (GetCurrentThreadId() != g_rc_tid || !g_rc_dep) return;
    if (w & 1 || w == 4) {                                  /* a store: 1 plain, 3 ordered / exclusive, 4 structures (no value) */
        uint64_t s = ++g_rc_wseq;
        for (uint64_t g = a >> 3; g <= (a + (uint64_t)n - 1) >> 3; g++) rc_tab(g_rc_wa, g_rc_ws, g_rc_wbase, g, s);
        uint64_t x[2] = {0, 0}; if (v && n <= 16) memcpy(x, v, (size_t)n);
        for (int d = 0; d < g_rc_dep; d++) { RcAct *e = &g_rc_st[d]; e->nw++; uint64_t ra = a - e->sp + 0x5000000000000000ull;
            int stk = a - e->sp + 0x100000 < 0x200000; e->wh = rc_mix(rc_mix(rc_mix(e->wh, stk ? ra : a), x[0]), x[1] ^ (uint64_t)n); }
        return;
    }
    uint64_t rs = ++g_rc_rseq;                              /* a load */
    uint64_t lw = 0; for (uint64_t g = a >> 3; g <= (a + (uint64_t)n - 1) >> 3; g++) { uint64_t o = rc_tab(g_rc_wa, g_rc_ws, g_rc_wbase, g, 0); if (o > lw) lw = o; }
    uint64_t lr = ~0ull; for (uint64_t l = a >> 6; l <= (a + (uint64_t)n - 1) >> 6; l++) { uint64_t o = rc_tab(g_rc_la, g_rc_ls, g_rc_lbase, l, rs); if (o < lr) lr = o; }
    uint64_t h0 = 0, h1 = 0;
    for (int o = 0; o < n; o += 16) { uint64_t x[2] = {0, 0}; memcpy(x, (const uint8_t *)v + o, (size_t)(n - o < 16 ? n - o : 16)); h0 = rc_mix(h0, x[0]); h1 = rc_mix(h1, x[1]); }
    for (int d = 0; d < g_rc_dep; d++) { RcAct *e = &g_rc_st[d];
        int stk = a - e->sp + 0x100000 < 0x200000; uint64_t ra = stk ? a - e->sp + 0x5000000000000000ull : a;
        e->nr++; e->br += n; e->nstk += stk;
        if (lw <= e->wseq0) e->nre++;                       /* not written by this call before: an input */
        if (lr <= e->rseq0) e->lines++;                     /* a line not read before by this call (one of them, if two) */
        e->rh = rc_mix(rc_mix(rc_mix(e->rh, ra), h0), h1 ^ (uint64_t)n); }
}
static void rc_ent(Ctx *c, uint64_t fn, int out) {
    if (GetCurrentThreadId() != g_rc_tid) { if (!out) g_rc_other_thr++; return; }
    int u = 0; while (u < RC_NU && rc_units[u] != fn) u++;
    if (u == RC_NU) return;
    int kind = g_ra_phase == 2; long long fr = g_rgs_next;
    if (!out) {
        if (g_rc_dep >= 16) { g_rc_deep++; return; }
        if (fr != g_rc_idx_frame || kind != g_rc_idx_kind) { memset(g_rc_idx, 0, sizeof g_rc_idx); g_rc_idx_frame = fr; g_rc_idx_kind = kind; g_rc_pass_gen++; }
        if (g_rc_dep == 0) { g_rc_wbase = g_rc_wseq + 1; g_rc_lbase = g_rc_rseq + 1; }
        RcAct *e = &g_rc_st[g_rc_dep]; memset(e, 0, sizeof *e); e->u = u; e->sp = c->x[31]; e->x0 = c->x[0];
        uint64_t h = 0; for (int i = 0; i < 8; i++) { uint64_t x = c->x[i]; if (x - e->sp + 0x100000 < 0x200000) x = x - e->sp + 0x5000000000000000ull; h = rc_mix(h, x); }
        for (int i = 0; i < 8; i++) { h = rc_mix(h, c->v[i].d[0]); h = rc_mix(h, c->v[i].d[1]); }
        e->args = h; e->rseq0 = g_rc_rseq; e->wseq0 = g_rc_wseq;
        g_rc_dep++; if (g_rc_mode == 1) *g_rc_on = 1;
        e->t0 = __rdtsc();
        return;
    }
    uint64_t t1 = __rdtsc();
    if (!g_rc_dep || g_rc_st[g_rc_dep - 1].u != u) return;      /* (a unit entered before the test began, or past the depth) */
    RcAct *e = &g_rc_st[--g_rc_dep]; if (!g_rc_dep) *g_rc_on = 0;
    double cy = (double)(t1 - e->t0);
    g_rc_n[u][kind]++; g_rc_nr[u][kind] += e->nr; g_rc_nre[u][kind] += e->nre; g_rc_br[u][kind] += e->br; g_rc_lines[u][kind] += e->lines;
    g_rc_nw[u][kind] += e->nw; g_rc_nstk[u][kind] += e->nstk; g_rc_cyc[u][kind] += cy;
    {   int fs = (int)(fr & 15), oc = rc_occ(rc_mix((uint64_t)u + 1, e->x0));
        if (oc >= 0 && g_rc_ob[fs]) { uint64_t key = rc_mix(rc_mix((uint64_t)u + 1, e->x0), (uint64_t)oc);
            if (!kind) { RcObj *o = rc_ob_find(fs, key, fr, 1); if (o) { o->rh = e->rh; o->wh = e->wh; o->args = e->args; } }
            else { RcObj *o = rc_ob_find(fs, key, fr, 0); g_rc_o_cmp[u]++;
                if (!o) g_rc_o_miss[u]++;
                else if (g_rc_mode == 1 && o->rh == e->rh) { g_rc_o_rh[u]++; g_rc_o_cyc[u] += cy; if (o->wh == e->wh) g_rc_o_rhw[u]++; if (o->args == e->args) g_rc_o_all[u]++; } } } }
    int ix = g_rc_idx[u]++; if (ix >= RC_KEEP) return;
    int fs = (int)(fr & 15);
    if (!kind) {                                            /* a real pass: kept for the re-runs of this frame */
        if (g_rc_realf[fs][u] != fr) { g_rc_realf[fs][u] = fr; g_rc_realn[fs][u] = 0; }
        RcKey *k = &g_rc_real[fs][u][ix]; k->args = e->args; k->rh = e->rh; k->wh = e->wh; if (ix + 1 > g_rc_realn[fs][u]) g_rc_realn[fs][u] = ix + 1;
        return;
    }
    if (g_rc_realf[fs][u] != fr || ix >= g_rc_realn[fs][u]) { g_rc_nomatch[u]++; return; }
    RcKey *k = &g_rc_real[fs][u][ix]; g_rc_cmp[u]++;
    if (k->args != e->args) return;
    g_rc_argeq[u]++;
    if (g_rc_mode != 1 || k->rh != e->rh) return;
    g_rc_same[u]++; g_rc_cyc_same[u] += cy; if (k->wh == e->wh) g_rc_samew[u]++;
}
static void rc_bind(HMODULE m) {
    const char *e = getenv("SSBU_RC"); g_rc_mode = e ? atoi(e) : 0;
    void (**pent)(Ctx *, uint64_t, int) = (void (**)(Ctx *, uint64_t, int))GetProcAddress(m, "r1_rc_ent");
    void (**pacc)(uint64_t, const void *, int, int) = (void (**)(uint64_t, const void *, int, int))GetProcAddress(m, "r1_rc_acc");
    g_rc_on = (volatile int *)GetProcAddress(m, "r1_rc_on");
    if (!g_rc_mode) return;
    for (int i = 0; i < 16; i++) g_rc_ob[i] = calloc((size_t)1 << RC_OB, sizeof(RcObj));
    if (!pent || !pacc || !g_rc_on) { LOG("r1: SSBU_RC: the game DLL has no read counting (-DR1_RDCOUNT): off\n"); g_rc_mode = 0; return; }
    size_t sz = ((size_t)1 << RC_HB) * sizeof(uint64_t);
    g_rc_wa = calloc(1, sz); g_rc_ws = calloc(1, sz); g_rc_la = calloc(1, sz); g_rc_ls = calloc(1, sz);
    if (!g_rc_wa || !g_rc_ws || !g_rc_la || !g_rc_ls) { g_rc_mode = 0; return; }
    *pacc = rc_acc; MemoryBarrier(); *pent = rc_ent;
    LOG("r1: SSBU_RC=%d: model update units counted (TEST)\n", g_rc_mode);
}
static void rc_report(void) {
    if (!g_rc_mode) return;
    static const char *kn[2] = {"real", "re-run"};
    for (int u = 0; u < RC_NU; u++) {
        for (int k = 0; k < 2; k++) { long long n = g_rc_n[u][k]; if (!n) continue;
            LOG("r1: RC %llx %s: %lld calls, per call: %.0f reads (%.0f inputs = not written by the call before, %.0f stack, %.0f bytes, %.0f lines), %.0f writes, %.1f kcycles\n",
                (unsigned long long)rc_units[u], kn[k], n, (double)g_rc_nr[u][k] / n, (double)g_rc_nre[u][k] / n, (double)g_rc_nstk[u][k] / n, (double)g_rc_br[u][k] / n,
                (double)g_rc_lines[u][k] / n, (double)g_rc_nw[u][k] / n, g_rc_cyc[u][k] / n / 1000.0); }
        if (g_rc_n[u][1]) LOG("r1: RC %llx re-run calls against the real pass of the same frame: %lld compared (%lld with no real call at that index), same arguments %lld (%.1f%%), same arguments + same reads %lld (%.1f%%; of those same writes %lld), cycles of the same ones %.1f%% of the re-run cycles\n",
            (unsigned long long)rc_units[u], g_rc_cmp[u], g_rc_nomatch[u], g_rc_argeq[u], g_rc_cmp[u] ? 100.0 * g_rc_argeq[u] / g_rc_cmp[u] : 0.0, g_rc_same[u],
            g_rc_cmp[u] ? 100.0 * g_rc_same[u] / g_rc_cmp[u] : 0.0, g_rc_samew[u], g_rc_cyc[u][1] > 0 ? 100.0 * g_rc_cyc_same[u] / g_rc_cyc[u][1] : 0.0);
    }
    for (int u = 0; u < RC_NU; u++) if (g_rc_o_cmp[u])
        LOG("r1: RC %llx re-run calls by object (x0 + its occurrence in the pass): %lld, no real call %lld, same reads (any arguments) %lld (%.1f%%; same writes %lld, same arguments too %lld), cycles of those %.1f%% of the re-run cycles\n",
            (unsigned long long)rc_units[u], g_rc_o_cmp[u], g_rc_o_miss[u], g_rc_o_rh[u], 100.0 * g_rc_o_rh[u] / g_rc_o_cmp[u], g_rc_o_rhw[u], g_rc_o_all[u], g_rc_cyc[u][1] > 0 ? 100.0 * g_rc_o_cyc[u] / g_rc_cyc[u][1] : 0.0);
    LOG("r1: RC calls on other threads %lld, past the depth %lld\n", g_rc_other_thr, g_rc_deep);
}
