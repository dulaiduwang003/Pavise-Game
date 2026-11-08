using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PerformanceInfoButton : PillButton
	{
		private sealed class InformationAccessibility : FxAccessibleObject
		{
			private readonly PerformanceInfoButton button;

			public override string DefaultAction
			{
				get
				{
					return PerformancePolicy.Text("查看详情", "Show details");
				}
			}

			internal InformationAccessibility(PerformanceInfoButton owner)
				: base(owner)
			{
				button = owner;
			}

			public override void DoDefaultAction()
			{
				button.PerformClick();
			}
		}

		internal PerformanceInfoButton()
			: base("i")
		{
			base.AccessibleRole = AccessibleRole.PushButton;
		}

		protected override AccessibleObject CreateAccessibilityInstance()
		{
			return new InformationAccessibility(this);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			int minLimit = Math.Min(Theme.S(15), Math.Min(base.Width - 4, base.Height - 4));
			Rectangle rectangle = new Rectangle((base.Width - minLimit) / 2, (base.Height - minLimit) / 2, minLimit, minLimit);
			Color color = (Focused ? Theme.Accent : Col.Lerp(Theme.Faint, Theme.Accent, hover.Value));
			using (Pen pen = new Pen(color, Math.Max(1f, Dpi.Scale)))
			{
				graphics.DrawEllipse(pen, rectangle);
			}
			TextRenderer.DrawText(graphics, "i", Theme.UI(8f, true), rectangle, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			if (base.WantsFocusCue)
			{
				ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(base.ClientRectangle, -2, -2), Theme.Accent, Theme.Card);
			}
		}
	}
}
