/* SSBU_ONLINE_NEX=1 (default 0; online-mm "strada A", TEST ONLY until the user decides): the console answers of a machine that IS
 * online, for the game's own Online mode (its login: the online session object [[[0x71052c3c58] + 8] + 0x80], states 2 network
 * request, 3 network service account, 4 user, 5 game server login; MATCHMAKING_ONLINE_PIANO_2026-11-15.md, phase A1).
 * Without the variable nothing here is registered and the offline answers of nnfs.c / nnmisc.c / nnsvc_c5a.inc stay.
 *
 * Registered after every other import table (boot.c), so these replace the offline handlers:
 *  - nn::nifm: the network is available, a request is never on hold (one waits up to 5 s for the lobby); the current address is
 *    this player's address on the lobby's virtual network (10.13.x.y, mask 255.255.0.0, gateway the lobby's 10.13.0.1), never
 *    a real one; 127.0.0.1 only before the lobby answered.
 *  - nn::account: the user has a network service account (id = the two halves of the user id xor'ed, never 0), the id token
 *    cache is filled at once ("arena2.<the Arena lobby ticket>", or without one "arena1.<account id hex>.<name>"), the cached
 *    Nintendo account info has a birthday, a gender and the country SSBU_ONLINE_COUNTRY (two letters, default US): the
 *    SDK's getters read birthday @0 (16 bytes), gender @0x10 (8), country @0x20 (4) ([sdk] 0x4a7f0 / 0x4a830 / 0x4a870).
 *  - nn::account::AsyncContext: a context given to EnsureNetworkServiceAccountIdTokenCacheAsync holds a finished request
 *    (a marker at +0x18, where the SDK keeps the request pointer); HasDone true, GetResult 0, its event signalled. The other
 *    context functions are nnsvc_c5a.inc's (they stop the game on a request pointer they do not know, so the marker is taken
 *    off before they run).
 * The game server address (NEX's own login, a direct address instead of Nintendo's name over TLS) is set by the hooks of
 * online_nex_hooks() (competitive.c installs them with the other game hooks). */
#include <winsock2.h>
#include <ws2tcpip.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <windows.h>
#include "runtime.h"
#include "hostrt.h"

#define A0 (c->x[0])
#define A1 (c->x[1])
#define A2 (c->x[2])
#define RET(v) (c->x[0] = (uint64_t)(v))

#define ACC_NO_INTERNET 0x767cu                     /* 124-0059 (nnsvc_c5a.inc) */
#define ONLINE_ASYNC_DONE 0x0000454e494c4e4full     /* "ONLINE": the finished request's marker at +0x18 */

int online_nex_on(void) {
    static int v = -1;
    if (v < 0) { const char *e = getenv("SSBU_ONLINE_NEX"); v = e && e[0] == '1'; }
    return v;
}
/* SSBU_ONLINE_NEX_LOBBY=1 (default 0: the test path of A1/A2, a game server on this PC at 127.0.0.1): the product path. The game
 * server (arena-server rb_nex) runs beside the lobby: NEX is told the lobby's own virtual address 10.13.0.1:60000, its stream
 * goes to the lobby server's real host (net_relay.c net_lobby_real) on TCP SSBU_ONLINE_NEX_TCP (default 60000; the secure server
 * the next port), and Pia's station port note goes as a datagram to 10.13.0.1 through the lobby, which stamps the sender's
 * virtual address on it and hands it to the game server (rb_lobby.c nex_relay). */
static int online_nex_lobby(void) {
    static int v = -1;
    if (v < 0) { const char *e = getenv("SSBU_ONLINE_NEX_LOBBY"); v = online_nex_on() && e && e[0] == '1'; }
    return v;
}
/* (online-mm A3) SSBU_ONLINE_RB=1 (default 0, with SSBU_ONLINE_NEX=1): the game's own Online match (melee mode 14) played with OUR
 * rollback: the rollback session's messages go to the other station of the gathering (online_rb_peer: net_relay.c net_rb_send), the
 * match set-up agrees with that game (rb_net.c rbn_handshake, debug_mode.c), every player a local one for the fight, the game's own
 * records back for its results. A game whose opponent has it off is never heard there: the game's own netcode plays, as before. */
