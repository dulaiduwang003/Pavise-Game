using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class PaviseCore
    {
        private float DesignScale { get { return Math.Min(Width, Height) * .00312f; } }

        private GraphicsState BeginDesign(Graphics g)
        {
            GraphicsState state = g.Save();
            g.TranslateTransform(Width * .5f, Height * .44f);
            g.ScaleTransform(DesignScale, DesignScale);
            return state;
        }

        private static PointF Polar(float radius, double degrees)
        {
            double a = degrees * Math.PI / 180;
            return new PointF(radius * (float)Math.Cos(a), radius * (float)Math.Sin(a));
        }

        private static PointF[] Capsule(float x, float y, float w, float h, float cut)
        {
            return new[] { new PointF(x + cut, y), new PointF(x + w - cut, y),
                new PointF(x + w, y + cut), new PointF(x + w, y + h - cut),
                new PointF(x + w - cut, y + h), new PointF(x + cut, y + h),
                new PointF(x, y + h - cut), new PointF(x, y + cut) };
        }

        private static void Edge(Graphics g, PointF[] points, Color color, int alpha, float width)
        { using (var p = new Pen(Col.Alpha(color, alpha), width)) g.DrawPolygon(p, points); }

        private static void Plate(Graphics g, PointF[] points, Color color, int alpha)
        { using (var b = new SolidBrush(Col.Alpha(color, alpha))) g.FillPolygon(b, points); }

        private static void Halo(Graphics g, RectangleF bounds, Color color, int alpha)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(bounds);
                using (var fill = new PathGradientBrush(path))
                {
                    fill.CenterColor = Col.Alpha(color, alpha);
                    fill.SurroundColors = new[] { Col.Alpha(color, 0) };
                    g.FillPath(fill, path);
                }
            }
        }

        private static void Armor(Graphics g, PointF[] points, Color color)
        {
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(points);
                RectangleF bounds = path.GetBounds();
                GraphicsState shadow = g.Save(); g.TranslateTransform(0, 3);
                using (var brush = new SolidBrush(Col.Alpha(Color.Black, Theme.LightMode ? 15 : 110))) g.FillPath(brush, path);
                g.Restore(shadow);
                using (var fill = new LinearGradientBrush(bounds,
                    Col.Lerp(Theme.Card, color, Theme.LightMode ? .06f : .16f), Theme.Inset, 100f))
                    g.FillPath(fill, path);
                using (var edge = new Pen(Col.Alpha(color, 95), .65f)) g.DrawPath(edge, path);
                using (var highlight = new Pen(Col.Alpha(Col.Lerp(color, Theme.Fg, .55f), 155), .6f))
                    g.DrawLines(highlight, new[] { points[points.Length - 1], points[0], points[1] });
            }
        }

        private static void GlowLine(Graphics g, PointF a, PointF b, Color color, float strength)
        {
            using (var glow = new Pen(Col.Alpha(color, (int)(25 * strength)), 5)) g.DrawLine(glow, a, b);
            using (var line = new Pen(Col.Alpha(color, (int)(230 * strength)), 1.25f)) g.DrawLine(line, a, b);
        }

        private void DrawModeStructure(Graphics g, Color c)
        {
            if (mode == PerformancePreset.Extreme) { DrawExtremeStructure(g,c); return; }
            if (mode == PerformancePreset.Handheld)
            {
                Halo(g, new RectangleF(-130, -130, 260, 260), c, Theme.LightMode ? 20 : 65);
                GraphicsState saved = g.Save(); g.RotateTransform(-22);
                using (var p = new Pen(Col.Alpha(c, 55), .8f)) g.DrawEllipse(p, -117, -70, 234, 140);
                using (var p = new Pen(Col.Alpha(c, 120), 2))
                { g.DrawArc(p, -122, -75, 244, 150, 203, 44); g.DrawArc(p, -122, -75, 244, 150, 23, 44); }
                g.Restore(saved);
                Halo(g, new RectangleF(-88, 53, 176, 39), c, 65);
                for (int i = 0; i < 3; i++)
                    using (var p = new Pen(Col.Alpha(c, 70 - i * 18), .8f))
                        g.DrawEllipse(p, -77 - i * 7, 67 - i * 5, 154 + i * 14, 20 + i * 10);
                for (int side = -1; side <= 1; side += 2)
                {
                    saved = g.Save(); g.ScaleTransform(side, 1);
                    PointF[] dock = { new PointF(93,-33), new PointF(105,-23), new PointF(105,22), new PointF(94,33), new PointF(89,26), new PointF(94,19), new PointF(94,-20) };
                    Armor(g, dock, c); GlowLine(g,new PointF(105,-16),new PointF(105,16),c,.8f);
                    g.Restore(saved);
                }
            }
            else DrawCompetitiveStructure(g, c);
        }

        private void DrawModeMotion(Graphics g, double seconds, Color c)
        {
            float power = guardEnabled ? 1f : .38f;
            if (mode == PerformancePreset.Extreme) { DrawExtremeMotion(g,seconds,c,power,false); return; }
            if (mode == PerformancePreset.Handheld)
            {
                GraphicsState saved=g.Save();g.RotateTransform(-22);
                DrawOrbitFlow(g, 117, 70, seconds * 45, c, power, false);
                g.Restore(saved);
                for(int i=0;i<12;i++)
                {
                    float phase=(float)((seconds/3+i/12.0)%1);
                    float x=-60+i*11;
                    using(var pen=new Pen(Col.Alpha(c,(int)(65*Math.Sin(phase*Math.PI)*power)),.8f))
                        g.DrawLine(pen,x,72-phase*80,x,65-phase*80);
                }
                float breath=.5f+.5f*(float)Math.Sin(seconds*Math.PI/3);
                using(var p=new Pen(Col.Alpha(c,(int)((80+90*breath)*power)),1.6f))
                    g.DrawArc(p,-79,64,158,25,15,145);
            }
            else DrawCompetitiveMotion(g, seconds, c, power, false);
        }

        private void DrawFrontLight(Graphics g, double seconds, Color c)
        {
            float power = guardEnabled ? 1f : .38f;
            if (mode == PerformancePreset.Extreme) { DrawExtremeMotion(g,seconds,c,power,true); return; }
            if (mode == PerformancePreset.Handheld)
            {
                GraphicsState saved = g.Save(); g.RotateTransform(-22);
                using (var p = new Pen(Col.Alpha(c, (int)(80 * power)), .8f))
                    g.DrawArc(p, -117, -70, 234, 140, 0, 180);
                DrawOrbitFlow(g, 117, 70, seconds * 45, c, power, true);
                g.Restore(saved);
            }
            else DrawCompetitiveMotion(g, seconds, c, power, true);
        }

        private void DrawModeHub(Graphics g, Color a, Color b)
        {
            if (mode == PerformancePreset.Extreme) { DrawExtremeHub(g,a); return; }
            if (mode == PerformancePreset.Handheld)
            {
                Armor(g, Capsule(-80,-39,160,78,16),a);
                Armor(g, Capsule(-44,-31,88,62,7),a);
                using (var screen = new LinearGradientBrush(new Rectangle(-37,-25,74,50),Col.Lerp(Theme.Inset,a,.25f),Theme.Inset,60f))
                    g.FillRectangle(screen,-37,-25,74,50);
                Halo(g,new RectangleF(-38,-29,76,58),a,70);
                GlowLine(g,new PointF(-68,-35),new PointF(60,-35),a,.85f);
                using (var pen = new Pen(Col.Lerp(a,Theme.Fg,.6f),2.3f))
                { g.DrawLine(pen,-69,-6,-53,-6); g.DrawLine(pen,-61,-14,-61,2); }
                using(var fill = new SolidBrush(a))
                { g.FillEllipse(fill,57,-15,4,4); g.FillEllipse(fill,66,-6,4,4); g.FillEllipse(fill,57,3,4,4); g.FillEllipse(fill,48,-6,4,4); }
                using(var pen = new Pen(Col.Alpha(a,110),1.2f))
                { g.DrawEllipse(pen,-62,12,10,10); g.DrawEllipse(pen,53,15,8,8); }
                PointF[] bolt = {new PointF(5,-20),new PointF(-16,4),new PointF(-2,4),new PointF(-6,22),new PointF(17,-5),new PointF(3,-5)};
                Plate(g,bolt,Col.Lerp(a,Theme.Fg,.25f),255);
            }
            else DrawCompetitiveHub(g, a);
        }
    }
}
