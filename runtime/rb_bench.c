/* (rb-8ms round 12, TEST ONLY) THE STANDARD ROLLBACK BENCH: SSBU_BENCH=<warm>,<measured>,<stages>[,<period>,<depth>]
 * Included by rb_rollback.c (the statics of r1_ring.c / rb_rollback.c). Nothing here runs without SSBU_BENCH.
 * One process, the loop test (SSBU_RB_NET=loop,3, the seeded bots of rb_net.c: the rollbacks are a pure function of the frame
 * number, so every run has the same rollback schedule), the matches set up by SSBU_DEBUG_MATCH_LIST (one stage per line) and
 * walked to by an input sequence (rb8ms/bench.sh). In every match:
 *   - real frames 1..warm: warm-up (nothing counted);
 *   - the next <measured> real frames: the segment. At its end ONE line "BENCH <n> <stage>" (r1 log and stdout) with the columns
 *     of rbtick3/rowsum.py over the segment only: rbmed / p99 / over (ticks with a rollback, wall ms), norm (ticks without),
 *     real / rerun (main-thread M cycles per pass), pre / load / rr / recap (ms per rollback), the k counts, the first and last
 *     re-run pass (ms), start -> poll of the re-run passes but the last;
 *   - then the match is ended: one stock left for both fighters and the bots walk off (rb_net.c g_bot_walkoff_at): the input
 *     sequence goes through the results to the next match;
 *   - after <stages> segments: "BENCH SUMMARY" (means over the stages) and the process ends (debug_mode.c, as SSBU_DEBUG_END).
 * Optional <period>,<depth> (SSBU_BENCH_SCHED, density test): in the segment, a real pass whose frame is a multiple of <period>
 * and that has no rollback of its own gets one of <depth> frames with the same pads (as SSBU_RB_NET_TEST_SCHED): more rollbacks
 * per stage in the same time. Off by default (the natural schedule of the loop bots, as the old harness). */
