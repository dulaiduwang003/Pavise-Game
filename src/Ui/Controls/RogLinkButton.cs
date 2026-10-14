// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 链接模块按钮：代号、图标、外链/前进标记；Enter/Space 激活并显示焦点环。
    internal sealed class RogLinkButton : FxControl
    {
        private readonly string code;
        private readonly string glyph;
        private bool dot;

        public bool External = true;

        public Color? Tint;
        private Color AccentColor { get { return Tint ?? Theme.Accent; } }

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
            AccessibleRole = AccessibleRole.PushButton;
            Font = DsType.Font(DsText.Body, true);
        }

        public void PerformClick() { if (CanInteract) OnClick(EventArgs.Empty); }

        protected override bool IsInputKey(Keys keyData)
        {
            return DsKeys.IsActivationInput(keyData) || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!DsKeys.IsPlainActivation(e) || !CanInteract) return;
            e.Handled = true; e.SuppressKeyPress = true; PerformClick();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            if (Width < 2 || Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            DsState state = State;
            float hot = state.HoverIfEnabled;
            float down = state.Press;
            Rectangle frame = new Rectangle(0, Theme.S(DsMetrics.Link.FrameTop), Width - 1,
                Height - Theme.S(DsMetrics.Link.FrameBottomInset));
            Color surface = Col.Lerp(Theme.Nav, Theme.Card, 0.72f + hot * 0.18f);
            surface = Col.Lerp(surface, AccentColor, 0.025f + hot * 0.07f);

            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(DsMetrics.Link.Cut)))
            {
                using (var fill = new SolidBrush(surface)) g.FillPath(fill, path);
                using (var border = new Pen(Col.Lerp(Theme.Stroke, AccentColor, 0.16f + hot * 0.58f), DsSpace.Line(1)))
                    g.DrawPath(border, path);

                GraphicsState saved = g.Save();
                g.SetClip(path);
                int bladeX = Width - Theme.S(DsMetrics.Link.BladeReach) + (int)(hot * Theme.S(DsMetrics.Link.BladeShift));
                int tail = Theme.S(DsMetrics.Link.BladeTail);
                Point[] blade = {
                    new Point(bladeX, frame.Top), new Point(Width, frame.Top),
                    new Point(Width, frame.Bottom), new Point(bladeX - tail, frame.Bottom)
                };
                using (var wash = new LinearGradientBrush(
                    DsPaint.GradientRect(new Rectangle(Math.Max(0, bladeX - tail), frame.Top,
                        Math.Max(1, Width - bladeX + tail), frame.Height)),
                    Col.Alpha(AccentColor, 0), Col.Alpha(AccentColor, (int)(16 + hot * 38)),
                    LinearGradientMode.Horizontal))
                    g.FillPolygon(wash, blade);
                g.Restore(saved);
                PaintBusy(g, frame, path);
            }

            int socket = Theme.S(DsMetrics.Link.Socket);
            Rectangle socketBox = new Rectangle(Theme.S(DsMetrics.Link.SocketX), (Height - socket) / 2, socket, socket);
            DsPaint.TechFrame(g, socketBox, Theme.S(DsMetrics.Link.SocketCut),
                Col.Lerp(Theme.Inset, Theme.Sel, 0.22f + hot * 0.24f), Col.Alpha(AccentColor, (int)(78 + hot * 106)));
            Glyphs.Draw(g, glyph,
                new Rectangle(socketBox.Left + Theme.S(DsMetrics.Link.GlyphInset), socketBox.Top + Theme.S(DsMetrics.Link.GlyphInset),
                    Theme.S(DsMetrics.Link.Glyph), Theme.S(DsMetrics.Link.Glyph)),
                AccentColor);

            int textX = Theme.S(DsMetrics.Link.TextX);
            int rightPad = Theme.S(DsMetrics.Link.RightPad);
            TextRenderer.DrawText(g, code, DsType.Mono(DsMono.Code),
                new Rectangle(textX, Theme.S(DsMetrics.Link.CodeY), Math.Max(1, Width - textX - rightPad), Theme.S(DsMetrics.Link.CodeH)),
                Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, Text, DsType.Font(DsText.Body, true),
                new Rectangle(textX, Theme.S(DsMetrics.Link.TitleY), Math.Max(1, Width - textX - rightPad), Theme.S(DsMetrics.Link.TitleH)),
                Col.Lerp(Theme.Dim, Theme.Fg, 0.55f + hot * 0.45f),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (External) DrawExternalMark(g, hot, down);
            else DrawForwardMark(g, hot, down);
            if (dot) DrawUnreadDot(g);

            int railY = Height - Theme.S(DsMetrics.Link.RailBottom);
            int railW = Theme.S(DsMetrics.Link.RailBase) + (int)(hot * Theme.S(DsMetrics.Link.RailReach));
            using (var rail = new Pen(Col.Alpha(AccentColor, (int)(92 + hot * 120)), DsSpace.Line(1)))
                g.DrawLine(rail, textX, railY, textX + railW, railY);
            int node = Theme.S(DsMetrics.Link.Node);
            using (var nodeBrush = new SolidBrush(AccentColor))
                g.FillRectangle(nodeBrush, textX - Theme.S(2), railY - Theme.S(2), node, node);

            if (!Enabled)
                using (GraphicsPath path = Theme.TechPath(frame, Theme.S(DsMetrics.Link.Cut)))
                    DsPaint.Veil(g, path, Col.Alpha(EffBg, DsAlpha.VeilButton));
            if (WantsFocusCue)
                DsFocus.Draw(g, DsFocus.Inset(frame, DsMetrics.Link.FocusInset), Theme.Fg, surface);
        }

        private void DrawForwardMark(Graphics g, float hot, float down)
        {
            int side = Theme.S(DsMetrics.Link.MarkSide);
            int x = Width - Theme.S(DsMetrics.Link.ForwardX) + (int)(hot * Theme.S(3)) - (int)(down * Theme.S(1));
            int cy = Height / 2 + Theme.S(1);
            using (var pen = new Pen(Col.Lerp(Theme.Dim, AccentColor, 0.25f + hot * 0.75f), Math.Max(1.2f, Theme.S(1))))
            {
                g.DrawLine(pen, x, cy - side / 2, x + side / 2, cy);
                g.DrawLine(pen, x + side / 2, cy, x, cy + side / 2);
            }
        }

        private void DrawUnreadDot(Graphics g)
        {
            int d = Theme.S(DsMetrics.Link.DotSize);
            var box = new Rectangle(Width - Theme.S(DsMetrics.Link.DotRight), Theme.S(DsMetrics.Link.DotTop), d, d);
            using (var fill = new SolidBrush(AccentColor)) g.FillEllipse(fill, box);
            int ring = Theme.S(DsMetrics.Link.DotRing);
            using (var ringPen = new Pen(Col.Alpha(AccentColor, 90), DsSpace.Line(1)))
                g.DrawEllipse(ringPen, Rectangle.Inflate(box, ring, ring));
        }

        private void DrawExternalMark(Graphics g, float hot, float down)
        {
            int side = Theme.S(DsMetrics.Link.MarkSide);
            int x = Width - Theme.S(DsMetrics.Link.ExternalX) + (int)(hot * Theme.S(2)) - (int)(down * Theme.S(1));
            int y = Height / 2 - side / 2 + Theme.S(1);
            Rectangle box = new Rectangle(x, y, side, side);
            using (var pen = new Pen(Col.Lerp(Theme.Dim, AccentColor, 0.25f + hot * 0.75f), Math.Max(1.2f, Theme.S(1))))
            {
                int notch = Theme.S(DsMetrics.Link.Notch);
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
