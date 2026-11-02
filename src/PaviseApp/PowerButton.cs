using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PowerButton : FxControl
	{
		private bool active;

		private string label = "";

		public Action Clicked;

		public PowerButton()
		{
			Bg = Theme.Bg;
			base.TabStop = false;
			SetStyle(ControlStyles.Selectable, false);
			base.AccessibleName = Lang.T("power.name");
		}

		public void SetState(bool on, string text)
		{
			string text2 = text ?? "";
			if (active != on || !(label == text2))
			{
				active = on;
				label = text2;
				Invalidate();
			}
		}

		protected override void OnClick(EventArgs e)
		{
			base.OnClick(e);
			if (Clicked != null)
			{
				Clicked();
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			float value = hover.Value;
			Rectangle r = new Rectangle(0, Theme.S(2), base.Width - 1, base.Height - Theme.S(5));
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(9)))
			{
				using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, value)))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, (active ? 0.46f : 0.3f) + value * 0.45f)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			TextRenderer.DrawText(graphics, label, Theme.UI(9.25f, true), new Rectangle(Theme.S(14), Theme.S(5), base.Width - Theme.S(42), Theme.S(22)), active ? Theme.Fg : Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			TextRenderer.DrawText(graphics, Lang.T("plan.button.hint"), Theme.UI(7.25f, false), new Rectangle(Theme.S(14), Theme.S(24), base.Width - Theme.S(42), Theme.S(15)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
			PointF[] points = new PointF[3]
			{
				new PointF(base.Width - Theme.S(25), Theme.S(19)),
				new PointF(base.Width - Theme.S(20), Theme.S(24)),
				new PointF(base.Width - Theme.S(15), Theme.S(19))
			};
			using (Pen pen2 = new Pen(Theme.Dim, Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawLines(pen2, points);
			}
		}
	}
}
