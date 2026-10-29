<!-- Online play in SSBU Arena: how it works, how to play, and the settings behind it. -->
# Online play

SSBU Arena plays online through **our relay server**: every packet between two players goes through it, so no player
ever learns another player's internet address. Fights use **rollback netcode**: each game runs the match locally and
only the players' inputs travel; a late input rolls the game back a few frames and replays them, instead of slowing
both players down.

## Play online

1. **Sign in in the launcher** with your Arena account ([create one](https://www.ssbuarena.com/signup)). Online play is
   on by default; it needs a signed-in player, whose player name is the one the other players see.
2. **Press Play.** The game joins our server (`eu-germany.ssbuarena.com`, UDP port 39010) as it starts.
3. In the Arena menu choose **Online LDN**: it opens the game's own Local Wireless rooms, played over the internet.
   - **Host:** create a room. When someone asks to join ("wants to join!"), accept.
   - **Guest:** find the room in the Rooms List and join it.
   - Both vote a stage and pick fighters; the room maker starts the match.
4. The **ping** of each player to the server is shown next to them in the match (green, yellow, red). It can be turned
   off in the launcher's settings or in the in-game Arena menu.

**Smash** (versus) stays a local fight: friends on the same PC and CPUs. **Online Matchmaking** finds you an opponent:
pick a fighter and a stage, the server pairs you with another player searching, and the match plays with rollback and
competitive rules (no ranking yet). **Battle Arena** is coming: Arena lobbies with friends and invites
([roadmap](https://www.ssbuarena.com/project)).

## Rollback in rooms

- A room's match is played with rollback when the room has **exactly 2 players** and both games have the rollback
  build (the setup makes it on every install). The room maker is player 1; fighters, colours, stage and rules are the
  room's, and each player keeps their own name-tag controls.
- The room's waiting stage, rooms with more than 2 players, and a player without the rollback build use the game's own
  online netcode, so everyone can still play together (a short message says so once).
- **Input delay** is chosen from the best measured round trip: 1 frame below about 117 ms, 2 frames from about 117 ms,
  3 from 150 ms, 4 from about 184 ms. The rest of the trip is covered by rollback.
- How rollback works, in plain words: [ssbuarena.com/rollback](https://www.ssbuarena.com/rollback).

## Requirements

- **Same game update** (13.0.4 or 13.0.5) and the **same mods** on both sides. The game version and a hash of the
  loaded mods are part of the build id shown in the log (`[online] on`, `[online] mods`).
- Rooms of another version or another mod set are listed but can't be joined: a box says why, for example
  "Different version: this room is 13.0.4, you have 13.0.5" or "MISSING: <mod names>".
- The launcher keeps everyone on the latest release, so players who update normally match.

## Limits today

- Rollbacks of 3 or more frames take a little longer than one frame: at very high ping, or with frequent rollbacks,
  short hitches can be felt. Making replayed frames cheaper is in progress.
- Windows only.
- Tested mostly between two games through our server, with simulated ping and loss; please report how it feels.

## Privacy

The relay server sees your internet address (it has to, to send you the other player's packets) and the match traffic.
It never sends your address to another player, and its log names players by their player name and an address inside
the room only. See the [privacy policy](https://www.ssbuarena.com/privacy).

---

## Advanced: the settings behind it

The launcher sets these for you. Without the launcher (a build by hand, see [BUILDING.md](../BUILDING.md)) they are
environment variables or keys in `config\graphics.ini`; environment variables are read first.

| Variable / graphics.ini key | What it does |
|---|---|
| `SSBU_ONLINE=<host:port>` / `online_server` | The server to join. `off` (or `online_mode = off`): offline |
| `SSBU_ONLINE_NAME` / `online_name` | Your name in the room and in the server's log |
| `SSBU_ONLINE_CODE` / `online_code` | The server's pool of players; the launcher uses `public` |
| `SSBU_ONLINE_DIRECT=0` / `online_direct = off` | Relay only: no direct connection between players. **The launcher always sets it** |
| `SSBU_ONLINE_TICKET` | The signed ticket of the player's account (the launcher asks for it at sign-in) |
| `SSBU_NET_PING=0/1` | The ping on screen in online matches |
| `SSBU_RB_NET=net` | Rollback in 2-player rooms (needs the rollback build: `ssbu_game_r1.dll`, `nro_bin_eh_o2r1`) |
| `SSBU_ONLINE_PORT` | Local UDP port |
| `SSBU_ONLINE_LOG=0..3` | Log level (default 1) |

The runtime still knows the other ways to connect it inherited from OpenSmash-Ultimate (`online_mode = lan`, `host`,
`join` with a room code). They connect the two games **directly**, which shows each player's internet address to the
other, so SSBU Arena does not use them; they are kept for testing on one network.

## Advanced: run a relay server

Our server runs [`tools/net/rb_lobby.c`](../tools/net/rb_lobby.c): the lobby protocol of `tools/net/ssbu_lobby.py`
(which stays its description) in C for Linux, **relay only**. One thread, a few MB of memory.

```bash
cc -O2 -o rb_lobby tools/net/rb_lobby.c
./rb_lobby --port 39010
```

Open that UDP port on the machine and in its provider's firewall. Tests: `tools/net/test_rb_lobby_ticket.py`,
`tools/net/test_rb_lobby_ping.py`, `tools/net/test_ssbu_lobby.py`.

## Credit

Online play adapts parts of SNO2207/WindowSmash's network code and lobby design, used with its author's permission;
the lobby protocol, the room code and the relay server are ours.
