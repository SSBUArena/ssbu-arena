// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.53) Ranked elo per fighter in the window (Ranked.cs reads it): the player card's badge (the tier of the main fighter: the
// hexagon in its colour, "GOLD · 1.184" or "UNRANKED · 3/5", the main's stock icon, the details in the tooltip), Settings > Account
// (every fighter's elo, peak, rated games and tier, the last 10 rated matches) and the leaderboard's "Your position".
// my_ranked() is read at sign-in, when the game closes (and once more a little later: the results writer records the match after
// the game has left), and when Home, Leaderboard or Settings is opened (at most every MyRankedFresh): no polling loop.
// SSBU_RANKED_ELO=0: nothing here runs; the badge stays 0.2.52's steel "UNRANKED".
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace Arena
{
    public partial class MainWindow
    {
        MyRanked myRanked;                      // the last my_ranked() read for myRankedFor (null: none)
        string myRankedFor = "";                // the account it belongs to (MeKey)
        DateTime myRankedAt = DateTime.MinValue, myRankedOk = DateTime.MinValue;
        int myRankedSeq;
        bool myRankedFailed;
        /// <summary>a tab opened again within this time does not read my_ranked() again</summary>
        static readonly TimeSpan MyRankedFresh = TimeSpan.FromSeconds(20);
        /// <summary>after the game closes, one more read this much later (the match's elo is applied when the VPS records it)</summary>
        static readonly TimeSpan MyRankedAfterGame = TimeSpan.FromSeconds(30);
        DispatcherTimer myRankedLater;

        /// <summary>my_ranked() again (force: even when it was read a moment ago). Signed out: the 0.2.52 badge.</summary>
        void LoadMyRanked(bool force)
        {
            if (!Ranked.On) return;
            string key = MeKey;
            if (!Account.SignedIn)
            {
                myRankedSeq++;
                myRanked = null; myRankedFor = ""; myRankedFailed = false; myRankedAt = DateTime.MinValue;
                ShowRanked();
                return;
            }
            if (!force && key == myRankedFor && DateTime.UtcNow - myRankedAt < MyRankedFresh) return;
            int seq = ++myRankedSeq;
            myRankedAt = DateTime.UtcNow;
            if (key != myRankedFor) { myRanked = null; myRankedFor = key; myRankedFailed = false; ShowRanked(); }
            Task.Run(() => { Ranked.LoadTiers(); return Ranked.ReadMine(); }).ContinueWith(t =>
            {
                if (seq != myRankedSeq || MeKey != key) return;      // a newer read, or another account
                if (t.Status != TaskStatus.RanToCompletion)
                {
                    Updater.Log("ranked: my_ranked not read (" + (t.Exception?.GetBaseException().Message ?? "failed") + ")");
                    myRankedFailed = true;                            // (the last data read stays on screen)
                }
                else { myRanked = t.Result; myRankedFailed = false; myRankedOk = DateTime.UtcNow; }
                ShowRanked();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>(GameExited) read now, and once more a little later</summary>
        void RankedAfterGame()
        {
            if (!Ranked.On || !Account.SignedIn) return;
            LoadMyRanked(true);
            if (myRankedLater == null)
            {
                myRankedLater = new DispatcherTimer { Interval = MyRankedAfterGame };
                myRankedLater.Tick += (s, e) => { myRankedLater.Stop(); if (game == null) LoadMyRanked(true); };
            }
            myRankedLater.Stop(); myRankedLater.Start();
        }

        void ShowRanked()
        {
            ShowBadge();
            ShowRankedAccount();
            if (PageLeaderboard.Visibility == Visibility.Visible) ShowLbMe();
        }

        // ---------------------------------------------------------------- the player card's badge
        Brush badgeFill0, badgeStroke0, badgeIn0, gemStroke0, gemFill0, gemCore0;

        /// <summary>0.2.52's steel hexagon with the empty diamond, "UNRANKED"</summary>
        void PlainBadge()
        {
            if (badgeFill0 == null) return;          // never changed
            RankHex.Fill = badgeFill0; RankHex.Stroke = badgeStroke0; RankHex.Effect = null;
            RankHexIn.Stroke = badgeIn0; RankGem.Stroke = gemStroke0; RankGem.Fill = gemFill0; RankGemCore.Fill = gemCore0;
        }

        static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
        static Brush Frozen(Brush b) { b.Freeze(); return b; }

        /// <summary>the hexagon in a tier's colour: the same shape, the diamond filled (a tier reached)</summary>
        void TierBadge(Color c, bool glow)
        {
            if (badgeFill0 == null)
            {
                badgeFill0 = RankHex.Fill; badgeStroke0 = RankHex.Stroke; badgeIn0 = RankHexIn.Stroke;
                gemStroke0 = RankGem.Stroke; gemFill0 = RankGem.Fill; gemCore0 = RankGemCore.Fill;
            }
            var deep = Color.FromRgb(0x0D, 0x17, 0x24);
            RankHex.Fill = Frozen(new LinearGradientBrush(Mix(deep, c, 0.28), deep, 90));
            RankHex.Stroke = Frozen(new LinearGradientBrush(Mix(c, Colors.White, 0.35), Mix(c, deep, 0.45), 90));
            RankHex.Effect = glow ? new DropShadowEffect { Color = c, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.65 } : null;
            RankHexIn.Stroke = Frozen(new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B)));
            RankGem.Stroke = Frozen(new SolidColorBrush(c));
            RankGem.Fill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)));
            RankGemCore.Fill = Frozen(new SolidColorBrush(Mix(c, Colors.White, 0.25)));
        }

        void ShowBadge()
        {
            var main = Ranked.On && Account.SignedIn ? myRanked?.MainFighter : null;
            if (main == null)
            {
                PlainBadge();
                RankText.Text = Spaced("UNRANKED");
                RankFighter.Visibility = Visibility.Collapsed;
                RankBadge.ToolTip = !Ranked.On || !Account.SignedIn ? null
                                  : myRanked == null ? (myRankedFailed ? "Your ranked data can't be read right now." : null)
                                  : "No ranked match yet. Each fighter gets its own elo, and its tier after " + myRanked.PlacementGames + " rated games.";
                return;
            }
            var steel = Color.FromRgb(0x8C, 0x9A, 0xAD);
            Color c = Ranked.ColorFrom(main.Color ?? Ranked.ColorOf(main.Tier) ?? "#8C9AAD", steel);
            RankText.Inlines.Clear();
            if (main.Placed)
            {
                var top = Ranked.Tiers.FirstOrDefault(t => t.Rule == "top");
                bool glow = top != null && string.Equals(main.Tier, top.Name, StringComparison.OrdinalIgnoreCase)
                            || Ranked.Tiers.Where(t => t.Rule == "elo").OrderByDescending(t => t.Position).FirstOrDefault()?.Name == main.Tier;
                TierBadge(c, glow);
                RankText.Inlines.Add(new Run(Spaced((main.Tier ?? "").ToUpperInvariant())) { Foreground = Frozen(new SolidColorBrush(Mix(c, Colors.White, 0.15))) });
                RankText.Inlines.Add(new Run("  ·  " + Ranked.Num(main.Elo)));
            }
            else
            {
                PlainBadge();                       // placement: still the steel badge, with its progress
                RankText.Inlines.Add(new Run(Spaced((main.Tier ?? Ranked.PlacementTier).ToUpperInvariant())));
                RankText.Inlines.Add(new Run("  ·  " + main.Games + "/" + main.PlacementGames) { Foreground = Res("Muted") });
            }
            var icon = main.Icon;
            RankFighterIcon.ImageSource = icon;
            RankFighterBadge.Text = main.Badge;
            RankFighterBadgeBox.Visibility = icon == null ? Visibility.Visible : Visibility.Collapsed;
            RankFighter.Visibility = Visibility.Visible;
            RankBadge.ToolTip = BadgeTip(myRanked, main);
        }

        /// <summary>the badge's tooltip: the main's tier and elo, the board position with W-L and win %, the other fighters' elo</summary>
        UIElement BadgeTip(MyRanked r, MyFighter main)
        {
            var p = new StackPanel { MinWidth = 220 };
            TextBlock Line(string text, Brush fg = null, double size = 14, FontWeight? weight = null, double top = 0)
            {
                var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, Margin = new Thickness(0, top, 0, 0) };
                if (fg != null) t.Foreground = fg;
                p.Children.Add(t);
                return t;
            }
            Line(main.Placed ? main.Tier + " · " + Ranked.Num(main.Elo) + " elo with " + main.Name + " (main)"
                             : Ranked.PlacementTier + " with " + main.Name + " (main): " + main.Games + " of " + main.PlacementGames + " placement games · " + Ranked.Num(main.Elo) + " elo",
                 main.TierBrush, 15, FontWeights.SemiBold);
            if (main.Peak > main.Elo || main.FighterRank != null)
                Line(string.Join(" · ", new[] { main.Peak > main.Elo ? "Peak " + Ranked.Num(main.Peak) : null, main.RankOfFighter }.Where(x => !string.IsNullOrEmpty(x))), Res("Muted"));
            if (r.HasBoard)
            {
                int n = r.BoardWins + r.BoardLosses;
                Line("Board: #" + Ranked.Num(r.BoardRank) + " · " + r.BoardWins + "–" + r.BoardLosses + (n > 0 ? " · " + (r.BoardWins * 100 + n / 2) / n + "% wins" : ""), null, 14, null, 6);
            }
            else Line("Not on the board yet", Res("Muted"), 14, null, 6);
            var others = r.Fighters.Where(f => f != main && f.Games > 0).ToList();
            if (others.Count > 0)
            {
                Line("Other fighters", Res("Muted"), 13, FontWeights.SemiBold, 6);
                foreach (var f in others.Take(8))
                    Line(f.Name + ": " + Ranked.Num(f.Elo) + " · " + f.TierText + " (" + f.Games + (f.Games == 1 ? " rated game)" : " rated games)"), f.TierBrush);
                if (others.Count > 8) Line("and " + (others.Count - 8) + " more in Settings > Account", Res("Muted"), 13);
            }
            if (myRankedFailed) Line("Can't be read right now: shown as of " + myRankedOk.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), Res("Warn"), 12.5, null, 6);
            return p;
        }

        // ---------------------------------------------------------------- Settings > Account
        void ShowRankedAccount()
        {
            bool on = Ranked.On && Account.SignedIn;
            RankedBox.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) return;
            RankedLegend.Text = "Tiers: " + Ranked.Legend();
            var r = myRanked;
            if (r == null)
            {
                RankedSummary.Text = myRankedFailed ? "Your ranked data can't be read right now." : "Reading your ranked data…";
                RankedNote.Text = myRankedFailed ? "The Arena server can't be reached or didn't answer: it is read again when you open this page." : "";
                RankedTables.Visibility = Visibility.Collapsed;
                return;
            }
            var main = r.MainFighter;
            if (main == null)
            {
                RankedSummary.Text = "No ranked match yet.";
                RankedNote.Text = "Each fighter you play in ranked has its own elo; it gets a tier after " + r.PlacementGames + " rated games.";
                RankedTables.Visibility = Visibility.Collapsed;
                return;
            }
            int n = r.BoardWins + r.BoardLosses;
            RankedSummary.Text = (r.HasBoard ? "#" + Ranked.Num(r.BoardRank) + " on the board" : "Not on the board yet")
                               + "   ·   " + (main.Placed ? main.Tier + " " + Ranked.Num(main.Elo) : main.TierText) + " with " + main.Name
                               + (r.HasBoard ? "   ·   " + r.BoardWins + "–" + r.BoardLosses + (n > 0 ? " (" + (r.BoardWins * 100 + n / 2) / n + "% wins)" : "") : "");
            RankedNote.Text = "Each fighter has its own elo, and its own tier after " + r.PlacementGames + " rated games. Your main (the fighter with the most rated games) is the one on the board and on your badge."
                            + (myRankedFailed ? " Can't be read right now: shown as of " + myRankedOk.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + "." : "");
            RankedTables.Visibility = Visibility.Visible;
            RankedFighters.ItemsSource = r.Fighters.ToList();
            RankedRecent.ItemsSource = r.Recent.Take(10).ToList();
            RankedNoRecent.Visibility = r.Recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
