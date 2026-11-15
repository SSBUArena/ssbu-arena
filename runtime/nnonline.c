/* SSBU_ONLINE_NEX=1 (default 0; online-mm "strada A", TEST ONLY until the user decides): the console answers of a machine that IS
 * online, for the game's own Online mode (its login: the online session object [[[0x71052c3c58] + 8] + 0x80], states 2 network
 * request, 3 network service account, 4 user, 5 game server login; MATCHMAKING_ONLINE_PIANO_2026-11-15.md, phase A1).
 * Without the variable nothing here is registered and the offline answers of nnfs.c / nnmisc.c / nnsvc_c5a.inc stay.
 *
 * Registered after every other import table (boot.c), so these replace the offline handlers:
 *  - nn::nifm: the network is available, a request is never on hold; the current address is 127.0.0.1 (the socket layer of
 *    nnnet_c4a.inc reaches the loopback network only: the game server of a local test listens there).
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

void account_uid(uint64_t *lo, uint64_t *hi);              /* nnfs.c */
void os_init_event(uint64_t key, int autoclear);           /* os_threads.c */
void os_signal_event(uint64_t key);
static uint64_t nsa_id(void) { uint64_t lo, hi; account_uid(&lo, &hi); uint64_t id = lo ^ hi; return id ? id : 1; }

/* ---------------------------------------------------------------- nn::nifm */
static void on_true(Ctx *c) { RET(1); }
static void on_false(Ctx *c) { RET(0); }
static void on_keep(Ctx *c) { (void)c; }
static void nifm_PrimaryIp(Ctx *c) { if (A0) wr32(A0, 0x0100007fu); RET(0); }                 /* (in_addr *) 127.0.0.1 */
static void nifm_IpConfig(Ctx *c) {                                                            /* (ip, mask, gateway, dns1, dns2) */
    if (c->x[0]) wr32(c->x[0], 0x0100007fu);
    if (c->x[1]) wr32(c->x[1], 0x000000ffu);
    if (c->x[2]) wr32(c->x[2], 0x0100007fu);
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
    {NI("20SubmitNetworkRequestEv"), on_keep}, {NI("27SubmitNetworkRequestAndWaitEv"), on_keep}, {NI("20CancelNetworkRequestEv"), on_keep},
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
static void pia_resolve(Ctx *c, void *u) {
    (void)u; char name[128];
    if (nintendo_name(c->x[0], name, sizeof name)) {
        printf("online: Pia resolves \"%s\" -> the game server\n", name); fflush(stdout);
        if (c->x[4]) { for (int i = 0; i < 16; i++) wr8(c->x[4] + 8 + (uint64_t)i, 0); wr32(c->x[4] + 8, g_nex_ip); }
        RET(1);
        return;
    }
    g_pia_orig(c);
}
void online_nex_hooks(void) {
    if (!online_nex_on()) return;
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
    if (e && *e) { unsigned a, b, d, f, pp; if (sscanf(e, "%u.%u.%u.%u:%u", &a, &b, &d, &f, &pp) == 5) { rip = a << 24 | b << 16 | d << 8 | f; rport = (uint16_t)(pp + (port - g_nex_port)); } }
    SOCKET s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
    if (s == INVALID_SOCKET) return -1;
    struct sockaddr_in sa; memset(&sa, 0, sizeof sa);
    sa.sin_family = AF_INET; sa.sin_port = htons(rport); sa.sin_addr.s_addr = htonl(rip);
    int r = connect(s, (struct sockaddr *)&sa, sizeof sa);       /* (blocking: a test server on this PC answers at once; the lobby
                                                                   * will carry the product's, with its own time limit) */
    printf("online: NEX stream (game fd %d) to %u.%u.%u.%u:%u: %s\n", fd, rip >> 24, (rip >> 16) & 255, (rip >> 8) & 255, rip & 255, rport, r == 0 ? "connected" : "FAILED");
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
