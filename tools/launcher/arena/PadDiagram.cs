// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.33) the controllers page's drawn controller: a plain vector outline of the connected kind of pad (Xbox-style, PlayStation-style,
// Switch Pro-style, GameCube-style, any other pad), no artwork of any maker. Every button lights up with what the game gets (the live test,
// MainWindow.PadTest.cs, through the button map of the page); the sticks show the real stick (gray), what the game gets (blue), the dead zone
// (dashed, yellow) and the outer zone (the ring where a full tilt starts); the analog triggers their level against the trigger threshold.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Arena
{
    public sealed class PadDiagram : FrameworkElement
    {
        // what the test says (MainWindow.PadTest.cs): the game's buttons (bits as the game's Pro Controller: A B X Y LS RS L R ZL ZR + - left up right down),
        // the sticks as the game gets them and before the dead zone (-32767..32767), the analog triggers (0..32767, -1 none)
        public ulong Buttons;
        public int LX, LY, RX, RY, RawLX, RawLY, RawRX, RawRY, TrigL = -1, TrigR = -1;
        public double DeadZone = .1, OuterZone = .98, TriggerThreshold = .33;
        /// <summary>0 Xbox, 1 PlayStation, 2 Switch, 3 GameCube, 4 other pad, 5 joystick without a mapping</summary>
        public int Kind = 4;
        /// <summary>which controller button presses each game button (Core.Buttons: "A" -> "east" ...); GameCube pads keep their own</summary>
        public Dictionary<string, string> Map = new Dictionary<string, string>();

        const double DW = 600, DH = 330;                  // the drawing's own units; scaled to the width the page gives
        protected override Size MeasureOverride(Size a)
        {
            double w = double.IsInfinity(a.Width) ? DW : Math.Min(a.Width, 680);
            return new Size(w, w * DH / DW);
        }

        static readonly Typeface Face = new Typeface(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Barlow Condensed"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        static Color C(string h) => (Color)ColorConverter.ConvertFromString(h);
        static SolidColorBrush B(Color c, double a = 1) { var b = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)); b.Freeze(); return b; }
        static Pen P(Brush b, double w, double[] dash = null) { var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; if (dash != null) p.DashStyle = new DashStyle(dash, 0); p.Freeze(); return p; }
        static readonly Color AccentC = C("#65C7FF"), WarnC = C("#FFC857");
        static readonly Brush BodyFill = B(C("#141E2B")), BodyEdge = B(C("#2C3D52")), Key = B(C("#0B121B")), KeyEdge = B(C("#34475E")), Lit = B(AccentC), LitGlow = B(AccentC, .25),
                              TextB = B(C("#EEF3F7")), Muted = B(C("#91A0B2")), Dim = B(C("#5D6B7C")), Warn = B(WarnC), BgDeep = B(C("#090D14"));
        static readonly Pen EdgeP = P(BodyEdge, 2), KeyP = P(KeyEdge, 1.4), LitP = P(Lit, 2), ZoneP = P(Warn, 1.2, new double[] { 2.5, 2.5 }), OuterP = P(B(C("#91A0B2"), .55), 1.1), ThrP = P(Warn, 2);

        bool On(int bit) => (Buttons >> bit & 1) != 0;
        static readonly string[] Game = { "A", "B", "X", "Y", "STICK_L", "STICK_R", "L", "R", "ZL", "ZR", "PLUS", "MINUS" };
        /// <summary>a physical control is lit when a game button it presses is on (its source in the map; GameCube: its own fixed buttons)</summary>
        bool SrcOn(string src)
        {
            for (int b = 0; b < Game.Length; b++)
                if (On(b) && Map.TryGetValue(Game[b], out var s) && s == src) return true;
            return false;
        }
        /// <summary>the game button a physical control presses, for the small tag beside it ("" none)</summary>
        string GameOf(string src)
        {
            foreach (var kv in Map) if (kv.Value == src) return kv.Key == "PLUS" ? "+" : kv.Key == "MINUS" ? "-" : kv.Key == "STICK_L" ? "LS" : kv.Key == "STICK_R" ? "RS" : kv.Key;
            return "";
        }

        double k, ox, oy;
        const double TextScale = 1.3;                       // (the labels readable when the drawing is small)
        Point Pt(double x, double y) => new Point(ox + x * k, oy + y * k);
        FormattedText T(string s, double size, Brush b) => new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size * k * TextScale, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        void Label(DrawingContext dc, string s, double x, double y, double size, Brush b) { var t = T(s, size, b); dc.DrawText(t, new Point(ox + x * k - t.Width / 2, oy + y * k - t.Height / 2)); }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            k = Math.Min(W / DW, H / DH); ox = (W - DW * k) / 2; oy = (H - DH * k) / 2;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            bool gc = Kind == 3;
            Body(dc, gc);
            // layout by kind: (x, y) of the left stick, the d-pad, the face buttons, the right stick
            Point ls, dp, face, rs;
            if (Kind == 1) { dp = new Point(150, 118); ls = new Point(222, 196); rs = new Point(378, 196); face = new Point(450, 118); }
            else if (gc) { ls = new Point(150, 120); dp = new Point(222, 206); face = new Point(448, 122); rs = new Point(380, 206); }
            else { ls = new Point(150, 118); dp = new Point(222, 196); rs = new Point(378, 196); face = new Point(450, 118); }
            Triggers(dc, gc);
            Stick(dc, ls, LX, LY, RawLX, RawLY, gc ? On(4) : SrcOn("leftstick"), gc ? "" : GameOf("leftstick"), "L STICK");
            Stick(dc, rs, RX, RY, RawRX, RawRY, gc ? On(5) : SrcOn("rightstick"), gc ? "" : GameOf("rightstick"), gc ? "C-STICK" : "R STICK", gc);
            DPad(dc, dp, gc ? .8 : 1);
            if (gc) GcFace(dc, face); else Face4(dc, face);
            // the small middle buttons: back / select / minus, start / options / plus (GameCube: START alone)
            if (gc) Small(dc, new Point(300, 128), On(10), "START", "+");
            else
            {
                string back = Kind == 0 ? "VIEW" : Kind == 1 ? "SHARE" : Kind == 2 ? "−" : "BACK", start = Kind == 0 ? "MENU" : Kind == 1 ? "OPTIONS" : Kind == 2 ? "+" : "START";
                Small(dc, new Point(262, 112), SrcOn("back"), back, GameOf("back"));
                Small(dc, new Point(338, 112), SrcOn("start"), start, GameOf("start"));
            }
        }

        void Body(DrawingContext dc, bool gc)
        {
            // one outline: a wide top, two grips, the bridge between them (a plain gamepad shape, no maker's); the GameCube-style a little rounder
            double gy = gc ? 236 : 244, gx = gc ? 18 : 0;
            var g = Geometry.Parse(string.Format(CultureInfo.InvariantCulture,
                "M 150,62 L 450,62 C 505,62 532,82 548,122 L {0},{1} C {2},292 {3},318 {4},302 C 482,288 464,{5} 442,{6} L 158,{6} C 136,{5} 118,288 {7},302 C {8},318 {9},292 {10},{1} L 52,122 C 68,82 95,62 150,62 Z",
                576 - gx, gy, 590 - gx, 552 - gx, 512 - gx, gy + 18, gy, 88 + gx, 48 + gx, 10 + gx, 24 + gx)).Clone();
            var t = new TransformGroup(); t.Children.Add(new ScaleTransform(k, k)); t.Children.Add(new TranslateTransform(ox, oy));
            g.Transform = t;
            dc.DrawGeometry(BodyFill, EdgeP, g);
        }

        void Triggers(DrawingContext dc, bool gc)
        {
            // bumpers (L / R; GameCube: Z on the right) and the triggers above them: a bar filled to the analog level, the threshold marked
            for (int side = 0; side < 2; side++)
            {
                double x0 = side == 0 ? 92 : 368, x1 = x0 + 140;
                bool bump = gc ? (side == 1 && On(9)) : SrcOn(side == 0 ? "leftshoulder" : "rightshoulder");
                string bl = gc ? (side == 1 ? "Z" : "") : Kind == 1 ? (side == 0 ? "L1" : "R1") : Kind == 0 ? (side == 0 ? "LB" : "RB") : (side == 0 ? "L" : "R");
                if (!gc || side == 1)
                {
                    var r = new Rect(Pt(x0 + 14, 42), Pt(x1 - 14, 58));
                    dc.DrawRoundedRectangle(bump ? Lit : Key, bump ? LitP : KeyP, r, 7 * k, 7 * k);
                    Label(dc, bl + Tag(gc ? (side == 1 ? "ZR" : "") : GameOf(side == 0 ? "leftshoulder" : "rightshoulder"), bl), (x0 + x1) / 2, 50, 11, bump ? BgDeep : Muted);
                }
                int level = side == 0 ? TrigL : TrigR;
                bool pressed = gc ? On(side == 0 ? 6 : 7) : SrcOn(side == 0 ? "lefttrigger" : "righttrigger");
                string tl = gc ? (side == 0 ? "L" : "R") : Kind == 1 ? (side == 0 ? "L2" : "R2") : Kind == 0 ? (side == 0 ? "LT" : "RT") : (side == 0 ? "ZL" : "ZR");
                var bar = new Rect(Pt(x0, 8), Pt(x1, 32));
                dc.DrawRoundedRectangle(Key, pressed ? LitP : KeyP, bar, 6 * k, 6 * k);
                double f = level < 0 ? (pressed ? 1 : 0) : Math.Max(0, Math.Min(1, level / 32767.0));
                if (f > 0) dc.DrawRoundedRectangle(pressed ? Lit : LitGlow, null, new Rect(bar.X, bar.Y, bar.Width * f, bar.Height), 6 * k, 6 * k);
                if (level >= 0)
                {
                    double tx = bar.X + bar.Width * TriggerThreshold;
                    dc.DrawLine(ThrP, new Point(tx, bar.Y - 3 * k), new Point(tx, bar.Bottom + 3 * k));
                }
                Label(dc, tl + Tag(gc ? (side == 0 ? "L" : "R") : GameOf(side == 0 ? "lefttrigger" : "righttrigger"), tl) + (level >= 0 ? "  " + (int)Math.Round(level / 327.67) + " %" : ""), (x0 + x1) / 2, 20, 12, pressed && f > .55 ? BgDeep : TextB);
            }
        }

        /// <summary>" → A": the game button, when its name is not already the label</summary>
        static string Tag(string game, string label) => string.IsNullOrEmpty(game) || game == label ? "" : " → " + game;

        void Stick(DrawingContext dc, Point c, int x, int y, int rx, int ry, bool pressed, string game, string name, bool small = false)
        {
            double R = (small ? 30 : 34) * k;
            Point p = Pt(c.X, c.Y);
            dc.DrawEllipse(Key, pressed ? LitP : KeyP, p, R + 6 * k, R + 6 * k);
            dc.DrawEllipse(null, OuterP, p, R * OuterZone, R * OuterZone);           // the outer zone: from there a full tilt
            if (DeadZone > 0) dc.DrawEllipse(B(WarnC, .08), ZoneP, p, Math.Max(1.5, R * DeadZone), Math.Max(1.5, R * DeadZone));
            Point raw = new Point(p.X + rx / 32767.0 * R, p.Y - ry / 32767.0 * R), got = new Point(p.X + x / 32767.0 * R, p.Y - y / 32767.0 * R);
            dc.DrawEllipse(Muted, null, raw, 4.2 * k, 4.2 * k);
            bool moved = x != 0 || y != 0;
            if (moved) dc.DrawLine(P(LitGlow, 3 * k), p, got);
            dc.DrawEllipse(moved ? Lit : Dim, null, got, 6 * k, 6 * k);
            Label(dc, name + Tag(game, ""), c.X, c.Y + (small ? 50 : 54), 11, pressed ? Lit : Muted);
        }

        void DPad(DrawingContext dc, Point c, double s)
        {
            double a = 15 * s, w = 13 * s;
            var dirs = new[] { (12, -1.0, 0.0), (13, 0.0, -1.0), (14, 1.0, 0.0), (15, 0.0, 1.0) };
            dc.DrawRoundedRectangle(Key, KeyP, new Rect(Pt(c.X - w / 2, c.Y - a - w / 2), Pt(c.X + w / 2, c.Y + a + w / 2)), 3 * k, 3 * k);
            dc.DrawRoundedRectangle(Key, KeyP, new Rect(Pt(c.X - a - w / 2, c.Y - w / 2), Pt(c.X + a + w / 2, c.Y + w / 2)), 3 * k, 3 * k);
            foreach (var d in dirs)
            {
                var r = new Rect(Pt(c.X + d.Item2 * a - w / 2, c.Y + d.Item3 * a - w / 2), Pt(c.X + d.Item2 * a + w / 2, c.Y + d.Item3 * a + w / 2));
                if (On(d.Item1)) dc.DrawRoundedRectangle(Lit, null, r, 3 * k, 3 * k);
                // a small arrow pointing out
                double ax = c.X + d.Item2 * a, ay = c.Y + d.Item3 * a, h = 4 * s;
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(Pt(ax + d.Item2 * h, ay + d.Item3 * h), true, true);
                    g.LineTo(Pt(ax - d.Item2 * h * .6 + d.Item3 * h, ay - d.Item3 * h * .6 + d.Item2 * h), true, false);
                    g.LineTo(Pt(ax - d.Item2 * h * .6 - d.Item3 * h, ay - d.Item3 * h * .6 - d.Item2 * h), true, false);
                }
                tri.Freeze();
                dc.DrawGeometry(On(d.Item1) ? BgDeep : Dim, null, tri);
            }
        }

        void Face4(DrawingContext dc, Point c)
        {
            // the four face buttons by position: south, east, west, north; the label of the kind of pad, and the game button they press
            var pos = new[] { ("south", 0.0, 1.0), ("east", 1.0, 0.0), ("west", -1.0, 0.0), ("north", 0.0, -1.0) };
            string[] lab = Kind == 0 ? new[] { "A", "B", "X", "Y" } : Kind == 1 ? new[] { "✕", "○", "□", "△" } : Kind == 2 ? new[] { "B", "A", "Y", "X" } : new[] { "", "", "", "" };
            double d = 27, r = 14;
            for (int i = 0; i < 4; i++)
            {
                var p = new Point(c.X + pos[i].Item2 * d, c.Y + pos[i].Item3 * d);
                bool on = SrcOn(pos[i].Item1);
                if (on) dc.DrawEllipse(LitGlow, null, Pt(p.X, p.Y), (r + 5) * k, (r + 5) * k);
                dc.DrawEllipse(on ? Lit : Key, on ? LitP : KeyP, Pt(p.X, p.Y), r * k, r * k);
                string g = GameOf(pos[i].Item1);
                Label(dc, lab[i] != "" ? lab[i] : g, p.X, p.Y, 13, on ? BgDeep : TextB);
                if (lab[i] != "" && g != "" && g != lab[i])      // the game's button beside it, when the label says another
                    Label(dc, g, p.X + pos[i].Item2 * 22 + (pos[i].Item2 == 0 ? 17 : 0), p.Y + pos[i].Item3 * 21 + (pos[i].Item3 == 0 ? -15 : 0), 9.5, on ? Lit : Muted);
            }
        }

        void GcFace(DrawingContext dc, Point c)
        {
            // GameCube-style: a big A, B below left, X right, Y above (fixed buttons: A, B, X, Y of the game)
            void Btn(double x, double y, double rx, double ry, int bit, string s)
            {
                bool on = On(bit);
                if (on) dc.DrawEllipse(LitGlow, null, Pt(x, y), (rx + 5) * k, (ry + 5) * k);
                dc.DrawEllipse(on ? Lit : Key, on ? LitP : KeyP, Pt(x, y), rx * k, ry * k);
                Label(dc, s, x, y, rx > 18 ? 16 : 12, on ? BgDeep : TextB);
            }
            Btn(c.X, c.Y, 22, 22, 0, "A");
            Btn(c.X - 34, c.Y + 24, 12, 12, 1, "B");
            Btn(c.X + 38, c.Y - 8, 10, 17, 2, "X");
            Btn(c.X - 6, c.Y - 36, 17, 10, 3, "Y");
        }

        void Small(DrawingContext dc, Point c, bool on, string label, string game)
        {
            var r = new Rect(Pt(c.X - 22, c.Y - 9), Pt(c.X + 22, c.Y + 9));
            dc.DrawRoundedRectangle(on ? Lit : Key, on ? LitP : KeyP, r, 9 * k, 9 * k);
            Label(dc, label, c.X, c.Y, 8.5, on ? BgDeep : Muted);
            if (game != "" && game != label) Label(dc, game, c.X, c.Y + 18, 9.5, on ? Lit : Muted);
        }
    }
}
