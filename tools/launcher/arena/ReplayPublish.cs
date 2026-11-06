// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: public replays (0.2.41). Nothing leaves the PC unless the player presses "Make public" on one replay: the
// compact file (version 5) goes to the edge function public-replay with the player's own session, which rewrites the opponent's
// name to "Opponent", keeps at most 10 public per player (the oldest goes back to private) and 30 days. The link opens the replay
// in the website's viewer. The PC remembers which of its files are public (<replays>\public.json); the server's list wins.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Arena
{
    public sealed class PublicReplay { public string Id; public DateTime Expires; }

    public static class ReplayPublish
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
        static string Fn => Account.Url + "/functions/v1/public-replay";
        static string ListFile => Path.Combine(ReplayLibrary.Folder, "public.json");
        public const int Keep = 10, Days = 30;

        public static string Link(string id) => Core.Site + "replays?id=" + id;

        static string Call(string method, string query, byte[] body)
        {
            var rq = (HttpWebRequest)WebRequest.Create(Fn + query);
            rq.Method = method; rq.Timeout = 60000;
            rq.Headers["apikey"] = Account.Key;
            rq.Headers["Authorization"] = "Bearer " + Account.AccessToken();
            rq.Accept = "application/json";
            if (body != null)
            {
                rq.ContentType = "application/octet-stream";
                using (var s = rq.GetRequestStream()) s.Write(body, 0, body.Length);
            }
            else if (method == "POST") rq.ContentLength = 0;
            try
            {
                using (var rs = (HttpWebResponse)rq.GetResponse())
                using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
            }
            catch (WebException e) when (e.Response is HttpWebResponse hr)
            {
                string text = "";
                try { using (var sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd(); } catch { }
                string msg = null;
                try { msg = Json.Deserialize<Dictionary<string, object>>(text)["error"] as string; } catch { }
                throw new Exception(msg ?? ("the Arena server answered " + (int)hr.StatusCode));
            }
            catch (WebException e) { throw new Exception("Cannot reach the Arena server: " + e.Message); }
        }

        /// <summary>the PC's list: file name -> its public copy</summary>
        public static Dictionary<string, PublicReplay> Load()
        {
            var d = new Dictionary<string, PublicReplay>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(ListFile)) return d;
                var raw = Json.Deserialize<Dictionary<string, Dictionary<string, object>>>(File.ReadAllText(ListFile));
                foreach (var kv in raw)
                {
                    var e = new PublicReplay { Id = kv.Value["id"] as string, Expires = DateTime.Parse((string)kv.Value["expires"], null, System.Globalization.DateTimeStyles.RoundtripKind) };
                    if (e.Id != null && e.Expires > DateTime.UtcNow) d[kv.Key] = e;
                }
            }
            catch { }
            return d;
        }

        static void Save(Dictionary<string, PublicReplay> d)
        {
            try
            {
                Directory.CreateDirectory(ReplayLibrary.Folder);
                var o = d.ToDictionary(kv => kv.Key, kv => new Dictionary<string, object> { { "id", kv.Value.Id }, { "expires", kv.Value.Expires.ToUniversalTime().ToString("o") } });
                File.WriteAllText(ListFile, Json.Serialize(o));
            }
            catch { }
        }

        /// <summary>the PC's list checked against the server (signed in): ids the server no longer has are dropped (expired, retired, withdrawn)</summary>
        public static Dictionary<string, PublicReplay> Refresh()
        {
            var d = Load();
            if (!Account.SignedIn || d.Count == 0) return d;
            var live = Json.Deserialize<List<Dictionary<string, object>>>(Call("GET", "?action=mine", null));
            var ids = new HashSet<string>(live.Select(r => r["id"] as string));
            foreach (var k in d.Keys.ToList()) if (!ids.Contains(d[k].Id)) d.Remove(k);
            Save(d);
            return d;
        }

        /// <summary>one replay made public; the file is made compact first when it is not yet. Returns its public copy</summary>
        public static PublicReplay Publish(string path)
        {
            if (!Account.SignedIn) throw new Exception("Sign in to make a replay public.");
            ReplayCompact.Compact(path);
            byte[] f = File.ReadAllBytes(path);
            if (f.Length < ReplayState.HeaderSize || BitConverter.ToUInt16(f, 4) != 5) throw new Exception("This replay cannot be made compact, so it cannot be made public.");
            var r = Json.Deserialize<Dictionary<string, object>>(Call("POST", "?action=publish", f));
            var p = new PublicReplay { Id = (string)r["id"], Expires = DateTime.Parse((string)r["expires_at"], null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime() };
            var d = Load(); d[Path.GetFileName(path)] = p; Save(d);
            return p;
        }

        /// <summary>a public replay made private again (the server deletes its copy)</summary>
        public static void Withdraw(string fileName, string id)
        {
            Call("POST", "?action=withdraw&id=" + Uri.EscapeDataString(id), null);
            var d = Load(); d.Remove(fileName); Save(d);
        }
    }
}
