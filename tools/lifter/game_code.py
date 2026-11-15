"""(online-mm 15/11) The game code: which code a game runs, as a short text the lobby and the rollback handshake compare.

    e<exe 8 hex>d<DLL 8 hex><DLL settings 4 hex>        e.g. e1a2b3c4dd5e6f7a8b9c0d

  exe          the runtime's own sources the exe is built from (build.py HOST_SOURCES, gamedll.c, menu.cpp and every header
               they include from the source folder): the same on every PC that builds the same release (the prebuilt exe of
               the runtime package, or one linked here)
  DLL          the sources of OUR code inside the game DLL: the headers the lifted code includes (runtime.h, neon.h,
               neon_fast.h, fpexact.h and what they include), the DLL's shims and the lifter scripts that write the lifted C.
               Not the lifted C itself: that comes from each player's own dump (13.0.2 / 13.0.4 / 13.0.5 play together,
               SSBU_XVER), and the build already recompiles a lifted file whose content changed (build.py _srchash.json)
  settings     the DLL's compile settings (runtime.h switches, R1_GAMEDLL, the extra flags, the shim): build_env of
               scripts/setup.py, the same on every PC

The exe carries its own part and the DLL part it was built with ("expects"); the DLL carries its DLL and settings parts:
  - in the DLL: the export ssbu_dll_code and a section .ssbuid ("SSBU-GAME-CODE:" + the text), written by build.py gamedll
    (game_code_dll.c in the DLL's data folder), read by the exe (runtime/game_code.c) and by the launcher (GameCode.cs,
    without loading the 660 MB DLL);
  - in the exe: section .ssbuid ("SSBU-EXE-CODE:" exe part, "SSBU-DLL-WANT:" DLL part), runtime/game_code.c compiled with
    -DSSBU_EXE_CODE / -DSSBU_DLL_WANT by build.py.
A DLL whose part differs from what the exe expects was built from other sources (13/11: exe 0.2.49 + a test DLL): the
launcher builds it again (SSBU_ARENA_CODE_CHECK), the lobby never pairs games with different codes (match_game_code),
the rollback handshake refuses them (SSBU_RB_NET_CODE_CHECK).

    python game_code.py [folder]      the three parts of a source folder (default: this script's folder)
No game content: hashes of our own source files only. Line ends do not count (CRLF = LF)."""
import hashlib
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
# what the lifted code includes (build.py GEN_HEADERS; neon.h includes neon_fast.h, runtime.h fpexact.h / neon.h)
DLL_HEADERS = ['runtime.h', 'neon.h', 'neon_fast.h', 'fpexact.h']
# every shim the DLL may be linked with (setup.py uses game_shim_r1.c; the list does not depend on RECOMP_SHIM, so the exe
# built without that variable (scripts/make_release.py) expects the same part)
DLL_SHIMS = ['game_shim.c', 'game_shim_eh.c', 'game_shim_r1.c']
# the lifter scripts that write the lifted C or split it into units
DLL_SCRIPTS = ['recomp.py', 'recomp_eh.py', 'recomp_partial.py', 'genflags.py', 'gensplit.py', 'tu_prepare.py', 'ctxscope.py',
               'sdklift.py', 'relift_sites.py', 'gen_imports.py']
# files the build writes into the source folder (scripts/setup.py GENERATED_NAMES): never part of a code
GENERATED = {'sdklift_gen.c', 'sdkdata.c', 'sdklift_install.h', 'zstd_gen.c', 'nro_registry.c', 'fake_whitelist.h', 'fake_openlist.h',
             'difftest_gen.c', 'eh_tests.h', 'quad_tests.h', 'sdktest_data.h', 'game_data.c', 'game_code_dll.c'}
INCLUDE = re.compile(rb'^\s*#\s*include\s+"([^"]+)"', re.M)


def _text(path):
    with open(path, 'rb') as f:
        return f.read().replace(b'\r\n', b'\n')


def closure(folder, names):
    """names and every file they #include "..." from folder (recursively), sorted; files not in folder are left out"""
    seen, todo = set(), list(names)
    while todo:
        n = todo.pop().replace('\\', '/')
        if n in seen or os.path.basename(n) in GENERATED:
            continue
        p = os.path.join(folder, n)
        if not os.path.isfile(p):
            continue
        seen.add(n)
        base = os.path.dirname(n)
        for inc in INCLUDE.findall(_text(p)):
            inc = inc.decode('ascii', 'replace')
            for cand in (os.path.join(base, inc).replace('\\', '/'), inc):
                if os.path.isfile(os.path.join(folder, cand)):
                    todo.append(os.path.normpath(cand).replace('\\', '/'))
                    break
    return sorted(seen)


