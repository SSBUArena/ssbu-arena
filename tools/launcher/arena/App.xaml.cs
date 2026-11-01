// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Data;

namespace Arena
{
    public partial class App : Application
    {
        /// <summary>(0.2.12) the update window first (UpdateWindow.cs); it opens the launcher</summary>
        void OnStartup(object sender, StartupEventArgs e)
        {
            if (!OnlyLauncher(e.Args)) { Shutdown(); return; }
            new UpdateWindow().Show();
        }

        static Mutex only;              // kept for the whole life of the process

        /// <summary>(0.2.26) one launcher at a time: a second one brings the first to the screen (it may be in the notification area) and
        /// ends. The new launcher of a self-update waits for the old one. A launcher opened on a replay or a page is a tool's: it just runs.</summary>
        static bool OnlyLauncher(string[] args)
        {
            if (args.Contains("--replay") || args.Contains("--page")) return true;
            only = new Mutex(false, @"Local\SSBUArena.Launcher");
            bool mine;
            try { mine = only.WaitOne(args.Contains("--updated") ? 20000 : 0); }
            catch (AbandonedMutexException) { mine = true; }
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\SSBUArena.Launcher.Show");
            if (!mine)
            {
                AllowSetForegroundWindow(-1);
                show.Set();
                return false;
            }
            var t = new Thread(() =>
            {
                while (show.WaitOne())
                    Current.Dispatcher.BeginInvoke(new Action(() =>
                    {
                        var main = Current.Windows.OfType<MainWindow>().FirstOrDefault();
                        if (main != null) main.FromTray();
                        else if (Current.MainWindow != null) Current.MainWindow.Activate();
                    }));
            }) { IsBackground = true };
            t.Start();
            return true;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool AllowSetForegroundWindow(int processId);
    }

    /// <summary>a length times the converter parameter (the hero art: a share of the card's width)</summary>
    public sealed class Scale : IValueConverter
    {
        public static readonly Scale Instance = new Scale();
        public object Convert(object v, Type t, object p, CultureInfo c)
        {
            return (v is double d ? d : 0) * double.Parse((string)p, CultureInfo.InvariantCulture);
        }
        public object ConvertBack(object v, Type t, object p, CultureInfo c) { throw new NotSupportedException(); }
    }
}
