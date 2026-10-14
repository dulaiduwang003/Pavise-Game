using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class QuantumProfilePicker : FxControl
    {
        private int selected;
        private ToolStripDropDown popup;
        private ProfileList list;
        private bool open;
        private readonly DropdownHost dropdown;
#if PAVISE_SELFTEST
        internal static Action<ToolStripDropDown> PopupCreatedForTest;
#endif
        internal event EventHandler SelectionChanged;
        internal int SelectedIndex { get { return selected; } }
        internal void SetSilently(int index)
        {
            if (IsDisposed || index < 0 || index >= QuantumProfiles.Values.Length) return;
            selected = index;
            AccessibleDescription = QuantumProfiles.Description(index);
            if (list != null && !list.IsDisposed) list.SetSelected(index);
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            Invalidate();
        }
        internal QuantumProfilePicker()
        {
            Size = new Size(Theme.S(230), Theme.S(38));
            TabStop = true; AccessibleRole = AccessibleRole.ComboBox;
            dropdown = new DropdownHost(this) { AlignRight = true };
            dropdown.Closed += delegate { open = false; if (!IsDisposed) Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            FillBg(e.Graphics);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(7))) {
                using (var brush = new SolidBrush(Col.Lerp(Theme.Inset, Theme.CardHover, open ? 1f : hover.Value))) e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Enabled && (open || Focused) ? Theme.Accent : Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover.Value))) e.Graphics.DrawPath(pen, path);
                PaintBusy(e.Graphics, ClientRectangle, path);
            }
            TextRenderer.DrawText(e.Graphics, QuantumProfiles.Title(selected), Theme.UI(9.5f, false),
                new Rectangle(Theme.S(11), 0, Width - Theme.S(40), Height), Enabled ? Theme.Fg : Theme.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            int x = Width - Theme.S(21), y = Height / 2;
            using (var pen = new Pen(Enabled ? Theme.Accent : Theme.Dim, Theme.S(2))) {
                int d = Theme.S(4), sign = open ? -1 : 1;
                e.Graphics.DrawLines(pen, new[] { new Point(x - d, y - sign * d / 2), new Point(x, y + sign * d / 2), new Point(x + d, y - sign * d / 2) });
            }
        }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && CanInteract) { dropdown.NoteMouseDown(); Focus(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            // A press dragged outside emits no Click; do not consume the next ordinary click.
            if (e.Button == MouseButtons.Left) dropdown.NoteMouseUp();
        }
        protected override void OnClick(EventArgs e)
        {
            if (!CanInteract) return;
            base.OnClick(e);
            if (!CanInteract) return;
            if (dropdown.ConsumeCloseClick() || open) Close(); else Open();
        }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (dropdown.HandleCommandKey(keyData, false, delegate { if (open) Close(); else Open(); })) return true;
            return base.ProcessCmdKey(ref msg, keyData);
        }
        private void Open()
        {
            if (!CanInteract || !Visible || open) return;
            var next = new ContextMenuStrip { Padding = Padding.Empty, Margin = Padding.Empty, BackColor = Theme.Card,
                AutoSize = false, AutoClose = true, ShowImageMargin = false, ShowCheckMargin = false, DropShadowEnabled = false };
            popup = next;
#if PAVISE_SELFTEST
            if (PopupCreatedForTest != null) PopupCreatedForTest(popup);
#endif
            var choices = new ProfileList(selected) { CanInteract = delegate { return this.CanInteract && ReferenceEquals(dropdown.Menu, next); } };
            list = choices;
            Rectangle screen = Screen.FromControl(this).WorkingArea, anchor = RectangleToScreen(ClientRectangle);
            int below = Math.Max(0, screen.Bottom - anchor.Bottom - Theme.S(6));
            int above = Math.Max(0, anchor.Top - screen.Top - Theme.S(6));
            Size fit = DsPopup.Fit(new Size(Theme.S(460), choices.Height + 2), screen, Theme.S(12), 1);
            int height = Math.Min(choices.Height, Math.Max(above, below) - 2);
            choices.Size = new Size(Math.Max(1, fit.Width - 2), Math.Max(1, Math.Min(fit.Height - 2, Math.Max(Theme.S(80), height))));
            choices.EnsureActiveVisible();
            next.Items.Add(new ToolStripControlHost(choices) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = choices.Size });
            next.Size = new Size(choices.Width + 2, choices.Height + 2);
            choices.Picked += delegate(int index) {
                if (!CanInteract || !ReferenceEquals(dropdown.Menu, next)) return;
                Close();
                if (!CanInteract) return;
                Focus(); SetSilently(index);
                if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            };
            choices.Cancelled += delegate { if (!CanInteract || !ReferenceEquals(dropdown.Menu, next)) return; Close(); if (CanInteract) Focus(); };
            choices.Tabbed += delegate(bool forward) {
                if (!CanInteract || !ReferenceEquals(dropdown.Menu, next)) return;
                Close(); if (!CanInteract) return;
                Focus(); if (Parent != null) Parent.SelectNextControl(this, forward, true, true, true);
            };
            dropdown.Show(next);
            open = ReferenceEquals(dropdown.Menu, next);
