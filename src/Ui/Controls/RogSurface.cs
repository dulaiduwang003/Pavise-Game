// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // ROG 表面：参数全部来自 DsSurfaces.Profile，导航面与控制面共用同一绘制流程。
    internal static class RogSurface
    {
        public static void Draw(Graphics g, Rectangle bounds, bool navigation)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            DsSurfaceProfile profile = DsSurfaces.Profile(navigation);
            SmoothingMode smoothing = g.SmoothingMode;
            PixelOffsetMode pixels = g.PixelOffsetMode;
            using (Region clip = g.Clip)
            {
                try
                {
                    g.SetClip(bounds, CombineMode.Intersect);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    DrawEtchedMesh(g, bounds, profile);

                    int sweep = bounds.Width / profile.SweepDivisor;
                    int band = Theme.S(profile.BandThickness);
                    DrawArmorBand(g, bounds, -sweep, bounds.Height * 14 / 100,
                        bounds.Right + sweep, bounds.Height * 38 / 100, band, profile.FaceAlpha, profile.EdgeAlpha);
                    DrawArmorBand(g, bounds, -sweep, bounds.Height * 43 / 100,
                        bounds.Right + sweep, bounds.Height * 67 / 100, band, profile.FaceAlpha, profile.EdgeAlpha);
                    DrawArmorBand(g, bounds, -sweep, bounds.Height * 72 / 100,
                        bounds.Right + sweep, bounds.Height * 96 / 100, band, profile.FaceAlpha, profile.EdgeAlpha);

                    DrawCircuitBus(g, bounds, profile);
                    DrawCornerCuts(g, bounds, profile);
                }
                finally
                {
                    g.SetClip(clip, CombineMode.Replace);
                    g.SmoothingMode = smoothing;
                    g.PixelOffsetMode = pixels;
                }
            }
        }

        private static void DrawEtchedMesh(Graphics g, Rectangle r, DsSurfaceProfile p)
        {
            int step = Math.Max(1, Theme.S(p.MeshStep));
            int travel = r.Height + r.Width;
            using (var primary = new Pen(Col.Alpha(Theme.StrokeHi, p.MeshAlpha), DsSpace.Line(1)))
                for (int x = r.Left - travel; x < r.Right + travel; x += step)
                    g.DrawLine(primary, x, r.Top, x + r.Height, r.Bottom);

            int crossAlpha = Math.Max(2, p.MeshAlpha / 2);
            using (var cross = new Pen(Col.Alpha(Theme.Accent, crossAlpha), DsSpace.Line(1)))
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

            using (var edge = new Pen(Col.Alpha(Theme.Accent, edgeAlpha), DsSpace.Line(1)))
            using (var steel = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(4, edgeAlpha - 10)), DsSpace.Line(1)))
            {
                g.DrawLine(edge, face[0], face[1]);
                g.DrawLine(steel, face[3], face[2]);
            }

            using (var inner = new Pen(Col.Alpha(Theme.Accent, Math.Max(4, edgeAlpha / 2)), DsSpace.Line(1)))
            {
                int insetX = px / 3, insetY = py / 3;
                g.DrawLine(inner, x1 + insetX, y1 + insetY, x2 + insetX, y2 + insetY);
            }
        }

        private static void DrawCircuitBus(Graphics g, Rectangle r, DsSurfaceProfile p)
        {
            int busX = r.Right - Theme.S(p.BusInset);
            int top = r.Top + Theme.S(p.BusTop);
            int bottom = r.Bottom - Theme.S(p.BusBottom);
            int edgeAlpha = p.EdgeAlpha;
            using (var steel = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(8, edgeAlpha - 7)), DsSpace.Line(1)))
            using (var live = new Pen(Col.Alpha(Theme.Accent, edgeAlpha), DsSpace.Line(1)))
            {
                g.DrawLine(steel, busX, top, busX, bottom);
                g.DrawLine(live, busX + Theme.S(2), top + Theme.S(18), busX + Theme.S(2), bottom - Theme.S(26));

                for (int i = 0; i < p.BusLevels.Length; i++)
                {
                    int y = r.Top + r.Height * p.BusLevels[i] / 100;
                    int reach = Theme.S(p.BusReach[i]);
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

            int tickStep = Math.Max(1, Theme.S(p.TickStep));
            using (var tick = new Pen(Col.Alpha(Theme.StrokeHi, Math.Max(6, edgeAlpha - 12)), DsSpace.Line(1)))
                for (int y = top; y < bottom; y += tickStep)
                    g.DrawLine(tick, busX - Theme.S(8), y, busX, y);
        }

        private static void DrawCornerCuts(Graphics g, Rectangle r, DsSurfaceProfile p)
        {
            using (var pen = new Pen(Col.Alpha(Theme.Accent, Math.Max(8, p.EdgeAlpha - 5)), DsSpace.Line(1)))
            {
                int cut = Theme.S(p.CornerCut);
                int reach = Theme.S(p.CornerReach);
                g.DrawLine(pen, r.Left + Theme.S(10), r.Top + cut, r.Left + reach, r.Top + cut);
                g.DrawLine(pen, r.Left + reach, r.Top + cut, r.Left + reach + cut, r.Top);
                g.DrawLine(pen, r.Right - Theme.S(10), r.Bottom - cut, r.Right - reach, r.Bottom - cut);
                g.DrawLine(pen, r.Right - reach, r.Bottom - cut, r.Right - reach - cut, r.Bottom);
            }

            if (p.CodeMinHeight == 0 || r.Height >= Theme.S(p.CodeMinHeight))
            {
                TextRenderer.DrawText(g, p.Code, DsType.Mono(DsMono.Surface),
                    new Rectangle(r.Right - Theme.S(p.CodeRight), r.Bottom - Theme.S(24),
                        Theme.S(p.CodeWidth), Theme.S(13)),
                    Col.Alpha(Theme.Faint, DsSurfaces.CodeAlpha),
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding
                        | TextFormatFlags.PreserveGraphicsClipping | TextFormatFlags.PreserveGraphicsTranslateTransform);
            }
        }
    }

    internal class WorkspacePanel : DBPanel
    {
        public WorkspacePanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintSurface(e.Graphics, e.ClipRectangle);
        }

        internal void PaintSurface(Graphics graphics, Rectangle clip)
        {
            if (Backdrop.AppliesTo(this)) { Backdrop.Paint(graphics, this, clip); return; }
            using (var fill = new SolidBrush(Theme.Bg)) graphics.FillRectangle(fill, ClientRectangle);
            RogSurface.Draw(graphics, ClientRectangle, false);
        }
    }
}