int online_rb_on(void) {
    static int v = -1;
    if (v < 0) { const char *e = getenv("SSBU_ONLINE_RB"); v = online_nex_on() && e && e[0] == '1'; }
    return v;
}

void account_uid(uint64_t *lo, uint64_t *hi);              /* nnfs.c */
void os_init_event(uint64_t key, int autoclear);           /* os_threads.c */
void os_signal_event(uint64_t key);
static uint64_t nsa_id(void) {
    uint64_t lo, hi; account_uid(&lo, &hi);
    /* (a build without RECOMP_LDN_ONLINE keeps the SDK's fixed user id: every game would be the same account; tests of two
     * games on one PC set SSBU_ACCOUNT_UID, taken here too) */
    const char *e = getenv("SSBU_ACCOUNT_UID");
    if (e && strlen(e) >= 32) { char h[17], l[17]; memcpy(h, e, 16); h[16] = 0; memcpy(l, e + 16, 16); l[16] = 0; hi = _strtoui64(h, NULL, 16); lo = _strtoui64(l, NULL, 16); }
    uint64_t id = lo * 0x9e3779b97f4a7c15ull ^ hi;
    return id ? id : 1;
}

/* ---------------------------------------------------------------- nn::nifm */
static void on_true(Ctx *c) { RET(1); }
static void on_false(Ctx *c) { RET(0); }
static void on_keep(Ctx *c) { (void)c; }
/* (A2) the console's address is this player's address on the lobby's virtual network (net_relay.c), never a real one: Pia
 * puts it in the station URL the game server hands to the other player, and compares it with what the NAT check saw
 * (online_ncs_answer below: the same address, so "no NAT"). 127.0.0.1 only before the lobby answered (a test without one). */
uint32_t net_my_ip(void);                                   /* net_relay.c */
#define VNET_GATEWAY_IP 0x0A0D0001u                         /* (net_relay.h) */
static uint32_t my_vip(void) { uint32_t ip = net_my_ip(); return ip ? ip : 0x7f000001u; }
static uint32_t be32(uint32_t v) { return (v >> 24) | ((v >> 8) & 0xff00u) | ((v << 8) & 0xff0000u) | (v << 24); }
static void on_wait_lobby(Ctx *c) { (void)c; for (int i = 0; i < 50 && !net_my_ip(); i++) Sleep(100); }
static void nifm_PrimaryIp(Ctx *c) { if (A0) wr32(A0, be32(my_vip())); RET(0); }              /* (in_addr *, network order) */
static void nifm_IpConfig(Ctx *c) {                                                            /* (ip, mask, gateway, dns1, dns2) */
    int v = net_my_ip() != 0;
    if (c->x[0]) wr32(c->x[0], be32(my_vip()));
    if (c->x[1]) wr32(c->x[1], be32(v ? 0xffff0000u : 0xff000000u));
    if (c->x[2]) wr32(c->x[2], be32(v ? VNET_GATEWAY_IP : 0x7f000001u));
    if (c->x[3]) wr32(c->x[3], 0);
    if (c->x[4]) wr32(c->x[4], 0);
    RET(0);
}

