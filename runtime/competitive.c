/* competitive.c - the competitive 1v1 mode, code side (SSBU_COMPETITIVE=1; the launcher sets it with "Modalita'
 * competitiva"). The data side (6 stages, no item in the item switch, preset rules) is tools/competitive/make_competitive.py
 * through the loose-file layer. Contains nothing of the game.
 *
 * The Final Smash meter is a rule (ui_rule_db "charge", rule struct + 0x5f; read off the rule loader 0x7103297784).
 * A battle is built by guest 0x71014e58f0 from the current rule, which lives in three globals of 13.0.4
 * (0x710530454c, 0x7105309924, 0x710530f49c: found by the rule struct's shape; writing charge = 1 into them before a
 * battle made the meter appear). The battle takes its copy before 0x71014e58f0 runs (clearing them there was too late),
 * so a thread clears charge and item_switch (+ 0x68) in all three every 2 ms from the start (the rule editor then shows
 * the meter off too), and once more when a battle is built: no FS meter and no random items, whatever ruleset the
 * player picked or the room host sent. Every player of a local-wireless room must use the mode (same rules).
 * Stage hazards and stage morph are rules of the same struct (the loader's field names, hash40): stage_gimmick + 0x61
 * (bool; the byte tests/DEBUG_MODE.md wrote as "hazards") and change_stage + 0x63 (an enum: 0 = off, 1..9 the morph
 * choices); both are kept at 0 the same way: hazards and morph always off.
 * (0.2.41) Pause is the rule can_pause + 0x65 (bool, hash40 0x9a84fe180, the loader's field next to stage_gimmick): kept at 0
 * the same way, so no match can be paused. SSBU_COMP_PAUSE=1: left as the ruleset says. */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <windows.h>
#include "runtime.h"
#include "hostrt.h"
#include "menu.h"
static int g_comp_nopause = -1;
static int comp_nopause(void) { if (g_comp_nopause < 0) { const char *e = getenv("SSBU_COMP_PAUSE"); g_comp_nopause = !(e && e[0] == '1'); } return g_comp_nopause; }

typedef void (*HookFn)(Ctx *c, void *user);
int hook_install(uint64_t addr, HookFn repl, void *user, ImportFn *orig);      /* fighter_sdk\hook_host.c */

static const uint64_t k_rules[] = {0x710530454cull, 0x7105309924ull, 0x710530f49cull};
#define MATCH_ITEM_APPEAR GA(0x71052c4204ull) /* the global match block 0x71052c41b0 + 0x54: item frequency (0 none; tests/DEBUG_MODE.md).
                                                 A saved ruleset keeps its own frequency and item list: a Poke Ball still came out
                                                 with item_switch 0 and the item switch list empty. */
static ImportFn g_battle_orig;
static long g_battles;

/* The stage choice: three entries of 0x20 bytes at 0x71053097c0, the current one's index at 0x7105309968; +0 the ui_stage
 * id (0x69 << 56 | hash40("ui_stage_<name>")) (runtime/debug_mode.c). "Random" (ui_stage_random...) is resolved by the game
 * from ALL its stages, not from the list ui_stage_db shows, and the light archive has only the 6: a choice that is not one
 * of them becomes one of them. The pick depends only on the choice entry itself (both games of a local-wireless room
 * hold the same one), so both sides get the same stage. */
static const char *const k_stages[6] = {"battle_field", "battle_field_s", "end", "poke_stadium2", "animal_village", "animal_city"};
static uint64_t comp_h40(const char *s) {
    uint32_t crc = 0xffffffffu; size_t n = strlen(s);
    for (size_t i = 0; i < n; i++) { crc ^= (uint8_t)s[i]; for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1))); }
    return ((uint64_t)n << 32) | (uint32_t)~crc;
}
static void comp_stage(void) {
    uint32_t cur = *(volatile uint32_t *)(uintptr_t)GA(0x7105309968ull);
    for (uint32_t e = 0; e < 3; e++) {
        volatile uint64_t *id = (volatile uint64_t *)(uintptr_t)(GA(0x71053097c0ull) + 0x20ull * e);
        uint64_t h = *id & 0xffffffffffull;
        if (!h) continue;
        int ok = 0; char nm[48];
        for (int k = 0; k < 6; k++) { snprintf(nm, sizeof nm, "ui_stage_%s", k_stages[k]); if (h == comp_h40(nm)) ok = 1; }
        if (ok) continue;
        uint64_t mix = *id ^ (uint64_t)e * 0x9e3779b97f4a7c15ull ^ *(volatile uint64_t *)(uintptr_t)(GA(0x71053097c0ull) + 0x20ull * e + 0x10);
        mix ^= mix >> 29; mix *= 0xbf58476d1ce4e5b9ull; mix ^= mix >> 32;
        int k = (int)(mix % 6);
        snprintf(nm, sizeof nm, "ui_stage_%s", k_stages[k]);
        *id = 0x6900000000000000ull | comp_h40(nm);
        *(volatile uint32_t *)(uintptr_t)(GA(0x71053097c0ull) + 0x20ull * e + 8) = 0;      /* normal form */
        if (e == cur || cur >= 3) printf("competitive: stage choice %llx is not one of the 6 (random?): %s instead\n", (unsigned long long)h, k_stages[k]);
    }
}

/* The modes outside the competitive 1v1. The current sequence is a global of 13.0.4 (0x71053040f0: SEQUENCE_* ids, the
 * lua constants registered at 0x7101bf1930: 0x01000000 title / menus, 0x02010000 Smash (also local wireless), 0x03.. Spirits,
 * 0x04.. Games & More, 0x05.. Vault, 0x06.. Options, 0x07.. Online). The scene dispatcher 0x710235cbf0 reads it and
 * creates <name>SequenceScene; any other id is put back to the menus there: the game returns to the main menu instead of
 * opening a mode whose data the light archive does not have. */
