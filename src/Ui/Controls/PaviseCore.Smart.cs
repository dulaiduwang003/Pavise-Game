using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace PaviseApp
{
    internal sealed partial class PaviseCore
    {
        private struct SmartPoint
        {
            internal float X, Y, Z;
            internal PointF Screen
            {
                get { float perspective = 340f / (340f - Z); return new PointF(X * perspective, Y * perspective); }
            }
        }

        private static SmartPoint SmartTransform(float x, float y, float z, double turn, double tilt, double roll)
        {
            double a = x * Math.Cos(turn) + z * Math.Sin(turn);
            double b = -x * Math.Sin(turn) + z * Math.Cos(turn);
            double c = y * Math.Cos(tilt) - b * Math.Sin(tilt);
            double depth = y * Math.Sin(tilt) + b * Math.Cos(tilt);
            return new SmartPoint { X = (float)(a * Math.Cos(roll) - c * Math.Sin(roll)),
                Y = (float)(a * Math.Sin(roll) + c * Math.Cos(roll)), Z = (float)depth };
        }

        private static void DrawSmartStructure(Graphics g, Color c)
        {
            Halo(g, new RectangleF(-139, -136, 278, 265), c, Theme.LightMode ? 27 : 60);
            Halo(g, new RectangleF(-93, 68, 186, 35), c, Theme.LightMode ? 22 : 58);
            for (int i = 0; i < 3; i++)
                using (var pen = new Pen(Col.Alpha(c, 72 - i * 19), .7f))
                    g.DrawEllipse(pen, -72 - 9 * i, 78 - i * 4, 144 + i * 18, 16 + i * 8);
            for (int side = -1; side <= 1; side += 2)
            {
                GraphicsState saved = g.Save(); g.ScaleTransform(side, 1);
                PointF[] wing = { new PointF(83,-63), new PointF(108,-43), new PointF(119,-5),
                    new PointF(108,38), new PointF(89,54), new PointF(97,28),
                    new PointF(103,-4), new PointF(95,-36) };
                Armor(g, wing, c);
                GlowLine(g, new PointF(110,-31), new PointF(117,-7), c, 1.2f);
                using (var p = new Pen(Col.Alpha(c, 100), .7f))
                {
                    g.DrawLines(p, new[] { new PointF(73,-77), new PointF(101,-57), new PointF(111,-32) });
                    g.DrawLine(p, 101, 51, 84, 65);
                    for (int i = 0; i < 5; i++) g.DrawLine(p, 125, -18 + i * 8, 128 + (i == 2 ? 4 : 0), -18 + i * 8);
                }
                g.Restore(saved);
            }
            using (var pen = new Pen(Col.Alpha(c, 36), .6f))
            {
                g.DrawLine(pen, 0,-107,0,-83);
                g.DrawLine(pen,-8,-97,8,-97);
            }
        }

        private void DrawSmartMotion(Graphics g, double time, Color c)
        {
            float power = guardEnabled ? 1f : .4f;
            double breath = .5 + .5 * Math.Sin(time * 1.6);
            float hover = (float)Math.Sin(time * 1.05) * 3;
            for (int i = 0; i < 3; i++)
            {
                double phase = time * .22 + i / 3.0; phase -= Math.Floor(phase);
                float radius = 26 + (float)phase * 60;
                using (var p = new Pen(Col.Alpha(c, (int)(Math.Sin(phase * Math.PI) * 60 * power)), .8f))
                    g.DrawEllipse(p, -radius, 86 - radius * .12f, radius * 2, radius * .24f);
            }
            GraphicsState saved = g.Save(); g.TranslateTransform(0, hover - 6);
            SmartOrbit(g, time, c, power, false, 0);
            SmartOrbit(g, time, c, power, false, 1);
            Halo(g, new RectangleF(-60,-68,120,136), c, (int)((42 + breath * 17) * power));
            DrawSmartCrystal(g, time, c, power);
            SmartOrbit(g, time, c, power, true, 0);
            SmartOrbit(g, time, c, power, true, 1);
            g.Restore(saved);
        }

        private static SmartPoint SmartOrbitPoint(double angle, double time, int orbit)
        {
            float radius = orbit == 0 ? 101 : 115;
            return SmartTransform(radius * (float)Math.Cos(angle), radius * (float)Math.Sin(angle), 0,
                0, orbit == 0 ? .91 : 1.13,
                (orbit == 0 ? -.48 : .69) + Math.Sin(time * .19 + orbit) * .09);
        }

        private static void SmartOrbit(Graphics g, double time, Color c, float power, bool front, int orbit)
        {
            Color ink = orbit == 0 ? c : Col.Lerp(c, Theme.LightMode ? Theme.Fg : Color.White, .28f);
            const int segments = 96;
            double head = time * (orbit == 0 ? .52 : -.38) + orbit * 2;
            using (var line = new Pen(c, .85f))
            {
                SmartPoint previous = SmartOrbitPoint(0, time, orbit);
                for (int i = 1; i <= segments; i++)
                {
                    double a = i * Math.PI * 2 / segments;
                    SmartPoint next = SmartOrbitPoint(a, time, orbit);
                    if ((next.Z + previous.Z >= 0) == front)
                    {
                        double trail = .5 + .5 * Math.Cos(a - head);
                        int alpha = (int)((front ? 65 : 24) * power + Math.Pow(trail, 18) * (front ? 145 : 60) * power);
                        line.Color = Col.Alpha(ink, alpha);
                        g.DrawLine(line, previous.Screen, next.Screen);
                    }
                    previous = next;
                }
            }
            for (int i = 0; i < 3; i++)
            {
                SmartPoint node = SmartOrbitPoint(head + i * Math.PI * 2 / 3, time, orbit);
                float blend = Math.Max(0, Math.Min(1, (node.Z + 9) / 18));
                float opacity = power * (front ? blend : 1 - blend);
                PointF at = node.Screen;
                Halo(g, new RectangleF(at.X-7,at.Y-7,14,14), ink, (int)(90 * opacity));
                using (var p = new Pen(Col.Alpha(ink, (int)(210 * opacity)), .8f))
                    g.DrawPolygon(p, new[] { new PointF(at.X,at.Y-3), new PointF(at.X+3,at.Y),
                        new PointF(at.X,at.Y+3), new PointF(at.X-3,at.Y) });
                using (var fill = new SolidBrush(Col.Alpha(Theme.LightMode ? c : Color.White, (int)(235 * opacity))))
                    g.FillEllipse(fill, at.X-1, at.Y-1, 2, 2);
            }
        }

        private static void DrawSmartCrystal(Graphics g, double time, Color c, float power)
        {
            double turn = time * .23 + .45, tilt = .20 + Math.Sin(time * .41) * .12;
            double roll = -.13 + Math.Sin(time * .28) * .07;
            SmartPoint[] points = {
                SmartTransform(0,-62,0,turn,tilt,roll), SmartTransform(0,62,0,turn,tilt,roll),
                SmartTransform(43,0,0,turn,tilt,roll), SmartTransform(0,0,43,turn,tilt,roll),
                SmartTransform(-43,0,0,turn,tilt,roll), SmartTransform(0,0,-43,turn,tilt,roll)
            };
            int[] faces = { 0,1,2,3,4,5,6,7 };
            float[] depth = new float[8];
            for (int i=0;i<8;i++) depth[i]=(points[i/4].Z + points[2+i%4].Z + points[2+(i+1)%4].Z)/3;
            Array.Sort(faces, delegate(int a, int b) { return depth[a].CompareTo(depth[b]); });
            foreach (int face in faces)
            {
                PointF[] triangle = { points[face/4].Screen, points[2+face%4].Screen, points[2+(face+1)%4].Screen };
                float light = Math.Max(.08f, Math.Min(.88f, .25f + depth[face] / 100f + (face%4 == 1 ? .17f : 0)));
                Color dark = Col.Lerp(Theme.Inset, c, light * power);
                Color bright = Col.Lerp(Theme.Card, c, (light + .17f) * power);
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(triangle);
                    using (var fill = new LinearGradientBrush(new RectangleF(-66,-78,132,156), bright, dark, 62f))
                        g.FillPath(fill, path);
                }
                float facing = Math.Max(0, Math.Min(1, (depth[face] + 8) / 16));
                using (var pen = new Pen(Col.Alpha(c, (int)((65 + facing * 135) * power)), .85f))
                    g.DrawPolygon(pen, triangle);
            }
            for (int i=0;i<4;i++)
            {
                SmartPoint a = points[2+i];
                if (a.Z < 0) continue;
                float visibility = Math.Min(1, a.Z / 8);
                for (int end=0;end<2;end++)
                {
                    PointF start = points[end].Screen, finish = a.Screen;
                    using (var fine = new Pen(Col.Alpha(c, (int)(65 * power * visibility)), .55f))
                        g.DrawLine(fine, start.X*.76f, start.Y*.76f, finish.X*.76f, finish.Y*.76f);
                    double phase = time * .25 + i*.25 + end*.5; phase -= Math.Floor(phase);
                    float t = (float)phase;
                    PointF at = new PointF(start.X+(finish.X-start.X)*t, start.Y+(finish.Y-start.Y)*t);
                    float alpha = (float)Math.Sin(phase*Math.PI) * power * visibility;
                    Halo(g, new RectangleF(at.X-5,at.Y-5,10,10), c, (int)(130*alpha));
                    using (var spark = new SolidBrush(Col.Alpha(Theme.LightMode ? c : Color.White,(int)(230*alpha))))
                        g.FillEllipse(spark, at.X-1,at.Y-1,2,2);
                }
            }
            float pulse = .5f + .5f*(float)Math.Sin(time*1.6);
            Halo(g, new RectangleF(-25,-30,50,60), c, (int)((95+25*pulse)*power));
            PointF[] seed = { new PointF(0,-16),new PointF(9,0),new PointF(0,16),new PointF(-9,0) };
            Plate(g,seed,Col.Lerp(c,Color.White,.65f),(int)(225*power));
            Edge(g,seed,Theme.LightMode ? c : Color.White,(int)(240*power),.8f);
        }
    }
}
