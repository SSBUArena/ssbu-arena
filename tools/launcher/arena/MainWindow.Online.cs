// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.48) Players online now, small in the header: the lobby server writes the count to public.lobby_status every 5 minutes
// (arena-server tools/net/lobby_status.py); read at start and every 5 minutes. Hidden while the count is older than
// 15 minutes (the server is not reporting), cannot be read, or is 0 (since 0.2.49). (0.2.51) One row per game and lobby
// (product, region): the count shown is the one of the lobby PLAY uses (the chosen region, or Auto's pick), with its name.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace Arena
{
    public partial class MainWindow
    {
        DispatcherTimer onlineTimer;

        // ---- (server region) Settings > Online > SERVER REGION: Auto (the closest lobby by measured ping), or one region (Regions.cs)
        /// <summary>the list and the texts under it, from Regions (no event: the caller's "loading" guard is set here)</summary>
        void FillRegions()
        {
            bool was = loading; loading = true;
            try
            {
                string byHand = Regions.Override;
                ServerBox.Items.Clear();
                if (byHand != null) ServerBox.Items.Add(byHand);
                else
                {
                    ServerBox.Items.Add(Regions.Choice < 0 ? Regions.Text : "Auto (closest server)");
                    foreach (var r in Regions.Table) ServerBox.Items.Add(r.Name);
                }
                bool env = Environment.GetEnvironmentVariable("SSBU_ONLINE_REGION") is string v && v.Trim() != "";
                ServerBox.SelectedIndex = byHand != null ? 0 : Regions.Choice + 1;
                ServerBox.IsEnabled = byHand == null && !env;
                string pings = Regions.MsText();
                RegionHelp.Text = (byHand != null ? "This server is set by hand (online_server / SSBU_ONLINE_SERVER): no region is chosen. "
                                   : "Auto measures the ping to each server and uses the closest. " + (pings != "" ? "Now: " + pings + ". " : "")
                                     + (Regions.Ms != null && Regions.Ms.Any(m => m < 0) ? "Auto never uses a server that gives no answer. " : ""))
                                  + "Players on different servers do not see each other: rooms and matchmaking are per server. To play with a friend, both pick the same region.";
            }
            finally { loading = was; }
        }

        /// <summary>Auto: the lobbies measured in the background (at the start, and when Auto is chosen); the pick is kept for the
        /// next start (graphics.ini online_region_last) and used by the next PLAY</summary>
        void MeasureRegions()
        {
            Regions.MeasureAsync(changed =>
            {
                if (changed) Core.SaveRegionLast();
                FillRegions();
                if (changed) ReadOnlineCount();      // (0.2.51) the count is the picked lobby's
            });
        }

        void RegionChanged()
        {
            FillRegions();
            MeasureRegions();
            ReadOnlineCount();      // (0.2.51) the count is the chosen lobby's
        }

        void StartOnlineCount()
        {
            ReadOnlineCount();
            onlineTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            onlineTimer.Tick += (s, e) => ReadOnlineCount();
            onlineTimer.Start();
        }

        /// <summary>(0.2.51) The count of one lobby. The lobbies write one row each to public.lobby_status (product, region, players,
        /// updated_at; row 1 is also the only row older servers wrote). rows = what was read; region = the lobby's id ("eu").
        /// The row of this game ("arena") and region; none: row 1 for the first region only (the old row). -1 = no count
        /// to show (no row, or older than 15 minutes: that lobby is not reporting).</summary>
        public static int OnlineCountOf(List<Dictionary<string, object>> rows, string region, DateTime nowUtc)
        {
            if (rows == null) return -1;
            Dictionary<string, object> pick = null, old = null;
            foreach (var r in rows)
            {
                object v;
                string product = r.TryGetValue("product", out v) ? v as string : null, reg = r.TryGetValue("region", out v) ? v as string : null;
                if (product == "arena" && string.Equals(reg, region, StringComparison.OrdinalIgnoreCase)) pick = r;
                if (r.TryGetValue("id", out v) && v != null && Convert.ToInt32(v, CultureInfo.InvariantCulture) == 1 && (product == null || product == "arena")) old = r;
            }
            if (pick == null && string.Equals(region, Regions.Table[0].Id, StringComparison.OrdinalIgnoreCase)) pick = old;
            if (pick == null || !(pick.TryGetValue("updated_at", out object at) && at is string) || !pick.TryGetValue("players", out object n) || n == null) return -1;
            var when = DateTime.Parse((string)at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            if (nowUtc - when > TimeSpan.FromMinutes(15)) return -1;
            return Convert.ToInt32(n, CultureInfo.InvariantCulture);
        }

        /// <summary>(0.2.51) the count of the lobby PLAY would use now (the chosen region, or Auto's pick): "12 online - Europe".
        /// A server set by hand that is not one of ours: no count.</summary>
        void ReadOnlineCount()
        {
            string byHand = Regions.Override;
            int at = byHand != null ? Regions.ByHost(byHand) : Regions.Pick;
            if (at < 0) { OnlineCountBox.Visibility = Visibility.Collapsed; return; }
            string id = Regions.Table[at].Id, name = Regions.Table[at].Name;
            Task.Run(() =>
            {
                List<Dictionary<string, object>> rows;
                try { rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(Account.Anon("/rest/v1/lobby_status?select=id,players,updated_at,product,region")); }
                catch (Exception) { rows = new JavaScriptSerializer().Deserialize<List<Dictionary<string, object>>>(Account.Anon("/rest/v1/lobby_status?id=eq.1&select=id,players,updated_at")); }      // (a server without the new columns)
                return OnlineCountOf(rows, id, DateTime.UtcNow);
            }).ContinueWith(t =>
            {
                if (t.IsFaulted) Updater.Log("online count not read: " + LogReport.FirstLine(t.Exception.GetBaseException().Message));      // (fix/install-launcher-errors) observed: no internet is no crash
                if (at != (Regions.Override != null ? Regions.ByHost(Regions.Override) : Regions.Pick)) return;      // the region changed meanwhile: its own read is on the way
                int n = t.Status == TaskStatus.RanToCompletion ? t.Result : -1;
                OnlineCountBox.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;      // (0.2.49) nobody online: no count
                if (n > 0) OnlineCountText.Text = n.ToString(CultureInfo.InvariantCulture) + " online \u00b7 " + name;
                OnlineCountBox.ToolTip = "Players connected to the " + name + " server (the one PLAY uses now), updated every 5 minutes";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
