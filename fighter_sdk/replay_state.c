/* replay_state.c - (Arena) the per-frame match state for the launcher's schematic replay viewer. #included by fighter_host.c
 * (uses its fd_* helpers and the fighters' frame-line hook fh_hook). No game content in this file.
 * SPDX-License-Identifier: GPL-3.0-or-later
 *
 * What: for every frame of a match, the state of each fighter as the game itself reports it through its own getters
 * (lua_bind PostureModule / DamageModule / StatusModule / MotionModule / AttackModule / HitModule, FighterInformation),
 * the stage's collision lines (ground rays at the match start, then the moving ones read every frame), the blast zones
 * (app::camera::get_dead_area) and the match block's players / stage / rules. Written at the match end, on a thread of its
 * own, to <SSBU_USERDATA>\replays\<time>.ssbv (layout below and in tools/replay/ssbv.py); the launcher draws it.
 *
 * Never a guess: in a rollback session (rb_net.c on) the state of frame f is taken from its LAST simulation, kept by frame
 * number at the safe point after the pass (rv_pass, called by rbn_pass_hash), and appended only when the frame is FINAL
 * (rv_final, called by rbn_finalize: both players' pads known) - the same path as the frame checksums. Without a rollback
 * session (offline matches) every frame is final when it runs.
 * Never a change: only getters are called and memory is only read (the getters run on the game's context, saved and put
 * back; the samples live in host memory outside the game's address region, so the snapshot ring never sees them).
 * Cost: the getters of one fighter are a few microseconds; the stage scan (rays) is spread over the first frames.
 *
 *   SSBU_REPLAY_STATE=0           do not record (default: record every match)
 *   SSBU_REPLAY_STATE_DIR=<dir>   where the files go (default <SSBU_USERDATA>\replays)
 *   SSBU_RV_RESEARCH=1            (test only) print the raw values the recorder reads, every 2 frames
 *   SSBU_RV_TIMING=1              (test only) the main-thread cost of the recorder, printed at each match end
 *
 * File (.ssbv, little endian): header 0x200 bytes (RvHead), then the body (codec 0 raw; the launcher may store it deflated,
 * codec 1): RvLine[nlines] (the stage lines: found by rays at the match start and every half second after), then per final
 * frame: u32 frame, u8 fighter mask, u8 moved lines, u16 0, u64 the lines that are on (app::ground::is_valid) at this frame,
 * [moved * {u16 line, u16 0, float x1 y1 x2 y2}], then per fighter in the mask an RvFig followed by
 * its RvHit[nhit] and RvHurt[nhurt]. Positions of hit / hurt boxes in 1/16 game units. */
#include <time.h>
uint64_t rbn_match_seed(void); int rbn_on(void); int rbn_local_player(void); int rbn_loop(void); int rbn_match_number(void);   /* rb_net.c */
#pragma pack(push, 1)
typedef struct { int16_t x, y, x2, y2; uint16_t r; uint8_t id, flags; uint16_t dmg10; int16_t angle; uint16_t kbg, bkb; } RvHit;   /* 20 B; flags 1 = capsule, 2 = bkb holds the fixed knockback */
typedef struct { int16_t x, y, x2, y2; uint16_t r; uint8_t part, status; } RvHurt;                                                   /* 12 B; status 0 normal 1 invincible 2 intangible */
typedef struct {
    float x, y, dmg, mframe;        /* PostureModule::pos, DamageModule::damage, MotionModule::frame */
    uint64_t motion;                /* MotionModule::motion_kind (hash40) */
    int32_t status;                 /* StatusModule::status_kind */
    int8_t lr; uint8_t situation, stocks, nhit;      /* PostureModule::lr, StatusModule::situation_kind, FighterInformation stock, hitboxes */
    uint8_t nhurt, flags; uint16_t pad;              /* flags: 1 present */
    float shield;                   /* shield health (work float), -1 unknown */
    uint32_t ff;                    /* the fighter's own frame-line count (fighter_host.c), for screenshot matching */
} RvFig;                            /* 44 B */
typedef struct { float x1, y1, x2, y2; uint32_t flags; } RvLine;   /* flags 1 floor, 2 passable, 4 wall, 16 moves; bits 8..31: the first frame it is
                                                                     * in the file (found by a later ray: platforms that come and go) */
typedef struct {
    char magic[4]; uint16_t version, header_size; uint32_t flags, frames; int64_t unix_time; uint32_t body_size, body_raw;
    uint8_t codec, nfighters, rule_kind, stocks; uint32_t time_limit;
    uint64_t ui_stage; uint32_t stage_form, stage_id;
    float dead[4], camera[4];       /* blast zones and camera range as app::camera returns them */
    uint32_t nlines, game_build;
    struct { uint64_t ui_chara; int32_t kind; uint8_t colour, type, entry, slot; char name[32]; uint32_t end_stocks; float end_damage; uint32_t pad[2]; } fighter[4];   /* 0x40 each */
    char build[32];
    uint32_t local_player, match_number; uint64_t seed;
    uint32_t first_ff, ended_frame; int32_t winner; uint32_t rec_us;      /* rec_us: mean recorder cost per frame (main thread), microseconds */
    uint8_t slot_entry[4];          /* the entry id (player) of each recorded slot, 0xff unused: a player can have two (Ice Climbers) */
    uint32_t match_id;              /* the rollback handshake's match id (online; the lobby's .ssbr has the same), 0 local */
    uint8_t kind, pad1[3];          /* 0 local, 1 matchmaking, 2 Local Wireless room, 3 other online (debug_mode.c dm_match_kind) */
    char local_name[32];            /* this player's name (SSBU_ONLINE_NAME / SSBU_NICKNAME) */
    uint8_t reserved[0x200 - 0x1cc];
} RvHead;
#pragma pack(pop)
_Static_assert(sizeof(RvHead) == 0x200, "RvHead");
_Static_assert(sizeof(RvFig) == 44, "RvFig");

#define RV_MAXF 4
#define RV_MAXHIT 16
#define RV_MAXHURT 32
#define RV_MAXLINE 64
#define RV_RING 512
typedef struct { RvFig f; RvHit hit[RV_MAXHIT]; RvHurt hurt[RV_MAXHURT]; } RvSample;
typedef struct { long long frame; uint8_t mask; uint64_t valid; RvSample s[RV_MAXF]; float lines[RV_MAXLINE][4]; } RvSlot;

static int g_rv_on = -1, g_rv_rb;                      /* on; this match runs in a rollback session */
/* (0.2.11) per match: g_rv_mrb = this match goes through the rollback session (final frames from rv_final). A LOCAL fight in a
 * rollback session (online on: friends or CPUs, no room code) never reaches rv_pass / rv_final: it is recorded as offline, every
 * frame (it was not recorded at all). A room's waiting-stage fight still is not. */
