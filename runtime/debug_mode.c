/* debug_mode.c - TEST / DEBUG FAST MODE (test-only; every feature is behind an environment variable, none set = no effect).
 * See tests\DEBUG_MODE.md. Called once from boot.c before nnMain (debug_mode_init). */
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>
#include "runtime.h"
#include "hostrt.h"

uint64_t hook_address(const char *name);              /* fighter_sdk\hook_host.c (included by nnro.c) */

static int safe_read(uint64_t addr, void *buf, size_t n) {
    SIZE_T got = 0;
    if (addr < 0x10000) return 0;
    return ReadProcessMemory(GetCurrentProcess(), (const void *)(uintptr_t)addr, buf, n, &got) && got == n;
}
static uint64_t sr64(uint64_t a) { uint64_t v = 0; return safe_read(a, &v, 8) ? v : 0; }

/* ---- the game's fighter entries (layout read off the lua_bind accessors of the game's own binary):
 *   FighterManager singleton -> *mgr = impl; impl + 0x20 + 8*entry_id = FighterEntry*; entry + 0xf0 = FighterInformation;
 *   *(information + 8) = the information record (hit point 0x38, colour 0x51, cpu flag 0x92, stock 0xd8, category 0x3ac). */
static uint64_t fighter_info_record(int id) {
    static uint64_t inst;
    if (!inst) { uint64_t a = hook_address("_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E"); if (!a) return 0; inst = a; }
    uint64_t mgr = sr64(inst), impl = mgr ? sr64(mgr) : 0, entry = impl ? sr64(impl + 0x20 + 8 * (uint64_t)id) : 0;
    uint64_t info = entry ? entry + 0xf0 : 0;
    return info ? sr64(info + 8) : 0;
}

/* SSBU_DEBUG_SCAN=1: once, when entry 1's record exists, every other copy of the pattern in it (HP 999.0 followed by
 * ff bytes, record + 0x40) in committed memory is printed with 0x60 bytes in front of it: finds the structure the record was built from. */
static void scan_copies(uint64_t rec) {
    static const uint8_t pat[16] = {0x00, 0xc0, 0x79, 0x44, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff};
    SYSTEM_INFO si; GetSystemInfo(&si);
    uint8_t *p = (uint8_t *)si.lpMinimumApplicationAddress;
    MEMORY_BASIC_INFORMATION mi;
    int hits = 0;
    while (p < (uint8_t *)si.lpMaximumApplicationAddress && VirtualQuery(p, &mi, sizeof mi)) {
        if (mi.State == MEM_COMMIT && !(mi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) && (mi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE | PAGE_READONLY)) && mi.RegionSize < (1ull << 31)) {
            const uint8_t *b = mi.BaseAddress;
            for (size_t i = 0; i + 16 <= mi.RegionSize; i += 4)
                if (b[i] == 0x00 && b[i + 1] == 0xc0 && memcmp(b + i, pat, 16) == 0 && (uint64_t)(b + i) != rec + 0x40 && hits < 12) {
                    hits++;
                    printf("debug scan: copy at %p (record base would be %p), region %p+%llx\n", b + i, b + i - 0x40, mi.BaseAddress, (unsigned long long)mi.RegionSize);
                    if (i >= 0x40) for (int k = -0x40; k < 0x40; k += 16) {
                        const uint8_t *q = b + i + k;
                        printf("debug scan:   %+04d %02x %02x %02x %02x %02x %02x %02x %02x  %02x %02x %02x %02x %02x %02x %02x %02x\n", k, q[0], q[1], q[2], q[3], q[4], q[5], q[6], q[7], q[8], q[9], q[10], q[11], q[12], q[13], q[14], q[15]);
                    }
                }
        }
        p = (uint8_t *)mi.BaseAddress + mi.RegionSize;
    }
    printf("debug scan: %d copies besides the record itself%c", hits, 10);
    fflush(stdout);
}

/* SSBU_DEBUG_WATCH=1: hardware write watchpoints (debug registers of every thread) on a few fields of the first two
 * records; each write prints the guest functions on the stack of the writer (finds the code that fills the record). */