#define CURRENT_SEQUENCE GA(0x71053040f0ull)
static ImportFn g_scene_orig;
static ImportFn g_menu_orig;
static ImportFn g_pallet_orig;
static ImportFn g_pallet_ready_orig;
static uint8_t *g_local_pending;
/* PalletMenu's native layout controller (13.0.4). Its completed selection is
 * consumed by 0x71032ecc80 and broadcast as PalletCommandMessage. Local Wireless
 * is command 3 (the third sidebar button, with one-based native IDs). */
static uint8_t *comp_pallet(void) {
    uint64_t owner = *(uint64_t *)(uintptr_t)GA(0x710532e7f0ull);
    uint64_t service = owner ? *(uint64_t *)(uintptr_t)(owner+8) : 0;
    uint64_t layout = service ? *(uint64_t *)(uintptr_t)(service+8) : 0;
    uint8_t *p = layout ? *(uint8_t **)(uintptr_t)(layout+128) : NULL;
    if (!p || *(uint64_t *)(p+72) != GA(0x7101af97f0ull) || *(uint64_t *)(p+120) != GA(0x7101af9ee0ull)) return NULL;
    return p;
}
/* The side bar command the Arena menu asks for: the fourth argument of prg_func.open_pallet (tools/ui/menu_actions_classic.py:
 * 3 Local Wireless, 5 Options). Lua 5.3, 64-bit: lua_State.ci at +0x20, CallInfo.func at +0, 16-byte TValues (value, then the
 * type tag; 0x13 an integer); a C function's arguments follow func. Anything else: 3, the earlier preview's only route. */
static int g_pallet_cmd = 3;
static int comp_lua_int_arg(uint64_t L, int n, int fallback) {
    if (!L) return fallback;
    uint64_t ci = *(uint64_t *)(uintptr_t)(L + 0x20);
    uint64_t func = ci ? *(uint64_t *)(uintptr_t)ci : 0, top = *(uint64_t *)(uintptr_t)(L + 0x10);
    uint64_t arg = func + 16ull * (uint64_t)n;
    if (!func || arg >= top || *(int *)(uintptr_t)(arg + 8) != 0x13) return fallback;
    return (int)*(int64_t *)(uintptr_t)arg;
}
static void comp_open_local(Ctx *c, void *user) {
    (void)user;
    int cmd = comp_lua_int_arg(c->x[0], 4, 0);
    /* Preserve the Lua C function's argument/result-stack convention. */
    g_pallet_orig(c);
    uint8_t *p = comp_pallet();
    if (!p || g_local_pending || *(int *)(p+476)) return;
    {   int rbn_ba_mode(void); void rbn_ba_set_mode(int m); void menu_battle_arena(void);      /* (Battle Arena) the Battle Arena tile (func_arg 7): a Local Wireless room that plays Squad Strike */
        if (cmd == 7) { rbn_ba_set_mode(1); menu_battle_arena(); }
        else if (cmd == 3 && rbn_ba_mode() == 1) rbn_ba_set_mode(0); }
    g_pallet_cmd = cmd == 5 ? 5 : 3;
    g_local_pending = p;
    printf("competitive UI: %s -> opening the native side bar command %d\n", g_pallet_cmd == 5 ? "Settings" : cmd == 7 ? "Battle Arena (Squad Strike in a Local Wireless room)" : "Online LDN", g_pallet_cmd); fflush(stdout);
}
static void comp_pallet_ready(Ctx *c, void *user) {
    (void)user;
    uint8_t *p = (uint8_t *)(uintptr_t)c->x[0];
    g_pallet_ready_orig(c);
    if (p != g_local_pending) return;
    int index = *(int *)(p+228);
    if (index < 0 || index >= 20 || *(int *)(p+152+4*index) != 2) return;
    static int trace_count;
    if (++trace_count == 1 || trace_count == 60) { printf("UI pallet ready: tick %d flags %d %d %d %d %d %d queue %llu\n",*(int *)(p+236),p[498],p[499],p[500],p[501],p[502],p[503],(unsigned long long)*(uint64_t *)(p+288));fflush(stdout); }
    /* ready: both flags (the side bar shown and idle), or one of them after about half a second (the side bar moved off
     * the screen by the Arena menu never sets the first one) */
    static int waited;
    if (!(p[499] && p[502]) && !((p[499] || p[502]) && ++waited >= 30)) return;
    waited = 0;
    g_local_pending = NULL;
    *(int *)(p+468) = g_pallet_cmd;
    *(int *)(p+152+4*index) = 4; /* native confirmation, close and publish */
    printf("competitive UI: side bar command %d confirmed natively\n", g_pallet_cmd); fflush(stdout);
}
static void comp_menu(Ctx *c, void *user) {
    (void)user;
    if (getenv("SSBU_UI_TRACE")) {
        uint8_t *obj = (uint8_t *)(uintptr_t)c->x[0];
        int index = *(int *)(obj+396);
        uint64_t mgr = *(uint64_t *)obj;
        int state = index >= 0 && index < 20 ? *(int *)(obj+320+4*index) : -1;
        int result = mgr ? *(int *)(uintptr_t)(mgr+64) : -1;
        int active = *(int *)(obj+404), mode = *(int *)(obj+408);
        static int ps=-9, pr=-9, pm=-9;
        if(state!=ps || result!=pr || mode!=pm) {
            printf("UI trace: state %d result %d active %d mode %d sequence %08x\n",state,result,active,mode,*(uint32_t *)(uintptr_t)CURRENT_SEQUENCE);
            fflush(stdout); ps=state;pr=result;pm=mode;
        }
    }
    g_menu_orig(c);
}
static int comp_mapped(uint64_t a);
int comp_in_options(void) {           /* menu.cpp: the game's own options screen is the current sequence (Arena UI only) */
    static int ui = -1;
    if (ui < 0) { const char *e = getenv("SSBU_COMPETITIVE_UI"); ui = e && e[0] == '1'; }
    /* (0.2.39) the game's .bss stays mapped once it is: asked of the OS until then only (the VirtualQuery cost ~0.7 ms of the
     * render thread every frame in the game's huge reservation). SSBU_COMP_MAPCACHE=0: asked every frame, as before. */
    static int cache = -1, seen;
    if (cache < 0) { const char *e = getenv("SSBU_COMP_MAPCACHE"); cache = !(e && e[0] == '0'); }
    if (!ui) return 0;
    if (!(cache && seen)) { if (!comp_mapped(CURRENT_SEQUENCE)) return 0; seen = 1; }
    return ((*(volatile uint32_t *)(uintptr_t)CURRENT_SEQUENCE) >> 24) == 0x06;
}
/* (field 15/11, matchmaking "like the real online", PROBE ONLY) SSBU_QUICK_NATIVE=1: the Online Ranked tile's sequence (and any
 * 0x07 Online one) is NOT turned into the Smash versus one: the game's own Online mode runs, to see how far it gets on this
 * runtime (scenes, nn:: services, missing files of the light archive). Not for play: there is no NEX server behind it. */
