// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.55) Ranked PER ACCOUNT (OpenSkill, Slippi-style; arena-server 20261121_ranked_account.sql): ONE rating per account, 18 tiers
// (Bronze 1 .. Master 3) + Grandmaster + Unranked. What the launcher reads, never writes:
//   public.ranked_tiers       the 20 tiers (tier, position, family, division, min rating, colour, rule, param), publishable key;
//                             read once per run, the built-in copy below when it cannot be read (colours, families, placement count:
//                             the tier of a rating always comes from the server)
//   public.ranked_seasons     the active season (name, placement games, pair limit, Grandmaster top N / min rating), once per run
//   public.my_ranked()        the signed-in player's rating, peak, W-L, tier, region, region / board rank, Grandmaster, fighters used,
//                             last 10 matches; with the player's session (null for anon)
//   public.ranked_board       the board (MainWindow.Leaderboard.cs)
// (0.2.58) Ranked PER FIGHTER again (arena-server 20261123_ranked_fighter.sql): my_ranked() has "ranked_fighters" (each fighter its own
// rating, tier, placement 0-5, peak, region / fighter rank): the card and the badge show the BEST fighter, the fighters' table every one
// with its own rating and tier; ranked_board rows carry their fighter ("fighter", "fighters" with rating and tier). An older server
// (no "ranked_fighters"): everything as 0.2.55.
// Shown rating: one decimal, culture-invariant ("1532.4"). SSBU_RANKED_ACCOUNT=0 (or off) gives back the 0.2.54 launcher (elo per
// fighter, Ranked.cs); SSBU_RANKED_ELO=0 still gives 0.2.52 (it turns this off too).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Arena
{
    /// <summary>one row of public.ranked_tiers</summary>
    public sealed class AccTier
    {
        public string Name, Family, Color, Rule;
        public int Position;
        public int? Division, Param;
        public double? MinRating;
    }

    /// <summary>the active row of public.ranked_seasons (the parts the launcher shows)</summary>
    public sealed class AccSeason
    {
        public int Id;
        public string Name = "Launch";
        public int PlacementN = 5, PairLimit = 3, PairWindowHours = 24, GmTopN = 20;
        public double GmMinRating = 2341.75;
    }

    /// <summary>what an emblem shows: the steel Unranked one, a tier (its colour, 1-3 pips) or Grandmaster (the star, always glowing)</summary>
    public sealed class EmblemSpec
    {
        public enum Kinds { Unranked, Tier, Grandmaster }
        public Kinds Kind { get; private set; }
        public Color Color { get; private set; }
        public int Division { get; private set; }
        public bool Glow { get; private set; }

        public static readonly EmblemSpec Unranked = new EmblemSpec { Kind = Kinds.Unranked, Color = Color.FromRgb(0x8C, 0x9A, 0xAD) };

        /// <summary>from a tier as the server names it (tier, tier_family, tier_division, tier_color; any of them may be null)</summary>
        public static EmblemSpec Of(string tier, string family, int? division, string color)
        {
            var t = RankedAccount.TierOf(tier);
            family = family ?? t?.Family;
            division = division ?? t?.Division;
            color = color ?? t?.Color;
            if (family == null || RankedAccount.IsPlacement(tier, family)) return Unranked;
            bool gm = RankedAccount.IsGrandmaster(tier, family);
            var c = Ranked.ColorFrom(color ?? (gm ? "#FF5A6A" : "#8C9AAD"), Color.FromRgb(0x8C, 0x9A, 0xAD));
            return new EmblemSpec
            {
                Kind = gm ? Kinds.Grandmaster : Kinds.Tier, Color = c, Division = gm ? 0 : Math.Max(0, Math.Min(3, division ?? 0)),
                Glow = gm || RankedAccount.Glows(family)
            };
        }
    }

    /// <summary>one fighter the signed-in player used in ranked (my_ranked().used): every decided game, also those over the pair limit</summary>
    public sealed class AccFighter
    {
        public string Label { get; set; }
        public string Key { get; set; }
        public int Games { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }

        LeaderFighter F => new LeaderFighter { Label = Label, Key = Key };
        public string Name => F.Name;
        public ImageSource Icon => MainWindow.StockIcon(Key);
        public Visibility BadgeVisible => Icon == null ? Visibility.Visible : Visibility.Collapsed;
        public string Badge => F.Badge;
        public string GamesText => Games.ToString(CultureInfo.InvariantCulture);
        public string WinsText => Wins.ToString(CultureInfo.InvariantCulture);
        public string RecordText => Wins.ToString(CultureInfo.InvariantCulture) + "–" + Losses.ToString(CultureInfo.InvariantCulture);
        public string RateText => Games > 0 ? (Wins * 100 + Games / 2) / Games + "%" : "–";

        // (0.2.58) ranked PER FIGHTER (my_ranked().ranked_fighters): this fighter's own rating, tier, placement and peak; null rating:
        // the fighter was used but has no rated game (only matches over the pair limit)
        public string RankKey { get; set; }      // the server's key (= the game's mastery key)
        public double? Rating { get; set; }
        public double? Peak { get; set; }
        public int RatedGames { get; set; }
        public bool Placed { get; set; }
        public bool Gm { get; set; }
        public string Tier { get; set; }
        public string TierFamily { get; set; }
        public int? TierDivision { get; set; }
        public string TierColor { get; set; }
        public string PeakTier { get; set; }
        public int? RegionRank { get; set; }
        public int? FighterRank { get; set; }
        public string RatingText => Rating == null ? "–" : RankedAccount.R(Rating.Value);
        /// <summary>"Gold 2", "Grandmaster", "Unranked 3/5", "–"</summary>
        public string TierText => Rating == null ? "–" : Placed || Gm ? (Tier ?? "") : RankedAccount.PlacementTier + " " + Math.Min(RatedGames, RankedAccount.PlacementGames) + "/" + RankedAccount.PlacementGames;
        public Brush TierBrush => Rating != null && (Placed || Gm) ? Ranked.BrushOf(TierColor ?? RankedAccount.TierOf(Tier)?.Color) : (Brush)Application.Current.Resources["Muted"];
        public EmblemSpec Emblem => Rating != null && (Placed || Gm) ? EmblemSpec.Of(Tier, TierFamily, TierDivision, TierColor) : EmblemSpec.Unranked;
        public string RankTip => Rating == null ? Name + ": no rated game yet"
                                 : Name + ": " + TierText + " · " + RatingText + (Peak != null && Peak.Value > Rating.Value + 0.05 ? " (peak " + RankedAccount.R(Peak.Value) + ")" : "")
                                   + " · " + RatedGames + (RatedGames == 1 ? " rated game" : " rated games") + (FighterRank != null ? " · #" + Ranked.Num(FighterRank.Value) + " " + Name : "");
    }

    /// <summary>one ranked game of the signed-in player (my_ranked().recent)</summary>
    public sealed class AccMatch
    {
        public string Match { get; set; }
        public DateTime? At { get; set; }
        public string Fighter { get; set; }
        public string Opponent { get; set; }
        public string OpponentFighter { get; set; }
        public bool Won { get; set; }
        public double Before { get; set; }
        public double After { get; set; }
        public double Delta { get; set; }
        public bool Counted { get; set; }
        public string TierAfter { get; set; }

        static string NameOf(string label) => string.IsNullOrWhiteSpace(label) ? "?" : new LeaderFighter { Label = label, Key = ReplayNames.FighterKeyOfLabel(label) }.Name;
        public string WhenText => At == null ? "" : At.Value.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
        public string ResultText => Won ? "W" : "L";
        public Brush ResultBrush => (Brush)Application.Current.Resources[Won ? "Good" : "Bad"];
        public ImageSource Icon => MainWindow.StockIcon(ReplayNames.FighterKeyOfLabel(Fighter ?? ""));
        public ImageSource OpponentIcon => MainWindow.StockIcon(ReplayNames.FighterKeyOfLabel(OpponentFighter ?? ""));
        public string VsText => NameOf(Fighter) + "  vs  " + (string.IsNullOrWhiteSpace(Opponent) ? "?" : Opponent) + " (" + NameOf(OpponentFighter) + ")";
        public string RatingText => Counted ? RankedAccount.R(Before) + " → " + RankedAccount.R(After) : "not rated (pair limit)";
        public string DeltaText => Counted ? RankedAccount.Delta(Delta) : "–";
        public Brush DeltaBrush => (Brush)Application.Current.Resources[!Counted || Math.Round(Delta, 1) == 0 ? "Muted" : Delta > 0 ? "Good" : "Bad"];
        public string Tip => !Counted ? "Not rated: at most " + RankedAccount.Season.PairLimit + " rated matches every " + RankedAccount.Season.PairWindowHours
                                        + " h between the same two players. It still counts in your fighters' games."
                           : TierAfter != null ? "After this game: " + TierAfter : null;
        public double RowOpacity => Counted ? 1 : 0.5;
    }

    /// <summary>my_ranked(): the signed-in player's ranked data (one rating for the account)</summary>
    public sealed class AccRanked
    {
        public string Username, SeasonName;
        public int Tag, PlacementGames = 5;
        public double Rating = 1100, Peak = 1100;
        public int Games, Wins, Losses;
        public bool Placed, Gm;
        public string Tier, TierFamily, TierColor, Region;
        public int? TierDivision, RegionRank, BoardRank;
        public List<AccFighter> Used = new List<AccFighter>();
        public List<AccMatch> Recent = new List<AccMatch>();
        /// <summary>(0.2.58) the server rates per fighter (my_ranked().system "fighter"): the numbers above are the BEST fighter's (Best), Used
        /// holds every fighter with its own rating (rated ones first, best first)</summary>
        public bool PerFighter;
        public AccFighter Best;
        public string BestName => Best?.Name;

        public EmblemSpec Emblem => Placed || Gm ? EmblemSpec.Of(Tier, TierFamily, TierDivision, TierColor) : EmblemSpec.Unranked;
        /// <summary>"Gold 2", "Grandmaster", "Unranked 3/5"</summary>
        public string TierText => Placed || Gm ? (Tier ?? "") : RankedAccount.PlacementTier + " " + Math.Min(Games, PlacementGames) + "/" + PlacementGames;
        public Brush TierBrush => Placed || Gm ? Ranked.BrushOf(TierColor ?? RankedAccount.TierOf(Tier)?.Color) : (Brush)Application.Current.Resources["Muted"];
        public string RegionName => RankedAccount.RegionName(Region);
    }

    public static class RankedAccount
    {
        /// <summary>SSBU_RANKED_ACCOUNT=0 (or off): the 0.2.54 launcher (elo per fighter). Off too with SSBU_RANKED_ELO=0 (0.2.52).</summary>
        public static readonly bool On = Ranked.On && !(Environment.GetEnvironmentVariable("SSBU_RANKED_ACCOUNT") is string v && (v.Trim() == "0" || v.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)));

        /// <summary>a copy of public.ranked_tiers (20261121) for when the table cannot be read</summary>
        static readonly List<AccTier> Fallback = BuildFallback();
        static List<AccTier> BuildFallback()
        {
            var l = new List<AccTier> { new AccTier { Name = "Unranked", Position = 0, Family = "Unranked", Color = "#8C9AAD", Rule = "placement", Param = 5 } };
            var fam = new[] { ("Bronze", "#C07A45"), ("Silver", "#BFC9D4"), ("Gold", "#E6B84A"), ("Platinum", "#5FD3C7"), ("Diamond", "#65C7FF"), ("Master", "#B38CFF") };
            double?[] min = { null, 915.43, 1063.72, 1204.87, 1338.88, 1465.75, 1585.48, 1698.07, 1803.52, 1901.83, 1993.00, 2077.03, 2153.92, 2223.67, 2286.28, 2341.75, 2425.00, 2500.00 };
            for (int i = 0; i < 18; i++)
                l.Add(new AccTier { Name = fam[i / 3].Item1 + " " + (i % 3 + 1), Position = i + 1, Family = fam[i / 3].Item1, Division = i % 3 + 1, MinRating = min[i], Color = fam[i / 3].Item2, Rule = "rating" });
            l.Add(new AccTier { Name = "Grandmaster", Position = 19, Family = "Grandmaster", MinRating = 2341.75, Color = "#FF5A6A", Rule = "top", Param = 20 });
            return l;
        }
        static List<AccTier> tiers;
        static AccSeason season;
        static bool seasonTried;
        static readonly object tiersLock = new object();
        public static List<AccTier> Tiers { get { lock (tiersLock) return tiers ?? Fallback; } }
        public static AccSeason Season { get { lock (tiersLock) return season ?? new AccSeason { PlacementN = PlacementGamesOfTiers, GmTopN = Fallback.Last().Param ?? 20 }; } }
        public static bool TiersRead { get { lock (tiersLock) return tiers != null; } }

        /// <summary>public.ranked_tiers and the active public.ranked_seasons row, once per run (again on the next call when the tiers
        /// failed). Off the UI thread.</summary>
        public static void LoadTiers()
        {
            if (!TiersRead)
                try
                {
                    var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(Account.Anon("/rest/v1/ranked_tiers?select=tier,position,family,division,min_rating,color,rule,param&order=position"));
                    var list = (rows ?? new List<Dictionary<string, object>>()).Select(d => new AccTier
                    {
                        Name = Ranked.Str(d, "tier"), Position = Ranked.Int(d, "position") ?? 0, Family = Ranked.Str(d, "family"), Division = Ranked.Int(d, "division"),
                        MinRating = Dbl(d, "min_rating"), Color = Ranked.Str(d, "color"), Rule = Ranked.Str(d, "rule"), Param = Ranked.Int(d, "param")
                    }).Where(t => !string.IsNullOrEmpty(t.Name)).OrderBy(t => t.Position).ToList();
                    foreach (var t in list) if (t.Family == null) t.Family = t.Name;
                    if (list.Count > 0) lock (tiersLock) tiers = list;
                }
                catch (Exception e) { Updater.Log("ranked: ranked_tiers not read (" + e.Message + "), the built-in copy is used"); }
            lock (tiersLock) { if (seasonTried) return; seasonTried = true; }
            try
            {
                var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(Account.Anon("/rest/v1/ranked_seasons?select=*&active=is.true"));
                var d = rows?.FirstOrDefault();
                if (d != null)
                {
                    var s = new AccSeason
                    {
                        Id = Ranked.Int(d, "id") ?? 0, Name = Ranked.Str(d, "name") ?? "Launch", PlacementN = Ranked.Int(d, "placement_n") ?? PlacementGamesOfTiers,
                        PairLimit = Ranked.Int(d, "pair_limit") ?? 3, PairWindowHours = Ranked.Int(d, "pair_window_hours") ?? 24,
                        GmTopN = Ranked.Int(d, "gm_top_n") ?? 20, GmMinRating = Dbl(d, "gm_min_rating") ?? 2341.75
                    };
                    lock (tiersLock) season = s;
                }
            }
            catch (Exception e) { Updater.Log("ranked: ranked_seasons not read (" + e.Message + "), the built-in values are used"); }
        }

        static int PlacementGamesOfTiers => Tiers.FirstOrDefault(t => t.Rule == "placement")?.Param ?? 5;
        public static int PlacementGames => Season.PlacementN;
        public static string PlacementTier => Tiers.FirstOrDefault(t => t.Rule == "placement")?.Name ?? "Unranked";
        public static AccTier TierOf(string name) => name == null ? null : Tiers.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
        static AccTier Top => Tiers.FirstOrDefault(t => t.Rule == "top");
        public static bool IsPlacement(string tier, string family)
        {
            var p = Tiers.FirstOrDefault(t => t.Rule == "placement");
            string pn = p?.Name ?? "Unranked", pf = p?.Family ?? "Unranked";
            return string.Equals(tier, pn, StringComparison.OrdinalIgnoreCase) || string.Equals(family, pf, StringComparison.OrdinalIgnoreCase);
        }
        public static bool IsGrandmaster(string tier, string family)
        {
            string n = Top?.Name ?? "Grandmaster", f = Top?.Family ?? "Grandmaster";
            return string.Equals(tier, n, StringComparison.OrdinalIgnoreCase) || string.Equals(family, f, StringComparison.OrdinalIgnoreCase);
        }
        /// <summary>0.2.53's rule kept: the top families glow (the family of the highest rating tier, Master; Grandmaster always)</summary>
        public static bool Glows(string family)
        {
            if (family == null) return false;
            if (IsGrandmaster(null, family)) return true;
            var best = Tiers.Where(t => t.Rule == "rating").OrderByDescending(t => t.Position).FirstOrDefault();
            return best != null && string.Equals(best.Family, family, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>the region's name as Settings > Online names it ("Europe", "NA East", "South America"); the id itself when not known</summary>
        public static string RegionName(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var r in Regions.Table) if (string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase)) return r.Name;
            return id;
        }

        /// <summary>the tiers in a line, by family: "Unranked until 5 rated games. Bronze < 1204.9 · Silver 1204.9+ · ... · Grandmaster: ..."</summary>
        public static string Legend()
        {
            var fams = Tiers.Where(t => t.Rule == "rating").GroupBy(t => t.Family).Select(g => (fam: g.Key, min: g.Where(t => t.MinRating != null).Select(t => t.MinRating.Value).DefaultIfEmpty(0).Min(),
                                                                                                 first: g.Any(t => t.MinRating == null))).ToList();
            var parts = new List<string>();
            for (int i = 0; i < fams.Count; i++)
                parts.Add(fams[i].first && i + 1 < fams.Count ? fams[i].fam + " < " + R(fams[i + 1].min) : fams[i].fam + " " + R(fams[i].min) + "+");
            var top = Top;
            if (top != null) parts.Add(top.Name + ": top " + (top.Param ?? Season.GmTopN) + " of a region at " + R(top.MinRating ?? Season.GmMinRating) + "+");
            return PlacementTier + " until " + PlacementGames + " rated games. " + string.Join(" · ", parts) + ". Each tier has 3 divisions (3 is the highest).";
        }

        /// <summary>every tier with its exact threshold, one per line (the legend's tooltip)</summary>
        public static string LegendFull() => string.Join("\n", Tiers.Select(t =>
            t.Rule == "placement" ? t.Name + ": fewer than " + (t.Param ?? 5) + " rated games"
            : t.Rule == "top" ? t.Name + ": top " + (t.Param ?? 20) + " of a region, at least " + (t.MinRating ?? 0).ToString("0.00", CultureInfo.InvariantCulture)
            : t.Name + ": " + (t.MinRating == null ? "below the next" : (t.MinRating.Value).ToString("0.00", CultureInfo.InvariantCulture) + "+")));

        /// <summary>my_ranked() with the player's session; null when not signed in or the server gives nothing. Throws when it cannot be read.</summary>
        public static AccRanked ReadMine()
        {
            if (!Account.SignedIn) return null;
            string text = Account.Rest("POST", "/rest/v1/rpc/my_ranked", "{}");
            var d = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(text) as IDictionary<string, object>;
            return d == null ? null : Parse(d);
        }

        public static AccRanked Parse(IDictionary<string, object> d)
        {
            var r = new AccRanked
            {
                Username = Ranked.Str(d, "username"), Tag = Ranked.Int(d, "tag") ?? 0, PlacementGames = Ranked.Int(d, "placement_games") ?? PlacementGames,
                Rating = Dbl(d, "rating") ?? 1100, Games = Ranked.Int(d, "games") ?? 0, Wins = Ranked.Int(d, "wins") ?? 0, Losses = Ranked.Int(d, "losses") ?? 0,
                Tier = Ranked.Str(d, "tier"), TierFamily = Ranked.Str(d, "tier_family"), TierDivision = Ranked.Int(d, "tier_division"), TierColor = Ranked.Str(d, "tier_color"),
                Region = Ranked.Str(d, "region"), RegionRank = Ranked.Int(d, "region_rank"), BoardRank = Ranked.Int(d, "board_rank"), Gm = Ranked.Bool(d, "gm")
            };
            r.Peak = Math.Max(Dbl(d, "peak") ?? r.Rating, r.Rating);
            r.Placed = d.ContainsKey("placed") ? Ranked.Bool(d, "placed") : r.Tier != null && !IsPlacement(r.Tier, r.TierFamily);
            if (d.TryGetValue("season", out object so) && so is IDictionary<string, object> s) r.SeasonName = Ranked.Str(s, "name");
            foreach (var f in List(d, "used"))
            {
                string label = Ranked.Str(f, "fighter");
                if (string.IsNullOrWhiteSpace(label)) continue;
                int g = Ranked.Int(f, "games") ?? 0, w = Ranked.Int(f, "wins") ?? 0;
                r.Used.Add(new AccFighter { Label = label.Trim(), Key = ReplayNames.FighterKeyOfLabel(label), Games = g, Wins = w, Losses = Ranked.Int(f, "losses") ?? Math.Max(0, g - w) });
            }
            r.Used = r.Used.OrderByDescending(f => f.Games).ThenByDescending(f => f.Wins).ToList();
            // (0.2.58) ranked per fighter: each rated fighter's own numbers; the headline = the best fighter (placed first, then rating)
            r.PerFighter = Ranked.Str(d, "system") == "fighter" || d.ContainsKey("ranked_fighters");
            if (r.PerFighter)
            {
                var rated = new List<AccFighter>();
                foreach (var f in List(d, "ranked_fighters"))
                {
                    string label = Ranked.Str(f, "fighter");
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    var u = r.Used.FirstOrDefault(x => string.Equals(x.Label, label.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (u == null)
                    {
                        int g0 = Ranked.Int(f, "games") ?? 0, w0 = Ranked.Int(f, "wins") ?? 0;
                        u = new AccFighter { Label = label.Trim(), Key = ReplayNames.FighterKeyOfLabel(label), Games = g0, Wins = w0, Losses = Ranked.Int(f, "losses") ?? Math.Max(0, g0 - w0) };
                        r.Used.Add(u);
                    }
                    u.RankKey = Ranked.Str(f, "key"); u.Rating = Dbl(f, "rating"); u.Peak = Dbl(f, "peak"); u.RatedGames = Ranked.Int(f, "games") ?? 0;
                    u.Tier = Ranked.Str(f, "tier"); u.TierFamily = Ranked.Str(f, "tier_family"); u.TierDivision = Ranked.Int(f, "tier_division");
                    u.TierColor = Ranked.Str(f, "tier_color"); u.PeakTier = Ranked.Str(f, "peak_tier"); u.Gm = Ranked.Bool(f, "gm");
                    u.Placed = f.ContainsKey("placed") ? Ranked.Bool(f, "placed") : u.Tier != null && !IsPlacement(u.Tier, u.TierFamily);
                    u.RegionRank = Ranked.Int(f, "region_rank"); u.FighterRank = Ranked.Int(f, "fighter_rank");
                    rated.Add(u);
                }
                r.Used = rated.Concat(r.Used.Where(x => !rated.Contains(x)).OrderByDescending(x => x.Games).ThenByDescending(x => x.Wins)).ToList();
                r.Best = rated.FirstOrDefault();
                if (r.Best != null)
                {
                    var b = r.Best;
                    r.Rating = b.Rating ?? r.Rating; r.Peak = Math.Max(b.Peak ?? r.Rating, r.Rating); r.Games = b.RatedGames; r.Wins = b.Wins; r.Losses = b.Losses;
                    r.Tier = b.Tier; r.TierFamily = b.TierFamily; r.TierDivision = b.TierDivision; r.TierColor = b.TierColor; r.Placed = b.Placed; r.Gm = b.Gm;
                    r.RegionRank = b.RegionRank; r.BoardRank = null;      // (my_ranked's board_rank is the MAIN fighter's ladder: the board itself tells)
                    foreach (var f in List(d, "ranked_fighters"))       // (the rated W-L, not the games over the pair limit)
                        if (string.Equals(Ranked.Str(f, "fighter")?.Trim(), b.Label, StringComparison.OrdinalIgnoreCase)) { r.Wins = Ranked.Int(f, "wins") ?? r.Wins; r.Losses = Ranked.Int(f, "losses") ?? r.Losses; }
                }
            }
            foreach (var m in List(d, "recent"))
            {
                DateTime? at = null;
                if (DateTime.TryParse(Ranked.Str(m, "at") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)) at = DateTime.SpecifyKind(t, DateTimeKind.Utc);
                double before = Dbl(m, "rating_before") ?? Dbl(m, "before") ?? 0, after = Dbl(m, "rating_after") ?? Dbl(m, "after") ?? before;
                r.Recent.Add(new AccMatch
                {
                    Match = Ranked.Str(m, "match"), At = at, Fighter = Ranked.Str(m, "fighter"), Opponent = Ranked.Str(m, "opponent"), OpponentFighter = Ranked.Str(m, "opponent_fighter"),
                    Won = Ranked.Bool(m, "won"), Before = before, After = after, Delta = Dbl(m, "rating_delta") ?? after - before,
                    Counted = !m.ContainsKey("counted") || Ranked.Bool(m, "counted"), TierAfter = Ranked.Str(m, "tier_after")
                });
            }
            return r;
        }

        // ---------------------------------------------------------------- small helpers
        /// <summary>a rating as shown: one decimal, culture-invariant ("1532.4")</summary>
        public static string R(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        /// <summary>a rating change: "+12.3", "−46.2", "±0.0"</summary>
        public static string Delta(double v)
        {
            double x = Math.Round(v, 1);
            return x > 0 ? "+" + R(x) : x < 0 ? "−" + R(-x) : "±0.0";
        }
        public static string WinRate(int wins, int losses) => wins + losses > 0 ? (wins * 100 + (wins + losses) / 2) / (wins + losses) + "% wins" : "";

        internal static double? Dbl(IDictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out object v) || v == null) return null;
            try { return v is string s ? (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ? x : (double?)null) : Convert.ToDouble(v, CultureInfo.InvariantCulture); }
            catch { return null; }
        }
        static IEnumerable<IDictionary<string, object>> List(IDictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out object v) || v == null) yield break;
            if (v is string text && text.TrimStart().StartsWith("[", StringComparison.Ordinal)) v = new JavaScriptSerializer().DeserializeObject(text);
            if (v is IEnumerable items && !(v is string))
                foreach (var it in items) if (it is IDictionary<string, object> x) yield return x;
        }
    }
}
