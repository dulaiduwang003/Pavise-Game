using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal sealed class SettingsButton : PillButton
	{
		private readonly ToolTip tips = new ToolTip();

		public SettingsButton(string name, string description)
			: base("")
		{
			base.AccessibleName = name;
			base.AccessibleDescription = description;
			tips.SetToolTip(this, description);
		}

		protected override void OnPaint(PaintEventArgs e)
		{
			base.OnPaint(e);
			int scaledDimension = Theme.S(16);
			Glyphs.Draw(e.Graphics, "gear", new Rectangle((base.Width - scaledDimension) / 2, (base.Height - scaledDimension) / 2, scaledDimension, scaledDimension), base.Enabled ? Col.Lerp(Theme.Dim, Theme.Fg, hover.Value) : Theme.Faint);
		}

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				tips.Dispose();
			}
			base.Dispose(disposing);
		}
	}
}
