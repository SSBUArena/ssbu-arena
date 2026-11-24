/* R3 (TEST ONLY, default OFF): the ONLINE ROLLBACK SESSION, the parts that do not touch the game's state
 * (plan\phases\R3_online_rollback.md). The game side (snapshot ring, re-run passes, the pads given to the game) is
 * rb_rollback.c, included by r1_state.c; the transport is net_relay.c (packet type 16, through the lobby / the host's
 * game). This file: the input queues (rb_input.c), the wire format (rb_wire.c), the time sync (rb_timesync.c), the
 * receive queue, the RTT / clock-offset measurement, the per-frame state checksums, a test bot and the session report.
 * Design after Slippi's netplay (project-slippi/Ishiiruka, GPL-2.0-or-later) and melee-unlocked's mu_net
 * (GPL-2.0-or-later), N1 / N1b studies; no code copied. No game content in this file.
 *
 *   SSBU_RB_NET=net        two games over net_relay (SSBU_ONLINE host / join / lobby with a private code)
 *   SSBU_RB_NET=loop[,<f>] one game: the other player is a bot whose pads "arrive" <f> frames late (default 4):
 *                          rollback without a network, for the determinism proof (f = 0: never late = reference)
 *   SSBU_RB_NET_PLAYER=0|1 this game's player (default: 0, 1 in SSBU_ONLINE=join mode)
 *   SSBU_RB_NET_DELAY=<n>  input delay in frames (default: from the measured round trip, 1..5); "auto": from the ping, agreed
 *                          with the other game (SSBU_RB_AUTO_DELAY_PING, auto_ping_frames: 150 ms -> 4, at most 7; SSBU_RB_AUTO_DELAY_V2=0: 5, at most 6)
 *   SSBU_RB_NET_BOT=<seed> the local player's pads come from a test bot instead of the controller / input sequence
 *   SSBU_RB_NET_LOG=<file> one line per finalized frame: "f <frame> h <checksum>" (compare two games' files)
 *   SSBU_RB_NET_AHEAD=<n>  most frames this game runs ahead of the newest remote pad (default 7 = the ring depth)
 *   SSBU_RB_NET_FIND=0     (R3 net3) no opponent search through the lobby (lobby / LAN mode without a room: the old way)
 * SPDX-License-Identifier: GPL-3.0-or-later */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>
#include <math.h>
#ifndef _WIN32
#include <sys/stat.h>
#endif
#include "rb.h"
#include "rb_input.c"
#include "rb_wire.c"
#include "rb_timesync.c"
#undef M
#include "net_stats.h"                                  /* (R3 net3) NetMatch, MM_*: the lobby's opponent search (net_relay.c) */

int net_rb_send(const void *p, int n);                 /* net_relay.c: 1 = sent (or queued by the latency simulation) */
int net_rb_ready(void);                                /* net_relay.c: joined a lobby (the host's game or a server) */
int net_rb_no_destination(void);                       /* net_relay.c (pub1): messages not sent: no other player in the room on the public lobby */
int net_mode(void);
int net_online(void);

static int g_on = -1, g_loop, g_loop_lat = 4, g_local, g_delay_cfg = -1, g_ahead = 7, g_started, g_peer_started;
static int g_delay_auto, g_delay_agreed;               /* (rb-8ms) SSBU_RB_NET_DELAY=auto; the delay both games agreed in the handshake (0 = none) */
static unsigned g_bot_seed; static int g_bot;
static FILE *g_hlog;
static volatile LONG64 g_cur;                          /* the frame this game is on (read by the receive thread for PONGs) */
static LARGE_INTEGER g_qpf;
static uint64_t now_us(void) { LARGE_INTEGER t; QueryPerformanceCounter(&t); if (!g_qpf.QuadPart) QueryPerformanceFrequency(&g_qpf); return (uint64_t)(t.QuadPart / (double)g_qpf.QuadPart * 1e6); }

int rbn_on(void) {
    if (g_on >= 0) return g_on;
    const char *e = getenv("SSBU_RB_NET");
    g_on = e && e[0] && e[0] != '0';
    if (!g_on) return 0;
    {   int game_rb_blocked(void);                      /* (issue #6) 13.0.5: no rollback (hostrt.c; SSBU_R1_1305=1: as before) */
        if (game_rb_blocked()) { g_on = 0; printf("[rbnet] off: online rollback is not supported on game update 13.0.5 yet (SSBU_RB_NET=%s asked for it; SSBU_R1_1305=1 turns it on)\n", e); fflush(stdout); return 0; } }
    if (!_strnicmp(e, "loop", 4)) { g_loop = 1; if (e[4] == ',') g_loop_lat = atoi(e + 5); }
    g_local = 0;
    if (!g_loop && net_online() && net_mode() == 3) g_local = 1;      /* NET_MODE_JOIN */
    if ((e = getenv("SSBU_RB_NET_PLAYER"))) g_local = atoi(e) ? 1 : 0;
    if ((e = getenv("SSBU_RB_NET_DELAY"))) { if (!_stricmp(e, "auto")) g_delay_auto = 1; else g_delay_cfg = atoi(e); }      /* (rb-8ms) "auto": hs_delay_agree */
    if ((e = getenv("SSBU_RB_NET_BOT")) && *e) { g_bot = 1; g_bot_seed = (unsigned)strtoul(e, NULL, 0); }
    if ((e = getenv("SSBU_RB_NET_AHEAD")) && atoi(e) > 0) g_ahead = atoi(e);
    if ((e = getenv("SSBU_RB_NET_LOG")) && *e) g_hlog = fopen(e, "w");
    printf("[rbnet] on: %s, local player %d, delay %s, ahead at most %d, bot %s\n", g_loop ? "loop test (no network)" : "over net_relay", g_local + 1, g_delay_cfg >= 0 ? "fixed" : g_delay_auto ? "auto (from the round trip, agreed with the other game)" : "from the round trip", g_ahead, g_bot ? "on" : "off");
    if (g_loop) printf("[rbnet] loop: remote pads arrive %d frames after the frame that would send them\n", g_loop_lat);
    fflush(stdout);
    return g_on;
}
int rbn_local_player(void) { return g_local; }
long long rbn_now(void) { return g_cur; }                /* (Arena, tests) the frame this game is on (nnhid.c's fight screenshots name it) */
int rbn_ahead(void) { return g_ahead; }

/* ---------------------------------------------------------------- the test bot: a pure function of (seed, frame) */
static uint32_t mix32(uint32_t x) { x ^= x >> 16; x *= 0x7feb352du; x ^= x >> 15; x *= 0x846ca68bu; x ^= x >> 16; return x; }
static RbPad bot_pad0(unsigned seed, long long f);
/* (rb-8ms 13/11, OpenSmash #181, TEST) SSBU_RB_NET_BOT_CSTICK=1: the bot also uses the C-stick (rx / ry: smash attacks on the ground,
 * aerials in the air) and the R / ZR triggers (shield, grab), which it never did: the C-stick desync class of their issue #181 was never
 * exercised by our pairs. A second segment stream (its own salt) laid over the bot's pad: with the switch off the bot is unchanged.
 * Pure function of (seed, frame) as the rest: both games of a pair compute the same pads. */
static void bot_cstick(unsigned seed, long long f, RbPad *p) {
    long long st = 1; uint32_t hh = 0; int sg = 0;
    for (;; sg++) { hh = mix32(seed * 0x2545F491u ^ (uint32_t)sg * 0x9E3779B1u ^ 0xC5717C4Bu); long long len = 4 + (hh % 18); if (f < st + len) break; st += len; }
    int a = (int)((hh >> 8) % 12), t = (int)(f - st); const int16_t M = 32767;
    switch (a) {
    case 0: p->rx = M; break;                                          /* C right (held: smash attack / forward aerial) */
    case 1: p->rx = (int16_t)-M; break;                                /* C left */
    case 2: p->ry = M; break;                                          /* C up */
    case 3: p->ry = (int16_t)-M; break;                                /* C down */
    case 4: if (t < 2) p->rx = M; break;                               /* C right flicked */
    case 5: if (t < 2) p->ry = (int16_t)-M; break;                     /* C down flicked */
    case 6: p->rx = (int16_t)((t & 3) < 2 ? M : -M); break;            /* C mashed left / right */
    case 7: p->buttons |= 1ull << 7; break;                            /* R (shield / grab, as the controls map it) */
    case 8: p->buttons |= 1ull << 9; break;                            /* ZR */
    case 9: if (t < 2) p->buttons |= 1ull << 2; else if (t > 3) p->ry = M; break;     /* jump, then C up (up aerial) */
    case 10: p->rx = (int16_t)(M / 2); p->ry = (int16_t)(M / 2); break;        /* C half diagonal (tilt range) */
    case 11: break;                                                    /* nothing */
    }
}
static RbPad bot_pad(unsigned seed, long long f) {       /* (crash phase, test) SSBU_RB_NET_BOT_JITTER=<n>: held sticks tremble by up to +-n every frame */
    static int jit = -1; if (jit < 0) { const char *e = getenv("SSBU_RB_NET_BOT_JITTER"); jit = e ? atoi(e) : 0; }
    static int cs = -1; if (cs < 0) { const char *e = getenv("SSBU_RB_NET_BOT_CSTICK"); cs = e && e[0] == '1'; }
    RbPad p = bot_pad0(seed, f);
    if (cs) bot_cstick(seed, f, &p);
    if (jit > 0) { int16_t *ax[2] = {&p.lx, &p.ly};
        for (int a = 0; a < 2; a++) if (*ax[a] > 1600 || *ax[a] < -1600) {
            int v = *ax[a] + (int)(mix32(seed * 31u + (uint32_t)f * 2u + (uint32_t)a) % (uint32_t)(2 * jit + 1)) - jit;
            *ax[a] = (int16_t)(v > 32767 ? 32767 : v < -32767 ? -32767 : v); } }
    return p;
}
static int g_bp_remote;                                 /* (round 14, TEST) 1 while loop_remote_pad asks the bot for the other player's pad */
volatile long long g_bot_walkoff_at = -1;              /* (rb-8ms round 12, TEST: SSBU_BENCH, rb_bench.c) set at run time: from that frame on the bots walk off */
volatile long g_rbn_loop_ontime;                       /* (0.2.58, player benchmark: rb_bench.c) 1: the loop test's pads arrive in time (no rollbacks) */
static RbPad bot_pad0(unsigned seed, long long f) {
    /* segments of 6..29 frames; each segment one action. Walking alternates sides around the middle of the stage. */
    RbPad p; memset(&p, 0, sizeof p);
    {   static long long off = -2; if (off == -2) { const char *e = getenv("SSBU_RB_NET_BOT_WALKOFF"); off = e && *e ? atoll(e) : -1; }      /* (R3 lwr, test) from that frame on: left held (the fighter walks off the stage: the match ends) */
        static int wj0 = -1; if (wj0 < 0) { const char *e = getenv("SSBU_RB_NET_BOT_WALKJUMP"); wj0 = e && e[0] == '1'; }
        if (off >= 0 && f >= off && !wj0) { p.lx = -32767; return p; }
        if (off >= 0 && f >= off) {                     /* (0.2.53, TEST) WALKOFF + WALKJUMP: dash and jump off the ledge (left held alone stops at the ledge) */
            long long t = f - off;
            if (t % 300 < 2) return p;
            p.lx = (t / 300) % 2 ? 32767 : -32767;
            if (t % 40 >= 20 && t % 40 < 22) p.buttons = 1ull << 2;
            return p; }
        if (g_bot_walkoff_at >= 0 && f >= g_bot_walkoff_at) {
            /* (round 14, TEST) SSBU_RB_NET_BOT_WALKJUMP=1 (default 0 = left held, as before): the stick first neutral for 2 frames (the next
             * left is a flick: a dash, not a walk) and a jump every 40 frames: with player 2 human (bench LISTX=",p2=human") the walking
             * fighter could stop at the ledge for good (status walk, teetering) and the match never ended (b13h2 / b13h3 / b13hg1) */
            static int wj = -1; if (wj < 0) { const char *e = getenv("SSBU_RB_NET_BOT_WALKJUMP"); wj = e && e[0] == '1'; }
            long long t = f - g_bot_walkoff_at;
            if (wj && g_bp_remote) goto normal;            /* (WALKJUMP) only the local bot walks off: the other player fights on */
            if (wj && t % 300 < 2) return p;              /* (WALKJUMP) the side changes every 300 frames (a wall on one side: PS2 b14h2) */
            p.lx = wj && (t / 300) % 2 ? 32767 : -32767;
            if (wj && t % 40 >= 20 && t % 40 < 22) p.buttons = 1ull << 2;
            return p; } }
normal:
    {   static int rich = -1; if (rich < 0) { const char *e = getenv("SSBU_RB_NET_BOT_RICH"); rich = e && e[0] == '1'; }
        if (rich) {                                                   /* (Arena, test) a fighter that fights: short segments, every kind of move */
            long long st = 1; uint32_t hh = 0; int sg = 0;
            for (;; sg++) { hh = mix32(seed * 0x9E3779B9u ^ (uint32_t)sg * 0x85EBCA6Bu ^ 0x51ED270Bu); long long len = 3 + (hh % 14); if (f < st + len) break; st += len; }
            int a = (int)((hh >> 8) % 20), d = ((hh >> 16) & 1) ? 1 : -1, t = (int)(f - st);
            const int16_t M = 32767;
            switch (a) {
            case 0: break;                                                                     /* nothing */
            case 1: case 2: p.lx = (int16_t)(-d * M); break;                                   /* run (both ways: the bots meet) */
            case 3: p.buttons = 1ull << 0; break;                                              /* jab */
            case 4: p.lx = (int16_t)(d * M); if (t < 2) p.buttons = 1ull << 0; break;          /* forward smash (flick + A) */
            case 5: p.ly = M; if (t < 2) p.buttons = 1ull << 0; break;                         /* up smash */
            case 6: p.ly = -M; p.buttons = 1ull << 0; break;                                   /* down tilt */
            case 7: p.buttons = 1ull << 1; break;                                              /* neutral special */
            case 8: p.lx = (int16_t)(d * M); p.buttons = 1ull << 1; break;                     /* side special */
            case 9: p.ly = M; p.buttons = 1ull << 1; break;                                    /* up special (recovery) */
            case 10: p.ly = -M; p.buttons = 1ull << 1; break;                                  /* down special */
            case 11: if (t < 2) p.buttons = 1ull << 2; else if (t > 4) p.buttons = 1ull << 0; break;   /* jump, then an aerial */
            case 12: if (t < 2) p.buttons = 1ull << 2; else if (t > 4) { p.ly = -M; p.buttons = 1ull << 0; } break;  /* jump, down air */
            case 13: p.buttons = 1ull << 8; break;                                             /* shield (ZL) */
            case 14: p.buttons = 1ull << 8; p.lx = (int16_t)(d * M); break;                    /* roll */
            case 15: p.buttons = 1ull << 8; p.ly = -M; break;                                  /* spot dodge */
            case 16: p.buttons = 1ull << 6; break;                                             /* grab (L) */
            case 17: if (t < 3) p.buttons = 1ull << 6; else p.lx = (int16_t)(d * M); break;    /* grab, then throw */
            case 18: p.buttons = (t & 3) < 2 ? 1ull << 0 : 0; p.lx = (int16_t)(-d * M / 2); break;     /* walk and mash A */
            case 19: if ((t & 7) < 2) p.buttons = 1ull << 2; p.lx = (int16_t)(d * M); break;   /* jump around */
            }
            return p;
        } }
    long long start = 1; uint32_t h = 0; int seg = 0;
    for (;; seg++) { h = mix32(seed * 0x9E3779B9u ^ (uint32_t)seg * 0x85EBCA6Bu); long long len = 6 + (h % 24); if (f < start + len) break; start += len; }
    int act = (int)((h >> 8) % 10);
    int dir = (seg & 1) ? 1 : -1;
    switch (act) {
    case 0: case 1: break;                                            /* nothing */
    case 2: case 3: p.lx = (int16_t)(dir * 32767); break;            /* walk / run */
    case 4: p.buttons = 1ull << 0; break;                            /* A (jab) */
    case 5: p.buttons = 1ull << 1; break;                            /* B (neutral special) */
    case 6: if (((f - start) & 15) < 3) p.buttons = 1ull << 2; break;        /* X (jump), tapped */
    case 7: p.lx = (int16_t)(dir * 32767); p.buttons = 1ull << 0; break;     /* forward A */
    case 8: p.ly = -32767; break;                                     /* crouch */
    case 9: p.buttons = (((f - start) & 7) < 4) ? 1ull << 0 : 0; break;       /* A mashed */
    }
    return p;
}
int rbn_bot_on(void) { return g_bot; }

