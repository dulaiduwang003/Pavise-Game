// @author bdth 2074055628@qq.com
// File purpose Window fade-in and page background reveal, without capturing the page or caching child controls one by one
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
                // A dialog may outlive the hidden main window, so its fade in/out must never
                // borrow or restore the main window's global animation clock state
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

    // Child HWNDs have no reliable whole-window opacity; only the page's own background is painted here and the system fades it away
    // Underneath is the real, immediately interactive page; no content is copied and the layered styles of pages and dialogs stay untouched
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
            // Even an initial alpha=255 must be set explicitly, otherwise the layered window never paints its first frame
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
                // When animation is unavailable still show the real page, never leave a covering layer or block navigation
                Cancel();
                return false;
            }
        }

        internal void Reveal()
        {
            if (surface == null || !surface.Visible || !CanReveal) { Cancel(); return; }
            WorkspacePanel target = surface;
            // Finish the real child window's first frame first; timing excludes layout, list refresh and first-paint cost
            bool painted;
            try { painted = RedrawWindow(target.Handle, IntPtr.Zero, IntPtr.Zero, 0x181); }
            catch
            {
                if (surface == target) Cancel();
                throw;
            }
            // A synchronous Paint may open a dialog, close the page or start the next navigation, must not revive a cancelled transition
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
