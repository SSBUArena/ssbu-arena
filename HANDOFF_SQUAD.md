# Battle Arena = Squad Strike online (handoff, aggiornato 2026-11-07)

Ramo `feature/battle-arena-squad` (worktree `ssbu-recomp-squad`, da `fix/0.2.12` c0679f5). Niente push / release / VPS / Supabase.
arena-server NON toccato (non serve). Build: `bash "C:/Users/Nicolo/Desktop/SSBU RECOMP/squad/build.sh" sq_N` (cartella squad/work; se cambia
rb_rollback.c / r1_ring.c toccare `squad/work/r1_state.c`). Test: rbtick3/pair.sh, solo.sh / seqs.sh in `squad/`.

## Stato in una riga
Tutto il percorso funziona (tessera, schermata squadra, stanza, accordo, Tag Team 3 vs 3, cambio lottatore al KO), ma la partita squad
online NON e' in sincronia: diverge al primo cambio di lottatore. BLOCCATO su questo punto (sotto: perche' e cosa provare).

## Come fa il gioco (13.0.4)
- Squad Strike = `SEQUENCE_MELEE_TEAM_COMPE 0x02020000`. Ruleset "Tag Team" = una partita, si entra al KO del precedente.
- Tabella squadre `0x71052c9dc0` (0x1818, azzerata da `0x710179d0a0`): +0 numero, +1 uguale, +8 voci [2][5] da 0x268 (record giocatore
  del record builder `0x7101788260` + una parola). Il battle builder `0x71014e58f0` la usa se match block +0x39 == 3. Altri byte: +0 = 2,
  +0x15 = 1, +0x20 = 1, +0x4c = 1.
- **Il lottatore successivo viene caricato DURANTE la partita**: al KO il gioco scarica il modulo del morto e carica quello che entra e il
  seguente (`nn::ro::LoadModule lua2cpp_<x>` dal thread `L2CDynamicModuleManager`, file da `ResLoadingThread`, poi job sui TaskWorker),
  e lo fa entrare quando il caricamento e' finito. E' tempo reale: su due PC entra a frame diversi. Offline non si vede; in rollback si'.

## Codice (tutto dietro la modalita' Battle Arena; senza, nulla cambia)
- `debug_mode.c` `squad_build`: versus -> Tag Team (tabella + regole) col record builder del gioco. Test offline: `SSBU_SQ_TEST=a,b,c/d,e,f`,
  `SSBU_SQ_TEST_CPU=9,1`.
- `rb_net.c`: modalita' (`rbn_ba_mode`), scelte 2 e 3, `RbhMatch.pad0` bit 0 nella proposta del creatore, scelte nel LAYOUT (`ctx_stage`).
  Test: `SSBU_BATTLE_ARENA=1`, `SSBU_BATTLE_ARENA_SQUAD=link,kirby`.
- `nnldn.c` (byte 2 di ADVERTISE/ACCEPT = stanza Battle Arena), `competitive.c` (func_arg 7), `menu.cpp` (schermata BATTLE ARENA),
  `tools/ui/menu_actions_classic.py` (tessera spirits -> 7), `replay_state.c` (flag 4).
- `rb_rollback.c` `rbg_squad_settle`: ESPERIMENTO, SPENTO di default (`SSBU_SQ_SETTLE=<ms>` lo accende): tiene fermi i thread di
  caricamento durante i frame e li lascia andare solo tra un frame e l'altro.
- Codice di ricerca (SSBU_DEBUG_SQSCAN) tolto.

## Prove (rbtick3/progress.log, rb/lwr_<tag>)
| tag | cosa | esito |
|---|---|---|
| t5 / t7 (solo) | offline, versus -> Tag Team, CPU 9 vs 1 | funziona: HUD squadra, Fox KO -> entra Pikachu; nessun crash |
| sq1 | coppia squad, NETSIM=27 | DIFFER dal frame 322 (primo cambio) |
| sq2b | coppia squad, NETSIM=0 (quasi nessun rollback) | DIFFER dal frame 322: NON e' il rollback, e' la tempistica del caricamento |
| sq4a | + attesa a fine frame finche' i thread sono fermi | DIFFER dal 296 (le richieste stesse cadono a frame diversi: passi brevi finiscono a meta' frame) |
| sq5a | + loader tenuti fermi durante i frame | DIFFER dal 291; su A watchdog (un frame aspetta un loader) |
| sq6a | + rilascio se il frame aspetta | nessuna divergenza nei 256 frame comuni, poi CRASH di B (ssbu_game_r1.dll+0xd8a397d): tenere fermo ResLoadingThread non e' sicuro |
| t6 | sequenza 0x02020000 durante la partita | crash in 0x71023108b0 (tolto) |
| norm8 | coppia NORMALE (senza Battle Arena), exe finale sq_8 | IDENTICAL (1431 frame), 0 crash |