static int g_rv_mrb; int rbn_local_fight(void);
static int rv_match_rb(void) { return g_rv_rb && !rbn_local_fight(); }
static RvSlot g_rv_acc;                                /* the pass now running */
static RvSlot *g_rv_ring;                              /* rollback: the last simulation of each frame, by frame number */
static uint8_t *g_rv_buf; static size_t g_rv_len, g_rv_cap;
static RvHead g_rv_head; static int g_rv_active; static long long g_rv_frames, g_rv_last_final = 0, g_rv_direct_n;
static RvLine g_rv_lines[RV_MAXLINE]; static uint64_t g_rv_lineptr[RV_MAXLINE]; static int g_rv_nlines, g_rv_scan_x, g_rv_scan_done;
static float g_rv_prev_lines[RV_MAXLINE][4];
static int g_rv_scan_final;                            /* the start scan (rays down, sideways, up) is over */
static uint64_t g_rv_boma[RV_MAXF]; static int g_rv_slot_entry[RV_MAXF];
static int g_rv_shield_idx = -2;                       /* the work float that holds the shield (found at the first frames: 50.0) */
static LARGE_INTEGER g_rv_qpf; static long long g_rv_cost_ticks, g_rv_cost_n, g_rv_cost_max, g_rv_scan_ticks, g_rv_lines_ticks, g_rv_cost_hist[4];      /* hist: lines under 20 / 50 / 100 us / more */
static SRWLOCK g_rv_lock = SRWLOCK_INIT;

static uint64_t rv_fn(const char *name) { return fd_fn(name); }
#define RV_LB(s) "_ZN3app8lua_bind" s "EPNS_26BattleObjectModuleAccessorE"
/* game functions read through their exported names (the same on 13.0.4 and 13.0.5) */
static struct { uint64_t posx, posy, lr, dmg, st, sit, mot, mf, isatk, atksize, atkcenter, atkdata, hitn, hitc, hitst, atkn, entry, dead, cam, ray, raync, lpt, rpt, pas, floor, wall, valid; } g_rvf;
static void rv_fns(void) {
    if (g_rvf.posx) return;
    g_rvf.posx = rv_fn(RV_LB("25PostureModule__pos_x_impl")); g_rvf.posy = rv_fn(RV_LB("25PostureModule__pos_y_impl")); g_rvf.lr = rv_fn(RV_LB("22PostureModule__lr_impl"));
    g_rvf.dmg = rv_fn(RV_LB("25DamageModule__damage_impl") "i"); g_rvf.st = rv_fn(RV_LB("30StatusModule__status_kind_impl"));
    g_rvf.sit = rv_fn(RV_LB("33StatusModule__situation_kind_impl")); g_rvf.mot = rv_fn(RV_LB("30MotionModule__motion_kind_impl")); g_rvf.mf = rv_fn(RV_LB("24MotionModule__frame_impl"));
    g_rvf.isatk = rv_fn(RV_LB("28AttackModule__is_attack_impl") "ib"); g_rvf.atksize = rv_fn(RV_LB("23AttackModule__size_impl") "i");
    g_rvf.atkcenter = rv_fn(RV_LB("29AttackModule__center_pos_impl") "ib"); g_rvf.atkdata = rv_fn(RV_LB("30AttackModule__attack_data_impl") "ib");
    g_rvf.hitst = rv_fn(RV_LB("26HitModule__get_status_impl") "ii");
    g_rvf.atkn = rv_fn(RV_LB("28AttackModule__part_size_impl"));
    g_rvf.hitn = rv_fn(RV_LB("29HitModule__get_part_size_impl") "i"); g_rvf.hitc = rv_fn(RV_LB("30HitModule__get_center_pos_impl") "ii");
    g_rvf.entry = rv_fn("_ZN3app7fighter20get_fighter_entry_idEj");
    g_rvf.dead = rv_fn("_ZN3app6camera13get_dead_areaEv"); g_rvf.cam = rv_fn("_ZN3app6camera16get_camera_rangeEv");
    g_rvf.ray = rv_fn(RV_LB("45GroundModule__ray_check_get_line_hit_pos_impl") "RKN3phx8Vector2fES6_RS4_b");
    /* the same without culling: also finds Town and City's platforms, which the plain ray never hits */
    g_rvf.raync = rv_fn(RV_LB("56GroundModule__ray_check_get_line_hit_pos_no_culling_impl") "RKN3phx8Vector2fES6_RS4_b");
    g_rvf.lpt = rv_fn("_ZN3app6ground14get_left_pointEPKNS_19GroundCollisionLineE"); g_rvf.rpt = rv_fn("_ZN3app6ground15get_right_pointEPKNS_19GroundCollisionLineE");
    g_rvf.pas = rv_fn("_ZN3app6ground11is_passableEPKNS_19GroundCollisionLineE"); g_rvf.floor = rv_fn("_ZN3app6ground8is_floorEPKNS_19GroundCollisionLineE");
    g_rvf.wall = rv_fn("_ZN3app6ground7is_wallEPKNS_19GroundCollisionLineE");
    g_rvf.valid = rv_fn("_ZN3app6ground8is_validEPKNS_19GroundCollisionLineE");
}
/* one getter call on the game's context: x0..x4 in, x0 / v0..v2 out. The caller saves and restores the context once. */
static inline uint64_t rv_call5(Ctx *c, uint64_t fn, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, V128 *v);
static inline uint64_t rv_call(Ctx *c, uint64_t fn, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3, V128 *v) { return rv_call5(c, fn, a0, a1, a2, a3, 0, v); }
static inline uint64_t rv_call5(Ctx *c, uint64_t fn, uint64_t a0, uint64_t a1, uint64_t a2, uint64_t a3, uint64_t a4, V128 *v) {
    if (!fn) return 0;
    c->x[0] = a0; c->x[1] = a1; c->x[2] = a2; c->x[3] = a3; c->x[4] = a4;
    c->v[0].d[0] = c->v[0].d[1] = 0; c->v[1].d[0] = c->v[1].d[1] = 0; c->v[2].d[0] = c->v[2].d[1] = 0;
    call_indirect(c, fn);
    if (v) { v[0] = c->v[0]; v[1] = c->v[1]; v[2] = c->v[2]; }
    return c->x[0];
}
static int rv_readable(uint64_t a, size_t n) {
    MEMORY_BASIC_INFORMATION mi;
    if (a < 0x10000 || !VirtualQuery((void *)(uintptr_t)a, &mi, sizeof mi) || mi.State != MEM_COMMIT || (mi.Protect & (PAGE_GUARD | PAGE_NOACCESS))) return 0;
    return a + n <= (uint64_t)(uintptr_t)mi.BaseAddress + mi.RegionSize;
}
static void rv_hex(const char *tag, uint64_t a, int n) {
    for (int k = 0; k < n; k += 16) {
        uint8_t b[16]; float f[4];
        if (!rv_readable(a + (uint64_t)k, 16)) break;
        memcpy(b, (void *)(uintptr_t)(a + (uint64_t)k), 16); memcpy(f, b, 16);
        printf("%s +%03x %02x%02x%02x%02x %02x%02x%02x%02x %02x%02x%02x%02x %02x%02x%02x%02x | %g %g %g %g\n", tag, k, b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7], b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15], f[0], f[1], f[2], f[3]);
    }
}
/* (0.2.17) a ground line may be gone: Steve's blocks (and other things the game makes during a match) have lines of their own, and
 * when one goes its memory is used for something else. The getters read the line's two points through + 0x88 and + 0x90 and crashed
 * on what was there (run steve2: 1 + 0x10). A line is used only while both are pointers to memory that can be read. */
static long long g_rv_ok_frame = -1;
/* (0.2.22) a page that can be read, asked once per frame by reading one byte of it. It was VirtualQuery, which walks the whole region
 * to say how long it is: with this game's memory 10 to 60 ms a frame, on the game's main thread, every frame of a recorded match. */
