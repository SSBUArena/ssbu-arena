// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: the compact replay body (file version 5). The game writes version 4; after each game the launcher
// re-codes every version 4 file into version 5 (about a sixth of the size: ~0.65 MB for 7 minutes instead of ~4 MB), and
// reading expands version 5 back into a version 4 body that ReplayState.ParseBody reads as before.
// The layout and the two lossy steps (fighter values to 1/256, motion frame to 1/16, hurtboxes to 1/4 of a unit and one stored
// set per move frame) are documented in tools/replay/ssbv5.py, the reference both this file and the web viewer follow.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace Arena
{
    public static class ReplayCompact
    {
        public const int Tol = 2, Hq = 4;

        static ulong Zz(long n) => (ulong)((n << 1) ^ (n >> 63));
        static long UnZz(ulong u) => (long)(u >> 1) ^ -(long)(u & 1);
        static void Uv(List<byte> o, ulong n) { while (true) { byte b = (byte)(n & 0x7f); n >>= 7; if (n != 0) o.Add((byte)(b | 0x80)); else { o.Add(b); return; } } }
        static void Zv(List<byte> o, long n) => Uv(o, Zz(n));
        static void Raw(List<byte> o, byte[] b, int at, int n) { for (int i = 0; i < n; i++) o.Add(b[at + i]); }
        static int Q(float v, float m) => (int)Math.Round(v * m, MidpointRounding.AwayFromZero);
        static int Qh(int v) => (int)Math.Floor((v + Hq / 2) / (double)Hq);

        sealed class Prev { public int Qx, Qy, Qd, Qm = -16, Qs; public long St; public long Ff = -1; public ulong Mot; public bool HasMot; public int[][] Hurts; }

        /// <summary>a version 4 plain body (lines first) as a version 5 plain body</summary>
        public static byte[] Encode(byte[] p, int nlines)
        {
            var F = new List<byte>(); var M = new List<byte>(); var H = new List<byte>(); var K = new List<byte>(); var O = new List<byte>();
            var prev = new Prev[4]; var poses = new Dictionary<string, int[][]>();
            long pframe = 0; ulong pvalid = 0; bool havevalid = false;
            int o = 20 * nlines, n = p.Length;
            while (o + 16 <= n)
            {
                uint fr = BitConverter.ToUInt32(p, o); int mask = p[o + 4], moved = p[o + 5], nobj = BitConverter.ToUInt16(p, o + 6); ulong valid = BitConverter.ToUInt64(p, o + 8); o += 16;
                Zv(F, fr - pframe); pframe = fr; F.Add((byte)mask); F.Add((byte)moved); Uv(F, (ulong)nobj);
                if (havevalid && valid == pvalid) F.Add(0); else { F.Add(1); F.AddRange(BitConverter.GetBytes(valid)); pvalid = valid; havevalid = true; }
                Raw(M, p, o, 20 * moved); o += 20 * moved;
                for (int k = 0; k < 4; k++)
                {
                    if ((mask & (1 << k)) == 0) continue;
                    var pr = prev[k] ?? (prev[k] = new Prev());
                    float x = BitConverter.ToSingle(p, o), y = BitConverter.ToSingle(p, o + 4), dmg = BitConverter.ToSingle(p, o + 8), mf = BitConverter.ToSingle(p, o + 12);
                    ulong mot = BitConverter.ToUInt64(p, o + 16); int st = BitConverter.ToInt32(p, o + 24); sbyte lr = (sbyte)p[o + 28];
                    int nh = p[o + 31], nu = p[o + 32]; float shield = BitConverter.ToSingle(p, o + 36); uint ff = BitConverter.ToUInt32(p, o + 40);
                    int qx = Q(x, 256), qy = Q(y, 256), qd = Q(dmg, 256), qm = Q(mf, 16), qs = Q(shield, 256);
                    Zv(F, qx - pr.Qx); Zv(F, qy - pr.Qy); Zv(F, qd - pr.Qd); Zv(F, qm - pr.Qm - 16);
                    if (pr.HasMot && mot == pr.Mot) F.Add(0); else { F.Add(1); F.AddRange(BitConverter.GetBytes(mot)); }
                    Zv(F, st - pr.St);
                    Raw(F, p, o + 28, 6); Raw(F, p, o + 34, 2);
                    Zv(F, qs - pr.Qs); Zv(F, (long)ff - pr.Ff - 1);
                    pr.Qx = qx; pr.Qy = qy; pr.Qd = qd; pr.Qm = qm; pr.Mot = mot; pr.HasMot = true; pr.St = st; pr.Qs = qs; pr.Ff = ff;
                    o += 44;
                    int bx = (qx + 8) >> 4, by = (qy + 8) >> 4;
                    for (int i = 0; i < nh; i++, o += 20) Hit(K, p, o, bx, by);
                    var cur = new int[nu][];
                    for (int i = 0; i < nu; i++, o += 12)
                        cur[i] = new[] { Qh(BitConverter.ToInt16(p, o) - bx), Qh(BitConverter.ToInt16(p, o + 2) - by), Qh(BitConverter.ToInt16(p, o + 4) - bx), Qh(BitConverter.ToInt16(p, o + 6) - by), Qh(BitConverter.ToUInt16(p, o + 8)), p[o + 10], p[o + 11] };
                    string key = k + ":" + mot + ":" + ((qm + 8) >> 4) + ":" + lr;
                    int[][] reference; var ph = pr.Hurts;
                    if (poses.TryGetValue(key, out reference) && Same(reference, cur, true)) { H.Add(2); cur = reference; }
                    else if (ph != null && Same(ph, cur, false))
                    {
                        H.Add(1);
                        for (int i = 0; i < cur.Length; i++) { for (int t = 0; t < 5; t++) Zv(H, cur[i][t] - ph[i][t]); H.Add((byte)((cur[i][6] - ph[i][6]) & 255)); }
                        poses[key] = cur;
                    }
                    else
                    {
                        H.Add(0); Uv(H, (ulong)cur.Length);
                        foreach (var b in cur) { for (int t = 0; t < 4; t++) Zv(H, b[t]); Uv(H, (ulong)b[4]); H.Add((byte)b[5]); H.Add((byte)b[6]); }
                        poses[key] = cur;
                    }
                    pr.Hurts = cur;
                }
                for (int i = 0; i < nobj; i++)
                {
                    uint oid = BitConverter.ToUInt32(p, o); int kind = BitConverter.ToUInt16(p, o + 4); short ox = BitConverter.ToInt16(p, o + 8), oy = BitConverter.ToInt16(p, o + 10);
                    Uv(O, oid); Uv(O, (ulong)kind); O.Add(p[o + 6]); O.Add(p[o + 7]); Zv(O, ox); Zv(O, oy); Uv(O, BitConverter.ToUInt16(p, o + 12)); Zv(O, BitConverter.ToInt16(p, o + 14)); Uv(O, BitConverter.ToUInt32(p, o + 16));
                    Raw(O, p, o + 20, 4);
                    int nh = p[o + 22]; o += 24;
                    for (int h = 0; h < nh; h++, o += 20) Hit(K, p, o, ox, oy);
                }
            }
            var all = new List<byte>(); all.AddRange(BitConverter.GetBytes((uint)nlines)); Raw(all, p, 0, 20 * nlines);
            foreach (var s in new[] { F, M, H, K, O }) all.AddRange(BitConverter.GetBytes((uint)s.Count));
            foreach (var s in new[] { F, M, H, K, O }) all.AddRange(s);
            return all.ToArray();
        }

        static bool Same(int[][] a, int[][] b, bool close)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i][5] != b[i][5]) return false;
                if (!close) continue;
                if (a[i][6] != b[i][6]) return false;
                for (int t = 0; t < 5; t++) if (Math.Abs(a[i][t] - b[i][t]) > Tol) return false;
            }
            return true;
        }

        static void Hit(List<byte> K, byte[] p, int o, int bx, int by)
        {
            Zv(K, BitConverter.ToInt16(p, o) - bx); Zv(K, BitConverter.ToInt16(p, o + 2) - by); Zv(K, BitConverter.ToInt16(p, o + 4) - bx); Zv(K, BitConverter.ToInt16(p, o + 6) - by);
            Uv(K, BitConverter.ToUInt16(p, o + 8)); K.Add(p[o + 10]); K.Add(p[o + 11]); Uv(K, BitConverter.ToUInt16(p, o + 12)); Zv(K, BitConverter.ToInt16(p, o + 14));
            Uv(K, BitConverter.ToUInt16(p, o + 16)); Uv(K, BitConverter.ToUInt16(p, o + 18));
        }

        sealed class Rd
        {
            readonly byte[] b; int o; readonly int end;
            public Rd(byte[] b, int at, int n) { this.b = b; o = at; end = at + n; if (end > b.Length) throw new Exception("the replay file is damaged"); }
            public bool More => o < end;
            public byte U8() { if (o >= end) throw new Exception("the replay file is damaged"); return b[o++]; }
            public void Raw(List<byte> to, int n) { if (o + n > end) throw new Exception("the replay file is damaged"); for (int i = 0; i < n; i++) to.Add(b[o + i]); o += n; }
            public ulong Raw8() { if (o + 8 > end) throw new Exception("the replay file is damaged"); ulong v = BitConverter.ToUInt64(b, o); o += 8; return v; }
            public ulong Uv() { ulong n = 0; int s = 0; while (true) { byte c = U8(); n |= (ulong)(c & 0x7f) << s; s += 7; if ((c & 0x80) == 0) return n; if (s > 63) throw new Exception("the replay file is damaged"); } }
            public long Zz() => UnZz(Uv());
        }

        /// <summary>a version 5 plain body as the version 4 plain body (lines first) ParseBody reads; nlines out</summary>
        public static byte[] Decode(byte[] b5, out int nlines)
        {
            nlines = (int)BitConverter.ToUInt32(b5, 0);
            int o = 4 + 20 * nlines;
            if (o + 20 > b5.Length) throw new Exception("the replay file is damaged");
            var rs = new Rd[5];
            int at = o + 20;
            for (int i = 0; i < 5; i++) { int len = (int)BitConverter.ToUInt32(b5, o + 4 * i); rs[i] = new Rd(b5, at, len); at += len; }
            Rd F = rs[0], M = rs[1], H = rs[2], K = rs[3], O = rs[4];
            var outp = new List<byte>(b5.Length * 6);
            for (int i = 0; i < 20 * nlines; i++) outp.Add(b5[4 + i]);
            var prev = new Prev[4]; var poses = new Dictionary<string, int[][]>();
            long pframe = 0; ulong pvalid = 0;
            while (F.More)
            {
                long fr = pframe + F.Zz(); pframe = fr;
                int mask = F.U8(), moved = F.U8(), nobj = (int)F.Uv();
                if (F.U8() != 0) pvalid = F.Raw8();
                outp.AddRange(BitConverter.GetBytes((uint)fr)); outp.Add((byte)mask); outp.Add((byte)moved); outp.AddRange(BitConverter.GetBytes((ushort)nobj)); outp.AddRange(BitConverter.GetBytes(pvalid));
                M.Raw(outp, 20 * moved);
                for (int k = 0; k < 4; k++)
                {
                    if ((mask & (1 << k)) == 0) continue;
                    var pr = prev[k] ?? (prev[k] = new Prev());
                    int qx = pr.Qx + (int)F.Zz(), qy = pr.Qy + (int)F.Zz(), qd = pr.Qd + (int)F.Zz(), qm = pr.Qm + 16 + (int)F.Zz();
                    ulong mot = F.U8() != 0 ? F.Raw8() : pr.Mot;
                    long st = pr.St + F.Zz();
                    var six = new List<byte>(8); F.Raw(six, 6); F.Raw(six, 2);
                    int qs = pr.Qs + (int)F.Zz(); long ffl = pr.Ff + 1 + F.Zz(); uint ff = (uint)ffl;
                    pr.Qx = qx; pr.Qy = qy; pr.Qd = qd; pr.Qm = qm; pr.Mot = mot; pr.St = st; pr.Qs = qs; pr.Ff = ffl;
                    outp.AddRange(BitConverter.GetBytes(qx / 256f)); outp.AddRange(BitConverter.GetBytes(qy / 256f)); outp.AddRange(BitConverter.GetBytes(qd / 256f)); outp.AddRange(BitConverter.GetBytes(qm / 16f));
                    outp.AddRange(BitConverter.GetBytes(mot)); outp.AddRange(BitConverter.GetBytes((int)st)); outp.AddRange(six);
                    outp.AddRange(BitConverter.GetBytes(qs / 256f)); outp.AddRange(BitConverter.GetBytes(ff));
                    int nh = six[3], nu = six[4]; sbyte lr = (sbyte)six[0];
                    int bx = (qx + 8) >> 4, by = (qy + 8) >> 4;
                    for (int i = 0; i < nh; i++) HitOut(K, outp, bx, by);
                    int mode = H.U8(); string key = k + ":" + mot + ":" + ((qm + 8) >> 4) + ":" + lr;
                    int[][] cur;
                    if (mode == 2) { if (!poses.TryGetValue(key, out cur)) throw new Exception("the replay file is damaged"); }
                    else if (mode == 1)
                    {
                        var ph = pr.Hurts ?? throw new Exception("the replay file is damaged");
                        cur = new int[ph.Length][];
                        for (int i = 0; i < ph.Length; i++)
                        {
                            var c = new int[7];
                            for (int t = 0; t < 5; t++) c[t] = ph[i][t] + (int)H.Zz();
                            c[5] = ph[i][5]; c[6] = (ph[i][6] + H.U8()) & 255; cur[i] = c;
                        }
                        poses[key] = cur;
                    }
                    else
                    {
                        cur = new int[(int)H.Uv()][];
                        for (int i = 0; i < cur.Length; i++)
                        {
                            var c = new int[7];
                            for (int t = 0; t < 4; t++) c[t] = (int)H.Zz();
                            c[4] = (int)H.Uv(); c[5] = H.U8(); c[6] = H.U8(); cur[i] = c;
                        }
                        poses[key] = cur;
                    }
                    if (cur.Length != nu) throw new Exception("the replay file is damaged");
                    pr.Hurts = cur;
                    foreach (var c in cur)
                    {
                        outp.AddRange(BitConverter.GetBytes((short)(c[0] * Hq + bx))); outp.AddRange(BitConverter.GetBytes((short)(c[1] * Hq + by)));
                        outp.AddRange(BitConverter.GetBytes((short)(c[2] * Hq + bx))); outp.AddRange(BitConverter.GetBytes((short)(c[3] * Hq + by)));
                        outp.AddRange(BitConverter.GetBytes((ushort)(c[4] * Hq))); outp.Add((byte)c[5]); outp.Add((byte)c[6]);
                    }
                }
                for (int i = 0; i < nobj; i++)
                {
                    uint oid = (uint)O.Uv(); int kind = (int)O.Uv(); byte cat = O.U8(), lr = O.U8(); short ox = (short)O.Zz(), oy = (short)O.Zz();
                    ushort mf8 = (ushort)O.Uv(); short rot = (short)O.Zz(); uint mot = (uint)O.Uv();
                    var four = new List<byte>(4); O.Raw(four, 4);
                    outp.AddRange(BitConverter.GetBytes(oid)); outp.AddRange(BitConverter.GetBytes((ushort)kind)); outp.Add(cat); outp.Add(lr);
                    outp.AddRange(BitConverter.GetBytes(ox)); outp.AddRange(BitConverter.GetBytes(oy)); outp.AddRange(BitConverter.GetBytes(mf8)); outp.AddRange(BitConverter.GetBytes(rot));
                    outp.AddRange(BitConverter.GetBytes(mot)); outp.AddRange(four);
                    for (int h = 0; h < four[2]; h++) HitOut(K, outp, ox, oy);
                }
            }
            return outp.ToArray();
        }

        static void HitOut(Rd K, List<byte> outp, int bx, int by)
        {
            short x = (short)(K.Zz() + bx), y = (short)(K.Zz() + by), x2 = (short)(K.Zz() + bx), y2 = (short)(K.Zz() + by);
            ushort r = (ushort)K.Uv(); byte id = K.U8(), fl = K.U8(); ushort d10 = (ushort)K.Uv(); short ang = (short)K.Zz(); ushort kbg = (ushort)K.Uv(), bkb = (ushort)K.Uv();
            outp.AddRange(BitConverter.GetBytes(x)); outp.AddRange(BitConverter.GetBytes(y)); outp.AddRange(BitConverter.GetBytes(x2)); outp.AddRange(BitConverter.GetBytes(y2));
            outp.AddRange(BitConverter.GetBytes(r)); outp.Add(id); outp.Add(fl); outp.AddRange(BitConverter.GetBytes(d10)); outp.AddRange(BitConverter.GetBytes(ang));
            outp.AddRange(BitConverter.GetBytes(kbg)); outp.AddRange(BitConverter.GetBytes(bkb));
        }

        static byte[] Deflate(byte[] b)
        {
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(b, 0, b.Length);
                return ms.ToArray();
            }
        }

        /// <summary>a version 4 file re-coded as version 5 in place; checked by decoding it back first (the original stays when anything differs).
        /// SSBU_ARENA_REPLAY_COMPACT=0 (environment of the launcher): off. Returns true when the file was re-coded</summary>
        public static bool Compact(string path)
        {
            if (Environment.GetEnvironmentVariable("SSBU_ARENA_REPLAY_COMPACT") == "0") return false;
            byte[] f = File.ReadAllBytes(path);
            int ver = f.Length < ReplayState.HeaderSize ? 0 : BitConverter.ToUInt16(f, 4);
            if (ver != 3 && ver != 4) return false;      // (version 3: the same layout, no grab boxes)
            int hs = BitConverter.ToUInt16(f, 6); uint stored = BitConverter.ToUInt32(f, 0x18), raw = BitConverter.ToUInt32(f, 0x1c);
            if (hs + (long)stored > f.Length) return false;
            byte[] plain;
            if (f[0x20] == 0) { plain = new byte[stored]; Buffer.BlockCopy(f, hs, plain, 0, (int)stored); }
            else if (f[0x20] == 1)
            {
                plain = new byte[raw];
                using (var ds = new DeflateStream(new MemoryStream(f, hs, (int)stored), CompressionMode.Decompress)) { int o = 0, k; while (o < raw && (k = ds.Read(plain, o, (int)raw - o)) > 0) o += k; if (o != raw) return false; }
            }
            else return false;
            int nlines = (int)BitConverter.ToUInt32(f, 0x58);
            byte[] b5 = Encode(plain, nlines);
            byte[] back = Decode(b5, out int nl2);
            if (nl2 != nlines || back.Length != plain.Length) return false;      // (the layout must come back byte for byte in size)
            byte[] packed = Deflate(b5);
            var o5 = new byte[hs + packed.Length];
            Buffer.BlockCopy(f, 0, o5, 0, hs);
            Buffer.BlockCopy(packed, 0, o5, hs, packed.Length);
            BitConverter.GetBytes((ushort)5).CopyTo(o5, 4);
            BitConverter.GetBytes((uint)packed.Length).CopyTo(o5, 0x18);
            BitConverter.GetBytes((uint)b5.Length).CopyTo(o5, 0x1c);
            o5[0x20] = 1;
            string tmp = path + ".v5";
            File.WriteAllBytes(tmp, o5);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
            return true;
        }

        /// <summary>every version 4 file of the folder re-coded (after a game; the newest first). Files in use are skipped</summary>
        public static int CompactFolder(string folder)
        {
            int n = 0;
            if (!Directory.Exists(folder)) return 0;
            var files = new DirectoryInfo(folder).GetFiles("*.ssbv");
            Array.Sort(files, (a, b) => string.CompareOrdinal(b.Name, a.Name));
            foreach (var fi in files) { try { if (Compact(fi.FullName)) n++; } catch { } }
            return n;
        }
    }
}
