// SPDX-License-Identifier: GPL-3.0-or-later
// Shared game data: a player who also has OpenSmash Ultimate keeps the game's data once. The install reads OpenSmash's files in
// place through <build>\dump_shared\romfs\data.arcl, a map that scripts/shared_gamedata.py makes (runtime/arclink.c reads it;
// tools/nro/arc_file.py says the format). Nothing is written in OpenSmash's folder.
//   - Problem(): the cheap check before every start (sizes and dates of the files the map reads, and of OpenSmash's game data
//     cfg and layer map): a fraction of a second, no file is read through
//   - Relink(): the map made again (shared_gamedata.py relink, about 2 s, no compile) when OpenSmash rewrote, re-extracted or
//     moved its files; it refuses a modded map, another game update, incomplete data (the reason comes back)
//   - DeleteOwnDump(): Arena's old copy (build\dump), only when the link is in use and checked, only after the player's
//     confirmation (MainWindow.Shared.cs), never anything else
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace Arena
{
    static class SharedData
    {
        public static string LinkedDump => Path.Combine(Core.Build, "dump_shared");
        public static string OwnDump => Path.Combine(Core.Build, "dump");
        static readonly Dictionary<uint, string> Builds = new Dictionary<uint, string> { { 0x39c7a90, "13.0.2" }, { 0x39c7e90, "13.0.4" }, { 0x39c8440, "13.0.5" } };   // scripts/setup.py GAME_BUILDS

        static bool Same(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>this install reads its game data through the link (state.json's romfs is dump_shared\romfs)</summary>
        public static bool InUse => Core.RomFS != null && Same(Core.RomFS, Path.Combine(LinkedDump, "romfs"));

        static long FileTime(FileInfo f) => f.LastWriteTimeUtc.ToFileTimeUtc();

        /// <summary>null when the link can be used as it is; else what changed. Reads only data.arcl's header, shared_from.json
        /// and the sizes and dates of the files they name.</summary>
        public static string Problem()
        {
            string arcl = Path.Combine(LinkedDump, @"romfs\data.arcl");
            try
            {
                if (!File.Exists(arcl)) return arcl + " is not there";
                byte[] d = File.ReadAllBytes(arcl);
                if (d.Length < 24 || Encoding.ASCII.GetString(d, 0, 8) != "SSBUARCL") return arcl + " is not a linked archive";
                uint ns = BitConverter.ToUInt32(d, 16);
                int pos = 24;
                for (uint i = 0; i < ns; i++)
                {
                    long size = BitConverter.ToInt64(d, pos), mt = BitConverter.ToInt64(d, pos + 8);
                    int n = BitConverter.ToInt32(d, pos + 16);
                    string p = Encoding.UTF8.GetString(d, pos + 20, n);
                    pos += 20 + n;
                    var fi = new FileInfo(p);
                    if (!fi.Exists) return p + " is not there any more";
                    if (fi.Length != size || FileTime(fi) != mt) return p + " changed since the link was made";
                }
                foreach (var kv in Stamps())
                {
                    var fi = new FileInfo(kv.Key);
                    if (!fi.Exists) return kv.Key + " is not there any more";
                    if (fi.Length != kv.Value.Item1 || FileTime(fi) != kv.Value.Item2) return kv.Key + " changed since the link was made";
                }
                return null;
            }
            catch (Exception e) { return arcl + ": " + e.Message; }
        }

        static Dictionary<string, object> FromJson()
        {
            try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(LinkedDump, "shared_from.json"))); }
            catch { return null; }
        }

        static IEnumerable<KeyValuePair<string, Tuple<long, long>>> Stamps()
        {
            var j = FromJson();
            if (j == null || !j.TryGetValue("stamps", out var st) || !(st is Dictionary<string, object> m)) yield break;
            foreach (var kv in m)
                if (kv.Value is ArrayList a && a.Count == 2)
                    yield return new KeyValuePair<string, Tuple<long, long>>(kv.Key, Tuple.Create(Convert.ToInt64(a[0]), Convert.ToInt64(a[1])));
        }

        /// <summary>the OpenSmash folder the link was made from (shared_from.json), or null</summary>
        public static string LinkedFrom() { var j = FromJson(); return j != null && j.TryGetValue("root", out var r) ? r as string : null; }

        /// <summary>an OpenSmash Ultimate install folder: its setup script and its game data folder</summary>
        public static bool IsOpenSmash(string dir) =>
            !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir, @"tools\install_windowsmash.ps1")) && Directory.Exists(Path.Combine(dir, @"dump\modules"));

        /// <summary>the game update of an OpenSmash install (its exefs main), or null when Arena does not handle it</summary>
        public static string Version(string dir)
        {
            try
            {
                using (var f = File.OpenRead(Path.Combine(dir, @"dump\exefs\main")))
                {
                    var h = new byte[0x20];
                    if (f.Read(h, 0, 0x20) != 0x20 || Encoding.ASCII.GetString(h, 0, 4) != "NSO0") return null;
                    return Builds.TryGetValue(BitConverter.ToUInt32(h, 0x18), out var v) ? v : null;
                }
            }
            catch { return null; }
        }

        /// <summary>the OpenSmash Ultimate install on this PC whose game data can be shared (launcher.ini opensmash_path, the folder
        /// OpenSmash remembers, its shortcuts: OpenSmash.Find), or null. Only names and an 32-byte header are read.</summary>
        public static string FindOpenSmash()
        {
            try
            {
                string set = (Core.OpenSmashPath ?? "").Trim().Trim('"');
                if (IsOpenSmash(set)) return Path.GetFullPath(set).TrimEnd('\\');
                string exe = OpenSmash.Find(out _);
                string dir = exe == null ? null : Path.GetDirectoryName(exe);
                if (IsOpenSmash(dir)) return Path.GetFullPath(dir).TrimEnd('\\');
                string from = LinkedFrom();
                if (IsOpenSmash(from)) return from;
            }
            catch { }
            return null;
        }

        /// <summary>a usable OpenSmash install with a game update Arena handles: (folder, update), or null</summary>
        public static Tuple<string, string> Candidate()
        {
            string dir = FindOpenSmash();
            string v = dir == null ? null : Version(dir);
            if (v == null) return null;
            string mod = Path.Combine(dir, @"dump\modules");
            if (!File.Exists(Path.Combine(mod, "romfs_vanilla.cfg")) && !File.Exists(Path.Combine(mod, "romfs.cfg"))) return null;
            if (File.Exists(Path.Combine(mod, "romfs.crypt"))) return null;      // read in place from the encrypted game file: not shared
            return Tuple.Create(dir, v);
        }

        /// <summary>the map made again (shared_gamedata.py relink <build> [folder]): (true, update) or (false, why). Blocks ~2 s.</summary>
        public static Tuple<bool, string> Relink(string folder)
        {
            string py = Installer.Python;
            if (py == null || Core.Repo == null) return Tuple.Create(false, "the setup's Python is missing (run the setup again)");
            var psi = new ProcessStartInfo(py, "-u scripts\\shared_gamedata.py relink " + Installer.Q(Core.Build) + (folder != null ? " " + Installer.Q(folder) : ""))
            { WorkingDirectory = Core.Repo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            try
            {
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    string o = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return Tuple.Create(false, "the link took too long"); }
                    string line = o.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("{"));
                    Updater.Log("shared data relink (" + p.ExitCode + "): " + (line ?? o.Trim() + err.Result.Trim()));
                    var j = line == null ? null : new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(line);
                    if (j != null && j.TryGetValue("ok", out var ok) && ok is bool b && b) return Tuple.Create(true, j["version"] as string);
                    return Tuple.Create(false, j != null && j.TryGetValue("why", out var w) ? w as string : (err.Result.Trim().Split('\n').LastOrDefault() ?? "unknown error"));
                }
            }
            catch (Exception e) { return Tuple.Create(false, e.Message); }
        }

        /// <summary>Arena's own copy of the game data (build\dump with romfs\data.arc), when there is one</summary>
        public static bool OwnDumpThere => File.Exists(Path.Combine(OwnDump, @"romfs\data.arc"));

        public static long OwnDumpBytes()
        {
            try { return new DirectoryInfo(OwnDump).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { return 0; }
        }

        /// <summary>deletes build\dump: only while the link is in use and checked, and only that folder; never one that holds a link
        /// (a junction to another dump) or the folder the game reads. null when done, else why not.</summary>
        public static string DeleteOwnDump()
        {
            if (!InUse) return "the game does not read the shared data";
            string bad = Problem();
            if (bad != null) return "the shared data cannot be used now (" + bad + ")";
            if (Core.RomFS != null && Core.RomFS.StartsWith(OwnDump + "\\", StringComparison.OrdinalIgnoreCase)) return "the game reads that folder";
            var di = new DirectoryInfo(OwnDump);
            if (!di.Exists) return null;
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0 || di.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Any(x => (x.Attributes & FileAttributes.ReparsePoint) != 0))
                return OwnDump + " holds a link to another folder: delete it by hand";
            di.Delete(true);
            Updater.Log("shared data: Arena's old copy " + OwnDump + " deleted (confirmed by the player)");
            return null;
        }
    }
}
