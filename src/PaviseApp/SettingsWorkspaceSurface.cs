using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class SettingsWorkspaceSurface : DBPanel
	{
		internal SettingsWorkspaceSurface() { Tag = "backdrop-card"; }
		internal int DivisionX;

		internal int DivisionTop;

		internal int DivisionBottom;

		internal int HeaderLine;

		internal int FrameTop;

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (Backdrop.AppliesTo(this)) Backdrop.Paint(e.Graphics, this, e.ClipRectangle);
			else e.Graphics.Clear(FrameTop > 0 ? Theme.Bg : Theme.Card);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle r = new Rectangle(1, FrameTop + 1, base.Width - 3, base.Height - FrameTop - 3);
			if (r.Width <= 0 || r.Height <= 0)
			{
				return;
			}
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(12)))
			{
				using (SolidBrush brush = new SolidBrush(Backdrop.CardFill(this, Theme.Card)))
				{
					using (Pen pen = new Pen((FrameTop > 0) ? Theme.Stroke : Col.Alpha(Theme.Accent, 165)))
					{
						graphics.FillPath(brush, path);
						graphics.DrawPath(pen, path);
					}
				}
			}
			if (FrameTop > 0)
			{
				using (Pen pen2 = new Pen(Theme.Accent))
				{
					int scaledDimension = Theme.S(12);
					graphics.DrawLine(pen2, r.Left, r.Top + scaledDimension, r.Left + scaledDimension, r.Top);
					graphics.DrawLine(pen2, r.Right - scaledDimension, r.Bottom, r.Right, r.Bottom - scaledDimension);
				}
			}
			using (Pen pen3 = new Pen(Theme.Stroke))
			{
				if (HeaderLine > 0)
				{
					graphics.DrawLine(pen3, Theme.S(20), HeaderLine, base.Width - Theme.S(20), HeaderLine);
				}
				if (DivisionX > 0 && DivisionBottom > DivisionTop)
				{
					graphics.DrawLine(pen3, DivisionX, DivisionTop, DivisionX, DivisionBottom);
				}
			}
		}
	}
}
