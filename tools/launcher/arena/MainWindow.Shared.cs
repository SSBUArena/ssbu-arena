// SPDX-License-Identifier: GPL-3.0-or-later
// Shared game data in the window (SharedData.cs): the question asked once when OpenSmash Ultimate is found, the link made again
// by itself at start and before Play when OpenSmash changed its files, the plain choices when its data cannot be used any more,
// Settings > Game files > Shared game files, and the offer (with the size, after a confirmation) to delete Arena's old copy.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Arena
{
    public partial class MainWindow
    {
        bool sharedBusy, sharedAsked;
        string sharedNote;          // the relink's line: shown again once the checks after it are done (RunChecks), and in Settings
        const string SharedQuestion = "OpenSmash Ultimate is installed on this PC: use its game files instead of a second copy? Saves about 16 GB.";

        static string Gb(long bytes) => bytes >= 1073741824L ? (bytes / 1073741824.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " GB"
                                                         : (bytes / 1048576.0).ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " MB";

        /// <summary>at start (Loaded): a stale link made again; an install that does not share yet asked once; the old copy offered</summary>
        void SharedAtStart()
        {
            if (Core.Repo == null || !File.Exists(Core.Exe)) return;
            bool tool = Environment.GetCommandLineArgs().Any(x => x == "--replay" || x == "--page");      // (a tool's launcher: the link only, no question)
            if (SharedData.InUse)
            {
                if (SharedEnsure(() => { RunChecks(); if (!tool) SharedOfferOldCopy(); }, false)) return;
                if (!tool) SharedOfferOldCopy();
                return;
            }
            if (Core.SharedData != "" || tool) return;
            Task.Run(() => SharedData.Candidate()).ContinueWith(t =>
            {
                if (t.Result != null && !setupRunning && Dialog.Visibility != Visibility.Visible) SharedAsk(t.Result, yes => { if (yes) SharedSetup(t.Result.Item1); });
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>the question, once: the answer goes to launcher.ini shared_data</summary>
        void SharedAsk(Tuple<string, string> cand, Action<bool> answered)
        {
            sharedAsked = true;
            string built = Core.GameVersion();
            string rebuild = File.Exists(Core.Exe) && built != cand.Item2
                ? "Your SSBU Arena is built for the game update " + built + " and OpenSmash has " + cand.Item2 + ": the game is built again once (about 35 minutes). " : "";
            Show("OpenSmash Ultimate found", SharedQuestion + "\n\n" + rebuild +
                 "Arena then reads the game data of OpenSmash in " + cand.Item1 + " (nothing is written there). While Arena is open, OpenSmash cannot update. " +
                 "You can change this later in Settings > Game files.", null,
                 ("Yes", () => { Core.SharedData = "on"; SaveQuiet(); answered(true); }, true),
                 ("No", () => { Core.SharedData = "off"; SaveQuiet(); answered(false); }, false));
        }

        /// <summary>the setup screen with --shared-data (the link, and the build again only when the game update differs)</summary>
        void SharedSetup(string folder)
        {
            if (game != null || prep != null || updating != null || setupRunning) { Show("Shared game files", "Close the game first.", null, ("OK", null, true)); return; }
            setupPy = Installer.Python;
            if (setupPy == null) { Show("Shared game files", "The setup's Python is missing: use Run the setup again.", null, ("OK", null, true)); return; }
            choice = new SetupChoice { Location = Core.Build, Shared = folder ?? "auto" };
            SetupOverlay.Visibility = Visibility.Visible;
            RunSetup();
        }

        /// <summary>the link checked (cheap); when OpenSmash changed its files, made again in the background, then then() on the
        /// UI thread. Returns true when it went to the background (the caller stops; then() goes on).</summary>
        bool SharedEnsure(Action then, bool forPlay)
        {
            if (!SharedData.InUse || sharedBusy) return false;
            string bad = SharedData.Problem();
            if (bad == null) return false;
            sharedBusy = true;
            Updater.Log("shared data: " + bad + ": linking again");
            Status("OpenSmash Ultimate's game files changed: linking to them again...", "busy");
            string folder = SharedData.FindOpenSmash();
            Task.Run(() => SharedData.Relink(folder)).ContinueWith(t =>
            {
                sharedBusy = false;
                if (t.Result.Item1 && SharedData.Problem() == null)
                {
                    sharedNote = "OpenSmash Ultimate had changed its game files: linked to them again at " + DateTime.Now.ToString("HH:mm") + " (" + t.Result.Item2 + ")";
                    Status(sharedNote, "good");
                    then?.Invoke();
                    return;
                }
                Status("OpenSmash Ultimate's game files cannot be used", "bad");
                SharedFailed(t.Result.Item1 ? SharedData.Problem() : t.Result.Item2, forPlay ? then : null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
            return true;
        }

        /// <summary>the data of OpenSmash cannot be used any more (gone, a mods map, another update, incomplete): the choices</summary>
        void SharedFailed(string why, Action retry)
        {
            Show("OpenSmash Ultimate's game files", "SSBU Arena uses the game files of your OpenSmash Ultimate install, and they cannot be used now:\n\n" + why +
                 "\n\nIf OpenSmash was moved, choose its folder. Otherwise SSBU Arena can go back to a copy of its own: the setup makes it from your game files " +
                 "(the game package, its update and prod.keys; about 17 GB).", null,
                 ("Choose the OpenSmash folder", () => SharedPickFolder(retry), true),
                 ("Use Arena's own copy", SharedTurnOff, false),
                 ("Not now", null, false));
        }

        void SharedPickFolder(Action after)
        {
            string f = PickFolder("Your OpenSmash Ultimate folder (the one with OpenSmash.exe and dump inside)");
            if (f == null) return;
            if (!SharedData.IsOpenSmash(f)) { Show("Shared game files", f + " is not an OpenSmash Ultimate install (no tools\\install_windowsmash.ps1 and dump\\modules there).", null, ("OK", null, true)); return; }
            Core.OpenSmashPath = f; SaveQuiet();
            if (!SharedData.InUse) { RefreshShared(); return; }
            Status("Linking to OpenSmash Ultimate's game files in " + f + "...", "busy");
            sharedBusy = true;
            Task.Run(() => SharedData.Relink(f)).ContinueWith(t =>
            {
                sharedBusy = false;
                RefreshShared();
                if (t.Result.Item1) { Status("Linked to OpenSmash Ultimate's game files (" + t.Result.Item2 + ")", "good"); RunChecks(); after?.Invoke(); }
                else { Status("OpenSmash Ultimate's game files cannot be used", "bad"); SharedFailed(t.Result.Item2, after); }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>back to Arena's own copy: the setup with --shared-data off (build\dump when it is still there, else the game files)</summary>
        void SharedTurnOff()
        {
            if (game != null || prep != null || updating != null || setupRunning) { Show("Shared game files", "Close the game first.", null, ("OK", null, true)); RefreshShared(); return; }
            Core.SharedData = "off"; SaveQuiet();
            if (SharedData.OwnDumpThere)
            {
                setupPy = Installer.Python;
                choice = new SetupChoice { Location = Core.Build, Shared = "off" };
                SetupOverlay.Visibility = Visibility.Visible;
                RunSetup();
            }
            else StartSetup(false);          // the game files page; SetupGo passes --shared-data off (launcher.ini says off)
        }

        /// <summary>Arena's old copy (build\dump), once the link is in use and checked: deleted only after the confirmation</summary>
        void SharedOfferOldCopy()
        {
            if (!SharedData.InUse || !SharedData.OwnDumpThere || Core.SharedOldCopy == "keep" || SharedData.Problem() != null || Dialog.Visibility == Visibility.Visible) return;
            Task.Run(() => SharedData.OwnDumpBytes()).ContinueWith(t =>
            {
                long n = t.Result;
                Show("Arena's old copy of the game files", "SSBU Arena now reads the game files of OpenSmash Ultimate, and its own old copy is not used any more:", new[] { SharedData.OwnDump + "   (" + Gb(n) + ")" },
                     ("Delete it (" + Gb(n) + ")", () => SharedConfirmDelete(n), true),
                     ("Keep it", () => { Core.SharedOldCopy = "keep"; SaveQuiet(); }, false));
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void SharedConfirmDelete(long n)
        {
            Show("Delete " + Gb(n) + "?", "This deletes " + SharedData.OwnDump + " (" + Gb(n) + ") for good. To go back to a copy of Arena's own later, the setup makes it again from your game files.", null,
                 ("Delete", () =>
                 {
                     Status("Deleting Arena's old copy of the game files...", "busy");
                     Task.Run(() => SharedData.DeleteOwnDump()).ContinueWith(t =>
                     {
                         string why = t.IsFaulted ? t.Exception.GetBaseException().Message : t.Result;
                         if (why == null) Status(Gb(n) + " freed: Arena's old copy of the game files is deleted", "good");
                         else { Status("Arena's old copy was not deleted", "warn"); Show("Not deleted", why, null, ("OK", null, true)); }
                         RefreshShared();
                     }, TaskScheduler.FromCurrentSynchronizationContext());
                 }, true),
                 ("Cancel", null, false));
        }

        // ---------------------------------------------------------------- Settings > Game files > Shared game files
        void RefreshShared()
        {
            if (SharedBox == null) return;
            SharedBox.IsChecked = SharedData.InUse;
            if (SharedData.InUse)
            {
                string bad = SharedData.Problem();
                SharedText.Foreground = Res(bad == null ? "Good" : "Warn");
                SharedText.Text = bad == null ? "On: the game reads OpenSmash Ultimate's game files in " + (SharedData.LinkedFrom() ?? "?") + "." + (sharedNote != null ? "\n" + sharedNote + "." : "")
                                              : "On, but OpenSmash changed its files (" + bad + "): linked again before the next start.";
            }
            else
            {
                Task.Run(() => SharedData.Candidate()).ContinueWith(t =>
                {
                    SharedText.Foreground = Res("Muted");
                    SharedText.Text = t.Result != null ? "Off: OpenSmash Ultimate (" + t.Result.Item2 + ") is in " + t.Result.Item1 + "."
                                    : SharedData.WhyNot != null ? "Off: OpenSmash Ultimate was found, but its game files cannot be shared: " + SharedData.WhyNot + "."      // (fix/shared-install)
                                                       : "Off: no OpenSmash Ultimate install found on this PC (Choose the OpenSmash folder if it is somewhere else).";
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        void SharedBox_Click(object sender, RoutedEventArgs e)
        {
            bool want = SharedBox.IsChecked == true;
            SharedBox.IsChecked = SharedData.InUse;          // the switch follows the install, not the click: changed when the setup is done
            if (want == SharedData.InUse) return;
            if (want)
            {
                var cand = SharedData.Candidate();
                if (cand == null) { Show("Shared game files", (SharedData.WhyNot != null ? "OpenSmash Ultimate was found, but its game files cannot be shared: " + SharedData.WhyNot + ".\n\nSSBU Arena keeps its own copy. " : "No OpenSmash Ultimate install with game files Arena can use was found on this PC. ") + "Choose its folder if it is somewhere else.", null, ("Choose the OpenSmash folder", () => SharedPickFolder(null), true), ("Cancel", null, false)); return; }
                SharedAsk(cand, yes => { if (yes) SharedSetup(cand.Item1); });
                return;
            }
            Show("Arena's own copy of the game files", "SSBU Arena goes back to a copy of its own: " + (SharedData.OwnDumpThere ? "the old one in " + SharedData.OwnDump + " is used again." :
                 "the setup makes it from your game files (the game package, its update and prod.keys; about 17 GB of space)."), null,
                 ("Use Arena's own copy", SharedTurnOff, true), ("Cancel", null, false));
        }

        void SharedFolder_Click(object sender, RoutedEventArgs e) { SharedPickFolder(null); }
    }
}
