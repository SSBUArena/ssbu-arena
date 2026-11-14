/* Track R1 round 3: the shim of ssbu_game_p15r1.dll (built with -DR1_MARK): game_shim_eh.c plus the dirty-page maps every
 * guest store of the lifted code marks (runtime.h R1M). The exe finds them with GetProcAddress and sets them; until then
 * both windows are empty and nothing is marked. No game content. */
#include "game_shim_eh.c"
__declspec(dllexport) uint8_t *r1_m1, *r1_m2;
__declspec(dllexport) uint64_t r1_lo1, r1_n1, r1_n2;
#ifdef R1_LINEMARK
/* (round 10) runtime.h R1_LINEMARK: the byte-per-64-byte-line maps of both windows (r1_ring.c SSBU_RB_LINES); set by the runtime before r1_n1.
 * Exported only by a DLL built with line marks: the runtime turns line loads on only when it finds them. */
__declspec(dllexport) uint8_t *r1_l1, *r1_l2;
#endif
#ifdef R1_GAMEDLL
/* (rb-8ms) set by the runtime (r1_state.c mark_bind): runtime.h rt_fmaf's exact-zero path (SSBU_FMAZERO, default 1) and the
 * vector copies of the hottest NEON functions (neon_fast.h, tu_prepare.py RECOMP_NF_FNS; SSBU_FASTNEON, default 0) */
__declspec(dllexport) int r1_fmazero = 1;
__declspec(dllexport) int r1_fastneon;
/* (rb-8ms 13/11) runtime.h R1MC: 1 = a store that leaves the bytes as they were marks nothing (SSBU_R1_MARKCMP); 0 until the runtime sets it */
__declspec(dllexport) int r1_mcmp;
#ifdef R1_RDCOUNT
/* (rb-8ms 14/11, TEST ONLY) runtime.h RC_ACC and the entries of tu_prepare.py RECOMP_RC_FNS: set by the runtime (rc_test.c, SSBU_RC) */
__declspec(dllexport) volatile int r1_rc_on;
__declspec(dllexport) void (*r1_rc_acc)(uint64_t a, const void *v, int n, int w);
__declspec(dllexport) void (*r1_rc_ent)(Ctx *c, uint64_t fn, int out);
#endif
#ifdef R1_MEMO
/* (rb-8ms 14/11) runtime.h MR_ACC and the entries of tu_prepare.py RECOMP_MEMO_FNS: set by the runtime (memo.c, SSBU_RB_MEMO) */
__declspec(dllexport) unsigned r1_mr_on;
__declspec(dllexport) uint64_t r1_mr_slo, r1_mr_sn;
__declspec(dllexport) void (*r1_mr_acc)(uint64_t a, uint64_t lo, uint64_t hi, uint64_t nw);      /* nw = size | kind << 16 */
__declspec(dllexport) int (*r1_mo_ent)(Ctx *c, uint64_t fn, int out);
/* runtime.h MR: the logger's call from inside lifted code, every register kept (the asm statement there clobbers only the flags):
 * rcx = address, rdx / r8 = value, r9 = size | kind << 16 go to r1_mr_acc unchanged; rax, r10, r11, xmm0-5 saved; stack aligned and
 * the callee's shadow space made here (the lifted function may be a leaf: its rsp is not known to be aligned). */
__asm__(".text\n.globl r1_mr_thunk\n.p2align 4\nr1_mr_thunk:\n"
        "  pushq %rbp\n  movq %rsp, %rbp\n  pushq %rax\n  pushq %rcx\n  pushq %rdx\n  pushq %r8\n  pushq %r9\n  pushq %r10\n  pushq %r11\n"
        "  andq $-16, %rsp\n  subq $128, %rsp\n"
        "  movdqu %xmm0, 32(%rsp)\n  movdqu %xmm1, 48(%rsp)\n  movdqu %xmm2, 64(%rsp)\n  movdqu %xmm3, 80(%rsp)\n  movdqu %xmm4, 96(%rsp)\n  movdqu %xmm5, 112(%rsp)\n"
        "  callq *r1_mr_acc(%rip)\n"
        "  movdqu 32(%rsp), %xmm0\n  movdqu 48(%rsp), %xmm1\n  movdqu 64(%rsp), %xmm2\n  movdqu 80(%rsp), %xmm3\n  movdqu 96(%rsp), %xmm4\n  movdqu 112(%rsp), %xmm5\n"
        "  leaq -56(%rbp), %rsp\n  popq %r11\n  popq %r10\n  popq %r9\n  popq %r8\n  popq %rdx\n  popq %rcx\n  popq %rax\n  popq %rbp\n  retq\n");
#endif
#endif
