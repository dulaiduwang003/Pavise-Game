// @author bdth 2074055628@qq.com
// 文件用途 绘制概览页底部的 ROG 风格高级设置入口
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AdvancedEntryButton : FxControl
    {
        public AdvancedEntryButton(string text)
        {
            Text = text;
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
                int bladeX = Width - Theme.S(58) + (int)(hot * Theme.S(5));
                Point[] blade = {
                    new Point(bladeX, frame.Top), new Point(Width, frame.Top),
                    new Point(Width, frame.Bottom), new Point(bladeX - Theme.S(27), frame.Bottom)
                };
                using (var wash = new LinearGradientBrush(
                    new Rectangle(Math.Max(0, bladeX - Theme.S(27)), frame.Top,
                        Math.Max(1, Width - bladeX + Theme.S(27)), frame.Height),
                    Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, (int)(16 + hot * 38)),
                    LinearGradientMode.Horizontal))
                    g.FillPolygon(wash, blade);
                g.Restore(state);
            }

            int socket = Theme.S(30);
            Rectangle socketBox = new Rectangle(Theme.S(10), (Height - socket) / 2, socket, socket);
            using (GraphicsPath socketPath = Theme.TechPath(socketBox, Theme.S(5)))
            {
                using (var fill = new SolidBrush(Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + hot * 0.24f)))
                    g.FillPath(fill, socketPath);
                using (var border = new Pen(Col.Alpha(Theme.Accent, (int)(78 + hot * 106))))
                    g.DrawPath(border, socketPath);
            }
            Glyphs.Draw(g, "settings",
                new Rectangle(socketBox.Left + Theme.S(7), socketBox.Top + Theme.S(7), Theme.S(16), Theme.S(16)),
                Theme.Accent);

            int textX = Theme.S(50);
            TextRenderer.DrawText(g, "CONTROL // 06", Theme.Mono(5.5f),
                new Rectangle(textX, Theme.S(7), Width - textX - Theme.S(50), Theme.S(12)),
                Theme.Faint, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Text, Theme.UI(9.2f, true),
                new Rectangle(textX, Theme.S(18), Width - textX - Theme.S(48), Theme.S(22)),
                Col.Lerp(Theme.Dim, Theme.Fg, 0.55f + hot * 0.45f),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            int arrowX = Width - Theme.S(23) + (int)(hot * Theme.S(3)) - (int)(down * Theme.S(1));
            int arrowY = Height / 2;
            using (var arrow = new Pen(Col.Lerp(Theme.Dim, Theme.Accent, 0.25f + hot * 0.75f),
                Math.Max(1.2f, Theme.S(1))))
            {
                g.DrawLine(arrow, arrowX - Theme.S(4), arrowY - Theme.S(5), arrowX + Theme.S(1), arrowY);
                g.DrawLine(arrow, arrowX + Theme.S(1), arrowY, arrowX - Theme.S(4), arrowY + Theme.S(5));
            }

            int railY = Height - Theme.S(4);
            int railW = Theme.S(40) + (int)(hot * Theme.S(50));
            using (var rail = new Pen(Col.Alpha(Theme.Accent, (int)(92 + hot * 120)), Math.Max(1f, Theme.S(1))))
                g.DrawLine(rail, textX, railY, textX + railW, railY);
            using (var node = new SolidBrush(Theme.Accent))
                g.FillRectangle(node, textX - Theme.S(2), railY - Theme.S(2), Theme.S(4), Theme.S(4));
        }
    }
}
