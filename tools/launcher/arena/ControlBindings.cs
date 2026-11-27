// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.78, Controls v3) the logic of "click an action, press a button" (MainWindow.ControlsV3.cs draws it), kept free of WPF so
// arena_tests can run it. Two layers carry a binding:
//   - the game's own controls (the save's name tag, NameTags.cs): one action per Switch button, per controller family (the Pro
//     Controller block for PC pads and the keyboard, the GameCube block for GameCube adapters). A pad press arrives as the
//     Switch button it presses (runtime --pad-live, after controllers.ini), so binding = writing the action at that button;
//   - the physical layer (controllers.ini): used only where the game's layer cannot say it: the stick presses (L3 / R3, no
//     action of their own in the game) are added as one more source of a Switch button that has the action ([type ...]
//     map.<button> = <its source>, <stick press>), and the keyboard's keys ([keyboard] <button> = <key>), since any key can
//     be bound there and the keyboard shares the Pro block with the pads.
using System;
using System.Collections.Generic;
using System.Linq;

namespace Arena
{
    /// <summary>which block of the save's controls a controller plays with</summary>
    public enum PadFamily { Pro, GameCube }

    /// <summary>the Switch buttons as --pad-live reports them (runtime nnpad_sdl.c g_pro bits), and the right stick pushed (16)</summary>
    public static class SwBtn
    {
        public const int A = 0, B = 1, X = 2, Y = 3, StickL = 4, StickR = 5, L = 6, R = 7, ZL = 8, ZR = 9, Plus = 10, Minus = 11, Left = 12, Up = 13, Right = 14, Down = 15, RStick = 16;
        /// <summary>controllers.ini names (map.<name>, [keyboard] <name>), index = button; then the stick directions of [keyboard] (16..23)</summary>
        public static readonly string[] Ini = { "A", "B", "X", "Y", "STICK_L", "STICK_R", "L", "R", "ZL", "ZR", "PLUS", "MINUS", "LEFT", "UP", "RIGHT", "DOWN",
                                                "LSTICK_LEFT", "LSTICK_UP", "LSTICK_RIGHT", "LSTICK_DOWN", "RSTICK_LEFT", "RSTICK_UP", "RSTICK_RIGHT", "RSTICK_DOWN" };
        /// <summary>the keyboard's built-in keys (nnpad_sdl.c g_kb_default), same order</summary>
        public static readonly string[] KeyDefaults = { "Z", "X", "C", "V", "none", "none", "Q", "E", "A", "S", "ENTER", "BACKSPACE", "J", "I", "L", "K",
                                                        "LEFT", "UP", "RIGHT", "DOWN", "none", "none", "none", "none" };
        public const int LStickLeft = 16, LStickUp = 17, LStickRight = 18, LStickDown = 19, RStickLeft = 20, RStickUp = 21, RStickRight = 22, RStickDown = 23;
    }

    /// <summary>the game's actions (the save's codes, NameTagSave.Actions) in the page's order</summary>
    public static class GameAction
    {
        public const int Attack = 0, Special = 1, Jump = 2, Shield = 3, Grab = 4, Smash = 5, UpTaunt = 10, SideTaunt = 11, DownTaunt = 12;
        public static readonly int[] Main = { Attack, Special, Jump, Shield, Grab, Smash, UpTaunt, SideTaunt, DownTaunt };
        public static string Name(int code) => NameTagSave.Actions.TryGetValue(code, out var n) ? n : "Other (" + code + ")";
    }

    /// <summary>one button of a family's block in the save: its place, its name, the Switch buttons that press it and the keyboard
    /// keys ([keyboard] indexes) that press it</summary>
    public sealed class CtlSlot
    {
        public int Index; public string Name; public int[] Sw; public int[] Keys;
        public CtlSlot(int index, string name, int[] sw, int[] keys) { Index = index; Name = name; Sw = sw; Keys = keys; }
    }

