/* online_mastery.c - (22/11) SSBU Arena earns OpenSmash MASTERY POINTS: the game's side. No game content in this file: switches, our
 * own small table of the player's points per fighter, the game's own Online select panel fed with our numbers, and a small notice.
 *
 * The points are computed by the accounts database only (arena-server tools/net/supabase/20261122_arena_mastery.sql): every match the
 * lobby referees (the Online Matchmaking, the quick matches) earns points with OpenSmash's formula; the results writer passes the
 * database's answer to the lobby, the lobby to the two games in VERDICT as an "MP" block (rb_ranked.c): "MP" 1 1, then both sides
 * (36 bytes): the fighter's total before u32, after u32, this match's points u16, flags u8 (1 first win of the day, 2 pair cap, 4 same
 * network, 8 day cap, 16 base only, 32 left, 64 fighter unknown), 0, fighter key[24]. A game never computes or sends a number.
 *
 *   SSBU_MASTERY_PANEL (default 1; 0 = the panel as 0.2.55): the game's own GSP panel of the Online character select (three rows, read
 *     off 0x7101a27570: layout panes set_parts_score_0 / _1 / _2, the number setter 0x7101a1c070 (pane, {shown u32, -, -, kind u32,
 *     value u32, -, ui_chara u64}: a number when shown && kind 1, else "---"), the row's fighter icon 0x7101a1bc20 (owner, pane, 0,
 *     &{ui_chara, 0})):
 *       row 1 "Elo"             the account's rating (the game's own number: rb_nex's record); its icon = the account's TIER EMBLEM
 *                               (SSBU_RANK_EMBLEM, below) instead of the fighter under the cursor;
 *       row 2 "No.1" -> "Peak"  the account's highest rating (the launcher's copy, "peak <n>", raised by every verdict; the current
 *                               rating when not known); its icon = the peak's tier emblem;
 *       row 3 "Roster" -> "Mastery Points"  the mastery points of the fighter under the cursor: set again each time the game sets
 *                               row 1 (0x7101a19990 -> 0x7101a1c070 from 0x7101a19c48, x20 its layout, x19 its icon owner), on a
 *                               fresh handle of the pane "set_parts_score_2" of that same layout (0x7103776090, released as the game
 *                               does). No icon: the roster row shows none, also when the game's own builder call gets a fighter
 *                               (seen 22/11; it would need a layout edit). Only in an Online sequence (0x07......).
 *   SSBU_PANEL_GROUPING / SSBU_ONLINE_ELO_DELTA / SSBU_ONLINE_ELO_SHIFT: the panels' numbers "1,186 (+86)" (ms_num_hook).
 *   Everything that hooks the game here is 13.0.4 only (online_mastery_hooks: off on 13.0.2 / 13.0.5, said once in the log).
 *     The labels are the menu's text patch (tools/ui/prepare_menu_classic.py, characters/arena_elo_text).
 *   SSBU_MASTERY_END (default 1 = a small notice at the top left, 5 s, "Mario +170 / Mastery 4,200" and a line why when the points
 *     were reduced; "card" = the 22/11 card at the top centre; 0 = nothing): the "MP" block of this game's side. The table is updated.
 *   SSBU_MASTERY_CSS (default 0; 1 = the 22/11 overlay pill on the Online select, menu.cpp)
 *   SSBU_MASTERY_FILE: the launcher's copy of public.my_opensmash_mastery() ("<key> <total>" per line) and the account's peak rating
 *     ("peak <n>"), read at the first use; the verdicts update it in memory.
 * TEST switches (off by default):
 *   SSBU_MASTERY_FAKE=<key>:<before>:<points>[:<flags>]  the table starts with <key> = <before>; at the end of an online rollback fight
 *     (and SSBU_MASTERY_FAKE_AT=<s> seconds after the start) the SAME notice shows before -> before + points (a fake verdict).
 *   SSBU_MASTERY_TRACE=1  the select screen's panel 0 / the scene names / the panel's rows in the log; =2 also every number setter. */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>
#include "runtime.h"
#include "hostrt.h"
#include "online_mastery.h"

uint64_t hook_address(const char *name);
int hook_install(uint64_t addr, void (*repl)(Ctx *, void *), void *user, ImportFn *orig);
int online_elo_side_of(uint64_t gid);                  /* online_elo.c: this game's side of a verdict's match */

static int env_on(const char *name, int dflt) { const char *e = getenv(name); return e && *e ? e[0] != '0' : dflt; }
int mastery_end_mode(void) {                            /* 0 nothing, 1 the notice (default), 2 the 22/11 card */
    static int v = -1;
    if (v < 0) { const char *e = getenv("SSBU_MASTERY_END"); v = !e || !*e ? 1 : !_stricmp(e, "card") || e[0] == '2' ? 2 : e[0] == '0' ? 0 : 1; }
    return v;
}
int mastery_end_on(void) { return mastery_end_mode() != 0; }
int mastery_css_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_MASTERY_CSS", 0); return v; }
int mastery_panel_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_MASTERY_PANEL", 1); return v; }
static int ms_trace(void) { static int v = -1; if (v < 0) v = env_on("SSBU_MASTERY_TRACE", 0); return v; }

/* ---- the fighters: the mastery key (ui_chara_db name without "ui_chara_", OpenSmash's keys) and the name shown */
static const char *const MS_NAMES[][2] = {
    {"mario", "Mario"}, {"donkey", "Donkey Kong"}, {"link", "Link"}, {"samus", "Samus"}, {"samusd", "Dark Samus"}, {"yoshi", "Yoshi"},
    {"kirby", "Kirby"}, {"fox", "Fox"}, {"pikachu", "Pikachu"}, {"luigi", "Luigi"}, {"ness", "Ness"}, {"captain", "Captain Falcon"},
    {"purin", "Jigglypuff"}, {"peach", "Peach"}, {"daisy", "Daisy"}, {"koopa", "Bowser"}, {"ice_climber", "Ice Climbers"}, {"sheik", "Sheik"},
    {"zelda", "Zelda"}, {"mariod", "Dr. Mario"}, {"pichu", "Pichu"}, {"falco", "Falco"}, {"marth", "Marth"}, {"lucina", "Lucina"},
    {"younglink", "Young Link"}, {"ganon", "Ganondorf"}, {"mewtwo", "Mewtwo"}, {"roy", "Roy"}, {"chrom", "Chrom"}, {"gamewatch", "Mr. Game & Watch"},
    {"metaknight", "Meta Knight"}, {"pit", "Pit"}, {"pitb", "Dark Pit"}, {"szerosuit", "Zero Suit Samus"}, {"wario", "Wario"}, {"snake", "Snake"},
    {"ike", "Ike"}, {"ptrainer", "Pokemon Trainer"}, {"diddy", "Diddy Kong"}, {"lucas", "Lucas"}, {"sonic", "Sonic"}, {"dedede", "King Dedede"},
    {"pikmin", "Olimar"}, {"lucario", "Lucario"}, {"robot", "R.O.B."}, {"toonlink", "Toon Link"}, {"wolf", "Wolf"}, {"murabito", "Villager"},
    {"rockman", "Mega Man"}, {"wiifit", "Wii Fit Trainer"}, {"rosetta", "Rosalina & Luma"}, {"littlemac", "Little Mac"}, {"gekkouga", "Greninja"},
    {"palutena", "Palutena"}, {"pacman", "Pac-Man"}, {"reflet", "Robin"}, {"shulk", "Shulk"}, {"koopajr", "Bowser Jr."}, {"duckhunt", "Duck Hunt"},
    {"ryu", "Ryu"}, {"ken", "Ken"}, {"cloud", "Cloud"}, {"kamui", "Corrin"}, {"bayonetta", "Bayonetta"}, {"inkling", "Inkling"}, {"ridley", "Ridley"},
    {"simon", "Simon"}, {"richter", "Richter"}, {"krool", "King K. Rool"}, {"shizue", "Isabelle"}, {"gaogaen", "Incineroar"},
    {"packun", "Piranha Plant"}, {"jack", "Joker"}, {"brave", "Hero"}, {"buddy", "Banjo & Kazooie"}, {"dolly", "Terry"}, {"master", "Byleth"},
    {"tantan", "Min Min"}, {"pickel", "Steve"}, {"edge", "Sephiroth"}, {"element", "Pyra / Mythra"}, {"demon", "Kazuya"}, {"trail", "Sora"},
    {"miifighter", "Mii Brawler"}, {"miiswordsman", "Mii Swordfighter"}, {"miigunner", "Mii Gunner"}};
