/* online_elo_panel.h - (0.2.54, SSBU_ONLINE_ELO_UI_DB) the numbers of the game's results panel: the database's elo instead of the
 * game's own update when they differ. Pure logic (no game memory, no locks): online_elo.c keeps one OePanel under its lock, the test
 * tests/test_elo_panel.c runs these functions alone.
 *
 * The game's rating update (online_elo.c oe_upd_hook) runs the database's formula, but it cannot know the 24 h pair limit, a disputed
 * match or another region's matches: there the game ends a match with e.g. 1032 -> 1048 while the database keeps 1032, and keeps
 * 1048 as the fighter's rating for the rest of the session (the next match's "before"; the next login gives the database's number
 * again). The panel asks for every number it shows (0x71017d14c0): the number shown goes through a small table of corrections
 * "the game's value -> the database's value", made when the database's verdict arrives (1-3 s after the fight, before the player
 * presses A on the winner screen) from the game's end values of that match (old -> new per member) and the database's (before ->
 * after per side). Only the number shown changes, never the game's own data. */
#ifndef ONLINE_ELO_PANEL_H
#define ONLINE_ELO_PANEL_H
#include <stdint.h>
#include <string.h>

#define OEP_FIX 4
typedef struct {
    uint32_t mid; int n_game, game_old[2], game_new[2];     /* this match: the game's end values (one per member) */
    int db_have, db_before[2], db_after[2];                 /* this match: the database's verdict (it may come first) */
    int n_fix, fix_from[OEP_FIX], fix_to[OEP_FIX];          /* the corrections, newest last (kept across matches of a session) */
} OePanel;

static int oep_fixed(const OePanel *p, int v) {             /* a value of the game -> the database's */
    for (int i = p->n_fix - 1; i >= 0; i--) if (p->fix_from[i] == v) return p->fix_to[i];
    return v;
}
static void oep_match(OePanel *p, uint32_t mid) {           /* a new match: its own values only, the corrections stay */
    if (p->mid != mid) { p->mid = mid; p->n_game = 0; p->db_have = 0; }
}
static void oep_add_fix(OePanel *p, int from, int to) {
    for (int i = 0; i < p->n_fix; i++) if (p->fix_from[i] == from) { p->fix_to[i] = to; return; }
    if (p->n_fix == OEP_FIX) { memmove(p->fix_from, p->fix_from + 1, sizeof p->fix_from[0] * (OEP_FIX - 1)); memmove(p->fix_to, p->fix_to + 1, sizeof p->fix_to[0] * (OEP_FIX - 1)); p->n_fix--; }
    p->fix_from[p->n_fix] = from; p->fix_to[p->n_fix] = to; p->n_fix++;
}
/* the corrections the database's verdict of this match needs (before / after per side; after = before when it changed nothing).
 * Returns how many were made. A member of the game is the side whose "before" is its old value (corrected); two equal "befores":
 * the side going the same way (a win up, a loss down; a side without change takes either). Run again when a value comes in. */
static int oep_apply(OePanel *p) {
    if (!p->db_have) return 0;
    int made = 0, used[2] = {0, 0};
    for (int k = 0; k < p->n_game; k++) {
        int old = oep_fixed(p, p->game_old[k]), nv = p->game_new[k], dir = nv > p->game_old[k] ? 1 : nv < p->game_old[k] ? -1 : 0, pick = -1;
        for (int s = 0; s < 2; s++) {
            if (used[s] || p->db_before[s] != old) continue;
            int d = p->db_after[s] > p->db_before[s] ? 1 : p->db_after[s] < p->db_before[s] ? -1 : 0;
            if (pick < 0 || d == dir) pick = s;
        }
        if (pick < 0) continue;
        used[pick] = 1;
        if (oep_fixed(p, nv) != p->db_after[pick]) { oep_add_fix(p, nv, p->db_after[pick]); made++; }      /* (also nv -> nv: an older correction of that number no longer holds) */
    }
    return made;
}
static int oep_db(OePanel *p, uint32_t mid, const int before[2], const int after[2]) {      /* the database's verdict of match mid */
    if (before[0] <= 0 || before[1] <= 0) return 0;
    oep_match(p, mid);
    for (int s = 0; s < 2; s++) { p->db_before[s] = before[s]; p->db_after[s] = after[s]; }
    p->db_have = 1;
    return oep_apply(p);
}
static int oep_game(OePanel *p, uint32_t mid, int old, int nv) {      /* the game's update at the match's end, per member */
    oep_match(p, mid);
    for (int k = 0; k < p->n_game; k++) if (p->game_old[k] == old && p->game_new[k] == nv) return 0;      /* (run again for a member) */
    if (p->n_game < 2) { p->game_old[p->n_game] = old; p->game_new[p->n_game] = nv; p->n_game++; }
    return oep_apply(p);
}
#endif
