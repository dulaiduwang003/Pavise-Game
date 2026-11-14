using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class SettingsWorkspaceCategoryPanel : DBPanel
    {
        internal SettingsWorkspaceCategoryPanel() { Tag = "backdrop-card"; }
        internal int DivisionX;

        internal int DivisionBottom;

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            DsPaint.CardBackground(e.Graphics, this, Theme.Card);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (DivisionX > 0 && DivisionBottom > 0)
            {
                using (Pen pen = new Pen(Col.Lerp(Theme.Card, Theme.Stroke, 0.62f)))
                {
                    e.Graphics.DrawLine(pen, DivisionX, Theme.S(8), DivisionX, DivisionBottom);
                }
            }
        }
    }
}