#define MS_NF ((int)(sizeof MS_NAMES / sizeof MS_NAMES[0]))
/* a fighter's other ui_chara_db rows -> its key (the select screen's own rows are the keys above) */
static const char *const MS_PARTS[][2] = {{"popo", "ice_climber"}, {"nana", "ice_climber"}, {"pzenigame", "ptrainer"}, {"pfushigisou", "ptrainer"},
    {"plizardon", "ptrainer"}, {"ptrainer_low", "ptrainer"}, {"eflame", "element"}, {"elight", "element"}, {"eflame_first", "element"},
    {"elight_first", "element"}, {"eflame_only", "element"}, {"elight_only", "element"}};
const char *mastery_name(const char *key) {
    for (int i = 0; i < MS_NF; i++) if (!strcmp(MS_NAMES[i][0], key)) return MS_NAMES[i][1];
    return key;
}
static uint64_t ms_hash40(const char *s) {
    uint32_t c = 0xFFFFFFFFu; size_t n = 0;
    for (; s[n]; n++) { c ^= (uint8_t)s[n]; for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (0u - (c & 1))); }
    return ((uint64_t)n << 32) | (uint32_t)~c;
}
static const char *ms_key_of_chara(uint64_t ch) {      /* a ui_chara id (0xc1 << 56 | hash40) -> its key, NULL: none (random, none) */
    static uint64_t h[MS_NF + 16]; static int ready;
    if (!ready) {
        char t[64];
        for (int i = 0; i < MS_NF; i++) { snprintf(t, sizeof t, "ui_chara_%s", MS_NAMES[i][0]); h[i] = ms_hash40(t); }
        for (int i = 0; i < (int)(sizeof MS_PARTS / sizeof MS_PARTS[0]); i++) { snprintf(t, sizeof t, "ui_chara_%s", MS_PARTS[i][0]); h[MS_NF + i] = ms_hash40(t); }
        ready = 1;
    }
    if ((ch >> 56) != 0xc1) return NULL;
    uint64_t w = ch & 0xFFFFFFFFFFull;
    for (int i = 0; i < MS_NF; i++) if (h[i] == w) return MS_NAMES[i][0];
    for (int i = 0; i < (int)(sizeof MS_PARTS / sizeof MS_PARTS[0]); i++) if (h[MS_NF + i] == w) return MS_PARTS[i][1];
    return NULL;
}

/* ---- the player's points per fighter */
typedef struct { char key[24]; uint32_t total; } MsEnt;
static MsEnt g_ms[MS_NF + 8]; static int g_nms;
static SRWLOCK g_ms_lock = SRWLOCK_INIT;
static void ms_put_locked(const char *key, uint32_t total) {
    for (int i = 0; i < g_nms; i++) if (!strcmp(g_ms[i].key, key)) { g_ms[i].total = total; return; }
    if (g_nms < (int)(sizeof g_ms / sizeof g_ms[0])) { snprintf(g_ms[g_nms].key, sizeof g_ms[g_nms].key, "%s", key); g_ms[g_nms].total = total; g_nms++; }
}
static int ms_key_ok(const char *k) {
    if (!*k || strlen(k) > 23) return 0;
    for (const char *p = k; *p; p++) if (!((*p >= 'a' && *p <= 'z') || (*p >= '0' && *p <= '9') || *p == '_')) return 0;
    return 1;
}
static void ms_tier_line(const char *line);
static volatile LONG g_peak = -1;                      /* the account's highest rating: the launcher's copy, raised by the verdicts (-1 unknown) */
static volatile LONG g_ms_file_ok;                      /* the launcher's copy was read: a fighter not in it has 0 points */
static void ms_load(void) {
    static volatile LONG done;
    if (InterlockedExchange(&done, 1)) return;
    const char *f = getenv("SSBU_MASTERY_FILE");
    int n = 0;
    if (f && *f) {
        FILE *fp = fopen(f, "r");
        if (fp) {
            char line[128], k[64]; unsigned long v;
            AcquireSRWLockExclusive(&g_ms_lock);
            while (fgets(line, sizeof line, fp)) {
                if (line[0] == '#' || sscanf(line, "%63s %lu", k, &v) != 2) continue;
                if (!strcmp(k, "peak")) { if (v > 0 && v < 100000) g_peak = (LONG)v; continue; }      /* (the account's highest rating) */
                if (!strcmp(k, "tier") || !strcmp(k, "peaktier")) { ms_tier_line(line); continue; }      /* (the tier and the peak's tier) */
                if (ms_key_ok(k)) { ms_put_locked(k, (uint32_t)v); n++; }
            }
            ReleaseSRWLockExclusive(&g_ms_lock);
            fclose(fp);
        }
        if (fp) InterlockedExchange(&g_ms_file_ok, 1);
        printf("[mastery] %d fighter(s) from the launcher's copy (SSBU_MASTERY_FILE), peak rating %ld%s\n", n, (long)g_peak, fp ? "" : ": not readable"); fflush(stdout);
    }
    const char *fk = getenv("SSBU_MASTERY_FAKE");      /* (TEST) key:before:points[:flags] */
    if (fk && *fk) {
        char k[32] = ""; unsigned long b = 0, p = 0, fl = 0;
        if (sscanf(fk, "%31[a-z0-9_]:%lu:%lu:%lu", k, &b, &p, &fl) >= 3 && ms_key_ok(k)) {
            AcquireSRWLockExclusive(&g_ms_lock); ms_put_locked(k, (uint32_t)b); ReleaseSRWLockExclusive(&g_ms_lock);
            printf("[mastery] TEST (SSBU_MASTERY_FAKE): %s starts at %lu, a fake verdict will add %lu (flags %lu)\n", k, b, p, fl); fflush(stdout);
        }
    }
}
int mastery_total(const char *key, uint32_t *total) {
    ms_load();
    int ok = 0;
    AcquireSRWLockShared(&g_ms_lock);
    for (int i = 0; i < g_nms; i++) if (!strcmp(g_ms[i].key, key)) { *total = g_ms[i].total; ok = 1; break; }
    ReleaseSRWLockShared(&g_ms_lock);
    return ok;
}

