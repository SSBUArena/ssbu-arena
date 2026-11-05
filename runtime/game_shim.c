/* Linked into ssbu_game.dll together with the lifted game code (obj\*.obj).
 *
 * The lifted code is 700+ MB of objects that change only when the lifter changes; keeping it in a
 * DLL means the host runtime (the part edited all day) links in a second or two. The DLL cannot
 * import from the exe, so the six host functions the lifted code calls are forwarded through
 * pointers the exe hands over once at start-up (gamedll.c). */
#include "runtime.h"

typedef struct {
    void (*call_indirect)(Ctx *, uint64_t);
    void (*host_import)(Ctx *, int);
    void (*guest_trap)(const char *, uint64_t);
    void (*guest_unimpl)(uint64_t, uint32_t, const char *);
    void (*guest_svc)(Ctx *, uint32_t);
    void (*stx_diag)(Ctx *, uint64_t, uint64_t, uint64_t, uint64_t, int);
    /* appended for a DLL re-lifted from a site list (tools/lifter/relift_sites.py): callouts compiled into re-lifted
     * functions at the addresses of plugin inline hooks / mid-function hooks. An older exe hands over a struct without
     * them; only a DLL built from a site list calls them. */
    void (*site_inline)(Ctx *, uint64_t);
    int (*site_midhook)(Ctx *, uint64_t);
} GameHost;

typedef struct {
    const FnEntry *fn_table;
    const int *fn_table_count;
    const DynSym *dyn_syms;
    const int *dyn_sym_count;
} GameTables;

static GameHost host;

void call_indirect(Ctx *c, uint64_t addr) { host.call_indirect(c, addr); }
/* (game code +30%) the miss of a call site's cache (runtime.h RECOMP_ICACHE): the call goes the usual way; before it, when
 * the exe turned the cache on (gamedll.c sets rt_ic_on; an older exe never does) and the target is a lifted function of
 * main's image, the site's word is filled from fn_table (sorted by address, the table the exe's lookup is built from). */
__declspec(dllexport) volatile int rt_ic_on;
void rt_ic_miss(Ctx *c, uint64_t addr, uint64_t *slot) {
    uint64_t e = *(volatile uint64_t *)slot;
    if (e) {                 /* a second target at this site (a virtual call on objects of several classes): the site is
                              * marked with ~0 (never a hit) and keeps the usual way, without the search (measured with PMC:
                              * searching fn_table at every call of such sites cost more than the cache saved) */
        if (e != ~0ull) *(volatile uint64_t *)slot = ~0ull;
        host.call_indirect(c, addr);
        return;
    }
    if (rt_ic_on && addr - 0x7100000000ull < 0x7500000ull) {
        int lo = 0, hi = fn_table_count - 1;
        while (lo <= hi) {
            int mid = (lo + hi) / 2;
            if (fn_table[mid].addr == addr) {
                intptr_t off = (intptr_t)(uintptr_t)fn_table[mid].fn - (intptr_t)(uintptr_t)rt_ic_miss;
                if (off == (intptr_t)(int32_t)off) *(volatile uint64_t *)slot = ((addr - 0x7100000000ull) << 32) | (uint32_t)(int32_t)off;
                break;
            }
            if (fn_table[mid].addr < addr) lo = mid + 1; else hi = mid - 1;
        }
    }
    host.call_indirect(c, addr);
}
void host_import(Ctx *c, int sym) { host.host_import(c, sym); }
void guest_trap(const char *what, uint64_t addr) { host.guest_trap(what, addr); }
void guest_unimpl(uint64_t pc, uint32_t insn, const char *what) { host.guest_unimpl(pc, insn, what); }
void guest_svc(Ctx *c, uint32_t number) { host.guest_svc(c, number); }
void stx_diag(Ctx *c, uint64_t a, uint64_t expected, uint64_t seen, uint64_t v, int bits) { host.stx_diag(c, a, expected, seen, v, bits); }
/* Site callouts (relift_sites.py). sky_site_inline: before the instruction at `addr`; the callback may read and change
 * x0..x30 and v0..v31 (they are all in c here: the lifted code keeps registers in the context). sky_site_midhook: a
 * function hook written at a non-entry address; 1 = the replacement has run (as a tail call), the lifted function
 * returns at once with the current registers. */
void sky_site_inline(Ctx *c, uint64_t addr) { host.site_inline(c, addr); }
int sky_site_midhook(Ctx *c, uint64_t addr) { return host.site_midhook(c, addr); }

__declspec(dllexport) void game_bind(const GameHost *h, GameTables *t) {
    host = *h;
    t->fn_table = fn_table;
    t->fn_table_count = &fn_table_count;
    t->dyn_syms = dyn_syms;
    t->dyn_sym_count = &dyn_sym_count;
}