    public static class CtlLayout
    {
        static int[] I(params int[] a) => a;
        static readonly CtlSlot[] Pro = {
            new CtlSlot(0, "L", I(SwBtn.L), I(SwBtn.L)), new CtlSlot(1, "R", I(SwBtn.R), I(SwBtn.R)),
            new CtlSlot(2, "ZL", I(SwBtn.ZL), I(SwBtn.ZL)), new CtlSlot(3, "ZR", I(SwBtn.ZR), I(SwBtn.ZR)),
            new CtlSlot(4, "D-pad up", I(SwBtn.Up), I(SwBtn.Up)), new CtlSlot(5, "D-pad side", I(SwBtn.Left, SwBtn.Right), I(SwBtn.Left, SwBtn.Right)),
            new CtlSlot(6, "D-pad down", I(SwBtn.Down), I(SwBtn.Down)), new CtlSlot(7, "A", I(SwBtn.A), I(SwBtn.A)), new CtlSlot(8, "B", I(SwBtn.B), I(SwBtn.B)),
            new CtlSlot(9, "Right stick", I(SwBtn.RStick), I(SwBtn.RStickUp, SwBtn.RStickLeft, SwBtn.RStickDown, SwBtn.RStickRight)),
            new CtlSlot(10, "X", I(SwBtn.X), I(SwBtn.X)), new CtlSlot(11, "Y", I(SwBtn.Y), I(SwBtn.Y)) };
        static readonly CtlSlot[] Gc = {
            new CtlSlot(0, "L", I(SwBtn.L), I()), new CtlSlot(1, "R", I(SwBtn.R), I()), new CtlSlot(2, "Z", I(SwBtn.ZR), I()),
            new CtlSlot(3, "D-pad up", I(SwBtn.Up), I()), new CtlSlot(4, "D-pad side", I(SwBtn.Left, SwBtn.Right), I()), new CtlSlot(5, "D-pad down", I(SwBtn.Down), I()),
            new CtlSlot(6, "A", I(SwBtn.A), I()), new CtlSlot(7, "B", I(SwBtn.B), I()), new CtlSlot(8, "C stick", I(SwBtn.RStick), I()),
            new CtlSlot(9, "X", I(SwBtn.X), I()), new CtlSlot(10, "Y", I(SwBtn.Y), I()) };

        public static CtlSlot[] Slots(PadFamily f) => f == PadFamily.GameCube ? Gc : Pro;
        /// <summary>the family's block in the 0x2b control bytes (NameTagSave.Kinds)</summary>
        public static ControllerKind Kind(PadFamily f) => NameTagSave.Kinds.First(k => k.Id == (f == PadFamily.GameCube ? "gamecube" : "pro"));
        public static int First(PadFamily f) => Kind(f).First;
        /// <summary>the slot a Switch button presses (null: none of the family's, e.g. + or a stick press)</summary>
        public static CtlSlot SlotOf(PadFamily f, int sw) => Slots(f).FirstOrDefault(s => s.Sw.Contains(sw));
        public static int Code(byte[] controls, PadFamily f, CtlSlot s) => controls[First(f) + s.Index];
    }

    public enum ChipKind { Button, Extra, Key }

    /// <summary>one button shown for an action: a slot of the save (Button), a stick press added in controllers.ini (Extra, on the
    /// Switch button Sw, physical source Source) or a keyboard key ([keyboard] index Key)</summary>
    public sealed class Chip
    {
        public ChipKind Kind; public CtlSlot Slot; public int Sw = -1; public string Source; public int Key = -1; public string Label = "";
        public override string ToString() => Label;
    }

    /// <summary>what a page edits: the controls of one family, the stick presses added to Switch buttons (PC pads), the keyboard's keys
    /// (null for a pad); the names of the controller's buttons come from Label</summary>
    public sealed class CtlState
    {
        public PadFamily Family = PadFamily.Pro;
        public byte[] Controls = (byte[])NameTagSave.DefaultControls.Clone();
        /// <summary>Switch button ini name ("X") -> extra physical sources after its first one ("rightstick")</summary>
        public Dictionary<string, List<string>> Extras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>the keyboard's 24 keys ([keyboard], SwBtn.Ini order); null = this is a pad</summary>
        public string[] Keys;
        /// <summary>the stick presses can be added (a PC pad of the Pro family; not GameCube adapters, not the keyboard)</summary>
        public bool CanExtras = true;
        /// <summary>physical source of a Switch button (controllers.ini [type ...] first source), for the stick presses</summary>
        public Func<int, string> Source = sw => sw == SwBtn.StickL ? "leftstick" : sw == SwBtn.StickR ? "rightstick" : null;
        /// <summary>a button's name on this controller ("B", "Circle", "LT", "Z key")</summary>
        public Func<CtlState, Chip, string> Label = (s, c) => c.Kind == ChipKind.Key ? s.Keys[c.Key] : c.Kind == ChipKind.Extra ? c.Source : c.Slot.Name;

