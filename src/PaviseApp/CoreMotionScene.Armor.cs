using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class CoreMotionScene
    {
        // Rebuild materials only when the palette changes.
        private readonly LinearGradientBrush[] armorMaterials = new LinearGradientBrush[8];
        private Color armorAccent, armorCard, armorInset;
        private bool armorLight;
        private Matrix armorInverseBasis;

        private void PrepareArmor()
        {
            if (armorMaterials[7] != null && armorAccent == accent && armorLight == light
                && armorCard == Theme.Card && armorInset == Theme.Inset) return;
            ReleaseArmor();
            for (int i = 0; i < armorMaterials.Length; i++)
            {
                float amount = i < 4 ? .012f + i * .043f : i == 4 ? .27f : i == 5 ? .48f : i == 6 ? .73f : .95f;
                armorMaterials[i] = new LinearGradientBrush(new Rectangle(-185, -185, 370, 370),
                    Col.Lerp(Theme.Card, accent, amount + .035f),
                    Col.Lerp(Theme.Inset, accent, amount * .36f), 68f);
            }
            armorAccent = accent; armorLight = light; armorCard = Theme.Card; armorInset = Theme.Inset;
        }
        private void ReleaseArmor()
        {
            for (int i = 0; i < armorMaterials.Length; i++)
                if (armorMaterials[i] != null) { armorMaterials[i].Dispose(); armorMaterials[i] = null; }
        }
        private void ArmorPanel(int material, params PointF[] points)
        {
#if PAVISE_SELFTEST
            if (PanelGeometryForTest) { brush.Color = Color.White; g.FillPolygon(brush, points); return; }
#endif
            Polygon(A(Color.Black, light ? 24 : 150), 4.2f, points);
            // Cancel only the local plate rotation/mirror, preserving the viewport's DPI transform.
            using (Matrix lighting = g.Transform)
            {
                lighting.Multiply(armorInverseBasis, MatrixOrder.Append); lighting.Invert();
                armorMaterials[material].Transform = lighting;
            }
            g.FillPolygon(armorMaterials[material], points);
            Polygon(A(accent, 53 + material * 16), .8f, points);
            Line(A(high, 63 + material * 19), .75f, points[points.Length - 1], points[0], points[1]);
        }
        private void ArmorTrace(double phase, double strength, params PointF[] points)
        {
            Line(A(accent, 20 * strength), 5, points);
            Line(A(accent, 185 * strength), 1.4f, points);
            double total = 0;
            for (int i = 1; i < points.Length; i++)
            { double dx = points[i].X - points[i - 1].X, dy = points[i].Y - points[i - 1].Y; total += Math.Sqrt(dx * dx + dy * dy); }
            if (total < .01) return;
            double start = Fract(phase) * total, length = Math.Min(14, total * .3);
            for (int pass = 0; pass < 2; pass++)
            {
                double low = pass == 0 ? start : 0, end = pass == 0 ? Math.Min(total, start + length) : start + length - total;
                if (end <= low) continue;
                double walked = 0;
                for (int i = 1; i < points.Length; i++)
                {
                    PointF a = points[i - 1], b = points[i]; double dx = b.X - a.X, dy = b.Y - a.Y, size = Math.Sqrt(dx * dx + dy * dy);
                    if (size > .001 && walked < end && walked + size > low)
                    {
                        double u = Math.Max(0, (low - walked) / size), v = Math.Min(1, (end - walked) / size);
                        Line(A(high, 210 * strength), 1.1f, P(a.X + dx * u, a.Y + dy * u), P(a.X + dx * v, a.Y + dy * v));
                    }
                    walked += size;
                }
            }
        }
        private void ArmorFrame(double t, int form)
        {
            PrepareArmor();
            if (armorInverseBasis != null) armorInverseBasis.Dispose();
            armorInverseBasis = g.Transform; armorInverseBasis.Invert(); pen.LineJoin = LineJoin.Miter;
            Halo(0, 0, 195, accent, light ? .17 : .27);
            for (int side = -1; side <= 1; side += 2)
            {
                GraphicsState s = g.Save(); g.ScaleTransform(side, 1);
                Line(A(accent, 35), .65f, P(78, -159), P(124, -137), P(166, -66), P(170, 52), P(131, 122), P(70, 155));
                Line(A(accent, 82), 1, P(154, -91), P(174, -56), P(174, -22));
                ArmorPanel(0, P(155, 66), P(160, 46), P(171, 31), P(165, 71), P(143, 106));
                double pulse = .36 + .3 * Math.Sin(t * .7 + form + side);
                ArmorTrace(t * .1 + side * .2, pulse, P(164, 62), P(148, 92), P(134, 105));
                for (int mark = 0; mark < 3; mark++)
                    Line(A(accent, 65 + mark * 25), 1, P(114 + mark * 4, -135), P(119 + mark * 4, -142));
                g.Restore(s);
            }
            Line(A(accent, 28), .6f, P(-75, 165), P(75, 165));
            for (int i = 0; i < 8; i++)
            {
                double a = i * 2.399963 + form, r = 143 + i % 3 * 9;
                PointF p = Polar(r, a); Dot(p.X, p.Y, .7, A(high, 25 + 42 * (.5 + .5 * Math.Sin(t * .55 + i))));
            }
        }
        private void ArmorShard(double t, double height, double width)
        {
            Halo(0, 0, width * 1.6, accent, .35);
            width *= .94 + .06 * Math.Cos(t * 1.15);
            double yaw = Math.Sin(t * .92) * width * .65;
            PointF top = P(0, -height), bottom = P(0, height), left = P(-width, 0), right = P(width, 0), ridge = P(yaw, -height * .1);
            ArmorPanel(5, top, left, ridge); ArmorPanel(7, top, ridge, right);
            ArmorPanel(3, left, bottom, ridge); ArmorPanel(1, right, ridge, bottom);
            ArmorTrace(t * .19, .8, top, ridge, bottom);
            double at = Math.Sin(t * 1.1) * height * .34;
            Fill(A(high, 210), P(0, at - 7), P(4, at), P(0, at + 7), P(-4, at));
        }
        private void ArmorHex(double radius, double phase, double alpha, double yScale)
        {
            double head = Fract(phase) * 6;
            for (int i = 0; i < 6; i++)
            {
                double a = -Math.PI / 2 + i * Tau / 6;
                PointF p = Polar(radius, a), q = Polar(radius, a + Tau / 6 * .76);
                Line(A(accent, alpha), .8f, P(p.X, p.Y * yScale), P(q.X, q.Y * yScale));
                double distance = Math.Abs(i + .38 - head); distance = Math.Min(distance, 6 - distance);
                double highlight = Math.Pow(Math.Max(0, 1 - distance / .95), 2);
                if (highlight > .01)
                    Line(A(high, alpha * highlight * 1.7), 1.4f, P(p.X, p.Y * yScale), P(q.X, q.Y * yScale));
            }
        }
        private static double ArmorDrive(double t, double period, double phase)
        {
            double x = .5 - .5 * Math.Cos(Tau * (t / period + phase));
            return x * x * (3 - 2 * x);
        }
        private static float ArmorTurn(double degrees) { return (float)(degrees % 360); }
        private void ArmorBurst(double t, double period, double phase, double inner, double outer, double flatten)
        {
            double q = Fract(t / period + phase), strength = Math.Sin(q * Math.PI) * (1 - q);
            ArmorHex(inner + (outer - inner) * q, q, 155 * strength, flatten);
        }
        private void ArmorDebris(double t, double period, double phase, int count, double inner, double outer, double turn)
        {
            for (int i = 0; i < count; i++)
            {
                double q = Fract(t / period + phase + i * .173), a = turn + i * Tau / count;
                double r = inner + q * (outer - inner), size = 1.7 + 2.3 * (1 - q);
                PointF p = Polar(r, a), tip = Polar(r + size * 2.2, a), end = Polar(r - size, a);
                Fill(A(accent, Math.Sin(q * Math.PI) * 175), tip,
                    P(p.X - Math.Sin(a) * size, p.Y + Math.Cos(a) * size), end,
                    P(p.X + Math.Sin(a) * size, p.Y - Math.Cos(a) * size));
            }
        }

        private void Seraph(double t)
        {
            ArmorFrame(t, 1);
            double deploy = ArmorDrive(t, 5.8, 0), spread = 1 + deploy * 12;
            ArmorBurst(t, 5.8, .48, 85, 174, .72);
            for (int side = -1; side <= 1; side += 2)
            {
                GraphicsState wing = g.Save(); g.ScaleTransform(side, 1);
                g.TranslateTransform((float)(deploy * 5), (float)(-deploy * 4)); g.RotateTransform((float)-spread);
                GraphicsState feather = g.Save(); g.TranslateTransform(27, 0); g.RotateTransform((float)(-deploy * 7)); g.TranslateTransform(-27, 0);
                ArmorPanel(2, P(20, -25), P(80, -91), P(150, -117), P(125, -65), P(69, -25), P(38, -5));
                ArmorPanel(6, P(37, -25), P(93, -75), P(143, -105), P(116, -64), P(64, -28));
                ArmorTrace(t * .3, .8, P(39, -29), P(95, -78), P(137, -103)); g.Restore(feather);
                feather = g.Save(); g.TranslateTransform((float)(deploy * 5), (float)(deploy * 5)); g.RotateTransform((float)(deploy * 5));
                ArmorPanel(1, P(32, 0), P(93, -37), P(148, -60), P(117, -14), P(56, 23));
                ArmorPanel(4, P(48, 0), P(97, -27), P(131, -40), P(106, -11), P(58, 18));
                ArmorTrace(t * .3 + .4, .6, P(49, 5), P(101, -24), P(133, -40)); g.Restore(feather);
                feather = g.Save(); g.TranslateTransform((float)(deploy * 8), (float)(deploy * 13)); g.RotateTransform((float)(deploy * 10));
                ArmorPanel(0, P(31, 26), P(90, 7), P(132, -7), P(103, 33), P(48, 58));
                ArmorPanel(3, P(46, 32), P(99, 15), P(113, 12), P(90, 34), P(49, 52));
                g.Restore(feather);
                g.Restore(wing);
            }
            GraphicsState crown = g.Save(); g.TranslateTransform(0, (float)(-deploy * 10));
            ArmorPanel(2, P(-39, -105), P(0, -128), P(39, -105), P(20, -109), P(0, -116), P(-20, -109));
            g.Restore(crown);
            GraphicsState core = g.Save(); g.TranslateTransform(0, (float)(9 + deploy * 13));
            ArmorShard(t, 79 + deploy * 9, 19); g.Restore(core);
            ArmorTrace(t * .1, .6, P(-28, 88), P(0, 119), P(28, 88));
        }
        private void PlasmaStorm(double t)
        {
            ArmorFrame(t, 2); g.ScaleTransform(.9f, .9f);
            double charge = ArmorDrive(t, 4.6, .1);
            GraphicsState rotor = g.Save(); g.RotateTransform(ArmorTurn(t * 24 + Math.Sin(t * .9) * 12));
            for (int blade = 0; blade < 6; blade++)
            {
                GraphicsState s = g.Save(); g.RotateTransform(blade * 60);
                g.TranslateTransform(0, (float)(-charge * 13)); g.RotateTransform((float)(charge * 8 - 4));
                ArmorPanel(1, P(35, -20), P(77, -104), P(132, -66), P(145, -19), P(116, -37), P(80, -48), P(51, 1));
                ArmorPanel(5, P(58, -41), P(84, -89), P(128, -63), P(94, -68));
                ArmorPanel(2, P(88, -45), P(123, -34), P(145, -19), P(118, -7), P(68, 6));
                ArmorTrace(t * .15 + blade / 6.0, .75, P(43, -19), P(80, -95), P(126, -65));
                g.Restore(s);
            }
            g.Restore(rotor);
            ArmorPanel(0, P(0, -49), P(42, -25), P(42, 25), P(0, 49), P(-42, 25), P(-42, -25));
            GraphicsState hub = g.Save(); g.RotateTransform(ArmorTurn(-t * 17));
            ArmorShard(t, 34, 20); ArmorHex(57, t * .22, 95, 1); g.Restore(hub);
            ArmorDebris(t, 3.5, 0, 6, 59, 173, -t * .15);
        }
        private void SpacetimeShock(double t)
        {
            ArmorFrame(t, 3);
            double shift = Math.Sin(t * .9) * 12, open = ArmorDrive(t, 5.1, .1);
            ArmorBurst(t, 5.1, .4, 65, 172, 1);
            for (int layer = 2; layer >= 0; layer--)
            {
                GraphicsState s = g.Save(); double scale = 1 - layer * .17;
                g.TranslateTransform((float)(shift * (layer - 1)), (float)(-layer * 5 + Math.Sin(t * 1.1 + layer) * 8));
                g.RotateTransform((float)(Math.Sin(t * .7 + layer) * 5)); g.ScaleTransform((float)scale, (float)scale);
                for (int side = -1; side <= 1; side += 2)
                {
                    GraphicsState half = g.Save(); g.ScaleTransform(side, 1);
                    g.TranslateTransform((float)(open * (18 - layer * 3)), (float)(side * shift * .2));
                    ArmorPanel(layer == 0 ? 2 : 0, P(11, -137), P(101, -93), P(126, 6), P(77, 110), P(14, 136), P(37, 102), P(92, 0), P(73, -71));
                    ArmorPanel(layer == 0 ? 5 : 1, P(25, -129), P(97, -91), P(112, -33), P(91, -53), P(76, -80));
                    ArmorTrace(t * .12 + layer * .2, layer == 0 ? .8 : .3, P(34, -119), P(91, -87), P(113, -1), P(71, 95));
                    g.Restore(half);
                }
                g.Restore(s);
            }
            GraphicsState rift = g.Save(); g.TranslateTransform((float)shift, 0);
            g.ScaleTransform((float)(.65 + open * .75), 1);
            Halo(0, 0, 71, accent, .3);
            ArmorPanel(6, P(-7, -87), P(15, -29), P(-2, 2), P(10, 43), P(-12, 95), P(-3, 31), P(-21, 3), P(0, -32));
            ArmorTrace(t * .2, .85, P(-7, -87), P(4, -31), P(-10, 3), P(1, 38), P(-12, 95));
            g.Restore(rift);
        }
        private void Phoenix(double t)
        {
            ArmorFrame(t, 4);
            GraphicsState flight = g.Save(); g.TranslateTransform(0, (float)(Math.Sin(t * 1.12) * 7));
            g.RotateTransform((float)(Math.Sin(t * .8) * 4));
            Halo(0, 0, 54, accent, .25);
            GraphicsState sweep = g.Save(); g.RotateTransform((float)(-10 + Math.Sin(t * 1.12) * 13));
            ArmorPanel(1, P(-20, 17), P(-66, -50), P(-146, -92), P(-116, -26), P(-62, 14), P(-3, 43));
            ArmorPanel(6, P(-41, -1), P(-81, -45), P(-133, -80), P(-100, -37), P(-63, -8));
            GraphicsState feather = g.Save(); g.RotateTransform((float)(Math.Sin(t * 1.12 - .6) * 7));
            ArmorPanel(2, P(-46, 29), P(-99, 4), P(-127, -20), P(-109, 28), P(-60, 53), P(-7, 53));
            ArmorPanel(0, P(-31, 62), P(-87, 48), P(-114, 33), P(-85, 71), P(-47, 84), P(0, 57));
            g.Restore(feather);
            ArmorTrace(t * .16, .8, P(-39, 2), P(-80, -43), P(-133, -80));
            g.Restore(sweep);
            GraphicsState tail = g.Save(); g.RotateTransform((float)(Math.Sin(t * 1.12 - .9) * 12));
            ArmorPanel(2, P(0, 24), P(30, 40), P(81, 114), P(37, 84), P(45, 142), P(7, 91), P(-11, 54));
            ArmorPanel(4, P(7, 47), P(22, 59), P(36, 122), P(16, 90));
            ArmorTrace(t * .12 + .2, .65, P(10, 49), P(21, 87), P(35, 120)); g.Restore(tail);
            ArmorPanel(2, P(-14, 16), P(3, -34), P(56, -82), P(105, -75), P(71, -60), P(51, -25), P(25, -5), P(18, 42), P(-3, 70), P(-25, 41));
            ArmorPanel(5, P(2, -6), P(24, -36), P(56, -70), P(90, -72), P(61, -58), P(39, -21), P(20, 0));
            ArmorPanel(7, P(11, -4), P(25, -16), P(16, 31), P(0, 54));
            ArmorPanel(0, P(20, 12), P(62, -8), P(102, -6), P(72, 20), P(36, 35), P(11, 51));
            Fill(A(high, 215), P(61, -58), P(78, -65), P(68, -55));
            ArmorTrace(t * .18, .8, P(15, -10), P(38, -37), P(57, -66), P(89, -71));
            for (int i = 0; i < 7; i++)
            {
                double q = Fract(t * .32 + i * .173), x = 18 + i % 3 * 11 - q * 25, y = 78 + q * 95;
                Fill(A(i % 3 == 0 ? high : accent, Math.Sin(q * Math.PI) * 160), P(x, y - 8), P(x + 3, y), P(x - 1, y + 4), P(x - 3, y));
            }
            g.Restore(flight);
        }
        private void VoidDragon(double t)
        {
            ArmorFrame(t, 5);
            GraphicsState crest = g.Save(); g.TranslateTransform(0, (float)(Math.Sin(t * .9) * 6));
            GraphicsState coil = g.Save(); g.RotateTransform((float)(Math.Sin(t * .83) * 9));
            double expand = 1 + ArmorDrive(t, 6.1, .1) * .045; g.ScaleTransform((float)expand, (float)expand);
            ArmorPanel(1, P(-32, -123), P(-98, -93), P(-130, -26), P(-112, 55), P(-52, 108), P(44, 119), P(97, 77), P(61, 88), P(-28, 74), P(-78, 36), P(-87, -22), P(-64, -64));
            ArmorPanel(4, P(-107, -49), P(-86, -86), P(-40, -112), P(-67, -67), P(-88, -19));
            ArmorPanel(3, P(-101, 34), P(-74, 53), P(-26, 86), P(49, 102), P(82, 88), P(43, 118), P(-48, 104));
            ArmorTrace(t * .28, .65, P(-104, -37), P(-96, 28), P(-45, 84), P(36, 104)); g.Restore(coil);
            GraphicsState head = g.Save(); g.TranslateTransform((float)(Math.Sin(t * .83 - .7) * 8), (float)(-ArmorDrive(t, 6.1, .1) * 9));
            g.RotateTransform((float)(Math.Sin(t * .83 - .7) * 7));
            ArmorPanel(2, P(-32, -31), P(18, -59), P(40, -99), P(53, -64), P(111, -94), P(91, -49), P(125, -24), P(91, -13), P(66, 12), P(35, 5), P(26, -15), P(-20, 3), P(-44, 43), P(-18, 62), P(28, 54), P(54, 31), P(44, 64), P(6, 80), P(-42, 68), P(-65, 29));
            ArmorPanel(5, P(-29, -21), P(19, -48), P(43, -53), P(79, -40), P(100, -25), P(73, -27), P(47, -12), P(25, -24));
            ArmorPanel(6, P(46, -57), P(90, -80), P(72, -48));
            Fill(A(high, 220), P(57, -34), P(83, -31), P(65, -23), P(54, -25));
            ArmorTrace(t * .18, .8, P(-26, -22), P(19, -48), P(43, -53), P(79, -40));
            ArmorPanel(2, P(-55, -44), P(-36, -103), P(-2, -74), P(-19, -48));
            double breath = Math.Pow(ArmorDrive(t, 6.1, .1), 4);
            Fill(A(accent, 85 * breath), P(120, -24), P(183, -37), P(174, -17));
            Line(A(high, 190 * breath), 1.2f, P(121, -24), P(144, -30), P(153, -22), P(185, -31));
            for (int i = 0; i < 3; i++)
            {
                double q = Fract(t * .65 + i / 3.0), x = 124 + q * 59, y = -24 - q * 6;
                Fill(A(accent, Math.Sin(q * Math.PI) * breath * 210), P(x - 4, y), P(x + 4, y - 2), P(x + 1, y + 2));
            }
            g.Restore(head);
            g.Restore(crest);
        }
        private void Demon(double t)
        {
            ArmorFrame(t, 7);
            double power = ArmorDrive(t, 5.6, 0), lift = Math.Sin(t * 1.1) * 5;
            ArmorBurst(t, 5.6, .45, 80, 178, .9);
            GraphicsState head = g.Save(); g.TranslateTransform(0, (float)lift);
            Halo(0, 0, 52, accent, .22);
            for (int side = -1; side <= 1; side += 2)
            {
                GraphicsState half = g.Save(); g.ScaleTransform(side, 1);
                g.TranslateTransform((float)(power * 10), (float)(power * 3));
                GraphicsState horn = g.Save(); g.TranslateTransform(60, -25);
                g.RotateTransform((float)(power * 13 - 4)); g.TranslateTransform(-60, 25);
                ArmorPanel(1, P(41, -56), P(72, -89), P(88, -140), P(116, -109), P(110, -52), P(82, -12), P(66, 6));
                ArmorPanel(4, P(79, -63), P(97, -109), P(99, -79), P(90, -45), P(74, -26));
                g.Restore(horn);
                ArmorPanel(2, P(5, -63), P(53, -73), P(89, -36), P(70, 35), P(32, 91), P(0, 118), P(15, 56), P(30, 17));
                ArmorPanel(0, P(20, -27), P(69, -35), P(73, 1), P(42, 20), P(27, 61), P(4, 80), P(15, 17));
                GraphicsState jaw = g.Save(); g.TranslateTransform(0, (float)(power * 14));
                ArmorPanel(3, P(37, 39), P(65, 21), P(38, 79), P(0, 118), P(14, 77));
                ArmorTrace(t * .28, .5, P(58, 31), P(32, 80), P(4, 110)); g.Restore(jaw);
                Fill(A(accent, 115 + power * 135), P(24, -16), P(67, -11), P(50, 3), P(31, -3));
                ArmorTrace(t * .15 + side * .3, .65, P(13, -55), P(49, -64), P(80, -34));
                g.Restore(half);
            }
            GraphicsState core = g.Save(); g.TranslateTransform(0, (float)(-power * 9));
            ArmorPanel(5, P(0, -91), P(20, -51), P(10, 18), P(0, 50), P(-10, 18), P(-20, -51));
            ArmorPanel(7, P(0, -58), P(8, -18), P(0, 6), P(-8, -18));
            g.Restore(core);
            g.Restore(head);
        }
        private void ReactorArray(double t)
        {
            ArmorFrame(t, 8);
            ArmorBurst(t, 4.8, .35, 60, 163, 1);
            for (int arm = 0; arm < 3; arm++)
            {
                GraphicsState s = g.Save(); g.RotateTransform(ArmorTurn(arm * 120 + t * 13));
                double feed = ArmorDrive(t, 4.8, arm / 3.0);
                g.TranslateTransform(0, (float)(-feed * 15));
                ArmorPanel(0, P(-23, -141), P(23, -141), P(40, -112), P(40, -74), P(23, -53), P(-23, -53), P(-40, -74), P(-40, -112));
                ArmorPanel(3, P(-27, -123), P(-14, -135), P(18, -135), P(30, -114), P(19, -120), P(-17, -120));
                ArmorPanel(1, P(30, -108), P(38, -100), P(38, -75), P(21, -60), P(14, -65), P(28, -85));
                ArmorTrace(t * .14 + arm / 3.0, .7, P(-26, -124), P(-15, -133), P(16, -133), P(30, -113));
                double cycle = .5 + .5 * Math.Sin(t * .95 - arm * Tau / 3);
                Fill(A(accent, 140 + cycle * 95), P(-9, -111), P(11, -111), P(15, -86), P(0, -72), P(-15, -86));
                Fill(A(high, 100 + cycle * 120), P(-2, -105), P(4, -105), P(6, -89), P(0, -81), P(-4, -89));
                ArmorPanel(1, P(-45, -63), P(-40, -51), P(-85, 26), P(-99, 30), P(-104, 13), P(-62, -59));
                ArmorTrace(t * .11 + arm / 3.0, .4, P(-52, -52), P(-95, 21));
                double at = Fract(t * .55 - arm / 3.0);
                double innerEnd = -12 + feed * 15, packet = -63 + at * (innerEnd + 63);
                Line(A(accent, 60 + feed * 120), 1.4f, P(0, -63), P(0, innerEnd));
                Fill(A(high, Math.Sin(at * Math.PI) * 230), P(-3, packet), P(0, packet - 4), P(3, packet), P(0, packet + 4));
                g.Restore(s);
            }
            GraphicsState hub = g.Save(); g.RotateTransform(ArmorTurn(-t * 19));
            ArmorPanel(1, P(0, -39), P(36, 22), P(0, 47), P(-36, 22));
            ArmorPanel(6, P(0, -31), P(11, 15), P(0, 34), P(-11, 15));
            ArmorTrace(t * .18, .8, P(0, -30), P(0, 32));
            g.Restore(hub);
        }
        private void VoidPulse(double t)
        {
            ArmorFrame(t, 9); g.ScaleTransform(.9f, .9f); Halo(0, 0, 46, accent, .24);
            double drive = ArmorDrive(t, 5.4, .05), open = 3 + drive * 28;
            for (int panel = 0; panel < 4; panel++)
            {
                GraphicsState s = g.Save(); g.RotateTransform((float)(45 + panel * 90 + drive * 15));
                g.TranslateTransform(0, (float)-open); g.RotateTransform((float)(-drive * 11));
                ArmorPanel(1, P(-72, -107), P(15, -125), P(81, -76), P(46, -24), P(12, -32), P(-24, -63), P(-83, -45));
                ArmorPanel(5, P(-69, -98), P(12, -114), P(65, -76), P(14, -86), P(-43, -74));
                ArmorPanel(0, P(-72, -57), P(-24, -72), P(16, -38), P(47, -24), P(41, -6), P(4, -12), P(-32, -43));
                ArmorTrace(t * .13 + panel * .25, .8, P(-68, -97), P(11, -114), P(64, -77)); g.Restore(s);
            }
            ArmorBurst(t, 5.4, .5, 46, 178, 1); ArmorBurst(t, 5.4, .68, 35, 166, 1);
            ArmorDebris(t, 5.4, .5, 4, 39, 164, Math.PI / 4);
            GraphicsState crack = g.Save(); g.ScaleTransform((float)(.65 + drive * 1.4), (float)(.8 + drive * .3));
            ArmorTrace(t * .23, .8, P(-4, -48), P(8, -15), P(-8, 10), P(3, 49));
            g.Restore(crack);
        }
        private void PhotonWeave(double t)
        {
            ArmorFrame(t, 10); g.ScaleTransform(.9f, .9f); Halo(0, 0, 39, accent, .22);
            for (int arm = 0; arm < 3; arm++)
            {
                double phase = t * .95 + arm * Tau / 3;
                GraphicsState s = g.Save(); g.RotateTransform(ArmorTurn(-12 + arm * 120 + Math.Sin(phase) * 15 + t * 8));
                g.TranslateTransform(0, (float)(Math.Sin(phase - .7) * 13));
                g.ScaleTransform(1, (float)(.88 + Math.Cos(phase) * .12));
                ArmorPanel(1, P(-119, -39), P(-38, -115), P(91, -57), P(111, -16), P(61, -31), P(-31, -76), P(-83, -26));
                ArmorPanel(5, P(-108, -40), P(-36, -102), P(86, -49), P(60, -45), P(-33, -86), P(-87, -34));
                ArmorPanel(2, P(-72, -30), P(-29, -64), P(50, -26), P(30, -10), P(-27, -37), P(-48, -18));
                ArmorTrace(t * .36 + arm / 3.0, .8, P(-108, -40), P(-36, -102), P(86, -49));
                g.Restore(s);
            }
            GraphicsState center = g.Save(); g.RotateTransform(ArmorTurn(-t * 26)); ArmorHex(32, t * .2, 110, 1);
            Fill(A(high, 210), P(0, -11), P(8, 4), P(0, 13), P(-8, 4));
            g.Restore(center);
        }
        private void NovaShock(double t)
        {
            ArmorFrame(t, 12);
            double charge = ArmorDrive(t, 5.2, .08);
            Halo(0, 0, 69, accent, .18 + charge * .2);
            for (int ray = 0; ray < 4; ray++)
            {
                GraphicsState s = g.Save(); g.RotateTransform((float)(ray * 90 + charge * (ray % 2 == 0 ? 7 : -7)));
                g.TranslateTransform(0, (float)(-charge * 20));
                ArmorPanel(1, P(0, -146), P(25, -78), P(54, -45), P(24, -17), P(0, -46), P(-24, -17), P(-54, -45), P(-25, -78));
                ArmorPanel(5, P(0, -136), P(16, -77), P(39, -46), P(18, -38), P(0, -62));
                ArmorPanel(2, P(0, -136), P(0, -62), P(-18, -38), P(-39, -46), P(-16, -77));
                ArmorTrace(t * .18 + ray * .25, .55 + charge * .35, P(0, -133), P(13, -77), P(36, -47));
                g.Restore(s);
            }
            for (int i = 0; i < 4; i++)
            {
                GraphicsState s = g.Save(); g.RotateTransform(45 + i * 90);
                g.TranslateTransform(0, (float)(-charge * 15));
                ArmorPanel(2, P(0, -120), P(13, -82), P(0, -63), P(-13, -82)); g.Restore(s);
            }
            ArmorBurst(t, 5.2, .5, 53, 182, 1); ArmorBurst(t, 5.2, .7, 40, 167, 1);
            ArmorDebris(t, 5.2, .5, 8, 61, 179, Math.PI / 8);
            GraphicsState core = g.Save(); float size = (float)(.85 + charge * .3); g.ScaleTransform(size, size);
            ArmorShard(t, 41, 28); g.Restore(core);
        }
        private void OrbitalStructure(double t)
        {
            ArmorFrame(t, 13); g.ScaleTransform(.9f, .9f);
            for (int ring = 2; ring >= 0; ring--)
            {
                GraphicsState s = g.Save(); g.RotateTransform(ArmorTurn(-18 + ring * 28 + t * (ring == 1 ? -17 : 11 + ring * 4)));
                g.ScaleTransform(1, (float)(.86 + Math.Cos(t * .7 + ring * 1.4) * .14));
                double radius = 139 - ring * 27;
                for (int sector = 0; sector < 6; sector++)
                {
                    GraphicsState slab = g.Save(); g.RotateTransform(sector * 60);
                    g.TranslateTransform(0, (float)(-ArmorDrive(t, 5.7, ring * .2) * 9));
                    double x = radius * .866, y = radius * .5;
                    ArmorPanel(ring == 0 ? 1 : 2, P(-y * .81, -x), P(y * .81, -x), P(y, -x + 10), P(y * .74, -x + 22), P(-y * .74, -x + 22), P(-y, -x + 10));
                    ArmorPanel(4, P(-y * .78, -x + 3), P(y * .78, -x + 3), P(y * .66, -x + 9), P(-y * .66, -x + 9));
                    ArmorTrace(t * .09 + sector / 6.0 + ring * .2, .5, P(-y * .76, -x + 4), P(y * .76, -x + 4));
                    g.Restore(slab);
                }
                g.Restore(s);
            }
            GraphicsState shard = g.Save(); g.TranslateTransform(0, (float)(Math.Sin(t * 1.1) * 10));
            g.RotateTransform(ArmorTurn(t * 16));
            ArmorShard(t + 1, 48, 29); g.Restore(shard);
            ArmorDebris(t, 6, .1, 6, 145, 169, t * .21);
        }
        private void TidalField(double t)
        {
            ArmorFrame(t, 15); Halo(0, 0, 51, accent, .25);
            double drive = ArmorDrive(t, 5.5, .1), lift = 3 + drive * 25;
            double shift = Math.Sin(t * .9) * 10;
            PointF topTip = PointF.Empty, bottomTip = PointF.Empty;
            for (int pole = -1; pole <= 1; pole += 2)
            {
                GraphicsState s = g.Save(); g.ScaleTransform(1, pole);
                double angle = Math.Sin(t * .9 + pole * .5) * 5;
                g.TranslateTransform((float)shift, (float)-lift); g.RotateTransform((float)angle);
                double radians = angle * Math.PI / 180;
                PointF tip = P(shift + 29 * Math.Sin(radians), pole * (-lift - 29 * Math.Cos(radians)));
                if (pole == 1) topTip = tip; else bottomTip = tip;
                ArmorPanel(1, P(-110, -114), P(0, -143), P(110, -114), P(88, -79), P(42, -64), P(0, -29), P(-42, -64), P(-88, -79));
                ArmorPanel(5, P(-94, -111), P(0, -134), P(94, -111), P(66, -98), P(0, -113), P(-66, -98));
                ArmorPanel(3, P(-71, -88), P(-6, -104), P(-9, -72), P(0, -29), P(-40, -64));
                ArmorPanel(0, P(0, -29), P(9, -72), P(6, -104), P(71, -88), P(40, -64));
                ArmorTrace(t * .14 + pole * .3, .7, P(-91, -111), P(0, -133), P(91, -111));
                ArmorTrace(t * .1 + pole * .2, .6, P(-65, -86), P(-14, -73), P(0, -36));
                g.Restore(s);
            }
            GraphicsState shard = g.Save(); g.TranslateTransform((float)(Math.Sin(t * 1.2) * 12), (float)(Math.Cos(t * 1.2) * 7));
            g.RotateTransform((float)(Math.Sin(t * .8) * 14)); ArmorShard(t, 29, 17); g.Restore(shard);
            for (int side = -1; side <= 1; side += 2)
            {
                GraphicsState fragment = g.Save(); g.TranslateTransform((float)(side * drive * 15), 0);
                double drift = Math.Sin(t * 1.2 + side) * 13;
                ArmorPanel(2, P(side * 47, -18 + drift), P(side * 68, 0 + drift), P(side * 44, 26 + drift), P(side * 52, 1 + drift));
                double q = Fract(t * .13 + (side + 1) * .25), y = -55 + q * 110;
                Line(A(accent, Math.Sin(q * Math.PI) * 80), .7f, P(side * 24, y - 6), P(side * 21, y + 6));
                g.Restore(fragment);
            }
            double crystalX = Math.Sin(t * 1.2) * 12, crystalY = Math.Cos(t * 1.2) * 7;
            Line(A(accent, 50 + drive * 90), 1.2f, topTip,
                P(crystalX - 7 * drive, crystalY - 14), P(crystalX + 8 * drive, crystalY + 13), bottomTip);
        }
    }
}