static uint64_t g_rv_ok_page[16]; static int g_rv_ok_pages;
static int rv_page_ok(uint64_t page) {
    for (int i = 0; i < g_rv_ok_pages; i++) if (g_rv_ok_page[i] == page) return 1;
    uint8_t b; SIZE_T got = 0;
    if (!ReadProcessMemory(GetCurrentProcess(), (const void *)(uintptr_t)page, &b, 1, &got) || got != 1) return 0;
    if (g_rv_ok_pages < 16) g_rv_ok_page[g_rv_ok_pages++] = page;
    return 1;
}
static int rv_ptr_ok(uint64_t a, size_t n, long long frame) {
    if (a < 0x10000 || a >> 47 || (a & 3)) return 0;
    if (frame != g_rv_ok_frame) { g_rv_ok_frame = frame; g_rv_ok_pages = 0; }
    uint64_t p0 = a & ~0xfffull, p1 = (a + n - 1) & ~0xfffull;
    return rv_page_ok(p0) && (p1 == p0 || rv_page_ok(p1));
}
static int rv_line_ok(uint64_t line, long long frame) {
    if (!rv_ptr_ok(line, 0x98, frame)) return 0;
    uint64_t p0 = rd64(line + 0x88), p1 = rd64(line + 0x90);
    return rv_ptr_ok(p0, 0x20, frame) && rv_ptr_ok(p1, 0x20, frame);
}
static inline int16_t rv_q(float v) { float q = v * 16.0f; return (int16_t)(q > 32767 ? 32767 : q < -32768 ? -32768 : q); }

/* ---------------------------------------------------------------- the match's fixed data (header) */
static uint64_t rv_fm_impl(void) {
    static uint64_t inst;
    if (!inst) inst = hk_address_of("_ZN3lib9SingletonIN3app14FighterManagerEE9instance_E");
    uint64_t mgr = inst ? rd64(inst) : 0;
    return mgr ? rd64(mgr) : 0;
}
static uint64_t rv_info_record(int entry) {             /* debug_mode.c fighter_info_record: impl + 0x20 + 8 * entry = FighterEntry; + 0xf0 FighterInformation; *(+8) its record */
    uint64_t impl = rv_fm_impl(), en = impl && entry >= 0 && entry < 8 ? rd64(impl + 0x20 + 8ull * (uint64_t)entry) : 0;
    return en ? rd64(en + 0xf0 + 8) : 0;
}
static void rv_head_start(void) {
    RvHead *h = &g_rv_head;
    memset(h, 0, sizeof *h);
    memcpy(h->magic, "SSBV", 4); h->version = 2;      /* 2: the frame header has the lines-on mask (1: test files of the first builds) */ h->header_size = sizeof *h;
    { FILETIME ft; GetSystemTimeAsFileTime(&ft); h->unix_time = (int64_t)((((uint64_t)ft.dwHighDateTime << 32) | ft.dwLowDateTime) / 10000000ull) - 11644473600ll; }
    h->game_build = (uint32_t)g_game_build;
    uint64_t mb = GA(0x71052c41b0ULL);                 /* the game's global match block (debug_mode.c MELEE_INFO) */
    h->stage_id = rd32(mb + 0x420);
    uint32_t cur = rd32(GA(0x7105309968ULL)); uint64_t sel = GA(0x71053097c0ULL) + 0x20ull * (cur < 3 ? cur : 0);   /* the stage choice (debug_mode.c) */
    h->ui_stage = rd64(sel); h->stage_form = rd32(sel + 8);
    h->stocks = rd8(mb + 0x4c);
    for (int p = 0, n = 0; p < 8 && n < 4; p++) {
        uint64_t rec = mb + 0x580 + 0x260ull * (uint64_t)p;      /* debug_mode.c: +0 ui_chara, +8 fighter kind, +0xa type (0 human 1 CPU 2 amiibo 3 none), +0x19 colour */
        if (rd8(rec + 0xa) == 3 || !rd64(rec) || (int8_t)rd8(rec + 8) < 0) continue;
        h->fighter[n].ui_chara = rd64(rec); h->fighter[n].kind = (int8_t)rd8(rec + 8); h->fighter[n].type = rd8(rec + 0xa); h->fighter[n].colour = rd8(rec + 0x19);
        h->fighter[n].entry = (uint8_t)p; h->fighter[n].slot = 0xff;
        n++; h->nfighters = (uint8_t)n;
    }
    const char *nick = getenv("SSBU_NICKNAME");
    h->local_player = rbn_on() > 0 ? (uint32_t)rbn_local_player() : 0;
    h->flags = (g_rv_mrb ? 1u : 0u) | (rbn_on() > 0 && rbn_loop() ? 2u : 0u);
    h->match_number = rbn_on() > 0 ? (uint32_t)rbn_match_number() : 0;
    h->seed = rbn_match_seed();
    for (int i = 0; i < h->nfighters; i++) {
        if (h->fighter[i].type == 1) snprintf(h->fighter[i].name, sizeof h->fighter[i].name, "CPU");
        else if (nick && *nick && (int)h->local_player == i && !(h->flags & 2)) snprintf(h->fighter[i].name, sizeof h->fighter[i].name, "%.31s", nick);
        else snprintf(h->fighter[i].name, sizeof h->fighter[i].name, "Player %d", h->fighter[i].entry + 1);
    }
    snprintf(h->build, sizeof h->build, "%s", g_game_build == GAME_13_0_5 ? "13.0.5" : "13.0.4");
}