/* ---------------------------------------------------------------- nn::account */
static void acc_out_true(Ctx *c) { wr8(A0, 1); RET(0); }
static void acc_ok(Ctx *c) { RET(0); }
static void acc_GetNsaId(Ctx *c) { wr64(A0, nsa_id()); RET(0); }                               /* (NetworkServiceAccountId *, handle) */
static void acc_LoadCachedInfo(Ctx *c) {                                                      /* (CachedNintendoAccountInfo *, handle, work, size) */
    const char *cc = getenv("SSBU_ONLINE_COUNTRY");
    uint8_t buf[0x24]; memset(buf, 0, sizeof buf);
    memcpy(buf, "1990-01-01", 10);
    memcpy(buf + 0x10, "unknown", 7);
    memcpy(buf + 0x20, cc && strlen(cc) == 2 ? cc : "US", 2);
    for (unsigned i = 0; i < sizeof buf; i++) wr8(A0 + i, buf[i]);
    RET(0);
}
int net_lobby_ticket(char *out, size_t n);                  /* net_relay.c */
static void acc_LoadIdToken(Ctx *c) {                                                         /* (u64 *size, char *buf, u64 capacity, handle) */
    char name[64] = "Player"; const char *e = getenv("SSBU_ONLINE_NAME"); if (e && *e) snprintf(name, sizeof name, "%s", e);
    /* the game server's login (rb_nex.c): "arena2.<the SSBU Arena lobby ticket>", which it checks as the lobby does; without a
     * ticket (tests on this PC) "arena1.<account id>.<name>", which only a server started with --test-tokens takes */
    char tok[400], tk[330]; int n;
    if (net_lobby_ticket(tk, sizeof tk) > 0) n = snprintf(tok, sizeof tok, "arena2.%s", tk);
    else n = snprintf(tok, sizeof tok, "arena1.%016llx.%s", (unsigned long long)nsa_id(), name);
    if (n < 0 || (uint64_t)n + 1 > c->x[2]) { RET(ACC_NO_INTERNET); return; }
    for (int i = 0; i <= n; i++) wr8(A1 + (uint64_t)i, (uint8_t)tok[i]);
    wr64(A0, (uint64_t)n);
    RET(0);
}
static void acc_EnsureIdTokenAsync(Ctx *c) { wr64(A0 + 0x18, ONLINE_ASYNC_DONE); RET(0); }    /* (AsyncContext *, handle) */
static ImportFn g_async_dtor, g_async_move_assign;
static void async_HasDone(Ctx *c) { wr8(A1, 1); RET(0); }                                     /* (this, bool *) */
static void async_GetResult(Ctx *c) { RET(rd64(A0 + 0x18) == ONLINE_ASYNC_DONE ? 0 : ACC_NO_INTERNET); }
static void async_Cancel(Ctx *c) { RET(0); }
static void async_GetSystemEvent(Ctx *c) { os_init_event(A1, 0); os_signal_event(A1); RET(0); }
static void async_dtor(Ctx *c) {
    if (rd64(A0 + 0x18) == ONLINE_ASYNC_DONE) wr64(A0 + 0x18, 0);
    if (g_async_dtor) g_async_dtor(c); else RET(A0);
}
static void async_move_assign(Ctx *c) {
    if (rd64(A0 + 0x18) == ONLINE_ASYNC_DONE) wr64(A0 + 0x18, 0);
    if (g_async_move_assign) g_async_move_assign(c); else RET(A0);
}

#define NI(n) "_ZN2nn4nifm" n
#define AC(n) "_ZN2nn7account" n
static const ImportDef online_imports[] = {
    {NI("18IsNetworkAvailableEv"), on_true}, {NI("22IsNetworkRequestOnHoldEv"), on_false},
    {NI("20SubmitNetworkRequestEv"), on_keep}, {NI("27SubmitNetworkRequestAndWaitEv"), on_wait_lobby}, {NI("20CancelNetworkRequestEv"), on_keep},
    {NI("31HandleNetworkRequestErrorResultEv"), on_false},
    {NI("26GetCurrentPrimaryIpAddressEP7in_addr"), nifm_PrimaryIp},
    {NI("22GetCurrentIpConfigInfoEP7in_addrS2_S2_S2_S2_"), nifm_IpConfig},
    {AC("32IsNetworkServiceAccountAvailableEPbRKNS0_10UserHandleE"), acc_out_true},
    {AC("36EnsureNetworkServiceAccountAvailableERKNS0_10UserHandleE"), acc_ok},
    {AC("26GetNetworkServiceAccountIdEPNS0_23NetworkServiceAccountIdERKNS0_10UserHandleE"), acc_GetNsaId},
    {AC("29LoadCachedNintendoAccountInfoEPNS0_25CachedNintendoAccountInfoERKNS0_10UserHandleEPvm"), acc_LoadCachedInfo},
    {AC("37LoadNetworkServiceAccountIdTokenCacheEPmPcmRKNS0_10UserHandleE"), acc_LoadIdToken},
    {AC("44EnsureNetworkServiceAccountIdTokenCacheAsyncEPNS0_12AsyncContextERKNS0_10UserHandleE"), acc_EnsureIdTokenAsync},
    {AC("12AsyncContext7HasDoneEPb"), async_HasDone}, {AC("12AsyncContext9GetResultEv"), async_GetResult},
    {AC("12AsyncContext6CancelEv"), async_Cancel}, {AC("12AsyncContext14GetSystemEventEPNS_2os11SystemEventE"), async_GetSystemEvent},
    {AC("12AsyncContextD1Ev"), async_dtor}, {AC("12AsyncContextaSEOS1_"), async_move_assign},
};

