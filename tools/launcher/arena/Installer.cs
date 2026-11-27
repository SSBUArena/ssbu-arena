// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena setup, inside the launcher (MainWindow.Setup.cs shows it): the work the PowerShell installer window
// (scripts\installer_window.ps1, still used by Install SSBU.bat) does, without its window.
//   1. tools: scripts\bootstrap.ps1 -NoLaunch puts the private Python into deps\python (hidden; its lines are shown)
//   2. game files: scripts\find_inputs.py --json finds prod.keys, the game, its update, a dump, a firmware, the DLC;
//      scripts\precheck.py --json checks the PC (disk, memory, Vulkan, codecs)
//   3. scripts\install.py --no-gui --progress-json with the chosen files: "##ssbu {json}" progress lines (install.py's
//      schema: plan, step, progress, done, error), every line also in <build>\logs\installer.log
// A dump folder already decrypted on the console (exefs\ and romfs\, e.g. from nxdumptool) needs no prod.keys.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Arena
{
    /// <summary>what find_inputs.py found: the best of each, plus the counts the page shows</summary>
    public sealed class Found
    {
        public string Keys, Base, Update, Build, Dump, Firmware;
        public List<string> Dlc = new List<string>();
        public List<string> Notes = new List<string>();
        public int KeysSeen, BaseSeen, UpdatesSeen;
    }

    /// <summary>one line of precheck.py: level ok | warn | bad</summary>
    public sealed class PcCheck
    {
        public string Level, What, Msg;
    }

    /// <summary>the choices on the setup page, turned into install.py's arguments</summary>
    public sealed class SetupChoice
    {
        public string Game, Update, Keys, Firmware, Location;
        public List<string> Dlc = new List<string>();
        public int Jobs;
        public bool Dry;
        /// <summary>install.py --shared-data: an OpenSmash Ultimate folder or "auto" (its game files, no copy of our own), "off"
        /// (Arena's own copy), null (as the install already is)</summary>
        public string Shared;
    }

    public static class Installer
    {
        public static string Root => Core.Repo ?? Path.GetDirectoryName(Updater.ExePath);
        public static string DefaultLocation => Path.Combine(Root, "build");

        /// <summary>the private Python (bootstrap.ps1), else the one it fell back to (build\python_path.txt)</summary>
        public static string Python
        {
            get
            {
                string p = Path.Combine(Root, @"deps\python\python.exe");
                if (File.Exists(p)) return p;
                try
                {
                    string f = Path.Combine(Root, @"build\python_path.txt");
                    string q = File.Exists(f) ? File.ReadAllLines(f).FirstOrDefault()?.Trim() : null;
                    if (!string.IsNullOrEmpty(q) && File.Exists(q)) return q;
                }
                catch { }
                return null;
            }
        }

        /// <summary>a dump folder: exefs\ and romfs\ inside, already decrypted</summary>
        public static bool IsDump(string p) =>
            !string.IsNullOrEmpty(p) && Directory.Exists(Path.Combine(p, "exefs")) && Directory.Exists(Path.Combine(p, "romfs"));

        static ProcessStartInfo Hidden(string exe, string args)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            ProcessEnv.Set(psi, "PYTHONIOENCODING", "utf-8");      // (fix/install-launcher-errors) "Path" and "PATH" both in the environment threw here
            return psi;
        }

        /// <summary>the log file of a hidden process, written while another program may read it. (fix/install-launcher-errors) A log
        /// still held open (by an earlier run of this launcher that did not start, an editor, an antivirus) is tried again for
        /// a few seconds, then written beside it as name-2.log, name-3.log instead of stopping the setup.</summary>
        internal static StreamWriter OpenLog(string log)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            string dir = Path.GetDirectoryName(log), name = Path.GetFileNameWithoutExtension(log), ext = Path.GetExtension(log);
            for (int n = 1; ; n++)
            {
                string f = n == 1 ? log : Path.Combine(dir, name + "-" + n + ext);
                try
                {
                    FileStream fs = null;
                    FileRetry.Do(() => fs = new FileStream(f, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), n == 1 ? 6 : 1, 500);
                    return new StreamWriter(fs, new UTF8Encoding(true)) { AutoFlush = true };
                }
                catch (IOException e) when (SetupErrors.InUse(e) && n < 4) { }
            }
        }

        /// <summary>a hidden process; every line to line() and to the log file; exited(code) at the end</summary>
        static Process Start(ProcessStartInfo psi, string log, Action<string> line, Action<int> exited)
        {
            var w = OpenLog(log);
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            DataReceivedEventHandler on = (s, e) =>
            {
                if (e.Data == null) return;
                lock (w) try { w.WriteLine(e.Data); } catch (ObjectDisposedException) { }
                line?.Invoke(e.Data);
            };
            p.OutputDataReceived += on;
            p.ErrorDataReceived += on;
            p.Exited += (s, e) => { p.WaitForExit(); lock (w) w.Dispose(); exited?.Invoke(p.ExitCode); };
            try { p.Start(); }
            catch (Exception ex)
            {
                // (fix/install-launcher-errors) the log was left open when the process could not start, and every Resume after that
                // failed on "the file is used by another process" (this launcher itself)
                lock (w) { try { w.WriteLine("could not start " + psi.FileName + ": " + ex.Message); } catch { } w.Dispose(); }
                throw;
            }
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        /// <summary>the release's sources are beside the exe (FirstInstall unpacked them): the scripts the setup runs</summary>
        public static bool SourcesThere =>
            File.Exists(Path.Combine(Root, @"scripts\bootstrap.ps1")) && File.Exists(Path.Combine(Root, @"scripts\install.py")) && File.Exists(Path.Combine(Root, @"scripts\find_inputs.py"));

        /// <summary>(fix/setup-0278) the release's files this install lacks: build\update_manifest.json's list (Updater.FirstInstall and
        /// scripts/update.py write it: every file of the release's sources with its SHA-256) against the files there. Paths with '/'
        /// as in the manifest; never build\, deps\, config\, saves\, userdata*\ or .git\ (update.py's PROTECTED). Empty when the
        /// manifest is not there or cannot be read (a git checkout, an install from before the manifest).</summary>
        public static List<string> MissingTree()
        {
            var miss = new List<string>();
            try
            {
                var files = ManifestFiles();
                if (files == null) return miss;
                foreach (var rel in files.Keys)
                {
                    string first = rel.Split('/')[0].ToLowerInvariant();
                    if (first == ".git" || first == "build" || first == "deps" || first == "config" || first == "saves" || first.StartsWith("userdata") ||
                        first == "build_location.txt" || rel.Contains("..") || rel.Contains(":")) continue;
                    if (!File.Exists(Path.Combine(Root, rel.Replace('/', '\\')))) miss.Add(rel);
                }
            }
            catch { }
            return miss;
        }

        /// <summary>build\update_manifest.json's "files" (path -> SHA-256), or null</summary>
        public static Dictionary<string, object> ManifestFiles() => Manifest()?.TryGetValue("files", out var f) == true ? f as Dictionary<string, object> : null;

        /// <summary>build\update_manifest.json (repo, commit, zip_top, files), or null</summary>
        public static Dictionary<string, object> Manifest()
        {
            try
            {
                string m = Path.Combine(Root, @"build\update_manifest.json");
                return File.Exists(m) ? new JavaScriptSerializer { MaxJsonLength = int.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(m)) : null;
            }
            catch { return null; }
        }

        /// <summary>what a Windows PowerShell exit code says (its console host's own codes), or null</summary>
        public static string PowerShellCode(int code)
        {
            switch (unchecked((uint)code))
            {
                case 0xFFFD0000: return @"PowerShell could not open scripts\bootstrap.ps1: the file is missing or blocked (an antivirus may have removed it)";
                case 0xFFFF0000: return "Windows PowerShell could not start on this PC (it is blocked or damaged)";
                case 0xFFFE0000: return "PowerShell was stopped";
                default: return null;
            }
        }

        /// <summary>step 1: the private Python (bootstrap.ps1 -NoLaunch: download, SHA-256 check, pip packages); nothing
        /// to do when it is already there. Blocks; returns the Python or throws.</summary>
        public static string PrepareTools(Action<string> line)
        {
            if (Python != null && File.Exists(Path.Combine(Root, @"deps\python\python.exe"))) return Python;
            string script = Path.Combine(Root, @"scripts\bootstrap.ps1");
            // (fix/install-launcher-errors) 0.2.73 / 0.2.74: "exit code -196608" on 8 PCs = PowerShell's "the -File argument does not
            // exist": the setup was opened (Settings > Run the setup again, Retry) where SSBU Arena's files were never downloaded
            // (the first "Install" answered "Not now") or an antivirus removed them. StartSetup downloads them first now.
            if (!File.Exists(script))
                throw new FileNotFoundException("SSBU Arena's setup files are not in " + Root + @" (scripts\bootstrap.ps1 is missing): an antivirus may have " +
                                                "removed it. Allow SSBU Arena's folder in the antivirus, then press Retry" +
                                                // (fix/launcher-0277) Retry downloads them again in an install too (MainWindow.Setup.cs NeedSources)
                                                (Updater.Installed && (Updater.DevCopy || Environment.GetEnvironmentVariable("SSBU_ARENA_SOURCES_REPAIR") == "0") ? " (or download SSBU-Arena.exe again into a new folder)." : ": the files are downloaded again."), script);
            var psi = Hidden("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + Q(script) + " -NoLaunch");
            psi.StandardOutputEncoding = psi.StandardErrorEncoding = null;          // Windows PowerShell writes the console code page
            int code = -1;
            var done = new System.Threading.ManualResetEvent(false);
            string last = null;
            Start(psi, Path.Combine(Root, @"build\bootstrap.log"), l => { if (!string.IsNullOrWhiteSpace(l)) last = l.Trim(); line?.Invoke(l); }, c => { code = c; done.Set(); });
            done.WaitOne();
            string py = Python;
            if (code != 0 || py == null)
            {
                string why = PowerShellCode(code) ?? (last != null ? @"the last line of build\bootstrap.log: " + last : null);
                throw new InvalidOperationException("The tools could not be prepared (exit code " + code + (why != null ? ": " + why : "") + @"). See build\bootstrap.log." +
                                                    (code == unchecked((int)0xFFFD0000) && !Updater.Installed ? " Press Retry: the setup files are downloaded again." : ""));
            }
            return py;
        }

        /// <summary>(fix/install-launcher-errors) an install whose game was never built keeps the sources it was first unpacked with
        /// (the launcher's update runs only on a built game): 0.2.74's setup passed --shared-data to a 0.2.49 install.py
        /// ("unrecognized arguments", code 2). scripts\update.py brings the sources to the latest release first. Blocks;
        /// returns its exit code (the setup goes on either way).</summary>
        public static int UpdateSources(string py, Action<string> line)
        {
            int code = -1;
            var done = new System.Threading.ManualResetEvent(false);
            Start(Hidden(py, @"-u scripts\update.py"), Path.Combine(Root, @"build\update_sources.log"), line, c => { code = c; done.Set(); });
            return done.WaitOne(TimeSpan.FromMinutes(10)) ? code : -1;
        }

        /// <summary>install.py of these sources knows the option (an older one stops on an unknown option)</summary>
        public static bool InstallKnows(string option)
        {
            try { return File.ReadAllText(Path.Combine(Root, @"scripts\install.py")).Contains("'" + option + "'"); }
            catch { return true; }
        }

        static string RunJson(string py, string args, int timeoutMs)
        {
            var psi = Hidden(py, args);
            using (var p = Process.Start(psi))
            {
                var err = p.StandardError.ReadToEndAsync();
                string o = p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } throw new TimeoutException("timed out: " + args); }
                int i = o.IndexOfAny(new[] { '{', '[' });
                if (i < 0) throw new InvalidDataException("no result from " + args + ": " + err.Result.Trim());
                return o.Substring(i);
            }
        }

        static string S(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v as string : null;
        static int N(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v is ArrayList a ? a.Count : 0;

        /// <summary>step 2a: find_inputs.py --json (only names and headers are read, no key value)</summary>
        public static Found Scan(string py)
        {
            var d = new JavaScriptSerializer { MaxJsonLength = 64 << 20 }.Deserialize<Dictionary<string, object>>(
                RunJson(py, "-u scripts\\find_inputs.py --json", 300000));
            var best = d.TryGetValue("best", out var b) ? b as Dictionary<string, object> : null;
            var f = new Found
            {
                Keys = S(best, "prod_keys"), Base = S(best, "base"), Update = S(best, "update"), Build = S(best, "build"),
                Dump = S(best, "dump"), Firmware = S(best, "firmware"),
                KeysSeen = N(d, "keys"), BaseSeen = N(d, "base"), UpdatesSeen = N(d, "updates"),
            };
            if (best != null && best.TryGetValue("dlc", out var dl) && dl is ArrayList dlc) f.Dlc = dlc.Cast<object>().Select(x => x as string).Where(x => x != null).ToList();
            if (d.TryGetValue("notes", out var nt) && nt is ArrayList notes) f.Notes = notes.Cast<object>().Select(x => x as string).Where(x => x != null).ToList();
            return f;
        }

        /// <summary>step 2b: precheck.py --json for the chosen files and install location</summary>
        public static List<PcCheck> Precheck(string py, SetupChoice c)
        {
            var a = new StringBuilder("-u scripts\\precheck.py --json --build " + Q(c.Location));
            if (!string.IsNullOrEmpty(c.Shared) && c.Shared != "off" && Directory.Exists(c.Shared)) a.Append(" --dump " + Q(c.Shared.TrimEnd('\\')));      // (shared game data: no room for a dump)
            else if (IsDump(c.Game)) a.Append(" --dump " + Q(c.Game.TrimEnd('\\')));
            else foreach (var g in new[] { c.Game, c.Update }) if (!string.IsNullOrEmpty(g) && File.Exists(g)) a.Append(" --game " + Q(g));
            var list = new JavaScriptSerializer().Deserialize<ArrayList>(RunJson(py, a.ToString(), 120000));
            return list.Cast<Dictionary<string, object>>().Select(x => new PcCheck { Level = S(x, "level"), What = S(x, "what"), Msg = S(x, "msg") }).ToList();
        }

        /// <summary>one command-line argument, quoted the way CreateProcess reads it</summary>
        public static string Q(string s)
        {
            if (s == null) return "\"\"";
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char ch in s)
            {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', slashes); sb.Append(ch); }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2);
            return sb.Append('"').ToString();
        }

        /// <summary>install.py's arguments for the choices (the sources are already at the release: no self-update)</summary>
        public static string Arguments(SetupChoice c)
        {
            var a = new StringBuilder("-u scripts\\install.py --no-gui --progress-json --no-self-update");
            if (IsDump(c.Game)) a.Append(" --dump " + Q(c.Game.TrimEnd('\\')));
            else
            {
                if (!string.IsNullOrEmpty(c.Game)) a.Append(" --base " + Q(c.Game));
                if (!string.IsNullOrEmpty(c.Update)) a.Append(" --update " + Q(c.Update));
                if (!string.IsNullOrEmpty(c.Keys)) a.Append(" --keys " + Q(c.Keys));
                foreach (var d in c.Dlc)
                {
                    if (a.Length + d.Length > 30000) break;          // the Windows command line holds 32767 characters
                    a.Append(" --dlc " + Q(d));
                }
            }
            if (!string.IsNullOrEmpty(c.Firmware)) a.Append(" --firmware " + Q(c.Firmware.TrimEnd('\\')));
            if (!string.IsNullOrEmpty(c.Location) && !string.Equals(Path.GetFullPath(c.Location).TrimEnd('\\'), DefaultLocation, StringComparison.OrdinalIgnoreCase))
                a.Append(" --build-dir " + Q(c.Location.TrimEnd('\\')));
            if (c.Jobs > 0) a.Append(" --jobs " + c.Jobs);
            if (!string.IsNullOrEmpty(c.Shared) && InstallKnows("--shared-data")) a.Append(" --shared-data " + Q(c.Shared.TrimEnd('\\')));
            if (c.Dry || Environment.GetEnvironmentVariable("SSBU_ARENA_SETUP_DRY") == "1") a.Append(" --dry-run");
            return a.ToString();
        }

        /// <summary>step 3: install.py, hidden; resumable (run it again after an error or a stop)</summary>
        public static Process Install(string py, SetupChoice c, Action<string> line, Action<int> exited)
        {
            string log = Path.Combine(string.IsNullOrEmpty(c.Location) ? DefaultLocation : c.Location, @"logs\installer.log");
            // (fix/install-launcher-errors) "The system cannot find the file specified" (code -1): the Python or the scripts went away
            // (a browser's temporary folder emptied, an antivirus): said as such
            if (string.IsNullOrEmpty(py) || !File.Exists(py))
                throw new FileNotFoundException("The setup's Python is missing (" + (py ?? @"deps\python") + "): an antivirus may have removed it, or the folder was moved or emptied. " +
                                                "Press Resume: the tools are prepared again.", py);
            if (!File.Exists(Path.Combine(Root, @"scripts\install.py")))
                throw new FileNotFoundException("SSBU Arena's setup files are not in " + Root + @" (scripts\install.py is missing). Press Resume: they are downloaded again.");
            return Start(Hidden(py, Arguments(c)), log, line, exited);
        }

        public static string LogPath(SetupChoice c) => Path.Combine(string.IsNullOrEmpty(c.Location) ? DefaultLocation : c.Location, @"logs\installer.log");

        /// <summary>an install location already built (another drive, an earlier install, a copy from another PC): the folder
        /// itself or one with build\ inside, holding work\ssbu_native.exe and state.json. Returns it, or null.</summary>
        public static string ExistingBuild(string folder)
        {
            foreach (var d in new[] { folder, Path.Combine(folder ?? "", "build") })
                if (!string.IsNullOrEmpty(folder) && File.Exists(Path.Combine(d, @"work\ssbu_native.exe")) && File.Exists(Path.Combine(d, "state.json")))
                    return Path.GetFullPath(d).TrimEnd('\\');
            return null;
        }

        /// <summary>the unpacked game an install was built from (state.json "dump"/"romfs", or dump\romfs next to it)</summary>
        public static bool GameFilesThere(string build)
        {
            try
            {
                if (Directory.Exists(Path.Combine(build, @"..\dump\romfs"))) return true;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(Path.Combine(build, "state.json")), "\"romfs\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                string romfs = m.Success ? System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value) : null;
                return romfs != null && (File.Exists(Path.Combine(romfs, "data.arc")) || File.Exists(Path.Combine(romfs, "data.arcp")) || File.Exists(Path.Combine(romfs, "data.arcl")));
            }
            catch { return false; }
        }

        /// <summary>the launcher, run.py and install.py use this install location from now on (build_location.txt, as
        /// scripts/build_location.py --set writes it; the default folder removes the file)</summary>
        public static void UseLocation(string build)
        {
            string f = Path.Combine(Root, "build_location.txt");
            if (string.Equals(Path.GetFullPath(build).TrimEnd('\\'), DefaultLocation, StringComparison.OrdinalIgnoreCase)) { if (File.Exists(f)) File.Delete(f); }
            else File.WriteAllText(f, build + "\r\n", new UTF8Encoding(false));
        }

        /// <summary>the Microsoft C++ Build Tools (install.py's error action "msvc", only without the prebuilt runtime): winget in
        /// a console the user sees</summary>
        public static void InstallBuildTools()
        {
            const string cmd = "winget install --id Microsoft.VisualStudio.2022.BuildTools -e --accept-package-agreements --accept-source-agreements --override \"--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended\"";
            Process.Start(new ProcessStartInfo("cmd.exe", "/k echo Installing the Microsoft C++ Build Tools (5-15 minutes)... & " + cmd +
                " & echo. & echo Finished: close this window and press Resume in SSBU Arena.") { UseShellExecute = true });
        }
    }
}
