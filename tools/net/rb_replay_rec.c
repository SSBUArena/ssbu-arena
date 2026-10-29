// SPDX-License-Identifier: GPL-3.0-or-later
/* rb_replay_rec.c - SSBU Arena replays, the lobby side (included by rb_lobby.c; on with --replay-dir <dir>).
 *
 * The lobby relays every message of an online rollback match (RB, type 16), so it can record the match without any help
 * from the games: the agreed match (the host's PROPOSE that the joiner ACCEPTs: runtime/rb_hs.h RbhMatch), the host's match
 * block (BLOCK chunks) and both players' final pads (PADS: rb_wire.c, each message repeats the pads its receiver has not
 * acknowledged yet, so every frame passes through here) with the per-frame checksums the games send each other. At the
 * end of the match (no pads for REC_IDLE s, the next handshake of the pair, a player gone) the recording is handed to a
 * writer thread that writes one .ssbr file; the relay loop itself never touches the disk and never allocates (a static
 * pool of REC_SLOTS recordings, each committed by the kernel only as far as it is used).
 *
 * No internet address is ever recorded: the players are their names and account ids (from the lobby ticket).
 *
 * The .ssbr file (version 1, little-endian; runtime/rb_replay.c reads it, tools/net/replay_uploader.py compresses it,
 * the launcher's ReplayStore.cs lists and inflates it):
 *   0x000 "SSBR"  0x004 u16 version (1)  0x006 u16 header size (0x1000)  0x008 u32 flags (1 quick match through the lobby's
 *         search, 2 a Local Wireless room's match, 4 host match block present, 8 the two games' checksums differed live,
 *         0x10 ended normally)
 *   0x00c i16 game build (0 = 13.0.4)  0x00e u8 handshake protocol  0x00f u8 body codec (0 raw, 1 raw deflate)
 *   0x010 u32 match id  0x014 u32 frames N  0x018 i64 unix time of the agreement  0x020 u32 duration ms (first to last pads)
 *   0x024 u32 body size as stored  0x028 u32 body size raw  0x02c u32 first frame whose checksums differed live (0 none)
 *   0x030 u8 winner (0xff unknown)  0x031 u8 end reason (0 pads stopped, 1 a player left, 2 next match, 3 too long, 4 lobby
 *         stopped)  0x032 u16 checksum every n frames (8)  0x034 u32 reserved
 *   0x038 build id (40 bytes, the games' JOIN build string)  0x060 names 2 x 32  0x0a0 account ids 2 x 40
 *   0x0f0 u32 x 2 the newest consecutive frame received from each player
 *   0x100 RbhMatch (0x184 bytes)  0x300 the host's match block (0xa48 bytes)  .. 0x1000 zero
 *   body (raw): for player 0 then 1: u16 buttons[N], i16 lx[N], ly[N], rx[N], ry[N] (frame 1 first); then u32 check[N / 8 + 1]:
 *   check[k] = the checksum of frame 8 k as the games sent it (0 = not seen). */
#include <pthread.h>
#include <semaphore.h>
#include <sys/stat.h>
#include <fcntl.h>

#define REC_SLOTS 24
#define REC_MAXF 40000                              /* frames (11 min); a quick match is 7 min + the countdown */
#define REC_IDLE 3.0                                /* s without pads: the match is over */
#define REC_NOSTART 90.0                            /* s agreed without any pads: dropped */
#define REC_MINF 180                                /* shorter recordings (3 s) are not written */
#define REC_HDR 0x1000
#define REC_CK_EVERY 8
#define RBH_PROTO_REC 5
#define RBH_MATCH_SIZE 388
#define RBH_MSG_SIZE (1 + 1 + 2 + 4 + 4 + 4 + RBH_MATCH_SIZE)
#define RBH_BLK_SIZE 0xa48
enum { REC_FREE = 0, REC_PROPOSED, REC_AGREED, REC_RUNNING, REC_WRITE };
typedef struct { uint16_t b; int16_t s[4]; } RecPad;
typedef struct {
    volatile int state;
    Client *host, *guest; uint64_t hcookie, gcookie;            /* (the clients' slots may be reused: their cookies tell) */
    uint32_t prop_nonce, match_id; uint8_t match[RBH_MATCH_SIZE]; int16_t game_build; uint8_t proto;
    double at, first, last; time_t wall;
    int quick, room, end_reason, winner, have_blk; uint8_t blk[RBH_BLK_SIZE]; uint8_t blk_have[4];
    int tag[2];                                                 /* each sender's PADS tag byte (-1: none yet) */
    int top[2], cons[2], maxf;                                  /* newest frame seen, newest consecutive frame */
    char name[2][NAME_LEN + 1], uid[2][40]; uint8_t build[BUILD_LEN];
    RecPad pad[2][REC_MAXF + 1]; uint8_t have[2][REC_MAXF + 1]; uint32_t ck[2][REC_MAXF + 1];
} Rec;
static Rec g_rec[REC_SLOTS];
static char g_rec_dir[256];
static sem_t g_rec_sem;
static unsigned long long g_rec_written, g_rec_skipped;