static int comp_quick_native(void) { static int v = -1; if (v < 0) { const char *e = getenv("SSBU_QUICK_NATIVE"); v = e && e[0] == '1'; } return v; }
static int comp_seq_allowed(uint32_t s) { return (s >> 24) == 0x01 || s == 0x02010000u || (s >> 24) == 0x06 || s == 0 || ((s >> 24) == 0x07 && comp_quick_native()); }

/* ---- Online matchmaking (no rating yet), the Arena menu's "Online Ranked" tile. The tile asks for SEQUENCE_ONLINE_MELEE_ANYONE
 * (tools/ui/menu_actions_classic.py). The SEQUENCE_* values are the lua constants the game registers at 0x7101bf18xx (x1 = the
 * name, w3 = the value, read off the binary): SEQUENCE_MELEE_NORMAL 0x02010000 (Smash), SEQUENCE_ONLINE_MELEE_ANYONE
 * 0x07010100. The light archive has no online mode, so the scene dispatcher turns that sequence into the Smash versus one
 * (character select, stage select, fight) with the quick-match flag set: rb_net.c then takes the lobby's opponent search
 * (MATCH op 2) for that fight, as SSBU_RB_NET_FIND=1 does, and debug_mode.c locks the rules both games use (1 on 1, stock,
 * 3 stocks, 7 minutes, no items, no hazards). Back at the main menu (0x01xxxxxx) the flag is cleared. Without a lobby
 * connection (online off / not signed in / rollback off / not joined yet) a popup says why and the game stays in the menu.
 * The rule kind / time / stock fields of the rule struct, read off its loader 0x7103295b30 (ui_rule_db field -> offset):
 * "rule" -> + 0x51 (rule_time 0, rule_stock 1, rule_hp 2), "time" -> + 0x54 (u16, SECONDS: the prc's MMSS-like value is
 * turned into minutes * 60 (+ 30); 0 = no limit), "stock" -> + 0x56. Forced in the three copies while the flag is on (the
 * select screens and the match set-up read them; a ruleset picked on the Rulesets screen is overwritten within 2 ms), the
 * player's own values put back at the menu. SSBU_QUICK_RULE_KIND / SSBU_QUICK_RULE_TIME (seconds) change them (tests). */
