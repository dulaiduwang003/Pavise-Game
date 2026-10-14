using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class PaviseCore
    {
        private void DrawExtremeStructure(Graphics g, Color c)
        {
            Halo(g, new RectangleF(-120, -120, 240, 240), c, Theme.LightMode ? 28 : 85);
            for (int i = 0; i < 3; i++)
            {
                GraphicsState s = g.Save(); g.RotateTransform(i * 120 - 30);
                using (var p = new Pen(Col.Alpha(c, 48), .65f))
                    g.DrawEllipse(p, -112, -52, 224, 104);
                using (var p = new Pen(Col.Alpha(c, 105), .8f))
                {
                    g.DrawArc(p, -117, -57, 234, 114, 190, 65);
                    g.DrawArc(p, -117, -57, 234, 114, 10, 65);
                    g.DrawLines(p, new[] { new PointF(-13,-119), new PointF(13,-119), new PointF(25,-107) });
                }
                GlowLine(g, new PointF(-13,-119), new PointF(8,-119), c, .8f);
                for (int j = 0; j < 5; j++)
                    using (var p = new Pen(Col.Alpha(c, 100 - j * 14), .8f))
                        g.DrawLine(p, -27 + j * 3, -106, -24 + j * 3, -110);
                g.Restore(s);
            }
            using (var p = new Pen(Col.Alpha(c, 50), .6f))
            {
                g.DrawLine(p, -130, 0, -102, 0); g.DrawLine(p, 102, 0, 130, 0);
                g.DrawLine(p, 0, -130, 0, -113);
            }
        }

        private void DrawExtremeMotion(Graphics g, double seconds, Color c, float power, bool front)
        {
            Color white = Col.Lerp(c, Theme.LightMode ? Theme.Fg : Color.White, .8f);
            for (int i = 0; i < 3; i++)
            {
                GraphicsState s = g.Save(); g.RotateTransform(i * 120 - 30);
                DrawOrbitFlow(g, 112, 52, seconds * 65 + i * 120, c, power, front);
                g.Restore(s);
            }
            if (!front) return;
            float pulse = .55f + .45f * (float)Math.Sin(seconds * Math.PI);
            GlowLine(g, new PointF(0,-34), new PointF(0,29), white, (.45f + pulse * .45f) * power);
            for (int i = 0; i < 3; i++)
            {
                double phase = (seconds / 2.4 + i / 3.0) % 1;
                float strength = (float)Math.Pow(Math.Sin(phase * Math.PI), 6) * power;
                PointF a = Polar(42, i * 120 - 90), b = Polar(56, i * 120 - 79);
                PointF d = Polar(70, i * 120 - 90);
                GlowLine(g, a, b, c, strength * .65f);
                GlowLine(g, b, d, white, strength * .55f);
            }
        }

        private void DrawExtremeHub(Graphics g, Color c)
        {
            Halo(g, new RectangleF(-54,-63,108,126), c, 110);
            PointF top = new PointF(0,-39), left = new PointF(-25,-7);
            PointF right = new PointF(25,-7), bottom = new PointF(0,34), mid = new PointF(0,3);
            Color bright = Col.Lerp(c, Theme.LightMode ? Theme.Fg : Color.White, .68f);
            Plate(g, new[] { top,left,mid }, c, 95);
            Plate(g, new[] { top,right,mid }, bright, 65);
            Plate(g, new[] { left,bottom,mid }, c, 45);
            Plate(g, new[] { right,bottom,mid }, c, 110);
            Edge(g, new[] { top,right,bottom,left }, bright, 225, 1);
            GlowLine(g, top, mid, bright, .9f);
            using (var p = new Pen(Col.Alpha(c, 190), .8f))
            { g.DrawLine(p,left,mid); g.DrawLine(p,right,mid); g.DrawLine(p,mid,bottom); }
            for (int side = -1; side <= 1; side += 2)
            {
                PointF[] bracket = { new PointF(side*13,-47),new PointF(side*35,-16),
                    new PointF(side*35,10),new PointF(side*19,31) };
                using (var p = new Pen(Col.Alpha(c, 130), .8f)) g.DrawLines(p,bracket);
            }
        }
    }
}