/* ---------------------------------------------------------------- the stage: collision lines by rays, a few per frame */
static void rv_add_line(Ctx *c, uint64_t line) {
    for (int i = 0; i < g_rv_nlines; i++) if (g_rv_lineptr[i] == line) return;
    if (g_rv_nlines >= RV_MAXLINE) return;
    if (!rv_line_ok(line, g_rv_ok_frame)) return;      /* (0.2.17) */
    V128 l[3], r[3];
    rv_call(c, g_rvf.lpt, line, 0, 0, 0, l); rv_call(c, g_rvf.rpt, line, 0, 0, 0, r);
    RvLine *L = &g_rv_lines[g_rv_nlines];
    L->x1 = l[0].f[0]; L->y1 = l[0].f[1]; L->x2 = r[0].f[0]; L->y2 = r[0].f[1];
    L->flags = ((rv_call(c, g_rvf.floor, line, 0, 0, 0, NULL) & 1) ? 1u : 0u) | ((rv_call(c, g_rvf.pas, line, 0, 0, 0, NULL) & 1) ? 2u : 0u) | ((rv_call(c, g_rvf.wall, line, 0, 0, 0, NULL) & 1) ? 4u : 0u);
    if (g_rv_scan_final) {                              /* found after the match start: from the next frame written on */
        L->flags |= (uint32_t)((g_rv_head.ended_frame + 1) & 0xffffff) << 8;
        g_rv_prev_lines[g_rv_nlines][0] = L->x1; g_rv_prev_lines[g_rv_nlines][1] = L->y1; g_rv_prev_lines[g_rv_nlines][2] = L->x2; g_rv_prev_lines[g_rv_nlines][3] = L->y2;
        printf("[rv] stage line %d found later (frame %u): %.2f %.2f -> %.2f %.2f flags %u\n", g_rv_nlines, g_rv_head.ended_frame + 1, L->x1, L->y1, L->x2, L->y2, L->flags & 0xff); fflush(stdout);
    }
    g_rv_lineptr[g_rv_nlines++] = line;
}
static void rv_scan_step(Ctx *c, uint64_t boma) {
    static float st[4], dir[4], out[4];
    if (g_rv_scan_done || !g_rvf.ray) { g_rv_scan_done = 1; return; }
    for (int n = 0; n < 2 && !g_rv_scan_done; n++) {
        int x = -258 + 6 * g_rv_scan_x++;
        if (x > 260) {                                  /* the sides: rays across, from far left / right at a few heights under the floor */
            g_rv_scan_done = 1;
            break;
        }
        float from = 260.0f;
        for (int k = 0; k < 5; k++) {                   /* down from the top: every line under this x, one after the other */
            st[0] = (float)x; st[1] = from; dir[0] = 0; dir[1] = -260.0f - from; out[0] = out[1] = 0;
            uint64_t line = rv_call5(c, g_rvf.ray, boma, (uint64_t)(uintptr_t)st, (uint64_t)(uintptr_t)dir, (uint64_t)(uintptr_t)out, 1, NULL);
            float o1[4] = {0, 0, 0, 0};
            uint64_t line0 = rv_call5(c, g_rvf.ray, boma, (uint64_t)(uintptr_t)st, (uint64_t)(uintptr_t)dir, (uint64_t)(uintptr_t)o1, 0, NULL);
            if (line0 && (!line || o1[1] > out[1])) { line = line0; out[0] = o1[0]; out[1] = o1[1]; }      /* (the flag's meaning is not known: the higher hit of the two) */
            float o2[4] = {0, 0, 0, 0};
            uint64_t line2 = rv_call5(c, g_rvf.raync, boma, (uint64_t)(uintptr_t)st, (uint64_t)(uintptr_t)dir, (uint64_t)(uintptr_t)o2, 1, NULL);
            if (line2) rv_add_line(c, line2);
            if (!line) break;
            rv_add_line(c, line);
            from = out[1] - 0.5f;
            if (from < -250.0f) break;
        }
    }
    if (g_rv_scan_done) {                               /* walls and the underside: rays sideways and up at the floor's width */
        for (int y = 0; y > -120; y -= 6) for (int side = 0; side < 2; side++) {
            st[0] = side ? 300.0f : -300.0f; st[1] = (float)y; dir[0] = side ? -600.0f : 600.0f; dir[1] = 0; out[0] = out[1] = 0;
            uint64_t line = rv_call(c, g_rvf.ray, boma, (uint64_t)(uintptr_t)st, (uint64_t)(uintptr_t)dir, (uint64_t)(uintptr_t)out, NULL);
            if (line) rv_add_line(c, line);
        }
        for (int x = -240; x <= 240; x += 8) {
            st[0] = (float)x; st[1] = -250.0f; dir[0] = 0; dir[1] = 250.0f; out[0] = out[1] = 0;
            uint64_t line = rv_call(c, g_rvf.ray, boma, (uint64_t)(uintptr_t)st, (uint64_t)(uintptr_t)dir, (uint64_t)(uintptr_t)out, NULL);
            if (line) rv_add_line(c, line);
        }
        V128 v[3];
        rv_call(c, g_rvf.dead, 0, 0, 0, 0, v); memcpy(g_rv_head.dead, v[0].f, 16);
        rv_call(c, g_rvf.cam, 0, 0, 0, 0, v); memcpy(g_rv_head.camera, v[0].f, 16);
        printf("[rv] blast zones (dead area) %.2f %.2f %.2f %.2f, camera range %.2f %.2f %.2f %.2f\n", g_rv_head.dead[0], g_rv_head.dead[1], g_rv_head.dead[2], g_rv_head.dead[3], g_rv_head.camera[0], g_rv_head.camera[1], g_rv_head.camera[2], g_rv_head.camera[3]);
        for (int i = 0; i < g_rv_nlines; i++) { g_rv_prev_lines[i][0] = g_rv_lines[i].x1; g_rv_prev_lines[i][1] = g_rv_lines[i].y1; g_rv_prev_lines[i][2] = g_rv_lines[i].x2; g_rv_prev_lines[i][3] = g_rv_lines[i].y2; }
        g_rv_scan_final = 1;
        printf("[rv] stage scan: %d collision lines\n", g_rv_nlines);
        for (int i = 0; i < g_rv_nlines; i++) printf("[rv]   line %d: %.2f %.2f -> %.2f %.2f flags %u\n", i, g_rv_lines[i].x1, g_rv_lines[i].y1, g_rv_lines[i].x2, g_rv_lines[i].y2, g_rv_lines[i].flags);
        fflush(stdout);
    }
}

