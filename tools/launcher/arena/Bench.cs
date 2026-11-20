// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.58) THE BENCHMARK: a fixed automatic test the player starts from Settings > Benchmark (MainWindow.Bench.cs). The game
// starts with its own window, walks the menus by itself (an input sequence), plays ONE match it sets up itself (Pokemon
// Stadium 2, Mario as a scripted bot against Fox as a CPU, no items, 9 stocks) and measures (runtime/perf_bench.c and the
// standard rollback bench runtime/rb_bench.c):
//   1. a warm-up (1200 game frames at 3x speed, with rollbacks), then an ONLINE-LIKE segment of 1800 frames at 60 Hz:
//      the other player's pads arrive 1 frame late (the loop test of rb_net.c, the same schedule on every PC), so about
//      280 rollbacks: the rollback tick (ms avg / median / p99), the frame times (present to present) and the game frame's CPU work;
//   2. the same match WITHOUT rollbacks for 1800 frames: frame times (avg FPS, 1% low, p99, max, late frames, stutters),
//      the game frame's CPU work;
//   in both: the render thread's holds for shaders and pipelines made at a draw (the shader compile hitches); and the times
//   of the start (boot = the opening movie), of the match load (select screen confirmed -> fight on). About 2-3 minutes.
// The player's save is not used: a copy of the launcher's base save in build\benchmark\userdata. The player's graphics
// settings (graphics.ini) are used: they are what is being measured, and the summary lists them.
// The results stay on this PC (build\benchmark\last.json) unless the player sends them: anonymously (no account, no
// name, no address; Account.AnonPost with the public key only) to public.perf_bench_add (arena-server
// tools/net/supabase/20261121_perf_bench_anon.sql, NOT applied yet: the button then says the server does not take them).
// SSBU_BENCH_UI=0: no Benchmark section. Test: SSBU_ARENA_BENCH_EXE=<exe in work\> runs another exe.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Arena
{
    public static class Bench
    {
        public const string Stage = "poke_stadium2", StageName = "Pokémon Stadium 2";
        public const int Warm = 1200, Meas = 1800, Match = 1800;
        public static readonly TimeSpan Limit = TimeSpan.FromMinutes(6);      // the run is stopped after this (about 2-3 minutes normally)
        public static readonly TimeSpan NoFightLimit = TimeSpan.FromMinutes(4);      // ... and when the fight has not started by then (75 s here)
        public static string Dir => Path.Combine(Core.Build, "benchmark");
        public static string LastFile => Path.Combine(Dir, "last.json");
        public static string LogFile => Path.Combine(Dir, "benchmark.log");
        static string ResultFile => Path.Combine(Dir, "result.json");
        static string UserData => Path.Combine(Dir, "userdata");
        static string ListFile => Path.Combine(Dir, "match.txt");
        static string DlcFile => Path.Combine(Dir, "dlc_none.txt");

        /// <summary>the game's input sequence from the start to the match (runtime/nnhid.c SSBU_SEQ; the original menus: the
        /// Arena menu is not used), after rb8ms/bench_seq.txt of the standard bench up to its first match; with the base save (Complete,
        /// no DLC) the stage select comes first (its hand on Random: A), then the fighters (300-900 draws: 553 with the DLC)</summary>
        const string Seq = "@movie @draws:36-44/30 30:Z @draws:700-800/40 60:W 60:Z 240:W 60:Z 240:W 20:d/6 40:W 30:Z 300:W @draws:250-330/40 30:Z @draws:340-900/40 " +
                           "30:u/30 20:r/18 30:Z 90 @untilmark:dmsetup1:Z/60";

        public static bool Shown => Environment.GetEnvironmentVariable("SSBU_BENCH_UI") != "0";

        public static string ExePath
        {
            get { string e = Environment.GetEnvironmentVariable("SSBU_ARENA_BENCH_EXE"); return !string.IsNullOrEmpty(e) ? Path.Combine(Core.Work, e) : Core.Exe; }
        }

        static int known = -1;
        /// <summary>null when this install can run it, else why not (for the page)</summary>
        public static string NotPossible()
        {
            if (Core.Repo == null || !File.Exists(ExePath)) return "The game is not installed yet.";
            if (!Core.RbPair) return "The benchmark needs the rollback build of the game (Settings > Game files > Run the setup again).";
            if (Core.RomFS == null) return "The game files were not found (Settings > Game files).";
            if (known < 0)
                try { known = File.ReadAllText(ExePath, Encoding.ASCII).Contains("SSBU_BENCH_OUT") ? 1 : 0; } catch { known = 0; }
            if (known == 0) return "This version of the game has no benchmark yet: update the game first.";
            return null;
        }

        /// <summary>starts the game in benchmark mode; its output goes to build\benchmark\benchmark.log (a hidden cmd.exe, as Launch)</summary>
        public static Process Start()
        {
            Directory.CreateDirectory(Dir);
            Directory.CreateDirectory(Path.Combine(Core.Build, "cache"));
            foreach (var f in new[] { ResultFile, ResultFile + ".tmp" }) try { File.Delete(f); } catch { }
            // a fresh copy of the base save every time: the same menus on every PC, the player's own save never touched
            try { if (Directory.Exists(UserData)) Directory.Delete(UserData, true); } catch { }
            Directory.CreateDirectory(UserData);
            string done = BaseSave.Ensure(UserData);
            if (!File.Exists(Path.Combine(UserData, @"save\save_data\system_data.bin"))) throw new Exception("The benchmark's save could not be made (" + done + ").");
            BaseSave.Complete(UserData);      // everything already given and shown: no notice between the title and the menus
            File.WriteAllText(DlcFile, "# (benchmark) no add-on content: the same menus and no DLC notices on every PC\n");
            File.WriteAllText(ListFile, "p1=mario,p2=fox,stage=" + Stage + ",stock=9,items=off\n");
            var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { WorkingDirectory = Core.Work, UseShellExecute = false, CreateNoWindow = true };
            var env = psi.EnvironmentVariables;
            Core.GameEnv(env);
            env["SSBU_SHOW"] = "1";
            env["SSBU_ONLINE"] = "off";
            env["SSBU_USERDATA"] = UserData;
            env["SSBU_LOOSE_FILES"] = "0"; env["SSBU_COMPETITIVE"] = "0";      // the same game data on every PC
            env["SSBU_REPLAY_MODES"] = ""; env["SSBU_REPLAY_STATE"] = "0";      // nothing recorded
            env["SSBU_SEQ"] = Seq;
            env["SSBU_DLC_LIST"] = DlcFile;      // (runtime/nnmisc.c) no add-on content in the benchmark
            env["SSBU_DEBUG_MATCH_LIST"] = ListFile;
            env["SSBU_BENCH"] = Warm + "," + Meas + ",1";
            env["SSBU_BENCH_FAST"] = "3";
            env["SSBU_BENCH_MATCH"] = Match.ToString();
            env["SSBU_BENCH_OUT"] = ResultFile;
            env["SSBU_DEBUG_END"] = ((Warm + Meas + Match) / 60 + 90) + "s";      // a safety net: the game ends by itself even if the match goes wrong
            env["SSBU_SKIP_MOVIES"] = "1"; env["SSBU_AUDIO_MUTE"] = "1"; env["SSBU_FIGHT_HEALTH"] = "5";
            env["SSBU_RB_NET"] = "loop,3"; env["SSBU_RB_NET_BOT"] = "7"; env["SSBU_RB_NET_BOT_RICH"] = "1";
            env["SSBU_R1_GAMEDLL"] = "ssbu_game_r1.dll"; env["SSBU_NRO_BIN"] = "nro_bin_eh_o2r1";
            psi.Arguments = "/d /c \"\"" + ExePath + "\" \"" + Core.MainElf + "\" 0 > \"" + LogFile + "\" 2>&1\"";
            Updater.Log("benchmark: started (" + Path.GetFileName(ExePath) + ", " + StageName + ")");
            var p = Process.Start(psi);
            p.EnableRaisingEvents = true;
            return p;
        }

        /// <summary>what the game is doing now, from its log (for the progress line)</summary>
        public static string Phase(out double share) => Phase(out share, out _);

        /// <summary>stuck: the opening movie was seen long ago and the fight never started (the menus were not as expected)</summary>
        public static string Phase(out double share, out bool fought)
        {
            share = 0; fought = false;
            string t;
            try
            {
                using (var fs = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > 400000) fs.Seek(-400000, SeekOrigin.End);
                    using (var sr = new StreamReader(fs)) t = sr.ReadToEnd();
                }
            }
            catch { return "Starting the game"; }
            fought = t.Contains("[pbench] fighton");
            if (t.Contains("[pbench] results")) { share = 1; return "Done: closing the game"; }
            if (t.Contains("[pbench] phase 2")) { share = 0.85; return "Measuring: the match without rollbacks"; }
            if (t.Contains("[pbench] phase 1")) { share = 0.65; return "Measuring: online match with rollbacks"; }
            if (t.Contains("[pbench] fighton")) { share = 0.55; return "Warming up the match"; }
            if (t.Contains("[pbench] dmsetup1")) { share = 0.45; return "Loading the match"; }
            if (t.Contains("[pbench] movie")) { share = 0.25; return "Going to the match"; }
            return "Starting the game";
        }

        // ---------------------------------------------------------------- results
        public sealed class Result
        {
            public Dictionary<string, object> Game;          // the game's JSON (runtime/perf_bench.c)
            public Dictionary<string, object> Pc;            // this PC (models only)
            public Dictionary<string, object> Settings;      // what changes the speed
            public string Version, Commit, When;
            public double TotalS;
        }

        static readonly JavaScriptSerializer Js = new JavaScriptSerializer();

        /// <summary>after the game ended: the result (also kept in last.json), or null and why</summary>
        public static Result Collect(double totalS, out string error)
        {
            error = null;
            if (!File.Exists(ResultFile))
            {
                error = "The game ended without results. The last lines of build\\benchmark\\benchmark.log:\n" + string.Join("\n", Core.LogTail(6, LogFile));
                return null;
            }
            try
            {
                var g = Js.Deserialize<Dictionary<string, object>>(File.ReadAllText(ResultFile));
                var r = new Result
                {
                    Game = g, Pc = PcInfo(), Settings = SettingsInfo(g), TotalS = Math.Round(totalS, 1),
                    Version = Updater.Current.ToString(3), Commit = Updater.Installed ? Updater.InstalledCommit() : null,
                    When = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                };
                if (r.Commit != null && r.Commit.Length > 10) r.Commit = r.Commit.Substring(0, 10);
                File.WriteAllText(LastFile, Js.Serialize(r), new UTF8Encoding(false));
                Updater.Log("benchmark: done in " + r.TotalS + " s: " + Summary(r).Replace("\n", " | "));
                return r;
            }
            catch (Exception e) { error = "The results could not be read: " + e.Message; return null; }
        }

        public static Result Last()
        {
            try { return File.Exists(LastFile) ? Js.Deserialize<Result>(File.ReadAllText(LastFile)) : null; } catch { return null; }
        }

        // the game's numbers
        public static double Num(Dictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out var v) || v == null) return double.NaN;
            try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch { return double.NaN; }
        }
        public static Dictionary<string, object> Sec(Result r, string name) => r?.Game != null && r.Game.TryGetValue(name, out var v) ? v as Dictionary<string, object> : null;
        public static string F(double v, string fmt = "0.0") => double.IsNaN(v) || v < 0 ? "–" : v.ToString(fmt, CultureInfo.InvariantCulture);
        static string S(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        // ---------------------------------------------------------------- this PC (models only: nothing that names the player or the PC)
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetPhysicallyInstalledSystemMemory(out long kb);

        static string Reg(string key, string name)
        {
            try { using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key)) return k?.GetValue(name)?.ToString(); } catch { return null; }
        }

        static Dictionary<string, object> PcInfo()
        {
            var pc = new Dictionary<string, object>();
            pc["cpu"] = Regex.Replace(Reg(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "?", @"\s+", " ").Trim();
            pc["cpu_threads"] = Environment.ProcessorCount;
            try
            {
                int cores = 0;
                using (var q = new System.Management.ManagementObjectSearcher("SELECT NumberOfCores FROM Win32_Processor"))
                    foreach (var o in q.Get()) cores += Convert.ToInt32(o["NumberOfCores"]);
                if (cores > 0) pc["cpu_cores"] = cores;
            }
            catch { }
            try { if (GetPhysicallyInstalledSystemMemory(out long kb)) pc["ram_gb"] = Math.Round(kb / 1048576.0, 1); } catch { }
            // the GPU the game used (its log), and that GPU's driver (Windows)
            string gpu = null;
            try { var m = Regex.Match(File.ReadAllText(LogFile), @"nvn_vk: using device \d+: ([^(\r\n]+)"); if (m.Success) gpu = m.Groups[1].Value.Trim(); } catch { }
            string driver = null, any = null, anyDriver = null;
            try
            {
                using (var q = new System.Management.ManagementObjectSearcher("SELECT Name, DriverVersion FROM Win32_VideoController"))
                    foreach (var o in q.Get())
                    {
                        string n = o["Name"]?.ToString(), d = o["DriverVersion"]?.ToString();
                        if (any == null) { any = n; anyDriver = d; }
                        if (gpu != null && n != null && (n.IndexOf(gpu, StringComparison.OrdinalIgnoreCase) >= 0 || gpu.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)) driver = d;
                    }
            }
            catch { }
            pc["gpu"] = gpu ?? any ?? "?";
            pc["gpu_driver"] = driver ?? (gpu == null ? anyDriver : null);
            const string nt = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            string build = Reg(nt, "CurrentBuild"), ubr = Reg(nt, "UBR");
            pc["os"] = "Windows " + Environment.OSVersion.Version.Major + "." + Environment.OSVersion.Version.Minor + "." + (build ?? Environment.OSVersion.Version.Build.ToString()) + (ubr != null ? "." + ubr : "");
            return pc;
        }

        static Dictionary<string, object> SettingsInfo(Dictionary<string, object> g)
        {
            var G = Core.ReadIni(Path.Combine(Core.Cfg, "graphics.ini"));
            string V(string k, string def) => G.TryGetValue(k, out var v) && v != "" ? v : def;
            var s = new Dictionary<string, object>
            {
                { "renderer", V("renderer", "vulkan") }, { "window", V("window", "windowed") }, { "width", V("width", "1920") }, { "height", V("height", "1080") },
                { "resolution_scale", V("resolution_scale", "1.0") }, { "vsync", V("vsync", "auto") }, { "profile", V("profile", "custom") },
                { "anisotropic_filtering", V("anisotropic_filtering", "game") }, { "shader_load", V("shader_load", "start") }, { "shader_wait", V("shader_wait", "hold") },
                { "stage_bg", new[] { "original", "black", "custom" }[Math.Max(0, Math.Min(2, Core.StageBg))] },
                { "frame_limit", "60" },      // (the game's own: always 60 frames per second)
            };
            double hz = Num(g, "display_hz");
            if (!double.IsNaN(hz) && hz > 0) s["display_hz"] = Math.Round(hz, 2);
            return s;
        }

        // ---------------------------------------------------------------- text
        public static string Summary(Result r)
        {
            var m = Sec(r, "match"); var o = Sec(r, "rollback"); var g = r.Game;
            var sb = new StringBuilder();
            sb.Append("Match: " + F(Num(m, "fps_avg"), "0.0") + " fps avg, 1% low " + F(Num(m, "fps_low1"), "0.0") + " fps, frame time p99 " + F(Num(m, "ft_p99"), "0.0") + " ms, game frame " + F(Num(m, "cpu_ms_avg"), "0.00") + " ms CPU\n");
            sb.Append("Online with rollbacks: " + F(Num(o, "fps_avg"), "0.0") + " fps avg, 1% low " + F(Num(o, "fps_low1"), "0.0") + " fps, " + F(Num(o, "rollbacks"), "0") + " rollbacks, rollback tick " + F(Num(o, "rb_ms_avg"), "0.00") + " ms avg / " + F(Num(o, "rb_ms_p99"), "0.00") + " ms p99\n");
            sb.Append("Shader hitches: " + F(Num(g, "shader_holds_fight"), "0") + " (" + F(Num(g, "shader_hold_ms_fight"), "0") + " ms); start " + F(Num(g, "boot_s"), "0.0") + " s, match load " + F(Num(g, "match_load_s"), "0.0") + " s");
            return sb.ToString();
        }

        /// <summary>the text of "Copy results"</summary>
        public static string CopyText(Result r)
        {
            var m = Sec(r, "match"); var o = Sec(r, "rollback"); var g = r.Game; var pc = r.Pc; var st = r.Settings;
            var sb = new StringBuilder();
            sb.AppendLine("SSBU Arena benchmark " + r.When + " (launcher " + r.Version + (r.Commit != null ? ", game " + r.Commit : "") + ")");
            sb.AppendLine("PC: " + S(pc, "cpu") + " (" + (S(pc, "cpu_cores") != null ? S(pc, "cpu_cores") + " cores / " : "") + S(pc, "cpu_threads") + " threads), " + S(pc, "ram_gb") + " GB RAM, " + S(pc, "gpu") + (S(pc, "gpu_driver") != null ? " (driver " + S(pc, "gpu_driver") + ")" : "") + ", " + S(pc, "os"));
            sb.AppendLine("Settings: " + S(st, "renderer") + ", " + S(st, "width") + "x" + S(st, "height") + " " + S(st, "window") + ", scale " + S(st, "resolution_scale") + ", vsync " + S(st, "vsync") +
                          (S(st, "display_hz") != null ? " (display " + S(st, "display_hz") + " Hz)" : "") + ", profile " + S(st, "profile") + ", stage background " + S(st, "stage_bg"));
            sb.AppendLine("Test: " + StageName + ", Mario (bot) vs Fox (CPU), no items, " + F(r.TotalS, "0") + " s in all");
            sb.AppendLine("Match without rollbacks: " + F(Num(m, "fps_avg"), "0.00") + " fps avg, 1% low " + F(Num(m, "fps_low1"), "0.00") + " fps, frame time avg " + F(Num(m, "ft_avg"), "0.00") + " / p99 " + F(Num(m, "ft_p99"), "0.00") + " / max " + F(Num(m, "ft_max"), "0.0") +
                          " ms, late frames " + F(Num(m, "late_pct"), "0.00") + " %, stutters " + F(Num(m, "stutters"), "0") + "; game frame CPU " + F(Num(m, "cpu_ms_avg"), "0.00") + " ms avg / " + F(Num(m, "cpu_ms_p99"), "0.00") + " ms p99");
            sb.AppendLine("Online match with rollbacks: " + F(Num(o, "fps_avg"), "0.00") + " fps avg, 1% low " + F(Num(o, "fps_low1"), "0.00") + " fps, frame time p99 " + F(Num(o, "ft_p99"), "0.00") + " ms, late frames " + F(Num(o, "late_pct"), "0.00") + " %; " +
                          F(Num(o, "rollbacks"), "0") + " rollbacks, rollback tick " + F(Num(o, "rb_ms_avg"), "0.00") + " ms avg / " + F(Num(o, "rb_ms_med"), "0.00") + " median / " + F(Num(o, "rb_ms_p99"), "0.00") + " p99, over one frame " + F(Num(o, "rb_over_pct"), "0.00") + " %; normal frame CPU " + F(Num(o, "cpu_ms_avg"), "0.00") + " ms");
            sb.AppendLine("Shader compile hitches in the fight: " + F(Num(g, "shader_holds_fight"), "0") + " (" + F(Num(g, "shader_hold_ms_fight"), "0") + " ms in all)");
            sb.AppendLine("Loading: game start " + F(Num(g, "boot_s"), "0.0") + " s, match load " + F(Num(g, "match_load_s"), "0.0") + " s, start to fight " + F(Num(g, "to_fight_s"), "0.0") + " s");
            return sb.ToString();
        }

        // ---------------------------------------------------------------- the anonymous upload
        /// <summary>exactly what "Send" sends (public.perf_bench_add's p): the PC's models, the settings, the numbers; no account, no
        /// name, no path, no address</summary>
        public static Dictionary<string, object> Payload(Result r)
        {
            var m = Sec(r, "match"); var o = Sec(r, "rollback"); var g = r.Game; var pc = r.Pc;
            object N(Dictionary<string, object> d, string k, int dec = 3) { double v = Num(d, k); return double.IsNaN(v) ? null : (object)Math.Round(v, dec); }
            var bench = new Dictionary<string, object>
            {
                { "format", 1 }, { "stage", S(g, "stage") }, { "total_s", r.TotalS }, { "boot_s", N(g, "boot_s", 2) }, { "match_load_s", N(g, "match_load_s", 2) }, { "to_fight_s", N(g, "to_fight_s", 2) },
                { "cpu_cores", pc.ContainsKey("cpu_cores") ? pc["cpu_cores"] : null },
                { "match", Pick(m, "fps_avg", "fps_low1", "ft_avg", "ft_p50", "ft_p95", "ft_p99", "ft_max", "late_pct", "stutters", "shader_holds", "shader_hold_ms", "cpu_ms_avg", "cpu_ms_med", "cpu_ms_p99", "rollbacks") },
                { "rollback", Pick(o, "fps_avg", "fps_low1", "ft_avg", "ft_p50", "ft_p95", "ft_p99", "ft_max", "late_pct", "stutters", "shader_holds", "shader_hold_ms", "cpu_ms_avg", "cpu_ms_med", "cpu_ms_p99",
                                   "rollbacks", "rb_ms_avg", "rb_ms_med", "rb_ms_p99", "rb_over_pct", "rb_load_ms", "rb_rerun_ms") },
            };
            var p = new Dictionary<string, object>
            {
                { "version", r.Version }, { "game_commit", r.Commit }, { "os", S(pc, "os") }, { "cpu", S(pc, "cpu") }, { "cpu_threads", pc.ContainsKey("cpu_threads") ? pc["cpu_threads"] : null },
                { "ram_gb", pc.ContainsKey("ram_gb") ? pc["ram_gb"] : null }, { "gpu", S(pc, "gpu") }, { "gpu_driver", S(pc, "gpu_driver") }, { "renderer", S(r.Settings, "renderer") },
                { "display_hz", Num(g, "display_hz") > 0 ? N(g, "display_hz", 2) : null }, { "width", int.TryParse(S(r.Settings, "width"), out int w) ? (object)w : null }, { "height", int.TryParse(S(r.Settings, "height"), out int h) ? (object)h : null },
                { "settings", r.Settings },
                { "session_s", r.TotalS }, { "fight_s", Math.Round((Warm / 3.0 + Meas + 60 + Match) / 60.0, 1) }, { "frames", N(m, "frames", 0) },
                { "ft_p50", N(m, "ft_p50") }, { "ft_p95", N(m, "ft_p95") }, { "ft_p99", N(m, "ft_p99") }, { "ft_max", N(m, "ft_max") }, { "late_pct", N(m, "late_pct") }, { "stutters", N(m, "stutters", 0) },
                { "hist", m != null && m.TryGetValue("hist", out var hs) ? hs : null },
                { "shader_compiles", N(g, "shader_holds_fight", 0) }, { "shader_hitch_ms", N(g, "shader_hold_ms_fight", 1) },
                { "rb_fights", 1 }, { "rb_rollbacks", N(o, "rollbacks", 0) }, { "rb_tick_p50", N(o, "rb_ms_med") }, { "rb_tick_p99", N(o, "rb_ms_p99") }, { "rb_over_pct", N(o, "rb_over_pct") },
                { "crashed", false }, { "bench", bench },
            };
            return p;
        }

        static Dictionary<string, object> Pick(Dictionary<string, object> d, params string[] keys)
        {
            var o = new Dictionary<string, object>();
            if (d == null) return o;
            foreach (var k in keys) { double v = Num(d, k); if (!double.IsNaN(v)) o[k] = Math.Round(v, 3); }
            return o;
        }

        public static string PayloadJson(Result r) => Js.Serialize(new Dictionary<string, object> { { "p", Payload(r) }, { "p_product", "arena" } });

        /// <summary>sends it with the public key only (no session): null when it was taken, else the reason</summary>
        public static string Send(Result r)
        {
            try
            {
                string id = Account.AnonPost("/rest/v1/rpc/perf_bench_add", PayloadJson(r));
                Updater.Log("benchmark: results sent anonymously (row " + id.Trim() + ")");
                return null;
            }
            catch (Account.ArenaHttpException e)
            {
                Updater.Log("benchmark: results not sent: " + e.Status + " " + e.Body);
                if (e.Status == 404 || e.Body.Contains("PGRST202") || e.Body.Contains("perf_bench_add")) return "The Arena server does not take benchmark results yet. Your results are still here: use Copy results to share them.";
                if (e.Body.Contains("too many")) return "The Arena server has taken many benchmark results today: try again tomorrow.";
                return "The Arena server refused them (" + e.Status + ").";
            }
            catch (Exception e) { Updater.Log("benchmark: results not sent: " + e.Message); return "The Arena server could not be reached: " + e.Message; }
        }
    }
}