#include <tlhelp32.h>
/* SSBU_DEBUG_MATCH="p1=<fighter>,p2=<fighter>,stage=<name>,form=omega,cpu=<level>,items=off|on,time=<min>|stock=<n>" */
static struct { int p[4]; char stage[48]; int omega, cpu, items, time, stock; char text[256]; } g_dm = {{-1, -1, -1, -1}, "", 0, -1, -1, -1, -1, ""};
static char g_dm_name[4][32];                          /* track V session 14: p3 / p4 (CPU players added to the panel states the screens left empty) */
static uint8_t g_dm_state1[0x230]; static int g_dm_state1_ok;      /* player 2's panel state as the game's function saw it: the template for p3 / p4 */
static int g_dm_p2type = -1, g_dm_applied, g_dm_fight, g_dm_form = -1, g_dm_hazards = -1;
static FILE *g_dm_list;
static uint64_t fighter_info_record(int id);
static void hex_lines(const char *tag, uint64_t base, int n) {
    for (int k = 0; k < n; k += 16) {
        uint8_t b[16]; if (!safe_read(base + k, b, 16)) break;
        int any = 0; for (int j = 0; j < 16; j++) any |= b[j];
        if (any) printf("%s +%03x %02x %02x %02x %02x %02x %02x %02x %02x  %02x %02x %02x %02x %02x %02x %02x %02x\n", tag, k, b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
    }
}

/* ---- SSBU_DEBUG_END="<n>s" (seconds of fight = n * 60 game frames) or "<n>" (frames): the frames presented while the game's
 * own FighterManager says the fight is on (is_ready_go, impl + 0xd2) are counted (display.c calls debug_mode_frame once per
 * presented frame); at the limit the exit request of a closed window is made (the normal shutdown path). */
int oe_exit_request(void);                             /* nnmisc.c */
static volatile LONG64 g_end_limit, g_end_fight_frames, g_end_fired;
static volatile LONG64 g_end_first_frame;
void debug_mode_frame(long long frame) {
    if (!g_end_limit && !g_dm.text[0]) return;
    uint64_t inst = hook_address("_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E");
    uint64_t mgr = inst ? sr64(inst) : 0, impl = mgr ? sr64(mgr) : 0;
    uint8_t go = 0;
    static int was_go;
    if (!impl || !safe_read(impl + 0xd2, &go, 1) || !go) {
        if (was_go) { printf("debug match: fight off (ready-go cleared) at presented frame %lld after %lld fight frames\n", frame, (long long)g_end_fight_frames); fflush(stdout); }
        was_go = 0; return;
    }
    if (!was_go) { was_go = 1; InterlockedExchange64(&g_end_fight_frames, 0); }
    LONG64 n = InterlockedIncrement64(&g_end_fight_frames);
    if (n == 1) {
        g_end_first_frame = frame;
        uint64_t sel = GA(0x71053097c0ULL) + 0x20ull * (rd32(GA(0x7105309968ULL)) < 3 ? rd32(GA(0x7105309968ULL)) : 0);
        uint64_t r0 = fighter_info_record(0), r1 = fighter_info_record(1);
        printf("debug match: fight %d on (the game's ready-go) at presented frame %lld: p1 kind %d (%s), p2 kind %d (%s) type %d cpu power %d | stage id 0x%x (match block + 0x420), chosen ui_stage %llx form %d (0 normal, 1 battlefield, 2 omega) flags %x/%x (%s) | rule: stock %d, item appear %d; stocks in the fighters' records %d / %d\n", g_dm_fight, frame,
               (int)(int8_t)rd8(GA(0x71052c4738ULL)), g_dm_name[0], (int)(int8_t)rd8(GA(0x71052c4738ULL) + 0x260), g_dm_name[1], rd8(GA(0x71052c4730ULL) + 0x260 + 0xa), rd8(GA(0x71052c4730ULL) + 0x260 + 0x79),
               rd32(GA(0x71052c41b0ULL) + 0x420), (unsigned long long)rd64(sel), rd32(sel + 8), rd32(sel + 0xc), rd8(GA(0x7105309985ULL)), g_dm.stage,
               rd8(GA(0x71052c41b0ULL) + 0x4c), rd32(GA(0x71052c41b0ULL) + 0x54), r0 ? (int)rd32(r0 + 0xd8) : -1, r1 ? (int)rd32(r1 + 0xd8) : -1);
        hex_lines("debug match:   stage block", GA(0x71052c41b0ULL) + 0x420, 0x30);
        if (getenv("SSBU_DEBUG_CTRLSCAN")) {              /* (R3 lwr, research) =<profile>: every copy of that profile's control bytes + 0x24 .. + 0x4c in memory */
            uint64_t x0 = sr64(GA(0x7105313510ULL)), x = x0 ? sr64(x0) : 0, a = x ? sr64(x + 0x58) : 0, b = a ? sr64(a) : 0, q = b ? sr64(b) : 0;
            uint8_t pat[0x10]; uint64_t src = q + 0xf7d8ull * (uint64_t)atoi(getenv("SSBU_DEBUG_CTRLSCAN")) + 0x30;
            if (q && safe_read(src, pat, sizeof pat)) {
                SYSTEM_INFO si; GetSystemInfo(&si); uint8_t *p = (uint8_t *)si.lpMinimumApplicationAddress; MEMORY_BASIC_INFORMATION mi; int hits = 0;
                while (p < (uint8_t *)si.lpMaximumApplicationAddress && VirtualQuery(p, &mi, sizeof mi)) {
                    if (mi.State == MEM_COMMIT && !(mi.Protect & (PAGE_GUARD | PAGE_NOACCESS)) && (mi.Protect & (PAGE_READWRITE | PAGE_EXECUTE_READWRITE)) && mi.RegionSize < (1ull << 31)) {
                        const uint8_t *bb = mi.BaseAddress;
                        for (size_t i = 0; i + sizeof pat <= mi.RegionSize; i++) if (bb[i] == pat[0] && !memcmp(bb + i, pat, sizeof pat) && (uint64_t)(bb + i) != src && hits < 30) { hits++; printf("debug ctrl: scan: copy of the profile's bytes + 0x30 at %p (region %p)\n", bb + i, mi.BaseAddress); hex_lines("debug ctrl: scan:   ", (uint64_t)(bb + i) - 0x40, 0x80); }
                    }
                    p = (uint8_t *)mi.BaseAddress + mi.RegionSize;
                }
                printf("debug ctrl: scan: %d copies (profile bytes at %llx)\n", hits, (unsigned long long)src);
                {   uint64_t base = GA(0x7105338860ULL); for (int s = 0; s < 3; s++) { uint64_t o = sr64(base + 8ull * s); printf("debug ctrl: slot %d obj %llx subs %llx %llx %llx %llx\n", s, (unsigned long long)o, (unsigned long long)sr64(o + 0x10), (unsigned long long)sr64(o + 0x18), (unsigned long long)sr64(o + 0x20), (unsigned long long)sr64(o + 0x28)); } }
                fflush(stdout);
            } }
        if (getenv("SSBU_DEBUG_CTRLWATCH")) hex_lines("debug ctrl: singleton at the fight start", sr64(GA(0x71052c40f0ULL)), 0x120);
        if (getenv("SSBU_DEBUG_P1PROFILE_LATE")) {        /* (R3 lwr, research) =<from>,<to>: profile from's control bytes over profile to's, at the fight start */
            int from = 0, to = 1; sscanf(getenv("SSBU_DEBUG_P1PROFILE_LATE"), "%d,%d", &from, &to);
            uint64_t x0 = sr64(GA(0x7105313510ULL)), x = x0 ? sr64(x0) : 0, a = x ? sr64(x + 0x58) : 0, b = a ? sr64(a) : 0, q = b ? sr64(b) : 0;
            if (q) for (int k = 0x24; k < 0x50; k++) wr8(q + 0xf7d8ull * (uint64_t)to + k, rd8(q + 0xf7d8ull * (uint64_t)from + k));
            printf("debug ctrl: research: at the fight start profile %d's control bytes copied over profile %d\n", from, to); }
        if (getenv("SSBU_DEBUG_MIDUMP")) { hex_lines("debug midump: fight on: FighterManager impl", impl, 0x200); hex_lines("debug midump: fight on: rule", GA(0x71052c41b0ULL), 0x580);      /* (R3 lwr, research) */
            for (int e = 0; e < 2; e++) { char t[64]; uint64_t en = sr64(impl + 0x20 + 8ull * e); snprintf(t, sizeof t, "debug midump: fight on: entry%d", e); hex_lines(t, en, 0x400);
                snprintf(t, sizeof t, "debug midump: fight on: info%d", e); hex_lines(t, fighter_info_record(e), 0x400); } }
        fflush(stdout);
    }
    if (g_end_limit && n >= g_end_limit) InterlockedExchange64(&g_end_fired, frame);
}
static int fighter_kind_by_name(const char *name) {
    char *e; long v = strtol(name, &e, 0);
    if (*name && !*e) return (int)v;
    FILE *f = fopen("..\\reference\\skyline-smash\\src\\lua_const.rs", "r");
    if (!f) return -1;
    char key[96], line[256]; snprintf(key, sizeof key, "FIGHTER_KIND_%s:", name);
    for (char *p = key; *p; p++) if (*p >= 'a' && *p <= 'z') *p -= 32;
    int kind = -1;
    while (fgets(line, sizeof line, f)) { char *s = strstr(line, key); if (s && s - line < 8) { kind = (int)strtol(strchr(s, ':') + 1, NULL, 0); break; } }
    fclose(f);
    return kind;
}
static void parse_match(const char *s) {
    snprintf(g_dm.text, sizeof g_dm.text, "%s", s);
    char buf[256]; snprintf(buf, sizeof buf, "%s", s);
    for (char *t = strtok(buf, ","); t; t = strtok(NULL, ",")) {
        char *eq = strchr(t, '='); if (!eq) continue;
        *eq = 0; const char *k = t, *v = eq + 1;
        if (!strcmp(k, "p1")) { g_dm.p[0] = fighter_kind_by_name(v); snprintf(g_dm_name[0], sizeof g_dm_name[0], "%s", v); }
        else if (!strcmp(k, "p2") && (!strcmp(v, "cpu") || !strcmp(v, "human"))) g_dm_p2type = !strcmp(v, "cpu");
        else if (!strcmp(k, "p2")) { g_dm.p[1] = fighter_kind_by_name(v); snprintf(g_dm_name[1], sizeof g_dm_name[1], "%s", v); }
        else if (!strcmp(k, "p3") || !strcmp(k, "p4")) { int i = k[1] - '1'; g_dm.p[i] = fighter_kind_by_name(v); snprintf(g_dm_name[i], sizeof g_dm_name[i], "%s", v); }
        else if (!strcmp(k, "stage")) snprintf(g_dm.stage, sizeof g_dm.stage, "%s", v);
        else if (!strcmp(k, "form")) { g_dm.omega = !strcmp(v, "omega"); g_dm_form = !strcmp(v, "omega") ? 2 : !strcmp(v, "battlefield") ? 1 : 0; }
        else if (!strcmp(k, "hazards")) g_dm_hazards = !strcmp(v, "on");
        else if (!strcmp(k, "cpu")) g_dm.cpu = atoi(v);
        else if (!strcmp(k, "items")) g_dm.items = !strcmp(v, "on");
        else if (!strcmp(k, "time")) g_dm.time = atoi(v);
        else if (!strcmp(k, "stock")) g_dm.stock = atoi(v);
    }
    printf("debug match: requested %s -> p1 kind %d, p2 kind %d, stage %s%s\n", s, g_dm.p[0], g_dm.p[1], g_dm.stage, g_dm.omega ? " (omega)" : "");
}
static void match_player_hook(Ctx *c, void *user);
static ImportFn g_orig_player_record;
uint64_t g_sq_obj; static ImportFn g_orig_sq_init;
void debug_watch(const uint64_t *addrs, int n);
/* (Battle Arena) the next squad fighter's load at fixed frames. The fighter entry's own loading state machine 0x71006556b0 (entry
 * + 0x5920 = 3 while loading, sub-state + 0x591c 0 .. 0xe, the fighter put in the entry on the way (+ 0x13c -1 until then), + 0x5a80
 * the id of its newest resource request; runs t12 .. t20) runs once per frame, waits for each request of the resource loader and
 * takes the next step at the first frame after it is done: wall time, so the new fighter came in at different frames on the two games
 * of a room (pairs sq1 .. sq16a). In a squad match it is run only every SSBU_SQ_STEP frames (default 20) from the frame its loading
 * begins, and not at that first frame (skipped frames: as if the request were not done yet; it is a poll, nothing is lost), until the
 * fighter is in the entry; the squad object's change of fighter (sq_step_hook below, state 2) the same way. Together with rb_rollback.c
 * (the game waits at every frame boundary until the loader threads are idle, and no frame runs on a guess from the KO until 90 frames
 * after the load) each step finds its request done and the fighter comes in at the same frame on both games: +80 / +100 frames.
 * Offline (t17 .. t20) the steps are the same at every change; pair sq18a IDENTICAL. SSBU_SQ_STEP=0: off. */
static ImportFn g_orig_sq_eload; static long long g_sq_eload_n; static int g_sq_made;
static volatile uint64_t g_sq_load_en;      /* (the entry the loading hook last saw loading) */
int dm_squad_entry_loading(void) {                  /* (rb_rollback.c) a fighter entry is loading its next fighter (entry + 0x5920 == 3) */
    static uint64_t inst; if (!inst) inst = hook_address("_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E");
    uint64_t mgr = inst ? sr64(inst) : 0, impl = mgr ? sr64(mgr) : 0;
    for (int e = 0; impl && e < 2; e++) { uint64_t en = sr64(impl + 0x20 + 8ull * (uint64_t)e); if (en && rd8(en + 0x5920) == 3) return 1; }
    /* (Battle Arena, game's squad, run cssm10) the game's Squad Strike also loads a side's next fighter ahead, in an entry that is not
     * one of these two (no KO, no lock: rollbacks across that load, DESYNC 9 frames after it): the entry the loading hook last saw
     * loading counts too (SSBU_SQ_LOADEN=0: off) */
    {   static int on = -1; if (on < 0) { const char *e = getenv("SSBU_SQ_LOADEN"); on = !(e && e[0] == '0'); }
        uint8_t b = 0; if (on && g_sq_load_en && safe_read(g_sq_load_en + 0x5920, &b, 1) && b == 3) return 1; }
    return 0;
}
static void sq_eload_hook(Ctx *c, void *user) {
    (void)user;
    static int step = -1; static int64_t base = -1; static uint64_t base_en;
    /* (Battle Arena, game's squad, run cssn4) the room's Squad Strike loads BOTH sides' next fighters at the same time: with one base the
     * two entries reset it in turn and neither ever stepped (loading stuck at sub-state 05 / 0a, no guesses for the rest of the match). Now
     * one base per entry (SSBU_SQ_EBASE=0: the single base, the old way). */
    static int per = -1; static struct { uint64_t en; int64_t base; } eb[4]; if (per < 0) { const char *e = getenv("SSBU_SQ_EBASE"); per = !(e && e[0] == '0'); }
    uint64_t en0 = c->x[0]; int ek = -1;
    if (per && en0) { for (int i = 0; i < 4; i++) if (eb[i].en == en0) ek = i; if (ek < 0) for (int i = 0; i < 4; i++) if (!eb[i].en) { ek = i; eb[i].en = en0; eb[i].base = -1; break; } if (ek < 0) { ek = 0; eb[0].en = en0; eb[0].base = -1; }
        base = eb[ek].base; base_en = en0; }
    if (step < 0) { const char *e = getenv("SSBU_SQ_STEP"); step = e && *e ? atoi(e) : 20; }
    uint64_t en = c->x[0], o = g_sq_obj;
    int64_t fc = o ? (int64_t)rd32(o + 0x112c) : 0;
    int loading = en && rd8(en + 0x5920) == 3, waiting = en && en == base_en && base >= 0 && rd32(en + 0x13c) == 0xffffffffu && fc - base < 1200;
    /* (research, SSBU_SQ_GATE01=1 with SSBU_BA_SQUAD_CSS=1; off by default) after "loaded" the entry waits at + 0x5920 = 1, then 6. Stepping
     * that wait did not help (run cssr2: the 1 -> 6 of a fighter loaded ahead is written outside this function, frame 953 on A and 954 on B),
     * and putting a 6 back to 1 until a step frame broke the game (cssr3: crash at the entry's next load; cssr4: 1 / 6 flip every frame) */
    {   static int g01 = -1; if (g01 < 0) { const char *e = getenv("SSBU_BA_SQUAD_CSS"), *k = getenv("SSBU_SQ_GATE01"); g01 = e && e[0] == '1' && k && k[0] == '1'; }
        if (g01 && en && rd8(en + 0x5920) == 1 && en == base_en && base >= 0 && fc - base < 1200) waiting = 1; }
    /* (Battle Arena, the game's own Squad Strike, SSBU_BA_SQUAD_CSS=1; SSBU_SQ_HOLD6=0 off) a fighter loaded ahead (no KO) goes from + 0x5920
     * = 1 to 6 when its module is there: at once on a game that still had the module, a frame later on one that loaded it (L2CDynamicModule-
     * Manager): DESYNC (cssn1 frame 1497, cssr1 1775, cssr2 954, cssr7 383). Outside a change of fighter, a 6 seen on a frame that is not
     * a step frame of the entry's loading is put back to 1 (once per load) and written again at its next step frame, the same frame on both
     * games. (cssr3: never written again: crash at the entry's next load; cssr4: put back again and again after the step frame.) */
    {   static int h6 = -1; if (h6 < 0) { const char *e = getenv("SSBU_BA_SQUAD_CSS"), *k = getenv("SSBU_SQ_HOLD6"); h6 = e && e[0] == '1' && !(k && k[0] == '0'); }
        static uint64_t s1[4]; static int pend[4];
        if (h6 && ek >= 0 && en && step > 0) {
            int chg = 0; uint8_t v = rd8(en + 0x5920);
            if (g_sq_obj) for (int k = 0; k < 3; k++) { uint32_t ps = rd32(g_sq_obj + (k == 0 ? 0x5b0 : k == 1 ? 0x950 : 0xd10) + 0x60); if (ps >= 2 && ps <= 4) chg = 1; }
            if (v == 3) { s1[ek] = 0; pend[ek] = 0; }
            if (v == 1 && base >= 0 && !pend[ek] && s1[ek] != en) s1[ek] = en;      /* loaded, waiting: armed for this load */
            if (pend[ek] && v == 1 && !((fc - base) % step) && fc != base) {
                wr8(en + 0x5920, 6); pend[ek] = 0; s1[ek] = 0; v = 6;
                printf("[squad] entry %llx: + 0x5920 6 written at fight frame %lld (step frame of its loading)\n", (unsigned long long)en, (long long)fc); fflush(stdout);
            } else if (s1[ek] == en && v == 6 && !chg && base >= 0 && ((fc - base) % step || fc == base)) {
                wr8(en + 0x5920, 1); pend[ek] = 1; s1[ek] = 0; v = 1;
                printf("[squad] entry %llx: + 0x5920 6 at fight frame %lld (not a step frame): kept at 1 until the next step frame\n", (unsigned long long)en, (long long)fc); fflush(stdout);
            } else if (s1[ek] == en && v == 6) s1[ek] = 0;
            if (s1[ek] == en && base >= 0 && fc - base >= 1200) s1[ek] = 0;
            if (pend[ek] || s1[ek] == en) waiting = 1;      /* (the base is kept while it waits) */
        } }
    if (loading) g_sq_load_en = en;
    {   static int lg = -1; if (lg < 0) lg = getenv("SSBU_SQ_ELOG") != NULL;      /* (research) every change of a loading entry's states, real and re-run passes */
        static uint64_t last_en; static uint32_t last_v;
        if (lg && en && en == g_sq_load_en) { long long rbn_now(void); uint32_t v = rd8(en + 0x5920) | (uint32_t)rd8(en + 0x591c) << 8 | (rd32(en + 0x13c) == 0xffffffffu ? 0x10000u : 0) | (uint32_t)rd8(en + 0x5921) << 24;
            if (en != last_en || v != last_v) { printf("[squad] entry %llx at frame %lld (fight %lld): + 0x5920 %02x + 0x591c %02x + 0x5921 %02x%s\n", (unsigned long long)en, rbn_now(), (long long)fc, v & 255, v >> 8 & 255, v >> 24, v & 0x10000 ? " (not in the entry)" : ""); fflush(stdout); last_en = en; last_v = v; } } }
    if (step <= 0 || !g_sq_made || !en || !o || !(loading || waiting)) { if (en == base_en) base = -1; if (ek >= 0) eb[ek].base = base; g_orig_sq_eload(c); return; }
    if (base < 0 || en != base_en || base > fc) { base = fc; base_en = en; }
    if (ek >= 0) eb[ek].base = base;
    if ((fc - base) % step || fc == base) { g_sq_eload_n++; return; }      /* (the first step one period in: the frames before it run without guesses, rb_rollback.c rbg_squad_lock) */      /* (not a step frame: the entry's loading waits, as if its request were not done) */
    uint8_t sub0 = rd8(en + 0x591c);
    g_orig_sq_eload(c);
    printf("[squad] next fighter's loading: step at fight frame %lld (+%lld): sub-state %02x -> %02x%s%s\n", (long long)fc, (long long)(fc - base), sub0, rd8(en + 0x591c), rd8(en + 0x5920) != 3 ? " (loaded)" : rd8(en + 0x591c) == sub0 ? " (NOT advanced: its request not done in time)" : "", rd32(en + 0x13c) != 0xffffffffu ? " (fighter in the entry)" : "");
    fflush(stdout);
}
static void sq_init_hook(Ctx *c, void *user) { (void)user; g_sq_obj = c->x[0]; g_orig_sq_init(c);}
/* (RESEARCH, SSBU_SQ_MODLOG=1) the fighter loader's modules: 0x71017e4940 (loader, kind) takes the kind's lua2cpp module from the
 * L2CDynamicModuleManager ([0x7105326cd0]: map at + 0x40 by hash, module + 0x132 loaded, + 0x133 being dropped, + 0x134 count; its own
 * thread loads / unloads: wall time); 0x71017e4790 = that loader's "ready" (vtable 0x710509eb10 slot 6: every module loaded, then the
 * base's 0x71017e28c0); 0x71022b6f60 drops one. Logged with the rollback frame and the fight frame. */
static ImportFn g_orig_mod_take, g_orig_mod_ready, g_orig_mod_drop;
static long long sq_fc(void) { return g_sq_obj ? (long long)rd32(g_sq_obj + 0x112c) : -1; }
/* (Battle Arena, game's squad, SSBU_BA_SQUAD_CSS=1; SSBU_SQ_READYGATE=0 off) a loader that takes a module DURING the fight (a next fighter
 * loaded ahead) becomes ready when the module manager's own thread is done: wall time (run csss1: ready at frame 2341 on A, 2340 on B,
 * asked every frame by 0x7100650010, which the squad code runs before the entry's loading for an entry at + 0x5920 = 1; run cssh5:
 * the fighter standing by began a frame apart, DESYNC 513). Such a loader answers "ready" only from a fixed frame on: counted from
 * the first frame it is asked, at the first step frame (SSBU_SQ_STEP) the game waits here until it really is ready (at most 5 s), and
 * that frame is kept, so re-run passes give the same answers. */
static int g_rgate = -1;
static struct { uint64_t o; long long base, at; } g_rg[16]; static int g_rg_n; static long long g_rg_last = -1;
static void mod_take_hook(Ctx *c, void *user) {
    (void)user; long long rbn_now(void); uint64_t o = c->x[0], ra = c->x[30]; int kind = (int)c->x[1];
    g_orig_mod_take(c);
    if (g_rgate > 0 && g_sq_made && sq_fc() > 0) { int k = 0; while (k < 16 && g_rg[k].o != o) k++;
        if (k == 16) { k = g_rg_n++ & 15; g_rg[k].o = o; g_rg[k].base = -1; g_rg[k].at = -1; } }
    if (getenv("SSBU_SQ_MODLOG")) printf("[squad] modlog: loader %llx takes the module of kind %d at frame %lld (fight %lld), caller %llx\n", (unsigned long long)o, kind, rbn_now(), sq_fc(), (unsigned long long)(ra - 4)); fflush(stdout);
}
static void mod_drop_hook(Ctx *c, void *user) {
    (void)user; long long rbn_now(void); uint64_t ra = c->x[30], m = sr64(c->x[1]);
    g_orig_mod_drop(c);
    printf("[squad] modlog: module key %llx dropped at frame %lld (fight %lld), caller %llx\n", (unsigned long long)m, rbn_now(), sq_fc(), (unsigned long long)(ra - 4)); fflush(stdout);
}
static void mod_ready_hook(Ctx *c, void *user) {
    (void)user; long long rbn_now(void); uint64_t o = c->x[0], ra = c->x[30];
    uint64_t save[32]; memcpy(save, c->x, sizeof save);
    g_orig_mod_ready(c);
    if (g_rgate > 0 && g_sq_made) {
        long long fc = sq_fc(); if (fc >= 0 && fc < g_rg_last) memset(g_rg, 0, sizeof g_rg); if (fc >= 0) g_rg_last = fc;      /* (a new fight) */
        int k = 0; while (k < 16 && (!g_rg[k].o || g_rg[k].o != o)) k++;
        if (k < 16 && fc > 0) {
            static int step = -1; if (step < 0) { const char *e = getenv("SSBU_SQ_STEP"); step = e && *e ? atoi(e) : 20; if (step <= 0) step = 20; }
            int r = (int)(c->x[0] & 1);
            if (g_rg[k].at >= 0) { if (fc < g_rg[k].at) c->x[0] = 0; }
            else {
                if (g_rg[k].base < 0 || g_rg[k].base > fc) g_rg[k].base = fc;
                if ((fc - g_rg[k].base) % step || fc == g_rg[k].base) c->x[0] = 0;
                else {
                    ULONGLONG t0 = GetTickCount64();
                    while (!r && GetTickCount64() - t0 < 5000) { Sleep(1); memcpy(c->x, save, sizeof save); g_orig_mod_ready(c); r = (int)(c->x[0] & 1); }
                    g_rg[k].at = fc;
                    printf("[squad] loader %llx (a module taken in the fight): ready from fight frame %lld (asked from %lld; waited %llu ms%s)\n", (unsigned long long)o, fc, g_rg[k].base, (unsigned long long)(GetTickCount64() - t0), r ? "" : ", NOT READY after 5 s"); fflush(stdout);
                }
            }
        }
    }
    if (!getenv("SSBU_SQ_MODLOG")) return;
    static struct { uint64_t o; int r; } t[16]; int k = 0; while (k < 16 && t[k].o && t[k].o != o) k++; if (k == 16) k = 0;
    int r = (int)(c->x[0] & 1);
    if (t[k].o != o || t[k].r != r) { printf("[squad] modlog: loader %llx ready %d at frame %lld (fight %lld), caller %llx\n", (unsigned long long)o, r, rbn_now(), sq_fc(), (unsigned long long)(ra - 4)); fflush(stdout); t[k].o = o; t[k].r = r; }
}
static int g_sq_made;
/* ---- (Battle Arena) the next squad fighter's entry at a fixed frame (Squad Strike, Tag Team, online with rollback).
 * The game's squad object of the fight (made by 0x710157c100, which g_sq_obj is taken from) runs the change of fighter as a state
 * machine at + 0xd70 (13.0.4, runs t8 / t9: hardware write watch): at the KO + 0xd08 = 60 and + 0xd04 counts the frames to it; then
 * state 2 = the next fighter is being loaded (its module and files, on the loader threads: wall time); 2 -> 3 is written by
 * 0x7101589830 (a per-frame callback of the fight, 0x7101755fd0 <- 0x710135f570) when the load is done; 3 counts + 0xd74 to 4,
 * 0x710158a360 then 4 -> 0 (the fighter is in). + 0x112c counts the fight frames. Offline state 2 lasted 30 frames; the two games of a
 * room needed different numbers of frames (the desyncs of pairs sq1 .. sq6a).
 * Here, in a squad match, state 2 is polled (0x7101589830 run) only every SSBU_SQ_STEP frames from its first frame (see the entry's
 * loading below): 2 -> 3 then comes at the same frame on both games (+100 in pair sq18a). Earlier tries, kept in the notes: waiting
 * inside the frame (t10: the load needs the game's frames), undoing an early 2 -> 3 (sq9a: the request sometimes lost).
 * SSBU_SQ_GATE=0: off (test). */
static ImportFn g_orig_sq_step; static int64_t g_sq_s2_from = -1; static long long g_sq_gate_held, g_sq_gate_waits; static double g_sq_gate_ms;
/* the change of fighter of each side: x0 = one of the squad object's per-side parts (+ 0x5b0, + 0x950, + 0xd10; run t21), its state at
 * x0 + 0x60 (the + 0xd70 above for + 0xd10); 0x7101589830 runs states 2 and 3, 0x710158a360 state 4 (the fighter put in): in states 2..4
 * both run only every SSBU_SQ_STEP frames from the first frame of state 2 (pair sq18b: state 4 also waits for a load) */
static ImportFn g_orig_sq_put; static struct { uint64_t part; int64_t from; } g_sq_base[4];
static int sq_gate(Ctx *c, const char *what) {      /* 1 = run the step now */
    static int on = -1, step = 20;
    if (on < 0) { const char *e = getenv("SSBU_SQ_GATE"), *k = getenv("SSBU_SQ_STEP"); on = !(e && e[0] == '0'); if (k && *k) step = atoi(k); }
    uint64_t o = g_sq_obj, part = c->x[0];
    if (!on || step <= 0 || !g_sq_made || !o || part < o || part > o + 0x1000) return 1;
    uint32_t st = rd32(part + 0x60); int64_t fc = (int64_t)rd32(o + 0x112c);
    int k = 0; while (k < 3 && g_sq_base[k].part && g_sq_base[k].part != part) k++;
    g_sq_base[k].part = part;
    if (st < 2 || st > 4) { g_sq_base[k].from = -1; return 1; }
    if (g_sq_base[k].from < 0 || g_sq_base[k].from > fc) g_sq_base[k].from = fc;
    int64_t from = g_sq_base[k].from;
    if ((fc - from) % step || fc == from) { g_sq_gate_held++; return 0; }
    (void)what; return 1;
}
static void sq_step_hook(Ctx *c, void *user) {
    (void)user; uint64_t part = c->x[0]; uint32_t st0 = g_sq_obj && part >= g_sq_obj && part <= g_sq_obj + 0x1000 ? rd32(part + 0x60) : 0;
    if (!sq_gate(c, "step")) return;
    g_orig_sq_step(c);
    if (getenv("SSBU_SQ_ELOG") && g_sq_obj && part >= g_sq_obj && part <= g_sq_obj + 0x1000 && rd32(part + 0x60) != st0) { long long rbn_now(void); printf("[squad] part +%llx state %u -> %u at frame %lld (fight %u)\n", (unsigned long long)(part - g_sq_obj), st0, rd32(part + 0x60), rbn_now(), rd32(g_sq_obj + 0x112c)); fflush(stdout); }
    if (st0 >= 2 && st0 <= 4 && rd32(part + 0x60) != st0) { printf("[squad] change of fighter (part +%llx): state %u -> %u at fight frame %u\n", (unsigned long long)(part - g_sq_obj), st0, rd32(part + 0x60), rd32(g_sq_obj + 0x112c)); fflush(stdout); }
}
static void sq_put_hook(Ctx *c, void *user) {
    (void)user; uint64_t part = c->x[0]; uint32_t st0 = g_sq_obj && part >= g_sq_obj && part <= g_sq_obj + 0x1000 ? rd32(part + 0x60) : 0;
    if (!sq_gate(c, "put")) return;
    g_orig_sq_put(c);
    if (st0 >= 2 && st0 <= 4 && rd32(part + 0x60) != st0) { printf("[squad] change of fighter (part +%llx): state %u -> %u at fight frame %u\n", (unsigned long long)(part - g_sq_obj), st0, rd32(part + 0x60), rd32(g_sq_obj + 0x112c)); fflush(stdout); }
}
static volatile LONG g_dm_setups;                      /* match set-ups seen (player 1's record built) */
#ifdef _WIN32
static LONG CALLBACK watch_veh(EXCEPTION_POINTERS *ep) {
    static volatile LONG shown;
    if (ep->ExceptionRecord->ExceptionCode != EXCEPTION_SINGLE_STEP) return EXCEPTION_CONTINUE_SEARCH;
    CONTEXT *cx = ep->ContextRecord;
    if (!(cx->Dr6 & 0xf)) return EXCEPTION_CONTINUE_SEARCH;
    int which = 0; while (!((cx->Dr6 >> which) & 1)) which++;
    uint64_t addr = (&cx->Dr0)[which];
    /* Tried and NOT working: overwriting the record's fighter field (record + 0x30 = kind + 1) here changes nothing in the
     * fight (run m1: record said 6 and 9, the fighters spawned were still kinds 0 and 8): the record is a copy made from
     * the select-screen data, not the source. */
    if (InterlockedIncrement(&shown) <= 160) {
        char line[1200]; int n = snprintf(line, sizeof line, "debug watch: write to %llx (reg %d) now %016llx by thread %lu, guest stack:", (unsigned long long)addr, which, (unsigned long long)*(uint64_t *)(uintptr_t)(addr & ~7ull), GetCurrentThreadId());
        CONTEXT c2 = *cx; uint64_t last = 0;
        for (int depth = 0, sh = 0; depth < 400 && sh < 14; depth++) {
            uint64_t g = guest_function_of(c2.Rip);
            if (g && g != last) { n += snprintf(line + n, sizeof line - n, " f_%llx", (unsigned long long)g); sh++; last = g; }
            DWORD64 ib; RUNTIME_FUNCTION *rf = RtlLookupFunctionEntry(c2.Rip, &ib, NULL);
            if (!rf) break;
            void *hd; DWORD64 est;
            RtlVirtualUnwind(UNW_FLAG_NHANDLER, ib, c2.Rip, rf, &c2, &hd, &est, NULL);
            if (!c2.Rip) break;
        }
        {   /* the guest's own frame chain: one of the host registers holds the Ctx of this thread (x29 = frame pointer, x30 = link) */
            const DWORD64 *regs = &cx->Rax; int found = 0;
            for (int r = 0; r < 16 && !found; r++) {
                uint64_t p = regs[r], x[32];
                if (r == 4 || !safe_read(p, x, sizeof x)) continue;
                uint64_t fp = x[29], sp = x[31], pair[2];
                if (fp < sp || fp - sp > 0x100000 || (x[30] >> 32) < 0x71 || (x[30] >> 32) > 0x72 || !safe_read(fp, pair, 16) || (pair[1] >> 32) < 0x71 || (pair[1] >> 32) > 0x72) continue;
                found = 1;
                n += snprintf(line + n, sizeof line - n, " | guest pc near lr %llx, frames:", (unsigned long long)x[30]);
                for (int d = 0; d < 10 && safe_read(fp, pair, 16) && (pair[1] >> 32) >= 0x71 && (pair[1] >> 32) <= 0x72; d++) { n += snprintf(line + n, sizeof line - n, " %llx", (unsigned long long)pair[1]); if (pair[0] <= fp) break; fp = pair[0]; }
            }
        }
        printf("%s\n", line); fflush(stdout);
    }
    cx->Dr6 = 0;
    return EXCEPTION_CONTINUE_EXECUTION;
}
static void watch_set(const uint64_t *addrs, int n) {
    static int veh;
    if (!veh) { veh = 1; AddVectoredExceptionHandler(1, watch_veh); }
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
    THREADENTRY32 te; te.dwSize = sizeof te;
    int set = 0;
    if (snap != INVALID_HANDLE_VALUE && Thread32First(snap, &te)) do {
        if (te.th32OwnerProcessID != GetCurrentProcessId() || te.th32ThreadID == GetCurrentThreadId()) continue;
        HANDLE th = OpenThread(THREAD_ALL_ACCESS, FALSE, te.th32ThreadID);
        if (!th) continue;
        SuspendThread(th);
        CONTEXT cx; memset(&cx, 0, sizeof cx); cx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (GetThreadContext(th, &cx)) {
            DWORD64 *dr = &cx.Dr0; DWORD64 d7 = 0;
            for (int i = 0; i < n && i < 4; i++) { dr[i] = addrs[i]; d7 |= (1ull << (2 * i)) | (1ull << (16 + 4 * i)) | (3ull << (18 + 4 * i)); }
            cx.Dr7 = d7;
            if (SetThreadContext(th, &cx)) set++;
        }
        ResumeThread(th); CloseHandle(th);
    } while (Thread32Next(snap, &te));
    CloseHandle(snap);
    printf("debug watch: %d watchpoints on %d threads\n", n, set); fflush(stdout);
}
#else
static void watch_set(const uint64_t *addrs, int n) {      /* Linux build: no debug registers from user space without ptrace */
    (void)addrs;
    printf("debug watch: %d watchpoints requested: not available in the Linux build\n", n); fflush(stdout);
}
#endif

/* research (R3, SSBU_RB_NET_WATCH=1 only): write watchpoints on up to 4 addresses from another file (fighter_host.c) */
static uint64_t g_dw_a[4]; static int g_dw_n;
static DWORD WINAPI dw_thread(LPVOID u) { (void)u; watch_set(g_dw_a, g_dw_n); return 0; }
void debug_watch(const uint64_t *addrs, int n) {        /* from a helper thread, so that the calling thread is watched too */
    g_dw_n = n > 4 ? 4 : n; memcpy(g_dw_a, addrs, sizeof(uint64_t) * (size_t)g_dw_n);
    HANDLE t = CreateThread(NULL, 0, dw_thread, NULL, 0, NULL); if (t) { WaitForSingleObject(t, 5000); CloseHandle(t); }
}
int hook_install(uint64_t addr, void (*repl)(Ctx *, void *), void *user, ImportFn *orig);

/* ---- the match set-up (from the game's own binary; see tests\DEBUG_MODE.md) */
#define MELEE_INFO   GA(0x71052c41b0ULL)                 /* the game's global match block (rule at +0, players at +0x588, 0x260 each) */
#define MI_PLAYER(i) (MELEE_INFO + 0x588 + 0x260ull * (uint64_t)(i))
#define FN_ENTRY_SETUP GA(0x7100653240ULL)               /* (FighterEntry *, init parameter *, flags *): copies 0x70 bytes to entry + 0x70 */
static ImportFn g_orig_entry_setup;
static void research_entry_setup(Ctx *c, void *user) {
    (void)user;
    printf("debug research: entry set-up: entry %llx, parameter %llx, called from %llx\n", (unsigned long long)c->x[0], (unsigned long long)c->x[1], (unsigned long long)c->x[30]);
    hex_lines("debug research:   param", c->x[1], 0x70);
    hex_lines("debug research:   rule ", MELEE_INFO, 0x588);
    hex_lines("debug research:   pl 0 ", MI_PLAYER(0), 0x260);
    hex_lines("debug research:   pl 1 ", MI_PLAYER(1), 0x260);
    fflush(stdout);
    g_orig_entry_setup(c);
}

/* ---- SSBU_DEBUG_MATCH, route A: the function that turns one player's choice on the select screens (a panel state) into
 * that player's record of the global match block. x0 = panel state, x1 = player index, x3 = record (MELEE_INFO + 0x580 + 0x260 * i).
 * State: +0x78 player type (0 human, 1 CPU, 2 amiibo, 3 none), +0x88 the ui_chara id (0xc1 << 56 | hash40("ui_chara_<name>")),
 * +0x90 colour, +0x224 CPU level. Record: +0 ui_chara id, +8 fighter kind, +0xa type, +0x19 colour, +0x79 CPU level.
 * The choice is replaced in the STATE before the game's own function runs, so everything it derives is the game's. */
#define FN_PLAYER_RECORD GA(0x7101788260ULL)
static uint64_t hash40(const char *s) {
    uint32_t crc = 0xffffffffu; size_t n = strlen(s);
    for (size_t i = 0; i < n; i++) { crc ^= (uint8_t)s[i]; for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1))); }
    return ((uint64_t)n << 32) | (uint32_t)~crc;
}
#define SQ_TABLE GA(0x71052c9dc0ULL)
#define SQ_N 3
static uint64_t g_sq_chara[2][SQ_N]; static uint8_t g_sq_colour[2][SQ_N]; static int g_sq_want, g_sq_test = -1;
static uint64_t g_sq_st[2], g_sq_x2[2]; static int g_sq_made;
/* each player's panel state as it was at its own record (after hs_apply): in a Local Wireless room both records are built from
 * the SAME state object, so at player 2's record the pointer of player 1 already holds player 2 (2026-11-07 room match: player 1's
 * fighter 1 was player 2's fighter, colour and controller slot, so it copied player 2's inputs). SSBU_SQ_STCOPY=0: the old way */