/* ---------------------------------------------------------------- one fighter's state (at the entry of its frame line) */
static void rv_sample(Ctx *c, uint64_t boma, int slot, long long ff) {
    RvSample *s = &g_rv_acc.s[slot];
    RvFig *f = &s->f;
    V128 v[3];
    memset(f, 0, sizeof *f);
    rv_call(c, g_rvf.posx, boma, 0, 0, 0, v); f->x = v[0].f[0];
    rv_call(c, g_rvf.posy, boma, 0, 0, 0, v); f->y = v[0].f[0];
    rv_call(c, g_rvf.lr, boma, 0, 0, 0, v); f->lr = v[0].f[0] < 0 ? -1 : 1;
    rv_call(c, g_rvf.dmg, boma, 0, 0, 0, v); f->dmg = v[0].f[0];
    rv_call(c, g_rvf.mf, boma, 0, 0, 0, v); f->mframe = v[0].f[0];
    f->motion = rv_call(c, g_rvf.mot, boma, 0, 0, 0, NULL);
    f->status = (int32_t)rv_call(c, g_rvf.st, boma, 0, 0, 0, NULL);
    f->situation = (uint8_t)rv_call(c, g_rvf.sit, boma, 0, 0, 0, NULL);
    f->ff = (uint32_t)ff; f->flags = 1;
    int entry = g_rv_slot_entry[slot];
    uint64_t rec = rv_info_record(entry);
    f->stocks = rec ? (uint8_t)rd32(rec + 0xd8) : 0;
    f->shield = -1;
    if (g_rv_shield_idx >= 0) {
        uint64_t wm = rd64(boma + 0x50), areas = wm ? rd64(wm + 0x18) : 0, ar = areas ? rd64(areas + 8ull * (uint64_t)(g_rv_shield_idx >> 12)) : 0;
        if (ar) memcpy(&f->shield, (void *)(uintptr_t)(ar + 4ull * (uint64_t)(g_rv_shield_idx & 0xfff)), 4);
    }
    /* hitboxes: the attack module's own collision of each active attack id */
    int nh = 0, na = (int)rv_call(c, g_rvf.atkn, boma, 0, 0, 0, NULL);
    static int research = -1; if (research < 0) research = getenv("SSBU_RV_RESEARCH") != NULL;
    if (research && (ff % 20 == 0)) printf("[rv] f %lld k %d pos %.3f %.3f lr %d dmg %.2f st %x sit %d mot %llx mf %.2f stocks %d shield %.2f attack parts %d\n", ff, slot, f->x, f->y, f->lr, f->dmg, f->status, f->situation, (unsigned long long)f->motion, f->mframe, f->stocks, f->shield, na);
    if (na > 16) na = 16;
    if (research && (f->status == 0x1b || f->status == 0x1c || f->status == 0x1d)) {      /* (guard statuses, research): the work floats that held 50 */
        uint64_t wm = rd64(boma + 0x50), areas = wm ? rd64(wm + 0x18) : 0, ar = areas ? rd64(areas + 8 * 4) : 0; float a, b;
        if (ar) { memcpy(&a, (void *)(uintptr_t)(ar + 4 * 113), 4); memcpy(&b, (void *)(uintptr_t)(ar + 4 * 115), 4); printf("[rv] f %lld k %d guard status %x: work 4/113 %.3f 4/115 %.3f\n", ff, slot, f->status, a, b); }
    }
    for (int id = 0; id < na && nh < RV_MAXHIT; id++) {
        if (!(rv_call(c, g_rvf.isatk, boma, (uint64_t)id, 0, 0, NULL) & 1)) continue;
        RvHit *h = &s->hit[nh];
        memset(h, 0, sizeof *h);
        /* the attack's collision shape, as AttackModule::center_pos finds it (module + 0x68 entries of 0x130; the entry's
         * collision object: module + 0x28 table, entry + 0x110 index): +0x10 the start point, +0xb0 the vector to the end
         * point (a stretched / interpolated hitbox is a capsule), +0xa0 the radius */
        uint64_t am = rd64(boma + 0xa0), ent = rd64(am + 104) + 0x130ull * (uint64_t)id;
        uint64_t obj = rd64(rd64(am + 40) + 0x2d0ull * (uint64_t)(int64_t)(int32_t)rd32(ent + 272) + 8ull * (uint64_t)id + 48);
        float r = 0;
        if (obj) {
            float p0[3], dv[3]; memcpy(p0, (void *)(uintptr_t)(obj + 0x10), 12); memcpy(dv, (void *)(uintptr_t)(obj + 0xb0), 12); memcpy(&r, (void *)(uintptr_t)(obj + 0xa0), 4);
            h->x = rv_q(p0[0]); h->y = rv_q(p0[1]); h->x2 = rv_q(p0[0] + dv[0]); h->y2 = rv_q(p0[1] + dv[1]);
            if (dv[0] != 0 || dv[1] != 0) h->flags |= 1;
        } else {
            rv_call(c, g_rvf.atkcenter, boma, (uint64_t)id, 0, 0, v);
            h->x = h->x2 = rv_q(v[0].f[0]); h->y = h->y2 = rv_q(v[0].f[1]);
            rv_call(c, g_rvf.atksize, boma, (uint64_t)id, 0, 0, v); r = v[0].f[0];
        }
        h->r = (uint16_t)(r > 0 ? (r * 16.0f > 65535 ? 65535 : r * 16.0f) : 0);
        h->id = (uint8_t)id;
        /* the attack's parameters (the entry = AttackModule::attack_data; AttackModule::get_power starts from +0x20): +0x20 base
         * damage, +0x24 size, +0x28 angle, +0x2c KBG, +0x30 FKB, +0x34 BKB (checked against Ultimate Frame Data's numbers) */
        float pw; memcpy(&pw, (void *)(uintptr_t)(ent + 0x20), 4);
        h->dmg10 = (uint16_t)(pw > 0 && pw < 6000 ? pw * 10.0f + 0.5f : 0);
        h->angle = (int16_t)rd32(ent + 0x28); h->kbg = (uint16_t)rd32(ent + 0x2c);      /* +0x28 angle, +0x2c KBG, +0x30 FKB, +0x34 BKB */
        if (rd32(ent + 0x30)) { h->bkb = (uint16_t)rd32(ent + 0x30); h->flags |= 2; } else h->bkb = (uint16_t)rd32(ent + 0x34);
        if (research) {
            static int dumps;
            printf("[rv]   f %lld k %d attack %d p %.3f %.3f -> %.3f %.3f r %.3f dmg %.1f w28 %d w2c %d w30 %d w08 %d w0c %d\n", ff, slot, id, h->x / 16.0, h->y / 16.0, h->x2 / 16.0, h->y2 / 16.0, r, pw,
                   (int)rd32(ent + 0x28), (int)rd32(ent + 0x2c), (int)rd32(ent + 0x30), (int)rd32(ent + 8), (int)rd32(ent + 0xc));
            if (dumps < 6) { dumps++; char t[64]; snprintf(t, sizeof t, "[rv]     data f%lld k%d id%d", ff, slot, id); rv_hex(t, ent, 0x130); }
        }
        nh++;
    }
    f->nhit = (uint8_t)nh;
    int n = (int)rv_call(c, g_rvf.hitn, boma, 0, 0, 0, NULL), nu = 0;
    uint64_t hm = rd64(boma + 0xb0), htab = hm ? rd64(hm + 40) : 0, hgrp = hm ? rd64(hm + 96) : 0;
    for (int i = 0; i < n && nu < RV_MAXHURT && htab && hgrp; i++) {
        /* HitModule::get_center_pos's path: module + 0x60 groups of 0x3c (+0 the first index), module + 0x28 table */
        uint64_t obj = rd64(htab + 0x2d0ull * (uint64_t)(int64_t)(int32_t)rd32(hgrp) + 8ull * (uint64_t)i + 48);
        if (!obj) continue;
        int st = (int)rv_call(c, g_rvf.hitst, boma, (uint64_t)i, 0, 0, NULL);
        if (st == 3) continue;                          /* HIT_STATUS_OFF */
        float p0[3], dv[3], r; memcpy(p0, (void *)(uintptr_t)(obj + 0x10), 12); memcpy(dv, (void *)(uintptr_t)(obj + 0xb0), 12); memcpy(&r, (void *)(uintptr_t)(obj + 0xa0), 4);
        RvHurt *u = &s->hurt[nu++];
        u->x = rv_q(p0[0]); u->y = rv_q(p0[1]); u->x2 = rv_q(p0[0] + dv[0]); u->y2 = rv_q(p0[1] + dv[1]);
        u->r = (uint16_t)(r > 0 && r < 100 ? r * 16.0f : 0); u->part = (uint8_t)i; u->status = (uint8_t)st;
        if (research && ff % 600 == 20) printf("[rv]   f %lld k %d hurt %d/%d p %.2f %.2f -> %.2f %.2f r %.2f status %d\n", ff, slot, i, n, p0[0], p0[1], p0[0] + dv[0], p0[1] + dv[1], r, st);
    }
    f->nhurt = (uint8_t)nu;
}

