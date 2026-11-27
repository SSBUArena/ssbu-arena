// SPDX-License-Identifier: GPL-3.0-or-later
// (fix/install-launcher-errors) what the automatic reports of 0.2.73 / 0.2.74 showed, handled in one place:
//  - ProcessEnv: a launcher started with both "Path" and "PATH" in its environment (some launchers and shells pass both):
//    .NET Framework's ProcessStartInfo.EnvironmentVariables threw "Item has already been added. Key in dictionary: 'Path'
//    Key being added: 'PATH'" on every process the setup starts. The two are merged into one at the start.
//  - InstallFolder: the folder the downloaded launcher unpacks SSBU Arena into must be its own: not a drive's root (C:\ is
//    not writable), not OpenSmash Ultimate's folder (its files are in use and would be overwritten), not a temporary
//    folder (a browser's "open" runs the exe from one and deletes it later), and writable.
//  - SetupErrors: Windows' file errors said with what to do (a file in use, access denied, a full disk, a long path).
//  - FileRetry: a write that meets a file in use (an antivirus scan, an indexer) is tried again for a few seconds.
//  - Expected: what is no crash (a cancelled or closed operation, no internet): logged, never sent as a launcher crash.
// SSBU_ARENA_REPORT_ALL=1 sends those as crash reports again (a test); SSBU_ARENA_FOLDER_CHECK=0 skips the folder check.
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Arena
{
    public static class ProcessEnv
    {
        /// <summary>names of this process' environment that appear more than once ignoring case ("Path" and "PATH"): merged into
        /// one (PATH-like values joined without repeats, others: the first). Returns the names merged.</summary>
        public static List<string> MergeDuplicates()
        {
            var merged = new List<string>();
            try
            {
                var all = new List<KeyValuePair<string, string>>();
                foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
                    if (e.Key is string k) all.Add(new KeyValuePair<string, string>(k, e.Value as string ?? ""));
                foreach (var g in all.GroupBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                {
                    bool list = g.Key.Equals("PATH", StringComparison.OrdinalIgnoreCase) || g.Key.Equals("PATHEXT", StringComparison.OrdinalIgnoreCase) ||
                                g.Key.Equals("PSModulePath", StringComparison.OrdinalIgnoreCase);
                    string value = list
                        ? string.Join(";", g.SelectMany(kv => kv.Value.Split(';')).Where(p => p.Trim() != "").Distinct(StringComparer.OrdinalIgnoreCase))
                        : g.First().Value;
                    string name = g.Any(kv => kv.Key == g.Key.ToUpperInvariant()) ? g.Key.ToUpperInvariant() : g.First().Key;
                    for (int i = 0; i < 8 && Environment.GetEnvironmentVariable(name) != null; i++) Environment.SetEnvironmentVariable(name, null);
                    Environment.SetEnvironmentVariable(name, value);
                    merged.Add(string.Join("/", g.Select(kv => kv.Key)));
                }
            }
            catch { }
            return merged;
        }

        /// <summary>psi.EnvironmentVariables[name] = value; if the environment still cannot be copied (the duplicates came back),
        /// the value goes into this process' own environment, which the child inherits</summary>
        public static void Set(System.Diagnostics.ProcessStartInfo psi, string name, string value)
        {
            try { psi.EnvironmentVariables[name] = value; }
            catch (ArgumentException)
            {
                MergeDuplicates();
                try { psi.EnvironmentVariables[name] = value; }
                catch (ArgumentException) { Environment.SetEnvironmentVariable(name, value); }
            }
        }
    }

    public static class InstallFolder
    {
        static bool Off => Environment.GetEnvironmentVariable("SSBU_ARENA_FOLDER_CHECK") == "0";

        static string Norm(string p) { try { return Path.GetFullPath(p).TrimEnd('\\') + "\\"; } catch { return (p ?? "").TrimEnd('\\') + "\\"; } }
        static bool Under(string p, string dir) => !string.IsNullOrEmpty(dir) && Norm(p).StartsWith(Norm(dir), StringComparison.OrdinalIgnoreCase);

        /// <summary>an OpenSmash Ultimate (or another SSBU recompilation's) install: its own files would be overwritten</summary>
        public static bool IsOtherInstall(string dir)
        {
            try
            {
                if (SharedData.IsOpenSmash(dir)) return true;
                if (File.Exists(Path.Combine(dir, "OpenSmash.exe")) || File.Exists(Path.Combine(dir, @"tools\install_windowsmash.ps1"))) return true;
                if (Directory.Exists(Path.Combine(dir, @"dump\modules"))) return true;
            }
            catch { }
            return false;
        }

        /// <summary>why SSBU Arena cannot be unpacked into this folder (the downloaded exe's), or null when it can</summary>
        public static string Problem(string dir)
        {
            if (Off || string.IsNullOrEmpty(dir)) return null;
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\');
                string root = Path.GetPathRoot(full)?.TrimEnd('\\');
                if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                    return "the launcher is at the top of the drive " + root + "\\ (Windows does not let programs write there)";
                if (Under(full, Path.GetTempPath()) || full.IndexOf(@"\MicrosoftEdgeDownloads\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    full.IndexOf(@"\scoped_dir", StringComparison.OrdinalIgnoreCase) >= 0 || full.IndexOf(@"\Temporary Internet Files\", StringComparison.OrdinalIgnoreCase) >= 0)
                    return "the launcher runs from a temporary folder (opened straight from the browser or from inside a ZIP): Windows deletes it later";
                foreach (var sys in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                                            Environment.GetFolderPath(Environment.SpecialFolder.Windows) })
                    if (Under(full, sys)) return "the launcher is in " + sys + " (only administrators can write there)";
                if (IsOtherInstall(full))
                    return "the launcher is inside OpenSmash Ultimate's folder (SSBU Arena's files would overwrite OpenSmash's)";
                string shell = ShellFolder(full);
                if (shell != null)
                    return "the launcher is right in your " + shell + " folder, not in a folder of its own (SSBU Arena puts its folders, " +
                           "config, scripts, build and others, beside the exe, among your own files)";
                if (!Writable(full)) return "Windows does not let SSBU Arena write in " + full;
            }
            catch (Exception e) { return "the folder cannot be used (" + e.Message + ")"; }
            return null;
        }

        /// <summary>(fix/setup-0278) SSBU_ARENA_SHELL_FOLDER=0: as 0.2.77, the downloaded exe unpacks SSBU Arena wherever it is. Otherwise
        /// the user's own folders themselves (Downloads, Desktop, Documents, the user folder, OneDrive, Music / Pictures / Videos,
        /// C:\Users) are refused as the folder SSBU Arena is unpacked into: the setup offers "&lt;that folder&gt;\SSBU Arena" instead
        /// (OfferOwnFolder, InstallFolder.Suggest). A launcher in Downloads crashed at its start on a file named "config" there
        /// (0.2.77, Core.Load), and an install right in Downloads mixed its build folder with the player's files. Only the folder
        /// itself: Downloads\SSBU Arena and the like are fine. An install already made in such a folder is not moved.</summary>
        static bool ShellCheck => Environment.GetEnvironmentVariable("SSBU_ARENA_SHELL_FOLDER") != "0";

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SHGetKnownFolderPath([System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPStruct)] Guid id,
                                               uint flags, IntPtr token, out IntPtr path);

        static string KnownFolder(string guid)
        {
            try
            {
                if (SHGetKnownFolderPath(new Guid(guid), 0, IntPtr.Zero, out IntPtr p) != 0) return null;
                try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(p); }
                finally { System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p); }
            }
            catch { return null; }
        }

        /// <summary>the name of the user's folder this is ("Downloads", "Desktop"...), or null (any other folder, or the check off)</summary>
        public static string ShellFolder(string dir)
        {
            if (!ShellCheck || string.IsNullOrEmpty(dir)) return null;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var named = new List<Tuple<string, string>>
            {
                Tuple.Create("Downloads", KnownFolder("374DE290-123F-4565-9164-39C4925E467B")),
                Tuple.Create("Downloads", string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads")),
                Tuple.Create("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
                Tuple.Create("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)),
                Tuple.Create("Desktop", string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Desktop")),
                Tuple.Create("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
                Tuple.Create("Documents", Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments)),
                Tuple.Create("Documents", string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Documents")),
                Tuple.Create("Music", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
                Tuple.Create("Pictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
                Tuple.Create("Videos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)),
                Tuple.Create("user", home),
                Tuple.Create("Users", string.IsNullOrEmpty(home) ? null : Path.GetDirectoryName(home.TrimEnd('\\'))),
                Tuple.Create("OneDrive", Environment.GetEnvironmentVariable("OneDrive")),
                Tuple.Create("OneDrive", Environment.GetEnvironmentVariable("OneDriveConsumer")),
                Tuple.Create("OneDrive", Environment.GetEnvironmentVariable("OneDriveCommercial")),
            };
            string me = Norm(dir);
            foreach (var n in named)
                if (!string.IsNullOrEmpty(n.Item2) && string.Equals(Norm(n.Item2), me, StringComparison.OrdinalIgnoreCase)) return n.Item1;
            return null;
        }

        public static bool Writable(string dir)
        {
            try
            {
                string f = Path.Combine(dir, ".ssbu-arena-write-test-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                File.WriteAllText(f, "x");
                File.Delete(f);
                return true;
            }
            catch { return false; }
        }

        /// <summary>a folder of its own for SSBU Arena, next to where the player put the exe when that is fine, else in the user's
        /// folder: the exe's folder\SSBU Arena, OpenSmash's parent\SSBU Arena, C:\SSBU Arena, %USERPROFILE%\SSBU Arena</summary>
        public static string Suggest(string dir)
        {
            var c = new List<string>();
            try
            {
                string full = Path.GetFullPath(dir).TrimEnd('\\');
                string root = Path.GetPathRoot(full)?.TrimEnd('\\');
                bool temp = Problem(dir)?.Contains("temporary") == true;
                bool sysdir = Problem(dir)?.Contains("administrators") == true;
                if (!temp && !sysdir)
                {
                    if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) c.Add(Path.Combine(root + "\\", "SSBU Arena"));
                    else if (IsOtherInstall(full)) { var up = Path.GetDirectoryName(full); if (up != null) c.Add(Path.Combine(up, "SSBU Arena")); }
                    else c.Add(Path.Combine(full, "SSBU Arena"));
                }
            }
            catch { }
            c.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "SSBU Arena"));
            foreach (var d in c)
            {
                try
                {
                    if (IsOtherInstall(d)) continue;
                    Directory.CreateDirectory(d);
                    if (Writable(d) && Problem(d) == null) return d;
                }
                catch { }
            }
            return null;
        }
    }

    public static class FileRetry
    {
        /// <summary>a sharing violation (another program has the file open) or a short lock: tried again for about 5 s</summary>
        public static void Do(Action a, int tries = 10, int waitMs = 500)
        {
            for (int i = 1; ; i++)
            {
                try { a(); return; }
                catch (IOException e) when (i < tries && SetupErrors.InUse(e)) { Thread.Sleep(waitMs); }
                catch (UnauthorizedAccessException) when (i < 3) { Thread.Sleep(waitMs); }      // (an antivirus holding a new file a moment)
            }
        }
    }

    public static class SetupErrors
    {
        public static bool InUse(Exception e) => e is IOException && ((e.HResult & 0xFFFF) == 32 || (e.HResult & 0xFFFF) == 33);       // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        public static bool DiskFull(Exception e) => e is IOException && ((e.HResult & 0xFFFF) == 112 || (e.HResult & 0xFFFF) == 39);    // ERROR_DISK_FULL, ERROR_HANDLE_DISK_FULL

        /// <summary>(fix/launcher-0277) access denied on a file already there that is hidden, read-only or system (a file of an
        /// earlier install: Updater.Blocked), when the launcher could not make it normal (SSBU_ARENA_UNPACK_ATTRIBS=0, or Windows
        /// refused that too): the folder itself is fine</summary>
        public static string ExplainBlockedFile(Exception ex, string file)
        {
            var e = ex?.GetBaseException() ?? ex;
            string attrs = "";
            try { attrs = (File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.ReadOnly | FileAttributes.System)).ToString().ToLowerInvariant(); } catch { }
            return (e?.Message ?? "") + " The file is there from an earlier install and is marked " + attrs + ", so Windows does not let it be replaced " +
                   "(the folder itself is fine). Delete " + file + " (or clear its Hidden / Read-only boxes in its Properties), then press Retry.";
        }

        /// <summary>the message for the player: what happened, and what to do (English, like the rest of the launcher)</summary>
        public static string Explain(Exception ex, string folder = null)
        {
            var e = ex?.GetBaseException() ?? ex;
            if (e == null) return "";
            string where = folder != null ? " (" + folder + ")" : "";
            if (InUse(e))
                return e.Message + " Close the program that has it open (OpenSmash Ultimate, another launcher, an open game, or an antivirus scan), then press Retry. " +
                       "If it is inside OpenSmash's folder, put SSBU-Arena.exe in a folder of its own instead.";
            if (e is UnauthorizedAccessException)
                return e.Message + " Windows does not let SSBU Arena write there" + where + ". Put SSBU-Arena.exe in a folder of your own (for example Documents\\SSBU Arena) " +
                       "and start it from there; if the folder is fine, an antivirus (Controlled folder access in Windows Security) may be blocking it: allow SSBU Arena, then press Retry.";
            if (DiskFull(e))
                return e.Message + " The drive is full" + where + ": free some space (SSBU Arena needs about 25 GB), then press Retry.";
            if (e is PathTooLongException)
                return e.Message + " The folder's path is too long: put SSBU-Arena.exe in a shorter folder, for example C:\\Games\\SSBU Arena.";
            if (e is FileNotFoundException || e is DirectoryNotFoundException || (e is Win32Exception w && w.NativeErrorCode == 2))
                return e.Message + ". A file of the setup is missing: an antivirus may have removed it, or the folder was moved. Press Retry: the setup downloads what is missing.";
            if (e is Win32Exception w2 && (w2.NativeErrorCode == 4551 || w2.NativeErrorCode == 1260 || w2.NativeErrorCode == 225))
                return e.Message + ". Windows blocked a program of the setup (Smart App Control, an Application Control policy or an antivirus). Allow it, then press Retry.";
            if (Expected.IsNetwork(e))
                return e.Message + ". Check the internet connection (and that no firewall blocks SSBU Arena), then press Retry.";
            return e.Message;
        }
    }

    /// <summary>exceptions that are no crash of the launcher: logged in launcher_crash.log, never a report or an error window</summary>
    public static class Expected
    {
        public static bool ReportAll => Environment.GetEnvironmentVariable("SSBU_ARENA_REPORT_ALL") == "1";

        static IEnumerable<Exception> All(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                yield return e;
                if (e is AggregateException a) foreach (var i in a.InnerExceptions) foreach (var x in All(i)) yield return x;
            }
        }

        /// <summary>no internet, a name that does not resolve, the server not answering: the player's network, not a bug</summary>
        public static bool IsNetwork(Exception ex) => ex != null && All(ex).Any(e =>
            e is ServerUnreachableException || e is SocketException ||
            (e is WebException w && w.Status != WebExceptionStatus.ProtocolError && w.Status != WebExceptionStatus.Success) ||
            e.GetType().FullName == "System.Net.Http.HttpRequestException" ||
            (e is IOException && e.InnerException is SocketException));

        /// <summary>a cancelled operation (the player's Cancel, a newer sign-in) or one whose socket was closed on purpose</summary>
        public static bool IsCancel(Exception ex) => ex != null && All(ex).Any(e => e is OperationCanceledException);

        /// <summary>only ObjectDisposedException inside: in a task nobody waited for, an operation whose object (a socket, a stream)
        /// was closed on purpose (the browser sign-in's listener when it is cancelled)</summary>
        public static bool IsDisposed(Exception ex) => ex != null && All(ex).Any(e => e is ObjectDisposedException) &&
                                                       All(ex).All(e => e is AggregateException || e is ObjectDisposedException);

        /// <summary>null: a real error (reported); else what kind of expected one it is ("network", "cancelled", "closed");
        /// task: the error of a task nobody waited for (a closed object counts as expected only there)</summary>
        public static string Kind(Exception ex, bool task)
        {
            if (ReportAll || ex == null) return null;
            if (IsCancel(ex)) return "cancelled";
            if (IsNetwork(ex)) return "network";
            if (task && IsDisposed(ex)) return "closed";
            return null;
        }
    }

    /// <summary>the Arena server (Supabase) did not answer: the player's network, never a launcher crash</summary>
    public sealed class ServerUnreachableException : Exception
    {
        public ServerUnreachableException(string message, Exception inner) : base(message, inner) { }
    }
}