/* ---------------------------------------------------------------- the match handshake (before the match; rb_hs.h)
 * Over the same channel as the pads (packet type 16), message types 0x48.. (the pad messages are 1..4):
 *   CHOICE  joiner -> host, every 100 ms from its match set-up: its own player-1 choice (fighter, colour, controls)
 *   PROPOSE host -> joiner, every 100 ms once the host is at its set-up and has the joiner's CHOICE: the whole match
 *           (player 1 = the host's choice, player 2 = the joiner's, the host's stage / items / stocks, a new seed)
 *   ACCEPT  joiner -> host (answered on the receive thread to every PROPOSE: a lost ACCEPT is made good)
 *   REFUSE  either way: protocol / game build differ, or the other game stopped waiting (the reason is printed on both sides)
 *   BLOCK   both, at the match start: the game's match block (rules + the two player records) in chunks; each game
 *           compares it with its own and prints the differing ranges (a check, nothing is changed)
 * Header of every message: type, protocol, game build, exe link stamp (a different stamp is only reported), nonce, echo.
 * (hs12) Every wait has its own nonce (a fresh random number, never 0): CHOICE carries the joiner's, PROPOSE the host's and
 * echoes the joiner's, ACCEPT echoes the host's; the host takes only CHOICEs that arrive while it waits. A message of an
 * older wait is ignored, so a wait given up on one side and the next match set-up never mix (two matches in one session).
 * While waiting: a message box over the last picture (menu_notice + nvn_front_osk_redraw: the game itself is held at its
 * set-up), Esc (game window in front) or B held on a controller stops waiting, the wait limit is SSBU_RB_NET_HS_WAIT
 * (seconds, default 120). No agreement = this match is played locally (Final Destination, one stock) and left at once
 * (rb_rollback.c: player 1 walks off the stage), back to the menus; the other game is told (REFUSE) so that it stops waiting. */
#include "rb_hs.h"
#define RBH_PROTO 6                                     /* 6 (rb-8ms 13/11, release requirement): Smashville at a fixed hour + light villagers, never with 5 and older; 3 (hs12): RbhMatch carries the stage set-up context; 4 (hs14): PADS end with a mode byte, the stage is drawn from both picks; 5 (R3 lwr): Local Wireless room matches, LAYOUT */
enum { RBH_CHOICE = 0x48, RBH_PROPOSE = 0x49, RBH_ACCEPT = 0x4a, RBH_REFUSE = 0x4b, RBH_BLOCK = 0x4c, RBH_LAYOUT = 0x4d };      /* (R3 lwr) LAYOUT: a room match, each game's own player's control layout */
#pragma pack(push, 1)
typedef struct { uint8_t type, proto; int16_t game_build; uint32_t exe_stamp; uint32_t nonce, echo; RbhMatch m; } RbhMsg;
typedef struct { uint8_t type, proto; int16_t game_build; uint32_t exe_stamp; uint32_t nonce; uint16_t off, len, total; uint8_t data[768]; } RbhBlock;
#pragma pack(pop)
extern int g_game_build;                                /* hostrt.c */
uint64_t ga(uint64_t addr_13_0_4);                      /* hostrt.c (GA) */
void host_fixed_random_reseed(uint64_t seed);           /* os_threads.c: nn::os::GenerateRandomBytes from the agreed seed on */
void menu_notice(const char *text);                     /* menu.cpp (menu_stub.c: nothing) */
void nvn_front_osk_redraw(void); long long nvn_front_presents(void);      /* nvn_gl.c: the last frame presented again (with the notice) */
uint64_t nnhid_menu_buttons(void);                      /* nnhid.c: every controller's buttons, Npad bit order (B = bit 1), not seen by the game */
static SRWLOCK g_hs_lock = SRWLOCK_INIT;
static volatile LONG g_hs_round;                        /* this game's match set-ups so far (log lines) */
static volatile LONG g_hs_waiting;                      /* rbn_handshake is waiting */
static uint32_t g_hs_nonce;                             /* this game's nonce of the newest wait (0 = none yet) */
static uint32_t g_hs_peer_nonce;                        /* the other game's nonce seen in this wait (0 = none yet) */
static RbhMsg g_hs_peer;  static int g_hs_have_peer;    /* host: the joiner's newest CHOICE of this wait */
static RbhMsg g_hs_prop;  static int g_hs_have_prop;    /* host: the proposal sent; joiner: the proposal accepted (this wait) */
static volatile LONG g_hs_accepted, g_hs_refused; static char g_hs_why[160];
static uint64_t g_hs_seed; static uint32_t g_match_id;  /* the agreed match: its seed, and its id (tag of the pad and block messages) */
uint32_t rbn_match_id(void) { return g_match_id; }      /* (Arena) the agreed match's id (seed ^ seed >> 32 | 1, the lobby's replay recorder uses the same) */
static volatile LONG g_hs_cancelled;                    /* this match: 1 = no agreement: played locally and left at once; 2 = (pub2) set up
                                                           * inside a Local Wireless room: the game's own netcode plays it, no rollback, nothing changed */
static int g_match_n;                                   /* (hs12) online matches of this session so far (1 = the first; rbn_start) */
/* (hs12) the tag byte at the end of every PADS message: the agreed match id and the match number of the session. Run tw7:
 * the other game's last pads of match 1 arrived after this game's match-1 reset and were taken at the start of match 2
 * (their acknowledgement 1081 made each game send no pads any more: both waited for ever at frame 7). */
/* (0.2.23) From the agreed match id alone when there is one: the match number is each game's own count, so after one game was restarted
 * (a crash, a new start) the two differed, each dropped all the other's pads and both waited 120 s at the start barrier: the "freeze at
 * the start of a match". */
static uint8_t pads_tag(void) {
    uint32_t id = g_match_id;
    return id ? (uint8_t)(id ^ (id >> 8) ^ (id >> 16) ^ (id >> 24)) : (uint8_t)((uint32_t)g_match_n * 0x3bu);
}
static long long g_hs_tx, g_hs_rx;
#define RBH_BLK 0xa48                                   /* match block + 0 .. + 0x580 + 2 * 0x260 + 8 */
static uint8_t g_hs_mine_blk[RBH_BLK], g_hs_peer_blk[RBH_BLK]; static int g_hs_mine_ok, g_hs_peer_got, g_hs_blk_done; static uint8_t g_hs_peer_have[RBH_BLK / 768 + 1];
static uint32_t exe_stamp(void) {
#ifdef _WIN32
    const uint8_t *b = (const uint8_t *)GetModuleHandleA(NULL); if (!b) return 0;
    const IMAGE_NT_HEADERS *nt = (const IMAGE_NT_HEADERS *)(b + ((const IMAGE_DOS_HEADER *)b)->e_lfanew);
    return nt->FileHeader.TimeDateStamp;
#else                                                   /* POSIX builds: ELF / Mach-O have no link time: the exe's modification time */
    char p[MAX_PATH]; struct stat st;
    DWORD n = GetModuleFileNameA(NULL, p, MAX_PATH);
    return n && n < MAX_PATH && !stat(p, &st) ? (uint32_t)st.st_mtime : 0;
#endif
}
int rbn_handshake_wanted(void) {
    static int w = -1;
    if (w < 0) { const char *e = getenv("SSBU_RB_NET"), *h = getenv("SSBU_RB_NET_HANDSHAKE");
        int game_rb_blocked(void);
        w = e && e[0] && e[0] != '0' && _strnicmp(e, "loop", 4) && !(h && h[0] == '0') && !game_rb_blocked(); }      /* (issue #6: no rollback on 13.0.5, no handshake) */
    return w;
}
uint64_t rbn_match_seed(void) {
    if (g_loop && !g_hs_seed) { const char *e = getenv("SSBU_RB_NET_LOOP_SEED"); if (e && *e) return strtoull(e, NULL, 0); }   /* (hs15, test) the loop game's generators from a seed */
    return g_hs_seed;
}
static volatile LONG g_hs_room;                         /* (pub2) the last set-up was inside a Local Wireless room (until the next set-up: the fight-off of the
                                                           * room's waiting-stage fight comes AFTER the room match's set-up, run rm4) */
static volatile LONG g_hs_local;                        /* (0.2.11) the last set-up was a LOCAL fight (no room code, not a quick match): not a room */
int rbn_local_fight(void) { return (int)g_hs_local; }
/* (0.2.34) the opponent's name for the replay recorder (replay_state.c): the other player of this Local Wireless room, else
 * the lobby's match; "" = not known (a local fight, more than two players) */
const char *rbn_peer_name(void) {
    static char n[33];
    int nnldn_other_name(char *out, int max);
    n[0] = 0;
    if (nnldn_other_name(n, sizeof n) <= 0) {
        NetMatch mm; net_match_get(&mm);
        if ((mm.state == MM_MATCHED || mm.state == MM_TOGETHER) && mm.peer_name[0]) snprintf(n, sizeof n, "%.32s", mm.peer_name);
    }
    return n;
}
int rbn_match_cancelled(void) { return g_hs_room ? 2 : (int)g_hs_cancelled; }      /* 0, 1 (not agreed: walk off) or 2 (pub2: a room fight, the game's own netcode) */
/* (R3 lwr) debug_mode.c tells which set-up this is: 1 = a Local Wireless room's waiting-stage fight (the record builder called from
 * the waiting-stage set-up, one player), 0 = any other (the select screens' "ready to fight", also the room's match) */
static int g_setup_waiting, g_hs_lwr;
void rbn_set_setup_waiting(int waiting) { g_setup_waiting = waiting; }
int rbn_room_rollback(void) { return g_hs_lwr; }       /* (R3 lwr) the last agreed match is a Local Wireless room's match played with rollback */
/* (online-mm A3, SSBU_ONLINE_RB=1) debug_mode.c tells, at player 1's record, that this set-up is the game's own Online one: 1 = its
 * waiting-stage fight (melee mode 13, each game alone), 2 = the Online match (melee mode 14), with this game's player (0 = player 1:
 * its own panel is player 1's, the gathering's owner; the same order on both games). The match is then agreed as a Local Wireless
 * room's (the game set it up on both from the game server's match: only the seed and the input delay are new), with the other
 * station of the gathering; g_hs_onl = the last agreed match is such one (rbn_online_rb). */
static int g_onl_setup, g_onl_local, g_hs_onl;
void rbn_set_online_setup(int kind, int local) { g_onl_setup = kind; g_onl_local = local; }
int rbn_online_rb(void) { return g_hs_onl; }
static uint64_t g_lay_peer_chara; static uint8_t g_lay_peer_colour; static uint64_t g_onl_own_chara; static uint8_t g_onl_own_colour; static uint32_t g_onl_own_hdr, g_lay_peer_hdr;
void rbn_online_own(uint64_t chara, uint8_t colour, uint32_t hdr) { g_onl_own_chara = chara; g_onl_own_colour = colour; g_onl_own_hdr = hdr; }      /* (A3) sent with the LAYOUT (hdr: match block + 0x500, run r1) */
void rbn_online_peer(uint64_t *chara, uint8_t *colour, uint32_t *hdr) { *chara = g_lay_peer_chara; *colour = g_lay_peer_colour; *hdr = g_lay_peer_hdr; }      /* (A3) the other game's own pick */
/* (rb-8ms 2026-11-13) rb_rollback.c SSBU_RB_LIGHTVILL: an agreed rollback match (online / room) from its agreement to its end
 * (rbn_match_reset), or any fight of the loop test */
static volatile LONG g_clk_match;
int rbn_rb_agreed(void) { return rbn_on() > 0 && (g_loop || g_clk_match); }
/* (Arena) a rollback match was agreed and has not started yet (its base state not made): the fight's entry and countdown run
 * on both games before it; a button held there (a real player's, or a test's input sequence) changed one game's fighter
 * and not the other's (a different base state: in sync again only by chance, and not at all with 4 frames of delay) */
static volatile int g_hs_pending; static ULONGLONG g_hs_pending_t;
/* (an agreement can come while the room's waiting-stage fight still runs: that fight's end must not clear it; it ends
 * when the agreed match starts, or after 60 s if it never does, so no later fight loses its pads; 60 s and not 30: a slow
 * PC's loading of the match must not give the pads back before the base state) */
int rbn_fight_pending(void) { return g_hs_pending && GetTickCount64() - g_hs_pending_t < 60000; }
static int rbn_peer_heard_ms(void);                     /* (R3 lwr) ms since the other game's rollback session was last heard, -1 = never */
static volatile LONG g_lay_have_mine, g_lay_have_peer; static uint8_t g_lay_peer_has; static uint16_t g_lay_peer[11]; static uint8_t g_lay_peer_ctl[0x2d];
static RbhMsg g_lay_mine;
/* (Battle Arena) Squad Strike in a Local Wireless room (debug_mode.c squad_build): this game's Battle Arena mode (0 off; 1 the Arena menu's
 * Battle Arena tile; 2 this game joined a room whose maker is in Battle Arena, nnldn.c) and its own fighters 2 and 3 (fighter 1 is the
 * one chosen on the room's character select). The room maker decides: its proposal (lwr) carries RbhMatch.pad0 bit 0 = a squad match;
 * the joiner's CHOICE carries its own mode the same way (logged). Both picks go with the LAYOUT message of the room match, in
 * RbhMatch.ctx_stage (not used by a LAYOUT otherwise): 'S' 'Q' 2, then 2 ui_chara ids and 2 colours. Without the mode nothing of
 * this runs: pad0 stays 0 and a room match is the 1-on-1 of before. SSBU_BATTLE_ARENA=1 (test): the mode on from the start;
 * SSBU_BATTLE_ARENA_SQUAD=<fighter>,<fighter> (test): the picks. */
static volatile LONG g_ba_mode = -1; static uint64_t g_ba_pick[2]; static uint8_t g_ba_pick_col[2]; static volatile LONG g_ba_picked;
static int g_sq_agreed; static uint8_t g_ba_sent[0xb0]; static uint8_t g_lay_peer_sq[0xb0]; static volatile LONG g_lay_peer_sq_ok;
static uint64_t hs_rand(void);
static uint64_t ba_h40(const char *s) { uint32_t crc = 0xffffffffu; size_t n = strlen(s); for (size_t i = 0; i < n; i++) { crc ^= (uint8_t)s[i]; for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320u & (0u - (crc & 1))); } return ((uint64_t)n << 32) | (uint32_t)~crc; }
static void ba_init(void) {
    if (g_ba_mode >= 0) return;
    const char *e = getenv("SSBU_BATTLE_ARENA"), *q = getenv("SSBU_BATTLE_ARENA_SQUAD");
    g_ba_mode = e && e[0] == '1' ? 1 : 0;
    if (q && *q) { char b[96]; snprintf(b, sizeof b, "%s", q); int k = 0;
        for (char *t = strtok(b, ","); t && k < 2; t = strtok(NULL, ","), k++) { char nm[64]; snprintf(nm, sizeof nm, "ui_chara_%s", t); g_ba_pick[k] = 0xc100000000000000ull | ba_h40(nm); g_ba_pick_col[k] = 0; }
        if (k == 2) g_ba_picked = 1; }
    if (g_ba_mode) { printf("[squad] TEST: Battle Arena mode on from the start (SSBU_BATTLE_ARENA=1)%s%s\n", g_ba_picked ? "; squad picks " : "", g_ba_picked ? q : ""); fflush(stdout); }
}
int rbn_ba_mode(void) { ba_init(); return (int)g_ba_mode; }
void rbn_ba_set_mode(int m) {
    ba_init();
    if (InterlockedExchange(&g_ba_mode, m) != m) { printf("[squad] Battle Arena mode %s\n", m == 1 ? "on (Battle Arena tile)" : m == 2 ? "on (joined a Battle Arena room)" : "off"); fflush(stdout); }
}
void rbn_ba_set_pick(int slot, uint64_t chara, uint8_t colour) { ba_init(); if (slot < 0 || slot > 1) return; g_ba_pick[slot] = chara; g_ba_pick_col[slot] = colour; g_ba_picked = 1; }
int rbn_ba_pick(int slot, uint64_t *chara, uint8_t *colour) { ba_init(); if (slot < 0 || slot > 1 || !g_ba_picked) return 0; *chara = g_ba_pick[slot]; *colour = g_ba_pick_col[slot]; return 1; }
void rbn_ba_fill_sent(uint8_t out[0xb0]) { memcpy(out, g_ba_sent, 0xb0); }      /* what this game sent as its picks (rbn_room_layout) */
int rbn_squad_agreed(void) { return g_sq_agreed; }      /* the last agreed match is a Battle Arena squad match (room maker's proposal) */
/* the other player's fighters 2 and 3, from its LAYOUT of this match: 1 = got them */
int rbn_room_squad(uint64_t chara[2], uint8_t colour[2]) {
    if (!g_lay_peer_sq_ok || g_lay_peer_sq[0] != 'S' || g_lay_peer_sq[1] != 'Q' || g_lay_peer_sq[2] != 2) return 0;
    memcpy(chara, g_lay_peer_sq + 3, 16); colour[0] = g_lay_peer_sq[19]; colour[1] = g_lay_peer_sq[20];
    return 1;
}
static void ba_fill(uint8_t out[0xb0]) {      /* this game's picks (random ones when none was chosen: they are sent, so both games know them) */
    static const char *const k_any[] = {"mario", "donkey", "link", "samus", "yoshi", "kirby", "fox", "pikachu", "luigi", "ness", "captain", "peach", "koopa", "sheik", "zelda", "marth", "ganon", "pit", "diddy", "sonic", "lucario", "wolf", "palutena", "cloud"};
    memset(out, 0, 0xb0);
    out[0] = 'S'; out[1] = 'Q'; out[2] = 2;
    uint64_t c[2]; uint8_t col[2] = {0, 0};
    int have = rbn_ba_pick(0, &c[0], &col[0]) && rbn_ba_pick(1, &c[1], &col[1]);
    uint64_t r = hs_rand();
    for (int k = 0; k < 2; k++) if (!have || !c[k]) {          /* none chosen, or Random */
        char nm[64]; snprintf(nm, sizeof nm, "ui_chara_%s", k_any[(r >> (16 * k)) % (sizeof k_any / sizeof k_any[0])]); c[k] = 0xc100000000000000ull | ba_h40(nm); col[k] = 0;
        printf("[squad] fighter %d of this game drawn at random: %s\n", k + 2, nm + 9);
    }
    memcpy(out + 3, c, 16); out[19] = col[0]; out[20] = col[1];
}
const char *rbn_hs_why(void) { return g_hs_why; }      /* (R3 pub1) why the last handshake failed (rb_rollback.c's notice) */
static void rtt_reset(void);                            /* (R3 net3) a new opponent: its round trip is measured afresh */
static void hs_head(RbhMsg *m, int type) { memset(m, 0, sizeof *m); m->type = (uint8_t)type; m->proto = RBH_PROTO; m->game_build = (int16_t)g_game_build; m->exe_stamp = exe_stamp(); m->nonce = g_hs_nonce; }
/* (online-mm 15/11) SSBU_RB_NET_CODE_CHECK (default 1): every CHOICE / PROPOSE / ACCEPT / REFUSE / LAYOUT ends with "GC" + length u8
 * + this game's code (game_code.c: exe + game DLL); a game whose code differs is refused ("different game files"), the safety net
 * behind the lobby's own pairing (match_game_code). A message without it (a build of before: proto 6 takes longer messages and
 * ignores the end) is only noted. =0: nothing sent, nothing checked (as before). */