static void rec_put16(uint8_t *p, unsigned v) { p[0] = (uint8_t)v; p[1] = (uint8_t)(v >> 8); }
static void rec_put32(uint8_t *p, uint32_t v) { for (int i = 0; i < 4; i++) p[i] = (uint8_t)(v >> (8 * i)); }
static uint32_t rec_get32(const uint8_t *p) { return (uint32_t)p[0] | (uint32_t)p[1] << 8 | (uint32_t)p[2] << 16 | (uint32_t)p[3] << 24; }
static uint16_t rec_get16(const uint8_t *p) { return (uint16_t)(p[0] | p[1] << 8); }

/* ---- the writer thread: a recording in state REC_WRITE -> one file; the slot is free again afterwards */
static int rec_write_all(int fd, const void *p, size_t n) { const uint8_t *b = p; while (n) { ssize_t w = write(fd, b, n); if (w <= 0) { if (errno == EINTR) continue; return 0; } b += w; n -= (size_t)w; } return 1; }
static void rec_write(Rec *r) {
    int n = r->cons[0] < r->cons[1] ? r->cons[0] : r->cons[1];
    if (n > REC_MAXF) n = REC_MAXF;
    if (n < REC_MINF) { g_rec_skipped++; return; }
    size_t nck = (size_t)n / REC_CK_EVERY + 1, raw = (size_t)n * 20 + nck * 4;
    static uint8_t hdr[REC_HDR]; static uint8_t body[REC_MAXF * 20 + (REC_MAXF / REC_CK_EVERY + 1) * 4];
    memset(hdr, 0, sizeof hdr);
    uint32_t desync = 0;
    for (int f = 1; f <= n && !desync; f++) if (r->ck[0][f] && r->ck[1][f] && r->ck[0][f] != r->ck[1][f]) desync = (uint32_t)f;
    memcpy(hdr, "SSBR", 4); rec_put16(hdr + 4, 1); rec_put16(hdr + 6, REC_HDR);
    uint32_t flags = (r->quick ? 1u : 0) | (r->room ? 2u : 0) | (r->have_blk ? 4u : 0) | (desync ? 8u : 0) | (r->end_reason == 0 ? 0x10u : 0);
    rec_put32(hdr + 8, flags); rec_put16(hdr + 0xc, (uint16_t)r->game_build); hdr[0xe] = r->proto; hdr[0xf] = 0;
    rec_put32(hdr + 0x10, r->match_id); rec_put32(hdr + 0x14, (uint32_t)n);
    { uint64_t t = (uint64_t)r->wall; rec_put32(hdr + 0x18, (uint32_t)t); rec_put32(hdr + 0x1c, (uint32_t)(t >> 32)); }
    rec_put32(hdr + 0x20, (uint32_t)((r->last - r->first) * 1000.0)); rec_put32(hdr + 0x24, (uint32_t)raw); rec_put32(hdr + 0x28, (uint32_t)raw);
    rec_put32(hdr + 0x2c, desync); hdr[0x30] = (uint8_t)(r->winner == 0 || r->winner == 1 ? r->winner : 0xff); hdr[0x31] = (uint8_t)r->end_reason; rec_put16(hdr + 0x32, REC_CK_EVERY);
    memcpy(hdr + 0x38, r->build, BUILD_LEN);
    for (int i = 0; i < 2; i++) { memcpy(hdr + 0x60 + 32 * i, r->name[i], strnlen(r->name[i], 31)); memcpy(hdr + 0xa0 + 40 * i, r->uid[i], strnlen(r->uid[i], 39)); rec_put32(hdr + 0xf0 + 4 * i, (uint32_t)r->cons[i]); }
    memcpy(hdr + 0x100, r->match, RBH_MATCH_SIZE);
    if (r->have_blk) memcpy(hdr + 0x300, r->blk, RBH_BLK_SIZE);
    uint8_t *q = body;
    for (int p = 0; p < 2; p++) {
        for (int f = 1; f <= n; f++, q += 2) rec_put16(q, r->pad[p][f].b);
        for (int k = 0; k < 4; k++) for (int f = 1; f <= n; f++, q += 2) rec_put16(q, (uint16_t)r->pad[p][f].s[k]);
    }
    for (size_t k = 0; k < nck; k++, q += 4) { int f = (int)k * REC_CK_EVERY; rec_put32(q, f >= 1 ? (r->ck[0][f] ? r->ck[0][f] : r->ck[1][f]) : 0); }
    struct tm tm; gmtime_r(&r->wall, &tm);
    char name[128], tmp[400], fin[400];
    snprintf(name, sizeof name, "%04d%02d%02d-%02d%02d%02d-%08x", tm.tm_year + 1900, tm.tm_mon + 1, tm.tm_mday, tm.tm_hour, tm.tm_min, tm.tm_sec, r->match_id);
    snprintf(tmp, sizeof tmp, "%s/pending/.%s.tmp", g_rec_dir, name); snprintf(fin, sizeof fin, "%s/pending/%s.ssbr", g_rec_dir, name);
    int fd = open(tmp, O_WRONLY | O_CREAT | O_TRUNC, 0644), ok = fd >= 0;
    if (ok) ok = rec_write_all(fd, hdr, sizeof hdr) && rec_write_all(fd, body, raw);
    if (fd >= 0) { if (ok) fsync(fd); close(fd); }
    if (ok && rename(tmp, fin) == 0) { g_rec_written++; say("replay: match %08x written: %s vs %s, %d frames (%.0f s)%s", r->match_id, r->name[0], r->name[1], n, n / 60.0, desync ? ", the games' checksums differed" : ""); }
    else { unlink(tmp); g_rec_skipped++; say("replay: match %08x NOT written (%s)", r->match_id, strerror(errno)); }
}
static void *rec_writer(void *u) {
    (void)u;
    for (;;) {
        while (sem_wait(&g_rec_sem) && errno == EINTR) {}
        for (int i = 0; i < REC_SLOTS; i++) if (__atomic_load_n(&g_rec[i].state, __ATOMIC_ACQUIRE) == REC_WRITE) {
            rec_write(&g_rec[i]);
            __atomic_store_n(&g_rec[i].state, REC_FREE, __ATOMIC_RELEASE);
        }
    }
    return NULL;
}
static int rec_start(const char *dir) {
    snprintf(g_rec_dir, sizeof g_rec_dir, "%s", dir);
    char p[300]; mkdir(dir, 0755); snprintf(p, sizeof p, "%s/pending", dir); mkdir(p, 0755);
    struct stat st; if (stat(p, &st) || !S_ISDIR(st.st_mode)) { fprintf(stderr, "replays: cannot use %s\n", p); return 0; }
    sem_init(&g_rec_sem, 0, 0);
    pthread_t t; if (pthread_create(&t, NULL, rec_writer, NULL)) { g_rec_dir[0] = 0; return 0; }
    pthread_detach(t);
    return 1;
}

