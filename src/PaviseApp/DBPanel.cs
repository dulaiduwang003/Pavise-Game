using System.Windows.Forms;

namespace PaviseApp
{
	internal class DBPanel : Panel
	{
		public DBPanel()
		{
			DoubleBuffered = true;
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			if (Backdrop.AppliesTo(this))
			{
				Backdrop.Paint(e.Graphics, this, e.ClipRectangle);
			}
			else
			{
				base.OnPaintBackground(e);
			}
		}
	}
}