        public bool Keyboard => Keys != null;

        public CtlState Clone()
        {
            var c = (CtlState)MemberwiseClone();
            c.Controls = (byte[])Controls.Clone();
            c.Extras = Extras.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
            c.Keys = Keys?.ToArray();
            return c;
        }

        public int Code(CtlSlot s) => CtlLayout.Code(Controls, Family, s);
        public void SetCode(CtlSlot s, int code) => Controls[CtlLayout.First(Family) + s.Index] = (byte)code;
        public List<string> ExtrasOf(int sw) => Extras.TryGetValue(SwBtn.Ini[sw], out var l) ? l : new List<string>();
    }

    /// <summary>what a press would do, before the player says yes: refused, nothing to do, done at once, or a question (the pressed
    /// button already does something else) with a swap and / or a "keep both" answer</summary>
    public sealed class BindPlan
    {
        public string Refused;                  // the press cannot be used for this (why)
        public bool Already;                    // the pressed button already does it
        public string Pressed = "";             // the pressed button's name
        public string Other;                    // a question: what the pressed button does now ("Jump")
        public CtlState Direct;                 // no question: the result
        public CtlState Swap; public string SwapText;     // the replaced button takes the pressed one's old job
        public CtlState Take; public string TakeText, TakeNote;
        public string Note;                     // shown after a direct result
        public bool Question => Other != null && (Swap != null || Take != null);
    }

    public static class CtlBind
    {
        /// <summary>the buttons shown for an action (keyboard: its keys; pad: the save's slots, then the stick presses on them)</summary>
        public static List<Chip> Chips(CtlState st, int action)
        {
            var list = new List<Chip>();
            foreach (var s in CtlLayout.Slots(st.Family))
            {
                if (st.Code(s) != action) continue;
                if (st.Keyboard) { foreach (int k in s.Keys) list.Add(Named(st, new Chip { Kind = ChipKind.Key, Slot = s, Key = k, Sw = k < 16 ? k : -1 })); continue; }
                list.Add(Named(st, new Chip { Kind = ChipKind.Button, Slot = s, Sw = s.Sw[0] }));
                foreach (int sw in s.Sw) foreach (var src in st.ExtrasOf(sw)) list.Add(Named(st, new Chip { Kind = ChipKind.Extra, Slot = s, Sw = sw, Source = src }));
            }
            return list;
        }

        /// <summary>the keyboard's own rows: the keys of fixed [keyboard] indexes (Move: the left stick, Pause: +)</summary>
        public static List<Chip> KeyChips(CtlState st, params int[] keys) => keys.Select(k => Named(st, new Chip { Kind = ChipKind.Key, Key = k, Sw = k < 16 ? k : -1 })).ToList();

        static Chip Named(CtlState st, Chip c) { c.Label = st.Label(st, c); return c; }

        /// <summary>the slots whose code is not one of the page's actions (shown as "Other")</summary>
        public static List<CtlSlot> Unknown(CtlState st) => CtlLayout.Slots(st.Family).Where(s => !GameAction.Main.Contains(st.Code(s))).ToList();

        /// <summary>what the pressed button does now, by name ("Jump", "Move left", "nothing")</summary>
        public static string KeyUse(CtlState st, int key)
        {
            if (key >= SwBtn.LStickLeft && key <= SwBtn.LStickDown) return new[] { "Move left", "Move up", "Move right", "Move down" }[key - SwBtn.LStickLeft];
            if (key == SwBtn.Plus) return "Pause";
            var slot = CtlLayout.Slots(st.Family).FirstOrDefault(s => s.Keys.Contains(key));
            return slot != null ? GameAction.Name(st.Code(slot)) : key == SwBtn.Minus ? "−" : "a stick press";
        }

