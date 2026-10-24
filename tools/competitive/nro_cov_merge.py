"""nro_cov_merge.py <campaign dir> <gen_nro dir> <list out> [--closure] [--exclude=<module>,<module>...]

Merges the runs of tools/competitive/nro_coverage.sh (<campaign dir>/<run>/<module>.txt, written by tools/lifter/nro_cov.c)
into one list of guest addresses: the script-module functions executed in competitive play, for build_opt.py
(coverage\\<game build>_nro.txt beside it). Prints, per module, how many functions and statements were executed.
--closure: also every function a kept function calls directly (f_X(c) in its body), as the game's list does
(tools/competitive/closure.py); without it the list is what ran. --exclude: modules left out of the list (compiled as
before): fighters the debug match cannot play (the Miis need Mii data: their runs end in a host exception before the
fight, 2026-10-24), whose few addresses come only from loading. Addresses only, no game content."""
import collections
import glob
import os
import re
import sys

FN = re.compile(r'^void ([fu])_([0-9a-f]+)\(Ctx \*c(?:, uint64_t rp)?\) \{\s*$')
CALL = re.compile(r'\b[fu]_([0-9a-f]{10})\(c')


def scan(gen):
    """{address: statements}, {address: direct callees} of one module's generated files."""
    size, calls = {}, {}
    for n in sorted(os.listdir(gen)):
        if not (n.startswith('c_') and n.endswith('.c')):
            continue
        cur = None
        with open(os.path.join(gen, n), encoding='latin-1') as f:
            for ln in f:
                m = FN.match(ln)
                if m:
                    cur = int(m.group(2), 16)
                    size.setdefault(cur, 0)
                    calls.setdefault(cur, set())
                    continue
                if cur is None:
                    continue
                if ln.startswith('}'):
                    cur = None
                    continue
                size[cur] += 1
                calls[cur].update(int(c, 16) for c in CALL.findall(ln))
    return size, calls


def main(argv):
    camp, gen_root, out = argv[:3]
    closure = '--closure' in argv
    exclude = set()
    for a in argv:
        if a.startswith('--exclude='):
            exclude.update(m if m.startswith('lua2cpp_') else 'lua2cpp_' + m for m in a.split('=', 1)[1].split(',') if m)
    runs = collections.defaultdict(set)
    nruns = collections.Counter()
    for p in glob.glob(os.path.join(camp, '*', '*.txt')):
        mod = os.path.basename(p)[:-4]
        with open(p) as f:
            runs[mod].update(int(l, 16) for l in f if l.strip() and not l.startswith('#'))
        nruns[mod] += 1
    keep = set()
    tot = collections.Counter()
    rows = []
    for mod in sorted(os.listdir(gen_root)):
        gen = os.path.join(gen_root, mod)
        if not os.path.isdir(gen):
            continue
        size, calls = scan(gen)
        hit = set() if mod in exclude else runs.get(mod, set()) & set(size)
        if closure and hit:
            todo = list(hit)
            while todo:
                for g in calls.get(todo.pop(), ()):
                    if g in size and g not in hit:
                        hit.add(g)
                        todo.append(g)
        st_all, st_hit = sum(size.values()), sum(size[a] for a in hit)
        tot['fn'] += len(size)
        tot['st'] += st_all
        tot['fn_hit'] += len(hit)
        tot['st_hit'] += st_hit
        if hit:
            tot['st_mods'] += st_all
        rows.append((mod, 0 if mod in exclude else nruns.get(mod, 0), len(hit), len(size), st_hit, st_all))
        keep |= hit
    for mod, nr, h, n, sh, sa in rows:
        print('%-24s runs %3d  functions %5d / %5d  statements %8d / %8d (%5.1f%%)' % (mod, nr, h, n, sh, sa, 100.0 * sh / max(1, sa)))
    print('all modules: functions %d / %d, statements %d / %d (%.1f%%); modules with coverage: %d of %d (%d statements, executed %.1f%%)'
          % (tot['fn_hit'], tot['fn'], tot['st_hit'], tot['st'], 100.0 * tot['st_hit'] / max(1, tot['st']), len([r for r in rows if r[2]]), len(rows),
             tot['st_mods'], 100.0 * tot['st_hit'] / max(1, tot['st_mods'])))
    with open(out, 'w', newline='\n') as f:
        f.write('# SSBU 13.0.4 script modules (lua2cpp_*, ro_study/nro_map.json addresses): guest addresses of the lifted module\n'
                '# functions executed in competitive play (tools/competitive/nro_coverage.sh: every fighter played by the CPU AI on\n'
                '# the 6 competitive stages, several CPU levels; tools/lifter/nro_cov.c)%s. Addresses only, no game content.\n'
                '# build_opt.py compiles every other function of a module at -O0; nothing is removed. Modules without any address\n'
                '# here are compiled as before.\n' % (', closed over direct calls' if closure else ''))
        for a in sorted(keep):
            f.write('%x\n' % a)
    print('written %s: %d addresses' % (out, len(keep)))


if __name__ == '__main__':
    main(sys.argv[1:])
