// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.18) the "What's new" page: the changes of each version, from the website's /changelog.json (the same file its /changelog page
// shows). The last copy read stays in build\changelog.json for when the site cannot be reached. SSBU_ARENA_CHANGELOG: another address (test).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace Arena
{
    public partial class MainWindow
    {
        bool changesLoaded;

        static string ChangesCopy => Path.Combine(Core.Build, "changelog.json");

        void ChangesShown()
        {
            if (changesLoaded) return;
            changesLoaded = true;
            string url = Environment.GetEnvironmentVariable("SSBU_ARENA_CHANGELOG");
            if (string.IsNullOrEmpty(url)) url = "https://www.ssbuarena.com/changelog.json";
            Task.Run(() =>
            {
                try
                {
                    string text = Encoding.UTF8.GetString(Updater.Get(url));
                    new JavaScriptSerializer().DeserializeObject(text);      // (not valid: the copy stays)
                    try { File.WriteAllText(ChangesCopy, text, Encoding.UTF8); } catch { }
                    return text;
                }
                catch (Exception ex)
                {
                    Updater.Log("changelog: " + ex.Message);
                    try { return File.ReadAllText(ChangesCopy, Encoding.UTF8); } catch { return null; }
                }
            }).ContinueWith(t => FillChanges(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
        }

        void FillChanges(string json)
        {
            ChangesList.Children.Clear();
            ArrayList list = null;
            try { if (json != null) list = new JavaScriptSerializer().DeserializeObject(json) is object[] a ? new ArrayList(a) : null; } catch { }
            if (list == null || list.Count == 0)
            {
                changesLoaded = false;      // (tried again the next time the page opens)
                ChangesNote.Text = "The list could not be loaded: check the internet connection.";
                return;
            }
            ChangesNote.Text = "Every version of SSBU Arena and what it brought.";
            foreach (var o in list)
            {
                if (!(o is Dictionary<string, object> r)) continue;
                string version = r.TryGetValue("version", out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) : "";
                string date = r.TryGetValue("date", out var dv) && DateTime.TryParse(Convert.ToString(dv, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                              ? d.ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB")) : "";
                var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, ChangesList.Children.Count == 0 ? 4 : 22, 0, 6) };
                title.Children.Add(new TextBlock { Text = "Version " + version, FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
                title.Children.Add(new TextBlock { Text = date, FontSize = 15, Foreground = Res("Muted"), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                if (version == Updater.Current.ToString(3))
                    title.Children.Add(new TextBlock { Text = "YOUR VERSION", FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Res("Accent"), Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
                ChangesList.Children.Add(title);
                if (!r.TryGetValue("items", out var items) || !(items is object[] lines)) continue;
                foreach (var line in lines)
                {
                    var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
                    var dot = new TextBlock { Text = "•", FontSize = 16, Foreground = Res("Accent"), Margin = new Thickness(2, 0, 10, 0) };
                    DockPanel.SetDock(dot, Dock.Left);
                    row.Children.Add(dot);
                    row.Children.Add(new TextBlock { Text = Convert.ToString(line, CultureInfo.InvariantCulture), FontSize = 16, Foreground = Res("Text"), TextWrapping = TextWrapping.Wrap });
                    ChangesList.Children.Add(row);
                }
            }
        }
    }
}