def _hash(folder, names):
    h = hashlib.sha256()
    for n in names:
        h.update(n.encode() + b'\0')
        h.update(_text(os.path.join(folder, n)))
        h.update(b'\0')
    return h.hexdigest()


def dll_part(folder=HERE):
    """8 hex: our sources inside the game DLL"""
    names = closure(folder, DLL_HEADERS + DLL_SHIMS) + [s for s in DLL_SCRIPTS if os.path.isfile(os.path.join(folder, s))]
    return _hash(folder, sorted(set(names)))[:8]


def dll_settings(env=None):
    """4 hex: the DLL's compile settings, from the environment of the build (the same rules as build_opt.py GAME_DEFS)"""
    env = os.environ if env is None else env
    on = lambda k, d: env.get(k, d) not in ('', '0')
    defs = ([] if on('RECOMP_LANES', '1') else ['-DRECOMP_LANES_LOOP']) + ([] if on('RECOMP_FAST_FMAF', '1') else ['-DRECOMP_FMAF_LIBM'])
    defs += ['-DRECOMP_ICACHE'] if on('RECOMP_ICACHE', '1') else []
    defs += ['-DR1_GAMEDLL'] if on('RECOMP_R1_GAMEDLL', '') else []
    text = ' '.join(defs) + '|' + ' '.join(env.get('RECOMP_CFLAGS', '').split()) + '|' + ' '.join(env.get('RECOMP_DATA_CFLAGS', '').split()) + \
           '|' + os.path.basename(env.get('RECOMP_SHIM', 'game_shim_eh.c')) + '|' + ' '.join(env.get('RECOMP_NF_FNS', '').split(','))
    return hashlib.sha256(text.encode()).hexdigest()[:4]


def exe_part(folder=HERE, sources=None):
    """8 hex: the runtime sources of the exe (build.py HOST_SOURCES + gamedll.c + menu.cpp and their headers)"""
    if sources is None:
        sys.path.insert(0, folder)
        import build                                  # noqa: E402  (HOST_SOURCES of this folder's build.py)
        sources = list(build.HOST_SOURCES)
    # (the sources some builds add or drop: FFmpeg's movie fallback, the POSIX platform layer: always counted, so that the part is
    # the same whatever the environment and the platform of the build)
    extra = ['gamedll.c', 'menu.cpp', 'game_code.c', 'mf_ffmpeg.c', 'plat_posix.c', 'plat_mf.c', 'net_off.c', 'net_relay.c', 'nnldn.c', 'rb_net.c']
    names = sorted(set(n for n in list(sources) + extra if os.path.isfile(os.path.join(folder, n))))
    return _hash(folder, closure(folder, names))[:8]


def dll_code(folder=HERE, env=None):
    """the DLL's text: d<8 hex><4 hex>"""
    return 'd' + dll_part(folder) + dll_settings(env)


def read_code(path, tag):
    """the text after <tag> (b'SSBU-GAME-CODE:' ...) in a built exe or DLL's .ssbuid section; None when there is none.
    Reads the PE section table only (not the whole 660 MB DLL)."""
    import struct
    try:
        with open(path, 'rb') as f:
            head = f.read(4096)
            if head[:2] != b'MZ':
                return None
            pe = struct.unpack_from('<I', head, 0x3c)[0]
            f.seek(pe)
            nt = f.read(24)
            if nt[:4] != b'PE\0\0':
                return None
            nsec, opt = struct.unpack_from('<H', nt, 6)[0], struct.unpack_from('<H', nt, 20)[0]
            f.seek(pe + 24 + opt)
            table = f.read(40 * nsec)
            for i in range(nsec):
                name, size, raw, off = table[i * 40:i * 40 + 8].rstrip(b'\0'), struct.unpack_from('<I', table, i * 40 + 16)[0], 0, 0
                if name != b'.ssbuid':
                    continue
                off = struct.unpack_from('<I', table, i * 40 + 20)[0]
                f.seek(off)
                data = f.read(min(size, 4096))
                k = data.find(tag)
                if k < 0:
                    return None
                end = data.find(b'\0', k)
                return data[k + len(tag):end if end >= 0 else len(data)].decode('ascii', 'replace')
    except (OSError, struct.error):
        return None
    return None


if __name__ == '__main__':
    d = os.path.abspath(sys.argv[1]) if len(sys.argv) > 1 else HERE
    print('exe part  %s' % exe_part(d))
    print('DLL part  %s (settings %s with this environment)' % (dll_part(d), dll_settings()))