#define SEQ_MELEE_NORMAL  0x02010000u
#define SEQ_ONLINE_ANYONE 0x07010100u
#define RULE_KIND  0x51
#define RULE_TIME  0x54
#define RULE_STOCK 0x56
static volatile LONG g_quick, g_quick_skip_rules;
static uint8_t g_quick_saved[3][8], g_quick_saved_4b; static int g_quick_have;      /* (+ 0x4b of copy 1: comp_stage_state) */
static int g_quick_kind = -1, g_quick_time = -1;            /* the values written (-1 = not written) */
int comp_quickplay(void) { return g_quick != 0; }
static void comp_rule_dump(const char *why) {
    for (int i = 0; i < 3; i++) {
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)GA(k_rules[i]);
        printf("competitive: rule copy %d (%s): + 0x50: %02x %02x %02x %02x %02x %02x %02x %02x\n", i, why, r[0x50], r[0x51], r[0x52], r[0x53], r[0x54], r[0x55], r[0x56], r[0x57]);
    }
    fflush(stdout);
}
static void comp_quick_rules(void) {          /* (comp_keep, every 2 ms while the flag is on) */
    for (int i = 0; i < 3; i++) {
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)GA(k_rules[i]);
        if (g_quick_kind >= 0 && r[RULE_KIND] != (uint8_t)g_quick_kind) r[RULE_KIND] = (uint8_t)g_quick_kind;
        if (g_quick_time >= 0 && *(volatile uint16_t *)(r + RULE_TIME) != (uint16_t)g_quick_time) *(volatile uint16_t *)(r + RULE_TIME) = (uint16_t)g_quick_time;
        if (r[RULE_STOCK] != 3) r[RULE_STOCK] = 3;
    }
}
static void comp_quick_end(const char *why) {
    if (!InterlockedExchange(&g_quick, 0)) return;
    if (g_quick_have) for (int k = 0; k < 2; k++) {             /* (twice: comp_keep may have been inside comp_quick_rules) */
        if (k) Sleep(5);
        for (int i = 0; i < 3; i++) memcpy((void *)(uintptr_t)(GA(k_rules[i]) + 0x50), g_quick_saved[i], 8);
        *(volatile uint8_t *)(uintptr_t)(GA(k_rules[1]) + 0x4b) = g_quick_saved_4b;
    }
    if (g_quick_have) comp_rule_dump("put back");
    g_quick_have = 0;
    printf("competitive UI: online matchmaking off (%s); the player's own rules are back\n", why); fflush(stdout);
}
static int comp_quick_begin(void) {           /* 1 = the quick match starts (the Smash versus sequence follows) */
    int net_online(void), net_mode(void), net_rb_ready(void), rbn_on(void), rbn_handshake_wanted(void);
    void menu_popup(const char *title, const char *text);
    const char *why = NULL; int mode = net_online() > 0 ? net_mode() : -1;
    int game_rb_blocked(void); extern const char *const g_rb_1305_text;
    if (net_online() <= 0) why = "Online play is off. Sign in to your SSBU Arena account in the launcher (Online on), then start the game again.";
    else if (game_rb_blocked()) why = g_rb_1305_text;      /* (issue #6) hostrt.c */
    else if (!rbn_handshake_wanted() || rbn_on() <= 0) why = "Online matches need the rollback netcode: turn Rollback on in the launcher's Online settings, then start the game again.";
    else if (mode != 0 /* NET_MODE_LOBBY */ && mode != 1 /* NET_MODE_LAN */) why = "Matchmaking works through the online lobby, not in this online mode (a room you host or join).";
    else if (!net_rb_ready()) why = "Not connected to the online lobby (yet). Check your internet connection and try again in a few seconds.";
    if (why) {
        printf("competitive UI: Online Ranked: no online matchmaking: %s\n", why); fflush(stdout);
        menu_popup("Online matchmaking", why);
        return 0;
    }
    {   const char *e = getenv("SSBU_QUICK_RULE_KIND"); g_quick_kind = e && *e ? atoi(e) : 1; }
    {   const char *e = getenv("SSBU_QUICK_RULE_TIME"); g_quick_time = e && *e ? atoi(e) : 420; }
    for (int i = 0; i < 3; i++) memcpy(g_quick_saved[i], (const void *)(uintptr_t)(GA(k_rules[i]) + 0x50), 8);
    g_quick_saved_4b = *(volatile uint8_t *)(uintptr_t)(GA(k_rules[1]) + 0x4b);
    g_quick_have = 1;
    comp_rule_dump("the player's own");
    {   void rbn_ba_set_mode(int m); rbn_ba_set_mode(0); }      /* (Battle Arena) a quick match is never a squad match */
    InterlockedExchange(&g_quick, 1);
    InterlockedExchange(&g_quick_skip_rules, 1);       /* (comp_rule_state) */
    comp_quick_rules();
    printf("competitive UI: Online Ranked -> online matchmaking (no rating yet): the Smash versus select screens; the fight searches an opponent through the lobby\n"); fflush(stdout);
    menu_popup("Online matchmaking", "Choose your fighter, then start: the game finds an opponent in the online lobby (no rating yet). Player 2 (shown as a CPU on the select screen) is only a placeholder: your online opponent takes that place. Rules: 1 on 1, 3 stocks, 7 minutes, no items, no hazards.");
    return 1;
}
static void comp_scene(Ctx *c, void *user) {
    (void)user;
    volatile uint32_t *seq = (volatile uint32_t *)(uintptr_t)CURRENT_SEQUENCE;
    uint32_t s = *seq;
    if (getenv("SSBU_UI_TRACE")) {              /* every mode change the scene dispatcher sees (menu route mapping) */
        static uint32_t last = 0xffffffffu;
        if (s != last) { printf("UI trace: sequence %08x\n", s); fflush(stdout); last = s; if (s == SEQ_MELEE_NORMAL) comp_rule_dump("Smash versus"); }
    }
    const char *ui = getenv("SSBU_COMPETITIVE_UI");
    if (ui && ui[0] == '1') {
        if ((s >> 24) == 0x01 && g_quick) comp_quick_end("back at the main menu");
        if (s == SEQ_ONLINE_ANYONE && comp_quick_native()) { static int said; if (!said++) { printf("competitive UI: Online Ranked -> the game's own Online mode (SSBU_QUICK_NATIVE=1, probe)\n"); fflush(stdout);
                /* (online-mm 15/11, PROBE) SSBU_QUICK_NATIVE_WATCH=1: a hardware write watch on the current sequence (debug_mode.c debug_watch:
                 * every thread, the guest functions of each write): who sends the Online mode back to the main menu */
                { const char *w = getenv("SSBU_QUICK_NATIVE_WATCH"); if (w && w[0] == '1') { void debug_watch(const uint64_t *a, int n); uint64_t a = CURRENT_SEQUENCE; debug_watch(&a, 1); }      /* (=2: comp_seqstep) */ } } }
        else if (s == SEQ_ONLINE_ANYONE) {
            if (comp_quick_begin()) *seq = SEQ_MELEE_NORMAL;
            else *seq = 0x01000000u;
            s = *seq;
        }
    }
    /* the Arena main menu (tools/ui/prepare_menu_classic.py): Settings is the game's own options (0x06, + there opens the
     * Arena settings: menu.cpp); Exit is the Games & More button, whose modes the competitive build does not have: its
     * sequence (0x04..) asks to exit the game instead (the settings open on the question) */
    if (ui && ui[0] == '1' && (s >> 24) == 0x04 && menu_enabled()) {
        printf("competitive UI: Exit -> exit the game? (sequence %08x)\n", s);
        *seq = 0x01000000u;
        menu_request_exit();
        s = *seq;
    }
    if (!comp_seq_allowed(s)) { printf("competitive: mode %08x is not part of the competitive build: back to the menu\n", s); *seq = 0x01000000u; }
    g_scene_orig(c);
}

