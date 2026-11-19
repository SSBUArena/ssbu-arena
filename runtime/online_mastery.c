/* online_mastery.c - (22/11) SSBU Arena earns OpenSmash MASTERY POINTS: the game's side. No game content in this file: switches, our
 * own small table of the player's points per fighter, and what menu.cpp draws (two cards of ours, never inside the game's own UI).
 *
 * The points are computed by the accounts database only (arena-server tools/net/supabase/20261122_arena_mastery.sql): every match the
 * lobby referees (the Online Matchmaking, the quick matches) earns points with OpenSmash's formula; the results writer passes the
 * database's answer to the lobby, the lobby to the two games in VERDICT as an "MP" block (rb_ranked.c): "MP" 1 1, then both sides
 * (36 bytes): the fighter's total before u32, after u32, this match's points u16, flags u8 (1 first win of the day, 2 pair cap, 4 same
 * network, 8 day cap, 16 base only, 32 left, 64 fighter unknown), 0, fighter key[24]. A game never computes or sends a number.
 *
 *   SSBU_MASTERY_END (default 1; 0 = nothing shown at the match's end): the "MP" block of this game's side as a card at the top of the
 *     picture (under the ranked notice when it is there): "MASTERY  Mario  4,180 -> 4,330  +150" with the number counting up, and
 *     why when it is not the full amount ("First win of the day +50", "Pair limit: no points", ...). The table below is updated.
 *   SSBU_MASTERY_CSS (default 1; 0 = nothing): on the Online character select (the game's own Online scene "OnlineMeleeAnyScene"),
 *     the player's mastery of the fighter in player 1's panel (the select screens' per-player table 0x7105307770, + 0x38 the ui_chara,
 *     debug_mode.c PANEL) as a small card of ours (menu.cpp mastery_draw: where and why).
 *   SSBU_MASTERY_FILE: the launcher's copy of public.my_opensmash_mastery() ("<key> <total>" per line), read at the first use; the
 *     verdicts update it in memory. Without it a fighter shows "-" until a match of this run tells its total.
 * TEST switches (off by default):
 *   SSBU_MASTERY_FAKE=<key>:<before>:<points>[:<flags>]  the table starts with <key> = <before>; at the end of an online rollback fight
 *     (and SSBU_MASTERY_FAKE_AT=<s> seconds after the start) the SAME end card shows before -> before + points (a fake verdict).
 *   SSBU_MASTERY_TRACE=1  the select screen's panel 0 / the scene names in the log. */
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
int mastery_end_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_MASTERY_END", 1); return v; }
int mastery_css_on(void) { static int v = -1; if (v < 0) v = env_on("SSBU_MASTERY_CSS", 1); return v; }
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
            while (fgets(line, sizeof line, fp)) if (line[0] != '#' && sscanf(line, "%63s %lu", k, &v) == 2 && ms_key_ok(k)) { ms_put_locked(k, (uint32_t)v); n++; }
            ReleaseSRWLockExclusive(&g_ms_lock);
            fclose(fp);
        }
        printf("[mastery] %d fighter(s) from the launcher's copy (SSBU_MASTERY_FILE)%s\n", n, fp ? "" : ": not readable"); fflush(stdout);
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
#define MS_CARD_MS 9000
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
void online_mastery_hooks(void) {                      /* debug_mode.c, with the other Online hooks */
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