void register_online_imports(void) {
    if (!online_nex_on()) return;
    g_async_dtor = import_lookup(AC("12AsyncContextD1Ev"));
    g_async_move_assign = import_lookup(AC("12AsyncContextaSEOS1_"));
    register_imports(online_imports, (int)(sizeof online_imports / sizeof online_imports[0]));
    printf("online: SSBU_ONLINE_NEX=1: the console answers of an online machine (network, network service account, id token) for the game's own Online mode (test)\n");
    fflush(stdout);
}

/* ---------------------------------------------------------------- NEX: the game server's address
 * (the addresses and offsets are those OpenSmash found for 13.0.5; the code is the same in 13.0.4: same addresses, same masked
 * signatures of the first 16 instructions.)
 *  - 0x7100249190 NEX's login job, the step that works out the game server's address (x0 the job, its NgsFacade at +360): the
 *    facade has a direct address that no retail code sets (host: nn::nex::String at +576 {vtable; local[16]; char *data @+24;
 *    capacity @+32}, port u16 at +616); set, NEX opens its WebSocket WITHOUT TLS to that address.
 *  - 0x71001becd0 NEX's InetAddress from a name (x0 the address, x1 the name -> bool; IPv4 at +12, network order): Nintendo's
 *    names (*nintendo.net) are the game server.
 *  - 0x71000ba850 Pia's name resolution (x0 the name, x4 a Pia InetAddress, IPv4 at +8 host order -> bool): the NAT check servers.
 * SSBU_ONLINE_NEX_SERVER=a.b.c.d:port (default 127.0.0.1:60000, a local test server). */
typedef void (*HookFn)(Ctx *, void *);
int hook_install(uint64_t addr, HookFn repl, void *user, ImportFn *orig);      /* fighter_sdk\hook_host.c */
static char g_nex_host[64] = "127.0.0.1"; static uint16_t g_nex_port = 60000; static uint32_t g_nex_ip = 0x7f000001u;
static ImportFn g_login_orig, g_inet_orig, g_pia_orig;
static int nintendo_name(uint64_t p, char *out, size_t n) {
    if (!p) return 0;
    size_t k = 0; for (; k + 1 < n; k++) { char ch = (char)rd8(p + k); if (!ch) break; out[k] = ch; } out[k] = 0;
    return strstr(out, "nintendo.net") != NULL;
}
static void nex_login_job(Ctx *c, void *u) {
    (void)u;
    uint64_t facade = rd64(c->x[0] + 360);
    if (facade) {
        uint64_t str = facade + 576, data = rd64(str + 24), cap = rd64(str + 32);
        size_t len = strlen(g_nex_host);
        if (data && cap > len) {
            for (size_t i = 0; i <= len; i++) wr8(data + i, (uint8_t)g_nex_host[i]);
            wr16(facade + 616, g_nex_port);
            printf("online: NEX login to %s:%u (direct address, no TLS)\n", g_nex_host, g_nex_port);
        } else printf("online: NEX server address left as the game's (no room for \"%s\")\n", g_nex_host);
        fflush(stdout);
    }
    g_login_orig(c);
}
static void nex_inet_from_name(Ctx *c, void *u) {
    (void)u; char name[128];
    if (nintendo_name(c->x[1], name, sizeof name)) {
        printf("online: NEX resolves \"%s\" -> the game server\n", name); fflush(stdout);
        { uint32_t ip = g_nex_ip; wr32(c->x[0] + 12, (ip >> 24) | ((ip >> 8) & 0xff00u) | ((ip << 8) & 0xff0000u) | (ip << 24)); }
        RET(1);
        return;
    }
    g_inet_orig(c);
}
/* (A2) Pia's names: the NAT check servers nncs1 / nncs2 are two addresses of the virtual network that no player ever gets
 * (the lobby's own 10.13.0.1 and 10.13.255.254; Pia wants them different) and that nnldn.c answers itself
 * (online_ncs_answer); any other Nintendo name Pia asks for is the game server, as before. */
