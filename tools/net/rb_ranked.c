// SPDX-License-Identifier: GPL-3.0-or-later
/* rb_ranked.c - SSBU Arena quick matches with a recorded result (lobby 1.5), included by rb_lobby.c. The lobby is the referee.
 *
 * Protocol (additions an older game or lobby ignores):
 *   MATCH op 3      a search for a QUICK MATCH (the Arena menu's Online Ranked tile): a rollback match like op 2, but only for a
 *                   player whose JOIN carried a valid lobby ticket (account id), paired only with another op 3 search of the
 *                   same build and code and ANOTHER account. Without an account: MATCH_STATE state 7 (needs an account).
 *                   An older lobby ignores op 3: the game then says "older lobby".
 *   MATCH_STATE     ends (lobby 1.5) with the game id u64 of the quick match (0 = none); an older game reads 49 bytes.
 *  22 RESULT        client: cookie u64, version u8 (1), game id u64, side u8 (0 player 1 / 1 player 2), winner u8 (0 / 1, 0xFF
 *                   none), end u8 (1 last stock lost, 2 time-out decided, 3 the other game went silent, 4 no result), stocks u8 x 2,
 *                   damage u16 x 2 (tenths of %), last finalized frame u32, its checksum u32, last frame both games' checksums
 *                   agreed u32, that checksum u32, desyncs u16, ui_chara u64 x 2, ui_stage u64, flags u8 (1 checksums on)
 *                   = 73 bytes. Sent again every second until a final VERDICT (or 30 s).
 *  23 VERDICT       lobby: game id u64, version u8 (1), verdict u8, winner side u8 (0 / 1 / 0xFF), reason u8, text[64].
 *                   verdict 0 pending (report kept; waiting for the other game), 1 recorded, 2 forfeit (winner side: the
 *                   other one left), 3 no contest (the two reports disagree, or a desync), 4 void (not recorded).
 *                   Answer to every RESULT, and sent by itself to both players when the lobby decides.
 *
 * Referee rules: both reports, same winner, no desync (reported by a game, or seen here: the PADS messages of both games carry
 * a checksum of a finalized frame, compared here when both sent one for the same frame) -> recorded. Reports that disagree or a
 * desync -> no contest. A game that sent no PADS for 10 s while the other kept sending them (the match started: both sent
 * PADS of frame 1 or later) -> forfeit, the silent one loses (closed the game, lost the connection, sat in a pause). One
 * report only: from a game that ended normally, taken when the other game did not report within 20 s (it left: a loss for
 * it anyway); otherwise void after 60 s. Both silent without reports -> void. A match that never started is not recorded.
 * Every decided match that started is one JSON line in --results-file (account ids, names, fighters, stage, winner, kind,
 * frames, build; NEVER an internet address). tools/net/arena_results.py sends the lines to the accounts database. */
#define KIND_QUICK 3
#define MS_NO_ACCOUNT 7
enum { RESULT = 22, VERDICT = 23 };
enum { V_PENDING, V_RECORDED, V_FORFEIT, V_NO_CONTEST, V_VOID };
#define RK_GAMES 1024
#define RK_CK 128
#define RK_REPORT 61                                /* RESULT body after the cookie */
typedef struct {
    int have, said_silent; double at;
    int side, winner, end, stocks[2], dmg[2]; uint32_t frames, fin_ck, agree_f, agree_ck; int desyncs; uint64_t chara[2], stage; int flags;
} RkReport;
typedef struct {
    int used; uint64_t id; double at, started_at, decided_at, last_seen_any[2];
    char uid[2][40], name[2][NAME_LEN + 1]; uint8_t build[BUILD_LEN]; int build_len;
    double last_pads[2]; int32_t newest[2]; int started;
    int32_t ckf[2][RK_CK]; uint32_t ckv[2][RK_CK];
    long long ck_same; int ck_diff; int32_t ck_diff_f;
    RkReport rep[2];
    int verdict, winner, reason; char text[64];
} RkGame;
static RkGame g_rk[RK_GAMES];
static char g_rk_file[256];
static unsigned long long g_rk_decided[5];

