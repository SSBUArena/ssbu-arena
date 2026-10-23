"""The translation units one generated C file is compiled as (build_opt.py; speedtest of 2026-10-23, scenario D).

    prepare(src, out_dir, split_lines, cold=None, own_decls=True) -> [(part source, kind)]
        kind 'opt': compiled the normal way (build_opt.compile_file_inline / compile_file: -O1, the fallback ladder)
        kind 'big': compiled once at -O0 (build_opt.compile_big)
    Without any function to move and with own_decls False, [(src, 'opt')]: the generated file itself, as before.

1. Split. Functions longer than `split_lines` statements go to <name>_b.c, compiled once at -O0 (the runtime helpers as
   calls, never force-inlined there: see build_opt.compile_big). Before, they got `optnone` inside the -O1 file, but clang 9's legacy pass manager
   still runs LoopSimplify / LoopInfo / LCSSA on optnone functions, ~8 times per file: that, not the optimisation, made
   these files run into the 180 s limit three times and then fall to WHOLE FILE -O0 (c_03d9.c: 565 s -> 12-14 s split).
   Their code is of the same kind as before (optnone = -O0 code generation); the rest of the file now stays at -O1.
   The limit is fixed, so the split no longer depends on how busy the PC was (the ladder's timeouts did).
2. Cold code (cold(name) True: a function the coverage campaign never saw executed in competitive play, build_opt.py
   COVERAGE) goes to <name>_b.c too, whatever its size. Nothing is removed: the function is in the DLL, correct, only
   compiled without optimisation. Executed functions keep exactly today's compile.
3. Declarations. `#include "funcs.h"` (main: 113,931 declarations, 3.2 MB, ~0.6-1.3 s of parsing per file) is replaced
   by the declarations the part uses (own_decls; the script modules keep their small funcs.h, which also declares their
   module struct). Verified on c_0182.c: identical disassembly and symbol table.

The generated files are not touched (other tools read them and funcs.h); the parts are written to out_dir, and only when
their text changed (incremental builds keep their dates). The generated files hold the two #includes, then functions:
multi-line `void f_/u_<addr>(Ctx *c[, uint64_t rp]) {` ... `}` and one-line ones (`void f_X(Ctx *c) { u_X(c, 0); }`), no
statics and no data (checked on every main and module file, 2026-10-23); every call goes through a declaration, so moving a
function to the other unit changes nothing but which object holds it. One-line functions stay in the 'opt' part, so each
function is defined exactly once. A file of another shape (no `#include "funcs.h"` line, e.g. imports.c) is compiled whole.
"""
import os
import re
import threading

# bump when the way parts are made changes: it is part of build_opt's hint key and split stamp
VERSION = 2
DEF = re.compile(r'^(?:static )?void (\w+)\((Ctx \*c(?:, uint64_t rp)?)\) \{$')
# the lifted functions' names (nearly all of funcs.h: f_ / u_<10 hex digits>) are found by their address part (a pattern
# that starts with a literal is scanned ~5x faster than an identifier pattern; an address that is no call only adds an
# unused declaration), the few other names of funcs.h (nro_catch_call) by a plain text search
LIFTED = re.compile(r'[fu]_[0-9a-f]{10}')
ADDR = re.compile(r'_([0-9a-f]{10})')
INCLUDE = '#include "funcs.h"'
_decls = {}
_lock = threading.Lock()


def decls(gen_dir):
    """{name: [declaration lines]} from <gen_dir>/funcs.h, read once per folder."""
    with _lock:
        if gen_dir not in _decls:
            d = {}
            with open(os.path.join(gen_dir, 'funcs.h'), encoding='latin-1') as f:
                for line in f:
                    m = re.search(r'\b(\w+)\(Ctx', line)
                    if m:
                        d.setdefault(m.group(1), []).append(line)
            d[None] = [n for n in d if not LIFTED.fullmatch(n)]      # the names LIFTED does not find
            _decls[gen_dir] = d
        return _decls[gen_dir]


def _write_if_changed(path, text):
    try:
        with open(path, encoding='latin-1', newline='') as f:
            if f.read() == text:
                return
    except OSError:
        pass
    with open(path, 'w', encoding='latin-1', newline='\n') as f:
        f.write(text)


def prepare(src, out_dir, split_lines, cold=None, own_decls=True, stats=None):
    """stats (a dict, optional): 'long' / 'cold' = [functions, statements] moved to the -O0 part for each reason."""
    with open(src, encoding='latin-1') as f:
        lines = f.read().split('\n')
    base = os.path.join(out_dir, os.path.basename(src)[:-2])
    try:
        cut = lines.index(INCLUDE) + 1
    except ValueError:
        cut = None
    if cut is None or (not split_lines and cold is None and not own_decls):
        for stale in (base + '.c', base + '_b.c'):
            if os.path.exists(stale):
                os.remove(stale)
        return [(src, 'opt')]
    # the head is everything up to `#include "funcs.h"` and nothing more: some files define one-line functions before their
    # first multi-line one; those must stay in exactly one part (a copy in both = duplicate symbols)
    head, rest = lines[:cut], lines[cut:]
    small, big, cur, name = [], [], None, None
    for line in rest:
        if cur is None:
            m = DEF.match(line)
            if m:
                cur, name = [line], m.group(1)
            else:
                small.append(line)                      # blank lines, one-line definitions
        else:
            cur.append(line)
            if line.startswith('}'):
                n = len(cur) - 2
                why = 'long' if split_lines and n > split_lines else 'cold' if cold is not None and cold(name) else None
                (big if why else small).extend(cur)
                if why and stats is not None:
                    s = stats.setdefault(why, [0, 0])
                    s[0] += 1
                    s[1] += n
                cur = None
    if cur is not None:                                 # not the shape this expects: the file is compiled whole
        return [(src, 'opt')]
    if not big and not own_decls:
        for stale in (base + '.c', base + '_b.c'):
            if os.path.exists(stale):
                os.remove(stale)
        return [(src, 'opt')]
    os.makedirs(out_dir, exist_ok=True)
    d = decls(os.path.dirname(src)) if own_decls else None
    out = []
    for part, body, kind in (('', small, 'opt'), ('_b', big, 'big')):
        path = base + part + '.c'
        # the 'opt' part is always made, even empty (a file whose functions are all cold): its object is the one
        # build.py's dependency check looks for
        if kind == 'big' and not big:
            if os.path.exists(path):
                os.remove(path)
            continue
        text = '\n'.join(head)
        if own_decls:
            btext = '\n'.join(body)
            used = set()
            for a in set(ADDR.findall(btext)):
                used.update(('f_' + a, 'u_' + a))
            used.update(n for n in d[None] if n in btext)
            need = ''.join(x for n in sorted(used) if n in d for x in d[n])
            text = text.replace(INCLUDE, '/* the declarations of funcs.h this part uses (tu_prepare.py) */\n' + need, 1)
        _write_if_changed(path, text + '\n' + '\n'.join(body) + '\n')
        out.append((path, kind))
    return out