#define NCS1_IP 0x0A0D0001u
#define NCS2_IP 0x0A0DFFFEu
static void pia_resolve(Ctx *c, void *u) {
    (void)u; char name[128];
    if (nintendo_name(c->x[0], name, sizeof name)) {
        uint32_t ip = !strncmp(name, "nncs1", 5) ? NCS1_IP : !strncmp(name, "nncs2", 5) ? NCS2_IP : g_nex_ip;
        printf("online: Pia resolves \"%s\" -> %s\n", name, ip == NCS1_IP || ip == NCS2_IP ? "the NAT check (answered in the game)" : "the game server"); fflush(stdout);
        if (c->x[4]) { for (int i = 0; i < 16; i++) wr8(c->x[4] + 8 + (uint64_t)i, 0); wr32(c->x[4] + 8, ip); }
        RET(1);
        return;
    }
    g_pia_orig(c);
}

/* (A2) Pia's NAT check (nn::pia::nex::NatPropertyDetecter), answered by nnldn.c's sendto without anything leaving the game:
 * on the virtual network there is no NAT, so the answer is fixed. Requests: 16 bytes, a big-endian u32 kind (101 and 102 to
 * nncs1, 103 to nncs2, ports 10025 / 10125) and zeros. Answer: the kind, the port and the address the request came from (this
 * player's virtual address and the socket's port: "no NAT"), a u32 0, all big-endian; it comes from where the request went,
 * except 102's, which comes from the other port (the test of the NAT's filtering). Anything else sent to the two addresses
 * (Pia's keep-alive to nncs1) is taken and dropped.
 * Returns -1: not for the NAT check; 0: taken, no answer; 1: answer in out[16], from port *from_port of dst. */
static volatile LONG g_ncs_said;
static void pia_port_note(uint32_t me, uint16_t port);
int online_ncs_answer(uint32_t dst, uint16_t dport, uint32_t me, uint16_t sport, const uint8_t *data, uint32_t len, uint8_t out[16], uint16_t *from_port) {
    if (!online_nex_on() || (dst != NCS1_IP && dst != NCS2_IP)) return -1;
    /* Pia's keep-alive to nncs1 (port 33334, the Pia packet magic 32 ab 98 64): its station socket's port, which the game server
     * puts in this player's station URL for the other player (pia_port_note) */
    if (dst == NCS1_IP && (dport == 33334 || dport == 33335) && len >= 4 && data[0] == 0x32 && data[1] == 0xab && data[2] == 0x98 && data[3] == 0x64) {
        pia_port_note(me, sport);
        return 0;
    }
    if (len < 16 || (dport != 10025 && dport != 10125)) {
        if (InterlockedIncrement(&g_ncs_said) <= 12) { printf("online: %u bytes to the NAT check's port %u dropped\n", len, dport); fflush(stdout); }
        return 0;
    }
    uint32_t kind = (uint32_t)data[0] << 24 | (uint32_t)data[1] << 16 | (uint32_t)data[2] << 8 | data[3];
    if (kind < 101 || kind > 103) return 0;
    uint16_t from = dport;
    if (kind == 102) from = dport == 10025 ? 10125 : 10025;
    uint32_t v[4] = {kind, sport, me, 0};
    for (int i = 0; i < 4; i++) { out[4 * i] = (uint8_t)(v[i] >> 24); out[4 * i + 1] = (uint8_t)(v[i] >> 16); out[4 * i + 2] = (uint8_t)(v[i] >> 8); out[4 * i + 3] = (uint8_t)v[i]; }
    *from_port = from;
    if (InterlockedIncrement(&g_ncs_said) <= 12) { printf("online: Pia NAT check %u answered (no NAT: the virtual address)\n", kind); fflush(stdout); }
    return 1;
}
/* (online-mm A3 prep) the other player of the gathering: the station Pia sends to on the virtual network (never the lobby's
 * 10.13.0.1 / the NAT check's 10.13.255.254). nnldn.c tells every unicast datagram's destination; 0 = none yet. For A3's
 * rollback handshake with the peer of the game's own Online match (rb_net.c). */
