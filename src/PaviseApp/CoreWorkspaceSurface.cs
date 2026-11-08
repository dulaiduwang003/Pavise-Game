using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class CoreWorkspaceSurface : DBPanel
	{
		internal int Divider1;

		internal int Divider2;

		internal CoreWorkspaceSurface()
		{
			Tag = "backdrop-card";
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Theme.Card;
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			PaintCoreFrame(e.Graphics, base.ClientRectangle, this);
			using (Pen pen = new Pen(Theme.Stroke))
			{
				int[] integerValues = new int[2] { Divider1, Divider2 };
				foreach (int numericValueEntry in integerValues)
				{
					if (numericValueEntry > 0)
					{
						e.Graphics.DrawLine(pen, Theme.S(22), numericValueEntry + base.AutoScrollPosition.Y, Math.Max(Theme.S(22), base.Width - Theme.S(22)), numericValueEntry + base.AutoScrollPosition.Y);
					}
				}
			}
		}

		internal static void PaintCoreFrame(Graphics graphics, Rectangle bounds, Control owner = null)
		{
			if (owner != null && Backdrop.AppliesTo(owner)) Backdrop.Paint(graphics, owner, bounds);
			else using (SolidBrush brush = new SolidBrush(Theme.Bg))
			{
				graphics.FillRectangle(brush, bounds);
			}
			if (bounds.Width < 2 || bounds.Height < 2)
			{
				return;
			}
			GraphicsState gstate = graphics.Save();
			try
			{
				graphics.SmoothingMode = SmoothingMode.AntiAlias;
				Rectangle r = new Rectangle(bounds.X, bounds.Y, bounds.Width - 1, bounds.Height - 1);
				using (GraphicsPath path = Theme.TechPath(r, Theme.S(10)))
				{
					using (SolidBrush brush2 = new SolidBrush(owner != null ? Backdrop.CardFill(owner, Theme.Card) : Theme.Card))
					{
						graphics.FillPath(brush2, path);
					}
					using (Pen pen = new Pen(Col.Lerp(Theme.Card, Theme.StrokeHi, 0.68f)))
					{
						graphics.DrawPath(pen, path);
					}
				}
				using (Pen pen2 = new Pen(Col.Alpha(Theme.Accent, 155), Math.Max(1, Theme.S(1))))
				{
					graphics.DrawLine(pen2, r.Left + Theme.S(1), r.Top, Math.Min(r.Right, r.Left + Theme.S(54)), r.Top);
					graphics.DrawLine(pen2, Math.Max(r.Left, r.Right - Theme.S(10)), r.Top, r.Right, Math.Min(r.Bottom, r.Top + Theme.S(10)));
				}
			}
			finally
			{
				graphics.Restore(gstate);
			}
		}
	}
}
