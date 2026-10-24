/* nro_cov.c - TEST ONLY: which lifted functions of one script module a run executes. Contains nothing of the game.
 *
 * Linked into a module DLL only by a coverage build (build_nro.py with RECOMP_NRO_COVERAGE=1, which also compiles the
 * module's lifted code with -finstrument-functions-after-inlining and -DNRO_MODULE=<module>). Every lifted function then
 * calls __cyg_profile_func_enter(itself, caller) on entry; the hook below sets one byte per 16 bytes of the DLL image
 * (functions are 16-byte aligned). Every 10 s and at exit, the guest addresses of the functions of fn_table_<module>
 * whose host entry is marked are merged into <SSBU_COVERAGE_NRO>\<module>.txt (one "<guest address hex>" per line;
 * what the file already lists is kept, so several runs add up). Unlike runtime/coverage.c (guard pages of the game DLL,
 * a whole 4 KB page at a time) the unit here is the function itself: the module functions are small and many share a
 * page. Without SSBU_COVERAGE_NRO the hook only marks the map; nothing is written. The shipped build never has this. */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include "runtime.h"

#define NI __attribute__((no_instrument_function))
#define CAT2(a, b) a##b
#define CAT(a, b) CAT2(a, b)
#define STR2(a) #a
#define STR(a) STR2(a)

extern const FnEntry CAT(fn_table_, NRO_MODULE)[];
extern const int CAT(fn_table_count_, NRO_MODULE);
extern IMAGE_DOS_HEADER __ImageBase;

static volatile uint8_t *g_map;
static uintptr_t g_lo;
static size_t g_size;
static char g_path[MAX_PATH];

NI void __cyg_profile_func_enter(void *fn, void *site) {
    (void)site;
    uintptr_t a = (uintptr_t)fn - g_lo;
    if (a < g_size && g_map) g_map[a >> 4] = 1;
}
NI void __cyg_profile_func_exit(void *fn, void *site) { (void)fn; (void)site; }

static NI int cmp64(const void *x, const void *y) {
    uint64_t a = *(const uint64_t *)x, b = *(const uint64_t *)y;
    return a < b ? -1 : a > b;
}

static NI void cov_write(void) {
    static SRWLOCK lock = SRWLOCK_INIT;
    if (!g_path[0] || !g_map) return;
    AcquireSRWLockExclusive(&lock);
    const FnEntry *t = CAT(fn_table_, NRO_MODULE);
    int n = CAT(fn_table_count_, NRO_MODULE);
    size_t cap = (size_t)n + 1024, nk = 0;
    uint64_t *keep = malloc(cap * 8);
    if (!keep) { ReleaseSRWLockExclusive(&lock); return; }
    for (int i = 0; i < n; i++) {
        uintptr_t a = (uintptr_t)t[i].fn - g_lo;
        if (a < g_size && g_map[a >> 4]) keep[nk++] = t[i].addr;
    }
    FILE *f = fopen(g_path, "r");
    char ln[64];
    while (f && fgets(ln, sizeof ln, f)) {
        uint64_t a = strtoull(ln, NULL, 16);
        if (!a) continue;
        if (nk == cap) { uint64_t *k2 = realloc(keep, (cap *= 2) * 8); if (!k2) break; keep = k2; }
        keep[nk++] = a;
    }
    if (f) fclose(f);
    qsort(keep, nk, 8, cmp64);
    char tmp[MAX_PATH + 8];
    snprintf(tmp, sizeof tmp, "%s.tmp", g_path);
    f = fopen(tmp, "w");
    for (size_t i = 0; f && i < nk; i++)
        if (!i || keep[i] != keep[i - 1]) fprintf(f, "%llx\n", (unsigned long long)keep[i]);
    if (f) { fclose(f); MoveFileExA(tmp, g_path, MOVEFILE_REPLACE_EXISTING); }
    free(keep);
    ReleaseSRWLockExclusive(&lock);
}

static NI DWORD WINAPI cov_loop(LPVOID arg) { (void)arg; for (;;) { Sleep(10000); cov_write(); } return 0; }
static NI void cov_atexit(void) { cov_write(); }

__attribute__((constructor)) static NI void cov_start(void) {
    uint8_t *base = (uint8_t *)&__ImageBase;
    IMAGE_NT_HEADERS64 *nt = (IMAGE_NT_HEADERS64 *)(base + __ImageBase.e_lfanew);
    g_lo = (uintptr_t)base;
    g_size = nt->OptionalHeader.SizeOfImage;
    g_map = calloc((g_size >> 4) + 1, 1);
    const char *d = getenv("SSBU_COVERAGE_NRO");
    if (!g_map || !d || !d[0]) return;
    CreateDirectoryA(d, NULL);
    snprintf(g_path, sizeof g_path, "%s\\%s.txt", d, STR(NRO_MODULE));
    atexit(cov_atexit);
    HANDLE h = CreateThread(NULL, 0, cov_loop, NULL, 0, NULL);
    if (h) CloseHandle(h);
    printf("coverage (TEST ONLY): %s records its executed functions in %s\n", STR(NRO_MODULE), g_path);
}
