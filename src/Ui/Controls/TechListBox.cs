// @author bdth 2074055628@qq.com
// 文件用途 自绘列表基类 拦截背景擦除并逐行离屏合成 消除滚动与悬浮闪烁
// 离屏面必须是屏幕兼容位图 换成 GDI+ 托管位图会让 ClearType 子像素渲染失真 亮色主题下字发虚

using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class TechListBox : ListBox
    {
        private const int WmEraseBkgnd = 0x0014;
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

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmEraseBkgnd && m.WParam != IntPtr.Zero)
            {
                FillTail(m.WParam);
                m.Result = (IntPtr)1;
                return;
            }
            base.WndProc(ref m);
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
            using (var back = new SolidBrush(BackColor)) surface.FillRectangle(back, bounds);
            base.OnDrawItem(new DrawItemEventArgs(surface, e.Font, bounds, e.Index, e.State, e.ForeColor, e.BackColor));
            surface.ResetClip();

            IntPtr dst = e.Graphics.GetHdc();
            try { BitBlt(dst, bounds.Left, bounds.Top, bounds.Width, bounds.Height, memDc, bounds.Left, bounds.Top, SrcCopy); }
            finally { e.Graphics.ReleaseHdc(dst); }
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
                using (Graphics g = Graphics.FromHdc(hdc))
                using (var brush = new SolidBrush(BackColor))
                    g.FillRectangle(brush, 0, used, width, height - used);
            }
            catch { }
        }
    }
}