/* ---- the end card */
static SRWLOCK g_card_lock = SRWLOCK_INIT;
static MasteryCard g_card; static ULONGLONG g_card_t0; static uint64_t g_card_gid;
static void ms_card(uint64_t gid, const char *key, uint32_t before, uint32_t after, unsigned points, unsigned flags, const char *why) {
    AcquireSRWLockExclusive(&g_ms_lock); if (key[0]) ms_put_locked(key, after); ReleaseSRWLockExclusive(&g_ms_lock);
    printf("[mastery] %s: %s %u -> %u (+%u, flags %u)%s\n", why, key[0] ? key : "?", before, after, points, flags, mastery_end_on() ? "" : " (not shown: SSBU_MASTERY_END=0)"); fflush(stdout);
    if (!mastery_end_on()) return;
    AcquireSRWLockExclusive(&g_card_lock);
    memset(&g_card, 0, sizeof g_card);
    snprintf(g_card.key, sizeof g_card.key, "%s", key); snprintf(g_card.name, sizeof g_card.name, "%s", key[0] ? mastery_name(key) : "Mastery");
    g_card.before = before; g_card.after = after; g_card.points = points; g_card.flags = flags;
    g_card_t0 = GetTickCount64(); g_card_gid = gid;
    ReleaseSRWLockExclusive(&g_card_lock);
}
#define MS_CARD_MS (mastery_end_mode() == 2 ? 9000 : 5000)
/* online_elo.c online_elo_verdict: the database's rating after a match of this account (the peak row) */
void online_mastery_rating(int after) {
    ms_load();
    if (after > 0 && after > (int)g_peak) { InterlockedExchange(&g_peak, after); printf("[mastery] peak rating now %d\n", after); fflush(stdout); }
}
int mastery_card_get(MasteryCard *out) {
    ms_fake_tick();
    AcquireSRWLockShared(&g_card_lock);
    int on = g_card_t0 && GetTickCount64() - g_card_t0 < MS_CARD_MS;
    if (on) { *out = g_card; out->age_ms = (int)(GetTickCount64() - g_card_t0); out->life_ms = MS_CARD_MS; }
    ReleaseSRWLockShared(&g_card_lock);
    return on;
}

/* net_relay.c rk_verdict: a VERDICT body (gid u64 .. text at 12, "EL" at 76 when there, then "MP") */
void online_mastery_verdict(uint64_t gid, const uint8_t *b, int n) {
    int at = n >= 76 + 72 && b[76] == 'E' && b[77] == 'L' ? 76 + 72 : 76;
    if (n >= at + 5 && b[at] == 'M' && b[at + 1] == 'P' && b[at + 2] == 2) { online_mastery_room(gid, b + at, n - at); return; }      /* (0.2.58) a room match */
    if (n < at + MASTERY_MP_LEN || b[at] != 'M' || b[at + 1] != 'P' || b[at + 2] != 1) return;
    static uint64_t shown;
    if (gid == shown) return;                          /* (every RESULT sent gets the same answer) */
    shown = gid;
    ms_load();
    int side = online_elo_side_of(gid) & 1;
    const uint8_t *q = b + at + 4 + 36 * side;
    uint32_t before = (uint32_t)q[0] << 24 | (uint32_t)q[1] << 16 | (uint32_t)q[2] << 8 | q[3];
    uint32_t after = (uint32_t)q[4] << 24 | (uint32_t)q[5] << 16 | (uint32_t)q[6] << 8 | q[7];
    unsigned pts = (unsigned)(q[8] << 8 | q[9]), flags = q[10];
    char key[25]; memcpy(key, q + 12, 24); key[24] = 0; if (!ms_key_ok(key)) key[0] = 0;
    {   const uint8_t *o = b + at + 4 + 36 * (1 - side); char ok2[25]; memcpy(ok2, o + 12, 24); ok2[24] = 0;
        printf("[mastery] VERDICT %08x: this game is player %d; the other player: %s +%u\n", (unsigned)gid, side + 1, ms_key_ok(ok2) ? ok2 : "?", (unsigned)(o[8] << 8 | o[9])); fflush(stdout); }
    ms_card(gid, key, before, after, pts, flags, "the database's points");
}

/* (0.2.58) a room match's points (arena-server rb_roommastery.c, Supabase 20261123_arena_mastery_rooms.sql): VERDICT "RM" | match id with
 * the "MP" block version 2: "MP" 2, applied u8, kind u8 (1 a 1-on-1 room match, 2 Squad Strike), then both sides: flags u8 (as the
 * version 1 flags, + 128 this game sent no result), fighters u8 (0..5), and per fighter that fought the 36 bytes of version 1 (total
 * before u32, after u32, points u16, flags u8 (1 first win of the day with it), 0, key[24]). This game's side: room_mastery.c. One line
 * per fighter on the notice (Squad Strike); a 1-on-1 room match shows the same notice as a matchmaking match. */
void online_mastery_room(uint64_t gid, const uint8_t *mp, int n) {
    int room_mastery_side_of(uint64_t gid);             /* room_mastery.c */
    static uint64_t shown;
    if (n < 5 || mp[0] != 'M' || mp[1] != 'P' || mp[2] != 2 || gid == shown) return;
    int side = room_mastery_side_of(gid); if (side < 0) side = 0;
    int applied = mp[3], kind = mp[4], at = 5;
    const uint8_t *me = NULL; int me_n = 0, me_flags = 0;
    for (int s = 0; s < 2; s++) {
        if (at + 2 > n) return;
        int fl = mp[at], k = mp[at + 1]; if (k > 5 || at + 2 + 36 * k > n) return;
        if (s == side) { me = mp + at + 2; me_n = k; me_flags = fl; }
        else { printf("[mastery] room VERDICT %08x: the other player: %d fighter(s), flags %d\n", (unsigned)gid, k, fl); fflush(stdout); }
        at += 2 + 36 * k;
    }
    shown = gid;
    ms_load();
    if (!applied || !me) { printf("[mastery] room VERDICT %08x: no points applied\n", (unsigned)gid); fflush(stdout); return; }
    printf("[mastery] room VERDICT %08x (%s): this game is player %d, %d fighter(s), side flags %d\n", (unsigned)gid, kind == 2 ? "Squad Strike" : "room", side + 1, me_n, me_flags); fflush(stdout);
    if (me_n == 0) { ms_card(gid, "", 0, 0, 0, (unsigned)me_flags, "the database's points (room match)"); return; }
    MasteryCard lines; memset(&lines, 0, sizeof lines);
    uint32_t sum = 0;
    for (int i = 0; i < me_n; i++) {
        const uint8_t *q = me + 36 * i;
        uint32_t before = (uint32_t)q[0] << 24 | (uint32_t)q[1] << 16 | (uint32_t)q[2] << 8 | q[3];
        uint32_t after = (uint32_t)q[4] << 24 | (uint32_t)q[5] << 16 | (uint32_t)q[6] << 8 | q[7];
        unsigned pts = (unsigned)(q[8] << 8 | q[9]), fl = q[10] | (unsigned)me_flags;
        char key[25]; memcpy(key, q + 12, 24); key[24] = 0; if (!ms_key_ok(key)) key[0] = 0;
        AcquireSRWLockExclusive(&g_ms_lock); if (key[0]) ms_put_locked(key, after); ReleaseSRWLockExclusive(&g_ms_lock);
        snprintf(lines.ln[i].key, sizeof lines.ln[i].key, "%s", key); snprintf(lines.ln[i].name, sizeof lines.ln[i].name, "%s", key[0] ? mastery_name(key) : "?");
        lines.ln[i].after = after; lines.ln[i].points = pts; lines.ln[i].flags = fl; sum += pts;
        printf("[mastery] room VERDICT %08x: %s %u -> %u (+%u, flags %u)\n", (unsigned)gid, key[0] ? key : "?", before, after, pts, fl); fflush(stdout);
        if (i == 0) ms_card(gid, key, before, after, pts, fl, me_n > 1 ? "the database's points (Squad Strike, fighter 1)" : "the database's points (room match)");
    }
    if (me_n > 1) {                                    /* (Squad Strike) the card above + one line per fighter */
        AcquireSRWLockExclusive(&g_card_lock);
        g_card.nl = me_n; memcpy(g_card.ln, lines.ln, sizeof g_card.ln); g_card.points = sum;
        ReleaseSRWLockExclusive(&g_card_lock);
    }
}

