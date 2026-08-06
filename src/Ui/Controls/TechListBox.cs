// @author bdth 2074055628@qq.com
// 文件用途 自绘列表基类 拦截背景擦除消除滚动闪烁

using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class TechListBox : ListBox
    {
        private const int WmEraseBkgnd = 0x0014;

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
