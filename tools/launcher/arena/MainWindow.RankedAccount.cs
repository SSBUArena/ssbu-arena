// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.55) Ranked per account in the window (RankedAccount.cs reads it): the player card's badge (the emblem of the account's tier with
// its pips or the Grandmaster star, "GOLD 2" over "1532.4 · 14–9", the details in the tooltip), Settings > Account (rating, tier,
// placement n/5, W-L, peak, region and its rank, Grandmaster, the fighters used, the last 10 games) and the leaderboard's
// "Your position". my_ranked() is read when 0.2.53 read it (sign-in, game closed, Home / Leaderboard / Settings opened, at most every
// MyRankedFresh). SSBU_RANKED_ACCOUNT=0: nothing here runs, MainWindow.Ranked.cs is 0.2.54's.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Arena
{
    public partial class MainWindow
    {
        AccRanked myAcc;                        // the last my_ranked() read for myAccFor (null: none)
        string myAccFor = "";
        DateTime myAccAt = DateTime.MinValue, myAccOk = DateTime.MinValue;
        int myAccSeq;
        bool myAccFailed;

        /// <summary>my_ranked() again (force: even when it was read a moment ago). Signed out: the steel UNRANKED badge.</summary>
        void LoadMyAcc(bool force)
        {
            string key = MeKey;
            if (!Account.SignedIn)
            {
                myAccSeq++;
                myAcc = null; myAccFor = ""; myAccFailed = false; myAccAt = DateTime.MinValue;
                ShowRanked();
                return;
            }
            if (!force && key == myAccFor && DateTime.UtcNow - myAccAt < MyRankedFresh) return;
            int seq = ++myAccSeq;
            myAccAt = DateTime.UtcNow;
            if (key != myAccFor) { myAcc = null; myAccFor = key; myAccFailed = false; ShowRanked(); }
            Task.Run(() => { RankedAccount.LoadTiers(); return RankedAccount.ReadMine(); }).ContinueWith(t =>
            {
                if (seq != myAccSeq || MeKey != key) return;
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    Updater.Log("ranked: my_ranked not read (" + (t.Exception?.GetBaseException().Message ?? "failed") + ")");
                    myAccFailed = true;
                }
                else { myAcc = t.Result; myAccFailed = false; myAccOk = DateTime.UtcNow; }
                ShowRanked();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        string AsOf => "Can't be read right now: shown as of " + myAccOk.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- the player card's badge
        void ShowAccBadge()
        {
            RankBadgeCanvas.Visibility = Visibility.Collapsed;      // (0.2.53's named paths stay for SSBU_RANKED_ACCOUNT=0)
            RankEmblemAcc.Visibility = Visibility.Visible;
            RankFighter.Visibility = Visibility.Collapsed;
            var r = Account.SignedIn ? myAcc : null;
            RankText.Inlines.Clear();
            if (r == null)
            {
                RankEmblemAcc.Spec = EmblemSpec.Unranked;
                RankText.Inlines.Add(new Run(Spaced("UNRANKED")));
                RankBadge.ToolTip = !Account.SignedIn ? null : myAccFailed ? "Your ranked data can't be read right now." : null;
                return;
            }
            var spec = r.Emblem;
            RankEmblemAcc.Spec = spec;
            bool tier = spec.Kind != EmblemSpec.Kinds.Unranked;
            var tierBrush = tier ? Frozen(new SolidColorBrush(Mix(spec.Color, Colors.White, 0.15))) : null;
            if (tier) RankText.Inlines.Add(new Run(Spaced((r.Tier ?? "").ToUpperInvariant())) { Foreground = tierBrush });
            else
            {
                RankText.Inlines.Add(new Run(Spaced(RankedAccount.PlacementTier.ToUpperInvariant())));
                RankText.Inlines.Add(new Run("  " + Math.Min(r.Games, r.PlacementGames) + "/" + r.PlacementGames) { Foreground = Res("Muted") });
            }
            RankText.Inlines.Add(new LineBreak());
            RankText.Inlines.Add(new Run(RankedAccount.R(r.Rating)) { FontSize = 14, FontWeight = FontWeights.SemiBold });
            if (r.Games > 0) RankText.Inlines.Add(new Run("  ·  " + r.Wins + "–" + r.Losses) { FontSize = 13, FontWeight = FontWeights.Normal, Foreground = Res("Muted") });
            RankBadge.ToolTip = AccBadgeTip(r);
        }

        /// <summary>the badge's tooltip: tier and rating, peak, board and region rank, W-L, Grandmaster, the most used fighters</summary>
        UIElement AccBadgeTip(AccRanked r)
        {
            var p = new StackPanel { MinWidth = 220 };
            TextBlock Line(string text, Brush fg = null, double size = 14, FontWeight? weight = null, double top = 0)
            {
                var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Margin = new Thickness(0, top, 0, 0) };
                if (fg != null) t.Foreground = fg;
                p.Children.Add(t);
                return t;
            }
            Line(r.TierText + " · " + RankedAccount.R(r.Rating) + " rating", r.TierBrush, 15, FontWeights.SemiBold);
            if (r.Games == 0) Line("No rated match yet: your tier comes after " + r.PlacementGames + " rated games.", Res("Muted"));
            else if (!r.Placed && !r.Gm) Line((r.PlacementGames - Math.Min(r.Games, r.PlacementGames)) + " more rated games to get your tier", Res("Muted"));
            if (r.Peak > r.Rating + 0.05) Line("Peak " + RankedAccount.R(r.Peak), Res("Muted"));
            var pos = new List<string>();
            if (r.BoardRank != null) pos.Add("#" + Ranked.Num(r.BoardRank.Value) + " on the board");
            if (r.RegionRank != null && r.RegionName != null) pos.Add("#" + Ranked.Num(r.RegionRank.Value) + " in " + r.RegionName);
            if (pos.Count > 0) Line(string.Join(" · ", pos), null, 14, null, 6);
            if (r.Games > 0) Line(r.Wins + "–" + r.Losses + " · " + RankedAccount.WinRate(r.Wins, r.Losses) + " · " + r.Games + (r.Games == 1 ? " rated game" : " rated games"), null, 14, null, pos.Count > 0 ? 0 : 6);
            if (r.Gm) Line("Grandmaster: top " + RankedAccount.Season.GmTopN + (r.RegionName != null ? " of " + r.RegionName : " of your region"), Ranked.BrushOf("#FF5A6A"), 14, FontWeights.SemiBold, 4);
            if (r.Used.Count > 0)
            {
                Line("Most played", Res("Muted"), 13, FontWeights.SemiBold, 6);
                foreach (var f in r.Used.Take(3)) Line(f.Name + ": " + f.Games + (f.Games == 1 ? " game, " : " games, ") + f.Wins + (f.Wins == 1 ? " win" : " wins") + " (" + f.RateText + ")");
            }
            if (myAccFailed) Line(AsOf, Res("Warn"), 12.5, null, 6);
            return p;
        }

        // ---------------------------------------------------------------- Settings > Account
        void ShowAccPanel()
        {
            RankedBox.Visibility = Visibility.Collapsed;
            bool on = Account.SignedIn;
            RankedAccBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            AccLegend.Text = "Tiers: " + RankedAccount.Legend();
            AccLegend.ToolTip = RankedAccount.LegendFull();
            var r = myAcc;
            AccStats.Children.Clear();
            AccPlaceBar.Children.Clear();
            if (r == null)
            {
                AccEmblem.Spec = EmblemSpec.Unranked;
                AccTier.Text = "RANKED"; AccTier.Foreground = Res("Muted");
                AccRating.Text = "";
                AccSub.Text = myAccFailed ? "Your ranked data can't be read right now." : "Reading your ranked data…";
                AccPlace.Visibility = Visibility.Collapsed;
                AccNote.Text = myAccFailed ? "The Arena server can't be reached or didn't answer: it is read again when you open this page." : "";
                AccTables.Visibility = Visibility.Collapsed;
                return;
            }
            var spec = r.Emblem;
            AccEmblem.Spec = spec;
            AccTier.Text = r.Placed || r.Gm ? (r.Tier ?? "").ToUpperInvariant() : RankedAccount.PlacementTier.ToUpperInvariant();
            AccTier.Foreground = r.TierBrush;
            AccRating.Text = RankedAccount.R(r.Rating);
            AccSub.Text = (r.SeasonName ?? RankedAccount.Season.Name) + " season" + (r.Gm ? "   ·   Grandmaster: top " + RankedAccount.Season.GmTopN + (r.RegionName != null ? " of " + r.RegionName : "") : "")
                        + (myAccFailed ? "   ·   " + AsOf : "");
            // placement: n of 5 as 5 segments
            bool placing = !r.Placed && !r.Gm;
            AccPlace.Visibility = placing ? Visibility.Visible : Visibility.Collapsed;
            if (placing)
            {
                int n = Math.Min(r.Games, r.PlacementGames);
                for (int i = 0; i < r.PlacementGames; i++)
                    AccPlaceBar.Children.Add(new Border { Width = 34, Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 5, 0), Background = i < n ? Res("Accent") : Ranked.BrushOf("#233041") });
                AccPlaceText.Text = "Placement " + n + "/" + r.PlacementGames + (n == 0 ? ": play rated matches to get your tier" : ": " + (r.PlacementGames - n) + " more to get your tier (your rating already moves)");
            }
            Stat("RATED GAMES", r.Games.ToString(CultureInfo.InvariantCulture), r.Games > 0 ? r.Wins + "–" + r.Losses : null);
            Stat("WIN RATE", r.Games > 0 ? (r.Wins * 100 + (r.Wins + r.Losses) / 2) / Math.Max(1, r.Wins + r.Losses) + "%" : "–", null);
            Stat("PEAK", RankedAccount.R(r.Peak), null);
            Stat("REGION", r.RegionName ?? "–", r.RegionRank != null ? "#" + Ranked.Num(r.RegionRank.Value) + " in the region" : null);
            Stat("BOARD", r.BoardRank != null ? "#" + Ranked.Num(r.BoardRank.Value) : "–", r.BoardRank == null ? "not on it yet" : null);
            Stat("GRANDMASTER", r.Gm ? "Yes" : "No", r.Gm ? "top " + RankedAccount.Season.GmTopN + " of the region" : "top " + RankedAccount.Season.GmTopN + " · " + RankedAccount.R(RankedAccount.Season.GmMinRating) + "+",
                 r.Gm ? Ranked.BrushOf("#FF5A6A") : null);
            if (AccStats.Children[AccStats.Children.Count - 1] is FrameworkElement gmTile)
                gmTile.ToolTip = "Grandmaster: the top " + RankedAccount.Season.GmTopN + " placed players of a region by rating, with at least " + RankedAccount.R(RankedAccount.Season.GmMinRating);
            AccNote.Text = "One rating for your account, whatever fighter you play: it moves after every rated game. It starts at 1100.0 and climbs as the system gets "
                         + "surer of your level. At most " + RankedAccount.Season.PairLimit + " rated games every " + RankedAccount.Season.PairWindowHours + " h against the same player.";
            AccTables.Visibility = Visibility.Visible;
            AccFighters.ItemsSource = r.Used.ToList();
            AccNoFighters.Visibility = r.Used.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            AccFightersHead.Visibility = r.Used.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            AccRecent.ItemsSource = r.Recent.Take(10).ToList();
            AccNoRecent.Visibility = r.Recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>one tile of the stats row: label, value, an optional small line under it</summary>
        void Stat(string label, string value, string sub, Brush valueBrush = null)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = label, FontFamily = (FontFamily)FindResource("Condensed"), FontWeight = FontWeights.Bold, FontSize = 13, Foreground = Res("Muted") });
            var v = new TextBlock { Text = value, FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 0) };
            if (valueBrush != null) v.Foreground = valueBrush;
            sp.Children.Add(v);
            if (sub != null) sp.Children.Add(new TextBlock { Text = sub, FontSize = 12.5, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
            AccStats.Children.Add(new Border
            {
                Child = sp, Width = 126, MinHeight = 66, Padding = new Thickness(12, 8, 10, 8), Margin = new Thickness(0, 0, 8, 8), CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1), Background = Ranked.BrushOf("#0F1620"), BorderBrush = Ranked.BrushOf("#233041")
            });
        }

        // ---------------------------------------------------------------- the leaderboard's "Your position"
        /// <summary>All regions: my_ranked()'s board rank, tier and rating; a region: my_ranked()'s region rank when it is that region, else the
        /// player's row of the board read</summary>
        void ShowLbMeAcc(Profile me, LeaderRow mine, string regionName)
        {
            var r = me != null && myAccFor == me.Username + "#" + me.Tag ? myAcc : null;
            string notInList = lbRows.Any(x => x.IsMe) ? "" : "   ·   not in this list";
            if (me == null) { LbMeText.Text = "Sign in to see where you are on the board."; return; }
            int? rank = r == null ? null : regionName == null ? r.BoardRank : r.RegionName == regionName ? r.RegionRank : null;
            if (r != null && r.Games > 0 && rank != null && lbFighter == null)
            {
                LbMeText.Text = me.Display + "   #" + Ranked.Num(rank.Value) + "   ·   " + r.TierText + "  " + RankedAccount.R(r.Rating)
                              + "   ·   " + r.Wins + "–" + r.Losses + (r.Games > 0 ? " (" + RankedAccount.WinRate(r.Wins, r.Losses) + ")" : "") + notInList;
                return;
            }
            LbMeText.Text = mine == null ? me.Display + ": not on the board yet" + (regionName != null ? " on " + regionName : "") + ". Your first rated match puts you on it."
                          : me.Display + "   #" + mine.Rank + "   ·   " + mine.AccTierText + "  " + mine.RatingText + "   ·   " + mine.Record + (mine.RateText != "" ? " (" + mine.RateText + ")" : "") + notInList;
        }
    }
}
