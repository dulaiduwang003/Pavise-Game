// @author bdth 2074055628@qq.com
// 文件用途 为导航栏与主工作区绘制统一的低对比 ROG 装甲底纹
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class RogSurface
    {
        public static void Draw(Graphics g, Rectangle bounds, bool navigation)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState state = g.Save();
            g.SetClip(bounds);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            int meshAlpha = navigation
                ? (Theme.LightMode ? 13 : 8)
                : (Theme.LightMode ? 8 : 5);
            int faceAlpha = navigation
                ? (Theme.LightMode ? 14 : 9)
                : (Theme.LightMode ? 8 : 5);
            int edgeAlpha = navigation
                ? (Theme.LightMode ? 44 : 30)
                : (Theme.LightMode ? 25 : 17);

            DrawEtchedMesh(g, bounds, meshAlpha, navigation);

            int sweep = navigation ? bounds.Width / 2 : bounds.Width / 5;
            int band = navigation ? Theme.S(15) : Theme.S(20);
            DrawArmorBand(g, bounds, -sweep, bounds.Height * 14 / 100,
                bounds.Right + sweep, bounds.Height * 38 / 100, band, faceAlpha, edgeAlpha);
            DrawArmorBand(g, bounds, -sweep, bounds.Height * 43 / 100,
                bounds.Right + sweep, bounds.Height * 67 / 100, band, faceAlpha, edgeAlpha);
            DrawArmorBand(g, bounds, -sweep, bounds.Height * 72 / 100,
                bounds.Right + sweep, bounds.Height * 96 / 100, band, faceAlpha, edgeAlpha);

            DrawCircuitBus(g, bounds, navigation, edgeAlpha);
            DrawCornerCuts(g, bounds, navigation, edgeAlpha);
            g.Restore(state);
        }

        private static void DrawEtchedMesh(Graphics g, Rectangle r, int alpha, bool navigation)
        {
            int step = Theme.S(navigation ? 17 : 22);
            int travel = r.Height + r.Width;
            using (var primary = new Pen(Col.Alpha(Theme.StrokeHi, alpha), Math.Max(1f, Theme.S(1))))
                for (int x = r.Left - travel; x < r.Right + travel; x += step)
                    g.DrawLine(primary, x, r.Top, x + r.Height, r.Bottom);

            int crossAlpha = Math.Max(2, alpha / 2);
            using (var cross = new Pen(Col.Alpha(Theme.Accent, crossAlpha), Math.Max(1f, Theme.S(1))))
                for (int x = r.Left; x < r.Right + r.Height; x += step * 4)
                    g.DrawLine(cross, x, r.Top, x - r.Height, r.Bottom);
        }

        private static void DrawArmorBand(Graphics g, Rectangle clip, int x1, int y1, int x2, int y2,
            int thickness, int faceAlpha, int edgeAlpha)
        {
            double dx = x2 - x1, dy = y2 - y1;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1) return;
            int px = (int)Math.Round(-dy / length * thickness);
            int py = (int)Math.Round(dx / length * thickness);
            Point[] face = {
                new Point(x1 + px, y1 + py), new Point(x2 + px, y2 + py),
                new Point(x2 - px, y2 - py), new Point(x1 - px, y1 - py)
            };

            Rectangle fillBounds = new Rectangle(clip.Left, Math.Min(y1, y2) - thickness,
                Math.Max(1, clip.Width), Math.Max(1, Math.Abs(y2 - y1) + thickness * 2));
            using (var fill = new LinearGradientBrush(fillBounds,
                Col.Alpha(Theme.Accent, Math.Max(1, faceAlpha / 3)),
                Col.Alpha(Theme.Accent, faceAlpha), LinearGradientMode.Horizontal))
                g.FillPolygon(fill, face);

            using (var edge = new Pen(Col.Alpha(Theme.Accent, edgeAlpha), Math.Max(1f, Theme.S(1))))
            using (var steel = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(4, edgeAlpha - 10)), Math.Max(1f, Theme.S(1))))
            {
                g.DrawLine(edge, face[0], face[1]);
                g.DrawLine(steel, face[3], face[2]);
            }

            using (var inner = new Pen(Col.Alpha(Theme.Accent, Math.Max(4, edgeAlpha / 2)), Math.Max(1f, Theme.S(1))))
            {
                int insetX = px / 3, insetY = py / 3;
                g.DrawLine(inner, x1 + insetX, y1 + insetY, x2 + insetX, y2 + insetY);
            }
        }

        private static void DrawCircuitBus(Graphics g, Rectangle r, bool navigation, int edgeAlpha)
        {
            int busX = r.Right - Theme.S(navigation ? 17 : 24);
            int top = r.Top + Theme.S(navigation ? 96 : 24);
            int bottom = r.Bottom - Theme.S(34);
            using (var steel = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(8, edgeAlpha - 7)), Math.Max(1f, Theme.S(1))))
            using (var live = new Pen(Col.Alpha(Theme.Accent, edgeAlpha), Math.Max(1f, Theme.S(1))))
            {
                g.DrawLine(steel, busX, top, busX, bottom);
                g.DrawLine(live, busX + Theme.S(2), top + Theme.S(18), busX + Theme.S(2), bottom - Theme.S(26));

                int[] levels = navigation
                    ? new[] { 15, 35, 57, 79 }
                    : new[] { 18, 48, 78 };
                for (int i = 0; i < levels.Length; i++)
                {
                    int y = r.Top + r.Height * levels[i] / 100;
                    int reach = Theme.S(navigation ? (i % 2 == 0 ? 56 : 82) : (i == 1 ? 146 : 92));
                    int elbow = busX - reach;
                    g.DrawLine(steel, busX, y, elbow + Theme.S(18), y);
                    g.DrawLine(live, elbow + Theme.S(18), y, elbow, y - Theme.S(18));
                    g.DrawLine(live, elbow, y - Theme.S(18), elbow - Theme.S(24), y - Theme.S(18));
                    using (var node = new SolidBrush(Col.Alpha(Theme.Accent, edgeAlpha + 28)))
                    {
                        g.FillRectangle(node, elbow - Theme.S(2), y - Theme.S(20), Theme.S(4), Theme.S(4));
                        g.FillRectangle(node, elbow - Theme.S(28), y - Theme.S(20), Theme.S(4), Theme.S(4));
                    }
                }
            }

            using (var tick = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(6, edgeAlpha - 12)), Math.Max(1f, Theme.S(1))))
                for (int y = top; y < bottom; y += Theme.S(navigation ? 25 : 31))
                    g.DrawLine(tick, busX - Theme.S(8), y, busX, y);
        }

        private static void DrawCornerCuts(Graphics g, Rectangle r, bool navigation, int edgeAlpha)
        {
            using (var pen = new Pen(Col.Alpha(Theme.Accent, Math.Max(8, edgeAlpha - 5)), Math.Max(1f, Theme.S(1))))
            {
                int cut = Theme.S(navigation ? 28 : 42);
                int reach = Theme.S(navigation ? 66 : 104);
                g.DrawLine(pen, r.Left + Theme.S(10), r.Top + cut, r.Left + reach, r.Top + cut);
                g.DrawLine(pen, r.Left + reach, r.Top + cut, r.Left + reach + cut, r.Top);
                g.DrawLine(pen, r.Right - Theme.S(10), r.Bottom - cut, r.Right - reach, r.Bottom - cut);
                g.DrawLine(pen, r.Right - reach, r.Bottom - cut, r.Right - reach - cut, r.Bottom);
            }

            if (navigation || r.Height >= Theme.S(120))
            {
                string code = navigation ? "PVS // NAV SURFACE" : "PAVISE // CONTROL SURFACE";
                TextRenderer.DrawText(g, code, Theme.Mono(5.4f),
                    new Rectangle(r.Right - Theme.S(navigation ? 142 : 220), r.Bottom - Theme.S(24),
                        Theme.S(navigation ? 116 : 192), Theme.S(13)),
                    Col.Alpha(Theme.Faint, Theme.LightMode ? 66 : 42),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }
    }

    internal sealed class WorkspacePanel : DBPanel
    {
        public WorkspacePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var fill = new SolidBrush(Theme.Bg)) e.Graphics.FillRectangle(fill, ClientRectangle);
            RogSurface.Draw(e.Graphics, ClientRectangle, false);
        }
    }
}
