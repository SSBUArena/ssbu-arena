/* online_mastery.h - (22/11) SSBU Arena earns OpenSmash mastery points: what online_mastery.c gives menu.cpp / net_relay.c. */
#ifndef ONLINE_MASTERY_H
#define ONLINE_MASTERY_H
#include <stdint.h>
#ifdef __cplusplus
extern "C" {
#endif
#define MASTERY_MP_LEN (4 + 2 * 36)          /* the VERDICT's "MP" block (rb_ranked.c) */
typedef struct {
    char key[24], name[40];
    uint32_t before, after;
    unsigned points, flags;                  /* flags: 1 first win of the day, 2 pair cap, 4 same network, 8 day cap, 16 base only, 32 left, 64 fighter unknown */
    int age_ms, life_ms;
} MasteryCard;
int mastery_end_on(void);                    /* SSBU_MASTERY_END (default 1) */
int mastery_end_mode(void);                  /* 0 off, 1 the top left notice, 2 the 22/11 card */
int mastery_panel_on(void);                  /* SSBU_MASTERY_PANEL (default 1) */
void online_mastery_rating(int after);
int online_mastery_per_fighter(void);        /* (0.2.58) SSBU_RANK_PER_FIGHTER (default 1): the select panel's Elo / Peak / emblems per fighter */
void online_mastery_fighter_verdict(uint64_t chara, int fam, const char *label, int games, int place, int after);
int mastery_css_on(void);                    /* SSBU_MASTERY_CSS (default 0: the 22/11 overlay pill) */
int mastery_card_get(MasteryCard *out);      /* 1: the end card is on (render thread, every frame) */
int mastery_css_get(char *name, int nn, char *key, int nk, uint32_t *total, int *known);     /* 1: the Online select's card is on */
int mastery_total(const char *key, uint32_t *total);
const char *mastery_name(const char *key);
void online_mastery_verdict(uint64_t gid, const uint8_t *body, int n);      /* net_relay.c rk_verdict */
void online_mastery_hooks(void);             /* debug_mode.c */
void online_mastery_fake_end(void);          /* online_elo.c fight end (TEST: SSBU_MASTERY_FAKE) */
void ms_fake_tick(void);
#ifdef __cplusplus
}
#endif
#endif
