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
    /// <summary>one row of the list: a match on this PC (R), on the Arena server (C), or both</summary>
    public sealed class ReplayRow
    {
        public ReplayState R; public CloudReplay C; public bool Uploaded;
        static ulong Hex(string h) => ulong.TryParse(h, NumberStyles.HexNumber, null, out ulong v) ? v : 0;
        DateTime WhenUtc => R != null ? R.When.ToUniversalTime() : C.PlayedAt;
        public DateTime Sort => WhenUtc;
        public string WhenText => WhenUtc.ToLocalTime().ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture);
        public string Kind
        {
            get
            {
                if (R == null) return "Online";
                if (R.LoopTest) return "Rollback test (one PC)";
                return R.Kind == 1 ? "Matchmaking" : R.Kind == 2 ? "Online LDN" : R.Kind == 3 ? "Online" : R.Rollback ? "Online (rollback)" : "Local match";
            }
        }
        RvFighter F(int i) => R != null && i < R.Fighters.Count ? R.Fighters[i] : null;
        public string P1Fighter => R != null ? F(0)?.FighterName ?? "-" : ReplayNames.FighterName(Hex(C.P1Chara), -1);
        public string P2Fighter => R != null ? F(1)?.FighterName ?? "-" : ReplayNames.FighterName(Hex(C.P2Chara), -1);
        public string P1Name => C != null ? C.P1Name : F(0)?.Name ?? "";
        public string P2Name => C != null ? C.P2Name : F(1)?.Name ?? "";
        public string Stage => R != null ? R.StageName : ReplayNames.StageName(Hex(C.Stage));
        public string Length { get { int fr = R != null ? R.Frames : C.Frames, s = fr / 60; return (s / 60) + ":" + (s % 60).ToString("00"); } }
        public string Result
        {
            get
            {
                if (R != null && R.Winner >= 0 && R.Winner < R.Fighters.Count) return R.Fighters[R.Winner].Name + " won (" + R.Fighters[R.Winner].FighterName + ")";
                if (R == null) return C.Winner == 0 ? C.P1Name + " won" : C.Winner == 1 ? C.P2Name + " won" : "";
                return R.Ended ? "Played to the end" : "Ended early";
            }
        }
        /// <summary>where the replay is: this PC, the server (private / public)</summary>
        public string Where
        {
            get
            {
                if (C != null) return (C.IsPublic ? "Public" : "Private: you and your opponent") + (C.HasState ? "" : " · inputs only");
                if (R != null && R.Kind != 0) return Uploaded ? "Uploaded" : "This PC (not uploaded yet)";
                return "This PC";
            }
        }
        public bool CanWatch => R != null || (C != null && C.HasState);
        // (0.2.11) visibility, upload and delete
        bool OnlineMatch => R != null && R.Kind != 0 && R.MatchId != 0 && !R.LoopTest;
        public bool IsPublic => C != null && C.IsPublic;
        /// <summary>a player of the match (a server row), or a local online match not uploaded yet (made public = uploaded first)</summary>
        public bool CanSetVis => (C != null && C.Mine) || (C == null && OnlineMatch && Account.SignedIn);
        public Visibility VisVisible => CanSetVis || IsPublic ? Visibility.Visible : Visibility.Collapsed;
        public string VisText => IsPublic ? "Public" : "Private";
        public Geometry VisIcon => (Geometry)Application.Current.Resources[IsPublic ? "IcoGlobe" : "IcoLock"];
        public Brush VisBrush => (Brush)Application.Current.Resources[IsPublic ? "Accent" : "Muted"];
        public string VisTip => !CanSetVis ? "Public: every signed-in player can watch it."
            : IsPublic ? "Public: every signed-in player can find it by name. Click to make it private."
            : C == null ? "Only on this PC. Click to upload it and make it public."
            : "Only you and your opponent. Click to make it public.";
        public Visibility UploadVisible => C == null && OnlineMatch && !Uploaded && Account.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DeleteVisible => R != null ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>what the search box looks in (lower case)</summary>
        public string Haystack => string.Join(" ", P1Name, P2Name, P1Fighter, P2Fighter, Stage, Kind).ToLowerInvariant();
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

        List<ReplayRow> replayRows = new List<ReplayRow>();
        string cloudError;

        /// <summary>this PC's recordings and, signed in, the server's replays this player may watch (one row per match)</summary>
        void LoadReplays()
        {
            ReplaysStatus.Text = "Reading the replays...";
            bool signed = Account.SignedIn;
            Task.Run(() =>
            {
                ReplayLibrary.Cap();
                if (signed) UploadPending();                // (0.2.11) the ones left behind, before the list is read
                var local = ReplayLibrary.List();
                var up = ReplayCloud.Uploaded();
                List<CloudReplay> cloud = null; string err = null;
                if (signed) { try { cloud = ReplayCloud.List(); } catch (Exception e) { err = e.Message; } }
                var rows = new List<ReplayRow>();
                foreach (var c in cloud ?? new List<CloudReplay>())
                {
                    // the server's row of a match recorded here: one row, played from this PC's file
                    var mine = local.FirstOrDefault(l => l.MatchId != 0 && l.MatchId == c.MatchId && Math.Abs((l.When.ToUniversalTime() - c.PlayedAt).TotalHours) < 6);
                    if (mine != null) local.Remove(mine);
                    rows.Add(new ReplayRow { C = c, R = mine, Uploaded = true });
                }
                foreach (var l in local) rows.Add(new ReplayRow { R = l, Uploaded = up.ContainsKey(Path.GetFileName(l.Path)) });
                return Tuple.Create(rows.OrderByDescending(r => r.Sort).ToList(), err);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted) { ReplaysStatus.Text = "Could not read the replays: " + t.Exception.GetBaseException().Message; return; }
                replayRows = t.Result.Item1; cloudError = t.Result.Item2;
                ShowReplayRows();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowReplayRows()
        {
            string f = RvFilterMine.IsChecked == true ? "mine" : RvFilterPublic.IsChecked == true ? "public" : RvFilterPc.IsChecked == true ? "pc" : "all";
            string q = (RvSearch.Text ?? "").Trim().ToLowerInvariant();
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var all = replayRows.Concat(searchRows.Where(s => !replayRows.Any(r => r.C != null && r.C.Id == s.C.Id)));
            var rows = all.Where(r => f == "all" || (f == "mine" && (r.C == null || r.C.Mine)) || (f == "public" && r.C != null && r.C.IsPublic) || (f == "pc" && r.R != null))
                          .Where(r => words.All(w => r.Haystack.Contains(w))).OrderByDescending(r => r.Sort).ToList();
            ReplaysList.ItemsSource = rows;
            ReplaysEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ReplaysEmptyTitle.Text = q.Length > 0 ? "No match for \u201c" + RvSearch.Text.Trim() + "\u201d" : replayRows.Count > 0 ? "Nothing here" : "No matches recorded yet";
            ReplaysEmptyText.Text = q.Length > 0 ? (Account.SignedIn ? "No replay of yours and no public replay has a player, fighter or stage with that name." : "Sign in to search the Arena server's public replays too.")
                                  : replayRows.Count > 0 ? "No replay fits this filter." : "Play a match: the game records it and it shows up here.";
            int server = replayRows.Count(r => r.C != null);
            ReplaysStatus.Text = rows.Count + " match" + (rows.Count == 1 ? "" : "es") + " shown; " + replayRows.Count(r => r.R != null) + " on this PC (the last " + ReplayLibrary.Keep + " are kept), "
                + (Account.SignedIn ? server + " on the Arena server" : "sign in to see the Arena server's replays") + (searching ? "; searching the server..." : searchError != null && RvSearch.Text.Trim().Length >= 2 ? "; the server's search failed: " + searchError : "")
                + (cloudError != null ? " (server: " + cloudError + ")" : "") + ".";
        }

        void ReplaysFilter_Checked(object sender, RoutedEventArgs e) { if (ReplaysList != null && replayRows != null) ShowReplayRows(); }

        // ------------------------------------------------------------ (0.2.11) search: this list at once, the server's replays of that player after a pause in typing
        List<ReplayRow> searchRows = new List<ReplayRow>();
        System.Windows.Threading.DispatcherTimer rvSearchTimer;
        bool searching; int searchSeq; string searchError;
        void ReplaysSearch_Changed(object sender, TextChangedEventArgs e)
        {
            bool empty = string.IsNullOrEmpty(RvSearch.Text);
            RvSearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            RvSearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (ReplaysList == null || replayRows == null) return;
            if (rvSearchTimer == null)
            {
                rvSearchTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
                rvSearchTimer.Tick += (s2, e2) => { rvSearchTimer.Stop(); SearchServer(); };
            }
            rvSearchTimer.Stop();
            string q = RvSearch.Text.Trim();
            if (q.Length < 2 || !Account.SignedIn) { searchSeq++; searchRows = new List<ReplayRow>(); searching = false; }
            else rvSearchTimer.Start();
            ShowReplayRows();
        }
        void ReplaysSearchClear_Click(object sender, RoutedEventArgs e) { RvSearch.Text = ""; RvSearch.Focus(); }
        void SearchServer()
        {
            string q = RvSearch.Text.Trim();
            if (q.Length < 2 || !Account.SignedIn) return;
            int seq = ++searchSeq; searching = true; ShowReplayRows();
            Task.Run(() => ReplayCloud.List(100, q)).ContinueWith(t =>
            {
                if (seq != searchSeq) return;               // (a newer search is on its way)
                searching = false;
                searchError = t.IsFaulted ? t.Exception.GetBaseException().Message : null;
                searchRows = t.IsFaulted ? new List<ReplayRow>() : t.Result.Select(c => new ReplayRow { C = c, Uploaded = true }).ToList();
                ShowReplayRows();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ReplayPublic_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;                               // (inside the row's own button: not a Watch click)
            var row = (sender as Button)?.Tag as ReplayRow;
            if (row == null || !row.CanSetVis) return;
            bool pub = !row.IsPublic;
            ReplaysStatus.Text = row.C == null ? "Uploading the replay..." : pub ? "Making it public..." : "Making it private...";
            Task.Run(() =>
            {
                string id = row.C?.Id ?? ReplayCloud.Upload(row.R);      // (a local match: on the server first)
                ReplayCloud.SetPublic(id, pub);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted) { ReplaysStatus.Text = ""; Show("Replay", "Could not change it: " + t.Exception.GetBaseException().Message, null, ("OK", null, true)); return; }
                searchRows.Clear(); LoadReplays();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ReplayUpload_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var row = (sender as Button)?.Tag as ReplayRow;
            if (row?.R == null) return;
            ReplaysStatus.Text = "Uploading the replay...";
            bool pub = Core.RecPublic;
            Task.Run(() => { string id = ReplayCloud.Upload(row.R); if (pub) ReplayCloud.SetPublic(id, true); }).ContinueWith(t =>
            {
                if (t.IsFaulted) { ReplaysStatus.Text = ""; Show("Replay", "Could not upload it: " + t.Exception.GetBaseException().Message, null, ("OK", null, true)); return; }
                LoadReplays();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ReplayDelete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var row = (sender as Button)?.Tag as ReplayRow;
            if (row?.R == null) return;
            string note = row.C != null || row.Uploaded ? "\n\nThe copy on the Arena server stays: you and your opponent can still watch it." : "";
            Show("Delete replay", row.P1Fighter + " vs " + row.P2Fighter + ", " + row.WhenText + ": this PC's file is deleted." + note, null,
                 ("Delete", () =>
                 {
                     try { File.Delete(row.R.Path); } catch (Exception ex) { Show("Replay", "Could not delete it: " + ex.Message, null, ("OK", null, true)); return; }
                     LoadReplays();
                 }, true), ("Cancel", null, false));
        }

        /// <summary>(GameExited) the cap on this PC's files, then the new online matches to the server (when chosen and signed in)</summary>
        void ReplaysAfterGame()
        {
            Task.Run(() => { ReplayLibrary.Cap(); UploadPending(); })
                .ContinueWith(t => { if (PageReplays.Visibility == Visibility.Visible && ReplayViewer.Visibility != Visibility.Visible) LoadReplays(); },
                              TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>(0.2.11) the online matches of the last 30 days not on the server yet (played offline, signed out, a failed try): sent
        /// after each game and when the Replays page opens, when uploads are on; one try per file per launcher run</summary>
        static readonly HashSet<string> uploadTried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static int UploadPending()
        {
            if (!Core.RecUpload || !Account.SignedIn) return 0;
            var up = ReplayCloud.Uploaded(); int n = 0;
            foreach (var l in ReplayLibrary.List())
            {
                string name = Path.GetFileName(l.Path);
                if (l.Kind == 0 || l.MatchId == 0 || l.LoopTest || up.ContainsKey(name) || (DateTime.Now - l.When).TotalDays > 30) continue;
                lock (uploadTried) if (!uploadTried.Add(name)) continue;
                try { string id = ReplayCloud.Upload(l); if (Core.RecPublic) ReplayCloud.SetPublic(id, true); n++; } catch { }
            }
            return n;
        }

        void ReplaysRefresh_Click(object sender, RoutedEventArgs e) { LoadReplays(); }
        void ReplaysFolder_Click(object sender, RoutedEventArgs e) { Directory.CreateDirectory(ReplayLibrary.Folder); Core.Open(ReplayLibrary.Folder); }

        void ReplayOpen_Click(object sender, RoutedEventArgs e)
        {
            var row = (sender as Button)?.Tag as ReplayRow;
            if (row == null) return;
            if (row.R != null) { OpenReplay(row.R.Path); return; }
            if (!row.CanWatch) { Show("Replay", "The server has only the input recording of this match: it will be watchable in the game itself later.", null, ("OK", null, true)); return; }
            ReplaysStatus.Text = "Downloading the replay...";
            Task.Run(() => ReplayCloud.Download(row.C)).ContinueWith(t =>
            {
                if (t.IsFaulted) { ReplaysStatus.Text = ""; Show("Replay", "Could not download it: " + t.Exception.GetBaseException().Message, null, ("OK", null, true)); return; }
                OpenReplay(t.Result);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>a match file in the viewer (also: --replay &lt;file&gt; [--frame n] on the command line, for screenshots)</summary>
        ReplayScene rvScene;
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
                if (rvScene == null) { rvScene = new ReplayScene(RvScene); RvView.Scene = rvScene; }
                rvScene.Load(r, t2 => { RvSceneNote.Text = t2; RvView.InvalidateVisual(); });
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
            if (rvScene != null) rvScene.On = RvModels.IsChecked == true;
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
