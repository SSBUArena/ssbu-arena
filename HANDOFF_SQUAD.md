# Battle Arena = Squad Strike online (handoff, 2026-11-06 sera)

Ramo `feature/battle-arena-squad` (worktree `ssbu-recomp-squad`, da `fix/0.2.12` c0679f5). Commit WIP. Niente push / release / VPS / Supabase.
arena-server: NON toccato (non serve: tutto passa nei messaggi gia' esistenti).

## Come fa il gioco (13.0.4, trovato oggi)
- Squad Strike = `SEQUENCE_MELEE_TEAM_COMPE 0x02020000`, scena `MeleeTeamCompeSequenceScene`. Ruleset "Tag Team" = 3 vs 3, 1 vita, tutti nella stessa partita.
- Tabella squadre globale `0x71052c9dc0` (0x1818 byte, azzerata da `0x710179d0a0`): +0 numero (3/5), +1 uguale, +8 voci [2 giocatori][5] da 0x268 = un record giocatore (0x260, fatto dal record builder `0x7101788260`) + una parola. La sequenza squad (`0x71023108b0`, 0x7102312610..) costruisce un record per lottatore e li copia li'.
- Il battle builder `0x71014e58f0` vede la squadra se match block +0x39 == 3 e carica tutti i lottatori della tabella. Altri byte di regola diversi dal versus: +0 melee mode 2, +0x15 = 1, +0x20 = 1, +0x4c vite 1.

## Cosa c'e' nel codice (tutto dietro interruttore: senza modalita' Battle Arena nulla cambia)
- `debug_mode.c` `squad_build`: dopo il record del giocatore 2 trasforma un versus in Tag Team (tabella + regole) usando il record builder del gioco. Test: `SSBU_SQ_TEST=mario,link,kirby/fox,pikachu,samus` (+ `SSBU_SQ_TEST_CPU=9,1`).
- `rb_net.c`: modalita' Battle Arena (`rbn_ba_mode`, 1 = tessera, 2 = entrato in una stanza Battle Arena), scelte lottatori 2 e 3; il creatore della stanza mette `RbhMatch.pad0` bit 0 nella proposta; le scelte viaggiano nel messaggio LAYOUT (`ctx_stage`: 'S' 'Q' 2 + 2 ui_chara + 2 colori). Random se non scelti. Test: `SSBU_BATTLE_ARENA=1`, `SSBU_BATTLE_ARENA_SQUAD=link,kirby`.
- `nnldn.c`: byte 2 dell'intestazione ADVERTISE/ACCEPT = stanza Battle Arena; chi entra passa in modalita' 2 e vede un popup.
- `competitive.c`: open_pallet arg 7 = Battle Arena (modalita' on, schermata squadra, poi Local Wireless); arg 3 / quick match = off.
- `menu.cpp`: schermata "BATTLE ARENA" (Fighter 2 / Fighter 3 con < >, Done, Other settings); si apre dalla tessera e ogni volta che si apre il menu in modalita' Battle Arena.
- `tools/ui/menu_actions_classic.py`: tessera spirits func_arg 0 -> 7 (il menu si rifa' all'installazione / aggiornamento).
- `replay_state.c`: flag 4 nell'intestazione = partita squad.
- Tutta la ricerca (SSBU_DEBUG_SQSCAN in debug_mode.c) e' ancora dentro: da togliere o tenere come test.

## Prove fatte
- Offline (solo.sh, romfs completo): versus locale trasformato in Tag Team: icone squadra nell'HUD, Fox KO -> entra Pikachu (run t5). Nessun crash. Il registratore replay ha retto i cambi.
- Squad Strike vero del gioco contro CPU (r13/r16): usato per leggere tabella e regole.
- **Coppia squad sq1 (pair.sh, NETSIM=27): NON IDENTICAL.** Entrambi i giochi costruiscono squadre identiche, match block check IDENTICAL, ma DIFFER dal frame 322: il P2 (joiner) cade, e il lottatore successivo appare su B al 322 e non su A. Su B al frame 305 "capture skipped: TaskWorker2 not at a wait 30 ms". Ipotesi: la creazione del lottatore successivo non e' deterministica (lavoro asincrono: TaskWorker / ResLoadingThread) oppure un rollback attraverso l'inizio della creazione non la rifa' uguale.
- Coppia sq2 (NETSIM=0, per separare le due ipotesi): NON partita (GAME_LOCK della sq1 ancora preso) -> riga sbagliata "PAIR sq2 ... FileNotFoundError" in rbtick3/progress.log: ignorarla.
- Coppia normale (prova che le stanze normali non cambiano): NON ancora fatta.

## Da dove riprendere
1. Build: `bash "C:/Users/Nicolo/Desktop/SSBU RECOMP/squad/build.sh" sq_N` (work in squad/work). Copiare `squad/work/sq_N.exe` in `SSBU Arena 0.2.8 Test/build/work` e cancellarlo alla fine.
2. `cd rbtick3; DEND=420s LASTM=1 TRIES=1 NETSIM=0 bash pair.sh sq2 sq_N sq_N "SSBU_BATTLE_ARENA=1 SSBU_BATTLE_ARENA_SQUAD=link,kirby"`:
   - se IDENTICAL -> il problema e' il rollback attorno alla creazione del lottatore;
   - se DIFFER -> e' la tempistica asincrona: trovare cosa decide quando entra il lottatore successivo (funzioni che leggono la tabella: 0x710157c100 inizializzazione squad nell'oggetto battaglia +0x900, 0x710176c1c0, 0x710143b0f0, 0x7101342830) e renderlo deterministico (es. far aspettare il main thread finche' il lavoro e' finito, o creare/caricare tutto prima dell'inizio).
3. Poi: una coppia normale (senza SSBU_BATTLE_ARENA) IDENTICAL; prova a mano della tessera e della schermata squadra (non provate: servono il menu Arena e il controller).
4. Pulizia: togliere SSBU_DEBUG_SQSCAN, riscrivere il messaggio di commit.

## Cosa manca / rischi
- Sync squad non raggiunta (punto 2). Regola stanza deve essere a vite (stock).
- Il replay mostra solo il lottatore 1 per giocatore nell'intestazione.
- Fine partita / ritorno nella stanza dopo una partita squad: non visto.
- Archivio leggero (light archive): non provato.

File di lavoro: `squad/` (build.sh, solo.sh, seqs.sh, runs/), analisi binario in scratchpad (`re/elf.py`).
