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
			int num = base.Width / 2;
			int num2 = base.Height / 2;
			if (value > 0.01f)
			{
				int num3 = Dpi.S(15);
				Color color = (close ? Col.Alpha(Theme.Danger, (int)(210f * value)) : Col.Alpha(Theme.Fg, (int)(22f * value)));
				using (SolidBrush brush = new SolidBrush(color))
				{
					graphics.FillEllipse(brush, num - num3, num2 - num3, num3 * 2, num3 * 2);
				}
			}
			Color color2 = (close ? Col.Lerp(Theme.Dim, Color.White, value) : Col.Lerp(Theme.Dim, Theme.Fg, value));
			int num4 = Dpi.S(5);
			using (Pen pen = new Pen(color2, (float)Dpi.S(1) + 0.2f))
			{
				if (close)
				{
					graphics.DrawLine(pen, num - num4, num2 - num4, num + num4, num2 + num4);
					graphics.DrawLine(pen, num + num4, num2 - num4, num - num4, num2 + num4);
				}
				else
				{
					graphics.DrawLine(pen, num - num4, num2, num + num4, num2);
				}
			}
		}
	}
}
