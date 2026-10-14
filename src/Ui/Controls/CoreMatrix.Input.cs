using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed partial class CoreMatrix
    {
        private bool editable;
        private int focusCpu;
        public event Action<ulong> SelectionChanged;
        public bool Editable
        {
            get { return editable; }
            set { editable = value; TabStop = value; Cursor = value ? Cursors.Hand : Cursors.Default; }
        }

        internal void ToggleSelection(ulong mask)
        {
            if (!Editable || !Enabled) return;
            mask &= allMask & (~DisabledMask | Selected);
            if (mask == 0) return;
            Selected = (Selected & mask) == mask ? Selected & ~mask : Selected | (mask & ~DisabledMask);
            if (SelectionChanged != null) SelectionChanged(Selected);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Editable || e.Button != MouseButtons.Left) return;
            Focus();
            foreach (Band band in bands) foreach (Group group in band.Groups)
            {
                foreach (Cell cell in group.Cells) if (cell.Rect.Contains(e.Location))
                { focusCpu = cell.Cpu; ToggleSelection(1UL << cell.Cpu); return; }
                if (group.Rect.Contains(e.Location)) { ToggleSelection(group.Mask); return; }
            }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return Editable && (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.Space)
                || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Editable || e.Modifiers != Keys.None) return;
            if (e.KeyCode == Keys.Space) ToggleSelection(1UL << focusCpu);
            else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Up || e.KeyCode == Keys.Right || e.KeyCode == Keys.Down)
            {
                int direction = e.KeyCode == Keys.Left || e.KeyCode == Keys.Up ? -1 : 1;
                for (int i = 0; i < 64; i++)
                {
                    focusCpu = (focusCpu + direction + 64) % 64;
                    if ((allMask & ~DisabledMask & (1UL << focusCpu)) != 0) break;
                }
            }
            else return;
            e.Handled = e.SuppressKeyPress = true;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        private void DrawInputFocus(Graphics g)
        {
            if (!Editable || !Focused) return;
            foreach (Band band in bands) foreach (Group group in band.Groups) foreach (Cell cell in group.Cells)
                if (cell.Cpu == focusCpu) ControlPaint.DrawFocusRectangle(g, cell.Rect, Theme.Fg, BackColor);
        }
    }
}
