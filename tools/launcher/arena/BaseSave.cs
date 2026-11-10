// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.10) Every base-game fighter is playable from the first start. Before the game starts (a match or the shader
// preparation), with the game closed:
//  - no save yet: the base save is put in its place (Assets\base_save.zip, embedded: a save the game made at its first
//    start, every base-game fighter unlocked in it and the screens the game shows once for them already seen, so the
//    first start goes from the title to the menu; tools/save/make_base_save.py makes it and says how);
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

        // (complete save) "the game finished": what the game gives along the way already given and already shown, so after a
        // match or at the start it has nothing new to announce (no challenge completed, no panel of the Challenges board
        // to reveal, no reward). Only the game's progress fields change; the player's own data (name tags, controls,
        // settings, records, replays, Mii fighters, profile icon, gold and the other counts) is never touched. Found
        // 2026-11-10 in 13.0.4's code (the save's accessor table 0x7105313510; tools/save/complete_save_masks.py makes
        // the masks from the game's own lists):
        //  - Challenges (the board "clear_getter", 124 panels): the bit array at CHALLENGES (u32 words), 3 bits per
        //    challenge by its save_no: +0 cleared (written where the game checks the condition, 0x7101777d0c), +1 shown
        //    (the board revealed its panel and gave the reward: 0x7101a8c214), +2 a mark the board shows on a panel not cleared
        //    yet (set with a hammer, 0x7101a8f028; left as it is). Cleared and shown: no "Challenge complete!", no
        //    reveal, no reward to give; the board reads 124/124 (seen in the game, 2026-11-10).
        //  - Music: owned at BGM_OWNED (1309 bits by ui_bgm_db save_no, the game's subfield 0x10 of the unlock flags at
        //    +0x20) and the same list at BGM_KNOWN (+0x5b45dc, a fresh save holds the same bits: a song owned but not
        //    there is new); every base-game song, not the DLC ones (the game sets those from the add-on contents).
        //  - Mii Fighter outfits: hats at HATS (+0x20, ui_mii_hat_db), bodies at BODIES (+0x54, ui_mii_body_db), the
        //    ones not sold as paid add-ons.
        //  - Movies of the Vault: MOVIES (+0x30c, ui_movie_db, 9).
        //  - The "how to play" pages a mode shows the first time it is opened: HELP (+0x310, subfield 0x22, 60 pages,
        //    the list whose rows name howtoplay_*.html): marked as already read.
        //  - Fighters: Ensure above (base-game ones).
        // A copy of the save is kept in <saves>\save-backups first (before-complete-<time>). Spirits are not given (their
        // collection is a separate structure, +0x43471c, not studied): the game gives none after a match anyway. The
        // add-on contents (DLC) are left to the game: at the first start with new add-on contents it unlocks their
        // fighters, stages, songs and Mii outfits itself and announces them once (X skips them all).
        const int Challenges = 0x555BB8, ChallengeCount = 124, BgmOwned = 0x1D0, BgmKnown = 0x5B45DC, Hats = 0x20, Bodies = 0x54, Movies = 0x30C, Help = 0x310;
        const string BgmMask =
            "ffffffffdfffff7cfdfeffebffbfffffbfcbdf8984df7ffffffffffffffffdfffffffffffffffbfffffffffffffffbffffffffffffffa3ea3ff8fefff9ffffffffff0100000000000000" +
            "b0ffbafffeff0b3effff7f00ffffffffffffffffff01b0fffffffff3ff0f8e0ffef7fbe5bfaaaaaaaaaaaaca377f1d53eaff679ce37fffbeefffe5fe25f8f7dfff03";
        const string HatsMask = "ffffffffffffffffff7f", BodiesMask = "ffffffffffffffffffffffff", MoviesMask = "ff01", HelpMask = "ffffffffffffff0f";

        static byte[] Hex(string h) { var b = new byte[h.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(h.Substring(2 * i, 2), 16); return b; }

        /// <summary>sets the bits of mask at off; how many were not set</summary>
        static int Or(byte[] d, int off, byte[] mask)
        {
            int n = 0;
            for (int i = 0; i < mask.Length; i++)
            {
                int add = mask[i] & ~d[off + i];
                if (add == 0) continue;
                for (int b = add; b != 0; b &= b - 1) n++;
                d[off + i] |= (byte)add;
            }
            return n;
        }

        /// <summary>(complete save) everything given and shown, see above; what was done, for the log ("" = nothing)</summary>
        public static string Complete(string saves)
        {
            try
            {
                if (File.Exists(Path.Combine(saves, "save.commit"))) return "";
                string file = Path.Combine(saves, @"save\save_data\system_data.bin");
                if (!File.Exists(file)) return "";
                byte[] d = File.ReadAllBytes(file);
                if (d.Length != Size || BitConverter.ToInt32(d, 0) != Size || BitConverter.ToInt32(d, 4) != Format) return "";
                Func<int, bool> bit = b => (d[Challenges + b / 8] >> (b % 8) & 1) != 0;
                int challenges = Enumerable.Range(0, ChallengeCount).Count(k => !bit(3 * k) || !bit(3 * k + 1));
                var ch = new byte[(ChallengeCount * 3 + 7) / 8];
                for (int k = 0; k < ChallengeCount; k++) { ch[3 * k / 8] |= (byte)(1 << (3 * k % 8)); ch[(3 * k + 1) / 8] |= (byte)(1 << ((3 * k + 1) % 8)); }
                Or(d, Challenges, ch);
                byte[] bgm = Hex(BgmMask);
                int songs = Or(d, BgmOwned, bgm);
                int heard = Or(d, BgmKnown, bgm), outfits = Or(d, Hats, Hex(HatsMask)) + Or(d, Bodies, Hex(BodiesMask)), movies = Or(d, Movies, Hex(MoviesMask)), help = Or(d, Help, Hex(HelpMask));
                if (challenges + songs + heard + outfits + movies + help == 0) return "";
                string backups = Path.Combine(saves, "save-backups");
                Directory.CreateDirectory(backups);
                File.Copy(file, Path.Combine(backups, "system_data.bin.before-complete-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")), true);
                string tmp = file + ".tmp";
                File.WriteAllBytes(tmp, d);
                File.Copy(tmp, file, true);
                File.Delete(tmp);
                return string.Format("complete save: {0} challenges cleared, {1} songs given ({2} marked known), {3} Mii outfits, {4} movies, {5} help pages read (a copy of the save is in save-backups)",
                                     challenges, songs, heard, outfits, movies, help);
            }
            catch (Exception e) { return "complete save: " + e.Message; }
        }
    }
}
