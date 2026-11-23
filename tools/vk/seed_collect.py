#!/usr/bin/env python3
"""The seed pipeline list (runtime/nvn_vk_m4.c "THE SEED LIST"): pipes_seed.bin, shipped beside ssbu_native.exe.

It holds pipeline state keys and stage hashes only (no game code, no SPIR-V): for each pipeline a draw or a dispatch used in
one of our collection runs, the hash of each stage's code (before our version / resolution-scale parts), its canonical
variant key and forced outputs, and the pass key (formats, samples, topology class, logic op; no blend state: extended
dynamic state 3). A player's game turns every entry into a pipeline of ITS GPU at start (AMD: whole pipelines, made once by
the first-run preparation, then pipeline-cache hits).

  python tools/vk/seed_collect.py matchlist <out.txt> [--per-match 4] [--cpu 9]
        the SSBU_DEBUG_MATCH_LIST lines of a collection session: every fighter at least once, the six competitive stages in
        turn (4 fighters a match: p1 / p2 / p3 / p4; CPU level 9 so the CPUs end a match quickly), and a last idle match
        (Mario against a level-1 CPU) that SSBU_DEBUG_END ends: the session ends by itself
  python tools/vk/seed_collect.py seq <out.txt> <matches>
        (amd-fixes) the SSBU_SEQ of such a session on the SSBU Arena menu: the way to the first match, then for every match
        its set-up (mark dmsetup<n>), a few moves of player 1, player 1 off the left edge (stock=1), A through the results
        until the character select (549-559 draws for 2 s; player 1's fighter is cleared there), the hand moves of the first
        match, then + until the next set-up
  python tools/vk/seed_collect.py merge <out pipes_seed.bin> <collected file> [<collected file> ...]
        the SSBU_VK_SEED_OUT files of several runs -> one list (same header everywhere, duplicates removed, sorted)
  python tools/vk/seed_collect.py info <file>
        what a list holds

A collection run (one game; the files of several runs are merged). (amd-fixes) The session of the shipped list:
  python tools/vk/seed_collect.py matchlist list.txt ; python tools/vk/seed_collect.py seq seq.txt <lines of list.txt - 1>
  SSBU_VK_AMD_SAFE=2 SSBU_VK_SNORM_EMU=1 SSBU_VK_PREWARM=0 SSBU_VK_PREP=0 SSBU_VK_NOHOLD=0 SSBU_VK_SEED_OUT=<file>
  SSBU_DEBUG_MATCH_LIST=list.txt SSBU_SEQ=<seq.txt, comment lines dropped, one line> SSBU_SKIP_MOVIES=1 SSBU_DEBUG_END=180s
  (no fight of the list lasts 180 s: only the last, idle one reaches it), on a test copy of an install with a fresh cache;
  then merge and info.
  SSBU_VK_SEED_OUT=<file>     every pipeline used first in the run is appended (header + records)
  SSBU_VK_CACHE=<empty folder> SSBU_VK_PREWARM=0
                              a stage made from a record of an earlier run has no code hash and is not collected
  SSBU_VK_SNORM_EMU=1         the vertex keys carry the A2B10G10R10 SNORM decode bits an AMD GPU needs (dropped at load
                              on a GPU without the decode)
  on a GPU with 24-bit depth formats, extended dynamic state 3 and dynamic vertex input (an NVIDIA card); any resolution
  scale (the list names codes without the scale part).
"""
import struct
import sys

SEED_MAGIC = 0x44454553
SEED_VERSION = 1
HDR = struct.Struct('<4I')                 # magic, version, sizeof(PKey), flags
STAGE = struct.Struct('<Q4IIi')            # hp, key[4], fo, stage            (32 bytes)
FLAGS = {1: 'SNORM decode bits', 2: '24-bit depth formats', 4: 'extended dynamic state 3', 8: 'dynamic vertex input'}
# PKey (Windows / Linux, no PKEY_VI): u32 vs, fs, chanmask, sample_mask; i32 cfmt[8], dfmt; u8 topo_class, ncolor, samples,
# poly, a2c, dclamp, logic, sshade, blend_on, pad[3]; u8 blend[8][8]
PKEY = struct.Struct('<4I8ii12B64B')

COMPETITIVE_STAGES = ['battle_field', 'battle_field_s', 'end', 'poke_stadium2', 'animal_village', 'animal_city']
FIGHTERS = ('mario donkey link samus samusd yoshi kirby fox pikachu luigi ness captain purin peach daisy koopa ice_climber sheik '
            'zelda mariod pichu falco marth lucina younglink ganon mewtwo roy chrom gamewatch metaknight pit pitb szerosuit wario '
            'snake ike ptrainer diddy lucas sonic dedede pikmin lucario robot toonlink wolf murabito rockman wiifit rosetta littlemac '
            'gekkouga palutena pacman reflet shulk koopajr duckhunt ryu ken cloud kamui bayonetta inkling ridley simon richter krool '
            'shizue gaogaen packun jack brave buddy dolly master tantan pickel edge element demon trail').split()


def read(path):
    data = open(path, 'rb').read()
    if len(data) < HDR.size:
        sys.exit('%s: too short' % path)
    magic, version, pkey_size, flags = HDR.unpack_from(data)
    if magic != SEED_MAGIC:
        sys.exit('%s: not a seed file' % path)
    rec = 8 + 2 * STAGE.size + pkey_size
    body = data[HDR.size:]
    if len(body) % rec:
        print('%s: %d trailing bytes ignored (a record cut at the end of a run)' % (path, len(body) % rec))
    recs = [body[i:i + rec] for i in range(0, len(body) - len(body) % rec, rec)]
    return (version, pkey_size, flags), recs


