using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class StandbyCleanerActionHost : RoundPanel
	{
		protected override void OnLayout(LayoutEventArgs e)
		{
			base.OnLayout(e);
			foreach (Control control in base.Controls)
			{
				if (control is Toggle)
				{
					control.Location = new Point(Math.Max(0, base.ClientSize.Width - control.Width), Math.Max(0, (base.ClientSize.Height - control.Height) / 2));
				}
			}
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			RoundPanel roundPanel = base.Parent as RoundPanel;
			Fill = ((roundPanel == null) ? Theme.Card : roundPanel.Fill);
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.PaintOnCard(e.Graphics, this, e.ClipRectangle);
				return;
			}
			using (SolidBrush brush = new SolidBrush(Fill))
			{
				e.Graphics.FillRectangle(brush, e.ClipRectangle);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
		}
	}
}
