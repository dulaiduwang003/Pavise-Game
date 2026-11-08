using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class DashboardTile : FxControl
	{
		public string Title = "";

		public string Detail = "";

		public string Glyph = "game";

		public int Channel = 1;

		public DashboardTile()
		{
			Cursor = Cursors.Default;
			base.TabStop = false;
			BackColor = Theme.Bg;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			FillBg(graphics);
			Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			Color color = Col.Lerp(Theme.Card, Theme.CardHover, hover.Value * 0.72f);
			if (Theme.LightMode)
			{
				color = Col.Lerp(color, Theme.Accent, 0.025f + hover.Value * 0.025f);
			}
			Color baseFill = (Theme.LightMode ? Col.Lerp(color, Theme.Inset, 0.34f) : Col.Lerp(color, Theme.Inset, 0.22f));
			Color color2 = Backdrop.CardFill(this, color);
			Color color3 = Backdrop.CardFill(this, baseFill);
			using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(8)))
			{
				using (LinearGradientBrush brush = new LinearGradientBrush(rectangle, color2, color3, LinearGradientMode.Horizontal))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover.Value * 0.72f)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			int scaledDimension = Theme.S(16);
			int scaledDimension2 = Theme.S(24);
			int scaledDimension3 = Theme.S(50);
			using (SolidBrush brush2 = new SolidBrush(Col.Alpha(Theme.Accent, (int)(10f + hover.Value * 14f))))
			{
				graphics.FillEllipse(brush2, scaledDimension - Theme.S(5), scaledDimension2 - Theme.S(5), scaledDimension3 + Theme.S(10), scaledDimension3 + Theme.S(10));
			}
			using (GraphicsPath path2 = Theme.TechPath(new Rectangle(scaledDimension, scaledDimension2, scaledDimension3, scaledDimension3), Theme.S(6)))
			{
				using (SolidBrush brush3 = new SolidBrush(Backdrop.CardFill(this, Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + hover.Value * 0.22f))))
				{
					graphics.FillPath(brush3, path2);
				}
				using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, (int)(72f + hover.Value * 92f))))
				{
					graphics.DrawPath(pen2, path2);
				}
			}
			Glyphs.Draw(graphics, Glyph, new Rectangle(scaledDimension + Theme.S(14), scaledDimension2 + Theme.S(14), Theme.S(22), Theme.S(22)), Theme.Accent);
			int left = Theme.S(82);
			using (Pen pen3 = new Pen(Col.Alpha(Theme.StrokeHi, 92)))
			{
				graphics.DrawLine(pen3, left, Theme.S(20), left, base.Height - Theme.S(20));
			}
			using (Pen pen4 = new Pen(Theme.Accent, Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawLine(pen4, left, Theme.S(28), left, Theme.S(51));
			}
			TextRenderer.DrawText(graphics, Title, Theme.UI(11f, true), new Rectangle(Theme.S(98), Theme.S(22), base.Width - Theme.S(145), Theme.S(28)), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, Detail, Theme.UI(8.4f, false), new Rectangle(Theme.S(98), Theme.S(56), base.Width - Theme.S(112), Theme.S(34)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, Channel.ToString("00"), Theme.Mono(6.7f), new Rectangle(base.Width - Theme.S(39), Theme.S(20), Theme.S(24), Theme.S(18)), Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			int top = base.Height - Theme.S(8);
			int sLimit = Math.Max(Theme.S(34), (base.Width - Theme.S(120)) / 3);
			using (Pen pen5 = new Pen(Col.Alpha(Theme.Accent, (int)(120f + 80f * hover.Value))))
			{
				graphics.DrawLine(pen5, Theme.S(98), top, Theme.S(98) + sLimit, top);
			}
		}
	}
}
