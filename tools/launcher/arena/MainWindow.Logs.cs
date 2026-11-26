// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.73) Send logs (LogReport.cs): the window that shows what goes and asks what happened, the report code at the end; the
// launcher's error window (LauncherCrash.cs); the setup's note when its error was reported on its own.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Arena
{
    public partial class MainWindow
    {
        /// <summary>SSBU_ARENA_SEND_LOGS=0: no Send logs button anywhere</summary>
        void SendLogsSwitch()
        {
            if (LogReport.Enabled) return;
            foreach (var b in new UIElement[] { HelpSendBox, FilesSendLogs, SetupBusySendLogs, SetupSendLogs }) b.Visibility = Visibility.Collapsed;
        }

        void SendLogs_Click(object sender, RoutedEventArgs e) { AskSendLogs(); }

        void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            try { Process.Start("explorer.exe", "\"" + Core.Build + "\""); } catch { }
        }

        /// <summary>the setup's own installer.log (an install location chosen on the setup page), when there is one</summary>
        IEnumerable<string> SetupLogs()
        {
            if (choice == null) yield break;
            string f = null;
            try { f = Installer.LogPath(choice); } catch { }
            if (f != null) yield return f;
        }

        static string Size(long b) => b < 1024 ? b + " bytes" : b < 1 << 20 ? (b / 1024.0).ToString("0") + " KB" : (b / 1048576.0).ToString("0.0") + " MB";

        void AskSendLogs()
        {
            if (!LogReport.Enabled) return;
            var extra = SetupLogs().ToList();
            var files = LogReport.Files(extra);
            var note = new TextBox
            {
                MaxLength = 500, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96, FontSize = 15,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top,
            };
            var count = new TextBlock { FontSize = 12.5, Foreground = Res("Dim"), HorizontalAlignment = HorizontalAlignment.Right, Text = "0 / 500" };
            note.TextChanged += (s, a) => count.Text = note.Text.Length + " / 500";
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "WHAT HAPPENED? (OPTIONAL)", Style = (Style)FindResource("FieldLabel") });
            panel.Children.Add(note);
            panel.Children.Add(count);
            string who = Account.SignedIn ? "with your account (" + Account.Me.Display + ")" : "without an account (you are not signed in)";
            Show("Send logs",
                 "These files go to the SSBU Arena staff " + who + ", with your launcher and Windows versions. Your Windows user and PC names, " +
                 "folders, e-mail and internet addresses and any pass or key are removed first; no saves and no settings are sent." +
                 (files.Count == 0 ? "\n\nNo log was found: only the launcher's and this PC's versions go." : ""),
                 files.Select(f => f.Name + "   " + Size(f.Size) + (f.Size > f.Max ? "  (start and end)" : "")),
                 ("Send", () => DoSendLogs(note.Text, extra), true), ("Cancel", null, false));
            DialogExtra.Content = panel;
            note.Focus();
        }

        void DoSendLogs(string note, List<string> extra)
        {
            Show("Sending logs", "Sending your logs to the Arena server...", null);
            Task.Run(() => LogReport.Send("manual", null, note, 60000, extra)).ContinueWith(t =>
            {
                var r = t.IsFaulted ? new LogReport.Result { Error = t.Exception.GetBaseException().Message } : t.Result;
                if (!r.Ok) { Show("Logs not sent", r.Error, null, ("Try again", () => DoSendLogs(note, extra), true), ("Close", null, false)); return; }
                Show("Logs sent", "Thank you. Give this report code to the SSBU Arena staff (on Discord, with what happened): they find your logs with it.", null,
                     ("Copy code", () => { try { Clipboard.SetText(r.Code); } catch { } Status("Report code " + r.Code + " copied", "good"); }, true), ("Close", null, false));
                DialogExtra.Content = CodeBox(r.Code);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        static UIElement CodeBox(string code) => new TextBox
        {
            Text = code, IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 30, FontWeight = FontWeights.Bold, Height = 58,
            VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 0, 14, 0), BorderThickness = new Thickness(1),
        };

        // ---------------------------------------------------------------- the launcher's own error (LauncherCrash.cs)
        TextBlock launcherErrorSent;

        /// <summary>the error window: what happened, the log's place, Send logs; closed() when it goes</summary>
        public void ShowLauncherError(string message, string logPath, Action closed)
        {
            var buttons = new List<(string, Action, bool)>();
            if (LogReport.Enabled) buttons.Add(("Send logs", () => { closed?.Invoke(); AskSendLogs(); }, false));
            buttons.Add(("OK", () => closed?.Invoke(), true));
            Show("The launcher had an error",
                 "Something went wrong in the launcher: " + message + "\n\nIt stays open, but what you were doing may not have finished: try it again. " +
                 "The details are in\n" + logPath, null, buttons.ToArray());
            launcherErrorSent = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = Res("Muted"),
                                                Text = LogReport.AutoEnabled ? "Sending a report automatically..." : "" };
            DialogExtra.Content = launcherErrorSent;
        }

        /// <summary>(fix/install-launcher-errors) the Arena server or GitHub did not answer (LauncherCrash: an expected error, not a
        /// crash): a status line, no error window, no report</summary>
        public void NetworkWarning(string message)
        {
            try { Status("No connection to the Arena server: check the internet connection (" + message + ")", "warn"); } catch { }
        }

        /// <summary>the automatic report of the error window is done</summary>
        public void LauncherErrorSent(LogReport.Result r)
        {
            if (launcherErrorSent == null || DialogExtra.Content != launcherErrorSent) return;
            launcherErrorSent.Text = r.Ok ? "The report was sent automatically (code " + r.Code + "): give this code to the staff if it happens again."
                                   : r.Duplicate ? "This error was already reported in the last hour." : "The report could not be sent: " + r.Error;
            launcherErrorSent.Foreground = r.Ok ? Res("Text") : Res("Muted");
        }

        // ---------------------------------------------------------------- setup errors, reported on their own
        /// <summary>(MainWindow.Setup.cs) the setup stopped with an error: sent in the background, then its code on the setup page</summary>
        void SetupErrorReport(string where, string error, string logPath)
        {
            SetupReportBox.Visibility = Visibility.Collapsed;
            if (!LogReport.AutoEnabled || string.IsNullOrWhiteSpace(error)) return;
            string what = where + ": " + LogReport.FirstLine(error);
            LogReport.AutoSend("install_error", what, SetupLogs().ToList(), r => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (r == null || !(r.Ok || r.Duplicate)) return;
                SetupReportNote.Text = r.Ok
                    ? "An error happened during the setup. The report was sent automatically (code " + r.Code + ")" + (logPath != null ? "; the log is " + logPath : "") + "."
                    : "This setup error was already reported in the last hour" + (logPath != null ? "; the log is " + logPath : "") + ".";
                SetupReportBox.Visibility = Visibility.Visible;
            })));
        }
    }
}
