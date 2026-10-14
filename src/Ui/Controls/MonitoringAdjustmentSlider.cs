using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class MonitoringAdjustmentSlider : Control
    {
        private int minimum, maximum = 100, value;
        private bool dragging;
        internal event EventHandler ValueChanged, ValueCommitted;
        internal int Minimum { get { return minimum; } set { minimum = value; SetSilently(this.value); } }
        internal int Maximum { get { return maximum; } set { maximum = value; SetSilently(this.value); } }
        internal int Value { get { return value; } set { SetValue(value, true); } }
        internal void SetSilently(int next) { SetValue(next, false); }
        internal void CommitValue()
        { if (!Enabled) return; EventHandler handler = ValueCommitted; if (handler != null) handler(this, EventArgs.Empty); }
        internal MonitoringAdjustmentSlider()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable
                | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            TabStop = true; AccessibleRole = AccessibleRole.Slider; Cursor = Cursors.Hand;
        }
        private void SetValue(int next, bool notify)
        {
            next = Math.Max(minimum, Math.Min(maximum, next));
            if (value == next) return;
            value = next; Invalidate();
            EventHandler handler = ValueChanged;
            if (notify && handler != null) handler(this, EventArgs.Empty);
        }
        private void SetFromPointer(int x)
        {
            int inset = Theme.S(10), length = Math.Max(1, Width - inset * 2);
            Value = minimum + (int)Math.Round(Math.Max(0, Math.Min(length, x - inset))
                * (double)(maximum - minimum) / length);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down
                || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            int next = value, step = e.Shift ? 5 : 1;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) next -= step;
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) next += step;
            else if (e.KeyCode == Keys.Home) next = minimum;
            else if (e.KeyCode == Keys.End) next = maximum;
            else return;
            Value = next; CommitValue(); e.Handled = e.SuppressKeyPress = true;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus(); dragging = true; Capture = true; SetFromPointer(e.X);
        }
        protected override void OnMouseMove(MouseEventArgs e)
        { base.OnMouseMove(e); if (Enabled && dragging) SetFromPointer(e.X); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!Enabled) { CancelDrag(); return; }
            if (!dragging || e.Button != MouseButtons.Left) return;
            SetFromPointer(e.X); dragging = false; Capture = false; CommitValue();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Enabled) { CancelDrag(); return; }
            if (dragging && !Capture) { dragging = false; CommitValue(); }
        }
        private void CancelDrag()
        {
            // 取消拖动后释放捕获，不能提交被禁用的手势。
            dragging = false;
            if (Capture) Capture = false;
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) CancelDrag();
            base.OnEnabledChanged(e); Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        { base.OnMouseWheel(e); if (!Enabled) return; Value += e.Delta > 0 ? 1 : -1; CommitValue(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int inset = Theme.S(10), center = Height / 2, length = Math.Max(1, Width - inset * 2);
            int position = inset + (int)Math.Round(length * (value - minimum) / (double)Math.Max(1, maximum - minimum));
            Color accent = Enabled ? Theme.ModeColor(Theme.CurrentMode) : Theme.Dim;
            using (var track = new Pen(Theme.Stroke, Theme.S(2))) g.DrawLine(track, inset, center, inset + length, center);
            int origin = minimum < 0 && maximum > 0 ? inset + (int)Math.Round(length * -minimum / (double)(maximum - minimum)) : inset;
            if (position != origin)
                using (var fill = new Pen(Col.Alpha(accent, 205), Theme.S(2))) g.DrawLine(fill, origin, center, position, center);
            if (minimum < 0 && maximum > 0)
            {
                using (var marker = new Pen(Theme.Dim)) g.DrawLine(marker, origin, center - Theme.S(5), origin, center + Theme.S(5));
            }
            Rectangle thumb = new Rectangle(position - Theme.S(5), center - Theme.S(7), Theme.S(10), Theme.S(14));
            using (var shape = Theme.TechPath(thumb, Theme.S(3)))
            {
                using (var brush = new SolidBrush(accent)) g.FillPath(brush, shape);
                using (var outline = new Pen(Focused ? Theme.Fg : Col.Alpha(accent, 210))) g.DrawPath(outline, shape);
            }
        }
    }
}
