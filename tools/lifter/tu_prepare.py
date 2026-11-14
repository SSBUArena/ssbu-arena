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
DEF = re.compile(r'^(?:static )?void (\w+)\((Ctx \*c(?:, uint64_t (?:rp|jt0))?)\) \{$')
# the lifted functions' names (nearly all of funcs.h: f_ / u_<10 hex digits>) are found by their address part (a pattern
# that starts with a literal is scanned ~5x faster than an identifier pattern; an address that is no call only adds an
# unused declaration), the few other names of funcs.h (nro_catch_call) by a plain text search
LIFTED = re.compile(r'[fus]_[0-9a-f]{10}')
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


# (rb-8ms 2026-11-12) RECOMP_NF_FNS=<addr>,<addr>..: these functions (plain `void f_<addr>(Ctx *c) {`, in the optimised part) get
# a copy f_<addr>ff (a hex suffix: ctxscope.py's alias information needs [fu]_<hex>) with the vector forms of neon_fast.h (v_tbl / v_f3v / v_f3e -> nf_v_*), entered at their start when
# r1_fastneon is set (SSBU_FASTNEON). Only for builds with -DR1_GAMEDLL (neon.h includes neon_fast.h there).
NF_FNS = {'f_' + a.strip().lower() for a in (os.environ.get('RECOMP_NF_FNS') or '').split(',') if a.strip()}
NF_SUB = re.compile(r'\bv_(tbl|f3v|f3e)\(')


def nf_copy(name, cur):
    """the function's lines -> its vector copy (static) followed by the function with the switch at its start"""
    body = cur[1:]
    copy = ['static void %sff(Ctx *c) {' % name] + [NF_SUB.sub(r'nf_v_\1(', l) for l in body]
    return copy + [cur[0], '  if (r1_fastneon) { %sff(c); return; }' % name] + body


# (rb-8ms 2026-11-14, TEST ONLY) RECOMP_RC_FNS=<addr>,..: these functions get their body moved to a copy f_<addr>fc and an entry
# that tells the runtime when they start and end (r1_rc_ent, game_shim_r1.c; -DR1_RDCOUNT builds): rc_test.c counts and hashes the
# guest reads / writes made inside them on main (runtime.h RC_ACC), real against re-run passes.
RC_FNS = {'f_' + a.strip().lower() for a in (os.environ.get('RECOMP_RC_FNS') or '').split(',') if a.strip()}


def rc_copy(name, cur):
    body = cur[1:]
    return (['static void %sfc(Ctx *c) {' % name] + body +
            [cur[0], '  if (r1_rc_ent) { r1_rc_ent(c, 0x%sULL, 0); %sfc(c); r1_rc_ent(c, 0x%sULL, 1); return; }' % (name[2:], name, name[2:]),
             '  %sfc(c);' % name, '}'])


# (rb-8ms 2026-11-14) RECOMP_MEMO_FNS=<addr>,..: SSBU_RB_MEMO (-DR1_MEMO builds, runtime memo.c). Each of these functions gets its body
# moved to a copy f_<addr>fm and an entry that asks the runtime first (r1_mo_ent, game_shim_r1.c): 1 = the call was replayed from an
# exact record (its stores made, its registers set): return at once; else the body runs and the runtime hears its end.
MEMO_FNS = {'f_' + a.strip().lower() for a in (os.environ.get('RECOMP_MEMO_FNS') or '').split(',') if a.strip()}


def mo_copy(name, cur):
    body = cur[1:]
    return (['static void %sfm(Ctx *c) {' % name] + body +
            [cur[0], '  if (r1_mo_ent) { if (r1_mo_ent(c, 0x%sULL, 0)) return; %sfm(c); r1_mo_ent(c, 0x%sULL, 1); return; }' % (name[2:], name, name[2:]),
             '  %sfm(c);' % name, '}'])


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
                m1 = re.match(r'void (f_[0-9a-f]+)\(Ctx \*c\) \{ (.*) \}$', line) if RC_FNS else None
                if m1 and m1.group(1) in RC_FNS:        # (RC_FNS) a one-line entry (EH builds: `{ u_<addr>(c, 0); }`)
                    small.extend(rc_copy(m1.group(1), ['void %s(Ctx *c) {' % m1.group(1), '  ' + m1.group(2), '}']))
                    continue
                m1 = re.match(r'void (f_[0-9a-f]+)\(Ctx \*c\) \{ (.*) \}$', line) if MEMO_FNS else None
                if m1 and m1.group(1) in MEMO_FNS:      # (MEMO_FNS) the same for the memo entries
                    small.extend(mo_copy(m1.group(1), ['void %s(Ctx *c) {' % m1.group(1), '  ' + m1.group(2), '}']))
                    continue
                small.append(line)                      # blank lines, one-line definitions
        else:
            cur.append(line)
            if line.startswith('}'):
                n = len(cur) - 2
                why = 'long' if split_lines and n > split_lines else 'cold' if cold is not None and cold(name) else None
                if not why and name in NF_FNS and cur[0] == 'void %s(Ctx *c) {' % name:
                    cur = nf_copy(name, cur)
                if not why and name in RC_FNS and cur[0] == 'void %s(Ctx *c) {' % name:
                    cur = rc_copy(name, cur)
                if not why and name in MEMO_FNS and cur[0] == 'void %s(Ctx *c) {' % name:
                    cur = mo_copy(name, cur)
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
                used.update(('f_' + a, 'u_' + a, 's_' + a))
            used.update(n for n in d[None] if n in btext)
            need = ''.join(x for n in sorted(used) if n in d for x in d[n])
            text = text.replace(INCLUDE, '/* the declarations of funcs.h this part uses (tu_prepare.py) */\n' + need, 1)
        _write_if_changed(path, text + '\n' + '\n'.join(body) + '\n')
        out.append((path, kind))
    return out
