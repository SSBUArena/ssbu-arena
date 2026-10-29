// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.10) Every base-game fighter is playable from the first start. Before the game starts (a match or the shader
// preparation), with the game closed:
//  - no save yet: the base save is put in its place (Assets\base_save.zip, embedded: a save the game made at its first
//    start, every base-game fighter unlocked in it; tools/save/make_base_save.py makes it and says how);
//  - a save of the known layout with a base-game fighter still locked: that fighter is unlocked, after a copy of the
//    save is kept in <saves>\save-backups\.
// The save layout (13.0.4 and 13.0.5 alike: system_data.bin of 0x5b4af8 bytes, format 0x1b at offset 4) after
// OpenSmash-Ultimate's tools/unlock_all.py: 8 bytes per fighter at FLAGS, indexed by its save_no (0 Mario ... 73 Mii
// Gunner; 74.. the DLC fighters, which the game unlocks itself from the add-on contents owned: never touched here),
// [0] 1 = selectable, [1..3] challenger state. Unlocked this way a fighter gets no "joined the battle" milestone (as a
// starter), so the next start shows no notices. A save of another layout, or one the game was committing (save.commit:
// finished at the game's next start), is left as it is.
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Arena
{
    public static class BaseSave
    {
        const int Size = 0x5B4AF8, Format = 0x1B, Flags = 0x53FF40, BaseFighters = 74;
        static readonly int[] Starters = { 0, 1, 2, 3, 5, 6, 7, 8 };     // Mario, Donkey Kong, Link, Samus, Yoshi, Kirby, Fox, Pikachu

        /// <summary>what was done, for the log ("" = nothing)</summary>
        public static string Ensure(string saves)
        {
            try
            {
                if (File.Exists(Path.Combine(saves, "save.commit"))) return "";      // the game finishes that save at its next start
                string dir = Path.Combine(saves, @"save\save_data"), file = Path.Combine(dir, "system_data.bin");
                if (!File.Exists(file))
                {
                    using (var z = typeof(BaseSave).Assembly.GetManifestResourceStream("Arena.base_save.zip"))
                    using (var zip = new ZipArchive(z))
                        foreach (var e in zip.Entries.Where(e => e.Name != ""))
                        {
                            string dst = Path.Combine(saves, @"save", e.FullName.Replace('/', '\\'));
                            Directory.CreateDirectory(Path.GetDirectoryName(dst));
                            e.ExtractToFile(dst, false);
                        }
                    return "base save put in place (every base-game fighter unlocked)";
                }
                byte[] d = File.ReadAllBytes(file);
                if (d.Length != Size || BitConverter.ToInt32(d, 0) != Size || BitConverter.ToInt32(d, 4) != Format) return "";
                if (Starters.Any(n => d[Flags + 8 * n] != 1) || Enumerable.Range(0, BaseFighters).Any(n => d[Flags + 8 * n] > 1)) return "";
                var locked = Enumerable.Range(0, BaseFighters).Where(n => d[Flags + 8 * n] == 0).ToList();
                if (locked.Count == 0) return "";
                string backups = Path.Combine(saves, "save-backups");
                Directory.CreateDirectory(backups);
                File.Copy(file, Path.Combine(backups, "system_data.bin.before-unlock-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), true);
                foreach (int n in locked)
                {
                    d[Flags + 8 * n] = 1;
                    d[Flags + 8 * n + 1] = d[Flags + 8 * n + 2] = d[Flags + 8 * n + 3] = 0;
                }
                string tmp = file + ".tmp";
                File.WriteAllBytes(tmp, d);
                File.Copy(tmp, file, true);
                File.Delete(tmp);
                return locked.Count + " base-game fighters unlocked in the save (a copy of it is in save-backups)";
            }
            catch (Exception e) { return "base save: " + e.Message; }
        }
    }
}