static const char *rk_vname(int v) { static const char *n[] = {"pending", "recorded", "forfeit", "no_contest", "void"}; return v >= 0 && v <= 4 ? n[v] : "?"; }
static RkGame *rk_by_id(uint64_t id) { if (!id) return NULL; for (int i = 0; i < RK_GAMES; i++) if (g_rk[i].used && g_rk[i].id == id) return &g_rk[i]; return NULL; }
static RkGame *rk_of(Client *c) { int g = c->rk_game; return g > 0 && g <= RK_GAMES && g_rk[g - 1].used && g_rk[g - 1].id == c->rk_gid ? &g_rk[g - 1] : NULL; }
static int rk_side(const RkGame *g, const Client *c) { return c->uid[0] && !strcmp(g->uid[0], c->uid) ? 0 : c->uid[0] && !strcmp(g->uid[1], c->uid) ? 1 : -1; }
static Client *rk_client(const RkGame *g, int side) {          /* the player's connected client (it may have joined again) */
    for (int i = 0; i < g_hi; i++) if (g_c[i].used && g_c[i].uid[0] && !strcmp(g_c[i].uid, g->uid[side])) return &g_c[i];
    return NULL;
}

/* MATCH op 3 paired two players (host = side 0 = player 1) */
static void rk_paired(Client *h, Client *gst) {
    RkGame *g = NULL; int idx = -1;
    for (int i = 0; i < RK_GAMES; i++) if (!g_rk[i].used) { g = &g_rk[i]; idx = i; break; }
    if (!g) {                                               /* full: the oldest decided one goes */
        for (int i = 0; i < RK_GAMES; i++) if (g_rk[i].verdict != V_PENDING && (!g || g_rk[i].decided_at < g->decided_at)) { g = &g_rk[i]; idx = i; }
        if (!g) { h->rk_game = gst->rk_game = 0; h->rk_gid = gst->rk_gid = 0; say("quick match: no free game record (%d running): not recorded", RK_GAMES); return; }
    }
    memset(g, 0, sizeof *g);
    g->used = 1; g->id = rnd64() & 0x7FFFFFFFFFFFFFFFull; if (!g->id) g->id = 1;
    g->at = g_now; g->verdict = V_PENDING; g->winner = 0xFF;
    snprintf(g->uid[0], sizeof g->uid[0], "%s", h->uid); snprintf(g->uid[1], sizeof g->uid[1], "%s", gst->uid);
    snprintf(g->name[0], sizeof g->name[0], "%s", h->name); snprintf(g->name[1], sizeof g->name[1], "%s", gst->name);
    memcpy(g->build, h->build, BUILD_LEN); g->build_len = h->build_len;
    g->last_seen_any[0] = g->last_seen_any[1] = g_now;
    for (int s = 0; s < 2; s++) for (int k = 0; k < RK_CK; k++) g->ckf[s][k] = -1;
    h->rk_game = gst->rk_game = idx + 1; h->rk_gid = gst->rk_gid = g->id;
    say("quick match %016llx: %s (player 1) vs %s (player 2)", (unsigned long long)g->id, h->name, gst->name);
}