static volatile LONG g_online_peer;
void online_peer_seen(uint32_t ip) {
    if (!online_nex_on() || ip == NCS1_IP || ip == NCS2_IP || (uint32_t)g_online_peer == ip) return;
    InterlockedExchange(&g_online_peer, (LONG)ip);
    printf("online: Pia talks to the station 10.13.%u.%u (the other player of the gathering)\n", (ip >> 8) & 255, ip & 255); fflush(stdout);
}
uint32_t online_peer_vip(void) { return (uint32_t)g_online_peer; }
uint32_t online_rb_peer(void) { return online_rb_on() ? (uint32_t)g_online_peer : 0; }      /* (A3) the rollback messages' destination, 0 = none */
void online_nex_hooks(void) {
    if (!online_nex_on()) return;
    if (online_nex_lobby()) { g_nex_ip = 0x0A0D0001u; snprintf(g_nex_host, sizeof g_nex_host, "10.13.0.1"); }    /* (the lobby's own address) */
    const char *e = getenv("SSBU_ONLINE_NEX_SERVER");
    if (e && *e) {
        unsigned a, b, d, f, port = 60000; char end = 0;
        int k = sscanf(e, "%u.%u.%u.%u:%u%c", &a, &b, &d, &f, &port, &end);
        if (k >= 4 && a < 256 && b < 256 && d < 256 && f < 256 && port && port < 65536) {
            g_nex_ip = a << 24 | b << 16 | d << 8 | f; g_nex_port = (uint16_t)port;
            snprintf(g_nex_host, sizeof g_nex_host, "%u.%u.%u.%u", a, b, d, f);
        } else printf("online: SSBU_ONLINE_NEX_SERVER=%s is not a.b.c.d:port: %s:%u kept\n", e, g_nex_host, g_nex_port);
    }
    int ok = !!hook_install(GA(0x7100249190ull), nex_login_job, NULL, &g_login_orig);
    ok += !!hook_install(GA(0x71001becd0ull), nex_inet_from_name, NULL, &g_inet_orig);
    ok += !!hook_install(GA(0x71000ba850ull), pia_resolve, NULL, &g_pia_orig);
    printf("online: game server %s:%u (NEX hooks %d of 3)\n", g_nex_host, g_nex_port, ok);
    fflush(stdout);
}

/* ---------------------------------------------------------------- NEX's stream sockets on the virtual network (nnldn.c)
 * With SSBU_ONLINE (the virtual network of Local Wireless over the lobby) nn::socket is nnldn.c's, which refuses every stream:
 * NEX's login (a WebSocket over TCP) then fails at once (probe a1a, 15/11: session state 5 -> -1 in 0.8 s, nothing reached the
 * test server). A stream to the game server's address (g_nex_ip, the NEX port or the next one: the authentication and the secure
 * server) is carried by a host TCP connection here, to SSBU_ONLINE_NEX_REAL=a.b.c.d:port (default: the same address and port,
 * i.e. a test server on this PC; the lobby will carry it in the product). A thread per stream hands what arrives to nnldn.c
 * (deliver(fd, handle, data, n), n <= 0 at the end; nnldn.c drops what comes for a descriptor that no longer has that handle). */
