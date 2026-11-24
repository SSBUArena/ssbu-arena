#!/usr/bin/env python3
"""Build the glslang 16.6.0 + SPIRV-Tools libraries with the STATIC C runtime (/MT) into deps\\glslang-mt, for the
ssbu_glslang.dll of the prebuilt runtime package (scripts/make_release.py).

    python scripts/build_glslang_static.py [--out deps\\glslang-mt] [--work build_glslang_mt] [--force]

Why: the official glslang release libraries (deps\\glslang, scripts/install.py TOOLS 'glslang') are built with /MD, so a
ssbu_glslang.dll linked against them imports MSVCP140.dll / VCRUNTIME140.dll / VCRUNTIME140_1.dll, the Visual C++
2015-2022 Redistributable, which many players do not have: on such a PC LoadLibrary("ssbu_glslang.dll") fails and the
first-run shader preparation stops ("ssbu_glslang.dll cannot be loaded", Arena 0.2.64 / 0.2.65). Built from the same
sources with /MT, the DLL imports only KERNEL32 and the Universal CRT (part of Windows 10 / 11).

The sources are the ones the official 16.6.0 release is built from: the glslang tag 16.6.0 and the SPIRV-Tools /
SPIRV-Headers commits of its known_good.json, as GitHub's source archives, SHA-256 checked. Needs the MSVC Build Tools
(their CMake and Ninja are used) and Python; nothing is installed on the PC. runtime/build_glslang.bat sees that the
libraries are /MT (their RuntimeLibrary=MT_StaticRelease linker directive) and links the shim with /MT as well.
The output folder has the same layout as the release zip (include\\, lib\\) plus LICENSE.txt (glslang, SPIRV-Tools,
SPIRV-Headers) and SOURCE.txt, so make_release.py --glslang <it> (or GLSLANG_ROOT) can use it; by default make_release.py
prefers deps\\glslang-mt over deps\\glslang.
"""
import argparse
import hashlib
import os
import shutil
import subprocess
import sys
import tarfile
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
GLSLANG_TAG = '16.6.0'
# name -> (url, sha256, folder inside the glslang tree); commits from glslang 16.6.0's known_good.json
SOURCES = [
    ('glslang', 'https://github.com/KhronosGroup/glslang/archive/refs/tags/16.6.0.tar.gz',
     '9c09b901149c729df745057dafa815278aaa101b84d2b6e14f16a42de52f97f2', ''),
    ('SPIRV-Tools', 'https://github.com/KhronosGroup/SPIRV-Tools/archive/ef96ed763b43b59b33b31b362f09a02b729fa1c9.tar.gz',
     '82c62146083fd558735a3171cf97cfc47903ca7d368482e87f94bd44883c0f00', os.path.join('External', 'spirv-tools')),
    ('SPIRV-Headers', 'https://github.com/KhronosGroup/SPIRV-Headers/archive/496543121ce6419f23d6fa5d7194ba66c36212d2.tar.gz',
     'a9bb9c48713245eacf97cc539b6f1d45405a92d8813f8b82e635f0a085ee9898', os.path.join('External', 'spirv-tools', 'external', 'spirv-headers')),
]
LIBS = ['glslang.lib', 'glslang-default-resource-limits.lib', 'SPIRV-Tools-opt.lib', 'SPIRV-Tools.lib']   # what build_glslang.bat links
MT_MARK = b'RuntimeLibrary=MT_StaticRelease'


def say(s=''):
    print(s, flush=True)


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for b in iter(lambda: f.read(1 << 20), b''):
            h.update(b)
    return h.hexdigest()


VSWHERE = os.path.join(os.environ.get('ProgramFiles(x86)', r'C:\Program Files (x86)'), 'Microsoft Visual Studio', 'Installer', 'vswhere.exe')


def vs_dir():
    vsw = VSWHERE
    if not os.path.isfile(vsw):
        sys.exit('vswhere.exe not found: the MSVC Build Tools are needed')
    r = subprocess.run([vsw, '-latest', '-products', '*', '-requires', 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64',
                        '-property', 'installationPath'], capture_output=True, text=True)
    d = r.stdout.strip().splitlines()[0] if r.stdout.strip() else ''
    if not d:
        sys.exit('no Visual Studio / Build Tools with the C++ x64 tools found')
    return d


def fetch(work, name, url, want):
    dst = os.path.join(work, 'dl', name + '.tar.gz')
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if not (os.path.isfile(dst) and sha256(dst) == want):
        say('downloading %s ...' % url)
        with urllib.request.urlopen(url, timeout=120) as r, open(dst + '.part', 'wb') as f:
            shutil.copyfileobj(r, f)
        os.replace(dst + '.part', dst)
    got = sha256(dst)
    if got != want:
        sys.exit('%s: SHA-256 %s, expected %s' % (dst, got, want))
    return dst


def extract(archive, dest):
    """The archive's single top folder becomes dest."""
    tmp = dest + '.x'
    if os.path.isdir(tmp):
        shutil.rmtree(tmp)
    with tarfile.open(archive) as t:
        t.extractall(tmp, filter='data')
    tops = os.listdir(tmp)
    if len(tops) != 1:
        sys.exit('%s: expected one top folder, found %s' % (archive, tops))
    if os.path.isdir(dest):
        shutil.rmtree(dest)
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    os.replace(os.path.join(tmp, tops[0]), dest)
    shutil.rmtree(tmp)


