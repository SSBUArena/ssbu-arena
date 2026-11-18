// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.52) The OpenSmash tab: OpenSmash Ultimate is the full-game project that shares SSBU Arena's accounts (its launcher has a tab
// that opens Arena). A click starts OpenSmash's own launcher when it is installed on this PC, else opens its download page in the
// browser. Nothing is started without the click, nothing is downloaded here.
// Where OpenSmash is (its repo, OpenSmash.Launcher ProjectSetup.cs / OpenSmash.Avalonia Core/Project.cs): the release's
// OpenSmash.exe sets the project up in a folder of the player's choice, copies itself there as OpenSmash.exe and remembers the
// folder in %LOCALAPPDATA%\OpenSmash\project_folder.txt (OPENSMASH_SETTINGS_DIR moves that file); its shortcuts
// (OpenSmash-Launcher.lnk, OpenSmash.lnk before) on the desktop and in the Start menu folder OpenSmash point at that exe; a
// build of the newer launcher is OpenSmash.Avalonia.exe in the same folder. Looked for in this order:
//   1. launcher.ini opensmash_path = <the folder, or the launcher's exe> (written by hand when the rest does not find it)
//   2. the remembered folder; 3. the shortcuts.
// SSBU_OPENSMASH_TAB=0 hides the tab.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;

namespace Arena
{
    static class OpenSmash
    {
        /// <summary>the page a player without OpenSmash gets: the install section of its site (opensmashultimate.com sends there)</summary>
        public const string DownloadPage = "https://denkishi.github.io/opensmash-site/#install";
        static readonly string[] LauncherNames = { "OpenSmash.exe", "OpenSmash.Avalonia.exe" };
        static readonly string[] ShortcutNames = { "OpenSmash-Launcher.lnk", "OpenSmash.lnk" };

        /// <summary>SSBU_OPENSMASH_TAB=0 (or off) hides the tab</summary>
        public static bool TabOn => !(Environment.GetEnvironmentVariable("SSBU_OPENSMASH_TAB") is string v && (v.Trim() == "0" || v.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)));

        /// <summary>OpenSmash's launcher on this PC, or null; how it was found in <paramref name="how"/></summary>
        public static string Find(out string how)
        {
            how = "";
            string set = (Core.OpenSmashPath ?? "").Trim().Trim('"');
            if (set != "")
            {
                string exe = InFolderOrExe(set);
                how = "launcher.ini opensmash_path";
                if (exe != null) return exe;
                Log("opensmash_path = " + set + ": no OpenSmash launcher there; looking for it elsewhere");
            }
            try
            {
                string dir = Environment.GetEnvironmentVariable("OPENSMASH_SETTINGS_DIR");
                if (string.IsNullOrEmpty(dir)) dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenSmash");
                string file = Path.Combine(dir, "project_folder.txt");
                if (File.Exists(file))
                {
                    string folder = (File.ReadLines(file, Encoding.UTF8).FirstOrDefault() ?? "").Trim().Trim('"');
                    string exe = folder == "" ? null : InFolderOrExe(folder);
                    how = "OpenSmash's own setup (" + file + ")";
                    if (exe != null) return exe;
                }
            }
            catch { }
            foreach (var place in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonPrograms })
            {
                string root;
                try { root = Environment.GetFolderPath(place); } catch { continue; }
                if (string.IsNullOrEmpty(root)) continue;
                foreach (string sub in new[] { "OpenSmash", "" })
                    foreach (string name in ShortcutNames)
                    {
                        string lnk = Path.Combine(root, sub, name);
                        string target = File.Exists(lnk) ? ShortcutTarget(lnk) : null;
                        if (target != null && File.Exists(target) && LauncherNames.Contains(Path.GetFileName(target), StringComparer.OrdinalIgnoreCase))
                        {
                            how = "the shortcut " + lnk;
                            return target;
                        }
                    }
            }
            how = "";
            return null;
        }

        /// <summary>the launcher in a folder (OpenSmash.exe first), or the path itself when it is one of the launcher's exes</summary>
        static string InFolderOrExe(string p)
        {
            try
            {
                if (File.Exists(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(p);
                if (!Directory.Exists(p)) return null;
                foreach (string n in LauncherNames) { string e = Path.Combine(p, n); if (File.Exists(e)) return Path.GetFullPath(e); }
            }
            catch { }
            return null;
        }

        /// <summary>a .lnk's target through the Windows Script Host (no extra reference), or null</summary>
        static string ShortcutTarget(string lnk)
        {
            object shell = null, sc = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return null;
                shell = Activator.CreateInstance(t);
                sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                return sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty, null, sc, null) as string;
            }
            catch { return null; }
            finally
            {
                try { if (sc != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(sc); } catch { }
                try { if (shell != null) System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); } catch { }
            }
        }

        /// <summary>the click: OpenSmash's launcher from its own folder, or the download page</summary>
        public static void Open()
        {
            string exe = Find(out string how);
            if (exe != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe), UseShellExecute = true });
                    Log("OpenSmash: started " + exe + " (found by " + how + ")");
                    return;
                }
                catch (Exception ex) { Log("OpenSmash: " + exe + " did not start (" + ex.Message + "): its download page instead"); }
            }
            else Log("OpenSmash: not installed on this PC (launcher.ini opensmash_path sets its folder by hand): its download page");
            Core.Open(DownloadPage);
        }

        static void Log(string s) { Updater.Log(s); }      // build\launcher_update.log
    }

    public partial class MainWindow
    {
        void InitOpenSmash()
        {
            TabOpenSmash.Visibility = OpenSmash.TabOn ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>the tooltip says what a click will do, looked for again each time the pointer comes on the tab</summary>
        void OpenSmash_ToolTipOpening(object sender, System.Windows.Controls.ToolTipEventArgs e)
        {
            string exe = OpenSmash.Find(out _);
            TabOpenSmash.ToolTip = exe != null
                ? "Open OpenSmash Ultimate, the full game with your same account (" + exe + ")"
                : "Get OpenSmash Ultimate, the full game with your same account: opens its download page";
        }

        void OpenSmash_Click(object sender, RoutedEventArgs e) { OpenSmash.Open(); }
    }
}
