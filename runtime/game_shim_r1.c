/* Track R1 round 3: the shim of ssbu_game_p15r1.dll (built with -DR1_MARK): game_shim_eh.c plus the dirty-page maps every
 * guest store of the lifted code marks (runtime.h R1M). The exe finds them with GetProcAddress and sets them; until then
 * both windows are empty and nothing is marked. No game content. */
#include "game_shim_eh.c"
__declspec(dllexport) uint8_t *r1_m1, *r1_m2;
__declspec(dllexport) uint64_t r1_lo1, r1_n1, r1_n2;
#ifdef R1_GAMEDLL
/* (rb-8ms) set by the runtime (r1_state.c mark_bind): runtime.h rt_fmaf's exact-zero path (SSBU_FMAZERO, default 1) and the
 * vector copies of the hottest NEON functions (neon_fast.h, tu_prepare.py RECOMP_NF_FNS; SSBU_FASTNEON, default 0) */
__declspec(dllexport) int r1_fmazero = 1;
__declspec(dllexport) int r1_fastneon;
#endif