/* (TEST) SSBU_MASTERY_FAKE: the same card from a fake verdict */
static volatile LONG g_fake_due;
void online_mastery_fake_end(void) { if (getenv("SSBU_MASTERY_FAKE")) InterlockedExchange(&g_fake_due, 1); }
void ms_fake_tick(void) {
    static ULONGLONG t0; static int at = -2, fired_at;
    if (at == -2) { const char *e = getenv("SSBU_MASTERY_FAKE_AT"); at = e && *e && getenv("SSBU_MASTERY_FAKE") ? atoi(e) : -1; t0 = GetTickCount64(); }
    if (at >= 0 && !fired_at && GetTickCount64() - t0 >= (ULONGLONG)at * 1000) { fired_at = 1; InterlockedExchange(&g_fake_due, 1); }
    if (!InterlockedExchange(&g_fake_due, 0)) return;
    ms_load();
    const char *fk = getenv("SSBU_MASTERY_FAKE"); char k[32] = ""; unsigned long b = 0, p = 0, fl = 0;
    if (!fk || sscanf(fk, "%31[a-z0-9_]:%lu:%lu:%lu", k, &b, &p, &fl) < 3 || !ms_key_ok(k)) return;
    uint32_t cur = (uint32_t)b; mastery_total(k, &cur);
    ms_card(0, k, cur, cur + (uint32_t)p, (unsigned)p, (unsigned)fl, "TEST fake verdict (SSBU_MASTERY_FAKE)");
}

/* ---- the Online character select: which scene runs, which fighter player 1's panel holds */
static ImportFn g_ms_orig_scene;
static volatile LONG g_ms_online_css;                  /* the last scene asked is the game's own Online select */
static void ms_scene_hook(Ctx *c, void *user) {        /* 0x7103726120: the sequence controller's "change to the scene named ..." */
    (void)user;
    char nm[72] = {0}; uint64_t p = c->x[1];
    if (p) { uint32_t n = 0; SIZE_T got; if (ReadProcessMemory(GetCurrentProcess(), (void *)(uintptr_t)(p + 4), &n, 4, &got) && n < 64) ReadProcessMemory(GetCurrentProcess(), (void *)(uintptr_t)(p + 8), nm, n, &got); }
    uint32_t seq = 0; { SIZE_T got; ReadProcessMemory(GetCurrentProcess(), (void *)(uintptr_t)GA(0x71053040f0ULL), &seq, 4, &got); }
    /* the game's own Online select: its character select scene while the current sequence is an Online one (0x07......) */
    int css = (seq >> 24) == 0x07 && !strcmp(nm, "CharaSelectScene");
    InterlockedExchange(&g_ms_online_css, css);
    if (ms_trace()) { printf("[mastery] TRACE scene %s asked, sequence %08x (online select: %s)\n", nm, seq, css ? "yes" : "no"); fflush(stdout); }
    g_ms_orig_scene(c);
}
/* (STUDY, SSBU_MASTERY_TRACE=2) the select panel's number setters (the functions of 0x7101a27570 that read the "_n" / "_none" labels) */
static const uint64_t k_ms_set[5] = {0x7101a1c070ull, 0x7101a1b4a0ull, 0x7101a1ba90ull, 0x7101a1be30ull, 0x7101a1c140ull};
static ImportFn g_ms_orig_set[5];
static void ms_set_trace(Ctx *c, void *user) {
    int k = (int)(intptr_t)user; static int n[5];
    if (n[k]++ < 40) {
        uint32_t w[8] = {0}; SIZE_T got; ReadProcessMemory(GetCurrentProcess(), (void *)(uintptr_t)c->x[1], w, sizeof w, &got);
        printf("[mastery] STUDY setter %llx from %llx: x0 %llx x1 %llx x2 %llx x3 %llx | x1 words %x %x %x %x %x %x %x %x\n", (unsigned long long)k_ms_set[k],
               (unsigned long long)c->x[30], (unsigned long long)c->x[0], (unsigned long long)c->x[1], (unsigned long long)c->x[2], (unsigned long long)c->x[3],
               w[0], w[1], w[2], w[3], w[4], w[5], w[6], w[7]);
        fflush(stdout);
    }
    g_ms_orig_set[k](c);
}
/* ---- SSBU_MASTERY_PANEL: the game's own GSP panel of the Online select shows the peak rating and the mastery points (see the top) */
#define MP_FROM_ROW1_BUILD 0x7101a292d8ull             /* 0x7101a27570 builds the panel: row 2 ("set_parts_score_1", the No.1 row), */
#define MP_FROM_ROW2_BUILD 0x7101a29334ull             /*   row 3 ("set_parts_score_2", the roster row) */
#define MP_FROM_ROW0_CURSOR 0x7101a19c48ull            /* 0x7101a19990: row 1 again for the fighter under the cursor (x20 the panel's
                                                        * layout, x19 the owner of the rows' icons, as its own icon call 0x7101a19cf0) */
static const char MP_PANE_ROW2[] = "set_parts_score_2";      /* (our own copy of the pane name: no read-only address of the game) */
static ImportFn g_mp_orig_set;
static int mp_online(void) { uint32_t seq = rd32(GA(0x71053040f0ULL)); return (seq >> 24) == 0x07; }      /* an Online sequence (0x07......) */
static uint32_t mp_points(uint64_t chara, int *shown) {
    const char *k = ms_key_of_chara(chara); uint32_t t = 0;
    ms_load();
    *shown = k && (mastery_total(k, &t) || g_ms_file_ok);   /* (a fighter not in the launcher's copy: 0; no copy at all: "---") */
    return t;
}
/* a pane handle of the layout (0x7103776090: x0 the layout, x1 the pane's name, x8 the handle {vtable, object}) and its release as the
 * game's own code does it (0x7101a19cf4: the object's + 24 freed by 0x710392e590, + 16 / + 24 back to the empty handle) */