/* ---- the relay thread */
static int rec_alive(Rec *r) { return r->host->used && r->guest->used && r->host->cookie == r->hcookie && r->guest->cookie == r->gcookie; }
static void rec_end(Rec *r, int reason) {                    /* hand over (state REC_RUNNING) or drop (any other) */
    if (r->state == REC_RUNNING) { r->end_reason = reason; __atomic_store_n(&r->state, REC_WRITE, __ATOMIC_RELEASE); sem_post(&g_rec_sem); }
    else if (r->state != REC_WRITE && r->state != REC_FREE) r->state = REC_FREE;
}
static Rec *rec_find(Client *a, Client *b, int either) {      /* the live recording of host a / guest b (either: in any order) */
    for (int i = 0; i < REC_SLOTS; i++) {
        Rec *r = &g_rec[i]; int s = r->state;
        if (s == REC_FREE || s == REC_WRITE) continue;
        if ((r->host == a && r->guest == b) || (either && r->host == b && r->guest == a)) { if (rec_alive(r)) return r; rec_end(r, 1); }
    }
    return NULL;
}
static void rec_pads(Rec *r, int from_host, const uint8_t *p, int n) {
    if (n < 20 + 2 || (n - 20) % 16 != 2) return;
    int player = p[1], count = rec_get16(p + 2), newest = (int)rec_get32(p + 4), cf = (int)rec_get32(p + 12), side = from_host ? 0 : 1;
    if (player != side || count > 64 || n != 20 + 16 * count + 2 || newest - count + 1 < 1) return;
    uint8_t tag = p[n - 2];
    if (r->tag[side] < 0) { if (newest > 64) return; r->tag[side] = tag; }      /* (the tag of this match: from its first frames; a late message of the
                                                                                * session's previous match ends at a high frame and has another tag) */
    else if (r->tag[side] != tag) return;
    if (r->state == REC_AGREED) { r->state = REC_RUNNING; r->first = g_now; }
    r->last = g_now;
    uint32_t ck = rec_get32(p + 16);
    if (cf >= 1 && cf <= REC_MAXF && ck) r->ck[side][cf] = ck;
    for (int k = 0; k < count; k++) {
        int f = newest - k; if (f < 1 || f > REC_MAXF) continue;
        if (r->have[side][f]) continue;
        const uint8_t *q = p + 20 + 16 * k;
        RecPad *d = &r->pad[side][f];
        d->b = rec_get16(q); for (int s = 0; s < 4; s++) d->s[s] = (int16_t)rec_get16(q + 8 + 2 * s);
        r->have[side][f] = 1;
        if (f > r->top[side]) r->top[side] = f;
    }
    while (r->cons[side] < REC_MAXF && r->have[side][r->cons[side] + 1]) r->cons[side]++;
    if (r->top[side] > r->maxf) r->maxf = r->top[side];
    if (r->maxf >= REC_MAXF) rec_end(r, 3);
}
static void rec_clear(Rec *r) {                              /* (only the part a previous recording used) */
    int m = r->maxf > 0 ? r->maxf : 0; if (m > REC_MAXF) m = REC_MAXF;
    for (int p = 0; p < 2; p++) { memset(r->have[p], 0, (size_t)m + 1); memset(r->ck[p], 0, ((size_t)m + 1) * 4); memset(r->pad[p], 0, ((size_t)m + 1) * sizeof(RecPad)); }
    r->maxf = 0; r->top[0] = r->top[1] = r->cons[0] = r->cons[1] = 0; r->tag[0] = r->tag[1] = -1;
    r->have_blk = 0; memset(r->blk_have, 0, sizeof r->blk_have); r->end_reason = 0; r->room = 0; r->winner = -1;
}
/* every RB message c sent to dst, after it was relayed */
static void rec_rb(Client *c, Client *dst, const uint8_t *p, int n) {
    if (!g_rec_dir[0] || !dst || n < 1) return;
    int t = p[0];
    if (t == 1) {                                            /* PADS */
        Rec *r = rec_find(c, dst, 1);
        if (r && (r->state == REC_AGREED || r->state == REC_RUNNING)) rec_pads(r, r->host == c, p, n);
        return;
    }
    if (t == 0x49 && n >= RBH_MSG_SIZE && p[1] == RBH_PROTO_REC) {      /* PROPOSE: host c -> joiner dst */
        uint32_t nonce = rec_get32(p + 8);
        Rec *r = rec_find(c, dst, 1);
        if (r && r->host == c && r->prop_nonce == nonce) { if (r->state == REC_PROPOSED) memcpy(r->match, p + 16, RBH_MATCH_SIZE); return; }      /* (sent again every 100 ms) */
        if (r) rec_end(r, 2);                                /* the next match of this pair: the last one is over */
        r = NULL;
        for (int i = 0; i < REC_SLOTS && !r; i++) if (__atomic_load_n(&g_rec[i].state, __ATOMIC_ACQUIRE) == REC_FREE) r = &g_rec[i];
        if (!r) { static double said; if (g_now - said > 60) { said = g_now; say("replay: no free recording slot (%d writing or running): match not recorded", REC_SLOTS); } return; }
        rec_clear(r);
        r->host = c; r->guest = dst; r->hcookie = c->cookie; r->gcookie = dst->cookie;
        r->prop_nonce = nonce; memcpy(r->match, p + 16, RBH_MATCH_SIZE); r->game_build = (int16_t)rec_get16(p + 2); r->proto = p[1];
        r->quick = (c->kind == KIND_ROLLBACK || c->kind == 3 /* KIND_QUICK, rb_ranked.c */) && c->rb_peer == dst->ip; r->at = g_now;
        snprintf(r->name[0], sizeof r->name[0], "%s", c->name); snprintf(r->name[1], sizeof r->name[1], "%s", dst->name);
        snprintf(r->uid[0], sizeof r->uid[0], "%s", c->uid); snprintf(r->uid[1], sizeof r->uid[1], "%s", dst->uid);
        memset(r->build, 0, sizeof r->build); memcpy(r->build, c->build, (size_t)c->build_len);
        r->state = REC_PROPOSED;
        return;
    }
    if (t == 0x4a && n >= RBH_MSG_SIZE) {                    /* ACCEPT: joiner c -> host dst, echoes the proposal */
        Rec *r = rec_find(dst, c, 0);
        if (r && r->state == REC_PROPOSED && rec_get32(p + 12) == r->prop_nonce && !memcmp(p + 16, r->match, RBH_MATCH_SIZE)) {
            uint64_t seed; memcpy(&seed, r->match + 0x68, 8);  /* (RbhMatch.seed: chara 16, colour 2, has_layout 2, layout 44, stage 32, 4 bytes, items 4) */
            r->match_id = (uint32_t)(seed ^ (seed >> 32)) | 1u;
            r->state = REC_AGREED; r->wall = time(NULL); r->at = g_now;
        }
        return;
    }
    if (t == 0x4d) { Rec *r = rec_find(c, dst, 1); if (r) r->room = 1; return; }      /* LAYOUT: a Local Wireless room's match */
    if (t == 0x4c && n >= 18) {                              /* BLOCK chunk of the host's match block (data at + 18) */
        Rec *r = rec_find(c, dst, 0);
        if (!r || (r->state != REC_AGREED && r->state != REC_RUNNING) || rec_get32(p + 8) != r->match_id) return;
        unsigned off = rec_get16(p + 12), len = rec_get16(p + 14);
        if (off % 768 || off + len > RBH_BLK_SIZE || 18 + (int)len > n) return;
        memcpy(r->blk + off, p + 18, len); r->blk_have[off / 768] = 1;
        r->have_blk = r->blk_have[0] && r->blk_have[1] && r->blk_have[2] && r->blk_have[3];
    }
}
/* (rb_ranked.c) the lobby's verdict of the pair's match: winner = 0 (player 1, the host of the handshake) or 1; taken while
 * the recording is still open (the pads stop REC_IDLE s before it is handed to the writer) */
