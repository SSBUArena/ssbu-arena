// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.20) the controllers page's live test: the game's exe with --pad-live (runtime/nnpad_sdl.c) says what the game gets from each pad,
// through the same code a match uses, mappings and dead zone included. Runs only while the page is open. (0.2.33) each pad drawn (PadDiagram.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Arena
{
    public partial class MainWindow
    {
        Process padLive;
        bool padRetry;
        readonly Dictionary<int, PadView> padViews = new Dictionary<int, PadView>();

        /// <summary>one pad on the page: its name, the drawn controller (PadDiagram.cs) and a line of numbers</summary>
        sealed class PadView
        {
            public readonly FrameworkElement Root;
            readonly TextBlock title, numbers;
            readonly PadDiagram pad = new PadDiagram { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 4) };
            string name = "";
            static readonly string[] Kinds = { "Xbox-style", "PlayStation-style", "Switch-style", "GameCube-style", "controller", "joystick" };

            public PadView()
            {
                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
                title = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 2), TextWrapping = TextWrapping.Wrap };
                numbers = new TextBlock { FontSize = 13, Foreground = Res("Muted"), Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
                panel.Children.Add(title);
                panel.Children.Add(pad);
                panel.Children.Add(numbers);
                Root = panel;
            }

            static string Pct(int x, int y) => ((int)Math.Round(Math.Min(1, Math.Sqrt((double)x * x + (double)y * y) / 32767.0) * 100)).ToString(CultureInfo.InvariantCulture) + " %";

            /// <summary>one line of the test (MainWindow.PadLiveLine): the 0.2.20 columns, then (0.2.33) the right stick before the dead zone, the triggers,
            /// the kind of controller, outer zone and trigger threshold. An older game exe has only the first ones: then the page's own values.</summary>
            public void Update(string[] c)
            {
                var inv = CultureInfo.InvariantCulture;
                int I(int i, int d) => i < c.Length && int.TryParse(c[i], NumberStyles.Integer, inv, out int v) ? v : d;
                double D(int i, double d) => i < c.Length && double.TryParse(c[i], NumberStyles.Float, inv, out double v) ? v : d;
                int player = I(1, 0);
                pad.Buttons = ulong.TryParse(c[3], out ulong b) ? b : 0;
                pad.LX = I(4, 0); pad.LY = I(5, 0); pad.RX = I(6, 0); pad.RY = I(7, 0); pad.RawLX = I(8, pad.LX); pad.RawLY = I(9, pad.LY);
                pad.DeadZone = D(10, Core.DeadZone);
                pad.RawRX = I(11, pad.RX); pad.RawRY = I(12, pad.RY); pad.TrigL = I(13, -1); pad.TrigR = I(14, -1);
                pad.Kind = I(15, 4); pad.OuterZone = D(16, Core.OuterZone); pad.TriggerThreshold = D(17, Core.TriggerThreshold);
                var forced = Environment.GetEnvironmentVariable("SSBU_ARENA_PAD_KIND");      // (screenshots) another kind's drawing
                if (int.TryParse(forced, out int fk) && fk >= 0 && fk <= 5) pad.Kind = fk;
                pad.Map = Core.Buttons;
                string label = c[2] + "  ·  " + Kinds[Math.Max(0, Math.Min(5, pad.Kind))];
                if (name != label || title.Tag as int? != player) { name = label; title.Tag = player; title.Text = "Player " + player + ": " + label; }
                numbers.Text = "Left stick " + Pct(pad.LX, pad.LY) + "   ·   right stick " + Pct(pad.RX, pad.RY) +
                               (pad.TrigL >= 0 ? "   ·   triggers " + (int)Math.Round(pad.TrigL / 327.67) + " % / " + (int)Math.Round(pad.TrigR / 327.67) + " %" : "") +
                               "   ·   dead zone " + (int)Math.Round(pad.DeadZone * 100) + " %, outer zone " + (int)Math.Round(pad.OuterZone * 100) + " %";
                pad.InvalidateVisual();
            }
        }

        void PadLiveStart()
        {
            if (padLive != null || game != null || prep != null || updating != null || !File.Exists(Core.Exe) || Core.GameRunning())
            {
                if (padLive == null) PadLiveNote.Text = !File.Exists(Core.Exe) ? "The test needs the game installed." : prep != null || updating != null ? "The test starts when the shaders or the update are done." : "The test works while the game is closed.";
                // (0.2.33) tried again while the page is open: the game exe also runs a moment for "Find controllers" (--pad-list), and the test
                // that started with the page never came when the page opened during it
                if (padLive == null && File.Exists(Core.Exe) && !padRetry)
                {
                    padRetry = true;
                    var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                    t.Tick += (s, e) => { t.Stop(); padRetry = false; if (PageSettings.Visibility == Visibility.Visible && SecPads.IsChecked == true && padLive == null) PadLiveStart(); };
                    t.Start();
                }
                return;
            }
            PadLiveNote.Text = "Press buttons and move the sticks. No controller? Connect one: it appears here by itself.";
            try
            {
                var psi = new ProcessStartInfo(Core.Exe, "--pad-live") { WorkingDirectory = Core.Work, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardInput = true };
                var p = new Process { StartInfo = psi };
                p.OutputDataReceived += (s, e) => { if (e.Data != null && e.Data.StartsWith("pad\t")) Dispatcher.BeginInvoke(new Action(() => PadLiveLine(e.Data))); };
                p.Start();
                p.BeginOutputReadLine();
                padLive = p;
            }
            catch (Exception ex) { PadLiveNote.Text = "The test could not start: " + ex.Message; }
        }

        void PadLiveStop()
        {
            var p = padLive; padLive = null;
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
            if (!padViews.TryGetValue(player, out var v))
            {
                v = new PadView(); padViews[player] = v;
                PadLiveList.Children.Clear();
                foreach (var kv in padViews.OrderBy(k => k.Key)) PadLiveList.Children.Add(kv.Value.Root);
            }
            v.Update(c);
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
        }
        void DeadZone_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (DeadZoneText != null) DeadZoneText.Text = (int)Math.Round(e.NewValue) + " %";
            if (loading) return;
            Core.DeadZone = Math.Round(e.NewValue) / 100.0;
            try { Core.SaveDeadZone(); } catch { }
        }
    }
}