static uint8_t g_sq_copy[2][0x480] __attribute__((aligned(16))); static int g_sq_copy_ok[2], g_sq_stcopy = -1;
/* ---- R3 online rollback (SSBU_RB_NET=net, rb_net.c): the match handshake. At player 1's record each game sends its own
 * player-1 choice (the panel state as the screens and SSBU_DEBUG_MATCH left it); the host adds its stage / items / stocks
 * and a seed; both then write the agreed match into the panel states of players 1 and 2 (type human: port 2 = the other
 * game) and the stage choice, before the game's own function builds the records. Fields read in the record builder
 * 0x7101788260 (Ghidra, 13.0.4): state + 0x90 colour -> record + 0x19 / + 0x1b; the control layout -> record + 0x28 ..
 * + 0x3d comes from state + 0x54 .. + 0x69 when state + 0x454 is set (or + 0x455 with type 0), else for a human from the
 * name-tag profile state + 0x214 (table [[[[0x7105313510] + 0x58]]] + 0xf7d8 * profile + 0xc, default 0x7105313518).
 * Both games force + 0x454 = 1 with the agreed layout of each player, so a player's own controls are the same on both PCs. */
#include "rb_hs.h"
static int g_hs_wanted, g_hs_ok, g_hs_local, g_hs_lwr;
/* (Battle Arena, SSBU_BA_SQUAD_CSS=1) the room plays the game's own Squad Strike: the squad is built by the game (0x7102311f00..:
 * the record builder 5 times per side, x1 = side, w2 = side + 2 * fighter, the record = the squad table entry). g_tc_native: this
 * record is one of those; g_tc_k its fighter. Only fighter 1 of a side runs hs_apply (handshake at side 0, LAYOUT at side 1);
 * the others get the same room normalization (tc_norm) with the layout kept here. */
static int g_bacss = -1, g_tc_native, g_tc_k; static uint16_t g_tc_lay[2][11];
/* (0.2.15) a Local Wireless room's match is played as a local versus match (melee mode 1 -> 0, each player a local human on
 * port i): after the fight the game went on as after a LOCAL match (the local character select, out of the room: "the game
 * does not go back to the room"), and the joiner's own player still read port 2 (its pad is port 1) on the result screen.
 * When the fight ends (rb_rollback.c rbg_fight_off) the room's mode and both players' panel states are given back. */
static int g_room_mode_was = -1, g_room_saved_n;
static struct { uint64_t st; uint8_t b450[6]; uint32_t slot, prof; uint8_t lay[22]; uint8_t a4; } g_room_saved[2];      /* (R3 lwr) g_hs_lwr: a Local Wireless room's match played with rollback */
static int g_hs_quick;
/* (Arena, online matchmaking) a quick match from the Arena menu's Online Ranked tile (competitive.c comp_quickplay): both games
 * play the same locked rules whatever the player's own ruleset: stock (rule kind 1), 3 stocks, no items, no hazards (the
 * competitive mode keeps the FS meter, the item switch, hazards and stage morph off already). Written into this game's
 * choice before the handshake and into BOTH players' panel states, so the two games' states and match blocks agree (no
 * "rule kind here differs"). The time limit comes from the rule struct copies competitive.c forces while the flag is on. */
/* (Arena, fighter_sdk/replay_state.c) the kind of the match set up last: 0 a local one, 1 matchmaking (a quick match from the
 * lobby's search), 2 a Local Wireless room over the relay, 3 another online match (host / join) */
/* (0.2.17) the fight scene's clean-up 0x710134c690 (when the result screen is over): for a room's mode it uses the object at
 * 0x71052c3d08, which the scene's set-up makes only in such a mode; the set-up saw the local mode (below), so there is none and
 * the game crashed reading it (x8 null, + 8). The clean-up of a room's match that was played as a local one runs as a local one. */
static ImportFn g_orig_scene_clean;
static volatile int g_room_clean;
static void scene_clean_hook(Ctx *c, void *user) {
    (void)user;
    if (!g_room_clean) { g_orig_scene_clean(c); return; }
    g_room_clean = 0;
    uint8_t mode = rd8(MELEE_INFO);
    wr8(MELEE_INFO, 0);
    g_orig_scene_clean(c);
    wr8(MELEE_INFO, mode);
    printf("[rbnet] room match: the fight scene's clean-up ran as a local match's (melee mode %u kept for what follows)\n", mode); fflush(stdout);
}
static void bacss_after_match(void);
void dm_room_restore(void) {
    if (g_room_mode_was < 0) return;
    bacss_after_match();
    if (g_bacss == 1) g_room_saved_n = 0;      /* (Battle Arena, game's squad: see below) */
    printf("[rbnet] room match over: melee mode %u -> %d (the room's), %d player panel state(s) given back\n", rd8(MELEE_INFO), g_room_mode_was, g_room_saved_n);
    wr8(MELEE_INFO, (uint8_t)g_room_mode_was);
    g_room_clean = 1;
    /* (Battle Arena, game's squad, 2026-11-09) the room's records are built from ONE panel state on the stack of the room's record
     * constructor (0x710178f3ec / 0x710178f488: x0 = sp + 0xa8, filled by 0x7101787830 from the screens' table for each player): it is
     * gone when the fight ends, so nothing is written back there (it would land on whatever stack frame is there now) */
    for (int j = 0; j < g_room_saved_n; j++) { int i = g_bacss == 1 ? g_room_saved_n - 1 - j : j;      /* (Battle Arena, game's squad: backwards, when both records come from one panel state the first one saved is the room's own) */
        uint64_t st = g_room_saved[i].st; if (!st) continue;
        for (int k = 0; k < 6; k++) wr8(st + 0x450 + (uint64_t)k, g_room_saved[i].b450[k]);
        wr32(st + 0x210, g_room_saved[i].slot); wr32(st + 0x214, g_room_saved[i].prof);
        for (int k = 0; k < 22; k++) wr8(st + 0x54 + (uint64_t)k, g_room_saved[i].lay[k]);
        wr8(st + 0xa4, g_room_saved[i].a4);      /* (Battle Arena, game's squad: the player number SSBU_BA_SQUAD_PNUM changed) */
    }
    fflush(stdout);
    g_room_mode_was = -1; g_room_saved_n = 0;
}
int dm_match_kind(void) { return !g_hs_wanted || !g_hs_ok || g_hs_local ? 0 : g_hs_lwr ? 2 : g_hs_quick ? 1 : 3; }
#define QUICK_RULE_KIND 1
#define QUICK_STOCKS 3
static RbhMatch g_hs_m;
/* (hs12) player 1's panel state as the game's function got it (before any change here): the template of player 2's state
 * when the select screen had NO panel for player 2 (each player picks only his own fighter; the other game's player is
 * player 2 / 1). Fields read off two panel states (run pd1: player 1 human, player 2 CPU, both built by 0x7101787830 from
 * the screens' per-player table 0x7105307770 + 0x1c8 * i): + 0x70 the player's number (1, 2), + 0x78 type, + 0x7c team,
 * + 0x80 / + 0x88 ui_chara, + 0x90 colour, + 0x91 CPU level, + 0x210 / + 0x214 name-tag fields (player 1: 0 / 1, the CPU:
 * ffffffff / 0), + 0x298 the number again (the second copy of the table entry), + 0x450 "human with a name tag" (1 / 0). */
static uint8_t g_hs_p1[0x470]; static int g_hs_p1_ok;
static void hs_fill_empty(uint64_t st, int idx) {
    for (int k = 0; k < (int)sizeof g_hs_p1; k++) wr8(st + (uint64_t)k, g_hs_p1[k]);
    wr32(st + 0x70, (uint32_t)idx + 1); if (rd32(st + 0x298) == 1) wr32(st + 0x298, (uint32_t)idx + 1);
    wr32(st + 0x7c, 0); wr32(st + 0x210, 0xffffffffu); wr32(st + 0x214, 0); wr8(st + 0x450, 0);
}
/* (R3 lwr) a name-tag profile (0xf7d8 bytes each, 0x3d of them): as the record builder 0x7101788260 finds it (0x71017884 5c..80):
 * [[0x7105313510]] = the save's root (byte + 0 must be 0), [[[root + 0x58]]] + 0xf7d8 * index; a profile whose byte + 0 is 0
 * (not in use) or index -1 / >= 0x3d -> the game's default profile 0x7105313518. Read in runs lwc1v / lwc2v (SSBU_DEBUG_CTRLDUMP):
 * + 0 in use, + 0xc the name (UTF-16), + 0x24 .. + 0x4e the controls (three controller kinds' button maps and options; the
 * builder reads the kind's last byte + 0x2f / + 0x3f / + 0x4b into record + 0x48). (hs12 read one pointer too few: it always
 * got the default profile.) */
static uint64_t prof_ptr(uint32_t prof) {
    uint64_t def = GA(0x7105313518ULL);
    if (prof >= 0x3d) return def;
    uint64_t x0 = sr64(GA(0x7105313510ULL)), x = x0 ? sr64(x0) : 0;
    if (!x || rd8(x) != 0) return def;
    uint64_t a = sr64(x + 0x58), b = a ? sr64(a) : 0, q = b ? sr64(b) : 0;
    uint64_t p = q ? q + 0xf7d8ull * prof : 0;
    return p && rd8(p) ? p : def;
}
/* (R3 lwr, owner 2026-10-22: each player plays with HIS OWN name-tag controls) the controls a match uses: the set-up
 * 0x7101780440 (after the records, from 0x710155ed20) copies, for each human player i, its profile's + 0x24 .. + 0x4e (record
 * + 0x40 = the profile) into the controls object [0x71052c40f0] + 0x18 + 0x50 * i, and binds the player to its controller slot
 * (record + 0x1c, 0x7101754d80); the fight reads only that copy (runs lwc_c0 / lwc_late: profile 0's bytes over player 1's
 * profile at its record change the fight, at the fight start they do not; lwc_w: hardware write watch on the copy). The
 * builder also takes ONE option byte of the controller kind into record + 0x48 (profile + 0x2f GameCube, + 0x3f Pro / two
 * Joy-Cons / handheld, + 0x4b one Joy-Con; kind = [[0x7105338860 + 8 * slot] + 0x10] + 0x9c, if [0x71053388b0] + 8). In a room
 * match each game sends its own player's 0x2b bytes + that byte; both games then hold both players' own controls. */
