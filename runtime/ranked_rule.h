/* ranked_rule.h - (0.2.78, SSBU_RANKED_RULE_FORCE) the ONE official SSBU Arena ranked rule, and the hash of the rule a game played.
 * Pure logic (no game memory, no locks): ranked_rule.c writes the image into the game's rule copies and reads them back for the
 * hash; tests/test_ranked_rule.c runs these functions alone; arena-server rb_ranked.c compares the hash with RR_OFFICIAL_HASH.
 *
 * The rule struct of the game (13.0.4) keeps its settings at + 0x50 .. + 0x87 (0x38 bytes); the save's rule set entries hold the
 * same bytes at + 0x10 .. + 0x47 (save_research/SAVE_FORMAT.txt section 3: the field table, read off the rule loader 0x7103295b30
 * and the store function 0x71017dc930, checked against 12 saves). RR_IMAGE below is that block, one byte per line; the offsets
 * are those of the block (memory + 0x50 + k, save entry + 0x10 + k). "game default" = what the game's own Rulesets editor writes
 * in every set it made in the 12 saves (built-in sets and players' sets alike); a field marked LOW / MEDIUM is one whose name or
 * meaning is inferred (the save research's confidence words). Nothing here is game data: these are SSBU Arena's chosen values.
 *
 * The hash (RrPlayed, rr_hash): 32-bit FNV-1a over a version byte, the three rule copies' settings blocks as the game holds them
 * at the match start, and the rule header of the match block the game built (+ 5 rule kind, + 0x48 time limit in frames, + 0x4c
 * stocks, + 0x54 item frequency) and the hazards switch (0x7105309985). Two games that played the official rule give
 * RR_OFFICIAL_HASH; any other value (a field forced differently, SSBU_RANKED_RULE_FORCE=0, another image) gives another hash. */
#ifndef RANKED_RULE_H
#define RANKED_RULE_H
#include <stdint.h>
#include <stdio.h>
#include <string.h>

#define RR_OFF 0x50                 /* the settings block inside the game's rule struct */
#define RR_LEN 0x38
#define RR_VERSION 1                /* the hash's layout (bump it when RrPlayed or the image changes) */

/* field offsets inside the block (memory + 0x50 + k = save entry + 0x10 + k) */
enum {
    RR_MODE = 0x00, RR_STYLE = 0x01, RR_SQUAD = 0x02, RR_U13 = 0x03, RR_TIME = 0x04 /* u16 LE */, RR_STOCK = 0x06, RR_HP = 0x07,
    RR_WINS = 0x08, RR_U19 = 0x09, RR_CPU = 0x0a, RR_U1B = 0x0b, RR_STAGESEL = 0x0c, RR_TEAMATK = 0x0d, RR_LAUNCH = 0x0e,
    RR_FSMETER = 0x0f, RR_U20 = 0x10, RR_HAZARDS = 0x11, RR_U22 = 0x12, RR_MORPH = 0x13, RR_U24 = 0x14, RR_PAUSE = 0x15,
    RR_U26 = 0x16, RR_U27 = 0x17, RR_ITEMFREQ = 0x18, RR_PAD = 0x19 /* 3 bytes */, RR_ITEMLIST = 0x1c /* 28 bytes, to the end */
};
#define RR_ITEM_BYTES 12            /* the item bits of the list: bits 0 .. 95 (see RR_IMAGE) */

