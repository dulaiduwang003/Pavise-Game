using System;
using System.Drawing;

namespace PaviseApp
{
	internal static class DsPopup
	{
		public static Rectangle Place(Rectangle anchor, Size size, Rectangle work, int gap, int edgeMargin, bool alignRight)
		{
			if (size.Width < 1)
			{
				size.Width = 1;
			}
			if (size.Height < 1)
			{
				size.Height = 1;
			}
			edgeMargin = Math.Max(0, Math.Min(edgeMargin, Math.Max(0, (work.Width - 1) / 2)));
			size.Width = Math.Min(size.Width, Math.Max(1, work.Width - edgeMargin * 2));
			size.Height = Math.Min(size.Height, Math.Max(1, work.Height));
			int val = (alignRight ? (anchor.Right - size.Width) : anchor.Left);
			val = Math.Max(work.Left + edgeMargin, Math.Min(val, work.Right - size.Width - edgeMargin));
			int num = anchor.Bottom + gap;
			if (num + size.Height > work.Bottom)
			{
				num = anchor.Top - size.Height - gap;
			}
			num = Math.Max(work.Top, Math.Min(num, work.Bottom - size.Height));
			return new Rectangle(val, num, size.Width, size.Height);
		}

		public static Size Fit(Size size, Rectangle work, int margin, int minWidth)
		{
			int val = Math.Max(1, work.Width - Math.Max(0, margin));
			int val2 = Math.Max(1, work.Height - Math.Max(0, margin));
			return new Size(Math.Max(1, Math.Min(size.Width, val)), Math.Max(1, Math.Min(size.Height, val2)));
		}
	}
}
