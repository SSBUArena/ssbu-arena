#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""Compare two data.arc (e.g. 13.0.4 and 13.0.5, docs/VERSIONS.md "Playing online across builds"): every path by its hash,
the stored sizes and flags, and a sha1 of the stored bytes. Reads both archives once (about 35 GB). No game content.

    python tools/port/arc_cmp.py <data.arc A> <data.arc B>
"""
import sys, os, hashlib
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'nro'))
from arc_extract import Arc
A, B = Arc(sys.argv[1]), Arc(sys.argv[2])
print('A paths %d datas %d version %#x | B paths %d datas %d version %#x' % (A.n_path, A.n_datas, A.version, B.n_path, B.n_datas, B.version))
def paths(a):
    d = {}
    for i in range(a.n_path):
        (ph, _), (xh, _), (parent, _), (name, _) = a.file_path(i)
        d[ph] = (i, xh, parent)
    return d
pa, pb = paths(A), paths(B)
print('only in A:', len(set(pa) - set(pb)), 'only in B:', len(set(pb) - set(pa)))
for h in sorted(set(pa) ^ set(pb)): print('  only %s %#012x' % ('A' if h in pa else 'B', h))
def raw(a, r):
    a.f.seek(r['offset']); return hashlib.sha1(a.f.read(r['comp_size'])).hexdigest()
nd = 0; ns = 0; n = 0
for h in sorted(set(pa) & set(pb)):
    ra, rb = A.file_record(pa[h][0]), B.file_record(pb[h][0])
    n += 1
    da = (ra['comp_size'], ra['decomp_size'], ra['data_flags']); db = (rb['comp_size'], rb['decomp_size'], rb['data_flags'])
    if da != db:
        nd += 1; print('DIFF size %#012x ext %#012x parent %#012x: %s vs %s' % (h, pa[h][1], pa[h][2], da, db)); continue
    if raw(A, ra) != raw(B, rb):
        ns += 1; print('DIFF data %#012x ext %#012x parent %#012x size %d' % (h, pa[h][1], pa[h][2], ra['decomp_size']))
    if n % 50000 == 0: print('..', n, flush=True)
print('compared %d paths: %d size differences, %d content differences' % (n, nd, ns))