static void mp_handle_free(Ctx *c, uint8_t h[16]) {
    uint64_t o; memcpy(&o, h + 8, 8); memset(h + 8, 0, 8);
    if (!o) return;
    uint64_t in = rd64(o + 24);
    wr64(o + 16, GA(0x710523c9a8ULL)); wr64(o + 24, 0);
    if (in) { Ctx save = *c; c->x[0] = in; call_guest(c, GA(0x710392e590ULL)); *c = save; }
}
/* ---- (22/11) the numbers of the game's own panels with thousands separators (SSBU_PANEL_GROUPING, default 1) and, on the results
 * panel, the change in brackets (SSBU_ONLINE_ELO_DELTA, default 1): "1,186 (+86)". The game writes a number into a text pane through
 * 0x71037ad390 (u16 *buffer, capacity, width, value, flags, ...) -> its length, then 0x71037a1cc0 (pane, buffer, length): the results
 * panel's elo (0x7101d5aa30: its start, callers 0x7101d5b130 / 0x7101d5b1dc; the count-up and the final value, 0x7101d5f0c4 /
 * 0x7101d5f120 / 0x7101d5f1c4 / 0x7101d5f220, two panes each) and
 * the select panel's rows (msbt number tags inside 0x7103779cd0, called by 0x7101a1c070 while mp_set_hook marks this thread). The
 * buffer is written again with our text; nothing else of the game changes. */
static volatile DWORD g_ms_arm_tid;                    /* the thread inside the select panel's number setter (mp_set_hook) */
static ImportFn g_ms_orig_num;
static int ms_grouping_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_PANEL_GROUPING", 1); return v; }
static int ms_delta_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_ONLINE_ELO_DELTA", 1); return v; }
static int ms_group_txt(char *out, size_t n, long long v) {
    char raw[24]; int k = snprintf(raw, sizeof raw, "%lld", v < 0 ? -v : v), o = 0;
    if (v < 0 && o + 1 < (int)n) out[o++] = '-';
    for (int i = 0; i < k && o + 2 < (int)n; i++) { if (i && (k - i) % 3 == 0) out[o++] = ','; out[o++] = raw[i]; }
    out[o] = 0;
    return o;
}
static void ms_num_hook(Ctx *c, void *user) {
    (void)user;
    uint64_t from = c->x[30], buf = c->x[0]; uint32_t cap = (uint32_t)c->x[1]; int v = (int)(int32_t)c->x[3]; int flags = (int)c->x[4];
    int results = from == GA(0x7101d5b130ULL) || from == GA(0x7101d5b1dcULL) || from == GA(0x7101d5f0c4ULL) || from == GA(0x7101d5f120ULL) || from == GA(0x7101d5f1c4ULL) || from == GA(0x7101d5f220ULL), select = g_ms_arm_tid && g_ms_arm_tid == GetCurrentThreadId();
    g_ms_orig_num(c);
    {   static int dumped; const char *e = getenv("SSBU_MASTERY_TRACE");      /* (STUDY) the results pane's first floats (its translate / size) */
        uint64_t pane = from == GA(0x7101d5f0c4ULL) ? c->x[19] : 0;
        if (e && e[0] == '2' && pane && dumped++ < 2) {
            printf("[mastery] STUDY results pane %llx:", (unsigned long long)pane);
            for (int i = 0; i < 48; i++) { float f; uint32_t u = rd32(pane + 4ull * (uint64_t)i); memcpy(&f, &u, 4); printf(" +%x:%g", i * 4, f); }
            printf("\n"); fflush(stdout);
        } }
    {   static int tr = -1, nn; if (tr < 0) { const char *e = getenv("SSBU_MASTERY_TRACE"); tr = e && e[0] == '2'; }      /* (STUDY) every number in 900..1300 */
        if (tr && v >= 900 && v <= 1300 && nn++ < 150) { printf("[mastery] STUDY number %d from %llx (cap %u, flags %d)\n", v, (unsigned long long)from, cap, flags); fflush(stdout); } }
    if (!(results || select) || !buf || cap < 12 || !ms_grouping_on() || (flags & 1)) return;
    if (v < 1000 && !results) return;                 /* (nothing to group) */
    char t[48]; int n = ms_group_txt(t, sizeof t, v);
    if (results && ms_delta_on()) {
        int online_elo_panel_delta(int v, int *delta);  /* online_elo.c: the database's change of the side this value belongs to */
        int d;
        if (online_elo_panel_delta(v, &d) && d) { char g[24]; ms_group_txt(g, sizeof g, d < 0 ? -d : d); n += snprintf(t + n, sizeof t - n, " (%c%s)", d < 0 ? '-' : '+', g); }
    }
    if (results) {
        const char *tt = getenv("SSBU_PANEL_TEST_TEXT");      /* (TEST) a long text in the results panel's number field ('_' = a space) */
        if (tt && *tt) { n = snprintf(t, sizeof t, "%s", tt); for (char *q = t; *q; q++) if (*q == '_') *q = ' '; }
        /* SSBU_ONLINE_ELO_SHIFT (layout units, default 10; 0 = the field as the game has it): the number block to the right and, for a
         * text longer than 9 characters, a little smaller, so that its start stays clear of the up / down arrow and its end inside the
         * card: the text pane (translate x + 0x30, scale + 0x48 / + 0x4c, width + 0x50; right-aligned text) gets
         *     scale s = 9.5 / length (0.7 .. 1),   x = x0 - width / 2 x (1 - s) + shift   (its start: the game's + shift) */
        static int shift = -1000; if (shift == -1000) { const char *e = getenv("SSBU_ONLINE_ELO_SHIFT"); shift = e && *e ? atoi(e) : 10; }
        uint64_t pane = from == GA(0x7101d5b130ULL) ? c->x[23] : from == GA(0x7101d5b1dcULL) ? c->x[21] : c->x[19];
        static struct { uint64_t pane; float x, sx, sy; } seen[16]; static int ns;
        if (shift && pane) {
            int k = -1; for (int i = 0; i < 16; i++) if (seen[i].pane == pane) k = i;
            float x, sx, sy, w; uint32_t u;
            u = rd32(pane + 0x30); memcpy(&x, &u, 4); u = rd32(pane + 0x48); memcpy(&sx, &u, 4); u = rd32(pane + 0x4c); memcpy(&sy, &u, 4); u = rd32(pane + 0x50); memcpy(&w, &u, 4);
            if (k < 0) { k = ns++ & 15; seen[k].pane = pane; seen[k].x = x; seen[k].sx = sx; seen[k].sy = sy; }
            float sc = n > 9 ? 9.5f / (float)n : 1.0f; if (sc < 0.7f) sc = 0.7f;
            float nx = seen[k].x - (w > 0 && w < 2000 ? w * 0.5f * (1.0f - sc) : 0.0f) + (float)shift, nsx = seen[k].sx * sc, nsy = seen[k].sy * sc;
            memcpy(&u, &nx, 4); wr32(pane + 0x30, u); memcpy(&u, &nsx, 4); wr32(pane + 0x48, u); memcpy(&u, &nsy, 4); wr32(pane + 0x4c, u);
        }
    }
    if (n >= (int)cap) return;                        /* (does not fit: the game's own text) */
    for (int i = 0; i <= n; i++) wr16(buf + 2ull * (uint64_t)i, (uint16_t)(uint8_t)t[i]);
    c->x[0] = (uint64_t)n;
    {   static int said; if (ms_trace() && said++ < 30) { printf("[mastery] TRACE number %d (%s, from %llx): \"%s\"\n", v, results ? "results panel" : "select panel", (unsigned long long)from, t); fflush(stdout); } }
}
/* ---- (22/11) SSBU_RANK_EMBLEM (default 1): the account's tier EMBLEM in the Online select panel's icon slots, as a game texture.
 * tools/ui/rank_emblems.py writes one emblem per tier family (0 Unranked .. 6 Master, 7 Grandmaster) over the 128 x 128 icons of eight
 * bosses the Online select never shows (chara_0_<boss>_00.bntx, served by the loose-file layer from characters/arena_rank_emblems);
 * the rows' icon setter 0x7101a1bc20 (owner, part handle, 0, &{ui_chara, u32}) gets that boss's ui_chara: row 1 (its call for the
 * fighter under the cursor, from 0x7101a19cf4) the account's tier, row 2 (the builder's, from 0x7101a292ec) the peak's tier.
 * The tier's NAME is not shown: the rows' "Elo" label is a static text of the layout (no pane the code can name: the parts have only
 * set_txt_match / _score_l / _score_m / _worldscore, the last three kept invisible by the part's own animation), 22/11.
 * The tier: the launcher's copy ("tier <position> <emblem> <name>", "peaktier ..." from my_ranked() and public.ranked_tiers), then each
 * database verdict of this game (the "EL" block: family and label, online_elo.c). */