        /// <summary>a pad press for an action. replace: the chip clicked (null = add one more button, or the row of an action without one)</summary>
        public static BindPlan PlanPad(CtlState st, int action, Chip replace, int sw)
        {
            var p = new BindPlan();
            if (sw == SwBtn.Plus || sw == SwBtn.Minus) { p.Refused = "That button opens the pause menu: choose another one."; return p; }
            if (sw == SwBtn.StickL || sw == SwBtn.StickR)
            {
                string src = st.CanExtras ? st.Source(sw) : null;
                p.Pressed = sw == SwBtn.StickL ? "the left stick press" : "the right stick press";
                if (src == null) { p.Refused = "Stick presses cannot do an action on this controller."; return p; }
                // the game has no action for a stick press: it presses a button that has the action (controllers.ini)
                int target = replace != null && replace.Kind != ChipKind.Key ? replace.Sw : -1;
                if (target < 0) { var s0 = CtlLayout.Slots(st.Family).FirstOrDefault(s => st.Code(s) == action); target = s0?.Sw[0] ?? -1; }
                if (target < 0 || target == SwBtn.RStick) { p.Refused = GameAction.Name(action) + " needs one normal button first; then the stick press can be added."; return p; }
                if (st.ExtrasOf(target).Contains(src, StringComparer.OrdinalIgnoreCase) && (replace == null || replace.Kind != ChipKind.Extra || string.Equals(replace.Source, src, StringComparison.OrdinalIgnoreCase))) { p.Already = true; return p; }
                var r = st.Clone();
                foreach (var l in r.Extras.Values) l.RemoveAll(x => string.Equals(x, src, StringComparison.OrdinalIgnoreCase));
                if (replace != null && replace.Kind == ChipKind.Extra && replace.Sw >= 0 && r.Extras.TryGetValue(SwBtn.Ini[replace.Sw], out var old)) old.RemoveAll(x => string.Equals(x, replace.Source, StringComparison.OrdinalIgnoreCase));
                if (!r.Extras.TryGetValue(SwBtn.Ini[target], out var list)) r.Extras[SwBtn.Ini[target]] = list = new List<string>();
                list.Add(src);
                foreach (var k in r.Extras.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) r.Extras.Remove(k);
                p.Direct = r;
                p.Note = (sw == SwBtn.StickL ? "Left" : "Right") + " stick press added to " + GameAction.Name(action) + ".";
                return p;
            }
            var t = CtlLayout.SlotOf(st.Family, sw);
            if (t == null) { p.Refused = "That button cannot do an action in the game."; return p; }
            p.Pressed = st.Label(st, new Chip { Kind = ChipKind.Button, Slot = t, Sw = sw });
            int cur = st.Code(t);
            if (cur == action)
            {
                if (replace != null && replace.Kind == ChipKind.Extra)
                {   // the stick press chip replaced by a button that already has the action: the stick press goes
                    var r0 = st.Clone(); RemoveExtra(r0, replace); p.Direct = r0; return p;
                }
                p.Already = true; return p;
            }
            p.Other = GameAction.Name(cur);
            // keep both / use it: only the pressed button changes
            var take = st.Clone(); take.SetCode(t, action);
            if (replace != null && replace.Kind == ChipKind.Extra) RemoveExtra(take, replace);
            bool replacing = replace != null && replace.Kind == ChipKind.Button && replace.Slot != null && replace.Slot.Index != t.Index;
            p.Take = take;
            p.TakeText = replacing ? "Keep both" : "Use " + p.Pressed;
            bool lost = !CtlLayout.Slots(st.Family).Any(s => s.Index != t.Index && st.Code(s) == cur);
            p.TakeNote = replacing ? replace.Label + " and " + p.Pressed + " both do " + GameAction.Name(action) + "; " + (lost ? p.Other + " is left with no button." : p.Other + " keeps its other button.")
                                   : lost ? p.Other + " is left with no button." : p.Other + " keeps its other button.";
            if (replacing)
            {
                var swap = st.Clone(); swap.SetCode(t, action); swap.SetCode(replace.Slot, cur);
                p.Swap = swap;
                p.SwapText = "Swap: " + replace.Label + " does " + p.Other;
            }
            return p;
        }

