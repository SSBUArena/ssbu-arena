/* SPDX-License-Identifier: GPL-3.0-or-later */
/* SSBU Arena's lobby servers by region (the C / C++ copy of the table; the launcher has the same one in
 * tools/launcher/arena/Regions.cs and tools/launcher/Launcher.ps1: tools/net/test_regions.py checks that the three match).
 * Every lobby is ours, relay only, with the same accounts and results database: one account plays on all of them, but
 * players on different lobbies do not see each other (rooms and matchmaking are per lobby).
 *
 * The setting (graphics.ini, read by the launcher and by the game; SSBU_ONLINE_REGION wins over the file):
 *     online_region = auto | eu | na-east | sa   auto (default): the lobby with the lowest measured round trip
 *     online_region_last = eu | na-east | sa     the last measured pick: what Auto starts with, measured again in the background
 * An explicit server (SSBU_ONLINE=<host:port>, SSBU_ONLINE_SERVER, online_server) always wins: that server, nothing measured.
 * SSBU_ONLINE_REGION_PROBE=0: nothing is measured, Auto = the first region (Europe, as 0.2.50 and older).
 * Only names here, never addresses. */
#ifndef NET_REGION_H
#define NET_REGION_H
#include <string.h>

#define NET_REGION_PORT 39010
#define NA_EAST_HOST "na-east.ssbuarena.com"

typedef struct { const char *id, *name, *host; } NetRegion;
/* REGION TABLE (id, name, host): the first one is the default and the fallback */
static const NetRegion k_net_regions[] = {
    {"eu", "Europe", "eu-germany.ssbuarena.com"},
    {"na-east", "NA East", NA_EAST_HOST},
    {"sa", "South America", "sa-brazil.ssbuarena.com"},      /* (23/11) Sao Paulo */
};
#define NET_REGION_COUNT ((int)(sizeof k_net_regions / sizeof k_net_regions[0]))
#define NET_REGION_KEEP_MS 10          /* Auto keeps its last pick unless another lobby is more than this much closer */
#define NET_REGION_SAMPLES 3           /* probes per lobby, NET_REGION_GAP_MS apart, all lobbies at once */
#define NET_REGION_GAP_MS 60
#define NET_REGION_TIMEOUT_MS 700      /* the whole measure, at most */

static inline int net_region_ieq(const char *a, const char *b) {
    for (; *a && *b; a++, b++) { int x = *a, y = *b; if (x >= 'A' && x <= 'Z') x += 32; if (y >= 'A' && y <= 'Z') y += 32; if (x != y) return 0; }
    return *a == *b;
}
/* "eu" / "na-east" / "sa" -> its place in the table; -1: auto, empty or not a region */
static inline int net_region_by_id(const char *id) {
    for (int i = 0; id && i < NET_REGION_COUNT; i++) if (net_region_ieq(id, k_net_regions[i].id)) return i;
    return -1;
}
/* "host" or "host:port" of one of our lobbies -> its place; -1: another server */
static inline int net_region_by_host(const char *server) {
    for (int i = 0; server && i < NET_REGION_COUNT; i++) {
        size_t l = strlen(k_net_regions[i].host), j = 0;
        for (; j < l; j++) { int x = server[j], y = k_net_regions[i].host[j]; if (x >= 'A' && x <= 'Z') x += 32; if (x != y) break; }
        if (j == l && (!server[l] || server[l] == ':')) return i;
    }
    return -1;
}
/* The pick from one measure: ms[i] = lobby i's median round trip, -1 = no answer; last = the pick before (-1: none).
 * The lowest median wins; the last pick stays while it answers and no lobby is more than NET_REGION_KEEP_MS closer;
 * nobody answered: the last pick, else the first region. */
static inline int net_region_choose(const int *ms, int last) {
    int best = -1;
    for (int i = 0; i < NET_REGION_COUNT; i++) if (ms[i] >= 0 && (best < 0 || ms[i] < ms[best])) best = i;
    if (best < 0) return last >= 0 && last < NET_REGION_COUNT ? last : 0;
    if (last >= 0 && last < NET_REGION_COUNT && ms[last] >= 0 && ms[last] <= ms[best] + NET_REGION_KEEP_MS) return last;
    return best;
}
#endif
