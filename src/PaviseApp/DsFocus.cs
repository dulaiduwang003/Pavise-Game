using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class DsFocus
	{
		public static bool Wants(bool focused, bool showCues, bool interactive)
		{
			if (focused && showCues)
			{
				return interactive;
			}
			return false;
		}

		public static Rectangle Inset(Rectangle r, int logicalInset)
		{
			int num = Theme.S(logicalInset);
			if (r.Width <= num * 2 + 2 || r.Height <= num * 2 + 2)
			{
				return Rectangle.Empty;
			}
			return Rectangle.Inflate(r, -num, -num);
		}

		public static void Draw(Graphics g, Rectangle r, Color ink, Color back)
		{
			if (r.Width > 2 && r.Height > 2)
			{
				ControlPaint.DrawFocusRectangle(g, r, ink, back);
			}
		}
	}
}
