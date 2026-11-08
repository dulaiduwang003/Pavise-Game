using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class WhitelistSearchSurface : RoundPanel
	{
		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen = new Pen(Theme.Dim, Math.Max(1.2f, Theme.S(1))))
			{
				int left = Theme.S(14);
				int top = Theme.S(11);
				int width = Theme.S(12);
				e.Graphics.DrawEllipse(pen, left, top, width, width);
				e.Graphics.DrawLine(pen, left + width - Theme.S(1), top + width - Theme.S(1), left + width + Theme.S(5), top + width + Theme.S(5));
			}
		}
	}
}
