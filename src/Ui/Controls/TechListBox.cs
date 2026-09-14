// @author bdth 2074055628@qq.com
// File purpose Owner-drawn list base class; intercepts background erase and composites row by row off-screen to kill scroll and hover flicker
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class TechListBox : ListBox
    {
        private const int WmEraseBkgnd = 0x0014;
        private const int WmSetRedraw = 0x000B;
        private const int WsVScroll = 0x00200000;
        private const int SrcCopy = 0x00CC0020;

        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern bool BitBlt(
            IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

        private IntPtr memDc = IntPtr.Zero;
        private IntPtr memBmp = IntPtr.Zero;
        private IntPtr oldBmp = IntPtr.Zero;
        private int bufW, bufH;
        private Graphics surface;
        private bool drawFaultLogged;
        private bool externalScrollBar;
        private bool viewportQueued;
        private bool redrawEnabled = true;
        private int viewportGeneration;
        private long wheelDelta;

        // Must be enabled explicitly; only the picker host provides a vertical scrollbar, other native lists stay unchanged
        internal bool ExternalScrollBar
        {
            get { return externalScrollBar; }
            set
            {
                if (externalScrollBar == value) return;
                externalScrollBar = value;
                if (IsHandleCreated) RecreateHandle();
            }
        }

        internal event EventHandler ViewportChanged;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                if (externalScrollBar) parameters.Style &= ~WsVScroll;
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            QueueViewportChanged();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmEraseBkgnd && m.WParam != IntPtr.Zero)
            {
                FillTail(m.WParam);
                m.Result = (IntPtr)1;
                return;
            }
            if (externalScrollBar && m.Msg == WmSetRedraw) redrawEnabled = m.WParam != IntPtr.Zero;
            base.WndProc(ref m);
            if (!externalScrollBar || !redrawEnabled) return;
            switch (m.Msg)
            {
                case WmSetRedraw:
                case 0x0005: // WM_SIZE
                case 0x0100: // WM_KEYDOWN
                case 0x0102: // WM_CHAR native incremental search can change the viewport
                case 0x0115: // WM_VSCROLL
                case 0x020A: // WM_MOUSEWHEEL
                case 0x0200: // WM_MOUSEMOVE native drag-selection can scroll too
                case 0x0201: // WM_LBUTTONDOWN
                case 0x0113: // WM_TIMER native drag-selection autoscroll
                case 0x0180: // LB_ADDSTRING
                case 0x0181: // LB_INSERTSTRING
                case 0x0182: // LB_DELETESTRING
                case 0x0184: // LB_RESETCONTENT
                case 0x0185: // LB_SETSEL
                case 0x0186: // LB_SETCURSEL
                case 0x0197: // LB_SETTOPINDEX
                case 0x019F: // LB_SETCARETINDEX
                case 0x01A0: // LB_SETITEMHEIGHT
                case 0x01A7: // LB_SETCOUNT
                    QueueViewportChanged();
                    break;
            }
        }

        private void QueueViewportChanged()
        {
            if (!externalScrollBar || viewportQueued || !IsHandleCreated || IsDisposed || Disposing) return;
            viewportQueued = true;
            int generation = viewportGeneration;
            try
            {
                // Native LB_ADDSTRING runs before the managed Items collection is updated
                // Coalesce until message processing is done, including BeginUpdate and EndUpdate batches
                BeginInvoke((MethodInvoker)delegate
                {
                    if (generation != viewportGeneration) return;
                    viewportQueued = false;
                    if (!externalScrollBar || IsDisposed || Disposing || !redrawEnabled) return;
                    EventHandler changed = ViewportChanged;
                    if (changed != null) changed(this, EventArgs.Empty);
                });
            }
            catch { viewportQueued = false; }
        }

        protected override void OnSelectedIndexChanged(EventArgs e)
        {
            base.OnSelectedIndexChanged(e);
            QueueViewportChanged();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!externalScrollBar || MultiColumn) return;
            var handled = e as HandledMouseEventArgs;
            if (handled != null && handled.Handled) return;
            if (handled != null) handled.Handled = true;
            // Native LISTBOX wheel handling needs WS_VSCROLL; with an external scrollbar
            // replace only that part and keep the system settings
            int lines = SystemInformation.MouseWheelScrollLines;
            if (lines == 0) return;
            int delta = SystemInformation.MouseWheelScrollDelta;
            wheelDelta += e.Delta;
            int steps = (int)(wheelDelta / delta);
            wheelDelta %= delta;
            if (steps == 0 || Items.Count == 0) return;
            int page = Math.Max(1, ClientSize.Height / Math.Max(1, ItemHeight));
            int distance = lines < 0 ? page : lines;
            long target = TopIndex - (long)steps * distance;
            TopIndex = (int)Math.Max(0, Math.Min(Math.Max(0, Items.Count - page), target));
            QueueViewportChanged();
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            Rectangle bounds = e.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0 || !EnsureBuffer(e.Graphics, bounds))
            {
                base.OnDrawItem(e);
                return;
            }

            surface.SetClip(bounds);
            if (Backdrop.AppliesTo(this)) Backdrop.Paint(surface, this, bounds);
            using (var back = new SolidBrush(Backdrop.CardFill(this, BackColor))) surface.FillRectangle(back, bounds);
            try
            {
                base.OnDrawItem(new DrawItemEventArgs(surface, e.Font, bounds, e.Index, e.State, e.ForeColor, e.BackColor));
            }
            catch (Exception ex)
            {
                if (!drawFaultLogged)
                {
                    drawFaultLogged = true;
                    Logger.Log(Lang.T("log.techlistbox.1") + e.Index + " " + ex.GetType().Name + " " + ex.Message);
                }
            }
            finally
            {
                surface.ResetClip();
                IntPtr dst = e.Graphics.GetHdc();
                try { BitBlt(dst, bounds.Left, bounds.Top, bounds.Width, bounds.Height, memDc, bounds.Left, bounds.Top, SrcCopy); }
                finally { e.Graphics.ReleaseHdc(dst); }
            }
        }

        private bool EnsureBuffer(Graphics target, Rectangle bounds)
        {
            int w = Math.Max(ClientSize.Width, bounds.Right);
            int h = Math.Max(ClientSize.Height, bounds.Bottom);
            if (w <= 0 || h <= 0) return false;
            if (memDc != IntPtr.Zero && bufW >= w && bufH >= h) return true;

            ReleaseBuffer();
            IntPtr refDc = target.GetHdc();
            try
            {
                IntPtr dc = CreateCompatibleDC(refDc);
                if (dc == IntPtr.Zero) return false;
                IntPtr bmp = CreateCompatibleBitmap(refDc, w, h);
                if (bmp == IntPtr.Zero) { DeleteDC(dc); return false; }
                memDc = dc;
                memBmp = bmp;
                oldBmp = SelectObject(dc, bmp);
                bufW = w;
                bufH = h;
            }
            finally { target.ReleaseHdc(refDc); }

            try { surface = Graphics.FromHdc(memDc); }
            catch { ReleaseBuffer(); return false; }
            return true;
        }

        private void ReleaseBuffer()
        {
            if (surface != null) { surface.Dispose(); surface = null; }
            if (memDc != IntPtr.Zero)
            {
                if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
                DeleteDC(memDc);
                memDc = IntPtr.Zero;
                oldBmp = IntPtr.Zero;
            }
            if (memBmp != IntPtr.Zero) { DeleteObject(memBmp); memBmp = IntPtr.Zero; }
            bufW = 0;
            bufH = 0;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            viewportGeneration++;
            viewportQueued = false;
            redrawEnabled = true;
            wheelDelta = 0;
            ReleaseBuffer();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseBuffer();
            base.Dispose(disposing);
        }

        private void FillTail(IntPtr hdc)
        {
            int height = ClientSize.Height;
            int width = ClientSize.Width;
            if (height <= 0 || width <= 0) return;

            int used = 0;
            int count = Items.Count;
            if (count > 0 && ItemHeight > 0)
            {
                int top = TopIndex;
                if (top < 0) top = 0;
                int rows = count - top;
                if (rows > 0) used = rows > height / ItemHeight + 2 ? height : rows * ItemHeight;
                if (used > height) used = height;
            }
            if (used >= height) return;

            try
            {
                var tail = new Rectangle(0, used, width, height - used);
                using (Graphics g = Graphics.FromHdc(hdc))
                {
                    if (Backdrop.AppliesTo(this)) Backdrop.Paint(g, this, tail);
                    using (var brush = new SolidBrush(Backdrop.CardFill(this, BackColor)))
                        g.FillRectangle(brush, tail);
                }
            }
            catch { }
        }
    }
}
