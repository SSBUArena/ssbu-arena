#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""The three copies of SSBU Arena's region table say the same thing: runtime/net_region.h (the game),
tools/launcher/arena/Regions.cs (the launcher) and tools/launcher/Launcher.ps1 (the PowerShell window): the regions in
the same order (id, name, host), the port and the numbers of the measure; hosts are names, never addresses.
    python tools/net/test_regions.py            (no game, no network)
    python tools/net/test_regions.py --live     also measures each lobby once, as the launcher does (needs the network)"""
import os
import re
import socket
import struct
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def read(rel):
    with open(os.path.join(ROOT, rel), encoding='utf-8-sig') as f:
        return f.read()


def table(text, start, row, consts):
    """the rows after the 'REGION TABLE' mark, with the constants' names replaced by their values"""
    body = text[text.index('REGION TABLE'):]
    body = body[body.index(chr(10)):]                   # (the rest of the mark's own line is a comment)
    body = body[:body.index(start)]
    rows = []
    for m in re.finditer(row, body):
        rows.append(tuple(consts.get(x, x.strip('"\'')) for x in m.groups()))
    return rows


def number(text, pattern):
    m = re.search(pattern, text)
    assert m, pattern
    return int(m.group(1))


def load():
    c = read('runtime/net_region.h')
    cs = read('tools/launcher/arena/Regions.cs')
    ps = read('tools/launcher/Launcher.ps1')
    out = {}
    na = re.search(r'#define NA_EAST_HOST "([^"]+)"', c).group(1)
    out['C'] = dict(
        rows=table(c, '};', r'\{\s*("[^"]*"|\w+)\s*,\s*("[^"]*"|\w+)\s*,\s*("[^"]*"|\w+)\s*\}', {'NA_EAST_HOST': na}),
        port=number(c, r'#define NET_REGION_PORT (\d+)'), keep=number(c, r'#define NET_REGION_KEEP_MS (\d+)'),
        samples=number(c, r'#define NET_REGION_SAMPLES (\d+)'), gap=number(c, r'#define NET_REGION_GAP_MS (\d+)'),
        timeout=number(c, r'#define NET_REGION_TIMEOUT_MS (\d+)'))
    na = re.search(r'const string NA_EAST_HOST = "([^"]+)"', cs).group(1)
    out['C#'] = dict(
        rows=table(cs, '};', r'\(\s*("[^"]*"|\w+)\s*,\s*("[^"]*"|\w+)\s*,\s*("[^"]*"|\w+)\s*\)', {'NA_EAST_HOST': na}),
        port=number(cs, r'const int Port = (\d+)'), keep=number(cs, r'const int KeepMs = (\d+)'),
        samples=number(cs, r'const int Samples = (\d+)'), gap=number(cs, r'const int GapMs = (\d+)'),
        timeout=number(cs, r'const int TimeoutMs = (\d+)'))
    na = re.search(r"\$NA_EAST_HOST = '([^']+)'", ps).group(1)
    out['PowerShell'] = dict(
        rows=table(ps, '\n)', r"@\{\s*id = ('[^']*'|\$\w+);\s*name = ('[^']*'|\$\w+);\s*host = ('[^']*'|\$\w+)\s*\}", {'$NA_EAST_HOST': na}),
        port=number(ps, r'\$RegionPort = (\d+)'), keep=number(ps, r'\$RegionKeepMs = (\d+)'),
        samples=number(ps, r'\$RegionSamples = (\d+)'), gap=number(ps, r'\$RegionGapMs = (\d+)'),
        timeout=number(ps, r'\$RegionTimeoutMs = (\d+)'))
    return out


def measure(rows, port, samples, gap, timeout):
    """one measure, as net_relay.c net_region_measure: a JOIN without a pass, refused with a REJECT carrying its nonce"""
    socks, sent, rtt = [], [], []
    for _, _, host in rows:
        try:
            s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            s.connect((socket.gethostbyname(host), port))
            s.setblocking(False)
        except OSError:
            s = None
        socks.append(s); sent.append({}); rtt.append([])
    t0, nxt = time.perf_counter(), 0
    while (time.perf_counter() - t0) * 1000 <= timeout and any(len(r) < samples for r, s in zip(rtt, socks) if s):
        if nxt < samples and (time.perf_counter() - t0) * 1000 >= nxt * gap:
            for i, s in enumerate(socks):
                if not s:
                    continue
                nonce = os.urandom(8)
                p = b'SU\x01\x01' + nonce + b'region-probe'.ljust(32, b'\0') * 2 + b'ssbu-recomp-ldn1'.ljust(40, b'\0') + bytes(7) + b'CV' + struct.pack('>H', 3)
                sent[i][nonce] = time.perf_counter()
                s.send(p)
            nxt += 1
        time.sleep(0.002)
        for i, s in enumerate(socks):
            if not s:
                continue
            try:
                b = s.recv(256)
            except OSError:
                continue
            if len(b) >= 12 and b[:3] == b'SU\x01' and b[3] in (2, 3) and b[4:12] in sent[i]:
                rtt[i].append((time.perf_counter() - sent[i].pop(b[4:12])) * 1000)
                if b[3] == 2 and len(b) >= 24:
                    s.send(b'SU\x01\x07' + b[16:24])
    for s in socks:
        if s:
            s.close()
    return [int(sorted(r)[(len(r) - 1) // 2] + 0.5) if r else -1 for r in rtt]


def main():
    t = load()
    ref = t['C']
    assert len(ref['rows']) >= 2, ref['rows']
    for name, d in t.items():
        assert d == ref, '%s differs from C:\n  %r\n  %r' % (name, d, ref)
    ids = [r[0] for r in ref['rows']]
    assert len(set(ids)) == len(ids) and 'auto' not in ids, ids
    for rid, rname, host in ref['rows']:
        assert re.fullmatch(r'[a-z0-9-]+', rid) and rname, (rid, rname)
        assert re.fullmatch(r'[a-z0-9-]+(\.[a-z0-9-]+)+', host) and not re.fullmatch(r'[0-9.]+', host), 'a name, not an address: ' + host
    for rel in ('runtime/net_region.h', 'tools/launcher/arena/Regions.cs'):
        assert not re.search(r'\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b', read(rel)), 'an address in ' + rel
    print('region tables: ok (%s; port %d, %d samples, keep %d ms)' % (', '.join('%s = %s' % (r[0], r[2]) for r in ref['rows']), ref['port'], ref['samples'], ref['keep']))
    if '--live' in sys.argv:
        ms = measure(ref['rows'], ref['port'], ref['samples'], ref['gap'], ref['timeout'])
        print('live: ' + ', '.join('%s %s' % (r[1], '%d ms' % m if m >= 0 else 'no answer') for r, m in zip(ref['rows'], ms)))
    return 0


if __name__ == '__main__':
    sys.exit(main())
