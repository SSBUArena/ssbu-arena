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
// most played first (elo per fighter, not there yet): then each icon shows its own elo, and the fighter filter ranks by it. The
// fighter filter appears once the board has rows: it works on the rows read (the first LbLimit), on main_fighter / fighters.
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
        /// <summary>the first icon of the row, a little bigger: the main, or the fighter the filter chose</summary>
        public bool Lead { get; set; }
        public double Size => Lead ? 34 : 28;
        public double BadgeFont => Lead ? 13 : 11.5;
        public LeaderFighter Copy(bool lead) { var c = (LeaderFighter)MemberwiseClone(); c.Lead = lead; return c; }
        public string EloText => HasElo ? Elo.ToString(CultureInfo.InvariantCulture) : "";
        public Visibility EloVisible => HasElo ? Visibility.Visible : Visibility.Collapsed;
        public string Tip => Name + (Main ? " (main)" : "") + (HasElo ? ": " + Elo + " elo" : "")
                             + (Games > 0 ? (HasElo ? ", " : ": ") + Games + (Games == 1 ? " ranked match" : " ranked matches") + (Wins + Losses > 0 ? " (" + Wins + "–" + Losses + ")" : "") : "");
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
        public string EloText => Elo.ToString(CultureInfo.InvariantCulture);
        public string RowTip => Name + TagText + ": " + Games + (Games == 1 ? " ranked match" : " ranked matches") + (Fighters.Count > 0 ? ", mostly " + Fighters[0].Name : "");
        /// <summary>(0.2.52) the fighters the player plays, the main first, at most MaxFighters</summary>
        public List<LeaderFighter> Fighters { get; set; } = new List<LeaderFighter>();
        public const int MaxFighters = 3;
        public Visibility NoFighterVisible => Fighters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        /// <summary>a copy for the list (the filter shows another rank and elo; a recycled row needs a new object to show them)</summary>
        public LeaderRow View(int rank, int elo, List<LeaderFighter> fighters)
        {
            var v = (LeaderRow)MemberwiseClone();
            v.Rank = rank; v.Elo = elo; v.Fighters = fighters;
            return v;
        }
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
            var row = new LeaderRow { Rank = I("rank"), Name = name, Tag = I("tag"), Avatar = S("avatar"), Elo = I("elo"), Wins = I("wins"), Losses = I("losses"),
                                      Games = I("games"), Total = I("total"), Fighter = string.IsNullOrWhiteSpace(S("main_fighter")) ? "-" : S("main_fighter") };
            row.Fighters = FightersOf(d.TryGetValue("fighters", out object fl) ? fl : null, S("main_fighter"));
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
            if (all.Count > 0) all[0].Main = all[0].Lead = true;
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
                    lbRows = new List<LeaderRow>();
                    FillLbFighters();
                    LbHead.Visibility = Visibility.Collapsed;
                    ShowLbState("The leaderboard can't be loaded right now",
                                offline ? "The Arena server can't be reached: check your internet connection, then try again." : "The Arena server didn't answer as expected. Try again in a minute.", true);
                    LbStatus.Text = "";
                    LbMeBox.Visibility = Visibility.Collapsed;
                    return;
                }
                var (rows, mine) = t.Result;
                foreach (var r in rows) { r.IsMe = r.Is(me); r.Icon = AvatarImage(r.Avatar, 76); }
                lbRows = rows; lbSearch = search; lbRegionName = regionName;
                lbReadAt = DateTime.UtcNow; lbFor = meKey;
                FillLbFighters();
                ShowLbRows();

                // your position
                LbMeBox.Visibility = Visibility.Visible;
                LbSignIn.Visibility = me == null ? Visibility.Visible : Visibility.Collapsed;
                LbMeText.Text = me == null ? "Sign in to see where you are on the board."
                              : mine == null ? me.Display + ": not on the board yet" + (regionName != null ? " on " + regionName : "") + ". Your first ranked match with a result puts you on it."
                              : me.Display + "   #" + mine.Rank + "   ·   " + mine.Elo + " elo   ·   " + mine.Record + (mine.RateText != "" ? " (" + mine.RateText + ")" : "")
                                + (rows.Any(r => r.IsMe) ? "" : "   ·   not in this list");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        // ---------------------------------------------------------------- (0.2.52) fighters: icons and the filter
        List<LeaderRow> lbRows = new List<LeaderRow>();
        string lbSearch = "", lbRegionName;
        string lbFighter;            // the filter's fighter (LeaderFighter.FilterKey), null: all
        bool lbFilling;

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
        }

        void LbFighter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (lbFilling || LbFighterBox.SelectedItem == null) return;
            lbFighter = (string)((ComboBoxItem)LbFighterBox.SelectedItem).Tag;
            ShowLbRows();
        }

        /// <summary>the rows read, through the fighter filter: the players who play that fighter, by its elo when the server gives one per
        /// fighter (ranked again among them), else in the board's order; that fighter's icon first</summary>
        void ShowLbRows()
        {
            var rows = lbRows;
            string fk = lbFighter;
            List<LeaderRow> shown;
            bool perFighter = false;
            string fighterName = null;
            if (fk == null) shown = rows.Select(r => r.View(r.Rank, r.Elo, r.Fighters.Take(LeaderRow.MaxFighters).ToList())).ToList();
            else
            {
                var with = rows.Select(r => (row: r, f: r.Fighters.FirstOrDefault(x => x.FilterKey == fk))).Where(x => x.f != null).ToList();
                fighterName = with.Count > 0 ? with[0].f.Name : "";
                perFighter = with.Count > 0 && with.All(x => x.f.HasElo);
                if (perFighter) with = with.OrderByDescending(x => x.f.Elo).ThenByDescending(x => x.f.Games).ThenBy(x => x.row.Rank).ToList();
                shown = with.Select((x, i) =>
                {
                    var fl = new List<LeaderFighter> { x.f.Copy(true) }.Concat(x.row.Fighters.Where(y => y != x.f).Select(y => y.Copy(false))).Take(LeaderRow.MaxFighters).ToList();
                    return x.row.View(perFighter ? i + 1 : x.row.Rank, perFighter ? x.f.Elo : x.row.Elo, fl);
                }).ToList();
            }
            LbList.ItemsSource = shown;
            LbHead.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            LbEloHead.Text = perFighter ? "FIGHTER ELO" : "ELO";
            LbEloHead.ToolTip = perFighter ? "Their " + fighterName + " elo" : "The player's elo";
            LbFightersHead.ToolTip = rows.Any(r => r.Fighters.Any(f => f.HasElo)) ? "The fighters played most in ranked, the main first, each with its own elo" : "The fighter played most in ranked (the main)";
            if (shown.Count == 0)
            {
                if (rows.Count > 0) ShowLbState("No player of this list plays " + fighterName, "Choose All fighters to see everyone.", false);
                else if (lbSearch != "") ShowLbState("No ranked player matches “" + lbSearch + "”", "Try another name, or part of it.", false);
                else if (lbRegionName != null) ShowLbState("No ranked results on " + lbRegionName + " yet", "Choose All regions to see everyone.", false);
                else ShowLbState("No ranked results yet", "Ranked 1v1 has just started. Players appear here after their first ranked match with a result.", false);
            }
            else LbEmpty.Visibility = Visibility.Collapsed;

            int total = rows.Count > 0 ? Math.Max(rows[0].Total, rows.Count) : 0;
            LbStatus.Text = rows.Count == 0 ? "" : total + (total == 1 ? " ranked player" : " ranked players") + (lbRegionName != null ? " on " + lbRegionName : "")
                            + (rows.Count < total ? " · the first " + rows.Count + " shown" : "")
                            + (fk != null ? " · " + shown.Count + " of them play " + fighterName + (perFighter ? ", ranked by their " + fighterName + " elo" : "") : "")
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
