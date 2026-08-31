// 文件用途 只给选择器用的滚动条 选中项 键盘导航 滚轮和 TopIndex
// 仍然归原生 ListBox 管 滚动条永远不接收行输入
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class TechListScrollHost : Panel
    {
        private readonly TechListBox list;
        private readonly ScrollRail rail;
        private bool syncing;
        private int visibleRows = 1;
        private int maximumTop;
        private bool scrollNeeded;

        internal TechListScrollHost(TechListBox list)
        {
            if (list == null) throw new ArgumentNullException("list");
            this.list = list;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            TabStop = false;
            BackColor = list.BackColor;
            list.Dock = DockStyle.None;
            list.ExternalScrollBar = true;
            rail = new ScrollRail(this);
            Controls.Add(list);
            Controls.Add(rail);
            list.ViewportChanged += OnViewportChanged;
            list.BackColorChanged += OnViewportChanged;
            SyncViewport();
        }

        internal int VisibleRows { get { return visibleRows; } }
        internal int MaximumTop { get { return maximumTop; } }
        internal bool ScrollNeeded { get { return scrollNeeded; } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (list == null || rail == null || list.IsDisposed || list.Parent != this) return;
            int width = scrollNeeded ? Math.Min(ClientSize.Width, Theme.S(14)) : 0;
            list.SetBounds(0, 0, Math.Max(0, ClientSize.Width - width), ClientSize.Height);
            rail.SetBounds(ClientSize.Width - width, 0, width, ClientSize.Height);
            SyncViewport();
        }

        private void OnViewportChanged(object sender, EventArgs e) { SyncViewport(); }

        internal void SyncViewport()
        {
            if (syncing || IsDisposed || Disposing || list.IsDisposed || list.Parent != this) return;
            syncing = true;
            try
            {
                int page = Math.Max(1, list.ClientSize.Height / Math.Max(1, list.ItemHeight));
                int maximum = Math.Max(0, list.Items.Count - page);
                bool rangeChanged = page != visibleRows || maximum != maximumTop;
                visibleRows = page;
                maximumTop = maximum;
                bool needed = maximum > 0;
                BackColor = list.BackColor;
                rail.BackColor = list.BackColor;
                if (needed != scrollNeeded)
                {
                    scrollNeeded = needed;
                    rail.Visible = needed;
                    PerformLayout();
                }
                // 列表刷新或者过滤 可能在拖动过程中把范围换掉
                if (rangeChanged || !needed) rail.CancelDrag();
                rail.Invalidate();
            }
            finally { syncing = false; }
        }

        internal void ScrollTo(int top)
        {
            if (IsDisposed || Disposing || list.IsDisposed || list.Parent != this || list.Items.Count == 0) return;
            int next = Math.Max(0, Math.Min(maximumTop, top));
            if (list.TopIndex != next) list.TopIndex = next;
            // LB_SETTOPINDEX 可能被原生上限截断 画的时候用实际索引
            SyncViewport();
        }

        protected override void OnControlRemoved(ControlEventArgs e)
        {
            base.OnControlRemoved(e);
            if (e.Control != list || list == null) return;
            Detach();
            if (!list.IsDisposed && !list.Disposing && !Disposing && !IsDisposed) list.ExternalScrollBar = false;
            if (rail != null && !rail.IsDisposed)
            {
                rail.CancelDrag();
                rail.Visible = false;
            }
        }

        private void Detach()
        {
            list.ViewportChanged -= OnViewportChanged;
            list.BackColorChanged -= OnViewportChanged;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Detach();
            base.Dispose(disposing);
        }

        private sealed class ScrollRail : Control
        {
            private readonly TechListScrollHost owner;
            private bool hot;
            private bool dragging;
            private int dragOffset;

            [DllImport("user32.dll")] private static extern IntPtr SendMessage(
                IntPtr window, int message, IntPtr wParam, IntPtr lParam);

            internal ScrollRail(TechListScrollHost owner)
            {
                this.owner = owner;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                Visible = false;
                Cursor = Cursors.Hand;
                AccessibleRole = AccessibleRole.ScrollBar;
                AccessibleName = Lang.T("ui.scroll.vertical");
            }

            internal Rectangle TrackRectangle
            {
                get
                {
                    int inset = Math.Min(Theme.S(4), ClientSize.Height / 2);
                    return new Rectangle(0, inset, ClientSize.Width, Math.Max(0, ClientSize.Height - inset * 2));
                }
            }

            internal Rectangle ThumbRectangle
            {
                get
                {
                    Rectangle track = TrackRectangle;
                    if (!owner.ScrollNeeded || track.Width <= 0 || track.Height <= 0) return Rectangle.Empty;
                    int height = (int)Math.Round(track.Height * (double)owner.VisibleRows / Math.Max(1, owner.list.Items.Count));
                    height = Math.Min(track.Height, Math.Max(Theme.S(32), height));
                    int top = Math.Max(0, Math.Min(owner.MaximumTop, owner.list.TopIndex));
                    int y = track.Top + (int)Math.Round((track.Height - height) * (double)top / Math.Max(1, owner.MaximumTop));
                    int width = Math.Min(track.Width, Theme.S(hot || dragging ? 8 : 6));
                    return new Rectangle(track.Left + (track.Width - width) / 2, y, width, height);
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                Rectangle thumb = ThumbRectangle;
                if (thumb.IsEmpty) return;
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Rectangle track = TrackRectangle;
                int trackWidth = Math.Min(track.Width, Math.Max(1, Theme.S(2)));
                var line = new Rectangle(track.Left + (track.Width - trackWidth) / 2, track.Top, trackWidth, track.Height);
                FillPill(g, line, Col.Lerp(BackColor, Theme.Stroke, 0.55f));
                Color color = dragging ? Theme.Accent : hot ? Col.Lerp(Theme.StrokeHi, Theme.Accent, 0.7f) : Theme.StrokeHi;
                FillPill(g, thumb, color);
            }

            private static void FillPill(Graphics g, Rectangle bounds, Color color)
            {
                if (bounds.Width <= 0 || bounds.Height <= 0) return;
                int diameter = Math.Min(bounds.Width, bounds.Height);
                using (var path = new GraphicsPath())
                using (var brush = new SolidBrush(color))
                {
                    path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 180);
                    path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 0, 180);
                    path.CloseFigure();
                    g.FillPath(brush, path);
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                hot = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                hot = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || !owner.ScrollNeeded) return;
                Rectangle thumb = ThumbRectangle;
                if (thumb.IsEmpty) return;
                // 整条轨道都是拖动目标 不只是那六像素宽的滑块
                if (e.Y >= thumb.Top && e.Y < thumb.Bottom)
                {
                    BeginDrag(e.Y);
                    Capture = true;
                }
                else owner.ScrollTo(owner.list.TopIndex + (e.Y < thumb.Top ? -owner.VisibleRows : owner.VisibleRows));
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!dragging || !Capture) return;
                DragTo(e.Y);
            }

            private void BeginDrag(int mouseY)
            {
                Rectangle thumb = ThumbRectangle;
                if (thumb.IsEmpty) return;
                dragging = true;
                dragOffset = mouseY - thumb.Top;
                Invalidate();
            }

            private void DragTo(int mouseY)
            {
                if (!dragging) return;
                Rectangle track = TrackRectangle, thumb = ThumbRectangle;
                int travel = track.Height - thumb.Height;
                if (travel <= 0) return;
                int position = Math.Max(0, Math.Min(travel, mouseY - dragOffset - track.Top));
                owner.ScrollTo((int)Math.Round(position * (double)owner.MaximumTop / travel));
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button == MouseButtons.Left) CancelDrag();
            }

            protected override void OnMouseCaptureChanged(EventArgs e)
            {
                base.OnMouseCaptureChanged(e);
                if (!Capture) { dragging = false; Invalidate(); }
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (!Visible) { hot = false; CancelDrag(); }
            }

            protected override void OnEnabledChanged(EventArgs e)
            {
                base.OnEnabledChanged(e);
                if (!Enabled) CancelDrag();
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                CancelDrag();
                base.OnHandleDestroyed(e);
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                if (owner.list.IsHandleCreated && !owner.list.IsDisposed)
                    SendMessage(owner.list.Handle, 0x020A, (IntPtr)unchecked((int)((uint)(ushort)e.Delta << 16)), IntPtr.Zero);
                var handled = e as HandledMouseEventArgs;
                if (handled != null) handled.Handled = true;
            }

            internal void CancelDrag()
            {
                dragging = false;
                if (Capture) Capture = false;
                Invalidate();
            }
        }
    }
}
