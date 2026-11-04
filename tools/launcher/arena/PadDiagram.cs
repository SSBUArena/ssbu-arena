// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.33) the controllers page's drawn controller: a plain vector outline of the connected kind of pad (Xbox-style, PlayStation-style,
// Switch Pro-style, GameCube-style, any other pad), no artwork of any maker. Each control lights up with what the game gets (the live test,
// MainWindow.PadTest.cs) and is labelled with what it does in Smash: the page's button map says which game button it presses, Smash's
// default controls what that button does (the player's own controls in Smash's Options > Controls cannot be read here). The sticks show
// the real stick (gray), what the game gets (blue), the dead zone (dashed yellow) and the outer zone ring; the analog triggers their
// level and the trigger threshold. Empty: the outline only, dimmed (no controller).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Arena
{
    public sealed class PadDiagram : FrameworkElement
    {
        // what the test says: the game's buttons (bits as the game's Pro Controller: A B X Y LS RS L R ZL ZR + - left up right down), the
        // sticks as the game gets them and before the dead zone (-32767..32767), the analog triggers (0..32767, -1 none)
        public ulong Buttons;
        public int LX, LY, RX, RY, RawLX, RawLY, RawRX, RawRY, TrigL = -1, TrigR = -1;
        public double DeadZone = .1, OuterZone = .98, TriggerThreshold = .33;
        /// <summary>0 Xbox, 1 PlayStation, 2 Switch, 3 GameCube, 4 other pad, 5 joystick without a mapping</summary>
        public int Kind = 4;
        /// <summary>no controller: the outline only, dimmed</summary>
        public bool Empty;
        /// <summary>which controller button presses each game button (Core.Buttons: "A" -> "east" ...); GameCube pads keep their own</summary>
        public Dictionary<string, string> Map = new Dictionary<string, string>();

        /// <summary>Smash's default controls: what each game button does</summary>
        public static readonly Dictionary<string, string> Action = new Dictionary<string, string> {
            { "A", "Attack" }, { "B", "Special" }, { "X", "Jump" }, { "Y", "Jump" }, { "L", "Grab" }, { "R", "Grab" }, { "ZL", "Shield" }, { "ZR", "Shield" },
            { "PLUS", "Pause" }, { "MINUS", "" }, { "STICK_L", "" }, { "STICK_R", "" } };

        const double DW = 640, DH = 350;                  // the drawing's own units; scaled to the room the page gives (width and height)
        protected override Size MeasureOverride(Size a)
        {
            double w = double.IsInfinity(a.Width) ? DW : a.Width, h = double.IsInfinity(a.Height) ? w * DH / DW : a.Height;
            w = Math.Min(w, h * DW / DH);
            return new Size(w, w * DH / DW);
        }

        static readonly Typeface Face = new Typeface(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Barlow Condensed"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        static readonly Typeface Body = new Typeface(new FontFamily(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Barlow"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static Color C(string h) => (Color)ColorConverter.ConvertFromString(h);
        static SolidColorBrush B(Color c, double a = 1) { var b = new SolidColorBrush(Color.FromArgb((byte)(a * 255), c.R, c.G, c.B)); b.Freeze(); return b; }
        static Pen P(Brush b, double w, double[] dash = null) { var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }; if (dash != null) p.DashStyle = new DashStyle(dash, 0); p.Freeze(); return p; }
        static readonly Color AccentC = C("#65C7FF"), WarnC = C("#FFC857");
        static readonly Brush BodyFill = B(C("#141E2B")), BodyEdge = B(C("#2C3D52")), Key = B(C("#0B121B")), KeyEdge = B(C("#34475E")), Lit = B(AccentC), LitGlow = B(AccentC, .25),
                              TextB = B(C("#EEF3F7")), Muted = B(C("#91A0B2")), Dim = B(C("#5D6B7C")), Warn = B(WarnC), BgDeep = B(C("#090D14"));
        static readonly Pen EdgeP = P(BodyEdge, 2), KeyP = P(KeyEdge, 1.4), LitP = P(Lit, 2), ZoneP = P(Warn, 1.4, new double[] { 2.5, 2 }), OuterP = P(B(C("#91A0B2"), .7), 1.2), ThrP = P(Warn, 2.4);

        bool On(int bit) => !Empty && (Buttons >> bit & 1) != 0;
        static readonly string[] Game = { "A", "B", "X", "Y", "STICK_L", "STICK_R", "L", "R", "ZL", "ZR", "PLUS", "MINUS" };
        /// <summary>a physical control is lit when a game button it presses is on (its source in the map)</summary>
        bool SrcOn(string src)
        {
            for (int b = 0; b < Game.Length; b++)
                if (On(b) && Map.TryGetValue(Game[b], out var s) && s == src) return true;
            return false;
        }
        /// <summary>what a physical control does in Smash ("" nothing known); GameCube: by its own buttons</summary>
        string ActionOf(string src)
        {
            var list = new List<string>();
            foreach (var kv in Map) if (kv.Value == src && Action.TryGetValue(kv.Key, out var a) && a != "" && !list.Contains(a)) list.Add(a);
            return string.Join(" / ", list);
        }

        double k, ox, oy;
        Point Pt(double x, double y) => new Point(ox + x * k, oy + y * k);
        FormattedText T(string s, double size, Brush b, bool body = false) => new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, body ? Body : Face, size * k, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        /// <summary>a text at (x, y): align 0 centre, -1 its right end there, 1 its left end there</summary>
        void Label(DrawingContext dc, string s, double x, double y, double size, Brush b, int align = 0, bool body = false)
        {
            if (string.IsNullOrEmpty(s)) return;
            var t = T(s, size, b, body);
            double px = ox + x * k - (align == 0 ? t.Width / 2 : align < 0 ? t.Width : 0);
            dc.DrawText(t, new Point(px, oy + y * k - t.Height / 2));
        }

        protected override void OnRender(DrawingContext dc)
        {
            double W = ActualWidth, H = ActualHeight;
            k = Math.Min(W / DW, H / DH); ox = (W - DW * k) / 2; oy = (H - DH * k) / 2;
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, W, H));
            if (Empty) dc.PushOpacity(.35);
            bool gc = Kind == 3;
            Outline(dc, gc);
            Point ls, dp, face, rs;                       // the left stick, the d-pad, the face buttons, the right stick
            if (Kind == 1) { dp = new Point(180, 140); ls = new Point(250, 214); rs = new Point(390, 214); face = new Point(460, 140); }
            else if (gc) { ls = new Point(178, 142); dp = new Point(250, 226); face = new Point(458, 144); rs = new Point(392, 226); }
            else { ls = new Point(178, 140); dp = new Point(250, 214); rs = new Point(390, 214); face = new Point(460, 140); }
            Shoulders(dc, gc);
            Stick(dc, ls, LX, LY, RawLX, RawLY, gc ? false : SrcOn("leftstick"), "Left stick", "Move");
            Stick(dc, rs, RX, RY, RawRX, RawRY, gc ? false : SrcOn("rightstick"), gc ? "C-stick" : "Right stick", "Smash attacks", gc);
            DPad(dc, dp, gc ? .85 : 1);
            if (gc) GcFace(dc, face); else Face4(dc, face);
            if (gc) Small(dc, new Point(320, 150), On(10), "START", "Pause");
            else
            {
                string back = Kind == 0 ? "View" : Kind == 1 ? "Share" : Kind == 2 ? "−" : "Back", start = Kind == 0 ? "Menu" : Kind == 1 ? "Options" : Kind == 2 ? "+" : "Start";
                Small(dc, new Point(286, 130), SrcOn("back"), back, ActionOf("back"));
                Small(dc, new Point(354, 130), SrcOn("start"), start, ActionOf("start"));
            }
            if (Empty) dc.Pop();
        }

        void Outline(DrawingContext dc, bool gc)
        {
            // one outline: a wide top, two grips, the bridge between them (a plain gamepad shape, no maker's); the GameCube-style a little rounder
            double gy = gc ? 262 : 270, gx = gc ? 18 : 0;
            var g = Geometry.Parse(string.Format(CultureInfo.InvariantCulture,
                "M 180,86 L 460,86 C 515,86 542,106 558,146 L {0},{1} C {2},318 {3},344 {4},328 C 492,314 474,{5} 452,{6} L 188,{6} C 166,{5} 148,314 {7},328 C {8},344 {9},318 {10},{1} L 82,146 C 98,106 125,86 180,86 Z",
                586 - gx, gy, 600 - gx, 562 - gx, 522 - gx, gy + 18, gy, 118 + gx, 78 + gx, 40 + gx, 54 + gx)).Clone();
            var t = new TransformGroup(); t.Children.Add(new ScaleTransform(k, k)); t.Children.Add(new TranslateTransform(ox, oy));
            g.Transform = t;
            dc.DrawGeometry(BodyFill, EdgeP, g);
        }

        void Shoulders(DrawingContext dc, bool gc)
        {
            // the bumper (L / R; GameCube: Z on the right) under each trigger; the trigger a bar: how far it is pressed, where it counts (yellow)
            for (int side = 0; side < 2; side++)
            {
                double x0 = side == 0 ? 112 : 388, x1 = x0 + 140, mid = (x0 + x1) / 2;
                string bsrc = side == 0 ? "leftshoulder" : "rightshoulder", tsrc = side == 0 ? "lefttrigger" : "righttrigger";
                if (!gc || side == 1)
                {
                    bool bump = gc ? On(9) : SrcOn(bsrc);
                    string bl = gc ? "Z" : Kind == 1 ? (side == 0 ? "L1" : "R1") : Kind == 0 ? (side == 0 ? "LB" : "RB") : (side == 0 ? "L" : "R");
                    var r = new Rect(Pt(x0 + 16, 64), Pt(x1 - 16, 82));
                    dc.DrawRoundedRectangle(bump ? Lit : Key, bump ? LitP : KeyP, r, 8 * k, 8 * k);
                    Label(dc, bl, mid, 73, 12, bump ? BgDeep : TextB);
                    Label(dc, gc ? "Grab" : ActionOf(bsrc), side == 0 ? x0 - 8 : x1 + 8, 73, 13, bump ? Lit : Muted, side == 0 ? -1 : 1, true);
                }
                int level = Empty ? -1 : side == 0 ? TrigL : TrigR;
                bool pressed = gc ? On(side == 0 ? 6 : 7) : SrcOn(tsrc);
                string tl = gc ? (side == 0 ? "L" : "R") : Kind == 1 ? (side == 0 ? "L2" : "R2") : Kind == 0 ? (side == 0 ? "LT" : "RT") : (side == 0 ? "ZL" : "ZR");
                var bar = new Rect(Pt(x0, 26), Pt(x1, 52));
                dc.DrawRoundedRectangle(Key, pressed ? LitP : KeyP, bar, 7 * k, 7 * k);
                double f = level < 0 ? (pressed ? 1 : 0) : Math.Max(0, Math.Min(1, level / 32767.0));
                if (f > 0) dc.DrawRoundedRectangle(pressed ? Lit : LitGlow, null, new Rect(bar.X, bar.Y, bar.Width * f, bar.Height), 7 * k, 7 * k);
                double tx = bar.X + bar.Width * TriggerThreshold;      // where it counts as pressed
                dc.DrawLine(ThrP, new Point(tx, bar.Y - 4 * k), new Point(tx, bar.Bottom + 4 * k));
                Label(dc, tl, mid, 39, 13, pressed && f > .5 ? BgDeep : TextB);
                Label(dc, (gc ? "Shield" : ActionOf(tsrc)) + (level >= 0 ? "  " + (int)Math.Round(level / 327.67) + " %" : ""), side == 0 ? x0 - 8 : x1 + 8, 39, 13, pressed ? Lit : Muted, side == 0 ? -1 : 1, true);
            }
        }

        void Stick(DrawingContext dc, Point c, int x, int y, int rx, int ry, bool pressed, string name, string action, bool small = false)
        {
            double R = (small ? 31 : 36) * k;
            Point p = Pt(c.X, c.Y);
            dc.DrawEllipse(Key, pressed ? LitP : KeyP, p, R + 7 * k, R + 7 * k);
            dc.DrawEllipse(null, OuterP, p, R * OuterZone, R * OuterZone);           // the outer zone: from there a full tilt
            if (DeadZone > 0) dc.DrawEllipse(B(WarnC, .1), ZoneP, p, Math.Max(1.5, R * DeadZone), Math.Max(1.5, R * DeadZone));
            if (!Empty)
            {
                Point raw = new Point(p.X + rx / 32767.0 * R, p.Y - ry / 32767.0 * R), got = new Point(p.X + x / 32767.0 * R, p.Y - y / 32767.0 * R);
                dc.DrawEllipse(Muted, null, raw, 4.5 * k, 4.5 * k);
                bool moved = x != 0 || y != 0;
                if (moved) dc.DrawLine(P(LitGlow, 3 * k), p, got);
                dc.DrawEllipse(moved ? Lit : Dim, null, got, 6.5 * k, 6.5 * k);
            }
            double ly = c.Y + (small ? 48 : 54);
            Label(dc, name, c.X, ly, 12, pressed ? Lit : Muted);
            Label(dc, action, c.X, ly + 15, 13, TextB, 0, true);
        }

        void DPad(DrawingContext dc, Point c, double s)
        {
            double a = 16 * s, w = 14 * s;
            var dirs = new[] { (12, -1.0, 0.0), (13, 0.0, -1.0), (14, 1.0, 0.0), (15, 0.0, 1.0) };
            dc.DrawRoundedRectangle(Key, KeyP, new Rect(Pt(c.X - w / 2, c.Y - a - w / 2), Pt(c.X + w / 2, c.Y + a + w / 2)), 3 * k, 3 * k);
            dc.DrawRoundedRectangle(Key, KeyP, new Rect(Pt(c.X - a - w / 2, c.Y - w / 2), Pt(c.X + a + w / 2, c.Y + w / 2)), 3 * k, 3 * k);
            foreach (var d in dirs)
            {
                var r = new Rect(Pt(c.X + d.Item2 * a - w / 2, c.Y + d.Item3 * a - w / 2), Pt(c.X + d.Item2 * a + w / 2, c.Y + d.Item3 * a + w / 2));
                if (On(d.Item1)) dc.DrawRoundedRectangle(Lit, null, r, 3 * k, 3 * k);
                double ax = c.X + d.Item2 * a, ay = c.Y + d.Item3 * a, h = 4 * s;      // a small arrow pointing out
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
            Label(dc, "Taunts", c.X, c.Y + a + w + 6, 13, TextB, 0, true);
        }

        void Face4(DrawingContext dc, Point c)
        {
            // the four face buttons by position: south, east, west, north; the controller's own label inside, what it does in Smash beside it
            var pos = new[] { ("south", 0.0, 1.0), ("east", 1.0, 0.0), ("west", -1.0, 0.0), ("north", 0.0, -1.0) };
            string[] lab = Kind == 0 ? new[] { "A", "B", "X", "Y" } : Kind == 1 ? new[] { "✕", "○", "□", "△" } : Kind == 2 ? new[] { "B", "A", "Y", "X" } : new[] { "", "", "", "" };
            double d = 29, r = 15;
            for (int i = 0; i < 4; i++)
            {
                var p = new Point(c.X + pos[i].Item2 * d, c.Y + pos[i].Item3 * d);
                bool on = SrcOn(pos[i].Item1);
                if (on) dc.DrawEllipse(LitGlow, null, Pt(p.X, p.Y), (r + 5) * k, (r + 5) * k);
                dc.DrawEllipse(on ? Lit : Key, on ? LitP : KeyP, Pt(p.X, p.Y), r * k, r * k);
                Label(dc, lab[i], p.X, p.Y, 14, on ? BgDeep : TextB);
                string act = ActionOf(pos[i].Item1);
                // beside: the east one to the right, the west one to the left, the north one above, the south one below
                if (pos[i].Item2 > 0) Label(dc, act, p.X + r + 6, p.Y, 13, on ? Lit : TextB, 1, true);
                else if (pos[i].Item2 < 0) Label(dc, act, p.X - r - 6, p.Y, 13, on ? Lit : TextB, -1, true);
                else if (pos[i].Item3 < 0) Label(dc, act, p.X + r + 6, p.Y - 4, 13, on ? Lit : TextB, 1, true);      // (the north one: to its right, under the shoulder)
                else Label(dc, act, p.X, p.Y + r + 10, 13, on ? Lit : TextB, 0, true);
            }
        }

        void GcFace(DrawingContext dc, Point c)
        {
            // GameCube-style: a big A, B below left, X right, Y above (the game's A, B, X, Y)
            void Btn(double x, double y, double rx, double ry, int bit, string s, string act, double tx, double ty, int align)
            {
                bool on = On(bit);
                if (on) dc.DrawEllipse(LitGlow, null, Pt(x, y), (rx + 5) * k, (ry + 5) * k);
                dc.DrawEllipse(on ? Lit : Key, on ? LitP : KeyP, Pt(x, y), rx * k, ry * k);
                Label(dc, s, x, y, rx > 18 ? 17 : 13, on ? BgDeep : TextB);
                Label(dc, act, tx, ty, 13, on ? Lit : TextB, align, true);
            }
            Btn(c.X, c.Y, 23, 23, 0, "A", "Attack", c.X + 30, c.Y + 22, 1);
            Btn(c.X - 36, c.Y + 26, 13, 13, 1, "B", "Special", c.X - 52, c.Y + 26, -1);
            Btn(c.X + 40, c.Y - 9, 11, 18, 2, "X", "Jump", c.X + 56, c.Y - 9, 1);
            Btn(c.X - 6, c.Y - 38, 18, 11, 3, "Y", "Jump", c.X - 30, c.Y - 38, -1);
        }

        void Small(DrawingContext dc, Point c, bool on, string label, string action)
        {
            var r = new Rect(Pt(c.X - 25, c.Y - 10), Pt(c.X + 25, c.Y + 10));
            dc.DrawRoundedRectangle(on ? Lit : Key, on ? LitP : KeyP, r, 10 * k, 10 * k);
            Label(dc, label, c.X, c.Y, 11, on ? BgDeep : Muted);
            Label(dc, action, c.X, c.Y - 22, 12, on ? Lit : Muted, 0, true);
        }
    }
}
