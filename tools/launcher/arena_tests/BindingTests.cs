// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.78, Controls v3) tests of tools/launcher/arena/ControlBindings.cs: the binding logic (swap, keep both, conflicts, stick presses,
// keyboard keys, reset), how a binding lands in the save (WriteControls on made-up saves), and the press capture with a fake input
// source (PadSample lines as --pad-live gives them).
using System;
using System.Collections.Generic;
using System.Linq;

namespace Arena.Tests
{
    static partial class Program
    {
        static CtlState Pad(PadFamily f = PadFamily.Pro)
        {
            // labels as an Xbox pad with the launcher's default button map (A = east ...)
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
                { "A", "east" }, { "B", "south" }, { "X", "north" }, { "Y", "west" }, { "L", "leftshoulder" }, { "R", "rightshoulder" },
                { "ZL", "lefttrigger" }, { "ZR", "righttrigger" }, { "PLUS", "start" }, { "MINUS", "back" }, { "STICK_L", "leftstick" }, { "STICK_R", "rightstick" } };
            int kind = f == PadFamily.GameCube ? 3 : 0;
            return new CtlState
            {
                Family = f, CanExtras = f == PadFamily.Pro,
                Source = sw => sw >= 0 && sw < 12 && map.TryGetValue(SwBtn.Ini[sw], out var s) ? s : null,
                Label = (s, c) => c.Kind == ChipKind.Extra ? PadNames.Source(kind, c.Source) : PadNames.Button(kind, c.Sw, ini => map.TryGetValue(ini, out var v) ? v : null)
            };
        }

        static CtlState Kb() => new CtlState { Keys = SwBtn.KeyDefaults.ToArray(), CanExtras = false, Label = (s, c) => KeyNames.Display(s.Keys[c.Key]) };

        static string Labels(CtlState st, int action) => string.Join(",", CtlBind.Chips(st, action).Select(c => c.Label));
        static int CodeAt(CtlState st, int sw) => st.Code(CtlLayout.SlotOf(st.Family, sw));

