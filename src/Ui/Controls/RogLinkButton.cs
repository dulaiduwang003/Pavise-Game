// @author bdth 2074055628@qq.com
// File purpose ROG-style external link button at the bottom of the Overview page, shared by the guide and the survey
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // Armor language matches the advanced entry next to it: chamfered frame, slanted light band on the right, icon slot on the left, rail at the bottom
    //   Two kinds of corner mark on the right end: the external-link mark means a browser opens, the forward arrow means a window opens inside the app
    //   Anything that jumps to the browser must draw the external-link mark; do not let users think it is just another page
    internal sealed class RogLinkButton : FxControl
    {
        private readonly string code;
        private readonly string glyph;
        private bool dot;

        public bool External = true;

        // When set the whole button switches to this color; the donate entry uses a warm tint so it stands apart from the two accent-colored entries beside it
        public Color? Tint;
        private Color AccentColor { get { return Tint ?? Theme.Accent; } }

        // Unread red dot, used by announcements; lights only when there is a new one, goes out once seen
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
            surface = Col.Lerp(surface, AccentColor, 0.025f + hot * 0.07f);

            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                using (var fill = new SolidBrush(surface)) g.FillPath(fill, path);
                using (var border = new Pen(Col.Lerp(Theme.Stroke, AccentColor, 0.16f + hot * 0.58f),
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
                    Col.Alpha(AccentColor, 0), Col.Alpha(AccentColor, (int)(16 + hot * 38)),
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
                using (var border = new Pen(Col.Alpha(AccentColor, (int)(78 + hot * 106))))
                    g.DrawPath(border, socketPath);
            }
            Glyphs.Draw(g, glyph,
                new Rectangle(socketBox.Left + Theme.S(6), socketBox.Top + Theme.S(6),
                    Theme.S(14), Theme.S(14)),
                AccentColor);

            int textX = Theme.S(43);
            int rightPad = Theme.S(36);
            // NoPrefix is required: an & in the title must not be eaten as a mnemonic
            //   Without it Help & feedback is drawn as Help _feedback
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
            using (var rail = new Pen(Col.Alpha(AccentColor, (int)(92 + hot * 120)), Math.Max(1f, Theme.S(1))))
                g.DrawLine(rail, textX, railY, textX + railW, railY);
            using (var node = new SolidBrush(AccentColor))
                g.FillRectangle(node, textX - Theme.S(2), railY - Theme.S(2), Theme.S(4), Theme.S(4));
        }

        // Drawn on buttons that open a window inside the app; same mark as the sidebar entries that go one level deeper
        private void DrawForwardMark(Graphics g, float hot, float down)
        {
            int side = Theme.S(9);
            int x = Width - Theme.S(24) + (int)(hot * Theme.S(3)) - (int)(down * Theme.S(1));
            int cy = Height / 2 + Theme.S(1);
            using (var pen = new Pen(Col.Lerp(Theme.Dim, AccentColor, 0.25f + hot * 0.75f),
                Math.Max(1.2f, Theme.S(1))))
            {
                g.DrawLine(pen, x, cy - side / 2, x + side / 2, cy);
                g.DrawLine(pen, x + side / 2, cy, x, cy + side / 2);
            }
        }

        // Red dot pressed into the top-right corner, drawn only when unread; positioned clear of the corner mark itself
        private void DrawUnreadDot(Graphics g)
        {
            int d = Theme.S(7);
            var box = new Rectangle(Width - Theme.S(17), Theme.S(9), d, d);
            using (var fill = new SolidBrush(AccentColor)) g.FillEllipse(fill, box);
            using (var ring = new Pen(Col.Alpha(AccentColor, 90), Math.Max(1f, Theme.S(1))))
                g.DrawEllipse(ring, Rectangle.Inflate(box, Theme.S(2), Theme.S(2)));
        }

        // A box missing its top-right corner plus an arrow pointing up-right; the generic open-in-browser mark
        private void DrawExternalMark(Graphics g, float hot, float down)
        {
            int side = Theme.S(9);
            int x = Width - Theme.S(26) + (int)(hot * Theme.S(2)) - (int)(down * Theme.S(1));
            int y = Height / 2 - side / 2 + Theme.S(1);
            Rectangle box = new Rectangle(x, y, side, side);
            using (var pen = new Pen(Col.Lerp(Theme.Dim, AccentColor, 0.25f + hot * 0.75f),
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
