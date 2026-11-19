// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.52) The Leaderboard tab: SSBU Arena's ranked 1v1 board, the same data as ssbuarena.com/leaderboard. It reads the database
// function public.ranked_leaderboard (arena-server tools/net/supabase/20261118_ranked_leaderboard.sql) with the publishable key:
// names, tags, avatars, elo, wins, losses and main fighter only, never account ids. Signed in, the player's own row is highlighted
// and "Your position" shows it even when it is not in the list. Ranked and elo are SSBU Arena's alone (OpenSmash shows mastery points).
// SSBU_LEADERBOARD=0 hides the tab.
// (0.2.52) The fighters each player plays, as icons like Slippi's board: the main first, at most 3. The icons are the game's own stock
// icons from this player's data.arc (Core.MakeAvatars -> avatars\stock\<fighter>.png), else the fighter's portrait (avatars\), else a
// text badge: no game art is shipped. The server names fighters as arena-server's arena_results.c does ("Mario", "Pyra / Mythra", or
// the hex id it had no name for): ReplayNames.FighterKeyOfLabel maps that to the game's key. Today the function gives main_fighter
// only; a row may also carry "fighters", a list (json array, or its text) of {fighter, elo, games, wins, losses} with the player's
// most played first (elo per fighter, not there yet): then each icon's tooltip gives its own elo, and the fighter filter ranks by it. The
// fighter filter appears once the board has rows: it works on the rows read (the first LbLimit), on main_fighter / fighters.
// (0.2.53) Elo per fighter (20261120_ranked_elo.sql): the board comes from public.ranked_leaderboard_fighters (ranked_leaderboard when
// that answers 404, for the rest of the run); each row has the tier of its fighter (tier, tier_color: under the elo) and "placed"
// (the "Placed only" chip), each fighter of "fighters" its tier (in the icon's tooltip). "Your position" comes from my_ranked().board
// (MainWindow.Ranked.cs) for All regions. SSBU_RANKED_ELO=0: 0.2.52's board.
// (0.2.55) Ranked per account (RankedAccount.cs; SSBU_RANKED_ACCOUNT=0: the 0.2.54 board above): public.ranked_board, one row per
// account with its rating (one decimal), tier (the emblem with its pips, Grandmaster rows stand out), W-L, region and region rank, and
// its 3 most played fighters (games and wins in each icon's tooltip). The region filter is the account's region (the server's), the
// fighter filter goes to the server too (p_fighter: the accounts that played that fighter), "Placed only" works on the rows read.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Arena
{
    /// <summary>(0.2.52) one fighter of a leaderboard row: its icon (or a text badge), and its own elo when the server gives one</summary>
    public sealed class LeaderFighter
    {
        public string Label { get; set; }       // as the server wrote it
        public string Key { get; set; }         // the game's fighter key ("mario"), null: not known
        public bool Main { get; set; }
        public bool HasElo { get; set; }
        public int Elo { get; set; }
        public int Games { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        /// <summary>(0.2.53) this fighter's tier (ranked_leaderboard_fighters), null: not given</summary>
        public string Tier { get; set; }
        public string Name => Key != null ? ReplayNames.FighterName(ReplayNames.Hash40("ui_chara_" + Key), 0) : IsId(Label) ? "Unknown fighter" : Label;
        static bool IsId(string s) => s != null && s.Length >= 10 && s.All(Uri.IsHexDigit);
        public ImageSource Icon => MainWindow.StockIcon(Key);
        public Visibility BadgeVisible => Icon == null ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>the text badge when this PC has no picture of the fighter: "DK", "Ma", "?"</summary>
        public string Badge
        {
            get
            {
                if (Key == null && IsId(Label)) return "?";
                var w = Name.Split(new[] { ' ', '.', '&', '/', '-' }, StringSplitOptions.RemoveEmptyEntries).Where(x => char.IsLetterOrDigit(x[0])).ToList();
                if (w.Count >= 2) return string.Concat(w.Take(2).Select(x => char.ToUpperInvariant(x[0])));
                return w.Count == 1 ? (w[0].Length >= 2 ? char.ToUpperInvariant(w[0][0]) + w[0].Substring(1, 1) : w[0].ToUpperInvariant()) : "?";
            }
        }
        // (every icon the same size, 32 px in MainWindow.xaml: half of the game's 64 px stock icons; the main only first; its elo in the tooltip)
        public string Tip => Name + (Main ? " (main)" : "") + (HasElo ? ": " + (Ranked.On ? Ranked.Num(Elo) : Elo.ToString()) + " elo" : "")
                             + (Ranked.On && Tier != null ? (HasElo ? " · " : ": ") + Tier : "")
                             + (Games > 0 ? (HasElo || (Ranked.On && Tier != null) ? ", " : ": ") + Games + (Games == 1 ? " ranked match" : " ranked matches") + (Wins + Losses > 0 ? " (" + Wins + "–" + Losses + ")" : "") : "");
        /// <summary>(0.2.55) ranked per account: "Mario: 12 games, 8 wins (67%)"</summary>
        public string AccTip => Name + ": " + Games + (Games == 1 ? " game, " : " games, ") + Wins + (Wins == 1 ? " win" : " wins") + (Games > 0 ? " (" + (Wins * 100 + Games / 2) / Games + "%)" : "");
        /// <summary>the same fighter, the same player: one key, or one label when the key is not known</summary>
        public bool Same(LeaderFighter o) => o != null && (Key != null ? Key == o.Key : string.Equals(Label, o.Label, StringComparison.OrdinalIgnoreCase));
        public string FilterKey => Key ?? "label:" + (Label ?? "").ToLowerInvariant();
    }

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
        public string EloText => Ranked.On ? Ranked.Num(Elo) : Elo.ToString(CultureInfo.InvariantCulture);
        /// <summary>(0.2.53) the tier of the row's fighter (its main, or the filter's fighter) and its colour; placed: past the placement games</summary>
        public string Tier { get; set; }
        public string TierColor { get; set; }
        public bool Placed { get; set; }
        public Visibility TierVisible => Ranked.On && !string.IsNullOrEmpty(Tier) ? Visibility.Visible : Visibility.Collapsed;
        public string TierText => (Tier ?? "").ToUpperInvariant();
        public Brush TierBrush => Ranked.BrushOf(TierColor ?? Ranked.ColorOf(Tier));
        public string TierTip => Placed || Tier != Ranked.PlacementTier ? Tier + " (" + (Fighters.Count > 0 ? Fighters[0].Name : "main fighter") + ")"
                                 : Tier + ": " + (Fighters.Count > 0 ? Fighters[0].Name : "the main fighter") + " has not played its " + Ranked.PlacementGames + " placement games yet";
        public string RowTip => Name + TagText + ": " + Games + (Games == 1 ? " ranked match" : " ranked matches") + (Fighters.Count > 0 ? ", mostly " + Fighters[0].Name : "");
        /// <summary>(0.2.52) the fighters the player plays, the main first, at most MaxFighters</summary>
        public List<LeaderFighter> Fighters { get; set; } = new List<LeaderFighter>();
        public const int MaxFighters = 3;
        public Visibility NoFighterVisible => Fighters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>a copy for the list (the filter shows another rank and elo; a recycled row needs a new object to show them)</summary>
        public LeaderRow View(int rank, int elo, List<LeaderFighter> fighters, string tier = null)
        {
            var v = (LeaderRow)MemberwiseClone();
            v.Rank = rank; v.Elo = elo; v.Fighters = fighters;
            if (tier != null) { v.Tier = tier; v.TierColor = Ranked.ColorOf(tier) ?? TierColor; v.Placed = tier != Ranked.PlacementTier; }
            return v;
        }
        public Visibility YouVisible => IsMe ? Visibility.Visible : Visibility.Collapsed;

        // ---------------------------------------------------------------- (0.2.55) ranked per account (ranked_board)
        public double Rating { get; set; }
        public string Family { get; set; }
        public int? Division { get; set; }
        public string Region { get; set; }
        public int? RegionRank { get; set; }
        public bool Gm => RankedAccount.IsGrandmaster(Tier, Family);
        public EmblemSpec Emblem => Placed || Gm ? EmblemSpec.Of(Tier, Family, Division, TierColor) : EmblemSpec.Unranked;
        public string RatingText => RankedAccount.R(Rating);
        /// <summary>"GOLD 2", "GRANDMASTER", "UNRANKED 3/5"</summary>
        public string AccTierText => Placed || Gm ? (Tier ?? "").ToUpperInvariant() : RankedAccount.PlacementTier.ToUpperInvariant() + " " + Math.Min(Games, RankedAccount.PlacementGames) + "/" + RankedAccount.PlacementGames;
        public Brush AccTierBrush => Placed || Gm ? Ranked.BrushOf(TierColor ?? RankedAccount.TierOf(Tier)?.Color) : (Brush)Application.Current.Resources["Muted"];
        /// <summary>"Europe #7" under the name; "" when the account has no region yet</summary>
        public string RegionText => RankedAccount.RegionName(Region) is string n ? n + (RegionRank != null ? " #" + RegionRank.Value.ToString(CultureInfo.InvariantCulture) : "") : "";
        public Visibility RegionVisible => RegionText != "" ? Visibility.Visible : Visibility.Collapsed;
        public string AccTierTip => Gm ? "Grandmaster: top " + RankedAccount.Season.GmTopN + " of " + (RankedAccount.RegionName(Region) ?? "a region") + " by rating"
                                  : Placed ? Tier + " · " + RatingText + " rating"
                                  : "Placement: " + Math.Min(Games, RankedAccount.PlacementGames) + " of " + RankedAccount.PlacementGames + " rated games played";
        public string AccRowTip => Name + TagText + ": " + Games + (Games == 1 ? " rated game" : " rated games") + (RegionText != "" ? " · " + RegionText : "");
        static readonly Brush GmBack = B("#1F1520"), GmLine = B("#8A3A47");
        public Brush AccRowBack => IsMe ? MeBack : Gm ? GmBack : Back;
        public Brush AccRowBorder => Gm ? GmLine : RowBorder;
        public Brush AccRankBrush => Gm ? Ranked.BrushOf("#FF5A6A") : RankBrush;

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
            var row = new LeaderRow { Rank = I("rank"), Name = name, Tag = I("tag"), Avatar = S("avatar"), Elo = I("elo"), Wins = I("wins"), Losses = I("losses"),
                                      Games = I("games"), Total = I("total"), Fighter = string.IsNullOrWhiteSpace(S("main_fighter")) ? "-" : S("main_fighter") };
            row.Fighters = FightersOf(d.TryGetValue("fighters", out object fl) ? fl : null, S("main_fighter"));
            row.Tier = S("tier"); row.TierColor = S("tier_color");
            row.Placed = d.TryGetValue("placed", out object pl) ? pl is bool pb && pb : row.Tier != null && row.Tier != Ranked.PlacementTier;
            // (0.2.55) ranked_board's columns (absent from the older functions)
            row.Rating = RankedAccount.Dbl(d, "rating") ?? 0; row.Family = S("tier_family"); row.Division = Ranked.Int(d, "tier_division");
            row.Region = S("region"); row.RegionRank = Ranked.Int(d, "region_rank");
            return row;
        }

        /// <summary>(0.2.52) the row's fighters: "fighters" (a list of {fighter, elo, games, wins, losses} or of names, or its json text;
        /// the server's order: most played first) with main_fighter first; only main_fighter when there is no list</summary>
        static List<LeaderFighter> FightersOf(object list, string main)
        {
            var all = new List<LeaderFighter>();
            try
            {
                if (list is string text && text.TrimStart().StartsWith("[", StringComparison.Ordinal)) list = new JavaScriptSerializer().DeserializeObject(text);
                if (list is IEnumerable items && !(list is string))
                    foreach (var it in items)
                    {
                        LeaderFighter f = null;
                        if (it is string s) f = FighterOf(s);
                        else if (it is IDictionary<string, object> d)
                        {
                            string label = (d.TryGetValue("fighter", out object v) ? v as string : null) ?? (d.TryGetValue("name", out v) ? v as string : null);
                            f = FighterOf(label);
                            if (f == null) continue;
                            int? N(string k) => d.TryGetValue(k, out object x) && x != null && !(x is string) ? Convert.ToInt32(x, CultureInfo.InvariantCulture) : (int?)null;
                            f.HasElo = N("elo") != null; f.Elo = N("elo") ?? 0; f.Games = N("games") ?? 0; f.Wins = N("wins") ?? 0; f.Losses = N("losses") ?? 0;
                            f.Tier = d.TryGetValue("tier", out object tv) ? tv as string : null;
                        }
                        if (f != null && !all.Any(x => x.Same(f))) all.Add(f);
                    }
            }
            catch { }
            var m = FighterOf(main);
            if (m != null)
            {
                var there = all.FirstOrDefault(x => x.Same(m));
                if (there != null) all.Remove(there);
                all.Insert(0, there ?? m);
            }
            if (all.Count > 0) all[0].Main = true;
            return all;
        }

        static LeaderFighter FighterOf(string label) => string.IsNullOrWhiteSpace(label) || label.Trim() == "-" ? null : new LeaderFighter { Label = label.Trim(), Key = ReplayNames.FighterKeyOfLabel(label) };

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
            if (RankedAccount.On)      // (0.2.55) the account rows: rating, emblem and tier
            {
                LbList.ItemTemplate = (DataTemplate)LbList.FindResource("LbAccRow");
                LbEloCol.Width = new GridLength(AccRatingWidth);
                LbSubtitle.Text = "Ranked 1v1 on SSBU Arena: one rating per account. Wins and losses count rated games.";
                LbEloHead.Text = "RATING";
                LbEloHead.HorizontalAlignment = HorizontalAlignment.Left; LbEloHead.Margin = new Thickness(24, 0, 0, 0);      // (over the emblem, as LbAccRow)
                LbEloHead.ToolTip = "The account's rating and tier (one rating per account, whatever fighter is played)";
                LbFightersHead.ToolTip = "The fighters played most in ranked (hover one: its games and wins)";
                LbRegionBox.ToolTip = "Only the players of this region (the region where they played the most rated matches)";
                LbFighterBox.ToolTip = "Only the players who played this fighter in ranked";
                LbPlacedBox.ToolTip = "Only the players who finished their " + RankedAccount.PlacementGames + " placement games";
            }
        }
        /// <summary>(0.2.55) the width of the rating column (MainWindow.xaml's LbAccRow uses the same)</summary>
        const double AccRatingWidth = 190;

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

        /// <summary>(0.2.53) ranked_leaderboard_fighters answered 404 (not on this server yet): ranked_leaderboard for the rest of the run</summary>
        static volatile bool lbOldFunction;

        /// <summary>one call of ranked_leaderboard_fighters, or ranked_leaderboard (0.2.52, SSBU_RANKED_ELO=0, or a server without the new
        /// function) (region: null = all; search: null = everyone)</summary>
        static List<LeaderRow> ReadBoard(string region, string search, int limit, string fighter = null)
        {
            var js = new JavaScriptSerializer();
            if (RankedAccount.On)      // (0.2.55) public.ranked_board (fighter: p_fighter, the label as the server writes it)
            {
                var a = new Dictionary<string, object> { { "p_region", region }, { "p_search", string.IsNullOrWhiteSpace(search) ? null : search.Trim() }, { "p_fighter", fighter },
                                                         { "p_limit", limit }, { "p_offset", 0 } };
                string t = Account.AnonPost("/rest/v1/rpc/ranked_board", js.Serialize(a));
                RankedAccount.LoadTiers();
                var acc = js.Deserialize<List<Dictionary<string, object>>>(t);
                return (acc ?? new List<Dictionary<string, object>>()).Select(LeaderRow.From).Where(r => r != null).ToList();
            }
            if (!Ranked.On)
            {
                string body = js.Serialize(new Dictionary<string, object> { { "p_region", region }, { "p_search", string.IsNullOrWhiteSpace(search) ? null : search.Trim() }, { "p_limit", limit }, { "p_offset", 0 } });
                var old = js.Deserialize<List<Dictionary<string, object>>>(Account.AnonPost("/rest/v1/rpc/ranked_leaderboard", body));
                return (old ?? new List<Dictionary<string, object>>()).Select(LeaderRow.From).Where(r => r != null).ToList();
            }
            bool perFighter = !lbOldFunction;
            var args = new Dictionary<string, object> { { "p_region", region }, { "p_search", string.IsNullOrWhiteSpace(search) ? null : search.Trim() } };
            if (perFighter) args["p_fighter"] = null;
            args["p_limit"] = limit; args["p_offset"] = 0;
            string text;
            try { text = Account.AnonPost(perFighter ? "/rest/v1/rpc/ranked_leaderboard_fighters" : "/rest/v1/rpc/ranked_leaderboard", js.Serialize(args)); }
            catch (Account.ArenaHttpException e) when (perFighter && e.Status == 404)
            {
                lbOldFunction = true;
                Updater.Log("leaderboard: ranked_leaderboard_fighters not on the server (404), ranked_leaderboard is used");
                return ReadBoard(region, search, limit);
            }
            Ranked.LoadTiers();      // (once per run: the colours when a row has none)
            var rows = js.Deserialize<List<Dictionary<string, object>>>(text);
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
            string fighter = RankedAccount.On && lbFighter != null && lbFighterSeen.TryGetValue(lbFighter, out var lf) ? lf.Label : null;      // (0.2.55) p_fighter
            Profile me = Account.SignedIn ? Account.Me : null;
            string meKey = MeKey;
            LbRefresh.IsEnabled = false;
            LbStatus.Text = "Loading the leaderboard…";
            if (LbList.Items.Count == 0) ShowLbState("Loading the leaderboard…", "", false);
            Task.Run(() =>
            {
                var rows = ReadBoard(region, search, LbLimit, fighter);
                LeaderRow mine = me != null ? rows.FirstOrDefault(r => r.Is(me)) : null;
                if (me != null && mine == null) mine = ReadBoard(region, me.Username, 20, fighter).FirstOrDefault(r => r.Is(me));
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
                    lbRows = new List<LeaderRow>();
                    FillLbFighters();
                    LbHead.Visibility = Visibility.Collapsed;
                    ShowLbState("The leaderboard can't be loaded right now",
                                offline ? "The Arena server can't be reached: check your internet connection, then try again." : "The Arena server didn't answer as expected. Try again in a minute.", true);
                    LbStatus.Text = "";
                    LbMeBox.Visibility = Visibility.Collapsed;
                    lbShown = false;
                    return;
                }
                var (rows, mine) = t.Result;
                foreach (var r in rows) { r.IsMe = r.Is(me); r.Icon = AvatarImage(r.Avatar, 76); }
                lbRows = rows; lbSearch = search; lbRegionName = regionName; lbFighterRead = fighter;
                lbReadAt = DateTime.UtcNow; lbFor = meKey;
                lbMe = me; lbMine = mine; lbShown = true;
                FillLbFighters();
                ShowLbRows();
                ShowLbMe();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        Profile lbMe;               // the account and its row of the last board shown
        LeaderRow lbMine;
        bool lbShown;

        /// <summary>"Your position": (0.2.53) my_ranked().board for All regions when it was read (the main fighter's tier and elo), else the
        /// player's row of the board read (0.2.52)</summary>
        void ShowLbMe()
        {
            if (!lbShown) return;
            var me = lbMe; var mine = lbMine; string regionName = lbRegionName;
            LbMeBox.Visibility = Visibility.Visible;
            LbSignIn.Visibility = me == null ? Visibility.Visible : Visibility.Collapsed;
            if (RankedAccount.On) { ShowLbMeAcc(me, mine, regionName); return; }
            var mr = Ranked.On && me != null && regionName == null && myRankedFor == me.Username + "#" + me.Tag ? myRanked : null;
            if (mr != null && mr.HasBoard)
            {
                int n = mr.BoardWins + mr.BoardLosses;
                string main = mr.BoardMain ?? mr.Main;
                LbMeText.Text = me.Display + "   #" + Ranked.Num(mr.BoardRank) + "   ·   " + (mr.BoardTier != null ? mr.BoardTier + " " : "") + Ranked.Num(mr.BoardElo) + " elo"
                              + (main != null ? " (" + new LeaderFighter { Label = main, Key = ReplayNames.FighterKeyOfLabel(main) }.Name + ")" : "")
                              + "   ·   " + mr.BoardWins + "–" + mr.BoardLosses + (n > 0 ? " (" + (mr.BoardWins * 100 + n / 2) / n + "% wins)" : "")
                              + (lbRows.Any(r => r.IsMe) ? "" : "   ·   not in this list");
                return;
            }
            LbMeText.Text = me == null ? "Sign in to see where you are on the board."
                          : mine == null ? me.Display + ": not on the board yet" + (regionName != null ? " on " + regionName : "") + ". Your first ranked match with a result puts you on it."
                          : me.Display + "   #" + mine.Rank + "   ·   " + (Ranked.On && mine.Tier != null ? mine.Tier + " " : "") + mine.EloText + " elo   ·   " + mine.Record + (mine.RateText != "" ? " (" + mine.RateText + ")" : "")
                            + (lbRows.Any(r => r.IsMe) ? "" : "   ·   not in this list");
        }

        // ---------------------------------------------------------------- (0.2.52) fighters: icons and the filter
        List<LeaderRow> lbRows = new List<LeaderRow>();
        string lbSearch = "", lbRegionName;
        string lbFighter;            // the filter's fighter (LeaderFighter.FilterKey), null: all
        bool lbFilling;
        bool lbPlacedOnly;           // (0.2.53) the "Placed only" chip

        static readonly Dictionary<string, ImageSource> stockIcons = new Dictionary<string, ImageSource>();
        // the files a fighter's icon can have: the game names some stock icons after the panel (Ice Climbers, Pyra / Mythra)
        static readonly Dictionary<string, string[]> StockAlso = new Dictionary<string, string[]>
        {
            { "ice_climber", new[] { "popo" } }, { "popo", new[] { "ice_climber" } }, { "nana", new[] { "ice_climber", "popo" } },
            { "eflame", new[] { "eflame_first", "eflame_only", "element" } }, { "elight", new[] { "elight_first", "elight_only", "element" } },
            { "element", new[] { "eflame_first", "elight_first" } }, { "eflame_first", new[] { "eflame_only", "eflame" } }, { "elight_first", new[] { "elight_only", "elight" } },
            { "eflame_only", new[] { "eflame_first", "eflame" } }, { "elight_only", new[] { "elight_first", "elight" } }, { "ptrainer_low", new[] { "ptrainer" } },
        };

        /// <summary>(0.2.52) a fighter's small icon from this player's game files: its stock icon (avatars\stock), else its portrait; null: none
        /// (the row shows a text badge)</summary>
        public static ImageSource StockIcon(string key)
        {
            if (key == null) return null;
            if (stockIcons.TryGetValue(key, out var im)) return im;
            var names = new[] { key }.Concat(StockAlso.TryGetValue(key, out var also) ? also : new string[0]).ToList();
            var web = WebStockIcon(names);   // (0.2.55) the site's icons first: the same as ssbuarena.com's board
            if (web != null) return stockIcons[key] = web;
            foreach (var n in names)
            {
                string f = Path.Combine(Core.AvatarsDir, "stock", n + ".png");
                if (!File.Exists(f)) continue;
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(f); b.EndInit(); b.Freeze();
                    return stockIcons[key] = b;
                }
                catch { }
            }
            foreach (var n in names) { var p = AvatarImage(n, 76); if (p != null) return stockIcons[key] = p; }
            return stockIcons[key] = null;
        }

        // (0.2.55) The website's stock icons (Max_Hamm's, credited on ssbuarena.com), the same the site's board shows: downloaded once per
        // fighter from https://www.ssbuarena.com/fighters/<key>/1.png into build\web_stock\<key>.png, then read from there. Nothing is
        // shipped in this repository. Until a fighter's file is here (first run, offline) the game's own icon above is used as before.
        // SSBU_STOCK_ICONS_WEB=0: the game's icons only (0.2.54). SSBU_STOCK_ICONS_BASE (TEST only): another base, accepted only as
        // http://localhost:<port>/ or http://127.0.0.1:<port>/ (the site's dev server); anything else is ignored.
        static string webStockBase;      // (read at the first download, after the log is opened: its line stays in launcher_update.log)
        static string WebStockBase => webStockBase ?? (webStockBase = WebStockBaseOf(Environment.GetEnvironmentVariable("SSBU_STOCK_ICONS_BASE")));
        static string WebStockBaseOf(string v)
        {
            const string site = "https://www.ssbuarena.com/fighters/";
            if (string.IsNullOrWhiteSpace(v)) return site;
            var m = System.Text.RegularExpressions.Regex.Match(v.Trim(), @"^http://(localhost|127\.0\.0\.1):([0-9]{1,5})/?$");
            if (!m.Success || !int.TryParse(m.Groups[2].Value, out int port) || port < 1 || port > 65535)
            {
                Updater.Log("stock icons: SSBU_STOCK_ICONS_BASE ignored (only http://localhost:<port>/ or http://127.0.0.1:<port>/)");
                return site;
            }
            Updater.Log("stock icons: from the TEST base " + m.Value.TrimEnd('/') + "/fighters/ (SSBU_STOCK_ICONS_BASE)");
            return m.Value.TrimEnd('/') + "/fighters/";
        }
        static readonly bool webStockOn = Environment.GetEnvironmentVariable("SSBU_STOCK_ICONS_WEB") != "0";
        static readonly HashSet<string> webStockAsked = new HashSet<string>();
        static int webStockRedraw;
        static readonly System.Text.RegularExpressions.Regex WebStockKey = new System.Text.RegularExpressions.Regex("^[a-z][a-z0-9_]{1,23}$");
        static string WebStockDir => Path.Combine(Core.Build, "web_stock");

        // the site has one folder per panel fighter: the game's other names map to it (Pyra / Mythra = "element" here, eflame / elight there)
        static readonly Dictionary<string, string> WebAlias = new Dictionary<string, string>
        {
            { "element", "eflame" }, { "eflame_first", "eflame" }, { "eflame_only", "eflame" }, { "elight_first", "elight" }, { "elight_only", "elight" },
            { "popo", "ice_climber" }, { "ptrainer_low", "ptrainer" },
        };
        static ImageSource WebStockIcon(List<string> names)
        {
            if (!webStockOn) return null;
            names = names.Select(n => WebAlias.TryGetValue(n, out var a) ? a : n).Distinct().ToList();
            foreach (var n in names)
            {
                if (!WebStockKey.IsMatch(n)) continue;
                string f = Path.Combine(WebStockDir, n + ".png");
                if (!File.Exists(f)) continue;
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.UriSource = new Uri(f); b.EndInit(); b.Freeze();
                    return b;
                }
                catch { try { File.Delete(f); } catch { } }   // (a damaged file: fetched again next run)
            }
            var want = names.Where(n => WebStockKey.IsMatch(n)).ToList();
            if (want.Count == 0) return null;
            lock (webStockAsked) { if (!webStockAsked.Add(want[0])) return null; }
            System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var n in want)
                {
                    try
                    {
                        var rq = (System.Net.HttpWebRequest)System.Net.WebRequest.Create(WebStockBase + n + "/1.png");
                        rq.Timeout = 10000; rq.ReadWriteTimeout = 10000;
                        byte[] data;
                        using (var rs = (System.Net.HttpWebResponse)rq.GetResponse())
                        using (var s = rs.GetResponseStream())
                        using (var ms = new MemoryStream())
                        {
                            var buf = new byte[8192]; int r;
                            while ((r = s.Read(buf, 0, buf.Length)) > 0) { ms.Write(buf, 0, r); if (ms.Length > 256 * 1024) throw new IOException("too big"); }
                            data = ms.ToArray();
                        }
                        // a PNG only (its signature), as the site serves it
                        if (data.Length < 8 || data[0] != 0x89 || data[1] != 0x50 || data[2] != 0x4E || data[3] != 0x47) continue;
                        Directory.CreateDirectory(WebStockDir);
                        string tmp = Path.Combine(WebStockDir, n + ".png.tmp"), dst = Path.Combine(WebStockDir, n + ".png");
                        File.WriteAllBytes(tmp, data);
                        if (File.Exists(dst)) File.Delete(dst);
                        File.Move(tmp, dst);
                        Updater.Log("stock icons: " + n + " from the site");
                        if (System.Threading.Interlocked.Exchange(ref webStockRedraw, 1) == 0)   // (one redraw for a burst of icons)
                            Application.Current?.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => { webStockRedraw = 0; (Application.Current?.MainWindow as MainWindow)?.WebStockArrived(); }));
                        return;
                    }
                    catch { }   // (offline, or the site has no icon for it: the game's icon stays)
                }
            });
            return null;
        }

        /// <summary>(0.2.55) a site icon arrived: the icons are looked for again; the badge, Settings > Account and the board (if open) redrawn</summary>
        void WebStockArrived()
        {
            stockIcons.Clear();
            ShowRanked();                    // the badge (0.2.54's fighter icon) and Settings > Account (fighters used, recent matches)
            if (PageLeaderboard.Visibility == Visibility.Visible && lbRows.Count > 0) ShowLbRows();
        }

        /// <summary>the pictures were (re)made: the board's icons are looked for again</summary>
        void LeaderboardIconsChanged()
        {
            bool missing = stockIcons.Values.Any(v => v == null);
            stockIcons.Clear();
            if (missing && PageLeaderboard.Visibility == Visibility.Visible && lbRows.Count > 0) ShowLbRows();
        }

        /// <summary>the filter's choices: the fighters of the rows read (by name); hidden while the board has none</summary>
        void FillLbFighters()
        {
            if (RankedAccount.On) { FillLbFightersAcc(); return; }
            var all = lbRows.SelectMany(r => r.Fighters).GroupBy(f => f.FilterKey).Select(g => g.First()).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            lbFilling = true;
            LbFighterBox.Items.Clear();
            LbFighterBox.Items.Add(new ComboBoxItem { Content = "All fighters", Tag = null });
            foreach (var f in all) LbFighterBox.Items.Add(new ComboBoxItem { Content = f.Name, Tag = f.FilterKey });
            if (lbFighter != null && !all.Any(f => f.FilterKey == lbFighter)) lbFighter = null;
            LbFighterBox.SelectedIndex = lbFighter == null ? 0 : 1 + all.FindIndex(f => f.FilterKey == lbFighter);
            lbFilling = false;
            LbFighterBox.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LbFilterCol.Width = all.Count > 0 ? new GridLength(200) : new GridLength(0);
            // (0.2.53) "Placed only": once the board has tiers (ranked_leaderboard_fighters)
            LbPlacedBox.Visibility = Ranked.On && lbRows.Any(r => r.Tier != null) ? Visibility.Visible : Visibility.Collapsed;
        }

        void LbPlaced_Changed(object sender, RoutedEventArgs e)
        {
            lbPlacedOnly = LbPlacedBox.IsChecked == true;
            if (lbShown) ShowLbRows();
        }

        void LbFighter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (lbFilling || LbFighterBox.SelectedItem == null) return;
            lbFighter = (string)((ComboBoxItem)LbFighterBox.SelectedItem).Tag;
            if (RankedAccount.On) { LoadLeaderboard(); return; }      // (0.2.55) the server filters (p_fighter)
            ShowLbRows();
        }

        /// <summary>the rows read, through the fighter filter: the players who play that fighter, by its elo when the server gives one per
        /// fighter (ranked again among them), else in the board's order; that fighter's icon first</summary>
        void ShowLbRows()
        {
            if (RankedAccount.On) { ShowLbRowsAcc(); return; }
            var rows = lbRows;
            string fk = lbFighter;
            bool placedOnly = lbPlacedOnly && Ranked.On && LbPlacedBox.Visibility == Visibility.Visible;
            List<LeaderRow> shown;
            bool perFighter = false;
            string fighterName = null;
            if (fk == null) shown = rows.Where(r => !placedOnly || r.Placed).Select(r => r.View(r.Rank, r.Elo, r.Fighters.Take(LeaderRow.MaxFighters).ToList())).ToList();
            else
            {
                var with = rows.Select(r => (row: r, f: r.Fighters.FirstOrDefault(x => x.FilterKey == fk))).Where(x => x.f != null).ToList();
                fighterName = with.Count > 0 ? with[0].f.Name : "";
                // (0.2.53) placed only: that fighter's own tier when the server gives it, else the row's
                if (placedOnly) with = with.Where(x => x.f.Tier != null ? x.f.Tier != Ranked.PlacementTier : x.row.Placed).ToList();
                perFighter = with.Count > 0 && with.All(x => x.f.HasElo);
                if (perFighter) with = with.OrderByDescending(x => x.f.Elo).ThenByDescending(x => x.f.Games).ThenBy(x => x.row.Rank).ToList();
                shown = with.Select((x, i) =>
                {
                    var fl = new List<LeaderFighter> { x.f }.Concat(x.row.Fighters.Where(y => y != x.f)).Take(LeaderRow.MaxFighters).ToList();
                    return x.row.View(perFighter ? i + 1 : x.row.Rank, perFighter ? x.f.Elo : x.row.Elo, fl, Ranked.On ? x.f.Tier : null);
                }).ToList();
            }
            LbList.ItemsSource = shown;
            LbHead.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LbEloHead.Text = perFighter ? "FIGHTER ELO" : "ELO";
            LbEloHead.ToolTip = perFighter ? "Their " + fighterName + " elo" : "The player's elo";
            LbFightersHead.ToolTip = rows.Any(r => r.Fighters.Any(f => f.HasElo)) ? "The fighters played most in ranked, the main first (each one's elo: hover it)" : "The fighter played most in ranked (the main)";
            if (shown.Count == 0)
            {
                if (rows.Count > 0 && placedOnly) ShowLbState(fk != null ? "No placed " + fighterName + " player in this list" : "No player of this list is placed yet",
                                                              "A fighter is placed after " + Ranked.PlacementGames + " rated games. Turn off Placed only to see everyone.", false);
                else if (rows.Count > 0) ShowLbState("No player of this list plays " + fighterName, "Choose All fighters to see everyone.", false);
                else if (lbSearch != "") ShowLbState("No ranked player matches “" + lbSearch + "”", "Try another name, or part of it.", false);
                else if (lbRegionName != null) ShowLbState("No ranked results on " + lbRegionName + " yet", "Choose All regions to see everyone.", false);
                else ShowLbState("No ranked results yet", "Ranked 1v1 has just started. Players appear here after their first ranked match with a result.", false);
            }
            else LbEmpty.Visibility = Visibility.Collapsed;

            int total = rows.Count > 0 ? Math.Max(rows[0].Total, rows.Count) : 0;
            LbStatus.Text = rows.Count == 0 ? "" : total + (total == 1 ? " ranked player" : " ranked players") + (lbRegionName != null ? " on " + lbRegionName : "")
                            + (rows.Count < total ? " · the first " + rows.Count + " shown" : "")
                            + (fk != null ? " · " + shown.Count + " of them play " + fighterName + (perFighter ? ", ranked by their " + fighterName + " elo" : "") : "")
                            + (placedOnly && fk == null ? " · " + shown.Count + " placed" : placedOnly ? " (placed)" : "")
                            + " · updated " + lbReadAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        // ---------------------------------------------------------------- (0.2.55) ranked per account
        /// <summary>every fighter seen on the board this run (the filter's choices stay while the server filters the rows)</summary>
        readonly Dictionary<string, LeaderFighter> lbFighterSeen = new Dictionary<string, LeaderFighter>();
        string lbFighterRead;        // the p_fighter of the rows shown (its label), null: all

        void FillLbFightersAcc()
        {
            foreach (var f in lbRows.SelectMany(r => r.Fighters)) if (!lbFighterSeen.ContainsKey(f.FilterKey)) lbFighterSeen[f.FilterKey] = f;
            var all = lbFighterSeen.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
            lbFilling = true;
            LbFighterBox.Items.Clear();
            LbFighterBox.Items.Add(new ComboBoxItem { Content = "All fighters", Tag = null });
            foreach (var f in all) LbFighterBox.Items.Add(new ComboBoxItem { Content = f.Name, Tag = f.FilterKey });
            if (lbFighter != null && !all.Any(f => f.FilterKey == lbFighter)) lbFighter = null;
            LbFighterBox.SelectedIndex = lbFighter == null ? 0 : 1 + all.FindIndex(f => f.FilterKey == lbFighter);
            lbFilling = false;
            LbFighterBox.Visibility = all.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LbFilterCol.Width = all.Count > 0 ? new GridLength(200) : new GridLength(0);
            LbPlacedBox.Visibility = lbRows.Count > 0 || lbPlacedOnly ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>the rows as the server ranked them (region and fighter filtered there); "Placed only" here</summary>
        void ShowLbRowsAcc()
        {
            var rows = lbRows;
            bool placedOnly = lbPlacedOnly && LbPlacedBox.Visibility == Visibility.Visible;
            string fighterName = lbFighterRead != null ? new LeaderFighter { Label = lbFighterRead, Key = ReplayNames.FighterKeyOfLabel(lbFighterRead) }.Name : null;
            var shown = rows.Where(r => !placedOnly || r.Placed || r.Gm).Select(r => r.View(r.Rank, r.Elo, r.Fighters.Take(LeaderRow.MaxFighters).ToList())).ToList();
            LbList.ItemsSource = shown;
            LbHead.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (shown.Count == 0)
            {
                if (rows.Count > 0 && placedOnly) ShowLbState("No player of this list is placed yet", "A player is placed after " + RankedAccount.PlacementGames + " rated games. Turn off Placed only to see everyone.", false);
                else if (lbSearch != "") ShowLbState("No ranked player matches “" + lbSearch + "”", "Try another name, or part of it.", false);
                else if (fighterName != null) ShowLbState("No ranked player played " + fighterName + (lbRegionName != null ? " on " + lbRegionName : "") + " yet", "Choose All fighters to see everyone.", false);
                else if (lbRegionName != null) ShowLbState("No ranked player from " + lbRegionName + " yet", "Choose All regions to see everyone.", false);
                else ShowLbState("No ranked results yet", "Players appear here after their first rated match.", false);
            }
            else LbEmpty.Visibility = Visibility.Collapsed;
            int total = rows.Count > 0 ? Math.Max(rows[0].Total, rows.Count) : 0;
            LbStatus.Text = rows.Count == 0 ? "" : total + (total == 1 ? " ranked player" : " ranked players") + (lbRegionName != null ? " from " + lbRegionName : "")
                            + (fighterName != null ? " who played " + fighterName : "")
                            + (rows.Count < total ? " · the first " + rows.Count + " shown" : "")
                            + (placedOnly ? " · " + shown.Count + " placed" : "")
                            + " · updated " + lbReadAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
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
