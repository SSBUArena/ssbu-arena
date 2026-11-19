/* online_elo.c - (0.2.53) SSBU Arena RANKED ELO PER FIGHTER, the game's side. No game content in this file: switches, our own result
 * message, our own notice, and two hooks that put OUR elo (the database's formula) where the game shows its own rating.
 *
 * PHASE 1 (SSBU_ONLINE_RESULT, default 1; 0 = as 0.2.52: nothing is sent, nothing is shown)
 *   The game's own Online match played with our rollback (nnonline.c SSBU_ONLINE_RB, rb_net.c rbn_online_rb) is refereed by the
 *   lobby (arena-server rb_ranked.c, rk_native): the lobby opens the ranked record itself when the two games agree the match, with
 *   the two accounts of their lobby tickets. At the fight's end (rb_rollback.c rbg_fight_off) this file tells the lobby what this
 *   game saw: RESULT (net_relay.c net_ranked_result, 73 bytes as rb_ranked.c reads them; game id "NX" | the agreed match id),
 *   sent every second until the lobby's verdict. Who won: the fighters' stocks (FighterInformation record + 0xd8, the lua_bind
 *   stock_count accessor's field, the same read as debug_mode.c), sampled during the fight (online_elo_sample, every 10th real
 *   frame, read only: nothing of the synchronised state is written); one at 0 = the last stock lost (end 1); both alive at the
 *   time limit = the one with more stocks (end 2), a tie = the sudden death that follows decides (the same match id) or "no
 *   result"; both alive before the time limit = the fight did not end normally (end 4, the lobby's silence rules decide). The
 *   checksums are rb_net.c's (the last frame both games' checksums agreed, the last final frame): the lobby compares them with
 *   what it relayed. A game NEVER says an elo: the lobby / the database compute it.
 * SSBU_ONLINE_ELO_TOAST (default 1; 0 = no notice): the verdict's elo ("EL" block of VERDICT) as a notice at the top of the
 *   picture, e.g. "Pikachu   1016 -> 1032  (+16)   Gold" (or "Unranked 3/5", "provisional" until the database's own number is
 *   there, the reason of a no contest / forfeit / a match that is not ranked).
 *
 * PHASE 2 (SSBU_ONLINE_ELO_UI, ON by default since 0.2.54; =0 = the game's GSP exactly as 0.2.53 / 0.2.52) the game's own Global Smash
 * Power numbers show the elo:
 *   - 0x71017d14c0 (rating -> the number shown, per category; the fighter GSP is category 0x15): for 0x15 the rating itself
 *     (1:1), every other category as the game does it;
 *   - 0x71017a55e0 (the rating update: the "if I lose" value at the match set-up and the real one at its end, both posted to the
 *     game server with DataStore 82): the game's Glicko update is run as always, then OUR result replaces the new rating and its
 *     copy (the panel's "after"): Elo with the database's formula against the opponent's rating, K from the rated games the game
 *     server put in the record's second value (rb_nex --elo-records 1: 200 + rated games), score 0 for the set-up's value and
 *     the game's own result of that member at the end (the direction of its own Glicko change: the update runs for every member).
 *   The game server gives every fighter's record from the database's elo at login (rb_nex --elo-records 1), so the panel starts
 *   from the real number; the database's number stays the truth (a match over the pair limit or disputed can differ until the
 *   next login). "Global Smash Power" is renamed "Arena Elo" by the menu's text patch (tools/ui/prepare_menu_classic.py).
 *   Seen in the local tests (elo_test, 19/11): the Online select shows the record's rating itself (also without the hooks: "GSP 1014"),
 *   with the text patch "Elo 1014"; the update hook gives the database's numbers at the set-up and the end (pairs IDENTICAL).
 *   The results panel (0.2.54, elo2_test): the panels with "Arena Elo" are the page the game shows when a player presses A on the
 *   winner screen (its 15 s countdown otherwise goes straight to "Rematch?": the 0.2.53 tests pressed nothing, so it never came
 *   up). The page (0x7101d5aa30) asks 0x71017d14c0 twice per player: before (caller 0x7101d5add4) and after (0x7101d5ae14) and
 *   animates between them with the arrow. "After" is the rating the game's result phase (0x71016acd80 -> 0x71016acff0 ->
 *   0x71016ad190) left after the update hook; when the game's own inactivity check ended the match for a player (a scripted test
 *   fighter: "You have been disconnected due to inactivity"), that phase is skipped and both numbers are the "before" one.
 *   SSBU_ONLINE_ELO_UI_DB (above) shows the database's "after" once the verdict is here. SSBU_ONLINE_ELO_TRACE=1 (test) logs every
 *   call of 0x71017d14c0, =2 also the result phases.
 *   Read off the 13.0.4 binary (elo_re study 19/11, results phases 20/11): addresses and fields only. Never inside the fight. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include <windows.h>
#include "runtime.h"
#include "hostrt.h"
#include "online_elo_panel.h"

uint64_t hook_address(const char *name);              /* fighter_sdk\hook_host.c */
int hook_install(uint64_t addr, void (*repl)(Ctx *, void *), void *user, ImportFn *orig);
void menu_popup(const char *title, const char *text);  /* menu.cpp */
int rbn_online_rb(void); uint32_t rbn_match_id(void); int rbn_local_player(void);
void rbn_result_info(uint32_t *fin_f, uint32_t *fin_ck, uint32_t *agree_f, uint32_t *agree_ck, int *desyncs, uint64_t chara[2], uint64_t *stage);
int online_rb_on(void);                                /* nnonline.c */
/* net_relay.c (net_off.c: nothing): the RESULT body after the cookie (61 bytes), sent every second until a final VERDICT (30 s) */
void net_ranked_result(const uint8_t body[61], uint64_t game_id);

