using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class AdvancedBackBar : FxControl
	{
		public Action BackRequested;

		private readonly string caption;

		private readonly string moduleCode;

		public AdvancedBackBar(string caption = null, string moduleCode = "RETURN // MAIN")
		{
			Cursor = Cursors.Hand;
			base.TabStop = true;
			this.caption = caption ?? Lang.T("v20.advanced.back");
			this.moduleCode = moduleCode;
			base.AccessibleName = this.caption;
		}

		protected override void OnMouseClick(MouseEventArgs e)
		{
			base.OnMouseClick(e);
			if (e.Button == MouseButtons.Left && BackRequested != null)
			{
				BackRequested();
			}
		}

		protected override void OnKeyDown(KeyEventArgs e)
		{
			base.OnKeyDown(e);
			if (e.KeyCode == Keys.Return || e.KeyCode == Keys.Space)
			{
				if (BackRequested != null)
				{
					BackRequested();
				}
				e.Handled = true;
				e.SuppressKeyPress = true;
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.PaintOnCard(graphics, this, base.ClientRectangle);
			}
			else
			{
				using (SolidBrush brush = new SolidBrush(Theme.Nav))
				{
					graphics.FillRectangle(brush, base.ClientRectangle);
				}
			}
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
			using (Pen pen = new Pen(Theme.Stroke))
			{
				graphics.DrawLine(pen, 0, base.Height - 1, base.Width, base.Height - 1);
			}
			float value = hover.Value;
			float value2 = press.Value;
			Rectangle r = new Rectangle(Theme.S(14), Theme.S(11), base.Width - Theme.S(30), base.Height - Theme.S(23));
			Color a = Col.Lerp(Theme.Nav, Theme.Card, 0.72f + value * 0.18f);
			a = Col.Lerp(a, Theme.Accent, 0.025f + value * 0.07f);
			using (GraphicsPath graphicsPath = Theme.TechPath(r, Theme.S(8)))
			{
				using (SolidBrush brush2 = new SolidBrush(a))
				{
					graphics.FillPath(brush2, graphicsPath);
				}
				using (Pen pen2 = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.16f + value * 0.58f), Math.Max(1f, Theme.S(1))))
				{
					graphics.DrawPath(pen2, graphicsPath);
				}
				GraphicsState gstate = graphics.Save();
				graphics.SetClip(graphicsPath);
				int onPaintValue = r.Left + Theme.S(52) - (int)(value * (float)Theme.S(5));
				Point[] points = new Point[4]
				{
					new Point(r.Left, r.Top),
					new Point(onPaintValue, r.Top),
					new Point(onPaintValue - Theme.S(20), r.Bottom),
					new Point(r.Left, r.Bottom)
				};
				using (LinearGradientBrush brush3 = new LinearGradientBrush(new Rectangle(r.Left, r.Top, Math.Max(1, onPaintValue - r.Left), r.Height), Col.Alpha(Theme.Accent, (int)(16f + value * 38f)), Col.Alpha(Theme.Accent, 0), LinearGradientMode.Horizontal))
				{
					graphics.FillPolygon(brush3, points);
				}
				graphics.Restore(gstate);
			}
			int scaledDimension = Theme.S(26);
			int onPaintValue2 = (int)(value * (float)Theme.S(2)) - (int)(value2 * (float)Theme.S(1));
			Rectangle r2 = new Rectangle(r.Left + Theme.S(8), r.Top + (r.Height - scaledDimension) / 2, scaledDimension, scaledDimension);
			using (GraphicsPath path = Theme.TechPath(r2, Theme.S(5)))
			{
				using (SolidBrush brush4 = new SolidBrush(Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + value * 0.24f)))
				{
					graphics.FillPath(brush4, path);
				}
				using (Pen pen3 = new Pen(Col.Alpha(Theme.Accent, (int)(78f + value * 106f))))
				{
					graphics.DrawPath(pen3, path);
				}
			}
			int remainingOnPaintValue2 = r2.Left + r2.Width / 2 - onPaintValue2;
			int height = r2.Top + r2.Height / 2;
			using (Pen pen4 = new Pen(Theme.Accent, Math.Max(1.6f, (float)Theme.S(2) * 0.85f)))
			{
				pen4.StartCap = LineCap.Round;
				pen4.EndCap = LineCap.Round;
				pen4.LineJoin = LineJoin.Round;
				int scaledDimension2 = Theme.S(4);
				graphics.DrawLine(pen4, remainingOnPaintValue2 + Theme.S(1), height - scaledDimension2, remainingOnPaintValue2 - Theme.S(3), height);
				graphics.DrawLine(pen4, remainingOnPaintValue2 - Theme.S(3), height, remainingOnPaintValue2 + Theme.S(1), height + scaledDimension2);
				using (Pen pen5 = new Pen(Col.Alpha(Theme.Accent, (int)(110f + value * 90f)), Math.Max(1.2f, (float)Theme.S(2) * 0.65f)))
				{
					pen5.StartCap = LineCap.Round;
					pen5.EndCap = LineCap.Round;
					pen5.LineJoin = LineJoin.Round;
					graphics.DrawLine(pen5, remainingOnPaintValue2 + Theme.S(6), height - scaledDimension2, remainingOnPaintValue2 + Theme.S(2), height);
					graphics.DrawLine(pen5, remainingOnPaintValue2 + Theme.S(2), height, remainingOnPaintValue2 + Theme.S(6), height + scaledDimension2);
				}
			}
			int rightOffset = r2.Right + Theme.S(10);
			TextRenderer.DrawText(graphics, moduleCode, Theme.Mono(5.5f), new Rectangle(rightOffset, r.Top + Theme.S(4), r.Right - rightOffset - Theme.S(6), Theme.S(12)), Theme.Faint, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			TextRenderer.DrawText(graphics, caption, Theme.UI(9.2f, true), new Rectangle(rightOffset, r.Top + Theme.S(15), r.Right - rightOffset - Theme.S(6), Theme.S(20)), Col.Lerp(Theme.Dim, Theme.Fg, 0.55f + value * 0.45f), TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			int top = r.Bottom - Theme.S(1);
			int sOffset = Theme.S(26) + (int)(value * (float)Theme.S(34));
			using (Pen pen6 = new Pen(Col.Alpha(Theme.Accent, (int)(92f + value * 120f)), Math.Max(1f, Theme.S(1))))
			{
				graphics.DrawLine(pen6, r.Right - Theme.S(8) - sOffset, top, r.Right - Theme.S(8), top);
			}
			using (SolidBrush brush5 = new SolidBrush(Theme.Accent))
			{
				graphics.FillRectangle(brush5, r.Right - Theme.S(10), top - Theme.S(2), Theme.S(4), Theme.S(4));
			}
		}
	}
}
