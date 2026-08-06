// @author bdth 2074055628@qq.com
// 文件用途 绘制界面使用的矢量图形符号

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class Glyphs
    {
        public static void Draw(Graphics g, string name, Rectangle b, Color c)
        {
            float u = b.Width / 24f;
            var old = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(c, Math.Max(1.4f, 1.9f * u)))
            using (var br = new SolidBrush(c))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                float x = b.X, y = b.Y;
                if (name == "game")
                {
                    PointF[] bolt = {
                        P(x,y,u,13,2), P(x,y,u,6,13.5f), P(x,y,u,11,13.5f), P(x,y,u,10,22),
                        P(x,y,u,17,10.5f), P(x,y,u,12,10.5f)
                    };
                    g.FillPolygon(br, bolt);
                }
                else if (name == "lol")
                {
                    using (var ring = new Pen(c, Math.Max(1.2f, 1.45f * u)))
                        g.DrawArc(ring, x + 3.5f * u, y + 3.5f * u, 17f * u, 17f * u, -68f, 272f);
                    PointF[] blade = {
                        P(x,y,u,10.5f,2.5f), P(x,y,u,14.5f,2.5f), P(x,y,u,13.4f,16.8f),
                        P(x,y,u,19.4f,16.8f), P(x,y,u,17.5f,21.2f), P(x,y,u,8.8f,21.2f)
                    };
                    g.FillPolygon(br, blade);
                }
                else if (name == "shield")
                {
                    using (var path = new GraphicsPath())
                    {
                        path.AddLines(new[] {
                            P(x,y,u,12,2.5f), P(x,y,u,20,5.5f), P(x,y,u,20,12),
                            P(x,y,u,12,21.5f), P(x,y,u,4,12), P(x,y,u,4,5.5f)
                        });
                        path.CloseFigure();
                        g.DrawPath(pen, path);
                    }
                }
                else if (name == "white")
                {
                    var rr = new RectangleF(x + 3.5f * u, y + 3.5f * u, 17 * u, 17 * u);
                    using (var path = Theme.Rounded(Rectangle.Round(rr), (int)(4 * u))) g.DrawPath(pen, path);
                    g.DrawLines(pen, new[] { P(x, y, u, 8.5f, 12), P(x, y, u, 11, 15), P(x, y, u, 16, 8.5f) });
                }
                else if (name == "log")
                {
                    g.DrawLine(pen, P(x, y, u, 5, 7).X, P(x, y, u, 5, 7).Y, P(x, y, u, 19, 7).X, P(x, y, u, 19, 7).Y);
                    g.DrawLine(pen, P(x, y, u, 5, 12).X, P(x, y, u, 5, 12).Y, P(x, y, u, 19, 12).X, P(x, y, u, 19, 12).Y);
                    g.DrawLine(pen, P(x, y, u, 5, 17).X, P(x, y, u, 5, 17).Y, P(x, y, u, 14, 17).X, P(x, y, u, 14, 17).Y);
                }
                else if (name == "chart")
                {
                    g.DrawLine(pen, P(x,y,u,4,19), P(x,y,u,4,5));
                    g.DrawLine(pen, P(x,y,u,4,19), P(x,y,u,20,19));
                    g.DrawLines(pen, new[] { P(x,y,u,6,15), P(x,y,u,10,11), P(x,y,u,13,13), P(x,y,u,19,6) });
                }
                else if (name == "settings")
                {
                    g.DrawLine(pen, P(x, y, u, 4, 8).X, P(x, y, u, 4, 8).Y, P(x, y, u, 20, 8).X, P(x, y, u, 20, 8).Y);
                    g.DrawLine(pen, P(x, y, u, 4, 16).X, P(x, y, u, 4, 16).Y, P(x, y, u, 20, 16).X, P(x, y, u, 20, 16).Y);
                    Knob(g, br, c, P(x, y, u, 9, 8), 2.6f * u);
                    Knob(g, br, c, P(x, y, u, 15, 16), 2.6f * u);
                }
                else if (name == "gear")
                {
                    g.DrawEllipse(pen, x + 5.5f * u, y + 5.5f * u, 13 * u, 13 * u);
                    g.DrawEllipse(pen, x + 9.5f * u, y + 9.5f * u, 5 * u, 5 * u);
                    for (int i = 0; i < 8; i++)
                    {
                        double a = i * Math.PI / 4.0;
                        float x1 = x + (12f + (float)Math.Cos(a) * 8f) * u;
                        float y1 = y + (12f + (float)Math.Sin(a) * 8f) * u;
                        float x2 = x + (12f + (float)Math.Cos(a) * 10f) * u;
                        float y2 = y + (12f + (float)Math.Sin(a) * 10f) * u;
                        g.DrawLine(pen, x1, y1, x2, y2);
                    }
                }
                else if (name == "gpu")
                {
                    var board = new RectangleF(x + 2.5f * u, y + 6.5f * u, 19 * u, 11 * u);
                    using (var path = Theme.Rounded(Rectangle.Round(board), (int)(2.5f * u))) g.DrawPath(pen, path);
                    using (var fan = new Pen(c, Math.Max(1.2f, 1.5f * u)))
                        g.DrawEllipse(fan, x + 5.2f * u, y + 8.7f * u, 6.6f * u, 6.6f * u);
                    using (var vent = new Pen(c, Math.Max(1f, 1.3f * u)))
                        for (int i = 0; i < 3; i++)
                        {
                            float vy = y + (9.6f + i * 2.4f) * u;
                            g.DrawLine(vent, x + 14.6f * u, vy, x + 18.6f * u, vy);
                        }
                }
                else if (name == "chip")
                {
                    var die = new RectangleF(x + 6.5f * u, y + 6.5f * u, 11 * u, 11 * u);
                    using (var path = Theme.Rounded(Rectangle.Round(die), (int)(2f * u))) g.DrawPath(pen, path);
                    g.FillRectangle(br, x + 10.6f * u, y + 10.6f * u, 2.8f * u, 2.8f * u);
                    using (var leg = new Pen(c, Math.Max(1f, 1.4f * u)))
                        for (int i = 0; i < 3; i++)
                        {
                            float t = (9.4f + i * 2.6f) * u;
                            g.DrawLine(leg, x + t, y + 6.5f * u, x + t, y + 3.4f * u);
                            g.DrawLine(leg, x + t, y + 17.5f * u, x + t, y + 20.6f * u);
                            g.DrawLine(leg, x + 6.5f * u, y + t, x + 3.4f * u, y + t);
                            g.DrawLine(leg, x + 17.5f * u, y + t, x + 20.6f * u, y + t);
                        }
                }
                else if (name == "info")
                {
                    g.DrawEllipse(pen, x + 3.5f * u, y + 3.5f * u, 17 * u, 17 * u);
                    g.DrawLine(pen, P(x, y, u, 12, 11.2f), P(x, y, u, 12, 16.8f));
                    g.FillEllipse(br, x + 10.6f * u, y + 6.2f * u, 2.8f * u, 2.8f * u);
                }
                else if (name == "sun")
                {
                    using (var thin = new Pen(c, Math.Max(1.1f, 1.55f * u)))
                    {
                        thin.StartCap = LineCap.Round; thin.EndCap = LineCap.Round;
                        g.DrawEllipse(thin, x + 7.6f * u, y + 7.6f * u, 8.8f * u, 8.8f * u);
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4.0;
                            float ca = (float)Math.Cos(a), sa = (float)Math.Sin(a);
                            g.DrawLine(thin,
                                x + (12f + ca * 7.6f) * u, y + (12f + sa * 7.6f) * u,
                                x + (12f + ca * 10.4f) * u, y + (12f + sa * 10.4f) * u);
                        }
                    }
                }
                else if (name == "moon")
                {
                    using (var thin = new Pen(c, Math.Max(1.1f, 1.55f * u)))
                    using (var path = Crescent(x + 12f * u, y + 12f * u, 8.6f * u, 7.4f * u, -38f))
                    {
                        thin.LineJoin = LineJoin.Round;
                        g.DrawPath(thin, path);
                    }
                }
            }
            g.SmoothingMode = old;
        }

        private static GraphicsPath Crescent(float cx, float cy, float r, float d, float tilt)
        {
            if (d > r * 1.94f) d = r * 1.94f;
            if (d < r * 0.2f) d = r * 0.2f;
            float half = (float)(Math.Acos(d / (2.0 * r)) * 180.0 / Math.PI);
            double rad = tilt * Math.PI / 180.0;
            float bx = cx + d * (float)Math.Cos(rad), by = cy + d * (float)Math.Sin(rad);
            var p = new GraphicsPath();
            p.AddArc(cx - r, cy - r, r * 2f, r * 2f, tilt + half, 360f - 2f * half);
            p.AddArc(bx - r, by - r, r * 2f, r * 2f, 180f + tilt + half, -2f * half);
            p.CloseFigure();
            RectangleF bb = p.GetBounds();
            using (var m = new Matrix())
            {
                m.Translate(cx - (bb.Left + bb.Right) / 2f, cy - (bb.Top + bb.Bottom) / 2f);
                p.Transform(m);
            }
            return p;
        }

        private static PointF P(float ox, float oy, float u, float px, float py) { return new PointF(ox + px * u, oy + py * u); }

        private static void Knob(Graphics g, SolidBrush fill, Color c, PointF center, float r)
        {
            using (var bg = new SolidBrush(Theme.Nav)) g.FillEllipse(bg, center.X - r, center.Y - r, r * 2, r * 2);
            using (var pen = new Pen(c, Math.Max(1.4f, r * 0.7f))) g.DrawEllipse(pen, center.X - r, center.Y - r, r * 2, r * 2);
        }
    }

}
