// SPDX-License-Identifier: GPL-3.0-or-later
// (22/11) The home column's OpenSmash card, the mirror of OpenSmash's own "SSBU Arena - Competitive" card: "OPENSMASH ULTIMATE · CASUAL",
// the player's mastery points (GMP: every fighter, OpenSmash's lobbies and Arena's matches together, public.my_opensmash_mastery) and,
// when the database has it, the position (my_opensmash_mastery_rank); the action "Play OpenSmash" when its launcher is on this PC
// (OpenSmash.Find, the OpenSmash tab's logic), else "Get OpenSmash" (its install page). The Discord community stays one click away:
// a small Discord button beside the card. SSBU_OPENSMASH_CARD=0: the community card alone, as before.
using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;

namespace Arena
{
    public partial class MainWindow
    {
        static bool OsCardOn => !(Environment.GetEnvironmentVariable("SSBU_OPENSMASH_CARD") is string v && (v.Trim() == "0" || v.Trim().Equals("off", StringComparison.OrdinalIgnoreCase)));
        int osCardSeq;
        DateTime osCardAt = DateTime.MinValue;

        void InitOsCard()
        {
            bool on = OsCardOn;
            OsCard.Visibility = CommunityMini.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            CommunityCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            if (on) LoadOsCard(true);
        }

        /// <summary>the card again: whether OpenSmash is installed, and the points (force: even when read a moment ago)</summary>
        void LoadOsCard(bool force)
        {
            if (!OsCardOn) return;
            OsCardAction.Text = OpenSmash.Find(out _) != null ? "Play OpenSmash" : "Get OpenSmash";
            if (!Account.SignedIn) { osCardSeq++; OsCardPoints.Text = "Mastery points"; OsCardSub.Text = "   sign in to see yours"; return; }
            if (!force && DateTime.UtcNow - osCardAt < TimeSpan.FromMinutes(2)) return;
            osCardAt = DateTime.UtcNow;
            int seq = ++osCardSeq;
            Task.Run(() => Mastery.ReadSummary()).ContinueWith(t =>
            {
                if (seq != osCardSeq) return;
                if (t.Status != TaskStatus.RanToCompletion || t.Result == null)
                {
                    if (t.Exception != null) Updater.Log("mastery: the home card not read (" + t.Exception.GetBaseException().Message + ")");
                    OsCardPoints.Text = "Mastery points"; OsCardSub.Text = "   can't be read now";
                    return;
                }
                var s = t.Result;
                OsCardPoints.Text = s.Total.ToString("N0", CultureInfo.InvariantCulture) + " GMP";
                string rank = s.Pos.HasValue && s.Pos.Value > 0 ? "#" + s.Pos.Value.ToString("N0", CultureInfo.InvariantCulture) + (s.Count.HasValue && s.Count.Value > 0 ? " of " + s.Count.Value.ToString("N0", CultureInfo.InvariantCulture) : "") : null;
                OsCardSub.Text = rank != null ? "   " + rank : "";
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        void OsCard_Click(object sender, RoutedEventArgs e) { OpenSmash.Open(); }
    }
}
