// @author bdth 2074055628@qq.com
// 文件用途 窗口淡入与页面背景揭示 不截取页面或逐个缓存子控件
using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static class Fx
    {
        public static void EnterForm(Form form)
        {
            if (form == null || form.IsDisposed || !form.Visible) return;
            if (UiClock.Frozen) { DropLayered(form); return; }
            new FormEntry(form);
        }

        private sealed class FormEntry
        {
            private const int DurationMs = 180;
            private readonly Form form;
            private readonly Timer timer;
            private readonly Stopwatch watch = new Stopwatch();
            private bool done;

            public FormEntry(Form target)
            {
                form = target;
                // 对话框可能比隐藏起来的主窗口活得久 它的淡入淡出绝不能
                // 借用或者还原主窗口那份全局动画时钟状态
                timer = new Timer();
                timer.Interval = UiClock.FrameMs;
                timer.Tick += OnTick;
                form.VisibleChanged += OnGone;
                form.Disposed += OnGone;
                form.Opacity = 0.0;
                watch.Start();
                timer.Start();
            }

            private void OnTick(object sender, EventArgs e)
            {
                if (form.IsDisposed || !form.Visible || UiClock.Frozen)
                { Finish(); return; }
                double progress = watch.Elapsed.TotalMilliseconds / DurationMs;
                if (progress >= 1.0) { Finish(); return; }
                double remaining = 1.0 - progress;
                form.Opacity = 1.0 - remaining * remaining * remaining;
            }

            private void OnGone(object sender, EventArgs e) { Finish(); }

            private void Finish()
            {
                if (done) return;
                done = true;
                timer.Stop();
                timer.Tick -= OnTick;
                timer.Dispose();
                watch.Stop();
                form.VisibleChanged -= OnGone;
                form.Disposed -= OnGone;
                if (!form.IsDisposed) DropLayered(form);
            }
        }

        internal static void DropLayered(Form f)
        {
            try
            {
                if (f == null || !f.AllowTransparency) return;
                f.Opacity = 1.0;
                f.AllowTransparency = false;
                f.Invalidate(true);
            }
            catch { }
        }
    }

    // 子 HWND 不支持可靠的整体透明度 这里只画页面原有背景 交给系统逐渐退去
    // 下方仍是可立即交互的真实页面 不复制内容 不改变页面/弹窗的分层样式
    internal sealed class PageReveal : Form
    {
        private const int DurationMs = 160;
        private const int Layered = 0x00080000, Transparent = 0x20;
        private const int ToolWindow = 0x80, NoActivate = 0x08000000;
        private readonly Form host;
        private readonly Stopwatch watch = new Stopwatch();
        private WorkspacePanel surface;
        private Point surfaceOffset;
        private bool frameAttached;
        private bool layeredReady;
        private byte alpha = 255;

        internal PageReveal(Form owner)
        {
            host = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            TabStop = false;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint, true);
            host.VisibleChanged += OnHostChanged;
            host.LocationChanged += OnHostChanged;
            host.SizeChanged += OnHostChanged;
            host.Deactivate += OnHostChanged;
            host.HandleDestroyed += OnHostChanged;
        }

        internal WorkspacePanel Surface { get { return surface; } }
        internal byte Alpha { get { return alpha; } }
        internal bool Fading { get { return watch.IsRunning; } }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                value.ExStyle |= Layered | Transparent | ToolWindow | NoActivate;
                return value;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 初始 alpha=255 也必须显式初始化 否则分层窗口的首帧不会绘制
            layeredReady = SetAlpha(255);
        }

        private bool CanReveal
        {
            get
            {
                return !host.IsDisposed && host.IsHandleCreated && host.Visible
                    && host.WindowState != FormWindowState.Minimized
                    && IsWindowEnabled(host.Handle) && !UiClock.Frozen && !UiClock.Suspended;
            }
        }

        internal bool Prepare(WorkspacePanel page)
        {
            Cancel();
            if (page == null || page.IsDisposed || page.Parent == null || !CanReveal) return false;
            Rectangle full = page.Parent.RectangleToScreen(page.Bounds);
            Rectangle clipped = Rectangle.Intersect(full, host.RectangleToScreen(host.ClientRectangle));
            if (clipped.Width <= 0 || clipped.Height <= 0) return false;
            surface = page;
            surfaceOffset = new Point(clipped.Left - full.Left, clipped.Top - full.Top);
            surface.VisibleChanged += OnSurfaceVisibleChanged;
            surface.LocationChanged += OnHostChanged;
            surface.SizeChanged += OnHostChanged;
            surface.ParentChanged += OnHostChanged;
            surface.Disposed += OnHostChanged;
            Bounds = clipped;
            try
            {
                if (IsHandleCreated) layeredReady = SetAlpha(255);
                Show(host);
                if (!layeredReady) { Cancel(); return false; }
                Invalidate();
                Update();
                return surface != null && Visible;
            }
            catch
            {
                // 动画不可用时仍显示真实页面 不能留下遮挡层或阻断导航
                Cancel();
                return false;
            }
        }

        internal void Reveal()
        {
            if (surface == null || !surface.Visible || !CanReveal) { Cancel(); return; }
            WorkspacePanel target = surface;
            // 先完成真实子窗口的首帧 计时不包含布局 列表刷新和首次绘制的耗时
            bool painted;
            try { painted = RedrawWindow(target.Handle, IntPtr.Zero, IntPtr.Zero, 0x181); }
            catch
            {
                if (surface == target) Cancel();
                throw;
            }
            // 同步 Paint 可能打开弹窗 关闭页面或发起下一次导航 不能复活已取消的过渡
            if (surface != target) return;
            if (!painted || target.IsDisposed || !target.Visible || !Visible || !CanReveal)
            { Cancel(); return; }
            watch.Restart();
            if (!frameAttached) { UiClock.Frame += OnFrame; frameAttached = true; }
            UiClock.Wake();
        }

        private void OnFrame(object sender, EventArgs e)
        {
            if (surface == null || surface.IsDisposed || !surface.Visible || !CanReveal)
            { Cancel(); return; }
            double progress = watch.Elapsed.TotalMilliseconds / DurationMs;
            if (progress >= 1.0) { Cancel(); return; }
            double remaining = 1.0 - progress;
            byte next = (byte)Math.Max(1, (int)Math.Round(255 * remaining * remaining * remaining));
            if (next != alpha && !SetAlpha(next)) Cancel();
        }

        internal void Cancel()
        {
            watch.Reset();
            if (frameAttached) { UiClock.Frame -= OnFrame; frameAttached = false; }
            if (surface != null)
            {
                surface.VisibleChanged -= OnSurfaceVisibleChanged;
                surface.LocationChanged -= OnHostChanged;
                surface.SizeChanged -= OnHostChanged;
                surface.ParentChanged -= OnHostChanged;
                surface.Disposed -= OnHostChanged;
                surface = null;
            }
            if (!IsDisposed && Visible) Hide();
        }

        private void OnHostChanged(object sender, EventArgs e) { Cancel(); }

        private void OnSurfaceVisibleChanged(object sender, EventArgs e)
        {
            if (surface != null && !surface.Visible) Cancel();
        }

        private bool SetAlpha(byte value)
        {
            if (!SetLayeredWindowAttributes(Handle, 0, value, 2)) return false;
            alpha = value;
            return true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (surface == null || surface.IsDisposed) return;
            var state = e.Graphics.Save();
            try
            {
                e.Graphics.TranslateTransform(-surfaceOffset.X, -surfaceOffset.Y);
                var clip = new Rectangle(surfaceOffset, ClientSize);
                surface.PaintSurface(e.Graphics, clip);
            }
            finally { e.Graphics.Restore(state); }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x84) { message.Result = new IntPtr(-1); return; } // HTTRANSPARENT
            if (message.Msg == 0x21) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE
            base.WndProc(ref message);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Cancel();
                host.VisibleChanged -= OnHostChanged;
                host.LocationChanged -= OnHostChanged;
                host.SizeChanged -= OnHostChanged;
                host.Deactivate -= OnHostChanged;
                host.HandleDestroyed -= OnHostChanged;
            }
            base.Dispose(disposing);
        }

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);
        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr window);
        [DllImport("user32.dll")]
        private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);
    }
}
