using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class CoreMotionScene
    {
        private void CelestialPaint(PointF[] points, Color bright, Color shade)
        {
            using (var path = new GraphicsPath())
            {
                path.AddClosedCurve(points, .5f);
#if PAVISE_SELFTEST
                if (PanelGeometryForTest)
                {
                    brush.Color = Color.White;
                    g.FillPath(brush, path);
                    return;
                }
#endif
                RectangleF bounds = path.GetBounds();
                if (bounds.Width < .01f || bounds.Height < .01f) return;
                using (var material = new LinearGradientBrush(bounds, bright, shade, 78f))
                    g.FillPath(material, path);
                pen.Color = A(second, light ? 95 : 120);
                pen.Width = .6f;
                g.DrawPath(pen, path);
            }
        }

        private void Seraph(double t)
        {
            double breath = .5 + .5 * Math.Sin(t * .82), radius = 77 + breath * 24;
            double drift = Math.Sin(t * .48) * 16;
            Halo(drift, 0, 192, accent, .72);
            for (int flame = 0; flame < 12; flame++)
            {
                double angle = flame * Tau / 12 + t * .055;
                double flare = 112 + Math.Sin(t * 1.13 + flame * 1.7) * 23;
                double curl = .12 + Math.Sin(t * .63 + flame) * .11;
                PointF a = Polar(radius - 4, angle - .08), b = Polar(flare, angle + curl);
                PointF c = Polar(flare + 18, angle + .13), d = Polar(radius - 3, angle + .10);
                CurveFill(new[] { P(a.X + drift, a.Y), P(b.X + drift, b.Y), P(c.X + drift, c.Y), P(d.X + drift, d.Y) },
                    A(flame % 3 == 0 ? high : accent, 65 + 27 * breath));
            }
            Orb(drift, 0, radius, accent, .9);
            double moonX = drift + 10 + Math.Sin(t * .48) * 22, moonY = Math.Cos(t * .48) * 9;
            Dot(moonX, moonY, radius - 7, Col.Lerp(Theme.Inset, Color.Black, light ? .12f : .68f));
            GraphicsState moon = g.Save();
            g.TranslateTransform((float)moonX, (float)moonY);
            Arc(radius - 7, -1.3, 2.45, A(high, 235), 1.4f);
            Arc(radius - 6, 1.2, 3.3, A(accent, 145), 2.2f);
            g.Restore(moon);
            GraphicsState orbit = g.Save();
            g.RotateTransform(-24); g.ScaleTransform(1, .34f);
            Arc(160, 0, Tau, A(accent, 65), .9f);
            PointF star = Polar(160, t * .45);
            Orb(star.X, star.Y, 6, high, .85);
            g.Restore(orbit);
            for (int i = 0; i < 21; i++)
            {
                double a = i * 2.399963 + t * .035, r = 150 + i % 4 * 12;
                PointF p = Polar(r, a);
                Dot(p.X, p.Y, i % 6 == 0 ? 1.15 : .65, A(second, 48 + 55 * (.5 + .5 * Math.Sin(t + i))));
            }
            double glint = .5 + .5 * Math.Sin(t * .48);
            Halo(moonX + (radius - 6) * .48, moonY - (radius - 6) * .87, 28, high, .45 + .4 * glint);
        }

        private void PlasmaStorm(double t)
        {
            double swell = .5 + .5 * Math.Sin(t * 1.05);
            Halo(0, 0, 188, accent, .58);
            PointF[] body = new PointF[28];
            for (int i = 0; i < body.Length; i++)
            {
                double a = i * Tau / body.Length;
                double r = 78 + swell * 24 + 12 * Math.Sin(a * 3 + t * 1.35) + 7 * Math.Cos(a * 5 - t * .9);
                body[i] = P(Math.Cos(a) * r, Math.Sin(a) * r * 1.05);
            }
            CurveFill(body, A(accent, 65));
            for (int filament = 0; filament < 7; filament++)
            {
                double a = filament * Tau / 7 + Math.Sin(t * .33) * .2;
                PointF[] flow = new PointF[12];
                for (int j = 0; j < flow.Length; j++)
                {
                    double q = j / (double)(flow.Length - 1), r = 22 + q * (119 + 16 * Math.Sin(t * 1.2 + filament));
                    double turn = a + .55 * Math.Sin(q * 3.9 - t * 1.4 + filament * .7) * q;
                    flow[j] = P(Math.Cos(turn) * r, Math.Sin(turn) * r);
                }
                Ribbon(flow, A(filament % 2 == 0 ? high : second, 155), 1.6f);
                PointF tip = flow[flow.Length - 1];
                PointF shoulder = flow[flow.Length - 4];
                double dx = tip.X - shoulder.X, dy = tip.Y - shoulder.Y;
                double length = Math.Max(.01, Math.Sqrt(dx * dx + dy * dy));
                dx /= length; dy /= length;
                Surface(new[] { P(tip.X + dx * 6, tip.Y + dy * 6), P(tip.X - dy * 1.8, tip.Y + dx * 1.8),
                    P(tip.X - dx * 10, tip.Y - dy * 10), P(tip.X + dy * 1.8, tip.Y - dx * 1.8) }, high, accent);
                Line(A(high, 115), .65f, P(tip.X - dx * 17, tip.Y - dy * 17),
                    P(tip.X - dx * 10 - dy * 6, tip.Y - dy * 10 + dx * 6),
                    P(tip.X - dx * 4 - dy * 8, tip.Y - dy * 4 + dx * 8));
            }
            Orb(Math.Sin(t * .91) * 8, Math.Cos(t * .72) * 10, 48 + swell * 18, accent, 1);
            for (int band = 0; band < 4; band++)
            {
                PointF[] arc = new PointF[32];
                for (int j = 0; j < arc.Length; j++)
                {
                    double a = -.3 + j * Math.PI * 1.45 / (arc.Length - 1) + t * (.16 + band * .04);
                    double r = 88 + band * 13 + Math.Sin(a * 3 + t * 1.1) * 5;
                    arc[j] = P(Math.Cos(a) * r, Math.Sin(a) * r * (.6 + band * .09));
                }
                Ribbon(arc, A(band == 0 ? high : accent, 75 + band * 12), .7f);
            }
            Sparks(t, 25, 145, .045);
        }

        private void SpacetimeShock(double t)
        {
            double turn = Math.Sin(t * .46) * 10;
            Halo(0, 0, 185, accent, .35);
            GraphicsState gate = g.Save();
            g.RotateTransform((float)turn);
            for (int layer = 5; layer >= 0; layer--)
            {
                double z = layer / 5.0, width = 100 - z * 54, height = 141 - z * 80;
                double offsetX = Math.Sin(t * .65 + z * 1.3) * (9 + z * 13), offsetY = -z * 11;
                double lean = Math.Sin(t * .57) * (14 - z * 6), rail = 4 + (1 - z) * 4;
                PointF a = P(offsetX - width + lean, offsetY - height);
                PointF b = P(offsetX + width + lean, offsetY - height + 8);
                PointF c = P(offsetX + width - lean, offsetY + height);
                PointF d = P(offsetX - width - lean, offsetY + height - 8);
                Color face = Col.Lerp(accent, high, (float)(.1 + (1 - z) * .45));
                Color shade = Col.Lerp(Theme.Inset, accent, (float)(.09 + (1 - z) * .17));
                Surface(new[] { a, b, P(b.X - rail, b.Y + rail), P(a.X + rail, a.Y + rail) }, face, shade);
                Surface(new[] { b, c, P(c.X - rail, c.Y - rail), P(b.X - rail, b.Y + rail) }, second, shade);
                Surface(new[] { c, d, P(d.X + rail, d.Y - rail), P(c.X - rail, c.Y - rail) }, face, shade);
                Surface(new[] { d, a, P(a.X + rail, a.Y + rail), P(d.X + rail, d.Y - rail) }, high, shade);
                Polygon(A(accent, 50 + (1 - z) * 115), .7f, new[] { a, b, c, d });
                if (layer > 0)
                {
                    double ahead = (layer - 1) / 5.0;
                    Line(A(accent, 32), .6f, a, P(-100 + ahead * 54, -141 + ahead * 80));
                    Line(A(accent, 32), .6f, c, P(100 - ahead * 54, 141 - ahead * 80));
                }
            }
            for (int streak = 0; streak < 22; streak++)
            {
                double q = Fract(t * .25 + streak * .618033), fade = Math.Sin(q * Math.PI);
                double x = (streak % 5 - 2) * (8 + q * 26), y = (streak % 2 == 0 ? -1 : 1) * (27 + q * 157);
                Line(A(streak % 4 == 0 ? high : accent, fade * 175), .75f,
                    P(x * .88, y * .88), P(x, y));
            }
            double scanY = Math.Sin(t * .85) * 96;
            Ribbon(new[] { P(-75, scanY), P(-25, scanY - 3), P(27, scanY - 3), P(75, scanY) }, A(high, 95), .9f);
            Halo(0, 0, 47, high, .25);
            g.Restore(gate);
        }

        private void Phoenix(double t)
        {
            double flap = Math.Sin(t * 1.22), lift = Math.Sin(t * 1.22 - .3) * 6;
            Halo(0, 10, 183, accent, .42);
            GraphicsState flight = g.Save();
            g.TranslateTransform(0, (float)lift);
            g.RotateTransform((float)(Math.Sin(t * .61) * 5));
            for (int side = -1; side <= 1; side += 2)
            {
                GraphicsState wing = g.Save();
                g.ScaleTransform(side, 1);
                for (int feather = 3; feather >= 0; feather--)
                {
                    double sweep = flap * (30 - feather * 4), endX = 164 - feather * 14, endY = -80 + feather * 34 - sweep;
                    PointF[] vane = {
                        P(12 + feather * 2, 5 + feather * 7), P(67, -27 + feather * 20 - sweep * .5),
                        P(endX, endY), P(endX - 28, endY + 33), P(77 - feather * 5, 29 + feather * 11), P(24, 33 + feather * 6)
                    };
                    CelestialPaint(vane, Col.Lerp(accent, high, .12f + feather * .09f), Col.Lerp(accent, Theme.Inset, .76f));
                    Ribbon(new[] { vane[0], vane[1], vane[2] }, A(high, 175 - feather * 17), .9f);
                    CurveFill(new[] { P(31, 19 + feather * 6), P(85, -4 + feather * 19 - sweep * .42),
                        P(endX - 4, endY + 6), P(88, 16 + feather * 18 - sweep * .25) }, A(second, 32));
                }
                g.Restore(wing);
            }
            for (int plume = 0; plume < 3; plume++)
            {
                double sway = Math.Sin(t * 1.1 - plume * .5) * (13 + plume * 4), x = (plume - 1) * 14;
                CelestialPaint(new[] { P(x - 10, 36), P(x + sway - 15, 82), P(x + sway + 4, 145 + plume * 8),
                    P(x + sway + 11, 99), P(x + 10, 40) }, Col.Lerp(accent, high, .12f + plume * .07f), Col.Lerp(accent, Theme.Inset, .65f));
                Ribbon(new[] { P(x, 48), P(x + sway - 2, 91), P(x + sway + 4, 145 + plume * 8) }, A(high, 130), .75f);
            }
            CelestialPaint(new[] { P(-15, 44), P(-23, 12), P(-7, -19), P(6, -58), P(24, -78),
                P(47, -75), P(24, -60), P(20, -27), P(27, 13), P(12, 47), P(0, 62) },
                Col.Lerp(accent, high, .32f), Col.Lerp(accent, Theme.Inset, .7f));
            CurveFill(new[] { P(1, -14), P(8, -49), P(22, -66), P(19, -22), P(10, 26), P(0, 49), P(-7, 17) }, A(high, 170));
            Fill(A(high, 240), P(25, -67), P(34, -69), P(28, -64));
            Ribbon(new[] { P(14, -58), P(6, -86), P(-4, -95) }, A(high, 140), 1.2f);
            for (int ember = 0; ember < 16; ember++)
            {
                double q = Fract(t * .23 + ember * .618033), x = (ember % 7 - 3) * 15 + Math.Sin(t + ember) * 7;
                double y = 50 + q * 125;
                Dot(x, y, .8 + (1 - q) * .8, A(ember % 4 == 0 ? high : accent, Math.Sin(q * Math.PI) * 155));
            }
            g.Restore(flight);
        }
    }
}