static void rec_result(Client *a, Client *b, int winner) {
    if (!g_rec_dir[0]) return;
    Rec *r = rec_find(a, b, 1);
    if (r && r->state == REC_RUNNING) r->winner = winner;
}
static void rec_client_gone(Client *c) {
    if (!g_rec_dir[0]) return;
    for (int i = 0; i < REC_SLOTS; i++) { Rec *r = &g_rec[i]; int s = r->state; if (s != REC_FREE && s != REC_WRITE && (r->host == c || r->guest == c)) rec_end(r, 1); }
}
static void rec_sweep(void) {                                /* once per second */
    if (!g_rec_dir[0]) return;
    for (int i = 0; i < REC_SLOTS; i++) {
        Rec *r = &g_rec[i]; int s = r->state;
        if (s == REC_FREE || s == REC_WRITE) continue;
        if (!rec_alive(r)) { rec_end(r, 1); continue; }
        if (s == REC_RUNNING && g_now - r->last > REC_IDLE) rec_end(r, 0);
        else if ((s == REC_PROPOSED || s == REC_AGREED) && g_now - r->at > REC_NOSTART) rec_end(r, 0);
    }
}
static void rec_stop_all(void) {                             /* SIGTERM: the running recordings are written before the exit */
    if (!g_rec_dir[0]) return;
    for (int i = 0; i < REC_SLOTS; i++) if (g_rec[i].state == REC_RUNNING) rec_end(&g_rec[i], 4);
    for (int w = 0; w < 50; w++) { int busy = 0; for (int i = 0; i < REC_SLOTS; i++) busy |= __atomic_load_n(&g_rec[i].state, __ATOMIC_ACQUIRE) == REC_WRITE; if (!busy) break; usleep(100000); }
}
