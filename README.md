<div align="center">

<img src="media/arena-launcher.png" alt="The SSBU Arena launcher" width="100%">

# SSBU Arena

**Super Smash Bros. Ultimate on PC, built for competitive online play.**

Rollback netcode through a relay that never shows your address to anyone, a competitive 1v1 mode,
and one small launcher that sets everything up and keeps it up to date.

[![Latest release](https://img.shields.io/github/v/release/ssbuarena/ssbu-arena?label=release&color=65c7ff)](https://github.com/ssbuarena/ssbu-arena/releases/latest)
[![Website](https://img.shields.io/badge/website-ssbuarena.com-65c7ff)](https://www.ssbuarena.com/)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-lightgrey)
![Game versions](https://img.shields.io/badge/game-13.0.2%20%7C%2013.0.4%20%7C%2013.0.5-green)

**[Download for Windows](https://github.com/ssbuarena/ssbu-arena/releases/latest/download/SSBU-Arena.exe)** ·
[Website](https://www.ssbuarena.com/) ·
[How rollback works](https://www.ssbuarena.com/rollback) ·
[Build from source](BUILDING.md)

</div>

> [!IMPORTANT]
> **This repository contains no game code, no game assets, no keys and no firmware.**
> You need your own legally obtained copy of the game (update 13.0.2, 13.0.4 or 13.0.5). Everything derived from it is built on
> your PC, from your copy, and never leaves it.

> [!NOTE]
> SSBU Arena is based on **[OpenSmash-Ultimate](https://github.com/OpenSmash-Ultimate/ssbu-recomp)** by Denkishi and
> its contributors: the static recompilation, the runtime that replaces the console, the Vulkan renderer and the
> rollback snapshot ring come from there. Arena started as a fork of it and is now developed on its own. See
> [Credits](#credits).

## Get started

| You need | |
|---|---|
| **Windows 10 or 11, 64-bit** | A graphics card with a Vulkan 1.3 driver |
| **Your own copy of the game** | Update 13.0.2, 13.0.4 or 13.0.5 (they all play together online), with the keys and firmware from your own console. The setup finds them in the folders of the usual emulators |
| **About 25 GB free** | The game unpacked from your copy and the files built from it |
| **About 20 minutes, once** | The first setup builds the game for your PC. Updates afterwards take a few minutes |

1. **Download [`SSBU-Arena.exe`](https://github.com/ssbuarena/ssbu-arena/releases/latest/download/SSBU-Arena.exe)**
   and save it in a new, empty folder: SSBU Arena installs itself next to it.
2. **Open it.** The setup checks your PC, finds your game, keys and firmware, and builds Ultimate for your PC.
3. **Create your account** on [ssbuarena.com](https://www.ssbuarena.com/signup) and sign in from the launcher to play
   online. Then press **Play**.

> [!TIP]
> **Windows says "Windows protected your PC"?** SSBU Arena is a new app, so SmartScreen asks before its first start:
> choose **More info**, then **Run anyway**. If a double-click does nothing, right-click `SSBU-Arena.exe` >
> **Properties** > tick **Unblock** > **OK**, and open it again. It never needs to run as administrator.

Every time it starts, the launcher checks for a new release and updates itself and the game in place. Your saves and
settings stay.

## What Arena adds

| | Feature | State |
|---|---|---|
| 🌐 | **Rollback netcode online**: the game's own Local Wireless rooms played over the internet as **Online Rooms**, with rollback instead of the game's delay-based netcode | Live |
| 🔀 | **One game across updates**: 13.0.2, 13.0.4 and 13.0.5 meet in the same rooms and play together with rollback, as if they were one version | Live |
| 🛡️ | **Relay-only server**: every packet goes through the server, no hole punching, no player ever learns another player's IP | Live |
| 📶 | **Ping on screen** in every online match, next to each player | Live |
| 🏆 | **Competitive mode**: the six legal stages (Battlefield, Small Battlefield, Final Destination, Pokémon Stadium 2, Smashville, Town and City), no items, no Final Smash meter, no hazards, no pausing | Live |
| 🖥️ | **SSBU Arena launcher** ([`tools/launcher/arena`](tools/launcher/arena)): one-file download, guided setup, automatic updates, account sign-in, controllers, video and audio settings, shader preparation | Live |
| ⚔️ | **Battle Arena**: Squad Strike (3 fighters each, 1 stock each) in an online room with rollback, picked on Smash's own Squad Strike screen, match after match | Live |
| 🎞️ | **Replays**: every match recorded and stored compact (~0.7 MB for 7 minutes). Watch them in the launcher with the game's 3D fighters and stages, or as a hitbox diagram on [ssbuarena.com/replays](https://www.ssbuarena.com/replays). Make one public to share its link | Live |
| 🎮 | **Arena main menu**: Smash, Online Rooms, Battle Arena, Online Ranked, Settings and Exit, with an in-game settings menu | Live |
| 🔓 | **Ready save**: every base fighter unlocked and the save completed (all challenges, music, Mii outfits and movies), so no rewards pop up between matches; a backup is made first and it can be turned off | Live |
| 🩺 | **Crash reports**: if the game crashes, the launcher sends its log (user and PC names removed) to help fix it; can be turned off | Live |
| 💾 | **Light archive**: only what competitive 1v1 needs (8.8 of 16.4 GB) | Live |
| 👥 | Friends, invites and Arena lobbies | In development |
| 📈 | Automatic ranked 1v1 with skill-based ratings | In development |

The full roadmap is on [ssbuarena.com/project](https://www.ssbuarena.com/project).

## How it works

The game's AArch64 code is translated to C ahead of time and compiled into a native Windows program: no emulation.
The console's system libraries, graphics, audio, input and files are re-implemented, and the picture is drawn with
Vulkan.

```
your copy --> tools/extract --> main executable, SDK, script modules, data
                 |
        tools/lifter (recomp.py)     AArch64 -> C, one C function per game function
                 |
        clang --> game DLL + one DLL per script module
                 |
        runtime/                      what the console provided, re-implemented (threads, files, audio, input,
                                      movies, fonts, network), NVN graphics on Vulkan,
                                      rollback (r1_*.c, rb_*.c) and the online layer (net_relay.c)
```

Releases ship the launcher and a prebuilt runtime (our code only, audited for game content by
[`scripts/make_release.py`](scripts/make_release.py)); the game itself is always built on the player's PC.

## Rollback performance

A rollback goes back to the last confirmed frame, loads the state saved there and plays the frames again with the right
inputs (re-run passes), then plays the new frame. At 60 Hz all of that has to fit in 16.7 ms, together with the frame itself.

Standard rollback bench, CPU time on the game's main thread per frame, median over the measured segment:

| Stage | Frame with a rollback | Normal frame | Pre-load hold | Load | Re-run passes (first + last) | Rest: real pass and its state save |
|---|---|---|---|---|---|---|
| Battlefield | 6.74 ms | 4.07 ms | 0.16 | 1.32 | 2.43 (1.44 + 0.96) | 2.83 |
| Small Battlefield | 6.53 ms | 4.02 ms | 0.14 | 1.33 | 2.30 (1.36 + 0.90) | 2.76 |
| Final Destination | 6.89 ms | 4.35 ms | 0.18 | 1.34 | 2.27 (1.32 + 0.91) | 3.10 |
| Pokémon Stadium 2 | 7.10 ms | 4.24 ms | 0.14 | 1.35 | 2.63 (1.50 + 1.09) | 2.98 |
| Town and City | 7.67 ms | 4.54 ms | 0.17 | 1.46 | 2.82 (1.62 + 1.16) | 3.22 |
| Smashville | 7.41 ms | 4.40 ms | 0.15 | 1.38 | 2.72 (1.54 + 1.13) | 3.16 |
| **Average** | **7.06 ms** | **4.27 ms** | 0.16 | 1.36 | 2.53 (1.46 + 1.03) | 3.01 |

- The breakdown columns are means per rollback, in ms; "Rest" is the median minus the other parts. The state of every new
  frame is saved: median 1.16 ms (mean 1.30), on the main thread (a helper thread shares the page copy). It is part of
  "Normal frame" and of "Rest". Each extra re-run pass of a deeper rollback costs about 0.9 ms.
- 281 rollbacks per stage: 270 of 2 frames, 11 of 3 frames. 99th percentile of the frames with a rollback: 9.9 to 11.4 ms.
- Version 0.2.49 measured about 9.7 ms on Battlefield, 10.9 ms on Town and City and 10.0 ms on Final Destination with the
  older harness (one match per stage, rollbacks from the loop bots without a fixed schedule), so the comparison is approximate.

How it is measured (`rb8ms/bench.sh` on the developers' PC, `SSBU_BENCH`, [`runtime/rb_bench.c`](runtime/rb_bench.c)): one game
process plays the six stages in a row, Mario against Fox with player 2 a CPU, inputs from seeded bots and a rollback schedule
that depends only on the frame number (the loop test: player 2's inputs arrive 3 frames late), so every run gets the same
rollbacks. Per stage, 1885 warm-up frames, then 1800 measured frames. The numbers are the mean of three runs (15/11/2026, commit
b86151d and its parent 44aabec: the rollback defaults of 0.2.50) on a Ryzen 5 3600 with an RTX 2080 SUPER, Windows 11.

## Build from source

Everything the launcher does can be done by hand:

```bash
git clone https://github.com/ssbuarena/ssbu-arena
```

Then follow **[BUILDING.md](BUILDING.md)** (Linux: [BUILDING_LINUX.md](BUILDING_LINUX.md), macOS:
[BUILDING_MAC.md](BUILDING_MAC.md)). Contributions are welcome: read **[CONTRIBUTING.md](CONTRIBUTING.md)** first.

## FAQ

**Is this legal?** The repository contains no game code, assets, keys or firmware, and does not help anyone obtain
them. You build the program on your PC from a copy of the game you own. This is not legal advice; laws differ by
country.

**Does it include the game?** No. Not the game, not the keys, not the firmware, and no links to them.

**Is my IP shared with the other player?** No. Every online packet goes through the Arena server; the other player only
sees a virtual address of the server's network.

**What data does SSBU Arena collect?** Only what an account and online play need. See the
[privacy policy](https://www.ssbuarena.com/privacy).

## Credits

SSBU Arena is made by **[Acid236](https://github.com/Acid236)**: online play through the relay server, rollback
netcode online, the ping overlay, the competitive mode and light archive, the Arena menu, Battle Arena, replays, the
support for several game updates and the launcher. Thanks to **aspect3322** for the first 13.0.5 address work (PR #2)
and to everyone who reports bugs and sends crash logs.

It exists because of **[OpenSmash-Ultimate / ssbu-recomp](https://github.com/OpenSmash-Ultimate/ssbu-recomp)**.
Thank you to **Denkishi**, who created it and wrote most of it, and to its contributors: **Stephen Jayakar**,
**xtlg511**, **v1Ta44**, **Frank Fly**, **Victor Navega** and **jfb112697**. The full history of their work is kept in
this repository's git history. Details, and the open-source projects both projects build on, are in
**[CREDITS.md](CREDITS.md)**.

## Legal and license

SSBU Arena is not affiliated with, endorsed or sponsored by Nintendo, Bandai Namco or Sora Ltd. *Super Smash Bros.* and
*Nintendo Switch* are trademarks of their owners and are named only to say what the software works with. The
repository contains no copyrighted game data, no console keys and no firmware, and does not help anyone obtain them.
**You must own the game and dump it yourself.** No Nintendo logos or artwork are used.

Licensed under the GNU General Public License, version 3 or later, the same as OpenSmash-Ultimate (see
[LICENSE](LICENSE)). Files keep their original copyright and license notices.
