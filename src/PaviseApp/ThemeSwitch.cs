using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class ThemeSwitch : FxControl
	{
		private sealed class ThemeAccessibleObject : FxAccessibleObject
		{
			private readonly ThemeSwitch owner;

			public override AccessibleStates State
			{
				get
				{
					return base.State | (owner.lightOn ? AccessibleStates.Checked : AccessibleStates.None);
				}
			}

			internal ThemeAccessibleObject(ThemeSwitch control)
				: base(control)
			{
				owner = control;
			}

			public override void DoDefaultAction()
			{
				owner.OnClick(EventArgs.Empty);
			}
		}

		private bool lightOn;

		private Motion flash;

		public Action<bool> Toggled;

		internal bool Compact;

		public ThemeSwitch(bool light)
		{
			lightOn = light;
			Bg = Theme.Bg;
			base.TabStop = true;
			SetStyle(ControlStyles.Selectable, true);
			base.AccessibleRole = AccessibleRole.CheckButton;
			base.AccessibleName = Lang.T("set.light");
			base.AccessibleDescription = Lang.T("set.light.n");
			flash.Speed = 0.12f;
			flash.Set(0f);
		}

		public void SetSilently(bool light)
		{
			if (lightOn != light)
			{
				lightOn = light;
				Invalidate();
			}
		}

		protected override bool StepAll()
		{
			bool flag = base.StepAll();
			bool result = flash.Step();
			if (!flag)
			{
				return result;
			}
			return true;
		}

		protected override void OnClick(EventArgs e)
		{
			if (base.CanInteract)
			{
				base.OnClick(e);
				lightOn = !lightOn;
				flash.Set(1f);
				flash.To(0f);
				UiClock.Wake();
				Invalidate();
				if (base.IsHandleCreated)
				{
					AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
				}
				if (Toggled != null)
				{
					Toggled(lightOn);
				}
			}
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.Modifiers == Keys.None && (e.KeyCode == Keys.Return || e.KeyCode == Keys.Space))
			{
				e.Handled = true;
				e.SuppressKeyPress = true;
				OnClick(EventArgs.Empty);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			if (base.Width < Theme.S(24) || base.Height < Theme.S(20))
			{
				return;
			}
			float num = (lightOn ? 1f : 0f);
			float value = hover.Value;
			float value2 = flash.Value;
			if (Compact)
			{
				Rectangle rectangle = new Rectangle(0, Theme.S(2), base.Width - 1, base.Height - Theme.S(5));
				using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, Theme.S(5)))
				{
					using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, value)))
					{
						graphics.FillPath(brush, graphicsPath);
					}
					using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, value * 0.7f + value2 * 0.3f)))
					{
						graphics.DrawPath(pen, graphicsPath);
					}
					PaintBusy(graphics, rectangle, graphicsPath);
				}
				int num2 = Theme.S(21);
				Glyphs.Draw(graphics, lightOn ? "moon" : "sun", new Rectangle((base.Width - num2) / 2, (base.Height - num2) / 2 - Theme.S(1), num2, num2), Col.Lerp(Theme.Dim, Theme.Fg, 0.35f + value * 0.65f));
				if (base.WantsFocusCue)
				{
					DsFocus.Draw(graphics, DsFocus.Inset(rectangle, 3), Theme.Fg, Theme.Card);
				}
				return;
			}
			Rectangle rectangle2 = new Rectangle(0, Theme.S(2), base.Width - 1, base.Height - Theme.S(5));
			int num3 = Theme.S(9);
			using (GraphicsPath graphicsPath2 = Theme.TechPath(rectangle2, num3))
			{
				using (SolidBrush brush2 = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, value)))
				{
					graphics.FillPath(brush2, graphicsPath2);
				}
				using (Pen pen2 = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.3f + value * 0.5f)))
				{
					graphics.DrawPath(pen2, graphicsPath2);
				}
				PaintBusy(graphics, rectangle2, graphicsPath2);
			}
			using (Pen pen3 = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
			{
				graphics.DrawLine(pen3, 0, Theme.S(13), 0, base.Height - Theme.S(14));
			}
			using (Pen pen4 = new Pen(Col.Alpha(Theme.Accent, (int)(110f + 110f * value)), Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawLine(pen4, rectangle2.Right - num3, rectangle2.Top, rectangle2.Right, rectangle2.Top + num3);
			}
			int num4 = Theme.S(7);
			int num5 = Theme.S(7);
			RectangleF bounds = new RectangleF(rectangle2.X + num4, rectangle2.Y + num4, rectangle2.Width - num4 * 2, rectangle2.Height - num4 * 2);
			if (bounds.Width < (float)Theme.S(12) || bounds.Height < (float)Theme.S(8))
			{
				return;
			}
			using (GraphicsPath path = TechPathF(bounds, num5))
			{
				using (SolidBrush brush3 = new SolidBrush(Theme.Inset))
				{
					graphics.FillPath(brush3, path);
				}
				GraphicsState gstate = graphics.Save();
				graphics.SetClip(path, CombineMode.Intersect);
				using (Pen pen5 = new Pen(Col.Alpha(Theme.Accent, (int)(13f + 15f * value)), Math.Max(1f, Theme.S(1))))
				{
					for (float num6 = bounds.Left - bounds.Height; num6 < bounds.Right; num6 += (float)Theme.S(6))
					{
						graphics.DrawLine(pen5, num6, bounds.Bottom, num6 + bounds.Height, bounds.Top);
					}
				}
				graphics.Restore(gstate);
				using (Pen pen6 = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, 0.45f)))
				{
					graphics.DrawPath(pen6, path);
				}
			}
			float num7 = bounds.Width / 2f;
			RectangleF bounds2 = new RectangleF(bounds.X + num7 * num, bounds.Y, num7, bounds.Height);
			using (GraphicsPath path2 = TechPathF(bounds2, num5))
			{
				for (int num8 = 3; num8 >= 1; num8--)
				{
					using (Pen pen7 = new Pen(Col.Alpha(Theme.Accent, (int)((24f + 30f * value + 55f * value2) / (float)num8)), Theme.S(2) * num8))
					{
						graphics.DrawPath(pen7, path2);
					}
				}
				using (LinearGradientBrush brush4 = new LinearGradientBrush(new PointF(bounds2.Left, bounds2.Top - 1f), new PointF(bounds2.Right, bounds2.Bottom + 1f), Col.Lerp(Theme.Accent, Color.White, 0.12f + value2 * 0.32f), Theme.Accent2))
				{
					graphics.FillPath(brush4, path2);
				}
				using (Pen pen8 = new Pen(Col.Alpha(Color.White, (int)(64f + 90f * value2)), Math.Max(1f, Theme.S(1))))
				{
					graphics.DrawLine(pen8, bounds2.Left + (float)Theme.S(2), bounds2.Top + 0.5f, bounds2.Right - (float)num5 - (float)Theme.S(1), bounds2.Top + 0.5f);
				}
				using (Pen pen9 = new Pen(Col.Alpha(Theme.Accent, 235), Math.Max(1f, Theme.S(1))))
				{
					graphics.DrawPath(pen9, path2);
				}
			}
			int size = Theme.S(17);
			Color a = Col.Lerp(Theme.Faint, Theme.Fg, 0.25f + value * 0.45f);
			Glyphs.Draw(graphics, "moon", IconBox(bounds.X, bounds.Y, num7, bounds.Height, size), Col.Lerp(a, Theme.OnAccent, 1f - num));
			Glyphs.Draw(graphics, "sun", IconBox(bounds.X + num7, bounds.Y, num7, bounds.Height, size), Col.Lerp(a, Theme.OnAccent, num));
			if (!base.Enabled)
			{
				DsPaint.Veil(graphics, rectangle2, Col.Alpha(Theme.Card, DsAlpha.VeilTier));
			}
			if (base.WantsFocusCue)
			{
				DsFocus.Draw(graphics, DsFocus.Inset(rectangle2, 4), Theme.Fg, Theme.Card);
			}
		}

		private static Rectangle IconBox(float x, float y, float width, float height, int size)
		{
			return new Rectangle((int)Math.Round(x + (width - (float)size) / 2f), (int)Math.Round(y + (height - (float)size) / 2f), size, size);
		}

		private static GraphicsPath TechPathF(RectangleF bounds, float cut)
		{
			GraphicsPath graphicsPath = new GraphicsPath();
			cut = Math.Max(1f, Math.Min(cut, Math.Min(bounds.Width, bounds.Height) / 3f));
			graphicsPath.AddPolygon(new PointF[6]
			{
				new PointF(bounds.Left, bounds.Top),
				new PointF(bounds.Right - cut, bounds.Top),
				new PointF(bounds.Right, bounds.Top + cut),
				new PointF(bounds.Right, bounds.Bottom),
				new PointF(bounds.Left + cut, bounds.Bottom),
				new PointF(bounds.Left, bounds.Bottom - cut)
			});
			graphicsPath.CloseFigure();
			return graphicsPath;
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new ThemeAccessibleObject(this);
		}
	}
}
