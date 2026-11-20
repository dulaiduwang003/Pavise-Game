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

        private void Ribbon(PointF[] points, Color color, float width)
        {
            if (points.Length < 2) return;
#if PAVISE_SELFTEST
            if (PanelGeometryForTest) return;
#endif
            pen.Color = Color.FromArgb(color.A / 8, color); pen.Width = width + 9;
            g.DrawCurve(pen, points, .45f);
            pen.Color = Color.FromArgb(color.A / 3, color); pen.Width = width + 3;
            g.DrawCurve(pen, points, .45f);
            pen.Color = color; pen.Width = width;
            g.DrawCurve(pen, points, .45f);
        }

        private void DrawAstralWhale(double t)
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
    }
}
