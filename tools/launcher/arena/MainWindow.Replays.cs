// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: the Replays page. The list of this PC's recorded matches (<Saves>\replays\*.ssbv, ReplayState.cs)
// and the schematic viewer (ReplayView.cs): play / pause, speeds 0.25x..4x, frame steps, the timeline, the stats, and the
// move data of Ultimate Frame Data (Assets/ufd_moves.json, tools/replay/ufd_moves.py) next to the moves being made.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Arena
{
    /// <summary>one row of the list</summary>
    public sealed class ReplayRow
    {
        public ReplayState R;
        public string WhenText => R.When.ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture);
        public string Kind => R.LoopTest ? "Rollback test (one PC)" : R.Rollback ? "Online (rollback)" : "Local match";
        RvFighter F(int i) => i < R.Fighters.Count ? R.Fighters[i] : null;
        public string P1Fighter => F(0)?.FighterName ?? "-";
        public string P2Fighter => F(1)?.FighterName ?? "-";
        public string P1Name => F(0)?.Name ?? "";
        public string P2Name => F(1)?.Name ?? "";
        public string Stage => R.StageName;
        public string Length => R.Length;
        public string Result
        {
            get
            {
                if (R.Winner >= 0 && R.Winner < R.Fighters.Count) return R.Fighters[R.Winner].Name + " won (" + R.Fighters[R.Winner].FighterName + ")";
                return R.Ended ? "Played to the end" : "Ended early";
            }
        }
    }

    public partial class MainWindow
    {
        ReplayStats rvStats;
        bool rvPlaying, rvHooked;
        double rvSpeed = 1, rvLastT;
        readonly Stopwatch rvClock = new Stopwatch();
        static Dictionary<string, object> ufd;

        void ReplaysShown()
        {
            if (ReplayViewer.Visibility == Visibility.Visible) return;
            LoadReplays();
        }

        void LoadReplays()
        {
            ReplaysStatus.Text = "Reading " + ReplayLibrary.Folder + " ...";
            Task.Run(() => ReplayLibrary.List()).ContinueWith(t =>
            {
                if (t.IsFaulted) { ReplaysStatus.Text = "Could not read the replays: " + t.Exception.GetBaseException().Message; return; }
                var rows = t.Result.Select(r => new ReplayRow { R = r }).ToList();
                ReplaysList.ItemsSource = rows;
                ReplaysEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ReplaysStatus.Text = rows.Count + " match" + (rows.Count == 1 ? "" : "es") + " in " + ReplayLibrary.Folder;
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ReplaysRefresh_Click(object sender, RoutedEventArgs e) { LoadReplays(); }
        void ReplaysFolder_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(ReplayLibrary.Folder); Core.Open(ReplayLibrary.Folder); }

        void ReplayOpen_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.Tag as ReplayRow;
            if (row == null) return;
            OpenReplay(row.R.Path);
        }

        /// <summary>a match file in the viewer (also: --replay &lt;file&gt; [--frame n] on the command line, for screenshots)</summary>
        void OpenReplay(string path, int frame = 0, bool play = false)
        {
            ReplaysStatus.Text = "Opening " + Path.GetFileName(path) + " ...";
            Task.Run(() => { var r = ReplayState.Read(path); return Tuple.Create(r, ReplayStats.Of(r)); }).ContinueWith(t =>
            {
                if (t.IsFaulted) { ReplaysStatus.Text = ""; Show("Replay", "Could not open the replay: " + t.Exception.GetBaseException().Message, null, ("OK", null, true)); return; }
                var r = t.Result.Item1; rvStats = t.Result.Item2;
                if (r.FrameList.Count == 0) { ReplaysStatus.Text = "This replay has no frames."; return; }
                ReplaysListPanel.Visibility = Visibility.Collapsed; ReplayViewer.Visibility = Visibility.Visible;
                RvView.Replay = r;
                RvTimeline.Replay = r; RvTimeline.Stats = rvStats; RvTimeline.Prepare();
                if (!rvHooked)
                {
                    rvHooked = true;
                    RvTimeline.Seek += f => { RvSet(f); };
                    CompositionTarget.Rendering += RvTick;
                    PreviewKeyDown += RvKey;
                    foreach (var sp in new[] { 0.25, 0.5, 1, 2, 4 })
                    {
                        var b = new RadioButton { Content = sp.ToString(CultureInfo.InvariantCulture) + "x", Tag = sp, Style = (Style)FindResource("RvSpeed"), IsChecked = sp == 1, GroupName = "rvspeed" };
                        b.Checked += (s2, e2) => { rvSpeed = (double)((RadioButton)s2).Tag; };
                        RvSpeeds.Children.Add(b);
                    }
                }
                var a = r.Fighters.Count > 0 ? r.Fighters[0] : null; var b2 = r.Fighters.Count > 1 ? r.Fighters[1] : null;
                string t1 = a?.FighterName ?? "?", t2 = b2?.FighterName ?? "?";
                RvTitle.Text = t1.Length + t2.Length > 18 ? t1 + "\nvs " + t2 : t1 + " vs " + t2;      // long names: "vs" starts the second line
                RvSub.Text = r.StageName + "  ·  " + r.When.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + "  ·  " + r.Length + "  ·  " + r.FrameList.Count + " frames"
                             + (r.Rollback ? "  ·  final frames of the rollback session" : "");
                BuildStats(r);
                RvSet(frame);
                RvPause(!play);
                RvView.Focus();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void RvBack_Click(object sender, RoutedEventArgs e)
        {
            RvPause(true);
            ReplayViewer.Visibility = Visibility.Collapsed; ReplaysListPanel.Visibility = Visibility.Visible;
            LoadReplays();
        }

        // ------------------------------------------------------------ playback
        void RvSet(double f)
        {
            RvView.Frame = f;
            RvTimeline.Frame = RvView.Frame; RvTimeline.InvalidateVisual();
            RefreshMoveInfo();
        }

        void RvPause(bool pause)
        {
            rvPlaying = !pause;
            RvPlayIcon.Data = pause ? (Geometry)FindResource("IcoPlay") : Geometry.Parse("M6,4 H10 V20 H6 Z M14,4 H18 V20 H14 Z");
            rvClock.Restart(); rvLastT = 0;
        }

        void RvTick(object sender, EventArgs e)
        {
            if (!rvPlaying || RvView.Replay == null || ReplayViewer.Visibility != Visibility.Visible) return;
            double t = rvClock.Elapsed.TotalSeconds, dt = Math.Min(t - rvLastT, 0.1);
            rvLastT = t;
            double f = RvView.Frame + dt * 60.0 * rvSpeed;
            if (f >= RvView.Replay.FrameList.Count - 1) { f = RvView.Replay.FrameList.Count - 1; RvPause(true); }
            RvView.Frame = f;
            RvTimeline.Frame = f; RvTimeline.InvalidateVisual();
            if (((int)f & 7) == 0) RefreshMoveInfo();
        }

        void RvPlay_Click(object sender, RoutedEventArgs e)
        {
            if (!rvPlaying && RvView.Frame >= RvView.Replay.FrameList.Count - 1) RvSet(0);
            RvPause(rvPlaying);
        }
        void RvStepBack_Click(object sender, RoutedEventArgs e) { RvPause(true); RvSet(Math.Round(RvView.Frame) - 1); }
        void RvStepFwd_Click(object sender, RoutedEventArgs e) { RvPause(true); RvSet(Math.Round(RvView.Frame) + 1); }
        void RvToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (RvView == null || RvHit == null || RvHurt == null || RvFollow == null) return;
            RvView.ShowHit = RvHit.IsChecked == true; RvView.ShowHurt = RvHurt.IsChecked == true; RvView.Follow = RvFollow.IsChecked == true;
            RvView.InvalidateVisual();
        }

        void RvKey(object sender, KeyEventArgs e)
        {
            if (ReplayViewer.Visibility != Visibility.Visible || PageReplays.Visibility != Visibility.Visible) return;
            if (e.Key == Key.Space) { RvPlay_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Left) { RvPause(true); RvSet(Math.Round(RvView.Frame) - (Keyboard.Modifiers == ModifierKeys.Shift ? 60 : 1)); e.Handled = true; }
            else if (e.Key == Key.Right) { RvPause(true); RvSet(Math.Round(RvView.Frame) + (Keyboard.Modifiers == ModifierKeys.Shift ? 60 : 1)); e.Handled = true; }
            else if (e.Key == Key.Home) { RvSet(0); e.Handled = true; }
            else if (e.Key == Key.End) { RvSet(RvView.Replay.FrameList.Count - 1); e.Handled = true; }
        }

        // ------------------------------------------------------------ the side panel
        TextBlock Line(string text, double size = 14, Brush b = null, FontWeight? w = null, Thickness? m = null)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = b ?? (Brush)FindResource("Text"), FontWeight = w ?? FontWeights.Normal, Margin = m ?? new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        }

        TextBlock[] rvMoveInfo;

        void BuildStats(ReplayState r)
        {
            RvStats.Children.Clear();
            var muted = (Brush)FindResource("Muted");
            rvMoveInfo = new TextBlock[r.Fighters.Count];
            for (int k = 0; k < r.Fighters.Count; k++)
            {
                var f = r.Fighters[k]; var p = rvStats.P[k];
                var col = new SolidColorBrush(RvPalette.Player[k % 4]);
                var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, k == 0 ? 6 : 22, 0, 6) };
                head.Children.Add(new Border { Width = 4, Height = 22, Background = col, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 10, 0) });
                head.Children.Add(Line(f.FighterName.ToUpperInvariant() + "   " + f.Name, 15, null, FontWeights.SemiBold, new Thickness(0)));
                RvStats.Children.Add(head);
                RvStats.Children.Add(Line(string.Format(CultureInfo.InvariantCulture, "Damage dealt  {0:0.0}%   ·   taken  {1:0.0}%", p.DamageDealt, p.DamageTaken), 14, muted));
                var kos = rvStats.Kos.Where(x => x.Fighter == k).ToList();
                RvStats.Children.Add(Line("Stocks lost  " + p.StocksLost + (f.EndStocks > 0 ? "   ·   left at the end  " + f.EndStocks : ""), 14, muted));
                foreach (var ko in kos)
                {
                    var b = new Button { Style = (Style)FindResource("Link"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 2, 0, 0), Tag = ko };
                    int sec = ko.Frame / 60;
                    b.Content = new TextBlock { Text = string.Format(CultureInfo.InvariantCulture, "KO at {0}:{1:00}, off the {2}", sec / 60, sec % 60, ko.Where), FontSize = 13 };
                    b.Click += (s, e) => { var kk = (ReplayStats.Ko)((Button)s).Tag; int i = RvView.Replay.FrameList.FindIndex(x => x.Number >= kk.Frame); RvPause(true); RvSet(Math.Max(0, i - 90)); };
                    RvStats.Children.Add(b);
                }
                if (p.Moves.Count > 0)
                {
                    RvStats.Children.Add(Line("Most used moves", 13, (Brush)FindResource("Dim"), FontWeights.SemiBold, new Thickness(0, 8, 0, 2)));
                    foreach (var m in p.Moves) RvStats.Children.Add(Line(m.Value + "×  " + m.Key, 14, null, null, new Thickness(10, 1, 0, 0)));
                }
                rvMoveInfo[k] = Line("", 13, col, null, new Thickness(0, 8, 0, 0));
                RvStats.Children.Add(rvMoveInfo[k]);
            }
        }

        /// <summary>the move each fighter is making now, with Ultimate Frame Data's numbers for it</summary>
        void RefreshMoveInfo()
        {
            var r = RvView.Replay;
            if (r == null || rvMoveInfo == null) return;
            var fr = r.FrameList[Math.Max(0, Math.Min((int)Math.Round(RvView.Frame), r.FrameList.Count - 1))];
            for (int k = 0; k < rvMoveInfo.Length && k < fr.F.Length; k++)
            {
                var s = fr.F[k];
                if (s == null) { rvMoveInfo[k].Text = ""; continue; }
                string mv = ReplayNames.MoveName(s.Motion);
                string text = mv != null ? "Now: " + mv + "  (frame " + (int)Math.Floor(s.MotionFrame + 1) + " of the move)" : "Now: " + ReplayNames.ActionText(s);
                if (s.Hits.Length > 0) text += "\nHitboxes now: " + s.Hits.Length + ", radius " + string.Join(" / ", s.Hits.Select(h => h.R.ToString("0.0", CultureInfo.InvariantCulture)));
                var u = mv != null ? UfdMove(r.Fighters[k].FighterKey, mv) : null;
                if (u != null)
                {
                    string g(string key) => u.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "-";
                    text += "\nUFD " + g("name") + ": startup " + g("startup") + ", active " + g("active") + ", total " + g("total") + ", damage " + g("damage");
                }
                rvMoveInfo[k].Text = text;
            }
        }

        static Dictionary<string, object> UfdMove(string fighterKey, string move)
        {
            if (fighterKey == null) return null;
            if (ufd == null)
            {
                try
                {
                    var st = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/ufd_moves.json"));
                    using (var rd = new StreamReader(st.Stream)) ufd = new JavaScriptSerializer { MaxJsonLength = 1 << 24 }.Deserialize<Dictionary<string, object>>(rd.ReadToEnd());
                }
                catch { ufd = new Dictionary<string, object>(); }
            }
            if (!ufd.TryGetValue("fighters", out var fs) || !(fs is Dictionary<string, object> fd) || !fd.TryGetValue(fighterKey, out var fo)) return null;
            var moves = (fo as Dictionary<string, object>)?["moves"] as System.Collections.ArrayList;
            if (moves == null) return null;
            Dictionary<string, object> best = null;
            foreach (Dictionary<string, object> m in moves)
            {
                string n = Convert.ToString(m["name"]);
                if (string.Equals(n, move, StringComparison.OrdinalIgnoreCase)) return m;
                if (best == null && n.StartsWith(move + " ", StringComparison.OrdinalIgnoreCase)) best = m;      // "Neutral B (Fireball)"
            }
            return best;
        }

        /// <summary>--replay &lt;file&gt; [--frame n] [--play]: opens the viewer at once (screenshots, tests)</summary>
        void OpenReplayFromArgs()
        {
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--replay");
            if (i < 0 || i + 1 >= args.Length) return;
            int j = Array.IndexOf(args, "--frame");
            int f = j > 0 && j + 1 < args.Length && int.TryParse(args[j + 1], out int ff) ? ff : 0;
            TabReplays.IsChecked = true;
            OpenReplay(args[i + 1], f, Array.IndexOf(args, "--play") > 0);
            int z = Array.IndexOf(args, "--zoom");          // --zoom <factor> [--zoomon <fighter>]
            if (z > 0 && z + 1 < args.Length && double.TryParse(args[z + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double zf)) RvView.Zoom = zf;
            int zo = Array.IndexOf(args, "--zoomon");
            if (zo > 0 && zo + 1 < args.Length && int.TryParse(args[zo + 1], out int zk)) RvView.zoomOn = zk;
        }
    }
}
