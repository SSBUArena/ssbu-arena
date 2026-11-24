/* LATENCY TRACE (TEST ONLY, off by default): SSBU_LAT_TRACE=1 | 2. Idea after OpenSmash PR #557 (tag each picture with the game
 * frame that drew it, read the tag at the present); reimplemented for this runtime's Vulkan path. Included at the end of display.c.
 *   - a REAL FRAME is one pad read of player 1 on the real timeline (display_l1_poll: not run-ahead's speculative pass, not a
 *     rollback's re-run passes); its number and QPC time are kept (lt_poll);
 *   - every submission of the game carries the real frame it was made in (nvn_gl.c posts nvn_vk_lat_frame_job before it); the
 *     render thread notes that frame on each texture a pass, a clear, a copy or a blit writes (Tex.lt_frame, nvn_vk_draw.c);
 *   - at vkQueuePresentKHR of each picture (at once, or when the kept present goes out) display_lat_present gets the frame of the
 *     window texture shown and the newest frame of the window's textures, and counts:
 *       frames behind  = real frames read since the shown picture's frame (0 = the picture of the newest pad read),
 *       picture age    = pad read of the shown picture's frame -> vkQueuePresentKHR (ms),
 *       older shown    = a window texture held a NEWER picture than the one shown (the window-buffer phase is wrong),
 *       back           = the picture shown is older than the one shown before it (the screen went back in time),
 *       repeats / gaps = the same picture shown again / real frames whose picture was never shown;
 *     per kind of present: 0 no fight (menus), 1 fight without rollback pacing, 2 any present while a rollback match paces the
 *     game (r1_rb_paced), 3 / 4 the player benchmark's measured segments (SSBU_BENCH_OUT: with rollbacks / the same match without).
 * 1 = summary lines every 600 presents of kinds 1-4 and at the end (display_report), 2 = also one "lat:" line per present (with
 * its QPC time: a display can be replayed from them) and per submission. SSBU_FRAME_INDEX's hook also reports, with the trace on,
 * the ring entry the game draws into against the window texture acquired last (display.c fi_hook; observe only at index 2).
 * Use: SSBU_LAT_TRACE=1 on any run (the player benchmark gives both segments in one run), read the "lat-trace: whole run" lines.
 * Off: one cached branch per pad read of player 1 and per submission; nothing on the render thread (g_lt_cur stays 0).
 * SPDX-License-Identifier: GPL-3.0-or-later */
#ifdef _WIN32
static int g_lt_on = -1;
static volatile LONG64 g_lt_f;                          /* real frames so far */
static LONG64 g_lt_qpc[256];                            /* QPC of real frame n's pad read at [n & 255] */
#define LT_K 5
#define LT_BINS 2000                                    /* 0.1 ms bins up to 200 ms */
#define LT_BEH 8
typedef struct { LONG64 n, beh[LT_BEH + 1], none, older, repeats, gaps, back, aged; LONG64 hist[LT_BINS + 1]; double sum, mx; } LtStat;
static LtStat g_lt[LT_K], g_lt_last[LT_K];
static LONG64 g_lt_prev_pic, g_lt_fight_n, g_lt_polls_seen, g_lt_subs_seen, g_lt_rerun_polls;
static const char *const k_lt_name[LT_K] = {"no fight", "fight (no rollback pacing)", "rollback match", "bench: rollback segment", "bench: segment w/o rollback"};
int lat_trace_on(void) {
    if (g_lt_on < 0) {
        const char *e = getenv("SSBU_LAT_TRACE");
        g_lt_on = e && *e ? atoi(e) : 0;
        if (g_lt_on > 0) { printf("lat-trace: on (SSBU_LAT_TRACE=%d): every picture carries the real frame that drew it (OpenSmash #557 idea), counted at vkQueuePresentKHR%c", g_lt_on, 10); fflush(stdout); }
    }
    return g_lt_on;
}
long long lat_trace_frame(void) { return g_lt_f; }
void lat_trace_submitted(void) { g_lt_subs_seen++; }      /* nvn_gl.c, game thread: a submission tagged */
static void lt_poll(LONG64 qpc) {                       /* display_l1_poll, the pad read of player 1 (not run-ahead's speculative pass) */
    if (lat_trace_on() <= 0) return;
    int r1_rb_rerun_main(void), r1_ra_phase(void), rbn_on(void);
    if (r1_rb_rerun_main() || (r1_ra_phase() == 2 && rbn_on() > 0)) { g_lt_rerun_polls++; return; }      /* a rollback's re-run pass (phase 2 of an online rollback match): not a real frame */
    LONG64 f = g_lt_f + 1;
    g_lt_qpc[f & 255] = qpc;
    g_lt_polls_seen++;
    InterlockedExchange64(&g_lt_f, f);
}
static double lt_q(const LtStat *s, double q) {
    LONG64 want = (LONG64)(q * (double)s->aged), acc = 0;
    for (int b = 0; b <= LT_BINS; b++) { acc += s->hist[b]; if (acc > want) return (b + 0.5) / 10.0; }
    return 0;
}
static void lt_line(FILE *out, const char *when, int k, const LtStat *s) {
    if (!s->n) return;
    fprintf(out, "lat-trace: %s, %-27s presents %6lld: frames behind 0:%lld 1:%lld 2:%lld 3:%lld 4+:%lld (no tag %lld); mean %.2f frames; picture age (its pad read -> vkQueuePresentKHR) mean %.2f ms, median %.2f, 95 %% %.2f, 99 %% %.2f, max %.2f; "
            "older picture shown while a newer one was in the window %lld; an older picture than the one before %lld; repeats %lld; real frames never shown %lld\n",
            when, k_lt_name[k], (long long)s->n, (long long)s->beh[0], (long long)s->beh[1], (long long)s->beh[2], (long long)s->beh[3],
            (long long)(s->beh[4] + s->beh[5] + s->beh[6] + s->beh[7] + s->beh[8]), (long long)s->none,
            (s->n - s->none) ? (double)(s->beh[1] + 2 * s->beh[2] + 3 * s->beh[3] + 4 * s->beh[4] + 5 * s->beh[5] + 6 * s->beh[6] + 7 * s->beh[7] + 8 * s->beh[8]) / (double)(s->n - s->none) : 0.0,
            s->aged ? s->sum / (double)s->aged : 0.0, lt_q(s, 0.5), lt_q(s, 0.95), lt_q(s, 0.99), s->mx, (long long)s->older, (long long)s->back, (long long)s->repeats, (long long)s->gaps);
}
/* render thread, right after vkQueuePresentKHR of a game picture: pic = the real frame that last drew the window texture shown,
 * newest = the newest real frame among the window's textures, idx = the window texture's index */
