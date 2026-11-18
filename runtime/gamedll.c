/* Host side of the exe / ssbu_game.dll split (see game_shim.c).
 *
 * build.py compiles every host file with
 *     -Dfn_table=(*game_fn_table_p) -Dfn_table_count=(*game_fn_table_count_p)
 *     -Ddyn_syms=(*game_dyn_syms_p) -Ddyn_sym_count=(*game_dyn_sym_count_p)
 * so the tables declared in runtime.h become pointers into the DLL, filled in here before main(). */
#include <stdio.h>
#include <stdlib.h>
#ifdef _WIN32
#include <windows.h>
#endif
#include "runtime.h"
#include "game_data.h"

typedef struct {
    void (*call_indirect)(Ctx *, uint64_t);
    void (*host_import)(Ctx *, int);
    void (*guest_trap)(const char *, uint64_t);
    void (*guest_unimpl)(uint64_t, uint32_t, const char *);
    void (*guest_svc)(Ctx *, uint32_t);
    void (*stx_diag)(Ctx *, uint64_t, uint64_t, uint64_t, uint64_t, int);
    void (*site_inline)(Ctx *, uint64_t);      /* appended (game_shim.c): an older DLL copies only the fields it knows */
    int (*site_midhook)(Ctx *, uint64_t);
} GameHost;

typedef struct {
    const FnEntry *table;
    const int *table_count;
    const DynSym *syms;
    const int *sym_count;
} GameTables;

__declspec(dllimport) void game_bind(const GameHost *h, GameTables *t);
/* game_data.h: the dump-derived tables and the lifted SDK code, in the DLL too (game_data.c) */
__declspec(dllimport) void game_data_bind(const GameDataHost *h, GameData *d);
GameData game_data;
void sdk_host_call(Ctx *c, int index);                   /* sdklift_rt.c */
void sdk_trap_call(Ctx *c, const char *name);
void sdk_dispatch(Ctx *c, uint64_t addr);
void *sdk_jmp_record(Ctx *c);
void sdk_jmp_restore(Ctx *c, void *r);
void *sdk_longjmp_target(Ctx *c);

/* The site callouts of a DLL re-lifted from a site list (tools/lifter/relift_sites.py) go to the Skyline layer
 * (skyline.c, included by nnro.c). */
void sky_site_inline(Ctx *c, uint64_t addr);
int sky_site_midhook(Ctx *c, uint64_t addr);

#undef fn_table
#undef fn_table_count
#undef dyn_syms
#undef dyn_sym_count
const FnEntry (*game_fn_table_p)[];
const int *game_fn_table_count_p;
const DynSym (*game_dyn_syms_p)[];
const int *game_dyn_sym_count_p;

/* (rb-8ms 14/11, SSBU_RB_MEMO) the runtime's memo.c (r1_state.c): while main records a call of a memo unit (g_mo_rec_tid = its
 * thread id, else 0), every way out of the lifted code into host code is noted (mo_esc_note): host code reads and writes guest memory
 * without the record's log, so such a record is never replayed. No way out: an indirect call to a function of main's image (a lifted
 * function of the game DLL; a hook on it is seen by the hook stubs, hook_host.c), an import bound to lifted SDK code (in the game DLL),
 * a lifted SDK function reached through sdk_dispatch. The other threads, and main when it does not record: one load and a branch. */