        static void Bindings()
        {
            // the layout: the save's order (SAVE_FORMAT.txt 4) and the Switch buttons
            Check(CtlLayout.Slots(PadFamily.Pro).Length == 12 && CtlLayout.Slots(PadFamily.GameCube).Length == 11, "slots: 12 Pro, 11 GameCube");
            Check(CtlLayout.SlotOf(PadFamily.Pro, SwBtn.X).Index == 10 && CtlLayout.SlotOf(PadFamily.Pro, SwBtn.RStick).Index == 9, "Pro X at 10, right stick at 9");
            Check(CtlLayout.SlotOf(PadFamily.Pro, SwBtn.Left) == CtlLayout.SlotOf(PadFamily.Pro, SwBtn.Right), "D-pad left and right: one slot (side)");
            Check(CtlLayout.SlotOf(PadFamily.GameCube, SwBtn.ZR).Name == "Z" && CtlLayout.SlotOf(PadFamily.GameCube, SwBtn.ZL) == null, "GameCube: Z is ZR, no ZL");
            Check(CtlLayout.SlotOf(PadFamily.Pro, SwBtn.Plus) == null && CtlLayout.SlotOf(PadFamily.Pro, SwBtn.StickR) == null, "+ and stick presses: not in the save");
            Check(CtlLayout.First(PadFamily.Pro) == 0x0F && CtlLayout.First(PadFamily.GameCube) == 0, "block offsets");

            // the default controls shown with an Xbox pad's names
            var st = Pad();
            Check(Labels(st, GameAction.Attack) == "B" && Labels(st, GameAction.Special) == "A", "defaults: attack on Xbox B, special on Xbox A (Nintendo positions)");
            Check(Labels(st, GameAction.Jump) == "Y,X" && Labels(st, GameAction.Shield) == "LT,RT" && Labels(st, GameAction.Grab) == "LB,RB", "defaults: jump, shield, grab");
            Check(Labels(st, GameAction.Smash) == "Right stick" && Labels(st, GameAction.SideTaunt) == "D-pad ← →", "defaults: smash, side taunt");
            Check(CtlBind.Unknown(st).Count == 0, "defaults: no unknown code");
            var gc = Pad(PadFamily.GameCube);
            Check(Labels(gc, GameAction.Shield) == "L,R" && Labels(gc, GameAction.Grab) == "Z" && Labels(gc, GameAction.Smash) == "C-stick", "GameCube defaults");

            // swap: Attack's B (Switch A) replaced by Xbox Y (Switch X, jump)
            var attackChip = CtlBind.Chips(st, GameAction.Attack)[0];
            var p = CtlBind.PlanPad(st, GameAction.Attack, attackChip, SwBtn.X);
            Check(p.Question && p.Other == "Jump" && p.Pressed == "Y", "a used button asks: Y already does Jump");
            Check(p.Swap != null && CodeAt(p.Swap, SwBtn.X) == GameAction.Attack && CodeAt(p.Swap, SwBtn.A) == GameAction.Jump, "swap: X attacks, A jumps");
            Check(p.Swap.Controls.Where((b, i) => b != st.Controls[i]).Count() == 2, "swap: only the two buttons change");
            Check(p.Take != null && CodeAt(p.Take, SwBtn.X) == GameAction.Attack && CodeAt(p.Take, SwBtn.A) == GameAction.Attack, "keep both: A and X attack");
            Check(p.TakeNote.Contains("Jump keeps its other button"), "keep both: jump keeps Y");
            Check(st.Controls.SequenceEqual(NameTagSave.DefaultControls), "a plan does not change the state shown");
            Check(p.SwapText == "Swap: B does Jump", "swap text names the replaced button");

            // conflict that leaves an action without a button
            p = CtlBind.PlanPad(st, GameAction.Attack, attackChip, SwBtn.B);
            Check(p.Question && p.Other == "Special" && p.TakeNote.Contains("Special is left with no button"), "keep both warns: Special left with no button");
            Check(CtlBind.Chips(p.Take, GameAction.Special).Count == 0 && CtlBind.Chips(p.Swap, GameAction.Special).Count == 1, "swap keeps every action reachable");

            // add one more button (+): no swap offered, the pressed button just takes it
            p = CtlBind.PlanPad(st, GameAction.Jump, null, SwBtn.L);
            Check(p.Question && p.Swap == null && p.Take != null && Labels(p.Take, GameAction.Jump) == "LB,Y,X" && Labels(p.Take, GameAction.Grab) == "RB", "add: LB jumps too, grab keeps RB");

            // nothing to do / refused
            Check(CtlBind.PlanPad(st, GameAction.Jump, null, SwBtn.Y).Already, "the button already does it");
            Check(CtlBind.PlanPad(st, GameAction.Jump, null, SwBtn.Plus).Refused != null && CtlBind.PlanPad(st, GameAction.Jump, null, SwBtn.Minus).Refused != null, "+ and - refused");
            Check(CtlBind.PlanPad(st, GameAction.Smash, CtlBind.Chips(st, GameAction.Smash)[0], SwBtn.X).Question, "the right stick slot takes a button");
            p = CtlBind.PlanPad(st, GameAction.Smash, null, SwBtn.RStick);
            Check(p.Already, "the right stick already does smash attacks");

            // stick presses: the physical layer (one more source of a button that has the action)
            p = CtlBind.PlanPad(st, GameAction.Jump, null, SwBtn.StickR);
            Check(p.Direct != null && p.Direct.ExtrasOf(SwBtn.X).SequenceEqual(new[] { "rightstick" }) && p.Direct.Controls.SequenceEqual(st.Controls), "R3 for jump: added to X in controllers.ini, the save unchanged");
            Check(Labels(p.Direct, GameAction.Jump) == "Y,Right stick press,X", "R3 shown with jump");
            var withR3 = p.Direct;
            Check(CtlBind.PlanPad(withR3, GameAction.Jump, null, SwBtn.StickR).Already, "R3 again: already");
            p = CtlBind.PlanPad(withR3, GameAction.Attack, null, SwBtn.StickR);
            Check(p.Direct != null && p.Direct.ExtrasOf(SwBtn.A).SequenceEqual(new[] { "rightstick" }) && p.Direct.ExtrasOf(SwBtn.X).Count == 0 && !p.Direct.Extras.ContainsKey("X"), "R3 moved to attack: one place only");
            var r3chip = CtlBind.Chips(withR3, GameAction.Jump).First(c => c.Kind == ChipKind.Extra);
            p = CtlBind.PlanPad(withR3, GameAction.Jump, r3chip, SwBtn.StickL);
            Check(p.Direct != null && p.Direct.ExtrasOf(SwBtn.X).SequenceEqual(new[] { "leftstick" }), "the R3 chip replaced by L3");
            p = CtlBind.PlanPad(withR3, GameAction.Jump, r3chip, SwBtn.Y);
            Check(p.Direct != null && p.Direct.Extras.Count == 0 && p.Direct.Controls.SequenceEqual(st.Controls), "the R3 chip replaced by a button that already jumps: R3 goes");
            Check(CtlBind.PlanPad(gc, GameAction.Jump, null, SwBtn.StickR).Refused != null, "GameCube: no stick press");
            var noJump = st.Clone();
            foreach (var s in CtlLayout.Slots(PadFamily.Pro)) if (noJump.Code(s) == GameAction.Jump) noJump.SetCode(s, GameAction.Attack);
            Check(CtlBind.PlanPad(noJump, GameAction.Jump, null, SwBtn.StickR).Refused != null, "a stick press needs a button with the action");

            // GameCube: Z pressed for shield, swap with L
            var lChip = CtlBind.Chips(gc, GameAction.Shield)[0];
            p = CtlBind.PlanPad(gc, GameAction.Shield, lChip, SwBtn.ZR);
            Check(p.Pressed == "Z" && p.Other == "Grab" && p.Swap.Controls[2] == GameAction.Shield && p.Swap.Controls[0] == GameAction.Grab, "GameCube swap Z / L in the GameCube block");
            Check(p.Swap.Controls.Skip(0x0F).SequenceEqual(gc.Controls.Skip(0x0F)), "GameCube edit leaves the Pro and Joy-Con blocks");

            // unknown codes stay and are shown as Other
            var odd = st.Clone(); odd.SetCode(CtlLayout.SlotOf(PadFamily.Pro, SwBtn.Up), 7);
            Check(CtlBind.Unknown(odd).Single().Name == "D-pad up" && CtlBind.Chips(odd, GameAction.UpTaunt).Count == 0, "an unknown code: Other");
            p = CtlBind.PlanPad(odd, GameAction.UpTaunt, null, SwBtn.Up);
            Check(p.Other == "Other (7)", "pressing it names the unknown code");

            // keyboard: keys only ([keyboard]), the save never
            var kb = Kb();
            Check(Labels(kb, GameAction.Attack) == "Z" && Labels(kb, GameAction.Jump) == "C,V" && Labels(kb, GameAction.SideTaunt) == "J,L" && Labels(kb, GameAction.Smash) == "—,—,—,—", "keyboard defaults");
            var zChip = CtlBind.Chips(kb, GameAction.Attack)[0];
            p = CtlBind.PlanKey(kb, GameAction.Attack, zChip, "SPACE");
            Check(p.Direct != null && p.Direct.Keys[SwBtn.A] == "SPACE" && p.Direct.Controls.SequenceEqual(kb.Controls), "a free key: at once");
            p = CtlBind.PlanKey(kb, GameAction.Attack, zChip, "c");
            Check(p.Question && p.Other == "Jump" && p.Swap.Keys[SwBtn.A] == "C" && p.Swap.Keys[SwBtn.X] == "Z", "a used key: swap");
            Check(p.Take.Keys[SwBtn.A] == "C" && p.Take.Keys[SwBtn.X] == "none", "a used key: take it");
            p = CtlBind.PlanKey(kb, GameAction.Attack, zChip, "LEFT");
            Check(p.Other == "Move left", "a movement key named");
            Check(CtlBind.PlanKey(kb, GameAction.Attack, zChip, "Z").Already, "the same key: already");
            var smash = CtlBind.Chips(kb, GameAction.Smash);
            p = CtlBind.PlanKey(kb, GameAction.Smash, null, "T");
            Check(p.Direct != null && p.Direct.Keys[SwBtn.RStickUp] == "T", "smash attack on the keyboard: the first free direction (up)");
            var move = CtlBind.KeyChips(kb, SwBtn.LStickLeft, SwBtn.LStickUp, SwBtn.LStickRight, SwBtn.LStickDown);
            p = CtlBind.PlanKey(kb, -1, move[0], "A");
            Check(p.Question && p.Other == "Shield" && p.Swap.Keys[SwBtn.LStickLeft] == "A" && p.Swap.Keys[SwBtn.ZL] == "LEFT", "move left on A: swaps with shield's key");

            // reset
            var changed = CtlBind.PlanPad(st, GameAction.Attack, attackChip, SwBtn.X).Swap;
            changed = CtlBind.PlanPad(changed, GameAction.Jump, null, SwBtn.StickR).Direct;
            changed.Controls[0] = 9;      // the GameCube block: not this controller's
            var back = CtlBind.Reset(changed);
            Check(back.Controls.Skip(0x0F).Take(16).SequenceEqual(NameTagSave.DefaultControls.Skip(0x0F).Take(16)) && back.Extras.Count == 0, "reset: the Pro block and its options, no stick press");
            Check(back.Controls[0] == 9, "reset: the other blocks stay");
            var kb2 = CtlBind.PlanKey(kb, GameAction.Attack, zChip, "SPACE").Direct;
            Check(CtlBind.Reset(kb2).Keys.SequenceEqual(SwBtn.KeyDefaults), "reset: keyboard keys");

            // key names as the runtime reads them
            Check(KeyNames.FromVk('Q') == "Q" && KeyNames.FromVk(0x25) == "LEFT" && KeyNames.FromVk(0xA0) == "SHIFT" && KeyNames.FromVk(0x70) == "F1" && KeyNames.FromVk(0x63) == "NUM3" && KeyNames.FromVk(0xBC) == "0xBC", "virtual key -> name");
            foreach (int vk in new[] { 'A', '7', 0x25, 0x26, 0x0D, 0x08, 0x20, 0x09, 0x1B, 0x10, 0x11, 0x12, 0x7B, 0x60, 0xBC })
                Check(KeyNames.ToVk(KeyNames.FromVk(vk)) == vk || (vk == 0xA0), "name -> virtual key " + vk);
            Check(KeyNames.Ignored(0x90) && KeyNames.Ignored(0x14) && KeyNames.Ignored(0x5B) && !KeyNames.Ignored('Z') && !KeyNames.Ignored(0x20), "lock and Windows keys ignored");
            Check(KeyNames.ToVk("z") == 'Z' && KeyNames.ToVk("none") == 0 && KeyNames.ToVk("Z ; comment") == 'Z' && KeyNames.Canonical("enter") == "ENTER", "ini spellings");
            Check(KeyNames.Display("LEFT") == "←" && KeyNames.Display("none") == "—" && KeyNames.Display("SPACE") == "Space", "names shown");
            Check(KeyNames.FromIni(new Dictionary<string, string> { { "a", "space" }, { "player", "1" } })[SwBtn.A] == "SPACE" && KeyNames.FromIni(null)[SwBtn.B] == "X", "[keyboard] read");

            // controllers.ini map values
            PhysMap.Parse("North, rightstick ,rightstick", out string first, out var ex);
            Check(first == "north" && ex.SequenceEqual(new[] { "rightstick" }), "map value parsed");
            Check(PhysMap.Format("north", new[] { "rightstick" }) == "north, rightstick" && PhysMap.Format("east", null) == "east", "map value written");

            // pad names
            Check(PadNames.Button(1, SwBtn.A, ini => "east") == "Circle" && PadNames.Button(2, SwBtn.A, ini => "east") == "A" && PadNames.Button(3, SwBtn.ZR, null) == "Z" && PadNames.Button(0, SwBtn.ZL, ini => "lefttrigger") == "LT", "button names per kind");
        }

