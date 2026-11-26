// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.73) an exception nobody caught used to close the launcher without a trace (a player's setup: Install pressed, the window
// gone, no installer.log). Now the three ways one ends up unhandled are caught (App.OnStartup):
//  - on the window's thread (DispatcherUnhandledException): written to build\launcher_crash.log, marked handled so the launcher
//    stays open, the error shown with Send logs; sent on its own (LogReport.AutoSend, kind 'launcher_crash');
//  - on another thread (AppDomain.UnhandledException, the process ends): written, sent waiting at most 8 s, a message box;
//  - a task nobody waited for (TaskScheduler.UnobservedTaskException, the process goes on): written and sent, no window.
// Never a loop: one error window at a time, a handler that fails inside itself only gives up, and after 20 errors on the window's
// thread in one run the launcher closes instead (an error in every frame would otherwise never stop). SSBU_ARENA_THROW_TEST=ui |
// thread | task throws one 6 s after the start (a test); =cancel | network | disposed throws an expected one in a task (SetupErrors.cs
// Expected: written, never reported).
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Arena
{
    public static class LauncherCrash
    {
        /// <summary>where the launcher was (the setup sets it: its stage, Install pressed): written with the error</summary>
        public static volatile string Where;
        static int uiErrors, inside, windowOpen, fatal;

        public static string LogPath
        {
            get
            {
                try { return Path.Combine(Core.Build, "launcher_crash.log"); }
                catch { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher_crash.log"); }      // Core itself failed
            }
        }

        public static void Install(Application app)
        {
            app.DispatcherUnhandledException += (s, e) =>
            {
                if (Interlocked.Increment(ref uiErrors) > 20) { Write(e.Exception, "window thread, 20th error: the launcher closes"); return; }
                e.Handled = true;
                Handle(e.Exception, "window thread", true);
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (Interlocked.Exchange(ref fatal, 1) == 1) return;
                var ex = e.ExceptionObject as Exception ?? new Exception(Convert.ToString(e.ExceptionObject));
                Write(ex, "background thread" + (e.IsTerminating ? ", the launcher closes" : ""));                try
                {
                    var r = LogReport.AutoSendNow("launcher_crash", Describe(ex), 8000);
                    MessageBox.Show("SSBU Arena's launcher had an error and has to close.\n\n" + LogReport.FirstLine(ex.Message) + "\n\n" +
                                    (r != null && r.Ok ? "The report was sent automatically (code " + r.Code + ").\n" : "") +
                                    "Details: " + LogPath + "\n\nStart the launcher again; if it happens again, give the report code or that file to the staff.",
                                    "SSBU Arena", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
            };
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                try { e.SetObserved(); } catch { }
                var ex = e.Exception?.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
                Handle(ex, "background task", false);
            };
            string test = Environment.GetEnvironmentVariable("SSBU_ARENA_THROW_TEST");
            if (!string.IsNullOrEmpty(test))
            {
                var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                t.Tick += (s, e) =>
                {
                    t.Stop();
                    if (test == "thread") new Thread(() => throw new InvalidOperationException("SSBU_ARENA_THROW_TEST: an error on a background thread")) { IsBackground = true }.Start();
                    else if (test == "task") { Task.Run(new Action(() => throw new InvalidOperationException("SSBU_ARENA_THROW_TEST: an error in a task nobody waits for"))); }
                    else if (test == "cancel") { Task.Run(new Action(() => throw new OperationCanceledException("SSBU_ARENA_THROW_TEST: a cancelled task"))); }
                    else if (test == "network") { Task.Run(new Action(() => throw new ServerUnreachableException("Cannot reach the Arena server: SSBU_ARENA_THROW_TEST", new WebException("test", WebExceptionStatus.NameResolutionFailure)))); }
                    else if (test == "disposed") { Task.Run(new Action(() => throw new ObjectDisposedException("System.Net.Sockets.Socket"))); }
                    else throw new InvalidOperationException("SSBU_ARENA_THROW_TEST: an error on the window's thread");
                };
                t.Start();
                if (test == "task" || test == "cancel" || test == "network" || test == "disposed") { var g = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) }; g.Tick += (s, e) => { g.Stop(); GC.Collect(); GC.WaitForPendingFinalizers(); }; g.Start(); }
            }
        }

        /// <summary>the exception's type and message, then where it was: the first line is what the report's "exception" holds</summary>
        static string Describe(Exception ex)
        {
            var b = ex.GetBaseException();
            return b.GetType().Name + ": " + LogReport.FirstLine(b.Message) + (Where != null ? " [" + Where + "]" : "");
        }

        static void Handle(Exception ex, string thread, bool window)
        {
            if (ex == null || Interlocked.Exchange(ref inside, 1) == 1) return;          // an error inside this handler: given up
            try
            {
                // (fix/install-launcher-errors) a cancelled sign-in, its closed listener, no internet: written, no report, no
                // error window (0.2.74: 13 of its 16 "launcher crashes" were these); on the window's thread a network one is a
                // status line instead. SSBU_ARENA_REPORT_ALL=1 reports them as before.
                string expected = Expected.Kind(ex, !window);
                if (expected != null)
                {
                    Write(ex, thread + ", " + expected + ": not a crash, not reported");
                    if (window && expected == "network")
                        try { Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsLoaded)?.NetworkWarning(LogReport.FirstLine(ex.GetBaseException().Message)); } catch { }
                    return;
                }
                Write(ex, thread);
                string what = Describe(ex);
                MainWindow main = null;
                if (window) try { main = Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault(w => w.IsLoaded); } catch { }
                bool show = window && Interlocked.CompareExchange(ref windowOpen, 1, 0) == 0;
                if (show && main != null) main.ShowLauncherError(LogReport.FirstLine(ex.GetBaseException().Message), LogPath, () => Interlocked.Exchange(ref windowOpen, 0));
                LogReport.AutoSend("launcher_crash", what, null, r =>
                {
                    if (show && main != null && r != null) try { main.Dispatcher.BeginInvoke(new Action(() => main.LauncherErrorSent(r))); } catch { }
                });
                if (show && main == null)
                {
                    // no launcher window yet (the update window, the start): a plain message box, on its own thread so this one goes on
                    var t = new Thread(() =>
                    {
                        try
                        {
                            MessageBox.Show("SSBU Arena's launcher had an error:\n\n" + LogReport.FirstLine(ex.GetBaseException().Message) + "\n\nDetails: " + LogPath +
                                            (LogReport.AutoEnabled ? "\n\nA report is sent automatically." : ""), "SSBU Arena", MessageBoxButton.OK, MessageBoxImage.Warning);
                        }
                        catch { }
                        Interlocked.Exchange(ref windowOpen, 0);
                    }) { IsBackground = true };
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                }
            }
            catch { }
            finally { Interlocked.Exchange(ref inside, 0); }
        }

        /// <summary>appends the error to build\launcher_crash.log (kept under 512 KB: the older half goes)</summary>
        static void Write(Exception ex, string thread)
        {
            try
            {
                string path = LogPath;
                var sb = new StringBuilder();
                sb.Append("=== ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("  launcher ");
                try { sb.Append(Updater.Current.ToString(3)); } catch { sb.Append('?'); }
                sb.Append("  ").Append(thread).Append("  where: ").Append(Where ?? "-").Append("\r\n");
                sb.Append(ex?.ToString() ?? "(no exception)").Append("\r\n");
                for (var inner = ex?.InnerException; inner != null; inner = inner.InnerException)
                    sb.Append("--- inner: ").Append(inner.GetType().FullName).Append(": ").Append(inner.Message).Append("\r\n");
                sb.Append("\r\n");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                if (File.Exists(path) && new FileInfo(path).Length > 512 << 10)
                {
                    string old = File.ReadAllText(path);
                    File.WriteAllText(path, old.Substring(old.Length / 2));
                }
                File.AppendAllText(path, sb.ToString());
                try { Updater.Log("launcher error (" + thread + "): " + LogReport.FirstLine(ex?.Message) + ", see launcher_crash.log"); } catch { }
            }
            catch { }
        }
    }
}
