using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class ModeChoice : FxControl
	{
		private readonly PerformancePreset mode;

		private bool selected;

		private Motion pick;

		public Action<PerformancePreset> Chosen;

		public PerformancePreset Mode
		{
			get
			{
				return mode;
			}
		}

		public ModeChoice(PerformancePreset value)
		{
			mode = value;
			Bg = Theme.Card;
			pick.Speed = 0.3f;
			Cursor = Cursors.Hand;
		}

		public void SetSelected(bool value)
		{
			if (selected != value)
			{
				selected = value;
				if (base.IsHandleCreated)
				{
					pick.To(value ? 1f : 0f);
					UiClock.Wake();
				}
				else
				{
					pick.Set(value ? 1f : 0f);
				}
				Invalidate();
			}
		}

		protected override bool StepAll()
		{
			bool flag = base.StepAll();
			bool result = pick.Step();
			if (!flag)
			{
				return result;
			}
			return true;
		}

		protected override void OnClick(EventArgs e)
		{
			base.OnClick(e);
			if (Chosen != null)
			{
				Chosen(mode);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Color color = Theme.ModeColor(mode);
			float value = pick.Value;
			Rectangle r = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(8)))
			{
				Color color2 = Col.Lerp(Col.Lerp(Theme.Card, Theme.CardHover, hover.Value * 0.7f), Col.Lerp(Theme.Card, color, 0.12f), value);
				using (SolidBrush brush = new SolidBrush(color2))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Lerp(Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover.Value), Col.Alpha(color, 210), value)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			using (SolidBrush brush2 = new SolidBrush(color))
			{
				graphics.FillEllipse(brush2, Theme.S(15), Theme.S(15), Theme.S(8), Theme.S(8));
			}
			TextRenderer.DrawText(graphics, ModeButton.ModeName(mode), Theme.UI(9.75f, true), new Rectangle(Theme.S(34), Theme.S(7), Theme.S(100), Theme.S(24)), Theme.Fg, TextFormatFlags.VerticalCenter);
			TextRenderer.DrawText(graphics, DetailKey(mode), Theme.UI(7.9f, false), new Rectangle(Theme.S(34), Theme.S(28), base.Width - Theme.S(50), base.Height - Theme.S(30)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);
			if (value > 0.01f)
			{
				DrawCheck(graphics, color, value);
			}
		}

		private void DrawCheck(Graphics g, Color accent, float t)
		{
			Rectangle rectangle = new Rectangle(base.Width - Theme.S(31), Theme.S(15), Theme.S(16), Theme.S(16));
			PointF pointF = new PointF(rectangle.X + Theme.S(2), rectangle.Y + Theme.S(8));
			PointF pointF2 = new PointF(rectangle.X + Theme.S(6), rectangle.Y + Theme.S(12));
			PointF to = new PointF(rectangle.X + Theme.S(14), rectangle.Y + Theme.S(3));
			int a = (int)(255f * Math.Min(1f, t * 2.5f));
			using (Pen pen = new Pen(Col.Alpha(accent, a), Math.Max(1.4f, Theme.S(1))))
			{
				pen.StartCap = LineCap.Round;
				pen.EndCap = LineCap.Round;
				pen.LineJoin = LineJoin.Round;
				if (t < 0.45f)
				{
					g.DrawLine(pen, pointF, Between(pointF, pointF2, t / 0.45f));
					return;
				}
				g.DrawLines(pen, new PointF[3]
				{
					pointF,
					pointF2,
					Between(pointF2, to, (t - 0.45f) / 0.55f)
				});
			}
		}

		private static PointF Between(PointF from, PointF to, float t)
		{
			if (t < 0f)
			{
				t = 0f;
			}
			else if (t > 1f)
			{
				t = 1f;
			}
			return new PointF(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
		}

		private string DetailKey(PerformancePreset value)
		{
			if (PresetValue.From((int)value) == PerformancePreset.Competitive)
			{
				return Lang.T("mode.pick.competitive");
			}
			return Lang.T("mode.pick.standard");
		}
	}
}