#define LWR_CTL 0x2b
static uint8_t g_lwr_ctl[2][LWR_CTL + 2];      /* + LWR_CTL: the option byte; + LWR_CTL + 1: 1 = the player's own controller is a GameCube one */ static int g_lwr_ctl_ok, g_lwr_ctl_applied;
static void lwr_own_controls(uint64_t st, uint8_t out[LWR_CTL + 2]) {
    uint32_t prof = rd32(st + 0x214), slot = rd32(st + 0x210);
    uint64_t p = rd8(st + 0x450) && rd32(st + 0x78) == 0 ? prof_ptr(prof) : GA(0x7105313518ULL);
    for (int k = 0; k < LWR_CTL; k++) out[k] = rd8(p + 0x24 + (uint64_t)k);
    uint8_t opt = 0;
    if (rd8(st + 0x450) && rd32(st + 0x78) == 0) {
        uint64_t ctl = GA(0x7105338830ULL), h = sr64(GA(0x71053388b0ULL));
        if (slot < 10 && h && rd8(h + 8)) ctl = sr64(GA(0x7105338860ULL) + 8ull * slot);
        uint64_t sub = ctl ? sr64(ctl + 0x10) : 0; uint32_t kind = sub ? rd32(sub + 0x9c) : 0xffffffffu;
        if (ctl && kind < 7 && ((0x66u >> kind) & 1)) opt = rd8(p + 0x3f); else if (ctl && kind - 3 < 2) opt = rd8(p + 0x4b); else if (ctl && kind == 7) opt = rd8(p + 0x2f);
    }
    out[LWR_CTL] = opt;
    {   int nnhid_native_gc0(void); const char *e = getenv("SSBU_RB_NET_TEST_GC");      /* (TEST ONLY) =1: this game's player's controller taken as a GameCube one */
        out[LWR_CTL + 1] = (uint8_t)(nnhid_native_gc0() || (e && e[0] == '1')); }
    {   const char *e = getenv("SSBU_RB_NET_TEST_CTL");    /* (TEST ONLY) =<0x2b bytes in hex>: this game's own player's controls, as if its profile held them */
        if (e && strlen(e) >= 2 * LWR_CTL) { for (int k = 0; k < LWR_CTL; k++) { unsigned v = 0; sscanf(e + 2 * k, "%2x", &v); out[k] = (uint8_t)v; }
            printf("[rbnet] room match: TEST: this game's player's controls from SSBU_RB_NET_TEST_CTL\n"); fflush(stdout); } }
}
static ImportFn g_orig_controls_fill;
static void controls_fill_hook(Ctx *c, void *user) {
    (void)user;
    g_orig_controls_fill(c);
    if (!g_hs_lwr || !g_lwr_ctl_ok || g_lwr_ctl_applied) return;
    uint64_t o = sr64(GA(0x71052c40f0ULL)); if (!o) return;
    for (int i = 0; i < 2; i++) {
        for (int k = 0; k < LWR_CTL; k++) wr8(o + 0x18 + 0x50ull * (uint64_t)i + (uint64_t)k, g_lwr_ctl[i][k]);
        char t[200]; int n = 0; for (int k = 0; k < LWR_CTL; k++) n += snprintf(t + n, sizeof t - n, "%02x", g_lwr_ctl[i][k]);
        printf("[rbnet] room match: player %d plays with its own name-tag controls (profile + 0x24 .. + 0x4e: %s; record option byte %u)\n", i + 1, t, g_lwr_ctl[i][LWR_CTL]);
    }
    g_lwr_ctl_applied = 1;
    fflush(stdout);
}
static int hs_layout(uint64_t st, uint16_t out[11]) {
    uint32_t type = rd32(st + 0x78);
    if (rd8(st + 0x454) || (rd8(st + 0x455) && type == 0)) { for (int k = 0; k < 11; k++) out[k] = rd16(st + 0x54 + 2ull * k); return 1; }
    uint32_t prof = rd32(st + 0x214);
    if (!rd8(st + 0x455) && prof != 0xffffffffu && type == 0) {
        uint64_t p = prof_ptr(prof);
        for (int k = 0; k < 11; k++) out[k] = rd16(p + 0xc + 2ull * k);
        return 1;
    }
    return 0;
}
static void hs_apply(uint64_t st, int idx, uint32_t type_was) {
    if (idx == 0) {
        RbhMatch me; memset(&me, 0, sizeof me);
        uint32_t cur = rd32(GA(0x7105309968ULL)); uint64_t sel = GA(0x71053097c0ULL) + 0x20ull * (cur < 3 ? cur : 0);
        me.chara[0] = rd64(st + 0x88); me.colour[0] = rd8(st + 0x90); me.has_layout[0] = (uint8_t)hs_layout(st, me.layout[0]);
        safe_read(sel, me.stage, sizeof me.stage);
        me.hazards = rd8(GA(0x7105309985ULL)); me.items = rd32(MELEE_INFO + 0x54); me.rule_kind = rd8(st + 5); me.stock = rd8(st + 0xa);
        {   int comp_quickplay(void); g_hs_quick = comp_quickplay(); }
        if (g_hs_quick) {
            printf("[rbnet] quick match (Online Ranked): rules locked: rule kind %d -> %d, stocks %d -> %d, items %u -> 0, hazards %d -> 0\n", me.rule_kind, QUICK_RULE_KIND, me.stock, QUICK_STOCKS, me.items, me.hazards);
            fflush(stdout);
            me.rule_kind = QUICK_RULE_KIND; me.stock = QUICK_STOCKS; me.items = 0; me.hazards = 0;
        }
        me.ctx_seed = rd32(GA(0x710530d380ULL)); safe_read(GA(0x71050c5ad0ULL), me.ctx_hist, sizeof me.ctx_hist); safe_read(MELEE_INFO + 0x420, me.ctx_stage, sizeof me.ctx_stage);   /* (hs12) the stage set-up's inputs (rb_hs.h) */
        if (getenv("SSBU_RB_NET_WATCH")) { uint64_t off = strtoull(getenv("SSBU_RB_NET_WATCH"), NULL, 16); if (off <= 1) off = 0x98;      /* (hs12: =<hex offset in the match block>) */
            uint64_t w[1] = {MELEE_INFO + (off & ~7ull)}; printf("[rbnet] research: match block + 0x%llx at the handshake = %016llx\n", (unsigned long long)(off & ~7ull), (unsigned long long)rd64(w[0])); debug_watch(w, 1); }
        {   const char *e = getenv("SSBU_SQ_NETWATCH");      /* (RESEARCH) =<hex off>[,<hex off>..]: who writes these bytes of the room's net object from now on */
            if (e && *e) { uint64_t net = sr64(sr64(sr64(GA(0x71052c3c58ULL)) + 8) + 0x88), w[4]; int n = 0; const char *p = e;
                while (*p && n < 4) { w[n++] = *p == 'g' ? GA(strtoull(p + 1, NULL, 16) & ~7ull) : net + (strtoull(p, NULL, 16) & ~7ull);      /* (g<address>: a guest address) */ p = strchr(p, ','); if (!p) break; p++; }
                if (net) { printf("[squadcss] RESEARCH: net object %llx watched at %d place(s)\n", (unsigned long long)net, n); fflush(stdout); debug_watch(w, n); } } }
        g_hs_ok = rbn_handshake(&me, &g_hs_m); g_hs_local = 0;
        {   int rbn_room_rollback(void); g_hs_lwr = g_hs_ok && rbn_room_rollback(); }
        {   int rbn_match_cancelled(void); if (!g_hs_ok && rbn_match_cancelled() == 2) return; }      /* (R3 pub2) a Local Wireless room fight: nothing changed */
        if (!g_hs_ok) {                                   /* (hs12) not agreed: a local match of this game (player 2 = this player's fighter again), left at once by rb_rollback.c */
            g_hs_m = me; g_hs_m.chara[1] = me.chara[0]; g_hs_m.colour[1] = (uint8_t)((me.colour[0] + 1) & 7); g_hs_m.has_layout[1] = me.has_layout[0];
            memcpy(g_hs_m.layout[1], me.layout[0], sizeof g_hs_m.layout[1]); g_hs_ok = 1; g_hs_local = 1;
            /* a match that ends by itself: Final Destination, one stock; rb_rollback.c walks player 1 off the stage */
            wr64(sel, 0x6900000000000000ull | hash40("ui_stage_end")); wr32(sel + 8, 0);
            if (g_hs_m.rule_kind == 1) g_hs_m.stock = 1;
        }
        if (g_hs_ok && !g_hs_local && g_hs_quick && !g_hs_lwr) { g_hs_m.rule_kind = QUICK_RULE_KIND; g_hs_m.stock = QUICK_STOCKS; g_hs_m.items = 0; g_hs_m.hazards = 0; }
        if (g_hs_ok && !g_hs_local) {
            if (!g_hs_lwr) {                              /* (R3 lwr: a room's match: the stage, hazards and items are the room's, the same on both PCs) */
            for (int k = 0; k < 0x20; k++) wr8(sel + (uint64_t)k, g_hs_m.stage[k]);
            wr8(GA(0x7105309985ULL), g_hs_m.hazards); wr32(MELEE_INFO + 0x54, g_hs_m.items);
            }
            /* (hs12) the stage set-up's inputs as the host had them (rb_hs.h: run tw5, a stage variation chosen from each game's own history) */
            {   int d = 0; uint8_t h[0x60], stg[0xb0];
                safe_read(GA(0x71050c5ad0ULL), h, sizeof h); safe_read(MELEE_INFO + 0x420, stg, sizeof stg);
                d = (rd32(GA(0x710530d380ULL)) != g_hs_m.ctx_seed) + (memcmp(h, g_hs_m.ctx_hist, sizeof h) != 0) * 2 + (memcmp(stg, g_hs_m.ctx_stage, sizeof stg) != 0) * 4;
                wr32(GA(0x710530d380ULL), g_hs_m.ctx_seed);
                for (int k = 0; k < (int)sizeof g_hs_m.ctx_hist; k++) wr8(GA(0x71050c5ad0ULL) + (uint64_t)k, g_hs_m.ctx_hist[k]);
                for (int k = 0; k < (int)sizeof g_hs_m.ctx_stage; k++) wr8(MELEE_INFO + 0x420 + (uint64_t)k, g_hs_m.ctx_stage[k]);
                printf("[rbnet] handshake: the stage set-up's inputs taken from the host (here they differed: seed word %s, history %s, stage block %s)\n", d & 1 ? "yes" : "no", d & 2 ? "yes" : "no", d & 4 ? "yes" : "no"); }
            /* the match's random seed: after this hook the game's set-up 0x71014d8620 stores the next value of the global
             * xorshift128 at 0x71052381c0 (4 words; advanced by the menus, so different on the two PCs) in match block
             * + 0x98 (at 0x71014d8f00..2c, when block + 0x9c is set), and the fight's script generators are seeded from it
             * (found with SSBU_RB_NET_WATCH, runs hs4 / hs5: equal + 0x98 at the handshake, different after that store).
             * Both games set that generator from the agreed seed here. */
            {   uint64_t g = GA(0x71052381c0ULL), z = g_hs_m.seed;
                printf("[rbnet] handshake: the global generator %llx was %08x %08x %08x %08x; set from the agreed seed\n", (unsigned long long)g, rd32(g), rd32(g + 4), rd32(g + 8), rd32(g + 12));
                for (int k = 0; k < 4; k++) { z += 0x9E3779B97F4A7C15ull; uint64_t y = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull; y = (y ^ (y >> 27)) * 0x94D049BB133111EBull; y ^= y >> 31; wr32(g + 4ull * k, (uint32_t)y | (k == 0)); }
                fflush(stdout); }
            /* (R3 lwr) the room's match made a local versus match: the match block's melee mode (+ 0; FighterManager impl + 0xc0 takes it,
             * app::lua_bind::FighterManager__is_melee_mode_*) is 1 in a Local Wireless room's match and 0 in a local Smash (versus) match,
             * the rest of the rule header the same (runs lwres3 / lwres3v, SSBU_DEBUG_MIDUMP: the room's match and a local versus match
             * at the set-up and at the fight start: rule + 0, + 1 and FighterManager impl + 0xc0 the only differences there). So the game's
             * own online netcode does not play this fight; both games run it as the local match rollback expects. SSBU_RB_NET_ROOM_MODE=<n>:
             * the mode written (test), -1 = not changed. */
            if (g_hs_lwr) {
                const char *e = getenv("SSBU_RB_NET_ROOM_MODE"); int to = e && *e ? atoi(e) : 0; uint8_t was = rd8(MELEE_INFO);
                if (!(g_tc_native && g_room_mode_was >= 0)) { g_room_mode_was = was; g_room_saved_n = 0; }      /* (0.2.15) given back when the fight ends (dm_room_restore); (Battle Arena, game's squad, run cssh5) the 2nd handshake (the squad's records) keeps what the room's records saved: else nothing was given back and the joiner's next select screen was broken */
                if (to >= 0) wr8(MELEE_INFO, (uint8_t)to);
                printf("[rbnet] room match: melee mode (match block + 0) %u -> %u: played as a local versus match with rollback (room maker = player 1)\n", was, rd8(MELEE_INFO));
                fflush(stdout);
            }
        }
    }
    if (!g_hs_ok || idx < 0 || idx > 1) return;
    if (g_hs_lwr) {                                     /* (R3 lwr) the players' fighters, colours, rules are the room's own (the same on both PCs) */
        /* Which controller drives a player: state + 0x210 = the controller slot (the record builder reads the controller object
         * 0x7105338860[state + 0x210]). In the room's records this game's own player has slot 0 (its name tag, + 0x450) and the
         * other game's player is a network player (+ 0x451 -> record + 0xa5 = 1, slot -1): made a local match, the network player
         * read no controller and this game's player read port 1 whatever its index (run lwr17, SSBU_RB_NET_DETAIL: the joiner's
         * player 2 moved with port 1's pad; lwr18: with no slot at all neither player moved). So on both PCs player i gets
         * controller slot i (port i, where rb_rollback.c gives player i's pads), no network flag, and no name-tag profile (+ 0x214
         * = -1: the controls are the game's default ones on both PCs; a player's own name-tag controls exist only on his own
         * PC). Run lwr20: both players move, each with its own pads, on both PCs. SSBU_RB_NET_ROOM_NORM=0: the room's states as
         * they are (test). The 11 words at state + 0x54 (record + 0x28, written with + 0x454 = 1) hold the player's name-tag text
         * in these runs ("Anto..."): each player's own, so both records are the same: player 1's from the handshake (the room
         * maker's own), player 2's sent by the joiner at this record (rb_net.c rbn_room_layout). */
        static int norm = -1; if (norm < 0) { const char *e = getenv("SSBU_RB_NET_ROOM_NORM"); norm = e && *e ? atoi(e) : 1; }
        if (norm) {
            int rbn_local_player(void); int rbn_room_layout(const uint16_t mine[11], int has, const uint8_t mine_ctl[LWR_CTL + 2], uint16_t other[11], int *other_has, uint8_t other_ctl[LWR_CTL + 2]);
            int local = rbn_local_player(), has = 0; uint16_t lay[11]; memset(lay, 0, sizeof lay);
            if (idx == 0) { g_lwr_ctl_ok = 0; g_lwr_ctl_applied = 0; }
            if (idx == local) lwr_own_controls(st, g_lwr_ctl[idx]);      /* (before the state is changed below: its own profile and controller) */
            if (idx == 0) { if (local == 0) has = hs_layout(st, lay); else { has = g_hs_m.has_layout[0]; memcpy(lay, g_hs_m.layout[0], sizeof lay); } }
            else {
                uint16_t own[11], other[11]; int own_has, other_has = 0;
                if (local == 1) own_has = hs_layout(st, own); else { own_has = g_hs_m.has_layout[0]; memcpy(own, g_hs_m.layout[0], sizeof own); }
                int got = rbn_room_layout(own, own_has, g_lwr_ctl[local], other, &other_has, g_lwr_ctl[1 - local]);
                g_lwr_ctl_ok = got;
                {   int rbn_squad_agreed(void), rbn_room_squad(uint64_t chara[2], uint8_t colour[2]), rbn_ba_pick(int slot, uint64_t *chara, uint8_t *colour);
                    g_sq_want = 0;
                    if (rbn_squad_agreed()) {                /* (Battle Arena) both squads: fighter 1 = the room's record, 2 and 3 = each player's picks */
                        uint64_t oc[2]; uint8_t ocol[2]; int peer = rbn_room_squad(oc, ocol);
                        uint8_t mine_sq[0xb0]; void rbn_ba_fill_sent(uint8_t out[0xb0]); rbn_ba_fill_sent(mine_sq);
                        memset(g_sq_chara, 0, sizeof g_sq_chara); memset(g_sq_colour, 0, sizeof g_sq_colour);
                        memcpy(&g_sq_chara[local][1], mine_sq + 3, 16); g_sq_colour[local][1] = mine_sq[19]; g_sq_colour[local][2] = mine_sq[20];
                        if (peer) { g_sq_chara[1 - local][1] = oc[0]; g_sq_chara[1 - local][2] = oc[1]; g_sq_colour[1 - local][1] = ocol[0]; g_sq_colour[1 - local][2] = ocol[1]; }
                        g_sq_want = peer;
                        printf("[squad] room match: squads %s (this game's fighters 2 / 3 %016llx / %016llx, the other's %016llx / %016llx)\n", peer ? "agreed" : "NOT RECEIVED from the other game: played as the 1-on-1 room match (the match may differ)",
                               (unsigned long long)g_sq_chara[local][1], (unsigned long long)g_sq_chara[local][2], (unsigned long long)g_sq_chara[1 - local][1], (unsigned long long)g_sq_chara[1 - local][2]);
                        fflush(stdout);
                    } }
                {   void nnhid_lwr_styles(int on, int gc0, int gc1);       /* (R3 lwr) port i in player i's own controller kind on both PCs */
                    if (got) { nnhid_lwr_styles(1, g_lwr_ctl[0][LWR_CTL + 1], g_lwr_ctl[1][LWR_CTL + 1]);
                        printf("[rbnet] room match: controller kinds: player 1 %s, player 2 %s (ports 1 / 2 shown so on this PC)\n", g_lwr_ctl[0][LWR_CTL + 1] ? "GameCube" : "Pro", g_lwr_ctl[1][LWR_CTL + 1] ? "GameCube" : "Pro"); } }
                if (local == 1) { has = own_has; memcpy(lay, own, sizeof lay); } else if (got) { has = other_has; memcpy(lay, other, sizeof lay); }
            }
            if (!has) { uint64_t p = GA(0x7105313518ULL); for (int k = 0; k < 11; k++) lay[k] = rd16(p + 0xc + 2ull * k); }      /* (the game's default profile) */
            printf("[rbnet] room match: player %d (%s): ui_chara %016llx colour %u; panel state: name tag %d, network player %d, controller slot %d, profile %d -> controller slot %d, a local human\n", idx + 1,
                   idx == local ? "this game's player" : "the other game's player", (unsigned long long)rd64(st + 0x88), rd8(st + 0x90), rd8(st + 0x450), rd8(st + 0x451), (int)rd32(st + 0x210), (int)rd32(st + 0x214), idx);
            if (idx >= 0 && idx < 2) memcpy(g_tc_lay[idx], lay, sizeof lay);
            if (idx >= 0 && idx < 2 && !g_tc_native) {      /* (0.2.15) the room's own panel state, given back when the fight ends */
                g_room_saved[idx].st = st; safe_read(st + 0x450, g_room_saved[idx].b450, sizeof g_room_saved[idx].b450);
                g_room_saved[idx].slot = rd32(st + 0x210); g_room_saved[idx].prof = rd32(st + 0x214); safe_read(st + 0x54, g_room_saved[idx].lay, sizeof g_room_saved[idx].lay); g_room_saved[idx].a4 = rd8(st + 0xa4);
                if (g_room_saved_n < idx + 1) g_room_saved_n = idx + 1; }
            wr8(st + 0x450, 1); wr8(st + 0x451, 0); wr32(st + 0x210, (uint32_t)idx); wr32(st + 0x214, 0xffffffffu);
            wr8(st + 0x454, 1); for (int k = 0; k < 11; k++) wr16(st + 0x54 + 2ull * k, lay[k]);
            fflush(stdout);
        }
        return;
    }
    if (type_was == 3) {                                /* (hs12) no panel for this player on this game's select screen: player 1's state is the template */
        if (!g_hs_p1_ok) { printf("[rbnet] handshake: player %d has NO panel on this game's select screen and player 1's state was not read: this match will not be the agreed one\n", idx + 1); fflush(stdout); return; }
        hs_fill_empty(st, idx); wr64(st + 0x80, g_hs_m.chara[idx]);
        printf("[rbnet] handshake: player %d had no panel on this game's select screen: made from player 1's panel state with the %s fighter\n", idx + 1, g_hs_local ? "local" : "agreed"); fflush(stdout);
    }
    wr64(st + 0x88, g_hs_m.chara[idx]); wr8(st + 0x90, g_hs_m.colour[idx]); wr32(st + 0x78, 0);
    if (g_hs_m.has_layout[idx]) { wr8(st + 0x454, 1); for (int k = 0; k < 11; k++) wr16(st + 0x54 + 2ull * k, g_hs_m.layout[idx][k]); }
    else { wr8(st + 0x454, 0); wr8(st + 0x455, 0); wr32(st + 0x214, 0xffffffffu); }
    if (g_hs_quick && !g_hs_local) { wr8(st + 5, QUICK_RULE_KIND); wr8(st + 0xa, QUICK_STOCKS); }      /* (Arena quick match) the locked rules in both players' states */
    if (rd8(st + 5) == g_hs_m.rule_kind) { if (g_hs_m.rule_kind == 1) wr8(st + 0xa, g_hs_m.stock); }
    else { printf("[rbnet] handshake: player %d: rule kind %d here, %d agreed (not changed: set the same rules on both)\n", idx + 1, rd8(st + 5), g_hs_m.rule_kind); fflush(stdout); }
}
/* ---- (Battle Arena) Squad Strike, Tag Team, 3 fighters each, 1 stock each, in ONE fight (the game's own mode: when a fighter loses its
 * stock the next one of the squad comes in). How the game sets it up (13.0.4, read off the binary and runs r13 / r16 of
 * MeleeTeamCompeSequenceScene, SEQUENCE_MELEE_TEAM_COMPE 0x02020000, ruleset "Tag Team"):
 *   - the squad table 0x71052c9dc0 (0x1818 bytes, cleared by 0x710179d0a0): + 0 count (3 or 5), + 1 the same count, + 8 the entries
 *     [2 players][5] of 0x268 bytes: a player record (0x260, built by the record builder 0x7101788260 from one fighter's panel
 *     state) + a word. The squad sequence (0x71023108b0, 0x7102312610..) builds one record per fighter and copies them there in
 *     the squad's order; slot 0 is also the match block's record of that player (fighter 1 starts the fight).
 *   - the battle builder 0x71014e58f0 sees a squad when match block + 0x39 == 3 (its "kind" 2: it loads every fighter of the table
 *     at the start, 0x71014e9f10..); the other rule bytes of a Tag Team match that differ from a Smash versus match at the fight
 *     start: + 0 melee mode 2 (0 local Smash, 1 Local Wireless), + 0x15 1, + 0x20 1, + 0x4c stocks 1.
 * A squad match is made from a Smash versus set-up (both records built) by writing those: the table from the two players' own panel
 * states with the fighter changed (the game's own record builder makes each entry), then the rule bytes. Everything lives in the
 * game's memory, so the rollback state holds it and the fighter changes happen inside the simulated fight.
 * Used by the Battle Arena room match (rb_net.c agrees both squads) and, TEST ONLY, by SSBU_SQ_TEST=<p1a>,<p1b>,<p1c>/<p2a>,<p2b>,<p2c>
 * (fighter names as SSBU_DEBUG_MATCH: any local versus match becomes a squad match with these fighters). */
