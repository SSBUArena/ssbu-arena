// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: the Replays page. The list of this PC's recorded matches (<Saves>\replays\*.ssbv, ReplayState.cs)
// and the schematic viewer (ReplayView.cs): play / pause, speeds 0.25x..4x, frame steps, the timeline, the stats, and the
// move data of Ultimate Frame Data (Assets/ufd_moves.json, tools/replay/ufd_moves.py) next to the moves being made.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Arena
{
    /// <summary>a day's heading in the list</summary>
    public sealed class ReplayDay { public string Title { get; set; } public string Count { get; set; } }

    /// <summary>one row of the list: a match recorded on this PC (0.2.39: replays live only on the players' PCs, nothing on the server)</summary>
    public sealed class ReplayRow : INotifyPropertyChanged
    {
        public ReplayState R;
        /// <summary>(0.2.41) its public copy (null: only on this PC); Busy: what the pill is doing now</summary>
        public PublicReplay Pub; public string Busy;
        public event PropertyChangedEventHandler PropertyChanged;
        public void Changed() { PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("")); }
        DateTime WhenUtc => R.When.ToUniversalTime();
        public DateTime Sort => WhenUtc;
        public DateTime Day => WhenUtc.ToLocalTime().Date;
        public string WhenText => WhenUtc.ToLocalTime().ToString("dd MMM yyyy  HH:mm", CultureInfo.InvariantCulture);
        public string TimeText => WhenUtc.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        public string Kind
        {
            get
            {
                if (R.LoopTest) return "Rollback test (one PC)";
                return R.Kind == 1 ? "Matchmaking" : R.Kind == 2 ? "Online room" : R.Kind == 3 ? "Online" : R.Rollback ? "Online (rollback)" : "Local match";
            }
        }
        RvFighter F(int i) => i < R.Fighters.Count ? R.Fighters[i] : null;
        public string P1Fighter => F(0)?.FighterName ?? "-";
        public string P2Fighter => F(1)?.FighterName ?? "-";
        public string P1Name => F(0)?.Name ?? "";
        public string P2Name => F(1)?.Name ?? "";
        public string Stage => R.StageName;
        public string Length { get { int s = R.Frames / 60; return (s / 60) + ":" + (s % 60).ToString("00"); } }
        // the fighters' portraits: from the player's own game files (Core.MakeAvatars); the initials under them when there is none
        public ImageSource P1Icon => MainWindow.FighterIcon(F(0)?.FighterKey);
        public ImageSource P2Icon => MainWindow.FighterIcon(F(1)?.FighterKey);
        static string Initials(string n) => string.Concat(n.Split(new[] { ' ', '.', '&' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
        public string P1Initials => Initials(P1Fighter);
        public string P2Initials => Initials(P2Fighter);
        /// <summary>the winner: 0, 1, -1 not known</summary>
        int Winner => R.Winner >= 0 ? R.Winner : -1;
        /// <summary>which of the two is this player: 0, 1, -1 none of them</summary>
        int MeIndex
        {
            get
            {
                if (R.LoopTest) return -1;
                if (R.Kind != 0) return R.LocalPlayer < 2 ? R.LocalPlayer : -1;
                return R.Fighters.Count(f => f.Type == 0) == 1 ? R.Fighters.FindIndex(f => f.Type == 0) : -1;      // (local: one human, the rest CPUs)
            }
        }
        /// <summary>true: this player won, false: lost, null: not a match of this player or no result</summary>
        public bool? MeWon => MeIndex >= 0 && (Winner == 0 || Winner == 1) ? Winner == MeIndex : (bool?)null;
        static Brush Res(string k) => (Brush)Application.Current.Resources[k];
        public Brush P1Brush => Winner == 1 ? Res("Muted") : Res("Text");
        public Brush P2Brush => Winner == 0 ? Res("Muted") : Res("Text");
        public string ChipText => MeWon == true ? "WIN" : MeWon == false ? "LOSS" : "";
        public Visibility ChipVisible => MeWon != null ? Visibility.Visible : Visibility.Collapsed;
        public Brush ChipBrush => new SolidColorBrush(MeWon == true ? Color.FromRgb(0x3D, 0xDC, 0x97) : Color.FromRgb(0xFF, 0x6B, 0x75));
        public Brush ChipBack => new SolidColorBrush(MeWon == true ? Color.FromArgb(0x26, 0x3D, 0xDC, 0x97) : Color.FromArgb(0x26, 0xFF, 0x6B, 0x75));
        /// <summary>under the chip: the stocks the winner kept; not a match of this player: who won</summary>
        public string ResultText
        {
            get
            {
                int w = Winner;
                if (MeWon != null) { int st = w < R.Fighters.Count ? R.Fighters[w].EndStocks : 0; return st > 0 ? "by " + st + (st == 1 ? " stock" : " stocks") : ""; }
                if (w == 0 || w == 1) { string n = w == 0 ? P1Name : P2Name; return (string.IsNullOrEmpty(n) ? (w == 0 ? P1Fighter : P2Fighter) : n) + " won"; }
                return R.Ended ? "No winner" : "Ended early";
            }
        }
        public string RowTip => WhenText + "  ·  " + (Pub != null ? "public until " + Pub.Expires.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture) : "on this PC");
        // (0.2.41) the pill: make public / public (click: copy the link or make it private)
        public bool CanShare => Busy == null && !R.LoopTest && Account.SignedIn;
        public string VisText => Busy ?? (Pub != null ? "Public · " + Math.Max(1, (int)Math.Ceiling((Pub.Expires - DateTime.UtcNow).TotalDays)) + "d" : "Make public");
        public Brush VisBrush => Res(Pub != null ? "Accent" : "Muted");
        public Brush VisBorder => CanShare ? Res(Pub != null ? "Accent" : "Muted") : Brushes.Transparent;
        public Visibility VisVisible => Account.SignedIn && !R.LoopTest ? Visibility.Visible : Visibility.Collapsed;
        public string VisTip => Pub != null ? "Public for " + ReplayPublish.Days + " days: anyone with the link can watch it on ssbuarena.com (your opponent is shown as \"Opponent\"). Click to copy the link or make it private."
            : "Only on this PC. Click to make it public for " + ReplayPublish.Days + " days: it is uploaded and anyone with the link can watch it. Your opponent is shown as \"Opponent\". At most " + ReplayPublish.Keep + " public: a new one makes your oldest private.";
        public string DeleteTip => "Delete it from this PC";
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
        bool rvLoading; int rvLoadSeq;

        static readonly Dictionary<string, ImageSource> fighterIcons = new Dictionary<string, ImageSource>();
        /// <summary>a fighter's portrait for the list (null: none)</summary>
        public static ImageSource FighterIcon(string key)
        {
            if (key == null) return null;
            if (fighterIcons.TryGetValue(key, out var im)) return im;
            return fighterIcons[key] = AvatarImage(key, 76);
        }

        /// <summary>this PC's recordings (0.2.39: the only replays: nothing goes to or comes from the Arena server)</summary>
        void LoadReplays()
        {
            int seq = ++rvLoadSeq;
            rvLoading = true;
            ShowReplayRows();
            Task.Run(() =>
            {
                ReplayLibrary.Cap();
                Dictionary<string, PublicReplay> pub;
                try { pub = ReplayPublish.Refresh(); } catch { pub = ReplayPublish.Load(); }
                return ReplayLibrary.List().Select(l => new ReplayRow { R = l, Pub = pub.TryGetValue(Path.GetFileName(l.Path), out var p) ? p : null }).OrderByDescending(r => r.Sort).ToList();
            }).ContinueWith(t =>
            {
                if (seq != rvLoadSeq) return;               // (a newer reading is on its way)
                rvLoading = false;
                replayRows = t.IsFaulted ? new List<ReplayRow>() : t.Result;
                ShowReplayRows();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        bool rvFilling;
        /// <summary>a filter's choices: "all" and what the rows have; the choice stays while it is still there</summary>
        void FillCombo(ComboBox box, string all, IEnumerable<string> values)
        {
            string was = box.SelectedItem as string;
            var items = new List<string> { all };
            items.AddRange(values.Where(v => !string.IsNullOrEmpty(v) && v != "-").Distinct().OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase));
            if (box.ItemsSource is List<string> cur && cur.SequenceEqual(items)) return;
            box.ItemsSource = items;
            box.SelectedItem = was != null && items.Contains(was) ? was : all;
        }

        const string AllFighters = "All fighters", AllStages = "All stages", AnyResult = "Any result", Wins = "Wins", Losses = "Losses";

        void ShowReplayRows()
        {
            var all = replayRows;
            rvFilling = true;
            FillCombo(RvFighterBox, AllFighters, all.SelectMany(r => new[] { r.P1Fighter, r.P2Fighter }));
            FillCombo(RvStageBox, AllStages, all.Select(r => r.Stage));
            if (RvResultBox.ItemsSource == null) { RvResultBox.ItemsSource = new List<string> { AnyResult, Wins, Losses }; RvResultBox.SelectedIndex = 0; }
            rvFilling = false;
            string f = RvFighterBox.SelectedItem as string, st = RvStageBox.SelectedItem as string, res = RvResultBox.SelectedItem as string;
            string q = (RvSearch.Text ?? "").Trim().ToLowerInvariant();
            var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var rows = all.Where(r => words.All(w => r.Haystack.Contains(w)))
                          .Where(r => f == AllFighters || f == null || r.P1Fighter == f || r.P2Fighter == f)
                          .Where(r => st == AllStages || st == null || r.Stage == st)
                          .Where(r => res == AnyResult || res == null || (res == Wins && r.MeWon == true) || (res == Losses && r.MeWon == false))
                          .OrderByDescending(r => r.Sort).ToList();
            // the rows under the heading of their day
            var items = new List<object>();
            DateTime today = DateTime.Now.Date;
            foreach (var g in rows.GroupBy(r => r.Day))
            {
                string name = g.Key == today ? "TODAY" : g.Key == today.AddDays(-1) ? "YESTERDAY" : g.Key.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant();
                int n = g.Count();
                items.Add(new ReplayDay { Title = name, Count = n + (n == 1 ? " match" : " matches") });
                items.AddRange(g);
            }
            var sv = ReplaysList.Template?.FindName("scroll", ReplaysList) as ScrollViewer;
            double at = sv?.VerticalOffset ?? 0;
            ReplaysList.ItemsSource = items;
            if (sv != null && at > 0) { var k = at; Dispatcher.BeginInvoke(new Action(() => sv.ScrollToVerticalOffset(k)), System.Windows.Threading.DispatcherPriority.Loaded); }
            bool narrowed = words.Length > 0 || (f != null && f != AllFighters) || (st != null && st != AllStages) || (res != null && res != AnyResult);
            ReplaysClear.Visibility = narrowed ? Visibility.Visible : Visibility.Collapsed;
            // the states: reading (the rows' shapes, only when nothing is there yet), empty
            bool first = rvLoading && replayRows.Count == 0;
            ReplaysLoading.Visibility = first ? Visibility.Visible : Visibility.Collapsed;
            ReplaysEmpty.Visibility = rows.Count == 0 && !first ? Visibility.Visible : Visibility.Collapsed;
            rvEmptyAction = null;
            if (rows.Count == 0 && q.Length > 0)
            {
                ReplaysEmptyTitle.Text = "No match for “" + RvSearch.Text.Trim() + "”";
                ReplaysEmptyText.Text = "No replay on this PC has a player, fighter or stage with that name.";
                rvEmptyAction = "Clear filters";
            }
            else if (rows.Count == 0 && replayRows.Count > 0)
            {
                ReplaysEmptyTitle.Text = "Nothing here";
                ReplaysEmptyText.Text = "No replay fits these filters.";
                rvEmptyAction = "Clear filters";
            }
            else if (rows.Count == 0)
            {
                ReplaysEmptyTitle.Text = "No matches recorded yet";
                ReplaysEmptyText.Text = Core.RecMatchmaking || Core.RecLdn || Core.RecArena || Core.RecLocal ? "Play a match: the game records it on this PC and it shows up here." : "The game records nothing now: turn the recording on in Settings, under Replays.";
                rvEmptyAction = "Refresh";
            }
            ReplaysEmptyButton.Visibility = rvEmptyAction != null ? Visibility.Visible : Visibility.Collapsed;
            ReplaysEmptyButton.Content = rvEmptyAction;
            int pc = replayRows.Count;
            ReplaysStatus.Text = rvLoading && first ? "Reading the replays..."
                : (rows.Count == pc ? rows.Count + (rows.Count == 1 ? " match" : " matches") : rows.Count + " of " + pc + " matches") + " on this PC" + (pc >= ReplayLibrary.Keep ? " (the newest " + ReplayLibrary.Keep + " are kept)" : "")
                  + (rvLoading ? "  ·  updating..." : "");
        }

        void ReplaysCombo_Changed(object sender, SelectionChangedEventArgs e) { if (!rvFilling && ReplaysStatus != null && replayRows != null && IsLoaded) ShowReplayRows(); }

        void ReplaysClear_Click(object sender, RoutedEventArgs e)
        {
            rvFilling = true;
            if (RvFighterBox.Items.Count > 0) RvFighterBox.SelectedIndex = 0;
            if (RvStageBox.Items.Count > 0) RvStageBox.SelectedIndex = 0;
            if (RvResultBox.Items.Count > 0) RvResultBox.SelectedIndex = 0;
            rvFilling = false;
            RvSearch.Text = "";                             // (shows the rows)
            ShowReplayRows();
        }

        string rvEmptyAction;
        void ReplaysEmpty_Click(object sender, RoutedEventArgs e) { if (rvEmptyAction == "Refresh") LoadReplays(); else ReplaysClear_Click(null, null); }

        // ------------------------------------------------------------ search: this PC's list
        void ReplaysSearch_Changed(object sender, TextChangedEventArgs e)
        {
            bool empty = string.IsNullOrEmpty(RvSearch.Text);
            RvSearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            RvSearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (ReplaysList == null || replayRows == null) return;
            ShowReplayRows();
        }
        void ReplaysSearchClear_Click(object sender, RoutedEventArgs e) { RvSearch.Text = ""; RvSearch.Focus(); }

        /// <summary>(0.2.41) the pill: private -> ask, then upload; public -> copy the link or make it private</summary>
        void ReplayPublic_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;                               // (inside the row's own button: not a Watch click)
            var row = (sender as Button)?.Tag as ReplayRow;
            if (row == null || !row.CanShare) return;
            string file = Path.GetFileName(row.R.Path);
            if (row.Pub == null)
            {
                Show("Make this replay public?", row.P1Fighter + " vs " + row.P2Fighter + ", " + row.WhenText + ".\n\nIt is uploaded to the Arena server and anyone with its link can watch it on ssbuarena.com for " + ReplayPublish.Days +
                     " days. Your opponent is shown as \"Opponent\", you with your account name. You can make it private again at any time. At most " + ReplayPublish.Keep + " of your replays are public: a new one makes your oldest private.", null,
                     ("Make public", () =>
                     {
                         row.Busy = "Uploading..."; row.Changed();
                         Task.Run(() => ReplayPublish.Publish(row.R.Path)).ContinueWith(t =>
                         {
                             row.Busy = null;
                             if (t.IsFaulted) { row.Changed(); Show("Replay", "Could not make it public: " + t.Exception.GetBaseException().Message, null, ("OK", null, true)); return; }
                             row.Pub = t.Result; row.Changed();
                             try { Clipboard.SetText(ReplayPublish.Link(t.Result.Id)); } catch { }
                             Show("Replay is public", "The link is copied: paste it to share the replay.\n\n" + ReplayPublish.Link(t.Result.Id), null, ("OK", null, true));
                             LoadReplays();                  // (an older one may have gone back to private)
                         }, TaskScheduler.FromCurrentSynchronizationContext());
                     }, true), ("Cancel", null, false));
                return;
            }
            string link = ReplayPublish.Link(row.Pub.Id);
            Show("Public replay", link + "\n\nPublic until " + row.Pub.Expires.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.InvariantCulture) + ".", null,
                 ("Copy link", () => { try { Clipboard.SetText(link); } catch { } }, true),
                 ("Make private", () =>
                 {
                     row.Busy = "Removing..."; row.Changed();
                     Task.Run(() => ReplayPublish.Withdraw(file, row.Pub.Id)).ContinueWith(t =>
                     {
                         row.Busy = null;
                         if (t.IsFaulted) { row.Changed(); Show("Replay", "Could not make it private: " + t.Exception.GetBaseException().Message, null, ("OK", null, true)); return; }
                         row.Pub = null; row.Changed();
                     }, TaskScheduler.FromCurrentSynchronizationContext());
                 }, false), ("Close", null, false));
        }

        void ReplayDelete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var row = (sender as Button)?.Tag as ReplayRow;
            if (row == null) return;
            Show("Delete replay", row.P1Fighter + " vs " + row.P2Fighter + ", " + row.WhenText + ": this PC's file is deleted.\n\nIt is on this PC only: it cannot be brought back.", null,
                 ("Delete", () =>
                 {
                     try { File.Delete(row.R.Path); } catch (Exception ex) { Show("Replay", "Could not delete it: " + ex.Message, null, ("OK", null, true)); return; }
                     replayRows.Remove(row);
                     ShowReplayRows();
                 }, true), ("Cancel", null, false));
        }

        /// <summary>(GameExited) the cap on this PC's files</summary>
        void ReplaysAfterGame()
        {
            Task.Run(() => { ReplayLibrary.Cap(); ReplayCompact.CompactFolder(ReplayLibrary.Folder); })      // (0.2.41) the new files re-coded small (version 5)
                .ContinueWith(t => { if (PageReplays.Visibility == Visibility.Visible && ReplayViewer.Visibility != Visibility.Visible) LoadReplays(); },
                              TaskScheduler.FromCurrentSynchronizationContext());
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
        ReplayScene rvScene;
        void OpenReplay(string path, int frame = 0, bool play = false)
        {
            ReplaysStatus.Text = "Opening " + Path.GetFileName(path) + " ...";
            Task.Run(() => { var r = ReplayState.Read(path); return Tuple.Create(r, ReplayStats.Of(r)); }).ContinueWith(t =>
            {
                if (t.IsFaulted) { ShowReplayRows(); Show("Replay", "Could not open the replay: " + t.Exception.GetBaseException().Message, null, ("OK", null, true)); return; }
                var r = t.Result.Item1; rvStats = t.Result.Item2;
                if (r.FrameList.Count == 0) { ShowReplayRows(); Show("Replay", "This replay has no frames.", null, ("OK", null, true)); return; }
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
                        var b = new RadioButton { Content = sp.ToString(CultureInfo.InvariantCulture) + "×", Tag = sp, Style = (Style)FindResource("RvSeg"), IsChecked = sp == 1, GroupName = "rvspeed" };
                        b.Checked += (s2, e2) => { rvSpeed = (double)((RadioButton)s2).Tag; };
                        RvSpeeds.Children.Add(b);
                    }
                }
                var a = r.Fighters.Count > 0 ? r.Fighters[0] : null; var b2 = r.Fighters.Count > 1 ? r.Fighters[1] : null;
                string t1 = a?.FighterName ?? "?", t2 = b2?.FighterName ?? "?";
                RvTitle.Text = t1 + " vs " + t2;
                RvSub.Text = r.StageName + "  ·  " + r.When.ToString("dd MMM yyyy HH:mm", CultureInfo.InvariantCulture) + "  ·  " + r.Length;
                RvSub.ToolTip = r.FrameList.Count + " frames" + (r.Rollback ? ", the final frames of the rollback session" : "");
                BuildStats(r);
                RvTotal.Text = RvClock(r.FrameList.Count - 1);
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
            RvNow.Text = RvClock(RvView.Frame); RvFrameText();
            RefreshMoveInfo();
        }

        /// <summary>the game's own number of the frame shown (beside the playback buttons)</summary>
        void RvFrameText()
        {
            var r = RvView.Replay; if (r == null || r.FrameList.Count == 0) return;
            RvFrame.Text = r.FrameList[Math.Max(0, Math.Min((int)Math.Round(RvView.Frame), r.FrameList.Count - 1))].Number.ToString(CultureInfo.InvariantCulture);
        }

        static string RvClock(double frame) { int s = (int)(frame / 60); return (s / 60) + ":" + (s % 60).ToString("00"); }

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
            if (((int)f & 7) == 0) { RefreshMoveInfo(); RvNow.Text = RvClock(f); }
            if (((int)f & 1) == 0) RvFrameText();
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
            if (RvView == null || RvHit == null || RvHurt == null || RvGrab == null || RvFollow == null) return;
            if (rvScene != null) rvScene.On = RvModels.IsChecked == true;
            RvView.ShowHit = RvHit.IsChecked == true; RvView.ShowHurt = RvHurt.IsChecked == true; RvView.ShowGrab = RvGrab.IsChecked == true; RvView.Follow = RvFollow.IsChecked == true;
            RvView.InvalidateVisual();
        }

        void RvKey(object sender, KeyEventArgs e)
        {
            if (ReplayViewer.Visibility != Visibility.Visible || PageReplays.Visibility != Visibility.Visible) return;
            if (e.Key == Key.Space) { RvPlay_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape) { RvBack_Click(null, null); e.Handled = true; }
            else if (e.Key == Key.Left) { RvPause(true); RvSet(Math.Round(RvView.Frame) - (Keyboard.Modifiers == ModifierKeys.Shift ? 60 : 1)); e.Handled = true; }
            else if (e.Key == Key.Right) { RvPause(true); RvSet(Math.Round(RvView.Frame) + (Keyboard.Modifiers == ModifierKeys.Shift ? 60 : 1)); e.Handled = true; }
            else if (e.Key == Key.Home) { RvSet(0); e.Handled = true; }
            else if (e.Key == Key.End) { RvSet(RvView.Replay.FrameList.Count - 1); e.Handled = true; }
            else if (e.Key == Key.M) { RvModels.IsChecked = RvModels.IsChecked != true; e.Handled = true; }
            else if (e.Key == Key.H) { RvHit.IsChecked = RvHit.IsChecked != true; e.Handled = true; }
            else if (e.Key == Key.U) { RvHurt.IsChecked = RvHurt.IsChecked != true; e.Handled = true; }
            else if (e.Key == Key.G) { RvGrab.IsChecked = RvGrab.IsChecked != true; e.Handled = true; }
            else if (e.Key == Key.F) { RvFollow.IsChecked = RvFollow.IsChecked != true; e.Handled = true; }
            else if (e.Key == Key.L) { RvLegendOn.IsChecked = RvLegendOn.IsChecked != true; e.Handled = true; }
            else if (e.Key == Key.I) { RvInfo.IsChecked = RvInfo.IsChecked != true; e.Handled = true; }
        }

        /// <summary>(0.2.34) the Info switch: the side panel shown or the stage wide (the back button then sits in the controls);
        /// the Legend switch: the legend over the stage</summary>
        void RvPanels_Changed(object sender, RoutedEventArgs e)
        {
            if (RvSide == null || RvLegend == null || RvInfo == null || RvLegendOn == null) return;
            bool info = RvInfo.IsChecked == true;
            RvSide.Visibility = info ? Visibility.Visible : Visibility.Collapsed;
            RvSideCol.Width = new GridLength(info ? 340 : 0); RvGapCol.Width = new GridLength(info ? 16 : 0);
            if (RvLegendOn.IsChecked == true && RvLegendItems.Children.Count == 0) BuildLegend();
            RvLegend.Visibility = RvLegendOn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }
        void RvLegendClose_Click(object sender, RoutedEventArgs e) { RvLegendOn.IsChecked = false; }

        /// <summary>the legend's rows: a small drawing in the viewer's own colours and what it means</summary>
        void BuildLegend()
        {
            Color hot = RvPalette.Hex("#FF8A1F"), grab = RvPalette.Hex("#C77DFF"), p1 = RvPalette.Player[0], p2 = RvPalette.Player[1];
            System.Windows.Shapes.Shape Circle(Color stroke, double fillA, double w = 2, DoubleCollection dash = null, Color? fill = null)
            {
                var c = new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Stroke = new SolidColorBrush(stroke), StrokeThickness = w, Fill = new SolidColorBrush(Color.FromArgb((byte)(fillA * 255), (fill ?? stroke).R, (fill ?? stroke).G, (fill ?? stroke).B)) };
                if (dash != null) c.StrokeDashArray = dash;
                return c;
            }
            System.Windows.Shapes.Shape Capsule(Color stroke, DoubleCollection dash = null, double w = 1.5)
            {
                var c = new System.Windows.Shapes.Rectangle { Width = 12, Height = 20, RadiusX = 6, RadiusY = 6, Stroke = new SolidColorBrush(stroke), StrokeThickness = w };
                if (dash != null) c.StrokeDashArray = dash;
                return c;
            }
            UIElement Pair(UIElement a, UIElement b) { var sp = new StackPanel { Orientation = Orientation.Horizontal }; sp.Children.Add(a); if (b is FrameworkElement fb) fb.Margin = new Thickness(3, 0, 0, 0); sp.Children.Add(b); return sp; }
            var facing = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,0 L9,6 L0,12 Z"), Fill = new SolidColorBrush(p1), VerticalAlignment = VerticalAlignment.Center };
            var tag = new Border { BorderBrush = new SolidColorBrush(p1), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0), Child = new TextBlock { Text = "P1", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(p1) } };
            var blast = new System.Windows.Shapes.Line { X1 = 0, Y1 = 9, X2 = 24, Y2 = 9, Stroke = new SolidColorBrush(RvPalette.Hex("#FF6B6B")), StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 3, 2.5 } };
            var curve = new System.Windows.Shapes.Path { Data = Geometry.Parse("M0,16 L6,14 L9,8 L15,7 L17,2 L24,1"), Stroke = new SolidColorBrush(p1), StrokeThickness = 1.6 };
            var rows = new List<Tuple<UIElement, string, string>>
            {
                Tuple.Create((UIElement)Circle(hot, .22, 2.4), "Hitbox", "Where an attack hits; big ones show their damage"),
                Tuple.Create((UIElement)Circle(grab, .22, 2.4), "Grab box", "Where a grab can catch"),
                Tuple.Create((UIElement)Circle(grab, .22, 2.4, new DoubleCollection { 1.6, 1 }), "Command grab", "A special that grabs (Bowser's Side B, Ridley's Side B...)"),
                Tuple.Create(Pair(Capsule(p1), Capsule(p2)), "Hurtbox", "Where the fighter can be hit, in the player's colour"),
                Tuple.Create((UIElement)Capsule(RvPalette.Hex("#8899AA"), new DoubleCollection { 2, 2 }), "Intangible", "Attacks pass through (dodges, ledge grab)"),
                Tuple.Create((UIElement)Capsule(RvPalette.Hex("#3DDC97"), null, 1.8), "Invincible", "Hit, but no damage and no knockback"),
                Tuple.Create((UIElement)Circle(p1, .22, 1.6), "Shield", "Smaller as the shield weakens"),
                Tuple.Create((UIElement)Circle(RvPalette.Hex("#FFD27A"), .22, 1.4), "Projectile, item", "With its name when there is no model"),
                Tuple.Create(Pair(tag, facing), "Player, facing", "The player's tag; the arrow: where they look"),
                Tuple.Create((UIElement)blast, "Blast zone", "Crossing it is a KO (Follow off: the whole stage)"),
                Tuple.Create((UIElement)curve, "Timeline", "Each player's damage; a mark in their colour: a KO"),
            };
            RvLegendItems.Children.Clear();
            foreach (var row in rows)
            {
                var g = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) }); g.ColumnDefinitions.Add(new ColumnDefinition());
                if (row.Item1 is FrameworkElement fe) { fe.HorizontalAlignment = HorizontalAlignment.Left; fe.VerticalAlignment = VerticalAlignment.Center; }
                g.Children.Add(row.Item1);
                var t = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                t.Inlines.Add(new System.Windows.Documents.Run(row.Item2) { FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Text") });
                t.Inlines.Add(new System.Windows.Documents.Run("  " + row.Item3) { Foreground = (Brush)FindResource("Muted") });
                Grid.SetColumn(t, 1); g.Children.Add(t);
                RvLegendItems.Children.Add(g);
            }
        }

        // ------------------------------------------------------------ the side panel
        TextBlock Line(string text, double size = 14, Brush b = null, FontWeight? w = null, Thickness? m = null)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = b ?? (Brush)FindResource("Text"), FontWeight = w ?? FontWeights.Normal, Margin = m ?? new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        }

        TextBlock[] rvMoveInfo;

        /// <summary>(0.2.34) one card per player: who (name, fighter, result), the move now, three numbers, the stocks lost
        /// (each a link to it), the most used moves. Spacing: 16 inside the card, 12 between blocks, 4 between rows</summary>
        void BuildStats(ReplayState r)
        {
            RvStats.Children.Clear();
            var text = (Brush)FindResource("Text"); var muted = (Brush)FindResource("Muted"); var dim = (Brush)FindResource("Dim");
            var cond = (FontFamily)FindResource("Condensed");
            rvMoveInfo = new TextBlock[r.Fighters.Count];
            int best = r.Fighters.Count > 0 ? r.Fighters.Max(x => x.EndStocks) : 0;
            bool oneWinner = best > 0 && r.Fighters.Count(x => x.EndStocks == best) == 1;
            TextBlock Label(string s, double top = 12) => new TextBlock { Text = s, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = dim, Margin = new Thickness(0, top, 0, 6) };
            for (int k = 0; k < r.Fighters.Count; k++)
            {
                var f = r.Fighters[k]; var p = rvStats.P[k];
                var colC = RvPalette.Player[k % 4]; var col = new SolidColorBrush(colC);
                if (k > 0) RvStats.Children.Add(new Border { Height = 1, Background = (Brush)FindResource("Line"), Margin = new Thickness(-20, 24, -20, 24) });
                var card = new StackPanel();
                RvStats.Children.Add(card);

                // who
                var head = new Grid();
                head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); head.ColumnDefinitions.Add(new ColumnDefinition()); head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                head.Children.Add(new Border { Width = 4, Background = col, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 2, 12, 2) });
                var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock { Text = f.Name, FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = text, TextTrimming = TextTrimming.CharacterEllipsis });
                names.Children.Add(new TextBlock { Text = f.FighterName.ToUpperInvariant(), FontSize = 13, FontFamily = cond, FontWeight = FontWeights.Bold, Foreground = muted, Margin = new Thickness(0, -1, 0, 0) });
                Grid.SetColumn(names, 1); head.Children.Add(names);
                if (oneWinner)
                {
                    bool won = f.EndStocks == best;
                    var pill = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 2, 9, 3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
                                            Background = won ? new SolidColorBrush(Color.FromArgb(0x26, 0x65, 0xC7, 0xFF)) : Brushes.Transparent, BorderBrush = won ? (Brush)FindResource("Accent") : (Brush)FindResource("Line"), BorderThickness = new Thickness(1),
                                            Child = new TextBlock { Text = won ? "WIN" : "LOSS", FontSize = 11, FontWeight = FontWeights.Bold, Foreground = won ? (Brush)FindResource("Accent") : muted } };
                    Grid.SetColumn(pill, 2); head.Children.Add(pill);
                }
                card.Children.Add(head);

                // the move now (the same height while it plays)
                card.Children.Add(Label("NOW", 20));
                rvMoveInfo[k] = new TextBlock { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Foreground = muted, LineHeight = 18, MinHeight = 54, Margin = new Thickness(0, -2, 0, 0) };
                card.Children.Add(rvMoveInfo[k]);

                // three numbers
                var opp = r.Fighters.Count == 2 ? rvStats.P[1 - k] : null;
                var tiles = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                void Tile(string v, string label, string tip)
                {
                    var sp = new StackPanel { ToolTip = tip, Background = Brushes.Transparent, Margin = new Thickness(0, 0, tiles.Children.Count < 2 ? 28 : 0, 0) };
                    sp.Children.Add(new TextBlock { Text = v, FontSize = 22, FontFamily = cond, FontWeight = FontWeights.Bold, Foreground = text });
                    sp.Children.Add(new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = dim, Margin = new Thickness(0, -2, 0, 0) });
                    tiles.Children.Add(sp);
                }
                Tile(p.DamageDealt.ToString("0", CultureInfo.InvariantCulture) + "%", "DAMAGE DEALT", "Damage dealt to the other player (taken: " + p.DamageTaken.ToString("0", CultureInfo.InvariantCulture) + "%)");
                Tile(p.Hits.ToString(CultureInfo.InvariantCulture), "HITS", "Frames in which the other player took damage (a multi-hit move counts each hit)");
                Tile(opp != null && opp.StocksLost > 0 ? (p.DamageDealt / opp.StocksLost).ToString("0", CultureInfo.InvariantCulture) + "%" : "–", "PER KO", "Damage dealt for each stock taken: lower is more efficient");
                card.Children.Add(tiles);

                // the stocks lost: each a link to 1.5 s before it
                var kos = rvStats.Kos.Where(x => x.Fighter == k).ToList();
                card.Children.Add(Label(kos.Count == 0 ? "NO STOCK LOST" : kos.Count == 1 ? "1 STOCK LOST" : kos.Count + " STOCKS LOST", 16));
                foreach (var ko in kos)
                {
                    int sec = ko.Frame / 60;
                    var g = new Grid();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) }); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) }); g.ColumnDefinitions.Add(new ColumnDefinition());
                    TextBlock C(string s, int c, Brush b) { var t = new TextBlock { Text = s, FontSize = 13 }; if (b != null) t.Foreground = b; Grid.SetColumn(t, c); g.Children.Add(t); return t; }
                    C(string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}", sec / 60, sec % 60), 0, null);
                    C(ko.Damage.ToString("0", CultureInfo.InvariantCulture) + "%", 1, null);
                    C("off the " + ko.Where, 2, null);
                    var b2 = new Button { Style = (Style)FindResource("Link"), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0, 2, 0, 2), Tag = ko, ToolTip = "Watch it, from 1.5 seconds before", Content = g };
                    b2.Click += (s, e) => { var kk = (ReplayStats.Ko)((Button)s).Tag; int i = RvView.Replay.FrameList.FindIndex(x => x.Number >= kk.Frame); RvPause(true); RvSet(Math.Max(0, i - 90)); };
                    card.Children.Add(b2);
                }

                // the most used moves
                if (p.Moves.Count > 0)
                {
                    card.Children.Add(Label("MOST USED MOVES", 16));
                    int top = p.Moves[0].Value;
                    foreach (var m in p.Moves.Take(5))
                    {
                        var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                        g.ColumnDefinitions.Add(new ColumnDefinition()); g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
                        g.RowDefinitions.Add(new RowDefinition()); g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                        g.Children.Add(new TextBlock { Text = m.Key, FontSize = 13, Foreground = text });
                        var c = new TextBlock { Text = m.Value + "×", FontSize = 13, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Right };
                        Grid.SetColumn(c, 1); g.Children.Add(c);
                        var track = new Grid { Height = 3, Margin = new Thickness(0, 4, 0, 0) };
                        track.Children.Add(new Border { Background = new SolidColorBrush(RvPalette.Hex("#1C2836")), CornerRadius = new CornerRadius(1.5) });
                        track.Children.Add(new Border { Background = col, Opacity = .6, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Width = 0, Tag = (double)m.Value / top });
                        track.SizeChanged += (s, e) => { var tr = (Grid)s; var fill = (Border)tr.Children[1]; fill.Width = Math.Max(3, tr.ActualWidth * (double)fill.Tag); };
                        Grid.SetRow(track, 1); Grid.SetColumnSpan(track, 2); g.Children.Add(track);
                        card.Children.Add(g);
                    }
                }
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
                var t = rvMoveInfo[k]; t.Inlines.Clear();
                var s = fr.Of(k);
                if (s == null) continue;
                var col = new SolidColorBrush(RvPalette.Player[k % 4]);
                string mv = ReplayNames.MoveName(s.Motion);
                t.Inlines.Add(new System.Windows.Documents.Run(mv ?? ReplayNames.ActionText(s)) { FontWeight = FontWeights.SemiBold, Foreground = col, FontSize = 14 });
                if (mv != null) t.Inlines.Add(new System.Windows.Documents.Run("   frame " + (int)Math.Floor(s.MotionFrame + 1)));
                var u = mv != null ? UfdMove(r.BodyKey(k, s) ?? r.Fighters[k].FighterKey, mv) : null;      // (0.2.37) the frame data of the body in play
                if (u != null)
                {
                    string g(string key) => u.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "-";
                    string nm = g("name");
                    int par = nm.IndexOf('('); if (par > 0 && nm.EndsWith(")")) t.Inlines.Add(new System.Windows.Documents.Run("  " + nm.Substring(par)));
                    t.Inlines.Add(new System.Windows.Documents.LineBreak());
                    t.Inlines.Add(new System.Windows.Documents.Run("Startup " + g("startup") + "  ·  active " + g("active") + "  ·  total " + g("total") + "  ·  " + g("damage") + "%"));
                }
                var gb = s.Hits.Count(h => h.Grab);
                if (gb > 0) { t.Inlines.Add(new System.Windows.Documents.LineBreak()); t.Inlines.Add(new System.Windows.Documents.Run(ReplayView.GrabName(s)) { Foreground = new SolidColorBrush(RvPalette.Hex("#C77DFF")) }); }
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
            int zu = Array.IndexOf(args, "--zoomup");       // --zoomup <game units>: the zoom's centre higher (a face)
            if (zu > 0 && zu + 1 < args.Length && double.TryParse(args[zu + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double up)) RvView.ZoomUp = up;
            int off = Array.IndexOf(args, "--off");          // --off hit,hurt,grab,info: those switches off (screenshots)
            if (Array.IndexOf(args, "--legend") > 0) RvLegendOn.IsChecked = true;
            if (off > 0 && off + 1 < args.Length)
                foreach (var w in args[off + 1].Split(','))
                {
                    if (w == "hit") RvHit.IsChecked = false;
                    else if (w == "hurt") RvHurt.IsChecked = false;
                    else if (w == "grab") RvGrab.IsChecked = false;
                    else if (w == "info") RvInfo.IsChecked = false;
                }
        }
    }
}