static int g_code_check = -1;
static int code_check_on(void) { if (g_code_check < 0) { const char *e = getenv("SSBU_RB_NET_CODE_CHECK"); g_code_check = !(e && e[0] == '0'); } return g_code_check; }
static char g_hs_peer_code[48];                         /* the code at the end of the newest handshake message (hs_rx), "" = none */
static void hs_send(const void *p, int n) {
    if (n == (int)sizeof(RbhMsg) && code_check_on()) {
        const char *game_code(void); const char *gc = game_code(); int gl = (int)strlen(gc);
        uint8_t b[sizeof(RbhMsg) + 3 + 47];
        if (gl > 47) gl = 47;
        memcpy(b, p, (size_t)n); b[n] = 'G'; b[n + 1] = 'C'; b[n + 2] = (uint8_t)gl; memcpy(b + n + 3, gc, (size_t)gl);
        if (net_rb_send(b, n + 3 + gl)) g_hs_tx++;
        return;
    }
    if (net_rb_send(p, n)) g_hs_tx++;
}
static int hs_check(const RbhMsg *m, char *why, size_t n) {           /* 0 = the other game cannot play this match with us */
    if (m->proto != RBH_PROTO) { snprintf(why, n, "handshake protocol %d here, %d there", RBH_PROTO, m->proto); return 0; }
    if (code_check_on()) {
        const char *game_code(void); const char *gc = game_code();
        if (g_hs_peer_code[0] && strcmp(g_hs_peer_code, gc)) {
            snprintf(why, n, "different game files (game codes %.24s, %.24s): update both games in the launcher", gc, g_hs_peer_code); return 0; }
        if (!g_hs_peer_code[0]) { static int said; if (!said) { said = 1; printf("[rbnet] handshake: NOTE the other game sends no game code (an older build): not compared\n"); fflush(stdout); } }
    }
    int game_xver_with(int other); const char *game_build_label(int b);
    if (m->game_build != (int16_t)g_game_build && !game_xver_with(m->game_build)) {      /* (cross-version: SSBU_XVER, hostrt.c) */
        snprintf(why, n, "game update %s here, %s there (build %d / %d): both players need the same game version", game_build_label(g_game_build), game_build_label(m->game_build), g_game_build, m->game_build); return 0; }
    if (m->game_build != (int16_t)g_game_build) { static int said; if (!said) { said = 1; printf("[rbnet] handshake: cross-version match: game update %s here, %s there (SSBU_XVER)\n", game_build_label(g_game_build), game_build_label(m->game_build)); fflush(stdout); } }
    return 1;
}
static void hs_refuse(uint32_t echo, const char *why) {
    RbhMsg r; hs_head(&r, RBH_REFUSE); r.echo = echo; snprintf((char *)&r.m, sizeof r.m, "%s", why); hs_send(&r, sizeof r);
}
/* (R3 lwr) a room match: each game knows only its OWN player's 11-word text field of the panel state (+ 0x54, record + 0x28:
 * the name-tag text in runs lwr17..20; hs12 took it for the control layout) (the other player is a network player in the room's
 * records). At player 2's record (both games are at the same set-up) each sends its own and waits for the other's
 * (at most SSBU_RB_NET_ROOM_WAIT s / 2; sent again every 50 ms, a received one is answered once). 1 = *other is the other
 * player's layout (*other_has: it had one); 0 = not received (each game keeps what it has: the match may differ, logged). */
/* (owner 2026-10-22) the message also carries the player's own name-tag controls: 0x2b bytes (profile + 0x24 .. + 0x4e) and the
 * option byte of its controller kind (debug_mode.c lwr_own_controls), in RbhMatch.ctx_hist (not used by a LAYOUT otherwise). */
int rbn_room_layout(const uint16_t mine[11], int has, const uint8_t mine_ctl[0x2d], uint16_t other[11], int *other_has, uint8_t other_ctl[0x2d]) {
    AcquireSRWLockExclusive(&g_hs_lock);
    hs_head(&g_lay_mine, RBH_LAYOUT); g_lay_mine.nonce = g_match_id; memcpy(g_lay_mine.m.layout[0], mine, sizeof g_lay_mine.m.layout[0]); g_lay_mine.m.has_layout[0] = (uint8_t)(has != 0);
    if (g_hs_onl) { g_lay_mine.m.chara[0] = g_onl_own_chara; g_lay_mine.m.colour[0] = g_onl_own_colour; g_lay_mine.m.items = g_onl_own_hdr; }      /* (A3) this game's own player's pick (each game shows its own player in its own colour) */
    memcpy(g_lay_mine.m.ctx_hist, mine_ctl, 0x2d);      /* (+ 0x2c: 1 = a GameCube controller) */
    if (g_sq_agreed) { ba_fill(g_lay_mine.m.ctx_stage); memcpy(g_ba_sent, g_lay_mine.m.ctx_stage, sizeof g_ba_sent); }      /* (Battle Arena) this game's fighters 2 and 3 */
    g_lay_have_mine = 1;
    RbhMsg ans = g_lay_mine; ans.echo = 1;
    ReleaseSRWLockExclusive(&g_hs_lock);
    double limit = (getenv("SSBU_RB_NET_ROOM_WAIT") ? atof(getenv("SSBU_RB_NET_ROOM_WAIT")) : 10.0) * 500.0;
    uint64_t t0 = GetTickCount64(), last = 0;
    while (!g_lay_have_peer && GetTickCount64() - t0 < limit) {
        if (GetTickCount64() - last >= 50) { last = GetTickCount64(); hs_send(&g_lay_mine, sizeof g_lay_mine); }
        Sleep(2);
    }
    for (int k = 0; k < 3; k++) hs_send(&ans, sizeof ans);          /* (the other game may still wait: answers that do not ask for one back) */
    int ok = g_lay_have_peer != 0;
    if (ok) { memcpy(other, g_lay_peer, sizeof g_lay_peer); *other_has = g_lay_peer_has; memcpy(other_ctl, g_lay_peer_ctl, sizeof g_lay_peer_ctl); }
    printf("[rbnet] room match: player name and controls (name tag text, profile controls): this game's player's sent, the other player's %s after %llu ms\n", ok ? "received" : "NOT RECEIVED (the match may differ)", (unsigned long long)(GetTickCount64() - t0));
    fflush(stdout);
    return ok;
}
/* the receive thread: a handshake message (1 = it was one) */
static int hs_rx(const uint8_t *p, int n) {
    if (n < 1 || p[0] < RBH_CHOICE || p[0] > RBH_LAYOUT) return 0;
    g_hs_rx++;
    if (p[0] == RBH_BLOCK) {
        if (n < (int)offsetof(RbhBlock, data)) return 1;
        RbhBlock b; memset(&b, 0, sizeof b); memcpy(&b, p, (size_t)n < sizeof b ? (size_t)n : sizeof b);
        if (b.nonce == g_match_id && b.off + b.len <= RBH_BLK && b.len <= sizeof b.data) { AcquireSRWLockExclusive(&g_hs_lock); memcpy(g_hs_peer_blk + b.off, b.data, b.len); g_hs_peer_have[b.off / 768] = 1; ReleaseSRWLockExclusive(&g_hs_lock); }
        return 1;
    }
    if (n < (int)sizeof(RbhMsg)) return 1;
    RbhMsg m; memcpy(&m, p, sizeof m);
    char why[160];
    AcquireSRWLockExclusive(&g_hs_lock);
    {   /* (online-mm) the game code at the end, if any (SSBU_RB_NET_CODE_CHECK) */
        int o = (int)sizeof(RbhMsg); g_hs_peer_code[0] = 0;
        if (n >= o + 3 && p[o] == 'G' && p[o + 1] == 'C' && p[o + 2] < sizeof g_hs_peer_code && n >= o + 3 + p[o + 2]) {
            int l = p[o + 2]; for (int k = 0; k < l; k++) g_hs_peer_code[k] = p[o + 3 + k] >= 0x21 && p[o + 3 + k] < 0x7f ? (char)p[o + 3 + k] : '?';
            g_hs_peer_code[l] = 0;
        }
    }
    int waiting = g_hs_waiting && g_hs_nonce;
    switch (m.type) {
    case RBH_CHOICE:                                    /* (host, while it waits: an older CHOICE is not for this match) */
        if (g_local != 0 || !waiting) break;
        if (!hs_check(&m, why, sizeof why)) { snprintf(g_hs_why, sizeof g_hs_why, "%s", why); g_hs_refused = 1; hs_refuse(m.nonce, why); break; }
        g_hs_peer = m; g_hs_have_peer = 1; g_hs_peer_nonce = m.nonce;
        if (g_hs_have_prop && g_hs_prop.echo == m.nonce) hs_send(&g_hs_prop, sizeof g_hs_prop);   /* our proposal was lost */
        break;
    case RBH_PROPOSE:                                   /* (joiner: for our newest wait; answered after the wait too, a lost ACCEPT is made good) */
        if (g_local != 1 || !g_hs_nonce || m.echo != g_hs_nonce) break;
        if (!hs_check(&m, why, sizeof why)) { snprintf(g_hs_why, sizeof g_hs_why, "%s", why); g_hs_refused = 1; hs_refuse(m.nonce, why); break; }
        if (g_hs_have_prop && g_hs_prop.nonce != m.nonce) break;     /* (one proposal per wait) */
        if (!g_hs_have_prop && !waiting) break;
        g_hs_prop = m; g_hs_have_prop = 1; g_hs_peer_nonce = m.nonce;
        { RbhMsg a; hs_head(&a, RBH_ACCEPT); a.echo = m.nonce; a.m = m.m; hs_send(&a, sizeof a); }
        break;
    case RBH_ACCEPT:                                    /* (host) */
        if (g_local == 0 && waiting && g_hs_have_prop && m.echo == g_hs_prop.nonce && !memcmp(&m.m, &g_hs_prop.m, sizeof m.m)) g_hs_accepted = 1;
        break;
    case RBH_LAYOUT:                                    /* (R3 lwr) the other game's own player's control layout for this match; answered once with ours */
        if (m.nonce != g_match_id || !g_match_id || m.proto != RBH_PROTO) break;
        g_lay_peer_chara = m.m.chara[0]; g_lay_peer_colour = m.m.colour[0]; g_lay_peer_hdr = m.m.items;      /* (A3) */
        memcpy(g_lay_peer, m.m.layout[0], sizeof g_lay_peer); g_lay_peer_has = m.m.has_layout[0]; memcpy(g_lay_peer_ctl, m.m.ctx_hist, sizeof g_lay_peer_ctl); memcpy(g_lay_peer_sq, m.m.ctx_stage, sizeof g_lay_peer_sq); g_lay_peer_sq_ok = 1; g_lay_have_peer = 1;
        if (g_lay_have_mine && !m.echo) hs_send(&g_lay_mine, sizeof g_lay_mine);
        break;
    case RBH_REFUSE:                                    /* about our wait, or from the other game's wait we deal with */
        if (!waiting || !((m.echo && m.echo == g_hs_nonce) || (g_hs_peer_nonce && m.nonce == g_hs_peer_nonce))) break;
        snprintf(g_hs_why, sizeof g_hs_why, "the other game refused: %.120s", (const char *)&m.m); g_hs_refused = 2;
        break;
    }
    ReleaseSRWLockExclusive(&g_hs_lock);
    return 1;
}
static uint64_t hs_rand(void) {
    LARGE_INTEGER t; QueryPerformanceCounter(&t);
    uint64_t s = (uint64_t)t.QuadPart ^ ((uint64_t)GetCurrentProcessId() << 32) ^ GetTickCount64() * 0x9E3779B97F4A7C15ull ^ (uint64_t)(uintptr_t)&t;
    s ^= s >> 29; s *= 0xBF58476D1CE4E5B9ull; s ^= s >> 32; s *= 0x94D049BB133111EBull; s ^= s >> 31;
    return s;
}
static uint64_t hs_new_seed(void) {
    const char *e = getenv("SSBU_RB_NET_SEED");
    uint64_t s = e && *e ? strtoull(e, NULL, 0) : hs_rand();
    return s ? s : 1;
}
static void hs_print(const char *what, const RbhMatch *m) {
    printf("[rbnet] handshake: %s: player 1 ui_chara %016llx colour %d controls %s, player 2 ui_chara %016llx colour %d controls %s; stage entry %016llx form %u flags %x second %016llx; hazards %d, items %u, rule kind %d, stocks %d; seed %016llx\n", what,
           (unsigned long long)m->chara[0], m->colour[0], m->has_layout[0] ? "own layout" : "none", (unsigned long long)m->chara[1], m->colour[1], m->has_layout[1] ? "own layout" : "none",
           (unsigned long long)*(const uint64_t *)m->stage, *(const uint32_t *)(m->stage + 8), *(const uint32_t *)(m->stage + 12), (unsigned long long)*(const uint64_t *)(m->stage + 16),
           m->hazards, m->items, m->rule_kind, m->stock, (unsigned long long)m->seed);
    fflush(stdout);
}
/* Esc (this game's window in front) or B on any controller, held 0.3 s; SSBU_RB_NET_TEST_CANCEL=<s>: as if pressed after s seconds (test) */
static int hs_stop_pressed(double waited_ms) {
    static ULONGLONG since; static double test = -2;
    if (test == -2) { const char *e = getenv("SSBU_RB_NET_TEST_CANCEL"); test = e && *e ? atof(e) * 1000.0 : -1; }
    if (test >= 0 && waited_ms >= test) return 1;
    int down = 0;
    HWND fg = GetForegroundWindow(); DWORD pid = 0;
    if (fg) GetWindowThreadProcessId(fg, &pid);
    if (pid == GetCurrentProcessId() && (GetAsyncKeyState(VK_ESCAPE) & 0x8000)) down = 1;
    if (waited_ms > 500 && (nnhid_menu_buttons() & 2)) down = 1;
    ULONGLONG now = GetTickCount64();
    if (!down) { since = 0; return 0; }
    if (!since) since = now;
    return now - since >= 300;
}
/* (hs14) the fair rule for what the two players chose differently (host, when it builds the proposal):
 *  - rules (stock / time kind, stocks, items, hazards) and the stage set-up's inputs: the host's (the joiner sees them in the
 *    proposal and its game takes them; a different rule KIND on the joiner's screens is only reported, see debug_mode.c);
 *  - the stage: one of the two players' own picks, drawn with the agreed seed (bit 40 of it: 0 = the host's pick, 1 = the
 *    joiner's), so over many matches each player plays on his own pick half the time and neither game can steer it (the seed
 *    is new for every match). A joiner pick that is not a stage entry (no ui_stage id) leaves the host's.
 *    SSBU_RB_NET_STAGE=host / joiner forces one side (tests). */
