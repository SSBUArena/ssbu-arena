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