        static void Capture()
        {
            var t0 = new DateTime(2026, 11, 27, 12, 0, 0);
            PadSample S(int player, ulong buttons = 0, int rx = 0, int ry = 0, int kind = 0, int tl = -1, int tr = -1) => new PadSample { Player = player, Buttons = buttons, RX = rx, RY = ry, Kind = kind, TrigL = tl, TrigR = tr };
            var c = new PressCapture(1, t0);
            Check(c.Feed(S(1, 1UL << SwBtn.A)) == -1, "a button held when the capture starts is not taken");
            Check(c.Feed(S(1, 1UL << SwBtn.A)) == -1, "still held");
            Check(c.Feed(S(1)) == -1, "released: armed");
            Check(c.Feed(S(2, 1UL << SwBtn.B)) == -1, "another player's pad is ignored");
            Check(c.Feed(S(1, 1UL << SwBtn.X)) == SwBtn.X, "the next press is taken");
            c = new PressCapture(1, t0); c.Feed(S(1));
            Check(c.Feed(S(1, 1UL << SwBtn.StickR | 1UL << SwBtn.X)) == SwBtn.StickR, "a stick press first (it may press its button too)");
            c = new PressCapture(1, t0); c.Feed(S(1));
            Check(c.Feed(S(1, 0, 10000, 0)) == -1 && c.Feed(S(1, 0, 0, -30000)) == SwBtn.RStick, "the right stick pushed far, not a little");
            c = new PressCapture(2, t0); c.Feed(S(2, kind: 3, tl: 0, tr: 0));
            Check(c.Feed(S(2, kind: 3, tl: 32000, tr: 0)) == SwBtn.L, "a GameCube trigger pressed deep: L");
            c = new PressCapture(1, t0);
            Check(c.Feed(S(1, 0, 20000, 0)) == -1 && c.Feed(S(1, 1UL << SwBtn.A)) == -1, "the right stick held at the start: not armed yet");
            Check(!c.Expired(t0.AddSeconds(4.9)) && c.Expired(t0.AddSeconds(5)), "5 s then it ends");
            Check(PressCapture.Pressed(S(1, 1UL << SwBtn.Left)) == SwBtn.Left && PressCapture.Pressed(S(1, 1UL << SwBtn.Plus)) == SwBtn.Plus, "d-pad, plus");
        }