        static void RemoveExtra(CtlState r, Chip c)
        {
            if (c.Sw >= 0 && r.Extras.TryGetValue(SwBtn.Ini[c.Sw], out var l))
            {
                l.RemoveAll(x => string.Equals(x, c.Source, StringComparison.OrdinalIgnoreCase));
                if (l.Count == 0) r.Extras.Remove(SwBtn.Ini[c.Sw]);
            }
        }

        /// <summary>a key for an action's key (replace: the key chip clicked; null = the action's first key without one, else its first)</summary>
        public static BindPlan PlanKey(CtlState st, int action, Chip replace, string key)
        {
            var p = new BindPlan { Pressed = KeyNames.Display(key) };
            int vk = KeyNames.ToVk(key);
            if (vk == 0) { p.Refused = "That key cannot be used."; return p; }
            int target = replace != null && replace.Kind == ChipKind.Key ? replace.Key : -1;
            if (target < 0)
            {
                var keys = Chips(st, action).Where(c => c.Kind == ChipKind.Key).Select(c => c.Key).ToList();
                if (keys.Count == 0) { p.Refused = GameAction.Name(action) + " has no button in the game's controls: give it one with a controller first."; return p; }
                var free = keys.Where(k => KeyNames.ToVk(st.Keys[k]) == 0).ToList();
                target = free.Count > 0 ? free[0] : keys[0];
            }
            if (KeyNames.ToVk(st.Keys[target]) == vk) { p.Already = true; return p; }
            int other = -1;
            for (int i = 0; i < st.Keys.Length; i++) if (i != target && KeyNames.ToVk(st.Keys[i]) == vk) { other = i; break; }
            if (other < 0) { var r = st.Clone(); r.Keys[target] = KeyNames.Canonical(key); p.Direct = r; return p; }
            p.Other = KeyUse(st, other);
            string oldKey = st.Keys[target];
            var swap = st.Clone(); swap.Keys[target] = KeyNames.Canonical(key); swap.Keys[other] = oldKey;
            p.Swap = swap;
            p.SwapText = KeyNames.ToVk(oldKey) == 0 ? "Move it here" : "Swap: " + KeyNames.Display(oldKey) + " does " + p.Other;
            if (KeyNames.ToVk(oldKey) != 0)
            {
                var take = st.Clone(); take.Keys[target] = KeyNames.Canonical(key); take.Keys[other] = "none";
                p.Take = take; p.TakeText = "Use " + p.Pressed + " only here"; p.TakeNote = p.Other + " is left without that key.";
            }
            return p;
        }

        /// <summary>the whole controller back to the defaults: the family's buttons and its 4 options (the game's defaults), no stick
        /// press added; the keyboard: its built-in keys</summary>
        public static CtlState Reset(CtlState st)
        {
            var r = st.Clone();
            if (r.Keyboard) { r.Keys = SwBtn.KeyDefaults.ToArray(); return r; }
            var k = CtlLayout.Kind(r.Family);
            for (int i = k.First; i < k.Options + 4; i++) r.Controls[i] = NameTagSave.DefaultControls[i];
            r.Extras.Clear();
            return r;
        }

        /// <summary>the controls of the player's own name tag, else the unnamed entry 0 the game uses without a tag (if in use), else the
        /// game's defaults: what player 1 plays with now</summary>
        public static byte[] Effective(NameTagSave s, int mySlot)
        {
            if (s == null) return (byte[])NameTagSave.DefaultControls.Clone();
            if (mySlot > 0) { var t = s.Tag(mySlot); if (t.InUse) return t.Controls; }
            var e0 = s.Tag(0);
            return e0.InUse ? e0.Controls : (byte[])NameTagSave.DefaultControls.Clone();
        }

        public const string TagName = "Arena";

