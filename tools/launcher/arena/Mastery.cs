// Mastery.cs - (22/11) OpenSmash mastery points in SSBU Arena: the launcher's part. Arena's refereed matches earn points too (the accounts
// database computes them: arena-server tools/net/supabase/20261122_arena_mastery.sql, region "arena"); the game shows them on its Online
// character select and at a match's end (runtime/online_mastery.c). Right before PLAY (online) the launcher reads the signed-in player's
// public.my_opensmash_mastery() (per fighter: points summed over every region + the largest veteran bonus) into build\cache\mastery.txt,
// one "<fighter key> <total>" per line, and the game gets the path in SSBU_MASTERY_FILE. Nothing else: the game updates its copy from
// each match's verdict. SSBU_MASTERY_LAUNCHER=0: no read, no file (the game then shows "-" until a match tells a fighter's total).
// (0.2.58) ranked PER FIGHTER: the same file also gets "rank <key> <rating> <games> <peak> <tier pos> <emblem> <peak pos> <peak emblem>"
// per rated fighter (my_ranked().ranked_fighters): the Online select's Elo / Peak Elo rows and emblems follow the fighter under the cursor.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace Arena
{
    public static class Mastery
    {
        public static string File => Path.Combine(Core.Build, "cache", "mastery.txt");
        static bool On => Environment.GetEnvironmentVariable("SSBU_MASTERY_LAUNCHER") != "0";

        /// <summary>(22/11, SSBU_RANK_LABEL=0: off) the Online select panel's row label "Elo" -> "Elo - Gold 2" ("Elo - Placement"): the
        /// menu step (tools/ui/prepare_menu_classic.py) leaves the message "Elo" + 32 spaces in characters\arena_elo_text's
        /// msg_melee.msbt; here that 35-character slot is written again in place (the file's sizes never change). The game reads it at
        /// its start: one label for the Elo and the Peak rows, so both show the CURRENT tier, as of this PLAY.</summary>
        public static void RankLabel(string tier)
        {
            try
            {
                const int Slot = 35;
                string file = Path.Combine(Core.Build, "characters", "arena_elo_text", "ui", "message", "msg_melee.msbt");
                if (!System.IO.File.Exists(file)) return;
                string want = Environment.GetEnvironmentVariable("SSBU_RANK_LABEL") == "0" || string.IsNullOrWhiteSpace(tier) ? "Elo" : "Elo - " + tier.Trim();
                if (want.Length > Slot) want = want.Substring(0, Slot);
                byte[] d = System.IO.File.ReadAllBytes(file), elo = Encoding.Unicode.GetBytes("Elo");
                for (int i = 0; i + 2 * Slot + 2 <= d.Length; i += 2)
                {
                    // the slot: 36 UTF-16 units from "Elo", the last a NUL, only NULs after the first NUL (the text ends at its NUL:
                    // trailing spaces would count in the label's width, which the game shrinks to fit)
                    bool at = true;
                    for (int k = 0; k < elo.Length && at; k++) at = d[i + k] == elo[k];
                    if (!at || d[i + 2 * Slot] != 0 || d[i + 2 * Slot + 1] != 0) continue;
                    bool slot = true, ended = false;
                    for (int k = 0; k <= Slot && slot; k++) { bool nul = d[i + 2 * k] == 0 && d[i + 2 * k + 1] == 0; if (ended && !nul) slot = false; if (nul) ended = true; }
                    if (!slot) continue;
                    byte[] w = Encoding.Unicode.GetBytes(want);
                    for (int k = 0; k < 2 * Slot; k++) d[i + k] = k < w.Length ? w[k] : (byte)0;
                    string tmp = file + ".part";
                    System.IO.File.WriteAllBytes(tmp, d);
                    System.IO.File.Copy(tmp, file, true);
                    System.IO.File.Delete(tmp);
                    Updater.Log("rank label: \"" + want + "\"");
                    return;
                }
                Updater.Log("rank label: no slot in " + file + " (an older menu step)");
            }
            catch (Exception e) { Updater.Log("rank label: not written (" + e.GetBaseException().Message + ")"); }
        }

        /// <summary>my_opensmash_mastery() into File (true: written). Never throws: a failed read leaves no file (the game shows "-").</summary>
        public static bool TryWrite()
        {
            try { System.IO.File.Delete(File); } catch { }
            if (!On || !Account.SignedIn) return false;
            try
            {
                string text = Account.Rest("POST", "/rest/v1/rpc/my_opensmash_mastery", "{}");
                var rows = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(text) as object[];
                var sb = new StringBuilder("# my_opensmash_mastery (the launcher, " + DateTime.UtcNow.ToString("u") + "): fighter key, total\n");
                int n = 0;
                foreach (var o in rows ?? new object[0])
                {
                    if (!(o is IDictionary<string, object> d) || !d.TryGetValue("fighter", out object f) || !(f is string key) || !Regex.IsMatch(key, "^[a-z0-9_]{1,23}$")) continue;
                    long total = 0;
                    if (d.TryGetValue("total", out object t) && t != null) long.TryParse(Convert.ToString(t, System.Globalization.CultureInfo.InvariantCulture), out total);
                    if (total < 0) continue;
                    sb.Append(key).Append(' ').Append(Math.Min(total, uint.MaxValue)).Append('\n');
                    n++;
                }
                try      // (22/11) the account's highest rating for the Online select's "Peak Elo" row (runtime/online_mastery.c "peak <n>")
                {
                    RankedAccount.LoadTiers();
                    var acc = RankedAccount.ReadMine();
                    var inv = System.Globalization.CultureInfo.InvariantCulture;
                    if (acc != null && acc.Peak > 0) sb.Append("peak ").Append(((long)Math.Round(acc.Peak)).ToString(inv)).Append('\n');
                    // (22/11) the tier for the Online select panel (runtime/online_mastery.c): "tier <position> <emblem> <name>", emblem = the
                    // family's place among public.ranked_tiers' families (0 Unranked .. the last, Grandmaster: tools/ui/rank_emblems.py)
                    if (acc != null)
                    {
                        var tiers = RankedAccount.Tiers.OrderBy(t => t.Position).ToList();
                        var fams = tiers.Select(t => t.Family ?? t.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        int Emb(AccTier t) => t == null ? 0 : Math.Max(0, Math.Min(7, fams.FindIndex(f => string.Equals(f, t.Family ?? t.Name, StringComparison.OrdinalIgnoreCase))));
                        // (0.2.58) ranked PER FIGHTER (arena-server 20261123_ranked_fighter.sql): one line per rated fighter for the Online select
                        // (runtime/online_mastery.c "rank"): "rank <key> <rating> <rated games> <peak> <tier position> <emblem> <peak tier position> <peak emblem>"
                        foreach (var f in acc.PerFighter ? acc.Used.Where(x => x.Rating != null && (x.RankKey ?? x.Key) != null) : Enumerable.Empty<AccFighter>())
                        {
                            string fk = f.RankKey ?? f.Key;      // (the server's key = the game's mastery key: "ice_climber", "element", "ptrainer")
                            if (!Regex.IsMatch(fk, "^[a-z0-9_]{1,23}$")) continue;
                            AccTier ft = f.Placed || f.Gm ? tiers.FirstOrDefault(t => string.Equals(t.Name, f.Tier, StringComparison.OrdinalIgnoreCase)) : tiers.FirstOrDefault();
                            AccTier pt = f.Placed || f.Gm ? tiers.FirstOrDefault(t => string.Equals(t.Name, f.PeakTier, StringComparison.OrdinalIgnoreCase)) ?? ft : tiers.FirstOrDefault();
                            sb.Append("rank ").Append(fk).Append(' ').Append(((long)Math.Round(f.Rating.Value)).ToString(inv)).Append(' ').Append(f.RatedGames.ToString(inv))
                              .Append(' ').Append(((long)Math.Round(Math.Max(f.Peak ?? f.Rating.Value, f.Rating.Value))).ToString(inv))
                              .Append(' ').Append((ft?.Position ?? 0).ToString(inv)).Append(' ').Append(Emb(ft).ToString(inv))
                              .Append(' ').Append((pt?.Position ?? 0).ToString(inv)).Append(' ').Append(Emb(pt).ToString(inv)).Append('\n');
                        }
                        AccTier cur = acc.Placed || acc.Gm ? tiers.FirstOrDefault(t => string.Equals(t.Name, acc.Tier, StringComparison.OrdinalIgnoreCase)) : tiers.FirstOrDefault();
                        string curName = acc.TierText.Replace(RankedAccount.PlacementTier + " ", "Placement ");
                        sb.Append("tier ").Append((cur?.Position ?? 0).ToString(inv)).Append(' ').Append(Emb(cur).ToString(inv)).Append(' ').Append(curName).Append('\n');
                        AccTier pk = acc.Gm ? cur : tiers.Where(t => t.Rule == "rating" && t.MinRating != null && t.MinRating.Value <= acc.Peak).OrderByDescending(t => t.Position).FirstOrDefault()
                                                   ?? tiers.FirstOrDefault(t => t.Rule == "rating");
                        if (acc.Placed || acc.Gm) sb.Append("peaktier ").Append((pk?.Position ?? 0).ToString(inv)).Append(' ').Append(Emb(pk).ToString(inv)).Append(' ').Append(pk?.Name ?? "").Append('\n');
                    }
                    // (0.2.58) per fighter: the rows' label is ONE static message for every fighter under the cursor, so it says only "Elo";
                    // each fighter's tier is its emblem (online_mastery.c). Per account (an older server): "Elo - Gold 2" as 0.2.56.
                    RankLabel(acc == null || acc.PerFighter ? null : acc.Placed || acc.Gm ? acc.Tier : "Placement");
                    try      // (the emblem textures' colours: tools/ui/rank_emblems.py --tiers, run by the menu step)
                    {
                        string tf = Path.Combine(Core.Build, "cache", "ranked_tiers.json");
                        var trows = RankedAccount.Tiers.Select(t => new Dictionary<string, object> { { "tier", t.Name }, { "position", t.Position }, { "family", t.Family }, { "color", t.Color } }).ToList();
                        System.IO.File.WriteAllText(tf, new JavaScriptSerializer().Serialize(trows), new UTF8Encoding(false));
                    }
                    catch { }
                }
                catch (Exception e) { Updater.Log("mastery: no peak rating (" + e.GetBaseException().Message + ")"); }
                Directory.CreateDirectory(Path.GetDirectoryName(File));
                string tmp = File + ".part";
                System.IO.File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                System.IO.File.Move(tmp, File);
                Updater.Log("mastery: " + n + " fighter(s) for the game");
                return true;
            }
            catch (Exception e) { Updater.Log("mastery: not read (" + e.GetBaseException().Message + ")"); return false; }
        }
    }
}
