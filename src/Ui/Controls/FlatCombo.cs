// @author bdth 2074055628@qq.com
// 文件用途 深色主题的下拉选择框 自绘显示区 箭头与列表项

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class FlatCombo : ComboBox
    {
        public FlatCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.Card;
            ForeColor = Theme.Fg;
            Font = Theme.UI(9.5f, false);
            ItemHeight = Theme.S(26);
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            using (var bg = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(bg, e.Bounds);
            if (e.Index < 0) return;
            bool inList = (e.State & DrawItemState.ComboBoxEdit) == 0;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (inList && sel)
                Theme.FillRound(e.Graphics, Rectangle.Inflate(e.Bounds, -Theme.S(3), -Theme.S(2)), Theme.S(6), Theme.Sel);
            TextRenderer.DrawText(e.Graphics, Items[e.Index].ToString(), Font,
                new Rectangle(e.Bounds.X + Theme.S(10), e.Bounds.Y, e.Bounds.Width - Theme.S(14), e.Bounds.Height),
                inList && sel ? Color.White : Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private const int WM_PAINT = 0x000F;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_PAINT) return;
            using (Graphics g = Graphics.FromHwnd(Handle))
            {
                int w = Width, h = Height, btn = Theme.S(24);
                using (var bg = new SolidBrush(Theme.Card)) g.FillRectangle(bg, w - btn - 1, 1, btn, h - 2);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = w - btn / 2f - 1, cy = h / 2f - 1, a = Theme.S(4);
                using (var pen = new Pen(Theme.Dim, Math.Max(1.4f, Theme.S(1))))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round; pen.LineJoin = LineJoin.Round;
                    g.DrawLines(pen, new[] {
                        new PointF(cx - a, cy - a / 2), new PointF(cx, cy + a / 2), new PointF(cx + a, cy - a / 2) });
                }
                using (var pen = new Pen(Theme.Stroke)) g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
            }
        }
    }
}