static void rk_json_str(FILE *f, const char *s) {
    fputc('"', f);
    for (; *s; s++) { unsigned char ch = (unsigned char)*s; if (ch == '"' || ch == '\\') fprintf(f, "\\%c", ch); else if (ch < 0x20) fprintf(f, "\\u%04x", ch); else fputc(ch, f); }
    fputc('"', f);
}
static void rk_iso(char *out, size_t n, double mono_at) {        /* a monotonic time as UTC ISO 8601 */
    time_t t = time(NULL) - (time_t)(g_now - mono_at); struct tm tm; gmtime_r(&t, &tm);
    strftime(out, n, "%Y-%m-%dT%H:%M:%SZ", &tm);
}
static void rk_store(RkGame *g) {                               /* one JSON line: no internet address */
    if (!g->started || !g_rk_file[0]) return;
    FILE *f = fopen(g_rk_file, "a");
    if (!f) { say("quick match %016llx: cannot write %s", (unsigned long long)g->id, g_rk_file); return; }
    const RkReport *r = g->rep[0].have ? &g->rep[0] : g->rep[1].have ? &g->rep[1] : NULL;
    char played[32], decided[32], b[BUILD_LEN + 1];
    rk_iso(played, sizeof played, g->started_at ? g->started_at : g->at); rk_iso(decided, sizeof decided, g->decided_at);
    memcpy(b, g->build, (size_t)g->build_len); b[g->build_len] = 0;
    fprintf(f, "{\"v\":1,\"id\":\"%llu\",\"played_at\":\"%s\",\"decided_at\":\"%s\",\"p1\":", (unsigned long long)g->id, played, decided);
    rk_json_str(f, g->uid[0]); fputs(",\"p2\":", f); rk_json_str(f, g->uid[1]);
    fputs(",\"p1_name\":", f); rk_json_str(f, g->name[0]); fputs(",\"p2_name\":", f); rk_json_str(f, g->name[1]);
    if (r) fprintf(f, ",\"p1_fighter\":\"%016llx\",\"p2_fighter\":\"%016llx\",\"stage\":\"%016llx\",\"p1_stocks\":%d,\"p2_stocks\":%d,\"p1_damage\":%.1f,\"p2_damage\":%.1f,\"end\":%d",
                   (unsigned long long)r->chara[0], (unsigned long long)r->chara[1], (unsigned long long)r->stage, r->stocks[0], r->stocks[1], r->dmg[0] / 10.0, r->dmg[1] / 10.0, r->end);
    fputs(",\"winner\":", f);
    if ((g->verdict == V_RECORDED || g->verdict == V_FORFEIT) && g->winner <= 1) rk_json_str(f, g->uid[g->winner]); else fputs("null", f);
    fprintf(f, ",\"result\":\"%s\",\"reason\":", rk_vname(g->verdict)); rk_json_str(f, g->text);
    uint32_t frames = 0; for (int s = 0; s < 2; s++) if (g->rep[s].have && g->rep[s].frames > frames) frames = g->rep[s].frames;
    if (!frames) frames = (uint32_t)(g->newest[0] > g->newest[1] ? g->newest[0] : g->newest[1]);
    fprintf(f, ",\"frames\":%u,\"reports\":%d,\"checks_agreed\":%lld,\"build\":", frames, g->rep[0].have + g->rep[1].have, g->ck_same);
    rk_json_str(f, b); fputs("}\n", f);
    fclose(f);
}
static void rk_verdict_send(const RkGame *g, Client *c, int side) {
    if (!c) return;
    uint8_t b[4 + 8 + 4 + 64], *q = hdr(b, VERDICT);
    q = w64(q, g->id); q = w8(q, 1); q = w8(q, (unsigned)g->verdict); q = w8(q, (unsigned)g->winner); q = w8(q, (unsigned)g->reason);
    const char *t = g->verdict == V_PENDING ? "waiting for the other game's result" : g->text;
    wpad(q, t, strlen(t), 64);
    csend(b, sizeof b, c);
    (void)side;
}
static void rk_decide(RkGame *g, int verdict, int winner, int reason, const char *fmt, ...) {
    g->verdict = verdict; g->winner = winner; g->reason = reason; g->decided_at = g_now;
    va_list a; va_start(a, fmt); vsnprintf(g->text, sizeof g->text, fmt, a); va_end(a);
    g_rk_decided[verdict]++;
    say("quick match %016llx: %s%s%s (%s)", (unsigned long long)g->id, rk_vname(verdict), winner <= 1 ? ", winner " : "", winner <= 1 ? g->name[winner] : "", g->text);
    rk_store(g);
    for (int s = 0; s < 2; s++) rk_verdict_send(g, rk_client(g, s), s);
    if (winner <= 1) { Client *h = rk_client(g, 0), *gu = rk_client(g, 1); if (h && gu) rec_result(h, gu, winner); }     /* (rb_replay_rec.c: the replay's winner) */
}
/* the referee: called on every event and once per second */
static void rk_judge(RkGame *g) {
    if (!g->used || g->verdict != V_PENDING) return;
    RkReport *r = g->rep;
    int normal[2] = {r[0].have && (r[0].end == 1 || r[0].end == 2) && r[0].winner <= 1, r[1].have && (r[1].end == 1 || r[1].end == 2) && r[1].winner <= 1};
    if (!g->started) {                                      /* (not stored: rk_store writes started matches only) */
        int gone = 0; for (int s = 0; s < 2; s++) gone |= !rk_client(g, s) || g_now - g->last_seen_any[s] > 15.0;
        if (g_now - g->at > g_match_for + 30 || (gone && g_now - g->at > 20.0)) rk_decide(g, V_VOID, 0xFF, 8, "the match did not start: not recorded");
        return;
    }
    if (g->ck_diff || (r[0].have && r[0].desyncs) || (r[1].have && r[1].desyncs)) {
        if (g->ck_diff) rk_decide(g, V_NO_CONTEST, 0xFF, 2, "desync: the two games differed at frame %d", (int)g->ck_diff_f);
        else rk_decide(g, V_NO_CONTEST, 0xFF, 2, "desync: the two games went out of sync");
        return;
    }
    if (normal[0] && normal[1]) {
        if (r[0].winner != r[1].winner) { rk_decide(g, V_NO_CONTEST, 0xFF, 3, "the two games saw different winners"); return; }
        if (r[0].agree_f && r[0].agree_f == r[1].agree_f && r[0].agree_ck != r[1].agree_ck) { rk_decide(g, V_NO_CONTEST, 0xFF, 2, "desync: checksums differ at frame %u", r[0].agree_f); return; }
        rk_decide(g, V_RECORDED, r[0].winner, 1, "%s won", g->name[r[0].winner]);
        return;
    }
    /* silence: a game that stopped playing while the other one still plays loses */
    for (int s = 0; s < 2; s++) {
        int o = 1 - s;
        if (normal[s]) continue;                                /* (it ended the match normally) */
        if (g_now - g->last_pads[s] > 10.0 && g_now - g->last_pads[o] < 3.0) {
            rk_decide(g, V_FORFEIT, o, 4, "%s left the match", g->name[s]);
            return;
        }
    }
    for (int s = 0; s < 2; s++) {                               /* one normal report; the other game left without one */
        int o = 1 - s;
        int gone = !rk_client(g, o) || g_now - g->last_seen_any[o] > 15.0;     /* (a live game sends rollback pings every 100 ms) */
        if (normal[s] && !r[o].have && gone && g_now - r[s].at > 20.0) {
            rk_decide(g, V_RECORDED, r[s].winner, 5, "%s won (%s's game left without a result)", g->name[r[s].winner], g->name[o]);
            return;
        }
    }
    double quiet = g_now - (g->last_pads[0] > g->last_pads[1] ? g->last_pads[0] : g->last_pads[1]);
    double first = r[0].have ? r[0].at : r[1].have ? r[1].at : 0;
    if ((!r[0].have && !r[1].have && quiet > 30.0) || (first && g_now - first > 60.0))
        rk_decide(g, V_VOID, 0xFF, 6, (r[0].have || r[1].have) ? "no matching result from the two games" : "both players left");
}