static const char *const MS_EMB_SLOT[8] = {"koopag", "masterhand", "crazyhand", "ganonboss", "kiila", "darz", "dracula", "galleom"};
static volatile LONG g_tier_emb = -1, g_peak_emb = -1;
static char g_tier_name[40];
static SRWLOCK g_tier_lock = SRWLOCK_INIT;
static int ms_emblem_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_RANK_EMBLEM", 1); return v; }
static uint64_t ms_emb_chara(int emb) {
    if (emb < 0 || emb > 7) return 0;
    char nm[48]; snprintf(nm, sizeof nm, "ui_chara_%s", MS_EMB_SLOT[emb]);
    return 0xc100000000000000ull | ms_hash40(nm);
}
static void ms_tier_line(const char *line) {               /* "tier 8 3 Gold 2" / "peaktier 12 4 Platinum 2" (ms_load) */
    char kw[16]; int pos, emb, off = 0;
    if (sscanf(line, "%15s %d %d %n", kw, &pos, &emb, &off) < 3 || emb < 0 || emb > 7) return;
    if (!strcmp(kw, "tier")) {
        AcquireSRWLockExclusive(&g_tier_lock);
        snprintf(g_tier_name, sizeof g_tier_name, "%s", line + off);
        for (char *p = g_tier_name + strlen(g_tier_name); p > g_tier_name && (p[-1] == '\n' || p[-1] == '\r' || p[-1] == ' '); ) *--p = 0;
        ReleaseSRWLockExclusive(&g_tier_lock);
        InterlockedExchange(&g_tier_emb, emb);
    } else if (!strcmp(kw, "peaktier")) InterlockedExchange(&g_peak_emb, emb);
}
/* online_elo.c online_elo_verdict, the database's verdict of this game's side: the 0.2.54 family (0 Unranked .. 6 Master, Grandmaster
 * = 6 with the label "Grandmaster"), the label ("Placement", "Placed: Gold 2", "Rank up: Gold 3", "Gold 2"), rated games, placement
 * games, the rating after */