static void hs_stage_rule(RbhMatch *m, const RbhMatch *peer) {
    const char *e = getenv("SSBU_RB_NET_STAGE");
    uint64_t hs = *(const uint64_t *)m->stage, js = *(const uint64_t *)peer->stage; uint32_t hf = *(const uint32_t *)(m->stage + 8);
    int valid = (js >> 56) == 0x69, draw = (int)((m->seed >> 40) & 1);
    int joiner = valid && (e && !_stricmp(e, "joiner") ? 1 : e && !_stricmp(e, "host") ? 0 : draw);
    if (joiner) memcpy(m->stage, peer->stage, sizeof m->stage);
    printf("[rbnet] handshake: stage: the host picked %016llx form %u, the joiner %016llx form %u; drawn with the agreed seed (%s): the %s's pick%s\n",
           (unsigned long long)hs, hf,
           (unsigned long long)js, *(const uint32_t *)(peer->stage + 8), e && *e ? e : "random", joiner ? "joiner" : "host", valid ? "" : " (the joiner's is not a stage entry)");
    fflush(stdout);
}
/* (rb-8ms 2026-11-13, OpenSmash A1: b57792a / bddea41 / cdcd591 rbn_online_delay_for_rtt) SSBU_RB_NET_DELAY=auto (launcher
 * "Auto"): the input delay from the least round trip measured by the pings: 1 under 15 ms (a home network), else the one-way trip
 * in frames rounded, less one below 100 ms one way, at least 2, at most SSBU_RB_NET_DELAY_MAX (default 5, 1..9). Both games use
 * ONE delay: RbhMatch.pad0 bits 4..7 = a delay, bit 3 = "auto". The joiner's CHOICE carries its own (its auto value, or its fixed
 * choice); the host's PROPOSE carries the agreed one when either side is auto: the larger of the two (a fixed player next to an
 * auto one is raised to the connection's value, never lowered); both fixed: nothing agreed, each keeps its own as before. A game
 * without this (0.2.49) sends and reads 0 there: it keeps its own delay and the other game its own (in sync either way: each
 * game's pads go out for every frame, the empty first ones too). -1 = no round trip measured yet. */
/* (0.2.65, SSBU_RB_AUTO_DELAY_PING, default 1; 0 = the rule above, 0.2.50..0.2.64) Auto = the delay that covers the one-way trip of
 * the ping (auto_ping_frames below: 150 ms -> 5, 180 ms -> 6). 0.2.64 gave 4 at 150 ms, and next to a fixed player it could
 * take the fixed value: the joiner sent its CHOICE before its first round trip was measured (an Auto CHOICE without a round
 * trip says 2), so a host fixed at 3 agreed 3. Now: (1) an Auto joiner holds its CHOICE until it has measured the ping (3
 * round trips, at most 3 s); (2) the host, when either side is Auto, waits for its own ping too and never agrees less than
 * the ping-based value it measured itself on the same connection (also with an older joiner that sent 2). Both games still
 * play ONE delay, the larger of the two wishes (a fixed player next to an Auto one is raised, never lowered; two fixed: the
 * larger, as since 0.2.52). No change to the handshake format (pad0 bits 3..7 as before; RBH_PROTO stays 6). */