void display_lat_present(long long pic, long long newest, int idx, int fight, int kept, long long pf, const long long *tags) {
    if (g_lt_on <= 0) return;
    LARGE_INTEGER now;
    QueryPerformanceCounter(&now);
    LONG64 f = g_lt_f;
    int r1_rb_paced(void);
    int k = g_pb_rec == 1 ? 3 : g_pb_rec == 2 ? 4 : r1_rb_paced() ? 2 : fight ? 1 : 0;      /* (perf_bench.c g_pb_rec: the player benchmark's measured segments) */
    LtStat *s = &g_lt[k];
    double age = -1;
    s->n++;
    if (pic <= 0) s->none++;
    else {
        LONG64 b = f - pic; if (b < 0) b = 0;
        s->beh[b > LT_BEH ? LT_BEH : b]++;
        if (b < 200 && g_lt_qpc[pic & 255]) {
            age = (double)(now.QuadPart - g_lt_qpc[pic & 255]) * 1000.0 / (double)g_freq.QuadPart;
            if (age >= 0) { int bin = (int)(age * 10.0); s->hist[bin > LT_BINS ? LT_BINS : bin]++; s->aged++; s->sum += age; if (age > s->mx) s->mx = age; }
        }
        if (newest > pic) s->older++;
        if (g_lt_prev_pic > 0 && pic < g_lt_prev_pic) s->back++;
        if (g_lt_prev_pic > 0 && pic == g_lt_prev_pic) s->repeats++;
        else if (g_lt_prev_pic > 0 && pic > g_lt_prev_pic + 1) s->gaps += pic - g_lt_prev_pic - 1;
        g_lt_prev_pic = pic;
    }
    if (g_lt_on >= 2) printf("lat: present %lld at %.3f ms %s texture %d: picture of real frame %lld, newest pad read %lld (%lld behind), newest in the window %lld [%lld %lld %lld], age %.2f ms%s\n",
                             pf, (double)now.QuadPart * 1000.0 / (double)g_freq.QuadPart, k_lt_name[k], idx, pic, (long long)f, pic > 0 ? (long long)(f - pic) : -1LL, newest, tags[0], tags[1], tags[2], age, kept ? " (kept present)" : "");
    if (k && ++g_lt_fight_n % 600 == 0) {
        for (int i = 1; i < LT_K; i++) {
            LtStat d = g_lt[i];
            const LtStat *p = &g_lt_last[i];
            d.n -= p->n; d.none -= p->none; d.older -= p->older; d.back -= p->back; d.repeats -= p->repeats; d.gaps -= p->gaps; d.aged -= p->aged; d.sum -= p->sum;
            for (int j = 0; j <= LT_BEH; j++) d.beh[j] -= p->beh[j];
            for (int j = 0; j <= LT_BINS; j++) d.hist[j] -= p->hist[j];
            if (d.n > 0) lt_line(stdout, "last 600 fight presents", i, &d);
        }
        memcpy(g_lt_last, g_lt, sizeof g_lt);
        fflush(stdout);
    }
}
static void lat_trace_report(FILE *out) {
    if (g_lt_on <= 0) return;
    fprintf(out, "lat-trace: real frames (pad reads of player 1) %lld, pad reads of rollback re-run passes left out %lld, submissions tagged %lld\n", (long long)g_lt_polls_seen, (long long)g_lt_rerun_polls, (long long)g_lt_subs_seen);
    for (int k = 0; k < LT_K; k++) lt_line(out, "whole run", k, &g_lt[k]);
    if (g_fi_rel[0] + g_fi_rel[1] + g_fi_rel[2]) fprintf(out, "lat-trace: frame index calls by (entry drawn - last window acquire index) mod 3: 0: %lld, 1: %lld, 2: %lld\n", (long long)g_fi_rel[0], (long long)g_fi_rel[1], (long long)g_fi_rel[2]);
}
#else
int lat_trace_on(void) { return 0; }
long long lat_trace_frame(void) { return 0; }
void lat_trace_submitted(void) {}
static void lt_poll(LONG64 qpc) { (void)qpc; }
void display_lat_present(long long pic, long long newest, int idx, int fight, int kept, long long pf, const long long *tags) { (void)pic; (void)newest; (void)idx; (void)fight; (void)kept; (void)pf; (void)tags; }
static void lat_trace_report(FILE *out) { (void)out; }
#endif
