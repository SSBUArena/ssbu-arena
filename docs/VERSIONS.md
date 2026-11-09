# Game builds: 13.0.4 and 13.0.5

The port runs **13.0.4** and **13.0.5** (update v2031616, released 1 September 2026, which replaced 13.0.4 on consoles;
Nintendo's notes list one change: "Fixed behavior that occurs when invalid data is sent or received in online battles").
Both are built from the same sources. 13.0.4 stays the reference: every game address written in the sources is a 13.0.4
address.

## How a build is recognised

Step `convert` reads the size of `main`'s code (the first segment of the converted dump) and refuses anything else:

| `main` code size | build | converted dump |
|---|---|---|
| 0x39c7e90 | 13.0.4 | `build/binaries/13.0.4/` |
| 0x39c8440 | 13.0.5 | `build/binaries/13.0.5/` |

The build goes into `build/state.json` (`game_version`); every later step and `scripts/run.py` take the folder from there,
and the tools find the dump through `RECOMP_BIN`. A build folder that held the other build runs every later step again.
The runtime recognises the build the same way when it loads `main.elf` and prints it:

```
main: build 13.0.5, text 0x39c8440, data at 0x4f17000, RELA 0x39c9070 / 0x7ad0d8, JMPREL 0x4176148 / 0xc600, SYMTAB 0x4198610, INIT_ARRAY 0x52a8be0 / 0x990
```

## What differs, measured on the binaries

| | 13.0.4 | 13.0.5 |
|---|---|---|
| `main` code | 0x39c7e90 bytes | 0x39c8440 (+0x5b0) |
| `main` read-only data | at 0x39c8000, 0x154df60 bytes | at 0x39c9000, same size |
| `main` data | at 0x4f16000, 0x392570 bytes in the file | at 0x4f17000, same size |
| `main` RELA / JMPREL / SYMTAB / STRTAB / INIT_ARRAY | 0x39c8070 / 0x4175148 / 0x4197610 / 0x41c11f0 / 0x52a7be0 | each +0x1000, same sizes |
| `sdk` | code 0x540950, image 0xcfb000, 25,400 symbols | same layout; the same 1,375 functions lift |
| script modules in `data.arc` | 98 | 98 |

Code: the 13.0.4 function list of the matching decompilation (sbergeron42/ssbu-decomp, `data/functions.csv`, 37,496
functions inside the code) was lined up with 13.0.5's own unwind table (`.eh_frame_hdr`, 110,842 entries) by function size.
Code is at the same address up to about 0x710236e830, apart from a small region near 0x71016ab000..0x71018cc000 that moved
by -0x1a0 / -0x40; after that it moves by +0x450, +0x490, +0x590 and finally +0x5b0 to the end. 26,897 functions match by
size under these offsets; most of the rest are entries whose Ghidra boundaries are approximate. Read-only data, data and
bss moved by exactly one page (+0x1000).

## How the sources handle both

- **The layout of `main`** is read from `main.elf` instead of being written down: the lifter (`tools/lifter/recomp.py`,
  `_layout`) and the runtime (`runtime/hostrt.c`, `load_layout` / `load_dynamic`) read the segments from the program
  headers and the relocation tables, symbol table and init array from the dynamic section that MOD0 points to. The
  constants this replaces are in the comments next to that code.
- **Game addresses** are written as their 13.0.4 value and passed through `GA()` (`runtime/hostrt.h`), which gives the
  address in the build that runs. On 13.0.4 it returns its argument. On 13.0.5 an address in read-only data, data or bss
  moves by one page, and an address in code is looked up in `g_code_13_0_5` (`runtime/hostrt.c`), one line per
  address the runtime uses. A code address that is not in that table is looked for by its code: `runtime/ga_sig_13_0_4.inc`
  (made by `tools/port/ga_sigs.py` from a 13.0.4 `main.elf`; addresses and hashes only) holds a fingerprint of the code at
  each address the sources name, a hash of a few instructions with the parts that change when code moves masked out, chosen
  so that it is unique within +-0x1000 on 13.0.4. On 13.0.5 `GA()` takes the one place within +-0x1000 with that hash
  and logs `found on 13.0.5 by its code`. An address with no fingerprint, or whose code is found at no place or at more
  than one, stops the program with a message: calling the wrong function silently would be worse. So does `GA()` before
  `main.elf` has been read. `SSBU_GA_CHECK=1` on 13.0.5 checks every table entry against the search at start and logs
  the count (`GA check: ...`): it shows whether the search finds the right places on a real 13.0.5 dump.
- `main`'s layout fields (`g_main`) replace the constants that described it: the init array in `boot.c` and in the C++
  runtime self-test, the start of the data segment in the image-protection check of `nnmisc.c`.

## Playing online across builds (cross-version)

13.0.4 and 13.0.5 play online together, rollback included, as if they were one version (`SSBU_XVER`, default on;
`runtime/hostrt.c` `game_xver`). What was compared (2026-11-09, `tools/port/arc_cmp.py` and the segment check below):

- `data.arc`: the same 590,711 paths at the same offsets with the same sizes; the stored bytes of one file differ:
  `pokemon/togedemaru/motion/body/c00/end.nuanmb` (the Poke Ball Pokemon's exit animation), where 29 one-frame
  material / visibility tracks (types vector, bool, float, texture, pattern; no bone transform) changed their constant
  encoding from 5 to 2. Nothing that moves a fighter, a hitbox or a stage: rendering only.
- `main`'s read-only data and data: every differing word is an address that moved (by +0x1000 for data, by the code
  shift for pointers to code, and the relative offsets of jump tables and unwind tables by their difference).
- `main`'s code: 8 functions changed size (13.0.4 addresses 0x169bef0, 0x178ab60 and 0x1799860 near the character
  select's slot code, 0x18393d0, 0x23108b0, 0x2633c90 `create_stage`, 0x2b4d3c0 near the stage builder's, 0x2d33e90), all
  else moved unchanged; this fits Nintendo's note (checks on data received online).
