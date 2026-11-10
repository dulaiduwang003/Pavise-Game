using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class CaptionButton : FxControl
    {
        private readonly bool close;

        public CaptionButton(bool isClose)
        {
            close = isClose;
            base.TabStop = false;
            SetStyle(ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float hoverProgress = hover.Value;
            int widthRatio = base.Width / 2;
            int top = base.Height / 2;
            if (hoverProgress > 0.01f)
            {
                int sResult = Dpi.S(15);
                Color color = (close ? Col.Alpha(Theme.Danger, (int)(210f * hoverProgress)) : Col.Alpha(Theme.Fg, (int)(22f * hoverProgress)));
                using (SolidBrush brush = new SolidBrush(color))
                {
                    graphics.FillEllipse(brush, widthRatio - sResult, top - sResult, sResult * 2, sResult * 2);
                }
            }
            Color currentColor = (close ? Col.Lerp(Theme.Dim, Color.White, hoverProgress) : Col.Lerp(Theme.Dim, Theme.Fg, hoverProgress));
            int sResult2 = Dpi.S(5);
            using (Pen pen = new Pen(currentColor, (float)Dpi.S(1) + 0.2f))
            {
                if (close)
                {
                    graphics.DrawLine(pen, widthRatio - sResult2, top - sResult2, widthRatio + sResult2, top + sResult2);
                    graphics.DrawLine(pen, widthRatio + sResult2, top - sResult2, widthRatio - sResult2, top + sResult2);
                }
                else
                {
                    graphics.DrawLine(pen, widthRatio - sResult2, top, widthRatio + sResult2, top);
                }
            }
        }
    }
}
