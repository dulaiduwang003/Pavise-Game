using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class ColorSwatch : Control
	{
		public Color Swatch;

		public bool Selected;

		public bool IsDefault;

		public Action Picked;

		public ColorSwatch(Color c)
		{
			Swatch = c;
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Color.Transparent;
			Cursor = Cursors.Hand;
		}

		protected override void OnClick(EventArgs e)
		{
			base.OnClick(e);
			if (Picked != null)
			{
				Picked();
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			int scaledDimension = Theme.S(2);
			Rectangle r = new Rectangle(scaledDimension, scaledDimension, base.Width - scaledDimension * 2 - 1, base.Height - scaledDimension * 2 - 1);
			int scaledDimension2 = Theme.S(6);
			using (GraphicsPath path = Theme.TechPath(r, scaledDimension2))
			{
				using (SolidBrush brush = new SolidBrush(Swatch))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Alpha(Color.Black, 60)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			if (IsDefault)
			{
				int scaledDimension3 = Theme.S(7);
				Rectangle rect = new Rectangle(r.X + (r.Width - scaledDimension3) / 2, r.Y + (r.Height - scaledDimension3) / 2, scaledDimension3, scaledDimension3);
				double measuredValue = 0.299 * (double)(int)Swatch.R + 0.587 * (double)(int)Swatch.G + 0.114 * (double)(int)Swatch.B;
				using (Pen pen2 = new Pen((measuredValue > 150.0) ? Color.FromArgb(40, 40, 40) : Color.White, Math.Max(1f, Theme.S(1))))
				{
					graphics.DrawEllipse(pen2, rect);
				}
			}
			if (!Selected)
			{
				return;
			}
			Rectangle r2 = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			using (GraphicsPath path2 = Theme.TechPath(r2, scaledDimension2 + Theme.S(2)))
			{
				using (Pen pen3 = new Pen(Theme.Accent, Math.Max(1.5f, Theme.S(2))))
				{
					graphics.DrawPath(pen3, path2);
				}
			}
		}
	}
}
