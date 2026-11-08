using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class ModuleBanner : RoundPanel
	{
		private string code = "MODULE // 00";

		private string title = "";

		private string detail = "";

		private string state = "READY";

		private string glyph = "settings";

		private Color stateColor = Theme.Accent;

		public string Code
		{
			get
			{
				return code;
			}
			set
			{
				SetText(ref code, value, "");
			}
		}

		public string TitleText
		{
			get
			{
				return title;
			}
			set
			{
				SetText(ref title, value, "");
			}
		}

		public string Detail
		{
			get
			{
				return detail;
			}
			set
			{
				SetText(ref detail, value, "");
			}
		}

		public string State
		{
			get
			{
				return state;
			}
			set
			{
				SetText(ref state, value, "");
			}
		}

		public string Glyph
		{
			get
			{
				return glyph;
			}
			set
			{
				SetText(ref glyph, value, "settings");
			}
		}

		public Color StateColor
		{
			get
			{
				return stateColor;
			}
			set
			{
				if (!(stateColor == value))
				{
					stateColor = value;
					Invalidate();
				}
			}
		}

		private void SetText(ref string field, string value, string fallback)
		{
			string valueOffset = value ?? fallback;
			if (!(field == valueOffset))
			{
				field = valueOffset;
				Invalidate();
			}
		}

		public ModuleBanner()
		{
			Radius = Theme.S(13);
			Fill = Theme.Card;
			Border = Theme.StrokeHi;
			BackColor = Theme.Bg;
			AccentEdge = true;
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
				GraphicsState gstate = graphics.Save();
				graphics.SetClip(clip);
				int remainingWidth = base.Width - Theme.S(280);
				Point[] points = new Point[5]
				{
					new Point(remainingWidth, 0),
					new Point(base.Width, 0),
					new Point(base.Width, base.Height),
					new Point(remainingWidth + Theme.S(88), base.Height),
					new Point(remainingWidth + Theme.S(36), base.Height / 2)
				};
				using (LinearGradientBrush brush = new LinearGradientBrush(new Rectangle(Math.Max(0, remainingWidth), 0, Math.Max(1, base.Width - remainingWidth), base.Height), Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, Theme.LightMode ? 18 : 11), LinearGradientMode.Horizontal))
				{
					graphics.FillPolygon(brush, points);
				}
				using (Pen pen = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 20 : 13)))
				{
					for (int i = remainingWidth - base.Height; i < base.Width + base.Height; i += Theme.S(18))
					{
						graphics.DrawLine(pen, i, 0, i + base.Height, base.Height);
					}
				}
				graphics.Restore(gstate);
			}
			int scaledDimension = Theme.S(42);
			Rectangle r2 = new Rectangle(Theme.S(16), (base.Height - scaledDimension) / 2, scaledDimension, scaledDimension);
			using (GraphicsPath path = Theme.TechPath(r2, Theme.S(6)))
			{
				using (SolidBrush brush2 = new SolidBrush(Col.Lerp(Theme.Inset, Theme.Sel, 0.28f)))
				{
					graphics.FillPath(brush2, path);
				}
				using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, 118)))
				{
					graphics.DrawPath(pen2, path);
				}
			}
			Glyphs.Draw(graphics, glyph, new Rectangle(r2.Left + Theme.S(11), r2.Top + Theme.S(11), Theme.S(20), Theme.S(20)), Theme.Accent);
			int scaledDimension2 = Theme.S(74);
			int scaledDimension3 = Theme.S(300);
			TextRenderer.DrawText(graphics, code, Theme.Mono(6.2f), new Rectangle(scaledDimension2, Theme.S(9), base.Width - scaledDimension2 - scaledDimension3, Theme.S(14)), Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, title, Theme.UI(10.2f, true), new Rectangle(scaledDimension2, Theme.S(24), base.Width - scaledDimension2 - scaledDimension3, Theme.S(23)), Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, detail, Theme.UI(7.7f, false), new Rectangle(scaledDimension2, Theme.S(48), base.Width - scaledDimension2 - Theme.S(40), Theme.S(18)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			int left = base.Width - Theme.S(276);
			using (SolidBrush brush3 = new SolidBrush(Col.Alpha(stateColor, 28)))
			{
				graphics.FillEllipse(brush3, left, Theme.S(18), Theme.S(20), Theme.S(20));
			}
			using (SolidBrush brush4 = new SolidBrush(stateColor))
			{
				graphics.FillEllipse(brush4, left + Theme.S(6), Theme.S(24), Theme.S(8), Theme.S(8));
			}
			TextRenderer.DrawText(graphics, state.ToUpperInvariant(), Theme.MonoFor(state, 7.2f), new Rectangle(left + Theme.S(28), Theme.S(15), Theme.S(230), Theme.S(28)), stateColor, TextFormatFlags.EndEllipsis | TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			using (Pen pen3 = new Pen(Col.Alpha(stateColor, 128)))
			{
				graphics.DrawLine(pen3, left + Theme.S(28), Theme.S(48), base.Width - Theme.S(18), Theme.S(48));
			}
		}
	}
}
