/* (0.2.58) THE PLAYER BENCHMARK, the game's side: SSBU_BENCH_OUT=<file.json> (set by the launcher's Benchmark page, with the
 * standard rollback bench of rb_bench.c: SSBU_BENCH, SSBU_DEBUG_MATCH_LIST, SSBU_SEQ, SSBU_RB_NET=loop). Nothing here runs
 * without SSBU_BENCH_OUT. Included at the end of display.c (the presents are counted there).
 *   - times since the process was created: the opening movie (= the boot is over), the first presented frame, the debug
 *     match set-up (the select screen confirmed) and the fight's ready-go (match load = set-up -> ready-go);
 *   - per measured phase (rb_bench.c says when one starts and ends): every present's time, from which the frame times
 *     (present to present: avg, p50, p99, max, 1% low FPS, late and stutter counts, a histogram) and the render thread's holds
 *     for shaders / pipelines made at a draw (the "shader compile hitches"), plus the numbers rb_bench.c adds (game-frame CPU
 *     work per tick, the rollback ticks);
 *   - at the end one JSON object written to the file (first to <file>.tmp, then renamed), then the game ends (rb_bench.c).
 * No personal data: no names, no paths, no account; the launcher adds the PC summary itself.
 * SPDX-License-Identifier: GPL-3.0-or-later */
#ifdef _WIN32
#define PB_PHASES 3                                     /* 0 unused, 1 the rollback segment, 2 the match without rollbacks */
#define PB_MAXF 8192
static int g_pb_on = -1;
static char g_pb_out[600];
static double g_pb_ev_movie = -1, g_pb_ev_first = -1, g_pb_ev_setup = -1, g_pb_ev_fight = -1;
static volatile LONG g_pb_rec;                          /* the phase being recorded at the presents, 0 none */
static LONG64 *g_pb_t[PB_PHASES]; static volatile LONG g_pb_n[PB_PHASES];
static long long g_pb_h0[PB_PHASES], g_pb_h1[PB_PHASES]; static double g_pb_hms0[PB_PHASES], g_pb_hms1[PB_PHASES], g_pb_hmax[PB_PHASES];
static long long g_pb_hf_n; static double g_pb_hf_ms;  /* holds from the ready-go to the end */
static char g_pb_kv[PB_PHASES][1200]; static int g_pb_kvn[PB_PHASES];
static char g_pb_top[1200]; static int g_pb_topn;

void nvn_vk_hold_totals(long long *n, double *ms, double *mx);      /* nvn_vk_m4.c */

