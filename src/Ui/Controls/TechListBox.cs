// @author bdth 2074055628@qq.com
// 文件用途 自绘列表基类 拦截背景擦除并逐行离屏合成 消除滚动与悬浮闪烁

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class TechListBox : ListBox
    {
        private const int WmEraseBkgnd = 0x0014;

        private Bitmap buffer;
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
            if (bounds.Width <= 0 || bounds.Height <= 0 || !EnsureBuffer(bounds))
            {
                base.OnDrawItem(e);
                return;
            }

            surface.SetClip(bounds);
            using (var back = new SolidBrush(BackColor)) surface.FillRectangle(back, bounds);
            base.OnDrawItem(new DrawItemEventArgs(surface, e.Font, bounds, e.Index, e.State, e.ForeColor, e.BackColor));
            surface.ResetClip();

            CompositingMode was = e.Graphics.CompositingMode;
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            e.Graphics.DrawImage(buffer, bounds, bounds, GraphicsUnit.Pixel);
            e.Graphics.CompositingMode = was;
        }

        private bool EnsureBuffer(Rectangle bounds)
        {
            int w = Math.Max(ClientSize.Width, bounds.Right);
            int h = Math.Max(ClientSize.Height, bounds.Bottom);
            if (w <= 0 || h <= 0) return false;
            if (buffer != null && buffer.Width >= w && buffer.Height >= h) return true;

            ReleaseBuffer();
            try
            {
                buffer = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                surface = Graphics.FromImage(buffer);
                return true;
            }
            catch
            {
                ReleaseBuffer();
                return false;
            }
        }

        private void ReleaseBuffer()
        {
            if (surface != null) { surface.Dispose(); surface = null; }
            if (buffer != null) { buffer.Dispose(); buffer = null; }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            ReleaseBuffer();
            base.OnHandleDestroyed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) ReleaseBuffer();
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
