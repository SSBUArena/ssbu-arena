/* tests/test_elo_panel.c - (0.2.54) runtime/online_elo_panel.h, the results panel's numbers (SSBU_ONLINE_ELO_UI_DB), alone.
 * Build and run (any C compiler):  clang -O1 -Wall -o test_elo_panel.exe tests/test_elo_panel.c && test_elo_panel.exe
 * The numbers are those of the end-to-end tests elo2_test x7 (a normal match) and x8d (the 24 h pair limit). */
#include <stdio.h>
#include "../runtime/online_elo_panel.h"

static int fails;
static void eq(const char *what, int got, int want) { if (got != want) { printf("FAIL %s: %d, want %d\n", what, got, want); fails++; } }

int main(void) {
    OePanel p; memset(&p, 0, sizeof p);
    /* x7: A 1014 -> 1032, B 986 -> 968, the game's update = the database: nothing changes */
    oep_game(&p, 0x9ea86c63u, 1014, 1032); oep_game(&p, 0x9ea86c63u, 986, 968); oep_game(&p, 0x9ea86c63u, 1014, 1032);
    { int b[2] = {1014, 986}, a[2] = {1032, 968}; eq("x7 corrections", oep_db(&p, 0x9ea86c63u, b, a), 0); }
    eq("x7 A before", oep_fixed(&p, 1014), 1014); eq("x7 A after", oep_fixed(&p, 1032), 1032); eq("x7 B after", oep_fixed(&p, 968), 968);
    /* x8d: the pair limit: the game 1032 -> 1048 / 968 -> 952, the database no change */
    oep_game(&p, 0x3156930fu, 1032, 1048); oep_game(&p, 0x3156930fu, 968, 952);
    { int b[2] = {1032, 968}, a[2] = {1032, 968}; eq("x8d corrections", oep_db(&p, 0x3156930fu, b, a), 2); }
    eq("x8d A before", oep_fixed(&p, 1032), 1032); eq("x8d A after", oep_fixed(&p, 1048), 1032);
    eq("x8d B before", oep_fixed(&p, 968), 968); eq("x8d B after", oep_fixed(&p, 952), 968);
    /* the rematch (same session): the game starts from its own 1048 / 952; it computes 1048 -> 1063 (A wins), 952 -> 937; the database
     * (from 1032 / 968, 3 + 1 in 24 h: limit again) no change: the panel shows 1032 -> 1032 and 968 -> 968 */
    oep_game(&p, 0x6d2b42f7u, 1048, 1063); oep_game(&p, 0x6d2b42f7u, 952, 937);
    eq("rematch before (game 1048)", oep_fixed(&p, 1048), 1032);
    { int b[2] = {1032, 968}, a[2] = {1032, 968}; eq("rematch corrections", oep_db(&p, 0x6d2b42f7u, b, a), 2); }
    eq("rematch A after", oep_fixed(&p, 1063), 1032); eq("rematch B after", oep_fixed(&p, 937), 968);
    /* a later match counted again (the database 1032 -> 1047 while the game, from 1063, says 1063 -> 1077): 1077 shown as 1047 */
    oep_game(&p, 0x11111111u, 1063, 1077); oep_game(&p, 0x11111111u, 937, 923);
    { int b[2] = {1032, 968}, a[2] = {1047, 953}; eq("counted corrections", oep_db(&p, 0x11111111u, b, a), 2); }
    eq("counted A after", oep_fixed(&p, 1077), 1047); eq("counted B after", oep_fixed(&p, 923), 953);
    /* equal "befores" (1000 vs 1000), the database's verdict before the game's update: the sides by direction */
    OePanel q; memset(&q, 0, sizeof q);
    { int b[2] = {1000, 1000}, a[2] = {1020, 980}; eq("equal: early verdict", oep_db(&q, 7u, b, a), 0); }
    eq("equal: winner", oep_game(&q, 7u, 1000, 1020), 0); eq("equal: loser", oep_game(&q, 7u, 1000, 980), 0);
    eq("equal: 1020", oep_fixed(&q, 1020), 1020); eq("equal: 980", oep_fixed(&q, 980), 980);
    /* equal "befores", the pair limit: both after = before */
    OePanel r; memset(&r, 0, sizeof r);
    oep_game(&r, 8u, 1000, 1020); oep_game(&r, 8u, 1000, 980);
    { int b[2] = {1000, 1000}, a[2] = {1000, 1000}; eq("equal limit corrections", oep_db(&r, 8u, b, a), 2); }
    eq("equal limit 1020", oep_fixed(&r, 1020), 1000); eq("equal limit 980", oep_fixed(&r, 980), 1000);
    /* a verdict without numbers (flags 8: no elo) changes nothing */
    { int b[2] = {0, 0}, a[2] = {0, 0}; eq("no numbers", oep_db(&r, 9u, b, a), 0); }
    printf(fails ? "%d FAILED\n" : "ALL ELO PANEL TESTS PASSED\n", fails);
    return fails != 0;
}