        static void Placement()
        {
            // a binding lands in the save: the player's own name tag, else Arena (made, from what player 1 played with)
            var s = new NameTagSave(MadeUp(21));
            int e0 = NameTagSave.Offset(0);
            s.D[e0] = 1;
            var entry0 = (byte[])NameTagSave.DefaultControls.Clone(); entry0[0x0F + 7] = GameAction.Jump;      // entry 0: Pro A jumps
            Buffer.BlockCopy(entry0, 0, s.D, e0 + NameTagSave.CtlOff, NameTagSave.CtlLen);
            Check(CtlBind.Effective(s, -1).SequenceEqual(entry0), "shown without a tag: entry 0's controls");
            var none = new NameTagSave(MadeUp(22));
            Check(CtlBind.Effective(none, -1).SequenceEqual(NameTagSave.DefaultControls), "entry 0 not in use: the game's defaults");
            int other = s.Create("Nico");
            int slot = CtlBind.WriteControls(s, -1, c => { c[0x0F + 10] = GameAction.Attack; return c; });
            var t = s.Tag(slot);
            Check(slot == other + 1 && t.Name == CtlBind.TagName && t.InUse && t.NameHashOk, "no tag of my own: Arena made in the first free entry");
            Check(t.Controls[0x0F + 7] == GameAction.Jump && t.Controls[0x0F + 10] == GameAction.Attack, "Arena starts from entry 0, with the change");
            Check(s.Tag(other).Controls.SequenceEqual(NameTagSave.DefaultControls) && s.Tag(0).Controls.SequenceEqual(entry0), "other tags and entry 0 untouched");
            string my = Core_MyTagValue(t);
            Check(CtlBind.FindMyTag(s, my) == slot, "my_name_tag finds it");
            int again = CtlBind.WriteControls(s, slot, c => { c[0x0F + 8] = GameAction.Grab; return c; });
            Check(again == slot && s.Tag(slot).Controls[0x0F + 8] == GameAction.Grab && s.Tag(slot).Controls[0x0F + 10] == GameAction.Attack, "my own tag: edited in place, the earlier change kept");
            Check(s.Named().Count(x => x.Name == CtlBind.TagName) == 1, "one Arena tag");
            // my own tag is another one: it is the one written
            int mine = CtlBind.WriteControls(s, other, c => { c[0] = GameAction.Jump; return c; });
            Check(mine == other && s.Tag(other).Controls[0] == GameAction.Jump, "my own tag (Nico): written there");
            // a tag called Arena that is not mine yet: taken, from what player 1 played with
            var s2 = new NameTagSave(MadeUp(23));
            int ar = s2.Create("arena");
            int got = CtlBind.WriteControls(s2, -1, c => c);
            Check(got == ar && s2.Named().Count == 1, "an existing Arena tag is used, no second one");
            // my_name_tag of a tag moved / renamed
            Check(CtlBind.FindMyTag(s, "9:" + NameTagSave.NameHash(CtlBind.TagName).ToString("x8")) == slot, "my_name_tag: found by name in another entry");
            Check(CtlBind.FindMyTag(s, "3:deadbeef") == -1 && CtlBind.FindMyTag(s, "") == -1 && CtlBind.FindMyTag(s, "x") == -1, "my_name_tag: gone or malformed");
            // the whole edit through the file (game closed, save.commit, copy, atomic) as the page does it
            string saves = SavesFolder("v3", (byte[])s.D.Clone());
            Throws(() => NameTagFile.Edit(saves, () => true, x => CtlBind.WriteControls(x, slot, c => c)), "v3 write refused while the game runs");
            string bak = NameTagFile.Edit(saves, () => false, x => CtlBind.WriteControls(x, slot, c => { c[0x0F + 11] = GameAction.Special; return c; }));
            Check(bak != null && NameTagFile.Read(saves).Tag(slot).Controls[0x0F + 11] == GameAction.Special, "v3 write through NameTagFile.Edit");
        }

        static string Core_MyTagValue(NameTag t) => t.Slot + ":" + NameTagSave.NameHash(t.Name).ToString("x8");      // as Core.MyTagValue
    }
}
