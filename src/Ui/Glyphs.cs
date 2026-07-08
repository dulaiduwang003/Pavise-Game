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

namespace AegisApp
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
                else if (name == "info")
                {
                    g.DrawEllipse(pen, x + 3.5f * u, y + 3.5f * u, 17 * u, 17 * u);
                    g.DrawLine(pen, P(x, y, u, 12, 11.2f), P(x, y, u, 12, 16.8f));
                    g.FillEllipse(br, x + 10.6f * u, y + 6.2f * u, 2.8f * u, 2.8f * u);
                }
            }
            g.SmoothingMode = old;
        }

        private static PointF P(float ox, float oy, float u, float px, float py) { return new PointF(ox + px * u, oy + py * u); }

        private static void Knob(Graphics g, SolidBrush fill, Color c, PointF center, float r)
        {
            using (var bg = new SolidBrush(Theme.Nav)) g.FillEllipse(bg, center.X - r, center.Y - r, r * 2, r * 2);
            using (var pen = new Pen(c, Math.Max(1.4f, r * 0.7f))) g.DrawEllipse(pen, center.X - r, center.Y - r, r * 2, r * 2);
        }
    }



}