#define OE_TAG 0x4E58000000000000ull                   /* "NX": the lobby's game id of a game's own Online match (rb_ranked.c RK_NATIVE_TAG) */
#define OE_TIME_FRAMES (420 * 60)                     /* the fixed rules' time limit (SSBU_ONLINE_RULES: 7 minutes) */

static int env_on(const char *name, int dflt) { const char *e = getenv(name); return e && *e ? e[0] != '0' : dflt; }
int online_elo_result_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_ONLINE_RESULT", 1); return v; }
static int toast_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_ONLINE_ELO_TOAST", 1); return v; }
int online_elo_ui_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_ONLINE_ELO_UI", 1); return v; }      /* (0.2.54: ON by default, verified on the results page; 0 = as 0.2.53) */

static int oe_read(uint64_t addr, void *buf, size_t n) {
    SIZE_T got = 0;
    if (addr < 0x10000) return 0;
    return ReadProcessMemory(GetCurrentProcess(), (const void *)(uintptr_t)addr, buf, n, &got) && got == n;
}
static uint64_t oe_r64(uint64_t a) { uint64_t v = 0; return oe_read(a, &v, 8) ? v : 0; }
/* the fighter entry's information record (debug_mode.c fighter_info_record: FighterManager -> entry -> information + 8) */
static uint64_t oe_info(int id) {
    static uint64_t inst;
    if (!inst) { uint64_t a = hook_address("_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E"); if (!a) return 0; inst = a; }
    uint64_t mgr = oe_r64(inst), impl = mgr ? oe_r64(mgr) : 0, entry = impl ? oe_r64(impl + 0x20 + 8 * (uint64_t)id) : 0;
    return entry ? oe_r64(entry + 0xf0 + 8) : 0;
}