int dm_squad_made(void) { return g_sq_made; }            /* (replay_state.c / rb_net.c) the last set-up was made a squad match */
void dm_squad_set(int on, const uint64_t chara[2][SQ_N], const uint8_t colour[2][SQ_N]) {      /* rb_net.c: the agreed squads of the next set-up */
    g_sq_want = on;
    if (on) { memcpy(g_sq_chara, chara, sizeof g_sq_chara); memcpy(g_sq_colour, colour, sizeof g_sq_colour); }
}
static void sq_test_parse(void) {
    const char *e = getenv("SSBU_SQ_TEST"); g_sq_test = 0;
    if (!e || !*e) return;
    char buf[200]; snprintf(buf, sizeof buf, "%s", e);
    int p = 0, k = 0;
    for (char *t = buf; *t && p < 2; ) {
        char *end = t + strcspn(t, ",/"); char sep = *end; *end = 0;
        char nm[64]; snprintf(nm, sizeof nm, "ui_chara_%s", t);
        if (k < SQ_N) { g_sq_chara[p][k] = 0xc100000000000000ull | hash40(nm); g_sq_colour[p][k] = (uint8_t)p; k++; }
        if (!sep) break;
        if (sep == '/') { p++; k = 0; }
        t = end + 1;
    }
    g_sq_test = 1;
    printf("[squad] TEST: every local versus match is made a squad match (SSBU_SQ_TEST=%s)\n", e); fflush(stdout);
}
/* after player 2's record (idx 1): both records exist; c is the record builder's context (its registers are given back) */
static void squad_build(Ctx *c) {
    if (!g_sq_st[0] || !g_sq_st[1]) { printf("[squad] NOT made: a player's panel state was not seen\n"); fflush(stdout); return; }
    uint64_t save[32]; memcpy(save, c->x, sizeof save);
    call_guest(c, GA(0x710179d0a0ULL));                  /* the game's own clear of the squad table */
    static uint8_t tmp[0x480] __attribute__((aligned(16)));
    for (int p = 0; p < 2; p++)
        for (int k = 0; k < SQ_N; k++) {
            uint64_t ent = SQ_TABLE + 8 + 0xc08ull * (uint64_t)p + 0x268ull * (uint64_t)k;
            if (g_sq_stcopy && g_sq_copy_ok[p]) memcpy(tmp, g_sq_copy[p], sizeof tmp); else safe_read(g_sq_st[p], tmp, sizeof tmp);
            uint64_t st = (uint64_t)(uintptr_t)tmp;
            uint64_t ch = g_sq_chara[p][k] ? g_sq_chara[p][k] : rd64(st + 0x88); uint8_t col = g_sq_chara[p][k] ? g_sq_colour[p][k] : rd8(st + 0x90);      /* 0: the room's own fighter 1 */
            if (p == 1) for (int j = 0; j < SQ_N; j++) { uint64_t e0 = SQ_TABLE + 8 + 0x268ull * (uint64_t)j; if ((rd64(e0) & 0xffffffffffull) == (ch & 0xffffffffffull) && rd8(e0 + 0x19) == col) { col = (uint8_t)((col + 1) & 7); j = -1; } }      /* the same fighter in the same colour on the other side: the next colour */
            wr64(st + 0x80, ch); wr64(st + 0x88, ch); wr8(st + 0x90, col);
            if (rd8(st + 5) == 1) wr8(st + 0xa, 1);       /* one stock per fighter */
            {   const char *e = getenv("SSBU_SQ_TEST_CPU"); int lv[2] = {0, 0};      /* (TEST ONLY) =<level p1>,<level p2>: both players CPUs of these levels (0 = as set up) */
                if (g_sq_test > 0 && e && sscanf(e, "%d,%d", &lv[0], &lv[1]) == 2 && lv[p] > 0) { wr32(st + 0x78, 1); wr8(st + 0x91, (uint8_t)lv[p]); wr32(st + 0x224, 0xffffffffu); } }
            memcpy(c->x, save, sizeof save);
            c->x[0] = st; c->x[1] = (uint64_t)p; c->x[2] = g_sq_x2[p]; c->x[3] = ent;
            g_orig_player_record(c);
            if ((int8_t)rd8(ent + 8) < 0 && k) {          /* not a fighter of this game: fighter 1 again (the same on both games) */
                printf("[squad] player %d fighter %d: ui_chara %016llx is no fighter here: fighter 1 instead\n", p + 1, k + 1, (unsigned long long)ch);
                if (g_sq_stcopy && g_sq_copy_ok[p]) memcpy(tmp, g_sq_copy[p], sizeof tmp); else safe_read(g_sq_st[p], tmp, sizeof tmp);
                if (rd8(st + 5) == 1) wr8(st + 0xa, 1);
                memcpy(c->x, save, sizeof save); c->x[0] = st; c->x[1] = (uint64_t)p; c->x[2] = g_sq_x2[p]; c->x[3] = ent; g_orig_player_record(c);
            }
            wr8(ent + 0x18, 1);
            if (g_hs_lwr && g_lwr_ctl_ok) wr8(ent + 0x48, g_lwr_ctl[p][LWR_CTL]);      /* (R3 lwr) the player's own option byte, as its record */
            printf("[squad] player %d fighter %d: ui_chara %016llx colour %u -> entry %llx: fighter kind %d\n", p + 1, k + 1, (unsigned long long)rd64(ent), rd8(ent + 0x19), (unsigned long long)ent, (int)(int8_t)rd8(ent + 8));
        }
    memcpy(c->x, save, sizeof save);
    wr8(SQ_TABLE, SQ_N); wr8(SQ_TABLE + 1, SQ_N);
    for (int p = 0; p < 2; p++) {                         /* fighter 1 starts: the match block's record = slot 0 */
        uint64_t rec = MELEE_INFO + 0x580 + 0x260ull * (uint64_t)p, ent = SQ_TABLE + 8 + 0xc08ull * (uint64_t)p;
        for (int k = 0; k < 0x260; k++) wr8(rec + (uint64_t)k, rd8(ent + (uint64_t)k));
    }
    uint8_t was0 = rd8(MELEE_INFO);
    wr8(MELEE_INFO, 2); wr8(MELEE_INFO + 0x15, 1); wr8(MELEE_INFO + 0x20, 1); wr8(MELEE_INFO + 0x39, 3); wr8(MELEE_INFO + 0x4c, 1);
    g_sq_made = 1;
    printf("[squad] Squad Strike (Tag Team, %d fighters each, 1 stock each): squad table filled, rule: melee mode %u -> 2, + 0x15 / + 0x20 1, + 0x39 3, stocks 1\n", SQ_N, was0);
    fflush(stdout);
}
static void match_player_hook(Ctx *c, void *user) {
    (void)user;
    if (g_sq_test < 0) sq_test_parse();
    if (c->x[1] == 0) g_sq_made = 0;
    if (c->x[1] < 2) { g_sq_st[c->x[1]] = c->x[0]; g_sq_x2[c->x[1]] = c->x[2]; }
    uint64_t st = c->x[0], rec = c->x[3]; int idx = (int)c->x[1];
    if (getenv("SSBU_DEBUG_SCENES")) { printf("[squadcss] record builder: idx %d, w2 %d, record %llx, caller %llx, ui_chara %016llx colour %u type %u\n", idx, (int)c->x[2], (unsigned long long)rec, (unsigned long long)(c->x[30] - 4), (unsigned long long)rd64(st + 0x88), rd8(st + 0x90), rd32(st + 0x78)); fflush(stdout); }
    uint64_t was = rd64(st + 0x88); uint32_t type_was = rd32(st + 0x78);
    if (idx == 0) { g_hs_p1_ok = safe_read(st, g_hs_p1, sizeof g_hs_p1); InterlockedIncrement(&g_dm_setups); }
    if (idx == 0 && g_dm_list) {                          /* SSBU_DEBUG_MATCH_LIST: every new match set-up takes the next line (the last one stays) */
        char line[256];
        if (fgets(line, sizeof line, g_dm_list)) { line[strcspn(line, "\r\n")] = 0; if (line[0]) { g_dm_fight++; parse_match(line); } }
    }
    if (g_dm.stock > 0 && type_was != 3 && rd8(st + 5) == 1) wr8(st + 0xa, (uint8_t)g_dm.stock);   /* state + 5: rule kind (1 = stock), + 0xa: stocks -> record + 0x18 */
    if ((idx == 2 || idx == 3) && g_dm_name[idx][0] && g_dm_state1_ok) {      /* session 14: p3 / p4 = CPU players. An empty panel (type 3) takes player 2's state as the template */
        static int mode = -1;
        if (mode < 0) mode = getenv("SSBU_DEBUG_P34") ? atoi(getenv("SSBU_DEBUG_P34")) : 1;      /* 1 copy the whole state of player 2 (default), 2 only the fields known */
        if (type_was == 3 || rd32(st + 0x78) != 1) {
            if (mode == 1) for (int k = 0; k < (int)sizeof g_dm_state1; k++) wr8(st + (uint64_t)k, g_dm_state1[k]);
            else { wr8(st + 5, g_dm_state1[5]); wr8(st + 0xa, g_dm_state1[0xa]); }
            wr32(st + 0x78, 1);
        }
        char nm[64]; snprintf(nm, sizeof nm, "ui_chara_%s", g_dm_name[idx]);
        wr64(st + 0x88, 0xc100000000000000ull | hash40(nm));
        wr8(st + 0x90, (uint8_t)idx);                      /* colour: one per player */
        if (g_dm.stock > 0 && rd8(st + 5) == 1) wr8(st + 0xa, (uint8_t)g_dm.stock);
        if (g_dm.cpu > 0) { wr8(st + 0x91, (uint8_t)g_dm.cpu); wr32(st + 0x224, 0xffffffffu); }
        g_dm_applied |= 1 << idx;
        type_was = 1;
    }
    if (getenv("SSBU_DEBUG_STATEDUMP") && idx >= 0 && idx < 2) {       /* S20t: the panel state as the screens left it (find the Mii fields) */
        char tag[48]; snprintf(tag, sizeof tag, "debug state: p%d ", idx); hex_lines(tag, st, 0x480); fflush(stdout);
    }
    if (idx >= 0 && idx < 2 && g_dm_name[idx][0] && type_was != 3 && !(getenv("SSBU_DEBUG_KEEP") && (atoi(getenv("SSBU_DEBUG_KEEP")) >> idx & 1))) {   /* SSBU_DEBUG_KEEP=<mask>: keep the screens' own choice for these players (S20t) */
        char nm[64]; snprintf(nm, sizeof nm, "ui_chara_%s", g_dm_name[idx]);
        wr64(st + 0x88, 0xc100000000000000ull | hash40(nm));
        if (idx == 1 && g_dm_p2type >= 0) wr32(st + 0x78, (uint32_t)g_dm_p2type);
        wr8(st + 0x90, (uint8_t)idx);                      /* colour: one per player, not the screens' (a Random panel left a random one: the fight started differently each run) */
        if (g_dm.cpu > 0 && rd32(st + 0x78) == 1) { wr8(st + 0x91, (uint8_t)g_dm.cpu); wr32(st + 0x224, 0xffffffffu); }   /* level 1..9; the game maps it to the power in record + 0x79 (0, 15, 21, 31, 42, 48, 60, 75, 100) */
        g_dm_applied |= 1 << idx;
    }
    if (idx == 0 && g_dm.text[0]) {
        /* the stage choice: the game reads it right after the player records, in the same function (0x7102311a24..): three
         * entries of 0x20 bytes at 0x71053097c0, the current one's index at 0x7105309968: +0 ui_stage id (0x69 << 56 | hash40),
         * +8 form (0 normal, 1 battlefield, 2 omega), +0xc a flag word, +0x10 a second id. The rule header (match block + 0..) is filled already. */
        uint32_t cur = rd32(GA(0x7105309968ULL)); uint64_t sel = GA(0x71053097c0ULL) + 0x20ull * (cur < 3 ? cur : 0);
        printf("debug match: stage choice of the screens: entry %u: ui_stage %llx form %u flag %x second id %llx; byte 7105309985 = %u; rule: stock %u item appear %u\n", cur,
               (unsigned long long)rd64(sel), rd32(sel + 8), rd32(sel + 0xc), (unsigned long long)rd64(sel + 0x10), rd8(GA(0x7105309985ULL)), rd8(MELEE_INFO + 0x4c), rd32(MELEE_INFO + 0x54));
        if (g_dm.stage[0]) {
            const char *sn = g_dm.stage; char nm[80];
            if (!strcmp(sn, "fd") || !strcmp(sn, "final_destination")) sn = "end"; else if (!strcmp(sn, "battlefield")) sn = "battle_field";
            snprintf(nm, sizeof nm, "ui_stage_%s", sn);
            wr64(sel, 0x6900000000000000ull | hash40(nm));
        }
        if (g_dm_form >= 0) wr32(sel + 8, (uint32_t)g_dm_form);
        if (g_dm_hazards >= 0) wr8(GA(0x7105309985ULL), (uint8_t)g_dm_hazards);
        if (g_dm.items >= 0) wr32(MELEE_INFO + 0x54, g_dm.items ? 2u : 0u);
    }
    if (idx == 0) { void nnhid_lwr_styles(int on, int gc0, int gc1); nnhid_lwr_styles(0, 0, 0); }      /* (R3 lwr) a new set-up: the ports in their own kinds again */
    if (idx == 0) { void rbn_set_setup_waiting(int waiting); rbn_set_setup_waiting(c->x[30] == GA(0x71017911dcULL)); }      /* (R3 lwr) a Local Wireless room's waiting-stage fight: built from its own set-up (runs lwres2 / lwres3: return address 0x71017911dc; the room's match and the select screens: 0x710178f498) */
    {   uint64_t ra = c->x[30];
        g_tc_native = g_bacss == 1 && ra >= GA(0x7102311f00ULL) && ra <= GA(0x7102312620ULL) && idx >= 0 && idx < 2;
        g_tc_k = g_tc_native ? ((int)c->x[2] - idx) / 2 : 0; }
    if (g_hs_wanted && g_tc_native && g_tc_k > 0) {     /* (Battle Arena, game's squad) fighters 2.. of a side: the room normalization only */
        if (g_hs_ok && g_hs_lwr) { wr8(st + 0x450, 1); wr8(st + 0x451, 0); wr32(st + 0x210, (uint32_t)idx); wr32(st + 0x214, 0xffffffffu);
            wr8(st + 0x454, 1); for (int k = 0; k < 11; k++) wr16(st + 0x54 + 2ull * k, g_tc_lay[idx][k]); }
    } else if (g_hs_wanted) hs_apply(st, idx, type_was);       /* R3: the match both online games agreed (rb_net.c handshake) */
    if (g_tc_native) g_sq_want = 0;                     /* (the game's own squad table: squad_build does not overwrite it) */
    {   /* (Battle Arena, game's squad) the room's Squad Strike (sequence 02020000): every player record of players 1 / 2 (the room's
         * match records and the squad's) gets the player number idx + 1 in panel state + 0xa4 (-> record + 0x258): the game gives each
         * station its own numbering (run cssm5: host 2 / 1, joiner 1 / 2: match block + 0x7d8 / + 0xa38 differ, DESYNC at frame 1).
         * squad_build stays off: the squad is the game's own (SSBU_BA_SQUAD_PNUM=0: the number as the game set it). */
        static int pnum = -1; if (pnum < 0) { const char *e = getenv("SSBU_BA_SQUAD_PNUM"); pnum = !(e && e[0] == '0'); }
        if (g_bacss == 1 && g_hs_wanted && idx >= 0 && idx < 2 && rd32(GA(0x71053040f0ULL)) == 0x02020000u) {
            g_sq_want = 0;
            if (pnum && rd8(st + 0xa4) != (uint8_t)(idx + 1)) {
                if (getenv("SSBU_DEBUG_SCENES")) { printf("[squadcss] player %d record: panel state + 0xa4 %u -> %d (caller %llx)\n", idx + 1, rd8(st + 0xa4), idx + 1, (unsigned long long)(c->x[30] - 4)); fflush(stdout); }
                wr8(st + 0xa4, (uint8_t)(idx + 1));
            }
        } }
    if (idx == 1) g_dm_state1_ok = safe_read(st, g_dm_state1, sizeof g_dm_state1);
    if (idx >= 0 && idx < 2) {                            /* (Battle Arena) this player's own panel state, kept for squad_build */
        if (g_sq_stcopy < 0) { const char *e = getenv("SSBU_SQ_STCOPY"); g_sq_stcopy = !(e && *e == '0'); }
        g_sq_copy_ok[idx] = safe_read(st, g_sq_copy[idx], sizeof g_sq_copy[idx]);
        if (idx == 1 && g_sq_st[0] == g_sq_st[1]) { static int said; if (!said++) { printf("[squad] both players' records are built from one panel state object (%llx): each player's own copy is used\n", (unsigned long long)st); fflush(stdout); } }
    }
    g_orig_player_record(c);
    if (g_tc_native) {                                  /* (Battle Arena, game's squad) */
        if (g_hs_lwr && g_lwr_ctl_ok) { wr8(rec + 0x48, g_lwr_ctl[idx][LWR_CTL]);
            if (idx == 1 && g_tc_k == 0) for (int j = 0; j < SQ_N; j++) wr8(SQ_TABLE + 8 + 0x268ull * (uint64_t)j + 0x48, g_lwr_ctl[0][LWR_CTL]); }
        if (idx == 1) g_sq_made = 1;
        if (g_tc_k == 0 && (rd8(MELEE_INFO + 0x39) != 3 || rd8(rec + 0x18) != 1)) { printf("[squad] WARNING: player %d's squad is not Tag Team with 1 stock (match block + 0x39 %u, record stocks %u)\n", idx + 1, rd8(MELEE_INFO + 0x39), rd8(rec + 0x18)); fflush(stdout); }
        printf("[squad] player %d fighter %d: ui_chara %016llx colour %u -> entry %llx: fighter kind %d (the game's own squad; match block + 0 %u, + 0x39 %u)\n", idx + 1, g_tc_k + 1, (unsigned long long)rd64(rec), rd8(rec + 0x19), (unsigned long long)rec, (int)(int8_t)rd8(rec + 8), rd8(MELEE_INFO), rd8(MELEE_INFO + 0x39));
        {   const char *d = getenv("SSBU_DEBUG_SCENES"); if (d && atoi(d) >= 3) { char t[48]; snprintf(t, sizeof t, "[squadcss] rec p%d k%d", idx + 1, g_tc_k + 1); hex_lines(t, rec, 0x268);
                snprintf(t, sizeof t, "[squadcss] st p%d k%d", idx + 1, g_tc_k + 1); hex_lines(t, st, 0x460); } }      /* (research) the records / panel states, to compare the two games */
        fflush(stdout);
    }
    if (idx == 1 && ((g_sq_test > 0 && !g_hs_wanted) || (g_sq_want && g_hs_wanted && g_hs_ok && !g_hs_local && g_hs_lwr))) squad_build(c);      /* (Battle Arena) */
    {   void hs_show_agreed(int idx); if (g_hs_wanted && g_hs_ok && !g_hs_local && !g_hs_lwr) hs_show_agreed(idx); }      /* (Arena) the screens before the fight show the agreed players */
    if (idx == 1 && g_hs_lwr && g_lwr_ctl_ok) for (int i = 0; i < 2; i++) wr8(MI_PLAYER(i) - 8 + 0x48, g_lwr_ctl[i][LWR_CTL]);      /* (R3 lwr) each player's own option byte (record + 0x48, see lwr_own_controls) */
    if (idx == 1 && rd32(st + 0x78) == 0) { void input_mark(const char *what); input_mark("setup-2p"); }      /* (R3 pub2, test aid) a match set up with a human player 2 (SSBU_SEQ @untilmark:setup-2p) */
    if (getenv("SSBU_DEBUG_P1PROFILE") && idx == 0) {     /* (R3 lwr, research) player 1's name-tag profile index forced (=<n>), or (=c<n>) profile n's
                                                           * control bytes + 0x24 .. + 0x50 copied over player 1's own profile's (in memory only, this run) */
        const char *e = getenv("SSBU_DEBUG_P1PROFILE");
        uint64_t x0 = sr64(GA(0x7105313510ULL)), x = x0 ? sr64(x0) : 0, a = x ? sr64(x + 0x58) : 0, b = a ? sr64(a) : 0, q = b ? sr64(b) : 0;
        if (e[0] == 'c' && q) { uint32_t from = (uint32_t)atoi(e + 1), own = rd32(st + 0x214);
            if (own < 0x3d) for (int k = 0x24; k < 0x50; k++) wr8(q + 0xf7d8ull * own + k, rd8(q + 0xf7d8ull * from + k));
            printf("debug ctrl: research: profile %u's control bytes copied over player 1's profile %u\n", from, own); }
        else { printf("debug ctrl: research: player 1's profile %d -> %d\n", (int)rd32(st + 0x214), atoi(e)); wr32(st + 0x214, (uint32_t)atoi(e)); }
        fflush(stdout);
    }
    if (getenv("SSBU_DEBUG_W500") && idx == 0) {          /* (R3 lwr, research) who writes match block + 0x500 (the block check's last differing bytes) */
        hex_lines("debug w500: block + 0x4d0 at player 1's record", MELEE_INFO + 0x4d0, 0xb0);
        uint64_t w[1] = {MELEE_INFO + 0x500}; debug_watch(w, 1); fflush(stdout);
    }
    if (getenv("SSBU_DEBUG_CTRLWATCH") && idx == 0) {     /* (R3 lwr, research) who writes the controls singleton's (0x71052c40f0) players 1 / 2 blocks */
        uint64_t o = sr64(GA(0x71052c40f0ULL));
        hex_lines("debug ctrl: singleton at player 1's record", o, 0x120);
        if (o) { uint64_t w[2] = {o + 0x18, o + 0x68}; debug_watch(w, 2); }
        fflush(stdout);
    }
    if (getenv("SSBU_DEBUG_CTRLDUMP") && idx <= 1) {      /* (R3 lwr, research) the player's name-tag profile and its controller slot's objects */
        uint32_t prof = rd32(st + 0x214), slot = rd32(st + 0x210); char t[80];
        uint64_t x0 = sr64(GA(0x7105313510ULL)), x = x0 ? sr64(x0) : 0, a = x ? sr64(x + 0x58) : 0, b = a ? sr64(a) : 0, q = b ? sr64(b) : 0;
        if (idx == 0) hex_lines("debug ctrl:   default profile", GA(0x7105313518ULL), 0x100);
        if (idx == 0) for (uint32_t k = 0; k < 0x3d; k++) { uint64_t pp = prof_ptr(k); if (pp == GA(0x7105313518ULL)) continue; char nm[24]; int j; for (j = 0; j < 10 && rd16(pp + 0xc + 2ull * j); j++) nm[j] = (char)rd16(pp + 0xc + 2ull * j); nm[j] = 0;
            char t[100]; int n = 0; for (int b2 = 0; b2 < 0x2b; b2++) n += snprintf(t + n, sizeof t - n, "%02x", rd8(pp + 0x24 + (uint64_t)b2)); printf("debug ctrl: profile %u \"%s\": %s\n", k, nm, t); }
        printf("debug ctrl: player %d: profile %d (table %llx) slot %d 0x450 %d 0x451 %d\n", idx, (int)prof, (unsigned long long)q, (int)slot, rd8(st + 0x450), rd8(st + 0x451));
        if (q && prof < 0x3d) { snprintf(t, sizeof t, "debug ctrl:   p%d profile", idx); hex_lines(t, q + 0xf7d8ull * prof, 0x100); }
        if (slot < 10) { uint64_t o = sr64(GA(0x7105338860ULL) + 8ull * slot); snprintf(t, sizeof t, "debug ctrl:   p%d slot obj", idx); hex_lines(t, o, 0x40);
            for (int k = 0; k < 4; k++) { snprintf(t, sizeof t, "debug ctrl:   p%d slot sub%d", idx, k); hex_lines(t, sr64(o + 0x10 + 8ull * k), 0x100); } }
        fflush(stdout);
    }
    if (getenv("SSBU_DEBUG_MIDUMP") && idx <= 1) {        /* (R3 lwr, research) the match block after this record: rule header and the two records */
        char tag[64]; snprintf(tag, sizeof tag, "debug midump: after p%d (from %llx) rule", idx, (unsigned long long)c->x[30]);
        if (idx == 1 || atoi(getenv("SSBU_DEBUG_MIDUMP")) > 1) { hex_lines(tag, MELEE_INFO, 0x580); hex_lines("debug midump:   rec0", MI_PLAYER(0) - 8, 0x260); hex_lines("debug midump:   rec1", MI_PLAYER(1) - 8, 0x260); }
        fflush(stdout);
    }
    printf("debug match: player %d: screen choice %llx type %u level %u -> record %llx: ui_chara %llx, fighter kind %d, type %d, colour %d, cpu level %d\n", idx,
           (unsigned long long)was, type_was, rd32(st + 0x224), (unsigned long long)rec, (unsigned long long)rd64(rec), (int)(int8_t)rd8(rec + 8), rd8(rec + 0xa), rd8(rec + 0x19), rd8(rec + 0x79));
    if (getenv("SSBU_DEBUG_RESEARCH") && idx == 0) {
        uint64_t fp = c->x[29], pair[2];
        printf("debug research: player record builder called from %llx, frames:", (unsigned long long)c->x[30]);
        for (int d = 0; d < 12 && safe_read(fp, pair, 16); d++) { printf(" %llx", (unsigned long long)pair[1]); if (pair[0] <= fp) break; fp = pair[0]; }
        printf("\n");
    }
    fflush(stdout);
}

