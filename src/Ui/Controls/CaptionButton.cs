// @author bdth 2074055628@qq.com
// 文件用途 提供窗口标题栏按钮控件

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class CaptionButton : FxControl
    {
        private readonly bool close;
        public CaptionButton(bool isClose) { close = isClose; TabStop = false; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Value;
            int cx = Width / 2, cy = Height / 2;

            if (h > 0.01f)
            {
                int rr = Dpi.S(15);
                Color hi = close ? Col.Alpha(Theme.Danger, (int)(210 * h)) : Col.Alpha(Color.White, (int)(22 * h));
                using (var b = new SolidBrush(hi)) g.FillEllipse(b, cx - rr, cy - rr, rr * 2, rr * 2);
            }

            Color c = close ? Col.Lerp(Theme.Dim, Color.White, h) : Col.Lerp(Theme.Dim, Theme.Fg, h);
            int d = Dpi.S(5);
            using (var pen = new Pen(c, Dpi.S(1) + 0.2f))
            {
                if (close) { g.DrawLine(pen, cx - d, cy - d, cx + d, cy + d); g.DrawLine(pen, cx + d, cy - d, cx - d, cy + d); }
                else g.DrawLine(pen, cx - d, cy, cx + d, cy);
            }
        }
    }

}
