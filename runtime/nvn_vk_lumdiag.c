/* (amd-black) SSBU_VK_LUMDIAG: the BLACK PICTURE diagnosis (included by nvn_vk_draw.c after nvn_vk_flash.c; render thread
 * only, except the shader dump thread). An RX 6750 XT (amdvlk 26.1.12, whole pipelines) draws whole fights black (mean
 * brightness 1-6 of 255 for minutes, then normal again at once); NVIDIA is right. The suspect is the game's exposure: small
 * render targets (luminance downsamples down to 1x1, the adapted exposure) and small compute results feeding the tonemap.
 *
 * When: nvn_vk_lum's brightness of the fight frames (fl_analyse, 8 presents late) calls ld_lum; only frames of 120 draws or
 *   more count as fight frames here (the loading screen before a fight is already "fight"). A CAPTURE is asked for
 *   - "reference": at fight frame 60 of every fight (while at most half of the line budget is used),
 *   - "dark": the picture stays under SSBU_VK_LUMDIAG_DARK (default 20) of 255 for 60 fight frames (once per dark episode),
 *   - "recovered": after a dark capture, the picture is back at 40 or more for 60 fight frames (the good state on the
 *     same PC, to compare with).
 *   SSBU_VK_LUMDIAG_TEST=1 (tests): also a "dark (test)" capture at fight frame 180 and a "recovered (test)" one at 600,
 *   whatever the brightness.
 * What: the next SSBU_VK_LUMDIAG_FRAMES (default 2) presented frames are recorded with, after every pass whose colour
 *   target is small (at most SSBU_VK_LUMDIAG_SMALL texels, default 65536 = 256x256), after every blit / downsample into
 *   a small texture, before every read-back of a small texture by the CPU, and after every compute dispatch that writes a
 *   small storage buffer (at most 64 KB, 16 a frame), a copy of what was written into a host buffer (made for the
 *   capture, freed after it). In the FIRST frame of a capture the LARGE colour targets are read too (SSBU_VK_LUMDIAG_BIG=0:
 *   not): float ones whole (exact NaN / Inf counts), the others (the RGB10A2 scene, the sRGB picture) blitted to 1/8 into a
 *   RGBA32F atlas: the means show which pass of the chain turns the picture dark. On NVIDIA (Battlefield) the chain is:
 *   shadow map, 1920x1080 RGB10A2 scene, a bloom chain 480x270 .. 30x16 R11G11B10F, then RGBA8_SRGB passes to the window.
 *   When the GPU has finished, "nvn_vk_lumdiag:" lines: the list of the frame's passes (size / format / targets / draws),
 *   then one line per entry in the order the GPU ran them: kind, size, format, level, the draws of the pass, the shaders
 *   (vertex / fragment: hb as in the nvn_vk_skip lines (includes the translator version), and "code" = the machine code's
 *   hash, the same on every GPU and version: what to compare between an AMD and an NVIDIA log), per channel min..max
 *   ~mean of the finite values, and the counts of NaN, Inf, denormals, negatives and huge (|v| >= 60000) values. Compute
 *   buffers (skinning) are summed in one line unless they have bad values. A summary line names the FIRST entry of the
 *   chain with NaN / Inf / huge values and the first entry whose colour means differ more than 4x (or NaN / Inf appear)
 *   from the last capture of the other kind (dark against bright); a cost line follows.
 *   The first dark (or test) capture also saves the GLSL and SPIR-V of the shaders it met in build\lumdiag\ (a thread;
 *   they stay on the PC: game shaders, sent only if the player sends them by hand; SSBU_VK_LUMDIAG_DUMP=0: not saved).
 * Cost: nothing while the picture is normal (one counter per analysed frame; the hooks test one int); a capture: copies
 *   of the small targets, ~25 blits of the large ones, a 64 MB host buffer and a 16 MB atlas for its frames (freed after).
 * Budget: at most SSBU_VK_LUMDIAG_LINES lines (default 200) a run; reference captures stop at half of it; the first
 *   capture of each kind lists every entry, later ones only entries with bad values or changes.
 * SSBU_VK_LUMDIAG: 2 = default, every GPU but NVIDIA (and the AMD test path SSBU_VK_AMD_SAFE=2); 1 = all; 0 = off. */
#define LD_MAXE 96
#define LD_PAIRS 16
#define LD_SMALLBUF 65536u
#define LD_DUMP_MAX 128
enum { LD_REF = 1, LD_DARK, LD_REC, LD_TDARK, LD_TREC };
static const char *const ld_why_name[] = {"", "reference (start of the fight)", "DARK picture", "recovered picture", "dark (TEST)", "recovered (TEST)"};
typedef struct {
    int kind, frame, order, d0, draws, ndist, read, w, h, level, layer, big, nch, fk, bpp, nvfmt, flag, ref_ord, red, ow, oh;      /* red: a large target read reduced 1 / red (blit into the atlas); ow x oh its size */
    VkFormat fmt;
    VTex *vt;                                      /* only between pass_begin and its pass_end */
    uint64_t hb[LD_PAIRS][2], hp[LD_PAIRS][2];     /* [0] vertex (compute: the compute stage), [1] fragment */
    uint64_t off, bytes;
    double sum[4]; float mn[4], mx[4], ref_mean; uint32_t n[4], nan, inf, den, neg, huge;
} LdEnt;
typedef struct { int kind, w, h, level, fmt, occ, bad; uint64_t key; double mean[4]; int nch, order; } LdRef;
typedef struct { unsigned short w, h; int fmt, d0; unsigned char ncol, depth; } LdPass;
#define LD_NPASS 64
typedef struct { int stage; uint64_t hp, hb; uint32_t key[4], fo; SSrc src; uint8_t *code, *cdata; } LdDump;
static int g_ld_cap, g_ld_npe;                     /* declared again (tentatively) above pass_end */
static struct {
    int on, test, lines, maxlines, nframes, dark_thr, small_tx, fight_n, dark_run, bright_run, armed, want_rec, ncap, pending, state, fidx, nent, dropped, order, passes, dispatches, big, dump, dumped, ndump, nref[2], cap_mean, cap_fight, why, nodump;
    int listed[6], pend_mean, pend_fight;
    LONG64 pend_frame;
    int pe[8];
    LdPass pass[LD_NPASS]; int npass, fd_end;
    LdEnt e[LD_MAXE];
    LdRef ref[2][LD_MAXE];                         /* frame 0 of the last bright (0) / dark (1) capture */
    int ref_cap[2];
    LdDump dq[LD_DUMP_MAX];
    VkBuffer buf; VkDeviceMemory mem; uint8_t *map; uint64_t size, used, wait_serial;
    VkImage atl; VMem atl_mem; int atl_n, atl_fail, ncomp[8], nocomp, nobig;      /* atl: RGBA32F 1024x1024, 28 tiles of 256x144 for the reduced large targets */
    LONG64 cap_frame;
} g_ld = {.on = -1};
static double g_ld_rec_ms;                         /* render-thread time of the capture's hooks (copies recorded) */
static LONG64 g_ld_t0;
static void ld_t_end(void) { LARGE_INTEGER fq; QueryPerformanceFrequency(&fq); g_ld_rec_ms += (double)(tk() - g_ld_t0) * 1000.0 / (double)fq.QuadPart; }

