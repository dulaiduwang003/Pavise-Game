using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class RogLinkButton : FxControl
	{
		private readonly string code;

		private readonly string glyph;

		private bool dot;

		public bool External = true;

		public Color? Tint;

		private Color AccentColor
		{
			get
			{
				return Tint ?? Theme.Accent;
			}
		}

		public bool Dot
		{
			get
			{
				return dot;
			}
			set
			{
				if (dot != value)
				{
					dot = value;
					Invalidate();
				}
			}
		}

		public RogLinkButton(string text, string code, string glyph)
		{
			Text = text;
			this.code = code ?? "";
			this.glyph = glyph ?? "info";
			base.TabStop = true;
			base.AccessibleName = text;
			base.AccessibleRole = AccessibleRole.PushButton;
			Font = DsType.Font(DsText.Body, true);
		}

		public void PerformClick()
		{
			if (base.CanInteract)
			{
				OnClick(EventArgs.Empty);
			}
		}

		protected override bool IsInputKey(Keys keyData)
		{
			if (!DsKeys.IsActivationInput(keyData))
			{
				return base.IsInputKey(keyData);
			}
			return true;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (DsKeys.IsPlainActivation(e) && base.CanInteract)
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				PerformClick();
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			DsState dsState = base.State;
			bool compact = base.Height < Theme.S(48);
			float hoverIfEnabled = dsState.HoverIfEnabled;
			float down = dsState.Press;
			Rectangle rectangle = new Rectangle(0, Theme.S(2), base.Width - 1, base.Height - Theme.S(4));
			Color a = Col.Lerp(Theme.Nav, Theme.Card, 0.72f + hoverIfEnabled * 0.18f);
			a = Col.Lerp(a, AccentColor, 0.025f + hoverIfEnabled * 0.07f);
			using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, Theme.S(8)))
			{
				using (SolidBrush brush = new SolidBrush(a))
				{
					graphics.FillPath(brush, graphicsPath);
				}
				using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, AccentColor, 0.16f + hoverIfEnabled * 0.58f), DsSpace.Line(1)))
				{
					graphics.DrawPath(pen, graphicsPath);
				}
				GraphicsState gstate = graphics.Save();
				graphics.SetClip(graphicsPath);
				int onPaintValue = base.Width - Theme.S(44) + (int)(hoverIfEnabled * (float)Theme.S(5));
				int scaledDimension = Theme.S(21);
				Point[] points = new Point[4]
				{
					new Point(onPaintValue, rectangle.Top),
					new Point(base.Width, rectangle.Top),
					new Point(base.Width, rectangle.Bottom),
					new Point(onPaintValue - scaledDimension, rectangle.Bottom)
				};
				using (LinearGradientBrush brush2 = new LinearGradientBrush(DsPaint.GradientRect(new Rectangle(Math.Max(0, onPaintValue - scaledDimension), rectangle.Top, Math.Max(1, base.Width - onPaintValue + scaledDimension), rectangle.Height)), Col.Alpha(AccentColor, 0), Col.Alpha(AccentColor, (int)(16f + hoverIfEnabled * 38f)), LinearGradientMode.Horizontal))
				{
					graphics.FillPolygon(brush2, points);
				}
				graphics.Restore(gstate);
				PaintBusy(graphics, rectangle, graphicsPath);
			}
			int scaledDimension2 = Theme.S(26);
			Rectangle r = new Rectangle(Theme.S(9), (base.Height - scaledDimension2) / 2, scaledDimension2, scaledDimension2);
			DsPaint.TechFrame(graphics, r, Theme.S(5), Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + hoverIfEnabled * 0.24f), Col.Alpha(AccentColor, (int)(78f + hoverIfEnabled * 106f)));
			Glyphs.Draw(graphics, glyph, new Rectangle(r.Left + Theme.S(6), r.Top + Theme.S(6), Theme.S(14), Theme.S(14)), AccentColor);
			if (base.Width >= Theme.S(79))
			{
				PaintCaption(graphics, compact, hoverIfEnabled, down);
			}
			if (dot)
			{
				DrawUnreadDot(graphics);
			}
			if (!base.Enabled)
			{
				using (GraphicsPath path = Theme.TechPath(rectangle, Theme.S(8)))
				{
					DsPaint.Veil(graphics, path, Col.Alpha(base.EffBg, 158));
				}
			}
			if (base.WantsFocusCue)
			{
				DsFocus.Draw(graphics, DsFocus.Inset(rectangle, 4), Theme.Fg, a);
			}
		}

		private void PaintCaption(Graphics g, bool compact, float hot, float down)
		{
			int left = Theme.S(43);
			int scaledDimension = Theme.S(36);
			if (!compact)
			{
				TextRenderer.DrawText(g, code, DsType.Mono(DsMono.Code), new Rectangle(left, Theme.S(7), Math.Max(1, base.Width - left - scaledDimension), Theme.S(12)), Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			Rectangle bounds = (compact ? new Rectangle(left, 0, Math.Max(1, base.Width - left - scaledDimension), base.Height) : new Rectangle(left, Theme.S(18), Math.Max(1, base.Width - left - scaledDimension), Theme.S(22)));
			TextRenderer.DrawText(g, Text, DsType.Font(DsText.Body, true), bounds, Col.Lerp(Theme.Dim, Theme.Fg, 0.55f + hot * 0.45f), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			if (External)
			{
				DrawExternalMark(g, hot, down);
			}
			else
			{
				DrawForwardMark(g, hot, down);
			}
			int top = base.Height - Theme.S(4);
			int sOffset = Theme.S(30) + (int)(hot * (float)Theme.S(44));
			using (Pen pen = new Pen(Col.Alpha(AccentColor, (int)(92f + hot * 120f)), DsSpace.Line(1)))
			{
				g.DrawLine(pen, left, top, left + sOffset, top);
			}
			int width = Theme.S(4);
			using (SolidBrush brush = new SolidBrush(AccentColor))
			{
				g.FillRectangle(brush, left - Theme.S(2), top - Theme.S(2), width, width);
			}
		}

		private void DrawForwardMark(Graphics g, float hot, float down)
		{
			int scaledDimension = Theme.S(9);
			int left = base.Width - Theme.S(24) + (int)(hot * (float)Theme.S(3)) - (int)(down * (float)Theme.S(1));
			int height = base.Height / 2 + ((base.Height >= Theme.S(48)) ? Theme.S(1) : 0);
			using (Pen pen = new Pen(Col.Lerp(Theme.Dim, AccentColor, 0.25f + hot * 0.75f), Math.Max(1.2f, Theme.S(1))))
			{
				g.DrawLine(pen, left, height - scaledDimension / 2, left + scaledDimension / 2, height);
				g.DrawLine(pen, left + scaledDimension / 2, height, left, height + scaledDimension / 2);
			}
		}

		private void DrawUnreadDot(Graphics g)
		{
			int scaledDimension = Theme.S(7);
			Rectangle rect = new Rectangle(base.Width - Theme.S(17), Theme.S(9), scaledDimension, scaledDimension);
			using (SolidBrush brush = new SolidBrush(AccentColor))
			{
				g.FillEllipse(brush, rect);
			}
			int scaledDimension2 = Theme.S(2);
			using (Pen pen = new Pen(Col.Alpha(AccentColor, 90), DsSpace.Line(1)))
			{
				g.DrawEllipse(pen, Rectangle.Inflate(rect, scaledDimension2, scaledDimension2));
			}
		}

		private void DrawExternalMark(Graphics g, float hot, float down)
		{
			int scaledDimension = Theme.S(9);
			int drawExternalMarkValue = base.Width - Theme.S(26) + (int)(hot * (float)Theme.S(2)) - (int)(down * (float)Theme.S(1));
			int drawExternalMarkValue2 = base.Height / 2 - scaledDimension / 2 + ((base.Height >= Theme.S(48)) ? Theme.S(1) : 0);
			Rectangle rectangle = new Rectangle(drawExternalMarkValue, drawExternalMarkValue2, scaledDimension, scaledDimension);
			using (Pen pen = new Pen(Col.Lerp(Theme.Dim, AccentColor, 0.25f + hot * 0.75f), Math.Max(1.2f, Theme.S(1))))
			{
				int scaledDimension2 = Theme.S(3);
				g.DrawLine(pen, rectangle.Left, rectangle.Top + scaledDimension2, rectangle.Left, rectangle.Bottom);
				g.DrawLine(pen, rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Bottom);
				g.DrawLine(pen, rectangle.Right, rectangle.Bottom, rectangle.Right, rectangle.Top + scaledDimension2);
				int width = rectangle.Right + scaledDimension2;
				int height = rectangle.Top - scaledDimension2;
				g.DrawLine(pen, rectangle.Left + scaledDimension2, rectangle.Bottom - scaledDimension2, width, height);
				g.DrawLine(pen, width - Theme.S(4), height, width, height);
				g.DrawLine(pen, width, height, width, height + Theme.S(4));
			}
		}
	}
}