static void comp_battle(Ctx *c, void *user) {
    (void)user;
    int changed = 0;
    comp_stage();
    for (int i = 0; i < 3; i++) {
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)GA(k_rules[i]);
        if (r[0x5f] || r[0x68] || r[0x61] || r[0x63] || (r[0x65] && comp_nopause())) changed++;
        r[0x5f] = 0;            /* charge: Final Smash meter */
        if (comp_nopause()) r[0x65] = 0;      /* can_pause: no pause */
        r[0x68] = 0;            /* item_switch */
        r[0x61] = 0;            /* stage_gimmick: hazards */
        r[0x63] = 0;            /* change_stage: stage morph */
    }
    int items_were_on = *(volatile uint32_t *)(uintptr_t)MATCH_ITEM_APPEAR != 0;
    *(volatile uint32_t *)(uintptr_t)MATCH_ITEM_APPEAR = 0;
    if (++g_battles <= 20) printf("competitive: battle %ld: FS meter, items, hazards, stage morph and pause off (%d of 3 rule copies had one on%s)\n", g_battles, changed, items_were_on ? ", item frequency was on" : "");
    g_battle_orig(c);
}

static void comp_clear(void) {
    for (int i = 0; i < 3; i++) {
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)GA(k_rules[i]);
        if (r[0x5f]) r[0x5f] = 0;
        if (r[0x65] && comp_nopause()) r[0x65] = 0;      /* can_pause */
        if (r[0x68]) r[0x68] = 0;
        if (r[0x61]) r[0x61] = 0;
        if (r[0x63]) r[0x63] = 0;
    }
    if (*(volatile uint32_t *)(uintptr_t)MATCH_ITEM_APPEAR) *(volatile uint32_t *)(uintptr_t)MATCH_ITEM_APPEAR = 0;
}
static int comp_mapped(uint64_t a) {        /* the game's .bss is mapped by the loader some time after the hook table works */
    MEMORY_BASIC_INFORMATION mi;
    return VirtualQuery((const void *)(uintptr_t)a, &mi, sizeof mi) && mi.State == MEM_COMMIT && (mi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE));
}
static DWORD WINAPI comp_keep(LPVOID arg) {
    (void)arg;
    for (;;) {
        int ok = 1;
        for (int i = 0; i < 3; i++) ok &= comp_mapped(GA(k_rules[i])) && comp_mapped(GA(k_rules[i]) + 0x68);
        ok &= comp_mapped(MATCH_ITEM_APPEAR);
        if (ok) break;
        Sleep(20);
    }
    for (;;) {
        comp_clear();
        if (g_quick) {          /* (the main menu's own scene is not made by the scene dispatcher comp_scene hooks: the flag ends here) */
            if ((*(volatile uint32_t *)(uintptr_t)CURRENT_SEQUENCE >> 24) == 0x01) comp_quick_end("back at the main menu");
            else comp_quick_rules();
        }
        Sleep(2);
    }
    return 0;
}
/* (online matchmaking) the Smash versus sequence (MeleeNormalSequenceScene, made at 0x7102336a00..) is a state machine:
 * this + 0x30 + 0x10 * state = the state's handler (2 Rulesets 0x71023343e0, 4 stage 0x71023353d0, 5 character select
 * 0x7102334f50, 7 after the fight 0x7102335ef0 -> 4), this + 0x11c the current step, + 0x120 the state, + 0x128 the one
 * before, this + 0xd0 + 4 * step the next one; a handler is called every frame while its state is on, with this + 0x124 = 0
 * the first time (it then asks for its scene, scene change 0x7103726120) and afterwards it waits for the scene's result
 * ([this] + 0x40; Rulesets: 1 decided -> next 4). A quick match does not show the Rulesets screen (its ruleset would not be
 * the one played, comp_quick_rules): the first time the Rulesets state starts after the Online Ranked tile, its handler is
 * not run and the next state is 4, as if a ruleset had been decided. Back from the character select (hold B), the Rulesets
 * screen is shown as usual; B there goes back to the main menu.
 * The stage state (4) shows the stage select only when rule copy 1 + 0x4b is 0, 1 or 2 (0x7102335464..); with a decided
 * ruleset the byte is not (run ui3: 4 after "3S Comp"; 0x64 in the game's own rules at boot), so the Arena flow never shows
 * that screen (the light archive's list has no stage pictures). Without the Rulesets screen the byte became 0 after the fight
 * (run ui2/ui3: the stage select came after the results, before the character select): in a quick match it is kept out of
 * 0..2 at that state's start (the game's own path then: next state 5, the character select). Put back at the main menu. */
#define RULE1_STAGE_SELECT GA(0x710530996full)   /* (k_rules[1] + 0x4b) */
static ImportFn g_rule_state_orig, g_stage_state_orig;
static void comp_rule_state(Ctx *c, void *user) {
    (void)user;
    uint64_t self = c->x[0];
    if (g_quick && self && *(volatile uint32_t *)(uintptr_t)(self + 0x124) == 0 && InterlockedExchange(&g_quick_skip_rules, 0)) {
        int32_t step = *(volatile int32_t *)(uintptr_t)(self + 0x11c);
        if (step >= 0 && step < 0x13) {
            *(volatile uint32_t *)(uintptr_t)(self + 0xd0 + 4ull * (uint64_t)step) = 4;
            printf("competitive UI: online matchmaking: the Rulesets screen skipped (step %d: next state 4, then the character select)\n", step); fflush(stdout);
            return;
        }
    }
    g_rule_state_orig(c);
}
static void comp_stage_state(Ctx *c, void *user) {
    (void)user;
    uint64_t self = c->x[0];
    if (g_quick && self && *(volatile uint32_t *)(uintptr_t)(self + 0x124) == 0 && *(volatile uint8_t *)(uintptr_t)RULE1_STAGE_SELECT <= 2) {
        printf("competitive UI: online matchmaking: no stage select (rule copy 1 + 0x4b %u -> 0x64)\n", *(volatile uint8_t *)(uintptr_t)RULE1_STAGE_SELECT); fflush(stdout);
        *(volatile uint8_t *)(uintptr_t)RULE1_STAGE_SELECT = 0x64;
    }
    g_stage_state_orig(c);
}
/* (online-mm 15/11, PROBE) SSBU_QUICK_NATIVE_WATCH=2: the scene sequence controller's state step 0x7102363970 (x0 = controller, its state
 * machine at +0xe8: wanted state +0xc0 [+0x10c index], current +0x110; state 7 = 0x710235c6b0 "back to the main menu"). Once the Online
 * sequence is set: hardware write watches on the wanted / current state and the sequence, to find who asks for state 7. */
