// SPDX-License-Identifier: GPL-3.0-or-later
// SSBU Arena launcher: the schematic replay view (ReplayState.cs): the stage drawn from the collision lines the game
// reported, the blast zones, the fighters as their hurt volumes in the player's colour with name and a facing mark, the
// hitboxes as wireframe spheres (the website's HitSphere style), the damage and stocks; and the timeline under it.
// Everything is drawn in OnRender from frozen pens / brushes, at the display's rate while playing.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Arena
{
    public static class RvPalette
    {
        public static readonly Color[] Player = { Color.FromRgb(0xFF, 0x5F, 0x6B), Color.FromRgb(0x4F, 0xA8, 0xFF), Color.FromRgb(0xFF, 0xC8, 0x57), Color.FromRgb(0x3D, 0xDC, 0x97) };
        public static readonly Color Navy = Color.FromRgb(0x09, 0x0E, 0x15), Surface = Color.FromRgb(0x0D, 0x15, 0x1F), Line = Color.FromRgb(0x23, 0x34, 0x48), Accent = Color.FromRgb(0x65, 0xC7, 0xFF);
        public static SolidColorBrush B(Color c, double a = 1) { var b = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)); b.Freeze(); return b; }
        public static Pen P(Color c, double a, double w, DashStyle d = null) { var p = new Pen(B(c, a), w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; if (d != null) p.DashStyle = d; p.Freeze(); return p; }
        public static Color Hex(string h) => (Color)ColorConverter.ConvertFromString(h);
    }

    public sealed class ReplayView : FrameworkElement
    {
        ReplayState r;
        public ReplayState Replay { get => r; set { r = value; Prepare(); InvalidateVisual(); } }
        double frame;
        public double Frame { get => frame; set { frame = Math.Max(0, Math.Min(value, (r?.FrameList.Count ?? 1) - 1)); InvalidateVisual(); } }
        public bool ShowHit = true, ShowHurt = true, Follow = true;
        public ReplayScene Scene;                           // (0.2.21) the game's models, under this

        static readonly Typeface Ui = new Typeface(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Barlow"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static readonly Typeface UiBold = new Typeface(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Barlow Condensed"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        // pens and brushes
        static readonly Brush BgB = RvPalette.B(RvPalette.Navy);
        static readonly Pen GridP = RvPalette.P(RvPalette.Line, .35, 1);
        static readonly Pen BlastP = RvPalette.P(RvPalette.Hex("#FF6B6B"), .45, 1.2, new DashStyle(new double[] { 6, 5 }, 0));
        static readonly Brush BlastOut = RvPalette.B(RvPalette.Hex("#FF6B6B"), .035);
        static readonly Pen StageEdge = RvPalette.P(RvPalette.Hex("#294359"), 1, 1.2);
        static readonly Pen StageRail = RvPalette.P(RvPalette.Hex("#83DBFF"), .95, 3);
        static readonly Pen StageDetail = RvPalette.P(RvPalette.Hex("#4687B5"), .45, 1);
        static readonly Pen PlatP = RvPalette.P(RvPalette.Hex("#83DBFF"), .9, 3);
        static readonly Pen PlatUnder = RvPalette.P(RvPalette.Hex("#356487"), .8, 1.2);
        static readonly Brush PlatFill = RvPalette.B(RvPalette.Hex("#102235"), .9);
        static readonly Pen HurtP = RvPalette.P(RvPalette.Hex("#B6D4E8"), .5, 1.3);
        static readonly Pen HurtInv = RvPalette.P(RvPalette.Hex("#3DDC97"), .8, 1.4);
        static readonly Pen HurtXlu = RvPalette.P(RvPalette.Hex("#8899AA"), .6, 1.2, new DashStyle(new double[] { 3, 3 }, 0));
        static readonly Brush HurtFill = RvPalette.B(RvPalette.Hex("#9FC6DD"), .05);
        static readonly Brush TextB = RvPalette.B(RvPalette.Hex("#EEF3F7")), MutedB = RvPalette.B(RvPalette.Hex("#91A0B2")), DimB = RvPalette.B(RvPalette.Hex("#5D6B7C"));
        Brush[] playerB, playerFill, hitFill; Pen[] playerP, hitP, hitInner, hurtP;
        static readonly Color Hot = RvPalette.Hex("#FF8A1F"), HotPale = RvPalette.Hex("#FFD27A");

        Geometry stageBody; RvLine[] floorLines;
        float[] blast;
        Rect worldAll;

        public ReplayView() { ClipToBounds = true; Focusable = true; SnapsToDevicePixels = false; }
        double zoom = 1; public int zoomOn = -1;
        public double Zoom { get => zoom; set { zoom = Math.Max(1, Math.Min(8, value)); InvalidateVisual(); } }
        protected override void OnMouseWheel(MouseWheelEventArgs e) { Zoom = zoom * (e.Delta > 0 ? 1.25 : 0.8); e.Handled = true; }
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            Focus();
            if (e.ClickCount == 2) { Zoom = 1; zoomOn = -1; return; }
            // a click on a fighter: the zoom follows that one
            if (r == null || r.FrameList.Count == 0) return;
            var fr = r.FrameList[Math.Max(0, Math.Min((int)Math.Round(frame), r.FrameList.Count - 1))];
            var p = e.GetPosition(this);
            for (int k = 0; k < fr.F.Length; k++) { var s0 = fr.F[k]; if (s0 == null) continue; var q = S(s0.X, s0.Y + 8); if (Math.Abs(q.X - p.X) < 40 && Math.Abs(q.Y - p.Y) < 60) { zoomOn = k; if (zoom < 1.5) Zoom = 2.5; InvalidateVisual(); return; } }
        }

        void Prepare()
        {
            playerB = RvPalette.Player.Select(c => (Brush)RvPalette.B(c)).ToArray();
            playerFill = RvPalette.Player.Select(c => (Brush)RvPalette.B(c, .13)).ToArray();
            hurtP = RvPalette.Player.Select(c => RvPalette.P(c, .55, 1.2)).ToArray();
            playerP = RvPalette.Player.Select(c => RvPalette.P(c, .95, 1.6)).ToArray();
            // hitboxes: one hot colour for every player (the website's HitSphere drawing), clearly apart from the bodies
            hitFill = RvPalette.Player.Select(c => (Brush)RvPalette.B(Hot, .22)).ToArray();
            hitP = RvPalette.Player.Select(c => RvPalette.P(Hot, 1, 2.6)).ToArray();
            hitInner = RvPalette.Player.Select(c => RvPalette.P(HotPale, .55, 1.3)).ToArray();
            stageBody = null; floorLines = new RvLine[0];
            if (r == null) return;
            blast = ReplayNames.Blast(r);
            worldAll = new Rect(new Point(blast[0], blast[3]), new Point(blast[1], blast[2]));
            // the main stage: the solid lines (not passable) chained end to end into outlines, each closed and filled
            var solid = r.Lines.Where(l => !l.Passable).ToList();
            floorLines = r.Lines.ToArray();
            var chains = Chains(solid);
            if (chains.Count > 0)
            {
                var g = new StreamGeometry { FillRule = FillRule.Nonzero };
                using (var c = g.Open())
                    foreach (var ch in chains.Where(x => x.Count >= 3)) { c.BeginFigure(ch[0], true, true); c.PolyLineTo(ch.Skip(1).ToList(), true, true); }
                g.Freeze();
                stageBody = g;
            }
        }

        /// <summary>the solid lines joined where their end points meet (the game's lines share their corner points)</summary>
        static List<List<Point>> Chains(List<RvLine> lines)
        {
            var segs = lines.Select(l => new[] { new Point(l.X1, l.Y1), new Point(l.X2, l.Y2) }).ToList();
            var used = new bool[segs.Count];
            var res = new List<List<Point>>();
            Func<Point, Point, bool> near = (a, b) => Math.Abs(a.X - b.X) < .05 && Math.Abs(a.Y - b.Y) < .05;
            for (int i = 0; i < segs.Count; i++)
            {
                if (used[i]) continue;
                used[i] = true;
                var ch = new List<Point> { segs[i][0], segs[i][1] };
                for (bool grew = true; grew;)
                {
                    grew = false;
                    for (int j = 0; j < segs.Count; j++)
                    {
                        if (used[j]) continue;
                        Point e = ch[ch.Count - 1], s0 = ch[0];
                        if (near(segs[j][0], e)) { ch.Add(segs[j][1]); used[j] = grew = true; }
                        else if (near(segs[j][1], e)) { ch.Add(segs[j][0]); used[j] = grew = true; }
                        else if (near(segs[j][1], s0)) { ch.Insert(0, segs[j][0]); used[j] = grew = true; }
                        else if (near(segs[j][0], s0)) { ch.Insert(0, segs[j][1]); used[j] = grew = true; }
                    }
                }
                res.Add(ch);
            }
            return res;
        }

        static Color Lighter(Color c) => Color.FromRgb((byte)(c.R + (255 - c.R) * .45), (byte)(c.G + (255 - c.G) * .45), (byte)(c.B + (255 - c.B) * .45));

        static List<Point> Hull(List<Point> p)
        {
            p = p.Distinct().OrderBy(a => a.X).ThenBy(a => a.Y).ToList();
            if (p.Count < 3) return p;
            Func<Point, Point, Point, double> cross = (o, a, b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
            var h = new List<Point>();
            foreach (var q in p) { while (h.Count >= 2 && cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
            int lo = h.Count + 1;
            for (int i = p.Count - 2; i >= 0; i--) { var q = p[i]; while (h.Count >= lo && cross(h[h.Count - 2], h[h.Count - 1], q) <= 0) h.RemoveAt(h.Count - 1); h.Add(q); }
            h.RemoveAt(h.Count - 1);
            return h;
        }

        // ------------------------------------------------------------------ camera
        Rect ViewRect(int fi)
        {
            if (!Follow) return worldAll;
            // the stage's floor and the fighters of a window of frames around this one (so the view moves smoothly and is
            // the same whichever way the timeline is moved)
            double x0 = double.MaxValue, x1 = double.MinValue, y0 = double.MaxValue, y1 = double.MinValue;
            foreach (var l in r.Lines) if (!l.Passable || l.Floor) { x0 = Math.Min(x0, Math.Min(l.X1, l.X2)); x1 = Math.Max(x1, Math.Max(l.X1, l.X2)); y0 = Math.Min(y0, Math.Min(l.Y1, l.Y2)); y1 = Math.Max(y1, Math.Max(l.Y1, l.Y2)); }
            if (x0 == double.MaxValue) { x0 = -80; x1 = 80; y0 = -20; y1 = 60; }
            y0 = Math.Max(y0, -30);
            double sx0 = 0, sx1 = 0, sy0 = 0, sy1 = 0, wsum = 0;
            for (int d = -45; d <= 45; d += 3)
            {
                int i = Math.Max(0, Math.Min(r.FrameList.Count - 1, fi + d));
                double a0 = x0, a1 = x1, b0 = y0, b1 = y1 + 40;
                foreach (var s in r.FrameList[i].F)
                {
                    if (s == null || !InBlast(s.X, s.Y)) continue;
                    a0 = Math.Min(a0, s.X - 25); a1 = Math.Max(a1, s.X + 25); b0 = Math.Min(b0, s.Y - 15); b1 = Math.Max(b1, s.Y + 40);
                }
                double w = 1.0 - Math.Abs(d) / 50.0;
                sx0 += a0 * w; sx1 += a1 * w; sy0 += b0 * w; sy1 += b1 * w; wsum += w;
            }
            var rc = new Rect(new Point(sx0 / wsum, sy0 / wsum), new Point(sx1 / wsum, sy1 / wsum));
            rc.Inflate(rc.Width * .08, rc.Height * .1);
            return rc;
        }
        bool InBlast(float x, float y) => x > blast[0] && x < blast[1] && y < blast[2] && y > blast[3];

        double sc, ox, oy;                                  // world -> screen
        Point S(double x, double y) => new Point(ox + x * sc, oy - y * sc);

        // SSBU_ARENA_RV_STATS=1 (tests): the time OnRender takes and the frames drawn per second, top left
        static readonly bool statsOn = Environment.GetEnvironmentVariable("SSBU_ARENA_RV_STATS") == "1";
        readonly System.Diagnostics.Stopwatch statClock = System.Diagnostics.Stopwatch.StartNew();
        double statMs, statMax, statFps; int statN; double statT0;
        public string StatsText => string.Format(CultureInfo.InvariantCulture, "render {0:0.00} ms (max {1:0.00}), {2:0} frames drawn/s", statMs, statMax, statFps);

        protected override void OnRender(DrawingContext dc)
        {
            double t0 = statClock.Elapsed.TotalMilliseconds;
            Render(dc);
            double t1 = statClock.Elapsed.TotalMilliseconds;
            if (!statsOn) return;
            statN++; statMs = statMs * .95 + (t1 - t0) * .05; statMax = Math.Max(statMax * .995, t1 - t0);
            if (t1 - statT0 >= 1000) { statFps = statN * 1000.0 / (t1 - statT0); statN = 0; statT0 = t1; }
            dc.DrawText(Text(StatsText, 12, MutedB, Ui), new Point(16, 30));
        }

        void Render(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            bool stageModel = Scene != null && Scene.StageShown;
            if (!stageModel && !(Scene != null && Scene.On && Enumerable.Range(0, 4).Any(Scene.FighterShown))) dc.DrawRectangle(BgB, null, new Rect(0, 0, W, H));
            else dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));      // (clicks)
            if (r == null || r.FrameList.Count == 0 || W < 10 || H < 10) return;
            int fi = (int)Math.Round(frame);
            var fr = r.FrameList[Math.Max(0, Math.Min(fi, r.FrameList.Count - 1))];
            double hudH = 120;
            var view = ViewRect(fi);
            if (zoom > 1.001)                               // mouse wheel: closer, centred on the fighters (double click: back)
            {
                double cx = view.Left + view.Width / 2, cy = view.Top + view.Height / 2;
                foreach (var f0 in fr.F) if (f0 != null && InBlast(f0.X, f0.Y)) { cx = f0.X; cy = f0.Y + 10; break; }
                if (zoomOn >= 0 && zoomOn < fr.F.Length && fr.F[zoomOn] != null) { cx = fr.F[zoomOn].X; cy = fr.F[zoomOn].Y + 10; }
                view = new Rect(cx - view.Width / zoom / 2, cy - view.Height / zoom / 2, view.Width / zoom, view.Height / zoom);
            }
            double aw = W, ah = H - hudH;
            sc = Math.Min(aw / view.Width, ah / view.Height);
            ox = aw / 2 - (view.Left + view.Width / 2) * sc; oy = ah / 2 + (view.Top + view.Height / 2) * sc;
            Scene?.Update(r, fr, W, H, sc, ox, oy);

            if (!stageModel)
            {
            // halo and grid
            var halo = new RadialGradientBrush(Color.FromArgb(28, 0x16, 0x89, 0xE8), Color.FromArgb(0, 0x16, 0x89, 0xE8)) { Center = new Point(.5, .55), GradientOrigin = new Point(.5, .55), RadiusX = .6, RadiusY = .6 };
            dc.DrawRectangle(halo, null, new Rect(0, 0, W, H));
            double step = 20 * sc; if (step < 14) step *= 5;
            var o0 = S(0, 0);
            for (double x = o0.X % step; x < W; x += step) dc.DrawLine(GridP, new Point(x, 0), new Point(x, ah));
            for (double y = o0.Y % step; y < ah; y += step) dc.DrawLine(GridP, new Point(0, y), new Point(W, y));

            }

            // blast zones
            var bz = new Rect(S(blast[0], blast[2]), S(blast[1], blast[3]));
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(-10, -10, W + 20, H + 20)), new RectangleGeometry(bz));
            dc.DrawGeometry(BlastOut, null, outside);
            dc.DrawRectangle(null, BlastP, bz);

            // the stage
            if (stageBody != null && !stageModel)
            {
                var tg = new MatrixTransform(sc, 0, 0, -sc, ox, oy);
                var g = stageBody.Clone(); g.Transform = tg;
                var fill = new LinearGradientBrush(RvPalette.Hex("#233C56"), RvPalette.Hex("#0B1B2A"), 90);
                dc.DrawGeometry(fill, StageEdge, g);
            }
            var plats = new List<float[]>();
            for (int i = 0; i < r.Lines.Count; i++)
            {
                var L = r.Lines[i]; var p = fr.Lines != null && i < fr.Lines.Length ? fr.Lines[i] : new[] { L.X1, L.Y1, L.X2, L.Y2 };
                if (fr.Number < L.FirstFrame || (i < 64 && (fr.LinesOn & (1UL << i)) == 0)) continue;      // not there (yet / now)
                if (stageModel && !(Scene.MovingAsLines && L.Passable)) continue;
                if (L.Passable) { plats.Add(new[] { Math.Min(p[0], p[2]), p[1], Math.Max(p[0], p[2]), p[3] }); continue; }
                Point a = S(p[0], p[1]), b = S(p[2], p[3]);
                if (L.Floor) dc.DrawLine(StageRail, a, b);
                else dc.DrawLine(StageDetail, a, b);
            }
            // platforms: the game keeps one as several touching segments; drawn as one (at this frame's positions)
            plats.Sort((u, v) => u[0].CompareTo(v[0]));
            for (int i = 0; i < plats.Count; i++)
            {
                var q = plats[i];
                while (i + 1 < plats.Count && Math.Abs(plats[i + 1][0] - q[2]) < .1 && Math.Abs(plats[i + 1][1] - q[3]) < .1) { q = new[] { q[0], q[1], plats[i + 1][2], plats[i + 1][3] }; i++; }
                Point a = S(q[0], q[1]), b = S(q[2], q[3]);
                var body = new Rect(new Point(a.X, Math.Min(a.Y, b.Y)), new Point(b.X, Math.Max(a.Y, b.Y) + 2.4 * sc));
                dc.DrawRoundedRectangle(PlatFill, PlatUnder, body, 2, 2);
                dc.DrawLine(PlatP, a, b);
            }

            // fighters: hurt volumes, the body mark, then hitboxes on top
            for (int k = 0; k < fr.F.Length && k < r.Fighters.Count; k++)
            {
                var s = fr.F[k]; if (s == null) continue;
                int pc = k % 4;
                if (ShowHurt) foreach (var u in s.Hurts) DrawCapsule(dc, u.X, u.Y, u.X2, u.Y2, u.R, u.Status == 1 ? HurtXlu : u.Status == 2 ? HurtInv : hurtP[pc], null);
                DrawFighter(dc, s, k, pc, true);
            }
            if (fr.Extra != null)
                foreach (var e in fr.Extra)
                {
                    if (ShowHurt) foreach (var u in e.Value.Hurts) DrawCapsule(dc, u.X, u.Y, u.X2, u.Y2, u.R, hurtP[e.Key % 4], null);
                    DrawFighter(dc, e.Value, e.Key, e.Key % 4, false);
                }
            if (ShowHit)
                for (int k = 0; k < fr.F.Length && k < r.Fighters.Count; k++)
                {
                    var s = fr.F[k]; if (s == null) continue;
                    foreach (var h in s.Hits) DrawHitSphere(dc, h, k % 4);
                }
            if (ShowHit && fr.Extra != null) foreach (var e in fr.Extra) foreach (var h in e.Value.Hits) DrawHitSphere(dc, h, e.Key % 4);

            DrawHud(dc, fr, W, H, hudH);
        }

        void DrawCapsule(DrawingContext dc, float x1, float y1, float x2, float y2, float rr, Pen pen, Brush fill)
        {
            double R = Math.Max(rr * sc, 1.5);
            Point a = S(x1, y1), b = S(x2, y2);
            double dx = b.X - a.X, dy = b.Y - a.Y, len = Math.Sqrt(dx * dx + dy * dy);
            if (len < .5) { dc.DrawEllipse(fill, pen, a, R, R); return; }
            double ang = Math.Atan2(dy, dx) * 180 / Math.PI;
            dc.PushTransform(new TranslateTransform(a.X, a.Y)); dc.PushTransform(new RotateTransform(ang));
            dc.DrawRoundedRectangle(fill, pen, new Rect(-R, -R, len + 2 * R, 2 * R), R, R);
            dc.Pop(); dc.Pop();
        }

        void DrawHitSphere(DrawingContext dc, RvHit h, int pc)
        {
            double R = Math.Max(h.R * sc, 2);
            Point c = S(h.X, h.Y);
            if (h.X2 != h.X || h.Y2 != h.Y) DrawCapsule(dc, h.X, h.Y, h.X2, h.Y2, h.R, hitP[pc], hitFill[pc]);
            dc.DrawEllipse(hitFill[pc], hitP[pc], c, R, R);
            dc.PushTransform(new TranslateTransform(c.X, c.Y));
            dc.PushTransform(new RotateTransform(20)); dc.DrawEllipse(null, hitInner[pc], new Point(0, 0), R * .36, R); dc.Pop();
            dc.PushTransform(new RotateTransform(-15)); dc.DrawEllipse(null, hitInner[pc], new Point(0, 0), R, R * .3); dc.Pop();
            dc.Pop();
            dc.DrawEllipse(hitP[pc].Brush, null, c, 2.2, 2.2);
            if (R > 16 && h.Damage > 0)
            {
                var t = Text(h.Damage.ToString("0.#", CultureInfo.InvariantCulture) + "%", 11, TextB, Ui);
                dc.DrawText(t, new Point(c.X - t.Width / 2, c.Y - R - t.Height));
            }
        }

        void DrawFighter(DrawingContext dc, RvFighterFrame s, int k, int pc, bool tagged)
        {
            Point feet = S(s.X, s.Y);
            bool model = Scene != null && Scene.FighterShown(k) && tagged;
            // body: the hurt volumes' extent (else a capsule of a typical size), filled in the player's colour
            double x0 = s.X - 4, x1 = s.X + 4, y0 = s.Y, y1 = s.Y + 14;
            foreach (var u in s.Hurts) { x0 = Math.Min(x0, Math.Min(u.X, u.X2) - u.R); x1 = Math.Max(x1, Math.Max(u.X, u.X2) + u.R); y0 = Math.Min(y0, Math.Min(u.Y, u.Y2) - u.R); y1 = Math.Max(y1, Math.Max(u.Y, u.Y2) + u.R); }
            if (model) { }
            else if (s.Hurts.Length > 0)
                foreach (var u in s.Hurts) DrawCapsule(dc, u.X, u.Y, u.X2, u.Y2, u.R, null, playerFill[pc]);
            else DrawCapsule(dc, s.X, s.Y + 4, s.X, s.Y + 10, 4, playerP[pc], playerFill[pc]);
            // ground shadow, feet point, facing mark
            if (s.Grounded && !model) dc.DrawEllipse(RvPalette.B(RvPalette.Player[pc], .22), null, feet, 7 * sc, 1.6 * sc);
            if (!model) dc.DrawEllipse(playerB[pc], null, feet, 3, 3);
            Point head = S(s.X, y1);
            double fx = feet.X, fy = S(0, (y0 + y1) / 2).Y, dir = s.Lr;
            var tri = new StreamGeometry();
            using (var c = tri.Open())
            {
                double ex = fx + dir * ((x1 - x0) / 2 * sc + 6), w = 7;
                c.BeginFigure(new Point(ex + dir * w, fy), true, true);
                c.LineTo(new Point(ex, fy - w * .8), true, false); c.LineTo(new Point(ex, fy + w * .8), true, false);
            }
            tri.Freeze();
            dc.DrawGeometry(playerB[pc], null, tri);
            // shield bubble
            if (s.Shield >= 0 && s.Shield < 50 && IsGuard(s))
            {
                double R = (s.Shield / 50.0 * 8 + 3) * sc * 1.2;
                dc.DrawEllipse(RvPalette.B(RvPalette.Player[pc], .22), playerP[pc], S(s.X, (y0 + y1) / 2), R, R);
            }
            if (!tagged) return;
            // name tag
            var f = r.Fighters[k];
            var nm = Text((f.Type == 1 ? "CPU" : "P" + (k + 1)), 13, playerB[pc], UiBold);
            var tag = new Point(head.X - nm.Width / 2, head.Y - nm.Height - 14);
            dc.DrawRoundedRectangle(RvPalette.B(RvPalette.Navy, .75), RvPalette.P(RvPalette.Player[pc], .7, 1), new Rect(tag.X - 6, tag.Y - 1, nm.Width + 12, nm.Height + 2), 4, 4);
            dc.DrawText(nm, tag);
            var tri2 = new StreamGeometry();
            using (var c = tri2.Open()) { c.BeginFigure(new Point(head.X - 5, tag.Y + nm.Height + 1), true, true); c.LineTo(new Point(head.X + 5, tag.Y + nm.Height + 1), true, false); c.LineTo(new Point(head.X, tag.Y + nm.Height + 7), true, false); }
            tri2.Freeze();
            dc.DrawGeometry(playerB[pc], null, tri2);
            // off screen: an arrow at the border
        }

        static bool IsGuard(RvFighterFrame s) { var m = ReplayNames.MotionText(s.Motion); return m == "guard" || m == "guard_on" || m == "guard_damage"; }

        void DrawHud(DrawingContext dc, RvFrame fr, double W, double H, double hudH)
        {
            int n = r.Fighters.Count;
            double cardW = Math.Min(260, (W - 40) / Math.Max(n, 1) - 16), total = n * (cardW + 16) - 16, x = (W - total) / 2, y = H - hudH + 14;
            dc.DrawRectangle(RvPalette.B(RvPalette.Navy, .92), null, new Rect(0, H - hudH, W, hudH));
            dc.DrawLine(RvPalette.P(RvPalette.Line, 1, 1), new Point(0, H - hudH), new Point(W, H - hudH));
            for (int k = 0; k < n; k++, x += cardW + 16)
            {
                var f = r.Fighters[k]; var s = fr.F[k]; int pc = k % 4;
                var rect = new Rect(x, y, cardW, hudH - 28);
                dc.DrawRoundedRectangle(RvPalette.B(RvPalette.Surface), RvPalette.P(RvPalette.Line, 1, 1), rect, 6, 6);
                dc.DrawRectangle(playerB[pc], null, new Rect(x, y + 8, 3, rect.Height - 16));
                dc.DrawText(Text(f.FighterName.ToUpperInvariant(), 13, MutedB, UiBold), new Point(x + 16, y + 9));
                dc.DrawText(Text(f.Name, 15, TextB, Ui), new Point(x + 16, y + 27));
                if (s != null)
                {
                    double d = Math.Floor(s.Damage * 10 + 1e-4) / 10;      // the game's own display: tenths, cut (10.08 shows 10.0)
                    var dcol = DamageColour(d);
                    var t = Text(d.ToString("0.0", CultureInfo.InvariantCulture), 40, new SolidColorBrush(dcol), UiBold);
                    var pct = Text("%", 18, new SolidColorBrush(dcol), UiBold);
                    double rx = x + cardW - 14 - pct.Width;
                    dc.DrawText(pct, new Point(rx, y + 30));
                    dc.DrawText(t, new Point(rx - t.Width - 1, y + 8));
                    for (int i = 0; i < Math.Min(s.Stocks, 8); i++) dc.DrawEllipse(playerB[pc], null, new Point(x + 22 + i * 16, y + rect.Height - 16), 5.5, 5.5);
                    if (s.Stocks == 0) dc.DrawText(Text("OUT", 13, DimB, UiBold), new Point(x + 16, y + rect.Height - 25));
                    var mv = ReplayNames.MoveName(s.Motion);
                    if (mv != null) { var m = Text(mv, 12, playerB[pc], Ui); dc.DrawText(m, new Point(x + cardW - 14 - m.Width, y + rect.Height - 22)); }
                }
            }
            // time and frame
            int sec = (int)(fr.Number / 60.0);
            var tt = Text(string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}.{2:00}   FRAME {3}", sec / 60, sec % 60, (int)(fr.Number % 60 * 100 / 60), fr.Number), 13, MutedB, UiBold);
            dc.DrawText(tt, new Point(16, 12));
            var st = Text(r.StageName.ToUpperInvariant(), 13, DimB, UiBold);
            dc.DrawText(st, new Point(W - st.Width - 16, 12));
        }

        static Color DamageColour(double d)
        {
            double t = Math.Min(1, d / 150.0);
            byte g = (byte)(243 - t * 170), b = (byte)(247 - t * 190);
            return Color.FromRgb(0xEE, g, b);
        }

        FormattedText Text(string s, double size, Brush b, Typeface tf)
        {
            return new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, tf, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }
    }

    /// <summary>the timeline: both players' damage over the match, the KO marks, the position; click or drag to seek</summary>
    public sealed class ReplayTimeline : FrameworkElement
    {
        public ReplayState Replay; public ReplayStats Stats; public double Frame;
        public event Action<double> Seek;
        Geometry[] curves;
        static readonly Brush Track = RvPalette.B(RvPalette.Surface), Played = RvPalette.B(RvPalette.Accent, .12);
        static readonly Pen Edge = RvPalette.P(RvPalette.Line, 1, 1), Head = RvPalette.P(RvPalette.Accent, 1, 2);

        public void Prepare()
        {
            curves = null; InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            dc.DrawRoundedRectangle(Track, Edge, new Rect(0, 0, W, H), 6, 6);
            if (Replay == null || Replay.FrameList.Count < 2) return;
            int N = Replay.FrameList.Count;
            double px = Frame / (N - 1) * W;
            dc.DrawRectangle(Played, null, new Rect(0, 0, px, H));
            if (curves == null || curves.Length != Replay.Fighters.Count)
            {
                curves = new Geometry[Replay.Fighters.Count];
                for (int k = 0; k < curves.Length; k++)
                {
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        bool started = false;
                        int stepN = Math.Max(1, N / 800);
                        for (int i = 0; i < N; i += stepN)
                        {
                            var s = Replay.FrameList[i].F[k]; if (s == null) continue;
                            var p = new Point(i / (double)(N - 1), 1 - Math.Min(s.Damage, 200) / 200.0);
                            if (!started) { c.BeginFigure(p, false, false); started = true; } else c.LineTo(p, true, false);
                        }
                    }
                    g.Freeze(); curves[k] = g;
                }
            }
            for (int k = 0; k < curves.Length; k++)
            {
                var g = curves[k].Clone(); g.Transform = new MatrixTransform(W, 0, 0, H - 12, 0, 6);
                dc.DrawGeometry(null, RvPalette.P(RvPalette.Player[k % 4], .8, 1.5), g);
            }
            if (Stats != null)
                foreach (var ko in Stats.Kos)
                {
                    int i = Replay.FrameList.FindIndex(f => f.Number >= ko.Frame); if (i < 0) continue;
                    double x = i / (double)(N - 1) * W;
                    var b = RvPalette.B(RvPalette.Player[ko.Fighter % 4]);
                    dc.DrawLine(RvPalette.P(RvPalette.Player[ko.Fighter % 4], .9, 1.5), new Point(x, 4), new Point(x, H - 4));
                    var tri = new StreamGeometry();
                    using (var c = tri.Open()) { c.BeginFigure(new Point(x - 5, 0), true, true); c.LineTo(new Point(x + 5, 0), true, false); c.LineTo(new Point(x, 7), true, false); }
                    tri.Freeze(); dc.DrawGeometry(b, null, tri);
                }
            dc.DrawLine(Head, new Point(px, 0), new Point(px, H));
            dc.DrawEllipse(RvPalette.B(RvPalette.Accent), null, new Point(px, H / 2), 5, 5);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e) { CaptureMouse(); Do(e.GetPosition(this)); e.Handled = true; }
        protected override void OnMouseMove(MouseEventArgs e) { if (IsMouseCaptured) Do(e.GetPosition(this)); }
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); }
        void Do(Point p)
        {
            if (Replay == null || Replay.FrameList.Count < 2) return;
            double f = Math.Max(0, Math.Min(1, p.X / ActualWidth)) * (Replay.FrameList.Count - 1);
            Seek?.Invoke(Math.Round(f));
        }
    }
}
