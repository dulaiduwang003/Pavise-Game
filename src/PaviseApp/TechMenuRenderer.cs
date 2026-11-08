using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class TechMenuRenderer : ToolStripRenderer
	{
		protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
		{
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.AntiAlias;
			Rectangle affectedBounds = e.AffectedBounds;
			affectedBounds.Width--;
			affectedBounds.Height--;
			DsPaint.TechFrame(graphics, affectedBounds, Theme.S(8), Theme.Card, Theme.Stroke);
			using (Pen pen = new Pen(Theme.Accent, Math.Max(1.6f, Theme.S(2))))
			{
				graphics.DrawLine(pen, affectedBounds.Left + Theme.S(2), affectedBounds.Top, affectedBounds.Left + Theme.S(26), affectedBounds.Top);
			}
		}

		protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
		{
		}

		protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
		{
			int left = Theme.S(12);
			using (Pen pen = new Pen(Theme.Stroke))
			{
				e.Graphics.DrawLine(pen, left, e.Item.Height / 2, e.Item.Width - left, e.Item.Height / 2);
			}
		}

		protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
		{
			if (!e.Item.Selected || !e.Item.Enabled)
			{
				return;
			}
			Graphics graphics = e.Graphics;
			graphics.SmoothingMode = SmoothingMode.None;
			Rectangle rect = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
			using (SolidBrush brush = new SolidBrush(Col.Alpha(Theme.Accent, 40)))
			{
				graphics.FillRectangle(brush, rect);
			}
			using (SolidBrush brush2 = new SolidBrush(Theme.Accent))
			{
				graphics.FillRectangle(brush2, new Rectangle(0, 0, Theme.S(3), rect.Height));
			}
		}

		protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
		{
			Graphics graphics = e.Graphics;
			MenuBadge menuBadge = e.Item.Tag as MenuBadge;
			Rectangle bounds = new Rectangle(Theme.S(12), 0, e.Item.Width - Theme.S(18), e.Item.Height);
			if (menuBadge == null || string.IsNullOrEmpty(menuBadge.Text))
			{
				TextRenderer.DrawText(graphics, e.Text, e.TextFont, bounds, e.TextColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				return;
			}
			Font font = DsType.MonoFor(DsMono.Badge, menuBadge.Text);
			int badgeWidthResult = DsPaint.BadgeWidth(graphics, menuBadge.Text, font);
			int scaledDimension = Theme.S(17);
			Rectangle pill = new Rectangle(bounds.Right - badgeWidthResult, (e.Item.Height - scaledDimension) / 2, badgeWidthResult, scaledDimension);
			bounds.Width -= badgeWidthResult + Theme.S(8);
			TextRenderer.DrawText(graphics, e.Text, e.TextFont, bounds, e.TextColor, TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
			DsPaint.Badge(graphics, pill, menuBadge.Text, font, menuBadge.Strong);
		}
	}
}
