/* room_mastery.c - (0.2.58) SSBU Arena mastery points ALSO in Online Rooms (a Local Wireless room through our relay, played with
 * rollback) and in Battle Arena's Squad Strike: the game's side. No game content in this file: switches, our own result message,
 * a few read-only fields of the game's own match state (the same fields online_elo.c / debug_mode.c already read).
 *
 * A game NEVER says a number of points. At the end of a room's rollback match (rb_rollback.c rbg_fight_off) this file tells the
 * lobby what this game saw, exactly as online_elo.c does for a ranked match: RESULT (net_relay.c net_room_result) with the game id
 * "RM" | the agreed match id, the 61 bytes of rb_ranked.c's RESULT body (side, winner, end, stocks, frames, checksums, desyncs,
 * fighter 1 of both sides, stage, flags) and an extension of ours (arena-server rb_roommastery.c reads it):
 *     "RX" 1, kind u8 (1 a 1-on-1 room match, 2 Squad Strike), fighters per side n u8 (1, or the squad's 3 / 5), stock start u8
 *     (the stocks each player started with; Squad Strike: n; 0 = not a stock match), left u8 x 2 (stocks left / squad fighters
 *     left at the end), then the ui_chara u64 of both sides' fighters (n each, in the squad's order)
 * sent every second until the lobby's final VERDICT (or 30 s). The lobby pairs the TWO games' reports: only when both agree (the
 * same winner, stocks, fighters) and the lobby's own checksum evidence does not contradict them, the database gives full points;
 * otherwise base points only (see rb_roommastery.c). The database's answer comes back in VERDICT as an "MP" block version 2 (one
 * entry per fighter that fought): online_mastery.c online_mastery_room shows it (one line per fighter).
 * Only 2-player rollback room matches exist (rb_net.c is 1 on 1): a room of 3..8 players plays its fights with the game's own
 * netcode, which earns nothing (no rollback match id, no report).
 *
 * Switches:
 *   SSBU_ROOM_MASTERY (default 1; 0 = nothing is sent: exactly 0.2.57)
 *   SSBU_ROOM_MASTERY_TRACE=1 (test) the sampled stocks / squad slots in the log every 2 s of the fight, =2 every sample. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>
#include "runtime.h"
#include "hostrt.h"

uint64_t hook_address(const char *name);              /* fighter_sdk\hook_host.c */
int rbn_room_rollback(void); int rbn_online_rb(void); uint32_t rbn_match_id(void); int rbn_local_player(void);
void rbn_result_info(uint32_t *fin_f, uint32_t *fin_ck, uint32_t *agree_f, uint32_t *agree_ck, int *desyncs, uint64_t chara[2], uint64_t *stage);
void net_room_result(const uint8_t *body, int n, uint64_t game_id);      /* net_relay.c (net_off.c: nothing) */

#define RM_TAG 0x524D000000000000ull                   /* "RM": the lobby's game id of a room match (rb_roommastery.c RM_TAG) */
#define RM_SQ_MAX 5
#define RM_MELEE GA(0x71052c41b0ULL)                    /* the match block (debug_mode.c MELEE_INFO): + 0x39 == 3 a squad match */
#define RM_SQ_TABLE GA(0x71052c9dc0ULL)                 /* the squad table (debug_mode.c SQ_TABLE): + 0 count, + 8 entries [2][5] of 0x268 */

static int env_on(const char *name, int dflt) { const char *e = getenv(name); return e && *e ? e[0] != '0' : dflt; }
int room_mastery_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_ROOM_MASTERY", 1); return v; }
static int rm_trace(void) { static int v = -1; if (v < 0) { const char *e = getenv("SSBU_ROOM_MASTERY_TRACE"); v = e && *e ? atoi(e) : 0; } return v; }

static int rm_read(uint64_t addr, void *buf, size_t n) {
    SIZE_T got = 0;
    if (addr < 0x10000) return 0;
    return ReadProcessMemory(GetCurrentProcess(), (const void *)(uintptr_t)addr, buf, n, &got) && got == n;
}
static uint64_t rm_r64(uint64_t a) { uint64_t v = 0; return rm_read(a, &v, 8) ? v : 0; }
static uint8_t rm_r8(uint64_t a) { uint8_t v = 0; return rm_read(a, &v, 1) ? v : 0; }
/* the fighter entry's information record (online_elo.c oe_info: FighterManager -> entry -> information + 8); + 0xd8 its stock count */
static uint64_t rm_info(int id) {
    static uint64_t inst;
    if (!inst) { uint64_t a = hook_address("_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E"); if (!a) return 0; inst = a; }
    uint64_t mgr = rm_r64(inst), impl = mgr ? rm_r64(mgr) : 0, entry = impl ? rm_r64(impl + 0x20 + 8 * (uint64_t)id) : 0;
    return entry ? rm_r64(entry + 0xf0 + 8) : 0;
}
static int rm_stock(int i) { uint64_t r = rm_info(i); uint32_t v; return r && rm_read(r + 0xd8, &v, 4) && v < 100 ? (int)v : -1; }

