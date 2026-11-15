/* (online-mm 15/11) The game code: which exe and which game DLL this game runs, as one short text the lobby and the rollback
 * handshake compare (tools/lifter/game_code.py writes the parts; this file only reads them). No game content.
 *
 *   "e<exe 8 hex>d<DLL 8 hex><DLL settings 4 hex>"
 *
 *   exe part       SSBU_EXE_CODE, given by build.py when it compiles this file (the runtime's own sources)
 *   DLL part       the game DLL's export ssbu_dll_code ("SSBU-GAME-CODE:d<8 hex><4 hex>", build.py game_code_object);
 *                  "d-none" for a DLL built before the code existed
 *   expected       SSBU_DLL_WANT: the DLL part of the sources this exe was built with. A DLL whose part differs was built
 *                  from other sources (13/11: exe 0.2.49 + a test DLL r8n6 the launcher's update left in place): logged at
 *                  the start; the launcher builds the DLL again before PLAY (GameCode.cs, SSBU_ARENA_CODE_CHECK)
 *
 * Switches: SSBU_GAME_CODE_TEST=<text> (TEST ONLY) this text instead of the real code (two games of one PC that should not
 * pair: lobby and handshake checks). net_relay.c sends the code in the JOIN ("GC" block: the lobby pairs only equal codes),
 * rb_net.c in every handshake message (SSBU_RB_NET_CODE_CHECK). */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifndef SSBU_EXE_CODE
#define SSBU_EXE_CODE "unknown"                /* (a build without game_code.py) */
#endif
#ifndef SSBU_DLL_WANT
#define SSBU_DLL_WANT "unknown"
#endif
/* in the exe's .ssbuid section too: the launcher reads them from the file (GameCode.cs) */
#ifdef _WIN32
__attribute__((section(".ssbuid"))) const char ssbu_exe_code_tag[] = "SSBU-EXE-CODE:" SSBU_EXE_CODE;
__attribute__((section(".ssbuid"))) const char ssbu_dll_want_tag[] = "SSBU-DLL-WANT:" SSBU_DLL_WANT;
#else
const char ssbu_exe_code_tag[] = "SSBU-EXE-CODE:" SSBU_EXE_CODE;
const char ssbu_dll_want_tag[] = "SSBU-DLL-WANT:" SSBU_DLL_WANT;
#endif

static SRWLOCK g_lock = SRWLOCK_INIT; static volatile LONG g_done;
static char g_code[64], g_dll[32];
static int g_dll_ok;                            /* 1: the DLL part is the one this exe expects; 0: another one, or unknown */

static void init(void) {
    const char *names[] = {getenv("SSBU_R1_GAMEDLL"), "ssbu_game_r1.dll", "ssbu_game.dll", "ssbu_game_eh_o1.dll", "ssbu_game_p15r1.dll", "ssbu_game_eh_o1.so", "ssbu_game_eh_o1.dylib"};
    const char *code = NULL;
    for (int i = 0; i < (int)(sizeof names / sizeof *names) && !code; i++) {
        if (!names[i] || !names[i][0]) continue;
        HMODULE m = GetModuleHandleA(names[i]);
        const char *s = m ? (const char *)GetProcAddress(m, "ssbu_dll_code") : NULL;
        if (s && !strncmp(s, "SSBU-GAME-CODE:", 15)) code = s + 15;
    }
    snprintf(g_dll, sizeof g_dll, "%.24s", code ? code : "d-none");
    /* the DLL text is d<8 hex><4 hex>: its first 8 after the d are the sources' part */
    g_dll_ok = code && strlen(SSBU_DLL_WANT) == 8 && !strncmp(g_dll + 1, SSBU_DLL_WANT, 8);
    const char *t = getenv("SSBU_GAME_CODE_TEST");
    if (t && *t) snprintf(g_code, sizeof g_code, "%.40s", t);
    else snprintf(g_code, sizeof g_code, "e%s%s", SSBU_EXE_CODE, g_dll);
    printf("game code: %s (exe e%s, game DLL %s, this exe expects d%s: %s)%s\n", g_code, SSBU_EXE_CODE, g_dll, SSBU_DLL_WANT,
           g_dll_ok ? "the same sources" : code ? "ANOTHER DLL: built from other sources, the launcher builds it again" : "a DLL without a code",
           t && *t ? " [SSBU_GAME_CODE_TEST]" : "");
    fflush(stdout);
}
static void once(void) {
    if (g_done) return;
    AcquireSRWLockExclusive(&g_lock);
    if (!g_done) { init(); g_done = 1; }
    ReleaseSRWLockExclusive(&g_lock);
}

/* the text sent to the lobby and in the handshake ("" never: "e..d-none" for an old DLL) */
const char *game_code(void) { once(); return g_code; }
/* 1 = the game DLL was built from the sources this exe expects */
int game_code_dll_ok(void) { once(); return g_dll_ok; }
/* the referenced tags (kept by the linker's /OPT:REF even if nothing else read them) */
const char *game_code_tags(void) { return ssbu_exe_code_tag[0] && ssbu_dll_want_tag[0] ? ssbu_exe_code_tag : ""; }
