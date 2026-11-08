using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class BackdropPreview : Control
	{
		public string EmptyText = "";

		public BackdropPreview()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Theme.Card;
			base.TabStop = false;
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.PaintOnCard(graphics, this, base.ClientRectangle);
			}
			else
			{
				using (SolidBrush brush = new SolidBrush(BackColor))
				{
					graphics.FillRectangle(brush, base.ClientRectangle);
				}
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle rectangle = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			if (rectangle.Width <= 0 || rectangle.Height <= 0)
			{
				return;
			}
			using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, Theme.S(8)))
			{
				GraphicsState gstate = graphics.Save();
				graphics.SetClip(graphicsPath);
				if (Backdrop.Active)
				{
					Backdrop.PaintPreview(graphics, rectangle);
				}
				else
				{
					using (LinearGradientBrush brush2 = new LinearGradientBrush(rectangle, Col.Lerp(Theme.Inset, Theme.Card, 0.18f), Theme.Inset, LinearGradientMode.ForwardDiagonal))
					{
						graphics.FillRectangle(brush2, rectangle);
					}
					using (SolidBrush brush3 = new SolidBrush(Col.Alpha(Theme.Accent, Theme.LightMode ? 10 : 7)))
					{
						graphics.FillPolygon(brush3, new Point[3]
						{
							new Point(rectangle.Left, rectangle.Bottom),
							new Point(rectangle.Right, rectangle.Top),
							new Point(rectangle.Right, rectangle.Bottom)
						});
					}
				}
				graphics.Restore(gstate);
				using (Pen pen = new Pen(Backdrop.Active ? Col.Alpha(Theme.Accent, 158) : Theme.StrokeHi))
				{
					graphics.DrawPath(pen, graphicsPath);
				}
			}
			if (!Backdrop.Active)
			{
				int scaledDimension = Theme.S(24);
				Glyphs.Draw(graphics, "settings", new Rectangle((base.Width - scaledDimension) / 2, Theme.S(22), scaledDimension, scaledDimension), Theme.Faint);
				TextRenderer.DrawText(graphics, EmptyText ?? "", Theme.Mono(7f), new Rectangle(Theme.S(8), Theme.S(56), base.Width - Theme.S(16), Theme.S(20)), Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			TextRenderer.DrawText(graphics, "LIVE PREVIEW", Theme.Mono(5.9f), new Rectangle(Theme.S(8), Theme.S(6), base.Width - Theme.S(16), Theme.S(14)), Backdrop.Active ? Theme.Accent : Theme.Faint, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
		}
	}
}
