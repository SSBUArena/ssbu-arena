/* (0.2.21) One file for the shader records (included by nvn_vk_m4.c). ..\cache\vk\spv and \stage hold about 75 000 small files: a
 * start after the PC was turned on opened each one, which is what the load's wait was (35 s here against 9 s with the files already
 * in memory, far worse on a hard disk). Once a preparation is done they are joined into shaders_v1.pack, read through a view of the
 * file, and the small files are removed (what the game makes later still goes to small files: both are read, the pack first).
 *
 * Layout: u32 'SPK1', u32 entries, u64 file size, then the entries sorted by (kind, hash): u64 hash, u32 kind, u32 size, u64 offset,
 * then the data. Kind: 0x100 spv, 0x200 | stage .st, 0x300 | stage .var.
 * Made after the game's load is done, on a thread at background priority; written to a temporary name and renamed when whole.
 * SSBU_VK_PACK=0: not used, not made. Any thread may read; the view is set once. */
#define VKP_FILE "shaders_v1.pack"
typedef struct { uint64_t h; uint32_t kind, size; uint64_t off; } VkpEntry;
static const VkpEntry *volatile g_vkp_index;
static const uint8_t *g_vkp_view;
static uint32_t g_vkp_n;
static uint64_t g_vkp_size;

static int vkp_cmp(uint32_t ka, uint64_t ha, const VkpEntry *b) {
    if (ka != b->kind) return ka < b->kind ? -1 : 1;
    return ha < b->h ? -1 : ha > b->h ? 1 : 0;
}
/* the record, or NULL (not in the pack: the small file may be there) */
static const void *vkp_get(uint32_t kind, uint64_t h, size_t *size) {
    const VkpEntry *ix = g_vkp_index;
    if (!ix) return NULL;
    uint32_t lo = 0, hi = g_vkp_n;
    while (lo < hi) {
        uint32_t mid = lo + (hi - lo) / 2;
        int c = vkp_cmp(kind, h, &ix[mid]);
        if (!c) { *size = ix[mid].size; return g_vkp_view + ix[mid].off; }
        if (c < 0) hi = mid; else lo = mid + 1;
    }
    return NULL;
}
static int vkp_on(void) { static int on = -1; if (on < 0) { const char *e = getenv("SSBU_VK_PACK"); on = !(e && e[0] == '0'); } return on; }
static int vkp_open(void) {
    if (g_vkp_index || !vkp_on()) return g_vkp_index != NULL;
    HANDLE f = CreateFileA(vkc_file(VKP_FILE), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) return 0;
    LARGE_INTEGER sz; GetFileSizeEx(f, &sz);
    HANDLE m = sz.QuadPart > 16 ? CreateFileMappingA(f, NULL, PAGE_READONLY, 0, 0, NULL) : NULL;
    const uint8_t *v = m ? MapViewOfFile(m, FILE_MAP_READ, 0, 0, 0) : NULL;
    CloseHandle(f);
    if (m) CloseHandle(m);
    if (!v) return 0;
    uint32_t magic = *(const uint32_t *)v, n = ((const uint32_t *)v)[1];
    if (magic != 0x314B5053 || *(const uint64_t *)(v + 8) != (uint64_t)sz.QuadPart || 16 + (uint64_t)n * sizeof(VkpEntry) > (uint64_t)sz.QuadPart) {
        printf("nvn_vk_pack: %s is not whole: not used\n", VKP_FILE); fflush(stdout);
        UnmapViewOfFile(v);
        return 0;
    }
    g_vkp_view = v; g_vkp_n = n; g_vkp_size = (uint64_t)sz.QuadPart;
    MemoryBarrier();
    g_vkp_index = (const VkpEntry *)(v + 16);
    printf("nvn_vk_pack: %s: %u records, %llu MB\n", VKP_FILE, n, (unsigned long long)(sz.QuadPart >> 20)); fflush(stdout);
    return 1;
}

/* (shader_load = start) the whole file read ahead in one go, in file order: what makes the load fast after the PC was turned on */
static DWORD WINAPI vkp_prefetch_thread(LPVOID a) {
    (void)a;
    typedef struct { PVOID VirtualAddress; SIZE_T NumberOfBytes; } Range;
    typedef BOOL (WINAPI *Prefetch)(HANDLE, ULONG_PTR, Range *, ULONG);
    Prefetch p = (Prefetch)(void *)GetProcAddress(GetModuleHandleA("kernel32.dll"), "PrefetchVirtualMemory");
    MEMORYSTATUSEX ms; ms.dwLength = sizeof ms;
    if (!p || !g_vkp_index || !GlobalMemoryStatusEx(&ms) || ms.ullAvailPhys < g_vkp_size + (g_vkp_size >> 2)) return 0;
    Range r = {(PVOID)g_vkp_view, (SIZE_T)g_vkp_size};
    LONG64 t0 = tk();
    BOOL ok = p(GetCurrentProcess(), 1, &r, 0);
    LARGE_INTEGER fq; QueryPerformanceFrequency(&fq);
    printf("nvn_vk_pack: read ahead asked %s in %.2f s\n", ok ? "and answered" : "and refused", (double)(tk() - t0) / (double)fq.QuadPart); fflush(stdout);
    return 0;
}
static void vkp_prefetch(void) { if (g_vkp_index) { HANDLE t = CreateThread(NULL, 0, vkp_prefetch_thread, NULL, 0, NULL); if (t) CloseHandle(t); } }

