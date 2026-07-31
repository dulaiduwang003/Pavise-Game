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
                else if (name == "delta")
                {
                    using (var path = new GraphicsPath(FillMode.Alternate))
                    {
                        path.AddPolygon(new[] {
                            P(x,y,u,12,2.6f), P(x,y,u,21.4f,20.2f), P(x,y,u,2.6f,20.2f)
                        });
                        path.AddPolygon(new[] {
                            P(x,y,u,12,8.6f), P(x,y,u,17.4f,18f), P(x,y,u,6.6f,18f)
                        });
                        g.FillPath(br, path);
                    }
                    using (var cut = new Pen(Theme.Nav, 1.9f * u))
                    {
                        g.DrawLine(cut, P(x, y, u, 9.8f, 8.2f), P(x, y, u, 5.6f, 10.6f));
                        g.DrawLine(cut, P(x, y, u, 14.2f, 8.2f), P(x, y, u, 18.4f, 10.6f));
                    }
                }
                else if (name == "cs2")
                {
                    if (csPath == null) csPath = ParseSvgPath(CsPathData);
                    GraphicsState st = g.Save();
                    g.TranslateTransform(x, y);
                    g.ScaleTransform(u, u);
                    g.FillPath(br, csPath);
                    g.Restore(st);
                }
            }
            g.SmoothingMode = old;
        }

        private static PointF P(float ox, float oy, float u, float px, float py) { return new PointF(ox + px * u, oy + py * u); }

        private static GraphicsPath csPath;
        private const string CsPathData = "M21.7101 3.2348a.0218.0218 0 0 1-.0216-.0215c.001-.0815.003-.3698.004-.424 0-.1292-.1428-.1831-.2117-.0829-.0113.016-.1457.212-.2287.3323a.0244.0244 0 0 1-.0198.0103h-6.5508a.0473.0473 0 0 1-.0473-.0463l-.0131-.177a.0477.0477 0 0 1 .056-.0477l.335.0319a.059.059 0 0 0 .0624-.0445l.2441-.989a.0475.0475 0 0 0-.0295-.0542l-.2268-.0848a.0427.0427 0 0 1-.0263-.0295c-.0412-.1722-.3766-1.3235-1.9935-1.581-.7867-.125-1.302.2107-1.577.4784a1.594 1.594 0 0 0-.3018.4095l-.0965.2125c-.008.0164-.046.2157-.046.234l.0513.9815a.109.109 0 0 0 .0422.0856l.3546.153-.1958.3244a.055.055 0 0 1-.053.0402s-.417.0108-.6227.0192c-.3856.0155-1.2444.4858-1.8773 1.8385-.6219 1.3282-.724 1.5496-.724 1.5496a.0736.0736 0 0 1-.0684.0398l-.5776.0019c-.0356 0-.0736.0285-.0886.0608l-.8982 2.5428c-.0149.0323-.006.081.0173.1081l.627.3918a.059.059 0 0 1 .0195.059l-.3274.9664a.1916.1916 0 0 1-.0235.0618l-.4343.3824a.1056.1056 0 0 0-.0356.059l-.5978 1.5308a.0632.0632 0 0 1-.0608.0455l-.3355.0018a.1633.1633 0 0 0-.162.1488l-.2007 2.2883a1.7194 1.7194 0 0 1-.0159.1207l-.1583.908a.128.128 0 0 1-.0333.0553l-.5584.4263c-.2559.2443-.5988.6903-.7665 1.0015l-1.86 3.9235c-.045.0833-.0811.227-.0788.3221l.1321.2354c.002.0842-.0319.4559-.0707.5307L.817 23.6545a.0985.0985 0 0 0-.003.0856l.0309.0706.0932.1859 1.8913.0029c.1173.0106.247-.1396.2507-.3005l.1027-1.2965-.0268-.1952 3.6063-4.232c.0942-.1146.222-.3168.2861-.4506l1.7186-3.7892a.1712.1712 0 0 1 .1004-.088l.1086-.035a.1694.1694 0 0 1 .1827.0528c.1505.1807.5042.781.6765 1.0315.1425.2079.8495 1.2304 1.1582 1.5675.0853.0926.3481.198.4658.2696a.083.083 0 0 1 .029.1119l-1.0298 1.8083-.4549 2.1357a.9542.9542 0 0 0-.0356.1526l-.4118 1.4831c.003.1873-.141.2856-.1527.5064l-.15 1.084a.0581.0581 0 0 0 .0582.0614l2.5445.014c.0951-.0003.1902-.0045.2854-.0067a1.1049 1.1049 0 0 0 .0755-.0069c.1242-.0158.5629-.0754.75-.1503.1097-.0388.1809-.0819.2268-.1295.1855-.1935.2002-.278.2034-.3975.001-.0212-.011-.0726-.0283-.1049-.0053-.0117-.0316-.0382-.059-.0477l-1.1815-.3562a.3693.3693 0 0 1-.1889-.1338l-.3172-.469a.0865.0865 0 0 1 .0173-.0973l.618-.609a.2017.2017 0 0 0 .0483-.072l1.9036-4.488c.089-.285.0595-.6056 0-.9445-.044-.2504-.6854-1.3264-.8532-1.6236-.1476-.262-1.0677-1.8707-1.286-2.2517-.0793-.1376-.1894-.1328-.2287-.276l-.0726-1.1173a.0398.0398 0 0 1 .0356-.0505l.3312-.0276a.093.093 0 0 0 .0741-.0487l1.147-2.1544a.0958.0958 0 0 0-.002-.094l-.2349-.2907a.0874.0874 0 0 1-.001-.0875l.3515-.3796a.054.054 0 0 1 .0736-.0206l.9343.5266a.3819.3819 0 0 0 .186.0505c.259-.0019.6858-.1549.908-.2906a.3833.3833 0 0 0 .1386-.148l.4583-1.0703c.006-.0136.0262-.0117.029.0028l.1278.5953a.0635.0635 0 0 0 .0788.0501l1.3494-.3a.0662.0662 0 0 0 .05-.0786l-.318-1.3437a.0716.0716 0 0 1 .009-.0534l.1313-.2036a.281.281 0 0 0 .036-.0814l.1589-.725a.0408.0408 0 0 1 .0397-.0323l3.7327.0047a.0916.0916 0 0 0 .0932-.0931v-.6336a.022.022 0 0 1 .0216-.0216h1.4393a.0465.0465 0 0 0 .0464-.0463v-.2855a.0465.0465 0 0 0-.0464-.0463h-1.4398z";

        private static GraphicsPath ParseSvgPath(string d)
        {
            var p = new GraphicsPath(FillMode.Winding);
            var list = new List<string>();
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(d, @"[A-Za-z]|-?\d*\.?\d+"))
                list.Add(m.Value);
            int i = 0;
            float cx = 0, cy = 0, sx = 0, sy = 0;
            char cmd = ' ';
            Func<float> N = () => float.Parse(list[i++], System.Globalization.CultureInfo.InvariantCulture);
            while (i < list.Count)
            {
                string t = list[i];
                if (t.Length == 1 && char.IsLetter(t[0]))
                {
                    cmd = t[0]; i++;
                    if (cmd == 'Z' || cmd == 'z') { p.CloseFigure(); cx = sx; cy = sy; continue; }
                }
                bool rel = char.IsLower(cmd);
                char c = char.ToUpperInvariant(cmd);
                if (c == 'M')
                {
                    float nx = N(), ny = N();
                    if (rel) { nx += cx; ny += cy; }
                    p.StartFigure(); cx = sx = nx; cy = sy = ny;
                    cmd = rel ? 'l' : 'L';
                }
                else if (c == 'L')
                {
                    float nx = N(), ny = N();
                    if (rel) { nx += cx; ny += cy; }
                    p.AddLine(cx, cy, nx, ny); cx = nx; cy = ny;
                }
                else if (c == 'H') { float nx = N(); if (rel) nx += cx; p.AddLine(cx, cy, nx, cy); cx = nx; }
                else if (c == 'V') { float ny = N(); if (rel) ny += cy; p.AddLine(cx, cy, cx, ny); cy = ny; }
                else if (c == 'C')
                {
                    float x1 = N(), y1 = N(), x2 = N(), y2 = N(), nx = N(), ny = N();
                    if (rel) { x1 += cx; y1 += cy; x2 += cx; y2 += cy; nx += cx; ny += cy; }
                    p.AddBezier(cx, cy, x1, y1, x2, y2, nx, ny); cx = nx; cy = ny;
                }
                else if (c == 'S')
                {
                    float x2 = N(), y2 = N(), nx = N(), ny = N();
                    if (rel) { x2 += cx; y2 += cy; nx += cx; ny += cy; }
                    p.AddBezier(cx, cy, x2, y2, x2, y2, nx, ny); cx = nx; cy = ny;
                }
                else if (c == 'A')
                {
                    N(); N(); N(); N(); N();
                    float nx = N(), ny = N();
                    if (rel) { nx += cx; ny += cy; }
                    p.AddLine(cx, cy, nx, ny); cx = nx; cy = ny;
                }
                else break;
            }
            return p;
        }

        private static void Knob(Graphics g, SolidBrush fill, Color c, PointF center, float r)
        {
            using (var bg = new SolidBrush(Theme.Nav)) g.FillEllipse(bg, center.X - r, center.Y - r, r * 2, r * 2);
            using (var pen = new Pen(c, Math.Max(1.4f, r * 0.7f))) g.DrawEllipse(pen, center.X - r, center.Y - r, r * 2, r * 2);
        }
    }

}