void online_mastery_tier(int fam, const char *label, int games, int place, int after) {
    ms_load();
    char name[40]; const char *l = label ? label : "";
    static const char *const pre[3] = {"Placed: ", "Rank up: ", "Rank down: "};
    for (int i = 0; i < 3; i++) if (!strncmp(l, pre[i], strlen(pre[i]))) { l += strlen(pre[i]); break; }
    int emb = !strcmp(l, "Grandmaster") ? 7 : fam < 0 ? 0 : fam > 6 ? 6 : fam;
    if (!strcmp(l, "Placement")) { snprintf(name, sizeof name, "Placement %d/%d", games < place ? games : place, place); emb = 0; }
    else snprintf(name, sizeof name, "%s", l);
    if (!name[0]) return;
    AcquireSRWLockExclusive(&g_tier_lock); snprintf(g_tier_name, sizeof g_tier_name, "%s", name); ReleaseSRWLockExclusive(&g_tier_lock);
    InterlockedExchange(&g_tier_emb, emb);
    if (after > 0 && after >= (int)g_peak) InterlockedExchange(&g_peak_emb, emb);
    printf("[mastery] tier now %s (emblem %d)\n", name, emb); fflush(stdout);
}
static ImportFn g_ms_orig_icon2;
static void ms_icon_hook(Ctx *c, void *user) {             /* 0x7101a1bc20 */
    (void)user;
    uint64_t from = c->x[30];
    if (ms_emblem_on() && c->x[3] && (from == GA(0x7101a19cf4ULL) || from == GA(0x7101a292ecULL)) && mp_online()) {
        ms_load();
        int emb = from == GA(0x7101a19cf4ULL) ? (int)g_tier_emb : (int)g_peak_emb;
        if (from == GA(0x7101a292ecULL) && emb < 0) emb = (int)g_tier_emb;      /* (no peak tier known: the current one) */
        uint64_t ch = ms_emb_chara(emb);
        if (ch) {
            static uint64_t want[2][2];
            int k = from == GA(0x7101a19cf4ULL) ? 0 : 1;
            want[k][0] = ch; want[k][1] = rd32(c->x[3] + 8);
            c->x[3] = (uint64_t)(uintptr_t)want[k];
            {   static int said[2]; if (!said[k]++) { printf("[mastery] the panel's row %d icon: the tier emblem %d (%s's icon slot) (SSBU_RANK_EMBLEM)\n", k + 1, emb, MS_EMB_SLOT[emb]); fflush(stdout); } }
        }
    }
    g_ms_orig_icon2(c);
}
static void mp_set_inner(Ctx *c, void *user);
static void mp_set_hook(Ctx *c, void *user) {          /* 0x7101a1c070 (pane handle, values): this thread marked for the grouping */
    DWORD was = g_ms_arm_tid; g_ms_arm_tid = GetCurrentThreadId();
    mp_set_inner(c, user);
    g_ms_arm_tid = was;
}
static void mp_set_inner(Ctx *c, void *user) {
    (void)user;
    uint64_t from = c->x[30], pane = c->x[0], vals = c->x[1];
    if (!mastery_panel_on() || !pane || !vals || (from != GA(MP_FROM_ROW1_BUILD) && from != GA(MP_FROM_ROW2_BUILD) && from != GA(MP_FROM_ROW0_CURSOR)) || !mp_online()) {
        g_mp_orig_set(c); return;
    }
    if (from == GA(MP_FROM_ROW1_BUILD)) {               /* row 2: the peak rating (else the current one, the record's) */
        ms_load();
        uint32_t cur = rd32(vals + 16), v = g_peak > 0 && (uint32_t)g_peak > cur ? (uint32_t)g_peak : cur;
        if (cur && cur != 0xffffffffu) { wr32(vals, 1); wr32(vals + 12, 1); wr32(vals + 16, v); }      /* (its icon: the game's own No.1 fighter; a "no fighter" icon showed an empty grey box) */
        if (ms_trace()) { printf("[mastery] TRACE panel: peak row %u (the record %u, the launcher's peak %ld)\n", v, cur, (long)g_peak); fflush(stdout); }
        g_mp_orig_set(c); return;
    }
    if (from == GA(MP_FROM_ROW2_BUILD)) {               /* row 3 when the panel is built: player 1's fighter if already chosen */
        int shown; uint64_t ch = rd64(GA(0x7105307770ULL) + 0x38); uint32_t pts = mp_points(ch, &shown);
        if (shown) { wr32(vals, 1); wr32(vals + 12, 1); wr32(vals + 16, pts); }
        if (ms_trace()) { printf("[mastery] TRACE panel: mastery row built, player 1's fighter %016llx: %u%s\n", (unsigned long long)ch, pts, shown ? "" : " (---)"); fflush(stdout); }
        g_mp_orig_set(c); return;
    }
    /* row 1 for the fighter under the cursor: row 3 follows it (its number and its icon) */
    uint64_t ch = rd64(vals + 24), layout = c->x[20], owner = c->x[19];
    g_mp_orig_set(c);
    if ((ch >> 56) != 0xc1 || !layout || !owner) return;
    int shown; uint32_t pts = mp_points(ch, &shown);
    static uint8_t h[16]; static uint32_t v2[8]; static uint64_t icon[2];     /* (the setter reads + 0, + 12, + 16; the icon setter {ui_chara, 0}) */
    v2[0] = (uint32_t)shown; v2[1] = v2[2] = 0xffffffffu; v2[3] = (uint32_t)shown; v2[4] = pts; v2[5] = 0; memcpy(&v2[6], &ch, 8);
    icon[0] = ch; icon[1] = 0;
    Ctx save = *c;
    memset(h, 0, sizeof h);
    c->x[0] = layout; c->x[1] = (uint64_t)(uintptr_t)MP_PANE_ROW2; c->x[8] = (uint64_t)(uintptr_t)h; call_guest(c, GA(0x7103776090ULL));
    *c = save;
    uint64_t obj; memcpy(&obj, h + 8, 8);
    if (obj) {
        c->x[0] = (uint64_t)(uintptr_t)h; c->x[1] = (uint64_t)(uintptr_t)v2; g_mp_orig_set(c);
        *c = save;
        (void)owner; (void)icon;      /* (no icon: the roster row's part shows none, also with the game's own icon call, 22/11) */
        mp_handle_free(c, h);
        *c = save;
    }
    {   static int sw = -1, done; if (sw < 0) { const char *e = getenv("SSBU_SHOT_MASTERY"); sw = e && e[0] == '1'; }      /* (TEST) a picture of the panel */
        if (sw && !done && obj && shown && pts > 0) { done = 1; void menu_shot_later(const char *name, int ms); menu_shot_later("mastery_panel", 1500); } }      /* (a fighter with points) */
    {   /* (STUDY, SSBU_MASTERY_PROBE=<file of "name hexaddr" lines, 13.0.4 strings>) once: which of those panes the rows' parts have */
        static int probed; const char *pf = getenv("SSBU_MASTERY_PROBE");
        if (pf && *pf && !probed) {
            probed = 1;
            static const uint64_t parts[3] = {0x71042b2d67ull, 0x710436acbdull, 0x7104328a08ull};      /* set_parts_score_0 / _1 / _2 */
            for (int pi = 0; pi < 3; pi++) {
                static uint8_t ph[16]; memset(ph, 0, sizeof ph);
                Ctx sv = *c; c->x[0] = layout; c->x[1] = GA(parts[pi]); c->x[8] = (uint64_t)(uintptr_t)ph; call_guest(c, GA(0x7103776090ULL)); *c = sv;
                uint64_t po; memcpy(&po, ph + 8, 8);
                printf("[mastery] PROBE part %d: handle %s\n", pi, po ? "found" : "NOT found"); fflush(stdout);
                if (!po) continue;
                FILE *fp = fopen(pf, "r"); char line[128], nm[96]; unsigned long long ad; int hits = 0;
                while (fp && fgets(line, sizeof line, fp)) {
                    if (sscanf(line, "%95s %llx", nm, &ad) != 2) continue;
                    static uint8_t th[16]; memset(th, 0, sizeof th);
                    c->x[0] = (uint64_t)(uintptr_t)ph; c->x[1] = ad; c->x[8] = (uint64_t)(uintptr_t)th; call_guest(c, GA(0x7103776360ULL)); *c = sv;
                    uint64_t to; memcpy(&to, th + 8, 8);
                    if (to && rd64(to + 16)) {
                        uint64_t pn = rd64(to + 16); uint32_t tx = rd32(pn + 0x30), ty = rd32(pn + 0x34), sw = rd32(pn + 0x50), sh = rd32(pn + 0x54); float f[4]; memcpy(&f[0], &tx, 4); memcpy(&f[1], &ty, 4); memcpy(&f[2], &sw, 4); memcpy(&f[3], &sh, 4);
                        printf("[mastery] PROBE part %d has %s: pane %llx at %g,%g size %gx%g, +0x58 bytes %02x %02x %02x %02x %02x %02x %02x %02x\n", pi, nm, (unsigned long long)pn, f[0], f[1], f[2], f[3],
                               rd8(pn + 0x58), rd8(pn + 0x59), rd8(pn + 0x5a), rd8(pn + 0x5b), rd8(pn + 0x5c), rd8(pn + 0x5d), rd8(pn + 0x5e), rd8(pn + 0x5f)); hits++;
                        const char *vis = getenv("SSBU_MASTERY_PROBE_SHOW");      /* (STUDY) make that pane visible (bit 0 of + 0x58) with its own name as text */
                        if (vis && strstr(nm, vis) && pi == 0) { wr8(pn + 0x58, (uint8_t)(rd8(pn + 0x58) | 1)); static uint16_t w[40]; int k = 0; for (; nm[k] && k < 39; k++) w[k] = (uint8_t)nm[k];
                            c->x[0] = pn; c->x[1] = (uint64_t)(uintptr_t)w; c->x[2] = (uint64_t)k; c->x[3] = 0; call_guest(c, GA(0x71037a1cc0ULL)); *c = sv; }
                    }
                    if (to) mp_handle_free(c, th);
                    *c = sv;
                }
                if (fp) fclose(fp);
                printf("[mastery] PROBE part %d: %d pane(s)\n", pi, hits); fflush(stdout);
                {   /* the part's own pane object: its bytes (name, links) and the children found through the first pointers that look like panes */
                    uint64_t pp = rd64(po + 16);
                    uint8_t raw[0x100]; SIZE_T got = 0; ReadProcessMemory(GetCurrentProcess(), (void *)(uintptr_t)pp, raw, sizeof raw, &got);
                    printf("[mastery] PROBE part %d pane %llx:", pi, (unsigned long long)pp);
                    for (int i = 0; i < 0x100; i += 8) { uint64_t q; memcpy(&q, raw + i, 8); printf(" +%x:%llx", i, (unsigned long long)q); }
                    printf("\n[mastery] PROBE part %d ascii: ", pi);
                    for (int i = 0; i < 0x100; i++) putchar(raw[i] >= 0x20 && raw[i] < 0x7f ? raw[i] : '.');
                    printf("\n"); fflush(stdout);
                }
                mp_handle_free(c, ph); *c = sv;
            }
        } }
    if (ms_trace()) { static uint64_t last; if (ch != last) { last = ch; printf("[mastery] TRACE panel: cursor on %016llx: mastery row %u%s%s\n", (unsigned long long)ch, pts, shown ? "" : " (---)", obj ? "" : " (NO pane)"); fflush(stdout); } }
}
/* (STUDY, SSBU_MASTERY_TRACE=2) the message formatter with numbers 0x7103779cd0 (text, label, count, values...): the labels with "pw" */
static ImportFn g_ms_orig_fmt;
static void ms_fmt_trace(Ctx *c, void *user) {
    (void)user; static int n;
    char lb[64] = {0}; SIZE_T got = 0;
    if (c->x[1]) ReadProcessMemory(GetCurrentProcess(), (void *)(uintptr_t)c->x[1], lb, 63, &got);
    if (n < 300 && strstr(lb, "pw")) { n++; printf("[mastery] STUDY fmt %s from %llx: count %llu, values %lld %lld\n", lb, (unsigned long long)c->x[30], (unsigned long long)c->x[2], (long long)(int32_t)c->x[3], (long long)(int32_t)c->x[4]); fflush(stdout); }
    g_ms_orig_fmt(c);
}
/* (STUDY, SSBU_MASTERY_TRACE=2) the rows' icon setter 0x7101a1bc20 (owner, pane handle, 0, &{ui_chara, u32}); SSBU_MASTERY_ICON_TEST=<name>
 * (TEST) gives row 1's icon the ui_chara_<name> instead of the fighter's */
