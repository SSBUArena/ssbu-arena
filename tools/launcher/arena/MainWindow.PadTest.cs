// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.20) the controllers page's live test: the game's exe with --pad-live (runtime/nnpad_sdl.c) says what the game gets from each pad,
// through the same code a match uses, mappings and dead zone included. Runs only while the page is open.
// (0.2.33) the page top to bottom: a card per player (its controller, connected or not, the choice) and the keyboard's; the selected
// player's controller drawn big and live (PadDiagram.cs), the keyboard's keys when the keyboard is its input, what to do without one;
// the settings the drawing shows (a slider moves its ring or mark at once); "Test vibration" and "Reset to defaults".
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Arena
{
    public partial class MainWindow
    {
        Process padLive;
        bool padRetry;
        int padSel = int.TryParse(Environment.GetEnvironmentVariable("SSBU_ARENA_PAD_SEL"), out int ps) && ps == 2 ? 2 : 1;      // the player whose controller the page draws (env: screenshots)
        DateTime padPreviewUntil;                          // a slider moved: the drawing shows the page's values, not the game's, until then
        DispatcherTimer padTick;
        DateTime padNoteUntil;                             // a message of the page's own (vibration, reset) stays until then
        readonly Dictionary<int, PadView> padViews = new Dictionary<int, PadView>();
        static readonly string[] PadKinds = { "Xbox-style", "PlayStation-style", "Switch-style", "GameCube", "controller", "joystick" };
        static readonly Brush PadOk = new SolidColorBrush(Color.FromRgb(0x3D, 0xDC, 0x97));

        /// <summary>one connected pad: its drawing and what the test said last</summary>
        sealed class PadView
        {
            public readonly PadDiagram Pad = new PadDiagram { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            public string Name = "";
            public DateTime Seen;

            /// <summary>one line of the test: the 0.2.20 columns, then (0.2.33) the right stick before the dead zone, the triggers, the kind of
            /// controller, outer zone and trigger threshold (an older game exe has only the first ones: then the page's own values)</summary>
            public void Update(string[] c, bool preview)
            {
                var inv = CultureInfo.InvariantCulture;
                int I(int i, int d) => i < c.Length && int.TryParse(c[i], NumberStyles.Integer, inv, out int v) ? v : d;
                double D(int i, double d) => i < c.Length && double.TryParse(c[i], NumberStyles.Float, inv, out double v) ? v : d;
                var p = Pad;
                p.Empty = false;
                p.Buttons = ulong.TryParse(c[3], out ulong b) ? b : 0;
                p.LX = I(4, 0); p.LY = I(5, 0); p.RX = I(6, 0); p.RY = I(7, 0); p.RawLX = I(8, p.LX); p.RawLY = I(9, p.LY);
                p.RawRX = I(11, p.RX); p.RawRY = I(12, p.RY); p.TrigL = I(13, -1); p.TrigR = I(14, -1);
                p.Kind = I(15, 4);
                if (!preview) { p.DeadZone = D(10, Core.DeadZone); p.OuterZone = D(16, Core.OuterZone); p.TriggerThreshold = D(17, Core.TriggerThreshold); }
                if (int.TryParse(Environment.GetEnvironmentVariable("SSBU_ARENA_PAD_KIND"), out int fk) && fk >= 0 && fk <= 5) p.Kind = fk;      // (screenshots) another kind's drawing
                p.Map = Core.Buttons;
                Name = c[2]; Seen = DateTime.Now;
                p.InvalidateVisual();
            }
        }

        // ---------------------------------------------------------------- the test process
        void PadLiveStart()
        {
            RefreshPads();
            if (padLive != null || game != null || prep != null || updating != null || !File.Exists(Core.Exe) || Core.GameRunning())
            {
                if (padLive == null) PadLiveNote.Text = !File.Exists(Core.Exe) ? "The live view needs the game installed." : prep != null || updating != null ? "Live view after the shaders or the update." : "Live view while the game is closed.";
                // (0.2.33) tried again while the page is open: the game exe also runs a moment for "Find controllers" (--pad-list), and the test
                // that started with the page never came when the page opened during it
                if (padLive == null && File.Exists(Core.Exe) && !padRetry)
                {
                    padRetry = true;
                    var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    t.Tick += (s, e) => { t.Stop(); padRetry = false; if (PageSettings.Visibility == Visibility.Visible && SecPads.IsChecked == true && padLive == null) PadLiveStart(); };
                    t.Start();
                }
                return;
            }
            PadLiveNote.Text = "Live: press buttons, move the sticks";
            try
            {
                var psi = new ProcessStartInfo(Core.Exe, "--pad-live") { WorkingDirectory = Core.Work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
                var p = new Process { StartInfo = psi };
                p.OutputDataReceived += (s, e) =>
                {
                    if (e.Data == null) return;
                    if (e.Data.StartsWith("pad\t")) Dispatcher.BeginInvoke(new Action(() => PadLiveLine(e.Data)));
                    else if (e.Data.StartsWith("rumble\t")) Dispatcher.BeginInvoke(new Action(() => { PadLiveNote.Text = "Vibrating..."; padNoteUntil = DateTime.Now.AddSeconds(2); }));
                };
                p.Start();
                p.BeginOutputReadLine();
                padLive = p;
            }
            catch (Exception ex) { PadLiveNote.Text = "The live view could not start: " + ex.Message; }
            if (padTick == null)
            {
                padTick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
                padTick.Tick += (s, e) => { if (padLive == null) { padTick.Stop(); return; } RefreshPads(); };
            }
            padTick.Start();
        }

        void PadLiveStop()
        {
            var p = padLive; padLive = null;
            padTick?.Stop();
            if (p == null) return;
            try { p.StandardInput.Close(); if (!p.WaitForExit(300)) { p.Kill(); p.WaitForExit(1000); } } catch { }      // (gone before a new test starts: Core.GameRunning counts it)
            padViews.Clear(); PadLiveList.Children.Clear();
        }

        void PadLiveLine(string line)
        {
            if (padLive == null) return;
            var c = line.Split('\t');
            if (c.Length < 11) return;
            if (!int.TryParse(c[1], out int player) || !ulong.TryParse(c[3], out ulong _)) return;
            bool fresh = !padViews.TryGetValue(player, out var v);
            if (fresh) padViews[player] = v = new PadView();
            v.Update(c, DateTime.Now < padPreviewUntil);
            if (fresh) RefreshPads();
        }

        // ---------------------------------------------------------------- the cards and the drawn controller
        void PadCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ComboBox>(d) != null) return;      // (the choice inside the card)
            if (sender is FrameworkElement f && int.TryParse(f.Tag as string, out int p)) { padSel = p; RefreshPads(); }
        }
        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            for (; d != null; d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) if (d is T t) return t;
            return null;
        }

        /// <summary>which player the keyboard plays as (0 none): Automatic is the first player without a controller</summary>
        int KeyboardPlayer()
        {
            int k = KbBox?.SelectedIndex ?? 0;
            if (k == 1 || k == 2) return k;
            if (k == 3) return 0;
            return !PadOn(1) ? 1 : !PadOn(2) ? 2 : 0;
        }
        bool PadOn(int p) => padViews.TryGetValue(p, out var v) && (DateTime.Now - v.Seen).TotalSeconds < 2.5;

        /// <summary>the cards' states and the drawing of the selected player (also every second while the page is open: a pad that went away)</summary>
        void RefreshPads()
        {
            if (P1Card == null || PadStage == null) return;
            int kb = KeyboardPlayer();
            for (int p = 1; p <= 2; p++)
            {
                var card = p == 1 ? P1Card : P2Card; var state = p == 1 ? P1State : P2State; var dot = p == 1 ? P1Dot : P2Dot; var box = p == 1 ? P1Box : P2Box;
                card.BorderBrush = Res(p == padSel ? "Accent" : "Line");
                if (PadOn(p)) { var v = padViews[p]; state.Text = v.Name + (kb == p ? " + keyboard" : ""); state.Foreground = Res("Text"); dot.Fill = PadOk; }
                else if (kb == p) { state.Text = "Keyboard"; state.Foreground = Res("Text"); dot.Fill = PadOk; }
                else { state.Text = box?.SelectedIndex == 1 ? "No controller (left empty)" : padLive == null ? "Not checked yet" : "Not connected"; state.Foreground = Res("Muted"); dot.Fill = Res("Muted"); }
            }
            KbState.Text = kb == 0 ? "Not used" : "Plays as player " + kb;
            // the stage
            PadLiveList.Children.Clear();
            PadEmpty.Visibility = Visibility.Collapsed; PadKeys.Visibility = Visibility.Collapsed;
            if (PadOn(padSel))
            {
                var v = padViews[padSel];
                PadStageTitle.Text = "Player " + padSel + "  ·  " + v.Name + "  ·  " + PadKinds[Math.Max(0, Math.Min(5, v.Pad.Kind))];
                PadLiveList.Children.Add(v.Pad);
            }
            else if (kb == padSel)
            {
                PadStageTitle.Text = "Player " + padSel + "  ·  keyboard";
                FillKeys();
                PadKeys.Visibility = Visibility.Visible;
            }
            else
            {
                PadStageTitle.Text = "Player " + padSel + "  ·  no controller";
                PadLiveList.Children.Add(new PadDiagram { Empty = true, Map = Core.Buttons, DeadZone = Core.DeadZone, OuterZone = Core.OuterZone, TriggerThreshold = Core.TriggerThreshold, HorizontalAlignment = HorizontalAlignment.Center });
                PadEmptyTitle.Text = (P1Box != null && (padSel == 1 ? P1Box : P2Box).SelectedIndex == 1) ? "Player " + padSel + " has no controller (None)" : "No controller for player " + padSel;
                PadEmpty.Visibility = Visibility.Visible;
            }
            foreach (var v in padViews.Values) v.Pad.Height = PadStage.Height;
            foreach (var d in PadLiveList.Children.OfType<PadDiagram>()) d.Height = PadStage.Height;
            if (RumbleTest != null) RumbleTest.IsEnabled = PadOn(padSel);
            if (padLive != null && DateTime.Now > padNoteUntil) PadLiveNote.Text = padViews.Count == 0 ? "Waiting for a controller..." : "Live: press buttons, move the sticks";
        }

        /// <summary>the keyboard's keys (controllers.ini [keyboard], else the game's defaults) by what they do in Smash</summary>
        void FillKeys()
        {
            var k = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "A", "Z" }, { "B", "X" }, { "X", "C" }, { "Y", "V" }, { "L", "Q" }, { "R", "E" }, { "ZL", "A" }, { "ZR", "S" }, { "PLUS", "ENTER" }, { "MINUS", "BACKSPACE" },
                { "LEFT", "J" }, { "UP", "I" }, { "RIGHT", "L" }, { "DOWN", "K" }, { "LSTICK_LEFT", "LEFT" }, { "LSTICK_UP", "UP" }, { "LSTICK_RIGHT", "RIGHT" }, { "LSTICK_DOWN", "DOWN" } };
            try { foreach (var kv in Core.ReadIniSection(Core.PadIni, "keyboard")) if (kv.Key != "player") k[kv.Key] = kv.Value; } catch { }
            string Of(params string[] ks) => string.Join("  ", ks.Select(x => k.TryGetValue(x, out var v) ? v : "none").Where(v => !string.Equals(v, "none", StringComparison.OrdinalIgnoreCase)).Distinct().Select(v => Arrow(v)));
            var rows = new List<Tuple<string, string>> {
                Tuple.Create("Move", Of("LSTICK_LEFT", "LSTICK_UP", "LSTICK_RIGHT", "LSTICK_DOWN")), Tuple.Create("Attack", Of("A")), Tuple.Create("Special", Of("B")),
                Tuple.Create("Jump", Of("X", "Y")), Tuple.Create("Grab", Of("L", "R")), Tuple.Create("Shield", Of("ZL", "ZR")),
                Tuple.Create("Smash attacks", Of("RSTICK_LEFT", "RSTICK_UP", "RSTICK_RIGHT", "RSTICK_DOWN")), Tuple.Create("Taunts", Of("LEFT", "UP", "RIGHT", "DOWN")), Tuple.Create("Pause", Of("PLUS")) };
            var g = new UniformGrid { Columns = 3, Width = 620 };
            foreach (var r in rows)
            {
                if (r.Item2 == "") continue;
                var sp = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
                sp.Children.Add(new TextBlock { Text = r.Item1, FontSize = 14, Foreground = Res("Muted") });
                sp.Children.Add(new Border { Background = Res("Field"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                                             Child = new TextBlock { Text = r.Item2, FontSize = 17, FontWeight = FontWeights.SemiBold } });
                g.Children.Add(sp);
            }
            var all = new StackPanel();
            all.Children.Add(g);
            all.Children.Add(new TextBlock { Text = "The keyboard counts only while the game window has the focus. Keys: controllers.ini [keyboard], or the game's settings menu (F1).", FontSize = 13, Foreground = Res("Muted"), Margin = new Thickness(8, 10, 8, 0), TextWrapping = TextWrapping.Wrap, MaxWidth = 600 });
            PadKeys.Children.Clear();
            PadKeys.Children.Add(all);
        }
        static string Arrow(string v) => v.Equals("LEFT", StringComparison.OrdinalIgnoreCase) ? "←" : v.Equals("RIGHT", StringComparison.OrdinalIgnoreCase) ? "→" : v.Equals("UP", StringComparison.OrdinalIgnoreCase) ? "↑" : v.Equals("DOWN", StringComparison.OrdinalIgnoreCase) ? "↓" : v.ToUpperInvariant();

        /// <summary>a slider moved: the drawing shows it at once (the game's own values come back with the test's next lines)</summary>
        void PadPreview()
        {
            if (DeadZoneSlider == null || OuterZoneSlider == null || TriggerSlider == null) return;
            padPreviewUntil = DateTime.Now.AddSeconds(2.5);
            foreach (var d in padViews.Values.Select(v => v.Pad).Concat(PadLiveList?.Children.OfType<PadDiagram>() ?? Enumerable.Empty<PadDiagram>()))
            {
                d.DeadZone = Math.Round(DeadZoneSlider.Value) / 100.0; d.OuterZone = Math.Round(OuterZoneSlider.Value) / 100.0; d.TriggerThreshold = Math.Round(TriggerSlider.Value) / 100.0;
                d.InvalidateVisual();
            }
        }

        void RumbleTest_Click(object sender, RoutedEventArgs e)
        {
            padNoteUntil = DateTime.Now.AddSeconds(3);
            if (padLive == null || !PadOn(padSel)) { PadLiveNote.Text = "Connect a controller for player " + padSel + " first."; return; }
            if (RumbleBox.IsChecked != true) { PadLiveNote.Text = "Vibration is off: turn it on to test it."; return; }
            try { padLive.StandardInput.WriteLine("rumble " + padSel); padLive.StandardInput.Flush(); } catch { PadLiveNote.Text = "The test could not reach the controller."; }
        }

        void PadReset_Click(object sender, RoutedEventArgs e)
        {
            // the built-in defaults of controllers.ini [general] (runtime/nnpad_sdl.c cfg_defaults); each control saves as when moved by hand
            DeadZoneSlider.Value = 10; OuterZoneSlider.Value = 98; TriggerSlider.Value = 33; RumbleSlider.Value = 100; RumbleBox.IsChecked = true;
            InvLX.IsChecked = InvLY.IsChecked = InvRX.IsChecked = InvRY.IsChecked = false;
            PadLiveNote.Text = "Back to the defaults."; padNoteUntil = DateTime.Now.AddSeconds(3);
        }

        /// <summary>(0.2.31) outer zone, trigger threshold, vibration, invert: the texts follow, then [general] is saved</summary>
        void FillPadGeneralTexts()
        {
            if (OuterZoneText != null && OuterZoneSlider != null) OuterZoneText.Text = (int)Math.Round(OuterZoneSlider.Value) + " %";
            if (TriggerText != null && TriggerSlider != null) TriggerText.Text = (int)Math.Round(TriggerSlider.Value) + " %";
            if (RumbleText != null && RumbleSlider != null) RumbleText.Text = (int)Math.Round(RumbleSlider.Value) + " %";
            if (RumbleSlider != null && RumbleBox != null) RumbleSlider.IsEnabled = RumbleBox.IsChecked == true;
        }
        void PadGeneral_Changed(object sender, RoutedEventArgs e)
        {
            FillPadGeneralTexts();
            if (loading || OuterZoneSlider == null || TriggerSlider == null || RumbleBox == null || RumbleSlider == null || InvLX == null || InvLY == null || InvRX == null || InvRY == null) return;
            Core.OuterZone = Math.Round(OuterZoneSlider.Value) / 100.0;
            Core.TriggerThreshold = Math.Round(TriggerSlider.Value) / 100.0;
            Core.Rumble = RumbleBox.IsChecked == true;
            Core.RumbleStrength = Math.Round(RumbleSlider.Value) / 100.0;
            Core.Invert[0] = InvLX.IsChecked == true; Core.Invert[1] = InvLY.IsChecked == true; Core.Invert[2] = InvRX.IsChecked == true; Core.Invert[3] = InvRY.IsChecked == true;
            try { Core.SavePadGeneral(); } catch { }
            PadPreview();
        }
        void DeadZone_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DeadZoneText != null) DeadZoneText.Text = (int)Math.Round(e.NewValue) + " %";
            if (loading) return;
            Core.DeadZone = Math.Round(e.NewValue) / 100.0;
            try { Core.SaveDeadZone(); } catch { }
            PadPreview();
        }
    }
}