static int auto_rule_delay(void), auto_rule_delay_ex(int final), auto_ping_on(void), auto_v2_on(void);
static int auto_rtt_shown_ms(int final, int *samples, double *best_ms);
static void hs_delay_want(RbhMatch *m) {                 /* this game's own wish into pad0 (CHOICE and the host's proposal) */
    int w = g_delay_auto ? auto_rule_delay_ex(1) : g_delay_cfg;
    if (g_delay_auto && w < 1) w = 2;
    m->pad0 = (uint8_t)((m->pad0 & 0x07) | (g_delay_auto ? 0x08 : 0) | ((w > 0 ? (w > 15 ? 15 : w) : 0) << 4));
}
static void hs_delay_agree(RbhMatch *m, const RbhMatch *peer) {      /* host: m = the proposal (pad0 has the host's wish) */
    int mw = m->pad0 >> 4, ma = (m->pad0 & 0x08) != 0, pw = peer->pad0 >> 4, pa = (peer->pad0 & 0x08) != 0;
    int d = mw > pw ? mw : pw;
    if (auto_ping_on() && (ma || pa)) {                 /* (0.2.65) never under the ping-based value of this connection */
        int n = 0; double best = -1; int rtt = auto_rtt_shown_ms(1, &n, &best), pd = auto_rule_delay_ex(1);
        printf("[rbnet] handshake: input delay Auto (%s): round trip %d ms (median of the last %d, best %.1f ms) -> %d frames from the ping%s\n",
               ma && pa ? "both games" : ma ? "this game" : "the other game", rtt, n < 5 ? n : 5, best, pd, pd > d ? ": raised to it" : "");
        if (pd > d) d = pd;
    }
    m->pad0 &= 0x07;
    /* (0.2.52, SSBU_RB_NET_DELAY_AGREE_FIXED, default 1) both fixed: both games use the larger one too, so both show the same "<n>f"
     * (the overlay) and play one delay; a peer that sent none (0: a 0.2.49 game, or no SSBU_RB_NET_DELAY) still keeps its own. A
     * 0.2.50 / 0.2.51 joiner takes the agreed bit of this proposal (rbn_handshake: g_delay_agreed = pad0 bit 3 ? pad0 >> 4); a 0.2.51
     * host never sets it for two fixed delays: each keeps its own, as before. =0: nothing agreed for two fixed delays (0.2.51). */
    static int fixed = -1; if (fixed < 0) { const char *e = getenv("SSBU_RB_NET_DELAY_AGREE_FIXED"); fixed = !(e && e[0] == '0'); }
    if ((ma || pa || (fixed && mw > 0 && pw > 0)) && d > 0) m->pad0 |= (uint8_t)(0x08 | (d << 4));
    printf("[rbnet] handshake: input delay: here %d%s, there %d%s -> %s%d\n", mw, ma ? " (auto)" : "", pw, pa ? " (auto)" : pw ? "" : " (none sent)",
           (m->pad0 & 0x08) ? "both games use " : "not agreed, each game its own; here ", (m->pad0 & 0x08) ? d : mw);
    fflush(stdout);
}
int rbn_handshake(const RbhMatch *mine, RbhMatch *out) {
    if (!rbn_handshake_wanted() || rbn_on() <= 0 || g_loop) return 0;
    LONG round = InterlockedIncrement(&g_hs_round);
    /* (R3 pub2: the Local Wireless "deadlock" of lb1 / lb3 and of the testers 2026-10-20) a fight set up while this game is in a Local
     * Wireless room is either the room's waiting-stage fight (each game alone, one player) or the room's own match, which the game
     * plays with its own online netcode: BOTH games build the records of BOTH players from the room (run nb2: player 1 = the room
     * maker's fighter, player 2 = the joiner's, the same on both PCs). Rollback cannot run under that netcode, and holding one game
     * here stopped its traffic: the other game waited on its loading screen for it, then left the room. So no handshake and no
     * rollback for these fights: they are played exactly as without rollback, whatever the other game's setting; a box says so
     * once. Rollback matches: Smash (versus) with a room code (host / join modes). SSBU_RB_NET_ROOM_MATCH=1: the old way (test). */
    InterlockedExchange(&g_hs_room, 0); InterlockedExchange(&g_hs_local, 0); InterlockedExchange(&g_clk_match, 0); g_delay_agreed = 0;
    /* (R3 lwr, owner 2026-10-21: "il rollback deve essere attivo anche nel local wireless con le stanze") the room's MATCH (not its
     * waiting-stage fight) with rollback on both games: the room has already agreed fighters, stage and rules on both PCs, so the
     * handshake here only checks that and agrees on a seed (no menus to wait for: both games reach this set-up together when the
     * room maker starts the match; at most SSBU_RB_NET_ROOM_WAIT s, default 10). debug_mode.c then makes the fight a local versus
     * match (the room's melee mode "local wireless" -> "local", match block + 0), so the game's own online netcode does not play it,
     * and rb_rollback.c runs it as every rollback match (room maker = player 1, port i = player i). It is tried only when the other
     * player of a 1-on-1 room is heard by this game's rollback session (its pings reach only a game with rollback on); otherwise,
     * and whenever the agreement fails, the fight is left exactly as the game set it up (pub2: the game's own netcode), so a
     * player with rollback off is never held. SSBU_RB_NET_ROOM_ROLLBACK=0: the pub2 way for every room fight. */
    int lwr = 0, onl = 0;
    g_hs_lwr = 0; g_hs_onl = 0;
    if (g_onl_setup) {                                  /* (online-mm A3, SSBU_ONLINE_RB=1) the game's own Online mode */
        int kind = g_onl_setup; g_onl_setup = 0;
        uint32_t online_rb_peer(void); uint32_t peer = online_rb_peer(); int heard = rbn_peer_heard_ms();
        if (kind == 1) {
            InterlockedExchange(&g_hs_room, 1);
            printf("[rbnet] handshake %ld: the Online mode's waiting-stage fight (melee mode 13): the game's own, no rollback, nothing changed\n", (long)round);
            fflush(stdout); return 0;
        }
        if (!peer || heard < 0 || heard >= 3000) {
            InterlockedExchange(&g_hs_room, 1);
            printf("[rbnet] handshake %ld: the game's Online match (melee mode 14): %s: the game's own online netcode plays it; no rollback, nothing changed\n", (long)round,
                   !peer ? "no other station of the gathering seen" : "the other game's rollback session never heard (rollback off there)");
            fflush(stdout); return 0;
        }
        lwr = 1; onl = 1;
        if (!getenv("SSBU_RB_NET_PLAYER")) g_local = g_onl_local;
        printf("[rbnet] handshake %ld: the game's Online match (melee mode 14; the other station 10.13.%u.%u, its rollback session heard %d ms ago): agreeing on a rollback match; this game is player %d (%s)\n", (long)round,
               (peer >> 8) & 255, peer & 255, heard, g_local + 1, g_local ? "joined the gathering" : "opened the gathering");
        fflush(stdout);
    }
    InterlockedExchange(&g_lay_have_peer, 0); InterlockedExchange(&g_lay_have_mine, 0); InterlockedExchange(&g_lay_peer_sq_ok, 0); g_sq_agreed = 0;      /* (a LAYOUT of an older match is not this one's) */
    {   int nnldn_role(void), nnldn_players(void); const char *e = getenv("SSBU_RB_NET_ROOM_MATCH"), *rr = getenv("SSBU_RB_NET_ROOM_ROLLBACK");
        if (!onl && nnldn_role() && !(e && e[0] == '1')) {
            int heard = rbn_peer_heard_ms(), players = nnldn_players(), want = !(rr && rr[0] == '0');
            if (!g_setup_waiting && want && players == 2 && heard >= 0 && heard < 3000) {
                lwr = 1;
                printf("[rbnet] handshake %ld: the Local Wireless room's match (%s; 2 players; the other game's rollback session heard %d ms ago): agreeing on a rollback match\n", (long)round, nnldn_role() == 1 ? "made here" : "joined", heard);
                fflush(stdout);
            } else {
                static int told; void menu_popup(const char *title, const char *text);
                InterlockedExchange(&g_hs_room, 1);
                printf("[rbnet] handshake %ld: this fight is set up inside a Local Wireless room (%s; %s): the game's own online netcode plays it; no rollback, no handshake, nothing changed\n", (long)round, nnldn_role() == 1 ? "made here" : "joined",
                       g_setup_waiting ? "the room's waiting-stage fight" : !want ? "room rollback off (SSBU_RB_NET_ROOM_ROLLBACK=0)" : players != 2 ? "not a 1-on-1 room" : "the other player's game has rollback off (never heard)");
                fflush(stdout);
                if (!g_setup_waiting && !told++) menu_popup("Rollback netcode", "This room match uses the game's own online netcode: the other player's game has rollback off (or the room has more than 2 players). Rollback is used in a 1-on-1 room when both players have it on.");
                return 0;
            }
        } }
    /* (R3 pub1, testers 2026-10-20) through a lobby server (and on a LAN) NEITHER game is in JOIN mode, so both were "the host":
     * each waited for the other's CHOICE and sent nothing ("messages sent 0, received 0" for 120 s). The roles now come from
     * the Local Wireless room: the game that made the room is player 1 (host of the handshake), the one that joined it player 2.
     * SSBU_RB_NET_PLAYER still forces one (tests). */
    {   int nnldn_role(void); int role = onl ? 0 : nnldn_role();
        if (role && !getenv("SSBU_RB_NET_PLAYER")) { int want = role == 2; if (want != g_local) printf("[rbnet] handshake: this game %s the Local Wireless room: it is player %d (was %d by the online mode)\n", role == 1 ? "made" : "joined", want + 1, g_local + 1); g_local = want; } }
    /* (R3 net3, goal 3) a rollback match without a room code: this game is in the lobby (or LAN) mode and not in a Local Wireless
     * room, so it does not know the other player. It asks the lobby for an opponent who does the same (lobby 1.4, MATCH op 2:
     * net_relay.c net_match_find_rb); the lobby pairs two such games of the same build and lobby code and says who is player 1
     * (the one that waited longer). Then the handshake below runs as with a room code, with the rollback messages addressed to
     * that opponent. The box shows the search. SSBU_RB_NET_FIND=0: the old way (no search). */
    /* (owner 2026-10-21: "così hai tolto la possibilità alla gente di giocare offline") a Smash fight with rollback on and no room
     * code searched the lobby for an opponent: local fights against friends or CPUs were gone. Now a fight without a room code
     * (online play off, lobby or LAN mode) is a local fight, untouched, like a room fight; the online rollback match comes from
     * a Local Wireless room's match (R3 lwr) or a room code (host / join). SSBU_RB_NET_FIND=1: the lobby search (test). */
    /* (Arena, online matchmaking) the Arena menu's Online Ranked tile (competitive.c comp_quickplay): this Smash fight is a quick
     * match: the lobby search below, as with SSBU_RB_NET_FIND=1; fights set up from the Smash tile stay local. */
    int comp_quickplay(void); int quick = comp_quickplay();
    {   const char *e = getenv("SSBU_RB_NET_FIND"); int mode = net_online() > 0 ? net_mode() : -1;
        if (!lwr /* (R3 lwr: a room match) */ && mode != 2 /* NET_MODE_HOST */ && mode != 3 /* NET_MODE_JOIN */ && !(e && e[0] == '1') && !quick) {
            InterlockedExchange(&g_hs_room, 1); InterlockedExchange(&g_hs_local, 1);
            printf("[rbnet] handshake %ld: no room code: a local fight, played as without rollback\n", (long)round);
            fflush(stdout);
            return 0;
        } }
    int find = 0, phase = 1;
    {   int nnldn_role(void); const char *e = getenv("SSBU_RB_NET_FIND"); int mode = net_online() > 0 ? net_mode() : -1;
        find = !onl && !nnldn_role() && (mode == 0 /* NET_MODE_LOBBY */ || mode == 1 /* NET_MODE_LAN */) && ((e && e[0] == '1') || quick);
        if (find) { phase = 0; net_match_find_rb(); } }
    double limit = getenv("SSBU_RB_NET_HS_WAIT") ? atof(getenv("SSBU_RB_NET_HS_WAIT")) * 1000.0 : 120000.0;
    if (lwr) limit = getenv("SSBU_RB_NET_ROOM_WAIT") ? atof(getenv("SSBU_RB_NET_ROOM_WAIT")) * 1000.0 : 10000.0;     /* (R3 lwr) both games are at this set-up together */
    uint64_t t0 = GetTickCount64(), last_tx = 0, last_log = t0, last_note = 0, last_redraw = 0;
    long long presents = nvn_front_presents(); uint64_t presents_at = t0, notes = 0, redraws = 0;
    int ok = 0, stopped = 0;
    AcquireSRWLockExclusive(&g_hs_lock);
    do g_hs_nonce = (uint32_t)hs_rand(); while (!g_hs_nonce);
    g_hs_peer_nonce = 0; g_hs_have_peer = g_hs_have_prop = 0; g_hs_accepted = g_hs_refused = 0; g_hs_why[0] = 0; g_hs_cancelled = 0;
    g_hs_waiting = 1;
    ReleaseSRWLockExclusive(&g_hs_lock);
    if (find) printf("[rbnet] handshake %ld: no room code and no Local Wireless room: asking the lobby for an opponent who starts a rollback match too; own choice ui_chara %016llx colour %d (at most %.0f s; nonce %08x)\n", (long)round,
           (unsigned long long)mine->chara[0], mine->colour[0], limit / 1000.0, g_hs_nonce);
    else printf("[rbnet] handshake %ld: this game is the %s; own choice ui_chara %016llx colour %d; waiting for the other game (at most %.0f s; nonce %08x)\n", (long)round, g_local ? "joiner (player 2)" : "host (player 1)",
           (unsigned long long)mine->chara[0], mine->colour[0], limit / 1000.0, g_hs_nonce);
    fflush(stdout);
    NetMatch mm; memset(&mm, 0, sizeof mm);
    for (;;) {
        uint64_t now = GetTickCount64();
        if (g_hs_refused) break;
        if ((double)(now - t0) > limit) { snprintf(g_hs_why, sizeof g_hs_why, phase ? "no answer from the other player after %.0f s" : "no opponent found in %.0f s", limit / 1000.0); break; }
        if (hs_stop_pressed((double)(now - t0))) { snprintf(g_hs_why, sizeof g_hs_why, "stopped waiting (Esc / B) after %.0f s", (now - t0) / 1000.0); stopped = 1; break; }
        if (phase == 0) {                               /* (R3 net3) the lobby's opponent search */
            net_match_get(&mm);
            if (mm.kind == 2 && (mm.state == MM_MATCHED || mm.state == MM_TOGETHER) && mm.peer) {
                int want = mm.role == MM_ROLE_JOIN;
                AcquireSRWLockExclusive(&g_hs_lock); g_local = want; ReleaseSRWLockExclusive(&g_hs_lock);
                rtt_reset();
                printf("[rbnet] handshake %ld: the lobby found an opponent after %.1f s: %s (10.13.%u.%u); this game is the %s\n", (long)round, (now - t0) / 1000.0, mm.peer_name, mm.peer >> 8 & 255, mm.peer & 255, want ? "joiner (player 2)" : "host (player 1)");
                fflush(stdout);
                phase = 1; t0 = now; last_note = 0;
            } else if (mm.state == MM_TIMEOUT || mm.state == MM_OLD_LOBBY || mm.state == MM_PEER_LEFT || mm.state == MM_EXPIRED || mm.state == MM_OFFLINE
                       || mm.state == MM_NO_ACCOUNT || mm.state == MM_BLOCKED || (mm.kind != 2 && mm.state != MM_ASKING)) {
                snprintf(g_hs_why, sizeof g_hs_why, "%s", mm.state == MM_TIMEOUT ? "no opponent found in time (nobody with your version, mods and game files searched)" :
                         mm.state == MM_NO_ACCOUNT ? "this search needs your SSBU Arena account: sign in in the launcher" :
                         mm.state == MM_BLOCKED ? "matchmaking is blocked for your account for now (fair play)" :
                         mm.state == MM_OLD_LOBBY ? "the lobby server is an older version (it cannot find rollback opponents): use a room code" :
                         mm.state == MM_OFFLINE ? "online play is off" : mm.kind != 2 ? "another search was started (F1 > Online)" : "the opponent left");
                break;
            }
        }
        AcquireSRWLockExclusive(&g_hs_lock);
        if (phase == 0) { /* searching: nothing to send yet */ }
        else if (g_local == 0) {                             /* host: proposal = our choice + the joiner's */
            int rtt_wait = (g_delay_auto || (auto_ping_on() && g_hs_have_peer && (g_hs_peer.m.pad0 & 0x08))) && auto_rule_delay() < 0 && now - t0 < 3000;      /* (rb-8ms, delay auto) a round trip first, at most 3 s; (0.2.65) also when only the other game is Auto */
            if (!g_hs_have_prop && g_hs_have_peer && lwr && !rtt_wait) {      /* (R3 lwr) the room's match: both games built it from the room; only the seed is new */
                if (g_hs_peer.exe_stamp != exe_stamp()) printf("[rbnet] handshake: NOTE the other game's exe was linked at another time (stamp %08x here, %08x there): a different build may desync\n", exe_stamp(), g_hs_peer.exe_stamp);
                if (g_hs_peer.m.chara[0] != mine->chara[0] || g_hs_peer.m.rule_kind != mine->rule_kind || g_hs_peer.m.stock != mine->stock) {
                    snprintf(g_hs_why, sizeof g_hs_why, "the room's match differs on the two games (player 1 %016llx here, %016llx there; rule kind %d / %d, stocks %d / %d)", (unsigned long long)mine->chara[0], (unsigned long long)g_hs_peer.m.chara[0], mine->rule_kind, g_hs_peer.m.rule_kind, mine->stock, g_hs_peer.m.stock);
                    g_hs_refused = 1; hs_refuse(g_hs_peer.nonce, g_hs_why);
                } else {
                    RbhMatch m = *mine; m.seed = hs_new_seed(); if (rbn_ba_mode()) m.pad0 |= 1;      /* (Battle Arena) the room maker's mode: a squad match */
                    hs_delay_want(&m); hs_delay_agree(&m, &g_hs_peer.m);
                    hs_head(&g_hs_prop, RBH_PROPOSE); g_hs_prop.echo = g_hs_peer.nonce; g_hs_prop.m = m; g_hs_have_prop = 1;
                }
            }
            if (!g_hs_have_prop && g_hs_have_peer && !lwr && !rtt_wait) {
                if (g_hs_peer.exe_stamp != exe_stamp()) printf("[rbnet] handshake: NOTE the other game's exe was linked at another time (stamp %08x here, %08x there): a different build may desync\n", exe_stamp(), g_hs_peer.exe_stamp);
                RbhMatch m = *mine;
                m.chara[1] = g_hs_peer.m.chara[0]; m.colour[1] = g_hs_peer.m.colour[0];
                m.has_layout[1] = g_hs_peer.m.has_layout[0]; memcpy(m.layout[1], g_hs_peer.m.layout[0], sizeof m.layout[1]);
                if (m.chara[1] == m.chara[0] && m.colour[1] == m.colour[0]) m.colour[1] = (uint8_t)((m.colour[0] + 1) & 7);   /* same fighter, same colour: the joiner takes the next one */
                m.seed = hs_new_seed();
                hs_stage_rule(&m, &g_hs_peer.m);
                hs_delay_want(&m); hs_delay_agree(&m, &g_hs_peer.m);
                hs_head(&g_hs_prop, RBH_PROPOSE); g_hs_prop.echo = g_hs_peer.nonce; g_hs_prop.m = m; g_hs_have_prop = 1;
            }
            if (g_hs_accepted && g_hs_have_prop) { *out = g_hs_prop.m; ok = 1; }
            else if (g_hs_have_prop && now - last_tx >= 100) { last_tx = now; hs_send(&g_hs_prop, sizeof g_hs_prop); }
        } else {                                        /* joiner: our choice until the proposal comes */
            if (g_hs_have_prop) {
                if (g_hs_prop.exe_stamp != exe_stamp()) printf("[rbnet] handshake: NOTE the other game's exe was linked at another time (stamp %08x here, %08x there): a different build may desync\n", exe_stamp(), g_hs_prop.exe_stamp);
                *out = g_hs_prop.m; ok = 1;
            } else if (now - last_tx >= 100 && net_rb_ready() && !(auto_ping_on() && g_delay_auto && auto_rule_delay() < 0 && now - t0 < 3000)) { last_tx = now;      /* (0.2.65) Auto: the ping first (at most 3 s) */ RbhMsg c; hs_head(&c, RBH_CHOICE); c.m = *mine; if (lwr && rbn_ba_mode()) c.m.pad0 |= 1; hs_delay_want(&c.m); hs_send(&c, sizeof c); }
        }
        if (ok) g_hs_waiting = 0;
        ReleaseSRWLockExclusive(&g_hs_lock);
        if (ok) break;
        if (now - last_note >= 250) {                   /* the message box, and the last frame shown again while the game itself is held here */
            last_note = now; notes++;
            char t[400]; double w = (now - t0) / 1000.0;
            char line[200];
            if (phase == 0) snprintf(line, sizeof line, !net_rb_ready() ? "Online match: connecting to the lobby..." : mm.searching > 0 ? "Online match: finding an opponent (%d of %d players of your version searching)..." : "Online match: finding an opponent...", mm.searching, mm.players);
            else if (onl) snprintf(line, sizeof line, "Online match: agreeing on a rollback match with the other player...");
            else if (lwr) snprintf(line, sizeof line, "Room match: agreeing on a rollback match with the other player...");
            else if (find) snprintf(line, sizeof line, g_hs_peer_nonce ? "Online match with %s: agreeing on the match..." : "Online match: opponent found (%s), waiting for their game...", mm.peer_name);
            else snprintf(line, sizeof line, "%s", !net_rb_ready() ? "Not connected to the other player's game yet (lobby / room not joined)." : net_rb_no_destination() && !g_hs_peer_nonce ? "The other player is not in your Local Wireless room: both players must be in the same room." : g_hs_peer_nonce ? "The other player is here: agreeing on the match..." : "Waiting for the other player to choose a fighter and start...");
            snprintf(t, sizeof t, "%s\nWaiting %.0f s (this game gives up after %.0f s and goes back to the character select).\nPress Esc or hold B to stop waiting.", line, w, limit / 1000.0);
            menu_notice(t);
        }
        {   long long p = nvn_front_presents();
            if (p != presents) { presents = p; presents_at = now; }
            else if (now - presents_at > 100 && now - last_redraw >= 100) { last_redraw = now; redraws++; nvn_front_osk_redraw(); } }
        if (now - last_log >= 5000) { last_log = now; printf("[rbnet] handshake %ld: still waiting (%s%.0f s; lobby %s; other game %s; messages sent %lld, received %lld; notice updates %llu, frames shown again %llu)\n", (long)round, phase ? "" : "finding an opponent, ", (now - t0) / 1000.0,
                                                          net_rb_ready() ? (net_rb_no_destination() ? "joined, no other player in the room" : "joined") : "NOT joined", g_hs_peer_nonce ? "seen" : "not seen", g_hs_tx, g_hs_rx, (unsigned long long)notes, (unsigned long long)redraws); fflush(stdout); }
        Sleep(5);
    }
    if (!ok) {
        AcquireSRWLockExclusive(&g_hs_lock);
        int told = g_hs_refused == 2; uint32_t echo = g_hs_peer_nonce;
        g_hs_waiting = 0; g_hs_cancelled = lwr ? 0 : 1;
        if (lwr) InterlockedExchange(&g_hs_room, 1);          /* (R3 lwr) nothing was changed: the game's own netcode plays the room's match */
        ReleaseSRWLockExclusive(&g_hs_lock);
        if (find) { net_match_get(&mm); if (mm.kind == 2 && (mm.state == MM_ASKING || mm.state == MM_SEARCHING || mm.state == MM_MATCHED)) net_match_cancel(); }   /* (R3 net3) the lobby and the opponent are told */
        if (!told) { char w[160]; snprintf(w, sizeof w, "the other player %s", stopped ? "stopped waiting" : "gave up waiting (time limit)"); for (int k = 0; k < 3; k++) { hs_refuse(echo, w); Sleep(20); } }
        if (lwr) {                                       /* (R3 lwr) */
            menu_notice(NULL);
            printf("[rbnet] handshake %ld FAILED in the %s: %s. The %s match is played by the game's own online netcode, as set up (no rollback)\n", (long)round, onl ? "game's Online match" : "Local Wireless room", g_hs_why, onl ? "Online" : "room's");
            fflush(stdout); return 0;
        }
        char t[400]; snprintf(t, sizeof t, "No online match: %s.\nThis match is left at once: back to the character select.", g_hs_why); menu_notice(t);
        printf("[rbnet] handshake %ld FAILED: %s. This match is played on this PC only (Final Destination, one stock) and left at once (player 1 walks off): back to the character select (notice updates %llu, frames shown again %llu)\n",
               (long)round, g_hs_why, (unsigned long long)notes, (unsigned long long)redraws);
        fflush(stdout); return 0;
    }
    menu_notice(NULL);
    g_hs_lwr = lwr; g_hs_onl = onl;
    InterlockedExchange(&g_clk_match, 1);
    g_delay_agreed = (out->pad0 & 0x08) ? out->pad0 >> 4 : 0;      /* (rb-8ms) the input delay of both games, or 0 */
    if (g_delay_agreed) {
        int n = 0; double best = -1; int rtt = auto_rtt_shown_ms(1, &n, &best);
        char own[48]; if (g_delay_auto) snprintf(own, sizeof own, "auto, %d from the ping", auto_rule_delay_ex(1)); else if (g_delay_cfg >= 0) snprintf(own, sizeof own, "fixed %d", g_delay_cfg); else snprintf(own, sizeof own, "from the round trip");
        printf("[rbnet] handshake: input delay %d frames on both games (agreed; this game %s; round trip %d ms, median of the last %d, best %.1f ms; Auto rule %s)\n", g_delay_agreed, own, rtt, n < 5 ? n : 5, best,
               auto_v2_on() ? "0.2.68 ping less one, at most 7 (SSBU_RB_AUTO_DELAY_V2=1)" : auto_ping_on() ? "0.2.65 ping (SSBU_RB_AUTO_DELAY_PING=1, SSBU_RB_AUTO_DELAY_V2=0)" : "0.2.50 (SSBU_RB_AUTO_DELAY_PING=0)");
        fflush(stdout);
    }
    g_sq_agreed = lwr && (out->pad0 & 1);
    if (lwr && (g_sq_agreed || rbn_ba_mode())) { printf("[squad] room match: %s (room maker's Battle Arena mode %s, this game's %d)\n", g_sq_agreed ? "a Battle Arena squad match (Squad Strike, Tag Team, 3 fighters each, 1 stock each)" : "a 1-on-1 match: the room maker is not in Battle Arena", g_sq_agreed ? "on" : "off", rbn_ba_mode()); fflush(stdout); }
    g_hs_pending = 1; g_hs_pending_t = GetTickCount64();      /* (Arena) agreed: the fight's pads are the session's from now on (rbn_fight_pending) */
    g_hs_seed = out->seed; g_match_id = (uint32_t)(out->seed ^ (out->seed >> 32)) | 1u;
    host_fixed_random_reseed(g_hs_seed);
    printf("[rbnet] handshake %ld agreed after %.2f s (messages sent %lld, received %lld; match id %08x; notice updates %llu, frames shown again %llu)\n", (long)round, (GetTickCount64() - t0) / 1000.0, g_hs_tx, g_hs_rx, g_match_id,
           (unsigned long long)notes, (unsigned long long)redraws);
    hs_print("the match", out);
    return 1;
}
/* rbn_start: the game's match block of this game, sent to the other in chunks (a check of the agreed start) */
static void hs_block_send(void) {
    if (!g_hs_mine_ok) return;
    for (int off = 0; off < RBH_BLK; off += 768) {
        RbhBlock b; memset(&b, 0, sizeof b); b.type = RBH_BLOCK; b.proto = RBH_PROTO; b.game_build = (int16_t)g_game_build; b.exe_stamp = exe_stamp(); b.nonce = g_match_id;
        b.off = (uint16_t)off; b.len = (uint16_t)(RBH_BLK - off < 768 ? RBH_BLK - off : 768); b.total = RBH_BLK; memcpy(b.data, g_hs_mine_blk + off, b.len);
        hs_send(&b, (int)(offsetof(RbhBlock, data) + b.len));
    }
}
static void hs_block_capture(void) {
    SIZE_T got = 0; uint64_t a = ga(0x71052c41b0ULL);            /* the game's global match block (debug_mode.c MELEE_INFO) */
    g_hs_mine_ok = ReadProcessMemory(GetCurrentProcess(), (const void *)(uintptr_t)a, g_hs_mine_blk, RBH_BLK, &got) && got == RBH_BLK;
}
static void hs_block_compare(void) {                    /* main thread (rbn_drain): once both copies are complete */
    if (g_hs_blk_done || !g_hs_mine_ok) return;
    for (int k = 0; k * 768 < RBH_BLK; k++) if (!g_hs_peer_have[k]) return;
    g_hs_blk_done = 1;
    char line[900]; int n = 0, ranges = 0, bytes = 0;
    AcquireSRWLockExclusive(&g_hs_lock);
    for (int i = 0; i < RBH_BLK; i++) {
        if (g_hs_mine_blk[i] == g_hs_peer_blk[i]) continue;
        int j = i; while (j + 1 < RBH_BLK && g_hs_mine_blk[j + 1] != g_hs_peer_blk[j + 1]) j++;
        bytes += j - i + 1;
        if (ranges++ < 24 && n < (int)sizeof line - 80) n += snprintf(line + n, sizeof line - n, " +%03x..%03x (here %02x there %02x)", i, j, g_hs_mine_blk[i], g_hs_peer_blk[i]);
        i = j;
    }
    ReleaseSRWLockExclusive(&g_hs_lock);
    if (!ranges) printf("[rbnet] match block check: IDENTICAL on both games (%d bytes: rules + the two player records)\n", RBH_BLK);
    else printf("[rbnet] match block check: %d ranges (%d bytes) differ (offsets from the match block; players at +0x580 + 0x260 * i):%s\n", ranges, bytes, line);
    fflush(stdout);
}

