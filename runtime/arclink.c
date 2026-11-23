/* arclink.c - the LINKED archive data.arcl (included by arcpack.c; not a build unit of its own). Contains nothing of the game.
 *
 * Shared game data (scripts/shared_gamedata.py): a player who also has OpenSmash Ultimate keeps the game's data once. Arena's
 * RomFS folder then holds data.arcl instead of data.arc: a map saying where each byte range of data.arc lies in OTHER files
 * (OpenSmash's decrypted base NCA and its update's bytes). Format (tools/nro/arc_file.py writes and reads it too):
 *     "SSBUARCL" | u64 size of data.arc | u32 source count | u32 range count
 *     sources: u64 size | u64 last-write time (FILETIME) | u32 path bytes | path (UTF-8, absolute)
 *     ranges sorted by offset, covering [0, size): u64 offset | u64 length | u64 offset in the source | u32 source
 *         (0xFFFFFFFF: zeros) | u32 0
 * Rules: the sources are opened READ-ONLY and shared for reading only (no FILE_SHARE_WRITE / DELETE): while the game runs,
 * the other product cannot rewrite or delete them under it, and it can still read them itself. A source whose size or
 * last-write time is not the recorded one makes the map STALE: nothing of it is read (the other product changed its data:
 * the installer makes the link again, or a dump of Arena's own). Nothing is ever written to a source.
 * When data.arc is there it wins; data.arcp (the light archive) comes before data.arcl. */
#ifdef _WIN32
typedef struct { uint64_t off, len, at; uint32_t src, pad; } ArclRange;
#define ARCL_ZEROS 0xFFFFFFFFu
#define ARCL_MAX_SRC 8
static struct {
    int state;                          /* 0 not loaded, 1 loaded, -1 refused */
    char dir[MAX_PATH];                 /* the RomFS folder it was loaded from */
    uint64_t size, n;
    uint32_t nsrc;
    HANDLE src[ARCL_MAX_SRC];
    ArclRange *r;
    char why[600];
} g_arcl;
static SRWLOCK g_arcl_lock = SRWLOCK_INIT;
static HANDLE g_arcl_handles[16];       /* the game's open handles of data.arc that are the linked archive */

static HANDLE arcl_open_utf8(const char *path, DWORD share) {
    wchar_t w[MAX_PATH * 2];
    if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, path, -1, w, (int)(sizeof w / sizeof w[0]))) return INVALID_HANDLE_VALUE;
    return CreateFileW(w, GENERIC_READ, share, NULL, OPEN_EXISTING, FILE_FLAG_RANDOM_ACCESS, NULL);
}