/* THE OFFICIAL SSBU ARENA RANKED RULE (to be confirmed by the owner before release) */
static const uint8_t RR_IMAGE[RR_LEN] = {
    [RR_MODE]     = 0,              /* + 0x10 mode: 0 melee (a free-for-all, here 1 on 1); 1 team, 2 tournament (HIGH) */
    [RR_STYLE]    = 1,              /* + 0x11 style: 1 stock (0 time, 2 stamina) (HIGH) */
    [RR_SQUAD]    = 0,              /* + 0x12 Squad Strike style: not used by Smash (MEDIUM) */
    [RR_U13]      = 0,              /* + 0x13 not understood: garbage in the game's own sets, never read as far as known (LOW) */
    [RR_TIME]     = 420 & 0xff,     /* + 0x14 u16 time limit in seconds: 420 = 7:00 (HIGH) */
    [RR_TIME + 1] = 420 >> 8,
    [RR_STOCK]    = 3,              /* + 0x16 stocks: 3 (HIGH) */
    [RR_HP]       = 5,              /* + 0x17 stamina HP index: 5 = 150 HP, the game default (only used by stamina) (HIGH) */
    [RR_WINS]     = 0,              /* + 0x18 "win_1 .. win_5": 0 = win_1, one game; game default (MEDIUM / LOW) */
    [RR_U19]      = 0,              /* + 0x19 a bool: game default 0 (LOW: one of the "more rules" switches) */
    [RR_CPU]      = 2,              /* + 0x1a CPU level index: 2 = level 3, game default (no CPU in a ranked match) (HIGH) */
    [RR_U1B]      = 0,              /* + 0x1b an enum 0..2: game default 0 (LOW) */
    [RR_STAGESEL] = 0,              /* + 0x1c stage selection: 0, game default (Arena picks the stage itself) (MEDIUM) */
    [RR_TEAMATK]  = 1,              /* + 0x1d team attack: ON, the competitive standard for doubles; no effect in 1 on 1 (HIGH) */
    [RR_LAUNCH]   = 10,             /* + 0x1e launch rate x10: 10 = 1.0x (HIGH) */
    [RR_FSMETER]  = 0,              /* + 0x1f Final Smash meter: off (HIGH) */
    [RR_U20]      = 0,              /* + 0x20 an enum 0..1: game default 0 (LOW) */
    [RR_HAZARDS]  = 0,              /* + 0x21 stage hazards: off (HIGH) */
    [RR_U22]      = 1,              /* + 0x22 not set by the loader: game default 1 (LOW) */
    [RR_MORPH]    = 0,              /* + 0x23 stage morph: 0 = off (MEDIUM) */
    [RR_U24]      = 0,              /* + 0x24 an enum 0..10: 0 in every set the game's editor made (an empty slot holds 3) (LOW) */
    [RR_PAUSE]    = 0,              /* + 0x25 pause: off (HIGH) */
    [RR_U26]      = 0,              /* + 0x26 a bool: game default 0 (LOW) */
    [RR_U27]      = 1,              /* + 0x27 a bool: game default 1 (LOW) */
    [RR_ITEMFREQ] = 0,              /* + 0x28 item frequency / switch: 0 = no items (MEDIUM) */
    /* + 0x29 .. + 0x2b padding: 0 */
    /* + 0x2c .. + 0x47 the 224-bit list: bits 0 .. 95 (bytes 0 .. 11) all OFF = no item can appear (the items are the first ~82
     * bits: players' "no items" sets clear bytes 0 .. 9 and two bits of 10). Bits 96 .. 223 are left as the game's default
     * (all on but one bit, 0x7f at byte 26), the pattern of the game's own sets with items off ("comp" sets of the samples): they
     * are probably not items (MEDIUM: likely the stage list), so they are not cleared. */
    [RR_ITEMLIST + 12] = 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x7f, 0xff,
};

/* the match block values the official rule gives (debug_mode.c SSBU_ONLINE_RULES writes the same ones in the game's Online match) */
#define RR_MB_KIND   1u                 /* match block + 5: rule kind 1 (stock) */
#define RR_MB_TIME   (420u * 60u)       /* + 0x48: the time limit in frames */
#define RR_MB_STOCK  3u                 /* + 0x4c */
#define RR_MB_ITEMS  0u                 /* + 0x54: item frequency, none */
#define RR_HAZ       0u                 /* 0x7105309985: the hazards switch */

typedef struct {
    uint8_t copy[3][RR_LEN];            /* the three rule copies' + 0x50 .. + 0x87, as read back */
    uint8_t mb_kind; uint32_t mb_time, mb_stock, mb_items; uint8_t hazards;
} RrPlayed;

