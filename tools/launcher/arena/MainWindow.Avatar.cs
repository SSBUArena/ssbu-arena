// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.20) the profile picture: a fighter's portrait from this player's own game files (Core.MakeAvatars). The account stores only the
// fighter's name (public.profiles.avatar, rpc set_my_avatar); no game content goes to the server. In the game it is the fighter's own
// profile icon (Core.WriteProfileIcon): the game has no way to show other images, so only fighters that have one can be chosen.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Arena
{
    public partial class MainWindow
    {
        Brush avatarPlain;

        static BitmapImage AvatarImage(string name, int width)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string f = Path.Combine(Core.AvatarsDir, name + ".png");
            if (!File.Exists(f)) f = Path.Combine(Core.AvatarsDir, "more", name + ".png");      // (the replay list's other portraits)
            if (!File.Exists(f)) f = Path.Combine(Core.AvatarsDir, "all", name + ".png");       // (0.2.34) the game's other profile icons
            if (!File.Exists(f)) return null;
            try
            {
                var b = new BitmapImage();
                b.BeginInit(); b.CacheOption = BitmapCacheOption.OnLoad; b.DecodePixelWidth = width; b.UriSource = new Uri(f); b.EndInit(); b.Freeze();
                return b;
            }
            catch { return null; }
        }

        /// <summary>the home page's disc: the picture, or the plain icon</summary>
        void ShowAvatar()
        {
            if (avatarPlain == null) avatarPlain = AvatarDisc.Fill;
            var im = Account.SignedIn ? AvatarImage(Account.Me.Avatar, 132) : null;
            AvatarDisc.Fill = im != null ? new ImageBrush(im) { Stretch = Stretch.UniformToFill } : avatarPlain;
            AvatarIcon.Visibility = im != null ? Visibility.Collapsed : Visibility.Visible;
            foreach (var b in AvatarGrid.Children.OfType<Button>())
                b.BorderBrush = (b.Tag as string) == (Account.Me?.Avatar ?? "") ? Res("Accent") : Res("Line");
        }

        /// <summary>(0.2.34) one picture of the settings' grid: the key the account stores (a fighter's name for its first
        /// colour, else icon_number), its group and its name</summary>
        sealed class AvatarEntry { public string Key, Kind, Label; public int Order; }
        string avatarTab;

        static string Pretty(string s) => string.Join(" ", (s ?? "").Split('_').Where(w => w.Length > 0).Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));

        /// <summary>every picture this PC has: the fighters (avatars\icons.txt, their other colours from all\icons.txt), the spirits
        /// and the series' emblems (all\icons.txt), each with the game's own icon behind it</summary>
        static List<AvatarEntry> AvatarEntries()
        {
            var list = new List<AvatarEntry>();
            var firstOf = new Dictionary<int, string>();      // the game's number -> the fighter key it has in icons.txt
            var order = new Dictionary<string, int>();
            try
            {
                string root = Path.Combine(Core.AvatarsDir, "icons.txt");
                if (File.Exists(root))
                    foreach (var l in File.ReadAllLines(root))
                    {
                        int eq = l.IndexOf('='); if (eq <= 0 || !int.TryParse(l.Substring(eq + 1), out int n)) continue;
                        string f = l.Substring(0, eq);
                        if (!File.Exists(Path.Combine(Core.AvatarsDir, f + ".png"))) continue;
                        firstOf[n] = f; order[f] = order.Count;
                        list.Add(new AvatarEntry { Key = f, Kind = "fighter", Label = ReplayNames.FighterName(ReplayNames.Hash40("ui_chara_" + f), 0), Order = order[f] * 16 });
                    }
                string all = Path.Combine(Core.AvatarsDir, "all", "icons.txt");
                if (File.Exists(all))
                    foreach (var l in File.ReadAllLines(all))
                    {
                        int eq = l.IndexOf('='); if (eq <= 0) continue;
                        var v = l.Substring(eq + 1).Split(',');
                        if (v.Length < 4 || !int.TryParse(v[0], out int n) || firstOf.ContainsKey(n)) continue;
                        int.TryParse(v[3], out int slot);
                        string key = l.Substring(0, eq), kind = v[1], name = v[2];
                        if (kind == "fighter")
                        {
                            int o = order.TryGetValue(name, out int fo) ? fo * 16 + 1 + slot : 100000 + n;
                            list.Add(new AvatarEntry { Key = key, Kind = kind, Label = ReplayNames.FighterName(ReplayNames.Hash40("ui_chara_" + name), 0) + ", colour " + (slot + 1), Order = o });
                        }
                        else list.Add(new AvatarEntry { Key = key, Kind = kind, Label = Pretty(kind == "series" ? name : name), Order = 200000 + n });
                    }
            }
            catch { }
            return list.OrderBy(x => x.Order).ToList();
        }

        /// <summary>the settings' grid of pictures: Fighters, Spirits, Series tabs (the one with the chosen picture first)</summary>
        void FillAvatars()
        {
            AvatarGrid.Children.Clear(); AvatarTabs.Children.Clear();
            bool newIcons = fighterIcons.Values.Any(v => v == null);      // (the replay list's portraits were made after it asked)
            fighterIcons.Clear();
            if (newIcons && PageReplays.Visibility == Visibility.Visible) ShowReplayRows();
            var entries = AvatarEntries();
            AvatarNote.Text = entries.Count == 0 ? "The pictures come from your game files: they appear here once they are ready."
                                                 : "Your picture, in the launcher and in the game's rooms, where the other players see it too. Used from the next start of the game. It comes from your own game files: your account stores only which one it is.";
            AvatarTabsWell.Visibility = entries.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (entries.Count == 0) { ShowAvatar(); return; }
            if (avatarTab == null) avatarTab = entries.FirstOrDefault(x => x.Key == Account.Me?.Avatar)?.Kind ?? "fighter";
            foreach (var t in new[] { ("fighter", "Fighters"), ("spirit", "Spirits"), ("series", "Series") })
            {
                int n = entries.Count(x => x.Kind == t.Item1); if (n == 0) continue;
                var rb = new RadioButton { Content = t.Item2 + "  " + n, Tag = t.Item1, Style = (Style)FindResource("RvSeg"), GroupName = "avatartab", IsChecked = avatarTab == t.Item1, Padding = new Thickness(14, 0, 14, 0) };
                rb.Checked += (s, e) => { avatarTab = (string)((RadioButton)s).Tag; FillAvatarGrid(entries); };
                AvatarTabs.Children.Add(rb);
            }
            FillAvatarGrid(entries);
        }

        void FillAvatarGrid(List<AvatarEntry> entries)
        {
            AvatarGrid.Children.Clear();
            if (avatarTab == "fighter") AvatarGrid.Children.Add(AvatarButton("", null, "No picture"));
            foreach (var x in entries.Where(x => x.Kind == avatarTab)) AvatarGrid.Children.Add(AvatarButton(x.Key, AvatarImage(x.Key, 96), x.Label));
            ShowAvatar();
        }

        Button AvatarButton(string name, BitmapImage image, string label)
        {
            var b = new Button
            {
                Tag = name, Width = 60, Height = 60, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = Res("Field"), BorderThickness = new Thickness(2), BorderBrush = Res("Line"), ToolTip = label,
                Template = AvatarTemplate(),
                Content = image != null ? (object)new Image { Source = image, Stretch = Stretch.UniformToFill } : new TextBlock { Text = "None", FontSize = 13, Foreground = Res("Muted"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            };
            b.Click += (s, e) => PickAvatar(name);
            return b;
        }

        static ControlTemplate avatarTemplate;
        static ControlTemplate AvatarTemplate()
        {
            if (avatarTemplate != null) return avatarTemplate;
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            border.SetValue(UIElement.ClipToBoundsProperty, true);
            border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.SetBinding(Border.BorderThicknessProperty, new System.Windows.Data.Binding("BorderThickness") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            return avatarTemplate = new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        void PickAvatar(string name)
        {
            if (!Account.SignedIn) return;
            string before = Account.Me.Avatar;
            Account.Me.Avatar = name == "" ? null : name;
            ShowAvatar();
            string body = new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new System.Collections.Generic.Dictionary<string, object> { { "p_avatar", Account.Me.Avatar } });
            Task.Run(() => Account.Rest("POST", "/rest/v1/rpc/set_my_avatar", body)).ContinueWith(t =>
            {
                if (t.Exception == null) return;
                Updater.Log("profile picture: " + t.Exception.GetBaseException().Message);
                if (Account.SignedIn) { Account.Me.Avatar = before; ShowAvatar(); }
                Status("The profile picture could not be saved", "warn");
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
