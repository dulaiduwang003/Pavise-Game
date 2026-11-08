using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal static class DsPaint
	{
		internal static void CardBackground(Graphics g, Control control, Color fill)
		{
			if (Backdrop.AppliesTo(control))
			{
				Backdrop.Paint(g, control, control.ClientRectangle);
				using (var brush = new SolidBrush(Backdrop.CardFill(control, fill))) g.FillRectangle(brush, control.ClientRectangle);
			}
			else using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, control.ClientRectangle);
		}
		public static Rectangle GradientRect(Rectangle r)
		{
			if (r.Width < 1)
			{
				r.Width = 1;
			}
			if (r.Height < 1)
			{
				r.Height = 1;
			}
			return r;
		}

		public static void FillBackground(Graphics g, Control control, Color fill)
		{
			if (object.Equals(control.Tag, "workspace-canvas"))
			{
				WorkspaceCanvasSurface.Paint(g, control, control.ClientRectangle);
				return;
			}
			if (Backdrop.AppliesTo(control))
			{
				Backdrop.PaintOnCard(g, control, control.ClientRectangle);
				return;
			}
			using (SolidBrush brush = new SolidBrush(fill))
			{
				g.FillRectangle(brush, control.ClientRectangle);
			}
		}

		public static void TechFrame(Graphics g, Rectangle r, int cut, Color fill, Color stroke)
		{
			using (GraphicsPath path = Theme.TechPath(r, cut))
			{
				using (SolidBrush brush = new SolidBrush(fill))
				{
					g.FillPath(brush, path);
				}
				if (!stroke.IsEmpty)
				{
					using (Pen pen = new Pen(stroke))
					{
						g.DrawPath(pen, path);
						return;
					}
				}
			}
		}

		public static void ButtonFace(Graphics g, GraphicsPath path, Rectangle r, int cut, Size size, BtnKind kind, DsState state, Color effBg)
		{
			float hoverIfEnabled = state.HoverIfEnabled;
			int width = size.Width;
			switch (kind)
			{
			case BtnKind.Primary:
			{
				using (LinearGradientBrush brush3 = new LinearGradientBrush(GradientRect(r), Col.Lerp(Theme.Accent, Color.White, hoverIfEnabled * 0.12f), Col.Lerp(Theme.Accent2, Color.White, hoverIfEnabled * 0.12f), LinearGradientMode.Horizontal))
				{
					g.FillPath(brush3, path);
				}
				using (Pen pen6 = new Pen(Col.Alpha(Color.White, 72)))
				{
					g.DrawLine(pen6, Theme.S(2), Theme.S(1), width - cut - Theme.S(1), Theme.S(1));
				}
				break;
			}
			case BtnKind.Danger:
			{
				Color color3 = (Theme.LightMode ? Col.Lerp(Color.White, Theme.Danger, 0.05f + hoverIfEnabled * 0.05f) : Col.Lerp(DsPalette.DangerTopDark, Theme.Danger, 0.1f + hoverIfEnabled * 0.13f));
				Color color4 = (Theme.LightMode ? Col.Lerp(Color.White, Theme.Danger, 0.11f + hoverIfEnabled * 0.07f) : Col.Lerp(DsPalette.DangerBottomDark, Theme.Danger, 0.04f + hoverIfEnabled * 0.1f));
				using (LinearGradientBrush brush2 = new LinearGradientBrush(GradientRect(r), color3, color4, LinearGradientMode.Vertical))
				{
					g.FillPath(brush2, path);
				}
				using (Pen pen3 = new Pen(Col.Alpha(Theme.Danger, (int)(Theme.LightMode ? (150f + 70f * hoverIfEnabled) : (125f + 80f * hoverIfEnabled)))))
				{
					g.DrawPath(pen3, path);
				}
				if (!Theme.LightMode)
				{
					using (Pen pen4 = new Pen(Col.Alpha(Color.White, (int)(24f + 24f * hoverIfEnabled))))
					{
						g.DrawLine(pen4, Theme.S(2), Theme.S(1), width - cut - Theme.S(1), Theme.S(1));
					}
				}
				using (Pen pen5 = new Pen(Theme.Danger, DsSpace.Line(2)))
				{
					g.DrawLine(pen5, Theme.S(1), Theme.S(10), Theme.S(1), size.Height - Theme.S(10));
				}
				break;
			}
			default:
			{
				Color color = Col.Lerp(Theme.CardHover, Color.White, 0.015f + hoverIfEnabled * 0.035f);
				Color color2 = Col.Lerp(Theme.Card, Color.Black, 0.06f - hoverIfEnabled * 0.02f);
				using (LinearGradientBrush brush = new LinearGradientBrush(GradientRect(r), color, color2, LinearGradientMode.Vertical))
				{
					g.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, hoverIfEnabled)))
				{
					g.DrawPath(pen, path);
				}
				using (Pen pen2 = new Pen(Col.Alpha(Color.White, (int)(18f + 24f * hoverIfEnabled))))
				{
					g.DrawLine(pen2, Theme.S(2), Theme.S(1), width - cut - Theme.S(1), Theme.S(1));
				}
				break;
			}
			}
			if (state.Press > 0.01f)
			{
				using (SolidBrush brush4 = new SolidBrush(Col.Alpha(Color.Black, (int)(45f * state.Press))))
				{
					g.FillPath(brush4, path);
				}
			}
			if (!state.Enabled)
			{
				Veil(g, path, Col.Alpha(effBg, 158));
			}
		}

		public static Color ButtonInk(BtnKind kind, DsState state)
		{
			float hoverIfEnabled = state.HoverIfEnabled;
			if (!state.Enabled)
			{
				return Theme.Faint;
			}
			switch (kind)
			{
			case BtnKind.Primary:
				return Theme.OnAccent;
			case BtnKind.Danger:
				return Col.Lerp(Theme.Danger, Color.White, Theme.LightMode ? 0f : (hoverIfEnabled * 0.45f));
			default:
				if (!Theme.LightMode)
				{
					return Col.Lerp(Theme.Fg, Color.White, hoverIfEnabled * 0.2f);
				}
				return Theme.Fg;
			}
		}

		public static void Veil(Graphics g, GraphicsPath path, Color color)
		{
			using (SolidBrush brush = new SolidBrush(color))
			{
				g.FillPath(brush, path);
			}
		}

		public static void Veil(Graphics g, Rectangle rect, Color color)
		{
			if (rect.Width <= 0 || rect.Height <= 0)
			{
				return;
			}
			using (SolidBrush brush = new SolidBrush(color))
			{
				g.FillRectangle(brush, rect);
			}
		}

		public static int BadgeWidth(Graphics g, string text, Font font)
		{
			return TextRenderer.MeasureText(g, text, font).Width + Theme.S(12);
		}

		public static void Badge(Graphics g, Rectangle pill, string text, Font font, bool strong)
		{
			if (pill.Width > 0 && pill.Height > 0)
			{
				Color fill = (strong ? Theme.Accent : Theme.Inset);
				Color stroke = (strong ? Col.Alpha(Theme.Accent, 235) : Theme.Stroke);
				Color foreColor = (strong ? Theme.OnAccent : Theme.Faint);
				g.SmoothingMode = SmoothingMode.AntiAlias;
				TechFrame(g, pill, Theme.S(4), fill, stroke);
				TextRenderer.DrawText(g, text, font, pill, foreColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			}
		}

		public static void Chevron(Graphics g, float x, float cy, float arm, bool pointUp, Color color)
		{
			float y = (pointUp ? (cy + arm / 2f) : (cy - arm / 2f));
			float y2 = (pointUp ? (cy - arm / 2f) : (cy + arm / 2f));
			g.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen = new Pen(color, Math.Max(1.4f, Theme.S(1))))
			{
				pen.StartCap = LineCap.Round;
				pen.EndCap = LineCap.Round;
				pen.LineJoin = LineJoin.Round;
				g.DrawLines(pen, new PointF[3]
				{
					new PointF(x, y),
					new PointF(x + arm, y2),
					new PointF(x + arm * 2f, y)
				});
			}
		}

		public static void DropArrow(Graphics g, int x, int y, int delta, bool open, Color color)
		{
			int dropArrowValue = ((!open) ? 1 : (-1));
			using (Pen pen = new Pen(color, DsSpace.Line(1)))
			{
				g.DrawLines(pen, new Point[3]
				{
					new Point(x - delta, y - dropArrowValue * delta / 2),
					new Point(x, y + dropArrowValue * delta / 2),
					new Point(x + delta, y - dropArrowValue * delta / 2)
				});
			}
		}

		public static void BusyRail(Graphics g, Rectangle bounds, float phase, Color accent)
		{
			int scaledDimension = Theme.S(8);
			int top = bounds.Bottom - Theme.S(3);
			int left = bounds.Left + scaledDimension;
			int width2 = bounds.Right - scaledDimension;
			if (width2 - left < Theme.S(8) || top <= bounds.Top)
			{
				return;
			}
			float width = DsSpace.Line(2);
			using (Pen pen = new Pen(Col.Alpha(accent, 110), width))
			{
				g.DrawLine(pen, left, top, width2, top);
			}
			int remainingWidth2 = width2 - left;
			int sLimit = Math.Max(Theme.S(16), remainingWidth2 / 3);
			if (phase < 0f)
			{
				phase = 0f;
			}
			if (phase > 1f)
			{
				phase = 1f;
			}
			int roundOffset = left - sLimit + (int)Math.Round(phase * (float)(remainingWidth2 + sLimit));
			int left2 = Math.Max(left, roundOffset);
			int width3 = Math.Min(width2, roundOffset + sLimit);
			if (width3 <= left2)
			{
				return;
			}
			using (Pen pen2 = new Pen(Col.Alpha(accent, 235), width))
			{
				g.DrawLine(pen2, left2, top, width3, top);
			}
		}
	}
}