static ImportFn g_seqstep_orig;
static void comp_seqstep(Ctx *c, void *user) {
    (void)user;
    static int armed;
    if (!armed && *(volatile uint32_t *)(uintptr_t)CURRENT_SEQUENCE == SEQ_ONLINE_ANYONE) {
        armed = 1;
        uint64_t sm = *(volatile uint64_t *)(uintptr_t)(c->x[0] + 0xe8);
        int idx = *(volatile int32_t *)(uintptr_t)(sm + 0x10c);
        printf("competitive probe: sequence controller %llx, state machine %llx, index %d, wanted %d, current %d\n", (unsigned long long)c->x[0],
               (unsigned long long)sm, idx, idx >= 0 && idx < 0x13 ? *(volatile int32_t *)(uintptr_t)(sm + 0xc0 + 4ull * (unsigned)idx) : -1, *(volatile int32_t *)(uintptr_t)(sm + 0x110));
        fflush(stdout);
        void debug_watch(const uint64_t *a, int n);
        uint64_t a[3] = {sm + 0xc0 + 4ull * (unsigned)(idx >= 0 && idx < 0x13 ? idx : 0), sm + 0x110, CURRENT_SEQUENCE};
        debug_watch(a, 3);
    }
    g_seqstep_orig(c);
}
/* (online-mm 15/11, PROBE) SSBU_QUICK_NATIVE_WATCH=3: the scene manager's two "change to the scene named ..." functions (0x7103725c90:
 * the dispatcher's, 0x7103726120: the sequence controller's), x1 = {hash u32, length u32, text}: every scene asked for, with the guest
 * caller and its frame chain (who sends the Online scene back to the menus) */
static ImportFn g_scn_a_orig, g_scn_b_orig;
static void comp_scn_log(Ctx *c, const char *which) {
    char name[72] = "?"; uint64_t p = c->x[1];
    if (p) { int k = 0; for (; k < 70; k++) { char ch = *(volatile char *)(uintptr_t)(p + 8 + (uint64_t)k); if (!ch) break; name[k] = ch >= 0x20 && ch < 0x7f ? ch : '?'; } name[k] = 0; }
    char fr[160] = ""; int n = 0; uint64_t fp = c->x[29];
    for (int d = 0; d < 8 && fp >= c->x[31] && fp - c->x[31] < 0x1000000 && n < (int)sizeof fr - 20; d++) {
        uint64_t nfp = *(volatile uint64_t *)(uintptr_t)fp, lr = *(volatile uint64_t *)(uintptr_t)(fp + 8);
        n += snprintf(fr + n, sizeof fr - n, " %llx", (unsigned long long)lr);
        if (nfp <= fp) break; fp = nfp;
    }
    printf("competitive probe: scene %s asked (%s) from %llx, frames%s; sequence %08x\n", name, which, (unsigned long long)c->x[30], fr,
           *(volatile uint32_t *)(uintptr_t)CURRENT_SEQUENCE);
    fflush(stdout);
}
static void comp_scn_a(Ctx *c, void *u) { (void)u; comp_scn_log(c, "dispatcher's"); g_scn_a_orig(c); }
/* (online-mm 15/11, PROBE A0) SSBU_QUICK_NATIVE_WATCH=4: why the Online scene ends at once. OnlineMeleeAnySequenceScene (factory
 * 0x71022d9c80, vtable 0x71050b8000: enter +0x50 0x710236ab10 makes a 13-state machine, update +0x60 0x710236b020) runs state 0
 * 0x71023675c0 first: it reads the game's online session object, [[[0x71052c3c58] + 8] + 0x80] (a 25-state machine ticked by the main
 * loop at 0x71022d2d94: current state + 0x218, wanted + 0x1c8 [+ 0x214 index]), and when its byte + 0x244 is 0 (or its state is 9)
 * it calls 0x7103726540 (x0 = the scene manager, w1 = 2): the scene ends and the manager goes back to the scene it came from (the
 * MenuSequenceScene asked right after it). Logged here: that object at state 0 and every 0x7103726540 call with its caller.
 * SSBU_QUICK_NATIVE_FORCE=1 (probe only): + 0x244 set to 1 before state 0, to see the scene's own flow past that check. */
