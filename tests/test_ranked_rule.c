/* tests/test_ranked_rule.c - (0.2.78) runtime/ranked_rule.h alone: the official ranked rule image and the rule hash.
 * Build and run (any C compiler):  clang -O1 -Wall -o test_ranked_rule.exe tests/test_ranked_rule.c && test_ranked_rule.exe
 * The image is checked field by field against the save layout (save_research SAVE_FORMAT.txt section 3) and against a real
 * competitive set of a player's save (time 7:00, 3 stocks, items off, hazards off, team attack on), the hash against FNV-1a's
 * published vectors, its pinned value (RR_OFFICIAL_HASH, the server's default ranked_rule_hash) and every single-byte change. */
#include <stdio.h>
#include "../runtime/ranked_rule.h"

static int fails;
static void eq(const char *what, unsigned got, unsigned want) { if (got != want) { printf("FAIL %s: %#x, want %#x\n", what, got, want); fails++; } }

int main(void) {
    /* FNV-1a 32: the published vectors ("" -> 811c9dc5, "a" -> e40c292c, "foobar" -> bf9cf968) */
    eq("fnv empty", rr_fnv(0x811c9dc5u, "", 0), 0x811c9dc5u);
    eq("fnv a", rr_fnv(0x811c9dc5u, "a", 1), 0xe40c292cu);
    eq("fnv foobar", rr_fnv(0x811c9dc5u, "foobar", 6), 0xbf9cf968u);

    /* the image, field by field */
    const uint8_t *b = RR_IMAGE;
    eq("size", sizeof RR_IMAGE, 0x38);
    eq("mode melee", b[0x00], 0); eq("style stock", b[0x01], 1); eq("squad", b[0x02], 0); eq("+13", b[0x03], 0);
    eq("time 420 s", (unsigned)(b[0x04] | b[0x05] << 8), 420); eq("stocks", b[0x06], 3); eq("hp index 150", b[0x07], 5);
    eq("wins", b[0x08], 0); eq("+19", b[0x09], 0); eq("cpu", b[0x0a], 2); eq("+1b", b[0x0b], 0); eq("stage selection", b[0x0c], 0);
    eq("team attack", b[0x0d], 1); eq("launch 1.0x", b[0x0e], 10); eq("FS meter", b[0x0f], 0); eq("+20", b[0x10], 0);
    eq("hazards", b[0x11], 0); eq("+22", b[0x12], 1); eq("morph", b[0x13], 0); eq("+24", b[0x14], 0); eq("pause", b[0x15], 0);
    eq("+26", b[0x16], 0); eq("+27", b[0x17], 1); eq("item frequency", b[0x18], 0);
    for (int k = 0x19; k < 0x1c; k++) eq("padding", b[k], 0);
    for (int k = 0; k < 12; k++) eq("item bits off", b[0x1c + k], 0);
    for (int k = 12; k < 26; k++) eq("list bits 96.. default", b[0x1c + k], 0xff);
    eq("list byte 26", b[0x1c + 26], 0x7f); eq("list byte 27", b[0x1c + 27], 0xff);
    eq("items off", (unsigned)rr_items_off(b), 1);
    /* a player's own competitive set (Smash list, a 13.0.x save made by an emulator: "1 stock 7:00 no items") + 0x10 .. + 0x2b: the
     * image differs from it only where Arena chose otherwise: + 0x18 (4 there), + 0x1c stage selection (4 there), + 0x25 pause (1) */
    {   static const uint8_t comp[0x1c] = {0x00,0x01,0x00,0x00,0xa4,0x01,0x03,0x05,0x04,0x00,0x02,0x00,0x04,0x01,0x0a,0x00,0x00,0x00,0x01,0x00,0x00,0x01,0x00,0x01,0x00,0x00,0x00,0x00};
        int nd = 0; for (int k = 0; k < 0x1c; k++) if (comp[k] != b[k]) { nd++; if (k != 0x08 && k != 0x0c && k != 0x15) eq("unexpected difference with a player's competitive set at", (unsigned)k, 0xffff); }
        eq("differences with a player's competitive set", (unsigned)nd, 3); }
    /* the test override of the image (SSBU_QUICK_RULE_KIND / _TIME) */
    {   uint8_t t[RR_LEN]; rr_image(t, -1, -1); eq("rr_image official", (unsigned)memcmp(t, RR_IMAGE, RR_LEN), 0);
        rr_image(t, 0, 150); eq("override style", t[RR_STYLE], 0); eq("override time", (unsigned)(t[RR_TIME] | t[RR_TIME + 1] << 8), 150);
        eq("override first diff", (unsigned)rr_first_diff(t, RR_IMAGE), RR_STYLE); }

    /* the hash: pinned, and every single change of what was played changes it */
    uint32_t off = rr_official_hash();
    printf("official ranked rule hash: %08x\n", off);
    eq("RR_OFFICIAL_HASH", off, RR_OFFICIAL_HASH);
    {   RrPlayed p; rr_official(&p); eq("official played", rr_hash(&p), off);
        int same = 0;
        for (int i = 0; i < 3; i++) for (int k = 0; k < RR_LEN; k++) { p.copy[i][k] ^= 1; same += rr_hash(&p) == off; p.copy[i][k] ^= 1; }
        eq("a one-bit change of any copy byte keeps the hash", (unsigned)same, 0);
        p.mb_kind = 0; eq("match block kind", rr_hash(&p) != off, 1); p.mb_kind = RR_MB_KIND;
        p.mb_time = 180 * 60; eq("match block time", rr_hash(&p) != off, 1); p.mb_time = RR_MB_TIME;
        p.mb_stock = 2; eq("match block stocks", rr_hash(&p) != off, 1); p.mb_stock = RR_MB_STOCK;
        p.mb_items = 2; eq("match block items", rr_hash(&p) != off, 1); p.mb_items = RR_MB_ITEMS;
        p.hazards = 1; eq("hazards switch", rr_hash(&p) != off, 1); p.hazards = RR_HAZ;
        p.copy[1][RR_LAUNCH] = 15; eq("launch 1.5x in copy 1", rr_hash(&p) != off, 1); p.copy[1][RR_LAUNCH] = 10;
        /* the copies are hashed in order (copy 0 with launch 1.5 and copy 2 with launch 1.5 are different rules played) */
        RrPlayed q = p; q.copy[0][RR_LAUNCH] = 15; p.copy[2][RR_LAUNCH] = 15; eq("copy order", rr_hash(&p) != rr_hash(&q), 1);
        eq("back to official", (rr_official(&p), rr_hash(&p)), off); }
    {   char t[256]; rr_describe(RR_IMAGE, t, sizeof t); printf("official rule: %s\n", t);
        eq("describe", (unsigned)strcmp(t, "style 1, 7:00, 3 stocks, launch 1.0x, FS meter 0, hazards 0, morph 0, pause 0, team attack 1, items 0 (list off)") != 0, 0); }

    if (fails) { printf("test_ranked_rule: %d FAILED\n", fails); return 1; }
    printf("test_ranked_rule: ok\n");
    return 0;
}
