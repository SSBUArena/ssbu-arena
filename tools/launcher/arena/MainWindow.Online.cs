// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.48) Players online now, small in the header: the lobby server writes the count to public.lobby_status every 5 minutes
// (arena-server tools/net/lobby_status.py); read at start and every 5 minutes. Hidden while the count is older than
// 15 minutes (the server is not reporting), cannot be read, or is 0 (since 0.2.49).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace Arena
{
    public partial class MainWindow
    {
        DispatcherTimer onlineTimer;

        void StartOnlineCount()
        {
            ReadOnlineCount();
            onlineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            onlineTimer.Tick += (s, e) => ReadOnlineCount();
            onlineTimer.Start();
        }

        void ReadOnlineCount()
        {
            Task.Run(() =>
            {
                var rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(Account.Anon("/rest/v1/lobby_status?id=eq.1&select=players,updated_at"));
                if (rows == null || rows.Count == 0) return -1;
                var at = DateTime.Parse((string)rows[0]["updated_at"], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                if (DateTime.UtcNow - at > TimeSpan.FromMinutes(15)) return -1;
                return Convert.ToInt32(rows[0]["players"], CultureInfo.InvariantCulture);
            }).ContinueWith(t =>
            {
                int n = t.Status == TaskStatus.RanToCompletion ? t.Result : -1;
                OnlineCountBox.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;      // (0.2.49) nobody online: no count
                if (n > 0) OnlineCountText.Text = n.ToString(CultureInfo.InvariantCulture) + " online";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