/* ---------------------------------------------------------------- phase 1: the fight's result */
static int g_oe_stocks[2] = {-1, -1}, g_oe_seen;       /* the last stocks read in this fight */
static uint32_t g_oe_tie_mid;                          /* a time-out tie of this match: the sudden death decides */
static volatile LONG g_oe_last_winner = -1;            /* (phase 2) the winner side of the last fight this game reported, and its match */
static volatile LONG g_oe_last_side = -1; static volatile LONG g_oe_last_mid;
static uint64_t g_oe_chara[2];                         /* the two players' ui_chara as the match block holds them (record + 0) */
void online_elo_sample(long long real_frames) {        /* rb_rollback.c, once per tick of a rollback match (main thread) */
    if (!online_elo_result_on() || !rbn_online_rb() || (real_frames % 10)) return;
    for (int i = 0; i < 2; i++) { uint64_t r = oe_info(i); uint32_t v; if (r && oe_read(r + 0xd8, &v, 4) && v < 100) { g_oe_stocks[i] = (int)v; g_oe_seen = 1; } }
    /* the fighters: the match block's records (0x71052c41b0 + 0x580 + 0x260 i, + 0 the ui_chara; debug_mode.c MI_PLAYER - 8): in the game's
     * own Online mode the handshake carries only player 1's choice, the match block has both (the game's own set-up) */
    for (int i = 0; i < 2; i++) { uint64_t ch = 0; if (oe_read(GA(0x71052c41b0ULL) + 0x580 + 0x260ull * (uint64_t)i, &ch, 8) && (ch >> 56) == 0xc1) g_oe_chara[i] = ch; }
}
void online_elo_fight_end(long long real_frames) {     /* rb_rollback.c rbg_fight_off, before the session forgets the match */
    if (!online_elo_result_on() || !online_rb_on() || !rbn_online_rb()) { g_oe_seen = 0; return; }
    {   int last[2] = {-1, -1};                         /* one more read: the very end */
        for (int i = 0; i < 2; i++) { uint64_t r = oe_info(i); uint32_t v; if (r && oe_read(r + 0xd8, &v, 4) && v < 100) last[i] = (int)v; }
        if (last[0] >= 0 && last[1] >= 0) { g_oe_stocks[0] = last[0]; g_oe_stocks[1] = last[1]; g_oe_seen = 1; } }
    uint32_t mid = rbn_match_id(); int side = rbn_local_player() & 1;
    if (!mid) { g_oe_seen = 0; return; }
    int s0 = g_oe_stocks[0], s1 = g_oe_stocks[1], winner = 0xFF, end = 4;
    if (g_oe_seen && s0 >= 0 && s1 >= 0) {
        if (s0 == 0 && s1 > 0) { winner = 1; end = 1; }
        else if (s1 == 0 && s0 > 0) { winner = 0; end = 1; }
        else if (s0 > 0 && s1 > 0 && real_frames >= OE_TIME_FRAMES - 120) {
            if (s0 != s1) { winner = s0 > s1 ? 0 : 1; end = 2; }
            else if (g_oe_tie_mid != mid) {             /* a tie at the time limit: the sudden death (same match) decides */
                g_oe_tie_mid = mid; g_oe_seen = 0; g_oe_stocks[0] = g_oe_stocks[1] = -1;
                printf("[elo] match %08x: a tie at the time limit (%d stocks each): waiting for the sudden death\n", mid, s0); fflush(stdout);
                return;
            }
        }
    }
    if (g_oe_tie_mid && g_oe_tie_mid != mid) g_oe_tie_mid = 0;
    uint32_t fin_f = 0, fin_ck = 0, ag_f = 0, ag_ck = 0; int desyncs = 0; uint64_t chara[2] = {0, 0}, stage = 0;
    rbn_result_info(&fin_f, &fin_ck, &ag_f, &ag_ck, &desyncs, chara, &stage);
    for (int i = 0; i < 2; i++) if (g_oe_chara[i]) chara[i] = g_oe_chara[i];      /* (what this game's match block holds: both players) */
    uint8_t b[61], *q = b; uint64_t gid = OE_TAG | mid;
    *q++ = 1;
    for (int k = 7; k >= 0; k--) *q++ = (uint8_t)(gid >> (8 * k));
    *q++ = (uint8_t)side; *q++ = (uint8_t)winner; *q++ = (uint8_t)end;
    *q++ = (uint8_t)(s0 < 0 ? 0 : s0); *q++ = (uint8_t)(s1 < 0 ? 0 : s1);
    *q++ = 0; *q++ = 0; *q++ = 0; *q++ = 0;           /* (damage: not read; the referee does not use it) */
    uint32_t w32[4] = {fin_f, fin_ck, ag_f, ag_ck};
    for (int i = 0; i < 4; i++) for (int k = 3; k >= 0; k--) *q++ = (uint8_t)(w32[i] >> (8 * k));
    *q++ = (uint8_t)(desyncs > 65535 ? 0xFF : desyncs >> 8); *q++ = (uint8_t)(desyncs > 65535 ? 0xFF : desyncs);
    uint64_t w64[3] = {chara[0], chara[1], stage};
    for (int i = 0; i < 3; i++) for (int k = 7; k >= 0; k--) *q++ = (uint8_t)(w64[i] >> (8 * k));
    *q++ = 1;                                          /* (flags: checksums on) */
    InterlockedExchange(&g_oe_last_winner, winner == 0xFF ? -1 : winner); InterlockedExchange(&g_oe_last_side, side); InterlockedExchange(&g_oe_last_mid, (LONG)mid);
    printf("[elo] match %08x over after %lld frames: stocks %d / %d -> winner %s (end %d), this game is player %d; final frame %u, checksums agreed up to %u, desyncs %d: RESULT to the lobby\n",
           mid, real_frames, s0, s1, winner == 0 ? "player 1" : winner == 1 ? "player 2" : "none", end, side + 1, fin_f, ag_f, desyncs);
    fflush(stdout);
    net_ranked_result(b, gid);
    g_oe_seen = 0; g_oe_stocks[0] = g_oe_stocks[1] = -1; g_oe_chara[0] = g_oe_chara[1] = 0;
}