/* ---------------------------------------------------------------- the recording buffer and the writer thread */
static void rv_put(const void *p, size_t n) {
    if (g_rv_len + n > g_rv_cap) {
        size_t cap = g_rv_cap ? g_rv_cap * 2 : (size_t)4 << 20;
        while (cap < g_rv_len + n) cap *= 2;
        uint8_t *b = realloc(g_rv_buf, cap);
        if (!b) return;
        g_rv_buf = b; g_rv_cap = cap;
    }
    memcpy(g_rv_buf + g_rv_len, p, n); g_rv_len += n;
}
static void rv_append(const RvSlot *sl, long long frame) {
    uint8_t mask = sl->mask, moved = 0; uint16_t z = 0;
    uint16_t idx[RV_MAXLINE];
    for (int i = 0; i < g_rv_nlines; i++)
        if (memcmp(sl->lines[i], g_rv_prev_lines[i], sizeof g_rv_prev_lines[i]) != 0 && (sl->lines[i][0] != 0 || sl->lines[i][2] != 0)) idx[moved++] = (uint16_t)i;
    uint32_t fr = (uint32_t)frame;
    rv_put(&fr, 4); rv_put(&mask, 1); rv_put(&moved, 1); rv_put(&z, 2); rv_put(&sl->valid, 8);
    for (int m = 0; m < moved; m++) {
        int i = idx[m]; rv_put(&idx[m], 2); rv_put(&z, 2); rv_put(sl->lines[i], 16);
        memcpy(g_rv_prev_lines[i], sl->lines[i], 16); g_rv_lines[i].flags |= 16;
    }
    for (int k = 0; k < RV_MAXF; k++) {
        if (!(mask & (1 << k))) continue;
        const RvSample *s = &sl->s[k];
        rv_put(&s->f, sizeof s->f); rv_put(s->hit, sizeof(RvHit) * s->f.nhit); rv_put(s->hurt, sizeof(RvHurt) * s->f.nhurt);
        for (int i = 0; i < g_rv_head.nfighters; i++) if (g_rv_head.fighter[i].slot == k) { g_rv_head.fighter[i].end_stocks = s->f.stocks; g_rv_head.fighter[i].end_damage = s->f.dmg; }
    }
    g_rv_frames++;
    g_rv_head.ended_frame = (uint32_t)frame;
}
typedef struct { RvHead h; uint8_t *body; size_t len; RvLine lines[RV_MAXLINE]; int nlines; } RvJob;
static DWORD WINAPI rv_writer(LPVOID p) {
    RvJob *j = p;
    char dir[MAX_PATH * 2], path[MAX_PATH * 2];
    const char *d = getenv("SSBU_REPLAY_STATE_DIR"), *ud = getenv("SSBU_USERDATA");
    if (d && *d) snprintf(dir, sizeof dir, "%s", d); else snprintf(dir, sizeof dir, "%s\\replays", ud && *ud ? ud : ".");
    CreateDirectoryA(dir, NULL);
    time_t t = (time_t)j->h.unix_time; struct tm lt; localtime_s(&lt, &t);
    snprintf(path, sizeof path, "%s\\%04d%02d%02d-%02d%02d%02d.ssbv", dir, lt.tm_year + 1900, lt.tm_mon + 1, lt.tm_mday, lt.tm_hour, lt.tm_min, lt.tm_sec);
    char tmp[MAX_PATH * 2]; snprintf(tmp, sizeof tmp, "%s.part", path);
    size_t lb = sizeof(RvLine) * (size_t)j->nlines;
    j->h.body_size = j->h.body_raw = (uint32_t)(lb + j->len);
    FILE *f = fopen(tmp, "wb");
    int ok = f && fwrite(&j->h, sizeof j->h, 1, f) == 1 && (!lb || fwrite(j->lines, lb, 1, f) == 1) && (!j->len || fwrite(j->body, j->len, 1, f) == 1);
    if (f) ok = (fclose(f) == 0) && ok;
    if (ok) ok = MoveFileExA(tmp, path, MOVEFILE_REPLACE_EXISTING);
    printf("[rv] replay state %s: %s (%u frames, %u fighters, %.1f MB)\n", ok ? "written" : "NOT written", path, j->h.frames, j->h.nfighters, (double)(sizeof j->h + lb + j->len) / 1048576.0);
    fflush(stdout);
    if (!ok) DeleteFileA(tmp);
    free(j->body); free(j);
    return 0;
}
static void rv_end(const char *why, int sync) {
    if (!g_rv_active) return;
    g_rv_active = 0;
    if (g_rv_frames < 30) { printf("[rv] match state not kept (%lld frames, %s)\n", g_rv_frames, why); fflush(stdout); g_rv_len = 0; return; }
    RvJob *j = calloc(1, sizeof *j);
    if (!j) return;
    j->h = g_rv_head; j->h.frames = (uint32_t)g_rv_frames; j->h.nlines = (uint32_t)g_rv_nlines;
    memcpy(j->lines, g_rv_lines, sizeof g_rv_lines); j->nlines = g_rv_nlines;
    if (!strcmp(why, "ready-go cleared")) j->h.flags |= 4;
    for (int i = 0; i < j->h.nfighters; i++) {         /* the stocks at the end as the game counts them now (the last KO lands after the last final frame) */
        uint64_t rec = rv_info_record(j->h.fighter[i].entry);
        if (rec) j->h.fighter[i].end_stocks = rd32(rec + 0xd8);
    }
    int alive = 0, w = -1;
    for (int i = 0; i < j->h.nfighters; i++) if (j->h.fighter[i].end_stocks > 0) { alive++; w = i; }
    j->h.winner = alive == 1 ? w : -1;
    j->h.rec_us = g_rv_cost_n ? (uint32_t)((g_rv_cost_ticks - g_rv_scan_ticks) * 1000000.0 / (double)g_rv_qpf.QuadPart / (double)g_rv_cost_n + 0.5) : 0;
    if (getenv("SSBU_RV_TIMING")) {
        double us = 1e6 / (double)g_rv_qpf.QuadPart;
        printf("[rv] recorder cost: %lld fighter frame lines: %.2f us each on average (%.2f without the stage scan, %.2f of it the moving lines), the stage scan %.1f ms in all, the slowest line after the scan %.1f us\n",
               g_rv_cost_n, g_rv_cost_n ? g_rv_cost_ticks * us / (double)g_rv_cost_n : 0.0, g_rv_cost_n ? (g_rv_cost_ticks - g_rv_scan_ticks) * us / (double)g_rv_cost_n : 0.0,
               g_rv_cost_n ? g_rv_lines_ticks * us / (double)g_rv_cost_n : 0.0, g_rv_scan_ticks * us / 1000.0, g_rv_cost_max * us);
        printf("[rv] recorder cost per line after the scan: under 20 us %lld, 20-50 us %lld, 50-100 us %lld, over 100 us %lld\n", g_rv_cost_hist[0], g_rv_cost_hist[1], g_rv_cost_hist[2], g_rv_cost_hist[3]);
    }
    j->body = g_rv_buf; j->len = g_rv_len;              /* the buffer goes to the writer; a new one is made for the next match */
    g_rv_buf = NULL; g_rv_len = g_rv_cap = 0;
    printf("[rv] match state: %lld final frames (%s), handed to the writer\n", g_rv_frames, why); fflush(stdout);
    HANDLE t = sync ? NULL : CreateThread(NULL, 0, rv_writer, j, 0, NULL);
    if (t) CloseHandle(t); else rv_writer(j);
}
/* SSBU_REPLAY_MODES=<list> (the launcher's choice, default all): local, mm (matchmaking), ldn (Local Wireless rooms over the
 * relay), arena (Battle Arena, when it exists); the other online matches (host / join) follow mm */
