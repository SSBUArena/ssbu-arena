// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.78) The name tags of the game's system save (save_data\system_data.bin): their names and their controls, the ones the
// game's own Options > Controls shows and the fight uses. A port of the name-tag part of tools/save/save_tool.py (the study
// tool; its tests and this file's tests, tools/launcher/arena_tests, must give the same bytes for the same edits). The layout
// (13.0.2 / 13.0.4 / 13.0.5 alike) is in save_research/SAVE_FORMAT.txt section 4:
//   - 61 entries of 0xf7d8 bytes at 0x42f0; entry 0 is an unnamed one the game keeps itself, named tags are 1..60;
//   - entry + 0 in use (1), + 4 name hash, + 8 name length (UTF-16 units), + 0xc name (UTF-16, 10 characters + NUL),
//     + 0x24 .. + 0x4e the controls (GameCube 11 buttons + 4 options, Pro 12 + 4, one Joy-Con 8 + 4), + 0x50 created,
//     + 0x54 modified (numbers of the save's change counter, u32 at 0x550ad8); + 0x58 .. the tag's records (never read here);
//   - no checksum over the file; the name hash is the game's (0x811c9dc5, then h = h * 0x89 ^ unit), which it recomputes anyway.
// Every write: only with the game closed and no <saves>\save.commit (a commit the game finishes at its next start), a copy of
// the save in <saves>\save-backups first (once per launcher run), the change counter + 1 and the entry's modified number,
// then the new file written beside the old one and swapped in (never a half-written save).
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Arena
{
    public sealed class SaveFileError : Exception { public SaveFileError(string m) : base(m) { } }

    /// <summary>one entry of the name-tag table, as read</summary>
    public sealed class NameTag
    {
        public int Slot;
        public bool InUse;
        public string Name = "";
        public bool NameHashOk;
        public uint Created, Modified;
        public byte[] Controls = new byte[NameTagSave.CtlLen];      // + 0x24 .. + 0x4e
        public override string ToString() => Name;
    }

    /// <summary>a controller kind's block inside the 0x2b control bytes: its buttons, then 4 option bytes</summary>
    public sealed class ControllerKind
    {
        public string Id, Title;
        public int First;                    // offset in the 0x2b bytes
        public string[] Buttons;             // in save order
        public int Options => First + Buttons.Length;
        public ControllerKind(string id, string title, int first, params string[] buttons) { Id = id; Title = title; First = first; Buttons = buttons; }
    }

    public sealed class NameTagSave
    {
        public const int Size = 0x5B4AF8, Format = 0x1B, Counter = 0x550AD8;
        public const int Base = 0x42F0, Slots = 61, EntrySize = 0xF7D8, FirstNamed = 1;
        public const int NameOff = 0x0C, NameUnits = 11, NameMax = 10, CtlOff = 0x24, CtlLen = 0x2B, Head = 0x58;

        /// <summary>the game's default controls (every never-used entry holds them; SAVE_FORMAT.txt section 4)</summary>
        public static readonly byte[] DefaultControls = Hex("0303040a0b0c00010502020101010104040303" + "0a0b0c0001050202010101010d0d04030200020101010101");

        /// <summary>the action codes the game uses for a button (the ones known; others are shown as "Other (n)" and kept)</summary>
        public static readonly Dictionary<int, string> Actions = new Dictionary<int, string> {
            { 0, "Attack" }, { 1, "Special" }, { 2, "Jump" }, { 3, "Shield" }, { 4, "Grab" }, { 5, "Smash attack" },
            { 10, "Up taunt" }, { 11, "Side taunt" }, { 12, "Down taunt" } };

        /// <summary>the three controller kinds, as save_tool.py CONTROLLERS (offsets inside the 0x2b bytes)</summary>
        public static readonly ControllerKind[] Kinds = {
            new ControllerKind("gamecube", "GameCube", 0x00, "L", "R", "Z", "D-pad up", "D-pad side", "D-pad down", "A", "B", "C stick", "X", "Y"),
            new ControllerKind("pro", "Pro Controller", 0x0F, "L", "R", "ZL", "ZR", "D-pad up", "D-pad side", "D-pad down", "A", "B", "Right stick", "X", "Y"),
            new ControllerKind("joycon", "Joy-Con", 0x1F, "Button 1", "Button 2", "Button 3", "Button 4", "Button 5", "Button 6", "Button 7", "Button 8") };

        public readonly byte[] D;

        public NameTagSave(byte[] data)
        {
            if (data == null || data.Length != Size) throw new SaveFileError("not a system_data.bin of the known layout: " + (data?.Length ?? 0) + " bytes, not " + Size);
            if (U32(data, 0) != Size || U32(data, 4) != Format) throw new SaveFileError(string.Format("header 0x{0:x} / 0x{1:x}: not the known layout", U32(data, 0), U32(data, 4)));
            D = (byte[])data.Clone();
        }

        public static NameTagSave Load(string path) => new NameTagSave(File.ReadAllBytes(path));

        public uint Version => U32(D, 8);
        public uint ChangeCounter => U32(D, Counter);

        uint BumpCounter() { uint n = unchecked(ChangeCounter + 1); W32(D, Counter, n); return n; }

        public static int Offset(int slot)
        {
            if (slot < 0 || slot >= Slots) throw new SaveFileError("name tag " + slot + " out of 0.." + (Slots - 1));
            return Base + EntrySize * slot;
        }

        /// <summary>the game's hash of a name (FixedString16): 0x811c9dc5, then h = h * 0x89 ^ unit for each UTF-16 unit</summary>
        public static uint NameHash(string name)
        {
            uint h = 0x811C9DC5;
            foreach (char u in name ?? "") h = unchecked(h * 0x89) ^ u;
            return h;
        }

        public NameTag Tag(int slot)
        {
            int o = Offset(slot);
            var t = new NameTag { Slot = slot, InUse = D[o] == 1, Name = ReadName(o), Created = U32(D, o + 0x50), Modified = U32(D, o + 0x54) };
            t.NameHashOk = U32(D, o + 4) == NameHash(t.Name) && U32(D, o + 8) == t.Name.Length;
            Buffer.BlockCopy(D, o + CtlOff, t.Controls, 0, CtlLen);
            return t;
        }

        /// <summary>the entries in use (all = the unused ones too), entry 0 included</summary>
        public List<NameTag> Tags(bool all = false) => Enumerable.Range(0, Slots).Where(k => all || D[Offset(k)] == 1).Select(Tag).ToList();

        /// <summary>the named tags a player picks on the character select: entries 1..60 in use</summary>
        public List<NameTag> Named() => Tags().Where(t => t.Slot >= FirstNamed).ToList();

        /// <summary>the first entry 1..60 not in use; -1 = all 60 are</summary>
        public int FreeSlot()
        {
            for (int k = FirstNamed; k < Slots; k++) if (D[Offset(k)] != 1) return k;
            return -1;
        }

        string ReadName(int o)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < NameUnits; i++) { char u = (char)(D[o + NameOff + 2 * i] | D[o + NameOff + 2 * i + 1] << 8); if (u == 0) break; sb.Append(u); }
            return sb.ToString();
        }

        /// <summary>why a name cannot be stored (null = it can): at most 10 UTF-16 units, no NUL (the game's own limits)</summary>
        public static string NameProblem(string name)
        {
            if (name == null) return "no name";
            if (name.Length > NameMax) return "a name tag holds at most " + NameMax + " characters";
            if (name.IndexOf('\0') >= 0) return "a name cannot hold NUL";
            return null;
        }

        /// <summary>save_tool.py set_profile with the fields given (null = not given): in use, name, the 0x2b control bytes. Only
        /// what differs is written (an unchanged name keeps its bytes); when anything changed the change counter goes up by one, the
        /// entry's modified number takes it, and an entry that comes into use takes it as its created number too. Returns whether
        /// the entry changed.</summary>
        public bool SetProfile(int slot, bool? inUse = null, string name = null, byte[] controls = null)
        {
            int o = Offset(slot);
            var before = new byte[Head];
            Buffer.BlockCopy(D, o, before, 0, Head);
            if (inUse.HasValue && inUse.Value != (D[o] == 1)) D[o] = (byte)(inUse.Value ? 1 : 0);
            if (name != null && name != ReadName(o))
            {
                string why = NameProblem(name);
                if (why != null) { Buffer.BlockCopy(before, 0, D, o, Head); throw new SaveFileError(why); }
                for (int i = 0; i < NameUnits; i++) { char u = i < name.Length ? name[i] : '\0'; D[o + NameOff + 2 * i] = (byte)u; D[o + NameOff + 2 * i + 1] = (byte)(u >> 8); }
                W32(D, o + 4, NameHash(name));
                W32(D, o + 8, (uint)name.Length);
            }
            if (controls != null)
            {
                if (controls.Length != CtlLen) { Buffer.BlockCopy(before, 0, D, o, Head); throw new SaveFileError("controls: " + CtlLen + " bytes, not " + controls.Length); }
                Buffer.BlockCopy(controls, 0, D, o + CtlOff, CtlLen);
            }
            bool changed = false;
            for (int i = 0; i < Head && !changed; i++) changed = D[o + i] != before[i];
            if (!changed) return false;
            uint n = BumpCounter();
            if (before[0] == 0 && D[o] != 0) W32(D, o + 0x50, n);
            W32(D, o + 0x54, n);
            return true;
        }

        public bool SetControls(int slot, byte[] controls) => SetProfile(slot, controls: controls);
        public bool SetName(int slot, string name) => SetProfile(slot, name: name);

        /// <summary>a new name tag in the first free entry 1..60, with the game's default controls (save_tool.py set-profile with
        /// in_use, name and raw_controls); returns its entry</summary>
        public int Create(string name)
        {
            int slot = FreeSlot();
            if (slot < 0) throw new SaveFileError("the save already holds 60 name tags, the game's limit");
            SetProfile(slot, true, name, DefaultControls);
            return slot;
        }

        /// <summary>a name tag removed as the game leaves a tag it deleted (seen in the samples: the whole entry zero, hash 0, the
        /// default controls; its records go with it); the change counter + 1 (save_tool.py delete-profile)</summary>
        public void Delete(int slot)
        {
            if (slot < FirstNamed) throw new SaveFileError("entry 0 is the game's own unnamed one: not deleted");
            int o = Offset(slot);
            Array.Clear(D, o, EntrySize);
            Buffer.BlockCopy(DefaultControls, 0, D, o + CtlOff, CtlLen);
            BumpCounter();
        }

        static uint U32(byte[] d, int o) => (uint)(d[o] | d[o + 1] << 8 | d[o + 2] << 16 | d[o + 3] << 24);
        static void W32(byte[] d, int o, uint v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
        public static byte[] Hex(string h) => Enumerable.Range(0, h.Length / 2).Select(i => Convert.ToByte(h.Substring(2 * i, 2), 16)).ToArray();
        public static string ToHex(byte[] b) => string.Concat(b.Select(x => x.ToString("x2")));
    }

    /// <summary>reading and writing the save file of a saves folder (the launcher's <saves>: save\save_data\system_data.bin)</summary>
    public static class NameTagFile
    {
        public static string SaveFile(string saves) => Path.Combine(saves, "save", "save_data", "system_data.bin");
        public static string CommitFile(string saves) => Path.Combine(saves, "save.commit");
        static readonly HashSet<string> backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static NameTagSave Read(string saves)
        {
            string f = SaveFile(saves);
            if (!File.Exists(f)) throw new SaveFileError("there is no save yet: start the game once");
            return NameTagSave.Load(f);
        }

        /// <summary>why the save cannot be written now (null = it can)</summary>
        public static string Blocked(string saves, Func<bool> gameRunning)
        {
            if (gameRunning != null && gameRunning()) return "Close the game first: it keeps the save open and would write over the change.";
            if (File.Exists(CommitFile(saves))) return "The game has a save to finish (save.commit): start the game once and close it, then try again.";
            if (!File.Exists(SaveFile(saves))) return "There is no save yet: start the game once.";
            return null;
        }

        /// <summary>reads the save again, applies edit, and writes it (see the file's head); returns the backup made this time
        /// (null = one was already made in this run). Throws SaveFileError when it cannot write.</summary>
        public static string Edit(string saves, Func<bool> gameRunning, Action<NameTagSave> edit)
        {
            string why = Blocked(saves, gameRunning);
            if (why != null) throw new SaveFileError(why);
            string file = SaveFile(saves);
            byte[] old = File.ReadAllBytes(file);
            var s = new NameTagSave(old);
            edit(s);
            if (s.D.SequenceEqual(old)) return null;
            string bak = null;
            lock (backedUp)
                if (!backedUp.Contains(file))
                {
                    string dir = Path.Combine(saves, "save-backups");
                    Directory.CreateDirectory(dir);
                    bak = Path.Combine(dir, "system_data.bin.before-nametags-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bak");
                    File.WriteAllBytes(bak, old);
                    backedUp.Add(file);
                }
            if (gameRunning != null && gameRunning()) throw new SaveFileError("Close the game first: it keeps the save open and would write over the change.");
            WriteAtomic(file, s.D);
            return bak;
        }

        /// <summary>the new bytes into file.tmp (flushed to the disk), then swapped in for the old file</summary>
        public static void WriteAtomic(string file, byte[] data)
        {
            string tmp = file + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                fs.Write(data, 0, data.Length);
                fs.Flush(true);
            }
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }
    }
}
