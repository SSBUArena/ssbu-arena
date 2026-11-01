#!/usr/bin/env python3
"""Only the condition flags that are read get computed (rollback 8 ms).

    python genflags.py <gen dir in> <gen dir out>     (or: process(text) -> text, stats)

The lifter's adds32 / adds64 / logic32 / logic64 compute and store N, Z, C and V at every flag-setting instruction, and nearly always one or two of
them are read (a CMP before a B.EQ needs Z). Liveness per lifted function, backwards over its statements:
  - a call (BL / BLR: `c->x[30] = ...`) and a return: no flag is live before them (AAPCS: the flags do not survive a call and are no
    arguments; compiled code cannot rely on them); a jump out of the function (tail call, fall through into the next function, a BR):
    every flag live, conservatively;
  - a statement that sets the flags unconditionally ends their liveness; `cond(c, n)` and any other mention of c->N .. V reads.
Rewritten: adds64(c, ...) -> adds64f(M, c, ...) with M the live flags after it (runtime.h: a dead flag is not computed). Nothing else changes."""
import os, re, sys

N, Z, C, V, ALL = 8, 4, 2, 1, 15
COND = {0: Z, 1: C, 2: N, 3: V, 4: C | Z, 5: N | V, 6: N | V | Z, 7: 0}
FN = re.compile(r'^void ([fus]_[0-9a-f]+)\(Ctx \*c(?:, uint64_t \w+)?\) \{$')
GOTO = re.compile(r'goto L_([0-9a-f]+);')
CONDC = re.compile(r'\bcond\(c, (\d+)\)')
FIELD = re.compile(r'\bc->([NZCV])\b')
WRITER = re.compile(r'\b(adds64|adds32|logic64|logic32)\(c,')
ANYW = re.compile(r'\b(adds64|adds32|logic64|logic32|set_nzcv|fcmp)\(c\b')
BL = re.compile(r'c->x\[30\] = ')
MASKN = {N: 'N', Z: 'Z', C: 'C', V: 'V'}


def stmt_reads(s):
    m = 0
    for x in CONDC.findall(s):
        m |= COND[int(x) >> 1]
    for x in FIELD.findall(s):
        m |= {'N': N, 'Z': Z, 'C': C, 'V': V}[x]
    if 'nzcv' in s.replace('set_nzcv', ''):
        m |= ALL
    return m


def uncond_writer(s):
    """the whole statement always sets all four flags (no `?`, no `if`)"""
    return ANYW.search(s) and 'if (' not in s and '?' not in s


def process_fn(body, stats):
    """body: the lines of one function; returns them rewritten"""
    idx, stm = {}, []
    for k, l in enumerate(body):
        if l.startswith('L_') and not l.startswith('L_dispatch'):
            c = l.index(':'); idx[l[2:c]] = len(stm); stm.append((k, l[c + 1:].strip()))
    if not stm:
        return body
    n = len(stm)
    # the line after the last statement: what the lifter wrote there
    tail = [l.strip() for l in body[stm[-1][0] + 1:]]
    end_live = 0 if tail and tail[0] == 'return;' else ALL
    succ, gen, kill, stop = [None] * n, [0] * n, [0] * n, [0] * n
    for i, (k, s) in enumerate(stm):
        gen[i] = stmt_reads(s)
        t = [idx[x] for x in GOTO.findall(s) if x in idx]
        wr = uncond_writer(s)
        if 'goto L_dispatch' in s:                       # BR: anywhere, or a tail call
            gen[i] = ALL; stop[i] = 1; t = []
        elif BL.search(s):                               # BL / BLR: nothing live before it
            gen[i] = 0; kill[i] = ALL; t.append(i + 1)
        elif s.startswith('return;') or s == 'return':
            stop[i] = 1; t = []
        elif s.startswith('goto ') or s.startswith('{ f_') or s.startswith('{ imp_') or s.startswith('{ h_') or ('return;' in s and not s.startswith('if')):
            if not t: gen[i] = ALL                       # a jump out of the function
            stop[i] = 1
        else:
            t.append(i + 1)
        if wr: kill[i] = ALL
        if 'goto' in s and not all(x in idx or x == 'dispatch' for x in re.findall(r'goto L_(\w+);', s)):
            gen[i] = ALL
        succ[i] = t
    live_in = [0] * (n + 1)
    live_in[n] = end_live
    changed = True
    while changed:
        changed = False
        for i in range(n - 1, -1, -1):
            out = 0
            for t in succ[i]:
                out |= live_in[t]
            v = gen[i] | (out & ~kill[i] & ALL)
            if v != live_in[i]:
                live_in[i] = v; changed = True
    out = list(body)
    for i, (k, s) in enumerate(stm):
        m = WRITER.search(s)
        stats['all'] += 1 if m else 0
        if not m or not uncond_writer(s):
            continue
        o = 0
        for t in succ[i]:
            o |= live_in[t]
        if stop[i]:
            o = ALL
        if o == ALL:
            stats['full'] += 1
            continue
        stats['masked'] += 1
        stats['dead'] += 1 if o == 0 else 0
        out[k] = WRITER.sub(lambda g: '%sf(%d, c,' % (g.group(1), o), body[k], count=1)
    return out


def process(text, stats):
    lines = text.split('\n'); out = []; i = 0
    while i < len(lines):
        if FN.match(lines[i]):
            e = i
            while lines[e] != '}': e += 1
            out.extend(process_fn(lines[i:e + 1], stats))
            i = e + 1
        else:
            out.append(lines[i]); i += 1
    return '\n'.join(out)


def main():
    gi, go = sys.argv[1:3]
    os.makedirs(go, exist_ok=True)
    stats = dict(all=0, full=0, masked=0, dead=0)
    for n in sorted(os.listdir(gi)):
        p = os.path.join(gi, n)
        if n.startswith('c_') and n.endswith('.c'):
            t = open(p, encoding='latin-1', newline='').read().replace('\r\n', '\n')
            open(os.path.join(go, n), 'w', encoding='latin-1', newline='').write(process(t, stats))
        elif os.path.isfile(p) and not n.endswith('.obj'):
            open(os.path.join(go, n), 'wb').write(open(p, 'rb').read())
    print(stats)


if __name__ == '__main__':
    main()
