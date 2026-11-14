using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class CoreMotionScene
    {
        private void LifePaint(GraphicsPath shape, Color lit, Color shade)
        {
#if PAVISE_SELFTEST
            if (PanelGeometryForTest)
            {
                brush.Color = Color.White;
                g.FillPath(brush, shape);
                return;
            }
#endif
            RectangleF bounds = shape.GetBounds();
            if (bounds.Width < .1f || bounds.Height < .1f) return;
            using (var material = new LinearGradientBrush(bounds, lit, shade, 105f))
                g.FillPath(material, shape);
            pen.Color = A(second, light ? 150 : 175);
            pen.Width = .85f;
            g.DrawPath(pen, shape);
        }

        private void LifeTendril(double t, double origin, double length, double phase, double width)
        {
            const int samples = 24;
            var ribbon = new PointF[samples * 2];
            for (int i = 0; i < samples; i++)
            {
                double u = i / (double)(samples - 1);
                double y = -13 + length * u;
                double x = origin + Math.Sin(t * 1.4 - u * 5.7 + phase) * (5 + u * 17);
                double half = width * (1 - u * .86);
                ribbon[i] = P(x - half, y);
                ribbon[ribbon.Length - 1 - i] = P(x + half, y);
            }
            Surface(ribbon, Color.FromArgb(180, second), Color.FromArgb(12, accent));
            var spine = new PointF[samples];
            for (int i = 0; i < samples; i++)
                spine[i] = P((ribbon[i].X + ribbon[ribbon.Length - 1 - i].X) * .5, ribbon[i].Y);
            Ribbon(spine, A(high, 125), .65f);
        }

        private void VoidDragon(double t)
        {
            double breath = .5 + .5 * Math.Sin(t * 1.45);
            double width = 80 + breath * 32;
            double crown = -125 + breath * 28;
            GraphicsState drift = g.Save();
            g.TranslateTransform((float)(Math.Sin(t * .51) * 9), (float)(Math.Sin(t * 1.45 - .7) * 13));
            Halo(0, -40, 153, accent, .36);
            for (int i = 0; i < 9; i++)
                LifeTendril(t, (i - 4) * 14, 111 + i % 3 * 17, i * .8, i % 2 == 0 ? 2.5 : 1.25);
            using (var bell = new GraphicsPath())
            {
                bell.AddBezier(P(-width, -19), P(-width, crown + 21), P(-width * .55, crown), P(0, crown));
                bell.AddBezier(P(0, crown), P(width * .55, crown), P(width, crown + 21), P(width, -19));
                bell.AddBezier(P(width, -19), P(width * .60, 5), P(width * .25, -2), P(0, -8));
                bell.AddBezier(P(0, -8), P(-width * .31, -1), P(-width * .72, 3), P(-width, -19));
                bell.CloseFigure();
                LifePaint(bell, Color.FromArgb(190, Col.Lerp(accent, Color.White, .57f)), Color.FromArgb(70, accent));
            }
            for (int rib = -3; rib <= 3; rib++)
            {
                double x = rib * width / 3.3;
                Ribbon(new[] { P(0, crown + 3), P(x * .62, crown + 23), P(x * .95, -56), P(x, -16) },
                    A(rib == 0 ? high : second, rib == 0 ? 210 : 98), rib == 0 ? 1.7f : .9f);
            }
            Ribbon(new[] { P(-width, -19), P(-width * .56, -5), P(0, -8), P(width * .58, -5), P(width, -19) }, A(high, 215), 2);
            Orb(0, -53 + breath * 7, 19 + breath * 3, accent, .82);
            for (int i = 0; i < 17; i++)
            {
                double phase = t * .17 + i * .618033;
                double y = -29 + 157 * Fract(phase);
                double alpha = Math.Sin(Fract(phase) * Math.PI) * 160;
                Dot(Math.Sin(i * 2.4 + t * .4) * (31 + i % 4 * 14), y, .8 + i % 3 * .35, A(high, alpha));
            }
            g.Restore(drift);
        }

        private void Demon(double t)
        {
            GraphicsState swim = g.Save();
            g.TranslateTransform((float)(Math.Sin(t * .47) * 8), (float)(Math.Sin(t * .9) * 13));
            g.RotateTransform((float)(Math.Sin(t * .7) * 7));
            Halo(6, 0, 170, accent, .27);
            GraphicsState tail = g.Save();
            g.TranslateTransform(-111, 0);
            g.RotateTransform((float)(Math.Sin(t * 1.3) * 27));
            using (var fluke = new GraphicsPath())
            {
                fluke.AddBezier(P(4, -3), P(-23, -16), P(-34, -49), P(-63, -58));
                fluke.AddBezier(P(-63, -58), P(-57, -30), P(-47, -7), P(-28, 1));
                fluke.AddBezier(P(-28, 1), P(-49, 10), P(-63, 27), P(-70, 48));
                fluke.AddBezier(P(-70, 48), P(-32, 39), P(-18, 13), P(4, 9));
                fluke.CloseFigure();
                LifePaint(fluke, Col.Lerp(accent, Color.White, .21f), Col.Lerp(accent, Color.Black, .84f));
            }
            g.Restore(tail);
            using (var body = new GraphicsPath())
            {
                body.AddBezier(P(137, -4), P(140, -38), P(104, -61), P(50, -56));
                body.AddBezier(P(50, -56), P(4, -57), P(-48, -34), P(-112, -7));
                body.AddBezier(P(-112, -7), P(-122, 0), P(-114, 10), P(-96, 14));
                body.AddBezier(P(-96, 14), P(-66, 21), P(-61, 56), P(-4, 57));
                body.AddBezier(P(-4, 57), P(31, 60), P(47, 34), P(80, 33));
                body.AddBezier(P(80, 33), P(114, 32), P(139, 23), P(137, -4));
                body.CloseFigure();
                LifePaint(body, Col.Lerp(accent, Color.White, .24f), Col.Lerp(accent, Color.Black, .83f));
            }
            using (var dorsal = new GraphicsPath())
            {
                dorsal.AddBezier(P(-31, -31), P(-14, -49), P(-27, -78), P(-14, -86));
                dorsal.AddBezier(P(-14, -86), P(-7, -54), P(14, -51), P(24, -48));
                dorsal.CloseFigure();
                LifePaint(dorsal, second, dark);
            }
            GraphicsState fin = g.Save();
            g.TranslateTransform(44, 19);
            g.RotateTransform((float)(Math.Sin(t * 1.3 - .8) * 23));
            using (var flipper = new GraphicsPath())
            {
                flipper.AddBezier(P(0, 0), P(-9, 29), P(-33, 65), P(-68, 83));
                flipper.AddBezier(P(-68, 83), P(-67, 54), P(-42, 6), P(-21, -4));
                flipper.CloseFigure();
                LifePaint(flipper, Col.Lerp(accent, Color.White, .30f), Col.Lerp(accent, Color.Black, .46f));
            }
            g.Restore(fin);
            for (int pleat = 0; pleat < 4; pleat++)
                Ribbon(new[] { P(121, 12 + pleat * 3), P(96, 23 + pleat * 4), P(65, 22 + pleat * 5), P(44, 17 + pleat * 5) }, A(high, 58), .7f);
            Ribbon(new[] { P(-92, -4), P(-42, -22), P(13, -40), P(66, -40), P(101, -27) }, A(second, 96), 1.05f);
            Ribbon(new[] { P(-81, 5), P(-34, -2), P(18, -19), P(56, -24) }, A(second, 42), .65f);
            Halo(105, -13, 13, high, .65);
            Dot(105, -13, 2.2, A(high, 245));
            for (int i = 0; i < 27; i++)
            {
                double x = -58 + i * 6.3;
                double y = -14 - Math.Sin(i * .52) * 19;
                Dot(x, y, .6 + i % 3 * .4, A(high, 60 + 110 * (.5 + .5 * Math.Sin(t * 1.1 + i))));
            }
            g.Restore(swim);
        }

        private void LifeEngineWing(double t, int side)
        {
            GraphicsState wing = g.Save();
            g.ScaleTransform(side, 1);
            g.TranslateTransform(24, 6);
            g.RotateTransform((float)(-7 + Math.Sin(t * .95) * 18));
            Surface(new[] { P(0, -36), P(29, -8), P(106, 52), P(126, 88), P(50, 59), P(15, 50) },
                Col.Lerp(accent, Color.White, .42f), dark);
            Surface(new[] { P(29, 2), P(101, 59), P(110, 72), P(49, 43), P(18, 29) },
                second, Col.Lerp(accent, Color.Black, .25f));
            Beam(A(high, 180), 1.3f, P(8, -18), P(49, 20), P(112, 75));
            g.Restore(wing);
        }

        private void ReactorArray(double t)
        {
            GraphicsState ship = g.Save();
            g.TranslateTransform(0, (float)(Math.Sin(t * .95) * 9));
            g.RotateTransform((float)(-8 + Math.Sin(t * .6) * 8));
            Halo(0, 45, 151, accent, .32);
            double thrust = .5 + .5 * Math.Sin(t * 1.9);
            for (int side = -1; side <= 1; side += 2)
            {
                double baseX = side * 29, end = 145 + thrust * 27;
                using (var exhaust = new GraphicsPath())
                {
                    exhaust.AddBezier(P(baseX - 11, 58), P(baseX - 21, 103), P(baseX + Math.Sin(t * 4 + side) * 7 - 4, end - 18), P(baseX, end));
                    exhaust.AddBezier(P(baseX, end), P(baseX + 5, end - 22), P(baseX + 20, 102), P(baseX + 11, 58));
                    exhaust.CloseFigure();
                    LifePaint(exhaust, Color.FromArgb(240, high), Color.FromArgb(0, accent));
                }
                Ribbon(new[] { P(baseX, 66), P(baseX - side * 3, 91), P(baseX + side * 2, 121), P(baseX, end - 4) }, A(high, 230), 2.5f);
                Surface(new[] { P(baseX - 14, 24), P(baseX + 14, 24), P(baseX + 17, 65), P(baseX - 17, 65) }, second, dark);
                Beam(A(high, 240), 2, P(baseX - 12, 62), P(baseX + 12, 62));
                LifeEngineWing(t, side);
            }
            Surface(new[] { P(0, -150), P(29, -56), P(22, 41), P(0, 66), P(-22, 41), P(-29, -56) },
                Col.Lerp(accent, Color.White, .58f), Col.Lerp(accent, Color.Black, .55f));
            Surface(new[] { P(0, -132), P(10, -50), P(0, 5), P(-10, -50) }, high, accent);
            Surface(new[] { P(0, -43), P(13, -12), P(9, 22), P(0, 34), P(-9, 22), P(-13, -12) },
                Col.Lerp(high, Color.White, .35f), Col.Lerp(accent, Color.Black, .48f));
            Beam(A(high, 185), 1.2f, P(0, -135), P(0, -62));
            g.Restore(ship);
        }

        private void LifePetal(double radius, double breadth, Color lit, Color shade)
        {
            using (var petal = new GraphicsPath())
            {
                petal.AddBezier(P(0, 19), P(-breadth, -radius * .2), P(-breadth * .8, -radius * .82), P(0, -radius));
                petal.AddBezier(P(0, -radius), P(breadth * .8, -radius * .82), P(breadth, -radius * .2), P(0, 19));
                petal.CloseFigure();
                LifePaint(petal, lit, shade);
            }
            Ribbon(new[] { P(0, 8), P(-breadth * .13, -radius * .25), P(0, -radius * .63), P(0, -radius + 8) }, A(high, 91), .7f);
            Ribbon(new[] { P(0, -radius * .24), P(-breadth * .30, -radius * .39), P(-breadth * .36, -radius * .64), P(-breadth * .22, -radius * .79) }, A(second, 65), .65f);
            Ribbon(new[] { P(0, -radius * .31), P(breadth * .29, -radius * .46), P(breadth * .33, -radius * .67), P(breadth * .20, -radius * .80) }, A(second, 49), .65f);
        }

        private void VoidPulse(double t)
        {
            double blossom = .5 + .5 * Math.Sin(t * .91);
            Halo(0, 0, 187, accent, .34);
            for (int layer = 0; layer < 2; layer++)
            {
                int count = layer == 0 ? 7 : 5;
                double radius = layer == 0 ? 98 + blossom * 66 : 59 + (1 - blossom) * 42;
                double breadth = layer == 0 ? 17 + blossom * 20 : 15 + (1 - blossom) * 15;
                for (int i = 0; i < count; i++)
                {
                    GraphicsState petal = g.Save();
                    g.RotateTransform((float)(i * 360.0 / count + layer * 31 + Math.Sin(t * .43) * (layer == 0 ? 13 : -22)));
                    g.TranslateTransform(0, (float)(-blossom * (layer == 0 ? 4 : 2)));
                    LifePetal(radius, breadth,
                        Col.Lerp(accent, Color.White, layer == 0 ? .13f : .40f),
                        Col.Lerp(accent, Color.Black, layer == 0 ? .80f : .57f));
                    g.Restore(petal);
                }
            }
            Orb(0, 0, 23 + blossom * 5, accent, 1);
            for (int i = 0; i < 21; i++)
            {
                double angle = i * 2.399963 + t * .13;
                double r = 54 + i % 7 * 18;
                PointF p = Polar(r, angle);
                Dot(p.X, p.Y, .65 + i % 3 * .35, A(high, 30 + 110 * (.5 + .5 * Math.Sin(t + i))));
            }
        }
    }
}
