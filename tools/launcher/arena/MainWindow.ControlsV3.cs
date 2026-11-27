// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.78, Controls v3) Settings > Controls as one section: the controllers found (the game's --pad-live, MainWindow.PadTest.cs) and
// the keyboard at the top; under them the game's actions, big, each with the buttons that do it on the chosen controller. Click an
// action (or one of its buttons, or +), press the button: it is saved (ControlBindings.cs says where: the save's name tag for a pad
// button, controllers.ini for a stick press or a key). A button that already does something else asks first: swap or keep both.
// Esc or 5 s without a press: nothing changes. "Reset to default" for the whole controller. The page writes into the player's own
// name tag (launcher.ini my_name_tag), else into a name tag called Arena that it makes and makes the player's own, so player 1
// plays with it without picking it. Advanced: the Controllers tab's settings, players and live view, the name-tag editor of v2 and
// the pad buttons, moved here at the start. The game running: read only, with a banner. SSBU_ARENA_CONTROLS_V3=0: v2's two tabs.
// Test aids (screenshots): SSBU_ARENA_FAKE_PADS=<name>:<kind>;... pads without the game's exe (player = place in the list),
// SSBU_ARENA_FAKE_PRESS=<file> holding "<player> <button>" (A B X Y L R ZL ZR LEFT UP DOWN STICK_R RSTICK ...) presses it once,
// SSBU_ARENA_FAKE_GAME_RUNNING=1 the read-only page.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Arena
{
    public partial class MainWindow
    {
        const int RowMove = -1, RowPause = -2, RowOther = -3;

        sealed class C3Pad { public string Id, Name; public int Kind, Player; public bool Keyboard; }
        sealed class C3Capture { public int Row; public Chip Replace; public PressCapture Pad; public bool Keyboard; public DateTime Until; }

        string c3Sel;                   // the controller shown: "kb" or "pad:<player>"
        bool c3SelChosen;               // the player clicked one (else the first pad is taken when one comes)
        string c3PadsSig = "";
        CtlState c3State;
        C3Capture c3Cap;
        BindPlan c3Plan; int c3PlanRow; Chip c3PlanChip;
        int c3MsgRow = int.MinValue; string c3Msg; bool c3MsgWarn;
        TextBlock c3CapText, c3Hint;
        DispatcherTimer c3Timer;
        readonly Dictionary<int, Border> c3RowOf = new Dictionary<int, Border>();
        HashSet<int> c3Lit = new HashSet<int>();
        bool c3Can;
        static Dictionary<string, string> c3PadActs;      // the live drawing's labels (PadDiagram.Acts): player 1's Pro Controller controls

        static readonly Brush C3RowBg = Frozen(Color.FromRgb(0x0A, 0x10, 0x18)), C3RowLit = Frozen(Color.FromArgb(0x33, 0x3D, 0xDC, 0x97)), C3RowCap = Frozen(Color.FromArgb(0x22, 0x65, 0xC7, 0xFF)),
                              C3Chip = Frozen(Color.FromRgb(0x17, 0x22, 0x31)), C3ChipEdge = Frozen(Color.FromRgb(0x3A, 0x4A, 0x5E)), C3Ask = Frozen(Color.FromArgb(0x1A, 0xFF, 0xC8, 0x57));
        static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        static readonly Geometry IcoKeyboard = MakeFrozen(Geometry.Parse("F0 M1.5,6 H22.5 A1.5,1.5 0 0 1 24,7.5 V16.5 A1.5,1.5 0 0 1 22.5,18 H1.5 A1.5,1.5 0 0 1 0,16.5 V7.5 A1.5,1.5 0 0 1 1.5,6 Z " +
            "M2.5,8.5 H5 V10.5 H2.5 Z M6.5,8.5 H9 V10.5 H6.5 Z M10.5,8.5 H13 V10.5 H10.5 Z M14.5,8.5 H17 V10.5 H14.5 Z M18.5,8.5 H21.5 V10.5 H18.5 Z " +
            "M2.5,12 H6 V14 H2.5 Z M7.5,12 H10 V14 H7.5 Z M11.5,12 H14 V14 H11.5 Z M15.5,12 H21.5 V14 H15.5 Z M6,15.2 H18 V16.5 H6 Z"));
        static Geometry MakeFrozen(Geometry g) { g.Freeze(); return g; }

        bool C3Shown => Core.ControlsV3 && BoxControls3 != null && BoxControls3.Visibility == Visibility.Visible;

        /// <summary>ControlsInit: the Controllers tab goes, its parts and v2's name-tag editor move into Advanced</summary>
        void ControlsV3Init()
        {
            SecPads.Visibility = Visibility.Collapsed;
            void Move(FrameworkElement el, Panel to)
            {
                if (el == null) return;
                if (el.Parent is Panel p) p.Children.Remove(el);
                else if (el.Parent is ContentControl cc) cc.Content = null;
                else if (el.Parent is Decorator d) d.Child = null;
                to.Children.Add(el);
            }
            Move(PadSettingsBlock, C3AdvSticks); PadSettingsBlock.Margin = new Thickness(0, 6, 0, 0);
            Move(PadSettingsReset, C3AdvSticks);
            Move(PadPlayersBlock, C3AdvPlayers); PadPlayersBlock.Margin = new Thickness(0, 10, 0, 0);
            Move(PadStageBlock, C3AdvPlayers);
            Move(CtlEditor, C3AdvTags); CtlEditor.Margin = new Thickness(0, 8, 0, 0);
            Move(MyTagTitle, C3AdvTags); MyTagTitle.Text = "PLAYER 1 PLAYS WITH";
            Move(MyTagBox, C3AdvTags);
            Move(MyTagHelp, C3AdvTags);
            Move(PadAdvBox, C3AdvPadButtons);
            PadAdvTitle.Text = "PAD BUTTONS";
            PreviewKeyDown += C3KeyDown;
            c3Timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            c3Timer.Tick += (s, e) => C3Tick();
        }

        void C3Adv_Click(object sender, RoutedEventArgs e)
        {
            bool open = C3Adv.Visibility != Visibility.Visible;
            C3Adv.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            C3AdvTurn.Angle = open ? 0 : -90;
        }

        // ---------------------------------------------------------------- the controllers
        List<C3Pad> C3Controllers()
        {
            var list = new List<C3Pad>();
            foreach (var kv in padViews.OrderBy(kv => kv.Key))
                if (PadOn(kv.Key)) list.Add(new C3Pad { Id = "pad:" + kv.Key, Name = kv.Value.Name, Kind = Math.Max(0, Math.Min(5, kv.Value.Pad.Kind)), Player = kv.Key });
            list.Add(new C3Pad { Id = "kb", Name = "Keyboard", Kind = 6, Player = KeyboardPlayer(), Keyboard = true });
            return list;
        }

        C3Pad C3Current()
        {
            var list = C3Controllers();
            var c = list.FirstOrDefault(x => x.Id == c3Sel);
            if (c == null || (!c3SelChosen && c.Keyboard)) c = list.FirstOrDefault(x => !x.Keyboard && x.Player == 1) ?? list.FirstOrDefault(x => !x.Keyboard) ?? list.Last();
            c3Sel = c.Id;
            return c;
        }

        static PadFamily C3Family(C3Pad c) => !c.Keyboard && c.Kind == 3 && !Core.GcAsPro ? PadFamily.GameCube : PadFamily.Pro;

        /// <summary>what the page shows for a controller: player 1's controls (the player's own name tag, else what the game uses without one)</summary>
        CtlState C3StateFor(C3Pad c)
        {
            var st = new CtlState { Controls = CtlBind.Effective(ctlSave, CtlBind.FindMyTag(ctlSave, Core.MyNameTag)), Family = C3Family(c) };
            if (c.Keyboard)
            {
                st.Keys = Core.KeyboardKeys.ToArray(); st.CanExtras = false;
                st.Label = (s, ch) => { string k = KeyNames.Display(s.Keys[ch.Key]); return ch.Key >= SwBtn.RStickLeft ? new[] { "←", "↑", "→", "↓" }[ch.Key - SwBtn.RStickLeft] + "  " + k : k; };
                return st;
            }
            int kind = c.Kind;
            Func<string, string> map = ini => Core.Buttons.TryGetValue(ini, out var src) ? src : null;
            st.CanExtras = kind != 3 && st.Family == PadFamily.Pro;
            if (st.CanExtras) foreach (var kv in Core.ButtonExtras) st.Extras[kv.Key] = kv.Value.ToList();
            st.Source = sw => sw >= 0 && sw < 12 ? map(SwBtn.Ini[sw]) : null;
            st.Label = (s, ch) => ch.Kind == ChipKind.Extra ? PadNames.Source(kind, ch.Source) : PadNames.Button(kind, ch.Sw, map);
            return st;
        }

        /// <summary>the page from ctlSave (ControlsFill read it) and the pads seen</summary>
        void C3Fill()
        {
            if (!Core.ControlsV3 || BoxControls3 == null) return;
            bool open = CtlGameOpen();
            c3Can = ctlSave != null && !open;
            C3Notice.Visibility = c3Can ? Visibility.Collapsed : Visibility.Visible;
            C3NoticeText.Text = open ? "The game is running: close it to change your controls. They are shown here as they are."
                              : "There is no save yet: start the game once, then your controls can be changed here.";
            if (!c3Can) { c3Cap = null; c3Plan = null; }
            var c = C3Current();
            c3PadsSig = string.Join("|", C3Controllers().Select(x => x.Id + ":" + x.Name + ":" + x.Kind + ":" + x.Player));
            c3State = C3StateFor(c);
            {   var pro = new CtlState { Controls = c3State.Controls, Family = PadFamily.Pro };
                c3PadActs = new Dictionary<string, string>(PadDiagram.Action);
                foreach (var b in new[] { SwBtn.A, SwBtn.B, SwBtn.X, SwBtn.Y, SwBtn.L, SwBtn.R, SwBtn.ZL, SwBtn.ZR })
                    c3PadActs[SwBtn.Ini[b]] = GameAction.Name(pro.Code(CtlLayout.SlotOf(PadFamily.Pro, b))); }
            C3DrawPads(c);
            C3Icon.Data = c.Keyboard ? IcoKeyboard : (Geometry)FindResource("IcoPad");
            C3Title.Text = c.Name;
            C3Sub.Text = (c.Keyboard ? "Keyboard" : PadNames.Kind(c.Kind) + (c.Kind == 3 ? " (adapter)" : "")) + "  ·  " + (c.Player > 0 ? "player " + c.Player : "not used");
            C3Reset.IsEnabled = c3Can;
            C3DrawRows();
            int my = CtlBind.FindMyTag(ctlSave, Core.MyNameTag);
            string mine = my > 0 ? ctlSave.Tag(my).Name : null;
            C3Foot.Text = c.Keyboard ? "Keys work while the game window has the focus. What each one does follows the game's controls of player 1."
                        : (c.Player > 1 ? "This controller is player " + c.Player + ": these are player 1's controls (player " + c.Player + " uses the ones picked on the character select). "
                                        : "Player 1 plays with these in every match, online too. ")
                          + (mine == null ? "Saved in your game save at your first change." : "Saved in your game save.");
            C3TagInfo.Text = mine != null ? "Your controls are kept in the save as the name tag \"" + mine + "\", which player 1 plays with (below). Choose another one, or edit every name tag of the save."
                           : "Until you change one, the controls above are the ones the game uses without a name tag. Your first change keeps them as the name tag \"" + CtlBind.TagName + "\" and makes player 1 play with it.";
        }

        void C3DrawPads(C3Pad sel)
        {
            C3Pads.Children.Clear();
            var all = C3Controllers();
            C3Pads.Columns = Math.Max(2, Math.Min(3, all.Count + (all.Count == 1 ? 1 : 0)));
            foreach (var c in all)
            {
                bool on = c.Id == sel.Id;
                var icon = new System.Windows.Shapes.Path { Data = c.Keyboard ? IcoKeyboard : (Geometry)FindResource("IcoPad"), Fill = on ? Res("Accent") : Res("Muted"), Stretch = Stretch.Uniform, Width = 34, Height = 26, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
                var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                text.Children.Add(new TextBlock { Text = c.Name, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
                string shortKind = c.Keyboard ? "Keyboard" : PadNames.Kind(c.Kind).Replace(" controller", "");
                text.Children.Add(new TextBlock { Text = shortKind + "  ·  " + (c.Player > 0 ? "player " + c.Player : "not used"), FontSize = 13, Foreground = Res("Muted"), TextTrimming = TextTrimming.CharacterEllipsis });
                var dp = new DockPanel(); dp.Children.Add(icon); dp.Children.Add(text);
                var b = new Button { Style = (Style)FindResource("CardButton"), Content = dp, Height = 66, Padding = new Thickness(14, 8, 12, 8), Margin = new Thickness(0, 0, 10, 10), Tag = c.Id,
                                     BorderBrush = on ? Res("Accent") : Res("Line"), Background = on ? Res("CardHover") : Res("Card"), ToolTip = c.Keyboard ? "The keyboard: its keys" : c.Name + " (player " + c.Player + ")" };
                b.Click += (s, e) => { c3Sel = (string)((Button)s).Tag; c3SelChosen = true; c3Cap = null; c3Plan = null; c3Msg = null; C3Fill(); };
                C3Pads.Children.Add(b);
            }
            if (all.Count == 1)
            {
                var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = "No controller found", FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Res("Muted") });
                sp.Children.Add(new TextBlock { Text = padLive == null && padFake == null ? "Shown while the game is closed." : "Connect one and press a button: it appears here.", FontSize = 13, Foreground = Res("Dim") });
                C3Pads.Children.Insert(0, new Border { Height = 66, Margin = new Thickness(0, 0, 10, 10), Padding = new Thickness(16, 8, 12, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                                                       BorderBrush = Res("LineSoft"), Background = Res("BgDeep"), Child = sp });
            }
        }

        // ---------------------------------------------------------------- the actions
        string C3RowName(int row) => row == RowMove ? "Move" : row == RowPause ? "Pause" : row == RowOther ? "Other" : GameAction.Name(row);

        List<Chip> C3Chips(int row) => row == RowMove ? CtlBind.KeyChips(c3State, SwBtn.LStickLeft, SwBtn.LStickUp, SwBtn.LStickRight, SwBtn.LStickDown)
                                     : row == RowPause ? CtlBind.KeyChips(c3State, SwBtn.Plus) : CtlBind.Chips(c3State, row);

        void C3DrawRows()
        {
            C3Rows.Children.Clear(); c3RowOf.Clear(); c3CapText = null;
            if (c3State == null) return;
            var rows = new List<int>();
            if (c3State.Keyboard) rows.Add(RowMove);
            rows.AddRange(GameAction.Main);
            if (c3State.Keyboard) rows.Add(RowPause);
            foreach (int r in rows) C3Rows.Children.Add(C3Row(r));
            var other = CtlBind.Unknown(c3State);
            if (other.Count > 0) C3Rows.Children.Add(C3Row(RowOther));
            C3Light(c3Lit);
        }

        FrameworkElement C3Row(int row)
        {
            bool cap = c3Cap != null && c3Cap.Row == row, ask = c3Plan != null && c3PlanRow == row, editable = c3Can && row != RowOther;
            var chips = row == RowOther ? CtlBind.Unknown(c3State).Select(s => new Chip { Kind = ChipKind.Button, Slot = s, Sw = s.Sw[0], Label = c3State.Label(c3State, new Chip { Kind = ChipKind.Button, Slot = s, Sw = s.Sw[0] }) + "  ·  " + GameAction.Name(c3State.Code(s)) }).ToList() : C3Chips(row);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock { Text = C3RowName(row), FontSize = 19, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = row == RowOther ? Res("Muted") : Res("Text") });
            var right = new TextBlock { FontSize = 13, Foreground = Res("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            Grid.SetColumn(right, 2); grid.Children.Add(right);
            if (cap)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                sp.Children.Add(new TextBlock { Text = c3Cap.Keyboard ? "Press a key…" : "Press a button on the controller…", FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Res("Accent"), VerticalAlignment = VerticalAlignment.Center });
                if (c3Cap.Replace != null) sp.Children.Add(new TextBlock { Text = "instead of " + c3Cap.Replace.Label, FontSize = 14, Foreground = Res("Muted"), Margin = new Thickness(12, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(sp, 1); grid.Children.Add(sp);
                c3CapText = right; right.FontSize = 14; right.Foreground = Res("Muted");
                C3CapCountdown();
            }
            else
            {
                var wp = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
                foreach (var ch in chips) wp.Children.Add(C3ChipView(row, ch, editable));
                if (chips.Count == 0) wp.Children.Add(new TextBlock { Text = row == RowOther ? "" : "no button", FontSize = 15, FontStyle = FontStyles.Italic, Foreground = Res("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
                if (editable && !c3State.Keyboard && row >= 0 && chips.Count < 4) wp.Children.Add(C3PlusView(row));
                Grid.SetColumn(wp, 1); grid.Children.Add(wp);
                if (row == RowOther) right.Text = "set in the game";
                else if (editable) { right.Text = "Click to change"; right.Visibility = Visibility.Hidden; c3Hint = right; }
            }
            var body = new StackPanel();
            body.Children.Add(grid);
            if (ask) body.Children.Add(C3AskView());
            else if (c3Msg != null && c3MsgRow == row)
                body.Children.Add(new TextBlock { Text = c3Msg, FontSize = 14, Margin = new Thickness(190, 6, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = c3MsgWarn ? Res("Warn") : Res("Good") });
            var hint = c3Hint; c3Hint = null;
            var b = new Border { Child = body, CornerRadius = new CornerRadius(8), Padding = new Thickness(18, 6, 14, 6), MinHeight = 52,
                                 Background = cap ? C3RowCap : ask ? C3Ask : C3RowBg, BorderThickness = new Thickness(cap || ask ? 1.5 : 1),
                                 BorderBrush = cap ? Res("Accent") : ask ? Res("Warn") : Res("LineSoft"), Cursor = editable ? Cursors.Hand : Cursors.Arrow, Tag = row };
            if (editable)
            {
                b.MouseEnter += (s, e) => { if (c3Cap == null && c3Plan == null) { ((Border)s).BorderBrush = Res("AccentDeep"); if (hint != null) hint.Visibility = Visibility.Visible; } };
                b.MouseLeave += (s, e) => { if (hint != null) hint.Visibility = Visibility.Hidden; if (!(c3Cap != null && c3Cap.Row == row) && !(c3Plan != null && c3PlanRow == row)) ((Border)s).BorderBrush = Res("LineSoft"); };
            }
            c3RowOf[row] = b;
            // a button (UI Automation can invoke it; Enter / Space on it): the chips inside are buttons of their own
            return C3Click(b, C3RowName(row), new Thickness(0, 0, 0, 5), !editable ? (Action)null : () =>
            {
                if (ask) return;
                if (cap) { C3Cancel(null); return; }
                var first = chips.FirstOrDefault(x => x.Kind != ChipKind.Extra);
                C3StartCapture(row, row == RowMove || row == RowPause ? first ?? chips.FirstOrDefault() : first);
            });
        }

        FrameworkElement C3Click(FrameworkElement content, string name, Thickness margin, Action click)
        {
            if (click == null) { content.Margin = margin; return content; }
            var b = new Button { Style = (Style)FindResource("Bare"), Content = content, Margin = margin, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            System.Windows.Automation.AutomationProperties.SetName(b, name);
            b.Click += (s, e) => { e.Handled = true; click(); };
            return b;
        }

        FrameworkElement C3ChipView(int row, Chip ch, bool editable)
        {
            var sp = new StackPanel { Orientation = Orientation.Horizontal };
            sp.Children.Add(new TextBlock { Text = ch.Label, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = ch.Label == "—" ? Res("Dim") : Res("Text") });
            var chip = new Border { Child = sp, CornerRadius = new CornerRadius(6), Background = C3Chip, BorderBrush = C3ChipEdge, BorderThickness = new Thickness(1), Padding = new Thickness(12, 4, 12, 5), MinWidth = 46 };
            string tip = ch.Kind == ChipKind.Extra ? "A stick press added to this action (controllers.ini). Click to change it; × removes it."
                       : row == RowMove ? new[] { "Move left", "Move up", "Move right", "Move down" }[Math.Max(0, Math.Min(3, ch.Key - SwBtn.LStickLeft))]
                       : "Click, then press the new button";
            chip.ToolTip = tip;
            if (ch.Kind == ChipKind.Extra && editable)
            {
                var x = new TextBlock { Text = "×", FontSize = 17, Foreground = Res("Muted"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Remove the stick press" };
                sp.Children.Add(C3Click(x, "remove " + ch.Label, new Thickness(10, 0, 0, 0), () =>
                {
                    var r = c3State.Clone();
                    if (r.Extras.TryGetValue(SwBtn.Ini[ch.Sw], out var l)) { l.RemoveAll(v => string.Equals(v, ch.Source, StringComparison.OrdinalIgnoreCase)); if (l.Count == 0) r.Extras.Remove(SwBtn.Ini[ch.Sw]); }
                    C3Apply(r, GameAction.Name(row) + ": " + ch.Label + " removed", row, null);
                }));
            }
            if (editable)
            {
                chip.Cursor = Cursors.Hand;
                chip.MouseEnter += (s, e) => ((Border)s).BorderBrush = Res("Accent");
                chip.MouseLeave += (s, e) => ((Border)s).BorderBrush = C3ChipEdge;
            }
            return C3Click(chip, C3RowName(row) + ": " + ch.Label, new Thickness(0, 3, 8, 3), !editable ? (Action)null : () => { if (c3Plan == null) C3StartCapture(row, ch); });
        }

        FrameworkElement C3PlusView(int row)
        {
            var plus = new Border { Child = new TextBlock { Text = "+", FontSize = 18, Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center }, CornerRadius = new CornerRadius(6), BorderBrush = Res("Line"),
                                    BorderThickness = new Thickness(1), Padding = new Thickness(10, 2, 10, 4), MinWidth = 38, ToolTip = "Add one more button for " + GameAction.Name(row) };
            plus.MouseEnter += (s, e) => ((Border)s).BorderBrush = Res("Accent");
            plus.MouseLeave += (s, e) => ((Border)s).BorderBrush = Res("Line");
            return C3Click(plus, C3RowName(row) + ": add", new Thickness(0, 3, 8, 3), () => { if (c3Plan == null) C3StartCapture(row, null); });
        }

        FrameworkElement C3AskView()
        {
            var p = c3Plan;
            var sp = new StackPanel { Margin = new Thickness(190, 8, 0, 2) };
            sp.Children.Add(new TextBlock { Text = p.Pressed + " already does " + p.Other + ".", FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Res("Warn"), TextWrapping = TextWrapping.Wrap });
            var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            if (p.Swap != null)
            {
                var b = new Button { Style = (Style)FindResource("Primary"), Height = 36, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 8, 4), ToolTip = "Enter" };
                b.Content = new TextBlock { Text = p.SwapText, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)new BrushConverter().ConvertFrom("#06121D") };
                b.Click += (s, e) => C3Answer(true);
                bar.Children.Add(b);
            }
            if (p.Take != null)
            {
                var b = new Button { Style = (Style)FindResource(p.Swap == null ? "Primary" : "Outline"), Height = 36, Padding = new Thickness(16, 0, 16, 0), Margin = new Thickness(0, 0, 8, 4), FontSize = 15, ToolTip = p.TakeNote };
                b.Content = p.Swap == null ? (object)new TextBlock { Text = p.TakeText, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = (Brush)new BrushConverter().ConvertFrom("#06121D") } : p.TakeText;
                b.Click += (s, e) => C3Answer(false);
                bar.Children.Add(b);
            }
            var cancel = new Button { Style = (Style)FindResource("Link"), Content = "Cancel", Margin = new Thickness(8, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Esc" };
            cancel.Click += (s, e) => C3Cancel(null);
            bar.Children.Add(cancel);
            sp.Children.Add(bar);
            if (p.Take != null && p.TakeNote != null) sp.Children.Add(new TextBlock { Text = (p.Swap != null ? p.TakeText + ": " : "") + p.TakeNote, FontSize = 13, Foreground = Res("Muted"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            return sp;
        }

        // ---------------------------------------------------------------- press to bind
        void C3StartCapture(int row, Chip replace)
        {
            if (!c3Can || c3State == null) return;
            var c = C3Current();
            c3Plan = null; c3Msg = null;
            c3Cap = new C3Capture { Row = row, Replace = replace, Keyboard = c.Keyboard, Until = DateTime.Now.AddSeconds(5), Pad = c.Keyboard ? null : new PressCapture(c.Player, DateTime.Now, 5) };
            Focus();
            c3Timer?.Start();
            C3DrawRows();
        }

        void C3CapCountdown()
        {
            if (c3CapText == null || c3Cap == null) return;
            int left = (int)Math.Ceiling((c3Cap.Until - DateTime.Now).TotalSeconds);
            c3CapText.Text = "Esc to cancel  ·  " + Math.Max(0, left) + " s";
        }

        void C3Tick()
        {
            if (c3Cap == null) { c3Timer.Stop(); return; }
            if (DateTime.Now >= c3Cap.Until || !C3Shown) { C3Cancel(C3Shown ? "Nothing pressed: no change." : null); return; }
            C3CapCountdown();
        }

        void C3Cancel(string why)
        {
            int row = c3Cap?.Row ?? (c3Plan != null ? c3PlanRow : int.MinValue);
            c3Cap = null; c3Plan = null;
            c3Msg = why; c3MsgRow = row; c3MsgWarn = true;
            C3DrawRows();
        }

        /// <summary>a line of --pad-live (MainWindow.PadTest.cs): the capture, else the rows of what is pressed light up</summary>
        void C3OnLine(int player, string[] c)
        {
            if (!C3Shown || c3State == null) return;
            int I(int i, int d) => i < c.Length && int.TryParse(c[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : d;
            var s = new PadSample { Player = player, Buttons = ulong.TryParse(c[3], out ulong b) ? b : 0, RX = I(6, 0), RY = I(7, 0), TrigL = I(13, -1), TrigR = I(14, -1), Kind = I(15, 4) };
            if (c3Cap != null && c3Cap.Pad != null)
            {
                int sw = c3Cap.Pad.Feed(s);
                if (sw >= 0) C3Pressed(sw, null);
                return;
            }
            if (c3Sel != "pad:" + player || c3State.Keyboard) return;
            var lit = new HashSet<int>();
            for (int bit = 0; bit < 16; bit++)
                if ((s.Buttons >> bit & 1) != 0) { var slot = CtlLayout.SlotOf(c3State.Family, bit); if (slot != null) lit.Add(c3State.Code(slot)); }
            if (Math.Sqrt((double)s.RX * s.RX + (double)s.RY * s.RY) >= 0.6 * 32767) { var slot = CtlLayout.SlotOf(c3State.Family, SwBtn.RStick); if (slot != null) lit.Add(c3State.Code(slot)); }
            if (!lit.SetEquals(c3Lit)) { c3Lit = lit; C3Light(lit); }
        }

        void C3Light(HashSet<int> lit)
        {
            foreach (var kv in c3RowOf)
            {
                bool busy = (c3Cap != null && c3Cap.Row == kv.Key) || (c3Plan != null && c3PlanRow == kv.Key);
                if (!busy) kv.Value.Background = lit.Contains(kv.Key) ? C3RowLit : C3RowBg;
            }
        }

        void C3KeyDown(object sender, KeyEventArgs e)
        {
            if (!C3Shown) return;
            var k = e.Key == Key.System ? e.SystemKey : e.Key;
            if (c3Cap != null)
            {
                if (k == Key.Escape) { e.Handled = true; C3Cancel(null); return; }
                if (!c3Cap.Keyboard) return;
                int vk = KeyInterop.VirtualKeyFromKey(k);
                if (KeyNames.Ignored(vk)) return;
                e.Handled = true;
                C3Pressed(-1, KeyNames.FromVk(vk));
                return;
            }
            if (c3Plan != null)
            {
                if (k == Key.Escape) { e.Handled = true; C3Cancel(null); }
                else if (k == Key.Enter) { e.Handled = true; C3Answer(c3Plan.Swap != null); }
            }
        }

        void C3Pressed(int sw, string key)
        {
            var cap = c3Cap; c3Cap = null; c3Timer?.Stop();
            if (cap == null || c3State == null) return;
            var plan = key != null ? CtlBind.PlanKey(c3State, cap.Row, cap.Replace, key) : CtlBind.PlanPad(c3State, cap.Row, cap.Replace, sw);
            string name = C3RowName(cap.Row);
            Updater.Log("controls: " + name + ": pressed " + (key ?? (sw >= 0 && sw < 16 ? SwBtn.Ini[sw] : "RSTICK")) + (plan.Refused != null ? " (refused)" : plan.Already ? " (already)" : plan.Question ? " (asks: does " + plan.Other + ")" : ""));
            if (plan.Refused != null) { c3Msg = plan.Refused; c3MsgRow = cap.Row; c3MsgWarn = true; C3DrawRows(); return; }
            if (plan.Already) { c3Msg = plan.Pressed + " already does " + name + "."; c3MsgRow = cap.Row; c3MsgWarn = false; C3DrawRows(); return; }
            if (plan.Direct != null) { C3Apply(plan.Direct, name + ": " + plan.Pressed, cap.Row, plan.Note); return; }
            c3Plan = plan; c3PlanRow = cap.Row; c3PlanChip = cap.Replace;
            C3DrawRows();
        }

        void C3Answer(bool swap)
        {
            var p = c3Plan; int row = c3PlanRow;
            if (p == null) return;
            var r = swap ? p.Swap : p.Take;
            if (r == null) return;
            c3Plan = null;
            C3Apply(r, C3RowName(row) + ": " + p.Pressed + (swap ? " (swapped: " + p.SwapText.Replace("Swap: ", "") + ")" : ""), row, null);
        }

        void C3Reset_Click(object sender, RoutedEventArgs e)
        {
            if (!c3Can || c3State == null) return;
            var c = C3Current();
            string text = c.Keyboard ? "The keyboard's keys go back to the built-in ones (Z attack, X special, C / V jump, arrows to move...)."
                        : "Every action of the " + (C3Family(c) == PadFamily.GameCube ? "GameCube controllers" : "controllers (Xbox, PlayStation, Switch and others)") + " goes back to the game's default buttons"
                          + (C3Family(c) == PadFamily.Pro ? ", the pad buttons swapped in Advanced too." : ".");
            Show("Reset " + c.Name + "?", text, null, ("Cancel", null, false), ("Reset", () =>
            {
                var r = CtlBind.Reset(c3State);
                if (!c.Keyboard && C3Family(c) == PadFamily.Pro && Core.Buttons.Any(kv => !Core.DefaultButtons.TryGetValue(kv.Key, out var v) || v != kv.Value))
                {
                    Core.Buttons = new Dictionary<string, string>(Core.DefaultButtons);
                    bool was = loading; loading = true; FillButtons(); loading = was;
                }
                C3Apply(r, c.Name + ": back to the defaults", int.MinValue, null, force: true);
            }, true));
        }

        /// <summary>writes what changed: the family's block into the save (the player's own name tag, else Arena: made and made the
        /// player's own), the stick presses into controllers.ini [type ...], the keys into [keyboard]; then the page again</summary>
        void C3Apply(CtlState after, string what, int row, string note, bool force = false)
        {
            var before = c3State;
            bool keys = after.Keyboard && !after.Keys.SequenceEqual(before.Keys);
            bool extras = !after.Keyboard && after.CanExtras && !C3SameExtras(after.Extras, before.Extras);
            bool save = !after.Keyboard && !after.Controls.SequenceEqual(before.Controls);
            string err = null;
            if (save)
            {
                string tag = null; string bak = null;
                var fam = after.Family; var k = CtlLayout.Kind(fam);
                try
                {
                    bak = NameTagFile.Edit(Core.Saves, CtlGameOpen, s =>
                    {
                        int slot = CtlBind.WriteControls(s, CtlBind.FindMyTag(s, Core.MyNameTag), c => { for (int i = k.First; i < k.Options + 4; i++) c[i] = after.Controls[i]; return c; });
                        tag = Core.MyTagValue(s.Tag(slot));
                    });
                    if (bak != null) Updater.Log("controls: a copy of the save: " + bak);
                    if (tag != null && tag != Core.MyNameTag) { Core.MyNameTag = tag; ctlSlot = int.Parse(tag.Split(':')[0]); Updater.Log("controls: my name tag = " + tag + " (the page's name tag)"); SaveQuiet(); }
                }
                catch (Exception e) { err = e is SaveFileError ? e.Message : "The save could not be written: " + e.Message; }
            }
            if (err == null && extras)
            {
                Core.ButtonExtras = after.Extras.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                if (!SaveQuiet()) err = "controllers.ini could not be written.";
            }
            if (err == null && keys)
            {
                Core.KeyboardKeys = after.Keys.ToArray();
                try { Core.SaveKeyboardKeys(); } catch (Exception e) { err = "controllers.ini could not be written: " + e.Message; }
            }
            if (err == null && force && !save && !extras && !keys) SaveQuiet();      // (the reset of the pad buttons alone)
            if (err != null)
            {
                Updater.Log("controls: " + what + " NOT written: " + err);
                Show("Controls", err, null, ("OK", null, true));
            }
            else
            {
                Updater.Log("controls: " + what + (save ? " (save)" : "") + (extras ? " (controllers.ini stick presses)" : "") + (keys ? " (controllers.ini keys)" : ""));
                Status(what + ": saved", "good");
                c3Msg = note ?? "Saved."; c3MsgRow = row; c3MsgWarn = false;
            }
            ControlsFill();      // the save again, then C3Fill
        }

        static bool C3SameExtras(Dictionary<string, List<string>> a, Dictionary<string, List<string>> b)
        {
            string S(Dictionary<string, List<string>> d) => string.Join(";", d.Where(kv => kv.Value.Count > 0).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key.ToUpperInvariant() + "=" + string.Join(",", kv.Value.Select(v => v.ToLowerInvariant()))));
            return S(a) == S(b);
        }

        /// <summary>RefreshPads (every second): a pad came or went</summary>
        void C3PadsChanged()
        {
            if (!C3Shown) return;
            string sig = string.Join("|", C3Controllers().Select(x => x.Id + ":" + x.Name + ":" + x.Kind + ":" + x.Player));
            if (sig == c3PadsSig) return;
            if (c3Cap != null && c3Cap.Pad != null && !C3Controllers().Any(x => x.Player == c3Cap.Pad.Player && !x.Keyboard)) c3Cap = null;
            if (c3Cap != null || c3Plan != null) return;      // not under the player's hands; the next second
            C3Fill();
        }

        // ---------------------------------------------------------------- test aid: pads without the game's exe
        DispatcherTimer padFake;
        DateTime padFakePressEnd;
        int padFakePlayer; ulong padFakeButtons; int padFakeRX, padFakeRY;

        bool PadFakeStart()
        {
            string spec = Environment.GetEnvironmentVariable("SSBU_ARENA_FAKE_PADS");
            if (string.IsNullOrEmpty(spec)) return false;
            if (padFake != null) return true;
            var pads = spec.Split(';').Where(x => x.Trim() != "").Select(x => { int i = x.LastIndexOf(':'); return Tuple.Create(i > 0 ? x.Substring(0, i) : x, i > 0 && int.TryParse(x.Substring(i + 1), out int k) ? k : 0); }).ToList();
            padFake = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            padFake.Tick += (s, e) =>
            {
                string pf = Environment.GetEnvironmentVariable("SSBU_ARENA_FAKE_PRESS");
                if (!string.IsNullOrEmpty(pf) && File.Exists(pf) && DateTime.Now > padFakePressEnd.AddMilliseconds(500))
                {
                    try
                    {
                        var w = File.ReadAllText(pf).Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        File.Delete(pf);
                        padFakePlayer = int.Parse(w[0]); padFakeButtons = 0; padFakeRX = padFakeRY = 0;
                        int b = Array.FindIndex(SwBtn.Ini, x => x.Equals(w[1], StringComparison.OrdinalIgnoreCase));
                        if (w[1].Equals("RSTICK", StringComparison.OrdinalIgnoreCase)) padFakeRX = 32000; else if (b >= 0 && b < 16) padFakeButtons = 1UL << b;
                        padFakePressEnd = DateTime.Now.AddMilliseconds(400);
                    }
                    catch { }
                }
                bool down = DateTime.Now < padFakePressEnd;
                for (int i = 0; i < pads.Count; i++)
                {
                    int p = i + 1; bool me = down && padFakePlayer == p;
                    ulong bt = me ? padFakeButtons : 0; int rx = me ? padFakeRX : 0, ry = me ? padFakeRY : 0;
                    PadLiveLine(string.Join("\t", "pad", p, pads[i].Item1, bt, 0, 0, rx, ry, 0, 0, "0.100", rx, ry, pads[i].Item2 == 3 ? 0 : -1, pads[i].Item2 == 3 ? 0 : -1, pads[i].Item2, "0.980", "0.330"));
                }
            };
            padFake.Start();
            return true;
        }

        void PadFakeStop() { padFake?.Stop(); padFake = null; }
    }
}
