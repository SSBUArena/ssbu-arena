// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.78) Settings > Controls: the game's own controls, as the save keeps them for each name tag (NameTags.cs). The list of the
// save's name tags (new / rename / delete), the chosen one's buttons for the three controller kinds (a list of actions each; a code
// the game has that is not named here is shown as "Other (n)" and kept), "Reset to game defaults", the player's "My name tag"
// (launcher.ini my_name_tag -> SSBU_ARENA_MY_TAG, runtime/my_tag.c) and, in "Advanced: pad buttons", the Controllers page's old
// button swaps (controllers.ini, moved here as they are). Every change is written to the save at once, only with the game closed.
// SSBU_ARENA_CONTROLS_V2=0: the page is hidden and the Controllers page is as in 0.2.77.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Arena
{
    public partial class MainWindow
    {
        NameTagSave ctlSave;              // the save as last read
        int ctlSlot = -1;                 // the name tag shown
        string ctlKind = "pro";           // the controller kind shown: PC controllers and the keyboard are Pro Controllers to the game
        bool ctlFilling;

        /// <summary>at the window's start (before FillSettings): the page on or off, the pad buttons moved into it</summary>
        void ControlsInit()
        {
            if (!Core.ControlsV2) { SecControls.Visibility = Visibility.Collapsed; return; }
            if (Core.ControlsV3) SecPads.Visibility = Visibility.Collapsed;      // (v3) one Controls tab: the Controllers tab's content moves into it
            // one more side tab: 13 with the Benchmark one, a little shorter again (InitBench: 12 at 38), so Help stays above "Back to home"
            if (SecControls.Parent is Panel side)
            {
                var tabs = side.Children.OfType<RadioButton>().Where(r => r.Visibility == Visibility.Visible).ToList();
                if (tabs.Count >= 13) foreach (var rb in tabs) rb.Height = 35; else if (tabs.Count == 12) foreach (var rb in tabs) rb.Height = 38;
            }
            BoxPads.Children.Remove(PadButtonsBlock);
            PadAdvancedHost.Content = PadButtonsBlock;
            PadButtonsTitle.Visibility = Visibility.Collapsed;
            PadButtonsHelp.Visibility = Visibility.Collapsed;
            PadButtonsBlock.Margin = new Thickness(0, 4, 0, 0);
            if (Core.ControlsV3) ControlsV3Init();      // (MainWindow.ControlsV3.cs)
        }

        bool CtlGameOpen() => game != null || Core.GameRunning() || Environment.GetEnvironmentVariable("SSBU_ARENA_FAKE_GAME_RUNNING") == "1";      // (test aid: the read-only page)

        /// <summary>reads the save again and fills the page (FillSettings: the launcher's start, the game's end)</summary>
        void ControlsFill()
        {
            if (!Core.ControlsV2) return;
            string problem = null;
            try { ctlSave = NameTagFile.Read(Core.Saves); }
            catch (Exception e) { ctlSave = null; problem = e is SaveFileError ? e.Message : "The save could not be read: " + e.Message; }
            var tags = ctlSave?.Named() ?? new List<NameTag>();
            if (Core.ControlsV3 && ctlSlot < 0) ctlSlot = CtlBind.FindMyTag(ctlSave, Core.MyNameTag);      // (v3) the editor opens on the player's own
            if (!tags.Any(t => t.Slot == ctlSlot)) ctlSlot = tags.Count > 0 ? tags[0].Slot : -1;
            bool open = CtlGameOpen();
            CtlNotice.Visibility = problem != null || open ? Visibility.Visible : Visibility.Collapsed;
            CtlNoticeText.Text = problem != null ? (problem.StartsWith("there is no save") ? "There is no save yet: start the game once, then your name tags are shown here." : problem)
                : "The game is running: close it to change the name tags (it keeps the save open and would write over the changes).";
            bool can = ctlSave != null && !open;
            CtlNew.IsEnabled = can && ctlSave.FreeSlot() > 0;
            CtlRename.IsEnabled = CtlDelete.IsEnabled = CtlReset.IsEnabled = can && ctlSlot > 0;
            CtlTagList.Children.Clear();
            foreach (var t in tags)
            {
                var rb = new RadioButton { Content = t.Name, Tag = t.Slot, Style = (Style)FindResource("SideTab"), GroupName = "nametags", IsChecked = t.Slot == ctlSlot, Height = 38,
                                           ToolTip = "The name tag " + t.Name + " (entry " + t.Slot + " of the save)" };
                rb.Checked += (s, e) => { if (ctlFilling) return; ctlSlot = (int)((RadioButton)s).Tag; ControlsFillTag(); };
                CtlTagList.Children.Add(rb);
            }
            if (tags.Count == 0)
                CtlTagList.Children.Add(new TextBlock { Text = ctlSave == null ? "" : "No name tag yet. Make one here or in the game.", Style = (Style)FindResource("Small"), FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 4, 8, 4) });
            ControlsFillTag();
            FillMyTag(tags);
            PadAdvRefresh();
            C3Fill();      // (v3) the page from this read of the save
        }

        /// <summary>the shown name tag's controls for the shown controller kind</summary>
        void ControlsFillTag()
        {
            ctlFilling = true;
            var t = ctlSave != null && ctlSlot > 0 ? ctlSave.Tag(ctlSlot) : null;
            bool can = t != null && !CtlGameOpen();
            CtlRename.IsEnabled = CtlDelete.IsEnabled = CtlReset.IsEnabled = can;
            CtlTitle.Text = t != null ? t.Name : "No name tag";
            CtlSub.Text = t != null ? "The in-game controls of the name tag " + t.Name + ". Pick it on the character select, or make it your name tag below, to play with them."
                                    : "Choose a name tag on the left, or make a new one.";
            CtlKinds.Children.Clear();
            foreach (var k in NameTagSave.Kinds)
            {
                var rb = new RadioButton { Content = k.Title, Tag = k.Id, Style = (Style)FindResource("RvSeg"), GroupName = "ctlkind", IsChecked = k.Id == ctlKind, FontSize = 14, Height = 32, Padding = new Thickness(16, 0, 16, 0) };
                rb.Checked += (s, e) => { if (ctlFilling) return; ctlKind = (string)((RadioButton)s).Tag; ControlsFillTag(); };
                CtlKinds.Children.Add(rb);
            }
            var kind = NameTagSave.Kinds.First(k => k.Id == ctlKind);
            CtlKindHelp.Text = ctlKind == "pro" ? "Xbox, PlayStation and other PC controllers, and the keyboard, play as a Pro Controller (so do two Joy-Cons and handheld)."
                             : ctlKind == "gamecube" ? "A GameCube controller on an adapter."
                             : "One Joy-Con held sideways. Which Joy-Con button is which number is not known yet.";
            var cells = new List<UIElement>();
            for (int i = 0; i < kind.Buttons.Length; i++)
            {
                int at = kind.First + i;
                int code = t != null ? t.Controls[at] : NameTagSave.DefaultControls[at];
                var sp = new StackPanel { Margin = new Thickness(0, 0, 12, 10) };
                var head = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
                string pc = ctlKind == "pro" ? PcButtonOf(kind.Buttons[i]) : null;
                if (pc != null) { var hint = new TextBlock { Text = pc, FontSize = 12, Foreground = Res("Dim"), VerticalAlignment = VerticalAlignment.Bottom, ToolTip = "The PC controller's button that presses it (Advanced: pad buttons)" }; DockPanel.SetDock(hint, Dock.Right); head.Children.Add(hint); }
                head.Children.Add(new TextBlock { Text = kind.Buttons[i], FontSize = 15, Foreground = Res("Muted") });
                sp.Children.Add(head);
                var codes = NameTagSave.Actions.Keys.ToList();
                if (!NameTagSave.Actions.ContainsKey(code)) codes.Add(code);      // a code not named here: shown and kept
                var cb = new ComboBox { ItemsSource = codes.Select(ActionName).ToList(), SelectedIndex = codes.IndexOf(code), HorizontalAlignment = HorizontalAlignment.Stretch, IsEnabled = can, Tag = Tuple.Create(at, codes),
                                        ToolTip = code == NameTagSave.DefaultControls[at] ? "The game's default" : "The game's default: " + ActionName(NameTagSave.DefaultControls[at]) };
                cb.SelectionChanged += CtlButton_Changed;
                sp.Children.Add(cb);
                cells.Add(sp);
            }
            CtlButtons.ItemsSource = cells;
            ctlFilling = false;
        }

        static string ActionName(int code) => NameTagSave.Actions.TryGetValue(code, out var n) ? n : "Other (" + code + ")";

        /// <summary>which button of an Xbox / PlayStation pad presses this Switch button with the pad buttons of this page (Core.Buttons)</summary>
        static string PcButtonOf(string sw)
        {
            if (!Core.Buttons.TryGetValue(sw, out var src)) return null;      // (the D-pad and the right stick are not swapped there)
            switch (src)
            {
                case "east": return "Xbox B · PS ○";
                case "south": return "Xbox A · PS ✕";
                case "north": return "Xbox Y · PS △";
                case "west": return "Xbox X · PS □";
                case "leftshoulder": return "LB · L1";
                case "rightshoulder": return "RB · R1";
                case "lefttrigger": return "LT · L2";
                case "righttrigger": return "RT · R2";
                case "leftstick": return "left stick press";
                case "rightstick": return "right stick press";
                case "start": return "Menu · Options";
                case "back": return "View · Share";
            }
            return null;
        }

        /// <summary>writes an edit to the save (NameTagFile.Edit); false and a message when it cannot</summary>
        bool CtlWrite(string what, Action<NameTagSave> edit)
        {
            try
            {
                string bak = NameTagFile.Edit(Core.Saves, CtlGameOpen, edit);
                Updater.Log("controls: " + what + (bak != null ? " (a copy of the save: " + bak + ")" : ""));
                Status(char.ToUpper(what[0]) + what.Substring(1) + ": saved in the save", "good");
                return true;
            }
            catch (Exception e)
            {
                Updater.Log("controls: " + what + " NOT written: " + e.Message);
                Show("Name tags", e is SaveFileError ? e.Message : "The save could not be written: " + e.Message, null, ("OK", null, true));
                return false;
            }
            finally { ControlsFill(); }
        }

        void CtlButton_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ctlFilling || ctlSave == null || ctlSlot <= 0) return;
            var cb = (ComboBox)sender;
            var tag = (Tuple<int, List<int>>)cb.Tag;
            if (cb.SelectedIndex < 0 || cb.SelectedIndex >= tag.Item2.Count) return;
            int code = tag.Item2[cb.SelectedIndex], at = tag.Item1, slot = ctlSlot;
            string name = ctlSave.Tag(slot).Name;
            var kind = NameTagSave.Kinds.First(k => k.Id == ctlKind);
            string button = kind.Buttons[at - kind.First];
            CtlWrite(name + ": " + kind.Title + " " + button + " = " + ActionName(code), s =>
            {
                var c = s.Tag(slot).Controls;
                c[at] = (byte)code;
                s.SetControls(slot, c);
            });
        }

        void CtlReset_Click(object sender, RoutedEventArgs e)
        {
            if (ctlSave == null || ctlSlot <= 0) return;
            int slot = ctlSlot;
            var kind = NameTagSave.Kinds.First(k => k.Id == ctlKind);
            string name = ctlSave.Tag(slot).Name;
            CtlWrite(name + ": " + kind.Title + " back to the game's defaults", s =>
            {
                var c = s.Tag(slot).Controls;
                for (int i = kind.First; i < kind.Options + 4; i++) c[i] = NameTagSave.DefaultControls[i];      // its buttons and its 4 options
                s.SetControls(slot, c);
            });
        }

        /// <summary>a name for a new or renamed tag: the dialog with a text box; done gets the trimmed name</summary>
        void CtlAskName(string title, string text, string start, Action<string> done)
        {
            var box = new TextBox { Text = start ?? "", MaxLength = NameTagSave.NameMax, FontSize = 18, Height = 40, Padding = new Thickness(8, 6, 8, 0), Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
            Show(title, text, null, ("Cancel", null, false), ("OK", () => done(box.Text.Trim()), true));
            DialogExtra.Content = box;
            Dispatcher.BeginInvoke(new Action(() => { box.Focus(); box.SelectAll(); }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>why a name cannot be used for entry slot (null = it can)</summary>
        string CtlNameProblem(string name, int slot)
        {
            if (name == "") return "A name tag needs a name.";
            string why = NameTagSave.NameProblem(name);
            if (why != null) return char.ToUpper(why[0]) + why.Substring(1) + ".";
            if (name.Any(char.IsControl)) return "A name cannot hold control characters.";
            if (ctlSave != null && ctlSave.Named().Any(t => t.Slot != slot && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) return "There is a name tag called " + name + " already.";
            return null;
        }

        void CtlNew_Click(object sender, RoutedEventArgs e)
        {
            if (ctlSave == null) return;
            CtlAskName("New name tag", "Its name (at most " + NameTagSave.NameMax + " characters). It starts with the game's default controls.", "", name =>
            {
                string why = CtlNameProblem(name, -1);
                if (why != null) { Show("New name tag", why, null, ("OK", null, true)); return; }
                int made = -1;
                if (CtlWrite("new name tag " + name, s => made = s.Create(name))) { ctlSlot = made; ControlsFill(); }
            });
        }

        void CtlRename_Click(object sender, RoutedEventArgs e)
        {
            if (ctlSave == null || ctlSlot <= 0) return;
            int slot = ctlSlot;
            string old = ctlSave.Tag(slot).Name;
            CtlAskName("Rename " + old, "The new name (at most " + NameTagSave.NameMax + " characters). Its controls and records stay.", old, name =>
            {
                if (name == old) return;
                string why = CtlNameProblem(name, slot);
                if (why != null) { Show("Rename", why, null, ("OK", null, true)); return; }
                bool mine = Core.MyNameTag != "" && Core.MyNameTag == Core.MyTagValue(ctlSave.Tag(slot));
                if (CtlWrite(old + " renamed " + name, s => s.SetName(slot, name)) && mine)
                {
                    Core.MyNameTag = Core.MyTagValue(ctlSave?.Tag(slot));      // (ControlsFill read the save again: the new name)
                    SaveQuiet(); ControlsFill();
                }
            });
        }

        void CtlDelete_Click(object sender, RoutedEventArgs e)
        {
            if (ctlSave == null || ctlSlot <= 0) return;
            int slot = ctlSlot;
            string name = ctlSave.Tag(slot).Name;
            Show("Delete " + name, "The name tag " + name + " goes from the save, with its controls and its records (as when the game deletes one). A copy of the save is kept in save-backups.", null,
                 ("Cancel", null, false), ("Delete", () =>
                 {
                     bool mine = Core.MyNameTag != "" && Core.MyNameTag == Core.MyTagValue(ctlSave.Tag(slot));
                     if (CtlWrite("name tag " + name + " deleted", s => s.Delete(slot)) && mine) { Core.MyNameTag = ""; SaveQuiet(); ControlsFill(); }
                 }, true));
        }

        // ---------------------------------------------------------------- my name tag
        List<string> myTagValues = new List<string>();

        void FillMyTag(List<NameTag> tags)
        {
            ctlFilling = true;
            myTagValues = new List<string> { "" };
            var names = new List<string> { "None: the one picked on the character select" };
            foreach (var t in tags) { myTagValues.Add(Core.MyTagValue(t)); names.Add(t.Name); }
            int sel = myTagValues.IndexOf(Core.MyNameTag);
            if (sel < 0 && Core.MyNameTag != "")
            {
                // the same name in another entry (moved in the game): that one; else the setting shown as lost
                string h = Core.MyNameTag.Split(':').Last();
                sel = myTagValues.FindIndex(v => v != "" && v.EndsWith(":" + h));
                if (sel < 0) { myTagValues.Add(Core.MyNameTag); names.Add("A name tag no longer in the save (off)"); sel = names.Count - 1; }
            }
            MyTagBox.ItemsSource = names;
            MyTagBox.SelectedIndex = Math.Max(0, sel);
            MyTagBox.IsEnabled = ctlSave != null;
            ctlFilling = false;
        }

        void MyTag_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (ctlFilling || loading || MyTagBox.SelectedIndex < 0 || MyTagBox.SelectedIndex >= myTagValues.Count) return;
            string v = myTagValues[MyTagBox.SelectedIndex];
            if (v == Core.MyNameTag) return;
            Core.MyNameTag = v;
            if (SaveQuiet()) Status(v == "" ? "No name tag of your own: the game's controls unless you pick one" : "My name tag: " + MyTagBox.SelectedItem + " (from the next start of the game)", "good");
            C3Fill();      // (v3) the actions shown are this name tag's
        }

        /// <summary>the warning of the pad buttons box: shown when a button is swapped</summary>
        void PadAdvRefresh()
        {
            if (!Core.ControlsV2 || PadAdvWarn == null) return;
            bool swapped = Core.DefaultButtons.Any(kv => !Core.Buttons.TryGetValue(kv.Key, out var v) || v != kv.Value);
            PadAdvWarn.Visibility = swapped ? Visibility.Visible : Visibility.Collapsed;
            if (BoxControls.Visibility == Visibility.Visible && ctlKind == "pro" && !ctlFilling) ControlsFillTag();      // the PC button hints
            if (C3Shown && !loading && !ctlFilling && c3Cap == null && c3Plan == null) C3Fill();      // (v3) the buttons' names
        }
    }
}
