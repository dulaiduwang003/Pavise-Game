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
				int num = Theme.S(14);
				int num2 = Theme.S(11);
				int num3 = Theme.S(12);
				e.Graphics.DrawEllipse(pen, num, num2, num3, num3);
				e.Graphics.DrawLine(pen, num + num3 - Theme.S(1), num2 + num3 - Theme.S(1), num + num3 + Theme.S(5), num2 + num3 + Theme.S(5));
			}
		}
	}
}
