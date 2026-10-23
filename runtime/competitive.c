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
 * choices); both are kept at 0 the same way: hazards and morph always off. */
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <string.h>
#include <windows.h>
#include "runtime.h"
#include "hostrt.h"
#include "menu.h"

typedef void (*HookFn)(Ctx *c, void *user);
int hook_install(uint64_t addr, HookFn repl, void *user, ImportFn *orig);      /* fighter_sdk\hook_host.c */

static const uint64_t k_rules[] = {0x710530454cull, 0x7105309924ull, 0x710530f49cull};
#define MATCH_ITEM_APPEAR 0x71052c4204ull     /* the global match block 0x71052c41b0 + 0x54: item frequency (0 none; tests/DEBUG_MODE.md).
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
    uint32_t cur = *(volatile uint32_t *)(uintptr_t)0x7105309968ull;
    for (uint32_t e = 0; e < 3; e++) {
        volatile uint64_t *id = (volatile uint64_t *)(uintptr_t)(0x71053097c0ull + 0x20ull * e);
        uint64_t h = *id & 0xffffffffffull;
        if (!h) continue;
        int ok = 0; char nm[48];
        for (int k = 0; k < 6; k++) { snprintf(nm, sizeof nm, "ui_stage_%s", k_stages[k]); if (h == comp_h40(nm)) ok = 1; }
        if (ok) continue;
        uint64_t mix = *id ^ (uint64_t)e * 0x9e3779b97f4a7c15ull ^ *(volatile uint64_t *)(uintptr_t)(0x71053097c0ull + 0x20ull * e + 0x10);
        mix ^= mix >> 29; mix *= 0xbf58476d1ce4e5b9ull; mix ^= mix >> 32;
        int k = (int)(mix % 6);
        snprintf(nm, sizeof nm, "ui_stage_%s", k_stages[k]);
        *id = 0x6900000000000000ull | comp_h40(nm);
        *(volatile uint32_t *)(uintptr_t)(0x71053097c0ull + 0x20ull * e + 8) = 0;      /* normal form */
        if (e == cur || cur >= 3) printf("competitive: stage choice %llx is not one of the 6 (random?): %s instead\n", (unsigned long long)h, k_stages[k]);
    }
}

/* The modes outside the competitive 1v1. The current sequence is a global of 13.0.4 (0x71053040f0: SEQUENCE_* ids, the
 * lua constants registered at 0x7101bf1930: 0x01000000 title / menus, 0x02010000 Smash (also local wireless), 0x03.. Spirits,
 * 0x04.. Games & More, 0x05.. Vault, 0x06.. Options, 0x07.. Online). The scene dispatcher 0x710235cbf0 reads it and
 * creates <name>SequenceScene; any other id is put back to the menus there: the game returns to the main menu instead of
 * opening a mode whose data the light archive does not have. */
#define CURRENT_SEQUENCE 0x71053040f0ull
static ImportFn g_scene_orig;
static ImportFn g_menu_orig;
static ImportFn g_pallet_orig;
static ImportFn g_pallet_ready_orig;
static uint8_t *g_local_pending;
/* PalletMenu's native layout controller (13.0.4). Its completed selection is
 * consumed by 0x71032ecc80 and broadcast as PalletCommandMessage. Local Wireless
 * is command 3 (the third sidebar button, with one-based native IDs). */