/* ---------------------------------------------------------------- receive queue (net thread -> main) */
#define RQ_N 1024
#define RQ_MAX 1100
static struct { uint64_t at_us; long long at_frame; int n; uint8_t b[RQ_MAX]; } *g_rq;
static volatile LONG g_rq_head, g_rq_tail; static SRWLOCK g_rq_lock = SRWLOCK_INIT;
static HANDLE g_rq_ev;                                   /* (phase 3) set for every queued message: the pacing wait wakes at once */
HANDLE rbn_rx_event(void) { if (!g_rq_ev) { HANDLE e = CreateEventW(NULL, FALSE, FALSE, NULL); if (InterlockedCompareExchangePointer((PVOID *)&g_rq_ev, e, NULL)) CloseHandle(e); } return g_rq_ev; }
static volatile LONG64 g_rx_pkts, g_rx_bad, g_rx_drop, g_tx_pkts, g_tx_fail, g_rx_last_ms, g_rx_oldmatch;
static volatile LONG g_rtt_us = -1, g_rtt_min_us = -1;
static volatile LONG g_rtt_hist[5], g_rtt_hist_n;      /* (0.2.47) the last 5 round trips, for the ping shown (rbn_rtt_ms) */
static void rtt_hist_add(long rtt) { LONG k = InterlockedIncrement(&g_rtt_hist_n) - 1; InterlockedExchange(&g_rtt_hist[k % 5], rtt); }
static long long g_rtt_n; static double g_rtt_sum;      /* (under g_rq_lock) */
#define TSQ 64
static int32_t g_tsq[TSQ]; static int g_tsq_n;          /* clock-offset samples waiting for the main thread (under g_rq_lock) */
/* (hs14) the mode byte at the end of every PADS message (after the match tag): bit 7 = this game plays the match WITHOUT the
 * ring (delay-based, r1_ring.c g_rg_nomem), bits 0..6 = the delay that covers the one-way trip as this game measured it at
 * its match start (frozen then, so both games choose from the same two numbers). The start barrier ends only with a PADS
 * message of the other game, so both know both bytes before frame 1. When either game has no ring, BOTH use the larger of
 * the two delays (SSBU_RB_NET_PEERDELAY=0: the old way, the game with the ring keeps its own rollback delay and the one
 * without it stalls almost every frame: pm1j 1,913 stalls in 2,400 frames). */
static int g_lockstep, g_my_lockd, g_peerdelay = -1;
static volatile LONG g_peer_mode = -1;                 /* the other game's mode byte of this match (-1 = none seen yet) */
static uint8_t my_mode(void) { return (uint8_t)((g_lockstep ? 0x80 : 0) | (g_my_lockd & 0x7f)); }
static void send_msg(const RbMsg *m) {
    uint8_t b[RB_PADS_HEAD + RB_MAX_PADS_PER_MSG * RB_PAD_WIRE + 2];
    size_t n = rb_msg_build(b, sizeof b - 2, m);
    if (!n || g_loop) return;
    if (m->type == RB_MSG_PADS) { b[n++] = pads_tag(); b[n++] = my_mode(); }      /* (hs12) the match these pads belong to: pads of another match are dropped; (hs14) this game's mode byte */
    if (net_rb_send(b, (int)n)) InterlockedIncrement64(&g_tx_pkts); else InterlockedIncrement64(&g_tx_fail);
}
/* net_relay.c's receive thread: one rollback message from the other player */
void rbn_rx(const uint8_t *p, int n) {
    if (rbn_on() > 0 && n > 0 && hs_rx(p, n)) return;  /* the match handshake */
    if (rbn_on() <= 0 || n <= 0 || n > RQ_MAX) { InterlockedIncrement64(&g_rx_bad); return; }
    if (p[0] == RB_MSG_PADS && n >= RB_PADS_HEAD + 2 && (n - RB_PADS_HEAD) % RB_PAD_WIRE == 2) {     /* (hs12) the match tag, (hs14) then the mode byte, at the end */
        if (!g_started || p[n - 2] != pads_tag()) { InterlockedIncrement64(&g_rx_oldmatch); return; }      /* (between matches: the other game sends them again) */
        InterlockedExchange(&g_peer_mode, p[n - 1]);    /* (before it is queued: rbn_drain sees it with the message that starts the match) */
        n -= 2;
    }
    InterlockedIncrement64(&g_rx_pkts); InterlockedExchange64(&g_rx_last_ms, (LONG64)GetTickCount64());
    if (p[0] == RB_MSG_PING) {                          /* answered at once on this thread (the round trip excludes the frame wait) */
        RbMsg m, r; if (!rb_msg_parse(p, (size_t)n, &m)) { InterlockedIncrement64(&g_rx_bad); return; }
        memset(&r, 0, sizeof r); r.type = RB_MSG_PONG; r.frame = (int32_t)g_cur; r.time_us = now_us(); r.echo_us = m.time_us;
        send_msg(&r); return;
    }
    if (p[0] == RB_MSG_PONG) {                          /* the round trip at once (also before the match: the delay is chosen from it) */
        RbMsg m; if (!rb_msg_parse(p, (size_t)n, &m)) { InterlockedIncrement64(&g_rx_bad); return; }
        long rtt = (long)(now_us() - m.echo_us);
        if (rtt <= 0 || rtt >= 5000000) return;
        InterlockedExchange(&g_rtt_us, rtt); if (g_rtt_min_us < 0 || rtt < g_rtt_min_us) InterlockedExchange(&g_rtt_min_us, rtt);
        rtt_hist_add(rtt);
        AcquireSRWLockExclusive(&g_rq_lock);
        g_rtt_sum += rtt; g_rtt_n++;
        if (g_started && m.frame > 0 && g_cur > 0 && g_tsq_n < TSQ) g_tsq[g_tsq_n++] = (int32_t)((g_cur - m.frame) * 16683 - rtt / 2);   /* > 0: this game runs ahead */
        ReleaseSRWLockExclusive(&g_rq_lock);
        return;
    }
    AcquireSRWLockExclusive(&g_rq_lock);
    if (!g_rq) g_rq = calloc(RQ_N, sizeof *g_rq);
    LONG next = (g_rq_head + 1) % RQ_N;
    if (g_rq && next != g_rq_tail) { g_rq[g_rq_head].at_us = now_us(); g_rq[g_rq_head].at_frame = g_cur; g_rq[g_rq_head].n = n; memcpy(g_rq[g_rq_head].b, p, (size_t)n); g_rq_head = next; }
    else InterlockedIncrement64(&g_rx_drop);
    ReleaseSRWLockExclusive(&g_rq_lock);
    if (g_rq_ev) SetEvent(g_rq_ev);
}
static int rbn_peer_heard_ms(void) {                    /* (R3 lwr) any rollback message of the other game (its pings come every 100 ms) */
    LONG64 last = g_rx_last_ms;
    return last ? (int)(GetTickCount64() - (ULONGLONG)last) : -1;
}
static void rtt_reset(void) {                           /* (R3 net3) */
    AcquireSRWLockExclusive(&g_rq_lock); g_rtt_sum = 0; g_rtt_n = 0; ReleaseSRWLockExclusive(&g_rq_lock);
    InterlockedExchange(&g_rtt_us, -1); InterlockedExchange(&g_rtt_min_us, -1); InterlockedExchange(&g_rtt_hist_n, 0);
}
/* a thread that pings the other player every 100 ms from the start (the delay is chosen from it when the match begins) */
static DWORD WINAPI ping_thread(void *u) {
    (void)u;
    { const char *e = getenv("SSBU_NET_RXPRIO"); if (!(e && *e == '0')) SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_HIGHEST); }      /* (0.2.47) the ping's send time is not delayed by busy game threads */
    for (;;) {
        Sleep(100);
        if (!net_rb_ready()) continue;
        RbMsg m; memset(&m, 0, sizeof m); m.type = RB_MSG_PING; m.frame = (int32_t)g_cur; m.time_us = now_us();
        send_msg(&m);
    }
    return 0;
}
void rbn_init(void) {                                   /* r1_state.c ra_init: before the match */
    static int done; if (done++ || rbn_on() <= 0) return;
    if (!g_loop) CloseHandle(CreateThread(NULL, 0, ping_thread, NULL, 0, NULL));
}

/* ---------------------------------------------------------------- the session */
static long long g_f0;                                  /* the base frame (the ring's first capture) */
static int g_delay, g_remote = 1;
static long long g_peer_ack, g_sent_newest;
static long long g_stat_pred, g_stat_padget, g_stat_mis;
static long long g_ts_n; static double g_ts_extra;     /* time sync: extra ms per frame for the next frames */
static int g_ts_left;
static uint64_t g_hacc = 0xcbf29ce484222325ull; static int g_hlines;
#define HR 512
static uint64_t g_hash[HR]; static long long g_hframe[HR], g_hfinal_upto; static long long g_desync, g_check_ok, g_desync_first = -1;
static long long g_peer_check_f; static uint32_t g_peer_check;
static long long g_agree_f; static uint32_t g_agree_ck;      /* (0.2.53, online_elo.c) the newest frame whose checksum both games had the same */

/* (hs13) g_lockstep: a match without the rollback ring: every frame waits for the other pad */
void rbn_set_lockstep(int on) { g_lockstep = on; }
int rbn_loop(void) { return g_loop; }
static int lock_delay(void) {                           /* the delay that covers the whole one-way trip (+ 1 frame): the wait is short or none */
    if (g_loop) return g_loop_lat + 1;
    int rt = g_rtt_min_us > 0 ? g_rtt_min_us : g_rtt_us;
    if (rt <= 0) return 4;
    int d = (int)ceil(rt / 2000.0 / (1000.0 / 60.0)) + 1;
    return d < 1 ? 1 : d > 12 ? 12 : d;
}
static int peer_lockstep(void) { return g_peer_mode >= 0 && (g_peer_mode & 0x80) && g_peerdelay; }
static int auto_ping_on(void) {                         /* (0.2.65) SSBU_RB_AUTO_DELAY_PING, default 1 */
    static int on = -1; if (on < 0) { const char *e = getenv("SSBU_RB_AUTO_DELAY_PING"); on = !(e && e[0] == '0'); }
    return on;
}
/* (0.2.68, SSBU_RB_AUTO_DELAY_V2, default 1; 0 = the 0.2.65 rule) Auto = the 0.2.65 ping rule less one frame, at most 7: rollback
 * covers the last frame of the trip, so the player feels a frame less of delay (only with the 0.2.65 ping rule on). */
static int auto_v2_on(void) {
    static int on = -1; if (on < 0) { const char *e = getenv("SSBU_RB_AUTO_DELAY_V2"); on = !(e && e[0] == '0'); }
    return on && auto_ping_on();
}
/* (0.2.65) the ping the Auto delay is chosen from: the median of the last 5 round trips (the number the ping overlay shows,
 * rbn_rtt_ms, so a player who sees 150 gets the 150 row), only once 3 are measured; final = 1 (the 3 s wait is over, or the
 * delay is needed now): the best / last round trip measured, if fewer. -1 = none. */
static int auto_rtt_shown_ms(int final, int *samples, double *best_ms) {
    LONG n = g_rtt_hist_n; if (samples) *samples = (int)n;
    if (best_ms) *best_ms = g_rtt_min_us > 0 ? g_rtt_min_us / 1000.0 : -1.0;
    if (n >= 3) {
        long v[5]; int k = n < 5 ? (int)n : 5;
        for (int i = 0; i < k; i++) v[i] = g_rtt_hist[i];
        for (int i = 1; i < k; i++) for (int j = i; j > 0 && v[j] < v[j - 1]; j--) { long t = v[j]; v[j] = v[j - 1]; v[j - 1] = t; }
        return (int)((v[k / 2] + 500) / 1000);
    }
    if (!final) return -1;
    int r = g_rtt_min_us > 0 ? g_rtt_min_us : g_rtt_us;
    return r > 0 ? (r + 500) / 1000 : -1;
}
/* (0.2.65) the Auto delay for a round trip of ms: the frames that cover the one-way trip (half the round trip) plus a quarter frame
 * (4 ms: the frame's own phase and the send), rounded up: d = ceil(ms / 2 / 16.67 + 0.25); 1 under 15 ms (a home network), at
 * least 2 otherwise (relay jitter, as before), at most SSBU_RB_NET_DELAY_MAX (default 6). Round trip -> frames:
 *   0-14 ms 1 | 15-58 ms 2 | 59-91 ms 3 | 92-125 ms 4 | 126-158 ms 5 | 159 ms and more 6
 * (0.2.68, SSBU_RB_AUTO_DELAY_V2) one frame less, at least 2, at most 7 (SSBU_RB_NET_DELAY_MAX default 7); 1 under 15 ms as before:
 *   0-14 ms 1 | 15-91 ms 2 | 92-125 ms 3 | 126-158 ms 4 | 159-191 ms 5 | 192-225 ms 6 | 226 ms and more 7
 * Whatever is left of the trip above the delay is covered by rollback (at most 7 frames). A 0.2.65..0.2.67 game takes an agreed
 * 7 as any other value (pad0 bits 4..7, rb_input_reset clamps at 15; launcher-fixed delays were 1..5, Auto at most 6). */