/* ---------------------------------------------------------------- phase 1: the notice (net_relay.c's receive thread) */
static const char *const OE_TIER[] = {"Unranked", "Bronze", "Silver", "Gold", "Platinum", "Diamond", "Master", "Elite"};
/* v: verdict (1 recorded, 2 forfeit, 3 no contest, 4 void), text: the lobby's reason; el: the "EL" block (NULL without one) */
void online_elo_verdict(uint64_t gid, int v, int winner, int reason, const char *text, const uint8_t *el, int el_n) {
    if ((gid & 0xFFFF000000000000ull) != OE_TAG) return;
    int side = (int)g_oe_last_side; if (side < 0 || (uint32_t)g_oe_last_mid != (uint32_t)gid) side = rbn_local_player() & 1;
    char t[300]; t[0] = 0;
    if (el && el_n >= 4 + 2 * 34 && el[0] == 'E' && el[1] == 'L') {
        int flags = el[3]; const uint8_t *p = el + 4 + 34 * side;
        int before = p[0] << 8 | p[1], after = p[2] << 8 | p[3], delta = (int)(int16_t)(p[4] << 8 | p[5]), games = p[6] << 8 | p[7], tier = p[8], place = p[9];
        char name[25]; memcpy(name, p + 10, 24); name[24] = 0;
        char tn[32]; if (tier == 0) snprintf(tn, sizeof tn, "Unranked %d/%d", games < place ? games : place, place); else snprintf(tn, sizeof tn, "%s", tier < 8 ? OE_TIER[tier] : "?");
        if (flags & 8) snprintf(t, sizeof t, "%s: elo not changed (%s)", v == 1 || v == 2 ? "Recorded" : "Not recorded", text && *text ? text : "no elo for this match");
        else if (flags & 4) snprintf(t, sizeof t, "%s   %d   (no change: 3 rated matches with this player in 24 h)   %s", name, before, tn);
        else snprintf(t, sizeof t, "%s   %d -> %d   (%+d)   %s%s", name, before, after, delta, tn, flags & 2 ? "" : "   (provisional)");
        printf("[elo] VERDICT %d for match %08x: %s (flags %x)\n", v, (unsigned)gid, t, flags); fflush(stdout);
        if (flags & 2) {                                   /* (0.2.54, phase 2) the database's numbers of both sides, for the results panel */
            void online_elo_db_numbers(uint32_t mid, const int before[2], const int after[2]);
            int b2[2], a2[2];
            for (int s2 = 0; s2 < 2; s2++) { const uint8_t *q = el + 4 + 34 * s2; b2[s2] = q[0] << 8 | q[1]; a2[s2] = flags & (4 | 8) ? b2[s2] : q[2] << 8 | q[3]; }
            online_elo_db_numbers((uint32_t)gid, b2, a2);
        }
    } else if (v == 3 || v == 4) {
        snprintf(t, sizeof t, "%s: %s. Elo unchanged.", reason == 13 ? "Not ranked" : v == 3 ? "No contest" : "Not recorded", text && *text ? text : "-");
        printf("[elo] VERDICT %d for match %08x: %s\n", v, (unsigned)gid, t); fflush(stdout);
    } else {
        printf("[elo] VERDICT %d for match %08x (winner %d, %s): no elo in it (an older lobby, or no rating file there)\n", v, (unsigned)gid, winner, text ? text : ""); fflush(stdout);
        return;
    }
    if (toast_on() && t[0]) menu_popup(v == 2 ? "Ranked (forfeit)" : "Ranked", t);
}