static DWORD WINAPI dump_thread(LPVOID arg) {
    (void)arg;
    uint64_t last[8] = {0};
    for (;;) {
        Sleep(g_dm.text[0] || g_end_limit ? 2 : 250);
        if (g_end_fired) {
            printf("debug end: %lld fight frames reached at presented frame %lld (SSBU_DEBUG_END): exit request, normal shutdown\n", (long long)g_end_fight_frames, (long long)g_end_fired);
            fflush(stdout);
            { void rv_flush_now(void); rv_flush_now(); }      /* (Arena) the replay state of the match so far (fighter_sdk/replay_state.c) */
            if (!oe_exit_request()) report_and_exit(0);
            return 0;
        }
        if (getenv("SSBU_DEBUG_WATCHENTRY")) {            /* research: who writes the entry pointers of the FighterManager (impl + 0x20, + 0x28) */
            static int tick;
            uint64_t inst = hook_address("_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E");
            uint64_t mgr = inst ? sr64(inst) : 0, impl = mgr ? sr64(mgr) : 0;
            if (impl && (tick++ % 40) == 0) { uint64_t w[2] = {impl + 0x20, impl + 0x28}; watch_set(w, 2); }
            continue;
        }
        if (getenv("SSBU_DEBUG_RESEARCH")) {              /* research: who fills the global match block (kind of player 0 / 1, first rule bytes) */
            static int tick;
            if (atoi(getenv("SSBU_DEBUG_RESEARCH")) == 3 && (tick++ % 40) == 0) { uint64_t w[4] = {MELEE_INFO + 0x420, MELEE_INFO + 0x428, MELEE_INFO + 0x430, MELEE_INFO + 0x4d0}; watch_set(w, 4); }
            if (atoi(getenv("SSBU_DEBUG_RESEARCH")) == 2 && (tick++ % 40) == 0) { uint64_t w[4] = {MI_PLAYER(0), MI_PLAYER(1), MELEE_INFO, MELEE_INFO + 0x10}; watch_set(w, 4); }
        }
        for (int i = 0; i < 8; i++) {
            uint64_t rec = fighter_info_record(i);
            uint8_t b[0x400];
            if (!rec || !safe_read(rec, b, sizeof b)) continue;
            uint64_t h = 1469598103934665603ull;
            for (int k = 0; k < (int)sizeof b; k++) h = (h ^ b[k]) * 1099511628211ull;
            if (h == last[i]) continue;
            last[i] = h;
            if (getenv("SSBU_DEBUG_DUMP")) {
            printf("debug dump: entry %d record %llx\n", i, (unsigned long long)rec);
            for (int k = 0; k < 0x400; k += 16) {
                int any = 0;
                for (int j = 0; j < 16; j++) any |= b[k + j];
                if (any) printf("debug dump:   +%03x %02x %02x %02x %02x %02x %02x %02x %02x  %02x %02x %02x %02x %02x %02x %02x %02x\n", k, b[k], b[k+1], b[k+2], b[k+3], b[k+4], b[k+5], b[k+6], b[k+7], b[k+8], b[k+9], b[k+10], b[k+11], b[k+12], b[k+13], b[k+14], b[k+15]);
            }
            fflush(stdout);
            }
            if (i == 1 && getenv("SSBU_DEBUG_WATCH")) { static int done; uint64_t r0 = fighter_info_record(0); if (!done && r0) { done = 1; uint64_t w[4] = {r0 + 0x30, rec + 0x30, rec + 0x20, r0 + 0x08}; watch_set(w, 4); } }
            if (i == 1 && getenv("SSBU_DEBUG_SCAN")) { static int done; if (!done) { done = 1; scan_copies(rec); } }
        }
        if (g_dm.text[0]) {                               /* read back from the game's own entries once they have been stable for ~1 s */
            static int printed, stable, prev0 = -1, prev1 = -1;
            uint64_t r0 = fighter_info_record(0), r1 = fighter_info_record(1);
            int k0 = r0 ? (int)sr64(r0 + 0x30 - 0) & 0xffffffff : 0, k1 = r1 ? (int)sr64(r1 + 0x30) & 0xffffffff : 0;
            if (k0 && k1 && k0 == prev0 && k1 == prev1) stable++; else stable = 0;
            prev0 = k0; prev1 = k1;
            if (!printed && stable > 400 && getenv("SSBU_DEBUG_DUMP")) {   /* record + 0x30 is NOT the fighter kind (session 2: the health lines and the match block are) */ printed = 1; printf("debug match: in the fight: entry 0 kind %d, entry 1 kind %d (records at %llx, %llx; kind + 1 at record + 0x30)\n", k0 - 1, k1 - 1, (unsigned long long)r0, (unsigned long long)r1); if (getenv("SSBU_DEBUG_RESEARCH")) hex_lines("debug research:   fight rule ", MELEE_INFO, 0x580); fflush(stdout); }
        }
    }
}

/* (hs12) the select screens' per-player table (0x7105307770 + 0x1c8 * i, 8 entries; read in Ghidra: 0x7101787830 builds a
 * panel state from it, 0x710179dd50 resets it, 0x71017aca10 counts the teams of the entries with type < 3): + 0 controller
 * byte, + 0x28 player number, + 0x30 type (0 human, 1 CPU, 3 none), + 0x34 team, + 0x38 / + 0x40 ui_chara, + 0x48 colour,
 * + 0x49 CPU level. Who writes it (hardware write watch, run pnW): when the character select opens, 0x710179e440(table - 0x10,
 * player, source) copies each entry from the saved choices (a save with a CPU panel brings player 2 = CPU, ui_chara_random,
 * level 3); while the screen runs, the screen writes its own panels back (0x7101831c70 / 0x7101833020, from 0x7101847460).
 * The screen keeps its own panels: an entry changed while it runs is NOT taken (run pnE), and with player 1 alone the match
 * cannot be started (run pnA: "Ready to fight" never came). So the online session (AUTO P2, on with the handshake, off with
 * SSBU_RB_NET_AUTOP2=0) puts a CPU panel in entry 1 right after 0x710179e440 copies a "none" there: the screen opens with
 * player 2 = CPU (random), the player picks only his own fighter, and the handshake replaces player 2 by the other player.
 * Research / test switches (off by default):
 *   SSBU_DEBUG_PANELS=1      a log line whenever entry 0 or 1 changes (=2: also a write watch on their types, guest frames)
 *   SSBU_RB_NET_P2NONE=1     (test) entry 1 copied as "none" whatever the save says (a save without a CPU panel)
 *   SSBU_RB_NET_AUTOP2=0|1   off / on (default: on when the handshake is wanted) */
#define PANEL(i) (GA(0x7105307770ULL) + 0x1c8ull * (uint64_t)(i))
static ImportFn g_orig_panel_copy;
static int g_p2none = -1, g_autop2 = -1; static volatile LONG g_autop2_made;
static void panel_copy_hook(Ctx *c, void *user) {
    (void)user;
    uint64_t base = c->x[0]; uint32_t idx = (uint32_t)c->x[1];
    g_orig_panel_copy(c);
    if (idx != 1) return;
    uint64_t e = base + 0x10 + 0x1c8ull;
    if (g_p2none > 0 && rd32(e + 0x30) != 3) {
        printf("debug panels: entry 1 copied as type %u: made none (SSBU_RB_NET_P2NONE)\n", rd32(e + 0x30));
        wr32(e + 0x30, 3); wr32(e + 0x34, 0xffffffffu); wr64(e + 0x38, 0xc1ffff0000000000ull); wr64(e + 0x40, 0xc1ffff0000000000ull);
    }
    int nnldn_in_use(void);
    /* (0.2.17) nnldn_in_use, not nnldn_role: the game opens the room's fighter select before it creates the room, so the room maker
     * saw "CPU Lv. 3" in player 2's box and not the second player */
    if (g_autop2 > 0 && rd32(e + 0x30) == 3 && !nnldn_in_use()) {        /* the CPU panel a save brings (run pnB); (R3 pub2) never in Local Wireless: its fights are the game's own */
        wr8(e, 0xff); wr32(e + 0x28, 2); wr32(e + 0x34, 0xffffffffu);
        wr64(e + 0x38, 0xc100000fd5f7fa78ull); wr64(e + 0x40, 0xc100000fd5f7fa78ull); wr8(e + 0x48, 0); wr8(e + 0x49, 3);
        wr32(e + 0x30, 1);
        InterlockedIncrement(&g_autop2_made);
        printf("[rbnet] select screen: player 2 had no panel: a CPU panel put there for the online match (the other player takes it at the handshake; %ld so far)\n", (long)g_autop2_made);
    }
    fflush(stdout);
}
static DWORD WINAPI panel_thread(LPVOID arg) {
    (void)arg;
    const char *e1 = getenv("SSBU_DEBUG_PANELS");
    int show = e1 ? atoi(e1) : 0;
    uint8_t last[2][0x50]; memset(last, 0xee, sizeof last);
    { extern int g_game_build; for (int w = 0; g_game_build < 0 && w < 2400; w++) Sleep(50); }      /* (GA needs main.elf read: loose_host.c) */
    ULONGLONG t0 = GetTickCount64();
    if (show == 2) { uint64_t w[2] = {PANEL(1) + 0x30, PANEL(0) + 0x30}; watch_set(w, 2); }      /* research: who writes the types (guest frames printed) */
    for (;;) {
        Sleep(20);
        double t = (GetTickCount64() - t0) / 1000.0;
        uint8_t cur[2][0x50];
        if (!safe_read(PANEL(0), cur[0], 0x50) || !safe_read(PANEL(1), cur[1], 0x50)) continue;
        for (int i = 0; i < 2; i++) if (memcmp(cur[i], last[i], 0x50)) {
            memcpy(last[i], cur[i], 0x50);
            printf("debug panels: at %.2f s (set-ups %ld) entry %d: controller %02x number %d type %u team %d ui_chara %016llx / %016llx colour %u level %u\n", t, (long)g_dm_setups, i, cur[i][0],
                   *(int32_t *)(cur[i] + 0x28), *(uint32_t *)(cur[i] + 0x30), *(int32_t *)(cur[i] + 0x34), (unsigned long long)*(uint64_t *)(cur[i] + 0x38), (unsigned long long)*(uint64_t *)(cur[i] + 0x40), cur[i][0x48], cur[i][0x49]);
            fflush(stdout);
        }
    }
    return 0;
}
/* (Arena, online match) the screens before the fight (the versus splash: fighters, colours, "CPU" / "P2" labels) are the
 * agreed players, not this game's select screen (player 2 = the CPU panel put there for the online match, or the save's).
 * Written into the select screens' table entries 0 / 1 right after the game built player 2's record from them (the match
 * block itself is the handshake's, untouched by this). */
void hs_show_agreed(int idx) {
    if (idx != 1) return;
    for (int i = 0; i < 2; i++) {
        uint64_t e = PANEL(i);
        uint32_t t = rd32(e + 0x30);
        printf("[rbnet] select table entry %d: type %u ui_chara %016llx / %016llx colour %u -> human, %016llx colour %u (shown before the fight)\n", i, t,
               (unsigned long long)rd64(e + 0x38), (unsigned long long)rd64(e + 0x40), rd8(e + 0x48), (unsigned long long)g_hs_m.chara[i], g_hs_m.colour[i]);
        wr64(e + 0x38, g_hs_m.chara[i]); wr64(e + 0x40, g_hs_m.chara[i]); wr8(e + 0x48, g_hs_m.colour[i]); wr32(e + 0x30, 0);
    }
    fflush(stdout);
}
/* (0.2.23) The game's allocator (jemalloc) after a rollback. Crash reports of two PCs: bitmap_sfu 0x7103932ba0 on a slab whose free count is
 * not 0 although its bitmap has no free bit: the first set bit of 0 is -1 and the read lands 32 GB away. What makes a slab so is not known
 * yet (two checks around each rollback load say when). extent_nfree_get 0x7103931e50 runs under the bin's lock: for such a slab it now
 * answers 0, so the allocator treats it as full and takes another one; the line in the log says it happened.
 * Bin: arena + 0x3e20 + 0x138 * n, its slab + 0x98; slab: + 0 bits (size class at bit 18, 8 bits; free count at bit 26, 14 bits), + 0x40
 * bitmap; 0x7104862158 + 0x58 * n: the bin's facts, + 0x18 its bitmap's (+ 8 levels, + 0x10 each level's first word).
 * SSBU_ALLOC_CHECK=0: none of this. SSBU_ALLOC_REPAIR=0 (test): no answer of 0. SSBU_ALLOC_TEST_BREAK=<n> (test): at the n-th check the
 * current slab of each bin of the first arena is made so. */
