// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.73) the player's logs to the SSBU Arena staff, so nobody has to ask for them:
//  - "Send logs" (Settings > Help, Settings > Game files, the setup screen, the launcher's error window): the player sees which
//    files go and can write what happened; at the end a short report code to give to the staff (kind 'manual');
//  - sent on its own (LauncherCrash.cs, MainWindow.Setup.cs): the launcher's own crash ('launcher_crash') and a setup that stopped
//    with an error ('install_error'); off with SSBU_ARENA_AUTO_REPORT=0 or the "Send crash reports" setting off.
// One text file with every log that exists (each cut to its start and end when long), cleaned like the crash reports
// (CrashReport.Clean: user and PC names, folders, e-mail and internet addresses, online passes) and with anything that looks
// like a token or key masked. Never a save, a settings file or the session. It goes to the edge function logs-send (website
// repo, supabase/functions/logs-send; limits in migrations 20261125120000 and 20261125140000) with the player's session when
// signed in, else without an account (the server limits it by address). SSBU_ARENA_SEND_LOGS=0 hides the button.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

namespace Arena
{
    public static class LogReport
    {
        public static bool Enabled => Environment.GetEnvironmentVariable("SSBU_ARENA_SEND_LOGS") != "0";
        public static bool AutoEnabled => Environment.GetEnvironmentVariable("SSBU_ARENA_AUTO_REPORT") != "0" && Core.CrashReports;
        const int MaxText = 1100000;          // characters of the whole file (the server takes 1.2 MB)

        public sealed class Item
        {
            public string Name, Path;
            public int Max;
            public long Size;
        }

        public sealed class Result
        {
            public string Code, Error;
            public bool Duplicate;
            public bool Ok => Code != null;
        }

        static string ExeDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        /// <summary>the folders a log can be in: the install's build folder, the sources' build folder, the launcher's own
        /// folder (a launcher not installed yet writes its launcher_update.log next to itself)</summary>
        static List<string> Dirs()
        {
            var d = new List<string>();
            void Add(string p) { try { if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !d.Any(x => string.Equals(x, Path.GetFullPath(p).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) d.Add(Path.GetFullPath(p).TrimEnd('\\')); } catch { } }
            try { Add(Core.Build); } catch { }
            try { if (Core.Repo != null) Add(Path.Combine(Core.Repo, "build")); } catch { }
            Add(ExeDir);
            // (fix/install-launcher-errors) a launcher not installed yet: build\bootstrap.log is in the sources' build folder beside the exe
            // (the reports of "exit code -196608" came without it)
            try { Add(Path.Combine(Installer.Root, "build")); } catch { }
            return d;
        }

        /// <summary>the files that would be sent, those that exist; extra: more files (the setup's own installer.log)</summary>
        public static List<Item> Files(IEnumerable<string> extra = null)
        {
            var names = new (string rel, int max)[]
            {
                ("launcher_crash.log", 128 << 10), ("launcher_update.log", 128 << 10), ("bootstrap.log", 64 << 10),
                (@"logs\installer.log", 256 << 10), ("update_last.log", 128 << 10), ("update_sources.log", 32 << 10), ("prepare.log", 64 << 10),
                (@"logs\arena_menu.log", 32 << 10), ("game.log", 320 << 10),
                (@"work\ssbu_native_lumdiag.txt", 96 << 10),      // (amd-black) the black-picture diagnosis' lines (runtime/nvn_vk_lumdiag.c), not cut with game.log's middle
            };
            var list = new List<Item>();
            void Add(string path, string name, int max)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists || list.Any(x => string.Equals(x.Path, fi.FullName, StringComparison.OrdinalIgnoreCase))) return;
                    list.Add(new Item { Name = name, Path = fi.FullName, Max = max, Size = fi.Length });
                }
                catch { }
            }
            foreach (var dir in Dirs())
                foreach (var n in names) Add(Path.Combine(dir, n.rel), n.rel, n.max);
            if (extra != null) foreach (var x in extra.Where(x => !string.IsNullOrEmpty(x))) Add(x, Path.GetFileName(x), 256 << 10);
            // the game's newest crash file (its last entries)
            try
            {
                var c = new DirectoryInfo(Core.Work).GetFiles("*_crash.txt").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (c != null) Add(c.FullName, @"work\" + c.Name, 64 << 10);
            }
            catch { }
            return list;
        }

