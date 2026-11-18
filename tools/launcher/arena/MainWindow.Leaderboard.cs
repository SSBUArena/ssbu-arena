// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.52) The Leaderboard tab: SSBU Arena's ranked 1v1 board, the same data as ssbuarena.com/leaderboard. It reads the database
// function public.ranked_leaderboard (arena-server tools/net/supabase/20261118_ranked_leaderboard.sql) with the publishable key:
// names, tags, avatars, elo, wins, losses and main fighter only, never account ids. Signed in, the player's own row is highlighted
// and "Your position" shows it even when it is not in the list. Ranked and elo are SSBU Arena's alone (OpenSmash shows mastery points).
// SSBU_LEADERBOARD=0 hides the tab.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Arena
{
    /// <summary>one player of the board, as the list shows it</summary>
    public sealed class LeaderRow
    {
        // (properties: the list's template binds to them)
        public int Rank { get; set; }
        public int Tag { get; set; }
        public int Elo { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Games { get; set; }
        /// <summary>how many players the whole (filtered) board has: every row of one answer carries it</summary>
        public int Total { get; set; }
        public string Name { get; set; }
        public string Avatar { get; set; }
        public string Fighter { get; set; }
        public bool IsMe { get; set; }
        public ImageSource Icon { get; set; }
        public string RankText => Rank.ToString(CultureInfo.InvariantCulture);
        public string TagText => "#" + Tag.ToString("0000", CultureInfo.InvariantCulture);
        public string Initials => string.Concat(Name.Split(new[] { '_', '.', '-' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])))
                                  is string s && s.Length >= 2 ? s : (Name.Length >= 2 ? Name.Substring(0, 2) : Name).ToUpperInvariant();
        public string Record => Wins.ToString(CultureInfo.InvariantCulture) + "–" + Losses.ToString(CultureInfo.InvariantCulture);
        public string RateText => Wins + Losses > 0 ? (Wins * 100 + (Wins + Losses) / 2) / (Wins + Losses) + "% wins" : "";
        public string EloText => Elo.ToString(CultureInfo.InvariantCulture);
        public string RowTip => Name + TagText + ": " + Games + (Games == 1 ? " ranked match" : " ranked matches") + (Fighter != "-" ? ", mostly " + Fighter : "");
        public Visibility YouVisible => IsMe ? Visibility.Visible : Visibility.Collapsed;
        static Brush B(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
        static readonly Brush MeBack = B("#12263A"), MeLine = B("#3E7FB0"), Back = B("#0F1620"), Line = B("#233041"), TopLine = B("#2C4A66");
        public Brush RowBack => IsMe ? MeBack : Back;
        public Brush RowBorder => IsMe ? MeLine : Rank == 1 ? TopLine : Line;
        public Brush RankBrush => (Brush)Application.Current.Resources[Rank <= 3 ? "Accent" : "Muted"];

        /// <summary>a row of ranked_leaderboard's answer (JavaScriptSerializer's dictionaries); null: not a usable row</summary>
        public static LeaderRow From(Dictionary<string, object> d)
        {
            int I(string k) => d.TryGetValue(k, out object v) && v != null ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;
            string S(string k) => d.TryGetValue(k, out object v) ? v as string : null;
            string name = S("username");
            if (string.IsNullOrEmpty(name)) return null;
            return new LeaderRow { Rank = I("rank"), Name = name, Tag = I("tag"), Avatar = S("avatar"), Elo = I("elo"), Wins = I("wins"), Losses = I("losses"),
                                   Games = I("games"), Total = I("total"), Fighter = string.IsNullOrWhiteSpace(S("main_fighter")) ? "-" : S("main_fighter") };
        }

        public bool Is(Profile p) => p != null && Tag == p.Tag && string.Equals(Name, p.Username, StringComparison.OrdinalIgnoreCase);
    }

    public partial class MainWindow
    {
        /// <summary>SSBU_LEADERBOARD=0 (or off) hides the Leaderboard tab</summary>
        static bool LeaderboardOn => !(Environment.GetEnvironmentVariable("SSBU_LEADERBOARD") is string v && (v.Trim() == "0" || v.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)));
        /// <summary>how many players the list asks for (the database function returns at most 200)</summary>
        const int LbLimit = 100;
        /// <summary>the tab, shown again after this long, reads the board again</summary>
        static readonly TimeSpan LbFresh = TimeSpan.FromSeconds(60);

        DateTime lbReadAt = DateTime.MinValue;
        string lbFor = "";          // the account the board was read for ("" = not signed in)
        int lbSeq;                   // the newest read; older answers are dropped
        DispatcherTimer lbTyping;

        void InitLeaderboard()
        {
            TabLeaderboard.Visibility = LeaderboardOn ? Visibility.Visible : Visibility.Collapsed;
            LbRegionBox.Items.Add("All regions");
            foreach (var r in Regions.Table) LbRegionBox.Items.Add(r.Name);
            LbRegionBox.SelectedIndex = 0;
            lbTyping = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            lbTyping.Tick += (s, e) => { lbTyping.Stop(); LoadLeaderboard(); };
        }

        static string MeKey => Account.SignedIn ? Account.Me.Username + "#" + Account.Me.Tag : "";

        void LeaderboardShown()
        {
            if (DateTime.UtcNow - lbReadAt > LbFresh || lbFor != MeKey) LoadLeaderboard();
        }

        /// <summary>signed in or out: the highlight and "Your position" follow at the next look at the tab (now, if it is open)</summary>
        void LeaderboardAccountChanged()
        {
            if (PageLeaderboard != null && PageLeaderboard.Visibility == Visibility.Visible && lbFor != MeKey) LoadLeaderboard();
        }

        void LbRefresh_Click(object sender, RoutedEventArgs e) { LoadLeaderboard(); }
        void LbWeb_Click(object sender, RoutedEventArgs e) { Core.Open(Core.Site + "leaderboard"); }
        void LbRegion_Changed(object sender, SelectionChangedEventArgs e) { if (lbTyping != null && PageLeaderboard.Visibility == Visibility.Visible) LoadLeaderboard(); else lbReadAt = DateTime.MinValue; }

        void LbSearch_Changed(object sender, TextChangedEventArgs e)
        {
            bool empty = LbSearch.Text.Length == 0;
            LbSearchHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            LbSearchClear.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (lbTyping == null) return;
            lbTyping.Stop(); lbTyping.Start();
        }

        void LbSearchClear_Click(object sender, RoutedEventArgs e) { LbSearch.Text = ""; LbSearch.Focus(); }

        /// <summary>one call of ranked_leaderboard (region: null = all; search: null = everyone)</summary>
        static List<LeaderRow> ReadBoard(string region, string search, int limit)
        {
            var js = new JavaScriptSerializer();
            string body = js.Serialize(new Dictionary<string, object> { { "p_region", region }, { "p_search", string.IsNullOrWhiteSpace(search) ? null : search.Trim() }, { "p_limit", limit }, { "p_offset", 0 } });
            var rows = js.Deserialize<List<Dictionary<string, object>>>(Account.AnonPost("/rest/v1/rpc/ranked_leaderboard", body));
            return (rows ?? new List<Dictionary<string, object>>()).Select(LeaderRow.From).Where(r => r != null).ToList();
        }

        void LoadLeaderboard()
        {
            if (lbTyping == null) return;
            lbTyping.Stop();
            int seq = ++lbSeq;
            string region = LbRegionBox.SelectedIndex > 0 ? Regions.Table[LbRegionBox.SelectedIndex - 1].Id : null;
            string regionName = LbRegionBox.SelectedIndex > 0 ? Regions.Table[LbRegionBox.SelectedIndex - 1].Name : null;
            string search = LbSearch.Text.Trim();
            Profile me = Account.SignedIn ? Account.Me : null;
            string meKey = MeKey;
            LbRefresh.IsEnabled = false;
            LbStatus.Text = "Loading the leaderboard…";
            if (LbList.Items.Count == 0) ShowLbState("Loading the leaderboard…", "", false);
            Task.Run(() =>
            {
                var rows = ReadBoard(region, search, LbLimit);
                LeaderRow mine = me != null ? rows.FirstOrDefault(r => r.Is(me)) : null;
                if (me != null && mine == null) mine = ReadBoard(region, me.Username, 20).FirstOrDefault(r => r.Is(me));
                return (rows, mine);
            }).ContinueWith(t =>
            {
                if (seq != lbSeq) return;      // a newer read is on the way
                LbRefresh.IsEnabled = true;
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    var ex = t.Exception?.GetBaseException();
                    Updater.Log("leaderboard: " + (ex?.Message ?? "failed"));
                    // (a POST that cannot connect fails in GetRequestStream, before Account.Http turns it into "Cannot reach ...")
                    bool offline = ex is System.Net.WebException || (ex != null && ex.Message.StartsWith("Cannot reach", StringComparison.Ordinal));
                    LbList.ItemsSource = null;
                    LbHead.Visibility = Visibility.Collapsed;
                    ShowLbState("The leaderboard can't be loaded right now",
                                offline ? "The Arena server can't be reached: check your internet connection, then try again." : "The Arena server didn't answer as expected. Try again in a minute.", true);
                    LbStatus.Text = "";
                    LbMeBox.Visibility = Visibility.Collapsed;
                    return;
                }
                var (rows, mine) = t.Result;
                foreach (var r in rows) { r.IsMe = r.Is(me); r.Icon = AvatarImage(r.Avatar, 76); }
                LbList.ItemsSource = rows;
                lbReadAt = DateTime.UtcNow; lbFor = meKey;
                LbHead.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (rows.Count == 0)
                {
                    if (search != "") ShowLbState("No ranked player matches “" + search + "”", "Try another name, or part of it.", false);
                    else if (regionName != null) ShowLbState("No ranked results on " + regionName + " yet", "Choose All regions to see everyone.", false);
                    else ShowLbState("No ranked results yet", "Ranked 1v1 has just started. Players appear here after their first ranked match with a result.", false);
                }
                else LbEmpty.Visibility = Visibility.Collapsed;

                // your position
                LbMeBox.Visibility = Visibility.Visible;
                LbSignIn.Visibility = me == null ? Visibility.Visible : Visibility.Collapsed;
                LbMeText.Text = me == null ? "Sign in to see where you are on the board."
                              : mine == null ? me.Display + ": not on the board yet" + (regionName != null ? " on " + regionName : "") + ". Your first ranked match with a result puts you on it."
                              : me.Display + "   #" + mine.Rank + "   ·   " + mine.Elo + " elo   ·   " + mine.Record + (mine.RateText != "" ? " (" + mine.RateText + ")" : "")
                                + (rows.Any(r => r.IsMe) ? "" : "   ·   not in this list");

                int total = rows.Count > 0 ? Math.Max(rows[0].Total, rows.Count) : 0;
                LbStatus.Text = rows.Count == 0 ? "" : total + (total == 1 ? " ranked player" : " ranked players") + (regionName != null ? " on " + regionName : "")
                                + (rows.Count < total ? " · the first " + rows.Count + " shown" : "") + " · updated " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void ShowLbState(string title, string text, bool retry)
        {
            LbEmpty.Visibility = Visibility.Visible;
            LbEmptyTitle.Text = title;
            LbEmptyText.Text = text;
            LbEmptyText.Visibility = text == "" ? Visibility.Collapsed : Visibility.Visible;
            LbRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
