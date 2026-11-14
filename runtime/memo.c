/* (rb-8ms 2026-11-14) SSBU_RB_MEMO: an EXACT memo of pure subtrees of the game in the re-run passes of a rollback. Included by
 * r1_state.c (after rb_rollback.c). Needs a game DLL built with -DR1_MEMO (rb8ms/build_dll8.sh <name> memo: runtime.h MR_ACC in
 * every guest load / store, tu_prepare.py RECOMP_MEMO_FNS entries); any other DLL: off.
 *   SSBU_RB_MEMO=0 off (default), 1 on, 2 check (every replay is undone and the call runs again, recorded: the two logs and the
 *   registers must be identical; counted and written to the log), 3 record only (the real passes' cost, nothing replayed).
 *   SSBU_RB_MEMO_FNS=<hex>,..: the units (default 71038d70f0, the model update). Only the outermost unit call of main is handled.
 * Real pass (main, ring ready): the unit call is RECORDED: every guest load and store of the lifted code on main inside it, in order
 * (address, size, value), except the call's own stack frames (below its entry SP inside main's guest stack: written by the call
 * before they are read, or garbage), plus the registers it changed. A record is refused when the call left the lifted code (an
 * import bound to host code, a host function, an svc, a site callout, an SDK host call: gamedll.c mo_esc_note), ran a hooked
 * function (hook_host.c counts them for main), used an ordered / exclusive access (shared with other threads), changed SP or
 * overflowed the log. Records are kept per frame (8 slots, frame & 7), found by (unit, x0, how many calls with this x0 came before
 * in the pass).
 * Re-run pass of the same frame: the record is VERIFIED and REPLAYED in one walk of its log: a load compares guest memory with the
 * recorded value; a store is made (its page taken / marked as the lifted store would: MARKCMP semantics, only a change marks; old
 * bytes kept for undo). The lifted code is deterministic: with the same registers that it reads (x0 is the key, SP compared; the
 * unit takes no other argument), the same values at every address it loaded, in order, it takes the same path, loads the same
 * addresses and stores the same values. So when every load matches (the stores of the call before a load are already made, as in
 * the call), the call's effect on guest memory is exactly the record's stores; its registers are then set as it left them and the
 * body is skipped. The first load that differs: the stores made so far are undone and the call runs normally. Not covered (by
 * design, as for any rollback): races with other threads that write what the call reads while it runs. */
