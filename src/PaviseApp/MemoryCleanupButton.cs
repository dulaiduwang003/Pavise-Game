using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class MemoryCleanupButton : PillButton
	{
		internal Rectangle FrameBounds;

		internal Rectangle IconBounds;

		internal Rectangle CaptionBounds;

		internal MemoryCleanupButton()
			: base(ManualMemoryText.Button)
		{
			Cursor = Cursors.Hand;
			Arrange();
		}

		private void Arrange()
		{
			FrameBounds = new Rectangle(1, 1, Math.Max(1, base.Width - 3), Math.Max(1, base.Height - 3));
			int maxLimit = Math.Min(Theme.S(22), Math.Max(1, base.Height - Theme.S(10)));
			IconBounds = new Rectangle(Theme.S(7), (base.Height - maxLimit) / 2, maxLimit, maxLimit);
			CaptionBounds = new Rectangle(Theme.S(40), 0, Math.Max(1, base.Width - Theme.S(77)), base.Height);
		}

		protected override void OnSizeChanged(EventArgs e)
		{
			base.OnSizeChanged(e);
			Arrange();
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			if (base.Width < 3 || base.Height < 3)
			{
				return;
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			DsState dsState = base.State;
			float hoverIfEnabled = dsState.HoverIfEnabled;
			float press = dsState.Press;
			Color color = (base.Enabled ? Theme.Accent : Theme.Dim);
			Color color2 = Col.Lerp(Theme.Inset, color, base.Enabled ? (0.12f + hoverIfEnabled * 0.13f + press * 0.08f) : 0.03f);
			Color color3 = Col.Lerp(Theme.Inset, color, base.Enabled ? (0.3f + hoverIfEnabled * 0.18f + press * 0.08f) : 0.06f);
			using (GraphicsPath graphicsPath = Theme.TechPath(FrameBounds, Theme.S(8)))
			{
				using (LinearGradientBrush brush = new LinearGradientBrush(FrameBounds, color2, color3, LinearGradientMode.Horizontal))
				{
					graphics.FillPath(brush, graphicsPath);
				}
				using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, color, base.Enabled ? (0.72f + hoverIfEnabled * 0.25f) : 0.18f), Math.Max(1f, Theme.S(1))))
				{
					graphics.DrawPath(pen, graphicsPath);
				}
				GraphicsState gstate = graphics.Save();
				try
				{
					graphics.SetClip(graphicsPath);
					using (Pen pen2 = new Pen(Col.Alpha(color, (int)(18f + hoverIfEnabled * 24f)), Theme.S(5)))
					{
						for (int i = 0; i < 3; i++)
						{
							int left = base.Width - Theme.S(52 - i * 13);
							graphics.DrawLine(pen2, left, 0, left - Theme.S(16), base.Height);
						}
					}
					PaintBusy(graphics, FrameBounds, graphicsPath);
				}
				finally
				{
					graphics.Restore(gstate);
				}
			}
			using (GraphicsPath path = Theme.TechPath(IconBounds, Theme.S(4)))
			{
				using (SolidBrush brush2 = new SolidBrush(color))
				{
					graphics.FillPath(brush2, path);
				}
			}
			Glyphs.Draw(graphics, "game", Rectangle.Inflate(IconBounds, -Theme.S(3), -Theme.S(3)), base.Enabled ? Theme.OnAccent : Theme.Inset);
			Rectangle captionBounds = CaptionBounds;
			captionBounds.Offset((int)(press * (float)Theme.S(1)), (int)(press * (float)Theme.S(1)));
			TextRenderer.DrawText(graphics, Text, Theme.UI(9f, true), captionBounds, base.Enabled ? Theme.Fg : Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			int heightRatio = base.Height / 2;
			int onPaintValue = base.Width - Theme.S(13) + (int)(hoverIfEnabled * (float)Theme.S(2));
			using (Pen pen3 = new Pen(Col.Alpha(color, base.Enabled ? 230 : 95), Math.Max(1.2f, Theme.S(1))))
			{
				for (int j = 0; j < 2; j++)
				{
					int remainingOnPaintValue = onPaintValue - j * Theme.S(7);
					graphics.DrawLines(pen3, new Point[3]
					{
						new Point(remainingOnPaintValue - Theme.S(4), heightRatio - Theme.S(4)),
						new Point(remainingOnPaintValue, heightRatio),
						new Point(remainingOnPaintValue - Theme.S(4), heightRatio + Theme.S(4))
					});
				}
			}
			if (!base.Busy)
			{
				using (Pen pen4 = new Pen(Col.Alpha(color, 180), Math.Max(1f, Theme.S(1))))
				{
					graphics.DrawLine(pen4, Theme.S(10), base.Height - Theme.S(3), Theme.S(10) + (int)((float)(base.Width - Theme.S(30)) * (0.3f + hoverIfEnabled * 0.5f)), base.Height - Theme.S(3));
				}
			}
			if (base.WantsFocusCue)
			{
				DsFocus.Draw(graphics, DsFocus.Inset(FrameBounds, 3), Theme.Fg, color2);
			}
		}
	}
}
