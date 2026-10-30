// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher window: home (play, player, setup), replays, community, settings. The work is in Core.cs.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Arena
{
    public partial class MainWindow : Window
    {
        List<Check> checks;                 // last game files check (null while it runs)
        bool shadersOk = true;              // the shaders prepared for this exe and graphics driver (last check)
        List<string> missingCodecs = new List<string>();
        List<Pad> pads = new List<Pad>();   // the game's own list (--pad-list)
        List<string> padGuids = new List<string>();
        int xinput;
        Process game;
        bool loading = true;                // filling the fields: their change events must not save

        public MainWindow()
        {
            InitializeComponent();
            VersionText.Text = "v" + Updater.Current.ToString(3);      // (0.2.11) the release in use, in the status bar after the online state
            // (0.2.18) opens at the design size (DesignW x DesignH under the title bar: the page is not scaled, its text is sharp) when
            // the screen has room, else at 88 % of the work area in the same shape
            {   var wa = SystemParameters.WorkArea; double dw = DesignW, dh = DesignH + 36, k = Math.Min(1.0, Math.Min(wa.Width * 0.88 / dw, wa.Height * 0.88 / dh));
                Width = Math.Max(MinWidth, Math.Round(dw * k)); Height = Math.Max(MinHeight, Math.Round(dh * k)); }
            // (0.2.8) resizing keeps the window's proportions (the page is laid out for one shape)
            SourceInitialized += (s0, e0) => System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle).AddHook(KeepAspect);
            Core.Load();
            SpaceOut(this);
            FillSettings();
            loading = false;
            RefreshPlayer();
            RefreshOnline();
            SetPadState();
            StateChanged += (s, e) => Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            Closing += (s, e) =>
            {
                SaveQuiet();
                PadLiveStop();
                if (prep != null) { prepCancelled = true; try { prep.Kill(); } catch { } }
                if (setupProc != null) SetupStop_Click(null, null);      // install.py is resumable
            };
            var poll = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            poll.Tick += (s, e) => { int n = Core.XInputCount(); if (n != xinput) { xinput = n; SetPadState(); } };
            poll.Start();
            xinput = Core.XInputCount();
            if (Environment.GetEnvironmentVariable("SSBU_ARENA_CRASH_TEST") == "1")      // (test) the newest crash file sent as after a game
                Task.Run(() => { for (int w = 0; w < 60 && !Account.SignedIn; w++) System.Threading.Thread.Sleep(500); bool sent = CrashReport.SendIfCrashed(DateTime.MinValue, out bool crashed); Updater.Log("crash test: crashed " + crashed + ", sent " + sent); });
            Loaded += (s, e) => { RunChecks(); FindPads(); OpenFromArgs(); OpenReplayFromArgs(); ResumeAccount(); CheckUpdates(); Task.Run(() => Core.MakeLoadingArt()); };
            Scaled.SizeChanged += (s, e) => Rescale();
            SizeChanged += (s, e) => Rescale();
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct Rect32 { public int Left, Top, Right, Bottom; }

        /// <summary>WM_SIZING: the dragged edge decides, the other side follows the MinWidth : MinHeight shape</summary>
        IntPtr KeepAspect(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
        {
            if (msg != 0x0214) return IntPtr.Zero;
            var r = (Rect32)System.Runtime.InteropServices.Marshal.PtrToStructure(lp, typeof(Rect32));
            double ratio = MinWidth / MinHeight;
            int edge = wp.ToInt32(), w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (edge == 3 || edge == 6) r.Right = r.Left + (int)Math.Round(h * ratio);       // top / bottom: the width follows
            else if (edge == 4 || edge == 5) r.Top = r.Bottom - (int)Math.Round(w / ratio);  // top corners: the top moves
            else r.Bottom = r.Top + (int)Math.Round(w / ratio);                              // left / right / bottom corners
            System.Runtime.InteropServices.Marshal.StructureToPtr(r, lp, false);
            handled = true;
            return (IntPtr)1;
        }

        // ---------------------------------------------------------------- look
        /// <summary>(0.2.18) the size the page is laid out for under the title bar (was the mockup's 1586 x 956: at the window's
        /// smallest size that scaled the text to 0.59, small and blurred; now 0.75)</summary>
        const double DesignW = 1280, DesignH = 748;

        /// <summary>the page scaled to the window, the longer side keeps the extra room</summary>
        void Rescale()
        {
            var host = (FrameworkElement)Scaled.Parent;
            double w = host.ActualWidth - Root.Margin.Left - Root.Margin.Right, h = host.ActualHeight - 36 - Root.Margin.Top - Root.Margin.Bottom;
            if (w <= 0 || h <= 0) return;
            double z = Math.Max(0.55, Math.Min(2.0, Math.Min(w / DesignW, h / DesignH)));
            if (z > 0.97 && z < 1.08) z = 1;          // about the design size: exactly 1 (sharp text), the rest is margin
            if (Math.Abs(z - Zoom.ScaleX) < 0.001) return;
            Zoom.ScaleX = Zoom.ScaleY = z;
            // at zoom 1 the glyphs snap to the pixel grid (Display); scaled, the layout stays even (Ideal)
            TextOptions.SetTextFormattingMode(Scaled, z == 1 ? TextFormattingMode.Display : TextFormattingMode.Ideal);
        }

        /// <summary>letter-spaced caps (TextBlock has no letter spacing): the TextBlocks tagged "sp"</summary>
        static void SpaceOut(DependencyObject root)
        {
            foreach (var o in LogicalTreeHelper.GetChildren(root))
            {
                if (o is TextBlock t && (t.Tag as string) == "sp") t.Text = Spaced(t.Text);
                if (o is DependencyObject d) SpaceOut(d);
            }
        }

        static string Spaced(string s) { return string.Join(" ", s.Select(c => c == ' ' ? " " : c.ToString())); }

        static Brush Res(string key) { return (Brush)Application.Current.Resources[key]; }

        /// <summary>the round status mark: ok (accent, check), warn (amber, !), bad (red, x), busy (ring)</summary>
        static UIElement Mark(string kind, double size = 30)
        {
            var g = new Grid { Width = size, Height = size };
            if (kind == "busy")
            {
                g.Children.Add(new Ellipse { Stroke = Res("Line"), StrokeThickness = 2.5 });
                var arc = new Ellipse { Stroke = Res("Accent"), StrokeThickness = 2.5, StrokeDashArray = new DoubleCollection { 3, 6 }, RenderTransformOrigin = new Point(0.5, 0.5) };
                var rt = new RotateTransform();
                arc.RenderTransform = rt;
                rt.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever });
                g.Children.Add(arc);
                return g;
            }
            var fill = kind == "ok" ? Res("Accent") : kind == "good" ? Res("Good") : kind == "warn" ? Res("Warn") : Res("Bad");
            g.Children.Add(new Ellipse { Fill = fill });
            string geo = kind == "ok" || kind == "good" ? "IcoCheck" : kind == "warn" ? "IcoBang" : "IcoCross";
            g.Children.Add(new Path
            {
                Data = (Geometry)Application.Current.Resources[geo], Stroke = new SolidColorBrush(Color.FromRgb(6, 18, 29)), StrokeThickness = size / 11,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
                Width = size, Height = size, Stretch = Stretch.None, RenderTransform = new ScaleTransform(size / 24, size / 24)
            });
            return g;
        }

        void Status(string text, string kind)
        {
            StatusText.Text = text;
            StatusMark.Content = Mark(kind, 24);
        }

        void SetState()
        {
            if (game != null)
            {
                StateText.Text = Spaced("GAME RUNNING"); StateDot.Fill = Res("Good");
                PlayButton.IsEnabled = false; PlayText.Text = "RUNNING";
                Status("Game running", "good");
                return;
            }
            PlayButton.IsEnabled = true; PlayText.Text = "PLAY";
            if (checks == null) { StateText.Text = Spaced("CHECKING FILES"); StateDot.Fill = Res("Muted"); Status("Checking game files...", "busy"); }
            else if (checks.All(c => c.Ok))
            {
                StateText.Text = Spaced("READY TO PLAY"); StateDot.Fill = Res("Accent");
                if (shadersOk) Status("Game ready", "good");
                else Status("Game ready · shaders not prepared yet", "warn");
            }
            else { StateText.Text = Spaced("NEEDS ATTENTION"); StateDot.Fill = Res("Warn"); Status("The game needs attention: see Game files", "warn"); }
        }

        void RefreshPlayer()
        {
            string n = Core.Nickname.Trim();
            if (Account.SignedIn)
            {
                PlayerName.Text = Account.Me.Username.ToUpperInvariant();
                PlayerTag.Text = Spaced("ARENA#" + Account.Me.Tag.ToString("0000"));
                return;
            }
            PlayerName.Text = n == "" ? "OFFLINE" : n.ToUpperInvariant();
            PlayerTag.Text = Spaced("NOT SIGNED IN");
            UidText.Text = n == "" ? "Write a name." : "Player id (from the name): " + Core.UidFromName(n).Substring(0, 16) + "...";
        }

        void RefreshOnline()
        {
            if (!Core.HasOnline) { NetDot.Fill = Res("Dim"); NetText.Text = "This build has no online"; }
            else if (!Core.Online) { NetDot.Fill = Res("Dim"); NetText.Text = "Offline"; }
            else
            {
                NetDot.Fill = Res("Good");
                NetText.Text = "Online";
            }
            bool on = OnlineBox.IsChecked == true && Core.HasOnline;
            OnlineFields.IsEnabled = on;
            OnlineFields.Opacity = on ? 1 : 0.45;
            OnlineHelp.Text = !Core.HasOnline ? "This build of the game has no online." : !on ? "Online off: local play only."
                            : "Online Matchmaking finds you an opponent; every online match plays with rollback.";
        }

        void SetPadState()
        {
            int n = Math.Max(xinput, pads.Count);
            if (n > 0)
            {
                PadSub.Text = pads.Count == 1 ? pads[0].Name : n == 1 ? "Connected" : n + " connected";
                PadMark.Content = Mark("ok");
            }
            else { PadSub.Text = "None: keyboard only"; PadMark.Content = Mark("warn"); }
        }

        // ---------------------------------------------------------------- checks and controllers
        void RunChecks()
        {
            checks = null;
            FilesSub.Text = "Checking...";
            FilesMark.Content = Mark("busy");
            SetState();
            Task.Run(() => { var r = Core.CheckFiles(out var mc); return Tuple.Create(r, mc); }).ContinueWith(t =>
            {
                checks = t.Result.Item1; missingCodecs = t.Result.Item2;
                int bad = checks.Count(c => !c.Ok);
                bool shaders = shadersOk = Core.CanPrepare() ? !Core.NeedsPrepare() : !Core.FirstRun();
                FilesSub.Text = bad > 0 ? bad + (bad == 1 ? " problem" : " problems") : shaders ? "Verified" : "Verified, shaders not prepared yet";
                FilesMark.Content = Mark(bad > 0 ? "bad" : shaders ? "ok" : "warn");
                var rows = checks.Select(c => CheckRow(c.Ok, c.Text)).ToList();
                rows.Add(CheckRow(shaders, shaders ? "Shaders prepared" : "Shaders not prepared yet", shaders ? "ok" : "warn"));
                CheckList.ItemsSource = rows;
                SetState();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static UIElement CheckRow(bool ok, string text, string kind = null)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 5, 0, 5) };
            sp.Children.Add(Mark(kind ?? (ok ? "ok" : "bad"), 22));
            sp.Children.Add(new TextBlock { Text = text, FontSize = 16, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
            return sp;
        }

        void FindPads()
        {
            if (game != null) return;
            PadsHelp.Text = "Looking for controllers...";
            Task.Run(() => Core.ListPads()).ContinueWith(t =>
            {
                pads = t.Result;
                FillPads();
                SetPadState();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void FillPads()
        {
            bool was = loading;
            loading = true;
            padGuids = new List<string> { "auto", "none" };
            var names = new List<string> { "Automatic", "None" };
            foreach (var p in pads) { padGuids.Add(p.Guid); names.Add(p.Name); }
            foreach (var v in new[] { Core.Player1, Core.Player2 })
                if (!padGuids.Contains(v)) { padGuids.Add(v); names.Add("(not connected) " + v.Substring(0, Math.Min(8, v.Length))); }
            foreach (var pair in new[] { Tuple.Create(P1Box, Core.Player1), Tuple.Create(P2Box, Core.Player2) })
            {
                pair.Item1.ItemsSource = names.ToList();
                pair.Item1.SelectedIndex = padGuids.IndexOf(pair.Item2);
            }
            PadsHelp.Text = "Controllers found: " + pads.Count + ". Automatic: the first controller that connects. None: the slot stays empty.";
            loading = was;
        }

        // ---------------------------------------------------------------- settings
        int filledSize = -1, filledVol = -1;      // (0.2.12) the size and volume boxes as FillSettings set them

        void FillSettings()
        {
            NameBox.Text = Core.Nickname;
            OnlineBox.IsChecked = Core.Online;
            OnlineBox.IsEnabled = Core.HasOnline;
            PingBox.IsChecked = Core.Ping;
            CrashBox.IsChecked = Core.CrashReports;
            DelayBox.SelectedIndex = Math.Max(0, Math.Min(5, Core.InputDelay));
            RecMmBox.IsChecked = Core.RecMatchmaking; RecLdnBox.IsChecked = Core.RecLdn; RecArenaBox.IsChecked = Core.RecArena;
            RecLocalBox.IsChecked = Core.RecLocal; RecUploadBox.IsChecked = Core.RecUpload; RecPublicBox.IsChecked = Core.RecPublic;
            ServerBox.Items.Clear();
            foreach (var s in Core.Servers) ServerBox.Items.Add(s.Name);
            ServerBox.SelectedIndex = Core.ServerIndex(Core.Server);
            WinBox.SelectedIndex = Core.WindowMode;
            SizeBox.SelectedIndex = Math.Max(0, SizeBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Content == Core.WindowSize.Replace(" ", "")));      // (0.2.8) our sizes only
            SizeBox.IsEnabled = Core.WindowMode == 0;
            AspectBox.SelectedIndex = Core.Aspect;
            ShaderBox.SelectedIndex = Core.ShaderLoad;
            FpsBox.IsChecked = Core.Fps;
            KbBox.SelectedIndex = Core.Keyboard;
            DeadZoneSlider.Value = Math.Round(Core.DeadZone * 100); DeadZoneText.Text = (int)DeadZoneSlider.Value + " %";
            VolSlider.Value = Math.Max(0, Math.Min(100, Core.Volume)); VolText.Text = (int)VolSlider.Value + " %";      // (0.2.16) steps of 1
            filledSize = SizeBox.SelectedIndex; filledVol = (int)VolSlider.Value;
            FillButtons();
            FillPads();
            RefreshSaves();
            RefreshDlc();
        }

        /// <summary>fields -> Core</summary>
        void ReadFields()
        {
            Core.Nickname = NameBox.Text.Trim();
            Core.Online = OnlineBox.IsChecked == true && Core.HasOnline;
            Core.Ping = PingBox.IsChecked == true;
            Core.CrashReports = CrashBox.IsChecked == true;
            Core.InputDelay = Math.Max(0, DelayBox.SelectedIndex);
            Core.RecMatchmaking = RecMmBox.IsChecked == true; Core.RecLdn = RecLdnBox.IsChecked == true; Core.RecArena = RecArenaBox.IsChecked == true;
            Core.RecLocal = RecLocalBox.IsChecked == true; Core.RecUpload = RecUploadBox.IsChecked == true; Core.RecPublic = RecPublicBox.IsChecked == true;
            Core.Server = Core.Servers[Math.Max(0, ServerBox.SelectedIndex)].Address;
            Core.WindowMode = Math.Max(0, WinBox.SelectedIndex);
            if (SizeBox.SelectedIndex != filledSize) Core.WindowSize = (SizeBox.SelectedItem as ComboBoxItem)?.Content as string ?? Core.WindowSize;      // (0.2.12) a size set in the game, not in this list, stays
            Core.Aspect = Math.Max(0, AspectBox.SelectedIndex);
            Core.ShaderLoad = Math.Max(0, ShaderBox.SelectedIndex);
            Core.Fps = FpsBox.IsChecked == true;
            Core.Keyboard = Math.Max(0, KbBox.SelectedIndex);
            if ((int)Math.Round(VolSlider.Value) != filledVol) Core.Volume = (int)Math.Round(VolSlider.Value);      // (0.2.12) the game's own volume stays until changed here
            foreach (var cb in btnBoxes) if (cb.SelectedIndex >= 0) Core.Buttons[(string)cb.Tag] = Core.PadSources[cb.SelectedIndex].Item1;
            if (P1Box.SelectedIndex >= 0) Core.Player1 = padGuids[P1Box.SelectedIndex];
            if (P2Box.SelectedIndex >= 0) Core.Player2 = padGuids[P2Box.SelectedIndex];
        }

        bool SaveQuiet()
        {
            ReadFields();
            try { Core.Save(); return true; }
            catch (Exception ex) { Show("Could not save", ex.Message, null, ("OK", null, true)); return false; }
        }

        void RefreshSaves()
        {
            SavesPath.Text = Core.Saves;
            SavesState.Text = Core.HasSave() ? "A save is there." : "Empty: the game makes a new one.";
        }

        void RefreshDlc()
        {
            int n = Core.DlcCount();
            DlcText.Text = n == 0 ? "No DLC active." : n + " DLC active.";
        }

        // (0.2.8) the controller's buttons: one list per game button, the controller's buttons to choose from (Core.Buttons)
        readonly List<ComboBox> btnBoxes = new List<ComboBox>();
        void FillButtons()
        {
            bool was = loading;
            loading = true;
            btnBoxes.Clear();
            var cells = new List<UIElement>();
            foreach (var b in Core.GameButtons)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 18, 12) };
                sp.Children.Add(new TextBlock { Text = b.Item2, FontSize = 15, Foreground = Res("Muted"), Margin = new Thickness(0, 0, 0, 4) });
                var cb = new ComboBox { Tag = b.Item1, Width = 330, HorizontalAlignment = HorizontalAlignment.Left, ItemsSource = Core.PadSources.Select(x => x.Item2).ToList() };
                cb.SelectedIndex = Math.Max(0, Core.PadSources.FindIndex(x => x.Item1 == (Core.Buttons.TryGetValue(b.Item1, out var v) ? v : "")));
                cb.SelectionChanged += (s, e) => Field_Changed(s, e);
                btnBoxes.Add(cb);
                sp.Children.Add(cb);
                cells.Add(sp);
            }
            BtnMap.ItemsSource = cells;
            loading = was;
        }

        void BtnReset_Click(object sender, RoutedEventArgs e)
        {
            Core.Buttons = new Dictionary<string, string>(Core.DefaultButtons);
            FillButtons();
            SaveQuiet();
            Status("Default buttons back", "good");
        }

        void Vol_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (VolText != null) VolText.Text = (int)Math.Round(e.NewValue) + " %";
            Field_Changed(sender, e);
        }

        void Field_Changed(object sender, RoutedEventArgs e)
        {
            if (loading) return;
            ReadFields();
            RefreshOnline();
        }

        void Name_Changed(object sender, TextChangedEventArgs e)
        {
            if (loading) return;
            ReadFields();
            RefreshPlayer();
        }

        void Online_Changed(object sender, RoutedEventArgs e) { Field_Changed(sender, e); }

        void Win_Changed(object sender, SelectionChangedEventArgs e)
        {
            SizeBox.IsEnabled = WinBox.SelectedIndex == 0;
            Field_Changed(sender, e);
        }

        void Server_Changed(object sender, SelectionChangedEventArgs e) { Field_Changed(sender, e); }

        void FindPads_Click(object sender, RoutedEventArgs e) { FindPads(); }

        void OpenSaves_Click(object sender, RoutedEventArgs e)
        {
            System.IO.Directory.CreateDirectory(Core.Saves);
            Core.Open(Core.Saves);
        }

        static string PickFolder(string what)
        {
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = what })
                return d.ShowDialog() == System.Windows.Forms.DialogResult.OK ? d.SelectedPath : null;
        }

        void ChangeSaves_Click(object sender, RoutedEventArgs e)
        {
            string p = PickFolder("The game's save folder");
            if (p == null) return;
            Core.Saves = p;
            RefreshSaves();
            SaveQuiet();
        }

        void Import(string from)
        {
            string msg;
            try { msg = Core.ImportSave(from); } catch (Exception ex) { msg = ex.Message; }
            RefreshSaves();
            Show("Saves", msg, null, ("OK", null, true));
        }

        void Ryujinx_Click(object sender, RoutedEventArgs e)
        {
            string f = Core.FindRyujinxSave();
            if (f != null) Import(f);
            else Show("Saves", "No Smash save found in Ryujinx (%APPDATA%\\Ryujinx\\bis\\user\\save). Use Import from folder.", null, ("OK", null, true));
        }

        void ImportFolder_Click(object sender, RoutedEventArgs e)
        {
            string p = PickFolder("The folder holding save_data (or system_data.bin): from Ryujinx, yuzu and similar, or from a console dump");
            if (p != null) Import(p);
        }

        void Dlc_Click(object sender, RoutedEventArgs e)
        {
            string p = PickFolder("The folder with the dumps of YOUR Smash DLC (files with [01006A800016Fxxx] in the name)");
            if (p == null) return;
            int n = 0;
            try { n = Core.ActivateDlc(p); } catch (Exception ex) { Show("DLC", ex.Message, null, ("OK", null, true)); return; }
            RefreshDlc();
            if (n == 0) Show("DLC", "No Smash DLC recognised from the file names.", null, ("OK", null, true));
        }

        void DlcOff_Click(object sender, RoutedEventArgs e) { Core.ClearDlc(); RefreshDlc(); }

        void Recheck_Click(object sender, RoutedEventArgs e) { RunChecks(); }

        void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            if (System.IO.File.Exists(Core.GameLog)) Process.Start("notepad.exe", "\"" + Core.GameLog + "\"");
        }

        // ---------------------------------------------------------------- navigation
        void ShowPage(UIElement page)
        {
            foreach (var p in new UIElement[] { PageHome, PageReplays, PageCommunity, PageChanges, PageSettings }) p.Visibility = p == page ? Visibility.Visible : Visibility.Collapsed;
            if (page != PageSettings) { SaveQuiet(); PadLiveStop(); }
            else if (SecPads.IsChecked == true) PadLiveStart();
        }

        void Tab_Checked(object sender, RoutedEventArgs e)
        {
            if (PageHome == null) return;
            ShowPage(sender == TabReplays ? PageReplays : sender == TabCommunity ? PageCommunity : sender == TabChanges ? PageChanges : sender == TabSettings ? (UIElement)PageSettings : PageHome);
            if (sender == TabReplays) ReplaysShown();
            if (sender == TabChanges) ChangesShown();
        }

        void Section_Checked(object sender, RoutedEventArgs e)
        {
            if (BoxPlayer == null) return;
            var map = new Dictionary<object, UIElement> { { SecPlayer, BoxPlayer }, { SecOnline, BoxOnline }, { SecReplays, BoxReplays }, { SecVideo, BoxVideo }, { SecAudio, BoxAudio }, { SecPads, BoxPads }, { SecSaves, BoxSaves }, { SecDlc, BoxDlc }, { SecFiles, BoxFiles } };
            foreach (var kv in map) kv.Value.Visibility = kv.Key == sender ? Visibility.Visible : Visibility.Collapsed;
            if (sender == SecPads && PageSettings.Visibility == Visibility.Visible) PadLiveStart(); else PadLiveStop();
        }

        void OpenSettings(RadioButton section)
        {
            section.IsChecked = true;
            TabSettings.IsChecked = true;
        }

        /// <summary>--page replays | community | settings[:player|online|video|controllers|saves|dlc|files] (for screenshots)</summary>
        void OpenFromArgs()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--page");
            if (i < 0 || i + 1 >= args.Length) return;
            var parts = args[i + 1].Split(':');
            if (parts[0] == "replays") TabReplays.IsChecked = true;
            else if (parts[0] == "community") TabCommunity.IsChecked = true;
            else if (parts[0] == "changes") TabChanges.IsChecked = true;
            else if (parts[0] == "settings")
            {
                var secs = new Dictionary<string, RadioButton> { { "player", SecPlayer }, { "online", SecOnline }, { "replays", SecReplays }, { "video", SecVideo }, { "controllers", SecPads }, { "saves", SecSaves }, { "dlc", SecDlc }, { "files", SecFiles } };
                OpenSettings(parts.Length > 1 && secs.ContainsKey(parts[1]) ? secs[parts[1]] : SecPlayer);
            }
        }

        void Home_Click(object sender, RoutedEventArgs e) { TabHome.IsChecked = true; }
        void Gear_Click(object sender, RoutedEventArgs e) { OpenSettings(SecPlayer); }
        void Configure_Click(object sender, RoutedEventArgs e) { OpenSettings(SecOnline); }
        void Profile_Click(object sender, RoutedEventArgs e) { OpenSettings(SecPlayer); }
        void Controller_Click(object sender, RoutedEventArgs e) { OpenSettings(SecPads); FindPads(); }
        void Files_Click(object sender, RoutedEventArgs e) { OpenSettings(SecFiles); }
        void Community_Click(object sender, RoutedEventArgs e) { TabCommunity.IsChecked = true; }
        void Site_Click(object sender, RoutedEventArgs e) { Core.Open(Core.Site); }
        void Project_Click(object sender, RoutedEventArgs e) { Core.Open(Core.Site + "project"); }
        void Started_Click(object sender, RoutedEventArgs e) { Core.Open(Core.Site + "getting-started"); }
        void Recomp_Click(object sender, RoutedEventArgs e) { Core.Open("https://github.com/OpenSmash-Ultimate/ssbu-recomp"); }
        void FrameData_Click(object sender, RoutedEventArgs e) { Core.Open("https://ultimateframedata.com/"); }

        void Minimize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState.Minimized; }
        void Maximize_Click(object sender, RoutedEventArgs e) { WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; }
        void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        // ---------------------------------------------------------------- dialog
        /// <summary>a modal card over the window; each button: (label, action or null, primary?)</summary>
        void Show(string title, string text, IEnumerable<string> list, params (string label, Action act, bool primary)[] buttons)
        {
            DialogTitle.Text = title.ToUpperInvariant();
            DialogText.Text = text ?? "";
            DialogText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            DialogList.ItemsSource = list?.Select(l => new TextBlock { Text = l, FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = Res("Muted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) }).ToList();
            DialogButtons.Children.Clear();
            foreach (var b in buttons)
            {
                var btn = new Button { Content = b.label, Height = 44, Margin = new Thickness(10, 0, 0, 0), MinWidth = 110, FontSize = 16, Padding = new Thickness(22, 0, 22, 0) };
                if (b.primary)
                {
                    btn.Style = (Style)Application.Current.Resources["Primary"];
                    btn.Content = new TextBlock { Text = b.label, FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(6, 18, 29)), Margin = new Thickness(22, 0, 22, 0) };
                }
                else btn.Style = (Style)Application.Current.Resources["Outline"];
                var act = b.act;
                btn.Click += (s, e) => { Dialog.Visibility = Visibility.Collapsed; act?.Invoke(); };
                DialogButtons.Children.Add(btn);
            }
            Dialog.Visibility = Visibility.Visible;
        }

        // ---------------------------------------------------------------- play
        void Play_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveQuiet()) return;
            if (Core.GameRunning()) { Show("Already running", "The game is already open.", null, ("OK", null, true)); return; }
            var bad = Core.CheckOnline();
            if (bad.Count > 0) { Show("Online settings", null, bad, ("Open settings", () => OpenSettings(SecOnline), true), ("Cancel", null, false)); return; }
            if (checks == null) { Status("Still checking the game files...", "busy"); return; }
            var badFiles = checks.Where(c => !c.Ok).ToList();
            if (badFiles.Count > 0)
            {
                bool onlyCodecs = badFiles.Count == missingCodecs.Count && missingCodecs.Count > 0;
                if (onlyCodecs)
                    Show("Missing video codecs", "The opening movie needs these Microsoft Store packages; without them the game starts on a black screen.",
                         missingCodecs.Select(m => m.Split('|')[1]),
                         ("Install", InstallCodecs, true), ("Cancel", null, false));
                else Show("The game is not ready", null, badFiles.Select(c => c.Text), ("Game files", () => OpenSettings(SecFiles), true), ("Cancel", null, false));
                return;
            }
            Action go = () =>
            {
                if (Core.CanPrepare() && Core.NeedsPrepare()) { StartPrep(true); return; }      // the shaders first, here, with progress: the game then starts without the long wait
                Start();
            };
            Core.Ticket = null;
            if (!(Core.Online && Core.HasOnline)) { go(); return; }
            // online: a fresh lobby ticket for this start (the lobby refuses a game without one)
            PlayButton.IsEnabled = false;
            Status("Checking your account...", "busy");
            Task.Run(() => Account.LobbyTicket()).ContinueWith(t =>
            {
                PlayButton.IsEnabled = true;
                if (t.IsFaulted) { SetState(); Show("Online", "Could not get your online pass: " + t.Exception.GetBaseException().Message, null, ("Play offline", () => { Core.Online = false; OnlineBox.IsChecked = false; SaveQuiet(); RefreshOnline(); go(); }, false), ("OK", null, true)); return; }
                Core.Ticket = t.Result;
                SetState();
                go();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------------------------------------------------------------- account (Account.cs)
        void ResumeAccount()
        {
            Status("Signing in...", "busy");
            Task.Run(() => { bool ok = Account.Resume(out var err); return Tuple.Create(ok, err); }).ContinueWith(t =>
            {
                ApplyAccount();
                SetState();
                if (!t.Result.Item1) ShowLogin(t.Result.Item2 != null ? "Cannot reach the Arena server: " + t.Result.Item2 : null);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>the account into the window: the player card, Settings > Player, the online state</summary>
        void ApplyAccount()
        {
            bool on = Account.SignedIn;
            AccountBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            OfflineBox.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            if (on)
            {
                AccountName.Text = Account.Me.Display.ToUpperInvariant();
                AccountEmail.Text = Account.Me.Email;
                Core.Nickname = Account.Me.Username;
            }
            RefreshPlayer();
            RefreshOnline();
            if (PageReplays.Visibility == Visibility.Visible && ReplayViewer.Visibility != Visibility.Visible) LoadReplays();      // (0.2.11) the server's replays, the visibility buttons
        }

        void ShowLogin(string error = null)
        {
            LoginError.Text = error ?? "";
            LoginError.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
            LoginOverlay.Visibility = Visibility.Visible;
            LoginButton.Focus();
        }

        System.Threading.CancellationTokenSource browserLogin;

        /// <summary>sign-in in the browser (Account.SignInWithBrowser): ssbuarena.com/launcher, with its CAPTCHA; a second press
        /// starts it again (a new page, the old wait is dropped)</summary>
        void Login_Click(object sender, RoutedEventArgs e)
        {
            browserLogin?.Cancel();
            var cts = browserLogin = new System.Threading.CancellationTokenSource();
            LoginError.Visibility = Visibility.Collapsed;
            LoginWaiting.Visibility = Visibility.Visible;
            LoginButtonText.Text = "OPEN AGAIN";
            Task.Run(() => Account.SignInWithBrowser(cts.Token)).ContinueWith(t =>
            {
                if (cts.IsCancellationRequested) return;          // a newer attempt took over
                browserLogin = null;
                LoginWaiting.Visibility = Visibility.Collapsed;
                LoginButtonText.Text = "SIGN IN";
                if (t.IsFaulted) { ShowLogin(t.Exception.GetBaseException().Message); return; }
                LoginOverlay.Visibility = Visibility.Collapsed;
                ApplyAccount();
                SetState();
                Status("Signed in as " + Account.Me.Display, "good");
                Activate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
        void CreateAccount_Click(object sender, RoutedEventArgs e) { Core.Open(Account.SignUpPage); }
        void ManageAccount_Click(object sender, RoutedEventArgs e) { Core.Open(Account.AccountPage); }
        void ShowLogin_Click(object sender, RoutedEventArgs e) { ShowLogin(); }

        void PlayOffline_Click(object sender, RoutedEventArgs e)
        {
            browserLogin?.Cancel(); browserLogin = null;
            LoginWaiting.Visibility = Visibility.Collapsed; LoginButtonText.Text = "SIGN IN";
            LoginOverlay.Visibility = Visibility.Collapsed;
            Status("Offline: local play only", "warn");
            RefreshOnline();
        }

        void SignOut_Click(object sender, RoutedEventArgs e)
        {
            Account.SignOut();
            ApplyAccount();
            ShowLogin();
        }

        void InstallCodecs()
        {
            Status("Installing video codecs...", "busy");
            var ids = missingCodecs.Select(m => m.Split('|')[0]).ToList();
            Task.Run(() => { foreach (var id in ids) Core.InstallCodec(id); }).ContinueWith(t => RunChecks(), TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------------------------------------------------------------- updates (Updater.cs)
        Process updating;
        Release release;
        List<string> updSteps = new List<string>();
        internal static readonly System.Text.RegularExpressions.Regex UpdField = new System.Text.RegularExpressions.Regex("\"(\\w+)\":\\s*(?:\"([^\"]*)\"|(-?[\\d.]+)|(true|false))");

        /// <summary>at every start: the latest release (in the background); then a newer launcher, the game's update or the
        /// first install. Offline or no release: the launcher goes on as it is.</summary>
        void CheckUpdates()
        {
            if (UpdateWindow.Checked)      // (0.2.12) the update window made the check and the updates: only what it left (the game was running)
            {
                release = UpdateWindow.Release;
                if (release != null && UpdateWindow.GameLeft && Updater.GameOutdated(release) && System.IO.File.Exists(Core.Exe)) UpdateGame();
                return;
            }
            Updater.CleanUp();
            try { System.IO.File.Delete(System.IO.Path.Combine(Core.Repo ?? System.IO.Path.GetDirectoryName(Updater.ExePath), Core.Repo != null ? @"build\launcher_update.log" : "launcher_update.log")); } catch { }
            Updater.Log("launcher " + Updater.Current + ", repo " + (Core.Repo ?? "(not installed)") + ", build " + Core.Build + (Updater.DevCopy ? ", git checkout" : ""));
            if (Updater.Off) { Updater.Log("SSBU_ARENA_UPDATE=off"); return; }
            // a portable copy (build\ beside dump\romfs, no sources to update: crea_pacchetto.py): it plays as it is
            if (Core.Portable && Core.Repo == null) { Updater.Log("portable copy: no install, no updates"); return; }
            Task.Run(() => { try { return Updater.Latest(); } catch (Exception ex) { Updater.Log("release check failed: " + ex.Message); return null; } }).ContinueWith(t =>
            {
                release = t.Result;
                if (release != null)
                    Updater.Log("latest release " + release.Tag + " (version " + release.Version + ", commit " + release.Commit + "); installed commit " +
                                (Updater.Installed ? Updater.InstalledCommit() ?? "unknown" : "-") + "; game exe " + (System.IO.File.Exists(Core.Exe) ? "there" : "missing"));
                if (!Updater.Installed)
                {
                    if (release == null) Show("SSBU Arena", "The first install downloads SSBU Arena from GitHub: no connection or no release found. Check the internet connection and start the launcher again.", null, ("OK", null, true));
                    else Show("Install SSBU Arena", "SSBU Arena " + release.Version + " is installed in this folder:\n" + System.IO.Path.GetDirectoryName(Updater.ExePath) +
                              "\n\nThe setup then asks for your own game files (about 25 GB of space).", null, ("Install", () => StartSetup(true), true), ("Not now", null, false));
                    return;
                }
                // installed but not finished (the setup was stopped or failed): its screen again, it goes on from where it was
                if (!Updater.DevCopy && !System.IO.File.Exists(Core.Exe)) { StartSetup(false); return; }
                if (release == null || Updater.DevCopy) return;
                if (Updater.LauncherOutdated(release)) { UpdateLauncher(); return; }
                if (Updater.GameOutdated(release))
                {
                    if (!System.IO.File.Exists(Core.Exe)) return;     // the install is not finished: its installer does it
                    UpdateGame();
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void UpdateLauncher()
        {
            Status("Updating the launcher to " + release.Version, "busy");
            Task.Run(() => Updater.ReplaceLauncher(release)).ContinueWith(t =>
            {
                if (t.Exception == null) { SaveQuiet(); Application.Current.Shutdown(); return; }
                Status("The launcher could not update itself: " + t.Exception.GetBaseException().Message, "warn");
                if (Updater.GameOutdated(release) && System.IO.File.Exists(Core.Exe)) UpdateGame();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowBusy(string title, string detail, string note)
        {
            TabHome.IsChecked = true;
            PlayButtons.Visibility = Visibility.Collapsed; PrepPanel.Visibility = Visibility.Visible; PrepCancel.Visibility = Visibility.Collapsed;
            PrepTitle.Text = title; PrepPct.Text = "0%"; SetPrepBar(0);
            PrepDetail.Text = detail; PrepEta.Text = ""; PrepNote.Text = note; PrepNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
            StateText.Text = Spaced(title); StateDot.Fill = Res("Accent");
        }

        void HideBusy()
        {
            PrepPanel.Visibility = Visibility.Collapsed; PrepCancel.Visibility = Visibility.Visible; PlayButtons.Visibility = Visibility.Visible;
            SetState();
        }

        void UpdateGame() { UpdateGame(0); }

        void UpdateGame(int attempt)
        {
            // the launcher's own short runs of the game (FindPads: --pad-list at the start) count as running: wait for them
            if (updating == null && prep == null && game == null && Core.GameRunning() && attempt < 20)
            {
                var again = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                again.Tick += (s, e) => { again.Stop(); UpdateGame(attempt + 1); };
                again.Start();
                return;
            }
            if (updating != null || prep != null || game != null || Core.GameRunning())
            {
                Updater.Log("game update not started: " + (updating != null ? "already updating" : prep != null ? "shaders being prepared" : "the game is running"));
                if (updating == null && game == null && prep == null) Status("Close the game to update SSBU Arena to " + release.Version, "warn");
                return;
            }
            Updater.Log("game update: scripts\\install.py (log: build\\update_last.log)");
            ShowBusy("UPDATING", "Downloading " + release.Tag + "...", "SSBU Arena " + release.Version + ": the game starts as soon as it is done.");
            Status("Updating SSBU Arena to " + release.Version, "busy");
            updSteps.Clear();
            try { updating = Updater.RunGameUpdate(l => Dispatcher.BeginInvoke(new Action(() => UpdateLine(l))), c => Dispatcher.BeginInvoke(new Action(() => UpdateEnded(c)))); }
            catch (Exception ex) { updating = null; HideBusy(); Show("Update", ex.Message, null, ("OK", null, true)); }
        }

        /// <summary>install.py's "##ssbu {json}" lines: the plan (step ids), each step's start, the progress inside a step</summary>
        void UpdateLine(string l)
        {
            if (!l.StartsWith("##ssbu ")) return;
            var v = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match m in UpdField.Matches(l))
                v[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            v.TryGetValue("ev", out var ev);
            if (ev == "plan") { foreach (System.Text.RegularExpressions.Match m in new System.Text.RegularExpressions.Regex("\"id\":\\s*\"([^\"]+)\"").Matches(l)) updSteps.Add(m.Groups[1].Value); return; }
            if (!v.TryGetValue("id", out var id)) return;
            int i = Math.Max(0, updSteps.IndexOf(id)), n = Math.Max(1, updSteps.Count);
            double inside = 0;
            if (ev == "progress" && double.TryParse(v.ContainsKey("done") ? v["done"] : "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
                && double.TryParse(v.ContainsKey("total") ? v["total"] : "0", System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var tot) && tot > 0)
                inside = Math.Min(1, d / tot);
            if (ev == "step" && v.TryGetValue("state", out var st) && st != "start") inside = 1;
            double f = (i + inside) / n;
            PrepPct.Text = (int)(f * 100) + "%";
            SetPrepBar(f);
            if (ev == "step" && v.TryGetValue("state", out var s2) && s2 == "start") PrepDetail.Text = UpdateStepName(id);
        }

        internal static string UpdateStepName(string id)
        {
            switch (id)
            {
                case "update": return "Downloading the new version";
                case "precheck": return "Checking this PC";
                case "packages": case "tools": case "media": return "Getting the tools";
                case "inputs": case "dump": case "dlc": return "Checking the game files";
                case "build": case "compile": case "nro": case "link": case "lift": return "Rebuilding the changed parts";
                case "rollback": return "Removing the old build";
                case "unblock": case "shortcuts": return "Finishing";
                default: return "Updating (" + id + ")";
            }
        }

        void UpdateEnded(int code)
        {
            updating = null;
            HideBusy();
            Core.Load();
            RunChecks();
            if (code == 0) { Status("SSBU Arena updated to " + release.Version, "good"); return; }
            Show("Update failed", "SSBU Arena could not update to " + release.Version + " (exit code " + code + "). The game you had is still there. Last lines of build\\update_last.log:",
                 Updater.UpdateLogTail(8), ("Try again", () => UpdateGame(), false), ("OK", null, true));
        }

        // ---------------------------------------------------------------- shader preparation
        Process prep;
        bool prepThenPlay, prepCancelled;
        // (0.2.8) progress and time left, worked out here from two counts the game prints: the stage libraries (the first
        // ~2.5 min of a fresh preparation, when the shader count hardly moves) and the shaders done. Each is weighed by
        // the seconds it took per item in a measured fresh preparation (owner's PC: 8602 libraries at ~64 / s, then 42382
        // shaders at ~85 / s, the first ~2300 of them almost free), so the bar moves with time; the time left is that work
        // times this PC's measured speed (with 60 s of "reference speed" as a start, so the first minute is not a guess),
        // smoothed, counting down by itself. A stopped preparation goes on from its records (runtime/nvn_vk_m4.c).
        const double PrepLibs = 8602, PrepJobs = 42382, PrepJobsFree = 2300, PrepLibRate = 64, PrepJobRate = 85;
        long prepLibs; double prepW0 = -1, prepBar, prepEtaShown = -1; DateTime prepT0, prepEtaAt;
        static readonly System.Text.RegularExpressions.Regex PrepLibsRx = new System.Text.RegularExpressions.Regex(@"nvn_vk_prep: progress: .*stage libraries (\d+)");
        static readonly System.Text.RegularExpressions.Regex PrepField = new System.Text.RegularExpressions.Regex("\"(\\w+)\":(?:\"([^\"]*)\"|(-?\\d+))");

        /// <summary>the game prepares the shaders hidden (Core.StartPrepare); the hero shows the progress; thenPlay: the
        /// match starts when it is done</summary>
        void StartPrep(bool thenPlay)
        {
            if (prep != null || game != null) return;
            if (Core.GameRunning()) { Show("Already running", "Close the game first.", null, ("OK", null, true)); return; }
            prepThenPlay = thenPlay; prepCancelled = false;
            prepLibs = 0; prepW0 = -1; prepBar = 0; prepEtaShown = -1;
            TabHome.IsChecked = true;
            PlayButtons.Visibility = Visibility.Collapsed; PrepPanel.Visibility = Visibility.Visible;
            PrepTitle.Text = "PREPARING SHADERS"; PrepPct.Text = "0%"; SetPrepBar(0);
            PrepDetail.Text = "Starting the game in the background..."; PrepEta.Text = "";
            PrepNote.Text = ""; PrepNote.Visibility = Visibility.Collapsed;
            StateText.Text = Spaced("PREPARING SHADERS"); StateDot.Fill = Res("Accent");
            Status("Preparing the shaders", "busy");
            try { prep = Core.StartPrepare(l => Dispatcher.BeginInvoke(new Action(() => PrepLine(l))), c => Dispatcher.BeginInvoke(new Action(() => PrepEnded(c)))); }
            catch (Exception ex) { prep = null; PrepPanel.Visibility = Visibility.Collapsed; PlayButtons.Visibility = Visibility.Visible; SetState(); Show("Could not prepare the shaders", ex.Message, null, ("OK", null, true)); }
        }

        void SetPrepBar(double f)
        {
            f = Math.Max(0, Math.Min(1, f));
            PrepDone.Width = new GridLength(f, GridUnitType.Star);
            PrepLeft.Width = new GridLength(1 - f, GridUnitType.Star);
        }

        void PrepLine(string l)
        {
            var lm = PrepLibsRx.Match(l);
            if (lm.Success) { long.TryParse(lm.Groups[1].Value, out prepLibs); return; }
            if (!l.StartsWith("##prep ")) return;
            var v = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match m in PrepField.Matches(l)) v[m.Groups[1].Value] = m.Groups[2].Success && m.Groups[2].Value != "" ? m.Groups[2].Value : m.Groups[3].Value;
            if (!v.TryGetValue("state", out var st) || st != "running") return;
            int pct = v.TryGetValue("pct", out var p) && int.TryParse(p, out var pi) ? pi : 0;
            long done = v.TryGetValue("done", out var d) && long.TryParse(d, out var di) ? di : 0;
            bool shaders = v.TryGetValue("phase", out var ph) && ph == "shaders";
            var now = DateTime.UtcNow;
            string eta = "estimating the time...";
            double bar = pct / 100.0;                                   // reading the game data: the game's own share (the first tenth)
            if (shaders)
            {
                double libTotal = prepLibs > 0 || (prepW0 >= 0 && (now - prepT0).TotalSeconds < 30) ? Math.Max(PrepLibs, prepLibs) : 0;      // no libraries on this card: none counted
                double total = libTotal / PrepLibRate + Math.Max(0, Math.Max(PrepJobs, done) - PrepJobsFree) / PrepJobRate;
                double w = Math.Min(libTotal, prepLibs) / PrepLibRate + Math.Max(0, done - PrepJobsFree) / PrepJobRate;
                if (prepW0 < 0) { prepW0 = w; prepT0 = now; }
                bar = 0.10 + 0.90 * Math.Min(1, w / total);
                double el = (now - prepT0).TotalSeconds, k = (el + 60) / (w - prepW0 + 60), raw = Math.Min(3 * 3600, k * Math.Max(0, total - w));
                if (prepEtaShown >= 0) prepEtaShown = Math.Max(0, prepEtaShown - (now - prepEtaAt).TotalSeconds);      // counts down by itself
                prepEtaAt = now;
                if (el >= 5) prepEtaShown = prepEtaShown < 0 ? raw : prepEtaShown + (raw - prepEtaShown) * (raw > prepEtaShown ? 0.04 : 0.08);
                if (prepEtaShown >= 0)
                {
                    int sec = (int)Math.Round(prepEtaShown), min = (int)Math.Round(sec / 60.0);
                    eta = sec < 45 ? "less than a minute left" : min <= 1 ? "about a minute left" : "about " + min + " minutes left";
                }
            }
            prepBar = Math.Max(prepBar, Math.Min(0.99, bar));            // never backwards
            PrepPct.Text = (int)(prepBar * 100) + "%";
            SetPrepBar(prepBar);
            PrepDetail.Text = shaders ? "Compiling the shaders" : "Reading the game data";
            PrepEta.Text = eta;
        }

        void PrepEnded(int code)
        {
            prep = null;
            PrepPanel.Visibility = Visibility.Collapsed; PlayButtons.Visibility = Visibility.Visible;
            if (!prepCancelled && code == 0) { Core.ShaderStamp = Core.ShaderKey(); SaveQuiet(); }      // before the check below reads it
            RunChecks();
            if (prepCancelled) { Status("Shader preparation stopped: it goes on from where it was next time", "warn"); return; }
            if (code == 0) { Status("Shaders prepared", "good"); if (prepThenPlay) Start(); return; }
            if (code == 3) { if (prepThenPlay) Start(); else Show("Shaders", "This graphics card prepares its shaders while you play (no stage libraries): nothing to do here.", null, ("OK", null, true)); return; }
            Show("Shader preparation failed", "Exit code " + code + ". Last lines of build\\prepare.log:", Core.PrepareLogTail(8),
                 ("Play anyway", () => Start(), false), ("OK", null, true));
        }

        void PrepCancel_Click(object sender, RoutedEventArgs e)
        {
            if (prep == null) return;
            prepCancelled = true;
            try { prep.Kill(); } catch { }
        }

        /// <summary>(0.2.8) the prepared shaders thrown away (build\cachek): the next PLAY prepares them again from nothing</summary>
        void DeleteShaders_Click(object sender, RoutedEventArgs e)
        {
            if (prep != null || game != null || Core.GameRunning()) { Show("Shaders", "Close the game and wait for the preparation to end first.", null, ("OK", null, true)); return; }
            Show("Delete the shaders?", "The prepared shaders of this PC are deleted. The next PLAY prepares them again (about 10 minutes). Do it when the game shows wrong pictures or stutters after a driver update.", null,
                 ("Delete", () =>
                 {
                     try { if (System.IO.Directory.Exists(Core.VkCache)) System.IO.Directory.Delete(Core.VkCache, true); }
                     catch (Exception ex) { Show("Shaders", "Some files could not be deleted: " + ex.Message, null, ("OK", null, true)); return; }
                     Core.ShaderStamp = ""; SaveQuiet();
                     RunChecks();
                     Status("Shaders deleted: PLAY prepares them again", "warn");
                 }, false),
                 ("Cancel", null, true));
        }

        void PrepareNow_Click(object sender, RoutedEventArgs e)
        {
            if (!SaveQuiet()) return;
            if (!Core.CanPrepare()) { Show("Shaders", "This build of the game cannot prepare its shaders outside the game yet: it prepares them itself at its first start.", null, ("OK", null, true)); return; }
            StartPrep(false);
        }

        void Start()
        {
            bool first = Core.FirstRun();
            PadLiveStop();
            gameStarted = DateTime.UtcNow;
            try { game = Core.Launch(); }
            catch (Exception ex) { Show("Could not start the game", ex.Message, null, ("OK", null, true)); return; }
            game.Exited += (s, e) => Dispatcher.Invoke(GameExited);
            SetState();
            if (first) StatusText.Text = "First start: preparing the shaders";
            ToTray();
        }

        // ---------------------------------------------------------------- the notification area while the game runs
        System.Windows.Forms.NotifyIcon tray;

        /// <summary>while the game runs the launcher leaves the screen and the taskbar for the notification area (the hidden
        /// icons), as game launchers do; the icon's menu or a double click brings it back</summary>
        void ToTray()
        {
            if (tray == null)
            {
                tray = new System.Windows.Forms.NotifyIcon { Text = "SSBU Arena", Icon = System.Drawing.Icon.ExtractAssociatedIcon(Updater.ExePath) };
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open SSBU Arena", null, (s, e) => Dispatcher.Invoke(FromTray));
                menu.Items.Add("Quit the launcher", null, (s, e) => Dispatcher.Invoke(() => { HideTray(); Application.Current.Shutdown(); }));
                tray.ContextMenuStrip = menu;
                tray.DoubleClick += (s, e) => Dispatcher.Invoke(FromTray);
                Closed += (s, e) => HideTray();
            }
            tray.Visible = true;
            Hide();
        }

        void FromTray()
        {
            base.Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }

        void HideTray() { if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; } }

        DateTime gameStarted;

        void GameExited()
        {
            game = null;
            {   // (0.2.18) the game crashed: its report to the server (CrashReport.cs)
                var started = gameStarted;
                Task.Run(() => { bool sent = CrashReport.SendIfCrashed(started, out bool crashed); return Tuple.Create(crashed, sent); }).ContinueWith(t =>
                {
                    if (t.Exception == null && t.Result.Item1) Status(t.Result.Item2 ? "The game crashed: a report was sent to help fix it" : "The game crashed", "warn");
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
            // (0.2.12) the in-game menu (F1) writes graphics.ini: its choices shown here, and kept by the next PLAY
            try { Core.LoadGfx(); bool was = loading; loading = true; FillSettings(); loading = was; } catch { }
            ReplaysAfterGame();         // (MainWindow.Replays.cs) the cap on local files, the upload of new online matches
            HideTray();
            FromTray();
            SetState();
        }
    }
}
