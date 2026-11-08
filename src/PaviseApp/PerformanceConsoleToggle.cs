using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class PerformanceConsoleToggle
	{
		internal static void Paint(Graphics g, Toggle toggle, float blend, float hover)
		{
			DsPaint.FillBackground(g, toggle, Theme.Card);
			if (toggle.Width < 2 || toggle.Height < 2)
			{
				return;
			}
			g.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle r = new Rectangle(1, 1, toggle.Width - 3, toggle.Height - 3);
			Color b = Col.Lerp(Theme.Accent2, Theme.Accent, 0.28f);
			Color a = Col.Lerp(Theme.Inset, b, blend * (toggle.Enabled ? 0.92f : 0.52f));
			a = Col.Lerp(a, Theme.CardHover, hover * 0.12f);
			Color color = ((!toggle.Enabled) ? Theme.Stroke : Col.Lerp(Theme.StrokeHi, Theme.Accent, blend));
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(7)))
			{
				using (SolidBrush brush = new SolidBrush(a))
				{
					using (Pen pen = new Pen(color))
					{
						g.FillPath(brush, path);
						g.DrawPath(pen, path);
					}
				}
			}
			int sLimit = Math.Min(Theme.S(18), r.Height - Theme.S(10));
			if (sLimit > 0)
			{
				int x = (toggle.Checked ? (r.Right - sLimit - Theme.S(8)) : (r.Left + Theme.S(8)));
				Rectangle r2 = new Rectangle(x, (toggle.Height - sLimit) / 2, sLimit, sLimit);
				using (GraphicsPath path2 = Theme.TechPath(r2, Theme.S(3)))
				{
					using (SolidBrush brush2 = new SolidBrush((!toggle.Enabled) ? Theme.Faint : (toggle.Checked ? Color.White : Theme.Dim)))
					{
						g.FillPath(brush2, path2);
					}
				}
			}
			Rectangle bounds = (toggle.Checked ? new Rectangle(Theme.S(8), 0, toggle.Width - Theme.S(40), toggle.Height) : new Rectangle(Theme.S(32), 0, toggle.Width - Theme.S(38), toggle.Height));
			Color foreColor = ((!toggle.Enabled) ? Theme.Dim : (toggle.Checked ? Color.White : Theme.Dim));
			TextRenderer.DrawText(g, toggle.Checked ? "ON" : "OFF", Theme.UI(9.5f, true), bounds, foreColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}
	}
}