## Dove riprendere (proposte, in ordine)
1. Rendere il cambio deterministico "a frame fisso": trovare la funzione che il frame interroga ("modulo/risorse pronte?") e farla
   rispondere "pronto" esattamente a frame_richiesta + K su entrambi i giochi (K ~ 90), bloccando il main a quel frame finche' il caricamento
   e' davvero finito. Punto di partenza: `0x71022b7100` (crea il thread "L2CDynamicModuleManager::thread"), il gestore moduli; nnro.c vede le
   chiamate LoadModule/UnloadModule (con il frame del rollback si puo' loggare quando arrivano su A e su B).
2. Alternativa: tenere caricati moduli e file di tutti i lottatori delle squadre per tutta la partita (niente caricamento al KO).
3. Solo dopo: coppia squad IDENTICAL con NETSIM=0 e 27, poi coppia normale di nuovo.

## Da provare a mano (quando la sincronia c'e')
Tessera Battle Arena e schermata squadra col controller (menu Arena rifatto all'installazione); stanza con un amico: popup "Battle Arena room";
fine partita e ritorno nella stanza; regola stanza a vite; archivio leggero.

## Opzione 2 (tutti i lottatori caricati dall'inizio): esame 2026-11-07, NON fattibile senza molto reverse engineering
- Anche lo Squad Strike VERO del gioco fa cosi' (run r13 / r16): prima della partita carica i moduli dei lottatori 1 e dei successivi, poi
  SCARICA i successivi prima del via; al KO scarica il morto e carica quello che entra e il seguente. E' la politica del gioco, non un
  effetto del nostro codice.
- Il caricamento al KO e' fatto di: file (NRO del modulo, modelli, animazioni) letti da ResLoadingThread / ResInflateThread (~200-400 ms),
  poi `nn::ro::LoadModule` (nostro, nnro.c: solo 15-24 ms), poi job sui TaskWorker. La parte lenta e asincrona sono i FILE, gestiti dal
  gestore risorse del gioco (memoria guest). Tenere caricati i moduli lato host (nnro.c: UnloadModule rimandato) non basta: il gioco
  rilegge comunque i file prima di chiedere il modulo. Per tenere tutto residente bisognerebbe cambiare la politica del gestore risorse /
  moduli del gioco (dove decide carica/scarica): da trovare nel binario.
- Opzione 1 (entrata a frame fisso dopo il KO) sembra PIU' fattibile: serve un solo punto, la decisione "il prossimo lottatore puo'
  entrare". Candidato: l'oggetto squadra della battaglia (vtable 0x710506fc40, init 0x710157c100, stato a +0x900 .. +0x1060; il metodo
  grande 0x710157d6f0 e' il primo da guardare). Con un watchpoint hardware (debug_mode.c debug_watch) sul suo stato al KO si trova chi lo
  cambia quando il caricamento finisce; poi un hook che rende "pronto" solo a frame_KO + N (uguale sui due giochi, N ~ 90) e, se a quel
  frame il caricamento non e' finito, ferma il main a fine frame finche' lo e' (stallo, ma uguale sui due).
  Stima: 1-2 sessioni.

## Opzione 1 (entrata a frame fisso dopo il KO): provata 2026-11-07, NON basta
- Trovato (run t8 / t9, watchpoint hardware): oggetto squadra della battaglia (fatto da `0x710157c100`), macchina a stati del cambio a
  +0xd70: KO -> +0xd08 = 60 e +0xd04 conta 60 frame -> stato 2 (caricamento) -> 3 (scritto da `0x7101589830`, callback per frame
  della battaglia, quando il caricamento e' finito) -> +0xd74 conta -> 4 -> `0x710158a360` -> 0 (lottatore dentro). +0x112c = contatore frame.
- `debug_mode.c` `sq_step_hook` (SSBU_SQ_GATE=1, SPENTO di default): finche' lo stato 2 ha meno di N frame (SSBU_SQ_GATE_N, 120) un passo
  che lo lascia viene annullato; da N in poi passa. Offline (t11): due cambi esattamente a stato2 + 120, caricamento pronto a +19 / +25.
- Aspettare il caricamento DENTRO il frame non funziona (t10): il caricamento ha bisogno che i frame del gioco vadano avanti.
- Coppia sq9a (NETSIM=0) con il gate: DIFFER al frame 318, PRIMA del frame del gate. L'oggetto del nuovo lottatore (status 0xb7 al punto
  di rinascita) compare su un gioco e non sull'altro: lo crea la fine del caricamento stessa, indipendentemente da +0xd70. In piu'
  l'annullamento a volte perde la richiesta (stato 2 per sempre su A, e su B al secondo cambio).
- Prossimo passo se si vuole insistere: trovare chi crea quell'oggetto (watchpoint sulle voci FighterManager impl + 0x20 + 8 * entry, o
  hook della creazione del fighter) e ritardare quella creazione a frame fisso; oppure l'opzione 2 a livello del gestore risorse.

## Stato finale del ramo
- Gate e settle sono esperimenti SPENTI di default; il codice di ricerca e' tolto.
- Coppia normale con l'exe finale sq_10 (norm10, NETSIM=27): IDENTICAL su 3225 frame, 0 crash.
