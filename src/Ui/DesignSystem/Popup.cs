// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    // 弹出层定位：纯函数，便于在各 DPI 与屏幕边缘下验证边界。
    internal static class DsPopup
    {
        // 锚点下方优先，放不下翻到上方；水平按左/右对齐并压回工作区。
        public static Rectangle Place(Rectangle anchor, Size size, Rectangle work, int gap, int edgeMargin, bool alignRight)
        {
            if (size.Width < 1) size.Width = 1;
            if (size.Height < 1) size.Height = 1;
            edgeMargin = Math.Max(0, Math.Min(edgeMargin, Math.Max(0, (work.Width - 1) / 2)));
            size.Width = Math.Min(size.Width, Math.Max(1, work.Width - edgeMargin * 2));
            size.Height = Math.Min(size.Height, Math.Max(1, work.Height));
            int left = alignRight ? anchor.Right - size.Width : anchor.Left;
            left = Math.Max(work.Left + edgeMargin, Math.Min(left, work.Right - size.Width - edgeMargin));
            int top = anchor.Bottom + gap;
            if (top + size.Height > work.Bottom) top = anchor.Top - size.Height - gap;
            top = Math.Max(work.Top, Math.Min(top, work.Bottom - size.Height));
            return new Rectangle(left, top, size.Width, size.Height);
        }

        // 弹出层尺寸不得超过工作区减去边距。
        public static Size Fit(Size size, Rectangle work, int margin, int minWidth)
        {
            int maxW = Math.Max(1, work.Width - Math.Max(0, margin));
            int maxH = Math.Max(1, work.Height - Math.Max(0, margin));
            return new Size(Math.Max(1, Math.Min(size.Width, maxW)), Math.Max(1, Math.Min(size.Height, maxH)));
        }
    }

    // 下拉菜单宿主：统一管理菜单托管、定位显示、关闭原因、上下文变化与忙碌自动关闭、延迟释放。
    internal sealed class DropdownHost : IDisposable
    {
        private readonly Control owner;
        private readonly IBusyInteractive busyAware;
        private readonly FxControl busySource;
        private readonly List<Control> watched = new List<Control>();
        private ContextMenuStrip menu;
        private bool closeOnClick;
        private bool disposed;

        public bool AlignRight;
        public int GapLogical = DsMetrics.Menu.Gap;
        public int EdgeMarginLogical = DsMetrics.Menu.EdgeMargin;
        public event EventHandler Closed;

        public DropdownHost(Control owner)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            this.owner = owner;
            busyAware = owner as IBusyInteractive;
            // 接口未声明事件，忙碌变化由 FxControl 提供；其他实现只参与门禁。
            busySource = owner as FxControl;
            if (busySource != null) busySource.BusyChanged += OwnerBusyChanged;
            owner.ParentChanged += ContextChanged;
            owner.LocationChanged += ContextChanged;
            owner.SizeChanged += ContextChanged;
            owner.HandleDestroyed += ContextChanged;
            owner.VisibleChanged += OwnerVisibleChanged;
            owner.EnabledChanged += OwnerEnabledChanged;
            owner.Disposed += OwnerDisposed;
        }

        public Control Owner { get { return owner; } }
        public ContextMenuStrip Menu { get { return menu != null && !menu.IsDisposed ? menu : null; } }
        public bool IsCreated { get { return Menu != null; } }
        public bool IsVisible { get { return IsCreated && menu.Visible; } }
        public Rectangle LastPlacement { get; private set; }

        // 所属控件可交互：未释放、启用，且实现忙碌契约时不忙碌。
        public bool OwnerCanInteract
        {
            get
            {
                if (disposed || owner.IsDisposed || !owner.Enabled) return false;
                return busyAware == null || busyAware.CanInteract;
            }
        }

        // 鼠标按下时菜单仍开着，则这次点击只负责关闭。
        public void NoteMouseDown() { closeOnClick = closeOnClick || IsVisible; }
        public void NoteMouseUp() { closeOnClick = false; }

        // 返回 true 表示本次点击只用于关闭（握手标记或菜单仍可见），调用方不得再打开。
        public bool ConsumeCloseClick()
        {
            if (!closeOnClick && !IsVisible) return false;
            closeOnClick = false;
            Close();
            return true;
        }

        public Rectangle Placement(Size size)
        {
            Rectangle work = Screen.FromControl(owner).WorkingArea;
            Rectangle anchor = owner.RectangleToScreen(owner.ClientRectangle);
            return DsPopup.Place(anchor, size, work, Theme.S(GapLogical), Theme.S(EdgeMarginLogical), AlignRight);
        }

        // 仅托管，不定位不显示：用于预建或缓存菜单；不经交互门禁，禁用控件仍可预建。
        public void Attach(ContextMenuStrip next)
        {
            if (next != null && ReferenceEquals(next, menu)) return;
            Close();
            if (next == null) return;
            if (disposed || owner.IsDisposed) { next.Dispose(); return; }
            closeOnClick = false;
            menu = next;
            next.Closing += MenuClosing;
            next.Closed += MenuClosed;
            WatchContext();
        }

        // 定位并显示：所属控件禁用或忙碌时拒绝，新菜单立即释放，已托管菜单关闭。
        public void Show(ContextMenuStrip next)
        {
            if (next == null) { Close(); return; }
            bool attached = ReferenceEquals(next, menu);
            if (!OwnerCanInteract)
            {
                Close();
                if (!attached) next.Dispose();
                return;
            }
            if (!attached) Attach(next);
            if (!ReferenceEquals(next, menu)) return;
            closeOnClick = false;
            Rectangle place = Placement(next.Size);
            LastPlacement = place;
            if (next.Size != place.Size) next.Size = place.Size;
#if !PAVISE_SELFTEST
            try { next.Show(owner, owner.PointToClient(place.Location)); }
            catch { Close(); throw; }
#endif
            if (!owner.IsDisposed) owner.Invalidate();
        }

        public void Close()
        {
            ContextMenuStrip owned = menu;
            menu = null;
            UnwatchContext();
            if (owned != null)
            {
                owned.Closing -= MenuClosing;
                owned.Closed -= MenuClosed;
                try { if (!owned.IsDisposed && owned.Visible) owned.Close(); }
                finally
                {
                    // 点击消息结束后再销毁菜单句柄，回调中删除所属控件也安全。
                    if (!owned.IsDisposed)
                    {
                        if (owned.IsHandleCreated)
                            owned.BeginInvoke((MethodInvoker)delegate { if (!owned.IsDisposed) owned.Dispose(); });
                        else owned.Dispose();
                    }
                }
                EventHandler closed = Closed;
                if (closed != null) closed(this, EventArgs.Empty);
            }
            if (!owner.IsDisposed) owner.Invalidate();
        }

        // Escape 仅在有菜单时吞掉；Tab 关闭后放行；展开键交给 open。
        public bool HandleCommandKey(Keys keys, bool menuButton, Action open)
        {
            if (keys == Keys.Escape)
            {
                if (!IsCreated) return false;
                Close();
                return true;
            }
            if (DsKeys.IsTab(keys)) { Close(); return false; }
            if (DsKeys.IsDropdownOpen(keys, menuButton))
            {
                if (open != null) open();
                return true;
            }
            return false;
        }

        private void MenuClosing(object sender, ToolStripDropDownClosingEventArgs args)
        {
            if (args.CloseReason != ToolStripDropDownCloseReason.AppClicked) return;
            if (owner.IsDisposed || !owner.IsHandleCreated) return;
            if (owner.ClientRectangle.Contains(owner.PointToClient(Cursor.Position))) closeOnClick = true;
        }

        private void MenuClosed(object sender, ToolStripDropDownClosedEventArgs args)
        {
            if (ReferenceEquals(sender, menu)) Close();
        }

        private void WatchContext()
        {
            UnwatchContext();
            for (Control parent = owner.Parent; parent != null; parent = parent.Parent)
            {
                watched.Add(parent);
                parent.LocationChanged += ContextChanged; parent.SizeChanged += ContextChanged;
                parent.VisibleChanged += ContextChanged; parent.ParentChanged += ContextChanged;
                var scroll = parent as ScrollableControl;
                if (scroll != null) scroll.Scroll += ContextScrolled;
            }
        }

        private void UnwatchContext()
        {
            foreach (Control parent in watched)
            {
                parent.LocationChanged -= ContextChanged; parent.SizeChanged -= ContextChanged;
                parent.VisibleChanged -= ContextChanged; parent.ParentChanged -= ContextChanged;
                var scroll = parent as ScrollableControl;
                if (scroll != null) scroll.Scroll -= ContextScrolled;
            }
            watched.Clear();
        }

        private void ContextChanged(object sender, EventArgs args) { Close(); }
        private void ContextScrolled(object sender, ScrollEventArgs args) { Close(); }
        private void OwnerVisibleChanged(object sender, EventArgs args) { if (!owner.Visible) Close(); }
        private void OwnerEnabledChanged(object sender, EventArgs args) { if (!owner.Enabled) Close(); }
        private void OwnerBusyChanged(object sender, EventArgs args) { if (busyAware != null && busyAware.Busy) Close(); }
        private void OwnerDisposed(object sender, EventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Close();
            if (busySource != null) busySource.BusyChanged -= OwnerBusyChanged;
            owner.ParentChanged -= ContextChanged;
            owner.LocationChanged -= ContextChanged;
            owner.SizeChanged -= ContextChanged;
            owner.HandleDestroyed -= ContextChanged;
            owner.VisibleChanged -= OwnerVisibleChanged;
            owner.EnabledChanged -= OwnerEnabledChanged;
            owner.Disposed -= OwnerDisposed;
        }
    }
}