#define AC_NARENA 8
static uint64_t g_ac_arena[AC_NARENA]; static ImportFn g_orig_fill, g_orig_nfree; static long long g_ac_bad, g_ac_checks; static volatile LONG g_ac_saved;
static int g_ac_repair = 1, g_ac_break;
static void ac_nfree_hook(Ctx *c, void *user) {
    (void)user;
    uint64_t slab = c->x[0];
    g_orig_nfree(c);
    if (!c->x[0] || !g_ac_repair) return;
    uint64_t bits = *(uint64_t *)(uintptr_t)slab, ind = (bits >> 18) & 0xff;
    if (ind >= 36) return;
    uint64_t facts = GA(0x7104862158ULL) + 0x58 * ind;
    uint32_t nl = *(uint32_t *)(uintptr_t)(facts + 0x18 + 8);
    if (nl < 1 || nl > 8) return;
    uint64_t top = *(uint64_t *)(uintptr_t)(facts + 0x18 + 0x10 + 8ull * (nl - 1));
    if (*(uint64_t *)(uintptr_t)(slab + 0x40 + top * 8)) return;
    LONG n = InterlockedIncrement(&g_ac_saved);
    if (n <= 20) { printf("alloc check: slab %llx of bin %llu has %llu free by its count and no free bit: taken as full (%ld so far)\n", (unsigned long long)slab, (unsigned long long)ind, (unsigned long long)c->x[0], n); fflush(stdout); }
    c->x[0] = 0;
}
static void ac_fill_hook(Ctx *c, void *user) {
    (void)user;
    uint64_t a = c->x[1];
    for (int i = 0; i < AC_NARENA; i++) {
        uint64_t have = g_ac_arena[i];
        if (have == a) break;
        if (!have && InterlockedCompareExchange64((volatile LONG64 *)&g_ac_arena[i], (LONG64)a, 0) == 0) { printf("alloc check: arena %d at %llx\n", i, (unsigned long long)a); fflush(stdout); break; }
    }
    g_orig_fill(c);
}
static int ac_read(uint64_t a, void *out, size_t n) { SIZE_T got = 0; return ReadProcessMemory(GetCurrentProcess(), (const void *)(uintptr_t)a, out, n, &got) && got == n; }
void dm_alloc_check(const char *when, long long frame) {
    if (!g_orig_fill) return;
    g_ac_checks++;
    { static int all = -1; if (all < 0) all = getenv("SSBU_ALLOC_CHECK_ALL") != NULL; if (!all && !g_ac_break && (g_ac_checks & 7)) return; }      /* (rollback 8 ms) 0.2 ms of each load: every 8th */
    if (g_ac_break && g_ac_checks == g_ac_break && g_ac_arena[0]) {
        for (int b = 0; b < 36; b++) {
            uint64_t slab = *(uint64_t *)(uintptr_t)(g_ac_arena[0] + 0x3e20 + 0x138ull * (unsigned)b + 0x98);
            if (!slab || !((*(uint64_t *)(uintptr_t)slab >> 26) & 0x3fff)) continue;
            uint64_t facts = GA(0x7104862158ULL) + 0x58ull * (unsigned)b; uint32_t nl = *(uint32_t *)(uintptr_t)(facts + 0x18 + 8);
            uint64_t end = *(uint64_t *)(uintptr_t)(facts + 0x18 + 0x10 + 8ull * nl);
            memset((void *)(uintptr_t)(slab + 0x40), 0, (size_t)end * 8);
            printf("alloc check: TEST: slab %llx of bin %d made to have no free bit\n", (unsigned long long)slab, b); fflush(stdout);
        }
    }
    for (int ai = 0; ai < AC_NARENA && g_ac_arena[ai]; ai++) {
        for (int b = 0; b < 36; b++) {
            uint64_t bin = g_ac_arena[ai] + 0x3e20 + 0x138ull * (unsigned)b, slab = 0, bits = 0, facts = GA(0x7104862158ULL) + 0x58ull * (unsigned)b;
            uint64_t lv[3] = {0, 0, 0}, nl = 0, w[64];
            if (!ac_read(bin + 0x98, &slab, 8) || !slab || !ac_read(slab, &bits, 8)) continue;
            if (!ac_read(facts + 0x18 + 8, &nl, 8) || !ac_read(facts + 0x18 + 0x10, lv, sizeof lv)) continue;
            nl &= 0xffffffffu;
            if (nl < 1 || lv[1] <= lv[0] || lv[1] > 64) continue;
            if (!ac_read(slab + 0x40 + lv[0] * 8, w, (size_t)(lv[1] - lv[0]) * 8)) continue;
            unsigned nfree = (unsigned)((bits >> 26) & 0x3fff), ones = 0;
            for (uint64_t k = 0; k < lv[1] - lv[0]; k++) ones += (unsigned)__popcnt64(w[k]);
            if (nfree != ones && g_ac_bad++ < 40) {
                printf("alloc check: BAD (%s, rollback frame %lld): arena %d bin %d slab %llx: free count %u, free bits %u\n", when, frame, ai, b, (unsigned long long)slab, nfree, ones);
                fflush(stdout);
            }
        }
    }
}
/* ---- (Battle Arena, SSBU_BA_SQUAD_CSS, RESEARCH probe) the room's character select in the Squad Strike layout.
 * The game's Squad Strike opens the same CharaSelectScene with no parameter (MeleeTeamCompeSequenceScene state 0x710233cfd0,
 * scene change at 0x710233db80); the layout comes from the current sequence (0x71053040f0 = SEQUENCE_MELEE_TEAM_COMPE).
 * SSBU_DEBUG_SCENES=1: every scene change (0x7103726120) logged with its name, caller and the current sequence. */
static ImportFn g_orig_scene_change, g_orig_vs_css;
static uint32_t g_bacss_saved; static int g_bacss_on;
#define BACSS_AREA GA(0x7105309974ULL)
static void bacss_dump(const char *why) {
    printf("[squadcss] %s: sequence %08x\n", why, rd32(GA(0x71053040f0ULL)));
    for (int s = 0; s < 2; s++) for (int k = 0; k < 5; k++) {
        char t[64]; snprintf(t, sizeof t, "[squadcss]  side %d chip %d A", s, k); hex_lines(t, BACSS_AREA + 0x54 + 0x1428ull * s + 0x1c8ull * k, 0x60);
        snprintf(t, sizeof t, "[squadcss]  side %d chip %d B", s, k); hex_lines(t, BACSS_AREA + 0x54 + 0x8e8 + 0x1428ull * s + 0x240ull * k, 0x40);
    }
    fflush(stdout);
}
static int g_css_round;      /* (test aid) Squad Strike select screens opened */
static void scene_change_hook(Ctx *c, void *user) {
    (void)user;
    char nm[72] = {0}, own[72] = {0}; uint32_t n = rd32(c->x[1] + 4), n2 = c->x[2] ? rd32(c->x[2] + 4) : 0;
    if (n < 64) safe_read(c->x[1] + 8, nm, n);
    if (n2 < 64 && c->x[2]) safe_read(c->x[2] + 8, own, n2);
    printf("[squadcss] scene change: %s (from %s), param %llx, caller %llx, sequence %08x\n", nm, own, (unsigned long long)c->x[3], (unsigned long long)(c->x[30] - 4), rd32(GA(0x71053040f0ULL)));
    fflush(stdout);
    if (rd32(GA(0x71053040f0ULL)) == 0x02020000u && n < 32) { char m[48]; snprintf(m, sizeof m, "sc_%s", nm); void input_mark(const char *what); input_mark(m);
        static struct { char nm[40]; int n; } cnt[24]; int k = 0; while (k < 24 && cnt[k].nm[0] && strcmp(cnt[k].nm, m)) k++;      /* (test aid) and sc_<scene>#<n> from the 2nd time on */
        if (!strcmp(m, "sc_CharaSelectScene")) g_css_round++;
        if (k < 24) { if (!cnt[k].nm[0]) snprintf(cnt[k].nm, sizeof cnt[k].nm, "%s", m); if (++cnt[k].n > 1) { char m2[56]; snprintf(m2, sizeof m2, "%s#%d", m, cnt[k].n); input_mark(m2); } } }      /* (test aid) SSBU_SEQ @sc_<scene> */
    g_orig_scene_change(c);
}
static void vs_css_hook(Ctx *c, void *user) {
    (void)user;
    uint64_t self = c->x[0];
    int rbn_ba_mode(void), nnldn_role(void);
    if (g_bacss == 2 && rbn_ba_mode() > 0 && nnldn_role() > 0) {
        volatile uint32_t *seq = (volatile uint32_t *)(uintptr_t)GA(0x71053040f0ULL);
        if (rd32(self + 0x124) == 0 && !g_bacss_on) {
            if (!rd64(GA(0x7105327e18ULL))) {         /* the Squad Strike select data (made by MeleeTeamCompeSequenceScene's set-up, 0x710233c070..): made here the same way */
                uint64_t save[32]; memcpy(save, c->x, sizeof save);
                c->x[0] = 0x10; c->x[1] = 8; call_guest(c, GA(0x710392dce0ULL)); uint64_t o = c->x[0];
                if (o) { c->x[0] = o; call_guest(c, GA(0x7102349480ULL)); wr64(GA(0x7105327e18ULL), o); }
                memcpy(c->x, save, sizeof save);
                printf("[squadcss] Squad Strike select data made: %llx -> %llx\n", (unsigned long long)o, (unsigned long long)(o ? rd64(o) : 0));
            }
            g_bacss_saved = *seq; *seq = 0x02020000u; g_bacss_on = 1;
            printf("[squadcss] the room's character select opens with the Squad Strike sequence (was %08x; parameter byte %u)\n", g_bacss_saved, rd8(self + 0x139)); fflush(stdout);
        } else if (g_bacss_on) {
            uint64_t mgr = rd64(self), m28 = rd64(mgr + 0x28);
            if (rd64(m28 + 0x60) == 0 && rd64(m28 + 0x30) == rd64(m28 + 0x38) && rd64(rd64(mgr + 0x20) + 0x10) == 0) {
                uint32_t res = rd32(mgr + 0x40);
                char w[64]; snprintf(w, sizeof w, "character select done (result %u)", res); bacss_dump(w);
                *seq = g_bacss_saved; g_bacss_on = 0;
            }
        }
    }
    g_orig_vs_css(c);
}
/* the Local sequence's state that starts the mode's sequence (0x710232f690, LocalSequenceScene; also a Local Wireless room's):
 * 0x02010000 -> MeleeNormalSequenceScene, 0x02020000 -> MeleeTeamCompeSequenceScene (0x710232f808..). A room's other games
 * follow the room maker's sequence (run cssp2: the joiner started MeleeTeamCompeSequenceScene by itself). */
static ImportFn g_orig_local_launch, g_orig_tc_css;
static void local_launch_hook(Ctx *c, void *user) {
    (void)user;
    int rbn_ba_mode(void), nnldn_role(void);
    volatile uint32_t *seq = (volatile uint32_t *)(uintptr_t)GA(0x71053040f0ULL);
    if (g_bacss == 1 && rd32(c->x[0] + 0x194) == 0) {
        printf("[squadcss] the room's sequence starts: %08x (Battle Arena %d, room role %d)\n", *seq, rbn_ba_mode(), nnldn_role());
        if (rbn_ba_mode() == 1 && nnldn_role() == 1 && *seq == 0x02010000u) { *seq = 0x02020000u; printf("[squadcss] Battle Arena room maker: the room plays the game's own Squad Strike (sequence 02020000)\n"); }
        fflush(stdout);
    }
    g_orig_local_launch(c);
}
/* The Squad Strike select screen's chips: side s (0 = the room maker, 1 = the joiner), fighter k: entry A at BACSS_AREA + 0x54 +
 * 0x1428 * s + 0x1c8 * k (0x7101787ce0 copies A + 0x28.. to the panel state + 0x70..: ui_chara A + 0x40 -> state + 0x88, colour
 * A + 0x48 -> + 0x90, type A + 0x30 -> + 0x78). */
#define BACSS_CHIP(s, k) (BACSS_AREA + 0x54 + 0x1428ull * (uint64_t)(s) + 0x1c8ull * (uint64_t)(k))
static int bacss_chips(int s) { int n = 0; for (int k = 0; k < 3; k++) { uint64_t v = rd64(BACSS_CHIP(s, k) + 0x40); if ((v >> 56) == 0xc1 && (v & 0xffffffffffull)) n++; } return n; }
static void bacss_show(const char *why) {
    printf("[squadcss] %s: side 1 %016llx %016llx %016llx, side 2 %016llx %016llx %016llx\n", why,
           (unsigned long long)rd64(BACSS_CHIP(0, 0) + 0x40), (unsigned long long)rd64(BACSS_CHIP(0, 1) + 0x40), (unsigned long long)rd64(BACSS_CHIP(0, 2) + 0x40),
           (unsigned long long)rd64(BACSS_CHIP(1, 0) + 0x40), (unsigned long long)rd64(BACSS_CHIP(1, 1) + 0x40), (unsigned long long)rd64(BACSS_CHIP(1, 2) + 0x40));
    fflush(stdout);
}
/* (RESEARCH, SSBU_SQ_CSSDUMP=<folder>) the main module's data segment, the sequence object, the select data object and the room's net
 * object, written at calls 5 and 200 of each Squad Strike select screen (to compare a room's first and second select screen) */
static void bacss_mem_dump(uint64_t self, int round, int call) {
    const char *dir = getenv("SSBU_SQ_CSSDUMP"); if (!dir || !*dir) return;
    int nnldn_role(void); char fn[400]; static uint8_t buf[0x10000];
    struct { const char *nm; uint64_t a; size_t n; } r[4] = {
        {"data", GA(0x7104f16000ULL), 0x2531000}, {"seq", self, 0x400}, {"tcobj", sr64(GA(0x7105327e18ULL)), 0x3b8},
        {"net", sr64(sr64(sr64(GA(0x71052c3c58ULL)) + 8) + 0x88), 0x28000} };
    for (int i = 0; i < 4; i++) {
        snprintf(fn, sizeof fn, "%s/css_role%d_r%d_c%d_%s.bin", dir, nnldn_role(), round, call, r[i].nm);
        FILE *f = fopen(fn, "wb"); if (!f) continue;
        for (size_t o = 0; o < r[i].n; o += sizeof buf) { size_t k = r[i].n - o < sizeof buf ? r[i].n - o : sizeof buf; if (!r[i].a || !safe_read(r[i].a + o, buf, k)) memset(buf, 0, k); fwrite(buf, 1, k, f); }
        fclose(f);
    }
    printf("[squadcss] RESEARCH: memory dumped at select screen %d call %d (seq %llx)\n", round, call, (unsigned long long)self); fflush(stdout);
}
/* (Battle Arena, game's squad, SSBU_BA_SQUAD_CSS=1; SSBU_SQ_KEEPHOST=0 off) back in the room after a match, the JOINER's copy of the
 * host's players (room net object [[[0x71052c3c58] + 8] + 0x88] + 0x24da0: the host station's 5 player entries of 0x1e0, + 0 station
 * byte, + 0x21 kind; runs csss2 / csss6: 00 / 01 at the first select screen, ff / 0f = an empty entry at the second) is found reset,
 * and the room's net update (0x71016e5310) then writes the select screen's side 1 as a free local seat: the joiner's own hand went to
 * P1 and its real side 2 showed "Make everyone CPU" (cssh5, cssh6), the host waited with the joiner's old squad. The entries as they
 * were when the first select screen ended are written back while they read empty. */
static uint8_t g_kh_buf[0x968]; static int g_kh_ok, g_kh_after;
static void bacss_after_match(void) { g_kh_after = 1; }
static uint64_t kh_net(void) { return sr64(sr64(sr64(GA(0x71052c3c58ULL)) + 8) + 0x88); }
static void bacss_keep_host(int capture) {
    static int on = -1; if (on < 0) { const char *e = getenv("SSBU_SQ_KEEPHOST"); on = e && e[0] == '1';      /* (OFF by default: run csst1, written back but the joiner still lands on P1) */ }
    int nnldn_role(void); if (!on || g_bacss != 1 || nnldn_role() != 2) return;
    uint64_t net = kh_net(); if (!net) return;
    if (capture) { uint8_t b[0x968]; int ok = safe_read(net + 0x24da0, b, sizeof b); if (ok && b[0] != 0xff) { memcpy(g_kh_buf, b, sizeof b); g_kh_ok = 1; } g_kh_after = 0;
        printf("[squadcss] joiner: the host's player entries at the end of the select screen: net %llx, station %u kind %u%s\n", (unsigned long long)net, ok ? b[0] : 0xffu, ok ? b[0x21] : 0xffu, g_kh_ok ? " (kept)" : " (NOT kept)"); fflush(stdout); return; }
    {   static uint8_t last = 0xfe; uint8_t now = rd8(net + 0x24da0); if (now != last) { printf("[squadcss] joiner: the host's first player entry now: station %u kind %u (net %llx; kept %d, after a match %d)\n", now, rd8(net + 0x24da0 + 0x21), (unsigned long long)net, g_kh_ok, g_kh_after); fflush(stdout); last = now; } }
    if (!g_kh_after && rd8(net + 0x24da0) != 0xff) { safe_read(net + 0x24da0, g_kh_buf, sizeof g_kh_buf); g_kh_ok = 1; }      /* (run csss8: the entry reads empty again once the host picks; the last good one is kept) */
    if (!g_kh_ok || !g_kh_after || rd8(net + 0x24da0) != 0xff) return;
    for (size_t k = 0; k < sizeof g_kh_buf; k++) wr8(net + 0x24da0 + k, g_kh_buf[k]);
    static int said; if (said++ < 5) { printf("[squadcss] joiner: the host's player entries in the room's net object read empty after the match: written back as at the first select screen (station %u, kind %u)\n", g_kh_buf[0], g_kh_buf[0x21]); fflush(stdout); }
}
static void tc_css_hook(Ctx *c, void *user) {      /* MeleeTeamCompeSequenceScene's character select state 0x710233cfd0 */
    (void)user;
    uint64_t self = c->x[0];
    bacss_keep_host(0);
    {   static int rd_seen = -1, calls; if (g_css_round != rd_seen) { rd_seen = g_css_round; calls = 0; } calls++;
        if (calls == 5 || calls == 200) bacss_mem_dump(self, g_css_round, calls);
        /* (Battle Arena, SSBU_BA_SQUAD_RULES=0 off) each game picked its Squad Strike ruleset from its OWN save (MeleeRuleScene): both are
         * set to the room's rules before the fighters' records are built: the rule struct at 0x7105309974 (the squad builder 0x7102311f00
         * copies it into every panel state + 4 .. + 0x1b): + 1 rule kind 1 (stock), + 6 stocks 1, + 0x2c style 2 (Tag Team: match block
         * + 0x39 = 3 in every Tag Team run; checked and logged at the squad's records) */
        static int rules = -1; if (rules < 0) { const char *e = getenv("SSBU_BA_SQUAD_RULES"); rules = !(e && e[0] == '0'); }
        int rbn_ba_mode(void);
        if (rules && rbn_ba_mode() > 0) { uint64_t r = GA(0x7105309974ULL); uint8_t k = rd8(r + 1), s = rd8(r + 6), y = rd8(r + 0x2c);
            if (k != 1 || s != 1 || y != 2) { wr8(r + 1, 1); wr8(r + 6, 1); wr8(r + 0x2c, 2);
                printf("[squadcss] Battle Arena room rules: rule kind %u -> 1 (stock), stocks %u -> 1, style %u -> 2 (Tag Team)\n", k, s, y); fflush(stdout); }
            else if (calls == 1) { printf("[squadcss] Battle Arena room rules: stock, 1 stock, Tag Team (as picked)\n"); fflush(stdout); } } }
    int nnldn_role(void); int mine = nnldn_role() == 2 ? 1 : 0;
    {   static int last_n[2] = {-1, -1};          /* (test aid) @sq_mine3: this game's side has its 3 fighters */
        for (int s = 0; s < 2; s++) { int n = bacss_chips(s); if (n != last_n[s]) { last_n[s] = n; char w[48]; snprintf(w, sizeof w, "side %d has %d fighter(s)", s + 1, n); bacss_show(w); } }
        {   int n = bacss_chips(mine); void input_mark(const char *what); static int said2, said3, round = 1;      /* (the entries lag the screen by one pick: 2 here = 3 shown) */
            static int seen = 1; if (g_css_round > seen) { seen = g_css_round; said2 = said3 = 0; round = seen; }      /* (test aid) a new select screen: the marks again, as sq_mine2#<round> */
            char m2[24], m3[24]; if (round > 1) { snprintf(m2, sizeof m2, "sq_mine2#%d", round); snprintf(m3, sizeof m3, "sq_mine3#%d", round); } else { strcpy(m2, "sq_mine2"); strcpy(m3, "sq_mine3"); }
            if (n >= 2 && !said2++) input_mark(m2);
            if (n >= 3 && !said3++) input_mark(m3); } }
    if (rd32(self + 0x114)) {
        uint64_t mgr = rd64(self), m28 = rd64(mgr + 0x28);
        if (rd64(m28 + 0x60) == 0 && rd64(m28 + 0x30) == rd64(m28 + 0x38) && rd64(rd64(mgr + 0x20) + 0x10) == 0) {
            static uint32_t last = 0xffffffffu; uint32_t res = rd32(mgr + 0x40);
            if (res != last) {
                last = res; char w[64]; snprintf(w, sizeof w, "Squad Strike select done (result %u)", res); bacss_show(w);
                if (res == 1) bacss_keep_host(1);
                /* TEST ONLY: SSBU_SQ_CSS_TEST_PICKS=<a>,<b>,<c>/<d>,<e>,<f> (the same on both games): the chips' fighters written here */
                const char *e = getenv("SSBU_SQ_CSS_TEST_PICKS");
                if (e && *e) {
                    char buf[200]; snprintf(buf, sizeof buf, "%s", e); int sd = 0, k = 0;
                    for (char *t = buf; *t && sd < 2; ) {
                        char *end = t + strcspn(t, ",/"); char sep = *end; *end = 0;
                        char nm[64]; snprintf(nm, sizeof nm, "ui_chara_%s", t);
                        if (k < 3) { wr64(BACSS_CHIP(sd, k) + 0x40, 0xc100000000000000ull | hash40(nm)); k++; }
                        if (!sep) break;
                        if (sep == '/') { sd++; k = 0; }
                        t = end + 1;
                    }
                    bacss_show("TEST: chips written (SSBU_SQ_CSS_TEST_PICKS)");
                }
            }
        }
    }
    g_orig_tc_css(c);
}
/* (RESEARCH, SSBU_DEBUG_SCENES=2) MeleeTeamCompeSequenceScene's state 0x710233de20 waits for TeamCompeMatchupScene to end:
 * the current scene object ([[mgr + 0x28] + 0x60]), its vtable and its first bytes, now and then */
