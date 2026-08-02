// @author bdth 2074055628@qq.com
// 文件用途 未上线专栏的占位控件 以高度模糊的界面预览与开发中徽章展示

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ComingSoonPreview : Control
    {
        private readonly Color accent;
        private readonly string title;
        private readonly string badge;
        private Bitmap preview;

        public ComingSoonPreview(string title, string badge, Color accent)
        {
            this.title = title;
            this.badge = badge;
            this.accent = accent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            TabStop = false;
        }

        private Bitmap BuildPreview()
        {
            var bmp = new Bitmap(96, 64);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.Clear(Theme.Bg);
                using (var deck = new SolidBrush(Color.FromArgb(70, accent)))
                    g.FillRectangle(deck, 4, 4, 88, 14);
                using (var hot = new SolidBrush(Color.FromArgb(160, accent)))
                    g.FillRectangle(hot, 6, 6, 30, 5);
                using (var card = new SolidBrush(Color.FromArgb(36, 40, 48)))
                {
                    g.FillRectangle(card, 4, 22, 42, 12);
                    g.FillRectangle(card, 50, 22, 42, 12);
                    for (int i = 0; i < 3; i++) g.FillRectangle(card, 4 + i * 30, 38, 26, 9);
                    g.FillRectangle(card, 4, 51, 88, 9);
                }
                using (var dot = new SolidBrush(Color.FromArgb(200, accent)))
                {
                    g.FillEllipse(dot, 40, 24, 4, 4);
                    g.FillEllipse(dot, 86, 24, 4, 4);
                }
            }
            var tiny = new Bitmap(24, 16);
            using (Graphics t = Graphics.FromImage(tiny))
            {
                t.InterpolationMode = InterpolationMode.HighQualityBilinear;
                t.DrawImage(bmp, new Rectangle(0, 0, 24, 16));
            }
            bmp.Dispose();
            return tiny;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (preview == null) preview = BuildPreview();
            Graphics g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(preview, ClientRectangle);
            using (var shade = new SolidBrush(Color.FromArgb(150, 10, 11, 14)))
                g.FillRectangle(shade, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Font big = Theme.UI(17f, true);
            Font small = Theme.UI(9.5f, false);
            SizeF ts = g.MeasureString(title, big);
            SizeF bs = g.MeasureString(badge, small);
            float cy = Height / 2f - Theme.S(26);
            using (var fg = new SolidBrush(Theme.Fg))
                g.DrawString(title, big, fg, (Width - ts.Width) / 2f, cy);
            float pillW = bs.Width + Theme.S(28);
            float pillH = bs.Height + Theme.S(10);
            float px = (Width - pillW) / 2f;
            float py = cy + ts.Height + Theme.S(14);
            using (GraphicsPath path = Rounded(px, py, pillW, pillH, pillH / 2f))
            {
                using (var fill = new SolidBrush(Color.FromArgb(46, accent))) g.FillPath(fill, path);
                using (var pen = new Pen(Color.FromArgb(190, accent))) g.DrawPath(pen, path);
            }
            using (var fg = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                g.DrawString(badge, small, fg, px + Theme.S(14), py + Theme.S(5));
        }

        private static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            p.AddArc(x, y, r * 2, r * 2, 180, 90);
            p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
            p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
            p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && preview != null) { preview.Dispose(); preview = null; }
            base.Dispose(disposing);
        }
    }
}
