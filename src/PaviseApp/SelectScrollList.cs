using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class SelectScrollList : ListBox
    {
        private readonly TechSelect owner;
        private string prefix = "";
        private int prefixTick;
        private int wheelRemainder;

        internal SelectScrollList(TechSelect owner, string[] choices, int width, int height)
        {
            this.owner = owner;
            BorderStyle = BorderStyle.None; IntegralHeight = false;
            DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = Theme.S(34);
            BackColor = Theme.Card; ForeColor = Theme.Fg; Font = owner.Font;
            Size = new Size(width, height); Margin = Padding.Empty;
            ScrollAlwaysVisible = true; HorizontalScrollbar = false;
            AccessibleName = owner.AccessibleName; AccessibleRole = AccessibleRole.List;
            Items.AddRange(choices); SelectedIndex = owner.SelectedIndex;
            Click += delegate { if (SelectedIndex >= 0) owner.SelectIndex(SelectedIndex); };
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            bool highlighted = (e.State & DrawItemState.Selected) != 0;
            using (var fill = new SolidBrush(highlighted ? Theme.CardHover : Theme.Card)) e.Graphics.FillRectangle(fill, e.Bounds);
            Rectangle text = Rectangle.Inflate(e.Bounds, -Theme.S(10), 0);
            bool current = e.Index == owner.SelectedIndex;
            string badge = current ? owner.CurrentBadge : "";
            int badgeWidth = String.IsNullOrEmpty(badge) ? 0 : TextRenderer.MeasureText(badge, Font).Width + Theme.S(16);
            text.Width = Math.Max(1, text.Width - badgeWidth);
            TextRenderer.DrawText(e.Graphics, (string)Items[e.Index], Font, text, current ? Theme.Accent : Theme.Fg,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (badgeWidth > 0)
                TextRenderer.DrawText(e.Graphics, badge, Font, new Rectangle(text.Right, e.Bounds.Top, badgeWidth, e.Bounds.Height), Theme.Accent,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (highlighted && Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(e.Bounds, -2, -2), Theme.Dim, Theme.CardHover);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End
                || key == Keys.PageUp || key == Keys.PageDown || base.IsInputKey(keyData);
        }

        private bool Navigate(Keys keys)
        {
            Keys key = keys & Keys.KeyCode;
            if (key == Keys.Enter) { if (SelectedIndex >= 0) owner.SelectIndex(SelectedIndex); return true; }
            if (key == Keys.Escape) { owner.CloseDropDown(); owner.Focus(); return true; }
            if (key == Keys.Tab)
            {
                owner.CloseDropDown();
                Form form = owner.FindForm();
                if (form != null) form.SelectNextControl(owner, (keys & Keys.Shift) == 0, true, true, true);
                return true;
            }
            int next = Math.Max(0, SelectedIndex), page = Math.Max(1, ClientSize.Height / ItemHeight - 1);
            switch (key)
            {
                case Keys.Up: next--; break;
                case Keys.Down: next++; break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = Items.Count - 1; break;
                case Keys.PageUp: next -= page; break;
                case Keys.PageDown: next += page; break;
                default: return false;
            }
            if (Items.Count > 0) SelectedIndex = Math.Max(0, Math.Min(Items.Count - 1, next));
            return true;
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        { return Navigate(keyData) || base.ProcessCmdKey(ref message, keyData); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (Navigate(e.KeyData)) { e.Handled = e.SuppressKeyPress = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (!Char.IsControl(e.KeyChar))
            {
                int now = Environment.TickCount;
                prefix = unchecked((uint)(now - prefixTick)) > 1000 ? e.KeyChar.ToString() : prefix + e.KeyChar;
                prefixTick = now;
                int index = FindString(prefix);
                if (index < 0) { prefix = e.KeyChar.ToString(); index = FindString(prefix, SelectedIndex); }
                if (index >= 0) SelectedIndex = index;
                e.Handled = true; return;
            }
            base.OnKeyPress(e);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            int visible = Math.Max(1, ClientSize.Height / ItemHeight);
            int lines = SystemInformation.MouseWheelScrollLines;
            int amount = lines < 0 ? visible : Math.Max(0, lines);
            wheelRemainder += e.Delta;
            int notches = wheelRemainder / 120; wheelRemainder %= 120;
            int steps = Math.Abs(notches) * amount;
            if (steps > 0) TopIndex = Math.Max(0, Math.Min(Math.Max(0, Items.Count - visible), TopIndex + (notches < 0 ? steps : -steps)));
            var handled = e as HandledMouseEventArgs;
            if (handled != null) handled.Handled = true;
        }
    }
}