static int auto_ping_frames(double ms, int mx) {
    if (ms < 15) return 1;
    int d = ((int)(ms + 0.5) * 3 + 25 + 99) / 100;         /* ceil(ms * 60 / 2000 + 0.25) in integers (no float edge at 125 ms) */
    if (auto_v2_on()) d -= 1;
    if (d < 2) d = 2;
    return d > mx ? (mx < 1 ? 1 : mx) : d;
}
static int auto_rule_delay(void) { return auto_rule_delay_ex(0); }
static int auto_rule_delay_ex(int final) {              /* (rb-8ms) SSBU_RB_NET_DELAY=auto: the rule above hs_delay_want; -1 = no round trip yet */
    static int mx = 0;
    if (!mx) { const char *e = getenv("SSBU_RB_NET_DELAY_MAX"); mx = e && atoi(e) >= 1 ? (atoi(e) > 9 ? 9 : atoi(e)) : auto_v2_on() ? 7 : auto_ping_on() ? 6 : 5; }
    if (auto_ping_on()) { int ms = auto_rtt_shown_ms(final, NULL, NULL); return ms < 0 ? -1 : auto_ping_frames(ms, mx); }
    int rtt = g_rtt_min_us > 0 ? g_rtt_min_us : g_rtt_us;
    if (rtt <= 0) return -1;
    double ms = rtt / 1000.0, one_way = ms / 2;
    if (ms < 15) return 1;
    int d = (int)(one_way / (1000.0 / 60.0) + 0.5) - (one_way >= 100 ? 0 : 1);
    return d < 2 ? (mx < 2 ? mx : 2) : d > mx ? mx : d;
}
static int auto_delay(void) {
    if (g_delay_agreed > 0 && !g_loop) return g_delay_agreed;      /* (rb-8ms) the one both games agreed (hs_delay_agree) */
    if (g_delay_cfg >= 0) return g_delay_cfg;
    if (g_delay_auto && !g_lockstep && !peer_lockstep()) { int d = g_loop ? 2 : auto_rule_delay_ex(1); return d < 1 ? 2 : d; }    /* (auto with a game that cannot agree: this game's own value) */
    if (g_lockstep && (!g_peerdelay || g_loop)) return lock_delay();     /* (the old way; the loop test) */
    if (g_lockstep || peer_lockstep()) {                /* (hs14) either game has no ring: both take the larger of the two announced delays */
        int d = g_my_lockd, pd = g_peer_mode >= 0 ? (g_peer_mode & 0x7f) : 0;
        return pd > d ? pd : d;
    }
    if (g_loop) return 2;
    int rtt = g_rtt_min_us > 0 ? g_rtt_min_us : g_rtt_us;
    if (rtt <= 0) return 2;
    double one_way = rtt / 2000.0 / (1000.0 / 60.0);     /* frames */
    /* (Arena) one frame of delay per frame of one-way trip, rounded up past a quarter: 54 ms round trip -> 2 (was 1: 162
     * rollbacks in 2400 frames, each a load and re-run passes a 4-core PC could not afford); the rest is covered by rollback */
    /* (0.2.14) plus 0.8 frame (13 ms) for the frame's own phase and the trip's jitter: 52 ms through the relay -> 3 (with
     * 2: 384 rollbacks in 6370 frames of an LDN room, each a late frame on screen); the switch to 3 is at about 40 ms */
    int d = (int)ceil(one_way + 0.8);
    return d < 1 ? 1 : d > 5 ? 5 : d;      /* 1..5 (delay 1 and 4 in sync since the agreed match gets no pad before its base) */
}
void rbn_start(long long f0) {
    {   /* (test) SSBU_RB_NET_TEST_MATCHN=<n>: this game counts as having played n matches, as after the other one was restarted */
        static int once; const char *e = once ? NULL : getenv("SSBU_RB_NET_TEST_MATCHN"); once = 1; if (e) g_match_n = atoi(e); }
    g_f0 = f0; g_started = 1; g_match_n++; g_hs_pending = 0;
    if (g_hlog) { fprintf(g_hlog, "m %d id %08x\n", g_match_n, g_match_id); fflush(g_hlog); }      /* (rbn_cmp.py: frames are per match) */
    g_remote = 1 - g_local;
    if (g_peerdelay < 0) { const char *e = getenv("SSBU_RB_NET_PEERDELAY"); g_peerdelay = !(e && e[0] == '0'); }
    g_my_lockd = lock_delay();                          /* (hs14) announced in every PADS message of this match (frozen here) */
    g_delay = auto_delay();
    if (g_delay < 1) g_delay = 1;                        /* (frames 1..delay are empty on both sides: no rollback can reach the base state, made before the start barrier) */
    rb_input_reset(2, g_local, g_delay);
    rb_ts_reset(16683);
    if (!g_loop && rbn_handshake_wanted()) { hs_block_capture(); hs_block_send(); }     /* the match block check */
    printf("[rbnet] match %d (id %08x) start at frame %lld: local player %d, input delay %d frames (round trip %.1f ms, best %.1f ms)\n", g_match_n, g_match_id, f0, g_local + 1, g_delay,
           g_rtt_us > 0 ? g_rtt_us / 1000.0 : -1.0, g_rtt_min_us > 0 ? g_rtt_min_us / 1000.0 : -1.0);
    fflush(stdout);
}
/* after the start barrier: the delay again from the round trip measured meanwhile (no frame has run: the queues start over;
 * pads of the other game already queued are sent again by it, from our acknowledgement on) */
void rbn_choose_delay(void) {
    int d = auto_delay(); if (d < 1) d = 1;
    if (g_lockstep && !g_peerdelay) printf("[rbnet] after the start barrier: this game has no snapshot ring (old way, SSBU_RB_NET_PEERDELAY=0): the other game keeps its own delay\n");
    else if (!g_loop && (g_lockstep || peer_lockstep())) printf("[rbnet] after the start barrier: %s snapshot ring: delay-based pacing, both games use input delay %d (announced here %d, there %d; mode byte here %02x, there %02x)\n",
        g_lockstep && peer_lockstep() ? "neither game has a" : g_lockstep ? "this game has no" : "the other game has no", d, g_my_lockd, g_peer_mode >= 0 ? (int)(g_peer_mode & 0x7f) : -1, my_mode(), (unsigned)(g_peer_mode & 0xff));
    else if (!g_loop) printf("[rbnet] after the start barrier: both games have the ring (mode byte here %02x, there %02x)\n", my_mode(), (unsigned)(g_peer_mode & 0xff));
    fflush(stdout);
    if (d == g_delay) return;
    printf("[rbnet] after the start barrier: input delay %d -> %d frames (round trip %.1f ms, best %.1f ms)\n", g_delay, d, g_rtt_us > 0 ? g_rtt_us / 1000.0 : -1.0, g_rtt_min_us > 0 ? g_rtt_min_us / 1000.0 : -1.0);
    fflush(stdout);
    g_delay = d; rb_input_reset(2, g_local, g_delay);
}
int rbn_started(void) { return g_started; }
int rbn_delay(void) { return g_started ? g_delay : 0; }
static void send_pads(void) {
    long long newest = rb_input_local_latest();
    if (newest < 1 || g_loop) return;
    RbMsg m; memset(&m, 0, sizeof m);
    m.type = RB_MSG_PADS; m.player = g_local; m.newest = (int32_t)newest; m.ack = rb_input_remote_latest(g_remote);
    long long from = g_peer_ack + 1; if (newest - from + 1 > RB_MAX_PADS_PER_MSG) from = newest - RB_MAX_PADS_PER_MSG + 1; if (from < 1) from = 1;
    for (long long f = newest; f >= from; f--) { RbPad p; if (!rb_input_local_get((int)f, &p)) break; m.pads[m.count++] = p; }
    long long cf = g_hfinal_upto;
    if (cf > 0 && g_hframe[cf % HR] == cf) { m.check_frame = (int32_t)cf; m.check = (uint32_t)(g_hash[cf % HR] ^ (g_hash[cf % HR] >> 32)); }
    send_msg(&m); g_sent_newest = newest;
}
static int16_t hyst1(int16_t v, int16_t *last, int h) {
    if (v >= -RB_STICK_REST && v <= RB_STICK_REST) { *last = 0; return 0; }
    if (v == 32767 || v == -32767 || abs((int)v - (int)*last) > h) *last = v;
    return *last;
}
static RbPad stick_hyst(RbPad p, int16_t st[4]) {        /* (Arena) SSBU_RB_NET_STICK_HYST, default 600 (0: off) */
    static int h = -1; if (h < 0) { const char *e = getenv("SSBU_RB_NET_STICK_HYST"); h = e ? atoi(e) : 600; }
    if (h <= 0) return p;
    p.lx = hyst1(p.lx, &st[0], h); p.ly = hyst1(p.ly, &st[1], h); p.rx = hyst1(p.rx, &st[2], h); p.ry = hyst1(p.ry, &st[3], h);
    return p;
}
/* the local player's pad of a real pass (frame f): kept for f + delay and sent */
void rbn_local_pad(long long f, RbPad now) {
    if (g_bot) now = bot_pad(g_bot_seed, f);
    { static int16_t st[4]; now = stick_hyst(now, st); }
    rb_input_local((int)f, now);
    send_pads();
}
/* (hs10) SSBU_RB_NET_DETAIL=1: the pads each pass gave the game and the fighters' frame lines of the last simulation of each
 * frame, written as "d" lines next to the "f" lines of SSBU_RB_NET_LOG. Always: a local check at the finalization (every read
 * of the final simulation of a frame must have got the final, received pad of both players). */
static int g_detail = -1;
static int detail_on(void) { if (g_detail < 0) { const char *e = getenv("SSBU_RB_NET_DETAIL"); g_detail = e && atoi(e) > 0; } return g_detail; }
static RbPad g_acc_pad[2]; static int g_acc_n[2], g_acc_var[2];          /* reads in the pass now running */
#define HR2 512
static RbPad g_upad[HR2][2]; static int g_un[HR2][2], g_uvar[HR2][2];   /* reads of the last simulation of each frame */
static uint8_t g_ulines[HR2][4][40]; static int g_unl[HR2];
static long long g_padcheck_bad, g_padcheck_first = -1;
RbPad rbn_pad(int player, long long f) {
    int pred = 0; RbPad p = rb_input_get(player, (int)f, &pred);
    g_stat_padget++; g_stat_pred += pred;
    if (player >= 0 && player < 2) { if (g_acc_n[player] && !rb_pad_equal(&g_acc_pad[player], &p)) g_acc_var[player] = 1; g_acc_pad[player] = p; g_acc_n[player]++; }
    return p;
}
static void desync_sample(long long f);                  /* below */
static void on_check(long long cf, uint32_t ck) {
    if (cf <= 0 || cf > g_hfinal_upto || g_hframe[cf % HR] != cf) { g_peer_check_f = cf; g_peer_check = ck; return; }      /* not final here yet: kept for later */
    uint32_t mine = (uint32_t)(g_hash[cf % HR] ^ (g_hash[cf % HR] >> 32));
    if (mine == ck) { g_check_ok++; if (cf > g_agree_f) { g_agree_f = cf; g_agree_ck = ck; } }
    else { g_desync++; if (g_desync_first < 0) { g_desync_first = cf; printf("[rbnet] DESYNC: frame %lld checksum here %08x, the other game %08x\n", cf, mine, ck);
        desync_sample(cf); fflush(stdout);
        static int note = -1; if (note < 0) { const char *e = getenv("SSBU_RB_NET_DESYNC_NOTE"); note = !(e && e[0] == '0'); }      /* (OpenSmash phase, idea: Rsoto95 f04166e) */
        if (note) { char t[300]; snprintf(t, sizeof t, "From frame %lld the two games no longer show the same match. It goes on, but its result may differ between the two players. Both logs have the details.", cf); void menu_popup(const char *title, const char *text); menu_popup("Out of sync", t); } } }
}
/* (hs15, test) SSBU_RB_NET_LOOP_BOT2=<seed>: the loop test's other player is the bot of that seed (11 = the joiner of a pair) */
static unsigned loop_bot2(void) {
    static int on = -1; static unsigned seed;
    if (on < 0) { const char *e = getenv("SSBU_RB_NET_LOOP_BOT2"); on = e && *e; if (on) seed = (unsigned)strtoul(e, NULL, 0); }
    return on ? seed : g_bot_seed ^ 0x5bd1e995u;
}
static RbPad stick_hyst(RbPad p, int16_t st[4]);
static RbPad loop_remote_pad(long long f) {             /* with SSBU_RB_NET_LOOP_BOT2: exactly what that bot sends as a pair's other game (its input delay applied) */
    if (!getenv("SSBU_RB_NET_LOOP_BOT2")) { static int16_t st[4]; g_bp_remote = 1; RbPad r = bot_pad(loop_bot2(), f); g_bp_remote = 0; return stick_hyst(r, st); }      /* (frames come in order) */
    RbPad z; memset(&z, 0, sizeof z);
    if (f <= g_delay) return z;
    g_bp_remote = 1; RbPad r = bot_pad(loop_bot2(), f - g_delay); g_bp_remote = 0;
    return rb_pad_rest(r);
}
/* main thread, at the safe point: the messages received since the last call */
void rbn_drain(long long cur) {
    InterlockedExchange64(&g_cur, cur);
    if (g_loop && g_started) {                          /* loop test: the bot's pad of frame f arrives at frame f + delay - lat */
        static long long lat2_from = -2; static int lat2;          /* (hs15, test) SSBU_RB_NET_LOOP_LAT2=<frame>,<lat>: the lateness changes at that frame */
        if (lat2_from == -2) { const char *e = getenv("SSBU_RB_NET_LOOP_LAT2"); lat2_from = -1; if (e && *e) sscanf(e, "%lld,%d", &lat2_from, &lat2); }
        static int jit = -2; static unsigned jseed;                /* (hs15, test) SSBU_RB_NET_LOOP_JIT=<seed>: lateness g_loop_lat or one less, changing every 4..20 frames */
        if (jit == -2) { const char *e = getenv("SSBU_RB_NET_LOOP_JIT"); jit = e && *e; if (jit) jseed = (unsigned)strtoul(e, NULL, 0); }
        int lat = lat2_from >= 0 && cur >= lat2_from ? lat2 : g_loop_lat;
        if (jit) { uint32_t h = mix32(jseed * 0x9E3779B9u ^ (uint32_t)(cur / (4 + (mix32(jseed) % 17))) * 0x85EBCA6Bu); lat -= (int)(h & 1); }
        {   /* (Arena, test) SSBU_RB_NET_LOOP_SPIKE=<seed>: a ping spike every 2..6 s, 4..24 frames long, the pads 1 or 2 frames later than usual */
            static int spk = -2; static unsigned sseed; static long long s_seen = -1, s_next, s_end; static int s_add, s_n;
            if (spk == -2) { const char *e = getenv("SSBU_RB_NET_LOOP_SPIKE"); spk = e && *e; if (spk) sseed = (unsigned)strtoul(e, NULL, 0); }
            if (spk && cur < s_seen - 100) { s_seen = -1; s_next = s_end = 0; }      /* a new match: its frames start again at 0 */
            if (spk && cur > s_seen) {
                s_seen = cur;
                uint32_t h = mix32(sseed * 0x9E3779B9u ^ (uint32_t)cur * 0x85EBCA6Bu);
                if (!s_next) s_next = cur + 120 + h % 241;
                else if (cur >= s_next) {
                    s_end = cur + 4 + h % 21; s_add = 1 + (int)((h >> 8) & 1); s_next = s_end + 120 + (h >> 12) % 241;
                    printf("[rbnet] loop: ping spike %d at frame %lld: %lld frames, pads %d frame(s) later\n", ++s_n, cur, s_end - cur, s_add);
                }
            }
            if (spk && cur < s_end) lat += s_add;
        }
        if (g_rbn_loop_ontime) lat = 0;                 /* (0.2.58) the player benchmark's match without rollbacks */
        long long upto = cur + g_delay - lat;
        for (long long f = rb_input_remote_latest(g_remote) + 1; f <= upto; f++) rb_input_remote_put(g_remote, (int)f, loop_remote_pad(f));
        return;
    }
    if (rbn_handshake_wanted()) hs_block_compare();
    {   int32_t ts[TSQ]; int nts;
        AcquireSRWLockExclusive(&g_rq_lock); nts = g_tsq_n; memcpy(ts, g_tsq, sizeof(int32_t) * (size_t)nts); g_tsq_n = 0; ReleaseSRWLockExclusive(&g_rq_lock);
        for (int k = 0; k < nts; k++) rb_ts_sample(ts[k]);
    }
    for (;;) {
        static uint8_t b[RQ_MAX]; int n = 0; uint64_t at = 0; long long atf = 0;
        AcquireSRWLockExclusive(&g_rq_lock);
        if (g_rq && g_rq_tail != g_rq_head) { n = g_rq[g_rq_tail].n; at = g_rq[g_rq_tail].at_us; atf = g_rq[g_rq_tail].at_frame; memcpy(b, g_rq[g_rq_tail].b, (size_t)n); g_rq_tail = (g_rq_tail + 1) % RQ_N; }
        ReleaseSRWLockExclusive(&g_rq_lock);
        if (!n) break;
        RbMsg m;
        if (!rb_msg_parse(b, (size_t)n, &m)) { g_rx_bad++; continue; }
        if (m.type != RB_MSG_PADS || !g_started || m.player != g_remote) continue;
        g_peer_started = 1;
        for (int k = 0; k < m.count; k++) rb_input_remote_put(g_remote, m.newest - k, m.pads[k]);
        if (m.ack > g_peer_ack && m.ack <= rb_input_local_latest()) g_peer_ack = m.ack;      /* (hs12: never an acknowledgement of pads not sent yet) */
        if (m.check_frame > 0) on_check(m.check_frame, m.check);
    }
}
int rbn_take_mismatch(void) { int m = rb_input_take_mismatch(); if (m) g_stat_mis++; return m; }
void rbn_put_mismatch(int m) { rb_input_put_mismatch(m); if (m) g_stat_mis--; }      /* (R3 pub1) r1_ring_load refused: the wrong guess stays pending */
long long rbn_confirmed(void) { return rb_input_remote_latest(g_remote); }
int rbn_peer_gone(void) {
    if (g_loop || !g_started) return 0;
    LONG64 last = g_rx_last_ms;
    return last && GetTickCount64() - (ULONGLONG)last > 5000;
}

/* ---------------------------------------------------------------- checksums: fighter_host.c gives each fighter's frame line */
static uint8_t g_lacc[4][40]; static int g_lacc_n;
void rbn_hash_add(const void *p, size_t n) {
    if (g_on <= 0) return;
    if (g_lacc_n < 4 && n <= 40) { memset(g_lacc[g_lacc_n], 0, 40); memcpy(g_lacc[g_lacc_n], p, n); g_lacc_n++; }
    const uint8_t *b = p; while (n--) { g_hacc ^= *b++; g_hacc *= 0x100000001b3ull; }
    g_hlines++;
}
/* the safe point after pass f: the lines since the last call belong to frame f (a re-run pass overwrites the value) */
/* (hs15, pp4: a frame simulated with a wrong pad can leave the fighters' frame lines unchanged, e.g. a button during an
 * animation that ignores it) the frame's checksum also folds the pads the pass gave the game (the last read of each
 * player, whether the reads varied) and the extra fighter fields of rbn_hash_extra: a frame whose final simulation got a
 * wrong pad now differs from the other game's. SSBU_RB_NET_CKPADS=0 = the old checksum (test). */
