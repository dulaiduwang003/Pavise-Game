using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class GameCardDetailsButton : PillButton
	{
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
			int num = base.Width / 2;
			int num2 = base.Height / 2;
			e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
			using (Pen pen2 = new Pen((hover.Value > 0.1f) ? Theme.Accent : Theme.Dim, Math.Max(1f, Theme.S(1))))
			{
				e.Graphics.DrawLines(pen2, new Point[3]
				{
					new Point(num - Theme.S(2), num2 - Theme.S(4)),
					new Point(num + Theme.S(2), num2),
					new Point(num - Theme.S(2), num2 + Theme.S(4))
				});
			}
			if (Focused && ShowFocusCues)
			{
				ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(base.ClientRectangle, -3, -3), Theme.Accent, color);
			}
		}
	}
}