static uint8_t *comp_pallet(void) {
    uint64_t owner = *(uint64_t *)(uintptr_t)0x710532e7f0ull;
    uint64_t service = owner ? *(uint64_t *)(uintptr_t)(owner+8) : 0;
    uint64_t layout = service ? *(uint64_t *)(uintptr_t)(service+8) : 0;
    uint8_t *p = layout ? *(uint8_t **)(uintptr_t)(layout+128) : NULL;
    if (!p || *(uint64_t *)(p+72) != 0x7101af97f0ull || *(uint64_t *)(p+120) != 0x7101af9ee0ull) return NULL;
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
    g_pallet_cmd = cmd == 5 ? 5 : 3;
    g_local_pending = p;
    printf("competitive UI: %s -> opening the native side bar command %d\n", g_pallet_cmd == 5 ? "Settings" : "Online LDN", g_pallet_cmd); fflush(stdout);
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
    return ui && comp_mapped(CURRENT_SEQUENCE) && ((*(volatile uint32_t *)(uintptr_t)CURRENT_SEQUENCE) >> 24) == 0x06;
}
static int comp_seq_allowed(uint32_t s) { return (s >> 24) == 0x01 || s == 0x02010000u || (s >> 24) == 0x06 || s == 0; }

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
static volatile LONG g_quick;
static uint8_t g_quick_saved[3][8]; static int g_quick_have;
static int g_quick_kind = -1, g_quick_time = -1;            /* the values written (-1 = not written) */
int comp_quickplay(void) { return g_quick != 0; }
static void comp_rule_dump(const char *why) {
    for (int i = 0; i < 3; i++) {
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)k_rules[i];
        printf("competitive: rule copy %d (%s): + 0x50: %02x %02x %02x %02x %02x %02x %02x %02x\n", i, why, r[0x50], r[0x51], r[0x52], r[0x53], r[0x54], r[0x55], r[0x56], r[0x57]);
    }
    fflush(stdout);
}
static void comp_quick_rules(void) {          /* (comp_keep, every 2 ms while the flag is on) */
    for (int i = 0; i < 3; i++) {
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)k_rules[i];
        if (g_quick_kind >= 0 && r[RULE_KIND] != (uint8_t)g_quick_kind) r[RULE_KIND] = (uint8_t)g_quick_kind;
        if (g_quick_time >= 0 && *(volatile uint16_t *)(r + RULE_TIME) != (uint16_t)g_quick_time) *(volatile uint16_t *)(r + RULE_TIME) = (uint16_t)g_quick_time;
        if (r[RULE_STOCK] != 3) r[RULE_STOCK] = 3;
    }
}
static void comp_quick_end(const char *why) {
    if (!InterlockedExchange(&g_quick, 0)) return;
    if (g_quick_have) for (int k = 0; k < 2; k++) {             /* (twice: comp_keep may have been inside comp_quick_rules) */
        if (k) Sleep(5);
        for (int i = 0; i < 3; i++) memcpy((void *)(uintptr_t)(k_rules[i] + 0x50), g_quick_saved[i], 8);
    }
    g_quick_have = 0;
    printf("competitive UI: online matchmaking off (%s); the player's own rules are back\n", why); fflush(stdout);
}
static int comp_quick_begin(void) {           /* 1 = the quick match starts (the Smash versus sequence follows) */
    int net_online(void), net_mode(void), net_rb_ready(void), rbn_on(void), rbn_handshake_wanted(void);
    void menu_popup(const char *title, const char *text);
    const char *why = NULL; int mode = net_online() > 0 ? net_mode() : -1;
    if (net_online() <= 0) why = "Online play is off. Sign in to your SSBU Arena account in the launcher (Online on), then start the game again.";
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
    for (int i = 0; i < 3; i++) memcpy(g_quick_saved[i], (const void *)(uintptr_t)(k_rules[i] + 0x50), 8);
    g_quick_have = 1;
    comp_rule_dump("the player's own");
    InterlockedExchange(&g_quick, 1);
    comp_quick_rules();
    printf("competitive UI: Online Ranked -> online matchmaking (no rating yet): the Smash versus select screens; the fight searches an opponent through the lobby\n"); fflush(stdout);
    menu_popup("Online matchmaking", "Choose your fighter and a stage, then start: the game finds an opponent in the online lobby (no rating yet). Rules: 1 on 1, 3 stocks, 7 minutes, no items, no hazards.");
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
        if (s == SEQ_ONLINE_ANYONE) {
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
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)k_rules[i];
        if (r[0x5f] || r[0x68] || r[0x61] || r[0x63]) changed++;
        r[0x5f] = 0;            /* charge: Final Smash meter */
        r[0x68] = 0;            /* item_switch */
        r[0x61] = 0;            /* stage_gimmick: hazards */
        r[0x63] = 0;            /* change_stage: stage morph */
    }
    int items_were_on = *(volatile uint32_t *)(uintptr_t)MATCH_ITEM_APPEAR != 0;
    *(volatile uint32_t *)(uintptr_t)MATCH_ITEM_APPEAR = 0;
    if (++g_battles <= 20) printf("competitive: battle %ld: FS meter, items, hazards and stage morph off (%d of 3 rule copies had one on%s)\n", g_battles, changed, items_were_on ? ", item frequency was on" : "");
    g_battle_orig(c);
}

static void comp_clear(void) {
    for (int i = 0; i < 3; i++) {
        volatile uint8_t *r = (volatile uint8_t *)(uintptr_t)k_rules[i];
        if (r[0x5f]) r[0x5f] = 0;
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
        for (int i = 0; i < 3; i++) ok &= comp_mapped(k_rules[i]) && comp_mapped(k_rules[i] + 0x68);
        ok &= comp_mapped(MATCH_ITEM_APPEAR);
        if (ok) break;
        Sleep(20);
    }
    for (;;) { comp_clear(); if (g_quick) comp_quick_rules(); Sleep(2); }
    return 0;
}
static DWORD WINAPI comp_wait(LPVOID arg) {
    (void)arg;
    for (int i = 0; i < 3000; i++) {        /* the hook table answers once the game's function table is set up */
        if (hook_install(0x71014e58f0ull, comp_battle, NULL, &g_battle_orig)) {
            if (!hook_install(0x710235cbf0ull, comp_scene, NULL, &g_scene_orig)) printf("competitive: scene hook FAILED: every mode can be opened\n");
            const char *arena_ui = getenv("SSBU_COMPETITIVE_UI");
            if (arena_ui && arena_ui[0] == '1') {
                int menu_hook = hook_install(0x710232ce40ull, comp_menu, NULL, &g_menu_orig);
                int local_hook = hook_install(0x7101bef020ull, comp_open_local, NULL, &g_pallet_orig);
                int ready_hook = hook_install(0x7101af97f0ull, comp_pallet_ready, NULL, &g_pallet_ready_orig);
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

__attribute__((constructor)) static void comp_start(void) {
    const char *e = getenv("SSBU_COMPETITIVE");
    if (!e || e[0] != '1') return;
    HANDLE h = CreateThread(NULL, 0, comp_wait, NULL, 0, NULL);
    if (h) CloseHandle(h);
}
