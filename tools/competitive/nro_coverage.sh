#!/bin/bash
# Coverage campaign of the SCRIPT MODULES (lua2cpp_*): which of their lifted functions competitive play executes. The
# list (tools/lifter/coverage/<game build>_nro.txt, made by nro_cov_merge.py) lets build_opt.py compile every other
# module function at -O0, like the game's coverage list does for the game code (nothing is removed).
#
# 1. a coverage build of the modules beside a normal build (TEST ONLY; the game DLL and the exe stay as they are), in
#    <build>\work, with the environment setup.py gives the nro step (scripts/setup.py build_env) plus
#        RECOMP_NRO_SUFFIX=_cov RECOMP_NRO_COVERAGE=1 RECOMP_COVERAGE=off   python build_opt.py nro <jobs>
#    -> nro_bin_cov\: the modules compiled with -finstrument-functions-after-inlining and tools/lifter/nro_cov.c
# 2. this script: debug matches (tests/DEBUG_MODE.md), every fighter played by the CPU AI (4 CPU players per match), on the
#    6 competitive stages, at the CPU levels of the list, competitive mode on; one process per match:
#        BUILD=<build folder> bash tools/competitive/nro_coverage.sh <save folder> <out dir> [fight seconds] [level ...]
#    each run writes <out dir>\<tag>\<module>.txt (SSBU_COVERAGE_NRO, nro_cov.c). SFX=<x> runs a second copy at once
#    (its own exe name and save copy).
# 3. python tools/competitive/nro_cov_merge.py <out dir> <gen_nro dir> <list>     (merge, statistics, the list)
set -u
SAVE="$1"; OUT="$2"; SECS="${3:-60}"; shift 3 2>/dev/null || shift $#
LEVELS=("$@"); [ ${#LEVELS[@]} -eq 0 ] && LEVELS=(9)
cd "$(dirname "$0")/../.." || exit 1
BUILD="${BUILD:-build}"
FIGHTERS=(mario donkey link samus samusd yoshi kirby fox pikachu luigi ness captain purin peach daisy koopa ice_climber sheik zelda
  mariod pichu falco marth lucina younglink ganon mewtwo roy chrom gamewatch metaknight pit pitb szerosuit wario snake ike
  pzenigame pfushigisou plizardon diddy lucas sonic dedede pikmin lucario robot toonlink wolf murabito rockman wiifit rosetta
  littlemac gekkouga palutena pacman reflet shulk koopajr duckhunt ryu ken cloud kamui bayonetta inkling ridley simon richter
  krool shizue gaogaen packun jack brave buddy dolly master tantan pickel edge eflame_first elight_first demon trail miifighter
  miiswordsman miigunner)
STAGES=(battle_field battle_field_s end poke_stadium2 animal_village animal_city)
mkdir -p "$OUT"
n=${#FIGHTERS[@]}; k=0
for lv in "${LEVELS[@]}"; do
  off=$(( (lv * 7) % n ))                  # another grouping of the fighters for each level
  for ((i = 0; i < n; i += 4)); do
    f1=${FIGHTERS[(i + off) % n]}; f2=${FIGHTERS[(i + off + 1) % n]}; f3=${FIGHTERS[(i + off + 2) % n]}; f4=${FIGHTERS[(i + off + 3) % n]}
    st=${STAGES[(k + lv) % 6]}; k=$((k + 1))
    tag=$(printf 'l%d_%02d' "$lv" "$k")
    [ -s "$OUT/$tag.out" ] && continue     # done in an earlier, interrupted campaign
    m="p1=$f1,p2=$f2,p3=$f3,p4=$f4,stage=$st,cpu=$lv,p2=cpu,stock=3"
    rm -rf "$SAVE.nrocov${SFX:-}"; cp -r "$SAVE" "$SAVE.nrocov${SFX:-}"; rm -rf "$SAVE.nrocov${SFX:-}"/*.work.*
    echo "=== $tag: $m"
    t0=$(date +%s)
    timeout 600 python -u scripts/run.py --build "$BUILD" --userdata "$SAVE.nrocov${SFX:-}" --exe "ssbu_nrocov${SFX:-}.exe" --refresh \
      --log "$OUT/$tag.log" --seq tools/competitive/seq_real_save.txt --seconds 400 \
      --env SSBU_SPEED=0 --env SSBU_SKIP_MOVIES=1 --env SSBU_AUDIO_MUTE=1 --env SSBU_PAIR_WARM=0 --env SSBU_ONLINE=off --env SSBU_VK_PREP=0 \
      --env SSBU_COMPETITIVE=1 --env SSBU_NRO_BIN=nro_bin_cov --env "SSBU_COVERAGE_NRO=$(cygpath -w "$OUT/$tag" 2>/dev/null || echo "$OUT/$tag")" \
      --env "SSBU_DEBUG_MATCH=$m" --env "SSBU_DEBUG_END=${SECS}s" > "$OUT/$tag.out" 2>&1 < /dev/null
    echo "    $(tail -1 "$OUT/$tag.out")  fights $(grep -ac 'debug match: fight .* on' "$OUT/$tag.log")  modules $(ls "$OUT/$tag" 2>/dev/null | wc -l)  $(( $(date +%s) - t0 )) s"
  done
done