#if !PAVISE_SELFTEST
            if (open && CanInteract) choices.Focus();
#endif
            Invalidate();
        }
        private void Close() { dropdown.Close(); open = false; }
        protected override void Dispose(bool disposing) { if (disposing) dropdown.Dispose(); base.Dispose(disposing); }

        private sealed class ProfileList : Control
        {
            private static readonly int[] order = { 0, 1, 8, 2, 3, 4, 5, 6, 7 };
            private int active, selected, hoverRow = -1, pressedRow = -1, offset;
            private int dragY = -1, dragOffset;
            private bool keyboard = true;
            private readonly int row = Theme.S(56), header = Theme.S(32), footer = Theme.S(28);
            internal event Action<int> Picked;
            internal event Action Cancelled;
            internal event Action<bool> Tabbed;
            internal Func<bool> CanInteract;
            private bool Interactive { get { return !IsDisposed && Enabled && (CanInteract == null || CanInteract()); } }
            internal ProfileList(int selected)
            {
                this.selected = selected; active = Array.IndexOf(order, selected);
                Size = new Size(Theme.S(458), header + row * order.Length + footer);
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand; TabStop = true; AccessibleRole = AccessibleRole.List;
                AccessibleName = AutomaticPolicy.Text("选择前台时间片", "Choose foreground time slice");
            }
            internal void SetSelected(int index) { selected = index; Invalidate(); }
            private int ViewHeight { get { return Math.Max(1, Height - header - footer); } }
            private int MaxOffset { get { return Math.Max(0, row * order.Length - ViewHeight); } }
            private int ThumbHeight { get { return Math.Min(ViewHeight, Math.Max(Theme.S(20), ViewHeight * ViewHeight / (row * order.Length))); } }
            internal void EnsureActiveVisible()
            {
                if (active * row < offset) offset = active * row;
                if ((active + 1) * row > offset + ViewHeight) offset = (active + 1) * row - ViewHeight;
                offset = Math.Max(0, Math.Min(MaxOffset, offset));
            }
            private int Hit(Point point)
            {
                if (point.X < 0 || point.X >= Width - Theme.S(8) || point.Y < header || point.Y >= Height - footer) return -1;
                int index = (point.Y - header + offset) / row; return index < order.Length ? index : -1;
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Card); g.SmoothingMode = SmoothingMode.AntiAlias;
                TextRenderer.DrawText(g, AutomaticPolicy.Text("按使用场景选择", "Choose for your workload"), Theme.UI(9, true), new Rectangle(Theme.S(12), 0, Width - Theme.S(24), header), Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                var state = g.Save(); g.SetClip(new Rectangle(0, header, Width, ViewHeight));
                for (int position = 0; position < order.Length; position++) {
                    int index = order[position], top = header + position * row - offset;
                    bool chosen = index == selected, highlighted = keyboard ? position == active : position == hoverRow;
                    var rect = new Rectangle(Theme.S(5), top + Theme.S(2), Width - Theme.S(16), row - Theme.S(4));
                    if (chosen || highlighted) Theme.FillRound(g, rect, Theme.S(6), chosen ? Col.Lerp(Theme.Card, Theme.Accent, highlighted ? 0.16f : 0.09f) : Theme.CardHover);
                    if (highlighted) using (var pen = new Pen(Theme.StrokeHi)) using (var path = Theme.Rounded(rect, Theme.S(6))) g.DrawPath(pen, path);
                    if (position == 3) using (var pen = new Pen(Theme.Stroke)) g.DrawLine(pen, Theme.S(12), top, Width - Theme.S(12), top);
                    TextRenderer.DrawText(g, QuantumProfiles.Title(index), Theme.UI(10, true), new Rectangle(Theme.S(14), top + Theme.S(6), Width - Theme.S(55), Theme.S(21)), chosen ? Theme.Accent : Theme.Fg, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, QuantumProfiles.Description(index), Theme.UI(9, false), new Rectangle(Theme.S(14), top + Theme.S(30), Width - Theme.S(35), Theme.S(20)), Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    if (chosen) TextRenderer.DrawText(g, "✓", Theme.UI(11, true), new Rectangle(Width - Theme.S(37), top + Theme.S(6), Theme.S(22), Theme.S(21)), Theme.Accent);
                }
                if (MaxOffset > 0) {
                    int thumb = ThumbHeight;
                    Theme.FillRound(g, new Rectangle(Width - Theme.S(6), header + offset * (ViewHeight - thumb) / MaxOffset, Theme.S(3), thumb), Theme.S(2), Theme.StrokeHi);
                }
                g.Restore(state);
                TextRenderer.DrawText(g, AutomaticPolicy.Text("↑ ↓ 浏览   Enter 选择   Esc 取消", "↑ ↓ Browse   Enter Select   Esc Cancel"), Theme.UI(8.5f, false), new Rectangle(Theme.S(12), Height - footer, Width - Theme.S(24), footer), Theme.Faint, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (!Interactive) return;
                base.OnMouseMove(e);
                if (dragY >= 0) {
                    offset = Math.Max(0, Math.Min(MaxOffset, dragOffset + (e.Y - dragY) * MaxOffset / Math.Max(1, ViewHeight - ThumbHeight)));
                    Invalidate(); return;
                }
                int next = Hit(e.Location); if (next == hoverRow) return;
                hoverRow = next; keyboard = false; Invalidate();
            }
            protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverRow = -1; Invalidate(); }
            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (!Interactive) return;
                base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return;
                if (MaxOffset > 0 && e.X >= Width - Theme.S(10) && e.Y >= header && e.Y < Height - footer) {
                    int top = header + offset * (ViewHeight - ThumbHeight) / MaxOffset;
                    if (e.Y < top || e.Y > top + ThumbHeight)
                        offset = Math.Max(0, Math.Min(MaxOffset, (e.Y - header - ThumbHeight / 2) * MaxOffset / Math.Max(1, ViewHeight - ThumbHeight)));
                    dragY = e.Y; dragOffset = offset; pressedRow = -1; keyboard = false; hoverRow = -1;
                    Capture = true; Invalidate(); return;
                }
                pressedRow = Hit(e.Location); Capture = true;
            }
            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return;
                int hit = Hit(e.Location), previous = pressedRow; pressedRow = -1; dragY = -1; Capture = false;
                if (!Interactive) return;
                if (hit >= 0 && hit == previous && Picked != null) Picked(order[hit]);
            }
            protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { pressedRow = -1; dragY = -1; } }
            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (!Interactive) return;
                base.OnMouseWheel(e); offset = Math.Max(0, Math.Min(MaxOffset, offset - Math.Sign(e.Delta) * row * 2));
                keyboard = false; hoverRow = Hit(e.Location); Invalidate();
                var handled = e as HandledMouseEventArgs; if (handled != null) handled.Handled = true;
            }
            protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
            {
                if (!Interactive) return base.ProcessCmdKey(ref msg, keyData);
                int next = active;
                if (keyData == Keys.Down) next++;
                else if (keyData == Keys.Up) next--;
                else if (keyData == Keys.Home) next = 0;
                else if (keyData == Keys.End) next = order.Length - 1;
                else if (keyData == Keys.PageDown) next += Math.Max(1, ViewHeight / row);
                else if (keyData == Keys.PageUp) next -= Math.Max(1, ViewHeight / row);
                else if (keyData == Keys.Enter || keyData == Keys.Space) { if (Picked != null) Picked(order[active]); return true; }
                else if (keyData == Keys.Escape || keyData == Keys.F4 || keyData == (Keys.Alt | Keys.Up)) { if (Cancelled != null) Cancelled(); return true; }
                else if (keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab)) { if (Tabbed != null) Tabbed(keyData == Keys.Tab); return true; }
                else return base.ProcessCmdKey(ref msg, keyData);
                active = Math.Max(0, Math.Min(order.Length - 1, next)); keyboard = true; EnsureActiveVisible(); Invalidate(); return true;
            }
        }
    }
}
