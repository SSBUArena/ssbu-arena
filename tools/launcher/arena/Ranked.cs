// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.53) Ranked elo PER FIGHTER (arena-server tools/net/supabase/20261120_ranked_elo.sql): what the launcher reads, never writes.
//   public.rank_tiers                     the tiers (name, position, min elo, colour, rule, param), publishable key; read once per run,
//                                         a copy of the 20261120 values when it cannot be read (the colours and the placement count only:
//                                         the tier of a rating always comes from the server)
//   public.my_ranked()                    the signed-in player's fighters (elo, peak, W-L, rated games, tier), main, board row, last 10
//                                         rated matches; with the player's session (null for anon)
//   public.ranked_leaderboard_fighters    the board (MainWindow.Leaderboard.cs)
// SSBU_RANKED_ELO=0 (or off) gives back 0.2.52 exactly: the fixed UNRANKED badge, ranked_leaderboard, no ranked block in Settings.
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
    /// <summary>one row of public.rank_tiers</summary>
    public sealed class RankTier
    {
        public string Name, Color, Rule;
        public int Position;
        public int? MinElo, Param;
    }

    /// <summary>one fighter rating of the signed-in player (my_ranked().fighters)</summary>
    public sealed class MyFighter
    {
        public string Label { get; set; }
        public string Key { get; set; }
        public int Elo { get; set; }
        public int Peak { get; set; }
        public int Wins { get; set; }
        public int Losses { get; set; }
        public int Games { get; set; }
        public string Tier { get; set; }
        public string Color { get; set; }
        public int? FighterRank { get; set; }
        public int PlacementGames { get; set; }
        public bool Main { get; set; }

        /// <summary>past the placement games: the server's tier decides, the count when it gave none</summary>
        public bool Placed => Tier != null ? !string.Equals(Tier, Ranked.PlacementTier, StringComparison.OrdinalIgnoreCase) : Games >= PlacementGames;
        public string Name => new LeaderFighter { Label = Label, Key = Key }.Name;
        public ImageSource Icon => MainWindow.StockIcon(Key);
        public Visibility BadgeVisible => Icon == null ? Visibility.Visible : Visibility.Collapsed;
        public string Badge => new LeaderFighter { Label = Label, Key = Key }.Badge;
        public Brush TierBrush => Ranked.BrushOf(Color ?? Ranked.ColorOf(Tier));
        /// <summary>"Gold", "Unranked 3/5"</summary>
        public string TierText => Placed ? (Tier ?? "") : (Tier ?? "Unranked") + " " + Games + "/" + PlacementGames;
        public string EloText => Ranked.Num(Elo);
        public string PeakText => Ranked.Num(Peak);
        public string GamesText => Games.ToString(CultureInfo.InvariantCulture);
        public string RecordText => Wins.ToString(CultureInfo.InvariantCulture) + "–" + Losses.ToString(CultureInfo.InvariantCulture);
        public string RankOfFighter => FighterRank != null ? "#" + Ranked.Num(FighterRank.Value) + " among " + Name + " players" : "";
        public string MainText => Main ? "MAIN" : "";
        public Visibility MainVisible => Main ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>one rated match of the signed-in player (my_ranked().recent)</summary>
    public sealed class MyMatch
    {
        public string Match { get; set; }
        public DateTime? At { get; set; }
        public string Fighter { get; set; }
        public string Opponent { get; set; }
        public string OpponentFighter { get; set; }
        public bool Won { get; set; }
        public int Before { get; set; }
        public int After { get; set; }
        public int Delta { get; set; }
        public bool Counted { get; set; }

        static string NameOf(string label) => string.IsNullOrWhiteSpace(label) ? "?" : new LeaderFighter { Label = label, Key = ReplayNames.FighterKeyOfLabel(label) }.Name;
        public string WhenText => At == null ? "" : At.Value.ToLocalTime().ToString("dd MMM HH:mm", CultureInfo.InvariantCulture);
        public string ResultText => Won ? "WIN" : "LOSS";
        public Brush ResultBrush => (Brush)Application.Current.Resources[Won ? "Good" : "Bad"];
        public string VsText => NameOf(Fighter) + "  vs  " + (string.IsNullOrWhiteSpace(Opponent) ? "?" : Opponent) + " (" + NameOf(OpponentFighter) + ")";
        public string EloText => Counted ? Ranked.Num(Before) + " → " + Ranked.Num(After) : Ranked.Num(Before);
        public string DeltaText => !Counted ? "±0" : Delta > 0 ? "+" + Delta.ToString(CultureInfo.InvariantCulture) : Delta < 0 ? "−" + (-Delta).ToString(CultureInfo.InvariantCulture) : "±0";
        public Brush DeltaBrush => (Brush)Application.Current.Resources[!Counted || Delta == 0 ? "Muted" : Delta > 0 ? "Good" : "Bad"];
        public string Tip => !Counted ? "Not rated: at most 3 rated matches a day between the same two players" : "";
        public double RowOpacity => Counted ? 1 : 0.6;
    }

    /// <summary>my_ranked(): the signed-in player's ranked data</summary>
    public sealed class MyRanked
    {
        public string Username;
        public int Tag, PlacementGames = 5;
        public List<MyFighter> Fighters = new List<MyFighter>();
        public string Main;
        public bool HasBoard;
        public int BoardRank, BoardElo, BoardWins, BoardLosses, BoardGames;
        public string BoardTier, BoardMain;
        public List<MyMatch> Recent = new List<MyMatch>();

        /// <summary>the fighter the badge shows: my_ranked().main, else the one with most rated games; null: no ranked game yet</summary>
        public MyFighter MainFighter
        {
            get
            {
                var m = Main != null ? Fighters.FirstOrDefault(f => string.Equals(f.Label, Main, StringComparison.OrdinalIgnoreCase)) : null;
                return m ?? Fighters.Where(f => f.Games > 0).OrderByDescending(f => f.Games).ThenByDescending(f => f.Elo).FirstOrDefault();
            }
        }
    }

    public static class Ranked
    {
        /// <summary>SSBU_RANKED_ELO=0 (or off): 0.2.52's launcher (fixed UNRANKED badge, ranked_leaderboard, no ranked block)</summary>
        public static readonly bool On = !(Environment.GetEnvironmentVariable("SSBU_RANKED_ELO") is string v && (v.Trim() == "0" || v.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)));

        /// <summary>a copy of public.rank_tiers (20261120) for when the table cannot be read: colours and the placement count only</summary>
        static readonly List<RankTier> Fallback = new List<RankTier>
        {
            new RankTier { Name = "Unranked", Position = 0, MinElo = null, Color = "#8C9AAD", Rule = "placement", Param = 5 },
            new RankTier { Name = "Bronze",   Position = 1, MinElo = 0,    Color = "#C07A45", Rule = "elo" },
            new RankTier { Name = "Silver",   Position = 2, MinElo = 900,  Color = "#BFC9D4", Rule = "elo" },
            new RankTier { Name = "Gold",     Position = 3, MinElo = 1100, Color = "#E6B84A", Rule = "elo" },
            new RankTier { Name = "Platinum", Position = 4, MinElo = 1300, Color = "#5FD3C7", Rule = "elo" },
            new RankTier { Name = "Diamond",  Position = 5, MinElo = 1500, Color = "#65C7FF", Rule = "elo" },
            new RankTier { Name = "Master",   Position = 6, MinElo = 1700, Color = "#B38CFF", Rule = "elo" },
            new RankTier { Name = "Elite",    Position = 7, MinElo = 1700, Color = "#FF5A6A", Rule = "top", Param = 100 },
        };
        static List<RankTier> tiers;
        static readonly object tiersLock = new object();
        public static List<RankTier> Tiers { get { lock (tiersLock) return tiers ?? Fallback; } }
        public static bool TiersRead { get { lock (tiersLock) return tiers != null; } }

        /// <summary>public.rank_tiers, once per run (again on the next read when it failed). Off the UI thread.</summary>
        public static void LoadTiers()
        {
            if (TiersRead) return;
            try
            {
                var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(Account.Anon("/rest/v1/rank_tiers?select=tier,position,min_elo,color,rule,param&order=position"));
                var list = (rows ?? new List<Dictionary<string, object>>()).Select(d => new RankTier
                {
                    Name = Str(d, "tier"), Position = Int(d, "position") ?? 0, MinElo = Int(d, "min_elo"), Color = Str(d, "color"), Rule = Str(d, "rule"), Param = Int(d, "param")
                }).Where(t => !string.IsNullOrEmpty(t.Name)).OrderBy(t => t.Position).ToList();
                if (list.Count > 0) lock (tiersLock) tiers = list;
            }
            catch (Exception e) { Updater.Log("ranked: rank_tiers not read (" + e.Message + "), the built-in copy is used"); }
        }

        public static int PlacementGames => Tiers.FirstOrDefault(t => t.Rule == "placement")?.Param ?? 5;
        public static string ColorOf(string tier) => Tiers.FirstOrDefault(t => string.Equals(t.Name, tier, StringComparison.OrdinalIgnoreCase))?.Color;
        public static string PlacementTier => Tiers.FirstOrDefault(t => t.Rule == "placement")?.Name ?? "Unranked";

        /// <summary>the tiers in a line, from the table: "Bronze < 900 · Silver 900 · ... · Elite: top 100 of a fighter at 1.700+"</summary>
        public static string Legend()
        {
            var parts = new List<string>();
            var elo = Tiers.Where(t => t.Rule == "elo" && t.MinElo != null).OrderBy(t => t.MinElo).ToList();
            for (int i = 0; i < elo.Count; i++)
                parts.Add(elo[i].Name + " " + (i == 0 && elo.Count > 1 ? "< " + Num(elo[1].MinElo.Value) : Num(elo[i].MinElo.Value) + "+"));
            foreach (var t in Tiers.Where(t => t.Rule == "top")) parts.Add(t.Name + ": top " + (t.Param ?? 100) + " of a fighter at " + Num(t.MinElo ?? 0) + "+");
            var p = Tiers.FirstOrDefault(t => t.Rule == "placement");
            return (p != null ? p.Name + " until " + (p.Param ?? 5) + " rated games. " : "") + string.Join(" · ", parts);
        }

        /// <summary>my_ranked() with the player's session; null when not signed in or the server gives nothing. Throws when it cannot be read.</summary>
        public static MyRanked ReadMine()
        {
            if (!Account.SignedIn) return null;
            string text = Account.Rest("POST", "/rest/v1/rpc/my_ranked", "{}");
            var d = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(text) as IDictionary<string, object>;
            return d == null ? null : Parse(d);
        }

        public static MyRanked Parse(IDictionary<string, object> d)
        {
            var r = new MyRanked
            {
                Username = Str(d, "username"), Tag = Int(d, "tag") ?? 0, PlacementGames = Int(d, "placement_games") ?? PlacementGames, Main = Str(d, "main")
            };
            foreach (var f in List(d, "fighters"))
            {
                string label = Str(f, "fighter");
                if (string.IsNullOrWhiteSpace(label)) continue;
                r.Fighters.Add(new MyFighter
                {
                    Label = label.Trim(), Key = ReplayNames.FighterKeyOfLabel(label), Elo = Int(f, "elo") ?? 1000, Peak = Int(f, "peak") ?? Int(f, "elo") ?? 1000,
                    Wins = Int(f, "wins") ?? 0, Losses = Int(f, "losses") ?? 0, Games = Int(f, "games") ?? 0, Tier = Str(f, "tier"), Color = Str(f, "color"),
                    FighterRank = Int(f, "fighter_rank"), PlacementGames = r.PlacementGames
                });
            }
            var main = r.MainFighter;
            if (main != null) main.Main = true;
            if (d.TryGetValue("board", out object bo) && bo is IDictionary<string, object> b)
            {
                r.HasBoard = true;
                r.BoardRank = Int(b, "rank") ?? 0; r.BoardElo = Int(b, "elo") ?? 0; r.BoardWins = Int(b, "wins") ?? 0; r.BoardLosses = Int(b, "losses") ?? 0;
                r.BoardGames = Int(b, "games") ?? 0; r.BoardTier = Str(b, "tier"); r.BoardMain = Str(b, "main_fighter");
            }
            foreach (var m in List(d, "recent"))
            {
                DateTime? at = null;
                if (DateTime.TryParse(Str(m, "at") ?? "", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t)) at = DateTime.SpecifyKind(t, DateTimeKind.Utc);
                r.Recent.Add(new MyMatch
                {
                    Match = Str(m, "match") ?? Convert.ToString(m.TryGetValue("match", out object id) ? id : null, CultureInfo.InvariantCulture), At = at,
                    Fighter = Str(m, "fighter"), Opponent = Str(m, "opponent"), OpponentFighter = Str(m, "opponent_fighter"), Won = Bool(m, "won"),
                    Before = Int(m, "before") ?? 0, After = Int(m, "after") ?? 0, Delta = Int(m, "delta") ?? 0, Counted = !m.ContainsKey("counted") || Bool(m, "counted")
                });
            }
            return r;
        }

        // ---------------------------------------------------------------- small helpers
        /// <summary>a big number as the launcher writes them (the setup's "{0:N0}"): grouped by this PC's language, "1.184" / "1,184"</summary>
        public static string Num(int n) => n.ToString("N0", CultureInfo.CurrentCulture);

        static readonly Dictionary<string, Brush> brushes = new Dictionary<string, Brush>();
        public static Brush BrushOf(string hex)
        {
            if (string.IsNullOrEmpty(hex)) hex = "#8C9AAD";
            if (brushes.TryGetValue(hex, out var b)) return b;
            try { b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); }
            catch { b = (Brush)Application.Current.Resources["Muted"]; }
            return brushes[hex] = b;
        }
        public static Color ColorFrom(string hex, Color fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
        }

        internal static string Str(IDictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out object v) && v != null ? (v as string ?? Convert.ToString(v, CultureInfo.InvariantCulture)) : null;
        internal static int? Int(IDictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out object v) || v == null) return null;
            try { return v is string s ? (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : (int?)null) : Convert.ToInt32(v, CultureInfo.InvariantCulture); }
            catch { return null; }
        }
        internal static bool Bool(IDictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out object v) && (v is bool b ? b : v is string s && s.Equals("true", StringComparison.OrdinalIgnoreCase));
        static IEnumerable<IDictionary<string, object>> List(IDictionary<string, object> d, string k)
        {
            if (d == null || !d.TryGetValue(k, out object v) || v == null) yield break;
            if (v is string text && text.TrimStart().StartsWith("[", StringComparison.Ordinal)) v = new JavaScriptSerializer().DeserializeObject(text);
            if (v is IEnumerable items && !(v is string))
                foreach (var it in items) if (it is IDictionary<string, object> x) yield return x;
        }
    }
}
