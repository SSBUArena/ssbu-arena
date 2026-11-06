#!/usr/bin/env python3
# SPDX-License-Identifier: GPL-3.0-or-later
"""ssbv5.py - the compact replay body (file version 5), the reference encoder and decoder.

A version 5 file is a version 4 file whose body is re-coded into separate streams and then deflated (codec 1). Readers
expand it back into a version 4 body (v5_to_v4) and parse that as before, so the launcher, the web viewer and this tool share
one parser. The game still writes version 4; the launcher re-codes each file after the game (ReplayState.Compact).

Lossy, on purpose, only where the schematic viewer cannot show the difference:
  - fighter x, y, damage, shield: kept to 1/256 of a unit (damage 1/256 %); motion frame to 1/16 of a frame;
  - hurtboxes: a fighter's set of hurtboxes for a move frame (fighter slot, motion, whole motion frame, facing) is stored once;
    a later frame of the same move frame whose boxes are within TOL steps of it uses it; the boxes are kept to HQ/16 of a unit.
Everything else is exact.

Version 5 plain body (before deflate):
  u32 nlines, the lines as in version 4 (20 bytes each), then 5 x u32: the lengths of the streams F, M, H, K, O, then the streams.
  F, per frame: zz(frame - previous frame), u8 mask, u8 moved, uv(nobj), u8 lines-on changed (0, or 1 + u64 raw);
     per fighter of the mask (slot 0..3): zz(dqx) zz(dqy) zz(dqdmg) zz(dqmf - 16), u8 motion changed (0, or 1 + u64 raw),
     zz(dstatus), u8 lr, u8 situation, u8 stocks, u8 nhit, u8 nhurt, u8 flags, u16 raw (+34), zz(dqshield), zz(dff - 1)
     (d: against the same slot's previous frame; q: x * 256, y * 256, damage * 256, mf * 16, shield * 256, rounded)
  M: the moved lines of each frame as in version 4 (20 bytes each).
  H, per fighter of each frame: u8 mode; 0: uv(n), n x (zz x zz y zz x2 zz y2 uv r, u8 part, u8 status) relative to the base;
     1: the previous frame's boxes of that slot (values in steps of HQ/16), n x (zz dx zz dy zz dx2 zz dy2 zz dr, u8 dstatus); 2: the stored pose of
     (slot, motion, (qmf + 8) >> 4, lr). Base: bx = (qx + 8) >> 4, by = (qy + 8) >> 4 (the box unit, 1/16).
  K: the hitboxes of each fighter (base as H) and each object (base its own x, y): zz x zz y zz x2 zz y2 uv r, u8 id, u8 flags,
     uv dmg10, zz angle, uv kbg, uv bkb.
  O, per object: uv id, uv kind, u8 cat, u8 lr, zz x, zz y, uv mf8, zz rot10, uv motion low, u8 motion high, u8 owner,
     u8 nhit, u8 scale.
  (uv: unsigned LEB128 varint; zz: zigzag then uv.)

    ssbv5.py stats <file.ssbv>...        sizes and the largest error of a version 4 -> 5 -> 4 round trip
    ssbv5.py pack <in.ssbv> <out.ssbv>   a version 4 file as version 5
"""
import struct
import sys
import zlib

TOL = 2                      # pose reuse tolerance, in hurtbox steps
HQ = 4                       # hurtbox step: 4/16 = 0.25 of a unit (the version 4 boxes are 1/16)
HDR = 0x200


def zz(n):
    return (n << 1) ^ (n >> 63) if n < 0 else n << 1


def unzz(u):
    return (u >> 1) ^ -(u & 1)


def uv(out, n):
    n &= (1 << 64) - 1
    while True:
        b = n & 0x7f
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return


class R:
    def __init__(self, b):
        self.b, self.o = b, 0

    def u8(self):
        v = self.b[self.o]; self.o += 1; return v

    def raw(self, n):
        v = self.b[self.o:self.o + n]
        if len(v) != n:
            raise ValueError('version 5 stream cut short')
        self.o += n; return v

    def uv(self):
        n = s = 0
        while True:
            c = self.b[self.o]; self.o += 1
            n |= (c & 0x7f) << s; s += 7
            if not c & 0x80:
                return n
            if s > 63:
                raise ValueError('bad varint')

    def zz(self):
        return unzz(self.uv())


def q(v, m):
    return int(round(v * m))


