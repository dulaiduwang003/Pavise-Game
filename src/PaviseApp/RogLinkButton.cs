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
				int num = base.Width - Theme.S(44) + (int)(hoverIfEnabled * (float)Theme.S(5));
				int num2 = Theme.S(21);
				Point[] points = new Point[4]
				{
					new Point(num, rectangle.Top),
					new Point(base.Width, rectangle.Top),
					new Point(base.Width, rectangle.Bottom),
					new Point(num - num2, rectangle.Bottom)
				};
				using (LinearGradientBrush brush2 = new LinearGradientBrush(DsPaint.GradientRect(new Rectangle(Math.Max(0, num - num2), rectangle.Top, Math.Max(1, base.Width - num + num2), rectangle.Height)), Col.Alpha(AccentColor, 0), Col.Alpha(AccentColor, (int)(16f + hoverIfEnabled * 38f)), LinearGradientMode.Horizontal))
				{
					graphics.FillPolygon(brush2, points);
				}
				graphics.Restore(gstate);
				PaintBusy(graphics, rectangle, graphicsPath);
			}
			int num3 = Theme.S(26);
			Rectangle r = new Rectangle(Theme.S(9), (base.Height - num3) / 2, num3, num3);
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
			int num = Theme.S(43);
			int num2 = Theme.S(36);
			if (!compact)
			{
				TextRenderer.DrawText(g, code, DsType.Mono(DsMono.Code), new Rectangle(num, Theme.S(7), Math.Max(1, base.Width - num - num2), Theme.S(12)), Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
			Rectangle bounds = (compact ? new Rectangle(num, 0, Math.Max(1, base.Width - num - num2), base.Height) : new Rectangle(num, Theme.S(18), Math.Max(1, base.Width - num - num2), Theme.S(22)));
			TextRenderer.DrawText(g, Text, DsType.Font(DsText.Body, true), bounds, Col.Lerp(Theme.Dim, Theme.Fg, 0.55f + hot * 0.45f), TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			if (External)
			{
				DrawExternalMark(g, hot, down);
			}
			else
			{
				DrawForwardMark(g, hot, down);
			}
			int num3 = base.Height - Theme.S(4);
			int num4 = Theme.S(30) + (int)(hot * (float)Theme.S(44));
			using (Pen pen = new Pen(Col.Alpha(AccentColor, (int)(92f + hot * 120f)), DsSpace.Line(1)))
			{
				g.DrawLine(pen, num, num3, num + num4, num3);
			}
			int num5 = Theme.S(4);
			using (SolidBrush brush = new SolidBrush(AccentColor))
			{
				g.FillRectangle(brush, num - Theme.S(2), num3 - Theme.S(2), num5, num5);
			}
		}

		private void DrawForwardMark(Graphics g, float hot, float down)
		{
			int num = Theme.S(9);
			int num2 = base.Width - Theme.S(24) + (int)(hot * (float)Theme.S(3)) - (int)(down * (float)Theme.S(1));
			int num3 = base.Height / 2 + ((base.Height >= Theme.S(48)) ? Theme.S(1) : 0);
			using (Pen pen = new Pen(Col.Lerp(Theme.Dim, AccentColor, 0.25f + hot * 0.75f), Math.Max(1.2f, Theme.S(1))))
			{
				g.DrawLine(pen, num2, num3 - num / 2, num2 + num / 2, num3);
				g.DrawLine(pen, num2 + num / 2, num3, num2, num3 + num / 2);
			}
		}

		private void DrawUnreadDot(Graphics g)
		{
			int num = Theme.S(7);
			Rectangle rect = new Rectangle(base.Width - Theme.S(17), Theme.S(9), num, num);
			using (SolidBrush brush = new SolidBrush(AccentColor))
			{
				g.FillEllipse(brush, rect);
			}
			int num2 = Theme.S(2);
			using (Pen pen = new Pen(Col.Alpha(AccentColor, 90), DsSpace.Line(1)))
			{
				g.DrawEllipse(pen, Rectangle.Inflate(rect, num2, num2));
			}
		}

		private void DrawExternalMark(Graphics g, float hot, float down)
		{
			int num = Theme.S(9);
			int num2 = base.Width - Theme.S(26) + (int)(hot * (float)Theme.S(2)) - (int)(down * (float)Theme.S(1));
			int num3 = base.Height / 2 - num / 2 + ((base.Height >= Theme.S(48)) ? Theme.S(1) : 0);
			Rectangle rectangle = new Rectangle(num2, num3, num, num);
			using (Pen pen = new Pen(Col.Lerp(Theme.Dim, AccentColor, 0.25f + hot * 0.75f), Math.Max(1.2f, Theme.S(1))))
			{
				int num4 = Theme.S(3);
				g.DrawLine(pen, rectangle.Left, rectangle.Top + num4, rectangle.Left, rectangle.Bottom);
				g.DrawLine(pen, rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Bottom);
				g.DrawLine(pen, rectangle.Right, rectangle.Bottom, rectangle.Right, rectangle.Top + num4);
				int num5 = rectangle.Right + num4;
				int num6 = rectangle.Top - num4;
				g.DrawLine(pen, rectangle.Left + num4, rectangle.Bottom - num4, num5, num6);
				g.DrawLine(pen, num5 - Theme.S(4), num6, num5, num6);
				g.DrawLine(pen, num5, num6, num5, num6 + Theme.S(4));
			}
		}
	}
}
