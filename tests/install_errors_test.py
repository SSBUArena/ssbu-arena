"""Test of the setup's error handling from the automatic reports of 0.2.73 / 0.2.74 (fix/install-launcher-errors), without game
files, compilers or a build: only made-up text and empty files in a temporary folder.

    python tests/install_errors_test.py

Checks: scripts/install.py error_line takes an exception's own line out of a Python traceback (not "~~~~^^^^"); explain
recognises Windows' refusals (Smart App Control's WinError 4551 in English and Spanish, a full disk, a file in use) and leaves
other text alone; check_given_update stops on the base game given as the update and on update 13.0.3, and lets 13.0.4 and
an update whose version is not in its name pass; tools/lifter/build.py drop_damaged_objects removes the objects lld-link
and link.exe call damaged (and nothing else) and says so with install.py's DAMAGED_MARK.
"""
import io
import os
import sys
import tempfile
import contextlib

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, '..', 'scripts'))
sys.path.insert(0, os.path.join(HERE, '..', 'tools', 'lifter'))
import install  # noqa: E402

fails = 0


def check(what, ok):
    global fails
    print(('ok    ' if ok else 'FAIL  ') + what)
    fails += 0 if ok else 1


TRACE = '''Traceback (most recent call last):
  File "scripts\\setup.py", line 1271, in step_check
    out = cx.run('check', [exe, main_elf, '60'])
    ~~~~~~~~~~~~~~^^^^^^^^^^^^^^^^^^^^^^^^^^^
  File "subprocess.py", line 1555, in _execute_child
OSError: [WinError 4551] An Application Control policy has blocked this file
'''
TRACE_ES = TRACE.replace('An Application Control policy has blocked this file', 'Una directiva de Control de aplicaciones bloque? este archivo')

check('error_line: the exception line of a traceback', install.error_line(TRACE).startswith('OSError: [WinError 4551]'))
check('error_line: not the caret line', '~~~' not in install.error_line(TRACE.rsplit('\n', 2)[0] + '\n    ~~~~^^^^\n'))
check('error_line: a plain last line', install.error_line('a\nb\nmain is not a build this port knows\n') == 'main is not a build this port knows')
e = install.explain(TRACE)
check('explain: WinError 4551 = Smart App Control', e is not None and 'Smart App Control' in e[0] + e[1])
e = install.explain(TRACE_ES)
check('explain: WinError 4551 in Spanish', e is not None and 'Smart App Control' in e[1])
check('explain: a full disk', 'full' in (install.explain('OSError: [WinError 112] There is not enough space on the disk') or ('',))[0])
check('explain: a file in use', 'another program' in (install.explain("PermissionError: [WinError 32] The process cannot access the file because it is being used by another process") or ('',))[0])
check('explain: other text is left alone', install.explain('FAILED at step convert: main is not a build this port knows') is None)

with tempfile.TemporaryDirectory() as d:
    base = os.path.join(d, 'Super Smash Bros Ultimate [01006A800016E000][v0].nsp')
    u3 = os.path.join(d, 'Super Smash Bros. Ultimate v13.0.3 [01006A800016E800].nsp')
    u4 = os.path.join(d, 'Super Smash Bros. Ultimate [01006A800016E800][v1966080].nsp')
    unk = os.path.join(d, 'ssbu_update.nsp')
    for p in (base, u3, u4, unk):
        open(p, 'wb').close()
    import find_inputs
    items = [find_inputs.classify_game_file(p) for p in (base, u3, u4)]
    r = {'base': [i for i in items if i and i['kind'] == 'base'], 'updates': [i for i in items if i and i['kind'] == 'update']}

    def stops(upd, b=base):
        try:
            install.check_given_update(upd, b, r)
            return ''
        except install.Stop as s:
            return s.msg
    check('given update: the base game itself', 'base game' in stops(base))
    check('given update: 13.0.3 stops', '13.0.3' in stops(u3))
    check('given update: 13.0.4 passes', stops(u4) == '')
    check('given update: no version in the name passes', stops(unk) == '')

    import build
    os.makedirs(os.path.join(d, 'obj_r1'))
    bad, good = os.path.join(d, 'obj_r1', 'c_0473.obj'), os.path.join(d, 'obj_r1', 'c_0474.obj')
    bad2 = os.path.join(d, 'obj_r1', 'c_0475.obj')
    for p in (bad, good, bad2):
        open(p, 'wb').close()
    out = ('lld-link: error: obj_r1/c_0473.obj: unknown file type\n'
           'lld-link: error: undefined symbol: foo\n'
           '%s : fatal error LNK1136: invalid or corrupt file\n' % bad2)
    buf = io.StringIO()
    with contextlib.redirect_stdout(buf):
        n = build.drop_damaged_objects(out, cwd=d)
    check('damaged objects: both removed', n == 2 and not os.path.exists(bad) and not os.path.exists(bad2))
    check('damaged objects: the others kept', os.path.exists(good))
    check('damaged objects: install.py sees the mark', install.DAMAGED_MARK in buf.getvalue())
    with contextlib.redirect_stdout(io.StringIO()):
        check('damaged objects: a plain link error removes nothing', build.drop_damaged_objects('lld-link: error: undefined symbol: foo', cwd=d) == 0)

print('%d failed' % fails if fails else 'all passed')
sys.exit(1 if fails else 0)
