// @author bdth 2074055628@qq.com
// 文件用途 绘制关于页的全宽品牌主舱与低饱和战术纹理
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AboutHeroPanel : RoundPanel
    {
        public AboutHeroPanel()
        {
            AccentEdge = true;
            Radius = Theme.S(14);
            Fill = Theme.Card;
            Border = Theme.StrokeHi;
            BackColor = Theme.Bg;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath clip = Theme.TechPath(bounds, Theme.S(8)))
            {
                g.SetClip(clip);
                DrawGrid(g);
                DrawArmorPlanes(g);
                g.ResetClip();
            }
        }

        private void DrawGrid(Graphics g)
        {
            int grid = Math.Max(Theme.S(18), 12);
            int left = Math.Max(Width / 2, Theme.S(470));
            using (var pen = new Pen(Col.Alpha(Theme.StrokeHi, Theme.LightMode ? 24 : 18)))
            {
                for (int x = left; x < Width; x += grid) g.DrawLine(pen, x, 0, x, Height);
                for (int y = 0; y < Height; y += grid) g.DrawLine(pen, left, y, Width, y);
            }
        }

        private void DrawArmorPlanes(Graphics g)
        {
            int split = Width - Theme.S(360);
            Point[] broad = {
                new Point(split, 0), new Point(Width, 0), new Point(Width, Height),
                new Point(split + Theme.S(132), Height), new Point(split + Theme.S(58), Height / 2)
            };
            using (var fill = new LinearGradientBrush(
                new Rectangle(split, 0, Math.Max(1, Width - split), Height),
                Col.Alpha(Theme.Accent, Theme.LightMode ? 5 : 3),
                Col.Alpha(Theme.Accent, Theme.LightMode ? 22 : 14),
                LinearGradientMode.ForwardDiagonal))
                g.FillPolygon(fill, broad);

            using (var pen = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 38 : 42)))
            {
                g.DrawLine(pen, split + Theme.S(38), 0, split + Theme.S(118), Height);
                g.DrawLine(pen, split + Theme.S(118), 0, split + Theme.S(198), Height);
                g.DrawLine(pen, Width - Theme.S(16), Theme.S(34), Width - Theme.S(16), Height - Theme.S(34));
            }

            int nodeY = Height - Theme.S(28);
            using (var rail = new Pen(Col.Alpha(Theme.Accent, 100)))
                g.DrawLine(rail, Width - Theme.S(238), nodeY, Width - Theme.S(32), nodeY);
            using (var node = new SolidBrush(Theme.Accent))
            {
                g.FillRectangle(node, Width - Theme.S(240), nodeY - Theme.S(2), Theme.S(5), Theme.S(5));
                g.FillRectangle(node, Width - Theme.S(34), nodeY - Theme.S(2), Theme.S(5), Theme.S(5));
            }
        }

    }
}
