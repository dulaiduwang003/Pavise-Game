using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class OverviewGuardSwitch
	{
		internal static Rectangle FrameBounds(Control control)
		{
			return new Rectangle(1, 1, Math.Max(1, control.Width - 3), Math.Max(1, control.Height - 3));
		}

		internal static Rectangle ThumbBounds(Control control, float progress)
		{
			Rectangle rectangle = FrameBounds(control);
			int minLimit = Math.Max(1, Math.Min(Theme.S(16), rectangle.Height - Theme.S(10)));
			int leftOffset = rectangle.Left + Theme.S(7);
			int remainingS = rectangle.Right - minLimit - Theme.S(7);
			int x = leftOffset + (int)Math.Round(Math.Max(0f, Math.Min(1f, progress)) * (float)Math.Max(0, remainingS - leftOffset));
			return new Rectangle(x, (control.Height - minLimit) / 2, minLimit, minLimit);
		}

		internal static Rectangle LabelBounds(Toggle control)
		{
			if (!control.Checked)
			{
				return new Rectangle(Theme.S(30), 0, Math.Max(1, control.Width - Theme.S(36)), control.Height);
			}
			return new Rectangle(Theme.S(6), 0, Math.Max(1, control.Width - Theme.S(35)), control.Height);
		}

		internal static void Paint(Graphics graphics, Toggle control, float progress, float hover)
		{
			if (control.Width < 2 || control.Height < 2)
			{
				return;
			}
			GraphicsState gstate = graphics.Save();
			try
			{
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				float minLimit = Math.Max(0f, Math.Min(1f, progress));
				Rectangle rectangle = FrameBounds(control);
				Color b = Col.Lerp(Theme.Accent2, Theme.Accent, 0.38f);
				Color a = Col.Lerp(Theme.Inset, b, minLimit * 0.9f);
				a = Col.Lerp(a, Theme.CardHover, Math.Max(0f, Math.Min(1f, hover)) * 0.14f);
				Color color = Col.Lerp(Theme.StrokeHi, Theme.Accent, minLimit);
				using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(6)))
				{
					using (LinearGradientBrush brush = new LinearGradientBrush(rectangle, Col.Lerp(a, Theme.Fg, 0.025f), a, LinearGradientMode.Vertical))
					{
						graphics.FillPath(brush, path);
					}
					using (Pen pen = new Pen(color))
					{
						graphics.DrawPath(pen, path);
					}
				}
				Rectangle r = ThumbBounds(control, minLimit);
				using (GraphicsPath path2 = Theme.TechPath(r, Theme.S(3)))
				{
					using (SolidBrush brush2 = new SolidBrush(Col.Lerp(Theme.Dim, Color.White, minLimit)))
					{
						graphics.FillPath(brush2, path2);
					}
				}
				Color foreColor = ((!control.Enabled) ? Theme.Faint : (control.Checked ? Color.White : Theme.Dim));
				TextRenderer.DrawText(graphics, control.Checked ? "ON" : "OFF", Theme.UI(8.5f, true), LabelBounds(control), foreColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			finally
			{
				graphics.Restore(gstate);
			}
		}
	}
}