int pb_on(void) {
    if (g_pb_on < 0) { const char *e = getenv("SSBU_BENCH_OUT"); g_pb_on = e && *e;
        if (g_pb_on) { snprintf(g_pb_out, sizeof g_pb_out, "%s", e); printf("[pbench] player benchmark on: results to the launcher's file\n"); fflush(stdout); } }
    return g_pb_on;
}
static double pb_since_start(void) {                    /* seconds since the process was created */
    FILETIME c, x, k, u, now; ULARGE_INTEGER a, b;
    if (!GetProcessTimes(GetCurrentProcess(), &c, &x, &k, &u)) return -1;
    GetSystemTimePreciseAsFileTime(&now);
    a.LowPart = c.dwLowDateTime; a.HighPart = c.dwHighDateTime; b.LowPart = now.dwLowDateTime; b.HighPart = now.dwHighDateTime;
    return b.QuadPart > a.QuadPart ? (double)(b.QuadPart - a.QuadPart) / 1e7 : 0;
}
/* nnhid.c input_mark (movie, dmsetup<n>, ...) and debug_mode.c (fighton): the first of each */
void pb_event(const char *what) {
    if (g_pb_on == 0 || !pb_on()) return;
    double t = pb_since_start();
    if (!strcmp(what, "movie") && g_pb_ev_movie < 0) g_pb_ev_movie = t;
    else if (!strcmp(what, "dmsetup1") && g_pb_ev_setup < 0) g_pb_ev_setup = t;
    else if (!strcmp(what, "fighton") && g_pb_ev_fight < 0) { g_pb_ev_fight = t; nvn_vk_hold_totals(&g_pb_hf_n, &g_pb_hf_ms, NULL); }
    else return;
    printf("[pbench] %s at %.2f s\n", what, t); fflush(stdout);
}
/* display.c frame_queued: every present */
static void pb_present(LARGE_INTEGER now) {
    if (g_pb_on <= 0) { if (g_pb_on == 0 || !pb_on()) return; }
    if (g_pb_ev_first < 0) g_pb_ev_first = pb_since_start();
    LONG ph = g_pb_rec;
    if (ph <= 0 || ph >= PB_PHASES || !g_pb_t[ph]) return;
    LONG i = g_pb_n[ph];
    if (i < PB_MAXF) { g_pb_t[ph][i] = now.QuadPart; g_pb_n[ph] = i + 1; }
}
/* rb_bench.c (main thread): phase ph starts (recording) or ends (ph = 0) */
void pb_phase(int ph) {
    LONG cur = g_pb_rec;
    if (cur > 0 && cur < PB_PHASES) nvn_vk_hold_totals(&g_pb_h1[cur], &g_pb_hms1[cur], NULL);
    if (ph > 0 && ph < PB_PHASES) {
        if (!g_pb_t[ph]) g_pb_t[ph] = calloc(PB_MAXF, sizeof(LONG64));
        g_pb_n[ph] = 0; nvn_vk_hold_totals(&g_pb_h0[ph], &g_pb_hms0[ph], &g_pb_hmax[ph]);
    }
    InterlockedExchange(&g_pb_rec, ph > 0 && ph < PB_PHASES ? ph : 0);
    if (ph > 0) { printf("[pbench] phase %d starts at %.2f s\n", ph, pb_since_start()); fflush(stdout); }
}
/* rb_bench.c: one number of a phase (ph 1 / 2) or of the whole run (ph 0) */
void pb_num(int ph, const char *key, double v) {
    char *b = ph > 0 && ph < PB_PHASES ? g_pb_kv[ph] : g_pb_top; int *n = ph > 0 && ph < PB_PHASES ? &g_pb_kvn[ph] : &g_pb_topn;
    int cap = ph > 0 && ph < PB_PHASES ? (int)sizeof g_pb_kv[0] : (int)sizeof g_pb_top;
    if (*n < cap - 80) *n += snprintf(b + *n, (size_t)(cap - *n), "%s\"%s\": %.3f", *n ? ", " : "", key, isfinite(v) ? v : 0.0);
}
static int pb_cmpd(const void *a, const void *b) { double x = *(const double *)a, y = *(const double *)b; return x < y ? -1 : x > y; }
static const double PB_EDGES[] = {8, 12, 17, 20, 25, 34, 50, 100};
static int pb_phase_json(int ph, const char *name, char *o, int cap) {
    int n = g_pb_n[ph], at = 0, m = n > 1 ? n - 1 : 0;
    double *d = m ? malloc((size_t)m * sizeof *d) : NULL, fq = (double)g_freq.QuadPart, sum = 0;
    long long hist[9] = {0}, late = 0, stut = 0;
    for (int i = 0; d && i < m; i++) { d[i] = (double)(g_pb_t[ph][i + 1] - g_pb_t[ph][i]) * 1000.0 / fq; sum += d[i];
        int b = 0; while (b < 8 && d[i] >= PB_EDGES[b]) b++; hist[b]++;
        late += d[i] > 1000.0 / 60.0 * 1.25; stut += d[i] > 50.0; }
    double avg = m ? sum / m : 0, p50 = 0, p95 = 0, p99 = 0, mx = 0, low1 = 0;
    if (d && m) { qsort(d, (size_t)m, sizeof *d, pb_cmpd); p50 = d[m / 2]; p95 = d[(m * 95) / 100 < m ? (m * 95) / 100 : m - 1]; p99 = d[(m * 99) / 100 < m ? (m * 99) / 100 : m - 1]; mx = d[m - 1];
        int k = m / 100; if (k < 1) k = 1; double s = 0; for (int i = m - k; i < m; i++) s += d[i]; low1 = s > 0 ? 1000.0 * k / s : 0; }
    free(d);
    at += snprintf(o + at, (size_t)(cap - at), "\"%s\": {\"frames\": %d, \"seconds\": %.3f, \"fps_avg\": %.2f, \"fps_low1\": %.2f, \"ft_avg\": %.3f, \"ft_p50\": %.3f, \"ft_p95\": %.3f, \"ft_p99\": %.3f, \"ft_max\": %.3f, "
                   "\"late_pct\": %.2f, \"stutters\": %lld, \"shader_holds\": %lld, \"shader_hold_ms\": %.1f, \"hist\": {",
                   name, m, sum / 1000.0, sum > 0 ? 1000.0 * m / sum : 0, low1, avg, p50, p95, p99, mx, m ? 100.0 * late / m : 0, stut,
                   g_pb_h1[ph] - g_pb_h0[ph], g_pb_hms1[ph] - g_pb_hms0[ph]);
    static const char *hn[9] = {"<8", "8-12", "12-17", "17-20", "20-25", "25-34", "34-50", "50-100", ">100"};
    for (int b = 0; b < 9; b++) at += snprintf(o + at, (size_t)(cap - at), "%s\"%s\": %lld", b ? ", " : "", hn[b], hist[b]);
    at += snprintf(o + at, (size_t)(cap - at), "}%s%s}", g_pb_kvn[ph] ? ", " : "", g_pb_kv[ph]);
    printf("[pbench] %s: %d frames, %.2f fps avg, 1%% low %.2f fps, frame time p99 %.2f ms, max %.2f ms, late %.2f %%, stutters %lld, shader holds %lld\n",
           name, m, sum > 0 ? 1000.0 * m / sum : 0, low1, p99, mx, m ? 100.0 * late / m : 0, stut, g_pb_h1[ph] - g_pb_h0[ph]);
    return at;
}
/* rb_bench.c: everything measured: the JSON file; then the caller ends the game */
void pb_finish(const char *stage, int ok) {
    pb_phase(0);
    long long hn = 0; double hms = 0, hmax = 0; nvn_vk_hold_totals(&hn, &hms, &hmax);
    double hz = 0; int lk = 0; display_refresh_info(&hz, &lk);
    static char o[8000]; int at = 0;
    at += snprintf(o + at, sizeof o - at, "{\"format\": 1, \"ok\": %s, \"stage\": \"%s\", \"p1\": \"mario (scripted bot)\", \"p2\": \"fox (CPU)\", "
                   "\"boot_s\": %.3f, \"first_frame_s\": %.3f, \"to_fight_s\": %.3f, \"match_load_s\": %.3f, \"display_hz\": %.2f, \"display_lock_k\": %d, "
                   "\"shader_holds_fight\": %lld, \"shader_hold_ms_fight\": %.1f, \"shader_hold_max_ms\": %.1f, \"end_s\": %.3f",
                   ok ? "true" : "false", stage ? stage : "?", g_pb_ev_movie, g_pb_ev_first, g_pb_ev_fight,
                   g_pb_ev_setup >= 0 && g_pb_ev_fight >= 0 ? g_pb_ev_fight - g_pb_ev_setup : -1.0, hz, lk,
                   g_pb_ev_fight >= 0 ? hn - g_pb_hf_n : 0, g_pb_ev_fight >= 0 ? hms - g_pb_hf_ms : 0, hmax, pb_since_start());
    if (g_pb_topn) at += snprintf(o + at, sizeof o - at, ", %s", g_pb_top);
    if (g_pb_t[1]) { at += snprintf(o + at, sizeof o - at, ", "); at += pb_phase_json(1, "rollback", o + at, (int)sizeof o - at); }
    if (g_pb_t[2]) { at += snprintf(o + at, sizeof o - at, ", "); at += pb_phase_json(2, "match", o + at, (int)sizeof o - at); }
    at += snprintf(o + at, sizeof o - at, "}\n");
    char tmp[620]; snprintf(tmp, sizeof tmp, "%s.tmp", g_pb_out);
    FILE *f = fopen(tmp, "wb");
    if (f) { fwrite(o, 1, (size_t)at, f); fclose(f); MoveFileExA(tmp, g_pb_out, MOVEFILE_REPLACE_EXISTING); }
    printf("[pbench] results %s (%d bytes): %s", f ? "written" : "NOT written", at, o); fflush(stdout);
}
#else      /* (Linux / macOS: not yet; the launcher's benchmark is Windows only) */
int pb_on(void) { return 0; }
void pb_event(const char *what) { (void)what; }
static void pb_present(LARGE_INTEGER now) { (void)now; }
void pb_phase(int ph) { (void)ph; }
void pb_num(int ph, const char *key, double v) { (void)ph; (void)key; (void)v; }
void pb_finish(const char *stage, int ok) { (void)stage; (void)ok; }
#endif
