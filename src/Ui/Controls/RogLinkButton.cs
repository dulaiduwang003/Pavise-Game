// @author bdth 2074055628@qq.com
// 文件用途 绘制概览页底部的 ROG 风格外链按钮 教程与问卷共用
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 装甲语言与旁边的高级入口保持一致 切角框 右侧斜切光带 左侧图标插槽 底部导轨
    //   右端角标分两种 外链标记表示会打开浏览器 前进箭头表示在程序里开一个窗
    //   点了跳浏览器的必须画外链标记 别让用户以为只是换个页面
    internal sealed class RogLinkButton : FxControl
    {
        private readonly string code;
        private readonly string glyph;
        private bool dot;

        public bool External = true;

        // 未读红点 公告用 有新的才亮 看过就灭
        public bool Dot
        {
            get { return dot; }
            set { if (dot != value) { dot = value; Invalidate(); } }
        }

        public RogLinkButton(string text, string code, string glyph)
        {
            Text = text;
            this.code = code ?? "";
            this.glyph = glyph ?? "info";
            TabStop = true;
            AccessibleName = text;
            Font = Theme.UI(9f, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float hot = hover.Value;
            float down = press.Value;
            Rectangle frame = new Rectangle(0, Theme.S(2), Width - 1, Height - Theme.S(4));
            Color surface = Col.Lerp(Theme.Nav, Theme.Card, 0.72f + hot * 0.18f);
            surface = Col.Lerp(surface, Theme.Accent, 0.025f + hot * 0.07f);

            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                using (var fill = new SolidBrush(surface)) g.FillPath(fill, path);
                using (var border = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.16f + hot * 0.58f),
                    Math.Max(1f, Theme.S(1))))
                    g.DrawPath(border, path);

                GraphicsState state = g.Save();
                g.SetClip(path);
                int bladeX = Width - Theme.S(44) + (int)(hot * Theme.S(5));
                Point[] blade = {
                    new Point(bladeX, frame.Top), new Point(Width, frame.Top),
                    new Point(Width, frame.Bottom), new Point(bladeX - Theme.S(21), frame.Bottom)
                };
                using (var wash = new LinearGradientBrush(
                    new Rectangle(Math.Max(0, bladeX - Theme.S(21)), frame.Top,
                        Math.Max(1, Width - bladeX + Theme.S(21)), frame.Height),
                    Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, (int)(16 + hot * 38)),
                    LinearGradientMode.Horizontal))
                    g.FillPolygon(wash, blade);
                g.Restore(state);
            }

            int socket = Theme.S(26);
            Rectangle socketBox = new Rectangle(Theme.S(9), (Height - socket) / 2, socket, socket);
            using (GraphicsPath socketPath = Theme.TechPath(socketBox, Theme.S(5)))
            {
                using (var fill = new SolidBrush(Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + hot * 0.24f)))
                    g.FillPath(fill, socketPath);
                using (var border = new Pen(Col.Alpha(Theme.Accent, (int)(78 + hot * 106))))
                    g.DrawPath(border, socketPath);
            }
            Glyphs.Draw(g, glyph,
                new Rectangle(socketBox.Left + Theme.S(6), socketBox.Top + Theme.S(6),
                    Theme.S(14), Theme.S(14)),
                Theme.Accent);

            int textX = Theme.S(43);
            int rightPad = Theme.S(36);
            // NoPrefix 是必须的 标题里出现 & 时不能被当成助记符吃掉那个字符
            //   Help & feedback 少了它会画成 Help _feedback
            TextRenderer.DrawText(g, code, Theme.Mono(5.5f),
                new Rectangle(textX, Theme.S(7), Math.Max(1, Width - textX - rightPad), Theme.S(12)),
                Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Text, Theme.UI(9f, true),
                new Rectangle(textX, Theme.S(18), Math.Max(1, Width - textX - rightPad), Theme.S(22)),
                Col.Lerp(Theme.Dim, Theme.Fg, 0.55f + hot * 0.45f),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (External) DrawExternalMark(g, hot, down);
            else DrawForwardMark(g, hot, down);
            if (dot) DrawUnreadDot(g);

            int railY = Height - Theme.S(4);
            int railW = Theme.S(30) + (int)(hot * Theme.S(44));
            using (var rail = new Pen(Col.Alpha(Theme.Accent, (int)(92 + hot * 120)), Math.Max(1f, Theme.S(1))))
                g.DrawLine(rail, textX, railY, textX + railW, railY);
            using (var node = new SolidBrush(Theme.Accent))
                g.FillRectangle(node, textX - Theme.S(2), railY - Theme.S(2), Theme.S(4), Theme.S(4));
        }

        // 在程序里开窗的按钮画这个 和侧栏那些进入下一层的入口同一个记号
        private void DrawForwardMark(Graphics g, float hot, float down)
        {
            int side = Theme.S(9);
            int x = Width - Theme.S(24) + (int)(hot * Theme.S(3)) - (int)(down * Theme.S(1));
            int cy = Height / 2 + Theme.S(1);
            using (var pen = new Pen(Col.Lerp(Theme.Dim, Theme.Accent, 0.25f + hot * 0.75f),
                Math.Max(1.2f, Theme.S(1))))
            {
                g.DrawLine(pen, x, cy - side / 2, x + side / 2, cy);
                g.DrawLine(pen, x + side / 2, cy, x, cy + side / 2);
            }
        }

        // 红点压在右上角 未读时才画 位置避开角标本身
        private void DrawUnreadDot(Graphics g)
        {
            int d = Theme.S(7);
            var box = new Rectangle(Width - Theme.S(17), Theme.S(9), d, d);
            using (var fill = new SolidBrush(Theme.Accent)) g.FillEllipse(fill, box);
            using (var ring = new Pen(Col.Alpha(Theme.Accent, 90), Math.Max(1f, Theme.S(1))))
                g.DrawEllipse(ring, Rectangle.Inflate(box, Theme.S(2), Theme.S(2)));
        }

        // 缺右上角的方框加一支朝右上的箭头 通用的"在浏览器中打开"记号
        private void DrawExternalMark(Graphics g, float hot, float down)
        {
            int side = Theme.S(9);
            int x = Width - Theme.S(26) + (int)(hot * Theme.S(2)) - (int)(down * Theme.S(1));
            int y = Height / 2 - side / 2 + Theme.S(1);
            Rectangle box = new Rectangle(x, y, side, side);
            using (var pen = new Pen(Col.Lerp(Theme.Dim, Theme.Accent, 0.25f + hot * 0.75f),
                Math.Max(1.2f, Theme.S(1))))
            {
                int notch = Theme.S(3);
                g.DrawLine(pen, box.Left, box.Top + notch, box.Left, box.Bottom);
                g.DrawLine(pen, box.Left, box.Bottom, box.Right, box.Bottom);
                g.DrawLine(pen, box.Right, box.Bottom, box.Right, box.Top + notch);

                int ax = box.Right + notch;
                int ay = box.Top - notch;
                g.DrawLine(pen, box.Left + notch, box.Bottom - notch, ax, ay);
                g.DrawLine(pen, ax - Theme.S(4), ay, ax, ay);
                g.DrawLine(pen, ax, ay, ax, ay + Theme.S(4));
            }
        }
    }
}
