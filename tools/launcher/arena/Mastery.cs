// Mastery.cs - (22/11) OpenSmash mastery points in SSBU Arena: the launcher's part. Arena's refereed matches earn points too (the accounts
// database computes them: arena-server tools/net/supabase/20261122_arena_mastery.sql, region "arena"); the game shows them on its Online
// character select and at a match's end (runtime/online_mastery.c). Right before PLAY (online) the launcher reads the signed-in player's
// public.my_opensmash_mastery() (per fighter: points summed over every region + the largest veteran bonus) into build\cache\mastery.txt,
// one "<fighter key> <total>" per line, and the game gets the path in SSBU_MASTERY_FILE. Nothing else: the game updates its copy from
// each match's verdict. SSBU_MASTERY_LAUNCHER=0: no read, no file (the game then shows "-" until a match tells a fighter's total).
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
                        AccTier cur = acc.Placed || acc.Gm ? tiers.FirstOrDefault(t => string.Equals(t.Name, acc.Tier, StringComparison.OrdinalIgnoreCase)) : tiers.FirstOrDefault();
                        string curName = acc.TierText.Replace(RankedAccount.PlacementTier + " ", "Placement ");
                        sb.Append("tier ").Append((cur?.Position ?? 0).ToString(inv)).Append(' ').Append(Emb(cur).ToString(inv)).Append(' ').Append(curName).Append('\n');
                        AccTier pk = acc.Gm ? cur : tiers.Where(t => t.Rule == "rating" && t.MinRating != null && t.MinRating.Value <= acc.Peak).OrderByDescending(t => t.Position).FirstOrDefault()
                                                   ?? tiers.FirstOrDefault(t => t.Rule == "rating");
                        if (acc.Placed || acc.Gm) sb.Append("peaktier ").Append((pk?.Position ?? 0).ToString(inv)).Append(' ').Append(Emb(pk).ToString(inv)).Append(' ').Append(pk?.Name ?? "").Append('\n');
                    }
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