typedef void (*OnlineDeliver)(int, intptr_t, const uint8_t *, int);
typedef struct { SOCKET s; int fd; OnlineDeliver deliver; } OnlineStream;
static DWORD WINAPI online_stream_reader(LPVOID p) {
    OnlineStream *st = (OnlineStream *)p;
    uint8_t buf[16384];
    for (;;) {
        int n = recv(st->s, (char *)buf, (int)sizeof buf, 0);
        st->deliver(st->fd, (intptr_t)st->s, buf, n);
        if (n <= 0) break;
    }
    free(st);
    return 0;
}
int online_stream_wanted(uint32_t ip, uint16_t port) { return online_nex_on() && ip == g_nex_ip && (port == g_nex_port || port == (uint16_t)(g_nex_port + 1)); }
intptr_t online_stream_open(int fd, uint32_t ip, uint16_t port, OnlineDeliver deliver) {
    static int wsa; if (!wsa) { WSADATA w; WSAStartup(MAKEWORD(2, 2), &w); wsa = 1; }
    uint32_t rip = ip; uint16_t rport = port;
    const char *e = getenv("SSBU_ONLINE_NEX_REAL");
    int via_lobby = 0;
    if (online_nex_lobby()) {                                    /* (product path) the game server beside the lobby */
        int net_lobby_real(uint32_t *ip, uint16_t *port);        /* net_relay.c */
        uint16_t lport; unsigned base = 60000;
        const char *t = getenv("SSBU_ONLINE_NEX_TCP"); if (t && atoi(t) > 0 && atoi(t) < 65535) base = (unsigned)atoi(t);
        if (!net_lobby_real(&rip, &lport)) { printf("online: NEX stream (game fd %d): the lobby is not known yet\n", fd); fflush(stdout); return -1; }
        rport = (uint16_t)(base + (port - g_nex_port)); via_lobby = 1;
    } else if (e && *e) { unsigned a, b, d, f, pp; if (sscanf(e, "%u.%u.%u.%u:%u", &a, &b, &d, &f, &pp) == 5) { rip = a << 24 | b << 16 | d << 8 | f; rport = (uint16_t)(pp + (port - g_nex_port)); } }
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return -1;
    struct sockaddr_in sa; memset(&sa, 0, sizeof sa);
    sa.sin_family = AF_INET; sa.sin_port = htons(rport); sa.sin_addr.s_addr = htonl(rip);
    int r;
    if (via_lobby) {                                             /* (a server far away: at most 5 s, then NEX's own error) */
        u_long nb = 1; ioctlsocket(s, FIONBIO, &nb);
        r = connect(s, (struct sockaddr *)&sa, sizeof sa);
        if (r != 0 && WSAGetLastError() == WSAEWOULDBLOCK) {
            fd_set w, x; FD_ZERO(&w); FD_ZERO(&x); FD_SET(s, &w); FD_SET(s, &x); struct timeval tv = {5, 0};
            int err = 0, el = sizeof err;
            r = select(0, NULL, &w, &x, &tv) == 1 && FD_ISSET(s, &w) && !getsockopt(s, SOL_SOCKET, SO_ERROR, (char *)&err, &el) && !err ? 0 : -1;
        }
        nb = 0; ioctlsocket(s, FIONBIO, &nb);
        printf("online: NEX stream (game fd %d) to the lobby's host, TCP %u: %s\n", fd, rport, r == 0 ? "connected" : "FAILED");
    } else {
        r = connect(s, (struct sockaddr *)&sa, sizeof sa);       /* (blocking: a test server on this PC answers at once) */
        printf("online: NEX stream (game fd %d) to %u.%u.%u.%u:%u: %s\n", fd, rip >> 24, (rip >> 16) & 255, (rip >> 8) & 255, rip & 255, rport, r == 0 ? "connected" : "FAILED");
    }
    fflush(stdout);
    if (r != 0) { closesocket(s); return -1; }
#ifdef TCP_NODELAY
    { int one = 1; setsockopt(s, IPPROTO_TCP, TCP_NODELAY, (const char *)&one, sizeof one); }
#endif
    OnlineStream *st = (OnlineStream *)calloc(1, sizeof *st);
    st->s = s; st->fd = fd; st->deliver = deliver;
    CloseHandle(CreateThread(NULL, 0, online_stream_reader, st, 0, NULL));
    return (intptr_t)s;
}
long online_stream_send(intptr_t h, const void *data, uint32_t len) {
    const char *p = (const char *)data; uint32_t left = len;
    while (left) { int n = send((SOCKET)h, p, (int)left, 0); if (n <= 0) return -1; p += n; left -= (uint32_t)n; }
    return (long)len;
}
void online_stream_close(intptr_t h) { shutdown((SOCKET)h, SD_BOTH); closesocket((SOCKET)h); }