def describe(hdr, recs):
    version, pkey_size, flags = hdr
    print('version %d, key size %d, flags: %s' % (version, pkey_size, ', '.join(v for k, v in FLAGS.items() if flags & k) or 'none'))
    stages, compute, depth = set(), 0, {}
    for r in recs:
        comp, has_fs = struct.unpack_from('<2I', r)
        compute += comp
        vs = STAGE.unpack_from(r, 8)
        stages.add((vs[0], vs[6]))
        if has_fs:
            fs = STAGE.unpack_from(r, 8 + STAGE.size)
            stages.add((fs[0], fs[6]))
        if not comp and pkey_size == PKEY.size:
            k = PKEY.unpack_from(r, 8 + 2 * STAGE.size)
            depth[k[12]] = depth.get(k[12], 0) + 1
    print('%d pipelines (%d compute), %d distinct stage codes; depth formats (Vulkan numbers): %s' % (
        len(recs), compute, len({h for h, _ in stages}), ', '.join('%d x%d' % kv for kv in sorted(depth.items()))))


def merge(out, inputs):
    hdr0, all_recs = None, set()
    for p in inputs:
        hdr, recs = read(p)
        if hdr0 is None:
            hdr0 = hdr
        elif hdr != hdr0:
            sys.exit('%s: header %s differs from %s (another version, key size or GPU kind): not merged' % (p, hdr, hdr0))
        all_recs.update(recs)
        print('%s: %d records' % (p, len(recs)))
    if not (hdr0[2] & 4) or not (hdr0[2] & 8):
        sys.exit('the runs were not made on a GPU with extended dynamic state 3 and dynamic vertex input: the list would be refused')
    if not hdr0[2] & 1:
        print('WARNING: collected WITHOUT SSBU_VK_SNORM_EMU=1: AMD GPUs will miss the vertex variants of packed normals')
    with open(out, 'wb') as f:
        f.write(HDR.pack(SEED_MAGIC, hdr0[0], hdr0[1], hdr0[2]))
        for r in sorted(all_recs):
            f.write(r)
    print('%s:' % out)
    describe(hdr0, sorted(all_recs))


def matchlist(out, per, cpu=9):
    lines = []
    for i in range(0, len(FIGHTERS), per):
        group = FIGHTERS[i:i + per]
        while len(group) < per:
            group.append(FIGHTERS[len(lines) % len(FIGHTERS)])
        stage = COMPETITIVE_STAGES[len(lines) % len(COMPETITIVE_STAGES)]
        lines.append(','.join('p%d=%s' % (n + 1, f) for n, f in enumerate(group)) + ',stage=%s,cpu=%d,stock=1' % (stage, cpu))
    for n, stage in enumerate(COMPETITIVE_STAGES):            # each stage at least twice
        if sum(stage in l for l in lines) < 2:
            lines.append('p1=mario,p2=fox,stage=%s,cpu=%d,stock=1' % (stage, cpu))
    lines.append('p1=mario,p2=mario,stage=battle_field,cpu=1,stock=3')      # (amd-fixes) the last match: idle, SSBU_DEBUG_END ends it
    with open(out, 'w', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    print('%s: %d matches (%d fighters, %d a match; stages %s); DLC fighters are skipped by a game without them' % (
        out, len(lines), len(FIGHTERS), per, ', '.join(COMPETITIVE_STAGES)))


SEQ_HEAD = """# seed collection on the SSBU Arena menu (tools/vk/seed_collect.py seq): boot -> Smash -> ruleset (one down) ->
# character select -> start; then one block per match of SSBU_DEBUG_MATCH_LIST. Use with SSBU_SKIP_MOVIES=1.
@movie
@draws:22-26/30
@draws:36-44/30
30:Z
@draws:700-800/40
60:W
60:Z
240:W
60:Z
240:W
20:d/6
40:W
30:Z
300:W
@draws:300-900/40
30:u/30 20:r/18 30:Z
120:W
300:W
30:Z
"""


def seq(out, matches):
    """(amd-fixes) The input sequence of a collection session: see the module's text."""
    text = SEQ_HEAD
    def next_match(n):      # A through the results until the character select (player 1's fighter is cleared there): the
        # same hand moves and A as for the first match, then + until set-up n (A does not start a match at that screen)
        return '@dmsetup1\n' if n == 1 else '@until:549-559/120:Z/40\n30:u/30 20:r/18 30:Z\n120:W\n300:W\n30:Z\n@untilmark:dmsetup%d:!/90\n' % n
    for n in range(1, matches + 1):
        text += next_match(n)
        text += '900\n30:X/10 30:rX/10 30:uX/10 30:dX/10 30:Z/10 30:rZ/10 30:C/10\n1:l/900\n'
    text += next_match(matches + 1)                # the idle last match: SSBU_DEBUG_END ends the session
    with open(out, 'w', newline='\n') as f:
        f.write(text)
    print('%s: %d steps for %d matches and the idle last one' % (out, len([t for l in text.splitlines() if not l.startswith('#') for t in l.split()]), matches))


def main(a):
    if len(a) >= 2 and a[0] == 'info':
        hdr, recs = read(a[1])
        describe(hdr, recs)
    elif len(a) >= 3 and a[0] == 'merge':
        merge(a[1], a[2:])
    elif len(a) >= 2 and a[0] == 'matchlist':
        matchlist(a[1], int(a[a.index('--per-match') + 1]) if '--per-match' in a else 4, int(a[a.index('--cpu') + 1]) if '--cpu' in a else 9)
    elif len(a) >= 3 and a[0] == 'seq':
        seq(a[1], int(a[2]))
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv[1:]))