/* load <dir>\data.arcl once (later calls: the result of the first). 1 = usable, 0 = none there, -1 = refused (why says it). */
int arcl_load(const char *dir) {
    AcquireSRWLockExclusive(&g_arcl_lock);
    if (g_arcl.state) { int s = g_arcl.state; ReleaseSRWLockExclusive(&g_arcl_lock); return s; }
    char p[MAX_PATH + 16];
    snprintf(p, sizeof p, "%s\\data.arcl", dir);
    HANDLE h = CreateFileA(p, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) { ReleaseSRWLockExclusive(&g_arcl_lock); return 0; }
    LARGE_INTEGER fs;
    uint8_t *d = NULL;
    DWORD got = 0;
    int ok = GetFileSizeEx(h, &fs) && fs.QuadPart >= 24 && fs.QuadPart < (64ll << 20) && (d = malloc((size_t)fs.QuadPart)) != NULL &&
             ReadFile(h, d, (DWORD)fs.QuadPart, &got, NULL) && got == (DWORD)fs.QuadPart;
    CloseHandle(h);
    snprintf(g_arcl.dir, sizeof g_arcl.dir, "%s", dir);
    g_arcl.why[0] = 0;
    if (!ok || memcmp(d, "SSBUARCL", 8)) { snprintf(g_arcl.why, sizeof g_arcl.why, "%s cannot be read or is not a linked archive", p); goto refuse; }
    uint64_t pos = 24, end = (uint64_t)fs.QuadPart;
    uint32_t nsrc, nr;
    memcpy(&g_arcl.size, d + 8, 8); memcpy(&nsrc, d + 16, 4); memcpy(&nr, d + 20, 4);
    if (nsrc > ARCL_MAX_SRC || !nr) { snprintf(g_arcl.why, sizeof g_arcl.why, "%s: %u sources, %u ranges", p, nsrc, nr); goto refuse; }
    uint64_t srcsize[ARCL_MAX_SRC];
    for (uint32_t i = 0; i < nsrc; i++) {
        uint64_t sz, mt; uint32_t len; char path[MAX_PATH * 2];
        if (pos + 20 > end) { snprintf(g_arcl.why, sizeof g_arcl.why, "%s is cut short", p); goto refuse; }
        memcpy(&sz, d + pos, 8); memcpy(&mt, d + pos + 8, 8); memcpy(&len, d + pos + 16, 4); pos += 20;
        if (len >= sizeof path || pos + len > end) { snprintf(g_arcl.why, sizeof g_arcl.why, "%s is cut short", p); goto refuse; }
        memcpy(path, d + pos, len); path[len] = 0; pos += len;
        HANDLE s = arcl_open_utf8(path, FILE_SHARE_READ);
        BY_HANDLE_FILE_INFORMATION fi;
        if (s == INVALID_HANDLE_VALUE) {
            DWORD e = GetLastError();
            snprintf(g_arcl.why, sizeof g_arcl.why, "%s cannot be opened (%s)", path, e == ERROR_FILE_NOT_FOUND || e == ERROR_PATH_NOT_FOUND ? "not there any more"
                     : e == ERROR_SHARING_VIOLATION ? "another program is writing it" : "error");
            goto refuse;
        }
        g_arcl.src[g_arcl.nsrc++] = s;
        if (!GetFileInformationByHandle(s, &fi) || (((uint64_t)fi.nFileSizeHigh << 32) | fi.nFileSizeLow) != sz ||
            (((uint64_t)fi.ftLastWriteTime.dwHighDateTime << 32) | fi.ftLastWriteTime.dwLowDateTime) != mt) {
            snprintf(g_arcl.why, sizeof g_arcl.why, "%s changed since the link was made (its size or date is not the recorded one)", path);
            goto refuse;
        }
        srcsize[i] = sz;
    }
    if (pos + (uint64_t)nr * sizeof(ArclRange) != end) { snprintf(g_arcl.why, sizeof g_arcl.why, "%s: wrong size for %u ranges", p, nr); goto refuse; }
    g_arcl.r = malloc((size_t)nr * sizeof(ArclRange));
    if (!g_arcl.r) { snprintf(g_arcl.why, sizeof g_arcl.why, "out of memory"); goto refuse; }
    memcpy(g_arcl.r, d + pos, (size_t)nr * sizeof(ArclRange));
    g_arcl.n = nr;
    uint64_t at = 0;
    for (uint64_t i = 0; i < g_arcl.n; i++) {          /* in order, no gap, inside their source */
        const ArclRange *r = &g_arcl.r[i];
        if (r->off != at || !r->len || (r->src != ARCL_ZEROS && (r->src >= nsrc || r->at + r->len > srcsize[r->src] || r->at + r->len < r->at))) {
            snprintf(g_arcl.why, sizeof g_arcl.why, "%s: range %llu is not valid", p, (unsigned long long)i);
            goto refuse;
        }
        at += r->len;
    }
    if (at != g_arcl.size) { snprintf(g_arcl.why, sizeof g_arcl.why, "%s covers %llu of %llu bytes", p, (unsigned long long)at, (unsigned long long)g_arcl.size); goto refuse; }
    free(d);
    g_arcl.state = 1;
    printf("arclink: %s: data.arc (%.2f GB) read from %u file(s) of another install, %llu ranges (shared game data)\n", p,
           g_arcl.size / 1073741824.0, g_arcl.nsrc, (unsigned long long)g_arcl.n);
    ReleaseSRWLockExclusive(&g_arcl_lock);
    return 1;
refuse:
    free(d); free(g_arcl.r); g_arcl.r = NULL; g_arcl.n = 0;
    for (uint32_t i = 0; i < g_arcl.nsrc; i++) CloseHandle(g_arcl.src[i]);
    g_arcl.nsrc = 0;
    g_arcl.state = -1;
    printf("arclink: the shared game data cannot be used: %s\n", g_arcl.why);
    ReleaseSRWLockExclusive(&g_arcl_lock);
    return -1;
}
const char *arcl_why(void) { return g_arcl.why; }
uint64_t arcl_total(void) { return g_arcl.state == 1 ? g_arcl.size : 0; }
/* the container store's stamp (nvn_vk_m4.c) for the linked archive: the archive's size and its map (ranges and their source
 * offsets), not the date of data.arcl: a link made again over the same data (the launcher's relink after OpenSmash rewrote its
 * files with the same content) keeps the store; a map of other data makes it stale. 1 when there is a usable link. */
int arcl_stamp(const char *dir, char *out, size_t n) {
    if (arcl_load(dir) != 1) return 0;
    uint64_t h = 0xcbf29ce484222325ULL;
    const uint8_t *p = (const uint8_t *)g_arcl.r;
    for (size_t i = 0; i < (size_t)g_arcl.n * sizeof(ArclRange); i++) { h ^= p[i]; h *= 0x100000001b3ULL; }
    snprintf(out, n, "data.arc size %llu linked map %016llx\n", (unsigned long long)g_arcl.size, (unsigned long long)h);
    return 1;
}

/* bytes [off, off + size) of data.arc through the map; returns how many were read (short only at the end or on an error).
 * guest: buf is the game's memory (a read the game asked for): a page the rollback's learned-set save protected is opened
 * again and the read tried once more, as read_at in nnfs.c does (R4) */