static int g_ckpads = -1;
int rbn_hash_wanted(void) { return g_on > 0; }
void rbn_hash_extra(const void *p, size_t n) {          /* folded into the frame's checksum only (not a "d" line field) */
    if (g_on <= 0) return;
    if (g_ckpads < 0) { const char *e = getenv("SSBU_RB_NET_CKPADS"); g_ckpads = !(e && e[0] == '0'); }
    if (!g_ckpads) return;
    const uint8_t *b = p; while (n--) { g_hacc ^= *b++; g_hacc *= 0x100000001b3ull; }
}
static uint64_t hash_pads(uint64_t h) {
    if (g_ckpads < 0) { const char *e = getenv("SSBU_RB_NET_CKPADS"); g_ckpads = !(e && e[0] == '0'); }
    if (!g_ckpads) return h;
    for (int q = 0; q < 2; q++) {
        struct { uint64_t buttons; int16_t lx, ly, rx, ry; uint32_t read, varied; } r;
        memset(&r, 0, sizeof r);
        if (g_acc_n[q]) { r.buttons = g_acc_pad[q].buttons; r.lx = g_acc_pad[q].lx; r.ly = g_acc_pad[q].ly; r.rx = g_acc_pad[q].rx; r.ry = g_acc_pad[q].ry; r.read = 1; r.varied = (uint32_t)g_acc_var[q]; }
        const uint8_t *b = (const uint8_t *)&r; for (size_t n = 0; n < sizeof r; n++) { h ^= b[n]; h *= 0x100000001b3ull; }
    }
    return h;
}
void rv_pass(long long f); void rv_final(long long f);     /* fighter_sdk/replay_state.c: the replay viewer's state of each final frame */
void rbn_pass_hash(long long f) {
    rv_pass(f);
    if (g_hlines && f > 0) { g_hash[f % HR] = hash_pads(g_hacc); g_hframe[f % HR] = f; }
    if (f > 0) { int x = (int)(f % HR2);
        for (int q = 0; q < 2; q++) { g_upad[x][q] = g_acc_pad[q]; g_un[x][q] = g_acc_n[q]; g_uvar[x][q] = g_acc_var[q]; }
        memcpy(g_ulines[x], g_lacc, sizeof g_lacc); g_unl[x] = g_lacc_n; }
    memset(g_acc_n, 0, sizeof g_acc_n); memset(g_acc_var, 0, sizeof g_acc_var); g_lacc_n = 0;
    g_hacc = 0xcbf29ce484222325ull; g_hlines = 0;
}
static void desync_sample(long long f) {             /* (OpenSmash phase) the first frame whose checksums differ: what this game had there */
    int x = (int)(f % HR2);
    printf("[rbnet] DESYNC sample, frame %lld: p1 %llx %d %d %d %d (reads %d), p2 %llx %d %d %d %d (reads %d)\n", f,
        (unsigned long long)g_upad[x][0].buttons, g_upad[x][0].lx, g_upad[x][0].ly, g_upad[x][0].rx, g_upad[x][0].ry, g_un[x][0],
        (unsigned long long)g_upad[x][1].buttons, g_upad[x][1].lx, g_upad[x][1].ly, g_upad[x][1].rx, g_upad[x][1].ry, g_un[x][1]);
    for (int l = 0; l < g_unl[x]; l++) { const uint8_t *b = g_ulines[x][l]; int k, st; uint64_t mot, rh; float mf, px, py, dmg;
        memcpy(&k, b, 4); memcpy(&st, b + 4, 4); memcpy(&mot, b + 8, 8); memcpy(&mf, b + 16, 4); memcpy(&px, b + 20, 4); memcpy(&py, b + 24, 4); memcpy(&dmg, b + 28, 4); memcpy(&rh, b + 32, 8);
        printf("[rbnet] DESYNC sample, frame %lld, fighter %d: status %x motion %llx frame %.3f x %.4f y %.4f damage %.3f random %016llx\n", f, k, st, (unsigned long long)mot, mf, px, py, dmg, (unsigned long long)rh); }
}
static void detail_frame(long long f) {              /* frame f is final: the pads its last simulation used against the final ones */
    int x = (int)(f % HR2);
    for (int q = 0; q < 2; q++) {
        RbPad fin; int pred = 0;
        if (q == g_local) { if (!rb_input_local_get((int)f, &fin)) memset(&fin, 0, sizeof fin); }
        else fin = rb_input_get(q, (int)f, &pred);
        int bad = g_un[x][q] && (!rb_pad_equal(&g_upad[x][q], &fin) || g_uvar[x][q]);
        if (bad || pred) { g_padcheck_bad++; if (g_padcheck_first < 0) g_padcheck_first = f;
            if (g_padcheck_bad <= 20) { printf("[rbnet] PAD CHECK: frame %lld player %d: its last simulation got %llx %d %d (reads %d, varied %d), the final pad is %llx %d %d%s\n", f, q + 1,
                (unsigned long long)g_upad[x][q].buttons, g_upad[x][q].lx, g_upad[x][q].ly, g_un[x][q], g_uvar[x][q], (unsigned long long)fin.buttons, fin.lx, fin.ly, pred ? " (NOT received)" : ""); fflush(stdout); } }
    }
    if (g_hlog && detail_on()) {
        fprintf(g_hlog, "d %lld p1 %llx %d %d %d %d n%d p2 %llx %d %d %d %d n%d", f, (unsigned long long)g_upad[x][0].buttons, g_upad[x][0].lx, g_upad[x][0].ly, g_upad[x][0].rx, g_upad[x][0].ry, g_un[x][0],
                (unsigned long long)g_upad[x][1].buttons, g_upad[x][1].lx, g_upad[x][1].ly, g_upad[x][1].rx, g_upad[x][1].ry, g_un[x][1]);
        for (int l = 0; l < g_unl[x]; l++) { const uint8_t *b = g_ulines[x][l]; int k, st; uint64_t mot, rh; float mf, px, py, dmg;
            memcpy(&k, b, 4); memcpy(&st, b + 4, 4); memcpy(&mot, b + 8, 8); memcpy(&mf, b + 16, 4); memcpy(&px, b + 20, 4); memcpy(&py, b + 24, 4); memcpy(&dmg, b + 28, 4); memcpy(&rh, b + 32, 8);
            fprintf(g_hlog, " | e%d st %x mot %llx mf %.3f x %.4f y %.4f dmg %.3f rng %016llx", k, st, (unsigned long long)mot, mf, px, py, dmg, (unsigned long long)rh); }
        fputc('\n', g_hlog);
    }
}
/* no rollback pending: every frame up to min(cur, newest remote pad) is final */
void rbn_finalize(long long cur) {
    long long upto = rb_input_remote_latest(g_remote);
    if (upto > cur) upto = cur;
    for (long long f = g_hfinal_upto + 1; f <= upto; f++) {
        if (g_hlog && g_hframe[f % HR] == f) fprintf(g_hlog, "f %lld h %016llx\n", f, (unsigned long long)g_hash[f % HR]);
        detail_frame(f);
        rv_final(f);
        g_hfinal_upto = f;
    }
    if (g_hlog && (upto & 63) == 0) fflush(g_hlog);
    if (g_peer_check_f > 0 && g_peer_check_f <= g_hfinal_upto) { long long cf = g_peer_check_f; g_peer_check_f = 0; on_check(cf, g_peer_check); }
}

/* ---------------------------------------------------------------- time sync: extra wait per frame (ms) */
static double g_tsbias = -1;
double rbn_pace_extra(long long f) {
    if (!g_started || g_loop) return 0;
    if (f % 30 == 0) {
        rb_ts_decide((int)f);
        double est_ms = rb_ts_offset() / 1000.0;
        /* (hs14) delay-based pacing (one game without the ring): that game aims at running half a frame BEHIND the other
         * (SSBU_RB_NET_TSBIAS ms, default 8), so the other player's pad of its frame is there before it needs it (a stall there
         * is a hitch); the game with the ring aims at the same split from its side */
        if (g_tsbias < 0) { const char *e = getenv("SSBU_RB_NET_TSBIAS"); g_tsbias = e ? atof(e) : 8.0; }
        if (g_peerdelay && g_lockstep) est_ms += g_tsbias; else if (peer_lockstep()) est_ms -= g_tsbias;
        /* ahead: half of the lead given back over the next 30 frames (the other game, behind, does nothing; both halves meet) */
        if (est_ms > 2.0) { g_ts_extra = est_ms / 2.0 / 30.0; if (g_ts_extra > 4.0) g_ts_extra = 4.0; g_ts_left = 30; g_ts_n++; }
        else g_ts_left = 0;
    }
    if (g_ts_left > 0) { g_ts_left--; return g_ts_extra; }
    return 0;
}
/* (0.2.47) the ping the player sees: the median of the last 5 round trips, not the last one alone (one late packet showed as
 * a spike on the overlay). SSBU_PING_SMOOTH=0: the last round trip, as before. The input delay and the time sync keep using the
 * raw samples. */
int rbn_rtt_ms(void) {
    static int smooth = -1; if (smooth < 0) { const char *e = getenv("SSBU_PING_SMOOTH"); smooth = !(e && *e == '0'); }
    if (!smooth || g_rtt_hist_n < 3) return g_rtt_us > 0 ? (int)(g_rtt_us / 1000) : -1;
    long v[5]; int n = g_rtt_hist_n < 5 ? (int)g_rtt_hist_n : 5;
    for (int i = 0; i < n; i++) v[i] = g_rtt_hist[i];
    for (int i = 1; i < n; i++) for (int j = i; j > 0 && v[j] < v[j - 1]; j--) { long t = v[j]; v[j] = v[j - 1]; v[j - 1] = t; }
    return (int)(v[n / 2] / 1000);
}
void rbn_resend(void) { if (g_started) send_pads(); }
/* the match-start barrier: an empty PADS message says "in the match" (count 0, newest 0) */
void rbn_hello(void) {
    if (!g_started || g_loop) return;
    { static int n; if (rbn_handshake_wanted() && n++ < 20 && (n % 4) == 0) hs_block_send(); }   /* (the block check again, a few times) */
    if (rb_input_local_latest() >= 1) { send_pads(); return; }
    RbMsg m; memset(&m, 0, sizeof m); m.type = RB_MSG_PADS; m.player = g_local; m.newest = 0; m.count = 0; send_msg(&m);
}
int rbn_peer_started(void) { return g_loop || g_peer_started; }
void rbn_status(char *out, size_t n) {
    LONG q = (g_rq_head - g_rq_tail + RQ_N) % RQ_N;
    snprintf(out, n, "remote pads up to %d, local up to %d, the other game has ours up to %lld; packets sent %lld received %lld (bad %lld, queue %ld, full %lld)",
             rb_input_remote_latest(g_remote), rb_input_local_latest(), g_peer_ack, (long long)g_tx_pkts, (long long)g_rx_pkts, (long long)g_rx_bad, (long)q, (long long)g_rx_drop);
}
double rbn_offset_ms(void) { return rb_ts_offset() / 1000.0; }

/* (hs12) the end of a match (rb_rollback.c, the first pass after the fight is off): everything of the session that
 * belongs to one match starts over (input queues and frames at the next rbn_start, checksums, counters, the block check,
 * received messages not drained yet); the round trip, the pings and the transport stay. */
void rbn_match_reset(void) {
    if (g_started) InterlockedExchange(&g_clk_match, 0);      /* (only a rollback match's end: a room's waiting-stage fight ends AFTER the room match's agreement, run r8_lvab2) */
    g_started = 0; g_peer_started = 0; g_f0 = 0; InterlockedExchange(&g_peer_mode, -1); g_peer_ack = 0; g_sent_newest = 0;
    g_stat_pred = g_stat_padget = g_stat_mis = 0; g_ts_n = 0; g_ts_extra = 0; g_ts_left = 0;
    g_hacc = 0xcbf29ce484222325ull; g_hlines = 0; g_lacc_n = 0;
    memset(g_hash, 0, sizeof g_hash); memset(g_hframe, 0, sizeof g_hframe); g_hfinal_upto = 0;
    g_desync = 0; g_check_ok = 0; g_desync_first = -1; g_peer_check_f = 0; g_peer_check = 0; g_agree_f = 0; g_agree_ck = 0;
    memset(g_acc_n, 0, sizeof g_acc_n); memset(g_acc_var, 0, sizeof g_acc_var); memset(g_un, 0, sizeof g_un); memset(g_unl, 0, sizeof g_unl);
    g_padcheck_bad = 0; g_padcheck_first = -1;
    AcquireSRWLockExclusive(&g_hs_lock); g_hs_mine_ok = 0; g_hs_blk_done = 0; memset(g_hs_peer_have, 0, sizeof g_hs_peer_have); ReleaseSRWLockExclusive(&g_hs_lock);
    AcquireSRWLockExclusive(&g_rq_lock); g_rq_tail = g_rq_head; g_tsq_n = 0; ReleaseSRWLockExclusive(&g_rq_lock);
    InterlockedExchange64(&g_cur, 0);
    if (g_hs_cancelled) menu_notice(NULL);
    InterlockedExchange(&g_hs_cancelled, 0);
    printf("[rbnet] match %d over: the session is ready for the next match (pads of an older match dropped so far: %lld)\n", g_match_n, (long long)g_rx_oldmatch);
    fflush(stdout);
}
int rbn_match_number(void) { return g_match_n; }
/* (0.2.53, online_elo.c: the RESULT of a ranked Online match) the last final frame and its checksum (as PADS sends it), the newest frame
 * whose checksums both games had equal, the desyncs, the agreed match's two ui_chara and stage */
void rbn_result_info(uint32_t *fin_f, uint32_t *fin_ck, uint32_t *agree_f, uint32_t *agree_ck, int *desyncs, uint64_t chara[2], uint64_t *stage) {
    long long cf = g_hfinal_upto;
    *fin_f = (uint32_t)cf; *fin_ck = cf > 0 && g_hframe[cf % HR] == cf ? (uint32_t)(g_hash[cf % HR] ^ (g_hash[cf % HR] >> 32)) : 0;
    *agree_f = (uint32_t)g_agree_f; *agree_ck = g_agree_ck; *desyncs = (int)g_desync;
    AcquireSRWLockShared(&g_hs_lock);
    if (g_hs_have_prop) { chara[0] = g_hs_prop.m.chara[0]; chara[1] = g_hs_prop.m.chara[1]; memcpy(stage, g_hs_prop.m.stage, 8); }
    ReleaseSRWLockShared(&g_hs_lock);
}
void rbn_report(FILE *o, long long frames) {
    if (g_on <= 0) return;
    if (g_hlog) fflush(g_hlog);
    fprintf(o, "[rbnet] report (match %d, id %08x): %lld frames; delay %d; round trip mean %.1f ms (best %.1f, %lld samples); clock offset estimate %.1f ms, time-sync corrections %lld\n",
            g_match_n, g_match_id, frames, g_delay, g_rtt_n ? g_rtt_sum / g_rtt_n / 1000.0 : -1.0, g_rtt_min_us > 0 ? g_rtt_min_us / 1000.0 : -1.0, g_rtt_n, rb_ts_offset() / 1000.0, g_ts_n);
    fprintf(o, "[rbnet] report: pads given %lld, predicted %lld (%.1f%%), mispredictions %lld; finalized frames %lld; checksums compared %lld, DESYNCS %lld (first %lld)\n",
            g_stat_padget, g_stat_pred, g_stat_padget ? 100.0 * g_stat_pred / g_stat_padget : 0.0, g_stat_mis, g_hfinal_upto, g_check_ok + g_desync, g_desync, g_desync_first);
    fprintf(o, "[rbnet] report: pad check of the final simulations: %lld pad values differ from the final ones (first frame %lld)\n", g_padcheck_bad, g_padcheck_first);
    fprintf(o, "[rbnet] report: packets sent %lld (failed %lld), received %lld (bad %lld, queue full %lld); remote pads up to %d, local up to %d\n",
            (long long)g_tx_pkts, (long long)g_tx_fail, (long long)g_rx_pkts, (long long)g_rx_bad, (long long)g_rx_drop, rb_input_remote_latest(g_remote), rb_input_local_latest());
    fflush(o);
}