/* ---- the fight being played (main thread only: rb_rollback.c) */
static struct {
    uint32_t mid; int seen, squad, n, smax[2], s[2];
    uint64_t chara[2][RM_SQ_MAX];                      /* fighter 1 (1-on-1) / the squad's fighters, in order */
    uint64_t cur[2];                                   /* the match block's record of each player now (the fighter in play) */
} g_rm;
static uint32_t g_rm_tie_mid;
static volatile LONG g_rm_side = -1, g_rm_last_mid;     /* this game's side of the last reported match (the verdict's side) */

static void rm_reset(void) { memset(&g_rm, 0, sizeof g_rm); g_rm.s[0] = g_rm.s[1] = -1; }
/* the squad table's fighters (count n) and the match block's records now */
static void rm_read_fighters(void) {
    for (int i = 0; i < 2; i++) { uint64_t ch = rm_r64(RM_MELEE + 0x580 + 0x260ull * (uint64_t)i); if ((ch >> 56) == 0xc1) g_rm.cur[i] = ch; }
    g_rm.squad = rm_r8(RM_MELEE + 0x39) == 3;
    if (g_rm.squad) {
        int n = rm_r8(RM_SQ_TABLE); if (n < 1 || n > RM_SQ_MAX) n = 3;
        g_rm.n = n;
        for (int p = 0; p < 2; p++) for (int k = 0; k < n; k++) {
            uint64_t ch = rm_r64(RM_SQ_TABLE + 8 + 0xc08ull * (uint64_t)p + 0x268ull * (uint64_t)k);
            if ((ch >> 56) == 0xc1 && (ch & 0xFFFFFFFFFFull) != 0) g_rm.chara[p][k] = ch;      /* (an empty slot: c1ffff0000000000) */
        }
    } else {
        g_rm.n = 1;
        for (int i = 0; i < 2; i++) if (g_rm.cur[i]) g_rm.chara[i][0] = g_rm.cur[i];
    }
}
void room_mastery_sample(long long real_frames) {       /* rb_rollback.c, once per tick of a rollback match (main thread): read only */
    if (!room_mastery_on() || !rbn_room_rollback() || rbn_online_rb() || (real_frames % 10)) return;
    uint32_t mid = rbn_match_id();
    if (mid != g_rm.mid) { rm_reset(); g_rm.mid = mid; }
    for (int i = 0; i < 2; i++) { int v = rm_stock(i); if (v >= 0) { g_rm.s[i] = v; if (v > g_rm.smax[i]) g_rm.smax[i] = v; g_rm.seen = 1; } }
    if (real_frames % 60 == 0 || !g_rm.n) rm_read_fighters();
    int tr = rm_trace();
    if (tr >= 2 || (tr == 1 && real_frames % 120 == 0)) {
        printf("[roommastery] TRACE frame %lld: stocks %d / %d (max %d / %d), squad %d n %d, in play %016llx / %016llx\n", real_frames, g_rm.s[0], g_rm.s[1],
               g_rm.smax[0], g_rm.smax[1], g_rm.squad, g_rm.n, (unsigned long long)g_rm.cur[0], (unsigned long long)g_rm.cur[1]);
        fflush(stdout);
    }
}
/* a squad side's fighters left: when the game counts the squad's stocks in the entry (max seen > 1) its stock count; else (one stock
 * per fighter) the squad slot of the fighter in play: slot k in play and alive = n - k left, 0 when its stock is gone */