static int g_rv_skip;
static int rv_mode_on(int kind) {
    const char *e = getenv("SSBU_REPLAY_MODES");
    if (!e) return 1;
    const char *want = kind == 0 ? "local" : kind == 2 ? "ldn" : "mm";
    for (const char *p = e; *p;) {
        size_t n = strcspn(p, ",; ");
        if (n == strlen(want) && !_strnicmp(p, want, n)) return 1;
        p += n; while (*p == ',' || *p == ';' || *p == ' ') p++;
    }
    return 0;
}
static void rv_begin(void) {
    if (g_rv_active) rv_end("a new match", 0);
    {   int dm_match_kind(void); int kind = rbn_on() > 0 && !rbn_loop() && !rbn_local_fight() ? dm_match_kind() : 0;
        if (!rv_mode_on(kind)) {
            if (!g_rv_skip) { printf("[rv] this match is not recorded (kind %d not in SSBU_REPLAY_MODES)\n", kind); fflush(stdout); }
            g_rv_skip = 1; return;
        }
        g_rv_skip = 0; }
    g_rv_mrb = rv_match_rb();                           /* (0.2.11) this match: rollback (final frames) or every frame */
    g_rv_active = 1; g_rv_frames = 0; g_rv_len = 0; g_rv_last_final = 0; g_rv_direct_n = 0;
    g_rv_nlines = 0; g_rv_scan_x = 0; g_rv_scan_done = 0; g_rv_scan_final = 0; g_rv_cost_ticks = g_rv_cost_n = g_rv_cost_max = g_rv_scan_ticks = g_rv_lines_ticks = 0; memset(g_rv_cost_hist, 0, sizeof g_rv_cost_hist);
    memset(&g_rv_acc, 0, sizeof g_rv_acc);
    for (int k = 0; k < RV_MAXF; k++) { g_rv_boma[k] = 0; g_rv_slot_entry[k] = -1; }
    if (g_rv_ring) for (int i = 0; i < RV_RING; i++) g_rv_ring[i].frame = -1;
    rv_head_start();
    if (g_rv_head.nfighters < 2) {                      /* (0.2.11) one fighter (the controls test, a menu): not a match */
        printf("[rv] this fight is not recorded (%d fighter)\n", g_rv_head.nfighters); fflush(stdout);
        g_rv_active = 0; g_rv_skip = 1; return;
    }
    memset(g_rv_head.slot_entry, 0xff, sizeof g_rv_head.slot_entry);
    {   int dm_match_kind(void); uint32_t rbn_match_id(void);
        g_rv_head.kind = (uint8_t)(rbn_on() > 0 && !rbn_loop() && !rbn_local_fight() ? dm_match_kind() : 0);
        g_rv_head.match_id = g_rv_head.kind ? rbn_match_id() : 0;
        const char *n = getenv("SSBU_ONLINE_NAME"); if (!n || !*n) n = getenv("SSBU_NICKNAME");
        if (n) snprintf(g_rv_head.local_name, sizeof g_rv_head.local_name, "%.31s", n);
        if (n && *n && g_rv_head.kind && g_rv_head.local_player < g_rv_head.nfighters)
            snprintf(g_rv_head.fighter[g_rv_head.local_player].name, sizeof g_rv_head.fighter[0].name, "%.31s", n); }
    if (!g_rv_buf) { g_rv_cap = (size_t)8 << 20; g_rv_buf = malloc(g_rv_cap); if (!g_rv_buf) g_rv_cap = 0; }      /* (about 7 minutes: no growth on the main thread in most matches) */
    printf("[rv] recording the match state (%s): %d fighters, stage %llx\n", g_rv_mrb ? "final frames of the rollback session" : "every frame", g_rv_head.nfighters, (unsigned long long)g_rv_head.ui_stage);
    fflush(stdout);
}
static int rv_enabled(void) {
    if (g_rv_on < 0) {
        const char *e = getenv("SSBU_REPLAY_STATE");
        g_rv_on = !(e && e[0] == '0');
        QueryPerformanceFrequency(&g_rv_qpf);
        g_rv_rb = rbn_on() > 0;
        if (g_rv_on && g_rv_rb) { g_rv_ring = calloc(RV_RING, sizeof *g_rv_ring); if (!g_rv_ring) g_rv_on = 0; else for (int i = 0; i < RV_RING; i++) g_rv_ring[i].frame = -1; }
        printf("[rv] match state recording %s%s\n", g_rv_on ? "on" : "off", g_rv_on && g_rv_rb ? " (rollback session: final frames only)" : ""); fflush(stdout);
    }
    return g_rv_on;
}
/* the moving lines: their end points read again (the left / right point getters: plain reads of the line) */
static void rv_lines_now(Ctx *c, RvSlot *sl) {
    V128 l[3], r[3];
    sl->valid = 0;
    static long long now; now++;
    for (int i = 0; i < g_rv_nlines; i++) {
        if (!rv_line_ok(g_rv_lineptr[i], now)) { sl->lines[i][0] = sl->lines[i][1] = sl->lines[i][2] = sl->lines[i][3] = 0; continue; }      /* (0.2.17) the line is gone: not valid in this frame */
        rv_call(c, g_rvf.lpt, g_rv_lineptr[i], 0, 0, 0, l); rv_call(c, g_rvf.rpt, g_rv_lineptr[i], 0, 0, 0, r);
        sl->lines[i][0] = l[0].f[0]; sl->lines[i][1] = l[0].f[1]; sl->lines[i][2] = r[0].f[0]; sl->lines[i][3] = r[0].f[1];
        if (!g_rvf.valid || (rv_call(c, g_rvf.valid, g_rv_lineptr[i], 0, 0, 0, NULL) & 1)) sl->valid |= 1ull << i;
    }
}
/* after the scan: one ray down every 20 frames at x -120 .. 120 in turn (a platform that appears later, Town and City) */
static void rv_rescan_step(Ctx *c, uint64_t boma, long long ff) {
    static float st[4], dir[4], out[4];
    static int x;
    if (ff % 8 != 0 || g_rv_nlines >= RV_MAXLINE) return;
    x = x >= 120 ? -120 : x + 8;
    float from = 200.0f;
    for (int k = 0; k < 3; k++) {
        st[0] = (float)x; st[1] = from; dir[0] = 0; dir[1] = -200.0f - from; out[0] = out[1] = 0;
        uint64_t line = rv_call5(c, g_rvf.ray, boma, (uint64_t)(uintptr_t)st, (uint64_t)(uintptr_t)dir, (uint64_t)(uintptr_t)out, 1, NULL);
        if (!line) break;
        rv_add_line(c, line);
        from = out[1] - 0.5f;
        if (from < -20.0f) break;
    }
    from = 200.0f;
    for (int k = 0; k < 3 && g_rvf.raync; k++) {       /* (no culling: Town and City's platforms) */
        st[0] = (float)x; st[1] = from; dir[0] = 0; dir[1] = -200.0f - from; out[0] = out[1] = 0;
        uint64_t line = rv_call5(c, g_rvf.raync, boma, (uint64_t)(uintptr_t)st, (uint64_t)(uintptr_t)dir, (uint64_t)(uintptr_t)out, 1, NULL);
        if (!line) break;
        rv_add_line(c, line);
        from = out[1] - 0.5f;
        if (from < -20.0f) break;
    }
}

