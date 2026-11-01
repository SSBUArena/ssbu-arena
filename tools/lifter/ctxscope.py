#!/usr/bin/env python3
"""Alias information for lifted functions (rollback 8 ms). In a lifted function the first argument is Ctx *c, the guest registers;
guest code never holds a pointer to a Ctx, so a guest load or store (an address made with inttoptr) or a page mark cannot touch it.
LLVM cannot see that: c is passed to calls, so it counts as captured, and every guest store forces every guest register to be
read again from memory. Here: on the textual IR after the mandatory inlining, a load / store through c gets !alias.scope and one
through an address made with inttoptr, a pointer loaded from a global (the mark maps) or a global gets !noalias. Only what is known gets
a tag: a phi or select of pointers (a store the compiler sank) gets none. Calls (which may read and write c) stay barriers.
    annotate(text) -> text;  python ctxscope.py in.ll out.ll"""
import re, sys

DEF = re.compile(r'^define .*@(?:[fu]_[0-9a-f]+|s_[0-9a-f]+)\(ptr (?:noundef )?(?:nonnull )?(?:[a-z]+ )*%(\w+)')
DERIV = re.compile(r'%([\w.]+) = (?:getelementptr|bitcast)\b')
MEM = re.compile(r'(?:%[\w.]+ = )?(?:load |store |atomicrmw |cmpxchg )')
MEMCALL = re.compile(r'call void @llvm\.mem(?:cpy|move|set)')
INT2P = re.compile(r'%([\w.]+) = inttoptr ')
POPER = re.compile(r'\bptr (?:[a-z_]+ )*([%@][\w.]+)')
LOADPTR = re.compile(r'%([\w.]+) = load ptr, ')


def annotate(text):
    out, based, guest, infn = [], set(), set(), False
    for line in text.split('\n'):
        if line.startswith('define '):
            m = DEF.match(line)
            infn, based, guest = bool(m), {m.group(1)} if m else set(), set()
        elif line.startswith('}'):
            infn = False
        elif infn and line.startswith('  '):
            s = line[2:]
            m = DERIV.match(s)
            if m:
                ptrs = POPER.findall(s)
                if ptrs and ptrs[0][1:] in based:
                    based.add(m.group(1))
                elif ptrs and (ptrs[0][1:] in guest or ptrs[0][0] == '@'):
                    guest.add(m.group(1))
            elif INT2P.match(s):
                guest.add(INT2P.match(s).group(1))
            elif MEM.match(s):
                ptrs = POPER.findall(s)
                if ptrs and 'volatile' not in s and '!alias.scope' not in s and '!noalias' not in s:
                    p = ptrs[-1]
                    if p[1:] in based:
                        if not (s.startswith('store ') and len(ptrs) > 1 and ptrs[0][1:] in based):      # (a store of c itself: never in lifted code)
                            line += ', !alias.scope !9001'
                    elif p[1:] in guest or p[0] == '@':
                        line += ', !noalias !9001'
                        lm = LOADPTR.match(s)
                        if lm and p[0] == '@':
                            guest.add(lm.group(1))
            elif MEMCALL.search(s) and 'volatile' not in s.split('(')[0]:
                ptrs = POPER.findall(s)
                if ptrs and all(q[1:] in guest for q in ptrs if q[0] == '%') and '!noalias' not in s:
                    line += ', !noalias !9001'
        out.append(line)
    out.append('!9002 = distinct !{!9002, !"ctx domain"}')
    out.append('!9003 = distinct !{!9003, !9002, !"ctx"}')
    out.append('!9001 = !{!9003}')
    return '\n'.join(out)


if __name__ == '__main__':
    t = open(sys.argv[1], encoding='latin-1', newline='').read()
    open(sys.argv[2], 'w', encoding='latin-1', newline='').write(annotate(t))