/* on_rb: a rollback message relayed from c (payload p, n bytes) */
static void rk_rb(Client *c, const uint8_t *p, int n) {
    RkGame *g = rk_of(c); if (!g || g->verdict != V_PENDING) return;
    int s = rk_side(g, c); if (s < 0) return;
    g->last_seen_any[s] = g_now;
    if (n < 22 || p[0] != 1 || (n - 20) % 16 != 2) return;      /* PADS (rb_wire.c) + match tag + mode byte */
    int32_t newest = (int32_t)rd32le(p + 4), cf = (int32_t)rd32le(p + 12); uint32_t ck = rd32le(p + 16);
    if (newest < 1) return;
    g->last_pads[s] = g_now; if (newest > g->newest[s]) g->newest[s] = newest;
    if (!g->started && g->newest[0] >= 1 && g->newest[1] >= 1) { g->started = 1; g->started_at = g_now; say("quick match %016llx: started", (unsigned long long)g->id); }
    if (cf > 0) {
        int k = cf % RK_CK;
        if (g->ckf[s][k] != cf) { g->ckf[s][k] = cf; g->ckv[s][k] = ck;
            int o = 1 - s;
            if (g->ckf[o][k] == cf) { if (g->ckv[o][k] == ck) g->ck_same++; else if (!g->ck_diff) { g->ck_diff = 1; g->ck_diff_f = cf; say("quick match %016llx: DESYNC seen by the lobby at frame %d", (unsigned long long)g->id, (int)cf); } }
        }
    }
}
/* 22 RESULT from c (p: the whole packet) */
static void rk_result(Client *c, const uint8_t *p, int n) {
    if (n < 12 + RK_REPORT || p[12] != 1) return;
    const uint8_t *b = p + 13;
    uint64_t id = rd64(b); b += 8;
    RkGame *g = rk_by_id(id);
    if (!g) {                                               /* (a lobby restart, or long gone) */
        uint8_t o[4 + 8 + 4 + 64], *q = hdr(o, VERDICT); q = w64(q, id); q = w8(q, 1); q = w8(q, V_VOID); q = w8(q, 0xFF); q = w8(q, 7);
        const char *t = "the lobby does not know this match (it restarted?): not recorded"; wpad(q, t, strlen(t), 64); csend(o, sizeof o, c); return;
    }
    int s = rk_side(g, c);
    if (s < 0) return;
    g->last_seen_any[s] = g_now;
    if (!g->rep[s].have) {
        RkReport *r = &g->rep[s];
        r->side = b[0]; r->winner = b[1]; r->end = b[2]; r->stocks[0] = b[3]; r->stocks[1] = b[4];
        r->dmg[0] = b[5] << 8 | b[6]; r->dmg[1] = b[7] << 8 | b[8];
        r->frames = rd32(b + 9); r->fin_ck = rd32(b + 13); r->agree_f = rd32(b + 17); r->agree_ck = rd32(b + 21); r->desyncs = b[25] << 8 | b[26];
        r->chara[0] = rd64(b + 27); r->chara[1] = rd64(b + 35); r->stage = rd64(b + 43); r->flags = b[51];
        if (r->winner > 1) r->winner = 0xFF;
        if (r->end == 3 && g->verdict == V_PENDING) {           /* "the other game went silent": not a result by itself (the silence rule decides) */
            if (!r->said_silent++) say("quick match %016llx: %s's game says the other game is silent (frame %u)", (unsigned long long)g->id, g->name[s], r->frames);
            r->have = 0; r->end = 0;
        } else {
            r->have = 1; r->at = g_now;
            if (r->side != s) say("quick match %016llx: %s reports as side %d, is side %d (taken as %d)", (unsigned long long)g->id, g->name[s], r->side, s, s);
            say("quick match %016llx: result from %s: winner %s, end %d, stocks %d-%d, frames %u, desyncs %d, checks agreed up to %u",
                (unsigned long long)g->id, g->name[s], r->winner <= 1 ? g->name[r->winner] : "none", r->end, r->stocks[0], r->stocks[1], r->frames, r->desyncs, r->agree_f);
        }
        rk_judge(g);
    }
    rk_verdict_send(g, c, s);
}
static void rk_client_gone(Client *c) {                         /* drop(): its game is judged by the silence rules */
    RkGame *g = rk_of(c); if (g) rk_judge(g);
    c->rk_game = 0; c->rk_gid = 0;
}
static void rk_sweep(void) {
    for (int i = 0; i < RK_GAMES; i++) {
        RkGame *g = &g_rk[i]; if (!g->used) continue;
        if (g->verdict == V_PENDING) rk_judge(g);
        else if (g_now - g->decided_at > 300.0) g->used = 0;   /* (late RESULTs get the verdict for 5 minutes) */
    }
}