static ImportFn g_onl0_orig, g_scnend_orig;
static uint64_t comp_online_obj(void) {
    if (!comp_mapped(GA(0x71052c3c58ull))) return 0;
    uint64_t g = *(volatile uint64_t *)(uintptr_t)GA(0x71052c3c58ull); if (!g) return 0;
    uint64_t app = *(volatile uint64_t *)(uintptr_t)(g + 8); if (!app) return 0;
    return *(volatile uint64_t *)(uintptr_t)(app + 0x80);
}
static void comp_online_dump(const char *why) {
    uint64_t o = comp_online_obj();
    if (!o) { printf("competitive probe: online session object (%s): none\n", why); fflush(stdout); return; }
    int idx = *(volatile int32_t *)(uintptr_t)(o + 0x214);
    printf("competitive probe: online session object %llx (%s): +0x244 %u, state %d (wanted %d, index %d, before %d), +0x208..: %02x %02x %02x %02x\n",
           (unsigned long long)o, why, *(volatile uint8_t *)(uintptr_t)(o + 0x244), *(volatile int32_t *)(uintptr_t)(o + 0x218),
           idx >= 0 && idx < 0x13 ? *(volatile int32_t *)(uintptr_t)(o + 0x1c8 + 4ull * (unsigned)idx) : -1, idx, *(volatile int32_t *)(uintptr_t)(o + 0x220),
           *(volatile uint8_t *)(uintptr_t)(o + 0x208), *(volatile uint8_t *)(uintptr_t)(o + 0x209), *(volatile uint8_t *)(uintptr_t)(o + 0x230), *(volatile uint8_t *)(uintptr_t)(o + 0x231));
    static int handlers;
    if (!handlers++) {          /* its vtable and state handlers (+ 0x38 + 0x10 * state, 25 states): to read them off the binary */
        printf("competitive probe: online session object: vtable %llx, handlers", (unsigned long long)*(volatile uint64_t *)(uintptr_t)o);
        for (int k = 0; k < 0x19; k++) printf(" %d:%llx", k, (unsigned long long)*(volatile uint64_t *)(uintptr_t)(o + 0x38 + 0x10ull * (unsigned)k));
        printf("\n");
    }
    fflush(stdout);
}
/* (online-mm 15/11, A0 fix, with SSBU_QUICK_NATIVE=1 only) The game's own Online menu logs in before it opens Quickplay: the session
 * object's start 0x7101685520 (x0 = the object, w1 = which steps: bit 0 the network request (state 2, nn::nifm), bit 1 the network
 * service account (3, nn::account), bit 2 the user + the game server login (4, 5 = NEX: its callback 0x71016850f0 sets + 0x244 and
 * goes to state 11) and 10, bits 3..8 the states 0xd, 0x10, 0x11, 0x14..0x16; the game's callers pass 0x1ff, 0x1fc or 0xcc). The
 * Arena tile goes straight to Quickplay, so nobody started it: the scene's state 0 found the object not logged in and ended the scene.
 * Here, the first time state 0 finds it not logged in and idle, the game's own start is called (SSBU_QUICK_NATIVE_LOGIN = the steps,
 * hex, default 1ff; 0 = not called, the scene ends as before) and state 0 is held (not run) while the login runs: logged in -> state 0
 * runs and the scene goes on with its own flow; the object back at state 0 / 1 / 9 without + 0x244 (a step failed: the game shows
 * its own error), or 90 s -> state 0 runs and the scene ends as before. Every change of the object's state is logged. */
static int comp_online_login(Ctx *c, uint64_t o) {          /* 1 = state 0 held this frame */
    static int started, last_state = -2, frames; static unsigned steps = 0x1ff; static ULONGLONG t0;
    int st = *(volatile int32_t *)(uintptr_t)(o + 0x218);
    if (st != last_state) { printf("competitive probe: online session state %d -> %d (+0x244 %u)\n", last_state, st, *(volatile uint8_t *)(uintptr_t)(o + 0x244)); fflush(stdout); last_state = st; }
    if (*(volatile uint8_t *)(uintptr_t)(o + 0x244)) return 0;
    if (!started) {
        const char *e = getenv("SSBU_QUICK_NATIVE_LOGIN"); if (e && *e) steps = (unsigned)strtoul(e, NULL, 16);
        if (!steps || (st != 0 && st != -1)) return 0;
        started = 1; t0 = GetTickCount64(); frames = 0;
        uint64_t save[32]; memcpy(save, c->x, sizeof save);
        c->x[0] = o; c->x[1] = steps;
        call_guest(c, GA(0x7101685520ull));
        uint64_t r = c->x[0];
        memcpy(c->x, save, sizeof save);
        printf("competitive probe: the game's online login started (steps %x): result %llx\n", steps, (unsigned long long)r); fflush(stdout);
        return 1;
    }
    if (started == 2) return 0;
    if (++frames > 3 && (st == 0 || st == 1 || st == 9 || st == -1 || GetTickCount64() - t0 > 90000)) {
        started = 2;
        printf("competitive probe: the game's online login ended without logging in (state %d, %llu ms): the Online scene ends\n", st, (unsigned long long)(GetTickCount64() - t0)); fflush(stdout);
        return 0;
    }
    return 1;
}
static void comp_onl_state0(Ctx *c, void *u) {
    (void)u;
    static int n;
    { const char *w = getenv("SSBU_QUICK_NATIVE_WATCH"); if (w && w[0] == '4' && n++ < 4) comp_online_dump("Online scene state 0"); }
    { const char *f = getenv("SSBU_QUICK_NATIVE_FORCE"); uint64_t o;
      if (f && f[0] == '1' && (o = comp_online_obj()) && !*(volatile uint8_t *)(uintptr_t)(o + 0x244)) {
          *(volatile uint8_t *)(uintptr_t)(o + 0x244) = 1; printf("competitive probe: + 0x244 set to 1 (SSBU_QUICK_NATIVE_FORCE)\n"); fflush(stdout); } }
    { uint64_t o = comp_quick_native() ? comp_online_obj() : 0; if (o && comp_online_login(c, o)) return; }
    g_onl0_orig(c);
}
static void comp_scnend(Ctx *c, void *u) {
    (void)u;
    printf("competitive probe: scene end 0x7103726540 (w1 %u) from %llx; sequence %08x\n", (unsigned)c->x[1], (unsigned long long)c->x[30], *(volatile uint32_t *)(uintptr_t)CURRENT_SEQUENCE);
    fflush(stdout);
    g_scnend_orig(c);
}
static void comp_scn_b(Ctx *c, void *u) { (void)u; comp_scn_log(c, "controller's"); g_scn_b_orig(c); }
static DWORD WINAPI comp_wait(LPVOID arg) {
    (void)arg;
    /* started by a constructor, before main.elf is read: GA() needs the game build (it stops the program before that) */
    for (int i = 0; i < 6000 && g_game_build < 0; i++) Sleep(10);
    if (g_game_build < 0) { printf("competitive: main.elf was not read in time: OFF\n"); return 0; }
    for (int i = 0; i < 3000; i++) {        /* the hook table answers once the game's function table is set up */
        if (hook_install(GA(0x71014e58f0ull), comp_battle, NULL, &g_battle_orig)) {
            if (!hook_install(GA(0x710235cbf0ull), comp_scene, NULL, &g_scene_orig)) printf("competitive: scene hook FAILED: every mode can be opened\n");
            { const char *w = getenv("SSBU_QUICK_NATIVE_WATCH"); if (w && w[0] == '2' && comp_quick_native()) hook_install(GA(0x7102363970ull), comp_seqstep, NULL, &g_seqstep_orig);
              if (w && (w[0] == '3' || w[0] == '4')) {      /* (also without SSBU_QUICK_NATIVE: the Smash versus flow, to compare) */ hook_install(GA(0x7103725c90ull), comp_scn_a, NULL, &g_scn_a_orig); hook_install(GA(0x7103726120ull), comp_scn_b, NULL, &g_scn_b_orig); }
              if (w && w[0] == '4') hook_install(GA(0x7103726540ull), comp_scnend, NULL, &g_scnend_orig);
              if ((w && w[0] == '4') || comp_quick_native()) hook_install(GA(0x71023675c0ull), comp_onl_state0, NULL, &g_onl0_orig);
              { void online_nex_hooks(void); online_nex_hooks(); } }      /* nnonline.c: SSBU_ONLINE_NEX=1 only */
            const char *arena_ui = getenv("SSBU_COMPETITIVE_UI");
            if (arena_ui && arena_ui[0] == '1') {
                int menu_hook = hook_install(GA(0x710232ce40ull), comp_menu, NULL, &g_menu_orig);
                int local_hook = hook_install(GA(0x7101bef020ull), comp_open_local, NULL, &g_pallet_orig);
                int ready_hook = hook_install(GA(0x7101af97f0ull), comp_pallet_ready, NULL, &g_pallet_ready_orig);
                if (!hook_install(GA(0x71023343e0ull), comp_rule_state, NULL, &g_rule_state_orig) || !hook_install(GA(0x71023353d0ull), comp_stage_state, NULL, &g_stage_state_orig))
                    printf("competitive UI: Smash versus state hooks FAILED (a quick match shows the Rulesets screen)\n");
                printf("competitive UI: menu hook %d; trace %s\n", menu_hook, getenv("SSBU_UI_TRACE") ? getenv("SSBU_UI_TRACE") : "off");
                if (!local_hook || !ready_hook) printf("competitive UI: Local Wireless action hook FAILED\n");
            }
            HANDLE k = CreateThread(NULL, 0, comp_keep, NULL, 0, NULL);
            if (k) CloseHandle(k);
            printf("competitive: ON (no FS meter, no random items, no hazards, no stage morph)\n");
            return 0;
        }
        Sleep(10);
    }
    printf("competitive: the hook could not be installed: OFF\n");
    return 0;
}

