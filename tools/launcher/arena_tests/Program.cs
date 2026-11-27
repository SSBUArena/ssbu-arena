// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.78) tests of tools/launcher/arena/NameTags.cs (see ArenaTests.csproj). Made-up saves only in the repo: random bytes of the
// right size and header, no game data. Real saves only from SSBU_SAVE_SAMPLES, always copied to a temporary folder first.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Arena.Tests
{
    static partial class Program
    {
        static int failed, passed;
        static readonly string Tmp = Path.Combine(Path.GetTempPath(), "arena_nametag_tests_" + Process.GetCurrentProcess().Id);

        static void Check(bool ok, string what)
        {
            if (ok) passed++;
            else { failed++; Console.WriteLine("FAIL: " + what); }
        }

        static void Throws(Action a, string what)
        {
            try { a(); Check(false, what + " (no error)"); }
            catch (SaveFileError) { Check(true, what); }
        }

        static byte[] MadeUp(int seed)
        {
            var r = new Random(seed);
            var d = new byte[NameTagSave.Size];
            r.NextBytes(d);
            void W(int o, uint v) { d[o] = (byte)v; d[o + 1] = (byte)(v >> 8); d[o + 2] = (byte)(v >> 16); d[o + 3] = (byte)(v >> 24); }
            W(0, NameTagSave.Size); W(4, NameTagSave.Format); W(8, 0xD004); W(NameTagSave.Counter, 100);
            for (int k = 0; k < NameTagSave.Slots; k++) d[NameTagSave.Offset(k)] = 0;
            return d;
        }

        static int Main()
        {
            Directory.CreateDirectory(Tmp);
            try
            {
                Basics();
                Edits();
                Files();
                Bindings();      // (Controls v3) BindingTests.cs
                Capture();
                Placement();
                Parity();
            }
            catch (Exception e) { failed++; Console.WriteLine("FAIL: exception " + e); }
            finally { try { Directory.Delete(Tmp, true); } catch { } }
            Console.WriteLine(failed == 0 ? "OK: " + passed + " checks" : failed + " FAILED, " + passed + " passed");
            return failed == 0 ? 0 : 1;
        }

        static void Basics()
        {
            Check(NameTagSave.DefaultControls.Length == NameTagSave.CtlLen, "default controls: 0x2b bytes");
            Check(NameTagSave.Kinds.Sum(k => k.Buttons.Length + 4) == NameTagSave.CtlLen, "the three kinds fill the 0x2b bytes");
            Check(NameTagSave.Kinds[1].First == 0x33 - 0x24 && NameTagSave.Kinds[2].First == 0x43 - 0x24, "kind offsets as save_tool.py");
            Check(NameTagSave.NameHash("") == 0x811C9DC5, "empty name hash");
            uint h = unchecked(0x811C9DC5u * 0x89) ^ 'a'; h = unchecked(h * 0x89) ^ 'b';
            Check(NameTagSave.NameHash("ab") == h, "hash formula");
            Check(NameTagSave.NameHash("è") == (unchecked(0x811C9DC5u * 0x89) ^ 0xE8), "hash of UTF-16 units");
            var d = MadeUp(1);
            Throws(() => new NameTagSave(d.Take(d.Length - 1).ToArray()), "wrong size refused");
            var bad = (byte[])d.Clone(); bad[4] = 0x1A;
            Throws(() => new NameTagSave(bad), "wrong format refused");
            Check(new NameTagSave(d).Version == 0xD004, "version read");
        }

        static void Edits()
        {
            var s = new NameTagSave(MadeUp(2));
            Check(s.Named().Count == 0 && s.FreeSlot() == 1, "made-up save: no tags, slot 1 free");
            int k = s.Create("Tester");
            int o = NameTagSave.Offset(k);
            var t = s.Tag(k);
            Check(k == 1 && t.InUse && t.Name == "Tester" && t.NameHashOk, "create: in use, name, hash");
            Check(t.Controls.SequenceEqual(NameTagSave.DefaultControls), "create: default controls");
            Check(s.ChangeCounter == 101 && t.Created == 101 && t.Modified == 101, "create: counter, created, modified");
            Check(s.Named().Select(x => x.Slot).SequenceEqual(new[] { 1 }), "listed");
            Check(!s.SetControls(k, NameTagSave.DefaultControls) && s.ChangeCounter == 101, "same controls: nothing written");
            var c = (byte[])t.Controls.Clone(); c[NameTagSave.Kinds[1].First + 8] = 2;      // Pro B = jump
            Check(s.SetControls(k, c) && s.ChangeCounter == 102 && s.Tag(k).Modified == 102 && s.Tag(k).Created == 101, "controls: counter and modified");
            Check(s.D[o + 0x33 + 8] == 2, "controls: Pro B at + 0x3b");
            Check(s.SetName(k, "Renamed") && s.Tag(k).Name == "Renamed" && s.Tag(k).NameHashOk, "rename");
            Check(s.D[o + 0xc + 2 * 7] == 0 && s.D[o + 0xc + 2 * 10] == 0, "rename: NUL and zeros after the name");
            Throws(() => s.SetName(k, "elevenchars"), "11 characters refused");
            Check(s.Tag(k).Name == "Renamed", "a refused name leaves the entry as it was");
            Throws(() => s.Delete(0), "entry 0 not deleted");
            s.D[o + 0x100] = 7;
            uint n = s.ChangeCounter;
            s.Delete(k);
            Check(s.ChangeCounter == n + 1 && !s.Tag(k).InUse && s.D[o + 0x100] == 0 && s.Tag(k).Controls.SequenceEqual(NameTagSave.DefaultControls), "delete as the game leaves a tag");
            for (int i = 1; i < NameTagSave.Slots; i++) s.D[NameTagSave.Offset(i)] = 1;
            Throws(() => s.Create("Full"), "61st tag refused");
        }

        static string SavesFolder(string name, byte[] data)
        {
            string saves = Path.Combine(Tmp, name);
            Directory.CreateDirectory(Path.Combine(saves, "save", "save_data"));
            File.WriteAllBytes(NameTagFile.SaveFile(saves), data);
            return saves;
        }

        static void Files()
        {
            string saves = SavesFolder("files", MadeUp(3));
            string file = NameTagFile.SaveFile(saves);
            byte[] before = File.ReadAllBytes(file);
            Throws(() => NameTagFile.Edit(saves, () => true, s => s.Create("A")), "refused while the game runs");
            Check(File.ReadAllBytes(file).SequenceEqual(before), "nothing written while the game runs");
            File.WriteAllText(NameTagFile.CommitFile(saves), "");
            Throws(() => NameTagFile.Edit(saves, () => false, s => s.Create("A")), "refused with save.commit");
            File.Delete(NameTagFile.CommitFile(saves));
            Check(NameTagFile.Edit(saves, () => false, s => { }) == null && !Directory.Exists(Path.Combine(saves, "save-backups")), "no change: no write, no copy");
            string bak = NameTagFile.Edit(saves, () => false, s => s.Create("A"));
            Check(bak != null && File.Exists(bak) && File.ReadAllBytes(bak).SequenceEqual(before), "a copy of the old save in save-backups");
            Check(!File.Exists(file + ".tmp"), "no temporary file left");
            Check(NameTagFile.Read(saves).Named().Single().Name == "A", "written");
            Check(NameTagFile.Edit(saves, () => false, s => s.SetName(1, "B")) == null && Directory.GetFiles(Path.Combine(saves, "save-backups")).Length == 1, "one copy per run");
            Check(NameTagFile.Read(saves).Tag(1).Name == "B", "second write");
            Throws(() => NameTagFile.Read(Path.Combine(Tmp, "none")), "no save");
        }

        // ------------------------------------------------------------ the same edits through save_tool.py
        static string Python()
        {
            string p = Environment.GetEnvironmentVariable("SSBU_PYTHON");
            if (!string.IsNullOrEmpty(p)) return p;
            try { using (var pr = Process.Start(new ProcessStartInfo("python", "--version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) { pr.WaitForExit(10000); return pr.ExitCode == 0 ? "python" : null; } }
            catch { return null; }
        }

        static string SaveTool()
        {
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            {
                string f = Path.Combine(d.FullName, "tools", "save", "save_tool.py");
                if (File.Exists(f)) return f;
            }
            return null;
        }

        static int step;
        static string Py(string py, string tool, string input, params string[] args)
        {
            string output = Path.Combine(Tmp, "py_" + (++step) + ".bin");
            var psi = new ProcessStartInfo(py, "-I \"" + tool + "\" " + args[0] + " \"" + input + "\" " + string.Join(" ", args.Skip(1).Select(a => "\"" + a + "\"")) + " --out \"" + output + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                string err = p.StandardError.ReadToEnd(); p.StandardOutput.ReadToEnd(); p.WaitForExit();
                if (p.ExitCode != 0) throw new Exception("save_tool.py " + args[0] + ": " + err);
            }
            return output;
        }

        static string Json(Dictionary<string, object> v)
        {
            string f = Path.Combine(Tmp, "edit_" + (++step) + ".json");
            File.WriteAllText(f, new JavaScriptSerializer().Serialize(v), new UTF8Encoding(false));
            return f;
        }

        static void Parity()
        {
            string py = Python(), tool = SaveTool();
            if (py == null || tool == null) { Console.WriteLine("SKIP: python or tools/save/save_tool.py not found: no parity check"); return; }
            var inputs = new List<(string name, string path)>();
            string made = Path.Combine(Tmp, "madeup.bin"); File.WriteAllBytes(made, MadeUp(4)); inputs.Add(("made-up", made));
            string folder = Environment.GetEnvironmentVariable("SSBU_SAVE_SAMPLES");
            var originals = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                foreach (string f in Directory.GetFiles(folder, "*.bin", SearchOption.AllDirectories))
                {
                    if (new FileInfo(f).Length != NameTagSave.Size) continue;
                    originals[f] = Sha(f);
                    string copy = Path.Combine(Tmp, "sample_" + inputs.Count + ".bin");
                    File.Copy(f, copy);
                    File.SetAttributes(copy, FileAttributes.Normal);
                    inputs.Add((f.Substring(folder.Length).TrimStart('\\', '/'), copy));
                }
            else Console.WriteLine("note: SSBU_SAVE_SAMPLES not set: parity on a made-up save only");
            foreach (var (name, path) in inputs) ParityOn(py, tool, name, path);
            foreach (var kv in originals) Check(Sha(kv.Key) == kv.Value, "original sample unchanged: " + kv.Key);
            Console.WriteLine("parity with save_tool.py: " + inputs.Count + " save(s)");
        }

        static void ParityOn(string py, string tool, string name, string path)
        {
            var s = NameTagSave.Load(path);
            string cur = path;
            void Same(string what) => Check(File.ReadAllBytes(cur).SequenceEqual(s.D), name + ": " + what + ": the same bytes as save_tool.py");
            // 1. a new tag (the launcher's New name tag)
            int k = s.Create("Arena 1");
            cur = Py(py, tool, cur, "set-profile", "--slot", k.ToString(), "--json", Json(new Dictionary<string, object> { { "in_use", true }, { "name", "Arena 1" }, { "raw_controls", NameTagSave.ToHex(NameTagSave.DefaultControls) } }));
            Same("new tag");
            // 2. its controls: Pro B = jump, X = special, GameCube C stick = attack, a Joy-Con button = an unknown code kept (13)
            var c = s.Tag(k).Controls;
            c[NameTagSave.Kinds[1].First + 8] = 2; c[NameTagSave.Kinds[1].First + 10] = 1; c[NameTagSave.Kinds[0].First + 8] = 0; c[NameTagSave.Kinds[2].First + 4] = 13;
            s.SetControls(k, c);
            cur = Py(py, tool, cur, "set-profile", "--slot", k.ToString(), "--json", Json(new Dictionary<string, object> { { "raw_controls", NameTagSave.ToHex(c) } }));
            Same("controls");
            // 3. the same controls again: no change on either side
            s.SetControls(k, c);
            cur = Py(py, tool, cur, "set-profile", "--slot", k.ToString(), "--json", Json(new Dictionary<string, object> { { "raw_controls", NameTagSave.ToHex(c) } }));
            Same("unchanged controls");
            // 4. rename, with characters outside ASCII
            s.SetName(k, "Nicolò★ 2");
            cur = Py(py, tool, cur, "set-profile", "--slot", k.ToString(), "--json", Json(new Dictionary<string, object> { { "name", "Nicolò★ 2" } }));
            Same("rename");
            // 5. a tag the save already had: its controls and its name
            var own = s.Named().FirstOrDefault(t => t.Slot != k);
            if (own != null)
            {
                var oc = own.Controls; oc[NameTagSave.Kinds[1].First + 3] = 4;
                s.SetControls(own.Slot, oc);
                cur = Py(py, tool, cur, "set-profile", "--slot", own.Slot.ToString(), "--json", Json(new Dictionary<string, object> { { "raw_controls", NameTagSave.ToHex(oc) } }));
                Same("existing tag's controls");
                s.SetName(own.Slot, "X");
                cur = Py(py, tool, cur, "set-profile", "--slot", own.Slot.ToString(), "--json", Json(new Dictionary<string, object> { { "name", "X" } }));
                Same("existing tag's name");
            }
            // 6. a second new tag, then the first deleted
            int k2 = s.Create("Second");
            cur = Py(py, tool, cur, "set-profile", "--slot", k2.ToString(), "--json", Json(new Dictionary<string, object> { { "in_use", true }, { "name", "Second" }, { "raw_controls", NameTagSave.ToHex(NameTagSave.DefaultControls) } }));
            Same("second new tag");
            s.Delete(k);
            cur = Py(py, tool, cur, "delete-profile", "--slot", k.ToString());
            Same("delete");
            // 7. a new tag takes the deleted entry again
            Check(s.Create("Again") == k, name + ": the deleted entry is the first free one");
            cur = Py(py, tool, cur, "set-profile", "--slot", k.ToString(), "--json", Json(new Dictionary<string, object> { { "in_use", true }, { "name", "Again" }, { "raw_controls", NameTagSave.ToHex(NameTagSave.DefaultControls) } }));
            Same("new tag in a deleted entry");
        }

        static string Sha(string f) { using (var h = SHA256.Create()) using (var s = File.OpenRead(f)) return Convert.ToBase64String(h.ComputeHash(s)); }
    }
}
