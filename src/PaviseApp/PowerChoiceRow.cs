using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class PowerChoiceRow : FxControl
	{
		public readonly string Id;

		private readonly string label;

		private readonly MenuBadge badge;

		private readonly bool selected;

		public Action<string> Chosen;

		public PowerChoiceRow(string id, string text, MenuBadge tag, bool on)
		{
			Id = id;
			label = text;
			badge = tag;
			selected = on;
			Bg = Theme.Card;
		}

		protected override void OnClick(EventArgs e)
		{
			base.OnClick(e);
			if (Chosen != null)
			{
				Chosen(Id);
			}
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			Graphics graphics = e.Graphics;
			FillBg(graphics);
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			float value = hover.Value;
			Rectangle r = new Rectangle(0, 0, base.Width - 1, base.Height - 1);
			using (GraphicsPath path = Theme.TechPath(r, Theme.S(8)))
			{
				Color color = (selected ? Col.Lerp(Theme.Card, Theme.Accent, 0.13f) : Col.Lerp(Theme.Card, Theme.CardHover, value));
				using (SolidBrush brush = new SolidBrush(color))
				{
					graphics.FillPath(brush, path);
				}
				using (Pen pen = new Pen(selected ? Col.Alpha(Theme.Accent, 215) : Col.Lerp(Theme.Stroke, Theme.StrokeHi, value)))
				{
					graphics.DrawPath(pen, path);
				}
			}
			int num = Theme.S(8);
			int num2 = base.Height / 2;
			if (selected)
			{
				using (SolidBrush brush2 = new SolidBrush(Theme.Accent))
				{
					graphics.FillEllipse(brush2, Theme.S(14), num2 - num / 2, num, num);
				}
			}
			else
			{
				using (Pen pen2 = new Pen(Theme.Faint))
				{
					graphics.DrawEllipse(pen2, Theme.S(14), num2 - num / 2, num, num);
				}
			}
			int num3 = 0;
			if (badge != null)
			{
				Font font = Theme.MonoFor(badge.Text, 7f);
				num3 = TextRenderer.MeasureText(graphics, badge.Text, font).Width + Theme.S(12);
				Rectangle rectangle = new Rectangle(base.Width - Theme.S(14) - num3, num2 - Theme.S(17) / 2, num3, Theme.S(17));
				Color color2 = (badge.Strong ? Theme.Accent : Theme.Inset);
				Color color3 = (badge.Strong ? Col.Alpha(Theme.Accent, 235) : Theme.Stroke);
				Color foreColor = (badge.Strong ? Theme.OnAccent : Theme.Faint);
				using (GraphicsPath path2 = Theme.TechPath(rectangle, Theme.S(4)))
				{
					using (SolidBrush brush3 = new SolidBrush(color2))
					{
						graphics.FillPath(brush3, path2);
					}
					using (Pen pen3 = new Pen(color3))
					{
						graphics.DrawPath(pen3, path2);
					}
				}
				TextRenderer.DrawText(graphics, badge.Text, font, rectangle, foreColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				num3 += Theme.S(10);
			}
			TextRenderer.DrawText(bounds: new Rectangle(Theme.S(32), 0, base.Width - Theme.S(48) - num3, base.Height), dc: graphics, text: label, font: Theme.UI(9.25f, selected), foreColor: selected ? Theme.Fg : Theme.Dim, flags: TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
		}
	}
}
