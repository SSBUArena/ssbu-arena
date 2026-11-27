// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena updates: the launcher is the only download. At every start it reads the latest GitHub Release of the
// repository (its asset arena-release.json, written by scripts/arena_release.py):
//   {"version": "0.2.0", "commit": "<40 hex>", "launcher": {"file": "SSBU-Arena.exe", "sha256": "..."}}
// - a newer launcher version: the exe is downloaded, checked against its SHA-256 and put in place of this one (the running
//   exe is renamed to .old, which Windows allows), then started again;
// - another commit than the installed one (build\update_manifest.json, scripts/update.py): scripts/install.py runs without
//   its window; it brings the sources to the release (update.py follows the latest release), takes the release's prebuilt
//   runtime (install.py RUNTIME_RELEASE, SHA-256 checked) and rebuilds here only what the change needs;
// - not installed yet (no scripts\ beside the exe): the sources of the release are unpacked beside the exe; the launcher's
//   setup screen (Installer.cs, MainWindow.Setup.cs) then prepares the tools and asks for the game files.
// A git checkout (a developer's copy) is never changed; SSBU_ARENA_UPDATE=off skips everything, =force checks a git checkout too.
// Tests: SSBU_ARENA_RELEASE=<folder> reads arena-release.json and its files from a folder instead of GitHub;
// SSBU_ARENA_UPDATE_DRY=1 runs install.py with --dry-run.
// (OpenSmash #480, Denkishi) the latest release is asked of GitHub's API, and when it does not answer (its limit of 60 requests an
// hour per address, reached on a shared network or at a tournament) of github.com's releases/latest page, whose redirect names the
// tag; arena-release.json is then read from that release's download address (not the API). Tested with SSBU_ARENA_API=<base url>
// and SSBU_ARENA_WEB=<base url> (e.g. http://127.0.0.1:9: nothing answers) in place of https://api.github.com / https://github.com.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Arena
{
    public sealed class Release
    {
        public string Tag, Version, Commit, LauncherUrl, LauncherSha, Page;
        /// <summary>(test, SSBU_ARENA_RELEASE only) the sources as a local zip (sources.zip in that folder, codeload's layout); null = GitHub</summary>
        public string SourcesUrl;
    }

    public static class Updater
    {
        public const string GitHubRepo = "ssbuarena/ssbu-arena";
        static readonly string Mode = (Environment.GetEnvironmentVariable("SSBU_ARENA_UPDATE") ?? "").ToLowerInvariant();

        static Updater() { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; }

        public static Version Current => Assembly.GetExecutingAssembly().GetName().Version;
        /// <summary>this exe. (fix/install-launcher-errors) Process.MainModule opens the process for reading its modules, which some
        /// security programs refuse ("Win32Exception: Access is denied" at the start, 0.2.74): the assembly's own path then</summary>
        public static string ExePath => exePath ?? (exePath = FindExePath());
        static string exePath;
        static string FindExePath()
        {
            try { return Process.GetCurrentProcess().MainModule.FileName; } catch { }
            try { string a = Assembly.GetEntryAssembly()?.Location; if (!string.IsNullOrEmpty(a)) return a; } catch { }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, AppDomain.CurrentDomain.FriendlyName);
        }
        public static bool Off => Mode == "off";
        /// <summary>a developer's git checkout: nothing is changed there unless SSBU_ARENA_UPDATE=force</summary>
        public static bool DevCopy => Core.Repo != null && Directory.Exists(Path.Combine(Core.Repo, ".git")) && Mode != "force";
        public static bool Installed => Core.Repo != null;
        static string Python => Core.Repo == null ? null : Path.Combine(Core.Repo, @"deps\python\python.exe");

        internal static byte[] Get(string url, string accept = null, int timeoutMs = 15000)
        {
            var rq = WebRequest.Create(url);              // https, or file:// (SSBU_ARENA_RELEASE)
            rq.Timeout = timeoutMs;
            if (rq is HttpWebRequest h)
            {
                h.UserAgent = "ssbu-arena-launcher/" + Current;
                h.ReadWriteTimeout = timeoutMs;
                if (accept != null) h.Accept = accept;
            }
            using (var rs = rq.GetResponse())
            using (var s = rs.GetResponseStream())
            using (var m = new MemoryStream()) { s.CopyTo(m); return m.ToArray(); }
        }

        static string Api => (Environment.GetEnvironmentVariable("SSBU_ARENA_API") ?? "https://api.github.com").TrimEnd('/');
        static string Web => (Environment.GetEnvironmentVariable("SSBU_ARENA_WEB") ?? "https://github.com").TrimEnd('/');

        /// <summary>why the last release check found nothing, said to the player; null = it answered</summary>
        public static string LastCheckError;

        static string Why(Exception ex)
        {
            if (ex is WebException w && w.Response is HttpWebResponse r)
                return "HTTP " + (int)r.StatusCode + ((int)r.StatusCode == 403 || (int)r.StatusCode == 429 ? " (the request limit)" : "");
            return ex.GetBaseException().Message;
        }

        /// <summary>the latest release, or null (no release with arena-release.json). When neither GitHub's API nor github.com
        /// answers it throws, with a message for the player (also in LastCheckError): the launcher goes on as it is.</summary>
        public static Release Latest()
        {
            var js = new JavaScriptSerializer();
            LastCheckError = null;
            string local = Environment.GetEnvironmentVariable("SSBU_ARENA_RELEASE");     // (test) a folder with arena-release.json and its files
            if (!string.IsNullOrEmpty(local))
            {
                var t = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(local, "arena-release.json")));
                var tl = t["launcher"] as Dictionary<string, object>;
                string src = Path.Combine(local, "sources.zip");       // (test) an unpublished commit's sources (git archive --prefix=<top>/)
                return new Release { Tag = "v" + t["version"], Version = t["version"] as string, Commit = t["commit"] as string, Page = local,
                                     LauncherUrl = new Uri(Path.Combine(local, (string)tl["file"])).AbsoluteUri, LauncherSha = (string)tl["sha256"],
                                     SourcesUrl = File.Exists(src) ? new Uri(Path.GetFullPath(src)).AbsoluteUri : null };
            }
            string apiWhy;
            try { return LatestFromApi(js); }
            catch (Exception ex) { apiWhy = Why(ex); Log("release check: GitHub's API did not answer (" + apiWhy + "): asking github.com's releases/latest page"); }
            string webWhy = null;
            for (int attempt = 1; attempt <= 2; attempt++)          // the page, and once more after a moment
            {
                try
                {
                    var r = LatestFromPage(js);
                    Log("release check: through github.com's page: " + (r == null ? "no release with arena-release.json" : r.Tag));
                    return r;
                }
                catch (Exception ex) { webWhy = Why(ex); Log("release check: github.com's page, try " + attempt + ": " + webWhy); }
                if (attempt == 1) System.Threading.Thread.Sleep(2000);
            }
            LastCheckError = "GitHub did not answer (API: " + apiWhy + "; github.com: " + webWhy + "). On a shared network GitHub's limit of 60 checks an hour " +
                             "may be reached: the launcher goes on with this version and checks again at its next start.";
            throw new WebException("could not check for updates: " + LastCheckError);
        }

        static Release LatestFromApi(JavaScriptSerializer js)
        {
            var rel = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(
                Get(Api + "/repos/" + GitHubRepo + "/releases/latest", "application/vnd.github+json")));
            var assets = ((rel["assets"] as System.Collections.ArrayList) ?? new System.Collections.ArrayList()).Cast<Dictionary<string, object>>().ToList();
            string Url(string name) => assets.Where(a => (string)a["name"] == name).Select(a => (string)a["browser_download_url"]).FirstOrDefault();
            string info = Url("arena-release.json");
            if (info == null) return null;
            var d = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Get(info)));
            var l = d.ContainsKey("launcher") ? d["launcher"] as Dictionary<string, object> : null;
            return new Release
            {
                Tag = rel["tag_name"] as string,
                Page = rel["html_url"] as string,
                Version = d["version"] as string,
                Commit = d["commit"] as string,
                LauncherUrl = l != null ? Url((string)l["file"]) : null,
                LauncherSha = l != null ? (string)l["sha256"] : null,
            };
        }

        /// <summary>the latest release without the API: github.com/&lt;repo&gt;/releases/latest redirects to .../releases/tag/&lt;tag&gt;
        /// (to .../releases when there is none: null); arena-release.json and the launcher come from .../releases/download/&lt;tag&gt;/.
        /// The SHA-256 checks are the same (ReplaceLauncher, install.py).</summary>
        static Release LatestFromPage(JavaScriptSerializer js)
        {
            string url = Web + "/" + GitHubRepo + "/releases/latest", tag = null;
            for (int hop = 0; hop < 4 && tag == null; hop++)
            {
                var rq = (HttpWebRequest)WebRequest.Create(url);
                rq.Method = "HEAD"; rq.AllowAutoRedirect = false; rq.Timeout = 15000; rq.ReadWriteTimeout = 15000;
                rq.UserAgent = "ssbu-arena-launcher/" + Current;
                using (var rs = (HttpWebResponse)rq.GetResponse())
                {
                    string loc = rs.Headers["Location"];
                    if ((int)rs.StatusCode < 300 || (int)rs.StatusCode >= 400 || string.IsNullOrEmpty(loc))
                        throw new WebException("github.com's releases/latest did not redirect (HTTP " + (int)rs.StatusCode + ")");
                    var to = new Uri(new Uri(url), loc);
                    var m = System.Text.RegularExpressions.Regex.Match(to.AbsolutePath, "/releases/tag/([^/]+)/?$");
                    if (m.Success) tag = Uri.UnescapeDataString(m.Groups[1].Value);
                    else if (to.AbsolutePath.TrimEnd('/').EndsWith("/releases")) return null;      // no release
                    url = to.AbsoluteUri;                                                          // e.g. the repository's old name
                }
            }
            if (tag == null) throw new WebException("github.com's releases/latest: too many redirects");
            string dl = Web + "/" + GitHubRepo + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
            Dictionary<string, object> d;
            try { d = js.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(Get(dl + "arena-release.json"))); }
            catch (WebException ex) when (ex.Response is HttpWebResponse r && r.StatusCode == HttpStatusCode.NotFound) { return null; }      // a release without it
            var l = d.ContainsKey("launcher") ? d["launcher"] as Dictionary<string, object> : null;
            return new Release
            {
                Tag = tag,
                Page = Web + "/" + GitHubRepo + "/releases/tag/" + Uri.EscapeDataString(tag),
                Version = d["version"] as string,
                Commit = d["commit"] as string,
                LauncherUrl = l != null ? dl + Uri.EscapeDataString((string)l["file"]) : null,
                LauncherSha = l != null ? (string)l["sha256"] : null,
            };
        }

        public static bool LauncherOutdated(Release r) =>
            r?.LauncherUrl != null && System.Version.TryParse(r.Version, out var v) && v > Current;

        /// <summary>the commit the sources are at (scripts/update.py's manifest), null = unknown (never updated)</summary>
        public static string InstalledCommit()
        {
            try
            {
                string f = Path.Combine(Core.Repo, @"build\update_manifest.json");
                if (!File.Exists(f)) return null;
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(f));
                return d.TryGetValue("commit", out var c) ? c as string : null;
            }
            catch { return null; }
        }

        // (0.2.23) update.py writes the commit before the build: an update that stopped after it (a file in use) looked done and the old game stayed
        static string UpdatePending => Path.Combine(Core.Repo, @"build\update_pending.txt");
        public static bool GameOutdated(Release r) => r?.Commit != null && Installed && !DevCopy && (InstalledCommit() != r.Commit || File.Exists(UpdatePending));

        /// <summary>what the update check found and did, in build\launcher_update.log (the last start's only)</summary>
        public static void Log(string s)
        {
            try
            {
                string dir = Core.Repo != null ? Path.Combine(Core.Repo, "build") : Path.GetDirectoryName(ExePath);
                File.AppendAllText(Path.Combine(dir, "launcher_update.log"), DateTime.Now.ToString("HH:mm:ss ") + s + "\r\n");
            }
            catch { }
        }

        static string Sha256(byte[] b) { using (var h = SHA256.Create()) return string.Concat(h.ComputeHash(b).Select(x => x.ToString("x2"))); }

        /// <summary>leftovers of the last self-update</summary>
        public static void CleanUp()
        {
            foreach (var p in new[] { ExePath + ".old", ExePath + ".new" })
                try { if (File.Exists(p)) File.Delete(p); } catch { }
            if (Installed && !Directory.Exists(Path.Combine(Core.Repo, ".git"))) HideScripts(Core.Repo);
        }

        /// <summary>(0.2.8) a player's install: the sources' own scripts at the top of the folder (Install SSBU.bat, Play SSBU.bat,
        /// play.sh, ...: the way in without this launcher) hidden, so the folder shows the launcher; they still work. Again after
        /// every update (update.py writes them anew). A developer's git checkout is left as it is.</summary>
        public static void HideScripts(string root)
        {
            try
            {
                // only the repository's own files (the update manifest's list): a file the player put there stays visible
                var m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(root, @"build\update_manifest.json")));
                if (!(m.TryGetValue("files", out var fo) && fo is Dictionary<string, object> files)) return;
                foreach (var rel in files.Keys)
                {
                    string ext = Path.GetExtension(rel).ToLowerInvariant();
                    if (rel.Contains("/") || (ext != ".bat" && ext != ".cmd" && ext != ".sh")) continue;
                    string f = Path.Combine(root, rel);
                    if (!File.Exists(f)) continue;
                    var a = File.GetAttributes(f);
                    if ((a & FileAttributes.Hidden) == 0) File.SetAttributes(f, a | FileAttributes.Hidden);
                }
            }
            catch { }
        }

        /// <summary>the new launcher in place of this exe (and of the copy in build\, if any); then it is started again</summary>
        public static void ReplaceLauncher(Release r)
        {
            byte[] exe = Get(r.LauncherUrl, null, 120000);
            if (!string.Equals(Sha256(exe), r.LauncherSha, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("the downloaded launcher does not match its SHA-256: not used");
            string me = ExePath;
            File.WriteAllBytes(me + ".new", exe);
            if (File.Exists(me + ".old")) File.Delete(me + ".old");
            File.Move(me, me + ".old");
            File.Move(me + ".new", me);
            // the other copy: build\SSBU Arena.exe when this one is at the repository's root (setup.py link's copy)
            string other = Core.Repo != null && Path.GetDirectoryName(me).TrimEnd('\\') == Core.Repo ? Path.Combine(Core.Build, "SSBU Arena.exe") : null;
            if (other != null && File.Exists(other)) try { File.WriteAllBytes(other, exe); } catch { }
            Process.Start(new ProcessStartInfo(me, "--updated") { UseShellExecute = false });
        }

        /// <summary>scripts/install.py without its window: sources to the release, its prebuilt runtime, the rebuild it needs.
        /// line gets every output line ("##ssbu {json}" progress lines among them), exited the exit code.</summary>
        public static Process RunGameUpdate(Action<string> line, Action<int> exited)
        {
            if (!File.Exists(Python)) throw new FileNotFoundException("The private Python is missing (deps\\python): run Install SSBU.bat once.");
            string dry = Environment.GetEnvironmentVariable("SSBU_ARENA_UPDATE_DRY") == "1" ? " --dry-run" : "";      // (test) says what it would do
            var psi = new ProcessStartInfo(Python, "-u scripts\\install.py --no-gui --progress-json --no-shortcuts" + dry)
            {
                WorkingDirectory = Core.Repo, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            ProcessEnv.Set(psi, "PYTHONIOENCODING", "utf-8");
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            string log = Path.Combine(Core.Repo, @"build\update_last.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log));
            if (dry == "") File.WriteAllText(UpdatePending, "an update of the game began and has not ended well yet\n");
            var w = new StreamWriter(log, false, Encoding.UTF8) { AutoFlush = true };
            DataReceivedEventHandler on = (s, e) => { if (e.Data == null) return; lock (w) w.WriteLine(e.Data); line(e.Data); };
            p.OutputDataReceived += on;
            p.ErrorDataReceived += on;
            p.Exited += (s, e) => { p.WaitForExit(); lock (w) w.Dispose(); if (p.ExitCode == 0 && dry == "") try { File.Delete(UpdatePending); } catch { } exited(p.ExitCode); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return p;
        }

        public static IEnumerable<string> UpdateLogTail(int n)
        {
            try { return File.ReadAllLines(Path.Combine(Core.Repo, @"build\update_last.log")).Where(l => !l.StartsWith("##ssbu ")).Reverse().Take(n).Reverse().ToList(); }
            catch { return new List<string>(); }
        }

        /// <summary>(fix/launcher-0277) SSBU_ARENA_UNPACK_ATTRIBS=0: as 0.2.76, a file already there is overwritten with its
        /// attributes as they are. Otherwise a hidden, read-only or system file (HideScripts marks Install SSBU.bat and the other
        /// scripts hidden after the first install) gets normal attributes first: Windows refuses to overwrite a hidden or read-only
        /// file ("Access to the path ... is denied", 2 reports, one on 0.2.75). HideScripts hides the scripts again at the end.</summary>
        public static bool UnpackAttribs => Environment.GetEnvironmentVariable("SSBU_ARENA_UNPACK_ATTRIBS") != "0";
        const FileAttributes Blocking = FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System;

        /// <summary>the file is there with an attribute that makes Windows refuse to overwrite it (hidden, read-only, system)</summary>
        public static bool Blocked(string f)
        {
            try { return File.Exists(f) && (File.GetAttributes(f) & Blocking) != 0; } catch { return false; }
        }

        /// <summary>first start of the downloaded launcher: the release's sources beside the exe (the setup screen goes on).
        /// root: (fix/launcher-0277) the folder to unpack into (an install whose setup files are missing: Installer.Root); null = beside the exe</summary>
        public static void FirstInstall(Release r, Action<string> say, string root = null)
        {
            root = root ?? Path.GetDirectoryName(ExePath);
            // (fix/install-launcher-errors) never into C:\, OpenSmash's folder, a temporary folder or one Windows does not let us write
            // (the setup screen offers a folder of its own first: MainWindow.Setup.cs StartSetup)
            string bad = InstallFolder.Problem(root);
            if (bad != null) throw new InvalidOperationException("SSBU Arena cannot be installed here: " + bad + ". Put SSBU-Arena.exe in a folder of its own " +
                                                                 "(for example Documents\\SSBU Arena) and start it from there.");
            say("Downloading SSBU Arena " + r.Version + "...");
            byte[] zip = Get(r.SourcesUrl ?? "https://codeload.github.com/" + GitHubRepo + "/zip/" + r.Commit, null, 600000);
            say("Unpacking...");
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            string top = null;
            int unhidden = 0;
            using (var z = new ZipArchive(new MemoryStream(zip)))
            {
                foreach (var e in z.Entries)
                {
                    int cut = e.FullName.IndexOf('/');
                    if (top == null && cut > 0) top = e.FullName.Substring(0, cut);
                    string rel = cut < 0 ? "" : e.FullName.Substring(cut + 1);
                    if (rel == "" || rel.EndsWith("/")) continue;
                    if (rel.Split('/').Any(p => p == ".." || p == "") || rel.Contains(":")) throw new InvalidDataException("unsafe path in the download: " + e.FullName);
                    string dst = Path.Combine(root, rel.Replace('/', '\\'));
                    try
                    {
                        // (fix/install-launcher-errors) a file in use (an antivirus scanning the new file, an indexer) is tried again
                        FileRetry.Do(() =>
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst));
                            if (UnpackAttribs && Blocked(dst)) { File.SetAttributes(dst, FileAttributes.Normal); unhidden++; }
                            e.ExtractToFile(dst, true);
                            files[rel] = Sha256(File.ReadAllBytes(dst));
                        });
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        // (fix/launcher-0277) a file of an earlier install that could not be replaced: not "Windows does not let SSBU
                        // Arena write there" (the folder can be written: the files before it were)
                        string msg = ex is UnauthorizedAccessException && Blocked(dst) ? SetupErrors.ExplainBlockedFile(ex, dst) : SetupErrors.Explain(ex, root);
                        throw new IOException("Unpacking " + rel + ": " + msg, ex.HResult);      // (no inner: the setup shows GetBaseException's message)
                    }
                }
            }
            if (unhidden > 0) Log("first install: " + unhidden + " file(s) of an earlier install were hidden or read-only: made normal, then replaced");
            // scripts/update.py's manifest (same format): the next update knows the commit and which files are the repository's
            var manifest = new Dictionary<string, object>
            {
                ["repo"] = GitHubRepo, ["branch"] = "main", ["commit"] = r.Commit, ["zip_top"] = top,
                ["updated"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), ["files"] = files,
            };
            Directory.CreateDirectory(Path.Combine(root, "build"));
            File.WriteAllText(Path.Combine(root, @"build\update_manifest.json"), new JavaScriptSerializer().Serialize(manifest));
            HideScripts(root);
        }

        /// <summary>(fix/setup-0278) the files of the install's own release that are missing (Installer.MissingTree: an antivirus or a
        /// cleaner removed them, runtime\game_data.h stopped a precheck), put back from the sources of the commit its manifest names
        /// (GitHub's zip of that commit; SSBU_ARENA_RELEASE's sources.zip when that release is the same commit). Only the missing
        /// files are written, each checked against the manifest's SHA-256 (a file that does not match is not written); nothing else
        /// is changed, the manifest neither. Returns how many were put back.</summary>
        public static int RepairSources(Release r, List<string> missing, Action<string> say, string root)
        {
            var m = Installer.Manifest();
            var files = Installer.ManifestFiles();
            string commit = m != null && m.TryGetValue("commit", out var c) ? c as string : null;
            if (files == null || string.IsNullOrEmpty(commit) || !System.Text.RegularExpressions.Regex.IsMatch(commit, "^[0-9a-f]{7,40}$"))
                throw new InvalidOperationException(@"SSBU Arena's files are incomplete and build\update_manifest.json does not say which release they are: download SSBU-Arena.exe again into a new folder.");
            var want = new HashSet<string>(missing, StringComparer.Ordinal);
            say("Downloading SSBU Arena's files (" + missing.Count + " missing)...");
            Log("sources repair: " + missing.Count + " file(s) missing (" + string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? ", ..." : "") + "): commit " + commit);
            string url = r != null && r.SourcesUrl != null && string.Equals(r.Commit, commit, StringComparison.OrdinalIgnoreCase)
                ? r.SourcesUrl : "https://codeload.github.com/" + GitHubRepo + "/zip/" + commit;
            byte[] zip = Get(url, null, 600000);
            int put = 0;
            var bad = new List<string>();
            using (var z = new ZipArchive(new MemoryStream(zip)))
            {
                foreach (var e in z.Entries)
                {
                    int cut = e.FullName.IndexOf('/');
                    string rel = cut < 0 ? "" : e.FullName.Substring(cut + 1);
                    if (rel == "" || rel.EndsWith("/") || !want.Contains(rel)) continue;
                    if (rel.Split('/').Any(p => p == ".." || p == "") || rel.Contains(":")) continue;
                    byte[] data;
                    using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); data = ms.ToArray(); }
                    if (files.TryGetValue(rel, out var h) && h is string hs && !string.Equals(Sha256(data), hs, StringComparison.OrdinalIgnoreCase)) { bad.Add(rel); continue; }
                    string dst = Path.Combine(root, rel.Replace('/', '\\'));
                    try
                    {
                        FileRetry.Do(() => { Directory.CreateDirectory(Path.GetDirectoryName(dst)); File.WriteAllBytes(dst, data); });
                    }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    {
                        throw new IOException("Putting back " + rel + ": " + SetupErrors.Explain(ex, root), ex.HResult);
                    }
                    put++;
                    want.Remove(rel);
                }
            }
            Log("sources repair: " + put + " put back" + (bad.Count > 0 ? ", " + bad.Count + " not matching the manifest (not written): " + string.Join(", ", bad.Take(6)) : "") +
                (want.Count > bad.Count ? ", " + (want.Count - bad.Count) + " not in the download" : ""));
            if (put > 0) HideScripts(root);
            return put;
        }
    }
}
