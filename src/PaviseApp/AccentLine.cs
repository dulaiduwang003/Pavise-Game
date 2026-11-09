using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AccentLine : Control
    {
        public AccentLine()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            base.TabStop = false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (SolidBrush brush = new SolidBrush(Theme.Accent))
            {
                e.Graphics.FillRectangle(brush, base.ClientRectangle);
            }
        }
    }
}