#include <intrin.h>
void r1_wrote(uint64_t a, uint64_t n); static void r1_mark_range(uint64_t a, uint64_t n);      /* (below in r1_state.c) */
extern uint64_t g_main_stack_base, g_main_stack_size;     /* os_threads.c: main's guest stack */
#define MO_SLOTS 8
#define MO_ARENA_W ((size_t)1 << 20)                   /* words per slot (8 MB) */
#define MO_HB 13                                        /* index entries per slot (8192) */
#define MO_HDR 8                                        /* header words: fn, x0, sp, nlog, xmask, vmask, fn at exit, occ */
#define MO_REGW (31 + 2 + 64)                           /* exit registers at most: x0..x30, NZCV (2 words), v0..v31 */
#define MO_UNDO_W ((size_t)1 << 18)
enum { MOB_ESC = 1, MOB_HOOK = 2, MOB_ATOMIC = 4, MOB_FULL = 8, MOB_SP = 16, MOB_SIZE = 32, MOB_STALE = 64 };
typedef struct { uint64_t key; long long fr; uint32_t off; } MoIdx;
typedef struct { long long frame; size_t used; uint64_t *w; MoIdx *idx; } MoSlot;
typedef struct { uint64_t x[31]; uint32_t nzcv[4]; V128 v[32]; uint64_t fn; } MoRegs;
static int g_mo_mode, g_mo_nfn; static uint64_t g_mo_fns[8];
static unsigned *g_mo_on; static uint64_t *g_mo_dslo, *g_mo_dsn; static MoSlot g_mo_s[MO_SLOTS];
static DWORD g_mo_tid;                                  /* main (rgs_step) */
static int g_mo_dep, g_mo_rec, g_mo_chk, g_mo_kind; static uint64_t g_mo_top_sp, g_mo_slo, g_mo_hk0; static uint32_t g_mo_bad;
static uint64_t *g_mo_p, *g_mo_end, *g_mo_r; static MoSlot *g_mo_rs; static uint64_t g_mo_rkey; static long long g_mo_rfr;
static MoRegs g_mo_er; static uint64_t *g_mo_ck, *g_mo_chkrec; static uint64_t *g_mo_un; static size_t g_mo_nun, *g_mo_uix, g_mo_nuix;
static long long g_mo_pfr = -1; static int g_mo_pkind = -1; static long long g_mo_pgen, g_mo_pgen_seen = -1, g_mo_occg;
static uint64_t g_mo_occ_k[4096]; static long long g_mo_occ_g[4096]; static int g_mo_occ_n[4096];
static int g_mo_mcmp = 1;
extern volatile DWORD g_mo_rec_tid; extern volatile uint64_t g_hk_memo[2];
/* stats */
static long long g_mo_n[2], g_mo_rok, g_mo_rbad[8], g_mo_hit, g_mo_miss, g_mo_norec, g_mo_spdiff, g_mo_nest, g_mo_stale, g_mo_nostk;
static long long g_mo_ck_ok, g_mo_ck_diff, g_mo_ck_regdiff, g_mo_ck_bad, g_mo_slotfull, g_mo_logw, g_mo_writes, g_mo_wchg, g_mo_undo;
static double g_mo_cyc_rec, g_mo_cyc_hit, g_mo_cyc_miss, g_mo_cyc_run, g_mo_cyc_plain[2]; static long long g_mo_nplain[2]; static int g_mo_tim; static uint64_t g_mo_t0;
static long long g_mo_esc_k[16]; static long long g_mo_esc_sym[8192];
static void mo_off_rec(void) { if (g_mo_on) *g_mo_on = 0; g_mo_rec_tid = 0; g_hk_memo[0] = 0; }
void mo_esc_note(int kind, int sym, uint64_t addr) {      /* gamedll.c: main left the lifted code while it records */
    (void)addr; g_mo_bad |= MOB_ESC; g_mo_esc_k[kind & 15]++;
    if (kind == 2 && sym >= 0 && sym < 8192) g_mo_esc_sym[sym]++;
}
static void mo_acc(uint64_t a, uint64_t lo, uint64_t hi, uint64_t nwk) {      /* (game_shim_r1.c r1_mr_thunk: every register kept) */
    if ((DWORD)__readgsqword(0x48) != g_mo_tid) return;
    if (a - g_mo_slo < g_mo_top_sp - g_mo_slo) return;     /* the call's own stack frames: below its entry SP, in main's guest stack */
    int n = (int)(nwk & 0xffff), w = (int)(nwk >> 16);
    if (w >= 2) { g_mo_bad |= MOB_ATOMIC; return; }
    if ((unsigned)(n - 1) >= 16u) { g_mo_bad |= MOB_SIZE; return; }
    uint64_t *p = g_mo_p; size_t nw = ((size_t)n + 7) >> 3;
    if (p + 1 + nw > g_mo_end) { g_mo_bad |= MOB_FULL; return; }
    p[0] = (a & 0xffffffffffffull) | ((uint64_t)n << 48) | ((uint64_t)w << 63);
    if (n < 8) lo &= (1ull << (8 * n)) - 1; else if (n > 8 && n < 16) hi &= (1ull << (8 * (n - 8))) - 1;
    p[1] = lo; if (nw > 1) p[2] = hi;
    g_mo_p = p + 1 + nw;
}
static inline uint64_t mo_mix(uint64_t h, uint64_t x) { h ^= x + 0x9E3779B97F4A7C15ull + (h << 6) + (h >> 2); return h * 0xff51afd7ed558ccdull; }
static int mo_occ(uint64_t k) {
    for (unsigned i = (unsigned)(k >> 52), n = 0; n < 4096; i = (i + 1) & 4095, n++) {
        if (g_mo_occ_g[i] != g_mo_occg) { g_mo_occ_g[i] = g_mo_occg; g_mo_occ_k[i] = k; g_mo_occ_n[i] = 1; return 0; }
        if (g_mo_occ_k[i] == k) return g_mo_occ_n[i]++;
    }
    return -1;
}
static MoSlot *mo_slot(long long fr, int make) {
    MoSlot *s = &g_mo_s[fr & (MO_SLOTS - 1)];
    if (s->frame == fr) return s;
    if (!make) return NULL;
    s->frame = fr; s->used = 0; return s;              /* (the index entries carry their frame: no clearing) */
}
static MoIdx *mo_find(MoSlot *s, uint64_t key, long long fr, int add) {
    size_t m = ((size_t)1 << MO_HB) - 1;
    for (size_t i = (size_t)(key >> (64 - MO_HB)), n = 0; n <= m; i = (i + 1) & m, n++) {
        MoIdx *e = &s->idx[i];
        if (e->fr != fr) { if (add) { e->fr = fr; e->key = key; return e; } return NULL; }
        if (e->key == key) return e;
    }
    return NULL;
}
static void mo_regs_get(MoRegs *r, const Ctx *c) {
    memcpy(r->x, c->x, sizeof r->x); r->nzcv[0] = c->N; r->nzcv[1] = c->Z; r->nzcv[2] = c->C; r->nzcv[3] = c->V; memcpy(r->v, c->v, sizeof r->v); r->fn = c->fn;
}
/* the registers the call changed (entry g_mo_er -> c), packed at q: returns the words written; masks in xm / vm */
static size_t mo_regs_put(uint64_t *q, const Ctx *c, uint64_t *xm, uint64_t *vm) {
    size_t k = 0; uint64_t x = 0, v = 0;
    for (int r = 0; r < 31; r++) if (c->x[r] != g_mo_er.x[r]) { x |= 1ull << r; q[k++] = c->x[r]; }
    if (c->N != g_mo_er.nzcv[0] || c->Z != g_mo_er.nzcv[1] || c->C != g_mo_er.nzcv[2] || c->V != g_mo_er.nzcv[3]) {
        x |= 1ull << 31; q[k++] = (uint64_t)c->N | (uint64_t)c->Z << 32; q[k++] = (uint64_t)c->C | (uint64_t)c->V << 32; }
    for (int r = 0; r < 32; r++) if (memcmp(&c->v[r], &g_mo_er.v[r], 16)) { v |= 1ull << r; memcpy(q + k, &c->v[r], 16); k += 2; }
    *xm = x; *vm = v; return k;
}
static void mo_regs_set(Ctx *c, const uint64_t *rec) {
    const uint64_t *q = rec + MO_HDR + rec[3]; uint64_t x = rec[4], v = rec[5];
    for (int r = 0; r < 31; r++) if (x >> r & 1) c->x[r] = *q++;
    if (x >> 31 & 1) { c->N = (uint32_t)q[0]; c->Z = (uint32_t)(q[0] >> 32); c->C = (uint32_t)q[1]; c->V = (uint32_t)(q[1] >> 32); q += 2; }
    for (int r = 0; r < 32; r++) if (v >> r & 1) { memcpy(&c->v[r], q, 16); q += 2; }
    c->fn = rec[6];
}
/* (check) would the replay have left the registers as the call did? recorded ones set, the others as they came in (g_mo_er) */
static int mo_regs_match(const Ctx *c, const uint64_t *rec) {
    const uint64_t *q = rec + MO_HDR + rec[3]; uint64_t x = rec[4], v = rec[5];
    for (int r = 0; r < 31; r++) { uint64_t want = (x >> r & 1) ? *q++ : g_mo_er.x[r]; if (c->x[r] != want) return 0; }
    if (x >> 31 & 1) { if (c->N != (uint32_t)q[0] || c->Z != (uint32_t)(q[0] >> 32) || c->C != (uint32_t)q[1] || c->V != (uint32_t)(q[1] >> 32)) return 0; q += 2; }
    else if (c->N != g_mo_er.nzcv[0] || c->Z != g_mo_er.nzcv[1] || c->C != g_mo_er.nzcv[2] || c->V != g_mo_er.nzcv[3]) return 0;
    for (int r = 0; r < 32; r++) { if (v >> r & 1) { if (memcmp(&c->v[r], q, 16)) return 0; q += 2; } else if (memcmp(&c->v[r], &g_mo_er.v[r], 16)) return 0; }
    return 1;
}
static void mo_undo(void) {                             /* the stores of a replay taken back, newest first */
    while (g_mo_nuix) { size_t i = g_mo_uix[--g_mo_nuix]; uint64_t h = g_mo_un[i]; int n = (int)((h >> 48) & 0x3fff);
        memcpy((void *)(uintptr_t)(h & 0xffffffffffffull), g_mo_un + i + 1, (size_t)n); }
    g_mo_nun = 0;
}
/* the walk: 1 = every load matched (the stores are made; keep = 0: taken back again) */
static int mo_replay(const uint64_t *rec, int keep) {
    const uint64_t *p = rec + MO_HDR, *e = p + rec[3]; g_mo_nun = 0; g_mo_nuix = 0;
    while (p < e) {
        uint64_t h = *p; const void *a = (const void *)(uintptr_t)(h & 0xffffffffffffull); int n = (int)((h >> 48) & 0x3fff); size_t nw = ((size_t)n + 7) >> 3;
        const uint64_t *val = p + 1; p += 1 + nw;
        int same = n == 8 ? *(const uint64_t *)a == val[0] : n == 4 ? *(const uint32_t *)a == (uint32_t)val[0] : memcmp(a, val, (size_t)n) == 0;
        if (!(h >> 63)) { if (!same) { mo_undo(); return 0; } continue; }
        g_mo_writes++;
        if (same) { if (!g_mo_mcmp) r1_mark_range((uint64_t)(uintptr_t)a, (uint64_t)n); continue; }
        if (g_mo_nun + 1 + nw > MO_UNDO_W || g_mo_nuix >= MO_UNDO_W / 2) { mo_undo(); return 0; }
        g_mo_uix[g_mo_nuix++] = g_mo_nun; g_mo_un[g_mo_nun] = h; memcpy(g_mo_un + g_mo_nun + 1, a, (size_t)n); g_mo_nun += 1 + nw;
        r1_wrote((uint64_t)(uintptr_t)a, (uint64_t)n); memcpy((void *)(uintptr_t)a, val, (size_t)n); g_mo_wchg++;
    }
    if (!keep) { mo_undo(); g_mo_undo++; }
    return 1;
}
static void mo_stale(void) { if (g_mo_rec || g_mo_chk) { mo_off_rec(); g_mo_stale++; } g_mo_rec = g_mo_chk = g_mo_tim = 0; g_mo_dep = 0; }
static void mo_pass_end(void) { if (g_mo_mode) { if (g_mo_dep) mo_stale(); g_mo_pgen++; g_mo_tid = g_rgp_main; } }        /* rgs_step: every pass ends at the safe point */
static void mo_rec_begin(Ctx *c, uint64_t *dst, uint64_t *end) {
    mo_regs_get(&g_mo_er, c);
    g_mo_r = dst; g_mo_p = dst + MO_HDR; g_mo_end = end;
    g_mo_slo = g_main_stack_base; g_mo_bad = 0; g_mo_hk0 = g_hk_memo[1]; g_hk_memo[0] = g_mo_tid; g_mo_rec_tid = g_mo_tid;
    *g_mo_dslo = g_mo_slo; *g_mo_dsn = g_mo_top_sp - g_mo_slo;      /* (the DLL's own filter: no call for those) */
    g_mo_t0 = __rdtsc(); *g_mo_on = (unsigned)g_mo_tid;
}
/* the record's header and registers; 0 = refused */
static int mo_rec_finish(Ctx *c, uint64_t fn, uint64_t occ) {
    mo_off_rec();
    if (g_hk_memo[1] != g_mo_hk0) g_mo_bad |= MOB_HOOK;
    if (c->x[31] != g_mo_top_sp) g_mo_bad |= MOB_SP;
    if (g_mo_bad) { for (int b = 0; b < 7; b++) if (g_mo_bad >> b & 1) g_mo_rbad[b]++; return 0; }
    uint64_t *r = g_mo_r, xm, vm; size_t nlog = (size_t)(g_mo_p - (r + MO_HDR));
    size_t k = mo_regs_put(r + MO_HDR + nlog, c, &xm, &vm);
    r[0] = fn; r[1] = g_mo_er.x[0]; r[2] = g_mo_top_sp; r[3] = nlog; r[4] = xm; r[5] = vm; r[6] = c->fn; r[7] = occ | (uint64_t)k << 32;
    g_mo_logw += (long long)nlog;
    return 1;
}
static int mo_ent(Ctx *c, uint64_t fn, int out) {
    if ((DWORD)__readgsqword(0x48) != g_mo_tid) return 0;
    int u = 0; while (u < g_mo_nfn && g_mo_fns[u] != fn) u++;
    if (u == g_mo_nfn) return 0;
    if (out) {
        if (g_mo_dep <= 0) return 0;
        if (--g_mo_dep > 0) return 0;
        if (g_mo_tim) { g_mo_tim = 0; g_mo_cyc_plain[g_mo_kind] += (double)(__rdtsc() - g_mo_t0); g_mo_nplain[g_mo_kind]++; return 0; }
        if (g_mo_rec) {                                 /* a real pass's record: kept */
            g_mo_rec = 0; double cy = (double)(__rdtsc() - g_mo_t0); g_mo_cyc_rec += cy;
            if (mo_rec_finish(c, fn, 0)) {
                MoIdx *e = mo_find(g_mo_rs, g_mo_rkey, g_mo_rfr, 1);
                if (e) { e->off = (uint32_t)(g_mo_r - g_mo_rs->w); g_mo_rs->used += MO_HDR + (size_t)g_mo_r[3] + (size_t)(g_mo_r[7] >> 32); g_mo_rok++; }
            }
        } else if (g_mo_chk) {                          /* check: the call ran again, recorded: the same as the replayed record? */
            g_mo_chk = 0; g_mo_cyc_run += (double)(__rdtsc() - g_mo_t0);
            uint64_t *rec = g_mo_chkrec; int rsame = mo_regs_match(c, rec);
            if (!mo_rec_finish(c, fn, 0)) { g_mo_ck_bad++; return 0; }
            uint64_t *r = g_mo_r; int same = r[3] == rec[3] && !memcmp(r + MO_HDR, rec + MO_HDR, (size_t)rec[3] * 8);
            if (same && rsame) g_mo_ck_ok++;
            else {
                if (!same) g_mo_ck_diff++; else g_mo_ck_regdiff++;
                if (g_mo_ck_diff + g_mo_ck_regdiff <= 20) {
                    size_t i = 0, n = r[3] < rec[3] ? r[3] : rec[3]; while (i < n && r[MO_HDR + i] == rec[MO_HDR + i]) i++;
                    LOG("r1: MEMO CHECK DIFFERS (%s): unit %llx x0 %llx frame %lld: log words %llu / %llu, first difference at word %llu (%llx / %llx), register masks %llx %llx / %llx %llx\n",
                        same ? "registers" : "log", (unsigned long long)fn, (unsigned long long)rec[1], g_mo_rfr, (unsigned long long)r[3], (unsigned long long)rec[3], (unsigned long long)i,
                        (unsigned long long)(i < r[3] ? r[MO_HDR + i] : 0), (unsigned long long)(i < rec[3] ? rec[MO_HDR + i] : 0), (unsigned long long)r[4], (unsigned long long)r[5], (unsigned long long)rec[4], (unsigned long long)rec[5]);
                }
            }
        }
        return 0;
    }
    if (g_mo_dep > 0) {
        if (c->x[31] < g_mo_top_sp) { g_mo_dep++; g_mo_nest++; return 0; }      /* inside the outer unit call: its record covers it */
        mo_stale();                                     /* the outer call never ended (an exception went through it): dropped */
    }
    if (!g_rg_ready) return 0;
    long long fr = g_rgs_next; int kind = g_ra_phase == 2;
    if (fr != g_mo_pfr || kind != g_mo_pkind || g_mo_pgen != g_mo_pgen_seen) { g_mo_pfr = fr; g_mo_pkind = kind; g_mo_pgen_seen = g_mo_pgen; g_mo_occg++; }
    uint64_t x0 = c->x[0]; int oc = mo_occ(mo_mix(fn, x0));
    if (oc < 0) return 0;
    uint64_t key = mo_mix(mo_mix(fn, x0), (uint64_t)oc) | 1;
    uint64_t sp = c->x[31];
    if (sp - g_main_stack_base >= g_main_stack_size) { g_mo_nostk++; return 0; }      /* (not on main's guest stack: the stack filter would not hold) */
    g_mo_n[kind]++;
    g_mo_dep = 1; g_mo_top_sp = sp; g_mo_kind = kind;
    if (g_mo_mode == 4) { g_mo_tim = 1; g_mo_t0 = __rdtsc(); return 0; }      /* (4: the unit calls only timed, as they run without the memo) */
    if (!kind) {                                        /* real pass: record */
        MoSlot *s = mo_slot(fr, 1);
        if (s->used + MO_HDR + MO_REGW + 1024 > MO_ARENA_W) { g_mo_slotfull++; return 0; }
        g_mo_rs = s; g_mo_rkey = key; g_mo_rfr = fr; g_mo_rec = 1;
        mo_rec_begin(c, s->w + s->used, s->w + MO_ARENA_W - MO_REGW);
        return 0;
    }
    if (g_mo_mode == 3) return 0;
    MoSlot *s = mo_slot(fr, 0); MoIdx *e = s ? mo_find(s, key, fr, 0) : NULL;
    if (!e) { g_mo_norec++; return 0; }
    uint64_t *rec = s->w + e->off;
    if (rec[2] != sp || rec[1] != x0 || rec[0] != fn) { g_mo_spdiff++; return 0; }
    uint64_t t0 = __rdtsc();
    int ok = mo_replay(rec, g_mo_mode == 1);
    double cy = (double)(__rdtsc() - t0);
    if (!ok) { g_mo_miss++; g_mo_cyc_miss += cy; return 0; }
    g_mo_hit++; g_mo_cyc_hit += cy;
    if (g_mo_mode == 1) { mo_regs_set(c, rec); g_mo_dep = 0; return 1; }
    /* check: the call runs again (memory as before the replay), recorded into the check buffer */
    g_mo_chk = 1; g_mo_chkrec = rec; g_mo_rfr = fr;
    mo_rec_begin(c, g_mo_ck, g_mo_ck + MO_ARENA_W - MO_REGW);
    return 0;
}
static void mo_bind(HMODULE m) {
    const char *e = getenv("SSBU_RB_MEMO"); g_mo_mode = e ? atoi(e) : 0;
    unsigned *on = (unsigned *)GetProcAddress(m, "r1_mr_on");
    void (**pacc)(uint64_t, uint64_t, uint64_t, uint64_t) = (void (**)(uint64_t, uint64_t, uint64_t, uint64_t))GetProcAddress(m, "r1_mr_acc");
    int (**pent)(Ctx *, uint64_t, int) = (int (**)(Ctx *, uint64_t, int))GetProcAddress(m, "r1_mo_ent");
    g_mo_dslo = (uint64_t *)GetProcAddress(m, "r1_mr_slo"); g_mo_dsn = (uint64_t *)GetProcAddress(m, "r1_mr_sn");
    if (!g_mo_mode) { if (on) LOG("r1: the game DLL has the exact memo hooks (SSBU_RB_MEMO off)\n"); return; }
    if (!on || !pacc || !pent || !g_mo_dslo || !g_mo_dsn) { LOG("r1: SSBU_RB_MEMO: the game DLL has no memo hooks (-DR1_MEMO): off\n"); g_mo_mode = 0; return; }
    { const char *f = getenv("SSBU_RB_MEMO_FNS"); if (!f || !f[0]) f = "71038d70f0";
      for (const char *p = f; *p && g_mo_nfn < 8; ) { uint64_t a = strtoull(p, (char **)&p, 16); if (a) g_mo_fns[g_mo_nfn++] = a; while (*p == ',' || *p == ' ') p++; if (!a) break; } }
    { const char *mc = getenv("SSBU_R1_MARKCMP"); g_mo_mcmp = mc ? atoi(mc) != 0 : 1; }
    for (int i = 0; i < MO_SLOTS; i++) {
        g_mo_s[i].frame = -1; g_mo_s[i].w = (uint64_t *)VirtualAlloc(NULL, MO_ARENA_W * 8, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
        g_mo_s[i].idx = (MoIdx *)calloc((size_t)1 << MO_HB, sizeof(MoIdx));
        if (!g_mo_s[i].w || !g_mo_s[i].idx) { LOG("r1: SSBU_RB_MEMO: no memory: off\n"); g_mo_mode = 0; return; }
        for (size_t k = 0; k < ((size_t)1 << MO_HB); k++) g_mo_s[i].idx[k].fr = -1;
    }
    g_mo_ck = (uint64_t *)VirtualAlloc(NULL, MO_ARENA_W * 8, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE); g_mo_un = (uint64_t *)malloc(MO_UNDO_W * 8); g_mo_uix = (size_t *)malloc(MO_UNDO_W / 2 * sizeof(size_t));
    if (!g_mo_ck || !g_mo_un || !g_mo_uix) { g_mo_mode = 0; return; }
    g_mo_on = on; *pacc = mo_acc; MemoryBarrier(); *pent = mo_ent;
    LOG("r1: SSBU_RB_MEMO=%d (%s): exact memo of %d unit(s) in the re-run passes, first %llx; main's guest stack %llx + %llx\n", g_mo_mode,
        g_mo_mode == 1 ? "on" : g_mo_mode == 2 ? "CHECK" : g_mo_mode == 4 ? "times only" : "record only", g_mo_nfn, (unsigned long long)g_mo_fns[0], (unsigned long long)g_main_stack_base, (unsigned long long)g_main_stack_size);
}
static void mo_report(void) {
    if (!g_mo_mode) return;
    double ghz = 1e6;                                   /* (cycles shown in thousands per call) */
    LOG("r1: MEMO: unit calls on main: real %lld, re-run %lld; records kept %lld (log %.0f words each), refused: escape %lld, hook %lld, atomic %lld, full %lld, SP %lld, size %lld; slot full %lld; nested calls %lld, stale %lld, not on main's stack %lld\n",
        g_mo_n[0], g_mo_n[1], g_mo_rok, g_mo_rok ? (double)g_mo_logw / g_mo_rok : 0.0, g_mo_rbad[0], g_mo_rbad[1], g_mo_rbad[2], g_mo_rbad[3], g_mo_rbad[4], g_mo_rbad[5], g_mo_slotfull, g_mo_nest, g_mo_stale, g_mo_nostk);
    LOG("r1: MEMO: re-run calls: replayed %lld (%.1f%%), a load differed %lld, no record %lld, record of another call %lld; kcycles per call: recording %.2f, replay %.2f, failed walk %.2f, check run %.2f; stores %lld (changed %lld), undone replays %lld\n",
        g_mo_hit, g_mo_n[1] ? 100.0 * g_mo_hit / g_mo_n[1] : 0.0, g_mo_miss, g_mo_norec, g_mo_spdiff, g_mo_n[0] ? g_mo_cyc_rec / g_mo_n[0] / 1e3 : 0.0, g_mo_hit ? g_mo_cyc_hit / g_mo_hit / 1e3 : 0.0, g_mo_miss ? g_mo_cyc_miss / g_mo_miss / 1e3 : 0.0,
        g_mo_hit ? g_mo_cyc_run / g_mo_hit / 1e3 : 0.0, g_mo_writes, g_mo_wchg, g_mo_undo);
    (void)ghz;
    if (g_mo_mode == 4) LOG("r1: MEMO TIMES (unit calls as they run, no memo): real %lld calls %.2f kcycles each, re-run %lld calls %.2f kcycles each\n", g_mo_nplain[0], g_mo_nplain[0] ? g_mo_cyc_plain[0] / g_mo_nplain[0] / 1e3 : 0.0, g_mo_nplain[1], g_mo_nplain[1] ? g_mo_cyc_plain[1] / g_mo_nplain[1] / 1e3 : 0.0);
    if (g_mo_mode == 2) LOG("r1: MEMO CHECK: replays compared with the call run again: identical %lld, log differs %lld, registers differ %lld, run not recordable %lld\n", g_mo_ck_ok, g_mo_ck_diff, g_mo_ck_regdiff, g_mo_ck_bad);
    {   char b[600]; int o = 0; for (int k = 0; k < 16; k++) if (g_mo_esc_k[k] && o < 500) o += snprintf(b + o, sizeof b - (size_t)o, " kind %d: %lld;", k, g_mo_esc_k[k]);
        if (o) LOG("r1: MEMO: ways out of the lifted code while recording (gamedll.c):%s\n", b);
        const char *hostrt_import_name(int sym);
        for (int t = 0; t < 8; t++) { int bi = -1; long long bv = 0; for (int i = 0; i < 8192; i++) if (g_mo_esc_sym[i] > bv) { bv = g_mo_esc_sym[i]; bi = i; }
            if (bi < 0) break; LOG("r1: MEMO:   import %s: %lld\n", hostrt_import_name(bi), bv); g_mo_esc_sym[bi] = -bv; }
        for (int i = 0; i < 8192; i++) if (g_mo_esc_sym[i] < 0) g_mo_esc_sym[i] = -g_mo_esc_sym[i]; }
}
