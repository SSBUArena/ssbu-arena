/* ranked_rule.c - (0.2.78) SSBU_RANKED_RULE_FORCE (default 1; 0 = as 0.2.77): every ranked match of SSBU Arena plays ONE fixed rule,
 * whatever the rule sets of the two players' saves, and each game tells the lobby a hash of the rule it really played.
 * Contains nothing of the game: the image (ranked_rule.h) is SSBU Arena's choice of values; the addresses are the rule copies and
 * the match block the other files use already (competitive.c k_rules, debug_mode.c MELEE_INFO).
 *
 * Why (save_research/SAVE_FORMAT.txt 7.1): up to 0.2.77 the runtime forced only part of the rule (style, time, stocks, item
 * frequency, hazards, stage, FS meter, morph, pause); launch rate, the item list, team attack, the stage selection mode and the
 * fields not understood yet came from a save: in the game's own Online mode from the host's Quickplay preferred rules, in the
 * classic matchmaking (the fallback when the game server does not answer) from each player's OWN last rule set, so the two games
 * could even simulate different fights.
 *
 * What: while a ranked match is on (the Arena menu's Online Ranked tile: competitive.c comp_quick_begin for the classic path, the
 * game's own Online mode for the native one, and its match set-up debug_mode.c SSBU_ONLINE_RULES), the whole settings block
 * (+ 0x50 .. + 0x87) of the three rule copies holds RR_IMAGE: written at once, kept every 2 ms by competitive.c's thread (a copy
 * that differs is written again), once more right before the battle builder (comp_battle). + 0x40 .. + 0x4f are never touched
 * (+ 0x4b: the stage-select byte the Arena flow sets). The player's own blocks are saved at the first write and put back at the
 * main menu. Rooms and local / friendly fights are NOT forced (only the FS meter / item switch / hazards / morph / pause clears of
 * competitive.c apply there, as before). Both games of a match write the same image, so both simulate the same rule; a 0.2.77 and
 * a 0.2.78 game never meet (another game code: the lobby pairs only equal codes).
 * The Quickplay preferred rules (save 0x3c5e20, memory 0x7105305970) are NOT written: their bytes are not mapped yet (LOW) and they
 * only make the gathering's search key; rb_nex pairs two Arena games whatever the key, and the rule played is the image anyway.
 *
 * The hash: at the rollback match's start (rb_rollback.c, the base state, read only) ranked_rule_match_start reads the three copies
 * and the match block's rule header back, hashes them (ranked_rule.h rr_hash) and logs one line ("[ranked rule] match start: ...");
 * online_elo.c appends it to the RESULT it sends the lobby ("RH" block); arena-server rb_ranked.c compares it with the official one
 * and the other game's (ranked_rule_check = off | log | void). */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <windows.h>
#include "runtime.h"
#include "hostrt.h"
#include "ranked_rule.h"

static uint64_t rr_copy(int i) { return i == 0 ? GA(0x710530454cull) : i == 1 ? GA(0x7105309924ull) : GA(0x710530f49cull); }      /* (competitive.c k_rules) */
#define RR_MB  GA(0x71052c41b0ull)          /* the match block (debug_mode.c MELEE_INFO) */
#define RR_HAZ_SWITCH GA(0x7105309985ull)   /* the hazards switch (debug_mode.c, competitive.c) */
#define RR_SEQ GA(0x71053040f0ull)          /* the current sequence (competitive.c CURRENT_SEQUENCE) */

int ranked_rule_force_on(void) { static int v = -1; if (v < 0) { const char *e = getenv("SSBU_RANKED_RULE_FORCE"); v = !(e && e[0] == '0'); } return v; }

static volatile LONG g_rr_active;           /* 1 = a ranked match flow: the image is kept on the copies */
static volatile LONG g_rr_classic;          /* 1 = the classic matchmaking path (competitive.c g_quick) */
static uint8_t g_rr_img[RR_LEN];            /* the image written (RR_IMAGE, or with a test's kind / time) */
static uint8_t g_rr_saved[3][RR_LEN]; static volatile LONG g_rr_have;      /* the player's own blocks */
static volatile LONG g_rr_writes;           /* copies written again by the keeper (for the log) */
static SRWLOCK g_rr_lock = SRWLOCK_INIT;

int ranked_rule_active(void) { return g_rr_active != 0; }
static void rr_write_all(void) {
    for (int i = 0; i < 3; i++) {
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)(rr_copy(i) + RR_OFF);
        for (int k = 0; k < RR_LEN; k++) if (r[k] != g_rr_img[k]) { r[k] = g_rr_img[k]; }
    }
}
/* a ranked match flow starts (or its set-up runs again): the player's own blocks saved the first time, the image written now.
 * kind / time_s: a test's values (competitive.c SSBU_QUICK_RULE_KIND / _TIME), < 0 = the official ones */