static int g_bn_on = -1, g_bn_warm, g_bn_meas, g_bn_n, g_bn_done, g_bn_period, g_bn_depth;
static long long g_bn_match = -1;
static struct {
    int nw, nol; long long rb, real, k[RG_MAXSLOT + 2], n_rerun, n_recap, extra;
    double t_pre, t_load, t_rerun, t_recap, cyc[2], ct[4][3], rap[3][4]; long long npass[2], rapn[3];
} g_bn0;
static double g_bn_rb[16], g_bn_norm[16], g_bn_p99[16], g_bn_ol[16]; static char g_bn_name[16][48]; static long long g_bn_extra;
static int bn_on(void) {
    if (g_bn_on < 0) { const char *e = getenv("SSBU_BENCH"); g_bn_on = 0;
        if (e && *e && sscanf(e, "%d,%d,%d,%d,%d", &g_bn_warm, &g_bn_meas, &g_bn_n, &g_bn_period, &g_bn_depth) >= 3 && g_bn_meas > 0 && g_bn_n > 0) {
            g_bn_on = 1; if (g_bn_n > 16) g_bn_n = 16; if (g_bn_depth < 1) g_bn_depth = 2; if (g_bn_depth > 6) g_bn_depth = 6;
            LOG("r1: BENCH on: warm-up %d real frames, segment %d real frames, %d stages%s\n", g_bn_warm, g_bn_meas, g_bn_n, g_bn_period > 0 ? " (extra same-pad rollbacks)" : "");
            printf("[bench] on: warm-up %d, segment %d real frames, %d stages, extra rollbacks every %d frames (depth %d)\n", g_bn_warm, g_bn_meas, g_bn_n, g_bn_period, g_bn_depth); fflush(stdout); } }
    return g_bn_on;
}
static void bn_snap(void) {
    g_bn0.nw = g_rbg_nw; g_bn0.nol = g_rbg_nol; g_bn0.rb = g_rbg_rb; g_bn0.real = g_rbg_real; memcpy(g_bn0.k, g_rbg_k, sizeof g_bn0.k); g_bn0.n_rerun = g_rbg_n_rerun; g_bn0.n_recap = g_rbg_n_recap;
    g_bn0.t_pre = g_rbg_t_pre; g_bn0.t_load = g_rbg_t_load; g_bn0.t_rerun = g_rbg_t_rerun; g_bn0.t_recap = g_rbg_t_recap; g_bn0.extra = g_bn_extra;
    memcpy(g_bn0.cyc, g_rgs_cyc, sizeof g_bn0.cyc); memcpy(g_bn0.npass, g_rgs_npass, sizeof g_bn0.npass); memcpy(g_bn0.ct, g_rgs_ct, sizeof g_bn0.ct);
    memcpy(g_bn0.rap, g_rap, sizeof g_bn0.rap); memcpy(g_bn0.rapn, g_rapn, sizeof g_bn0.rapn);
}
static int bn_cmpf(const void *a, const void *b) { float x = *(const float *)a, y = *(const float *)b; return x < y ? -1 : x > y; }
static void bn_dist(int from, int to, int want, int side, int *n, double *med, double *mean, double *p99, double *over) {      /* want 1: ticks with a rollback, 0: without; side -1 any */
    float *w = malloc((size_t)(to - from + 1) * sizeof *w); int m = 0; double s = 0, o = 0;
    for (int j = from; j < to; j++) if ((want ? g_rbg_wk[j] > 0 : g_rbg_wk[j] == 0) && (side < 0 || (g_rbg_wab && g_rbg_wab[j] == side))) { w[m++] = g_rbg_work[j]; s += g_rbg_work[j]; o += g_rbg_work[j] > 1000.0 / 60.0; }
    *n = m; *med = *mean = *p99 = *over = 0;
    if (m) { qsort(w, (size_t)m, sizeof *w, bn_cmpf); *med = w[m / 2]; *mean = s / m; *p99 = w[m * 99 / 100]; *over = 100.0 * o / m; }
    free(w);
}
static void bn_segment_end(long long i) {
    int to = g_rbg_nw, n, nn; double med, mean, p99, over, nmed, nmean, np99, nover;
    bn_dist(g_bn0.nw, to, 1, -1, &n, &med, &mean, &p99, &over);
    bn_dist(g_bn0.nw, to, 0, -1, &nn, &nmed, &nmean, &np99, &nover);
    long long rb = g_rbg_rb - g_bn0.rb; double rbd = rb ? (double)rb : 1.0;
    double np0 = (double)(g_rgs_npass[0] - g_bn0.npass[0]), np1 = (double)(g_rgs_npass[1] - g_bn0.npass[1]);
    double c0 = np0 > 0 ? (g_rgs_cyc[0] - g_bn0.cyc[0]) / np0 / 1e6 : 0, c1 = np1 > 0 ? (g_rgs_cyc[1] - g_bn0.cyc[1]) / np1 / 1e6 : 0;
    double ct[4][3]; for (int a = 0; a < 4; a++) for (int b = 0; b < 3; b++) ct[a][b] = g_rgs_ct[a][b] - g_bn0.ct[a][b];
    double rapn1 = (double)(g_rapn[1] - g_bn0.rapn[1]), sp1 = rapn1 > 0 ? (g_rap[1][0] - g_bn0.rap[1][0]) / rapn1 : 0;
    const char *dm_bench_stage(void); unsigned sid = rd32(GA(0x71052c41b0ULL) + 0x420);
    int s = g_bn_done; const char *nm = dm_bench_stage(); snprintf(g_bn_name[s], sizeof g_bn_name[s], "%s", nm && *nm ? nm : "?");
    g_bn_rb[s] = med; g_bn_norm[s] = nmed; g_bn_p99[s] = p99;
    char line[1200];
    snprintf(line, sizeof line, "BENCH %d %s | stage id 0x%x | frames %lld-%lld | rbmed %.2f p99 %.2f over %.2f mean %.2f n %d | norm %.2f n %d | real %.3f rerun %.3f | pre %.3f load %.3f rr %.3f recap %.3f "
             "| rollbacks %lld (k1 %lld k2 %lld k3 %lld k4+ %lld; extra %lld) | first %.3f last %.3f middle %.3f ms, start->poll %.3f, after a test reload %.3f",
             s + 1, g_bn_name[s], sid, i - g_bn_meas + 1, i, med, p99, over, mean, n, nmed, nn, c0, c1,
             (g_rbg_t_pre - g_bn0.t_pre) / rbd, (g_rbg_t_load - g_bn0.t_load) / rbd, (g_rbg_t_rerun - g_bn0.t_rerun) / rbd, (g_rbg_t_recap - g_bn0.t_recap) / rbd,
             rb, g_rbg_k[1] - g_bn0.k[1], g_rbg_k[2] - g_bn0.k[2], g_rbg_k[3] - g_bn0.k[3], rb - (g_rbg_k[1] - g_bn0.k[1]) - (g_rbg_k[2] - g_bn0.k[2]) - (g_rbg_k[3] - g_bn0.k[3]), g_bn_extra - g_bn0.extra,
             ct[0][2] > 0 ? ct[0][0] / ct[0][2] : 0, ct[2][2] > 0 ? ct[2][0] / ct[2][2] : 0, ct[1][2] > 0 ? ct[1][0] / ct[1][2] : 0, sp1, ct[3][2] > 0 ? ct[3][0] / ct[3][2] : 0);
    LOG("r1: %s\n", line); printf("[bench] %s\n", line);
    if (g_lw_ev) { printf("[bench] BENCH %d LOADWARM: runs %lld, skipped (lock busy) %lld, pages per run %.0f, %.3f ms per run\n", s + 1, g_lw_runs, g_lw_skips, g_lw_runs ? (double)g_lw_pages / g_lw_runs : 0.0, g_lw_runs ? g_lw_ms / g_lw_runs : 0.0); }
    if (g_rbab > 0 && g_rbg_wab) for (int sd = 0; sd < 2; sd++) {      /* (SSBU_RB_AB: A/B inside the segment, blocks of 8 rollbacks) */
        int an, bn2; double am, amn, ap, ao, bm, bmn, bp, bo; bn_dist(g_bn0.nw, to, 1, sd, &an, &am, &amn, &ap, &ao); bn_dist(g_bn0.nw, to, 0, sd, &bn2, &bm, &bmn, &bp, &bo);
        LOG("r1: BENCH %d AB %d side %s: rbmed %.2f mean %.2f p99 %.2f n %d | norm %.2f n %d\n", s + 1, g_rbab, sd ? "ON" : "off", am, amn, ap, an, bm, bn2);
        printf("[bench] BENCH %d AB %d side %s: rbmed %.2f mean %.2f p99 %.2f n %d | norm %.2f n %d\n", s + 1, g_rbab, sd ? "ON" : "off", am, amn, ap, an, bm, bn2); }
    {   /* (round 13) the online-like cost over the segment (rb_rollback.c g_rbg_olv): every frame period with a rollback, by start */
        int a0 = g_bn0.nol, a1 = g_rbg_olon > 0 ? g_rbg_nol : a0, nk[5] = {0}; double md[5] = {0}, ks[5] = {0};
        float *w = malloc((size_t)(a1 - a0 + 1) * sizeof *w);
        for (int q = 0; q <= 4 && w; q++) { int m = 0; for (int j = a0; j < a1; j++) if (!q || g_rbg_olkind[j] == q) { w[m++] = g_rbg_olv[j]; ks[q] += g_rbg_olk[j]; }
            nk[q] = m; if (m) { qsort(w, (size_t)m, sizeof *w, bn_cmpf); md[q] = w[m / 2]; ks[q] /= m; } }
        free(w); g_bn_ol[s] = md[0];
        snprintf(line, sizeof line, "BENCH %d ONLINE %s | olmed %.2f n %d k %.2f | safe point n %d med %.2f k %.2f | go check n %d med %.2f k %.2f | in the wait n %d med %.2f k %.2f | wait + safe point n %d med %.2f",
                 s + 1, g_bn_name[s], md[0], nk[0], ks[0], nk[1], md[1], ks[1], nk[2], md[2], ks[2], nk[3], md[3], ks[3], nk[4], md[4]);
        LOG("r1: %s\n", line); printf("[bench] %s\n", line); }
    fflush(stdout);
    {   const char *e = getenv("SSBU_BENCH_TICKS"); FILE *tf; char fn[600];      /* every tick of this match so far: "<tick> <work ms> <k> <main M cycles>" (windows studied offline) */
        if (e && *e) { snprintf(fn, sizeof fn, "%s_%d_%s.txt", e, s + 1, g_bn_name[s]);
            if ((tf = fopen(fn, "w"))) { for (int j = 0; j < to; j++) fprintf(tf, "%d %.3f %d %.3f\n", j, g_rbg_work[j], g_rbg_wk[j], g_rbg_wcyc[j]); fclose(tf); } } }
    g_bn_done++;
    if (g_bn_done >= g_bn_n) {
        double a = 0, b = 0; char lst[400]; int at = 0; lst[0] = 0;
        for (int k = 0; k < g_bn_done; k++) { a += g_bn_rb[k]; b += g_bn_norm[k]; at += snprintf(lst + at, sizeof lst - at > 0 ? sizeof lst - at : 0, "%s%s %.2f", k ? ", " : "", g_bn_name[k], g_bn_rb[k]); }
        LOG("r1: BENCH SUMMARY %d stages: rollback tick mean of medians %.3f ms, normal %.3f ms | %s\n", g_bn_done, a / g_bn_done, b / g_bn_done, lst);
        printf("[bench] BENCH SUMMARY %d stages: rollback tick mean of medians %.3f ms, normal %.3f ms | %s\n", g_bn_done, a / g_bn_done, b / g_bn_done, lst);
        { double o = 0; for (int k = 0; k < g_bn_done; k++) o += g_bn_ol[k];
          LOG("r1: BENCH SUMMARY ONLINE %d stages: online-like work per period with a rollback, mean of medians %.3f ms\n", g_bn_done, o / g_bn_done);
          printf("[bench] BENCH SUMMARY ONLINE %d stages: online-like work per period with a rollback, mean of medians %.3f ms\n", g_bn_done, o / g_bn_done); }
        fflush(stdout);
        void dm_bench_exit(long long); dm_bench_exit(i);
    }
}
/* rb_rollback.c rbg_step: the safe point of a real pass (frame i) after its tick was counted */
extern volatile long long g_bot_walkoff_at;            /* rb_net.c */
static int g_bn_state;                                  /* this match: 0 warm-up, 1 in the segment, 2 the segment is over */
static void rbg_bench_tick(long long i) {
    if (!bn_on() || g_bn_done >= g_bn_n) return;
    long long mn = rbn_match_number();
    if (mn != g_bn_match) { g_bn_match = mn; g_bot_walkoff_at = -1; g_bn_state = 0; }
    if (g_bn_state == 2) { void dm_bench_last_stock(void); dm_bench_last_stock(); return; }      /* the segment is over: one stock (again: a load may bring the old count back) */
    long long r = g_rbg_real;
    if (g_bn_state == 0 && r >= g_bn_warm) { bn_snap(); g_bn_state = 1; int dm_bench_stocks(int); LOG("r1: BENCH segment %d starts after frame %lld (stocks %d / %d)\n", g_bn_done + 1, i, dm_bench_stocks(0), dm_bench_stocks(1)); }
    else if (g_bn_state == 1 && r >= (long long)g_bn_warm + g_bn_meas) {
        g_bn_state = 2;
        bn_segment_end(i);
        g_bot_walkoff_at = i + 10;
        void dm_bench_last_stock(void); dm_bench_last_stock();
    }
}
/* rb_rollback.c, the pacing wait: SSBU_BENCH_FAST=<f> (e.g. 3): the warm-up and the walk-off after the segment paced at f x 60 Hz
 * (the stage's own timeline, counted in frames, reaches the segment's start sooner; the segment itself is paced at 60 Hz) */
static double rbg_bench_speed(void) {
    static double f = -1; if (f < 0) { const char *e = getenv("SSBU_BENCH_FAST"); f = e ? atof(e) : 1.0; if (f < 1.0) f = 1.0; if (f > 8.0) f = 8.0; }
    if (g_bn_done >= g_bn_n || g_bn_match != rbn_match_number()) return 1.0;
    return g_bn_state == 1 ? 1.0 : f;
}
/* rb_rollback.c, the real pass's wrong-guess check (optional density test): the frame of a same-pad rollback, or m */
static int rbg_bench_sched(long long i, int m) {
    if (!bn_on() || g_bn_period <= 0 || g_bn_done >= g_bn_n || g_bn_state != 1 || g_bn_match != rbn_match_number()) return m;
    if ((m > 0 && m <= i) || i % g_bn_period || i <= g_bn_depth) return m;
    g_bn_extra++;
    return (int)(i - g_bn_depth + 1);
}
