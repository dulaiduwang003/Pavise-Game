// @author bdth 2074055628@qq.com
// 文件用途 深度调优侧栏顶部的 ROG 风格返回模块
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AdvancedBackBar : FxControl
    {
        public Action BackRequested;

        public AdvancedBackBar()
        {
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleName = Lang.T("v20.advanced.back");
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button == MouseButtons.Left && BackRequested != null) BackRequested();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode != Keys.Enter && e.KeyCode != Keys.Space) return;
            if (BackRequested != null) BackRequested();
            e.Handled = true; e.SuppressKeyPress = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (Backdrop.AppliesTo(this)) Backdrop.PaintOnCard(g, this, ClientRectangle);
            else using (var bg = new SolidBrush(Theme.Nav)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            // 与右侧顶栏底线对齐，替代深度调优侧栏的品牌区。
            using (var p = new Pen(Theme.Stroke)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);

            float hot = hover.Value;
            float down = press.Value;
            Rectangle frame = new Rectangle(Theme.S(14), Theme.S(11), Width - Theme.S(30), Height - Theme.S(23));
            Color surface = Col.Lerp(Theme.Nav, Theme.Card, 0.72f + hot * 0.18f);
            surface = Col.Lerp(surface, Theme.Accent, 0.025f + hot * 0.07f);

            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                using (var fill = new SolidBrush(surface)) g.FillPath(fill, path);
                using (var border = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.16f + hot * 0.58f),
                    Math.Max(1f, Theme.S(1))))
                    g.DrawPath(border, path);

                // 返回是向左 光刃从左侧扫入 与入口按钮的右侧刃互为镜像
                GraphicsState state = g.Save();
                g.SetClip(path);
                int bladeX = frame.Left + Theme.S(52) - (int)(hot * Theme.S(5));
                Point[] blade = {
                    new Point(frame.Left, frame.Top), new Point(bladeX, frame.Top),
                    new Point(bladeX - Theme.S(20), frame.Bottom), new Point(frame.Left, frame.Bottom)
                };
                using (var wash = new LinearGradientBrush(
                    new Rectangle(frame.Left, frame.Top, Math.Max(1, bladeX - frame.Left), frame.Height),
                    Col.Alpha(Theme.Accent, (int)(16 + hot * 38)), Col.Alpha(Theme.Accent, 0),
                    LinearGradientMode.Horizontal))
                    g.FillPolygon(wash, blade);
                g.Restore(state);
            }

            // 图标座 双雪佛龙向左
            int socket = Theme.S(26);
            int shift = (int)(hot * Theme.S(2)) - (int)(down * Theme.S(1));
            Rectangle socketBox = new Rectangle(frame.Left + Theme.S(8), frame.Top + (frame.Height - socket) / 2, socket, socket);
            using (GraphicsPath socketPath = Theme.TechPath(socketBox, Theme.S(5)))
            {
                using (var fill = new SolidBrush(Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + hot * 0.24f)))
                    g.FillPath(fill, socketPath);
                using (var border = new Pen(Col.Alpha(Theme.Accent, (int)(78 + hot * 106))))
                    g.DrawPath(border, socketPath);
            }
            int cx = socketBox.Left + socketBox.Width / 2 - shift;
            int cy = socketBox.Top + socketBox.Height / 2;
            using (var chevron = new Pen(Theme.Accent, Math.Max(1.6f, Theme.S(2) * 0.85f)))
            {
                chevron.StartCap = LineCap.Round; chevron.EndCap = LineCap.Round; chevron.LineJoin = LineJoin.Round;
                int s = Theme.S(4);
                g.DrawLine(chevron, cx + Theme.S(1), cy - s, cx - Theme.S(3), cy);
                g.DrawLine(chevron, cx - Theme.S(3), cy, cx + Theme.S(1), cy + s);
                using (var ghost = new Pen(Col.Alpha(Theme.Accent, (int)(110 + hot * 90)), Math.Max(1.2f, Theme.S(2) * 0.65f)))
                {
                    ghost.StartCap = LineCap.Round; ghost.EndCap = LineCap.Round; ghost.LineJoin = LineJoin.Round;
                    g.DrawLine(ghost, cx + Theme.S(6), cy - s, cx + Theme.S(2), cy);
                    g.DrawLine(ghost, cx + Theme.S(2), cy, cx + Theme.S(6), cy + s);
                }
            }

            int textX = socketBox.Right + Theme.S(10);
            TextRenderer.DrawText(g, "RETURN // MAIN", Theme.Mono(5.5f),
                new Rectangle(textX, frame.Top + Theme.S(4), frame.Right - textX - Theme.S(6), Theme.S(12)),
                Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Lang.T("v20.advanced.back"), Theme.UI(9.2f, true),
                new Rectangle(textX, frame.Top + Theme.S(15), frame.Right - textX - Theme.S(6), Theme.S(20)),
                Col.Lerp(Theme.Dim, Theme.Fg, 0.55f + hot * 0.45f),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

            // 底部 rail 线 hover 时向左点亮
            int railY = frame.Bottom - Theme.S(1);
            int railW = Theme.S(26) + (int)(hot * Theme.S(34));
            using (var rail = new Pen(Col.Alpha(Theme.Accent, (int)(92 + hot * 120)), Math.Max(1f, Theme.S(1))))
                g.DrawLine(rail, frame.Right - Theme.S(8) - railW, railY, frame.Right - Theme.S(8), railY);
            using (var node = new SolidBrush(Theme.Accent))
                g.FillRectangle(node, frame.Right - Theme.S(10), railY - Theme.S(2), Theme.S(4), Theme.S(4));
        }
    }
}