static int ld_on(void) {
    if (g_ld.on < 0) {
        const char *e = getenv("SSBU_VK_LUMDIAG");
        int m = e && e[0] ? atoi(e) : 2;
        g_ld.on = m == 1 || (m == 2 && ((V.props.vendorID && V.props.vendorID != 0x10de) || vk_amd_forced()));
        e = getenv("SSBU_VK_LUMDIAG_TEST"); g_ld.test = e && atoi(e) > 0;
        if (g_ld.test) g_ld.on = 1;
        e = getenv("SSBU_VK_LUMDIAG_LINES"); g_ld.maxlines = e && atoi(e) > 0 ? atoi(e) : 200;
        e = getenv("SSBU_VK_LUMDIAG_FRAMES"); g_ld.nframes = e && atoi(e) > 0 ? (atoi(e) > 8 ? 8 : atoi(e)) : 2;
        e = getenv("SSBU_VK_LUMDIAG_DARK"); g_ld.dark_thr = e && atoi(e) > 0 ? atoi(e) : 20;
        e = getenv("SSBU_VK_LUMDIAG_SMALL"); g_ld.small_tx = e && atoi(e) > 0 ? atoi(e) : 65536;
        e = getenv("SSBU_VK_LUMDIAG_DUMP"); g_ld.nodump = e && e[0] == '0';
        e = getenv("SSBU_VK_LUMDIAG_BIG"); g_ld.nobig = e && e[0] == '0';
        g_ld.armed = 1;
    }
    return g_ld.on;
}
/* Every line also goes to <exe>_lumdiag.txt next to the exe (the launcher's Send logs takes it: the middle of a long game.log
 * is cut there); a file over 512 KB is started again. Only these lines: nothing of the game. */
static FILE *ld_file(void) {
    static FILE *fp; static int tried;
    if (!tried) {
        char path[MAX_PATH + 32];
        DWORD n = GetModuleFileNameA(NULL, path, MAX_PATH);
        tried = 1;
        if (n > 4) {
            WIN32_FILE_ATTRIBUTE_DATA fa; SYSTEMTIME st;
            strcpy(path + n - 4, "_lumdiag.txt");
            int big = GetFileAttributesExA(path, GetFileExInfoStandard, &fa) && (fa.nFileSizeHigh || fa.nFileSizeLow > (512u << 10));
            fp = fopen(path, big ? "wb" : "ab");
            GetLocalTime(&st);
            if (fp) fprintf(fp, "nvn_vk_lumdiag: --- run of %04d-%02d-%02d %02d:%02d:%02d, GPU %s (vendor 0x%x, driver 0x%x)\n", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, V.props.deviceName, V.props.vendorID, V.props.driverVersion);
        }
    }
    return fp;
}
#define LDP(...) do { printf(__VA_ARGS__); { FILE *ldf_ = ld_file(); if (ldf_) fprintf(ldf_, __VA_ARGS__); } } while (0)
static int ld_say(void) { if (g_ld.lines >= g_ld.maxlines) return 0; g_ld.lines++; return 1; }

/* ---- formats: fk 1 half, 2 float, 3 unorm8, 4 r11g11b10 ufloat, 5 e5b9g9r9, 6 unorm16, 7 a2b10g10r10 unorm, 8 uint32, 9 srgb8, 10 bgra8, 11 bgra8 srgb, 12 uint16, 13 uint8 */
static int ld_fmt(VkFormat f, int *bpp, int *nch, const char **name) {
    static const struct { VkFormat f; int fk, bpp, nch; const char *n; } t[] = {
        {VK_FORMAT_R16_SFLOAT, 1, 2, 1, "R16F"}, {VK_FORMAT_R16G16_SFLOAT, 1, 4, 2, "RG16F"}, {VK_FORMAT_R16G16B16A16_SFLOAT, 1, 8, 4, "RGBA16F"},
        {VK_FORMAT_R32_SFLOAT, 2, 4, 1, "R32F"}, {VK_FORMAT_R32G32_SFLOAT, 2, 8, 2, "RG32F"}, {VK_FORMAT_R32G32B32A32_SFLOAT, 2, 16, 4, "RGBA32F"},
        {VK_FORMAT_B10G11R11_UFLOAT_PACK32, 4, 4, 3, "R11G11B10F"}, {VK_FORMAT_E5B9G9R9_UFLOAT_PACK32, 5, 4, 3, "RGB9E5"},
        {VK_FORMAT_R8_UNORM, 3, 1, 1, "R8"}, {VK_FORMAT_R8G8_UNORM, 3, 2, 2, "RG8"}, {VK_FORMAT_R8G8B8A8_UNORM, 3, 4, 4, "RGBA8"}, {VK_FORMAT_R8G8B8A8_SRGB, 9, 4, 4, "RGBA8_SRGB"},
        {VK_FORMAT_B8G8R8A8_UNORM, 10, 4, 4, "BGRA8"}, {VK_FORMAT_B8G8R8A8_SRGB, 11, 4, 4, "BGRA8_SRGB"}, {VK_FORMAT_A2B10G10R10_UNORM_PACK32, 7, 4, 4, "RGB10A2"},
        {VK_FORMAT_R16_UNORM, 6, 2, 1, "R16"}, {VK_FORMAT_R16G16_UNORM, 6, 4, 2, "RG16"}, {VK_FORMAT_R16G16B16A16_UNORM, 6, 8, 4, "RGBA16"},
        {VK_FORMAT_R32_UINT, 8, 4, 1, "R32UI"}, {VK_FORMAT_R32G32_UINT, 8, 8, 2, "RG32UI"}, {VK_FORMAT_R32G32B32A32_UINT, 8, 16, 4, "RGBA32UI"},
        {VK_FORMAT_D16_UNORM, 0, 2, 1, "D16"}, {VK_FORMAT_X8_D24_UNORM_PACK32, 0, 4, 1, "D24"}, {VK_FORMAT_D32_SFLOAT, 0, 4, 1, "D32F"}, {VK_FORMAT_D24_UNORM_S8_UINT, 0, 4, 1, "D24S8"},
        {VK_FORMAT_D32_SFLOAT_S8_UINT, 0, 8, 1, "D32FS8"}, {VK_FORMAT_S8_UINT, 0, 1, 1, "S8"},
        {VK_FORMAT_R16_UINT, 12, 2, 1, "R16UI"}, {VK_FORMAT_R16G16_UINT, 12, 4, 2, "RG16UI"}, {VK_FORMAT_R8_UINT, 13, 1, 1, "R8UI"}, {VK_FORMAT_R8G8B8A8_UINT, 13, 4, 4, "RGBA8UI"},
    };
    for (size_t i = 0; i < sizeof t / sizeof t[0]; i++) if (t[i].f == f) { *bpp = t[i].bpp; *nch = t[i].nch; if (name) *name = t[i].n; return t[i].fk; }
    *bpp = 0; *nch = 0; if (name) *name = "?";
    return 0;
}
static int ld_float_fk(int fk) { return fk == 1 || fk == 2 || fk == 4 || fk == 5; }
static float ld_h2f(uint32_t h, int *cls) {        /* cls: 0 normal / zero, 1 denormal, 2 inf, 3 nan */
    uint32_t e = h >> 10 & 31, m = h & 1023; float s = (h & 0x8000) ? -1.0f : 1.0f;
    if (e == 31) { *cls = m ? 3 : 2; return m ? NAN : s * INFINITY; }
    if (!e) { *cls = m ? 1 : 0; return s * ldexpf((float)m, -24); }
    *cls = 0; return s * ldexpf((float)(m | 1024), (int)e - 25);
}
static float ld_uf(uint32_t v, int mbits, int *cls) {      /* unsigned small float: 5-bit exponent, mbits mantissa */
    uint32_t e = v >> mbits & 31, m = v & ((1u << mbits) - 1);
    if (e == 31) { *cls = m ? 3 : 2; return m ? NAN : INFINITY; }
    if (!e) { *cls = m ? 1 : 0; return ldexpf((float)m, -14 - mbits); }
    *cls = 0; return ldexpf((float)(m | (1u << mbits)), (int)e - 15 - mbits);
}
static void ld_val(LdEnt *e, int c, float v, int cls) {
    if (cls == 3 || v != v) { e->nan++; return; }
    if (cls == 2 || isinf(v)) { e->inf++; return; }
    if (cls == 1) e->den++;
    if (v < 0) e->neg++;
    if (fabsf(v) >= 60000.0f) e->huge++;
    if (!e->n[c] || v < e->mn[c]) e->mn[c] = v;
    if (!e->n[c] || v > e->mx[c]) e->mx[c] = v;
    e->sum[c] += v; e->n[c]++;
}
static void ld_analyse(LdEnt *e) {
    const uint8_t *p = g_ld.map + e->off;
    uint64_t texels = e->bpp ? e->bytes / (uint64_t)e->bpp : 0;
    for (uint64_t i = 0; i < texels; i++, p += e->bpp) {
        int cls = 0;
        switch (e->fk) {
        case 1: for (int c = 0; c < e->nch; c++) { uint16_t h; memcpy(&h, p + 2 * c, 2); float v = ld_h2f(h, &cls); ld_val(e, c, v, cls); } break;
        case 2: for (int c = 0; c < e->nch; c++) { uint32_t u; float v; memcpy(&u, p + 4 * c, 4); memcpy(&v, &u, 4); cls = (u & 0x7f800000u) == 0x7f800000u ? ((u & 0x7fffffu) ? 3 : 2) : ((u & 0x7f800000u) == 0 && (u & 0x7fffffu)) ? 1 : 0; ld_val(e, c, v, cls); } break;
        case 4: { uint32_t u; memcpy(&u, p, 4); float v;
                  v = ld_uf(u & 0x7ff, 6, &cls); ld_val(e, 0, v, cls); v = ld_uf(u >> 11 & 0x7ff, 6, &cls); ld_val(e, 1, v, cls); v = ld_uf(u >> 22 & 0x3ff, 5, &cls); ld_val(e, 2, v, cls); break; }
        case 5: { uint32_t u; memcpy(&u, p, 4); float sc = ldexpf(1.0f, (int)(u >> 27) - 15 - 9);
                  ld_val(e, 0, (float)(u & 511) * sc, 0); ld_val(e, 1, (float)(u >> 9 & 511) * sc, 0); ld_val(e, 2, (float)(u >> 18 & 511) * sc, 0); break; }
        case 3: case 9: for (int c = 0; c < e->nch; c++) ld_val(e, c, (float)p[c] / 255.0f, 0); break;
        case 10: case 11: ld_val(e, 0, (float)p[2] / 255.0f, 0); ld_val(e, 1, (float)p[1] / 255.0f, 0); ld_val(e, 2, (float)p[0] / 255.0f, 0); ld_val(e, 3, (float)p[3] / 255.0f, 0); break;
        case 6: for (int c = 0; c < e->nch; c++) { uint16_t h; memcpy(&h, p + 2 * c, 2); ld_val(e, c, (float)h / 65535.0f, 0); } break;
        case 7: { uint32_t u; memcpy(&u, p, 4); ld_val(e, 0, (float)(u & 1023) / 1023.0f, 0); ld_val(e, 1, (float)(u >> 10 & 1023) / 1023.0f, 0); ld_val(e, 2, (float)(u >> 20 & 1023) / 1023.0f, 0); ld_val(e, 3, (float)(u >> 30) / 3.0f, 0); break; }
        case 8: for (int c = 0; c < e->nch; c++) { uint32_t u; memcpy(&u, p + 4 * c, 4); ld_val(e, c, (float)u, 0); } break;
        case 12: for (int c = 0; c < e->nch; c++) { uint16_t h; memcpy(&h, p + 2 * c, 2); ld_val(e, c, (float)h, 0); } break;
        case 13: for (int c = 0; c < e->nch; c++) ld_val(e, c, (float)p[c], 0); break;
        }
    }
}
static double ld_mean(const LdEnt *e, int c) { return e->n[c] ? e->sum[c] / e->n[c] : 0.0; }
static uint64_t ld_key(const LdEnt *e) { return e->kind == 1 ? e->hp[0][0] ^ e->bytes : e->hp[0][1] ^ e->hp[0][0] * 31; }

