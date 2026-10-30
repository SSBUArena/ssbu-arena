// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.20) the profile picture: a fighter's portrait from this player's own game files (Core.MakeAvatars). The account stores only the
// fighter's name (public.profiles.avatar, rpc set_my_avatar); no game content goes to the server. In the game it is the fighter's own
// profile icon (Core.WriteProfileIcon): the game has no way to show other images, so only fighters that have one can be chosen.
using System;
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

        /// <summary>the settings' grid of pictures</summary>
        void FillAvatars()
        {
            AvatarGrid.Children.Clear();
            string[] files = { };
            try { if (Directory.Exists(Core.AvatarsDir)) files = Directory.GetFiles(Core.AvatarsDir, "*.png").Where(f => Core.ProfileIconOf(Path.GetFileNameWithoutExtension(f)) >= 0).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray(); } catch { }
            AvatarNote.Text = files.Length == 0 ? "The pictures come from your game files: they appear here once they are ready."
                                                : "Your picture, in the launcher and in the game's rooms, where the other players see it too. Used from the next start of the game. It comes from your own game files: your account stores only the fighter's name.";
            if (files.Length > 0) AvatarGrid.Children.Add(AvatarButton("", null));
            foreach (var f in files) AvatarGrid.Children.Add(AvatarButton(Path.GetFileNameWithoutExtension(f), AvatarImage(Path.GetFileNameWithoutExtension(f), 96)));
            ShowAvatar();
        }

        Button AvatarButton(string name, BitmapImage image)
        {
            var b = new Button
            {
                Tag = name, Width = 60, Height = 60, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
                Background = Res("Field"), BorderThickness = new Thickness(2), BorderBrush = Res("Line"), ToolTip = name == "" ? "No picture" : name,
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