static ImportFn g_orig_tc_match;
static void tc_match_hook(Ctx *c, void *user) {
    (void)user;
    static unsigned calls; static uint64_t last_sc; static uint8_t last[0x400];
    uint64_t mgr = rd64(c->x[0]), m28 = mgr ? rd64(mgr + 0x28) : 0, sc = m28 ? rd64(m28 + 0x60) : 0;
    static int last_st = -2; int st_now = sc && rd64(sc + 0x58) ? (int)rd32(rd64(sc + 0x58) + 0x128) : -1;
    {   static uint64_t prev_sc; if (prev_sc && !sc && mgr) { printf("[squadcss] matchup scene ended: result %u (byte 0x710530996f %u, Local Wireless byte %u, sequence %08x)\n", rd32(mgr + 0x40), rd8(GA(0x710530996fULL)), rd8(GA(0x71053040f4ULL)), rd32(GA(0x71053040f0ULL))); fflush(stdout); } prev_sc = sc; }
    {   const char *d = getenv("SSBU_DEBUG_SCENES"); if (!(d && atoi(d) >= 2)) { calls++; g_orig_tc_match(c); return; } }
    if (sc && (sc != last_sc || calls % 600 == 0 || st_now != last_st)) {
        last_st = st_now;
        uint8_t cur[0x400]; safe_read(sc, cur, sizeof cur);
        printf("[squadcss] matchup wait %u: scene %llx vtable %llx (mgr %llx, m28 %llx)\n", calls, (unsigned long long)sc, (unsigned long long)rd64(sc), (unsigned long long)mgr, (unsigned long long)m28);
        {   uint64_t sm = rd64(sc + 0x58), fo = rd64(rd64(GA(0x710532e7c8ULL)) + 8);      /* the scene's state machine (0x710197f170): index +0x124 into the ids at +0xd8, state +0x128, frames +0x12c */
            if (sm) { printf("[squadcss]  states: index %d state %d frames %d prev %d | sm+8 %llx\n", (int)rd32(sm + 0x124), (int)rd32(sm + 0x128), (int)rd32(sm + 0x12c), (int)rd32(sm + 0x130), (unsigned long long)rd64(sm + 8)); hex_lines("[squadcss]  sm", sm + 0xd8, 0x80); }
            if (fo) hex_lines("[squadcss]  worker 0x710532e7c8", fo + 0x50, 0x30); }
        for (int o = 0; o < 0x400; o += 0x20) if (sc != last_sc || memcmp(cur + o, last + o, 0x20)) { char t[48]; snprintf(t, sizeof t, "[squadcss]  scene +%03x", o); hex_lines(t, sc + o, 0x20); }
        memcpy(last, cur, sizeof last); last_sc = sc; fflush(stdout);
    }
    calls++;
    g_orig_tc_match(c);
}
/* TeamCompeMatchupScene's state 7 (0x710197d5f0, this = the scene's state machine): after the VS screen it waits for the matchup
 * UI's decision w = (ui + 0x248 ? 0 : ui + 0x2a0), ui = [[sm + 8] + 0x80]; 1 = to the fight. Offline (no Local Wireless flag at
 * 0x71053040f4) w = 1 at once gives result 3 and the end state 9. In a room (online path, 0x710197d6dc..) w = 1 marks this station
 * ready (0x71016a1f90, sm + 0x14c = 1) and then waits for the room's "all ready" byte (net + 0x26a62, net = [[[0x71052c3c58] + 8]
 * + 0x88]) that our virtual LDN never brings (runs cssq9 / cssq11 / cssm3: both games in state 7 for minutes).
 * SSBU_BA_SQUAD_CSS=1 (SSBU_BA_SQUAD_VS=0 off): once this game is ready for 30 frames, state 7 runs once as offline (flag cleared for
 * the call): result 3 -> MeleeScene; the two games meet again at the rollback handshake on the record builder. */
static ImportFn g_orig_tc_vs; static int g_bacss_vs;
static void tc_vs_hook(Ctx *c, void *user) {
    (void)user;
    uint64_t sm = c->x[0], ui = rd64(rd64(sm + 8) + 0x80), nc = rd64(rd64(rd64(GA(0x71052c3c58ULL)) + 8) + 0x88), net = nc;
    uint32_t frames = rd32(sm + 0x12c), w248 = ui ? rd32(ui + 0x248) : 0, w2a0 = ui ? rd32(ui + 0x2a0) : 0, ready = rd32(sm + 0x14c);
    uint8_t all = net ? rd8(net + 0x26a62) : 0, lw = rd8(GA(0x71053040f4ULL));
    static uint32_t ready_at; int bypass = 0;
    if (frames <= 1) ready_at = 0;
    if ((ready == 1 || (ready == 3 && w248 == 0 && w2a0 == 3)) && !ready_at) ready_at = frames;
    if (getenv("SSBU_DEBUG_SCENES") && (frames % 300 == 1 || (ready == 1 && ready_at == frames)))
        { printf("[squadcss] matchup state 7: frame %u ui %llx +248 %u +2a0 %u, ready %u, all-ready byte %u, local wireless %u\n", frames, (unsigned long long)ui, w248, w2a0, ready, all, lw); fflush(stdout); }
    if (g_bacss_vs && lw && ready == 1 && ready_at && frames >= ready_at + 30 && !all) bypass = 1;
    /* (after a match, runs cssr1 / cssr5) the result screen's OK gives w = 3: in a room the state then waits for the host station's record
     * (net + 0x26a78); with ours it ended either as result 5 (stage vote, back to the room's select screen: cssr1) or, later, result 4
     * (the end of the Squad Strike sequence and DestroyNetwork: cssr5, cssn8). Run as offline too: w = 3 -> result 5 on both games. */
    if (g_bacss_vs && lw && ready == 3 && w248 == 0 && w2a0 == 3 && ready_at && frames >= ready_at + 30) bypass = 1;
    if (bypass) {
        volatile uint8_t *f = (volatile uint8_t *)(uintptr_t)GA(0x71053040f4ULL);
        printf("[squadcss] matchup: this game ready for %u frames, the room's all-ready never comes: the VS screen ends as offline (result 3 -> the fight)\n", frames - ready_at); fflush(stdout);
        *f = 0; g_orig_tc_vs(c); *f = lw;
        printf("[squadcss] matchup: state 7 done: result %u, next state id %d\n", rd32(sm + 0x138), (int)rd32(sm + 0xd8 + 4 * rd32(sm + 0x124))); fflush(stdout);
        return;
    }
    g_orig_tc_vs(c);
}
/* (Battle Arena, game's squad) the fight set-up 0x71014d8620 stores the next value of the global xorshift128 (0x71052381c0) in
 * match block + 0x98 (the fight's script generators come from it). The handshake set that generator from the agreed seed, but in
 * the room's Squad Strike each game draws from it a different number of times between the handshake (the squad builder) and this
 * set-up (run cssm6: + 0x98 differed, DESYNC at frame 1). SSBU_BA_SQUAD_CSS=1 (SSBU_BA_SQUAD_RESEED=0 off): the generator is set
 * again from the agreed seed when the set-up starts. */
static ImportFn g_orig_fight_setup; static int g_bacss_reseed;
static void fight_setup_hook(Ctx *c, void *user) {
    (void)user;
    if (g_bacss_reseed && g_hs_wanted && g_hs_ok && g_hs_lwr && !g_hs_local && rd32(GA(0x71053040f0ULL)) == 0x02020000u) {
        uint64_t g = GA(0x71052381c0ULL), z = g_hs_m.seed ^ 0x5351554144ull;      /* ("SQUAD": not the handshake's own sequence again) */
        printf("[squadcss] fight set-up: the global generator %08x %08x %08x %08x set again from the agreed seed\n", rd32(g), rd32(g + 4), rd32(g + 8), rd32(g + 12));
        for (int k = 0; k < 4; k++) { z += 0x9E3779B97F4A7C15ull; uint64_t y = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ull; y = (y ^ (y >> 27)) * 0x94D049BB133111EBull; y ^= y >> 31; wr32(g + 4ull * k, (uint32_t)y | (k == 0)); }
        /* run cssm8: + 0x98 is NOT drawn here in the room's Squad Strike (+ 0x9c = 0: the team compe sequence drew it before, on each
         * game from its own generator: different at the handshake already); + 0x9c = 1 as in a room's versus match: drawn now */
        if (!rd8(MELEE_INFO + 0x9c)) { wr8(MELEE_INFO + 0x9c, 1); printf("[squadcss] fight set-up: match block + 0x9c 0 -> 1 (+ 0x98 %08x drawn again from the agreed generator)\n", rd32(MELEE_INFO + 0x98)); }
        fflush(stdout);
    }
    g_orig_fight_setup(c);
    if (getenv("SSBU_DEBUG_SCENES") && rd32(GA(0x71053040f0ULL)) == 0x02020000u) { uint64_t g = GA(0x71052381c0ULL);
        printf("[squadcss] fight set-up done: match block + 0x98 %08x + 0x9c %u, generator %08x %08x %08x %08x\n", rd32(MELEE_INFO + 0x98), rd8(MELEE_INFO + 0x9c), rd32(g), rd32(g + 4), rd32(g + 8), rd32(g + 12)); fflush(stdout); }
}
/* (RESEARCH, SSBU_SQ_MSGLOG=1) the room's messages: 0x7101685cb0 (net, w1 id, x2 data, w3 size) sends one, 0x7101698160 (net, x1 station,
 * w2 id, x3 {size, data}) handles one received (ids 0xa3 .. 0xce; 0x71016f0550 the ones 0xb3 .. 0xce) */
static ImportFn g_orig_msg_send, g_orig_msg_recv;
static void msg_note(const char *dir, unsigned id, long st, unsigned size, uint64_t data) {
    static unsigned cnt[2][0x400]; unsigned k = id & 0x3ff, d = dir[0] == 's';
    if (++cnt[d][k] <= 6 || cnt[d][k] % 200 == 0) { long long rbn_now(void);
        char t[100] = {0}; int n = 0; for (unsigned j = 0; data && j < size && j < 24; j++) { uint8_t b = 0; safe_read(data + j, &b, 1); n += snprintf(t + n, sizeof t - n, "%02x", b); }
        printf("[squadcss] msg %s id %x station %ld size %u (#%u, frame %lld, scene round %d): %s\n", dir, id, st, size, cnt[d][k], rbn_now(), g_css_round, t); fflush(stdout); }
}
static void msg_send_hook(Ctx *c, void *user) { (void)user; msg_note("sent", (unsigned)c->x[1], -1, (unsigned)c->x[3], c->x[2]); g_orig_msg_send(c); }
static void msg_recv_hook(Ctx *c, void *user) { (void)user; uint64_t m = c->x[3]; msg_note("received", (unsigned)(c->x[2] & 0xffff), (long)(c->x[1] & 0xff), m ? rd32(m) : 0, m ? rd64(m + 8) : 0); g_orig_msg_recv(c); }
static void bacss_init(void) {
    if (getenv("SSBU_SQ_MSGLOG")) printf("[squadcss] RESEARCH: room message hooks %d %d\n", hook_install(GA(0x7101685cb0ULL), msg_send_hook, NULL, &g_orig_msg_send), hook_install(GA(0x7101698160ULL), msg_recv_hook, NULL, &g_orig_msg_recv));
    const char *e = getenv("SSBU_BA_SQUAD_CSS"); g_bacss = e && *e ? atoi(e) : 0;
    {   const char *d = getenv("SSBU_DEBUG_SCENES"); if (d && atoi(d) >= 1) printf("[squadcss] RESEARCH: hook on the matchup wait 0x710233de20: id %d\n", hook_install(GA(0x710233de20ULL), tc_match_hook, NULL, &g_orig_tc_match)); }
    if (getenv("SSBU_DEBUG_SCENES")) printf("[squadcss] hook on the scene change: id %d\n", hook_install(GA(0x7103726120ULL), scene_change_hook, NULL, &g_orig_scene_change));
    if (g_bacss == 2) printf("[squadcss] EXPERIMENT: Battle Arena rooms' character select in the Squad Strike layout: hook %d\n", hook_install(GA(0x7102334f50ULL), vs_css_hook, NULL, &g_orig_vs_css));
    if (g_bacss == 1) printf("[squadcss] Battle Arena rooms play the game's own Squad Strike: hooks %d %d\n", hook_install(GA(0x710232f690ULL), local_launch_hook, NULL, &g_orig_local_launch), hook_install(GA(0x710233cfd0ULL), tc_css_hook, NULL, &g_orig_tc_css));
    if (g_bacss == 1) { const char *r = getenv("SSBU_BA_SQUAD_RESEED"); g_bacss_reseed = !(r && r[0] == '0');
        if (g_bacss_reseed) printf("[squadcss] hook on the fight set-up 0x71014d8620: id %d (the global generator set again from the agreed seed)\n", hook_install(GA(0x71014d8620ULL), fight_setup_hook, NULL, &g_orig_fight_setup)); }
    if (g_bacss == 1) { const char *v = getenv("SSBU_BA_SQUAD_VS"); g_bacss_vs = !(v && v[0] == '0');
        printf("[squadcss] hook on the matchup's state 7 (0x710197d5f0): id %d (the VS screen ends as offline once this game is ready: %s)\n", hook_install(GA(0x710197d5f0ULL), tc_vs_hook, NULL, &g_orig_tc_vs), g_bacss_vs ? "on" : "off, SSBU_BA_SQUAD_VS=0"); }
    fflush(stdout);
}
void debug_mode_init(void) {
    bacss_init();
    {   const char *e = getenv("SSBU_ALLOC_CHECK");
        if (!(e && e[0] == '0')) {
            e = getenv("SSBU_ALLOC_REPAIR"); g_ac_repair = !(e && e[0] == '0');
            e = getenv("SSBU_ALLOC_TEST_BREAK"); g_ac_break = e ? atoi(e) : 0;
            hook_install(GA(0x7103931cb0ULL), ac_fill_hook, NULL, &g_orig_fill);
            hook_install(GA(0x7103931e50ULL), ac_nfree_hook, NULL, &g_orig_nfree);
        } }
    if (getenv("SSBU_DEBUG_PANELS")) CreateThread(NULL, 0, panel_thread, NULL, 0, NULL);
    const char *end = getenv("SSBU_DEBUG_END"), *list = getenv("SSBU_DEBUG_MATCH_LIST");
    if (end && atof(end) > 0) { g_end_limit = (LONG64)(strchr(end, 's') ? atof(end) * 60.0 : atof(end)); printf("debug end: the run ends after %lld fight frames\n", (long long)g_end_limit); }
    if (list && !(g_dm_list = fopen(list, "r"))) printf("debug match: SSBU_DEBUG_MATCH_LIST: cannot open %s\n", list);
    if (g_dm_list && !getenv("SSBU_DEBUG_MATCH")) snprintf(g_dm.text, sizeof g_dm.text, "(list)");
    g_hs_wanted = rbn_handshake_wanted();
    {   const char *e = getenv("SSBU_RB_NET_P2NONE"), *a = getenv("SSBU_RB_NET_AUTOP2");      /* (hs12) the select screen's player 2 for an online match */
        g_p2none = e && atoi(e) > 0; g_autop2 = a && a[0] ? atoi(a) > 0 : g_hs_wanted;
        if (g_p2none || g_autop2) printf("debug match: hook on the select screen's table copy %llx: id %d (auto player 2 %s%s)\n", GA(0x710179e440ULL), hook_install(GA(0x710179e440ULL), panel_copy_hook, NULL, &g_orig_panel_copy),
                                         g_autop2 ? "on" : "off", g_p2none ? "; test: entry 1 copied as none" : ""); }
    if (getenv("SSBU_DEBUG_MATCH") || g_dm_list || g_hs_wanted) {
        if (getenv("SSBU_DEBUG_MATCH")) parse_match(getenv("SSBU_DEBUG_MATCH"));
        printf("debug match: hook on the player record builder %llx: id %d\n", FN_PLAYER_RECORD, hook_install(FN_PLAYER_RECORD, match_player_hook, NULL, &g_orig_player_record));
        if (g_hs_wanted) printf("debug match: hook on the fight scene's clean-up: id %d\n", hook_install(GA(0x710134c690ULL), scene_clean_hook, NULL, &g_orig_scene_clean));
        if (g_hs_wanted) printf("debug match: hook on the match controls set-up %llx: id %d (R3 lwr: Local Wireless room matches, each player's own controls)\n", (unsigned long long)GA(0x7101780440ULL), hook_install(GA(0x7101780440ULL), controls_fill_hook, NULL, &g_orig_controls_fill));
    }
    if (g_hs_wanted || getenv("SSBU_SQ_TEST")) { int a = hook_install(GA(0x710157c100ULL), sq_init_hook, NULL, &g_orig_sq_init), b = hook_install(GA(0x7101589830ULL), sq_step_hook, NULL, &g_orig_sq_step), l = hook_install(GA(0x71006556b0ULL), sq_eload_hook, NULL, &g_orig_sq_eload), p = hook_install(GA(0x710158a360ULL), sq_put_hook, NULL, &g_orig_sq_put);      /* (Battle Arena) */
        printf("debug match: hooks on the squad object (init %d, change of fighter %d / %d, fighter load %d)\n", a, b, p, l);
        {   const char *e = getenv("SSBU_BA_SQUAD_CSS"), *k = getenv("SSBU_SQ_READYGATE"); g_rgate = e && e[0] == '1' && !(k && k[0] == '0'); }
        if (getenv("SSBU_SQ_MODLOG") || g_rgate > 0) printf("[squad] module hooks (log %s, ready at step frames %s) %d %d\n", getenv("SSBU_SQ_MODLOG") ? "on" : "off", g_rgate > 0 ? "on" : "off",
                                             hook_install(GA(0x71017e4940ULL), mod_take_hook, NULL, &g_orig_mod_take), hook_install(GA(0x71017e4790ULL), mod_ready_hook, NULL, &g_orig_mod_ready));
        if (getenv("SSBU_SQ_MODLOG")) printf("[squad] RESEARCH: module drop hook %d\n", hook_install(GA(0x71022b6f60ULL), mod_drop_hook, NULL, &g_orig_mod_drop)); }
    if (getenv("SSBU_DEBUG_RESEARCH")) printf("debug research: hook on the entry set-up: id %d\n", hook_install(FN_ENTRY_SETUP, research_entry_setup, NULL, &g_orig_entry_setup));
    if (getenv("SSBU_DEBUG_DUMP") || g_dm.text[0] || g_end_limit ||getenv("SSBU_DEBUG_RESEARCH") || getenv("SSBU_DEBUG_WATCHENTRY")) CreateThread(NULL, 0, dump_thread, NULL, 0, NULL);
}
