// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: the schematic replays (.ssbv). Each game writes one per match into <Saves>\replays
// (fighter_sdk/replay_state.c documents the layout): the state of each fighter in every final frame as the game itself
// reported it (position, damage, action, shield, stocks, hit and hurt boxes), the stage's collision lines and blast zones.
// Here: reading (the raw body is deflated in place the first time, codec 1), the list, names, and the match statistics.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Arena
{
    public struct RvHit { public float X, Y, X2, Y2, R; public int Id, Flags; public float Damage; public int Angle; }
    public struct RvHurt { public float X, Y, X2, Y2, R; public int Part, Status; }
    public sealed class RvFighterFrame
    {
        public float X, Y, Damage, MotionFrame, Shield;
        public ulong Motion;
        public int Status, Lr, Situation, Stocks, FightFrame;
        public RvHit[] Hits;
        public RvHurt[] Hurts;
        public bool Grounded => Situation == 0;
    }
    public sealed class RvLine { public float X1, Y1, X2, Y2; public int Flags; public bool Floor => (Flags & 1) != 0; public bool Passable => (Flags & 2) != 0; public bool Wall => (Flags & 4) != 0; public bool Moves => (Flags & 16) != 0; public int FirstFrame => (int)((uint)Flags >> 8); }
    public sealed class RvFrame
    {
        public int Number;
        public RvFighterFrame[] F;                      // by fighter (its main slot; null: not in this frame)
        public List<KeyValuePair<int, RvFighterFrame>> Extra;   // a player's other bodies (Nana of the Ice Climbers): fighter index, state
        public float[][] Lines;                         // the stage lines' end points at this frame (shared arrays when unchanged)
        public ulong LinesOn;                           // the lines that exist at this frame (bit per line)
    }
    public sealed class RvFighter
    {
        public ulong UiChara; public int Kind, Colour, Type, Entry, Slot, EndStocks; public string Name = ""; public float EndDamage;
        public string FighterKey => ReplayNames.FighterKey(UiChara);
        public string FighterName => ReplayNames.FighterName(UiChara, Kind);
    }

    public sealed class ReplayState
    {
        public const int HeaderSize = 0x200;
        public string Path;
        public byte[] SlotEntry = { 0xff, 0xff, 0xff, 0xff };
        public uint MatchId; public int Kind; public string LocalName = "";      // online: the handshake's match id; 0 local 1 matchmaking 2 LDN room 3 other online
        public int Version, Flags, Frames, Codec, RuleKind, Stocks, GameBuild, LocalPlayer, MatchNumber, FirstFf, EndedFrame, Winner, RecUs;
        public long UnixTime;
        public ulong UiStage, Seed; public int StageForm, StageId;
        public float[] Dead = new float[4], Camera = new float[4];
        public string Build = "";
        public List<RvFighter> Fighters = new List<RvFighter>();
        public List<RvLine> Lines = new List<RvLine>();
        public List<RvFrame> FrameList = new List<RvFrame>();
        public long FileSize;

        public DateTime When => DateTimeOffset.FromUnixTimeSeconds(UnixTime).LocalDateTime;
        public bool Rollback => (Flags & 1) != 0;
        public bool LoopTest => (Flags & 2) != 0;
        public bool Ended => (Flags & 4) != 0;
        public string StageName => ReplayNames.StageName(UiStage);
        public string StageKey => ReplayNames.StageKey(UiStage);
        public string Length { get { int s = Frames / 60; return (s / 60) + ":" + (s % 60).ToString("00"); } }

        static string CStr(byte[] b, int o, int n) { int e = o; while (e < o + n && b[e] != 0) e++; return Encoding.UTF8.GetString(b, o, e - o); }

        /// <summary>the header only (the list)</summary>
        public static ReplayState ReadHeader(string path) { return Read(path, false); }

        public static ReplayState Read(string path, bool body = true)
        {
            byte[] f = File.ReadAllBytes(path);
            if (f.Length < HeaderSize || Encoding.ASCII.GetString(f, 0, 4) != "SSBV") throw new Exception("not an SSBU Arena match state file");
            var r = new ReplayState { Path = path, FileSize = f.Length };
            r.Version = BitConverter.ToUInt16(f, 4);
            if (r.Version < 1 || r.Version > 2) throw new Exception("match state format " + r.Version + ": update SSBU Arena");
            int hs = BitConverter.ToUInt16(f, 6);
            r.Flags = (int)BitConverter.ToUInt32(f, 8); r.Frames = (int)BitConverter.ToUInt32(f, 0xc); r.UnixTime = BitConverter.ToInt64(f, 0x10);
            uint stored = BitConverter.ToUInt32(f, 0x18), raw = BitConverter.ToUInt32(f, 0x1c);
            r.Codec = f[0x20]; int nf = f[0x21]; r.RuleKind = f[0x22]; r.Stocks = f[0x23];
            r.UiStage = BitConverter.ToUInt64(f, 0x28); r.StageForm = (int)BitConverter.ToUInt32(f, 0x30); r.StageId = (int)BitConverter.ToUInt32(f, 0x34);
            for (int i = 0; i < 4; i++) { r.Dead[i] = BitConverter.ToSingle(f, 0x38 + 4 * i); r.Camera[i] = BitConverter.ToSingle(f, 0x48 + 4 * i); }
            int nlines = (int)BitConverter.ToUInt32(f, 0x58); r.GameBuild = (int)BitConverter.ToUInt32(f, 0x5c);
            for (int i = 0; i < nf && i < 4; i++)
            {
                int o = 0x60 + 0x40 * i;
                if (BitConverter.ToInt32(f, o + 8) < 0 && f[o + 15] == 0xff) continue;     // (files of the first test builds: empty player records)
                r.Fighters.Add(new RvFighter
                {
                    UiChara = BitConverter.ToUInt64(f, o), Kind = BitConverter.ToInt32(f, o + 8), Colour = f[o + 12], Type = f[o + 13], Entry = f[o + 14], Slot = f[o + 15],
                    Name = CStr(f, o + 16, 32), EndStocks = (int)BitConverter.ToUInt32(f, o + 48), EndDamage = BitConverter.ToSingle(f, o + 52),
                });
            }
            r.Build = CStr(f, 0x160, 32);
            r.LocalPlayer = (int)BitConverter.ToUInt32(f, 0x180); r.MatchNumber = (int)BitConverter.ToUInt32(f, 0x184); r.Seed = BitConverter.ToUInt64(f, 0x188);
            r.FirstFf = (int)BitConverter.ToUInt32(f, 0x190); r.EndedFrame = (int)BitConverter.ToUInt32(f, 0x194); r.Winner = BitConverter.ToInt32(f, 0x198); r.RecUs = (int)BitConverter.ToUInt32(f, 0x19c);
            Buffer.BlockCopy(f, 0x1a0, r.SlotEntry, 0, 4);
            r.MatchId = BitConverter.ToUInt32(f, 0x1a4); r.Kind = f[0x1a8]; r.LocalName = CStr(f, 0x1ac, 32);
            if (!body) return r;
            if (hs + (long)stored > f.Length) throw new Exception("the match state file is cut short");
            byte[] b;
            if (r.Codec == 0) { b = new byte[stored]; Buffer.BlockCopy(f, hs, b, 0, (int)stored); }
            else if (r.Codec == 1) b = Inflate(f, hs, (int)stored, (int)raw);
            else throw new Exception("unknown compression " + r.Codec);
            r.ParseBody(b, nlines);
            return r;
        }

        static byte[] Inflate(byte[] f, int at, int n, int raw)
        {
            var b = new byte[raw];
            using (var ds = new DeflateStream(new MemoryStream(f, at, n), CompressionMode.Decompress))
            {
                int o = 0, k;
                while (o < raw && (k = ds.Read(b, o, raw - o)) > 0) o += k;
                if (o != raw) throw new Exception("the match state file is damaged");
            }
            return b;
        }

        void ParseBody(byte[] b, int nlines)
        {
            int o = 0;
            var cur = new float[nlines][];
            for (int i = 0; i < nlines; i++)
            {
                var L = new RvLine { X1 = BitConverter.ToSingle(b, o), Y1 = BitConverter.ToSingle(b, o + 4), X2 = BitConverter.ToSingle(b, o + 8), Y2 = BitConverter.ToSingle(b, o + 12), Flags = (int)BitConverter.ToUInt32(b, o + 16) };
                Lines.Add(L); cur[i] = new[] { L.X1, L.Y1, L.X2, L.Y2 };
                o += 20;
            }
            int nf = Fighters.Count;
            int fh = Version >= 2 ? 16 : 8;                 // (version 1: no lines-on mask)
            while (o + fh <= b.Length)
            {
                var fr = new RvFrame { Number = (int)BitConverter.ToUInt32(b, o), F = new RvFighterFrame[Math.Max(nf, 4)], LinesOn = fh == 16 ? BitConverter.ToUInt64(b, o + 8) : ulong.MaxValue };
                int mask = b[o + 4], moved = b[o + 5]; o += fh;
                if (moved > 0) { cur = (float[][])cur.Clone(); }
                for (int m = 0; m < moved; m++)
                {
                    int li = BitConverter.ToUInt16(b, o);
                    if (li < nlines) cur[li] = new[] { BitConverter.ToSingle(b, o + 4), BitConverter.ToSingle(b, o + 8), BitConverter.ToSingle(b, o + 12), BitConverter.ToSingle(b, o + 16) };
                    o += 20;
                }
                fr.Lines = cur;
                for (int k = 0; k < 4; k++)
                {
                    if ((mask & (1 << k)) == 0) continue;
                    var s = new RvFighterFrame
                    {
                        X = BitConverter.ToSingle(b, o), Y = BitConverter.ToSingle(b, o + 4), Damage = BitConverter.ToSingle(b, o + 8), MotionFrame = BitConverter.ToSingle(b, o + 12),
                        Motion = BitConverter.ToUInt64(b, o + 16), Status = BitConverter.ToInt32(b, o + 24), Lr = (sbyte)b[o + 28], Situation = b[o + 29], Stocks = b[o + 30],
                        Shield = BitConverter.ToSingle(b, o + 36), FightFrame = (int)BitConverter.ToUInt32(b, o + 40),
                    };
                    int nh = b[o + 31], nu = b[o + 32];
                    o += 44;
                    s.Hits = new RvHit[nh];
                    for (int i = 0; i < nh; i++, o += 20)
                        s.Hits[i] = new RvHit
                        {
                            X = BitConverter.ToInt16(b, o) / 16f, Y = BitConverter.ToInt16(b, o + 2) / 16f, X2 = BitConverter.ToInt16(b, o + 4) / 16f, Y2 = BitConverter.ToInt16(b, o + 6) / 16f,
                            R = BitConverter.ToUInt16(b, o + 8) / 16f, Id = b[o + 10], Flags = b[o + 11], Damage = BitConverter.ToUInt16(b, o + 12) / 10f, Angle = BitConverter.ToInt16(b, o + 14),
                        };
                    s.Hurts = new RvHurt[nu];
                    for (int i = 0; i < nu; i++, o += 12)
                        s.Hurts[i] = new RvHurt
                        {
                            X = BitConverter.ToInt16(b, o) / 16f, Y = BitConverter.ToInt16(b, o + 2) / 16f, X2 = BitConverter.ToInt16(b, o + 4) / 16f, Y2 = BitConverter.ToInt16(b, o + 6) / 16f,
                            R = BitConverter.ToUInt16(b, o + 8) / 16f, Part = b[o + 10], Status = b[o + 11],
                        };
                    // the recorder's slot k -> the fighter whose main slot it is; else another body of a fighter (same entry)
                    int fi = Fighters.FindIndex(x => x.Slot == k);
                    if (fi >= 0) fr.F[fi] = s;
                    else
                    {
                        int fe = SlotEntry[k] != 0xff ? Fighters.FindIndex(x => x.Entry == SlotEntry[k]) : -1;
                        if (fe < 0 && SlotEntry[k] == 0xff && k < Fighters.Count && fr.F[k] == null) fr.F[k] = s;       // (older files: no slot table)
                        else if (fe >= 0) (fr.Extra ?? (fr.Extra = new List<KeyValuePair<int, RvFighterFrame>>())).Add(new KeyValuePair<int, RvFighterFrame>(fe, s));
                    }
                }
                FrameList.Add(fr);
            }
            Frames = FrameList.Count;
            // the lines of the start scan are known only once the scan is over (its first frames): they were there before too
            for (int i = 0; i < Lines.Count && i < 64; i++)
            {
                if (Lines[i].FirstFrame != 0) continue;
                int on = FrameList.FindIndex(x => (x.LinesOn & (1UL << i)) != 0);
                for (int j = 0; j < on; j++) FrameList[j].LinesOn |= 1UL << i;
            }
        }

        /// <summary>a raw file (codec 0) rewritten with its body deflated (codec 1): about a tenth of the size</summary>
        public static void Pack(string path)
        {
            byte[] f = File.ReadAllBytes(path);
            if (f.Length < HeaderSize || f[0x20] != 0) return;
            int hs = BitConverter.ToUInt16(f, 6); uint raw = BitConverter.ToUInt32(f, 0x1c);
            byte[] packed;
            using (var ms = new MemoryStream())
            {
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true)) ds.Write(f, hs, (int)raw);
                packed = ms.ToArray();
            }
            var o = new byte[hs + packed.Length];
            Buffer.BlockCopy(f, 0, o, 0, hs);
            Buffer.BlockCopy(packed, 0, o, hs, packed.Length);
            o[0x20] = 1;
            BitConverter.GetBytes((uint)packed.Length).CopyTo(o, 0x18);
            string tmp = path + ".pack";
            File.WriteAllBytes(tmp, o);
            File.Copy(tmp, path, true);
            File.Delete(tmp);
        }
    }

    /// <summary>the local list: <Saves>\replays\*.ssbv</summary>
    public static class ReplayLibrary
    {
        public static string Folder => System.IO.Path.Combine(Core.Saves ?? ".", "replays");
        public const int Keep = 100;

        /// <summary>this PC keeps the newest Keep recordings (the server's copies of uploaded ones stay)</summary>
        public static void Cap()
        {
            try
            {
                if (!Directory.Exists(Folder)) return;
                var files = new DirectoryInfo(Folder).GetFiles("*.ssbv").OrderByDescending(f => f.Name).ToList();      // (names are the date and time)
                foreach (var f in files.Skip(Keep)) { try { f.Delete(); } catch { } }
            }
            catch { }
        }

        public static List<ReplayState> List()
        {
            var list = new List<ReplayState>();
            if (!Directory.Exists(Folder)) return list;
            foreach (var p in Directory.GetFiles(Folder, "*.ssbv"))
            {
                try
                {
                    var h = ReplayState.ReadHeader(p);
                    if (h.Codec == 0) { try { ReplayState.Pack(p); h = ReplayState.ReadHeader(p); } catch { } }
                    list.Add(h);
                }
                catch { }
            }
            return list.OrderByDescending(r => r.UnixTime).ToList();
        }
    }

    /// <summary>the match's numbers for the side panel</summary>
    public sealed class ReplayStats
    {
        public sealed class Ko { public int Frame, Fighter; public float X, Y; public string Where; }
        public sealed class PerFighter { public float DamageDealt, DamageTaken; public int StocksLost, Hits; public List<KeyValuePair<string, int>> Moves = new List<KeyValuePair<string, int>>(); }
        public List<Ko> Kos = new List<Ko>();
        public PerFighter[] P;

        public static ReplayStats Of(ReplayState r)
        {
            int n = r.Fighters.Count;
            var st = new ReplayStats { P = Enumerable.Range(0, n).Select(_ => new PerFighter()).ToArray() };
            var moves = Enumerable.Range(0, n).Select(_ => new Dictionary<string, int>()).ToArray();
            var last = new RvFighterFrame[n];
            var lastAlive = new RvFighterFrame[n];
            for (int fi = 0; fi < r.FrameList.Count; fi++)
            {
                var fr = r.FrameList[fi];
                for (int k = 0; k < n; k++)
                {
                    var s = fr.F[k];
                    if (s == null) continue;
                    var p = last[k];
                    if (p != null)
                    {
                        float d = s.Damage - p.Damage;
                        if (d > 0.05f && s.Stocks == p.Stocks)
                        {
                            st.P[k].DamageTaken += d;
                            if (n == 2) { st.P[1 - k].DamageDealt += d; st.P[1 - k].Hits++; }
                        }
                        if (s.Stocks < p.Stocks)
                        {
                            st.P[k].StocksLost += p.Stocks - s.Stocks;
                            var a = lastAlive[k] ?? p;
                            st.Kos.Add(new Ko { Frame = fr.Number, Fighter = k, X = a.X, Y = a.Y, Where = KoSide(r, a) });
                        }
                        if (s.Motion != p.Motion && s.Hits.Length >= 0)
                        {
                            string mv = ReplayNames.MoveName(s.Motion);
                            if (mv != null) { moves[k].TryGetValue(mv, out int c); moves[k][mv] = c + 1; }
                        }
                    }
                    else if (ReplayNames.MoveName(s.Motion) is string m0) moves[k][m0] = 1;
                    last[k] = s;
                    if (Math.Abs(s.X) < 400 && Math.Abs(s.Y) < 400 && InsideBlast(r, s)) lastAlive[k] = s;
                }
            }
            for (int k = 0; k < n; k++) st.P[k].Moves = moves[k].OrderByDescending(x => x.Value).Take(6).ToList();
            return st;
        }

        static bool InsideBlast(ReplayState r, RvFighterFrame s)
        {
            var z = ReplayNames.Blast(r);
            return s.X > z[0] && s.X < z[1] && s.Y < z[2] && s.Y > z[3];
        }

        static string KoSide(ReplayState r, RvFighterFrame a)
        {
            var z = ReplayNames.Blast(r);
            double dl = a.X - z[0], dr = z[1] - a.X, dt = z[2] - a.Y, db = a.Y - z[3];
            double m = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            return m == dt ? "top" : m == db ? "bottom" : m == dl ? "left" : "right";
        }
    }

    /// <summary>names of fighters, stages and moves from the game's hash40 ids (crc32 of the name + its length)</summary>
    public static class ReplayNames
    {
        public static ulong Hash40(string s)
        {
            uint crc = 0xffffffff;
            foreach (byte b in Encoding.ASCII.GetBytes(s)) { crc ^= b; for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xedb88320u & (uint)-(int)(crc & 1)); }
            return ((ulong)s.Length << 32) | ~crc;
        }

        static readonly string[,] Fighters = {
            { "mario", "Mario" }, { "donkey", "Donkey Kong" }, { "link", "Link" }, { "samus", "Samus" }, { "samusd", "Dark Samus" }, { "yoshi", "Yoshi" },
            { "kirby", "Kirby" }, { "fox", "Fox" }, { "pikachu", "Pikachu" }, { "luigi", "Luigi" }, { "ness", "Ness" }, { "captain", "Captain Falcon" },
            { "purin", "Jigglypuff" }, { "peach", "Peach" }, { "daisy", "Daisy" }, { "koopa", "Bowser" }, { "ice_climber", "Ice Climbers" }, { "sheik", "Sheik" },
            { "zelda", "Zelda" }, { "mariod", "Dr. Mario" }, { "pichu", "Pichu" }, { "falco", "Falco" }, { "marth", "Marth" }, { "lucina", "Lucina" },
            { "younglink", "Young Link" }, { "ganon", "Ganondorf" }, { "mewtwo", "Mewtwo" }, { "roy", "Roy" }, { "chrom", "Chrom" }, { "gamewatch", "Mr. Game & Watch" },
            { "metaknight", "Meta Knight" }, { "pit", "Pit" }, { "pitb", "Dark Pit" }, { "szerosuit", "Zero Suit Samus" }, { "wario", "Wario" }, { "snake", "Snake" },
            { "ike", "Ike" }, { "ptrainer", "Pokemon Trainer" }, { "pzenigame", "Squirtle" }, { "pfushigisou", "Ivysaur" }, { "plizardon", "Charizard" },
            { "diddy", "Diddy Kong" }, { "lucas", "Lucas" }, { "sonic", "Sonic" }, { "dedede", "King Dedede" }, { "pikmin", "Olimar" }, { "lucario", "Lucario" },
            { "robot", "R.O.B." }, { "toonlink", "Toon Link" }, { "wolf", "Wolf" }, { "murabito", "Villager" }, { "rockman", "Mega Man" }, { "wiifit", "Wii Fit Trainer" },
            { "rosetta", "Rosalina & Luma" }, { "littlemac", "Little Mac" }, { "gekkouga", "Greninja" }, { "miifighter", "Mii Brawler" }, { "miiswordsman", "Mii Swordfighter" },
            { "miigunner", "Mii Gunner" }, { "palutena", "Palutena" }, { "pacman", "Pac-Man" }, { "reflet", "Robin" }, { "shulk", "Shulk" }, { "koopajr", "Bowser Jr." },
            { "duckhunt", "Duck Hunt" }, { "ryu", "Ryu" }, { "ken", "Ken" }, { "cloud", "Cloud" }, { "kamui", "Corrin" }, { "bayonetta", "Bayonetta" },
            { "inkling", "Inkling" }, { "ridley", "Ridley" }, { "simon", "Simon" }, { "richter", "Richter" }, { "krool", "King K. Rool" }, { "shizue", "Isabelle" },
            { "gaogaen", "Incineroar" }, { "packun", "Piranha Plant" }, { "jack", "Joker" }, { "brave", "Hero" }, { "buddy", "Banjo & Kazooie" }, { "dolly", "Terry" },
            { "master", "Byleth" }, { "tantan", "Min Min" }, { "pickel", "Steve" }, { "edge", "Sephiroth" }, { "eflame", "Pyra" }, { "elight", "Mythra" },
            { "element", "Pyra / Mythra" }, { "eflame_first", "Pyra" }, { "elight_first", "Mythra" }, { "demon", "Kazuya" }, { "trail", "Sora" } };
        // the six legal stages (Arena's competitive list) and a few more by name only
        static readonly string[,] Stages = {
            { "battle_field", "Battlefield" }, { "battle_field_s", "Small Battlefield" }, { "end", "Final Destination" }, { "poke_stadium2", "Pokemon Stadium 2" },
            { "animal_village", "Smashville" }, { "animal_city", "Town and City" }, { "battle_field_l", "Big Battlefield" }, { "dolly_stadium", "Hollow Bastion" },
            { "kalos_pokemon_league", "Kalos Pokemon League" }, { "yoshi_story", "Yoshi's Story" }, { "fox_lylatcruise", "Lylat Cruise" } };
        static Dictionary<ulong, int> fighterIx, stageIx;
        static Dictionary<ulong, string> moveByHash;

        static Dictionary<ulong, int> Index(string[,] t, string prefix)
        {
            var d = new Dictionary<ulong, int>();
            for (int i = 0; i < t.GetLength(0); i++) d[Hash40(prefix + t[i, 0])] = i;
            return d;
        }
        public static string FighterKey(ulong uiChara)
        {
            if (fighterIx == null) fighterIx = Index(Fighters, "ui_chara_");
            return fighterIx.TryGetValue(uiChara & 0xffffffffffUL, out int i) ? Fighters[i, 0] : null;
        }
        public static string FighterName(ulong uiChara, int kind)
        {
            if (fighterIx == null) fighterIx = Index(Fighters, "ui_chara_");
            return fighterIx.TryGetValue(uiChara & 0xffffffffffUL, out int i) ? Fighters[i, 1] : "Fighter " + kind;
        }
        public static string StageKey(ulong uiStage)
        {
            if (stageIx == null) stageIx = Index(Stages, "ui_stage_");
            return stageIx.TryGetValue(uiStage & 0xffffffffffUL, out int i) ? Stages[i, 0] : null;
        }
        public static string StageName(ulong uiStage)
        {
            if (stageIx == null) stageIx = Index(Stages, "ui_stage_");
            return stageIx.TryGetValue(uiStage & 0xffffffffffUL, out int i) ? Stages[i, 1] : "Stage";
        }

        /// <summary>blast zones (left, right, top, bottom) as the game reported them (app::camera::get_dead_area)</summary>
        public static float[] Blast(ReplayState r)
        {
            var d = r.Dead;
            float l = Math.Min(d[0], d[1]), rr = Math.Max(d[0], d[1]), t = Math.Max(d[2], d[3]), b = Math.Min(d[2], d[3]);
            if (rr - l < 50 || t - b < 50) return new[] { -240f, 240f, 200f, -140f };
            return new[] { l, rr, t, b };
        }

        // motion names (the game's own animation names of the common moves) -> the move, as Ultimate Frame Data names it
        static readonly string[,] Moves = {
            { "attack_11", "Jab 1" }, { "attack_12", "Jab 2" }, { "attack_13", "Jab 3" }, { "attack_100", "Rapid Jab" }, { "attack_100_start", "Rapid Jab" }, { "attack_100_end", "Rapid Jab Finisher" },
            { "attack_dash", "Dash Attack" }, { "attack_s3_s", "Forward Tilt" }, { "attack_s3_hi", "Forward Tilt" }, { "attack_s3_lw", "Forward Tilt" }, { "attack_s3_s2", "Forward Tilt" },
            { "attack_hi3", "Up Tilt" }, { "attack_lw3", "Down Tilt" },
            { "attack_s4_s", "Forward Smash" }, { "attack_s4_hi", "Forward Smash" }, { "attack_s4_lw", "Forward Smash" }, { "attack_s4_hold", null }, { "attack_s4_s2", "Forward Smash" },
            { "attack_hi4", "Up Smash" }, { "attack_lw4", "Down Smash" },
            { "attack_air_n", "Neutral Air" }, { "attack_air_f", "Forward Air" }, { "attack_air_b", "Back Air" }, { "attack_air_hi", "Up Air" }, { "attack_air_lw", "Down Air" },
            { "special_n", "Neutral B" }, { "special_air_n", "Neutral B" }, { "special_n_start", "Neutral B" }, { "special_air_n_start", "Neutral B" },
            { "special_s", "Side B" }, { "special_air_s", "Side B" }, { "special_s_start", "Side B" }, { "special_air_s_start", "Side B" },
            { "special_hi", "Up B" }, { "special_air_hi", "Up B" }, { "special_hi_start", "Up B" }, { "special_air_hi_start", "Up B" },
            { "special_lw", "Down B" }, { "special_air_lw", "Down B" }, { "special_lw_start", "Down B" }, { "special_air_lw_start", "Down B" },
            { "catch", "Grab" }, { "catch_dash", "Dash Grab" }, { "catch_turn", "Pivot Grab" }, { "catch_attack", "Pummel" },
            { "throw_f", "Forward Throw" }, { "throw_b", "Backward Throw" }, { "throw_hi", "Up Throw" }, { "throw_lw", "Down Throw" },
            { "escape", "Spot Dodge" }, { "escape_f", "Forward Roll" }, { "escape_b", "Backward Roll" }, { "escape_air", "Air Dodge" }, { "escape_air_slide", "Air Dodge" },
            { "cliff_attack", "Ledge Attack" }, { "down_attack_u", "Getup Attack" }, { "down_attack_d", "Getup Attack" }, { "slip_attack", "Trip Attack" },
            { "final", "Final Smash" }, { "final_air", "Final Smash" } };

        /// <summary>the move a motion is (null: not an attack the stats count)</summary>
        public static string MoveName(ulong motion)
        {
            if (moveByHash == null)
            {
                moveByHash = new Dictionary<ulong, string>();
                for (int i = 0; i < Moves.GetLength(0); i++) moveByHash[Hash40(Moves[i, 0])] = Moves[i, 1];
            }
            return moveByHash.TryGetValue(motion & 0xffffffffffUL, out var s) ? s : null;
        }

        /// <summary>what a fighter is doing when it is not one of the named moves: a few plain words, never a raw id</summary>
        public static string ActionText(RvFighterFrame s)
        {
            string m = MotionText(s.Motion);
            if (m.StartsWith("damage_fly") || m.StartsWith("damage_")) return "Hitstun";
            if (m.StartsWith("guard")) return "Shield";
            if (m.StartsWith("cliff")) return "On the ledge";
            if (m.StartsWith("down_") || m.StartsWith("passive")) return "Knocked down / tech";
            if (m.StartsWith("catch_wait") || m.StartsWith("catch_pull") || m.StartsWith("capture")) return "Grab";
            if (m == "rebirth" || m.StartsWith("entry")) return "Respawn";
            if (m.StartsWith("dead")) return "KO";
            if (m.StartsWith("landing")) return "Landing";
            if (m.StartsWith("jump")) return "Jump";
            if (m.StartsWith("fall")) return "Falling";
            if (m.StartsWith("dash") || m.StartsWith("run") || m.StartsWith("turn")) return "Running";
            if (m.StartsWith("walk")) return "Walking";
            if (m.StartsWith("wait") || m.StartsWith("squat")) return "Standing";
            if (m.StartsWith("escape")) return "Dodge";
            if (Math.Abs(s.X) > 400 || Math.Abs(s.Y) > 400) return "KO";
            return s.Situation == 0 ? "On the ground" : s.Situation == 2 ? "In the air" : "On the ledge";
        }

        static Dictionary<ulong, string> motionNames;
        /// <summary>the motion's name when it is one of the known common ones, else its hash</summary>
        public static string MotionText(ulong motion)
        {
            if (motionNames == null)
            {
                motionNames = new Dictionary<ulong, string>();
                var all = new List<string>();
                for (int i = 0; i < Moves.GetLength(0); i++) all.Add(Moves[i, 0]);
                all.AddRange(new[] { "wait", "wait_2", "wait_3", "wait_4", "walk_slow", "walk_middle", "walk_fast", "dash", "run", "run_brake_l", "run_brake_r", "turn", "turn_run", "turn_dash",
                    "jump_squat", "jump_f", "jump_b", "jump_aerial_f", "jump_aerial_b", "fall", "fall_f", "fall_b", "fall_aerial", "fall_aerial_f", "fall_aerial_b", "fall_special", "landing_light",
                    "landing_heavy", "landing_fall_special", "landing_air_n", "landing_air_f", "landing_air_b", "landing_air_hi", "landing_air_lw", "squat", "squat_wait", "squat_rv",
                    "guard_on", "guard", "guard_off", "guard_damage", "shield_break_fly", "furafura", "damage_hi_1", "damage_hi_2", "damage_hi_3", "damage_n_1", "damage_n_2",
                    "damage_n_3", "damage_lw_1", "damage_lw_2", "damage_lw_3", "damage_air_1", "damage_air_2", "damage_air_3", "damage_fly_hi", "damage_fly_n", "damage_fly_lw",
                    "damage_fly_top", "damage_fly_roll", "damage_fly_meteor", "down_bound_u", "down_bound_d", "down_wait_u", "down_wait_d", "down_stand_u", "down_stand_d",
                    "down_forward_u", "down_back_u", "passive", "passive_stand_f", "passive_stand_b", "passive_wall_jump", "cliff_catch", "cliff_wait", "cliff_climb", "cliff_jump_quick1",
                    "cliff_escape", "catch_wait", "catch_pull", "capture_wait_hi", "capture_damage_hi", "capture_pulled_hi", "entry_r", "entry_l", "rebirth", "appeal_hi_r", "appeal_s_r", "appeal_lw_r",
                    "win_1", "dead_up_star", "dead_up_fall" });
                foreach (var m in all) motionNames[Hash40(m)] = m;
            }
            return motionNames.TryGetValue(motion & 0xffffffffffUL, out var s) ? s : "0x" + (motion & 0xffffffffffUL).ToString("x10");
        }
    }
}