/* (0.2.50) one line in game.log saying whether Arena's menu is on and, when it is not, why. A player got the original Smash
 * menu after 4 reinstalls: the launcher (Core.cs) sets SSBU_COMPETITIVE_UI only when characters\arena_menu is complete, and
 * nothing said so. SSBU_LAUNCHER (the launcher's version, 0.2.50 and later) tells a start from the launcher apart from a
 * shortcut, Play SSBU.bat or ssbu_native.exe started by hand; SSBU_ARENA_MENU_WHY is the launcher's reason. Only a log line. */
__attribute__((constructor)) static void comp_menu_report(void) {
    const char *la = getenv("SSBU_LAUNCHER"), *ui = getenv("SSBU_COMPETITIVE_UI"), *lf = getenv("SSBU_LOOSE_FILES"), *why = getenv("SSBU_ARENA_MENU_WHY");
    const char *chars = getenv("SSBU_CHARACTERS");
    char root[MAX_PATH * 2], f[MAX_PATH * 3];
    if (chars && chars[0]) snprintf(root, sizeof root, "%s", chars);
    else {
        DWORD n = GetModuleFileNameA(NULL, root, MAX_PATH);
        while (n && root[n - 1] != '\\' && root[n - 1] != '/') n--;
        snprintf(root + n, sizeof root - n, "..\\characters");
    }
    snprintf(f, sizeof f, "%s\\arena_menu\\ui\\layout\\menu\\main_menu\\main_menu\\layout.arc", root);
    int have = GetFileAttributesA(f) != INVALID_FILE_ATTRIBUTES;
    int on = ui && ui[0] == '1' && lf && lf[0] == '1' && have;
    if (on) printf("arena menu: ON (launcher %s, %s\\arena_menu)\n", la ? la : "unknown", root);
    else if (!la) printf("arena menu: OFF, the original Smash menu: not started by the SSBU Arena launcher (no SSBU_LAUNCHER: a shortcut, Play SSBU.bat, "
                         "ssbu_native.exe started by hand, or a launcher older than 0.2.50); Arena menu file %s\n", have ? "present" : "MISSING");
    else if (why && why[0]) printf("arena menu: OFF, the original Smash menu: launcher %s says: %s\n", la, why);
    else if (!(lf && lf[0] == '1')) printf("arena menu: OFF, the original Smash menu: the loose-file layer is off (SSBU_LOOSE_FILES=%s)\n", lf ? lf : "unset");
    else if (!have) printf("arena menu: OFF, the original Smash menu: no file %s\n", f);
    else printf("arena menu: OFF, the original Smash menu: SSBU_COMPETITIVE_UI=%s\n", ui ? ui : "unset");
    fflush(stdout);
}

__attribute__((constructor)) static void comp_start(void) {
    const char *e = getenv("SSBU_COMPETITIVE");
    if (!e || e[0] != '1') return;
    HANDLE h = CreateThread(NULL, 0, comp_wait, NULL, 0, NULL);
    if (h) CloseHandle(h);
}
