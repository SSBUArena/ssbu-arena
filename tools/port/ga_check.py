"""Every game address the runtime names has a place on 13.0.5 and 13.0.2 (no dump needed: CI and arena_release.py run it).

    python tools/port/ga_check.py

The sources name game addresses by their 13.0.4 value and GA() (runtime/hostrt.c) gives the address in the build that runs.
On 13.0.5 and 13.0.2 a code address must be in the build's table (g_code_13_0_5, g_code_13_0_2) or found by its code at
run time; if neither, ga() STOPS the game ("GA: the 13.0.4 code address ... has no 13.0.5 counterpart": issue #10, where
0.2.51 .. 0.2.59 stopped every 13.0.5 game at start). The search by code can miss, so this check asks for the table:
  * every 0x71xxxxxxxx code literal (with or without GA() around it, arrays included) is in BOTH code tables, or in
    ONLY_1304 below with its reason (a feature that is off on 13.0.2 / 13.0.5 by its own build check);
  * every read-only data literal is in g_ro_13_0_2 (13.0.5 moves that segment by one page; 13.0.2 has no search);
  * the tables are sorted with no address twice (ga() searches them by halves), and every table entry has a fingerprint
    in ga_sig_13_0_4.inc unless ga_sigs.py found none (SSBU_GA_CHECK=1 checks the table with them);
  * a literal not passed through GA() is explained in verify_build.py RAW (a raw 13.0.4 address compared with a return
    address is silently never met on the other builds);
  * rb_rollback.c's default SKIPF string and its number list are the same addresses.
Add a new address to both tables after finding it on a 13.0.5 and a 13.0.2 dump (tools/port/verify_build.py, docs/VERSIONS.md),
then run tools/port/ga_sigs.py. Exit code 1 when anything fails. Addresses only: nothing of the game is read or stored."""
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import verify_build as vb  # noqa: E402

ROOT = vb.ROOT
# code addresses that are deliberately used on 13.0.4 only: the feature checks the build and stays off elsewhere
ONLY_1304 = {
    0x1a028b0: 'the amiibo panel hooks (nnapplet.c): raw, installed only when g_game_build == GAME_13_0_4',
    0x1a1dbf0: 'the amiibo panel hooks (nnapplet.c), as above',
    0x1a1fa30: 'the amiibo panel hooks (nnapplet.c), as above',
    0x368ab70: 'rc_units[] (rc_test.c): a study tool of -DR1_RDCOUNT game builds (SSBU_RC), compared raw; never in a release build',
    0x36906b0: 'rc_units[] (rc_test.c), as above', 0x389a390: 'rc_units[] (rc_test.c), as above', 0x38d70f0: 'rc_units[] (rc_test.c), as above',
}
# literals that are not addresses GA() maps (the same in every build, or not in the image)
NOT_MAPPED = {0x0, 0x1000, 0x4000000}


def main():
    bad = []
    t5 = vb.code_table('g_code_13_0_5')          # (asserts sorted and unique)
    t2 = vb.code_table('g_code_13_0_2')
    ro2 = vb.code_table('g_ro_13_0_2')
    sigs = vb.fingerprints()
    wrapped, raw = vb.source_addresses()
    names = set(wrapped) | set(raw)
    n_code = n_ro = 0
    for a in sorted(names):
        where = ', '.join(((wrapped.get(a) or []) + (raw.get(a) or []))[:3])
        if a in NOT_MAPPED or a >= vb.DATA4:
            continue                                  # data and bss: one page later in both builds (ga())
        if a in ONLY_1304:
            continue
        if a < vb.RO4:
            n_code += 1
            miss = [n for n, t in (('g_code_13_0_5', t5), ('g_code_13_0_2', t2)) if a not in t]
            if miss:
                bad.append('0x71%08x (%s): code address missing from %s%s' % (a, where, ' and '.join(miss),
                           '' if a in sigs else '; no fingerprint either (run tools/port/ga_sigs.py)'))
        else:
            n_ro += 1
            if a not in ro2:
                bad.append('0x71%08x (%s): read-only data address missing from g_ro_13_0_2' % (a, where))
    for a, reason in ONLY_1304.items():
        if a not in names:
            bad.append('ONLY_1304 lists 0x71%08x, which no source names any more: remove it' % a)
    for name, t in (('g_code_13_0_5', t5), ('g_code_13_0_2', t2)):
        for a in t:
            if a not in sigs and a not in (0x1358ef0, 0x39bfb30):         # (ga_sigs.py finds no unique window for these two)
                bad.append('%s entry 0x71%08x has no fingerprint in ga_sig_13_0_4.inc: run tools/port/ga_sigs.py' % (name, a))
    if set(t5) != set(t2):
        for a in sorted(set(t5) ^ set(t2)):
            bad.append('0x71%08x is in %s only' % (a, 'g_code_13_0_5' if a in t5 else 'g_code_13_0_2'))
    for a, where in sorted(raw.items()):
        if a not in vb.RAW and a not in ONLY_1304:
            bad.append('0x71%08x (%s) is not passed through GA() and not explained in verify_build.py RAW' % (a, ', '.join(where[:3])))
    s = open(os.path.join(ROOT, 'runtime', 'rb_rollback.c'), encoding='utf-8').read()
    m = re.search(r'g_rbg_skipf_def = "([0-9a-f,]*)"', s)
    n = re.search(r'g_rbg_skipf_def_a\[\] = \{([^}]*)\}', s)
    if not m or not n:
        bad.append('rb_rollback.c: g_rbg_skipf_def / g_rbg_skipf_def_a not found')
    else:
        txt = [int(x, 16) for x in m.group(1).split(',') if x]
        num = [int(x, 16) for x in re.findall(r'0x([0-9a-fA-F]+)', n.group(1))]
        if txt != num:
            bad.append('rb_rollback.c: the default SKIPF string %s and g_rbg_skipf_def_a differ' % m.group(1))
    for b in bad:
        print('FAIL ' + b)
    print('%d code and %d read-only data addresses checked against g_code_13_0_5 / g_code_13_0_2 / g_ro_13_0_2 (%d table entries, '
          '%d marked 13.0.4 only): %s' % (n_code, n_ro, len(t5), len(ONLY_1304), '%d failed' % len(bad) if bad else 'all passed'))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