static int rm_squad_left(int p) {
    int n = g_rm.n, s = g_rm.s[p];
    if (s < 0) return -1;
    if (g_rm.smax[p] > 1) return s > n ? n : s;
    if (s == 0) return 0;
    for (int k = 0; k < n; k++) if (g_rm.chara[p][k] && (g_rm.chara[p][k] & 0xFFFFFFFFFFull) == (g_rm.cur[p] & 0xFFFFFFFFFFull)) return n - k;
    return 1;                                          /* (not found: at least the one in play) */
}
void room_mastery_fight_end(long long real_frames) {    /* rb_rollback.c rbg_fight_off, before the session forgets the match */
    if (!room_mastery_on() || !rbn_room_rollback() || rbn_online_rb()) { rm_reset(); return; }
    uint32_t mid = rbn_match_id(); int side = rbn_local_player() & 1;
    if (!mid) { rm_reset(); return; }
    if (mid != g_rm.mid) { rm_reset(); g_rm.mid = mid; }
    for (int i = 0; i < 2; i++) { int v = rm_stock(i); if (v >= 0) { g_rm.s[i] = v; if (v > g_rm.smax[i]) g_rm.smax[i] = v; g_rm.seen = 1; } }      /* the very end */
    rm_read_fighters();
    int n = g_rm.n > 0 ? g_rm.n : 1, sq = g_rm.squad, left[2], winner = 0xFF, end = 4, ss;
    for (int p = 0; p < 2; p++) left[p] = !g_rm.seen ? -1 : sq ? rm_squad_left(p) : g_rm.s[p];
    ss = sq ? n : (g_rm.smax[0] > g_rm.smax[1] ? g_rm.smax[0] : g_rm.smax[1]);
    if (left[0] >= 0 && left[1] >= 0) {
        if (left[0] == 0 && left[1] > 0) { winner = 1; end = 1; }
        else if (left[1] == 0 && left[0] > 0) { winner = 0; end = 1; }
        else if (left[0] > 0 && left[1] > 0) {         /* both still in: the time limit (a room match ends no other way while both play) */
            if (left[0] != left[1]) { winner = left[0] > left[1] ? 0 : 1; end = 2; }
            else if (g_rm_tie_mid != mid) {             /* a tie at the time limit: the sudden death (same match) decides */
                g_rm_tie_mid = mid;
                printf("[roommastery] match %08x: a tie at the time limit (%d each): waiting for the sudden death\n", mid, left[0]); fflush(stdout);
                int keep = g_rm.n; uint64_t ch[2][RM_SQ_MAX]; memcpy(ch, g_rm.chara, sizeof ch);
                rm_reset(); g_rm.mid = mid; g_rm.n = keep; memcpy(g_rm.chara, ch, sizeof ch);
                return;
            }
        }
    }
    if (g_rm_tie_mid && g_rm_tie_mid != mid) g_rm_tie_mid = 0;
    uint32_t fin_f = 0, fin_ck = 0, ag_f = 0, ag_ck = 0; int desyncs = 0; uint64_t hs[2] = {0, 0}, stage = 0;
    rbn_result_info(&fin_f, &fin_ck, &ag_f, &ag_ck, &desyncs, hs, &stage);
    for (int p = 0; p < 2; p++) if (!g_rm.chara[p][0]) g_rm.chara[p][0] = hs[p];
    uint8_t b[61 + 8 + 2 * RM_SQ_MAX * 8], *q = b; uint64_t gid = RM_TAG | mid;
    *q++ = 1;
    for (int k = 7; k >= 0; k--) *q++ = (uint8_t)(gid >> (8 * k));
    *q++ = (uint8_t)side; *q++ = (uint8_t)winner; *q++ = (uint8_t)end;
    *q++ = (uint8_t)(left[0] < 0 ? 0 : left[0]); *q++ = (uint8_t)(left[1] < 0 ? 0 : left[1]);
    *q++ = 0; *q++ = 0; *q++ = 0; *q++ = 0;           /* (damage: not read) */
    uint32_t w32[4] = {fin_f, fin_ck, ag_f, ag_ck};
    for (int i = 0; i < 4; i++) for (int k = 3; k >= 0; k--) *q++ = (uint8_t)(w32[i] >> (8 * k));
    *q++ = (uint8_t)(desyncs > 65535 ? 0xFF : desyncs >> 8); *q++ = (uint8_t)(desyncs > 65535 ? 0xFF : desyncs);
    uint64_t w64[3] = {g_rm.chara[0][0], g_rm.chara[1][0], stage};
    for (int i = 0; i < 3; i++) for (int k = 7; k >= 0; k--) *q++ = (uint8_t)(w64[i] >> (8 * k));
    *q++ = 1;                                          /* (flags: checksums on) */
    *q++ = 'R'; *q++ = 'X'; *q++ = 1; *q++ = (uint8_t)(sq ? 2 : 1); *q++ = (uint8_t)n; *q++ = (uint8_t)(ss < 0 ? 0 : ss > 99 ? 99 : ss);
    *q++ = (uint8_t)(left[0] < 0 ? 0 : left[0]); *q++ = (uint8_t)(left[1] < 0 ? 0 : left[1]);
    for (int p = 0; p < 2; p++) for (int k = 0; k < n; k++) for (int j = 7; j >= 0; j--) *q++ = (uint8_t)(g_rm.chara[p][k] >> (8 * j));
    InterlockedExchange(&g_rm_side, side); InterlockedExchange(&g_rm_last_mid, (LONG)mid);
    printf("[roommastery] %s match %08x over after %lld frames: %s left %d / %d (of %d) -> winner %s (end %d), this game is player %d; checksums agreed up to %u, desyncs %d: RESULT to the lobby\n",
           sq ? "Squad Strike" : "room", mid, real_frames, sq ? "fighters" : "stocks", left[0], left[1], ss, winner == 0 ? "player 1" : winner == 1 ? "player 2" : "none", end, side + 1, ag_f, desyncs);
    for (int p = 0; p < 2; p++) { printf("[roommastery]   player %d:", p + 1); for (int k = 0; k < n; k++) printf(" %016llx", (unsigned long long)g_rm.chara[p][k]); printf("\n"); }
    fflush(stdout);
    net_room_result(b, (int)(q - b), gid);
    rm_reset();
}
/* online_mastery.c: this game's side of a room verdict (-1: not the last reported match) */
int room_mastery_side_of(uint64_t gid) {
    if ((gid & 0xFFFF000000000000ull) != RM_TAG) return -1;
    return (uint32_t)g_rm_last_mid == (uint32_t)gid ? (int)g_rm_side : rbn_local_player() & 1;
}
