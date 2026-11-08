using System;
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
			float value = hover.Value;
			int widthRatio = base.Width / 2;
			int top = base.Height / 2;
			if (value > 0.01f)
			{
				int sResult = Dpi.S(15);
				Color color = (close ? Col.Alpha(Theme.Danger, (int)(210f * value)) : Col.Alpha(Theme.Fg, (int)(22f * value)));
				using (SolidBrush brush = new SolidBrush(color))
				{
					graphics.FillEllipse(brush, widthRatio - sResult, top - sResult, sResult * 2, sResult * 2);
				}
			}
			Color color2 = (close ? Col.Lerp(Theme.Dim, Color.White, value) : Col.Lerp(Theme.Dim, Theme.Fg, value));
			int sResult2 = Dpi.S(5);
			using (Pen pen = new Pen(color2, (float)Dpi.S(1) + 0.2f))
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
