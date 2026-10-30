"""The launcher's profile pictures: each fighter's square portrait, taken from YOUR data.arc (game content: never in this
repository, never uploaded; an account stores only the fighter's name).

    python tools/ui/extract_fighter_icons.py --arc <data.arc> --out <build>/avatars

Writes <fighter>.png (ui/replace/chara/chara_4/chara_4_<fighter>_00.bntx, 162 x 162) and a marker file when done."""
import argparse
from pathlib import Path
import sys

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from prepare_menu import Arc      # (also sets the path of tools/nro)
from menu_recolor import decode
from menu_textures import texture_slots

# the fighters of the select screen; the ones that share a panel appear once (Ice Climbers: popo, Pokemon Trainer: ptrainer)
FIGHTERS = '''mario donkey link samus samusd yoshi kirby fox pikachu luigi ness captain purin peach daisy koopa popo sheik zelda mariod
pichu falco marth lucina younglink ganon mewtwo roy chrom gamewatch metaknight pit pitb szerosuit wario snake ike ptrainer diddy
lucas sonic dedede pikmin lucario robot toonlink wolf murabito rockman wiifit rosetta littlemac gekkouga palutena pacman reflet
shulk koopajr duckhunt ryu ken cloud kamui bayonetta inkling ridley simon richter krool shizue gaogaen packun jack brave buddy
dolly master tantan pickel edge eflame elight demon trail miifighter miiswordsman miigunner'''.split()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--arc', type=Path, required=True)
    ap.add_argument('--out', type=Path, required=True)
    a = ap.parse_args()
    arc = Arc(str(a.arc))
    a.out.mkdir(parents=True, exist_ok=True)
    made = 0
    for f in FIGHTERS:
        try:
            bntx = bytearray(arc.get('ui/replace/chara/chara_4/chara_4_%s_00.bntx' % f))
        except KeyError:
            continue
        for _, base in texture_slots(bntx).items():
            image = decode(bntx, base)
            if image:
                image[0].save(a.out / (f + '.png'))
                made += 1
            break
    print('profile pictures: %d of %d fighters' % (made, len(FIGHTERS)))
    if made:
        (a.out / 'done.txt').write_text('%d\n' % made)


if __name__ == '__main__':
    main()