/* ---------------------------------------------------------------- the entry points */
static int rv_fight_on(void) { uint64_t impl = rv_fm_impl(); return impl && rd8(impl + 0xd2); }
/* fh_hook, at the entry of fighter k's frame line (k = fighter_host.c's slot; ff = its frame count) */
static void rv_frame_line(Ctx *c, uint64_t boma, int k, long long ff) {
    if (!rv_enabled() || k >= RV_MAXF) return;
    if (!rv_fight_on()) return;                         /* the countdown and the end of the match are not recorded */
    LARGE_INTEGER t0; QueryPerformanceCounter(&t0);
    rv_fns();
    if (!rv_match_rb() && !g_rv_active && !g_rv_skip) rv_begin();
    if (!g_rv_active) return;
    AcquireSRWLockExclusive(&g_rv_lock);
    Ctx saved = *c;
    if (g_rv_boma[k] != boma) {                         /* the fighter's entry id (its player number) from its object id */
        g_rv_boma[k] = boma;
        g_rv_slot_entry[k] = (int)rv_call(c, g_rvf.entry, rd32(boma + 8), 0, 0, 0, NULL);
        for (int i = 0; i < g_rv_head.nfighters; i++) if (g_rv_head.fighter[i].entry == g_rv_slot_entry[k] && g_rv_head.fighter[i].slot == 0xff) g_rv_head.fighter[i].slot = (uint8_t)k;
        g_rv_head.slot_entry[k] = (uint8_t)g_rv_slot_entry[k];
        if (g_rv_shield_idx == -2) {                    /* the shield's work float: the one that holds 50.0 (a full shield) at the start */
            uint64_t wm = rd64(boma + 0x50), areas = wm ? rd64(wm + 0x18) : 0;
            g_rv_shield_idx = -1;
            for (int a = 0; a < 6 && areas && g_rv_shield_idx < 0; a++) {
                uint64_t ar = rd64(areas + 8ull * (uint64_t)a);
                if (!rv_readable(ar, 0x800)) continue;
                for (int i = 0; i < 0x200; i++) { float x; memcpy(&x, (void *)(uintptr_t)(ar + 4ull * (uint64_t)i), 4); if (x == 50.0f) { g_rv_shield_idx = (a << 12) | i; break; } }
            }
            printf("[rv] shield work float: %s (area %d index %d)\n", g_rv_shield_idx >= 0 ? "found" : "not found", g_rv_shield_idx >> 12, g_rv_shield_idx & 0xfff);
        }
    }
    if (!g_rv_mrb && k == 0 && g_rv_acc.mask) {          /* offline: the next frame begins at fighter 0: the last one is final */
        if (g_rv_nlines) rv_lines_now(c, &g_rv_acc);
        rv_append(&g_rv_acc, ++g_rv_direct_n);
        g_rv_acc.mask = 0;
    }
    if (!g_rv_scan_done && k == 0) { LARGE_INTEGER a, b; QueryPerformanceCounter(&a); rv_scan_step(c, boma); QueryPerformanceCounter(&b); g_rv_scan_ticks += b.QuadPart - a.QuadPart; }
    else if (k == 0) rv_rescan_step(c, boma, ff);
    rv_sample(c, boma, k, ff);
    {   /* a fighter on the ground where no known line is: the line under its feet, found now (Town and City's platforms) */
        const RvFig *f = &g_rv_acc.s[k].f;
        if (f->situation == 0 && g_rv_scan_final && g_rv_nlines < RV_MAXLINE && f->x > -400 && f->x < 400) {
            int known = 0;
            for (int i = 0; i < g_rv_nlines && !known; i++) {
                const float *L = g_rv_acc.lines[i];
                float lo = L[0] < L[2] ? L[0] : L[2], hi = L[0] < L[2] ? L[2] : L[0];
                if (!(g_rv_lines[i].flags & 1) || f->x < lo - 1 || f->x > hi + 1) continue;
                float t = hi > lo ? (f->x - L[0]) / (L[2] - L[0]) : 0; t = t < 0 ? 0 : t > 1 ? 1 : t;
                float y = L[1] + (L[3] - L[1]) * t;
                if (y - f->y < 1.0f && f->y - y < 1.0f) known = 1;
            }
            if (!known) {
                static float st[4], dir[4], out[4];
                st[0] = f->x; st[1] = f->y + 5; dir[0] = 0; dir[1] = -10; out[0] = out[1] = 0;
                uint64_t line = rv_call5(c, g_rvf.raync ? g_rvf.raync : g_rvf.ray, boma, (uint64_t)(uintptr_t)st, (uint64_t)(uintptr_t)dir, (uint64_t)(uintptr_t)out, 1, NULL);
                if (line) { int was = g_rv_nlines; rv_add_line(c, line); if (g_rv_nlines > was) rv_lines_now(c, &g_rv_acc); }
            }
        }
    }
    if (g_rv_mrb && k == 0 && g_rv_nlines) { LARGE_INTEGER a, b; QueryPerformanceCounter(&a); rv_lines_now(c, &g_rv_acc); QueryPerformanceCounter(&b); g_rv_lines_ticks += b.QuadPart - a.QuadPart; }
    g_rv_acc.mask |= (uint8_t)(1 << k);
    *c = saved;
    ReleaseSRWLockExclusive(&g_rv_lock);
    LARGE_INTEGER t1; QueryPerformanceCounter(&t1); g_rv_cost_ticks += t1.QuadPart - t0.QuadPart; g_rv_cost_n++;
    if (g_rv_scan_done) {
        long long dt = t1.QuadPart - t0.QuadPart; double us = dt * 1e6 / (double)g_rv_qpf.QuadPart;
        if (dt > g_rv_cost_max) g_rv_cost_max = dt;
        g_rv_cost_hist[us < 20 ? 0 : us < 50 ? 1 : us < 100 ? 2 : 3]++;
    }
}
/* rb_net.c rbn_pass_hash: the safe point after pass f (0 = the base state: a new match) */
void rv_pass(long long f) {
    if (!rv_enabled() || !rv_match_rb()) return;
    AcquireSRWLockExclusive(&g_rv_lock);
    if (f == 0) { ReleaseSRWLockExclusive(&g_rv_lock); rv_begin(); AcquireSRWLockExclusive(&g_rv_lock); }
    else if (g_rv_active && g_rv_ring) { RvSlot *s = &g_rv_ring[f % RV_RING]; memcpy(s, &g_rv_acc, sizeof *s); s->frame = g_rv_acc.mask ? f : -1; }
    g_rv_acc.mask = 0;
    ReleaseSRWLockExclusive(&g_rv_lock);
}
/* rb_net.c rbn_finalize: frame f is final (both pads known, no rollback can reach it any more) */
void rv_final(long long f) {
    if (!g_rv_on || !g_rv_mrb || !g_rv_active || !g_rv_ring || f <= g_rv_last_final) return;
    AcquireSRWLockExclusive(&g_rv_lock);
    const RvSlot *s = &g_rv_ring[f % RV_RING];
    if (s->frame == f) rv_append(s, f);
    g_rv_last_final = f;
    ReleaseSRWLockExclusive(&g_rv_lock);
}
/* nnhid.c (through fight_seq_ticks), at player 1's pad poll: the match ended when the game's ready-go flag is cleared */
static void rv_poll(void) {
    static int was;
    if (g_rv_on <= 0) return;
    int on = rv_fight_on();
    if (was && !on && g_rv_active) { AcquireSRWLockExclusive(&g_rv_lock); rv_end("ready-go cleared", 0); ReleaseSRWLockExclusive(&g_rv_lock); }
    if (was && !on) g_rv_skip = 0;                      /* (a match not recorded: the next one is decided again) */
    was = on;
}
static void rv_exit(void) { if (g_rv_active) rv_end("process exit", 1); }
/* debug_mode.c SSBU_DEBUG_END (tests): the match so far is written now, before the exit request */
void rv_flush_now(void) { if (g_rv_on > 0 && g_rv_active) { AcquireSRWLockExclusive(&g_rv_lock); rv_end("debug end", 1); ReleaseSRWLockExclusive(&g_rv_lock); } }
