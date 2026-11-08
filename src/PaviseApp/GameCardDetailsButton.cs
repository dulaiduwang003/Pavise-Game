using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameCardDetailsButton : PillButton
	{
		private bool showAdjustLabel;

		internal bool ShowAdjustLabel
		{
			get { return showAdjustLabel; }
			set
			{
				showAdjustLabel = value;
				Text = value ? PerformancePolicy.Text("调整", "Adjust") : "›";
				Font = Theme.UI(value ? 8f : 13f, true);
				Invalidate();
			}
		}

		internal GameCardDetailsButton()
			: base("›")
		{
			Font = Theme.UI(13f, true);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			FamilySuppressionSwitch familySuppressionSwitch = base.Parent as FamilySuppressionSwitch;
			Color color = Col.Lerp(Theme.Inset, Theme.Accent, (familySuppressionSwitch != null && familySuppressionSwitch.Checked) ? 0.065f : 0.015f);
			using (SolidBrush brush = new SolidBrush(Col.Lerp(color, Theme.Accent, hover.Value * 0.12f)))
			{
				e.Graphics.FillRectangle(brush, base.ClientRectangle);
			}
			using (Pen pen = new Pen(Theme.Stroke))
			{
				e.Graphics.DrawLine(pen, 0, Theme.S(5), 0, base.Height - Theme.S(5));
			}
			if (showAdjustLabel)
			{
				TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Theme.S(8), 0, Math.Max(1, Width - Theme.S(22)), Height),
					Enabled ? Theme.Fg : Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
			}
			int remainingWidth = showAdjustLabel ? base.Width - Theme.S(10) : base.Width / 2;
			int heightRatio = base.Height / 2;
			e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen2 = new Pen((hover.Value > 0.1f) ? Theme.Accent : Theme.Dim, Math.Max(1f, Theme.S(1))))
			{
				e.Graphics.DrawLines(pen2, new Point[3]
				{
					new Point(remainingWidth - Theme.S(2), heightRatio - Theme.S(4)),
					new Point(remainingWidth + Theme.S(2), heightRatio),
					new Point(remainingWidth - Theme.S(2), heightRatio + Theme.S(4))
				});
			}
			if (Focused && ShowFocusCues)
			{
				ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -3, -3), Theme.Accent, color);
			}
		}
	}
}
