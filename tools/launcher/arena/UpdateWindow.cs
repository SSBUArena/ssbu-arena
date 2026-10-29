// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.12) the first thing a player sees: a small window that checks the latest release before the launcher opens, and
// brings in a newer launcher (it starts again) or the game's update (install.py, its progress here). Then the launcher.
// Not installed, an install not finished, a git checkout, a portable copy or SSBU_ARENA_UPDATE=off: the launcher at once
// (its own screens do the first install and the setup).
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Arena
{
    public sealed class UpdateWindow : Window
    {
        /// <summary>the check was made here: MainWindow does not make it again (Release: what it found, null offline)</summary>
        public static bool Checked;
        public static Release Release;
        /// <summary>the game's update was left to MainWindow (the game was running: it says "close the game to update")</summary>
        public static bool GameLeft;

        readonly TextBlock status, detail, pct;
        readonly Border track, fill;
        readonly TranslateTransform slide = new TranslateTransform();
        readonly StackPanel buttons;
        readonly List<string> steps = new List<string>();
        readonly DateTime shown = DateTime.UtcNow;

        static Brush Res(string k) => (Brush)Application.Current.Resources[k];

        public UpdateWindow()
        {
            Title = "SSBU Arena";
            Width = 460; Height = 290;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Res("BgDeep");
            BorderBrush = Res("Line"); BorderThickness = new Thickness(1);
            UseLayoutRounding = true;
            try { Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/arena.ico")); } catch { }
            MouseLeftButtonDown += (s, e) => { try { DragMove(); } catch { } };

            var root = new Grid();
            root.Background = new RadialGradientBrush((Color)ColorConverter.ConvertFromString("#14223A"), (Color)ColorConverter.ConvertFromString("#090D14"))
                { Center = new Point(0.5, 0.2), GradientOrigin = new Point(0.5, 0.2), RadiusX = 0.9, RadiusY = 1.1 };
            var inner = new Grid { Margin = new Thickness(32, 34, 32, 20) };
            root.Children.Add(inner);
            for (int i = 0; i < 6; i++) inner.RowDefinitions.Add(new RowDefinition { Height = i == 1 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var brand = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            brand.Children.Add(new Path { Data = (Geometry)Application.Current.Resources["BrandMarkGeometry"], Fill = Res("Accent"), Width = 40, Height = 34, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 14, 0) });
            brand.Children.Add(new Path { Data = (Geometry)Application.Current.Resources["WordmarkGeometry"], Fill = Res("Text"), Height = 26, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center });
            inner.Children.Add(brand);

            status = new TextBlock { Text = "Checking for updates", Foreground = Res("Text"), FontFamily = (FontFamily)Application.Current.Resources["Display"], FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            pct = new TextBlock { Foreground = Res("Accent"), FontFamily = (FontFamily)Application.Current.Resources["Display"], FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
            var line = new Grid { VerticalAlignment = VerticalAlignment.Bottom };
            line.Children.Add(status); line.Children.Add(pct);
            Grid.SetRow(line, 2); inner.Children.Add(line);

            fill = new Border { Background = Res("Accent"), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Width = 90, RenderTransform = slide };
            track = new Border { Background = Res("Line"), CornerRadius = new CornerRadius(2), Height = 4, ClipToBounds = true, Child = fill };
            Grid.SetRow(track, 3); inner.Children.Add(track);

            detail = new TextBlock { Text = "Version " + Updater.Current.ToString(3), Foreground = Res("Muted"), FontFamily = (FontFamily)Application.Current.Resources["Body"], FontSize = 13, Margin = new Thickness(0, 10, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetRow(detail, 4); inner.Children.Add(detail);

            buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
            Grid.SetRow(buttons, 5); inner.Children.Add(buttons);

            Content = root;
            Indeterminate(true);
            Loaded += (s, e) => Begin();
        }

        // ------------------------------------------------------------ the bar
        void Indeterminate(bool on)
        {
            if (on)
            {
                fill.Width = 90; pct.Text = "";
                var a = new DoubleAnimation(-90, 400, TimeSpan.FromSeconds(1.2)) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
                slide.BeginAnimation(TranslateTransform.XProperty, a);
            }
            else { slide.BeginAnimation(TranslateTransform.XProperty, null); slide.X = 0; }
        }

        void Progress(double f)
        {
            Indeterminate(false);
            f = Math.Max(0, Math.Min(1, f));
            fill.Width = Math.Max(4, track.ActualWidth * f);
            pct.Text = (int)(f * 100) + "%";
        }

        // ------------------------------------------------------------ the check
        void Begin()
        {
            bool skip = Updater.Off || (Core.Portable && Core.Repo == null) || !Updater.Installed || Updater.DevCopy || !System.IO.File.Exists(Core.Exe);
            if (skip) { OpenLauncher(); return; }      // MainWindow.CheckUpdates: the first install, the setup, the log line
            Checked = true;
            Updater.CleanUp();
            try { System.IO.File.Delete(System.IO.Path.Combine(Core.Repo, @"build\launcher_update.log")); } catch { }
            Updater.Log("launcher " + Updater.Current + ", repo " + Core.Repo + ", build " + Core.Build + " (update window)");
            Task.Run(() => { try { return Updater.Latest(); } catch (Exception ex) { Updater.Log("release check failed: " + ex.Message); return null; } }).ContinueWith(t =>
            {
                Release = t.Result;
                if (Release == null) { Say("Could not check for updates", "No connection to GitHub: the launcher opens as it is."); Later(1500, OpenLauncher); return; }
                Updater.Log("latest release " + Release.Tag + " (version " + Release.Version + ", commit " + Release.Commit + "); installed commit " + (Updater.InstalledCommit() ?? "unknown"));
                if (Updater.LauncherOutdated(Release)) { UpdateLauncher(); return; }
                GameCheck();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void GameCheck()
        {
            if (!Updater.GameOutdated(Release)) { Progress(1); pct.Text = ""; Say("Up to date", "SSBU Arena " + Updater.Current.ToString(3)); Later(500, OpenLauncher); return; }
            if (Core.GameRunning()) { GameLeft = true; OpenLauncher(); return; }      // the launcher says "close the game to update"
            UpdateGame();
        }

        void UpdateLauncher()
        {
            Say("Updating the launcher", "SSBU Arena " + Release.Version);
            Task.Run(() => Updater.ReplaceLauncher(Release)).ContinueWith(t =>
            {
                if (t.Exception == null) { Application.Current.Shutdown(); return; }      // the new launcher was started (--updated)
                Updater.Log("launcher update failed: " + t.Exception.GetBaseException().Message);
                GameCheck();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void UpdateGame()
        {
            Updater.Log("game update: scripts\\install.py (log: build\\update_last.log, update window)");
            Say("Updating to " + Release.Version, "Downloading " + Release.Tag + "...");
            try { Updater.RunGameUpdate(l => Dispatcher.BeginInvoke(new Action(() => UpdateLine(l))), c => Dispatcher.BeginInvoke(new Action(() => UpdateEnded(c)))); }
            catch (Exception ex) { Failed(ex.Message); }
        }

        void UpdateLine(string l)
        {
            if (!l.StartsWith("##ssbu ")) return;
            var v = new Dictionary<string, string>();
            foreach (System.Text.RegularExpressions.Match m in MainWindow.UpdField.Matches(l))
                v[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Success ? m.Groups[3].Value : m.Groups[4].Value;
            v.TryGetValue("ev", out var ev);
            if (ev == "plan") { foreach (System.Text.RegularExpressions.Match m in new System.Text.RegularExpressions.Regex("\"id\":\\s*\"([^\"]+)\"").Matches(l)) steps.Add(m.Groups[1].Value); return; }
            if (!v.TryGetValue("id", out var id)) return;
            int i = Math.Max(0, steps.IndexOf(id)), n = Math.Max(1, steps.Count);
            double inside = 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (ev == "progress" && double.TryParse(v.ContainsKey("done") ? v["done"] : "0", System.Globalization.NumberStyles.Float, inv, out var d)
                && double.TryParse(v.ContainsKey("total") ? v["total"] : "0", System.Globalization.NumberStyles.Float, inv, out var tot) && tot > 0)
                inside = Math.Min(1, d / tot);
            if (ev == "step" && v.TryGetValue("state", out var st) && st != "start") inside = 1;
            Progress((i + inside) / n);
            if (ev == "step" && v.TryGetValue("state", out var s2) && s2 == "start") detail.Text = MainWindow.UpdateStepName(id);
        }

        void UpdateEnded(int code)
        {
            Updater.Log("game update ended with " + code + " (update window)");
            if (code == 0) { Progress(1); Say("Updated to " + Release.Version, "Opening SSBU Arena"); Later(700, OpenLauncher); return; }
            Failed("Exit code " + code + ": the game you had is still there (build\\update_last.log).");
        }

        void Failed(string why)
        {
            Indeterminate(false); fill.Width = 0; pct.Text = "";
            status.Text = "The update failed"; status.Foreground = Res("Warn");
            detail.Text = why; detail.TextWrapping = TextWrapping.Wrap; detail.TextTrimming = TextTrimming.None;
            buttons.Children.Clear();
            buttons.Children.Add(Button("Try again", false, () => { status.Foreground = Res("Text"); buttons.Visibility = Visibility.Collapsed; Indeterminate(true); UpdateGame(); }));
            buttons.Children.Add(Button("Continue", true, OpenLauncher));
            buttons.Visibility = Visibility.Visible;
            Height = 330;
        }

        Button Button(string text, bool primary, Action click)
        {
            var b = new Button { Content = text, MinWidth = 104, Height = 34, Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand };
            var style = Application.Current.TryFindResource(primary ? "Primary" : "Outline") as Style;
            if (style != null) b.Style = style;
            b.Click += (s, e) => click();
            return b;
        }

        void Say(string title, string text) { status.Text = title; detail.Text = text; }

        void Later(int ms, Action a)
        {
            ms = Math.Max(ms, 900 - (int)(DateTime.UtcNow - shown).TotalMilliseconds);      // on screen long enough to be read
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, ms)) };
            t.Tick += (s, e) => { t.Stop(); a(); };
            t.Start();
        }

        bool opened;
        void OpenLauncher()
        {
            if (opened) return;
            opened = true;
            var m = new MainWindow();
            Application.Current.MainWindow = m;
            m.Show();
            Close();
        }
    }
}
