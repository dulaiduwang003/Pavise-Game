using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class PaviseCore
    {
        private static PointF OrbitPoint(float rx, float ry, double degrees)
        {
            double a = degrees * Math.PI / 180;
            return new PointF(rx * (float)Math.Cos(a), ry * (float)Math.Sin(a));
        }

        private static void DrawOrbitFlow(Graphics g, float rx, float ry, double angle,
            Color c, float power, bool front)
        {
            using (var trail = new Pen(c, 1.4f))
            {
                trail.StartCap = trail.EndCap = LineCap.Round;
                for (int j = 23; j >= 0; j--)
                {
                    double a = angle - j * 2;
                    if ((Math.Sin((a - 1) * Math.PI / 180) >= 0) != front) continue;
                    float fade = 1f - j / 24f;
                    trail.Color = Col.Alpha(c, (int)(185 * fade * fade * power));
                    g.DrawLine(trail, OrbitPoint(rx, ry, a - 2), OrbitPoint(rx, ry, a));
                }
            }
            if ((Math.Sin(angle * Math.PI / 180) >= 0) != front) return;
            PointF head = OrbitPoint(rx, ry, angle);
            using (var glow = new SolidBrush(Col.Alpha(c, (int)(30 * power))))
                g.FillEllipse(glow, head.X - 5, head.Y - 5, 10, 10);
            using (var dot = new SolidBrush(Col.Alpha(Col.Lerp(c, Theme.Fg, .65f), (int)(255 * power))))
                g.FillEllipse(dot, head.X - 1.6f, head.Y - 1.6f, 3.2f, 3.2f);
        }

        private static readonly PointF[] CompetitiveCrown = {
            new PointF(-73, 1), new PointF(-28, -34), new PointF(73, -43),
            new PointF(47, -22), new PointF(-25, -18) };
        private static readonly PointF[] CompetitiveBlade = {
            new PointF(-64, 10), new PointF(-26, 34), new PointF(30, 17),
            new PointF(61, -17), new PointF(13, 7), new PointF(-24, 22) };
        private static readonly PointF[] CompetitiveIris = {
            new PointF(-15, 1), new PointF(5, -15), new PointF(25, -13),
            new PointF(7, 5), new PointF(-8, 10) };
        private const double CompetitiveEyePeriod = 9.8;
        private static readonly double[] CompetitiveGazeTimes = { 0, 1.1, 2.35, 3.7, 4.95, 6.15, 7.5, 8.65, CompetitiveEyePeriod };
        private static readonly PointF[] CompetitiveGazeTargets = {
            new PointF(0, 0), new PointF(10, -2), new PointF(10, -2),
            new PointF(-11, 2), new PointF(-11, 2), new PointF(4, 4),
            new PointF(4, 4), new PointF(0, 0), new PointF(0, 0) };

        private static float CompetitiveEase(double value)
        {
            double t = Math.Max(0, Math.Min(1, value));
            return (float)(t * t * t * (t * (t * 6 - 15) + 10));
        }

        private static PointF CompetitiveGaze(double seconds)
        {
            double phase = seconds % CompetitiveEyePeriod;
            for (int i = 1; i < CompetitiveGazeTimes.Length; i++)
            {
                if (phase > CompetitiveGazeTimes[i]) continue;
                float t = CompetitiveEase((phase - CompetitiveGazeTimes[i - 1]) / (CompetitiveGazeTimes[i] - CompetitiveGazeTimes[i - 1]));
                PointF a = CompetitiveGazeTargets[i - 1], b = CompetitiveGazeTargets[i];
                return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
            }
            return PointF.Empty;
        }

        private static float CompetitiveEyeOpen(double seconds)
        {
            double phase = seconds % CompetitiveEyePeriod;
            double blink = phase >= 8.3 ? phase - 8.3 : phase - 3.0;
            float closure = blink < 0 || blink >= .38 ? 0
                : blink < .12 ? CompetitiveEase(blink / .12)
                : blink < .17 ? 1 : 1 - CompetitiveEase((blink - .17) / .21);
            return (1 - .94f * closure) * (1 - .035f * (float)Math.Pow(Math.Sin(seconds * Math.PI / CompetitiveEyePeriod), 2));
        }

        private static void CompetitiveFacet(Graphics g, PointF[] points, Color bright, Color dark)
        {
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(points);
                using (var fill = new LinearGradientBrush(path.GetBounds(), bright, dark, 115f))
                    g.FillPath(fill, path);
            }
        }

        private void DrawCompetitiveStructure(Graphics g, Color c)
        {
            Halo(g, new RectangleF(-126, -121, 252, 242), c, Theme.LightMode ? 18 : 55);
            Color graphite = Col.Lerp(Theme.Card, c, Theme.LightMode ? .04f : .07f);
            for (int side = 0; side < 2; side++)
            {
                GraphicsState saved = g.Save(); g.RotateTransform(side * 180);
                PointF[] armor = { new PointF(-117, -18), new PointF(-86, -58), new PointF(-44, -75),
                    new PointF(-56, -54), new PointF(-78, -40), new PointF(-103, -7), new PointF(-113, 17) };
                Armor(g, armor, Col.Lerp(graphite, c, .35f));
                GlowLine(g, new PointF(-112, -17), new PointF(-85, -51), c, .65f);
                using (var edge = new Pen(Col.Alpha(c, 45), .65f))
                    g.DrawLines(edge, new[] { new PointF(-117, 27), new PointF(-107, 47), new PointF(-73, 78) });
                using (var mark = new Pen(Col.Alpha(c, 145), 1.4f))
                    for (int i = 0; i < 3; i++)
                        g.DrawLine(mark, 47 + i * 5, -83, 52 + i * 5, -91);
                g.Restore(saved);
            }
            using (var edge = new Pen(Col.Alpha(c, 70), .7f))
            {
                g.DrawLines(edge, new[] { new PointF(-38, -97), new PointF(-27, -105), new PointF(32, -105) });
                g.DrawLines(edge, new[] { new PointF(38, 89), new PointF(27, 97), new PointF(-32, 97) });
            }
        }

        private void DrawCompetitiveHub(Graphics g, Color c)
        {
            Halo(g, new RectangleF(-94, -65, 188, 126), c, Theme.LightMode ? 25 : 75);
        }

        private void DrawCompetitiveEye(Graphics g, double seconds, Color c, float power)
        {
            GraphicsState eyeState = g.Save();
            double poseSeconds = gameActive ? 0 : seconds;
            float open = CompetitiveEyeOpen(poseSeconds);
            using (var pose = new Matrix(1, -.24f * (1 - open), 0, open, 0, 0))
                g.MultiplyTransform(pose);
            PointF[] upperArmor = { new PointF(-85, 3), new PointF(-31, -43), new PointF(89, -53),
                new PointF(62, -25), new PointF(-25, -9), new PointF(-57, 12) };
            PointF[] lowerArmor = { new PointF(-75, 12), new PointF(-28, 45), new PointF(39, 23),
                new PointF(73, -17), new PointF(64, 12), new PointF(40, 39), new PointF(-29, 56), new PointF(-79, 23) };
            Armor(g, upperArmor, Col.Lerp(Theme.Faint, c, .35f));
            Armor(g, lowerArmor, Col.Lerp(Theme.Faint, c, .35f));
            CompetitiveFacet(g, CompetitiveCrown, Col.Lerp(c, Theme.Fg, .3f), Col.Lerp(Theme.Inset, c, .55f));
            CompetitiveFacet(g, CompetitiveBlade, c, Col.Lerp(Theme.Inset, c, .3f));
            GraphicsState pupilState = g.Save();
            using (var aperture = new GraphicsPath())
            {
                aperture.AddPolygon(new[] { new PointF(-61, 5), new PointF(-23, -17), new PointF(48, -24),
                    new PointF(57, -17), new PointF(13, 7), new PointF(-24, 21) });
                g.SetClip(aperture, CombineMode.Intersect);
                PointF gaze = CompetitiveGaze(poseSeconds);
                g.TranslateTransform(gaze.X, gaze.Y - gaze.X * .24f);
                CompetitiveFacet(g, CompetitiveIris, Col.Lerp(c, Theme.Fg, .8f), c);
                float focus = .5f + .5f * (float)Math.Sin(seconds * Math.PI / 2.5);
                Plate(g, CompetitiveIris, Col.Lerp(c, Theme.Fg, .75f), (int)((30 + focus * 90) * power));
            }
            g.Restore(pupilState);
            GlowLine(g, new PointF(-68, 0), new PointF(-26, -32), c, .6f);
            GlowLine(g, new PointF(-26, -32), new PointF(69, -41), c, .8f);
            using (var etch = new Pen(Col.Alpha(c, 105), .6f))
            {
                g.DrawLine(etch, -21, 44, 34, 28);
                g.DrawLine(etch, -46, -37, -32, -49);
                g.DrawLine(etch, -32, -49, 18, -54);
            }
            float breath = .5f + .5f * (float)Math.Sin(seconds * Math.PI / 2.5);
            using (var edge = new Pen(Col.Alpha(c, (int)((70 + 90 * breath) * power)), .85f))
                g.DrawLines(edge, new[] { new PointF(-61, 11), new PointF(-25, 32), new PointF(29, 16) });
            GraphicsState state = g.Save();
            using (var mask = new GraphicsPath())
            {
                mask.AddPolygon(CompetitiveCrown); mask.AddPolygon(CompetitiveBlade);
                g.SetClip(mask, CombineMode.Intersect);
                double phase = (seconds / 3.6) % 1;
                float x = -105 + (float)phase * 210;
                RectangleF band = new RectangleF(x - 20, -65, 40, 130);
                Color shine = Col.Lerp(c, Theme.Fg, .8f);
                using (var scan = new LinearGradientBrush(band, Color.Transparent, Color.Transparent, 0f))
                {
                    scan.InterpolationColors = new ColorBlend {
                        Colors = new[] { Col.Alpha(shine, 0), Col.Alpha(shine, (int)(100 * Math.Sin(phase * Math.PI) * power)), Col.Alpha(shine, 0) },
                        Positions = new[] { 0f, .5f, 1f }
                    };
                    g.FillRectangle(scan, band);
                }
            }
            g.Restore(state);
            g.Restore(eyeState);
        }

        private static PointF CompetitiveTrack(double phase)
        {
            double value = ((phase % 1 + 1) % 1) * 6;
            int segment = (int)value;
            float t = (float)(value - segment);
            PointF a = Polar(92, -90 + segment * 60);
            PointF b = Polar(92, -90 + (segment + 1) * 60);
            return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        private void DrawCompetitiveMotion(Graphics g, double seconds, Color c, float power, bool front)
        {
            if (!front)
            {
                GraphicsState saved = g.Save(); g.RotateTransform((float)(seconds * 7));
                for (int i = 0; i < 6; i++)
                {
                    float angle = -90 + i * 60;
                    PointF[] facet = { Polar(91, angle + 5), Polar(91, angle + 45),
                        Polar(96, angle + 42), Polar(96, angle + 10) };
                    Plate(g, facet, c, (int)(16 * power));
                    using (var line = new Pen(Col.Alpha(c, (int)(75 * power)), .75f))
                        g.DrawLine(line, facet[0], facet[1]);
                }
                using (var beam = new Pen(c, 1.5f))
                {
                    beam.StartCap = beam.EndCap = LineCap.Round;
                    for (int head = 0; head < 2; head++)
                    for (int i = 0; i < 28; i++)
                    {
                        double phase = seconds / 10 + head * .5 - i * .003;
                        beam.Color = Col.Alpha(c, (int)(210 * Math.Pow(1 - i / 28f, 2) * power));
                        g.DrawLine(beam, CompetitiveTrack(phase), CompetitiveTrack(phase - .003));
                    }
                }
                g.Restore(saved);
                return;
            }
            DrawCompetitiveEye(g, seconds, c, power);
        }
    }
}