        /// <summary>the name tag the page writes into (s: the save as it is on the disk now): the player's own (mySlot), else the one
        /// called Arena, else a new Arena tag; a tag that was not the player's own starts from what player 1 played with
        /// (Effective). Returns its entry; edit gets the controls to change.</summary>
        public static int WriteControls(NameTagSave s, int mySlot, Func<byte[], byte[]> edit)
        {
            byte[] start = Effective(s, mySlot);
            int slot = mySlot > 0 && s.Tag(mySlot).InUse ? mySlot : -1;
            if (slot < 0)
            {
                var named = s.Named().FirstOrDefault(t => string.Equals(t.Name, TagName, StringComparison.OrdinalIgnoreCase));
                slot = named != null ? named.Slot : s.Create(TagName);
            }
            s.SetControls(slot, edit((byte[])start.Clone()));
            return slot;
        }

        /// <summary>the entry of my_name_tag (<entry>:<hash>) in a save: the same entry with that name, else that name anywhere; -1</summary>
        public static int FindMyTag(NameTagSave s, string value)
        {
            if (s == null || string.IsNullOrEmpty(value)) return -1;
            var parts = value.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int slot)) return -1;
            uint hash;
            try { hash = Convert.ToUInt32(parts[1], 16); } catch { return -1; }
            var tags = s.Named();
            var t = tags.FirstOrDefault(x => x.Slot == slot && NameTagSave.NameHash(x.Name) == hash) ?? tags.FirstOrDefault(x => NameTagSave.NameHash(x.Name) == hash);
            return t?.Slot ?? -1;
        }
    }

    /// <summary>one line of --pad-live for one pad</summary>
    public sealed class PadSample
    {
        public int Player; public ulong Buttons; public int RX, RY, TrigL = -1, TrigR = -1, Kind = 4;
    }

    /// <summary>"Press a button...": waits until the pad shows nothing pressed, then takes the first press. Feed it every sample of
    /// the pads; Expired after the time (5 s).</summary>
    public sealed class PressCapture
    {
        public readonly int Player;
        public readonly DateTime Until;
        bool armed;
        public PressCapture(int player, DateTime now, double seconds = 5) { Player = player; Until = now.AddSeconds(seconds); }
        public bool Expired(DateTime now) => now >= Until;

        static readonly int[] Order = { SwBtn.A, SwBtn.B, SwBtn.X, SwBtn.Y, SwBtn.L, SwBtn.R, SwBtn.ZL, SwBtn.ZR, SwBtn.Up, SwBtn.Left, SwBtn.Right, SwBtn.Down, SwBtn.Plus, SwBtn.Minus };

        /// <summary>the button a sample shows pressed (-1 none). A stick press first (an added stick press also presses its button),
        /// the right stick pushed far (16), a GameCube trigger pressed deep (L / R)</summary>
        public static int Pressed(PadSample s)
        {
            if ((s.Buttons >> SwBtn.StickL & 1) != 0) return SwBtn.StickL;
            if ((s.Buttons >> SwBtn.StickR & 1) != 0) return SwBtn.StickR;
            foreach (int b in Order) if ((s.Buttons >> b & 1) != 0) return b;
            if (Math.Sqrt((double)s.RX * s.RX + (double)s.RY * s.RY) >= 0.6 * 32767) return SwBtn.RStick;
            if (s.Kind == 3 && s.TrigL >= 0.85 * 32767) return SwBtn.L;
            if (s.Kind == 3 && s.TrigR >= 0.85 * 32767) return SwBtn.R;
            return -1;
        }

        static bool Held(PadSample s) => Pressed(s) >= 0 || Math.Sqrt((double)s.RX * s.RX + (double)s.RY * s.RY) > 0.3 * 32767
                                         || (s.Kind == 3 && (s.TrigL > 0.5 * 32767 || s.TrigR > 0.5 * 32767));

        /// <summary>-1 = nothing yet, else the button pressed</summary>
        public int Feed(PadSample s)
        {
            if (s == null || s.Player != Player) return -1;
            if (!armed) { if (!Held(s)) armed = true; return -1; }
            return Pressed(s);
        }
    }

    /// <summary>[keyboard] key names as the runtime reads them (nnpad_sdl.c parse_vk)</summary>
    public static class KeyNames
    {
        public const int Esc = 0x1B;

        /// <summary>keys that never end a "press a key": the locks (Num / Caps / Scroll Lock, also sent by some tools around a key),
        /// the Windows and menu keys, a key Windows does not name</summary>
        public static bool Ignored(int vk) => vk <= 0 || vk == 0x90 || vk == 0x91 || vk == 0x14 || vk == 0x5B || vk == 0x5C || vk == 0x5D || vk == 0xFF || vk == 0xE7;

        /// <summary>a Windows virtual key -> its controllers.ini name</summary>
        public static string FromVk(int vk)
        {
            if ((vk >= 'A' && vk <= 'Z') || (vk >= '0' && vk <= '9')) return ((char)vk).ToString();
            switch (vk)
            {
                case 0x25: return "LEFT"; case 0x26: return "UP"; case 0x27: return "RIGHT"; case 0x28: return "DOWN";
                case 0x0D: return "ENTER"; case 0x08: return "BACKSPACE"; case 0x20: return "SPACE"; case 0x09: return "TAB"; case 0x1B: return "ESC";
                case 0x10: case 0xA0: case 0xA1: return "SHIFT";
                case 0x11: case 0xA2: case 0xA3: return "CTRL";
                case 0x12: case 0xA4: case 0xA5: return "ALT";
            }
            if (vk >= 0x70 && vk <= 0x7B) return "F" + (vk - 0x70 + 1);
            if (vk >= 0x60 && vk <= 0x69) return "NUM" + (vk - 0x60);
            return "0x" + vk.ToString("X2");
        }

        /// <summary>a name -> the virtual key the runtime reads (0 = none / not known)</summary>
        public static int ToVk(string name)
        {
            string b = (name ?? "").Trim();
            int sc = b.IndexOfAny(new[] { ';', '#' });
            if (sc > 0) b = b.Substring(0, sc).Trim();
            if (b == "" || b.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;
            if (b.Length == 1) return char.ToUpperInvariant(b[0]);
            switch (b.ToUpperInvariant())
            {
                case "LEFT": return 0x25; case "UP": return 0x26; case "RIGHT": return 0x27; case "DOWN": return 0x28;
                case "ENTER": return 0x0D; case "BACKSPACE": return 0x08; case "SPACE": return 0x20; case "TAB": return 0x09; case "ESC": return 0x1B;
                case "SHIFT": return 0x10; case "CTRL": return 0x11; case "ALT": return 0x12;
            }
            if ((b[0] == 'F' || b[0] == 'f') && int.TryParse(b.Substring(1), out int f) && f >= 1 && f <= 12) return 0x70 + f - 1;
            if (b.StartsWith("NUM", StringComparison.OrdinalIgnoreCase) && b.Length == 4 && char.IsDigit(b[3])) return 0x60 + (b[3] - '0');
            if (b.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { try { return Convert.ToInt32(b.Substring(2), 16); } catch { return 0; } }
            return 0;
        }

        /// <summary>the name written to the file (one spelling per key)</summary>
        public static string Canonical(string name) { int vk = ToVk(name); return vk == 0 ? "none" : FromVk(vk); }

        /// <summary>the name shown on the page</summary>
        public static string Display(string name)
        {
            int vk = ToVk(name);
            if (vk == 0) return "—";
            switch (vk)
            {
                case 0x25: return "←"; case 0x26: return "↑"; case 0x27: return "→"; case 0x28: return "↓";
                case 0x0D: return "Enter"; case 0x08: return "Backspace"; case 0x20: return "Space"; case 0x09: return "Tab"; case 0x1B: return "Esc";
                case 0x10: return "Shift"; case 0x11: return "Ctrl"; case 0x12: return "Alt";
            }
            if (vk >= 0x60 && vk <= 0x69) return "Num " + (vk - 0x60);
            string n = FromVk(vk);
            return n.StartsWith("0x") ? "Key " + n : n;
        }

        /// <summary>[keyboard] as read (lower-case keys) -> the 24 keys, the built-in ones where a key is missing</summary>
        public static string[] FromIni(IDictionary<string, string> section)
        {
            var keys = SwBtn.KeyDefaults.ToArray();
            for (int i = 0; i < keys.Length; i++)
                if (section != null && section.TryGetValue(SwBtn.Ini[i].ToLowerInvariant(), out var v)) keys[i] = Canonical(v);
            return keys;
        }
    }

    /// <summary>controllers.ini [type ...] map.<button> values: the first source is the pad button the launcher's button list shows, the
    /// others were added (stick presses)</summary>
    public static class PhysMap
    {
        public static void Parse(string value, out string first, out List<string> extras)
        {
            var parts = (value ?? "").Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x != "").ToList();
            first = parts.FirstOrDefault();
            extras = parts.Skip(1).Distinct().ToList();
        }
        public static string Format(string first, IEnumerable<string> extras) => string.Join(", ", new[] { first }.Concat(extras ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrEmpty(x)).Distinct());
    }

    /// <summary>the names of a controller's buttons: kind as --pad-live (0 Xbox, 1 PlayStation, 2 Switch, 3 GameCube, 4 other pad,
    /// 5 joystick); a PC pad's face buttons, bumpers and triggers through the launcher's button map (Switch button -> source)</summary>
    public static class PadNames
    {
        public static string Kind(int kind) => kind == 0 ? "Xbox controller" : kind == 1 ? "PlayStation controller" : kind == 2 ? "Switch controller" : kind == 3 ? "GameCube controller" : kind == 5 ? "Joystick" : "Controller";

        public static string Source(int kind, string src)
        {
            switch ((src ?? "").ToLowerInvariant())
            {
                case "south": return kind == 0 ? "A" : kind == 1 ? "Cross" : kind == 2 ? "B" : "Bottom button";
                case "east": return kind == 0 ? "B" : kind == 1 ? "Circle" : kind == 2 ? "A" : "Right button";
                case "west": return kind == 0 ? "X" : kind == 1 ? "Square" : kind == 2 ? "Y" : "Left button";
                case "north": return kind == 0 ? "Y" : kind == 1 ? "Triangle" : kind == 2 ? "X" : "Top button";
                case "leftshoulder": return kind == 0 ? "LB" : kind == 1 ? "L1" : kind == 2 ? "L" : "Left bumper";
                case "rightshoulder": return kind == 0 ? "RB" : kind == 1 ? "R1" : kind == 2 ? "R" : "Right bumper";
                case "lefttrigger": return kind == 0 ? "LT" : kind == 1 ? "L2" : kind == 2 ? "ZL" : "Left trigger";
                case "righttrigger": return kind == 0 ? "RT" : kind == 1 ? "R2" : kind == 2 ? "ZR" : "Right trigger";
                case "leftstick": return kind == 1 ? "L3" : "Left stick press";
                case "rightstick": return kind == 1 ? "R3" : "Right stick press";
                case "start": return kind == 0 ? "Menu" : kind == 1 ? "Options" : kind == 2 ? "+" : "Start";
                case "back": return kind == 0 ? "View" : kind == 1 ? "Share" : kind == 2 ? "−" : "Back";
            }
            return src ?? "?";
        }

        /// <summary>a Switch button's name on this controller; map: Switch button ini name -> its source (PC pads)</summary>
        public static string Button(int kind, int sw, Func<string, string> map)
        {
            switch (sw)
            {
                case SwBtn.Up: return "D-pad ↑";
                case SwBtn.Down: return "D-pad ↓";
                case SwBtn.Left: case SwBtn.Right: return "D-pad ← →";
                case SwBtn.RStick: return kind == 3 ? "C-stick" : "Right stick";
            }
            if (kind == 3)
                switch (sw)
                {
                    case SwBtn.A: return "A"; case SwBtn.B: return "B"; case SwBtn.X: return "X"; case SwBtn.Y: return "Y";
                    case SwBtn.L: return "L"; case SwBtn.R: return "R"; case SwBtn.ZR: return "Z"; case SwBtn.ZL: return "ZL"; case SwBtn.Plus: return "Start";
                }
            if (sw >= 0 && sw < 12) { string src = map?.Invoke(SwBtn.Ini[sw]); if (src != null) return Source(kind, src); }
            return sw >= 0 && sw < 16 ? SwBtn.Ini[sw] : "?";
        }
    }
}
