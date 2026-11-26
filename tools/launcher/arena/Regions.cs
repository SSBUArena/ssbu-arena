// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena's lobby servers by region (the C# copy of the table; the game has the same one in runtime/net_region.h and
// the PowerShell window in tools/launcher/Launcher.ps1: tools/net/test_regions.py checks that the three match).
// Every lobby is ours, relay only, with the same accounts and results database: one account plays on all of them, but
// players on different lobbies do not see each other (rooms and matchmaking are per lobby).
//
// The setting (graphics.ini, read by the launcher and by the game; SSBU_ONLINE_REGION wins over the file):
//     online_region = auto | eu | na-east | sa   auto (default): the lobby with the lowest measured round trip
//     online_region_last = eu | na-east | sa     the last measured pick: what Auto starts with, measured again in the background
// An explicit server (SSBU_ONLINE_SERVER, or online_server = host:port in launcher.ini) wins: that server, nothing measured.
// SSBU_ONLINE_REGION_PROBE=0: nothing is measured, Auto = the first region (Europe, as 0.2.50 and older).
// The measure: the lobby answers a PING only to a game that has joined, so each probe is a JOIN without a pass, which the
// lobby refuses at once with a REJECT carrying the probe's nonce (no place taken): the time to that answer is the round
// trip. Samples probes per lobby, all lobbies at once, the median of each. Only names here, never addresses.
// (23/11, South America) Auto never plays on a lobby that did not answer: a name that does not resolve (yet), a network
// that cannot reach it, no answer in TimeoutMs = no answer. PLAY with Auto waits for this run's measure (SettleForPlay).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Arena
{
    public static class Regions
    {
        public const int Port = 39010;
        public const string NA_EAST_HOST = "na-east.ssbuarena.com";
        // REGION TABLE (id, name, host): the first one is the default and the fallback
        public static readonly (string Id, string Name, string Host)[] Table = {
            ("eu", "Europe", "eu-germany.ssbuarena.com"),
            ("na-east", "NA East", NA_EAST_HOST),
            ("sa", "South America", "sa-brazil.ssbuarena.com"),      // (23/11) Sao Paulo
        };
        public const int KeepMs = 10;          // Auto keeps its last pick unless another lobby is more than this much closer
        public const int Samples = 3;          // probes per lobby, GapMs apart, all lobbies at once
        public const int GapMs = 60;
        public const int TimeoutMs = 700;      // the whole measure, at most
        public const int LookupMs = 1500;      // (sa) the name look-ups, all at once: one that does not resolve in time = no answer
        public const int PlayWaitMs = 2500;    // (sa) PLAY with Auto before the start's measure ended: wait at most this long

        /// <summary>the saved choice: -1 = auto, else a place in Table (graphics.ini online_region)</summary>
        public static int Setting = -1;
        /// <summary>the last measured pick (graphics.ini online_region_last), -1 = none yet</summary>
        public static int Last = -1;
        /// <summary>the last measure of this run: each lobby's median round trip in ms, -1 = no answer; null = not measured yet</summary>
        public static int[] Ms;
        /// <summary>online_server of launcher.ini: a server set by hand (kept as it is), null = none</summary>
        public static string IniServer;

        static string Env(string name) { var v = Environment.GetEnvironmentVariable(name); return string.IsNullOrWhiteSpace(v) ? null : v.Trim(); }
        static bool Off(string v) { return v == "0" || string.Equals(v, "off", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "no", StringComparison.OrdinalIgnoreCase) || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase); }

        /// <summary>"eu" / "na-east" / "sa" -> its place in Table; -1: auto, empty or not a region</summary>
        public static int ById(string id)
        {
            for (int i = 0; i < Table.Length; i++) if (string.Equals(Table[i].Id, (id ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        /// <summary>"host" or "host:port" of one of our lobbies -> its place; -1: another server</summary>
        public static int ByHost(string server)
        {
            string h = (server ?? "").Trim();
            int c = h.LastIndexOf(':');
            if (c >= 0) h = h.Substring(0, c);
            for (int i = 0; i < Table.Length; i++) if (string.Equals(Table[i].Host, h, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        public static string Id(int i) { return i < 0 ? "auto" : Table[i].Id; }
        public static string Address(int i) { return Table[i].Host + ":" + Port; }

        /// <summary>a server set by hand (it wins over the region), null = none</summary>
        public static string Override => Env("SSBU_ONLINE_SERVER") ?? IniServer;
        public static bool Probe { get { var v = Env("SSBU_ONLINE_REGION_PROBE"); return v == null || !Off(v); } }
        /// <summary>the choice in use: SSBU_ONLINE_REGION wins over the saved one; -1 = auto</summary>
        public static int Choice { get { var e = Env("SSBU_ONLINE_REGION"); return e != null ? ById(e) : Setting; } }
        public static bool Auto => Override == null && Choice < 0;
        /// <summary>the region the game is started on now: the choice, or Auto's last measured pick (none yet: the first)</summary>
        public static int Pick
        {
            get
            {
                int c = Choice;
                if (c >= 0) return c;
                if (!Probe) return 0;
                if (Ms == null) return Last >= 0 && !unsettled ? Last : 0;      // (sa) PLAY found no measure in time: the first region
                int p = Last >= 0 ? Last : 0;
                return Ms[p] >= 0 || !Ms.Any(m => m >= 0) ? p : Choose(Ms, -1);     // (sa) never one that did not answer while another did
            }
        }
        /// <summary>the lobby the game is started with (host:port)</summary>
        public static string Server => Override ?? Address(Pick);
        /// <summary>"Auto (Europe)", "Europe", or the server set by hand</summary>
        public static string Text => Override ?? (Auto ? "Auto (" + Table[Pick].Name + ")" : Table[Pick].Name);

        /// <summary>The pick from one measure: the lowest median wins; the last pick stays while it answers and no lobby is
        /// more than KeepMs closer; nobody answered: the last pick, else the first region.</summary>
        public static int Choose(int[] ms, int last)
        {
            int best = -1;
            for (int i = 0; i < Table.Length; i++) if (ms[i] >= 0 && (best < 0 || ms[i] < ms[best])) best = i;
            if (best < 0) return last >= 0 && last < Table.Length ? last : 0;
            if (last >= 0 && last < Table.Length && ms[last] >= 0 && ms[last] <= ms[best] + KeepMs) return last;
            return best;
        }

        /// <summary>Each lobby's median round trip in ms (-1 = no answer). Blocks: the name look-ups, then at most TimeoutMs.</summary>
        public static int[] Measure()
        {
            int n = Table.Length;
            var ms = Enumerable.Repeat(-1, n).ToArray();
            var socks = new Socket[n];
            var sent = new Dictionary<ulong, double>[n];
            var rtt = new List<double>[n];
            int left = 0, next = 0;
            var rng = RandomNumberGenerator.Create();
            try
            {
                // (sa) all names at once, LookupMs at most: a name that does not resolve (yet) never holds the others back
                var look = Table.Select(r => Task.Run(() => Dns.GetHostAddresses(r.Host))).ToArray();
                // (fix/install-launcher-errors) a look-up that fails after the wait below (no internet: "host unknown") is observed
                foreach (var l in look) l.ContinueWith(x => { var _ = x.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                try { Task.WaitAll(look, LookupMs); } catch (AggregateException) { }
                for (int i = 0; i < n; i++)
                {
                    sent[i] = new Dictionary<ulong, double>(); rtt[i] = new List<double>();
                    try
                    {
                        var ip = look[i].Status == TaskStatus.RanToCompletion ? look[i].Result.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) : null;
                        if (ip == null) continue;
                        socks[i] = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        socks[i].Connect(new IPEndPoint(ip, Port));
                        left += Samples;
                    }
                    catch { socks[i] = null; }
                }
                var clock = Stopwatch.StartNew();
                var buf = new byte[256];
                while (left > 0 && clock.Elapsed.TotalMilliseconds <= TimeoutMs)
                {
                    if (next < Samples && clock.Elapsed.TotalMilliseconds >= next * GapMs)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            if (socks[i] == null) continue;
                            // JOIN (runtime/net_relay.c send_join) without a pass: 'S' 'U' 1 1 | nonce u64 | code[32] | name[32] | build[40] | lan ip, port, direct | "CV" level u16
                            var p = new byte[4 + 8 + 32 + 32 + 40 + 7 + 4];
                            p[0] = (byte)'S'; p[1] = (byte)'U'; p[2] = 1; p[3] = 1;
                            var nb = new byte[8]; rng.GetBytes(nb);
                            Array.Copy(nb, 0, p, 4, 8);
                            var tag = Encoding.ASCII.GetBytes("region-probe");
                            Array.Copy(tag, 0, p, 12, tag.Length); Array.Copy(tag, 0, p, 44, tag.Length);
                            var build = Encoding.ASCII.GetBytes("ssbu-recomp-ldn1");
                            Array.Copy(build, 0, p, 76, build.Length);
                            p[123] = (byte)'C'; p[124] = (byte)'V'; p[125] = 0; p[126] = 3;
                            sent[i][BitConverter.ToUInt64(nb, 0)] = clock.Elapsed.TotalMilliseconds;
                            try { socks[i].Send(p); } catch { }
                        }
                        next++;
                    }
                    var read = socks.Where(s => s != null).ToList();
                    if (read.Count == 0) break;
                    Socket.Select(read, null, null, 5000);
                    foreach (var s in read)
                    {
                        int i = Array.IndexOf(socks, s), got;
                        try { got = s.Receive(buf); } catch { continue; }      // (an ICMP "port unreachable" comes back as an error)
                        double t = clock.Elapsed.TotalMilliseconds;
                        if (got < 12 || buf[0] != 'S' || buf[1] != 'U' || buf[2] != 1 || (buf[3] != 3 && buf[3] != 2)) continue;      // REJECT (3) or WELCOME (2)
                        ulong nonce = BitConverter.ToUInt64(buf, 4);
                        if (!sent[i].TryGetValue(nonce, out double at)) continue;
                        sent[i].Remove(nonce);
                        rtt[i].Add(t - at); left--;
                        if (buf[3] == 2 && got >= 24)      // a lobby without accounts took the probe in: it leaves at once (LEAVE = 7, its cookie)
                        {
                            var q = new byte[12];
                            q[0] = (byte)'S'; q[1] = (byte)'U'; q[2] = 1; q[3] = 7;
                            Array.Copy(buf, 16, q, 4, 8);
                            try { s.Send(q); } catch { }
                        }
                    }
                }
                for (int i = 0; i < n; i++)
                {
                    if (rtt[i].Count == 0) continue;
                    rtt[i].Sort();
                    ms[i] = (int)(rtt[i][(rtt[i].Count - 1) / 2] + 0.5);
                }
            }
            finally { foreach (var s in socks) if (s != null) try { s.Close(); } catch { } }
            return ms;
        }

        static int measuring;
        static Task<int[]> running;      // (sa) the measure in flight
        static bool unsettled;           // (sa) PLAY found no measure in time
        /// <summary>The measure in the background (nothing when a server is set by hand, the region is chosen, or the probe is
        /// off). done(changed) runs on the caller's context: changed = Auto's pick is another one now (Last is set).</summary>
        public static void MeasureAsync(Action<bool> done)
        {
            if (!Auto || !Probe || System.Threading.Interlocked.Exchange(ref measuring, 1) == 1) return;
            running = Task.Run(() => Measure());
            running.ContinueWith(t =>
            {
                measuring = 0;
                if (t.Status != TaskStatus.RanToCompletion) return;
                done?.Invoke(Apply(t.Result));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        /// <summary>a measure's result kept: Ms, and Auto's pick in Last; true = the pick is another one now. Once per measure
        /// (SettleForPlay may have taken it already: then nothing changes the second time).</summary>
        static bool Apply(int[] ms)
        {
            if (ReferenceEquals(Ms, ms)) return false;
            Ms = ms; unsettled = false;
            bool any = Ms.Any(m => m >= 0);
            int pick = Choose(Ms, Last);
            bool changed = any && pick != Last;
            if (changed) Last = pick;
            Updater.Log("regions: " + MsText() + "; Auto picks " + Table[any ? pick : Pick].Name + (any ? "" : " (nobody answered: kept)"));
            return changed;
        }
        /// <summary>(sa) PLAY with Auto: the lobby must be one that answered in this run. The start's measure not ended yet:
        /// wait for it (PlayWaitMs at most; none running: one now); it ended with no answer at all (the network was down?):
        /// measured once more now. Still no measure: the first region for this PLAY (Pick). Nobody answers even now: the last
        /// pick (every lobby is as unreachable as the others). true = Auto's pick changed (the caller saves online_region_last).</summary>
        public static bool SettleForPlay()
        {
            if (!Auto || !Probe || (Ms != null && Ms.Any(m => m >= 0))) return false;
            var t = (Ms == null ? running : null) ?? Task.Run(() => Measure());
            bool ended = false;
            try { ended = t.Wait(PlayWaitMs); } catch (AggregateException) { }
            if (ended && t.Status == TaskStatus.RanToCompletion) return Apply(t.Result);
            unsettled = true;
            Updater.Log("regions: PLAY before the measure ended: " + Table[Pick].Name);
            return false;
        }
        /// <summary>"Europe 28 ms, NA East 104 ms" (the last measure; "" before it)</summary>
        public static string MsText()
        {
            if (Ms == null) return "";
            return string.Join(", ", Table.Select((r, i) => r.Name + (Ms[i] >= 0 ? " " + Ms[i] + " ms" : " no answer")));
        }
    }
}