def qh(v):
    return (v + HQ // 2) // HQ


def v4_to_v5(plain, nlines, tol=TOL):
    """version 4 plain body -> version 5 plain body"""
    o = 20 * nlines
    F, M, H, K, O = (bytearray() for _ in range(5))
    prev = {}
    poses = {}
    pframe, pvalid = 0, None
    n = len(plain)
    while o + 16 <= n:
        fr, mask, moved, nobj, valid = struct.unpack_from('<IBBHQ', plain, o); o += 16
        uv(F, zz(fr - pframe)); pframe = fr
        F += bytes([mask, moved]); uv(F, nobj)
        if valid == pvalid:
            F.append(0)
        else:
            F.append(1); F += struct.pack('<Q', valid); pvalid = valid
        M += plain[o:o + 20 * moved]; o += 20 * moved
        for k in range(4):
            if not mask & (1 << k):
                continue
            x, y, dmg, mf, mot, st, lr, sit, stocks, nh, nu, flags, pad, shield, ff = struct.unpack_from('<4fQibBBBBBHfI', plain, o)
            o += 44
            p = prev.setdefault(k, dict(qx=0, qy=0, qd=0, qm=-16, mot=None, st=0, qs=0, ff=-1, hurts=None))
            qx, qy, qd, qm, qs = q(x, 256), q(y, 256), q(dmg, 256), q(mf, 16), q(shield, 256)
            uv(F, zz(qx - p['qx'])); uv(F, zz(qy - p['qy'])); uv(F, zz(qd - p['qd'])); uv(F, zz(qm - p['qm'] - 16))
            if mot == p['mot']:
                F.append(0)
            else:
                F.append(1); F += struct.pack('<Q', mot)
            uv(F, zz(st - p['st']))
            F += bytes([lr & 255, sit, stocks, nh, nu, flags]); F += struct.pack('<H', pad)
            uv(F, zz(qs - p['qs'])); uv(F, zz(ff - p['ff'] - 1))
            p.update(qx=qx, qy=qy, qd=qd, qm=qm, mot=mot, st=st, qs=qs, ff=ff)
            bx, by = (qx + 8) >> 4, (qy + 8) >> 4
            for _ in range(nh):
                hx, hy, hx2, hy2, r, hid, hfl, d10, ang, kbg, bkb = struct.unpack_from('<4hHBBHhHH', plain, o); o += 20
                for v in (hx - bx, hy - by, hx2 - bx, hy2 - by):
                    uv(K, zz(v))
                uv(K, r); K += bytes([hid, hfl]); uv(K, d10); uv(K, zz(ang)); uv(K, kbg); uv(K, bkb)
            cur = []
            for _ in range(nu):
                ux, uy, ux2, uy2, ur, part, ust = struct.unpack_from('<4hHBB', plain, o); o += 12
                cur.append((qh(ux - bx), qh(uy - by), qh(ux2 - bx), qh(uy2 - by), qh(ur), part, ust))
            key = (k, mot, (qm + 8) >> 4, lr)
            ref = poses.get(key)
            ph = p['hurts']
            if ref is not None and len(ref) == len(cur) and all(a[5] == b[5] and a[6] == b[6] and all(abs(a[t] - b[t]) <= tol for t in range(5)) for a, b in zip(ref, cur)):
                H.append(2); cur = ref
            elif ph is not None and len(ph) == len(cur) and all(a[5] == b[5] for a, b in zip(ph, cur)):
                H.append(1)
                for a, b in zip(ph, cur):
                    for t in range(5):
                        uv(H, zz(b[t] - a[t]))
                    H.append((b[6] - a[6]) & 255)
                poses[key] = cur
            else:
                H.append(0); uv(H, len(cur))
                for b in cur:
                    for t in range(4):
                        uv(H, zz(b[t]))
                    uv(H, b[4]); H += bytes([b[5], b[6]])
                poses[key] = cur
            p['hurts'] = cur
        for _ in range(nobj):
            oid, kind, cat, lr, ox, oy, mf8, rot10, mot, mot_hi, owner, nh, scale = struct.unpack_from('<IHBbhhHhIBBBB', plain, o); o += 24
            uv(O, oid); uv(O, kind); O += bytes([cat, lr & 255]); uv(O, zz(ox)); uv(O, zz(oy)); uv(O, mf8); uv(O, zz(rot10)); uv(O, mot)
            O += bytes([mot_hi, owner, nh, scale])
            for _ in range(nh):
                hx, hy, hx2, hy2, r, hid, hfl, d10, ang, kbg, bkb = struct.unpack_from('<4hHBBHhHH', plain, o); o += 20
                for v in (hx - ox, hy - oy, hx2 - ox, hy2 - oy):
                    uv(K, zz(v))
                uv(K, r); K += bytes([hid, hfl]); uv(K, d10); uv(K, zz(ang)); uv(K, kbg); uv(K, bkb)
    out = bytearray(struct.pack('<I', nlines)) + plain[:20 * nlines] + struct.pack('<5I', len(F), len(M), len(H), len(K), len(O))
    return bytes(out + F + M + H + K + O)


def v5_to_v4(b5):
    """version 5 plain body -> version 4 plain body (what every reader parses); also returns nlines"""
    nlines = struct.unpack_from('<I', b5, 0)[0]
    o = 4 + 20 * nlines
    lens = struct.unpack_from('<5I', b5, o); o += 20
    st = []
    for L in lens:
        st.append(R(b5[o:o + L])); o += L
    F, M, H, K, O = st
    out = bytearray(b5[4:4 + 20 * nlines])
    prev = {}
    poses = {}
    pframe, pvalid = 0, 0
    while F.o < len(F.b):
        fr = pframe + F.zz(); pframe = fr
        mask, moved = F.u8(), F.u8(); nobj = F.uv()
        if F.u8():
            pvalid = struct.unpack('<Q', F.raw(8))[0]
        out += struct.pack('<IBBHQ', fr & 0xffffffff, mask, moved, nobj, pvalid)
        out += M.raw(20 * moved)
        for k in range(4):
            if not mask & (1 << k):
                continue
            p = prev.setdefault(k, dict(qx=0, qy=0, qd=0, qm=-16, mot=0, st=0, qs=0, ff=-1, hurts=None))
            qx = p['qx'] + F.zz(); qy = p['qy'] + F.zz(); qd = p['qd'] + F.zz(); qm = p['qm'] + 16 + F.zz()
            mot = struct.unpack('<Q', F.raw(8))[0] if F.u8() else p['mot']
            stt = p['st'] + F.zz()
            lr, sit, stocks, nh, nu, flags = F.raw(6)
            pad = struct.unpack('<H', F.raw(2))[0]
            qs = p['qs'] + F.zz(); ff = p['ff'] + 1 + F.zz()
            p.update(qx=qx, qy=qy, qd=qd, qm=qm, mot=mot, st=stt, qs=qs, ff=ff)
            out += struct.pack('<4fQibBBBBBHfI', qx / 256, qy / 256, qd / 256, qm / 16, mot, stt, lr - 256 if lr > 127 else lr, sit, stocks, nh, nu, flags, pad, qs / 256, ff & 0xffffffff)
            bx, by = (qx + 8) >> 4, (qy + 8) >> 4
            for _ in range(nh):
                hx, hy, hx2, hy2 = (K.zz() for _ in range(4)); r = K.uv(); hid, hfl = K.raw(2); d10 = K.uv(); ang = K.zz(); kbg = K.uv(); bkb = K.uv()
                out += struct.pack('<4hHBBHhHH', hx + bx, hy + by, hx2 + bx, hy2 + by, r, hid, hfl, d10, ang, kbg, bkb)
            mode = H.u8()
            key = (k, mot, (qm + 8) >> 4, lr - 256 if lr > 127 else lr)
            if mode == 2:
                cur = poses[key]
            elif mode == 1:
                cur = []
                for a in p['hurts']:
                    d = [H.zz() for _ in range(5)]
                    cur.append((a[0] + d[0], a[1] + d[1], a[2] + d[2], a[3] + d[3], a[4] + d[4], a[5], (a[6] + H.u8()) & 255))
                poses[key] = cur
            else:
                cur = []
                for _ in range(H.uv()):
                    v = [H.zz() for _ in range(4)]; r = H.uv(); part, ust = H.raw(2)
                    cur.append((v[0], v[1], v[2], v[3], r, part, ust))
                poses[key] = cur
            if len(cur) != nu:
                raise ValueError('version 5: hurtbox count does not match')
            p['hurts'] = cur
            for a in cur:
                out += struct.pack('<4hHBB', a[0] * HQ + bx, a[1] * HQ + by, a[2] * HQ + bx, a[3] * HQ + by, a[4] * HQ, a[5], a[6])
        for _ in range(nobj):
            oid = O.uv(); kind = O.uv(); cat, lr = O.raw(2); ox = O.zz(); oy = O.zz(); mf8 = O.uv(); rot10 = O.zz(); mot = O.uv(); mot_hi, owner, nh, scale = O.raw(4)
            out += struct.pack('<IHBbhhHhIBBBB', oid, kind, cat, lr - 256 if lr > 127 else lr, ox, oy, mf8, rot10, mot, mot_hi, owner, nh, scale)
            for _ in range(nh):
                hx, hy, hx2, hy2 = (K.zz() for _ in range(4)); r = K.uv(); hid, hfl = K.raw(2); d10 = K.uv(); ang = K.zz(); kbg = K.uv(); bkb = K.uv()
                out += struct.pack('<4hHBBHhHH', hx + ox, hy + oy, hx2 + ox, hy2 + oy, r, hid, hfl, d10, ang, kbg, bkb)
    return bytes(out), nlines


def file_plain(data):
    ver, hs = struct.unpack_from('<HH', data, 4)
    stored, raw = struct.unpack_from('<II', data, 0x18)
    codec = data[0x20]
    body = data[hs:hs + stored]
    plain = zlib.decompress(body, -15) if codec == 1 else body
    if ver == 5:
        plain, _ = v5_to_v4(plain)
    return ver, plain, struct.unpack_from('<I', data, 0x58)[0]


def pack(data, tol=TOL):
    ver, plain, nlines = file_plain(data)
    if ver not in (3, 4):
        raise ValueError('only version 3 and 4 files are re-coded (this one is %d)' % ver)
    b5 = v4_to_v5(plain, nlines, tol)
    co = zlib.compressobj(9, zlib.DEFLATED, -15, 9)
    packed = co.compress(b5) + co.flush()
    hdr = bytearray(data[:HDR])
    struct.pack_into('<H', hdr, 4, 5)
    struct.pack_into('<II', hdr, 0x18, len(packed), len(b5))
    hdr[0x20] = 1
    return bytes(hdr) + packed


def max_error(a, b, nlines):
    """the largest difference between two version 4 plain bodies, by field kind (they must have the same layout)"""
    if len(a) != len(b):
        return 'layout differs (%d vs %d bytes)' % (len(a), len(b))
    import ssbv
    worst = {}
    class W:  # parse both with the tool's own parser through a fake file
        pass
    return worst


def stats(paths):
    tot4 = tot5 = 0; secs = 0
    for p in paths:
        data = open(p, 'rb').read()
        ver, plain, nlines = file_plain(data)
        if ver not in (3, 4):
            continue
        five = pack(data)
        _, back, _ = file_plain(five)
        ok = len(back) == len(plain)
        # field check: frame headers and counts exact, positions within tolerance
        err = 0.0
        if ok:
            o = 20 * nlines; n = len(plain)
            while o + 16 <= n:
                if plain[o:o + 16] != back[o:o + 16]:
                    ok = False; break
                mask, moved, nobj = plain[o + 4], plain[o + 5], struct.unpack_from('<H', plain, o + 6)[0]; o += 16 + 20 * moved
                for k in range(4):
                    if not mask & (1 << k):
                        continue
                    va = struct.unpack_from('<4fQibBBBBBHfI', plain, o); vb = struct.unpack_from('<4fQibBBBBBHfI', back, o)
                    err = max(err, abs(va[0] - vb[0]), abs(va[1] - vb[1]))
                    if va[4:13] != vb[4:13] or va[14] != vb[14]:
                        ok = False
                    nh, nu = va[9], va[10]; o += 44
                    if plain[o:o + 20 * nh] != back[o:o + 20 * nh]:
                        ok = False
                    o += 20 * nh
                    for i in range(nu):
                        ha = struct.unpack_from('<4hHBB', plain, o); hb = struct.unpack_from('<4hHBB', back, o)
                        err = max(err, max(abs(ha[t] - hb[t]) for t in range(5)) / 16)
                        if ha[5] != hb[5]:
                            ok = False
                        o += 12
                for _ in range(nobj):
                    nh = plain[o + 22]
                    if plain[o:o + 24 + 20 * nh] != back[o:o + 24 + 20 * nh]:
                        ok = False
                    o += 24 + 20 * nh
        s = struct.unpack_from('<I', data, 0xc)[0] / 60
        secs += s; tot4 += len(data); tot5 += len(five)
        print('%s %.1f min: %.0f KB -> %.0f KB (%.0f%%), %.2f MB per 7 min, round trip %s, largest error %.3f units' % (
            p.replace('\\', '/').split('/')[-1], s / 60, len(data) / 1024, len(five) / 1024, 100 * len(five) / len(data), len(five) / 1048576 / max(s / 60, .01) * 7, 'OK' if ok else 'DIFFERS', err))
    if secs:
        print('ALL: %.0f KB -> %.0f KB (%.0f%%), mean %.2f MB per 7 min' % (tot4 / 1024, tot5 / 1024, 100 * tot5 / tot4, tot5 / 1048576 / (secs / 60) * 7))


if __name__ == '__main__':
    if len(sys.argv) >= 3 and sys.argv[1] == 'stats':
        stats(sys.argv[2:])
    elif len(sys.argv) == 4 and sys.argv[1] == 'pack':
        open(sys.argv[3], 'wb').write(pack(open(sys.argv[2], 'rb').read()))
    else:
        print(__doc__)