        /// <summary>a file's text: all of it, or its first quarter and its last three quarters of max bytes</summary>
        static string Read(string path, int max)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long len = fs.Length;
                if (len <= max)
                {
                    var all = new byte[len];
                    int n = 0, r;
                    while (n < all.Length && (r = fs.Read(all, n, all.Length - n)) > 0) n += r;
                    return Encoding.UTF8.GetString(all, 0, n);
                }
                int head = max / 4, tail = max - head;
                var a = new byte[head];
                int na = fs.Read(a, 0, head);
                fs.Seek(-tail, SeekOrigin.End);
                var b = new byte[tail];
                int nb = 0, rb;
                while (nb < tail && (rb = fs.Read(b, nb, tail - nb)) > 0) nb += rb;
                return Encoding.UTF8.GetString(a, 0, na) + "\n\n[... " + (len - na - nb).ToString("N0") + " bytes left out ...]\n\n" + Encoding.UTF8.GetString(b, 0, nb);
            }
        }

        /// <summary>CrashReport.Clean, then anything that looks like a secret: tokens (JWT, Supabase keys, "Bearer"), key = value
        /// pairs whose name says key / token / secret / password / ticket</summary>
        public static string Mask(string t)
        {
            if (string.IsNullOrEmpty(t)) return t ?? "";
            t = t.Replace("﻿", "");      // the byte order marks of the files put one after the other
            try { t = t.Replace(Updater.Current.ToString(), Updater.Current.ToString(3)); } catch { }      // "0.2.73.0" is no internet address
            try { t = CrashReport.Clean(t); } catch { }
            t = Regex.Replace(t, @"eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", "<token>");
            t = Regex.Replace(t, @"\bsb_(publishable|secret)_[A-Za-z0-9_-]{8,}", "<key>");
            t = Regex.Replace(t, @"(?i)\bbearer\s+[A-Za-z0-9._~+/=-]{8,}", "Bearer <token>");
            t = Regex.Replace(t, @"(?im)\b([\w.-]*(key|token|secret|password|passwd|ticket|apikey|refresh)[\w.-]*)(\s*[=:]\s*)(""?)[^\s""]{6,}\4", "$1$3<hidden>");
            t = Regex.Replace(t, @"(?i)([?&](token|key|ticket|state|refresh_token|access_token)=)[^&\s""]+", "$1<hidden>");
            return t;
        }

        /// <summary>the whole file: a short header about this PC and launcher, then each log under its name</summary>
        public static string Build(string kind, string what, IEnumerable<string> extra = null)
        {
            var sb = new StringBuilder();
            sb.Append("SSBU Arena logs (" + kind + ")\n");
            sb.Append("sent:      " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") + " UTC (local time " + DateTime.Now.ToString("HH:mm") + ")\n");
            try { sb.Append("launcher:  " + Updater.Current.ToString(3) + (Updater.Installed ? ", installed commit " + (Updater.InstalledCommit() ?? "?") : ", not installed yet") + "\n"); } catch { }
            sb.Append("windows:   " + Environment.OSVersion.Version + (Environment.Is64BitOperatingSystem ? " 64-bit" : " 32-bit") + ", " + Environment.ProcessorCount + " threads, culture " + System.Globalization.CultureInfo.CurrentCulture.Name + "\n");
            try
            {
                sb.Append("install:   game exe " + (File.Exists(Core.Exe) ? "there" : "missing") + "; signed in " + (Account.SignedIn ? "yes" : "no"));
                var root = Path.GetPathRoot(Core.Build);
                if (!string.IsNullOrEmpty(root)) { var di = new DriveInfo(root); sb.Append("; free space on " + di.Name.TrimEnd('\\') + " " + (di.AvailableFreeSpace / (1024.0 * 1024 * 1024)).ToString("0.0") + " GB (" + di.DriveFormat + ")"); }
                sb.Append("\n");
            }
            catch { sb.Append("\n"); }
            if (!string.IsNullOrEmpty(what)) sb.Append("what:      " + what + "\n");
            var files = Files(extra);
            sb.Append("files:     " + (files.Count == 0 ? "none found" : string.Join(", ", files.Select(f => f.Name))) + "\n");
            foreach (var f in files)
            {
                string body;
                try { body = Read(f.Path, f.Max); } catch (Exception ex) { body = "(not readable: " + ex.Message + ")"; }
                sb.Append("\n==================== " + f.Name + "  (" + f.Size.ToString("N0") + " bytes, changed " + SafeTime(f.Path) + ")\n");
                sb.Append(body.Replace("\r\n", "\n"));
                if (!body.EndsWith("\n")) sb.Append("\n");
            }
            string text = Mask(sb.ToString());
            if (text.Length > MaxText) text = text.Substring(0, MaxText / 4) + "\n\n[... cut ...]\n\n" + text.Substring(text.Length - (MaxText - MaxText / 4 - 40));
            return text;
        }

        static string SafeTime(string p) { try { return File.GetLastWriteTime(p).ToString("yyyy-MM-dd HH:mm"); } catch { return "?"; } }

        /// <summary>sends the logs (blocks: a background thread). kind: manual | launcher_crash | install_error; exception: the
        /// error's first line for the automatic kinds. Never throws.</summary>
        public static Result Send(string kind, string exception, string note, int timeoutMs, IEnumerable<string> extra = null)
        {
            try
            {
                string text = Build(kind, kind == "manual" ? null : exception, extra);
                var args = new Dictionary<string, object>
                {
                    { "product", "arena" }, { "kind", kind }, { "text", text },
                    { "note", string.IsNullOrWhiteSpace(note) ? null : Mask(note.Trim()).Substring(0, Math.Min(500, Mask(note.Trim()).Length)) },
                    { "exception", string.IsNullOrEmpty(exception) ? null : Mask(FirstLine(exception)) },
                    { "version", Updater.Current.ToString(3) },
                    { "commit", SafeCommit() },
                    { "windows", Environment.OSVersion.Version.ToString() },
                };
                string json = new JavaScriptSerializer { MaxJsonLength = 16 << 20 }.Serialize(args);
                string bearer = null;
                if (Account.SignedIn) try { bearer = Account.AccessToken(); } catch { }
                var r = Post(json, bearer, timeoutMs);
                if (r.status == 401 && bearer != null) r = Post(json, null, timeoutMs);      // a session the server does not take: without the account
                var d = Parse(r.body);
                if (r.status == 200 && d.TryGetValue("code", out var c) && c is string code) { Updater.Log("logs sent (" + kind + "): report " + code); return new Result { Code = code }; }
                string err = d.TryGetValue("error", out var e) ? e as string : null;
                Updater.Log("logs not sent (" + kind + "): " + r.status + " " + (err ?? ""));
                if (r.status == 409) return new Result { Duplicate = true, Error = "This error was already reported in the last hour." };
                if (r.status == 429) return new Result { Error = err == "too many today" ? "Too many reports were sent today: try again tomorrow." : "You sent logs several times already: try again in an hour." };
                if (r.status == 0) return new Result { Error = "Cannot reach the Arena server: " + (r.body ?? "no answer") + ". Check the internet connection." };
                return new Result { Error = "The Arena server did not take the logs (" + r.status + (err != null ? ", " + err : "") + ")." };
            }
            catch (Exception ex)
            {
                try { Updater.Log("logs not sent (" + kind + "): " + ex.Message); } catch { }
                return new Result { Error = ex.Message };
            }
        }

        static string SafeCommit() { try { return Updater.Installed ? Updater.InstalledCommit() : null; } catch { return null; } }
        public static string FirstLine(string s) => (s ?? "").Replace("\r", "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

        static (int status, string body) Post(string json, string bearer, int timeoutMs)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(json);
                var rq = (HttpWebRequest)WebRequest.Create(Account.Url + "/functions/v1/logs-send");
                rq.Method = "POST"; rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs; rq.ContentType = "application/json";
                rq.Headers["apikey"] = Account.Key;
                if (bearer != null) rq.Headers["Authorization"] = "Bearer " + bearer;
                using (var s = rq.GetRequestStream()) s.Write(data, 0, data.Length);
                using (var rs = (HttpWebResponse)rq.GetResponse())
                using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return ((int)rs.StatusCode, sr.ReadToEnd());
            }
            catch (WebException e) when (e.Response is HttpWebResponse hr)
            {
                string text = "";
                try { using (var sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd(); } catch { }
                return ((int)hr.StatusCode, text);
            }
            catch (Exception e) { return (0, e.Message); }
        }

        static Dictionary<string, object> Parse(string s)
        {
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(s ?? "") ?? new Dictionary<string, object>(); }
            catch { return new Dictionary<string, object>(); }
        }

        // ---------------------------------------------------------------- sent on their own
        static readonly HashSet<string> autoSent = new HashSet<string>();
        static int autoBusy;

        /// <summary>(launcher crash, setup error) sends in the background, at most once per error per launcher run, 3 a run, one
        /// at a time, with a short timeout; done(result) runs on that thread (null: not sent, off or already sent). Never throws.</summary>
        public static void AutoSend(string kind, string error, IEnumerable<string> extra, Action<Result> done)
        {
            try
            {
                if (!AutoEnabled) { done?.Invoke(null); return; }
                string key = kind + "|" + FirstLine(error);
                lock (autoSent)
                {
                    if (autoSent.Contains(key) || autoSent.Count >= 3) { done?.Invoke(null); return; }
                    autoSent.Add(key);
                }
                var list = extra?.ToList();
                var t = new Thread(() =>
                {
                    Result r = null;
                    if (Interlocked.Exchange(ref autoBusy, 1) == 0)
                        try { r = Send(kind, error, null, 20000, list); } catch { } finally { Interlocked.Exchange(ref autoBusy, 0); }
                    try { done?.Invoke(r); } catch { }
                }) { IsBackground = true, Name = "auto report" };
                t.Start();
            }
            catch { }
        }

        /// <summary>(a crash that ends the process) the same, waiting for it at most timeoutMs</summary>
        public static Result AutoSendNow(string kind, string error, int timeoutMs)
        {
            Result r = null;
            var ev = new ManualResetEvent(false);
            AutoSend(kind, error, null, x => { r = x; ev.Set(); });
            ev.WaitOne(timeoutMs);
            return r;
        }
    }
}