/* (A2) Pia's station port for the game server. On Nintendo's network the server learns it from Pia's keep-alive to nncs1 (a UDP
 * datagram from the station socket; NEX itself speaks over a stream, so its URL has no Pia port): the server puts that port in
 * the station URL it hands to the other player of the gathering. Here the keep-alive never leaves the game (online_ncs_answer);
 * instead the game sends the game server a 12-byte note on UDP to the server's address (SSBU_ONLINE_NEX_REAL or
 * SSBU_ONLINE_NEX_SERVER, the authentication port): "RBPP", this player's VIRTUAL address (u32 BE), the port (u16 BE), 0 u16.
 * At most once per 5 s, and when the port changes. TEST PATH (as the NEX stream on the host's TCP): in the product the lobby,
 * which knows each player's virtual address, will carry the keep-alive to the game server. Nothing real is in the note. */
static void pia_port_note(uint32_t me, uint16_t port) {
    static SOCKET s = INVALID_SOCKET; static uint16_t last_port; static uint64_t last_ms; static int said;
    uint64_t now = GetTickCount64();
    if (!me || (port == last_port && now - last_ms < 5000)) return;
    if (online_nex_lobby()) {                                    /* (product path) through the lobby, which stamps who sent it */
        void net_send(uint32_t src, uint16_t sport, uint32_t dst, uint16_t dport, const void *data, uint32_t len);   /* net_relay.c */
        uint8_t m[12] = {'R', 'B', 'P', 'P', (uint8_t)(me >> 24), (uint8_t)(me >> 16), (uint8_t)(me >> 8), (uint8_t)me, (uint8_t)(port >> 8), (uint8_t)port, 0, 0};
        net_send(me, port, g_nex_ip, g_nex_port, m, sizeof m);
        if (port != last_port || said < 2) { printf("online: Pia's station port %u told to the game server (through the lobby)\n", port); fflush(stdout); said++; }
        last_port = port; last_ms = now;
        return;
    }
    static int wsa; if (!wsa) { WSADATA w; WSAStartup(MAKEWORD(2, 2), &w); wsa = 1; }
    if (s == INVALID_SOCKET) s = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
    if (s == INVALID_SOCKET) return;
    uint32_t rip = g_nex_ip; uint16_t rport = g_nex_port;
    const char *e = getenv("SSBU_ONLINE_NEX_REAL");
    if (e && *e) { unsigned a, b, d, f, pp; if (sscanf(e, "%u.%u.%u.%u:%u", &a, &b, &d, &f, &pp) == 5) { rip = a << 24 | b << 16 | d << 8 | f; rport = (uint16_t)pp; } }
    uint8_t m[12] = {'R', 'B', 'P', 'P', (uint8_t)(me >> 24), (uint8_t)(me >> 16), (uint8_t)(me >> 8), (uint8_t)me, (uint8_t)(port >> 8), (uint8_t)port, 0, 0};
    struct sockaddr_in sa; memset(&sa, 0, sizeof sa);
    sa.sin_family = AF_INET; sa.sin_port = htons(rport); sa.sin_addr.s_addr = htonl(rip);
    sendto(s, (const char *)m, (int)sizeof m, 0, (struct sockaddr *)&sa, sizeof sa);
    if (port != last_port || said < 2) { printf("online: Pia's station port %u told to the game server\n", port); fflush(stdout); said++; }
    last_port = port; last_ms = now;
}
