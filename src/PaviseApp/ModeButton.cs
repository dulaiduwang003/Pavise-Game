using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class ModeButton : FxControl
	{
		private PerformancePreset mode;

		private string source;

		internal bool Compact;

		public Action Clicked;

		internal string SummaryText
		{
			get
			{
				return ModeName(mode);
			}
		}

		internal string SourceText
		{
			get
			{
				return source;
			}
		}

		public ModeButton()
		{
			Bg = Theme.Bg;
			source = Lang.T("mode.source.global");
			base.TabStop = true;
			SetStyle(ControlStyles.Selectable, true);
			base.AccessibleRole = AccessibleRole.ButtonDropDown;
			base.AccessibleName = DsLabels.Pick("游戏场景", "Game scene");
			Text = SummaryText;
			base.AccessibleDescription = source;
		}

		public void SetMode(PerformancePreset value)
		{
			if (mode != value)
			{
				mode = value;
				Text = SummaryText;
				Invalidate();
			}
		}

		public void SetSource(string value)
		{
			string accessibleDescriptionValue = value ?? "";
			if (!(source == accessibleDescriptionValue))
			{
				source = accessibleDescriptionValue;
				base.AccessibleDescription = accessibleDescriptionValue;
				Invalidate();
			}
		}

		public void PerformClick()
		{
			if (base.Enabled)
			{
				OnClick(EventArgs.Empty);
			}
		}

		protected override void OnClick(EventArgs e)
		{
			if (base.CanInteract)
			{
				base.OnClick(e);
				if (Clicked != null)
				{
					Clicked();
				}
			}
		}

		protected override bool IsInputKey(Keys keys)
		{
			if ((keys & Keys.KeyCode) != Keys.Down)
			{
				return base.IsInputKey(keys);
			}
			return true;
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Modifiers == Keys.None && (e.KeyCode == Keys.Return || e.KeyCode == Keys.Space || e.KeyCode == Keys.Down || e.KeyCode == Keys.F4))
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
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			if (base.Width < 2 || base.Height < 2)
			{
				return;
			}
			Rectangle rectangle = new Rectangle(0, Theme.S(2), base.Width - 1, base.Height - Theme.S(5));
			if (rectangle.Width < 1 || rectangle.Height < 1)
			{
				return;
			}
			if (Compact)
			{
				PaintCompact(graphics, rectangle);
				return;
			}
			using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, Theme.S(9)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, hover.Value)))
				{
					graphics.FillPath(brush, graphicsPath);
				}
				using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.38f + hover.Value * 0.45f)))
				{
					graphics.DrawPath(pen, graphicsPath);
				}
				PaintBusy(graphics, rectangle, graphicsPath);
			}
			using (Pen pen2 = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
			{
				graphics.DrawLine(pen2, 0, Theme.S(13), 0, base.Height - Theme.S(14));
			}
			using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, (int)(110f + 110f * hover.Value)), Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawLine(pen3, rectangle.Right - Theme.S(9), rectangle.Top, rectangle.Right, rectangle.Top + Theme.S(9));
			}
			Glyphs.Draw(graphics, "game", new Rectangle(Theme.S(12), (base.Height - Theme.S(18)) / 2, Theme.S(18), Theme.S(18)), base.Enabled ? Theme.Accent : Theme.Faint);
			TextRenderer.DrawText(graphics, SummaryText, Theme.UI(9.25f, true), new Rectangle(Theme.S(38), 0, Math.Max(1, base.Width - Theme.S(68)), base.Height), base.Enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
			PointF[] points = new PointF[3]
			{
				new PointF(base.Width - Theme.S(22), Theme.S(18)),
				new PointF(base.Width - Theme.S(17), Theme.S(23)),
				new PointF(base.Width - Theme.S(12), Theme.S(18))
			};
			using (Pen pen4 = new Pen(Theme.Dim, Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawLines(pen4, points);
			}
			if (base.WantsFocusCue)
			{
				DsFocus.Draw(graphics, DsFocus.Inset(rectangle, 4), Theme.Fg, Theme.Card);
			}
		}

		private static GraphicsPath CompactFrame(Rectangle bounds, int bevel)
		{
			int minLimit = Math.Max(0, Math.Min(bevel, Math.Min(bounds.Width, bounds.Height) / 2));
			GraphicsPath graphicsPath = new GraphicsPath();
			graphicsPath.AddPolygon(new Point[8]
			{
				new Point(bounds.Left + minLimit, bounds.Top),
				new Point(bounds.Right - minLimit, bounds.Top),
				new Point(bounds.Right, bounds.Top + minLimit),
				new Point(bounds.Right, bounds.Bottom - minLimit),
				new Point(bounds.Right - minLimit, bounds.Bottom),
				new Point(bounds.Left + minLimit, bounds.Bottom),
				new Point(bounds.Left, bounds.Bottom - minLimit),
				new Point(bounds.Left, bounds.Top + minLimit)
			});
			graphicsPath.CloseFigure();
			return graphicsPath;
		}

		private void PaintCompact(Graphics g, Rectangle bounds)
		{
			bool matchesWidth = base.Width < Theme.S(180);
			int scaledDimension = Theme.S(matchesWidth ? 38 : 43);
			int scaledDimension2 = Theme.S(matchesWidth ? 29 : 33);
			int scaledDimension3 = Theme.S(7);
			int remainingRight = bounds.Right - scaledDimension2;
			Color color = (base.Enabled ? Theme.Accent : Theme.Faint);
			Color a = Col.Lerp(Theme.Card, Theme.CardHover, 0.35f + hover.Value * 0.65f);
			Color color2 = Col.Lerp(Theme.Nav, Theme.Card, 0.32f + hover.Value * 0.22f);
			a = Col.Lerp(a, Theme.Nav, press.Value * 0.24f);
			using (GraphicsPath graphicsPath = CompactFrame(bounds, Theme.S(9)))
			{
				GraphicsState gstate = g.Save();
				try
				{
					g.SetClip(graphicsPath, CombineMode.Intersect);
					using (LinearGradientBrush brush = new LinearGradientBrush(bounds, a, color2, LinearGradientMode.Vertical))
					{
						g.FillPath(brush, graphicsPath);
					}
					Point[] points = new Point[4]
					{
						new Point(bounds.Left, bounds.Top),
						new Point(scaledDimension + scaledDimension3, bounds.Top),
						new Point(scaledDimension - scaledDimension3, bounds.Bottom),
						new Point(bounds.Left, bounds.Bottom)
					};
					using (LinearGradientBrush brush2 = new LinearGradientBrush(bounds, Col.Lerp(a, color, 0.22f + hover.Value * 0.12f), Col.Lerp(color2, color, 0.07f + hover.Value * 0.08f), LinearGradientMode.Vertical))
					{
						g.FillPolygon(brush2, points);
					}
					Point[] points2 = new Point[4]
					{
						new Point(remainingRight + scaledDimension3, bounds.Top),
						new Point(bounds.Right, bounds.Top),
						new Point(bounds.Right, bounds.Bottom),
						new Point(remainingRight - scaledDimension3, bounds.Bottom)
					};
					using (LinearGradientBrush brush3 = new LinearGradientBrush(bounds, Col.Lerp(Theme.Inset, a, 0.28f + hover.Value * 0.16f), Theme.Inset, LinearGradientMode.Vertical))
					{
						g.FillPolygon(brush3, points2);
					}
					using (Pen pen = new Pen(Col.Alpha(color, (int)(100f + hover.Value * 75f))))
					{
						g.DrawLine(pen, scaledDimension + scaledDimension3, bounds.Top, scaledDimension - scaledDimension3, bounds.Bottom);
					}
					using (Pen pen2 = new Pen(Col.Alpha(Theme.Bg, 180)))
					{
						g.DrawLine(pen2, scaledDimension + scaledDimension3 + Theme.S(2), bounds.Top, scaledDimension - scaledDimension3 + Theme.S(2), bounds.Bottom);
					}
					using (Pen pen3 = new Pen(Col.Lerp(Theme.StrokeHi, color, hover.Value * 0.36f)))
					{
						g.DrawLine(pen3, remainingRight + scaledDimension3, bounds.Top, remainingRight - scaledDimension3, bounds.Bottom);
					}
					int roundedValue = (int)Math.Round(press.Value * (float)Theme.S(1));
					int roundedValueOffset = bounds.Top + bounds.Height / 2 + roundedValue;
					int scaledDimension4 = Theme.S(23);
					int leftOffset = bounds.Left + (scaledDimension - scaledDimension4) / 2;
					Glyphs.Draw(g, "game", new Rectangle(leftOffset, roundedValueOffset - scaledDimension4 / 2, scaledDimension4, scaledDimension4), color);
					int scaledDimensionOffset = scaledDimension + Theme.S(12);
					int remainingRemainingRight = remainingRight - Theme.S(10);
					TextRenderer.DrawText(g, SummaryText, Theme.UI(10.5f, true), new Rectangle(scaledDimensionOffset, bounds.Top + roundedValue, Math.Max(1, remainingRemainingRight - scaledDimensionOffset), bounds.Height), base.Enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
					int sOffset = remainingRight + scaledDimension2 / 2 + Theme.S(1);
					using (Pen pen4 = new Pen(base.Enabled ? Col.Lerp(Theme.Dim, Theme.Fg, hover.Value) : Theme.Faint, Math.Max(1f, Theme.S(1))))
					{
						g.DrawLines(pen4, new Point[3]
						{
							new Point(sOffset - Theme.S(4), roundedValueOffset - Theme.S(2)),
							new Point(sOffset, roundedValueOffset + Theme.S(2)),
							new Point(sOffset + Theme.S(4), roundedValueOffset - Theme.S(2))
						});
					}
					Rectangle bounds2 = bounds;
					bounds2.Inflate(-Theme.S(2), -Theme.S(2));
					if (bounds2.Width > 0 && bounds2.Height > 0)
					{
						using (GraphicsPath path = CompactFrame(bounds2, Theme.S(7)))
						{
							using (Pen pen5 = new Pen(Col.Alpha(Theme.StrokeHi, Theme.LightMode ? 62 : 55)))
							{
								g.DrawPath(pen5, path);
							}
						}
					}
					using (Pen pen6 = new Pen(color, Math.Max(1f, Theme.S(2))))
					{
						g.DrawLine(pen6, bounds.Left + Theme.S(12), bounds.Top, scaledDimension - Theme.S(3), bounds.Top);
					}
					using (Pen pen7 = new Pen(Col.Alpha(color, (int)(110f + hover.Value * 105f))))
					{
						g.DrawLine(pen7, bounds.Right - Theme.S(9), bounds.Bottom, bounds.Right, bounds.Bottom - Theme.S(9));
					}
					PaintBusy(g, bounds, graphicsPath);
				}
				finally
				{
					g.Restore(gstate);
				}
				using (Pen pen8 = new Pen(Col.Lerp(Theme.StrokeHi, color, 0.34f + hover.Value * 0.4f + press.Value * 0.2f)))
				{
					g.DrawPath(pen8, graphicsPath);
				}
			}
			if (base.WantsFocusCue)
			{
				DsFocus.Draw(g, DsFocus.Inset(bounds, 4), Theme.Fg, Theme.Card);
			}
		}

		internal static string ModeName(PerformancePreset value)
		{
			if (PresetValue.From((int)value) != PerformancePreset.Competitive)
			{
				return Lang.T("preset.standard");
			}
			return Lang.T("preset.competitive");
		}
	}
}