def is_mt(lib):
    with open(lib, 'rb') as f:
        return MT_MARK in f.read()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--out', default=os.path.join(REPO, 'deps', 'glslang-mt'))
    ap.add_argument('--work', default=os.path.join(REPO, 'build_glslang_mt'), help='scratch folder (sources, build tree)')
    ap.add_argument('--force', action='store_true', help='rebuild even when --out already holds /MT libraries')
    a = ap.parse_args()
    out, work = os.path.abspath(a.out), os.path.abspath(a.work)
    if len(work) > 80:      # SPIRV-Tools' deepest object paths are ~170 characters below the build folder (MAX_PATH 260)
        sys.exit('--work %s is too long a path (%d characters, at most 80): the build would fail' % (work, len(work)))
    if not a.force and all(os.path.isfile(os.path.join(out, 'lib', l)) for l in LIBS) and is_mt(os.path.join(out, 'lib', 'glslang.lib')):
        say('%s already holds the /MT glslang libraries (--force to rebuild)' % out)
        return 0
    os.makedirs(work, exist_ok=True)
    src = os.path.join(work, 'glslang-' + GLSLANG_TAG)
    for name, url, want, sub in SOURCES:
        arc = fetch(work, name, url, want)
        extract(arc, os.path.join(src, sub) if sub else src)
        say('  %s: %s' % (name, want[:16]))
    vs = vs_dir()
    cm_dir = os.path.join(vs, 'Common7', 'IDE', 'CommonExtensions', 'Microsoft', 'CMake')
    bld = os.path.join(work, 'build')
    if os.path.isdir(bld):
        shutil.rmtree(bld)
    stage = os.path.join(work, 'install')
    if os.path.isdir(stage):
        shutil.rmtree(stage)
    cmake_args = ['-G', 'Ninja', '-S', src, '-B', bld, '-DCMAKE_BUILD_TYPE=Release',
                  '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW', '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded',
                  '-DCMAKE_INSTALL_PREFIX=' + stage, '-DBUILD_SHARED_LIBS=OFF', '-DENABLE_OPT=ON',
                  '-DGLSLANG_TESTS=OFF', '-DENABLE_GLSLANG_BINARIES=OFF',
                  '-DSPIRV_SKIP_TESTS=ON', '-DSPIRV_SKIP_EXECUTABLES=ON', '-DSPIRV_WERROR=OFF', '-DGLSLANG_ENABLE_INSTALL=ON']
    bat = os.path.join(work, 'build.bat')
    q = lambda s: '"%s"' % s
    with open(bat, 'w', newline='\r\n') as f:
        f.write('@echo off\nsetlocal\n')
        f.write('set "PATH=%s;%%PATH%%"\n' % os.path.dirname(VSWHERE))       # vcvars64.bat calls vswhere
        f.write('call %s >nul\nif errorlevel 1 exit /b 1\n' % q(os.path.join(vs, 'VC', 'Auxiliary', 'Build', 'vcvars64.bat')))
        f.write('set "PATH=%s;%s;%%PATH%%"\n' % (os.path.join(cm_dir, 'CMake', 'bin'), os.path.join(cm_dir, 'Ninja')))
        f.write('cmake %s\nif errorlevel 1 exit /b 1\n' % ' '.join(q(x) for x in cmake_args))
        f.write('cmake --build %s --config Release\nif errorlevel 1 exit /b 1\n' % q(bld))
        f.write('cmake --install %s --config Release\nexit /b %%errorlevel%%\n' % q(bld))
    say('building (CMake + Ninja, /MT) in %s ...' % bld)
    r = subprocess.run([os.environ.get('COMSPEC', 'cmd.exe'), '/c', bat], capture_output=True, text=True, errors='replace')
    if r.returncode:
        sys.exit('the build failed (exit %d):\n%s' % (r.returncode, (r.stdout + r.stderr)[-4000:]))
    for l in LIBS:
        p = os.path.join(stage, 'lib', l)
        if not os.path.isfile(p):
            sys.exit('%s was not produced' % p)
        if not is_mt(p):
            sys.exit('%s is not built with the static C runtime' % p)
    # the output folder: include\ and lib\ as in the release zip, the licences, where it comes from
    if os.path.isdir(out):
        shutil.rmtree(out)
    shutil.copytree(os.path.join(stage, 'include'), os.path.join(out, 'include'))
    shutil.copytree(os.path.join(stage, 'lib'), os.path.join(out, 'lib'), ignore=shutil.ignore_patterns('cmake', 'pkgconfig'))
    with open(os.path.join(out, 'LICENSE.txt'), 'w', newline='\r\n', encoding='utf-8') as f:
        f.write('ssbu_glslang.dll links glslang %s and SPIRV-Tools, built from their sources with the static C runtime.\n\n' % GLSLANG_TAG)
        for name, rel in (('glslang', 'LICENSE.txt'), ('SPIRV-Tools', os.path.join('External', 'spirv-tools', 'LICENSE')),
                          ('SPIRV-Headers', os.path.join('External', 'spirv-tools', 'external', 'spirv-headers', 'LICENSE'))):
            f.write('=' * 78 + '\n%s\n' % name + '=' * 78 + '\n')
            with open(os.path.join(src, rel), encoding='utf-8', errors='replace') as g:
                f.write(g.read().replace('\r\n', '\n').rstrip() + '\n\n')
    with open(os.path.join(out, 'SOURCE.txt'), 'w', newline='\r\n') as f:
        f.write('Built by scripts/build_glslang_static.py (CMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded) from:\n')
        for name, url, want, _ in SOURCES:
            f.write('  %s  %s  sha256 %s\n' % (name, url, want))
    say('%s: %s, all /MT' % (out, ', '.join(LIBS)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