static uint64_t arcl_pread_x(uint64_t off, void *buf, uint64_t size, int guest) {
    if (g_arcl.state != 1 || off >= g_arcl.size) return 0;
    if (size > g_arcl.size - off) size = g_arcl.size - off;
    uint64_t lo = 0, hi = g_arcl.n, done = 0;
    while (hi - lo > 1) { uint64_t mid = (lo + hi) / 2; if (g_arcl.r[mid].off <= off) lo = mid; else hi = mid; }
    for (uint64_t k = lo; done < size && k < g_arcl.n; k++) {
        const ArclRange *r = &g_arcl.r[k];
        uint64_t pos = off + done, n = r->off + r->len - pos;
        if (n > size - done) n = size - done;
        if (r->src == ARCL_ZEROS) { memset((char *)buf + done, 0, (size_t)n); done += n; continue; }
        uint64_t got_all = 0;
        while (got_all < n) {
            OVERLAPPED ov = {0}; uint64_t sp = r->at + (pos - r->off) + got_all;
            ov.Offset = (DWORD)sp; ov.OffsetHigh = (DWORD)(sp >> 32);
            DWORD chunk = n - got_all > 0x40000000 ? 0x40000000 : (DWORD)(n - got_all), g = 0;
            if (!ReadFile(g_arcl.src[r->src], (char *)buf + done + got_all, chunk, &g, &ov) || !g) {
                if (guest && GetLastError() == ERROR_NOACCESS) {
                    void r1_wrote(uint64_t, uint64_t); void r1_read_retry_note(void);
                    r1_wrote((uint64_t)(uintptr_t)((char *)buf + done + got_all), chunk); r1_read_retry_note();
                    memset(&ov, 0, sizeof ov); ov.Offset = (DWORD)sp; ov.OffsetHigh = (DWORD)(sp >> 32);
                    if (ReadFile(g_arcl.src[r->src], (char *)buf + done + got_all, chunk, &g, &ov) && g) { got_all += g; continue; }
                }
                return done + got_all;
            }
            got_all += g;
        }
        done += n;
    }
    return done;
}
uint64_t arcl_pread(uint64_t off, void *buf, uint64_t size) { return arcl_pread_x(off, buf, size, 0); }

/* the game's open of <dir>\data.arc when only data.arcl is there: a handle of data.arcl itself stands for it (every read is
 * translated, arcl_read); INVALID_HANDLE_VALUE if there is no usable link */
static HANDLE arcl_open(const char *arc_path) {
    char dir[MAX_PATH], p[MAX_PATH + 4];
    size_t n = strlen(arc_path);
    if (n < 9 || n - 9 >= sizeof dir || !ends_with_i(arc_path, "\\data.arc")) return INVALID_HANDLE_VALUE;
    memcpy(dir, arc_path, n - 9); dir[n - 9] = 0;
    if (arcl_load(dir) != 1) return INVALID_HANDLE_VALUE;
    snprintf(p, sizeof p, "%sl", arc_path);
    HANDLE h = CreateFileA(p, GENERIC_READ, FILE_SHARE_READ, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h == INVALID_HANDLE_VALUE) return h;
    AcquireSRWLockExclusive(&g_arcl_lock);
    int k = 0;
    while (k < 16 && g_arcl_handles[k]) k++;
    if (k < 16) g_arcl_handles[k] = h;
    ReleaseSRWLockExclusive(&g_arcl_lock);
    if (k >= 16) { CloseHandle(h); return INVALID_HANDLE_VALUE; }
    return h;
}
static int arcl_is(HANDLE h) { if (!h) return 0; for (int i = 0; i < 16; i++) if (g_arcl_handles[i] == h) return 1; return 0; }
static void arcl_closed(HANDLE h) {
    AcquireSRWLockExclusive(&g_arcl_lock);
    for (int i = 0; i < 16; i++) if (g_arcl_handles[i] == h) g_arcl_handles[i] = NULL;
    ReleaseSRWLockExclusive(&g_arcl_lock);
}
static int arcl_size(HANDLE h, int64_t *out) { if (!arcl_is(h)) return 0; *out = (int64_t)g_arcl.size; return 1; }
/* a read of the linked archive: 1 when h is one (got = bytes delivered) */
static int arcl_read(HANDLE h, int64_t off, void *buf, uint64_t size, uint64_t *got) {
    if (!arcl_is(h)) return 0;
    *got = off < 0 ? 0 : arcl_pread_x((uint64_t)off, buf, size, 1);
    return 1;
}
#else
int arcl_load(const char *dir) { (void)dir; return 0; }
const char *arcl_why(void) { return ""; }
int arcl_stamp(const char *dir, char *out, size_t n) { (void)dir; (void)out; (void)n; return 0; }
uint64_t arcl_total(void) { return 0; }
uint64_t arcl_pread(uint64_t off, void *buf, uint64_t size) { (void)off; (void)buf; (void)size; return 0; }
static HANDLE arcl_open(const char *arc_path) { (void)arc_path; return INVALID_HANDLE_VALUE; }
static void arcl_closed(HANDLE h) { (void)h; }
static int arcl_is(HANDLE h) { (void)h; return 0; }
static int arcl_size(HANDLE h, int64_t *out) { (void)h; (void)out; return 0; }
static int arcl_read(HANDLE h, int64_t off, void *buf, uint64_t size, uint64_t *got) { (void)h; (void)off; (void)buf; (void)size; (void)got; return 0; }
#endif