static ImportFn g_ms_orig_icon;
static void ms_icon_trace(Ctx *c, void *user) {
    (void)user; static int n;
    uint64_t ch = c->x[3] ? rd64(c->x[3]) : 0; uint32_t w2 = c->x[3] ? rd32(c->x[3] + 8) : 0;
    const char *t = getenv("SSBU_MASTERY_ICON_TEST");
    if (t && *t && (c->x[30] == GA(0x7101a19cf4ULL) || c->x[30] == GA(0x7101a29350ULL)) && c->x[3]) {
        static uint64_t want[2]; char nm[64]; snprintf(nm, sizeof nm, "ui_chara_%s", t);
        want[0] = 0xc100000000000000ull | ms_hash40(nm); want[1] = w2;
        c->x[3] = (uint64_t)(uintptr_t)want;
    }
    if (n++ < 60) { printf("[mastery] STUDY icon from %llx: owner %llx, ui_chara %016llx, word %x%s\n", (unsigned long long)c->x[30], (unsigned long long)c->x[0], (unsigned long long)ch, w2, t && *t ? " (ICON TEST)" : ""); fflush(stdout); }
    g_ms_orig_icon(c);
}
void online_mastery_hooks(void) {                      /* debug_mode.c, with the other Online hooks */
    if (g_game_build != GAME_13_0_4) {                 /* (the addresses below are 13.0.4's, checked there only: on 13.0.2 / 13.0.5 nothing of this file is hooked) */
        static int said; if (!said++) { printf("[mastery] game build %d: the panels' mastery / elo additions are 13.0.4 only: off\n", g_game_build); fflush(stdout); }
        return;
    }
    {   const char *e = getenv("SSBU_MASTERY_TRACE"), *t = getenv("SSBU_MASTERY_ICON_TEST"); static int done;
        if (((e && e[0] == '2') || (t && *t)) && !done) { done = 1; hook_install(GA(0x7101a1bc20ULL), ms_icon_trace, NULL, &g_ms_orig_icon); } }
    {   const char *e = getenv("SSBU_MASTERY_TRACE"); static int done;
        if (e && e[0] == '2' && !done) { done = 1; for (int k = 0; k < 5; k++) hook_install(GA(k_ms_set[k]), ms_set_trace, (void *)(intptr_t)k, &g_ms_orig_set[k]);
            hook_install(GA(0x7103779cd0ULL), ms_fmt_trace, NULL, &g_ms_orig_fmt); printf("[mastery] STUDY: the select panel's number setters and the number formatter hooked\n"); fflush(stdout); } }
    if (!g_ms_orig_num && (ms_grouping_on() || ms_delta_on())) {
        int a = hook_install(GA(0x71037ad390ULL), ms_num_hook, NULL, &g_ms_orig_num);
        printf("[mastery] the panels' numbers: thousands separators (SSBU_PANEL_GROUPING), the results panel's change (SSBU_ONLINE_ELO_DELTA) (hook 0x71037ad390 id %d)\n", a); fflush(stdout);
    }
    if (ms_emblem_on() && !g_ms_orig_icon2) {
        int a = hook_install(GA(0x7101a1bc20ULL), ms_icon_hook, NULL, &g_ms_orig_icon2);
        printf("[mastery] the panel's icons: the tier emblems (SSBU_RANK_EMBLEM, hook 0x7101a1bc20 id %d)\n", a); fflush(stdout);
    }
    if (mastery_panel_on() && !g_mp_orig_set) {
        int a = hook_install(GA(0x7101a1c070ULL), mp_set_hook, NULL, &g_mp_orig_set);
        printf("[mastery] the Online select's GSP panel: peak rating and mastery points (SSBU_MASTERY_PANEL, hook 0x7101a1c070 id %d)\n", a); fflush(stdout);
    }
    if (!mastery_css_on() || g_ms_orig_scene) return;
    int a = hook_install(GA(0x7103726120ULL), ms_scene_hook, NULL, &g_ms_orig_scene);
    printf("[mastery] hook on the scene change 0x7103726120 (id %d): the Online select's mastery card (SSBU_MASTERY_CSS)\n", a); fflush(stdout);
}
int mastery_css_get(char *name, int nn, char *key, int nk, uint32_t *total, int *known) {
    if (!mastery_css_on() || !g_ms_online_css || !g_ms_orig_scene) return 0;
    uint64_t panel = GA(0x7105307770ULL), ch = 0;      /* debug_mode.c PANEL(0): + 0x38 the ui_chara of player 1's panel */
    SIZE_T got;
    if (!ReadProcessMemory(GetCurrentProcess(), (void *)(uintptr_t)(panel + 0x38), &ch, 8, &got)) return 0;
    const char *k = ms_key_of_chara(ch);
    if (ms_trace()) { static uint64_t last; if (ch != last) { last = ch; printf("[mastery] TRACE online select: panel 0 ui_chara %016llx -> %s\n", (unsigned long long)ch, k ? k : "(none)"); fflush(stdout); } }
    if (!k) return 0;
    snprintf(name, (size_t)nn, "%s", mastery_name(k)); snprintf(key, (size_t)nk, "%s", k);
    *known = mastery_total(k, total);
    return 1;
}