typedef struct { uint64_t h; uint32_t kind, size; uint64_t off; char name[40]; } VkpNew;
static int vkp_new_cmp(const void *a, const void *b) {
    const VkpNew *x = a, *y = b;
    if (x->kind != y->kind) return x->kind < y->kind ? -1 : 1;
    return x->h < y->h ? -1 : x->h > y->h ? 1 : 0;
}
static void vkp_list(const char *sub, VkpNew **list, uint32_t *n, uint32_t *cap) {
    char pat[300];
    WIN32_FIND_DATAA fd;
    snprintf(pat, sizeof pat, "%s/%s/*", vkc_dir(), sub);
    HANDLE h = FindFirstFileA(pat, &fd);
    if (h == INVALID_HANDLE_VALUE) return;
    do {
        if (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) continue;
        unsigned long long hash = 0;
        int s = 0;
        char ext[8] = "";
        uint32_t kind;
        if (sscanf(fd.cFileName, "%llx.%3s", &hash, ext) == 2 && !strcmp(ext, "spv") && !strcmp(sub, "spv")) kind = 0x100;
        else if (sscanf(fd.cFileName, "%d_%llx.%3s", &s, &hash, ext) == 3 && !strcmp(sub, "stage") && (!strcmp(ext, "st") || !strcmp(ext, "var"))) kind = (ext[0] == 's' ? 0x200u : 0x300u) | (uint32_t)s;
        else continue;
        if (*n == *cap) { *cap = *cap ? *cap * 2 : 65536; *list = realloc(*list, *cap * sizeof **list); }
        VkpNew *e = &(*list)[(*n)++];
        e->h = hash; e->kind = kind; e->size = fd.nFileSizeLow;
        snprintf(e->name, sizeof e->name, "%s", fd.cFileName);
        e->off = (uint64_t)(uintptr_t)sub;           /* (the folder, until the offsets are known) */
    } while (FindNextFileA(h, &fd));
    FindClose(h);
}
static int wipe_glob(const char *pattern);
/* a stop while the small files were being removed: the rest of them. The .var files stay: one beside the pack holds only what came later. */
static void vkp_clean(void) {
    VkpNew *l = NULL; uint32_t n = 0, cap = 0, gone = 0;
    size_t sz;
    vkp_list("stage", &l, &n, &cap);
    vkp_list("spv", &l, &n, &cap);
    for (uint32_t i = 0; i < n; i++) {
        if ((l[i].kind & 0xf00) == 0x300 || !vkp_get(l[i].kind, l[i].h, &sz)) continue;
        char path[300];
        snprintf(path, sizeof path, "%s/%s/%s", vkc_dir(), l[i].kind == 0x100 ? "spv" : "stage", l[i].name);
        if (DeleteFileA(path)) gone++;
    }
    if (gone) { printf("nvn_vk_pack: %u small files left by a stop removed\n", gone); fflush(stdout); }
    free(l);
}
static DWORD WINAPI vkp_build_thread(LPVOID a) {
    (void)a;
    SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
    HANDLE one = CreateMutexA(NULL, FALSE, "ssbu_vk_pack");           /* two games on one PC: one makes it */
    if (one && WaitForSingleObject(one, 0) == WAIT_TIMEOUT) { CloseHandle(one); return 0; }
    if (vkp_open()) { vkp_clean(); if (one) { ReleaseMutex(one); CloseHandle(one); } return 0; }
    wipe_glob(VKP_FILE ".tmp*");
    char tmp[300], fin[300];
    snprintf(tmp, sizeof tmp, "%s/%s.tmp%lu", vkc_dir(), VKP_FILE, GetCurrentProcessId());
    snprintf(fin, sizeof fin, "%s/%s", vkc_dir(), VKP_FILE);
    VkpNew *l = NULL; uint32_t n = 0, cap = 0;
    LONG64 t0 = tk();
    vkp_list("stage", &l, &n, &cap);
    vkp_list("spv", &l, &n, &cap);
    if (n < 100) { free(l); if (one) { ReleaseMutex(one); CloseHandle(one); } return 0; }
    qsort(l, n, sizeof *l, vkp_new_cmp);
    HANDLE out = CreateFileA(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
    if (out == INVALID_HANDLE_VALUE) { free(l); if (one) { ReleaseMutex(one); CloseHandle(one); } return 0; }
    uint64_t pos = 16 + (uint64_t)n * sizeof(VkpEntry);
    enum { BUF = 4 << 20 };
    uint8_t *buf = malloc(BUF);
    LARGE_INTEGER at; at.QuadPart = (LONGLONG)pos;
    SetFilePointerEx(out, at, NULL, FILE_BEGIN);
    uint32_t have = 0, kept = 0;
    DWORD w;
    int bad = 0;
    for (uint32_t i = 0; i < n && !bad; i++) {
        VkpNew *e = &l[i];
        char path[300];
        snprintf(path, sizeof path, "%s/%s/%s", vkc_dir(), (const char *)(uintptr_t)e->off, e->name);
        e->off = (uint64_t)-1;
        HANDLE f = CreateFileA(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, FILE_FLAG_SEQUENTIAL_SCAN, NULL);
        if (f == INVALID_HANDLE_VALUE) continue;
        DWORD size = GetFileSize(f, NULL), got = 0;
        if (size > (DWORD)BUF) { CloseHandle(f); continue; }          /* (none is that large: stays a small file) */
        have += (uint32_t)((4u - ((pos + have) & 3u)) & 3u);          /* the words of a shader, aligned */
        if (have + size > BUF) { if (!WriteFile(out, buf, have, &w, NULL) || w != have) bad = 1; pos += have; have = 0; }
        if (ReadFile(f, buf + have, size, &got, NULL) && got == size) { e->off = pos + have; e->size = size; have += size; kept++; }
        CloseHandle(f);
    }
    if (have && !bad) { if (!WriteFile(out, buf, have, &w, NULL) || w != have) bad = 1; pos += have; }
    free(buf);
    if (!bad) {
        VkpEntry *ix = malloc(n * sizeof *ix);
        uint32_t k = 0;
        for (uint32_t i = 0; i < n; i++) if (l[i].off != (uint64_t)-1) { ix[k].h = l[i].h; ix[k].kind = l[i].kind; ix[k].size = l[i].size; ix[k].off = l[i].off; k++; }
        uint32_t head[2] = {0x314B5053, k};
        at.QuadPart = 0;
        SetFilePointerEx(out, at, NULL, FILE_BEGIN);
        /* (the entries take room for all that were listed; the ones left out only leave a gap: the offsets are absolute) */
        if (!WriteFile(out, head, 8, &w, NULL) || !WriteFile(out, &pos, 8, &w, NULL) || !WriteFile(out, ix, k * (DWORD)sizeof *ix, &w, NULL) || w != k * sizeof *ix) bad = 1;
        free(ix);
    }
    FlushFileBuffers(out);
    CloseHandle(out);
    if (bad || !MoveFileExA(tmp, fin, MOVEFILE_REPLACE_EXISTING)) {
        DeleteFileA(tmp);
        printf("nvn_vk_pack: not made (disk full?): the small files stay\n"); fflush(stdout);
        free(l);
        if (one) { ReleaseMutex(one); CloseHandle(one); }
        return 0;
    }
    LARGE_INTEGER fq; QueryPerformanceFrequency(&fq);
    printf("nvn_vk_pack: %s made: %u records, %llu MB, %.1f s\n", VKP_FILE, kept, (unsigned long long)(pos >> 20), (double)(tk() - t0) / (double)fq.QuadPart); fflush(stdout);
    if (vkp_open()) {                                /* whole and in use: the small files it holds go */
        uint32_t gone = 0;
        for (uint32_t i = 0; i < n; i++) {
            if (l[i].off == (uint64_t)-1) continue;
            char path[300];
            snprintf(path, sizeof path, "%s/%s/%s", vkc_dir(), l[i].kind == 0x100 ? "spv" : "stage", l[i].name);
            if (DeleteFileA(path)) gone++;
        }
        printf("nvn_vk_pack: %u small files removed, %.1f s in all\n", gone, (double)(tk() - t0) / (double)fq.QuadPart); fflush(stdout);
    }
    free(l);
    if (one) { ReleaseMutex(one); CloseHandle(one); }
    return 0;
}
static void vkp_after_load(void) {
    if (!vkp_on()) return;
    HANDLE t = CreateThread(NULL, 0, vkp_build_thread, NULL, 0, NULL);
    if (t) CloseHandle(t);
}
