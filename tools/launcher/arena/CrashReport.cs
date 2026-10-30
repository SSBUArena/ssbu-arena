// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.18) the game crashed: the launcher sends the crash file (build\work\<exe>_crash.txt, its last entry) and the end of game.log to
// the Arena server, with the player's own session (schema: website repo, supabase/migrations/20261030120000_crash_reports.sql). Only
// when signed in and with the setting on (launcher.ini crash_reports). Before sending: the user's and the PC's names, the folders and
// any address other than the room's own (10.x) are removed. Minidumps (memory contents) are never sent.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Arena
{
    public static class CrashReport
    {
        const int MaxLog = 1024 * 1024, MaxCrash = 256 * 1024;      // bytes sent at most of each file

        /// <summary>the newest crash file changed since the game started, or null</summary>
        static string NewCrashFile(DateTime started)
        {
            try
            {
                return new DirectoryInfo(Core.Work).GetFiles("*_crash.txt").Where(f => f.LastWriteTimeUtc > started)
                       .OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>what identifies the player or the PC removed from a text</summary>
        public static string Clean(string t)
        {
            var words = new List<KeyValuePair<string, string>>();
            void Add(string w, string to) { if (!string.IsNullOrEmpty(w) && w.Length >= 3) words.Add(new KeyValuePair<string, string>(w, to)); }
            Add(Core.RomFS, "<game files>"); Add(Core.Saves, "<saves>"); Add(Core.Repo, "<install>");
            Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>");
            Add(Environment.UserName, "<user>"); Add(Environment.MachineName, "<pc>");
            foreach (var w in words.OrderByDescending(w => w.Key.Length))
                t = Regex.Replace(t, Regex.Escape(w.Key), w.Value, RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"\b(?!10\.)(?!0\.0\.0\.0\b)\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b", "<address>");
            return Regex.Replace(t, @"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", "<email>");
        }

        static string Tail(string path, int max)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length > max) fs.Seek(-max, SeekOrigin.End);
                var b = new byte[Math.Min(fs.Length, max)];
                int n = fs.Read(b, 0, b.Length);
                return Encoding.UTF8.GetString(b, 0, n);
            }
        }

        static void Put(string name, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            var rq = (HttpWebRequest)WebRequest.Create(Account.Url + "/storage/v1/object/crashes/" + name);
            rq.Method = "POST"; rq.Timeout = 60000; rq.ContentType = "text/plain";
            rq.Headers["apikey"] = Account.Key; rq.Headers["Authorization"] = "Bearer " + Account.AccessToken();
            using (var s = rq.GetRequestStream()) s.Write(data, 0, data.Length);
            using (rq.GetResponse()) { }
        }

        /// <summary>after a game: true when a crash was found and sent. Any failure stays in launcher_update.log only.</summary>
        public static bool SendIfCrashed(DateTime started, out bool crashed)
        {
            crashed = false;
            string file = NewCrashFile(started);
            if (file == null) return false;
            crashed = true;
            if (!Core.CrashReports) { Updater.Log("crash report: off in the settings"); return false; }
            if (!Account.SignedIn) { Updater.Log("crash report: not signed in, not sent"); return false; }
            try
            {
                string all = Tail(file, MaxCrash);
                int i = all.LastIndexOf("=== ", StringComparison.Ordinal);
                string entry = Clean(i >= 0 ? all.Substring(i) : all);
                string log = Path.Combine(Core.Build, "game.log");
                string logText = File.Exists(log) ? Clean(Tail(log, MaxLog)) : null;

                string first = entry.Split('\n')[0].Trim();
                var stack = Regex.Match(entry, @"guest call stack[^\n]*\n((?:\s+f_[0-9a-f]+[^\n]*\n)+)");
                var gpu = logText == null ? null : Regex.Match(logText, @"nvn_vk: using device \d+: ([^(\r\n]+)");
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
                string folder = "r/" + Account.Me.Id + "/" + stamp;
                string crashPath = folder + "-crash.txt", logPath = logText == null ? null : folder + "-game.log";
                if (Environment.GetEnvironmentVariable("SSBU_ARENA_CRASH_TEST") == "1") { File.WriteAllText(Path.Combine(Core.Build, "crash_test_entry.txt"), entry); if (logText != null) File.WriteAllText(Path.Combine(Core.Build, "crash_test_log.txt"), logText); }
                Put(crashPath, entry);
                if (logPath != null) Put(logPath, logText);
                var args = new Dictionary<string, object>
                {
                    { "p_version", Updater.Current.ToString(3) }, { "p_commit", Updater.Installed ? Updater.InstalledCommit() : null },
                    { "p_exception", first }, { "p_stack", stack.Success ? stack.Groups[1].Value : null },
                    { "p_gpu", gpu != null && gpu.Success ? gpu.Groups[1].Value.Trim() : null }, { "p_windows", Environment.OSVersion.Version.ToString() },
                    { "p_log_path", logPath }, { "p_crash_path", crashPath },
                };
                Account.Rest("POST", "/rest/v1/rpc/crash_report_add", new JavaScriptSerializer().Serialize(args));
                Updater.Log("crash report sent: " + first);
                return true;
            }
            catch (Exception ex) { Updater.Log("crash report not sent: " + ex.Message); return false; }
        }
    }
}
