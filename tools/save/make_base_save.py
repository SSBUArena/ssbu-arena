"""Make the launcher's base save (tools/launcher/arena/Assets/base_save.zip) from saves the game has just made.

    python tools/save/make_base_save.py <userdata folder of a first start>           step 1
    python tools/save/make_base_save.py --seen <userdata folder of step 1's start>   step 2

The base save is what a player without a save starts from (tools/launcher/arena/BaseSave.cs): the game's own save of a
first start, every base-game fighter unlocked, and the screens the game shows once for that already seen.
- Step 1: start the game with SSBU_USERDATA set to a new, empty folder and no SSBU_COMPETITIVE / SSBU_COMPETITIVE_UI /
  SSBU_MENU (the game as it is), press A through the title, the first milestone and its rewards until the main menu,
  close it. This step reads that folder's save\\save_data, unlocks the base-game fighters and writes the zip.
- Step 2: put that zip's save_data in another new folder (<folder>\\save\\save_data), start the game on it the same way
  and press A (B to leave the challenge board) through what it shows once for the unlocked fighters (the challenge
  "Unlock 10 fighters" and its coins, the milestone "You've unlocked every fighter!") until the main menu, close it.
  This step checks that save and writes it as the zip: a new player's first start then goes from the title to the menu.

The save layout (13.0.4 and 13.0.5) after OpenSmash-Ultimate's tools/unlock_all.py: system_data.bin of 0x5b4af8 bytes,
format 0x1b at offset 4, 8 bytes per fighter at FLAGS indexed by save_no ([0] 1 = selectable, [1..3] challenger state);
0..73 are the base game's fighters, 74.. the DLC fighters (the game unlocks those itself from the add-on contents
owned: left as they are). A fighter unlocked here gets no "joined the battle" milestone, as a starter has none.
Step 1's source is checked to be a first start's save (only the eight starters selectable, no challenger) and step 2's
to have exactly the base-game fighters, so no one's progress or settings end up in the base save. The made save holds no
game content: it is the game's save data.
Generated 2026-10-29 from the 13.0.4 game (save version 0xd004; 13.0.5 reads it and upgrades it itself).
"""
import os
import struct
import sys
import zipfile

SIZE, FORMAT, FLAGS, BASE_FIGHTERS = 0x5B4AF8, 0x1B, 0x53FF40, 74
STARTERS = {0, 1, 2, 3, 5, 6, 7, 8}        # Mario, Donkey Kong, Link, Samus, Yoshi, Kirby, Fox, Pikachu
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'launcher', 'arena', 'Assets', 'base_save.zip')
FIXED_TIME = (2026, 10, 29, 0, 0, 0)       # every member's date: the same zip from the same save


def main():
    seen = len(sys.argv) == 3 and sys.argv[1] == '--seen'
    if len(sys.argv) != 2 and not seen:
        sys.exit(__doc__)
    src = os.path.join(sys.argv[-1], 'save', 'save_data')
    path = os.path.join(src, 'system_data.bin')
    d = bytearray(open(path, 'rb').read())
    if len(d) != SIZE or struct.unpack_from('<I', d, 0)[0] != SIZE or struct.unpack_from('<I', d, 4)[0] != FORMAT:
        sys.exit('%s: not a system_data.bin of the known layout' % path)
    selectable = {n for n in range(86) if d[FLAGS + 8 * n] == 1}
    challengers = any(d[FLAGS + 8 * n + 1:FLAGS + 8 * n + 8].strip(b'\0') for n in range(86))
    if seen:
        if selectable != set(range(BASE_FIGHTERS)) or challengers:
            sys.exit('%s: not a start from step 1\'s save (selectable %s)' % (path, sorted(selectable)))
    else:
        if selectable != STARTERS or challengers:
            sys.exit('%s: not a first start\'s save (selectable %s): make a new one as the help says' % (path, sorted(selectable)))
        for n in range(BASE_FIGHTERS):
            d[FLAGS + 8 * n] = 1
    files = [('save_data/system_data.bin', bytes(d))]
    for root, _, names in os.walk(src):
        for name in sorted(names):
            rel = os.path.relpath(os.path.join(root, name), src).replace(os.sep, '/')
            if rel != 'system_data.bin':
                files.append(('save_data/' + rel, open(os.path.join(root, name), 'rb').read()))
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with zipfile.ZipFile(OUT, 'w', zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for rel, data in sorted(files):
            z.writestr(zipfile.ZipInfo(rel, FIXED_TIME), data, zipfile.ZIP_DEFLATED, 9)
    print('%s: %d files, %d bytes; %d base-game fighters selectable%s' % (OUT, len(files), os.path.getsize(OUT), BASE_FIGHTERS,
                                                                         '; first-start screens seen' if seen else ''))


if __name__ == '__main__':
    main()