volatile DWORD g_mo_rec_tid; uintptr_t g_mo_dll_lo, g_mo_dll_hi;
void mo_esc_note(int kind, int sym, uint64_t addr);
void *hostrt_import_handler(int sym);
#ifdef _WIN32
#define MO_REC() (g_mo_rec_tid && GetCurrentThreadId() == g_mo_rec_tid)
#else
#define MO_REC() 0
#endif
static int mo_in_dll(const void *p) { return p && (uintptr_t)p - g_mo_dll_lo < g_mo_dll_hi - g_mo_dll_lo; }
static void mo_call_indirect(Ctx *c, uint64_t addr) { if (MO_REC() && addr - 0x7100000000ULL >= 0x7500000ULL) mo_esc_note(1, -1, addr); call_indirect(c, addr); }
static void mo_host_import(Ctx *c, int sym) { if (MO_REC() && !mo_in_dll(hostrt_import_handler(sym))) mo_esc_note(2, sym, c->x[30]); host_import(c, sym); }
static void mo_guest_trap(const char *w, uint64_t a) { if (MO_REC()) mo_esc_note(3, -1, a); guest_trap(w, a); }
static void mo_guest_unimpl(uint64_t pc, uint32_t insn, const char *w) { if (MO_REC()) mo_esc_note(4, -1, pc); guest_unimpl(pc, insn, w); }
static void mo_guest_svc(Ctx *c, uint32_t n) { if (MO_REC()) mo_esc_note(5, (int)n, c->x[30]); guest_svc(c, n); }
static void mo_stx_diag(Ctx *c, uint64_t a, uint64_t e, uint64_t s, uint64_t v, int b) { if (MO_REC()) mo_esc_note(6, -1, a); stx_diag(c, a, e, s, v, b); }
static void mo_site_inline(Ctx *c, uint64_t a) { if (MO_REC()) mo_esc_note(7, -1, a); sky_site_inline(c, a); }
static int mo_site_midhook(Ctx *c, uint64_t a) { if (MO_REC()) mo_esc_note(8, -1, a); return sky_site_midhook(c, a); }
static void mo_sdk_host_call(Ctx *c, int i) { if (MO_REC()) mo_esc_note(9, i, c->x[30]); sdk_host_call(c, i); }
static void mo_sdk_trap_call(Ctx *c, const char *n) { if (MO_REC()) mo_esc_note(10, -1, c->x[30]); sdk_trap_call(c, n); }
static void mo_sdk_dispatch(Ctx *c, uint64_t a) { if (MO_REC() && a - 0x7500000000ULL >= 0xcfb000ULL) mo_esc_note(11, -1, a); sdk_dispatch(c, a); }
static void *mo_sdk_jmp_record(Ctx *c) { if (MO_REC()) mo_esc_note(12, -1, c->x[30]); return sdk_jmp_record(c); }
static void mo_sdk_jmp_restore(Ctx *c, void *r) { if (MO_REC()) mo_esc_note(12, -1, c->x[30]); sdk_jmp_restore(c, r); }
static void *mo_sdk_longjmp_target(Ctx *c) { if (MO_REC()) mo_esc_note(12, -1, c->x[30]); return sdk_longjmp_target(c); }

#ifdef _WIN32
/* (NEON generator 16/11) a game DLL built with RECOMP_NEON_VEC (neon_vec.h): its fused multiply-adds use FMA3 and its TBL PSHUFB when
 * the CPU has them (game_shim.c rt_cpu_detect), the generic x86-64 ways otherwise; the same results bit for bit. SSBU_NEON_FMA=0 /
 * SSBU_NEON_SSSE3=0 (tests): the generic ways on this CPU too. A test DLL with RECOMP_NEON_CHECK counts its checks (rt_nchk): a
 * "NEONCHK" line on stderr every 10 s while they grow, with the first samples of any difference. An older DLL has none of these. */
static volatile long long *g_nchk, *g_nchk_op; static volatile uint32_t (*g_nchk_log)[20];
static DWORD WINAPI neon_chk_report(LPVOID p) {
    (void)p; long long last = 0; unsigned shown = 0;
    for (;;) {
        Sleep(10000);
        long long n = g_nchk[0];
        if (n == last) continue;
        last = n;
        fprintf(stderr, "NEONCHK ops %lld | two ways differ %lld | differ from the old helper: with NaN / inf %lld, other %lld | generator 2 forms checked: "
                "folded loads %lld, fixed shuffles %lld, vector bit ops %lld\n", n, g_nchk[1], g_nchk[2], g_nchk[3], g_nchk[4], g_nchk[5], g_nchk[6]);
        if (g_nchk_op) { fprintf(stderr, "NEONCHK NaN/inf differences per op:"); for (int k = 0; k < 512; k++) if (g_nchk_op[k]) fprintf(stderr, " %d=%lld", k, g_nchk_op[k]); fputc(10, stderr); }
        for (int k = 0; k < (g_nchk_op ? 24 : 16); k++) {
            if (g_nchk_log[k][0] == 0 || (shown >> k) & 1) continue;
            volatile uint32_t *l = g_nchk_log[k]; shown |= 1u << k;
            fprintf(stderr, "NEONCHK sample kind %u op %u: got %08x %08x %08x %08x other %08x %08x %08x %08x in %08x %08x %08x %08x / %08x %08x %08x %08x\n",
                    l[0], l[1], l[2], l[3], l[4], l[5], l[6], l[7], l[8], l[9], l[10], l[11], l[12], l[13], l[14], l[15], l[16], l[17]);
        }
        fflush(stderr);
    }
    return 0;
}
static void neon_bind(HMODULE m) {
    int *fma = (int *)(void *)GetProcAddress(m, "rt_cpu_fma"), *ss = (int *)(void *)GetProcAddress(m, "rt_cpu_ssse3");
    const char *e1 = getenv("SSBU_NEON_FMA"), *e2 = getenv("SSBU_NEON_SSSE3");
    if (!fma || !ss) return;
    if (e1 && e1[0] == '0') *fma = 0;
    if (e2 && e2[0] == '0') *ss = 0;
    fprintf(stderr, "[neon] game DLL NEON: FMA3 %s, SSSE3 %s (used only by a DLL built with RECOMP_NEON_VEC)\n", *fma ? "on" : "off", *ss ? "on" : "off");
    g_nchk = (volatile long long *)(void *)GetProcAddress(m, "rt_nchk"); g_nchk_op = (volatile long long *)(void *)GetProcAddress(m, "rt_nchk_op"); g_nchk_log = (volatile uint32_t (*)[20])(void *)GetProcAddress(m, "rt_nchk_log");
    if (g_nchk && g_nchk_log) CloseHandle(CreateThread(NULL, 0, neon_chk_report, NULL, 0, NULL));
}
#endif