/* ---------------------------------------------------------------- phase 2: the game's own panel shows the elo */
static ImportFn g_oe_orig_conv, g_oe_orig_upd;
static long long g_oe_conv_n, g_oe_upd_n;
/* (0.2.54) SSBU_ONLINE_ELO_UI_DB (default 1; 0 = the game's own update shown as it is): the numbers of the results panel are the
 * DATABASE's once its verdict is here (online_elo_panel.h: why and how; display only, the game's own data never changes). */
static CRITICAL_SECTION g_oe_cs; static volatile LONG g_oe_cs_init;
static OePanel g_oe_pan;
static void oe_lock(void) { if (!InterlockedCompareExchange(&g_oe_cs_init, 1, 0)) { InitializeCriticalSection(&g_oe_cs); InterlockedExchange(&g_oe_cs_init, 2); } while (g_oe_cs_init != 2) Sleep(0); EnterCriticalSection(&g_oe_cs); }
static int oe_ui_db_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_ONLINE_ELO_UI_DB", 1); return v; }
void online_elo_db_numbers(uint32_t mid, const int before[2], const int after[2]) {      /* online_elo_verdict, the database's verdict */
    if (!online_elo_ui_on() || !oe_ui_db_on()) return;
    oe_lock(); int made = oep_db(&g_oe_pan, mid, before, after); LeaveCriticalSection(&g_oe_cs);
    if (made) { printf("[elo] the results panel: %d number(s) of match %08x shown as the database's (SSBU_ONLINE_ELO_UI_DB)\n", made, mid); fflush(stdout); }
}
static uint32_t oe_panel_number(uint32_t r) {           /* the rating the panel asks for -> the number shown */
    if (!oe_ui_db_on()) return r;
    oe_lock(); int v = oep_fixed(&g_oe_pan, (int)r); LeaveCriticalSection(&g_oe_cs);
    if ((uint32_t)v != r) { static int n; if (n++ < 12) { printf("[elo] the results panel: the game's %u shown as the database's %d (SSBU_ONLINE_ELO_UI_DB)\n", r, v); fflush(stdout); } }
    return (uint32_t)v;
}
static void oe_conv_hook(Ctx *c, void *user) {          /* 0x71017d14c0 (table, u16 category, u32 rating) -> u32 shown */
    (void)user;
    {   static int tr = -1, n; if (tr < 0) { const char *e = getenv("SSBU_ONLINE_ELO_TRACE"); tr = e && (e[0] == '1' || e[0] == '2'); }      /* (TEST) every call, the first 60 */
        if (tr && n++ < 60) { printf("[elo] trace: the rating shown: category %u, rating %u, caller %llx\n", (unsigned)(c->x[1] & 0xffff), (unsigned)c->x[2], (unsigned long long)c->x[30]); fflush(stdout); } }
    if ((c->x[1] & 0xffff) == 0x15) {
        uint32_t r = (uint32_t)c->x[2], v = oe_panel_number(r); c->x[0] = v ? v : 1;
        if (g_oe_conv_n++ < 6) { printf("[elo] the game's rating %u shown as it is (Arena Elo, SSBU_ONLINE_ELO_UI)\n", r); fflush(stdout); }
        return;
    }
    g_oe_orig_conv(c);
}
static int oe_k(int games, int elo) { return games < 10 ? 40 : elo >= 1800 ? 16 : 24; }
static int oe_round(double x) { return (int)(x < 0 ? -(long long)(-x + 0.5) : (long long)(x + 0.5)); }
/* 0x71017a55e0 (u32 *new rating, u32 *new deviation, u32 *new third value, u8 *my member slot, std::function score, ...) -> my ui_chara id */
static void oe_upd_hook(Ctx *c, void *user) {
    (void)user;
    uint64_t pr = c->x[0], pd = c->x[1], pt = c->x[2], pme = c->x[3], fn = c->x[4];
    int me = pme ? rd8(pme) : 0xff;
    uint64_t fvt = fn ? oe_r64(oe_r64(fn + 0x20)) : 0;
    g_oe_orig_upd(c);
    uint64_t id = c->x[0];
    if (me == 0xff || !id || (id >> 56) != 0xc1 || !pr || !pt) return;
    uint64_t tab = GA(0x7105312f08ULL), mine = 0, opp = 0;
    for (int i = 0; i < 8; i++) {
        uint64_t e = tab + 0xb0ull * (uint64_t)i; uint8_t slot = rd8(e);
        if (slot == 0xff || !rd64(e + 8)) continue;
        if (slot == me) mine = e; else if (!opp) opp = e;
    }
    if (!mine || !opp) return;
    int old = (int)rd32(mine + 0x60), op = (int)rd32(opp + 0x60), dev = (int)rd32(mine + 0x68), glicko = (int)rd32(pr);
    int games = dev >= 200 ? dev - 200 : 10;
    int provisional = fvt == GA(0x71050823a0ULL), s;
    /* the score: 0 for the set-up's "if I lose" value; at the end the game's own result for THIS member (the update runs once per
     * member: its Glicko change goes up for a win, down for a loss, never 0 with the deviation of 200 and more rb_nex gives) */
    if (provisional) s = 0;
    else s = glicko > old;
    double e = 1.0 / (1.0 + pow(10.0, (op - old) / 400.0));
    int nv = old + oe_round(oe_k(games, old) * (s - e)); if (nv < 100) nv = 100;
    wr32(pr, (uint32_t)nv); wr32(pt, (uint32_t)nv); if (pd) wr32(pd, (uint32_t)(dev >= 200 ? dev + 1 : dev));
    if (!provisional && oe_ui_db_on()) {               /* (SSBU_ONLINE_ELO_UI_DB) this match's end values, per member */
        oe_lock(); int made = oep_game(&g_oe_pan, (uint32_t)g_oe_last_mid, old, nv); LeaveCriticalSection(&g_oe_cs);
        if (made) { printf("[elo] the results panel: %d number(s) shown as the database's (SSBU_ONLINE_ELO_UI_DB)\n", made); fflush(stdout); }
    }
    if (g_oe_upd_n++ < 20) { printf("[elo] the game's rating update (%s): %d vs %d, %d rated games, score %d: %d (its own Glicko %d) (SSBU_ONLINE_ELO_UI)\n",
                                    provisional ? "the set-up's if-I-lose value" : "the match's end", old, op, games, s, nv, glicko); fflush(stdout); }
}
/* (TEST, SSBU_ONLINE_ELO_TRACE=2) the result phases of the Online match's network object (its phase function at + 0x10, run every
 * frame until it sets the next): 0x71016acd80 -> 0x71016acff0 (decides: 0x71016ad190 the rating phase, the panel's before / after
 * numbers, or 0x71016adb80 without it) -> ... Each change of the phase run is logged with what 0x71016acff0 reads. */
