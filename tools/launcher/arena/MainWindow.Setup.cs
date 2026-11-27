// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: the setup screen (the work is in Installer.cs). Stage 1 downloads the release's sources (first
// install only) and prepares the tools; stage 2 shows the game files the scan found, each can be changed, and checks
// the PC; stage 3 runs install.py with its steps, one bar, the time left and the log. install.py is resumable: after an
// error or Stop the button says Resume. At the end the launcher starts again on the installed game.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Arena
{
    public partial class MainWindow
    {
        sealed class SetupStep
        {
            public string Id, Title, State = "wait";
            public double Est = 1, Secs, Done, Total;
            public DateTime T0;
        }

        string setupPy;
        Found found;
        SetupChoice choice;
        Process setupProc;
        bool setupRunning, setupOk, setupStopped, setupDownload;
        List<SetupStep> setupSteps = new List<SetupStep>();
        DateTime setupStart;
        DispatcherTimer setupTimer, precheckLater;
        readonly Queue<string> setupLog = new Queue<string>();

        /// <summary>the setup screen; download: the release's sources first (the launcher was downloaded alone)</summary>
        void StartSetup(bool download)
        {
            // (fix/install-launcher-errors) the setup without SSBU Arena's files beside the exe (Settings > Run the setup again after
            // "Not now", Retry, an antivirus that removed them): they are downloaded first, never PowerShell on a missing script
            // (exit code -196608 on 8 PCs with 0.2.73 / 0.2.74)
            if (!download && NeedSources) { Updater.Log("setup: SSBU Arena's files are not in " + Installer.Root + (Updater.Installed ? " (" + MissingSources() + " missing)" : "") + ": downloaded first"); download = true; }
            if (download && !Updater.Installed)
            {
                string bad = InstallFolder.Problem(Installer.Root);
                if (bad != null) { OfferOwnFolder(bad); return; }
            }
            setupDownload = download;
            LauncherCrash.Where = download ? "setup stage 1: download and tools" : "setup stage 1: tools and game file scan";
            SetupReportBox.Visibility = Visibility.Collapsed;
            SetupOverlay.Visibility = Visibility.Visible;
            SetupStageOn(1);
            SetupIntro.Text = "SSBU Arena runs your own copy of Super Smash Bros. Ultimate (13.0.2, 13.0.4 or 13.0.5) natively on this PC. " +
                              "Nothing of the game is downloaded: the setup builds it from your files, here. About 25 GB of space; it takes a while once.";
            SetupBusy.Visibility = Visibility.Visible; SetupFiles.Visibility = Visibility.Collapsed; SetupRun.Visibility = Visibility.Collapsed;
            SetupFooter.Visibility = Visibility.Collapsed; SetupBusyButtons.Visibility = Visibility.Collapsed;
            SetupBusyTitle.Text = download ? "DOWNLOADING SSBU ARENA" : "PREPARING THE TOOLS";
            SetupBusyDetail.Foreground = Res("Muted");
            SetupBusyDetail.Text = download ? "SSBU Arena " + release?.Version + " from GitHub..." : "A private Python and its packages, inside this folder only (nothing is installed on Windows).";
            SetupBusyLog.Text = "";
            SetBar(SetupBusyDone, SetupBusyLeft, 0.05);
            Action<string> say = s => Dispatcher.BeginInvoke(new Action(() => SetupBusyLog.Text = s));
            var rel = release;
            Task.Run(() =>
            {
                if (download)
                {
                    if (rel == null) rel = Updater.Latest() ?? throw new InvalidOperationException("No SSBU Arena release was found on GitHub: check the internet connection, then press Retry.");
                    Dispatcher.BeginInvoke(new Action(() => { release = release ?? rel; }));
                    Updater.FirstInstall(rel, s => Dispatcher.BeginInvoke(new Action(() => SetupBusyDetail.Text = s)), Installer.Root);
                    Dispatcher.Invoke(() =>
                    {
                        SetupBusyTitle.Text = "PREPARING THE TOOLS"; SetBar(SetupBusyDone, SetupBusyLeft, 0.35);
                        SetupBusyDetail.Text = "A private Python and its packages, inside this folder only (nothing is installed on Windows).";
                    });
                }
                // (fix/setup-0278, SSBU_ARENA_TREE_REPAIR) any other file of the release missing (runtime\game_data.h stopped a precheck):
                // put back from the install's own release, checked against its manifest, before the tools and the setup run
                if (!download && TreeRepair)
                {
                    var miss = Installer.MissingTree();
                    if (miss.Count > 0)
                    {
                        treeRepairs++;
                        Dispatcher.Invoke(() => { SetupBusyTitle.Text = "REPAIRING SSBU ARENA"; SetupBusyDetail.Text = miss.Count + " of SSBU Arena's files are missing (" + string.Join(", ", miss.Take(3).Select(f => f.Replace('/', '\\'))) + (miss.Count > 3 ? ", ..." : "") + "): downloading them again..."; });
                        Updater.RepairSources(rel, miss, s => Dispatcher.BeginInvoke(new Action(() => SetupBusyDetail.Text = s)), Installer.Root);
                        var still = Installer.MissingTree();
                        if (still.Count > 0)
                            throw new FileNotFoundException(still.Count + " of SSBU Arena's files are still missing after they were downloaded again (" + string.Join(", ", still.Take(3).Select(f => f.Replace('/', '\\'))) +
                                                            (still.Count > 3 ? ", ..." : "") + "): an antivirus may be removing them. Allow SSBU Arena's folder (" + Installer.Root + ") in it, then press Retry.");
                    }
                }
                string py = Installer.PrepareTools(say);
                // (fix/install-launcher-errors) an install whose game was never built still has the sources it was first unpacked with
                // (0.2.49's install.py did not know --shared-data): brought to the release before the scan
                if (!download && rel != null && !Updater.DevCopy && Updater.GameOutdated(rel) && !File.Exists(Core.Exe))
                {
                    Dispatcher.Invoke(() => { SetupBusyTitle.Text = "UPDATING SSBU ARENA"; SetupBusyDetail.Text = "SSBU Arena's files to " + rel.Version + " before the setup..."; });
                    int uc = Installer.UpdateSources(py, say);
                    Updater.Log("setup: the sources brought to " + rel.Version + " first (scripts\\update.py ended with " + uc + ")");
                }
                Dispatcher.Invoke(() => { SetupBusyTitle.Text = "LOOKING FOR YOUR GAME FILES"; SetBar(SetupBusyDone, SetupBusyLeft, 0.8); SetupBusyDetail.Text = "Emulator folders, Downloads, Desktop and the other usual places: only names and headers are read."; });
                return Tuple.Create(py, Installer.Scan(py));
            }).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    var b = t.Exception.GetBaseException();
                    // (fix/install-launcher-errors) Windows' file errors with what to do (a file in use, access denied, a full disk)
                    string msg = b is IOException && !(b is FileNotFoundException) || b is UnauthorizedAccessException || b is System.ComponentModel.Win32Exception ||
                                 Expected.IsNetwork(b) ? SetupErrors.Explain(b, Installer.Root) : b.Message;
                    SetupBusyTitle.Text = "THE SETUP STOPPED";
                    SetupBusyDetail.Foreground = Res("Bad");
                    SetupBusyDetail.Text = msg;
                    SetupBusyButtons.Visibility = Visibility.Visible;
                    Updater.Log("setup: stage 1 stopped: " + msg);
                    // (no internet is no setup error to report: the player's network)
                    if (!Expected.IsNetwork(b) || Expected.ReportAll)
                        SetupErrorReport(setupDownload ? "setup download/tools" : "setup tools/scan", b.GetType().Name + ": " + msg,
                                         Path.Combine(Installer.Root, @"build\bootstrap.log"));      // (0.2.73)
                    return;
                }
                setupPy = t.Result.Item1;
                found = t.Result.Item2;
                ShowSetupFiles(true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SetupRetry_Click(object sender, RoutedEventArgs e)
        {
            StartSetup(NeedSources);
        }

        /// <summary>(fix/launcher-0277) SSBU_ARENA_SOURCES_REPAIR=0: as 0.2.76, only a folder without SSBU Arena (no scripts\setup.py)
        /// gets the release's files downloaded. Otherwise also an install whose setup files are missing (scripts\bootstrap.ps1,
        /// install.py, find_inputs.py: Installer.SourcesThere; an antivirus removed them, an unpacking that stopped half-way): there
        /// Retry repeated "bootstrap.ps1 is missing" for ever (0.2.75). A developer's git checkout is never changed.</summary>
        static bool SourcesRepair => Environment.GetEnvironmentVariable("SSBU_ARENA_SOURCES_REPAIR") != "0";
        static bool NeedSources => !Installer.SourcesThere && (!Updater.Installed || SourcesRepair && !Updater.DevCopy);
        /// <summary>(fix/setup-0278) SSBU_ARENA_TREE_REPAIR=0 (or SSBU_ARENA_SOURCES_REPAIR=0): as 0.2.77, only the three setup files above
        /// are noticed. Otherwise every file of the release's sources (Installer.MissingTree, build\update_manifest.json) is checked
        /// at stage 1 and before install.py starts (Install, Resume): missing ones are downloaded again from the install's own
        /// release (Updater.RepairSources) instead of a precheck stopped on runtime\game_data.h at every Resume. An installed copy
        /// only, never a git checkout; at most twice per launcher start, then install.py says what is missing.</summary>
        static bool TreeRepair => SourcesRepair && Environment.GetEnvironmentVariable("SSBU_ARENA_TREE_REPAIR") != "0" && Updater.Installed && !Updater.DevCopy;
        static int treeRepairs;
        static string MissingSources() =>
            string.Join(", ", new[] { @"scripts\bootstrap.ps1", @"scripts\install.py", @"scripts\find_inputs.py" }.Where(f => !File.Exists(Path.Combine(Installer.Root, f))));

        /// <summary>(fix/install-launcher-errors) the exe is where SSBU Arena cannot be unpacked (C:\, OpenSmash's folder, a temporary
        /// folder, one not writable: InstallFolder.Problem): it copies itself into a folder of its own and goes on from there</summary>
        void OfferOwnFolder(string why)
        {
            SetupOverlay.Visibility = Visibility.Collapsed;
            string dest = InstallFolder.Suggest(Installer.Root);
            Updater.Log("setup: not here: " + why + "; a folder of its own: " + (dest ?? "none found"));
            if (dest == null)
            {
                Show("A folder of its own", "SSBU Arena cannot be installed here: " + why + ".\n\nPut SSBU-Arena.exe in a new empty folder (for example " +
                     "Documents\\SSBU Arena) and start it from there.", null, ("OK", null, true));
                return;
            }
            Show("A folder of its own", "SSBU Arena cannot be installed here: " + why + ".\n\nIt can copy itself to\n" + dest +
                 "\nand install there (about 25 GB with the game). Start it from that folder from now on; the copy here is not needed.", null,
                 ("Move and install", () => MoveAndInstall(dest), true), ("Cancel", null, false));
        }

        void MoveAndInstall(string dest)
        {
            try
            {
                string exe = Path.Combine(dest, Path.GetFileName(Updater.ExePath));
                FileRetry.Do(() => File.Copy(Updater.ExePath, exe, true));
                Updater.Log("setup: copied to " + exe + ", the install goes on there");
                Process.Start(new ProcessStartInfo(exe, "--moved --install") { UseShellExecute = false, WorkingDirectory = dest });
                try { Process.Start("explorer.exe", "\"" + dest + "\""); } catch { }
                Application.Current.Shutdown();
            }
            catch (Exception ex) { Show("A folder of its own", SetupErrors.Explain(ex, dest), null, ("OK", null, true)); }
        }

        void SetupStageOn(int n)
        {
            var all = new[] { SetupStage1, SetupStage2, SetupStage3 };
            for (int i = 0; i < 3; i++) all[i].Foreground = i + 1 == n ? Res("Accent") : i + 1 < n ? Res("Text") : Res("Dim");
            SetupScroll.ScrollToTop();      // (hotfix setup-scroll) each stage from its top
        }

        static void SetBar(ColumnDefinition done, ColumnDefinition left, double f)
        {
            f = Math.Max(0, Math.Min(1, f));
            done.Width = new GridLength(f, GridUnitType.Star);
            left.Width = new GridLength(1 - f, GridUnitType.Star);
        }

        // ---------------------------------------------------------------- stage 2: the game files
        void ShowSetupFiles(bool fill)
        {
            LauncherCrash.Where = "setup stage 2: game files";
            SetupStageOn(2);
            SetupIntro.Text = "These are the files found on this PC: check them, change any with File or Folder. " +
                              "A dump already decrypted on your Switch (a folder with exefs and romfs, e.g. from nxdumptool) needs no prod.keys and no update.";
            SetupBusy.Visibility = Visibility.Collapsed; SetupRun.Visibility = Visibility.Collapsed; SetupFiles.Visibility = Visibility.Visible;
            SetupFooter.Visibility = Visibility.Visible; SetupLogButton.Visibility = Visibility.Collapsed; SetupExisting.Visibility = Visibility.Visible;
            SetupBack.Visibility = Visibility.Collapsed; SetupStop.Visibility = Visibility.Collapsed; SetupTools.Visibility = Visibility.Collapsed;
            SetupGoText.Text = "INSTALL";
            if (fill && found != null)
            {
                bool was = loading;
                loading = true;
                SetupGame.Text = found.Dump ?? found.Base ?? "";
                SetupUpdate.Text = found.Update ?? "";
                SetupKeys.Text = found.Keys ?? "";
                SetupFirmware.Text = found.Firmware ?? "";
                SetupLocation.Text = Core.Repo != null ? Core.Build : Installer.DefaultLocation;
                SetupDlc.IsChecked = found.Dlc.Count > 0;
                loading = was;
            }
            CheckSetupFields();
            RunPrecheck();
            // (shared game data) OpenSmash Ultimate on this PC: asked once, here, before INSTALL (the PC check and the fields follow)
            if (fill && Core.SharedData == "" && !sharedAsked)
                Task.Run(() => SharedData.Candidate()).ContinueWith(t =>
                {
                    if (t.Result != null && SetupFiles.Visibility == Visibility.Visible && !setupRunning)
                        SharedAsk(t.Result, yes => { CheckSetupFields(); RunPrecheck(); });
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>(shared game data) the OpenSmash install whose game files this setup uses (the player said yes), or null</summary>
        string SetupShared() => Core.SharedData == "on" ? SharedData.Candidate()?.Item1 : null;

        void SetupField_Changed(object sender, RoutedEventArgs e) { if (!loading) CheckSetupFields(); }

        void SetupLocation_Changed(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            CheckSetupFields();
            // the disk check again, once the typing stops
            precheckLater?.Stop();
            precheckLater = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            precheckLater.Tick += (s, a) => { precheckLater.Stop(); RunPrecheck(); };
            precheckLater.Start();
        }

        static string Clean(TextBox b) => b.Text.Trim().Trim('"');

        static void FieldState(ContentControl mark, TextBlock text, string kind, string msg)
        {
            mark.Content = Mark(kind, 20);
            text.Text = msg;
            text.Foreground = kind == "bad" ? Res("Bad") : Res("Muted");
        }

        /// <summary>each field's state line; Install only when the game can be built from them</summary>
        bool CheckSetupFields()
        {
            string game = Clean(SetupGame), upd = Clean(SetupUpdate), keys = Clean(SetupKeys), fw = Clean(SetupFirmware);
            bool dump = Installer.IsDump(game), ok = true;
            string shared = SetupShared();
            if (shared != null)
            {
                // (shared game data) nothing of the game is unpacked: the fields stay as they are (used only if the link fails)
                SetupPackageFields.Visibility = Visibility.Collapsed;
                FieldState(SetupGameMark, SetupGameText, "ok", "Not needed: OpenSmash Ultimate's game files are used (no second copy).");
                SetupGameText.ToolTip = shared;
                if (fw == "") FieldState(SetupFirmwareMark, SetupFirmwareText, "ok", "Not needed: the system fonts come with OpenSmash's game files.");
                bool loc = Clean(SetupLocation) != "";
                SetupGo.IsEnabled = loc;
                return loc;
            }
            if (game == "") { FieldState(SetupGameMark, SetupGameText, "bad", "Choose your game: its .xci / .nsp / .nsz file, or a dump folder (exefs and romfs)."); ok = false; }
            else if (dump) FieldState(SetupGameMark, SetupGameText, "ok", "A decrypted dump: no prod.keys and no update needed.");
            else if (File.Exists(game)) FieldState(SetupGameMark, SetupGameText, "ok", "Your game package" + (string.Equals(game, found?.Base, StringComparison.OrdinalIgnoreCase) ? " (found on this PC)." : "."));
            else if (Directory.Exists(game)) FieldState(SetupGameMark, SetupGameText, "warn", "A folder without exefs and romfs: the setup looks inside it for the game package.");
            else { FieldState(SetupGameMark, SetupGameText, "bad", "Not found."); ok = false; }

            SetupPackageFields.Visibility = dump ? Visibility.Collapsed : Visibility.Visible;
            if (!dump)
            {
                if (upd == "") { FieldState(SetupUpdateMark, SetupUpdateText, "bad", "Needed: your game's update 13.0.2, 13.0.4 or 13.0.5 (.nsp / .nsz)."); ok = false; }
                // (fix/install-launcher-errors) the base game chosen as its own update: install.py stopped after unpacking it ("not an update")
                else if (string.Equals(upd, game, StringComparison.OrdinalIgnoreCase)) { FieldState(SetupUpdateMark, SetupUpdateText, "bad", "This is the base game, not its update: choose the update 13.0.2, 13.0.4 or 13.0.5 (a separate .nsp / .nsz, usually with [v1966080] or similar in its name)."); ok = false; }
                else if (File.Exists(upd) || Directory.Exists(upd))
                    FieldState(SetupUpdateMark, SetupUpdateText, "ok", string.Equals(upd, found?.Update, StringComparison.OrdinalIgnoreCase) && found?.Build != null ? "Update " + found.Build + " (found on this PC)." : "Your update.");
                else { FieldState(SetupUpdateMark, SetupUpdateText, "bad", "Not found."); ok = false; }

                if (keys == "") { FieldState(SetupKeysMark, SetupKeysText, "bad", "Needed to decrypt your game package. Or choose a decrypted dump folder above instead."); ok = false; }
                else if (File.Exists(keys)) FieldState(SetupKeysMark, SetupKeysText, "ok", "Used where it is: never copied, never sent anywhere.");
                else { FieldState(SetupKeysMark, SetupKeysText, "bad", "Not found."); ok = false; }

                int n = found?.Dlc.Count ?? 0;
                SetupDlc.IsEnabled = n > 0;
                SetupDlcTitle.Text = n == 0 ? "No DLC found" : "Add your DLC (" + n + " found)";
                SetupDlcText.Text = n == 0 ? "DLC can be added later in Settings > DLC." : "The DLC packages found next to your game: their fighters, stages and music.";
            }

            if (fw == "") FieldState(SetupFirmwareMark, SetupFirmwareText, "ok", "Not needed: open-source fonts are used (no Chinese, Japanese or Korean text).");
            else if (Directory.Exists(fw)) FieldState(SetupFirmwareMark, SetupFirmwareText, "ok", "Your console's own system fonts are taken from it.");
            else FieldState(SetupFirmwareMark, SetupFirmwareText, "warn", "Not found: the open-source fonts are used instead.");

            if (Clean(SetupLocation) == "") ok = false;
            SetupGo.IsEnabled = ok;
            return ok;
        }

        SetupChoice ReadChoice()
        {
            string game = Clean(SetupGame), fw = Clean(SetupFirmware);
            bool dump = Installer.IsDump(game);
            var c = new SetupChoice
            {
                Game = game,
                Update = dump ? null : Clean(SetupUpdate),
                Keys = dump ? null : Clean(SetupKeys),
                Firmware = Directory.Exists(fw) ? fw : null,
                Location = Clean(SetupLocation) == "" ? Installer.DefaultLocation : Clean(SetupLocation),
                Jobs = choice?.Jobs ?? 0,
                Shared = SetupShared() ?? (Core.SharedData == "off" ? "off" : null),
            };
            if (!dump && SetupDlc.IsChecked == true && found != null) c.Dlc = found.Dlc.ToList();
            return c;
        }

        void RunPrecheck()
        {
            if (setupPy == null) return;
            var c = ReadChoice();
            SetupPc.ItemsSource = new[] { CheckRow(true, "Checking this PC...", "busy") };
            Task.Run(() => Installer.Precheck(setupPy, c)).ContinueWith(t =>
            {
                if (t.IsFaulted)
                {
                    SetupPc.ItemsSource = new[] { CheckRow(false, "The PC check did not run: " + t.Exception.GetBaseException().Message, "warn") };
                    SetupErrorReport("setup PC check (precheck.py)", t.Exception.GetBaseException().GetType().Name + ": " + t.Exception.GetBaseException().Message, null);      // (0.2.73)
                    return;
                }
                var rows = new List<UIElement>();
                foreach (var p in t.Result)
                {
                    var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
                    sp.Children.Add(Mark(p.Level == "ok" ? "ok" : p.Level == "warn" ? "warn" : "bad", 20));
                    var txt = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
                    txt.Children.Add(new TextBlock { Text = p.What, FontSize = 15, FontWeight = FontWeights.SemiBold });
                    txt.Children.Add(new TextBlock { Text = p.Msg, FontSize = 14, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, MaxWidth = 280 });
                    sp.Children.Add(txt);
                    rows.Add(sp);
                    // the memory check suggests the compiler jobs this PC can hold ("--jobs 10")
                    var m = System.Text.RegularExpressions.Regex.Match(p.Msg ?? "", @"--jobs (\d+)");
                    if (p.What == "Memory" && m.Success) { if (choice == null) choice = new SetupChoice(); choice.Jobs = int.Parse(m.Groups[1].Value); }
                }
                SetupPc.ItemsSource = rows;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static string PickFile(string title, string filter)
        {
            var d = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
            return d.ShowDialog() == true ? d.FileName : null;
        }

        void SetupGameFile_Click(object sender, RoutedEventArgs e) { var f = PickFile("Your Super Smash Bros. Ultimate (base game)", "Game (*.xci;*.nsp;*.nsz;*.xcz)|*.xci;*.nsp;*.nsz;*.xcz|All files|*.*"); if (f != null) SetupGame.Text = f; }
        void SetupGameFolder_Click(object sender, RoutedEventArgs e) { var f = PickFolder("A dump of your game decrypted on the console (exefs and romfs inside), or a folder with your game package"); if (f != null) SetupGame.Text = f; }
        void SetupUpdateFile_Click(object sender, RoutedEventArgs e) { var f = PickFile("Your game's update 13.0.2, 13.0.4 or 13.0.5", "Update (*.nsp;*.nsz)|*.nsp;*.nsz|All files|*.*"); if (f != null) SetupUpdate.Text = f; }
        void SetupKeysFile_Click(object sender, RoutedEventArgs e) { var f = PickFile("Your prod.keys (dumped from your Switch)", "Keys (*.keys)|*.keys|All files|*.*"); if (f != null) SetupKeys.Text = f; }
        void SetupFirmware_Click(object sender, RoutedEventArgs e) { var f = PickFolder("Your firmware dump (a folder of .nca files), or the extracted system fonts"); if (f != null) SetupFirmware.Text = f; }
        void SetupLocation_Click(object sender, RoutedEventArgs e) { var f = PickFolder("Where the game is unpacked and built (about 25 GB)"); if (f != null) SetupLocation.Text = Path.Combine(f, "SSBU Arena"); }

        void SetupRescan_Click(object sender, RoutedEventArgs e)
        {
            if (setupPy == null) return;
            SetupPc.ItemsSource = new[] { CheckRow(true, "Looking for your game files...", "busy") };
            Task.Run(() => Installer.Scan(setupPy)).ContinueWith(t =>
            {
                if (!t.IsFaulted) found = t.Result;
                else SetupErrorReport("setup game file scan (find_inputs.py)", t.Exception.GetBaseException().GetType().Name + ": " + t.Exception.GetBaseException().Message, null);      // (0.2.73)
                ShowSetupFiles(!t.IsFaulted);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>an install already built elsewhere (another drive, an earlier install, a copy): the launcher uses it as it
        /// is; when the game files it was built from are not there any more, the setup goes on in it (only what is missing)</summary>
        void SetupExisting_Click(object sender, RoutedEventArgs e)
        {
            string f = PickFolder(@"The folder where SSBU Arena was already built (work\ssbu_native.exe and state.json inside, or a folder with build inside)");
            if (f == null) return;
            string b = Installer.ExistingBuild(f);
            if (b == null)
            {
                Show("Not an install", @"This folder has no built SSBU Arena: it needs work\ssbu_native.exe and state.json inside (or a build folder holding them). " +
                     "For a dump of your game (exefs and romfs), use Folder next to Your game.", null, ("OK", null, true));
                return;
            }
            if (Installer.GameFilesThere(b))
            {
                try { Installer.UseLocation(b); } catch (Exception ex) { Show("Install location", ex.Message, null, ("OK", null, true)); return; }
                Updater.Log("setup: an existing install is used: " + b);
                Show("Install found", "SSBU Arena is already built in\n" + b + "\n\nThe launcher uses it from now on: nothing is rebuilt.", null, ("Start", RestartLauncher, true));
                return;
            }
            SetupLocation.Text = b;
            Show("Install found", "SSBU Arena is built in\n" + b + "\nbut the unpacked game it was made from is not at its place any more. Choose your game above (a dump or your " +
                 "game package) and press Install: only what is missing is done, the build is kept.", null, ("OK", null, true));
        }

        // ---------------------------------------------------------------- stage 3: install.py
        void SetupGo_Click(object sender, RoutedEventArgs e)
        {
            if (setupRunning) return;
            if (setupOk) { RestartLauncher(); return; }
            LauncherCrash.Where = "setup: INSTALL pressed";
            if (SetupFiles.Visibility == Visibility.Visible)
            {
                if (!CheckSetupFields()) return;
                int jobs = choice?.Jobs ?? 0;
                choice = ReadChoice();
                choice.Jobs = jobs;
                // (shared game data) OpenSmash Ultimate on this PC: asked once whether its game files are used instead of a copy
                var cand = Core.SharedData == "" && !sharedAsked ? SharedData.Candidate() : null;
                if (cand != null) { SharedAsk(cand, yes => { choice.Shared = yes ? cand.Item1 : "off"; RunSetup(); }); return; }
                if (Core.SharedData == "on" && choice.Shared == null) choice.Shared = SharedData.FindOpenSmash() ?? "auto";
            }
            RunSetup();
        }

        void RunSetup()
        {
            // (fix/install-launcher-errors) the files went away since stage 1 (a browser's temporary folder emptied, an antivirus):
            // stage 1 again (download / tools) instead of "The system cannot find the file specified" at every Resume
            if (!Updater.DevCopy && (!Installer.SourcesThere || string.IsNullOrEmpty(setupPy) || !File.Exists(setupPy)))
            {
                Updater.Log("setup: " + (!Installer.SourcesThere ? "SSBU Arena's files" : "the setup's Python") + " missing at INSTALL: stage 1 again");
                StartSetup(NeedSources);
                return;
            }
            // (fix/setup-0278, SSBU_ARENA_TREE_REPAIR) another file of the release missing: stage 1 again, which downloads it
            if (TreeRepair && treeRepairs < 2)
            {
                var miss = Installer.MissingTree();
                if (miss.Count > 0)
                {
                    Updater.Log("setup: " + miss.Count + " of SSBU Arena's files missing at INSTALL (" + string.Join(", ", miss.Take(6)) + (miss.Count > 6 ? ", ..." : "") + "): stage 1 again");
                    StartSetup(NeedSources);
                    return;
                }
            }
            SetupStageOn(3);
            LauncherCrash.Where = "setup stage 3: install.py";
            SetupReportBox.Visibility = Visibility.Collapsed;
            SetupIntro.Text = "The setup unpacks your game, takes the prebuilt SSBU Arena runtime and builds the rest on this PC. " +
                              "You can stop it: it goes on from where it was.";
            SetupFiles.Visibility = Visibility.Collapsed; SetupRun.Visibility = Visibility.Visible;
            SetupLogButton.Visibility = Visibility.Visible; SetupExisting.Visibility = Visibility.Collapsed; SetupBack.Visibility = Visibility.Collapsed; SetupTools.Visibility = Visibility.Collapsed;
            SetupStop.Visibility = Visibility.Visible; SetupGo.IsEnabled = false; SetupGoText.Text = "INSTALLING";
            SetupError.Visibility = Visibility.Collapsed;
            SetupRunTitle.Text = "INSTALLING"; SetupPct.Text = "0%"; SetupEta.Text = ""; SetupRunDetail.Text = "Starting...";
            SetBar(SetupRunDone, SetupRunLeft, 0);
            setupSteps.Clear(); SetupSteps.ItemsSource = null; setupLog.Clear(); SetupLog.Text = "";
            setupOk = setupStopped = false;
            setupRunning = true;
            setupStart = DateTime.Now;
            try
            {
                // (0.2.73) inside the try: with OpenSmash's shared game files the prod.keys field may be empty, and Replace("") threw
                // ArgumentException out of the Install click, which closed the launcher before logs\installer.log was made
                // (Arguments' Path.GetFullPath can throw too, on a location with characters Windows does not take)
                string args = Installer.Arguments(choice);
                Updater.Log("setup: install.py " + (string.IsNullOrEmpty(choice.Keys) ? args : args.Replace(choice.Keys, "<prod.keys>")));
                setupProc = Installer.Install(setupPy, choice, l => Dispatcher.BeginInvoke(new Action(() => SetupLine(l))),
                                              c => Dispatcher.BeginInvoke(new Action(() => SetupEnded(c))));
            }
            catch (Exception ex) { setupRunning = false; SetupEnded(-1, ex.Message); return; }
            setupTimer?.Stop();
            setupTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            setupTimer.Tick += (s, a) => SetupProgress();
            setupTimer.Start();
        }

        Dictionary<string, object> setupDone;

        /// <summary>one line of install.py: "##ssbu {json}" events, any other line into the log box</summary>
        void SetupLine(string l)
        {
            if (!l.StartsWith("##ssbu "))
            {
                setupLog.Enqueue(l);
                while (setupLog.Count > 400) setupLog.Dequeue();
                SetupLog.Text = string.Join("\n", setupLog);
                SetupLog.ScrollToEnd();
                return;
            }
            Dictionary<string, object> ev;
            try { ev = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(l.Substring(7)); } catch { return; }
            string kind = ev.TryGetValue("ev", out var k) ? k as string : null;
            string id = ev.TryGetValue("id", out var i) ? i as string : null;
            var step = setupSteps.FirstOrDefault(s => s.Id == id);
            switch (kind)
            {
                case "plan":
                    setupSteps = ((System.Collections.ArrayList)ev["steps"]).Cast<Dictionary<string, object>>().Select(s => new SetupStep
                    {
                        Id = s["id"] as string, Title = s.TryGetValue("title", out var t) ? t as string : s["id"] as string,
                        Est = s.TryGetValue("est_s", out var est) ? Convert.ToDouble(est) : 1,
                    }).ToList();
                    break;
                case "step":
                    if (step == null) { step = new SetupStep { Id = id, Title = id }; setupSteps.Add(step); }
                    string st = ev.TryGetValue("state", out var sv) ? sv as string : "";
                    if (st == "start") { step.State = "run"; step.T0 = DateTime.Now; SetupRunDetail.Text = step.Title; }
                    else step.State = st;
                    if (ev.TryGetValue("secs", out var secs) && secs != null) step.Secs = Convert.ToDouble(secs);
                    break;
                case "progress":
                    if (step != null && ev.TryGetValue("total", out var tot) && Convert.ToDouble(tot) > 0)
                    {
                        step.Done = Convert.ToDouble(ev["done"]); step.Total = Convert.ToDouble(tot);
                        SetupRunDetail.Text = step.Title + string.Format("  ({0:N0} of {1:N0})", step.Done, step.Total);
                    }
                    break;
                case "error":
                    SetupError.Text = (ev.TryGetValue("msg", out var m) ? m as string : "The setup stopped.") +
                                      (ev.TryGetValue("hint", out var h) && !string.IsNullOrEmpty(h as string) ? "\n" + h : "");
                    SetupError.Visibility = Visibility.Visible;
                    SetupTools.Visibility = ev.TryGetValue("action", out var act) && act as string == "msvc" ? Visibility.Visible : Visibility.Collapsed;
                    break;
                case "done":
                    setupDone = ev;
                    break;
            }
            ShowSetupSteps();
            SetupProgress();
        }

        void ShowSetupSteps()
        {
            SetupSteps.ItemsSource = setupSteps.Where(s => s.Est > 0).Select(s =>
            {
                string mark = s.State == "done" || s.State == "dry" ? "ok" : s.State == "skip" ? "good" : s.State == "warn" ? "warn" : s.State == "fail" ? "bad" : s.State == "run" ? "busy" : null;      // (0.2.50) warn: a step that did not make its files (the Arena menu), shown as such
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
                sp.Children.Add(mark != null ? Mark(mark, 20) : new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderBrush = Res("Line"), BorderThickness = new Thickness(2) });
                sp.Children.Add(new TextBlock
                {
                    Text = s.Title + (s.State == "skip" ? "  (already done)" : s.State == "warn" ? "  (not made: see the log)" : s.Secs >= 1 && s.State == "done" ? "  " + Dur(s.Secs) : ""),
                    FontSize = 16, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = s.State == "wait" ? Res("Dim") : s.State == "fail" ? Res("Bad") : Res("Text"),
                    FontWeight = s.State == "run" ? FontWeights.SemiBold : FontWeights.Normal,
                });
                return (UIElement)sp;
            }).ToList();
        }

        static string Dur(double s) => s < 60 ? (int)s + " s" : s < 3600 ? string.Format("{0}:{1:00} min", (int)s / 60, (int)s % 60) : string.Format("{0}:{1:00} h", (int)s / 3600, (int)s % 3600 / 60);

        /// <summary>the overall fraction, from the steps' expected seconds (a running step: its counter, else its time so
        /// far against the estimate); the time left corrected by how the finished steps went</summary>
        void SetupProgress()
        {
            double tot = 0, done = 0, rs = 0, re = 0, wait = 0, left = 0;
            foreach (var s in setupSteps)
            {
                tot += s.Est;
                double f = s.Total > 0 ? Math.Min(1, s.Done / s.Total) : s.T0 != default(DateTime) ? Math.Min(0.95, (DateTime.Now - s.T0).TotalSeconds / Math.Max(1, s.Est)) : 0;
                if (s.State == "done" || s.State == "skip" || s.State == "dry" || s.State == "warn") { done += s.Est; if (s.State == "done" && s.Secs > 0) { rs += s.Secs; re += s.Est; } }
                else if (s.State == "run" || s.State == "fail") { done += s.Est * f; left += s.Est * (1 - f); }
                else wait += s.Est;
            }
            double ratio = re > 0 ? Math.Max(0.25, Math.Min(5, rs / re)) : 1;
            double frac = tot > 0 ? Math.Min(1, done / tot) : 0;
            if (setupOk) frac = 1;
            SetBar(SetupRunDone, SetupRunLeft, frac);
            SetupPct.Text = (int)(frac * 100) + "%";
            if (setupRunning)
            {
                double l = (left + wait) * ratio;
                SetupEta.Text = setupSteps.Count == 0 ? "working..." : l < 60 ? "less than a minute left" : "about " + Dur(l) + " left";
            }
        }

        void SetupEnded(int code, string why = null)
        {
            setupRunning = false;
            setupProc = null;
            setupTimer?.Stop();
            bool ok = code == 0 && (setupDone == null || !(setupDone.TryGetValue("ok", out var o) && o is bool b && !b));
            setupDone = null;
            SetupStop.Visibility = Visibility.Collapsed;
            SetupGo.IsEnabled = true;
            SetupEta.Text = "took " + Dur((DateTime.Now - setupStart).TotalSeconds);
            Updater.Log("setup: install.py ended with " + code);
            if (ok && !choice.Dry && Environment.GetEnvironmentVariable("SSBU_ARENA_SETUP_DRY") != "1")
            {
                setupOk = true;
                SetupStageOn(4);
                SetupRunTitle.Text = "READY";
                SetupRunDetail.Text = "SSBU Arena is installed.";
                SetupGoText.Text = "PLAY";
                SetupProgress();
                return;
            }
            SetupRunTitle.Text = ok ? "DRY RUN DONE" : setupStopped ? "STOPPED" : "THE SETUP STOPPED";
            SetupRunDetail.Text = ok ? "Nothing was changed (--dry-run)." : setupStopped ? "Resume goes on from where it was." : "Resume goes on from where it was, after the problem is fixed.";
            if (why != null) { SetupError.Text = why; SetupError.Visibility = Visibility.Visible; }
            else if (!ok && !setupStopped && SetupError.Visibility != Visibility.Visible)
            {
                SetupError.Text = "install.py ended with code " + code + ": the last lines of the log say why.";
                SetupError.Visibility = Visibility.Visible;
            }
            SetupGoText.Text = "RESUME";
            SetupBack.Visibility = Visibility.Visible;
            LauncherCrash.Where = "setup stage 3: stopped";
            // (0.2.73) an install that stopped with an error (not the player's Stop, not a dry run): reported on its own
            if (!ok && !setupStopped)
            {
                string err = SetupError.Visibility == Visibility.Visible ? SetupError.Text : why ?? "install.py ended with code " + code;
                string log = null;
                try { log = choice != null ? Installer.LogPath(choice) : null; } catch { }
                SetupErrorReport("install.py (code " + code + ")", err, log);
            }
        }

        void SetupStop_Click(object sender, RoutedEventArgs e)
        {
            if (setupProc == null) return;
            setupStopped = true;
            SetupRunDetail.Text = "Stopping...";
            // the whole tree: install.py runs setup.py, which runs the compilers
            try { Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + setupProc.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }); } catch { }
        }

        void SetupBack_Click(object sender, RoutedEventArgs e) { ShowSetupFiles(false); }
        void SetupBuildTools_Click(object sender, RoutedEventArgs e) { try { Installer.InstallBuildTools(); } catch (Exception ex) { Show("Build Tools", ex.Message, null, ("OK", null, true)); } }

        void SetupOpenLog_Click(object sender, RoutedEventArgs e)
        {
            string f = choice != null ? Installer.LogPath(choice) : null;
            if (f != null && File.Exists(f)) Process.Start("notepad.exe", "\"" + f + "\"");
        }

        void RestartLauncher()
        {
            SaveQuiet();
            Process.Start(new ProcessStartInfo(Updater.ExePath) { UseShellExecute = false });
            Application.Current.Shutdown();
        }

        /// <summary>Settings > Game files: the setup again (other game files, an install location, a repair)</summary>
        void Reinstall_Click(object sender, RoutedEventArgs e)
        {
            if (game != null || prep != null || updating != null) { Show("Setup", "Close the game first.", null, ("OK", null, true)); return; }
            StartSetup(false);
        }

        /// <summary>Settings > Game files > Check and repair (FileCheck.cs): the check, then the repair through the setup
        /// screen (scripts/verify.py repair, then install.py, which remakes only what was removed)</summary>
        void FileCheck_Click(object sender, RoutedEventArgs e)
        {
            if (game != null || prep != null || updating != null || setupRunning) { Show("Check and repair", "Close the game first.", null, ("OK", null, true)); return; }
            bool full = FileCheckFull.IsChecked == true;
            FileCheckButton.IsEnabled = false;
            FileCheckText.Foreground = Res("Muted");
            FileCheckText.Text = FileCheck.HasList ? "Checking..." : "No list of the files yet: making it from the files as they are now...";
            Task.Run(() =>
            {
                Core.EnsureArenaMenu();      // (0.2.50) the Arena menu is not in the file list when the setup could not make it: made here
                if (!FileCheck.HasList) return Tuple.Create((List<FileProblem>)null, FileCheck.Write());
                var bad = FileCheck.Check(!full, (i, n) => { if (i % 10 == 0 || i == n) Dispatcher.BeginInvoke(new Action(() => FileCheckText.Text = "Checking " + i + " / " + n + "...")); });
                return Tuple.Create(bad, 0);
            }).ContinueWith(t =>
            {
                FileCheckButton.IsEnabled = true;
                if (t.IsFaulted) { FileCheckText.Foreground = Res("Bad"); FileCheckText.Text = "The check stopped: " + t.Exception.GetBaseException().Message; return; }
                var bad = t.Result.Item1;
                Updater.Log("file check (" + (full ? "full" : "sizes") + "): " + (bad == null ? "list made, " + t.Result.Item2 + " files" : bad.Count + " problem(s)"));
                if (bad == null)
                {
                    FileCheckText.Foreground = Res("Good");
                    FileCheckText.Text = "List made: " + t.Result.Item2 + " files, taken as they are now. The next check compares against it.";
                    return;
                }
                if (bad.Count == 0 && !Core.ArenaMenuOk)
                {
                    // (0.2.50) the listed files are fine but the Arena menu could not be made (EnsureArenaMenu above): say so, never "fine"
                    FileCheckText.Foreground = Res("Warn");
                    FileCheckText.Text = "The game files are fine, but the Arena menu could not be made (" + Core.ArenaMenuOffReason() + "). See build\\logs\\arena_menu.log.";
                    return;
                }
                if (bad.Count == 0)
                {
                    FileCheckText.Foreground = Res("Good");
                    FileCheckText.Text = "All the files are fine" + (full ? "." : " (sizes; the full check also reads their content).");
                    return;
                }
                FileCheckText.Foreground = Res("Warn");
                FileCheckText.Text = bad.Count + " file(s) to repair:\n" + string.Join("\n", bad.Take(8)) + (bad.Count > 8 ? "\n..." : "");
                if (!FileCheck.CanRepair)
                {
                    FileCheckText.Text += "\nThis copy has no setup (a portable copy): copy these files again from the copy it was made from.";
                    return;
                }
                Show("Repair the game files", null, bad.Take(12).Select(b => b.ToString()).Concat(bad.Count > 12 ? new[] { "..." } : new string[0]),
                     ("Repair", () => StartRepair(full), true), ("Not now", null, false));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void StartRepair(bool full)
        {
            setupPy = Installer.Python;
            if (setupPy == null) { Show("Repair", "The setup's Python is missing: use Run the setup again.", null, ("OK", null, true)); return; }
            FileCheckText.Text = "Repairing: the setup remakes the files...";
            Task.Run(() =>
            {
                var psi = new ProcessStartInfo(setupPy, "-u scripts\\verify.py repair --build \"" + Core.Build + "\"" + (full ? "" : " --quick"))
                { WorkingDirectory = Core.Repo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Updater.Log("verify.py repair (" + p.ExitCode + "):\n" + o.Trim());
                    if (p.ExitCode != 0) throw new Exception(o.Trim().Split('\n').LastOrDefault());
                }
            }).ContinueWith(t =>
            {
                if (t.IsFaulted) { FileCheckText.Foreground = Res("Bad"); FileCheckText.Text = "The repair stopped: " + t.Exception.GetBaseException().Message; return; }
                choice = new SetupChoice { Location = Core.Build };      // (install.py keeps the shared game data of an install that has it)
                SetupOverlay.Visibility = Visibility.Visible;
                RunSetup();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