static inline uint32_t rr_fnv(uint32_t h, const void *p, size_t n) {
    const uint8_t *b = (const uint8_t *)p;
    for (size_t i = 0; i < n; i++) { h ^= b[i]; h *= 0x01000193u; }
    return h;
}
static inline uint32_t rr_fnv_u32(uint32_t h, uint32_t v) { uint8_t b[4] = {(uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16), (uint8_t)(v >> 24)}; return rr_fnv(h, b, 4); }
static inline uint32_t rr_hash(const RrPlayed *p) {
    uint32_t h = 0x811c9dc5u; uint8_t v = RR_VERSION;
    h = rr_fnv(h, &v, 1);
    for (int i = 0; i < 3; i++) h = rr_fnv(h, p->copy[i], RR_LEN);
    h = rr_fnv(h, &p->mb_kind, 1);
    h = rr_fnv_u32(h, p->mb_time); h = rr_fnv_u32(h, p->mb_stock); h = rr_fnv_u32(h, p->mb_items);
    h = rr_fnv(h, &p->hazards, 1);
    return h;
}
/* the image with the time / rule kind a test asks for (competitive.c SSBU_QUICK_RULE_KIND / _TIME; < 0 = the official one) */
static inline void rr_image(uint8_t out[RR_LEN], int kind, int time_s) {
    memcpy(out, RR_IMAGE, RR_LEN);
    if (kind >= 0) out[RR_STYLE] = (uint8_t)kind;
    if (time_s >= 0) { out[RR_TIME] = (uint8_t)time_s; out[RR_TIME + 1] = (uint8_t)(time_s >> 8); }
}
static inline void rr_official(RrPlayed *p) {
    for (int i = 0; i < 3; i++) memcpy(p->copy[i], RR_IMAGE, RR_LEN);
    p->mb_kind = RR_MB_KIND; p->mb_time = RR_MB_TIME; p->mb_stock = RR_MB_STOCK; p->mb_items = RR_MB_ITEMS; p->hazards = RR_HAZ;
}
static inline uint32_t rr_official_hash(void) { RrPlayed p; memset(&p, 0, sizeof p); rr_official(&p); return rr_hash(&p); }
/* the official hash, pinned (tests/test_ranked_rule.c checks rr_official_hash() == this; arena-server lobby.conf ranked_rule_hash) */
#define RR_OFFICIAL_HASH 0xedc75c77u    /* (rr_official_hash() of RR_IMAGE, RR_VERSION 1) */

/* the main values of a block, for the log line */
static inline int rr_items_off(const uint8_t b[RR_LEN]) { for (int k = 0; k < RR_ITEM_BYTES; k++) if (b[RR_ITEMLIST + k]) return 0; return 1; }
static inline void rr_describe(const uint8_t b[RR_LEN], char *out, size_t n) {
    snprintf(out, n, "style %u, %u:%02u, %u stocks, launch %u.%ux, FS meter %u, hazards %u, morph %u, pause %u, team attack %u, items %u (%s)",
             b[RR_STYLE], (unsigned)(b[RR_TIME] | b[RR_TIME + 1] << 8) / 60, (unsigned)(b[RR_TIME] | b[RR_TIME + 1] << 8) % 60, b[RR_STOCK],
             b[RR_LAUNCH] / 10u, b[RR_LAUNCH] % 10u, b[RR_FSMETER], b[RR_HAZARDS], b[RR_MORPH], b[RR_PAUSE], b[RR_TEAMATK], b[RR_ITEMFREQ],
             rr_items_off(b) ? "list off" : "LIST ON");
}
/* the first offset where a block differs from the image (-1: none), for the log */
static inline int rr_first_diff(const uint8_t a[RR_LEN], const uint8_t b[RR_LEN]) { for (int k = 0; k < RR_LEN; k++) if (a[k] != b[k]) return k; return -1; }
#endif
