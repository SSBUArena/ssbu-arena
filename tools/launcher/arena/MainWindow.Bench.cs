// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.58) Settings > Benchmark: runs the fixed test of Bench.cs, follows it (progress from the game's log, a time limit),
// shows the results (three cards, a verdict, the PC), copies them as text and, only when the player ticks the switch and
// presses Send, sends them anonymously (Bench.Send). The switch is off every time the page shows new results.
// SSBU_BENCH_UI=0: the section is not shown.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Arena
{
    public partial class MainWindow
    {
        Process bench;
        DateTime benchT0;
        DispatcherTimer benchTimer;
        bool benchStopped;
        Bench.Result benchLast;

        void InitBench()
        {
            SecBench.Visibility = Bench.Shown ? Visibility.Visible : Visibility.Collapsed;
            // one more side tab: the eleven tabs a little shorter (42 -> 38), so the last one stays above "Back to home"
            if (Bench.Shown && SecBench.Parent is Panel side) foreach (var rb in side.Children.OfType<RadioButton>()) rb.Height = 38;
            // (test aid) SSBU_ARENA_SNAP_DIR=<folder>: a file snap.txt there holding a .png path makes the launcher save its own window
            // there (WPF's picture of itself: right even when another window covers the launcher), then the request is deleted
            string snap = Environment.GetEnvironmentVariable("SSBU_ARENA_SNAP_DIR");
            if (string.IsNullOrEmpty(snap)) return;
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            t.Tick += (s, e) =>
            {
                string req = System.IO.Path.Combine(snap, "snap.txt");
                if (!System.IO.File.Exists(req)) return;
                try
                {
                    string png = System.IO.File.ReadAllText(req).Trim(); System.IO.File.Delete(req);
                    var el = (FrameworkElement)Content;
                    var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)el.ActualWidth, (int)el.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    var dv = new DrawingVisual();
                    using (var dc = dv.RenderOpen()) { dc.DrawRectangle(Res("Bg"), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight)); dc.DrawRectangle(new VisualBrush(el), null, new Rect(0, 0, el.ActualWidth, el.ActualHeight)); }
                    bmp.Render(dv);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder(); enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    using (var f = System.IO.File.Create(png)) enc.Save(f);
                }
                catch (Exception ex) { Updater.Log("snap: " + ex.Message); }
            };
            t.Start();
        }

        /// <summary>the section was opened: the last results (build\benchmark\last.json) or what is missing</summary>
        void BenchShow()
        {
            if (bench != null) return;
            string why = Bench.NotPossible();
            BenchRun.IsEnabled = why == null;
            if (benchLast == null) benchLast = Bench.Last();
            if (benchLast != null) BenchFill(benchLast, false);
            BenchState.Text = why ?? (benchLast == null ? "No results yet on this PC." : "");
            BenchState.Foreground = why != null ? Res("Warn") : Res("Muted");
        }

        void BenchRun_Click(object sender, RoutedEventArgs e)
        {
            if (bench != null) return;
            if (!SaveQuiet()) return;
            string why = Bench.NotPossible();
            if (why != null) { Show("Benchmark", why, null, ("OK", null, true)); return; }
            if (game != null || prep != null || updating != null || Core.GameRunning()) { Show("Benchmark", "Close the game first (and wait for an update or the shader preparation to end).", null, ("OK", null, true)); return; }
            if (checks == null) { Show("Benchmark", "The launcher is still checking the game files: try again in a moment.", null, ("OK", null, true)); return; }
            var bad = checks.Where(c => !c.Ok).Select(c => c.Text).Where(t => t != Core.ArenaMenuMissingText).ToList();      // (the benchmark uses the original menus)
            if (bad.Count > 0) { Show("The game is not ready", null, bad, ("Game files", () => OpenSettings(SecFiles), true), ("Cancel", null, false)); return; }
            if (Core.CanPrepare() && Core.NeedsPrepare())
            {
                Show("Shaders first", "The game's shaders are not prepared on this PC yet (a one-time step of several minutes). Without them the benchmark would mostly measure their preparation. " +
                     "Prepare them now, then run the benchmark.", null, ("Prepare now", () => StartPrep(false), true), ("Cancel", null, false));
                return;
            }
            PadLiveStop();
            benchStopped = false;
            try { bench = Bench.Start(); }
            catch (Exception ex) { bench = null; Show("Could not start the benchmark", ex.Message, null, ("OK", null, true)); return; }
            benchT0 = DateTime.UtcNow;
            bench.Exited += (s, a) => Dispatcher.BeginInvoke(new Action(BenchEnded));
            BenchRun.IsEnabled = false; BenchStop.Visibility = Visibility.Visible; BenchBar.Visibility = Visibility.Visible; SetBenchBar(0);
            BenchState.Foreground = Res("Muted");
            BenchState.Text = "Starting the game...";
            Status("Benchmark running", "busy");
            benchTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            benchTimer.Tick += (s, a) => BenchTick();
            benchTimer.Start();
        }

        void SetBenchBar(double f)
        {
            f = Math.Max(0, Math.Min(1, f));
            BenchDone.Width = new GridLength(f, GridUnitType.Star);
            BenchLeft.Width = new GridLength(1 - f, GridUnitType.Star);
        }

        void BenchTick()
        {
            if (bench == null) return;
            var el = DateTime.UtcNow - benchT0;
            string what = Bench.Phase(out double share, out bool fought);
            // a smooth bar: the phase's share, and within the long first phases the time (about 2.5 minutes in all)
            double byTime = Math.Min(0.97, el.TotalSeconds / 150.0);
            SetBenchBar(Math.Max(share, Math.Min(byTime, share + 0.2)));
            BenchState.Text = what + "...   " + (int)el.TotalMinutes + ":" + el.Seconds.ToString("00") + "   (the game closes by itself at the end)";
            if (el > Bench.Limit) { Updater.Log("benchmark: over " + Bench.Limit.TotalMinutes + " minutes, stopped"); BenchKill(); }
            else if (!fought && el > Bench.NoFightLimit) { Updater.Log("benchmark: no fight after " + Bench.NoFightLimit.TotalSeconds + " s (the menus?), stopped"); BenchKill(); }
        }

        void BenchStop_Click(object sender, RoutedEventArgs e) { benchStopped = true; BenchKill(); }

        void BenchKill()
        {
            var p = bench;
            if (p == null) return;
            // the game itself (cmd.exe /c is its parent: killing cmd would leave the game running)
            try
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(Bench.ExePath);
                foreach (var x in Process.GetProcessesByName(name))
                    try { if (x.StartTime.ToUniversalTime() >= benchT0.AddSeconds(-5)) x.Kill(); } catch { }
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        void BenchEnded()
        {
            if (bench == null) return;
            benchTimer?.Stop();
            double total = (DateTime.UtcNow - benchT0).TotalSeconds;
            bench = null;
            BenchKill();      // (nothing left normally)
            BenchStop.Visibility = Visibility.Collapsed; BenchBar.Visibility = Visibility.Collapsed;
            BenchRun.IsEnabled = Bench.NotPossible() == null;
            var r = Bench.Collect(total, out string error);
            try { Core.LoadGfx(); } catch { }
            SetState();
            if (r == null)
            {
                BenchState.Foreground = Res("Warn");
                BenchState.Text = benchStopped ? "Stopped: no results." : "The benchmark did not finish.";
                Status(benchStopped ? "Benchmark stopped" : "Benchmark failed", "warn");
                if (!benchStopped) Show("Benchmark", error, null, ("OK", null, true));
                return;
            }
            benchLast = r;
            BenchState.Foreground = Res("Muted");
            BenchState.Text = "Done in " + (int)(total / 60) + ":" + ((int)total % 60).ToString("00") + ".";
            Status("Benchmark done", "good");
            BenchFill(r, true);
            FromTray();
        }

        // ---------------------------------------------------------------- the results
        void BenchFill(Bench.Result r, bool fresh)
        {
            var m = Bench.Sec(r, "match"); var o = Bench.Sec(r, "rollback"); var g = r.Game;
            BenchResults.Visibility = Visibility.Visible;
            BenchWhen.Text = "RESULTS OF " + r.When + (r.TotalS > 0 ? "  ·  " + Bench.F(r.TotalS, "0") + " S IN ALL" : "");
            BenchVerdict.Text = Verdict(r);
            BenchCards.Children.Clear();
            BenchCards.Children.Add(BenchCard("MATCH", Bench.F(Bench.Num(m, "fps_avg"), "0.0"), "fps on average", new[]
            {
                ("1% low", Bench.F(Bench.Num(m, "fps_low1"), "0.0") + " fps"),
                ("Frame time p99", Bench.F(Bench.Num(m, "ft_p99"), "0.0") + " ms"),
                ("Slowest frame", Bench.F(Bench.Num(m, "ft_max"), "0.0") + " ms"),
                ("Late frames", Bench.F(Bench.Num(m, "late_pct"), "0.0") + " %"),
                ("Game frame (CPU)", Bench.F(Bench.Num(m, "cpu_ms_avg"), "0.0") + " ms, p99 " + Bench.F(Bench.Num(m, "cpu_ms_p99"), "0.0")),
            }, "No rollbacks: as an offline match."));
            BenchCards.Children.Add(BenchCard("ONLINE WITH ROLLBACKS", Bench.F(Bench.Num(o, "rb_ms_avg"), "0.0") + " ms", "per rollback frame, on average", new[]
            {
                ("Rollbacks", Bench.F(Bench.Num(o, "rollbacks"), "0")),
                ("Median / p99", Bench.F(Bench.Num(o, "rb_ms_med"), "0.0") + " / " + Bench.F(Bench.Num(o, "rb_ms_p99"), "0.0") + " ms"),
                ("Over one frame", Bench.F(Bench.Num(o, "rb_over_pct"), "0.0") + " %"),
                ("FPS avg / 1% low", Bench.F(Bench.Num(o, "fps_avg"), "0.0") + " / " + Bench.F(Bench.Num(o, "fps_low1"), "0.0")),
                ("Frame time p99", Bench.F(Bench.Num(o, "ft_p99"), "0.0") + " ms"),
            }, "The opponent's inputs one frame late: about 280 rollbacks."));
            BenchCards.Children.Add(BenchCard("LOADING AND SHADERS", Bench.F(Bench.Num(g, "shader_holds_fight"), "0"), "shader hitches in the match", new[]
            {
                ("Hitch time", Bench.F(Bench.Num(g, "shader_hold_ms_fight"), "0") + " ms"),
                ("Game start", Bench.F(Bench.Num(g, "boot_s"), "0.0") + " s"),
                ("Match load", Bench.F(Bench.Num(g, "match_load_s"), "0.0") + " s"),
                ("Start to fight", Bench.F(Bench.Num(g, "to_fight_s"), "0.0") + " s"),
            }, "Shaders made during the match stop a frame."));
            string Sv(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : "?";
            var pc = r.Pc; var st = r.Settings;
            BenchPc.Text = Sv(pc, "cpu") + " · " + Sv(pc, "cpu_threads") + " threads · " + Sv(pc, "ram_gb") + " GB · " + Sv(pc, "gpu") + (pc != null && pc.ContainsKey("gpu_driver") && pc["gpu_driver"] != null ? " (driver " + Sv(pc, "gpu_driver") + ")" : "") + " · " + Sv(pc, "os") +
                           "\n" + Sv(st, "renderer") + " · " + Sv(st, "width") + "x" + Sv(st, "height") + " " + Sv(st, "window") + " · scale " + Sv(st, "resolution_scale") + " · vsync " + Sv(st, "vsync") + (st != null && st.ContainsKey("display_hz") ? " (" + Sv(st, "display_hz") + " Hz)" : "") +
                           " · stage background " + Sv(st, "stage_bg") + " · launcher " + r.Version + (r.Commit != null ? ", game " + r.Commit : "");
            if (fresh) { BenchShare.IsChecked = false; BenchSendNote.Visibility = Visibility.Collapsed; }
            BenchSend.IsEnabled = BenchShare.IsChecked == true;
        }

        /// <summary>one or two sentences over the cards. From the shares, which are steady from run to run (the 1% low and the p99
        /// follow a handful of frames: a moment of load from another program moves them a lot)</summary>
        static string Verdict(Bench.Result r)
        {
            var m = Bench.Sec(r, "match"); var o = Bench.Sec(r, "rollback");
            double late = Bench.Num(m, "late_pct"), fps = Bench.Num(m, "fps_avg"), cpu = Bench.Num(m, "cpu_ms_avg"), over = Bench.Num(o, "rb_over_pct"), olate = Bench.Num(o, "late_pct");
            string a = double.IsNaN(late) ? "The match was not measured." :
                       fps >= 59 && late < 2 ? "Smooth: the match holds 60 frames per second." :
                       fps >= 55 && late < 6 ? "Mostly smooth: a few frames are late." :
                       "Frames are late often: a lower Resolution scale or window size (Video) can help.";
            if (!double.IsNaN(cpu) && cpu > 10) a += " The game frame is heavy for this CPU.";
            string b = double.IsNaN(over) ? "" :
                       over < 1 && olate < 3 ? " Online with rollbacks it stays smooth too." :
                       over < 5 ? " Online, a few rollbacks take longer than one frame: a short hitch now and then with a bad connection." :
                       " Online, many rollbacks take longer than one frame: with a bad connection matches can slow down.";
            return a + b;
        }

        Border BenchCard(string title, string big, string bigNote, (string k, string v)[] rows, string foot)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("Muted") });
            var bigLine = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 10) };
            bigLine.Children.Add(new TextBlock { Text = big, FontSize = 34, FontWeight = FontWeights.SemiBold, Foreground = Res("Accent") });
            bigLine.Children.Add(new TextBlock { Text = bigNote, FontSize = 14, Foreground = Res("Muted"), Margin = new Thickness(10, 0, 0, 7), VerticalAlignment = VerticalAlignment.Bottom, TextWrapping = TextWrapping.Wrap, MaxWidth = 140 });
            sp.Children.Add(bigLine);
            foreach (var (k, v) in rows)
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(new TextBlock { Text = k, FontSize = 15, Foreground = Res("Muted") });
                var tv = new TextBlock { Text = v, FontSize = 15, Foreground = Res("Text"), Margin = new Thickness(12, 0, 0, 0) };
                Grid.SetColumn(tv, 1); g.Children.Add(tv);
                sp.Children.Add(g);
            }
            sp.Children.Add(new TextBlock { Text = foot, FontSize = 13, Foreground = Res("Dim"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap });
            return new Border { Child = sp, Width = 274, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(16, 14, 16, 14), CornerRadius = new CornerRadius(8),
                                Background = Res("Card"), BorderBrush = Res("Line"), BorderThickness = new Thickness(1) };
        }

        void BenchCopy_Click(object sender, RoutedEventArgs e)
        {
            if (benchLast == null) return;
            try { Clipboard.SetText(Bench.CopyText(benchLast)); Status("Benchmark results copied", "good"); }
            catch (Exception ex) { Show("Copy results", "The clipboard is busy: " + ex.Message, null, ("OK", null, true)); }
        }

        void BenchFolder_Click(object sender, RoutedEventArgs e) { System.IO.Directory.CreateDirectory(Bench.Dir); Core.Open(Bench.Dir); }

        void BenchShare_Changed(object sender, RoutedEventArgs e) { if (BenchSend != null) BenchSend.IsEnabled = BenchShare.IsChecked == true && benchLast != null; }

        /// <summary>"See exactly what is sent": the request's body itself, under the switch (shown / hidden)</summary>
        void BenchWhat_Click(object sender, RoutedEventArgs e)
        {
            if (benchLast == null) return;
            if (BenchPayload.Visibility == Visibility.Visible) { BenchPayload.Visibility = Visibility.Collapsed; return; }
            BenchPayload.Text = "POST /rest/v1/rpc/perf_bench_add (the Arena server's public key only: no sign-in, no account)\n\n" +
                                System.Text.RegularExpressions.Regex.Unescape(Bench.PayloadJson(benchLast)).Replace(",\"", ", \"");
            BenchPayload.Visibility = Visibility.Visible;
        }

        void BenchSend_Click(object sender, RoutedEventArgs e)
        {
            if (benchLast == null || BenchShare.IsChecked != true) return;
            var r = benchLast;
            BenchSend.IsEnabled = false;
            BenchSendNote.Visibility = Visibility.Visible; BenchSendNote.Foreground = Res("Muted"); BenchSendNote.Text = "Sending...";
            Task.Run(() => Bench.Send(r)).ContinueWith(t =>
            {
                string err = t.Exception != null ? t.Exception.GetBaseException().Message : t.Result;
                BenchSendNote.Foreground = err == null ? Res("Good") : Res("Warn");
                BenchSendNote.Text = err ?? "Sent, anonymously. Thank you!";
                BenchShare.IsChecked = false;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
