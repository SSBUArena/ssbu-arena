// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.20) the controllers page's live test: the game's exe with --pad-live (runtime/nnpad_sdl.c) says what the game gets from each pad,
// through the same code a match uses, mappings and dead zone included. Runs only while the page is open.
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
        readonly Dictionary<int, PadView> padViews = new Dictionary<int, PadView>();

        /// <summary>one pad on the page. Buttons as the game's Pro Controller (g_pro in nnpad_sdl.c): bit -> label.</summary>
        sealed class PadView
        {
            static readonly string[] Names = { "A", "B", "X", "Y", "LS", "RS", "L", "R", "ZL", "ZR", "+", "-", "◄", "▲", "►", "▼" };
            static readonly int[] Order = { 0, 1, 2, 3, 6, 7, 8, 9, 10, 11, 4, 5, 12, 13, 14, 15 };
            const double Size = 96;
            public readonly FrameworkElement Root;
            readonly TextBlock title;
            readonly Border[] chips = new Border[16];
            readonly Ellipse left = Dot(Res("Accent")), right = Dot(Res("Accent")), raw = Dot(Res("Muted")), zone;
            string name = "";

            static Ellipse Dot(Brush b) => new Ellipse { Width = 12, Height = 12, Fill = b };

            public PadView()
            {
                var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
                title = new TextBlock { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) };
                panel.Children.Add(title);
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                zone = new Ellipse { Stroke = Res("Warn"), StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };
                row.Children.Add(Stick("LEFT STICK", new UIElement[] { zone, raw, left }));
                row.Children.Add(Stick("RIGHT STICK", new UIElement[] { right }));
                var grid = new WrapPanel { Width = 300, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                foreach (int b in Order)
                {
                    chips[b] = new Border { Width = 50, Height = 34, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 8, 8), BorderThickness = new Thickness(1), BorderBrush = Res("Line"), Background = Brushes.Transparent,
                                            Child = new TextBlock { Text = Names[b], FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
                    grid.Children.Add(chips[b]);
                }
                row.Children.Add(grid);
                panel.Children.Add(row);
                Root = panel;
            }

            static UIElement Stick(string label, UIElement[] marks)
            {
                var c = new Canvas { Width = Size, Height = Size };
                c.Children.Add(new Ellipse { Width = Size, Height = Size, Stroke = Res("Line"), StrokeThickness = 1.5, Fill = Res("Field") });
                foreach (var m in marks) c.Children.Add(m);
                var s = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
                s.Children.Add(c);
                s.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) });
                return s;
            }

            static void Place(Ellipse e, int x, int y)
            {
                Canvas.SetLeft(e, Size / 2 + x / 32767.0 * (Size / 2 - 6) - 6);
                Canvas.SetTop(e, Size / 2 - y / 32767.0 * (Size / 2 - 6) - 6);
            }

            public void Update(int player, string padName, ulong buttons, int lx, int ly, int rx, int ry, int rawX, int rawY, double dz)
            {
                if (name != padName || title.Tag as int? != player) { name = padName; title.Tag = player; title.Text = "Player " + player + ": " + padName; }
                for (int b = 0; b < 16; b++)
                {
                    bool on = (buttons >> b & 1) != 0;
                    chips[b].Background = on ? Res("Accent") : Brushes.Transparent;
                    ((TextBlock)chips[b].Child).Foreground = on ? Res("BgDeep") : Res("Muted");
                }
                Place(left, lx, ly); Place(right, rx, ry); Place(raw, rawX, rawY);
                double d = Math.Max(1, dz * (Size - 12));
                zone.Width = zone.Height = d;
                Canvas.SetLeft(zone, Size / 2 - d / 2); Canvas.SetTop(zone, Size / 2 - d / 2);
            }
        }

        void PadLiveStart()
        {
            if (padLive != null || game != null || prep != null || updating != null || !File.Exists(Core.Exe) || Core.GameRunning()) { if (padLive == null) PadLiveNote.Text = "The test works while the game is closed."; return; }
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
            try { p.StandardInput.Close(); if (!p.WaitForExit(300)) p.Kill(); } catch { }
            padViews.Clear(); PadLiveList.Children.Clear();
        }

        void PadLiveLine(string line)
        {
            if (padLive == null) return;
            var c = line.Split('\t');
            if (c.Length < 11) return;
            var inv = CultureInfo.InvariantCulture;
            if (!int.TryParse(c[1], out int player) || !ulong.TryParse(c[3], out ulong b)) return;
            if (!padViews.TryGetValue(player, out var v))
            {
                v = new PadView(); padViews[player] = v;
                PadLiveList.Children.Clear();
                foreach (var kv in padViews.OrderBy(k => k.Key)) PadLiveList.Children.Add(kv.Value.Root);
            }
            double.TryParse(c[10], NumberStyles.Float, inv, out double dz);
            v.Update(player, c[2], b, int.Parse(c[4], inv), int.Parse(c[5], inv), int.Parse(c[6], inv), int.Parse(c[7], inv), int.Parse(c[8], inv), int.Parse(c[9], inv), dz);
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