__attribute__((constructor)) static void bind_game(void) {
    GameHost h = {call_indirect, host_import, guest_trap, guest_unimpl, guest_svc, stx_diag, sky_site_inline, sky_site_midhook};
    const char *mo_e = getenv("SSBU_RB_MEMO"); int mo = mo_e && atoi(mo_e) > 0;      /* (SSBU_RB_MEMO) the ways out watched only then */
    if (mo) { GameHost hm = {mo_call_indirect, mo_host_import, mo_guest_trap, mo_guest_unimpl, mo_guest_svc, mo_stx_diag, mo_site_inline, mo_site_midhook}; h = hm; }
    GameTables t;
    game_bind(&h, &t);
    game_fn_table_p = (const FnEntry (*)[])t.table;
    game_fn_table_count_p = t.table_count;
    game_dyn_syms_p = (const DynSym (*)[])t.syms;
    game_dyn_sym_count_p = t.sym_count;
#ifdef _WIN32
    {   /* (game code +30%) the call sites' caches of a DLL built with RECOMP_ICACHE (game_shim.c rt_ic_miss): on, unless a
         * diagnostic must see every indirect call (hostrt.c icall_record) or SSBU_ICACHE=0. An older DLL has no rt_ic_on. */
        HMODULE m = NULL; const char *e = getenv("SSBU_ICACHE");
        if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)t.table, &m) && m) {
            volatile int *on = (volatile int *)(void *)GetProcAddress(m, "rt_ic_on");
            if (on) *on = !(e && e[0] == '0') && !getenv("SSBU_ICALLS") && !getenv("SSBU_ICALLS_STEP") && !getenv("SSBU_EXPERIMENT_POKE");
            neon_bind(m);
            {   const IMAGE_DOS_HEADER *dh = (const IMAGE_DOS_HEADER *)(void *)m; const IMAGE_NT_HEADERS *nt = (const IMAGE_NT_HEADERS *)((const char *)m + dh->e_lfanew);
                g_mo_dll_lo = (uintptr_t)m; g_mo_dll_hi = (uintptr_t)m + nt->OptionalHeader.SizeOfImage; }      /* (SSBU_RB_MEMO) the game DLL's image */
        }
    }
#endif
    GameDataHost dh = {GAME_DATA_VERSION, sdk_host_call, sdk_trap_call, sdk_dispatch, sdk_jmp_record, sdk_jmp_restore, sdk_longjmp_target};
    if (mo) { GameDataHost dm = {GAME_DATA_VERSION, mo_sdk_host_call, mo_sdk_trap_call, mo_sdk_dispatch, mo_sdk_jmp_record, mo_sdk_jmp_restore, mo_sdk_longjmp_target}; dh = dm; }
    game_data_bind(&dh, &game_data);
    if (game_data.version != GAME_DATA_VERSION || game_data.size != (int)sizeof game_data) {
        fprintf(stderr, "the game DLL was built for another version of this exe (game data interface %d, this exe %d): build both "
                        "from the same tree (scripts/setup.py)\n", game_data.version, GAME_DATA_VERSION);
        exit(9);
    }
}
