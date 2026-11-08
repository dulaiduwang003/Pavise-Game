using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class EmptyStatePanel : RoundPanel
	{
		public bool ShowEmpty;

		public string EmptyTitle = "";

		public string EmptyDetail = "";

		public string EmptyGlyph = "game";

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			if (ShowEmpty)
			{
				int widthRatio = base.ClientSize.Width / 2;
				int heightRatio = base.ClientSize.Height / 2;
				Glyphs.Draw(e.Graphics, EmptyGlyph, new Rectangle(widthRatio - Theme.S(18), heightRatio - Theme.S(72), Theme.S(36), Theme.S(36)), Theme.Accent);
				TextRenderer.DrawText(e.Graphics, EmptyTitle, Theme.UI(9.25f, true), new Rectangle(Theme.S(24), heightRatio - Theme.S(26), base.ClientSize.Width - Theme.S(48), Theme.S(24)), Theme.Fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
				TextRenderer.DrawText(e.Graphics, EmptyDetail, Theme.UI(8.4f, false), new Rectangle(Theme.S(42), heightRatio + Theme.S(8), base.ClientSize.Width - Theme.S(84), Theme.S(54)), Theme.Faint, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
			}
		}
	}
}