/* ---- the host buffer of a capture */
static int ld_buf_make(uint64_t size) {
    VkBufferCreateInfo bi = {VK_STRUCTURE_TYPE_BUFFER_CREATE_INFO};
    VkMemoryRequirements rq;
    bi.size = size; bi.usage = VK_BUFFER_USAGE_TRANSFER_DST_BIT; bi.sharingMode = VK_SHARING_MODE_EXCLUSIVE;
    if (vkCreateBuffer(V.device, &bi, NULL, &g_ld.buf) != VK_SUCCESS) { g_ld.buf = NULL; return 0; }
    vkGetBufferMemoryRequirements(V.device, g_ld.buf, &rq);
    uint32_t type = 32;
    VkFlags want[2] = {VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT | VK_MEMORY_PROPERTY_HOST_CACHED_BIT, VK_MEMORY_PROPERTY_HOST_VISIBLE_BIT | VK_MEMORY_PROPERTY_HOST_COHERENT_BIT};
    for (int k = 0; k < 2 && type == 32; k++)      /* cached first: the CPU reads it */
        for (uint32_t i = 0; i < D.mp.memoryTypeCount && type == 32; i++) if ((rq.memoryTypeBits >> i & 1) && (D.mp.memoryTypes[i].propertyFlags & want[k]) == want[k] && vk_memtype_ok(i)) type = i;
    VkMemoryAllocateInfo ai = {VK_STRUCTURE_TYPE_MEMORY_ALLOCATE_INFO};
    ai.allocationSize = rq.size; ai.memoryTypeIndex = type;
    void *map = NULL;
    if (type == 32 || vkAllocateMemory(V.device, &ai, NULL, &g_ld.mem) != VK_SUCCESS) { vkDestroyBuffer(V.device, g_ld.buf, NULL); g_ld.buf = NULL; g_ld.mem = NULL; return 0; }
    if (vkBindBufferMemory(V.device, g_ld.buf, g_ld.mem, 0) != VK_SUCCESS || vkMapMemory(V.device, g_ld.mem, 0, VK_WHOLE_SIZE, 0, &map) != VK_SUCCESS) {
        vkDestroyBuffer(V.device, g_ld.buf, NULL); vkFreeMemory(V.device, g_ld.mem, NULL); g_ld.buf = NULL; g_ld.mem = NULL; return 0;
    }
    g_ld.map = map; g_ld.size = size; g_ld.used = 0;
    return 1;
}
static void ld_buf_free(void) {                    /* every command buffer that wrote it has finished */
    if (!g_ld.buf) return;
    vkDestroyBuffer(V.device, g_ld.buf, NULL); vkFreeMemory(V.device, g_ld.mem, NULL);      /* freeing unmaps */
    g_ld.buf = NULL; g_ld.mem = NULL; g_ld.map = NULL;
}
static int ld_space(LdEnt *e) {
    uint64_t off = (g_ld.used + 15) & ~(uint64_t)15;
    if (!e->bytes || off + e->bytes > g_ld.size) return 0;
    e->off = off; g_ld.used = off + e->bytes;
    return 1;
}
static void ld_host_barrier(void) {
    VkMemoryBarrier mb = {VK_STRUCTURE_TYPE_MEMORY_BARRIER};
    mb.srcAccessMask = VK_ACCESS_TRANSFER_WRITE_BIT; mb.dstAccessMask = VK_ACCESS_HOST_READ_BIT;
    vkCmdPipelineBarrier(R.cmd, VK_PIPELINE_STAGE_TRANSFER_BIT, VK_PIPELINE_STAGE_HOST_BIT, 0, 1, &mb, 0, NULL, 0, NULL);
}
static LdEnt *ld_new(int kind) {
    if (g_ld.nent == LD_MAXE) { g_ld.dropped++; return NULL; }
    LdEnt *e = &g_ld.e[g_ld.nent++];
    memset(e, 0, sizeof *e);
    e->kind = kind; e->frame = g_ld.fidx; e->order = g_ld.order++; e->d0 = (int)R.frame_draws; e->ref_ord = -1;
    return e;
}
/* the copy of one level / layer of an image into the host buffer (outside a pass); the image's layout is put back */
static int ld_atlas(void) {                        /* the RGBA32F image the large targets are reduced into (device memory, this capture only) */
    if (g_ld.atl) return 1;
    if (g_ld.atl_fail || !fmt_supported(VK_FORMAT_R32G32B32A32_SFLOAT, VK_FORMAT_FEATURE_BLIT_DST_BIT | VK_FORMAT_FEATURE_TRANSFER_SRC_BIT)) { g_ld.atl_fail = 1; return 0; }
    VkImageCreateInfo ci = {VK_STRUCTURE_TYPE_IMAGE_CREATE_INFO};
    VkMemoryRequirements rq;
    ci.imageType = VK_IMAGE_TYPE_2D; ci.format = VK_FORMAT_R32G32B32A32_SFLOAT; ci.extent.width = 1024; ci.extent.height = 1024; ci.extent.depth = 1; ci.mipLevels = 1; ci.arrayLayers = 1;
    ci.samples = VK_SAMPLE_COUNT_1_BIT; ci.tiling = VK_IMAGE_TILING_OPTIMAL; ci.usage = VK_IMAGE_USAGE_TRANSFER_SRC_BIT | VK_IMAGE_USAGE_TRANSFER_DST_BIT;
    ci.sharingMode = VK_SHARING_MODE_EXCLUSIVE; ci.initialLayout = VK_IMAGE_LAYOUT_UNDEFINED;
    if (vkCreateImage(V.device, &ci, NULL, &g_ld.atl) != VK_SUCCESS) { g_ld.atl = NULL; g_ld.atl_fail = 1; return 0; }
    vkGetImageMemoryRequirements(V.device, g_ld.atl, &rq);
    g_ld.atl_mem = vmem_alloc(rq, VK_MEMORY_PROPERTY_DEVICE_LOCAL_BIT, 0);
    VKC(vkBindImageMemory(V.device, g_ld.atl, g_ld.atl_mem.mem, g_ld.atl_mem.off));
    cmd_begin();
    image_barrier(g_ld.atl, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_UNDEFINED, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
    g_ld.atl_n = 0;
    return 1;
}
static void ld_atlas_free(void) {                  /* every command buffer that used it has finished */
    if (!g_ld.atl) return;
    vkDestroyImage(V.device, g_ld.atl, NULL); vmem_free(g_ld.atl_mem);
    g_ld.atl = NULL; g_ld.atl_n = 0;
}
static void ld_copy_reduced(LdEnt *e, VTex *vt) {  /* a large target: blitted (linear) to 1 / red of its size into a tile of the atlas, the tile copied */
    if (g_ld.atl_n >= 28 || !ld_space(e)) return;
    VkFormatProperties fp; vkGetPhysicalDeviceFormatProperties(V.gpu, vt->fmt, &fp);
    if (!(fp.optimalTilingFeatures & VK_FORMAT_FEATURE_BLIT_SRC_BIT)) return;
    int tx = (g_ld.atl_n % 4) * 256, ty = (g_ld.atl_n / 4) * 144;
    g_ld.atl_n++;
    cmd_begin();
    VkImageLayout was = vt->layout;
    vt_layout(vt, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    VkImageBlit b;
    memset(&b, 0, sizeof b);
    b.srcSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; b.srcSubresource.mipLevel = (uint32_t)e->level; b.srcSubresource.baseArrayLayer = (uint32_t)e->layer; b.srcSubresource.layerCount = 1;
    b.srcOffsets[1].x = e->ow; b.srcOffsets[1].y = e->oh; b.srcOffsets[1].z = 1;
    b.dstSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; b.dstSubresource.layerCount = 1;
    b.dstOffsets[0].x = tx; b.dstOffsets[0].y = ty; b.dstOffsets[1].x = tx + e->w; b.dstOffsets[1].y = ty + e->h; b.dstOffsets[1].z = 1;
    vkCmdBlitImage(R.cmd, vt->img, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, g_ld.atl, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, 1, &b,
                   (fp.optimalTilingFeatures & VK_FORMAT_FEATURE_SAMPLED_IMAGE_FILTER_LINEAR_BIT) ? VK_FILTER_LINEAR : VK_FILTER_NEAREST);
    if (was != VK_IMAGE_LAYOUT_UNDEFINED) vt_layout(vt, was);
    image_barrier(g_ld.atl, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    VkBufferImageCopy c;
    memset(&c, 0, sizeof c);
    c.bufferOffset = e->off;
    c.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; c.imageSubresource.layerCount = 1;
    c.imageOffset.x = tx; c.imageOffset.y = ty; c.imageExtent.width = (uint32_t)e->w; c.imageExtent.height = (uint32_t)e->h; c.imageExtent.depth = 1;
    vkCmdCopyImageToBuffer(R.cmd, g_ld.atl, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, g_ld.buf, 1, &c);
    image_barrier(g_ld.atl, VK_IMAGE_ASPECT_COLOR_BIT, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, VK_IMAGE_LAYOUT_TRANSFER_DST_OPTIMAL);
    ld_host_barrier();
    e->read = 1;
}
static void ld_copy_image(LdEnt *e, VTex *vt) {
    if (e->red) { ld_copy_reduced(e, vt); return; }
    if (!e->fk || !ld_space(e)) return;
    cmd_begin();
    VkImageLayout was = vt->layout;
    vt_layout(vt, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    VkBufferImageCopy c;
    memset(&c, 0, sizeof c);
    c.bufferOffset = e->off;
    c.imageSubresource.aspectMask = VK_IMAGE_ASPECT_COLOR_BIT; c.imageSubresource.mipLevel = (uint32_t)e->level; c.imageSubresource.baseArrayLayer = (uint32_t)e->layer; c.imageSubresource.layerCount = 1;
    c.imageExtent.width = (uint32_t)e->w; c.imageExtent.height = (uint32_t)e->h; c.imageExtent.depth = 1;
    vkCmdCopyImageToBuffer(R.cmd, vt->img, VK_IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL, g_ld.buf, 1, &c);
    if (was != VK_IMAGE_LAYOUT_UNDEFINED) vt_layout(vt, was);
    ld_host_barrier();
    e->read = 1;
}
/* an entry for level / layer of texture t: 0 when it is not looked at */
static LdEnt *ld_tex_entry(int kind, Tex *t, int level, int layer, int allow_big) {
    VTex *vt = t ? t->vk : NULL;
    if (!vt || vt->samples != 1 || vt->is3d || vt->aspect != VK_IMAGE_ASPECT_COLOR_BIT || vt->decode || vt->expand) return NULL;
    int w = lvl_(vt->w, level), h = lvl_(vt->h, level), bpp, nch, fk = ld_fmt(vt->fmt, &bpp, &nch, NULL);
    int big = (long long)w * h > g_ld.small_tx;
    int red = 0;
    if (big) {                                     /* frame 0 only: float targets whole (exact NaN / Inf counts) up to 16 MB, the others reduced */
        if (!allow_big || !g_ld.big || g_ld.fidx != 0 || !fk || fk == 8 || fk == 12 || fk == 13) return NULL;
        if (!ld_float_fk(fk) || (uint64_t)w * h * (uint64_t)bpp > (16u << 20)) { red = 8; while (w / red > 256 || h / red > 144) red *= 2; if (!ld_atlas()) return NULL; }
    }
    if (level >= vt->levels || layer >= vt->layers) return NULL;
    LdEnt *e = ld_new(kind);
    if (!e) return NULL;
    e->w = w; e->h = h; e->level = level; e->layer = layer; e->big = big; e->fmt = vt->fmt; e->nvfmt = (int)t->b.format; e->fk = fk; e->bpp = bpp; e->nch = nch; e->ow = w; e->oh = h;
    if (red) { e->red = red; e->w = w / red > 0 ? w / red : 1; e->h = h / red > 0 ? h / red : 1; e->fk = 2; e->bpp = 16; e->nch = nch < 1 ? 4 : nch == 3 ? 3 : nch; }
    e->bytes = (uint64_t)e->w * e->h * (uint64_t)e->bpp; e->vt = vt;
    return e;
}
/* pass_begin, R.col bound and their images made (w x h: the pass) */
static void ld_pass_begin(void) {
    g_ld_t0 = tk();
    g_ld.passes++;
    if (g_ld.fidx == 0 && g_ld.npass < LD_NPASS) {      /* frame 0: every pass, for the chain's shape */
        LdPass *q = &g_ld.pass[g_ld.npass++];
        Tex *c0 = R.ncol ? R.col[0].tex : NULL, *dt = R.depth.tex && !R.depth.tex->dead ? R.depth.tex : NULL;
        VTex *v0 = c0 && !c0->dead ? c0->vk : dt ? dt->vk : NULL;
        q->w = (unsigned short)R.pass_w; q->h = (unsigned short)R.pass_h; q->fmt = v0 ? (int)v0->fmt : 0; q->d0 = (int)R.frame_draws; q->ncol = (unsigned char)R.ncol; q->depth = dt != NULL;
        if (v0) { int lv = c0 && !c0->dead ? target_level(&R.col[0]) : target_level(&R.depth); q->w = (unsigned short)lvl_(v0->w, lv); q->h = (unsigned short)lvl_(v0->h, lv); }
    }
    for (int i = 0; i < R.ncol && i < 8; i++) {
        Tex *t = R.col[i].tex;
        if (!t || t->dead) continue;
        int level = target_level(&R.col[i]), layer = (R.col[i].view.set & 0x82) == 0x82 ? R.col[i].view.lay0 : 0;
        LdEnt *e = ld_tex_entry(0, t, level, layer, 1);
        if (e) g_ld.pe[g_ld_npe++] = (int)(e - g_ld.e);
    }
    ld_t_end();
}
static void ld_pair(LdEnt *e, uint64_t hb0, uint64_t hb1, uint64_t hp0, uint64_t hp1) {
    for (int k = 0; k < e->ndist && k < LD_PAIRS; k++) if (e->hp[k][0] == hp0 && e->hp[k][1] == hp1 && e->hb[k][1] == hb1) return;
    if (e->ndist < LD_PAIRS) { e->hb[e->ndist][0] = hb0; e->hb[e->ndist][1] = hb1; e->hp[e->ndist][0] = hp0; e->hp[e->ndist][1] = hp1; }
    e->ndist++;
}
static void ld_dump_queue(const Prog *p, int s, const VStage *v, uint64_t hp) {
    if (!g_ld.dump || !p || !v || !hp || g_ld.ndump >= LD_DUMP_MAX) return;
    for (int i = 0; i < g_ld.ndump; i++) if (g_ld.dq[i].hp == hp && g_ld.dq[i].stage == s && !memcmp(g_ld.dq[i].key, v->key, 16) && g_ld.dq[i].fo == v->fo) return;
    LdDump *d = &g_ld.dq[g_ld.ndump];
    if (!ssrc_of(&d->src, p, s)) return;
    d->code = malloc((size_t)d->src.code_len); d->cdata = d->src.cdata ? malloc(d->src.cdata_size) : NULL;
    if (!d->code || (d->src.cdata && !d->cdata)) { free(d->code); free(d->cdata); return; }
    memcpy(d->code, d->src.code, (size_t)d->src.code_len); if (d->cdata) memcpy(d->cdata, d->src.cdata, d->src.cdata_size);
    d->src.code = d->code; d->src.cdata = d->cdata;
    d->stage = s; d->hp = hp; d->hb = v->hb; memcpy(d->key, v->key, 16); d->fo = v->fo;
    g_ld.ndump++;
}
static uint64_t ld_hp(const VStage *v, const Prog *p, int s) {
    SSrc src;
    if (v && v->hp) return v->hp;
    return p && ssrc_of(&src, p, s) ? ssrc_hpre(&src) : 0;
}
/* after a draw recorded inside a pass with entries (g_dvs / g_dfs: its stages) */
static void ld_draw(void) {
    g_ld_t0 = tk();
    uint64_t hp0 = ld_hp(g_dvs, R.prog[0], 0), hp1 = g_dfs ? ld_hp(g_dfs, R.prog[1], 1) : 0;
    for (int i = 0; i < g_ld_npe; i++) { LdEnt *e = &g_ld.e[g_ld.pe[i]]; e->draws++; ld_pair(e, g_dvs ? g_dvs->hb : 0, g_dfs ? g_dfs->hb : 0, hp0, hp1); }
    ld_dump_queue(R.prog[0], 0, g_dvs, hp0);
    if (g_dfs) ld_dump_queue(R.prog[1], 1, g_dfs, hp1);
    ld_t_end();
}
/* pass_end, after vkCmdEndRendering */
static void ld_pass_done(void) {
    g_ld_t0 = tk();
    int n = g_ld_npe;
    g_ld_npe = 0;
    for (int i = 0; i < n; i++) {
        LdEnt *e = &g_ld.e[g_ld.pe[i]];
        if (!e->draws) { e->kind = -1; continue; }      /* clears only: not listed */
        ld_copy_image(e, e->vt);
        e->vt = NULL;
    }
    ld_t_end();
}
/* compute_dispatch_run, after the dispatch and its barrier: the small storage buffers it wrote */
static void ld_dispatch(const Prog *p, VStage *v, const BufRef *bufs) {
    int ib = 0;
    g_ld.dispatches++;
    for (unsigned n = 0; n < 18; n++) if (v->cbufs >> n & 1) ib++;
    for (unsigned k = 0; k < 16; k++) {
        if (!(v->ssbos >> k & 1)) continue;
        const BufRef *b = &bufs[ib++];
        if (!(v->ssbo_w >> k & 1) || b->zero || !b->size || b->size > LD_SMALLBUF || (b->size & 3)) continue;
        if (g_ld.nocomp || g_ld.ncomp[g_ld.fidx & 7]++ >= 16) continue;      /* at most 16 a frame (skinning fills the list otherwise) */
        LdEnt *e = ld_new(1);
        if (!e) return;
        uint64_t hp = ld_hp(v, p, 5);
        e->w = (int)(b->size / 4); e->h = 1; e->fk = 2; e->bpp = 4; e->nch = 1; e->bytes = b->size; e->draws = 1; e->level = (int)k;
        ld_pair(e, v->hb, 0, hp, 0);
        ld_dump_queue(p, 5, v, hp);
        if (!ld_space(e)) continue;
        VkBufferCopy c = {b->off, e->off, b->size};
        vkCmdCopyBuffer(R.cmd, b->buf, g_ld.buf, 1, &c);
        ld_host_barrier();
        e->read = 1;
    }
}
/* a blit / downsample into texture d (kind 2), or a read-back of texture d by the CPU (kind 3), outside a pass */
static void ld_tex_written(int kind, Tex *d, const View *v) {
    if (!d || d->dead) return;
    int level = v && (v->set & 0x81) == 0x81 ? v->lvl0 : 0, layer = v && (v->set & 0x82) == 0x82 ? v->lay0 : 0;
    LdEnt *e = ld_tex_entry(kind, d, level, layer, 0);
    if (!e) return;
    pass_end();
    e->draws = 1;
    ld_copy_image(e, e->vt);
    e->vt = NULL;
}

/* ---- the shader dump (a thread: translator and glslang, as the workers) */
static DWORD WINAPI ld_dump_thread(void *arg) {
    int n = (int)(intptr_t)arg, saved = 0;
    char dir[MAX_PATH + 32], path[MAX_PATH + 80];
    DWORD k = GetModuleFileNameA(NULL, dir, MAX_PATH);
    while (k && dir[k - 1] != '\\' && dir[k - 1] != '/') k--;
    dir[k] = 0;
    strcat(dir, "..\\lumdiag");
    {   char full[MAX_PATH + 32]; DWORD f = GetFullPathNameA(dir, sizeof full, full, NULL); if (f && f < sizeof full) strcpy(dir, full); }      /* build\lumdiag */
    CreateDirectoryA(dir, NULL);
    for (int i = 0; i < n; i++) {
        LdDump *d = &g_ld.dq[i];
        MxIn in; MxOut out;
        const char *sn = d->stage == 5 ? "compute" : d->stage ? "fragment" : "vertex";
        memset(&out, 0, sizeof out);
        mxin_fill(&in, &d->src, d->key, d->fo);
        if (mx_translate(&in, &out) && out.glsl) {
            snprintf(path, sizeof path, "%s\\%s_%016llx_%08x.glsl", dir, sn, (unsigned long long)d->hp, (unsigned)(fnv(0xcbf29ce484222325ULL, d->key, 16) ^ d->fo));
            FILE *fp = fopen(path, "wb");
            if (fp) {
                fprintf(fp, "// SSBU Arena lumdiag: %s stage, code %016llx, hb %016llx, key %x %x %x %x, forced outputs %x, NaN rules %d, exact half %d\n", sn, (unsigned long long)d->hp, (unsigned long long)d->hb,
                        d->key[0], d->key[1], d->key[2], d->key[3], d->fo, in.nanexact, in.f16exact);
                fputs(out.glsl, fp); fclose(fp); saved++;
            }
            uint32_t *words = NULL; size_t count = 0; char log[512];
            if (g_glsl_to_spirv && g_glsl_to_spirv(d->stage, out.glsl, &words, &count, log, sizeof log)) {
                strcpy(path + strlen(path) - 5, ".spv");
                if ((fp = fopen(path, "wb")) != NULL) { fwrite(words, 4, count, fp); fclose(fp); }
                g_spirv_free(words);
            }
            free(out.glsl);
        }
        free(d->code); free(d->cdata); d->code = d->cdata = NULL;
    }
    LDP("nvn_vk_lumdiag: %d of %d shaders saved (GLSL and SPIR-V) in %s; they stay on this PC (only sent if you send them yourself)\n", saved, n, dir);
    fflush(stdout); { FILE *f = ld_file(); if (f) fflush(f); }
    InterlockedExchange((volatile LONG *)&g_ld.dumped, 2);
    return 0;
}

/* ---- the report of a finished capture */
static void ld_fmt_name(const LdEnt *e, char *o, size_t n) { int bpp, nch; const char *nm; ld_fmt(e->fmt, &bpp, &nch, &nm); if (e->kind == 1) snprintf(o, n, "F32"); else if (!strcmp(nm, "?")) snprintf(o, n, "vk%d", (int)e->fmt); else snprintf(o, n, "%s", nm); }
static void ld_print_ent(const LdEnt *e, const char *note) {
    static const char *const kn[] = {"pass", "compute", "blit", "cpu-read"};
    char fn[24], vals[200], pairs[160];
    int o = 0;
    ld_fmt_name(e, fn, sizeof fn);
    if (!e->read) snprintf(vals, sizeof vals, "%s", e->fk ? "not read (capture buffer full)" : "format not decoded");
    else for (int c = 0; c < e->nch && o < (int)sizeof vals - 40; c++) o += snprintf(vals + o, sizeof vals - (size_t)o, "%sc%d %.4g..%.4g ~%.4g", c ? " " : "", c, e->n[c] ? e->mn[c] : 0.0f, e->n[c] ? e->mx[c] : 0.0f, ld_mean(e, c));
    if (e->kind == 1) snprintf(pairs, sizeof pairs, "cs %016llx (code %016llx) binding %d", (unsigned long long)e->hb[0][0], (unsigned long long)e->hp[0][0], e->level);
    else if (e->kind >= 2) snprintf(pairs, sizeof pairs, "-");
    else snprintf(pairs, sizeof pairs, "vs %016llx fs %016llx (code %016llx %016llx)%s", (unsigned long long)e->hb[0][0], (unsigned long long)e->hb[0][1], (unsigned long long)e->hp[0][0], (unsigned long long)e->hp[0][1],
                  e->ndist > 1 ? " +more" : "");
    if (!ld_say()) return;
    char red[24] = "";
    if (e->red) snprintf(red, sizeof red, " (read at 1/%d)", e->red);
    LDP("nvn_vk_lumdiag: c%d f%d #%d %s %dx%d%s %s L%d @%d draws %d (%d pairs): %s | %s | nan %u inf %u den %u neg %u huge %u%s\n", g_ld.ncap, e->frame, e->order, kn[e->kind], e->red ? e->ow : e->w, e->red ? e->oh : e->h, red, fn, e->level, e->d0, e->draws, e->ndist, pairs, vals,
           e->nan, e->inf, e->den, e->neg, e->huge, note);
}
static int ld_differs(const LdEnt *e, const LdRef *r) {
    if ((e->nan + e->inf > 0) != (r->bad > 0)) return 1;
    for (int c = 0; c < e->nch && c < r->nch && c < 3; c++) {      /* colour channels only (alpha varies with what is drawn) */
        double a = fabs(ld_mean(e, c)), b = fabs(r->mean[c]), hi = a > b ? a : b, lo = a > b ? b : a;
        if (hi > 0.02 && (lo * 4.0 < hi)) return 1;
    }
    return 0;
}
static const LdRef *ld_match(const LdRef *ref, int nref, const LdEnt *e, int occ) {
    for (int i = 0; i < nref; i++) if (ref[i].kind == e->kind && ref[i].w == e->w && ref[i].h == e->h && ref[i].level == e->level && ref[i].fmt == (int)e->fmt && ref[i].key == ld_key(e) && ref[i].occ == occ) return &ref[i];
    return NULL;
}
static int ld_occ(int upto, const LdEnt *e) {     /* how many entries of the same frame and key came before e */
    int n = 0;
    for (int i = 0; i < upto; i++) { const LdEnt *q = &g_ld.e[i]; if (q->kind == e->kind && q->frame == e->frame && q->w == e->w && q->h == e->h && q->level == e->level && q->fmt == e->fmt && ld_key(q) == ld_key(e)) n++; }
    return n;
}
static int ld_say_ent(void) { return g_ld.lines < g_ld.maxlines - 1; }      /* one line is kept for the summary */
static void ld_ref_of(const LdEnt *e, int occ, LdRef *r) {
    memset(r, 0, sizeof *r);
    r->kind = e->kind; r->w = e->w; r->h = e->h; r->level = e->level; r->fmt = (int)e->fmt; r->occ = occ; r->bad = (int)(e->nan + e->inf); r->key = ld_key(e); r->nch = e->nch; r->order = e->order;
    for (int c = 0; c < 4; c++) r->mean[c] = ld_mean(e, c);
}
/* the decode of a finished capture and its report (tens of ms for the large targets): a thread of its own; the render
 * thread starts it when the GPU has finished and frees the buffers when it is done (no capture starts meanwhile) */
static void ld_report(void);
static volatile LONG g_ld_analysed;
static HANDLE g_ld_ath;
static double g_ld_an_ms;
static DWORD WINAPI ld_analyse_thread(void *arg) {
    LARGE_INTEGER fq, t0, t1;
    (void)arg;
    QueryPerformanceFrequency(&fq); QueryPerformanceCounter(&t0);
    for (int i = 0; i < g_ld.nent; i++) { LdEnt *e = &g_ld.e[i]; if (e->kind >= 0 && e->read) ld_analyse(e); }
    QueryPerformanceCounter(&t1);
    g_ld_an_ms = (double)(t1.QuadPart - t0.QuadPart) * 1000.0 / (double)fq.QuadPart;
    ld_report();
    InterlockedExchange(&g_ld_analysed, 1);
    return 0;
}
static void ld_report(void) {
    LONG64 t_rep = tk();
    int why = g_ld.why, cls = why == LD_TDARK ? 1 : why == LD_TREC ? 0 : g_ld.cap_mean < 40, other = !cls, listed = 0, ndiff_f[8] = {0}, nent_f[8] = {0};
    static LdRef cur[LD_MAXE];
    int ncur = 0, first_bad = -1, first_diff = -1, full = !g_ld.listed[why];
    g_ld.listed[why] = 1;
    for (int i = 0; i < g_ld.nent; i++) {
        LdEnt *e = &g_ld.e[i];
        if (e->kind < 0) continue;
        int occ = ld_occ(i, e);
        if (e->nan + e->inf + e->huge) e->flag |= 1;
        if (e->frame == 0) {
            if (ncur < LD_MAXE) ld_ref_of(e, occ, &cur[ncur++]);
            const LdRef *r = g_ld.nref[other] ? ld_match(g_ld.ref[other], g_ld.nref[other], e, occ) : NULL;
            if (r && e->read && ld_differs(e, r)) { e->flag |= 2; e->ref_mean = (float)r->mean[0]; e->ref_ord = r->order; }
        } else {
            const LdRef *r = ld_match(cur, ncur, e, occ);
            if (r && e->read && ld_differs(e, r)) { e->flag |= 4; e->ref_mean = (float)r->mean[0]; e->ref_ord = r->order; }
            if (e->frame < 8) { nent_f[e->frame]++; if (e->flag & 4) ndiff_f[e->frame]++; }
        }
        if (e->frame == 0 && (e->flag & 1) && first_bad < 0) first_bad = i;
        if (e->frame == 0 && (e->flag & 2) && first_diff < 0) first_diff = i;
    }
    int n = 0, ncl = 0;
    for (int i = 0; i < g_ld.nent; i++) { if (g_ld.e[i].kind >= 0) n++; else ncl++; }
    if (ld_say())
        LDP("nvn_vk_lumdiag: capture c%d: %s, mean brightness %d of 255 at fight frame %d (present %lld); %d frame(s): %d entries (%d passes with clears only not listed, %d dropped over %d), %d passes, %d dispatches, %llu KB read%s; compared with %s (c%d); %s\n",
               g_ld.ncap, ld_why_name[why], g_ld.cap_mean, g_ld.cap_fight, (long long)g_ld.cap_frame, g_ld.nframes, n, ncl, g_ld.dropped, LD_MAXE, g_ld.passes, g_ld.dispatches, (unsigned long long)(g_ld.used >> 10),
               g_ld.big ? " (large targets of frame 0 included: float ones whole, the others reduced)" : "", g_ld.nref[other] ? (other ? "the last dark capture" : "the last bright capture") : "nothing yet", g_ld.nref[other] ? g_ld.ref_cap[other] : 0,
               full ? "every entry of frame 0 listed, later frames only what changed" : "only entries with bad values or changes listed");
    if (full && g_ld.npass && ld_say_ent()) {     /* the passes of frame 0: size / format / colour targets (D = depth) / draws */
        static char pl[LD_NPASS * 40 + 64];
        int o = 0;
        for (int i = 0; i < g_ld.npass; i++) {
            const LdPass *q = &g_ld.pass[i];
            int bpp, nch, dr = (i + 1 < g_ld.npass ? g_ld.pass[i + 1].d0 : g_ld.fd_end) - q->d0; const char *nm;
            ld_fmt((VkFormat)q->fmt, &bpp, &nch, &nm);
            if (!strcmp(nm, "?")) o += snprintf(pl + o, sizeof pl - (size_t)o, " %dx%d/vk%d/%d%s/%d", q->w, q->h, q->fmt, q->ncol, q->depth ? "D" : "", dr);
            else o += snprintf(pl + o, sizeof pl - (size_t)o, " %dx%d/%s/%d%s/%d", q->w, q->h, nm, q->ncol, q->depth ? "D" : "", dr);
        }
        g_ld.lines++;
        LDP("nvn_vk_lumdiag: c%d f0 passes in order (size/format/colour targets, D = depth/draws):%s\n", g_ld.ncap, pl);
    }
    for (int i = 0; i < g_ld.nent && ld_say_ent(); i++) {
        LdEnt *e = &g_ld.e[i];
        char note[96];
        if (e->kind < 0) continue;
        if (!((e->frame == 0 && full && e->kind != 1) || e->flag)) continue;      /* compute buffers (skinning): only with bad values or changes */
        note[0] = 0;
        if (e->flag & 1) strcat(note, " <-- NaN/Inf/huge");
        if (e->flag & 2) snprintf(note + strlen(note), sizeof note - strlen(note), " <-- DIFFERS from c%d #%d (c0 ~%.4g)", g_ld.ref_cap[other], e->ref_ord, e->ref_mean);
        if (e->flag & 4) snprintf(note + strlen(note), sizeof note - strlen(note), " <-- changed from f0 #%d (c0 ~%.4g)", e->ref_ord, e->ref_mean);
        ld_print_ent(e, note); listed++;
        if (i == first_bad && e->kind == 0 && e->ndist > 1 && ld_say_ent()) {      /* the first pass with bad values: every shader pair drawn in it */
            char buf[LD_PAIRS * 36 + 64]; int o = 0;
            for (int k = 0; k < e->ndist && k < LD_PAIRS; k++) o += snprintf(buf + o, sizeof buf - (size_t)o, " %016llx/%016llx", (unsigned long long)e->hp[k][0], (unsigned long long)e->hp[k][1]);
            g_ld.lines++;
            LDP("nvn_vk_lumdiag: c%d #%d: the %d shader pairs of that pass (code vertex/fragment)%s:%s\n", g_ld.ncap, e->order, e->ndist, e->ndist > LD_PAIRS ? " (first 16)" : "", buf);
        }
    }
    {   int nc = 0, ncb = 0; float cmn = 0, cmx = 0;
        for (int i = 0; i < g_ld.nent; i++) { const LdEnt *e = &g_ld.e[i]; if (e->kind != 1 || !e->read) continue; if (!nc || e->mn[0] < cmn) cmn = e->mn[0]; if (!nc || e->mx[0] > cmx) cmx = e->mx[0]; nc++; ncb += (e->flag & 1) != 0; }
        if (nc && ld_say_ent()) { g_ld.lines++; LDP("nvn_vk_lumdiag: c%d compute: %d small storage buffers written by compute read (at most 16 a frame), %d with NaN/Inf/huge values, values %.4g..%.4g\n", g_ld.ncap, nc, ncb, cmn, cmx); }
    }
    for (int f = 1; f < g_ld.nframes && f < 8 && ld_say_ent(); f++) { g_ld.lines++; LDP("nvn_vk_lumdiag: c%d f%d: %d entries, %d changed from frame 0 (mean more than 3x apart or NaN/Inf)\n", g_ld.ncap, f, nent_f[f], ndiff_f[f]); }
    if (ld_say()) {
        char a[200], b[220];
        if (first_bad >= 0) { const LdEnt *e = &g_ld.e[first_bad]; snprintf(a, sizeof a, "#%d (%dx%d, fs code %016llx%s: nan %u inf %u huge %u)", e->order, e->red ? e->ow : e->w, e->red ? e->oh : e->h, (unsigned long long)(e->kind == 1 ? e->hp[0][0] : e->hp[0][1]), e->kind == 1 ? " compute" : "", e->nan, e->inf, e->huge); }
        else snprintf(a, sizeof a, "none");
        if (first_diff >= 0) { const LdEnt *e = &g_ld.e[first_diff]; snprintf(b, sizeof b, "#%d (%dx%d, fs code %016llx: c0 ~%.4g here, ~%.4g in c%d #%d)", e->order, e->red ? e->ow : e->w, e->red ? e->oh : e->h, (unsigned long long)(e->kind == 1 ? e->hp[0][0] : e->hp[0][1]), ld_mean(e, 0), e->ref_mean, g_ld.ref_cap[other], e->ref_ord); }
        else snprintf(b, sizeof b, g_ld.nref[other] ? "none" : "no capture of the other kind yet");
        LDP("nvn_vk_lumdiag: c%d summary: first entry with NaN/Inf/huge values: %s | first entry that differs from the %s capture: %s\n", g_ld.ncap, a, other ? "dark" : "bright", b);
    }
    memcpy(g_ld.ref[cls], cur, sizeof cur[0] * (size_t)ncur); g_ld.nref[cls] = ncur; g_ld.ref_cap[cls] = g_ld.ncap;
    if (g_ld.dump && g_ld.ndump && g_ld.lines < g_ld.maxlines) {
        HANDLE h;
        g_ld.lines++;                              /* the thread's one line */
        g_ld.dumped = 1;
        h = CreateThread(NULL, 0, ld_dump_thread, (void *)(intptr_t)g_ld.ndump, 0, NULL);
        if (h) { SetThreadPriority(h, THREAD_PRIORITY_LOWEST); CloseHandle(h); } else { g_ld.dumped = 0; for (int i = 0; i < g_ld.ndump; i++) { free(g_ld.dq[i].code); free(g_ld.dq[i].cdata); } g_ld.ndump = 0; }
    } else { for (int i = 0; i < g_ld.ndump; i++) { free(g_ld.dq[i].code); free(g_ld.dq[i].cdata); } g_ld.ndump = 0; }
    g_ld.dump = 0;
    {   LARGE_INTEGER fq; QueryPerformanceFrequency(&fq);
        if (ld_say()) LDP("nvn_vk_lumdiag: c%d cost: render thread %.2f ms recording the copies; on a thread of their own: %.1f ms decoding the values, %.1f ms for this report\n", g_ld.ncap, g_ld_rec_ms, g_ld_an_ms, (double)(tk() - t_rep) * 1000.0 / (double)fq.QuadPart); }
    fflush(stdout);
    { FILE *f = ld_file(); if (f) fflush(f); }
}

/* ---- when */
static int ld_trigger(int why, int mean, LONG64 frame) {
    if (g_ld.state || g_ld.pending) return 0;
    if (g_ld.lines >= g_ld.maxlines - 4) return 0;
    if (why == LD_REF && g_ld.lines >= g_ld.maxlines / 2) return 0;
    g_ld.pending = why; g_ld.pend_mean = mean; g_ld.pend_frame = frame; g_ld.pend_fight = g_ld.fight_n;
    return 1;
}
/* fl_analyse: every analysed frame (8 presents late) */
static void ld_lum(int fight, int draws, int mean, LONG64 frame) {
    if (!ld_on()) return;
    if (!fight) { g_ld.fight_n = 0; g_ld.dark_run = g_ld.bright_run = 0; return; }
    if (draws < 120) return;                       /* the loading screen before the fight (fight already set) and pauses: not counted */
    g_ld.fight_n++;
    if (mean < g_ld.dark_thr) { g_ld.dark_run++; g_ld.bright_run = 0; }
    else { g_ld.dark_run = 0; if (mean >= 40) g_ld.bright_run++; else g_ld.bright_run = 0; }
    if (g_ld.fight_n == 60) ld_trigger(LD_REF, mean, frame);
    if (g_ld.test && g_ld.fight_n >= 180 && g_ld.fight_n < 400 && !g_ld.listed[LD_TDARK]) ld_trigger(LD_TDARK, mean, frame);
    if (g_ld.test && g_ld.fight_n >= 600 && g_ld.fight_n < 800 && !g_ld.listed[LD_TREC]) ld_trigger(LD_TREC, mean, frame);
    if (g_ld.armed && g_ld.dark_run >= 60 && ld_trigger(LD_DARK, mean, frame)) { g_ld.armed = 0; g_ld.want_rec = 1; }
    if (g_ld.bright_run >= 60) { g_ld.armed = 1; if (g_ld.want_rec && ld_trigger(LD_REC, mean, frame)) g_ld.want_rec = 0; }
}
static int ld_gpu_done(uint64_t ser) {
    if (R.serial < ser) return 0;                  /* not submitted yet */
    for (int i = 0; i < NSLOT; i++) { Slot *s = &R.slot[i]; if (s->pending && s->serial <= ser && vkGetFenceStatus(V.device, s->fence) != VK_SUCCESS) return 0; }
    return 1;
}
/* vk_present2, after the frame's last pass has ended: a frame of a capture is over, a capture begins, a finished one is read */
static void ld_frame_end(void) {
    if (g_ld.state == 2 && ld_gpu_done(g_ld.wait_serial)) {      /* the GPU has finished: the decode starts */
        g_ld_analysed = 0;
        g_ld_ath = CreateThread(NULL, 0, ld_analyse_thread, NULL, 0, NULL);
        if (!g_ld_ath) ld_analyse_thread(NULL);
        g_ld.state = 3;
    }
    if (g_ld.state == 3 && g_ld_analysed) {
        if (g_ld_ath) { WaitForSingleObject(g_ld_ath, INFINITE); CloseHandle(g_ld_ath); g_ld_ath = NULL; }
        ld_buf_free(); ld_atlas_free();
        g_ld.state = 0;
    }
    if (g_ld.state == 1) {
        if (g_ld.fidx == 0) g_ld.fd_end = (int)R.frame_draws;
        if (++g_ld.fidx >= g_ld.nframes) { g_ld_cap = 0; g_ld_npe = 0; g_ld.state = 2; g_ld.wait_serial = R.recording ? R.serial + 1 : R.serial; }
    } else if (g_ld.state == 0 && g_ld.pending) {
        int why = g_ld.pending;
        g_ld.pending = 0;
        g_ld.big = !g_ld.nobig;
        if (!ld_buf_make(g_ld.big ? (64ull << 20) : (8ull << 20))) { static int said; if (!said++ && ld_say()) LDP("nvn_vk_lumdiag: no host buffer for a capture: diagnosis skipped\n"); return; }
        g_ld.why = why; g_ld.cap_mean = g_ld.pend_mean; g_ld.cap_frame = g_ld.pend_frame; g_ld.cap_fight = g_ld.pend_fight;
        g_ld.ncap++; g_ld.state = 1; g_ld.fidx = 0; g_ld.nent = 0; g_ld.dropped = 0; g_ld.order = 0; g_ld.passes = 0; g_ld.dispatches = 0; g_ld_npe = 0; g_ld.npass = 0; g_ld_rec_ms = 0;
        g_ld.dump = !g_ld.nodump && !g_ld.dumped && (why == LD_DARK || why == LD_TDARK); g_ld.ndump = 0; memset(g_ld.ncomp, 0, sizeof g_ld.ncomp);
        g_ld_cap = 1;
    }
}
static void ld_start_line(void) {
    if (!ld_on()) { printf("nvn_vk_lumdiag: off (SSBU_VK_LUMDIAG=0, or an NVIDIA GPU: 2 = the default = every GPU but NVIDIA, 1 = all)\n"); return; }
    g_ld.lines++;
    LDP("nvn_vk_lumdiag: ON (black picture diagnosis%s): small render targets (<= %d texels), blits and compute results read back, the large targets reduced in the first frame, at fight frame 60 and when a fight stays under %d of 255 for 1 s, %d frames each; at most %d lines (SSBU_VK_LUMDIAG=0 off)\n",
           g_ld.test ? ", TEST captures at fight frames 180 and 600" : "", g_ld.small_tx, g_ld.dark_thr, g_ld.nframes, g_ld.maxlines);
}
static void ld_shutdown(void) {
    if (g_ld_ath) { WaitForSingleObject(g_ld_ath, INFINITE); CloseHandle(g_ld_ath); g_ld_ath = NULL; }
    if (g_ld.state) { cmd_wait_all(); ld_buf_free(); ld_atlas_free(); g_ld.state = 0; g_ld_cap = 0; g_ld_npe = 0; }
    if (g_ld.dumped != 1) for (int i = 0; i < g_ld.ndump; i++) { free(g_ld.dq[i].code); free(g_ld.dq[i].cdata); g_ld.dq[i].code = g_ld.dq[i].cdata = NULL; }
}
