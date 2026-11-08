using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class AboutHeroPanel : RoundPanel
	{
		public AboutHeroPanel()
		{
			AccentEdge = true;
			Radius = Theme.S(14);
			Fill = Theme.Card;
			Border = Theme.StrokeHi;
			BackColor = Theme.Bg;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			Rectangle r = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			using (GraphicsPath clip = Theme.TechPath(r, Theme.S(8)))
			{
				graphics.SetClip(clip);
				DrawGrid(graphics);
				DrawArmorPlanes(graphics);
				graphics.ResetClip();
			}
		}

		private void DrawGrid(Graphics g)
		{
			int sLimit = Math.Max(Theme.S(18), 12);
			int left = Math.Max(base.Width / 2, Theme.S(470));
			using (Pen pen = new Pen(Col.Alpha(Theme.StrokeHi, Theme.LightMode ? 24 : 18)))
			{
				for (int i = left; i < base.Width; i += sLimit)
				{
					g.DrawLine(pen, i, 0, i, base.Height);
				}
				for (int j = 0; j < base.Height; j += sLimit)
				{
					g.DrawLine(pen, left, j, base.Width, j);
				}
			}
		}

		private void DrawArmorPlanes(Graphics g)
		{
			int remainingWidth = base.Width - Theme.S(360);
			Point[] points = new Point[5]
			{
				new Point(remainingWidth, 0),
				new Point(base.Width, 0),
				new Point(base.Width, base.Height),
				new Point(remainingWidth + Theme.S(132), base.Height),
				new Point(remainingWidth + Theme.S(58), base.Height / 2)
			};
			using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(remainingWidth, 0, Math.Max(1, base.Width - remainingWidth), base.Height), Col.Alpha(Theme.Accent, Theme.LightMode ? 5 : 3), Col.Alpha(Theme.Accent, Theme.LightMode ? 22 : 14), LinearGradientMode.ForwardDiagonal))
			{
				g.FillPolygon(brush, points);
			}
			using (Pen pen = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 38 : 42)))
			{
				g.DrawLine(pen, remainingWidth + Theme.S(38), 0, remainingWidth + Theme.S(118), base.Height);
				g.DrawLine(pen, remainingWidth + Theme.S(118), 0, remainingWidth + Theme.S(198), base.Height);
				g.DrawLine(pen, base.Width - Theme.S(16), Theme.S(34), base.Width - Theme.S(16), base.Height - Theme.S(34));
			}
			int top = base.Height - Theme.S(28);
			using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, 100)))
			{
				g.DrawLine(pen2, base.Width - Theme.S(238), top, base.Width - Theme.S(32), top);
			}
			using (SolidBrush brush2 = new SolidBrush(Theme.Accent))
			{
				g.FillRectangle(brush2, base.Width - Theme.S(240), top - Theme.S(2), Theme.S(5), Theme.S(5));
				g.FillRectangle(brush2, base.Width - Theme.S(34), top - Theme.S(2), Theme.S(5), Theme.S(5));
			}
		}
	}
}
