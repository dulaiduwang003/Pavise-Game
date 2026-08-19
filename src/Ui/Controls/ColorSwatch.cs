// @author bdth 2074055628@qq.com
// 文件用途 设置页模式主题色的可点色块 选中画高亮环 「默认」块中心画空心圈以区分
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ColorSwatch : Control
    {
        public Color Swatch;
        public bool Selected;
        public bool IsDefault;
        public Action Picked;

        public ColorSwatch(Color c)
        {
            Swatch = c;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Picked != null) Picked();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int pad = Theme.S(2);
            var box = new Rectangle(pad, pad, Width - pad * 2 - 1, Height - pad * 2 - 1);
            int rad = Theme.S(6);

            using (GraphicsPath path = Theme.TechPath(box, rad))
            {
                using (var b = new SolidBrush(Swatch)) g.FillPath(b, path);
                using (var pen = new Pen(Col.Alpha(Color.Black, 60))) g.DrawPath(pen, path);
            }

            if (IsDefault)
            {
                int d = Theme.S(7);
                var dot = new Rectangle(box.X + (box.Width - d) / 2, box.Y + (box.Height - d) / 2, d, d);
                double luma = 0.299 * Swatch.R + 0.587 * Swatch.G + 0.114 * Swatch.B;
                using (var pen = new Pen(luma > 150 ? Color.FromArgb(40, 40, 40) : Color.White, Math.Max(1f, Theme.S(1))))
                    g.DrawEllipse(pen, dot);
            }

            if (Selected)
            {
                var ring = new Rectangle(0, 0, Width - 1, Height - 1);
                using (GraphicsPath rp = Theme.TechPath(ring, rad + Theme.S(2)))
                using (var pen = new Pen(Theme.Accent, Math.Max(1.5f, Theme.S(2))))
                    g.DrawPath(pen, rp);
            }
        }
    }
}