static ImportFn g_oe_orig_ph[5];
static const uint64_t k_oe_ph[5] = {0x71016acd80ull, 0x71016acff0ull, 0x71016ad190ull, 0x71016adb80ull, 0x71016adf70ull};
static void oe_phase_hook(Ctx *c, void *user) {
    int k = (int)(intptr_t)user; static int last = -1; static unsigned n;
    uint64_t o = c->x[0];
    if (k != last && n++ < 200) {
        uint64_t a = oe_r64(oe_r64(oe_r64(o + 8) + 8) + 0xa8), b = oe_r64(oe_r64(oe_r64(a + 8) + 8) + 0x80); uint8_t b244 = 0; uint32_t a10 = 0, mode = 0, n74 = 0, mask = 0;
        oe_read(b + 0x244, &b244, 1); oe_read(a + 0x10, &a10, 4); oe_read(o + 0x269c0, &mode, 4); oe_read(o + 0xa000, &mask, 4);
        oe_read(oe_r64(oe_r64(oe_r64(GA(0x71052c3c58ULL)) + 8) + 0x88) + 0x74, &n74, 4);
        uint64_t p8 = oe_r64(o + 0x9ff8), p0 = oe_r64(o + 0x9ff0); int i8 = -1, i0 = -1;
        for (int i = 0; i < 8; i++) { uint64_t e = oe_r64(o + 0xa010 + 8 + 0x78ull * (uint64_t)i); if (e && e == p8) i8 = i; if (e && e == p0) i0 = i; }
        printf("[elo] TRACE result phase %llx (was %llx): mode %u, net %u, +0x244 %u / +0x10 %u, + 0x9ff8 %s (member %d), + 0x9ff0 %s (member %d), members mask %x\n",
               (unsigned long long)k_oe_ph[k], last >= 0 ? (unsigned long long)k_oe_ph[last] : 0ull, mode, n74, b244, a10, p8 ? "set" : "0", i8, p0 ? "set" : "0", i0, mask); fflush(stdout);
    }
    last = k;
    g_oe_orig_ph[k](c);
}
void online_elo_hooks(void) {                           /* debug_mode.c, with the other Online hooks (online_nex_on) */
    {   const char *e = getenv("SSBU_ONLINE_ELO_TRACE"); static int done;
        if (e && e[0] == '2' && !done) { done = 1; for (int k = 0; k < 5; k++) hook_install(GA(k_oe_ph[k]), oe_phase_hook, (void *)(intptr_t)k, &g_oe_orig_ph[k]); printf("[elo] TRACE: the result phases hooked\n"); fflush(stdout); } }
    if (!online_elo_ui_on() || g_oe_orig_conv) return;
    int a = hook_install(GA(0x71017d14c0ULL), oe_conv_hook, NULL, &g_oe_orig_conv);
    int b = hook_install(GA(0x71017a55e0ULL), oe_upd_hook, NULL, &g_oe_orig_upd);
    printf("[elo] phase 2 (SSBU_ONLINE_ELO_UI): hooks on the rating shown 0x71017d14c0 (id %d) and the rating update 0x71017a55e0 (id %d)\n", a, b); fflush(stdout);
}