void ranked_rule_begin(const char *why, int classic, int kind, int time_s) {
    if (!ranked_rule_force_on()) return;
    AcquireSRWLockExclusive(&g_rr_lock);
    int first = !g_rr_active;
    if (first) {
        rr_image(g_rr_img, kind, time_s);
        if (!g_rr_have) { for (int i = 0; i < 3; i++) memcpy(g_rr_saved[i], (const void *)(uintptr_t)(rr_copy(i) + RR_OFF), RR_LEN); InterlockedExchange(&g_rr_have, 1); }
        InterlockedExchange(&g_rr_classic, classic);
        InterlockedExchange(&g_rr_writes, 0);
        InterlockedExchange(&g_rr_active, 1);
    }
    rr_write_all();
    ReleaseSRWLockExclusive(&g_rr_lock);
    if (first) {
        char own[200], img[200]; rr_describe(g_rr_saved[1], own, sizeof own); rr_describe(g_rr_img, img, sizeof img);
        int d = rr_first_diff(g_rr_saved[1], g_rr_img);
        printf("[ranked rule] %s: the ranked rule on the three rule copies (SSBU_RANKED_RULE_FORCE): %s%s; the player's own (copy 1): %s (first difference at + 0x%x)\n",
               why, img, memcmp(g_rr_img, RR_IMAGE, RR_LEN) ? " (TEST: not the official image)" : "", own, d < 0 ? 0 : RR_OFF + d);
        fflush(stdout);
    }
}
/* the image kept (competitive.c's 2 ms thread, comp_battle): a copy that differs is written again */
void ranked_rule_keep(void) {
    if (!g_rr_active) return;
    AcquireSRWLockExclusive(&g_rr_lock);
    if (g_rr_active)
        for (int i = 0; i < 3; i++)
            if (memcmp((const void *)(uintptr_t)(rr_copy(i) + RR_OFF), g_rr_img, RR_LEN)) {
                memcpy((void *)(uintptr_t)(rr_copy(i) + RR_OFF), g_rr_img, RR_LEN);
                if (InterlockedIncrement(&g_rr_writes) <= 8) { printf("[ranked rule] rule copy %d was changed (by the game or a ruleset): the ranked rule written again\n", i); fflush(stdout); }
            }
    ReleaseSRWLockExclusive(&g_rr_lock);
}
/* the player's own rule back (the main menu) */
void ranked_rule_end(const char *why) {
    if (!InterlockedExchange(&g_rr_active, 0)) return;
    AcquireSRWLockExclusive(&g_rr_lock);
    if (g_rr_have) for (int i = 0; i < 3; i++) memcpy((void *)(uintptr_t)(rr_copy(i) + RR_OFF), g_rr_saved[i], RR_LEN);
    InterlockedExchange(&g_rr_have, 0);
    ReleaseSRWLockExclusive(&g_rr_lock);
    printf("[ranked rule] %s: the player's own rule back on the three rule copies (%ld rewrites while the ranked rule was on)\n", why, (long)g_rr_writes);
    fflush(stdout);
}
/* competitive.c comp_keep, every 2 ms: the flow ends at the main menu (the native Online mode has no other end the runtime sees) */
void ranked_rule_tick(void) {
    if (!g_rr_active) return;
    if ((*(volatile uint32_t *)(uintptr_t)RR_SEQ >> 24) == 0x01) { ranked_rule_end("back at the main menu"); return; }
    ranked_rule_keep();
}

/* ---- the hash of the rule played (rb_rollback.c: the rollback match's base state, main thread, read only) */
static volatile LONG g_rr_hash_have; static volatile LONG g_rr_hash; static volatile LONG g_rr_hash_flags;
void ranked_rule_match_start(void) {
    int comp_quickplay(void), rbn_online_rb(void);
    InterlockedExchange(&g_rr_hash_have, 0);
    if (!comp_quickplay() && !rbn_online_rb()) return;          /* (rooms and local fights: not ranked) */
    RrPlayed p; memset(&p, 0, sizeof p);
    for (int i = 0; i < 3; i++) memcpy(p.copy[i], (const void *)(uintptr_t)(rr_copy(i) + RR_OFF), RR_LEN);
    p.mb_kind = *(volatile uint8_t *)(uintptr_t)(RR_MB + 5);
    p.mb_time = *(volatile uint32_t *)(uintptr_t)(RR_MB + 0x48); p.mb_stock = *(volatile uint32_t *)(uintptr_t)(RR_MB + 0x4c); p.mb_items = *(volatile uint32_t *)(uintptr_t)(RR_MB + 0x54);
    p.hazards = *(volatile uint8_t *)(uintptr_t)RR_HAZ_SWITCH;
    uint32_t h = rr_hash(&p), off = rr_official_hash();
    int forced = g_rr_active != 0, classic = comp_quickplay() != 0;
    InterlockedExchange(&g_rr_hash, (LONG)h); InterlockedExchange(&g_rr_hash_flags, (forced ? 1 : 0) | (classic ? 2 : 0)); InterlockedExchange(&g_rr_hash_have, 1);
    char t[220]; rr_describe(p.copy[1], t, sizeof t);
    int same01 = !memcmp(p.copy[0], p.copy[1], RR_LEN), same12 = !memcmp(p.copy[1], p.copy[2], RR_LEN), d = rr_first_diff(p.copy[1], RR_IMAGE);
    printf("[ranked rule] match start (%s): rule hash %08x, the official %08x: %s; ranked rule %s; copy 1: %s%s; match block: kind %u, time %u frames, %u stocks, items %u; hazards switch %u\n",
           classic ? "classic matchmaking" : "the game's Online match", h, off, h == off ? "the official rule" : "NOT the official rule",
           forced ? "forced (SSBU_RANKED_RULE_FORCE)" : "NOT forced", t, same01 && same12 ? "" : " (the three copies differ)",
           p.mb_kind, p.mb_time, p.mb_stock, p.mb_items, p.hazards);
    if (h != off && d >= 0) printf("[ranked rule] match start: copy 1 differs from the official rule first at + 0x%x (%u, official %u)\n", RR_OFF + d, p.copy[1][d], RR_IMAGE[d]);
    fflush(stdout);
}
/* online_elo.c: the hash of this match's start for its RESULT; 0 = none (not taken) */
int ranked_rule_last(uint32_t *hash, uint8_t *flags) {
    if (!g_rr_hash_have) return 0;
    *hash = (uint32_t)g_rr_hash; *flags = (uint8_t)g_rr_hash_flags;
    return 1;
}
