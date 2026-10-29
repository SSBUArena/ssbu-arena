// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: replays on the Arena server (Supabase project ssbu-arena; schema in the website repo,
// supabase/migrations/20261029140000_replay_state_and_visibility.sql). One row of public.replays per online match holds the
// lobby's input recording (.ssbr, written by the VPS through the edge function replay-ingest, with both players' account ids)
// and one player's game recording (.ssbv, uploaded here with the player's OWN session: never a service key). Rows are
// readable by the match's players, or by every signed-in player once one of them made the replay public.
//   upload:   storage object replays/state/<my uid>/<yyyymmdd-hhmmss>-<match id>.ssbv, then rpc replay_attach_state
//   list:     /rest/v1/replays (the rows the database lets this player read)
//   download: /storage/v1/object/authenticated/replays/<path> into <Saves>\replays\cloud
//   public:   rpc replay_set_public (players only)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace Arena
{
    public sealed class CloudReplay
    {
        public string Id, P1Name, P2Name, P1Uid, P2Uid, SsbvUid, P1Chara, P2Chara, Stage, Path, SsbvPath;
        public long MatchId; public DateTime PlayedAt; public int Frames, Flags; public int? Winner; public bool IsPublic;
        public bool Mine => Account.SignedIn && new[] { P1Uid, P2Uid, SsbvUid }.Any(u => string.Equals(u, Account.Me.Id, StringComparison.OrdinalIgnoreCase));
        public bool HasState => !string.IsNullOrEmpty(SsbvPath);
        public string LocalCopy => System.IO.Path.Combine(ReplayLibrary.Folder, "cloud", Id + ".ssbv");
    }

    public static class ReplayCloud
    {
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 1 << 24 };
        static string UploadedList => Path.Combine(ReplayLibrary.Folder, "uploaded.txt");

        static string S(Dictionary<string, object> r, string k) => r.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

        /// <summary>the replays this player may read: its own matches and the public ones (newest first)</summary>
        /// <param name="player">(0.2.11) only the matches of a player whose name holds this text (the server's search: every public replay of
        /// that player, not only the newest rows)</param>
        public static List<CloudReplay> List(int limit = 200, string player = null)
        {
            string q = "/rest/v1/replays?select=id,match_id,played_at,p1_name,p2_name,p1_uid,p2_uid,ssbv_uid,p1_chara,p2_chara,stage,frames,flags,winner,is_public,path,ssbv_path&order=played_at.desc&limit=" + limit;
            string p = new string((player ?? "").Where(ch => char.IsLetterOrDigit(ch) || ch == ' ' || ch == '_' || ch == '-' || ch == '.').ToArray()).Trim();
            if (p.Length > 0) q += "&or=" + Uri.EscapeDataString("(p1_name.ilike.*" + p + "*,p2_name.ilike.*" + p + "*)");
            var rows = Json.Deserialize<List<Dictionary<string, object>>>(Account.Rest("GET", q, null));
            return rows.Select(r =>
            {
                DateTime.TryParse(S(r, "played_at"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t);
                return new CloudReplay
                {
                    Id = S(r, "id"), MatchId = Convert.ToInt64(r["match_id"]), PlayedAt = DateTime.SpecifyKind(t, DateTimeKind.Utc),
                    P1Name = S(r, "p1_name"), P2Name = S(r, "p2_name"), P1Uid = S(r, "p1_uid"), P2Uid = S(r, "p2_uid"), SsbvUid = S(r, "ssbv_uid"),
                    P1Chara = S(r, "p1_chara"), P2Chara = S(r, "p2_chara"), Stage = S(r, "stage"), Frames = Convert.ToInt32(r["frames"]),
                    Flags = Convert.ToInt32(r["flags"]), Winner = r["winner"] == null ? (int?)null : Convert.ToInt32(r["winner"]),
                    IsPublic = r["is_public"] is bool b && b, Path = S(r, "path"), SsbvPath = S(r, "ssbv_path"),
                };
            }).ToList();
        }

        /// <summary>the local files already on the server: file name -> row id</summary>
        public static Dictionary<string, string> Uploaded()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try { foreach (var l in File.ReadAllLines(UploadedList)) { var p = l.Split('\t'); if (p.Length == 2) d[p[0]] = p[1]; } } catch { }
            return d;
        }

        /// <summary>one local online recording to the server; returns the row id (the match's row)</summary>
        public static string Upload(ReplayState h)
        {
            if (!Account.SignedIn) throw new Exception("Sign in to upload replays.");
            if (h.Kind == 0 || h.MatchId == 0) throw new Exception("Only online matches go to the Arena server.");
            if (h.Codec == 0) { ReplayState.Pack(h.Path); h = ReplayState.ReadHeader(h.Path); }
            byte[] data = File.ReadAllBytes(h.Path);
            if (data.Length > 4 * 1024 * 1024) throw new Exception("This replay is too large for the server (4 MB).");
            string name = "state/" + Account.Me.Id + "/" + DateTimeOffset.FromUnixTimeSeconds(h.UnixTime).UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
                          + "-" + h.MatchId.ToString("x8") + ".ssbv";
            var rq = (HttpWebRequest)WebRequest.Create(Account.Url + "/storage/v1/object/replays/" + name);
            rq.Method = "POST"; rq.Timeout = 60000; rq.ContentType = "application/octet-stream";
            rq.Headers["apikey"] = Account.Key; rq.Headers["Authorization"] = "Bearer " + Account.AccessToken();
            using (var s = rq.GetRequestStream()) s.Write(data, 0, data.Length);
            try { using (rq.GetResponse()) { } }
            catch (WebException e) when (e.Response is HttpWebResponse hr && (int)hr.StatusCode != 409 && (int)hr.StatusCode != 400)
            { throw new Exception("The replay server answered " + (int)hr.StatusCode); }
            catch (WebException e) when (e.Response is HttpWebResponse hr2)
            {   // 409 / 400 "Duplicate": already there from an earlier try: attach it anyway
                string body = ""; try { using (var sr = new StreamReader(hr2.GetResponseStream())) body = sr.ReadToEnd(); } catch { }
                if (!body.Contains("Duplicate") && !body.Contains("already exists")) throw new Exception("The replay server answered " + (int)hr2.StatusCode + ": " + body);
            }
            var f = h.Fighters;
            var meta = new Dictionary<string, object>
            {
                { "local", h.LocalPlayer }, { "frames", h.Frames }, { "stocks", h.Stocks }, { "rule_kind", 1 }, { "game_build", h.GameBuild }, { "flags", h.Flags },
                { "build", h.Build }, { "stage", (h.UiStage & 0xffffffffffUL).ToString("x16") }, { "stage_form", h.StageForm },
                { "p1_name", f.Count > 0 ? f[0].Name : "" }, { "p2_name", f.Count > 1 ? f[1].Name : "" },
                { "p1_chara", f.Count > 0 ? f[0].UiChara.ToString("x16") : "" }, { "p2_chara", f.Count > 1 ? f[1].UiChara.ToString("x16") : "" },
                { "p1_colour", f.Count > 0 ? f[0].Colour : 0 }, { "p2_colour", f.Count > 1 ? f[1].Colour : 0 },
            };
            if (h.Winner == 0 || h.Winner == 1) meta["winner"] = h.Winner;
            var args = new Dictionary<string, object>
            {
                { "p_match_id", (long)h.MatchId }, { "p_played_at", DateTimeOffset.FromUnixTimeSeconds(h.UnixTime).UtcDateTime.ToString("o", CultureInfo.InvariantCulture) },
                { "p_path", name }, { "p_size", data.Length }, { "meta", meta },
            };
            string id = Json.Deserialize<string>(Account.Rest("POST", "/rest/v1/rpc/replay_attach_state", Json.Serialize(args)));
            File.AppendAllLines(UploadedList, new[] { System.IO.Path.GetFileName(h.Path) + "\t" + id });
            return id;
        }

        /// <summary>the row's game recording, kept in <Saves>\replays\cloud (downloaded once)</summary>
        public static string Download(CloudReplay r)
        {
            if (!r.HasState) throw new Exception("This match has only the server's input recording (watchable in the game later).");
            if (File.Exists(r.LocalCopy)) return r.LocalCopy;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(r.LocalCopy));
            var rq = (HttpWebRequest)WebRequest.Create(Account.Url + "/storage/v1/object/authenticated/replays/" + r.SsbvPath);
            rq.Timeout = 60000; rq.Headers["apikey"] = Account.Key; rq.Headers["Authorization"] = "Bearer " + Account.AccessToken();
            byte[] b;
            using (var rs = rq.GetResponse()) using (var ms = new MemoryStream()) { rs.GetResponseStream().CopyTo(ms); b = ms.ToArray(); }
            string tmp = r.LocalCopy + ".part";
            File.WriteAllBytes(tmp, b);
            ReplayState.ReadHeader(tmp);                    // (a damaged download fails here)
            if (File.Exists(r.LocalCopy)) File.Delete(r.LocalCopy);
            File.Move(tmp, r.LocalCopy);
            return r.LocalCopy;
        }

        public static void SetPublic(string id, bool pub)
        {
            Account.Rest("POST", "/rest/v1/rpc/replay_set_public", Json.Serialize(new Dictionary<string, object> { { "p_id", id }, { "p_public", pub } }));
        }
    }
}
