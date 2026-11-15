// SPDX-License-Identifier: GPL-3.0-or-later
// (online-mm 15/11) The game code (tools/lifter/game_code.py, runtime/game_code.c): does the game DLL come from the sources the exe
// was built with? 13/11: an update replaced the exe (0.2.49) but its compile step kept a test DLL because it was newer than the
// objects (dates only), and that game played online against normal 0.2.49 games: the match desynced. The exe carries in its
// section .ssbuid "SSBU-DLL-WANT:<8 hex>" (the DLL sources it was built with), the DLL "SSBU-GAME-CODE:d<8 hex><4 hex>". Read from
// the files' section tables: the 660 MB DLL is not loaded. SSBU_ARENA_CODE_CHECK=0: no check (as before).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Arena
{
    public static class GameCode
    {
        public static bool On => Environment.GetEnvironmentVariable("SSBU_ARENA_CODE_CHECK") != "0";
        public static string Dll => Path.Combine(Core.Work, "ssbu_game_r1.dll");

        /// <summary>the text after tag in the file's .ssbuid section; null: no such section or tag (a build of before)</summary>
        public static string Read(string path, string tag)
        {
            try
            {
                using (var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new BinaryReader(f))
                {
                    if (r.ReadUInt16() != 0x5A4D) return null;                       // MZ
                    f.Seek(0x3C, SeekOrigin.Begin); int pe = r.ReadInt32();
                    f.Seek(pe, SeekOrigin.Begin); if (r.ReadUInt32() != 0x00004550) return null;      // PE\0\0
                    f.Seek(pe + 6, SeekOrigin.Begin); int nsec = r.ReadUInt16();
                    f.Seek(pe + 20, SeekOrigin.Begin); int opt = r.ReadUInt16();
                    for (int i = 0; i < nsec && i < 96; i++)
                    {
                        f.Seek(pe + 24 + opt + i * 40, SeekOrigin.Begin);
                        string name = Encoding.ASCII.GetString(r.ReadBytes(8)).TrimEnd('\0');
                        if (name != ".ssbuid") continue;
                        f.Seek(pe + 24 + opt + i * 40 + 16, SeekOrigin.Begin);
                        int size = r.ReadInt32(), raw = r.ReadInt32();
                        f.Seek(raw, SeekOrigin.Begin);
                        string data = Encoding.ASCII.GetString(r.ReadBytes(Math.Min(size, 4096)));
                        int k = data.IndexOf(tag, StringComparison.Ordinal);
                        if (k < 0) return null;
                        int end = data.IndexOf('\0', k);
                        return data.Substring(k + tag.Length, (end < 0 ? data.Length : end) - k - tag.Length);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>null: the game DLL is the one the exe expects, or there is nothing to compare (an exe of before, no DLL yet);
        /// otherwise why it is not (for the log and the player)</summary>
        public static string Mismatch()
        {
            if (!On || !File.Exists(Core.Exe) || !File.Exists(Dll)) return null;
            string want = Read(Core.Exe, "SSBU-DLL-WANT:");
            if (want == null || want.Length != 8 || want == "unknown") return null;           // an exe without the code: not checked
            string have = Read(Dll, "SSBU-GAME-CODE:");
            if (have != null && have.Length >= 9 && have.Substring(1, 8) == want) return null;
            return "the game DLL " + (have == null ? "has no code (built before this version, or copied in by hand)" : "is d" + have.Substring(1)) +
                   ", this version's exe expects d" + want;
        }

        /// <summary>the repair: the DLL removed and the setup's compile / link / check steps marked to run again (build\state.json,
        /// as scripts\verify.py repair does). The setup then compiles what changed (and what a test build left: build.py's
        /// content check) and links the DLL. False when the files cannot be changed (the game is running).</summary>
        public static bool PrepareRepair(out string error)
        {
            error = null;
            try
            {
                string state = Path.Combine(Core.Build, "state.json");
                var json = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var s = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(state));
                if (s.TryGetValue("done", out var d) && d is Dictionary<string, object> done)
                    foreach (var step in new[] { "compile", "nro", "link", "rollback", "check" }) done.Remove(step);
                foreach (var f in new[] { Dll, Path.ChangeExtension(Dll, ".lib") })
                    if (File.Exists(f)) File.Delete(f);
                File.WriteAllText(state + ".tmp", json.Serialize(s), new UTF8Encoding(false));
                if (File.Exists(state)) File.Replace(state + ".tmp", state, null); else File.Move(state + ".tmp", state);
                return true;
            }
            catch (Exception e) { error = e.Message; return false; }
        }
    }
}
