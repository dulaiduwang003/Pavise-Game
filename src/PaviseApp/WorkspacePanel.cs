using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
	internal class WorkspacePanel : DBPanel
	{
		public WorkspacePanel()
		{
			SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
			BackColor = Theme.Bg;
		}

		protected override void OnPaintBackground(PaintEventArgs e)
		{
			PaintSurface(e.Graphics, e.ClipRectangle);
		}

		internal void PaintSurface(Graphics graphics, Rectangle clip)
		{
			WorkspaceCanvasSurface.Paint(graphics, this, clip);
		}
	}
}