- The rollback checksum (fighter_host.c: fighter entry, status, motion hash, frame, position, damage, the random
  generators' words, the pads) holds values, not addresses.
- Room pairs 13.0.4 against 13.0.5 with rollback (two matches each, NETSIM 0 and 27, and Squad Strike): IDENTICAL.

How: the build id sent to the lobby is `ssbu-recomp-ldn1-13.0.x` for both (the lobby, the in-game LAN lobby and
matchmaking part players by an exact build id, so no server change), and the rollback handshake (`rb_net.c` hs_check)
accepts the other build of the family (log `[rbnet] handshake: cross-version match: game update 13.0.4 here, 13.0.5
there`). The game's own Local Wireless room join works across the two as is. `SSBU_XVER=0`: build id `-13.0.4` /
`-13.0.5` and the handshake refuses another build, as before; a game with `SSBU_XVER=0` and one without never see each
other's rooms. Players of an older release (build id `-13.0.4` / `-13.0.5`) are in a part of their own, as before.

Replays (`.ssbv`, the launcher's viewer) hold ids (ui_chara / motion hash40, kinds, stage ids) and positions, no
addresses; `game_build` in the header is information only. The two games' files of a cross-version match have the same
fighters, objects and frames (the stage-line scan of the first frames differs between any two games, also of one build).

## Checking the table on your own dump

```
python tools/port/verify_13_0_5.py build/binaries/13.0.5/main.elf <functions.csv>
```

`<functions.csv>` is `data/functions.csv` of sbergeron42/ssbu-decomp (addresses, sizes and names; no code). The script
finds every game address in `runtime/` and `fighter_sdk/` and checks what `GA()` answers on 13.0.5: a code address must
be in `g_code_13_0_5`, and its delta must be the one at which the same-size functions on both sides of it (or its own
function) line up in the 13.0.5 unwind table; a data address must be formed by 13.0.5 code (ADRP + ADD/LDR/STR) more
often at its new address than at its old one. It also checks the one-page rule against the segments, and that no
`0x71xxxxxxxx` literal in code bypasses `GA()` without a stated reason. On the author's dump it reports
`all checks passed` (46 code and data addresses, 3 segment checks, 7 literals that are not addresses of things that move);
on a 13.0.5 dump on 2026-11-09, after the additions since: `all checks passed` (210 checks).

## Adding a game address

Write the 13.0.4 address and wrap it: `GA(0x71xxxxxxxxull)`. If it is in code, run
`python tools/port/ga_sigs.py build/binaries/13.0.4/main.elf` so it gets a fingerprint; with a 13.0.5 dump at hand, also
add its 13.0.5 counterpart to `g_code_13_0_5` (sorted) and run the check above. An address only known on 13.0.5 cannot be written this way; find its
13.0.4 counterpart with the same method, or say so in the pull request.

## Not covered

- `tools/lifter/patches/kinds.txt` (new fighter kinds, off by default) holds instruction patches at 13.0.4 addresses.
  Every patch states the original instruction and `recomp.py` refuses a patch the binary does not match, so on 13.0.5 the
  lift stops instead of patching the wrong instruction; `kinds_scan.py` would have to be run on the 13.0.5 binary.
- `fighter_sdk/tools/`, `tools/audit/` and the scripts under `tests/` still name `binaries/13.0.4` and 13.0.4 constants:
  they are the author's analysis and test drivers, not part of the build.
- A mod DLL that hooks a function of `main` by its address is written for one build.
- Run-ahead and the online rollback run on 13.0.5 as on 13.0.4 again (since 2026-11-09). They had been turned off
  (`runtime/hostrt.c`, `game_rb_blocked`) after a 13.0.5 player's run-ahead crashed a few seconds into every fight
  (render thread, `nu::GraphicsDevice::Update`). Checked on a real 13.0.5 dump: the main loop is the same code at +0x5b0
  (every kick / wait call site, the save point, the workers' hold and the skipped preparation kicks), run-ahead ran
  ~3000 speculative passes without a crash, and a 13.0.5 room pair with rollback was IDENTICAL. The online rollback
  had in fact stopped at start on 13.0.5 (a game address missing from `g_code_13_0_5`, fixed). The player's crash was
  not reproduced; plain run-ahead now also has the rollback's retainer fixes (`SSBU_RA_RETAINFIX=0` turns them off).
  `SSBU_R1_1305=0` turns both off on 13.0.5 as before (popup, menu notes, launcher text); `SSBU_R1_1305_TEST=1`
  (tests only) makes 13.0.4 take the 13.0.5 path.
- Replays: Nintendo's notes say replays from 9.0.0 to 13.0.4 may have compatibility issues in 13.0.5.
