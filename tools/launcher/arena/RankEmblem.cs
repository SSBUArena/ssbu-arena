// SPDX-License-Identifier: GPL-3.0-or-later
// (0.2.55) The ranked emblem (ranked per account, RankedAccount.cs): the player card's hexagon with the diamond (MainWindow.xaml
// RankHex / RankGem, the same paths and colours as 0.2.53's TierBadge), drawn at any size, plus the sub-tier pips and the Grandmaster
// star. The website draws the same geometry in SVG (RankEmblem in src/components/ranked.tsx): both follow the contract, viewBox
// 0 0 56 62:
//   division N (1-3): N pips "M x,46.5 L x+2.5,49 L x,51.5 L x-2.5,49 Z" centred on x = 28, 6 apart, filled like the small diamond
//   Grandmaster: no pips, the small diamond replaced by the star "M28,24 L30,29 L35,31 L30,33 L28,38 L26,33 L21,31 L26,29 Z"
//   Unranked: the steel emblem, no pips. Glow (Master, Grandmaster): a drop shadow in the tier's colour, as 0.2.53's badge.
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Arena
{
    public sealed class RankEmblem : FrameworkElement
    {
        public static readonly DependencyProperty SpecProperty = DependencyProperty.Register(nameof(Spec), typeof(EmblemSpec), typeof(RankEmblem),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (o, e) => ((RankEmblem)o).SpecChanged()));
        /// <summary>what to draw; null: the steel Unranked emblem</summary>
        public EmblemSpec Spec { get => (EmblemSpec)GetValue(SpecProperty); set => SetValue(SpecProperty, value); }

        const double W = 56, H = 62;
        static readonly Geometry Hex = G("M28,1 L54,16 L54,46 L28,61 L2,46 L2,16 Z"), HexIn = G("M28,7 L49,19 L49,43 L28,55 L7,43 L7,19 Z"),
                                 Gem = G("M28,18 L39,31 L28,44 L17,31 Z"), GemCore = G("M28,26 L32.5,31 L28,36 L23.5,31 Z"),
                                 Star = G("M28,24 L30,29 L35,31 L30,33 L28,38 L26,33 L21,31 L26,29 Z");
        static readonly Geometry[] Pips = { null, PipsOf(1), PipsOf(2), PipsOf(3) };
        static readonly Color Deep = Color.FromRgb(0x0D, 0x17, 0x24);

        // the steel (Unranked) emblem: MainWindow.xaml's RankHex / RankHexIn / RankGem / RankGemCore as written there
        static readonly Brush SteelFill = Frozen(new LinearGradientBrush(C("#22344A"), C("#0D1724"), 90)), SteelStroke = Frozen(new LinearGradientBrush(C("#C3CEDA"), C("#56657A"), 90));
        static readonly Pen SteelHexPen = P(SteelStroke, 2), SteelInPen = P(Frozen(new SolidColorBrush(C("#33B6D4E8"))), 1), SteelGemPen = P(Frozen(new SolidColorBrush(C("#AAB6C4"))), 2);
        static readonly Brush SteelGemFill = Frozen(new SolidColorBrush(C("#14FFFFFF"))), SteelCore = Frozen(new SolidColorBrush(C("#7D8C9E")));

        Brush hexFill, gemFill, coreFill;
        Pen hexPen, inPen, gemPen;

        static Geometry G(string d) { var g = Geometry.Parse(d); g.Freeze(); return g; }
        static Geometry PipsOf(int n)
        {
            var g = new GeometryGroup();
            for (int i = 0; i < n; i++)
            {
                double x = 28 + (i - (n - 1) / 2.0) * 6;
                g.Children.Add(Geometry.Parse(string.Format(CultureInfo.InvariantCulture, "M{0},46.5 L{1},49 L{0},51.5 L{2},49 Z", x, x + 2.5, x - 2.5)));
            }
            g.Freeze();
            return g;
        }
        static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);
        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen P(Brush b, double t) { var p = new Pen(b, t) { LineJoin = PenLineJoin.Round }; p.Freeze(); return p; }
        static Color Mix(Color a, Color b, double t) => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

        public RankEmblem() { SnapsToDevicePixels = false; SpecChanged(); }

        void SpecChanged()
        {
            var s = Spec;
            if (s == null || s.Kind == EmblemSpec.Kinds.Unranked)
            {
                hexFill = SteelFill; hexPen = SteelHexPen; inPen = SteelInPen; gemPen = SteelGemPen; gemFill = SteelGemFill; coreFill = SteelCore;
                Effect = null;
                return;
            }
            // 0.2.53's TierBadge, colour for colour
            var c = s.Color;
            hexFill = Frozen(new LinearGradientBrush(Mix(Deep, c, 0.28), Deep, 90));
            hexPen = P(Frozen(new LinearGradientBrush(Mix(c, Colors.White, 0.35), Mix(c, Deep, 0.45), 90)), 2);
            inPen = P(Frozen(new SolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B))), 1);
            gemPen = P(Frozen(new SolidColorBrush(c)), 2);
            gemFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, c.R, c.G, c.B)));
            coreFill = Frozen(new SolidColorBrush(Mix(c, Colors.White, 0.25)));
            Effect = s.Glow ? new DropShadowEffect { Color = c, BlurRadius = 12 * Scale, ShadowDepth = 0, Opacity = 0.65 } : null;
        }

        /// <summary>0.2.53's glow was 12 on the 56 x 62 canvas, then scaled with it (0.68 on the player card): the same here</summary>
        double Scale => double.IsNaN(Width) || Width <= 0 ? 1 : Width / W;

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == WidthProperty) SpecChanged();
        }

        protected override Size MeasureOverride(Size available)
        {
            double w = double.IsInfinity(available.Width) ? W : available.Width, h = double.IsInfinity(available.Height) ? H : available.Height;
            double k = Math.Min(w / W, h / H);
            return new Size(W * k, H * k);
        }

        protected override void OnRender(DrawingContext dc)
        {
            double k = Math.Min(ActualWidth / W, ActualHeight / H);
            if (k <= 0) return;
            dc.PushTransform(new TranslateTransform((ActualWidth - W * k) / 2, (ActualHeight - H * k) / 2));
            dc.PushTransform(new ScaleTransform(k, k));
            var s = Spec;
            dc.DrawGeometry(hexFill, hexPen, Hex);
            dc.DrawGeometry(null, inPen, HexIn);
            dc.DrawGeometry(gemFill, gemPen, Gem);
            if (s != null && s.Kind == EmblemSpec.Kinds.Grandmaster) dc.DrawGeometry(coreFill, null, Star);
            else dc.DrawGeometry(coreFill, null, GemCore);
            if (s != null && s.Kind == EmblemSpec.Kinds.Tier && s.Division >= 1 && s.Division <= 3) dc.DrawGeometry(coreFill, null, Pips[s.Division]);
            dc.Pop(); dc.Pop();
        }
    }
}
