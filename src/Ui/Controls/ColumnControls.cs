// @author bdth 2074055628@qq.com

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ColumnCommandDeck : FxControl
    {
        private float phase;
        private bool live;
        private string eyebrow = "";
        private string title = "";
        private string subtitle = "";
        private string state = "";
        private string glyph = "lol";
        private string memoryLabel = "";
        private string memoryValue = "";
        private string processValue = "";

        public ColumnCommandDeck()
        {
            Cursor = Cursors.Default;
            BackColor = Theme.Bg;
        }

        public string Eyebrow { get { return eyebrow; } set { eyebrow = value ?? ""; Invalidate(); } }
        public string Title { get { return title; } set { title = value ?? ""; Invalidate(); } }
        public string Subtitle { get { return subtitle; } set { subtitle = value ?? ""; Invalidate(); } }
        public string MemoryLabel { get { return memoryLabel; } set { memoryLabel = value ?? ""; Invalidate(); } }
        public string Glyph { get { return glyph; } set { glyph = value ?? "lol"; Invalidate(); } }

        public void SetState(string stateText, string memoryText, string processText, bool isLive)
        {
            state = stateText ?? "";
            memoryValue = memoryText ?? "";
            processValue = processText ?? "";
            live = isLive;
            Invalidate();
            if (live) UiClock.WakeSlow();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiClock.SlowFrame += OnSlowFrame;
            if (live && Visible) UiClock.WakeSlow();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UiClock.SlowFrame -= OnSlowFrame;
            base.OnHandleDestroyed(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (live && Visible) UiClock.WakeSlow();
        }

        private void OnSlowFrame(object sender, EventArgs e)
        {
            if (!live || !Visible) return;
            UiClock.WakeSlow();
            phase += 0.56f;
            if (phase > 6.2832f) phase -= 6.2832f;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(13)))
            using (var fill = new LinearGradientBrush(frame,
                Col.Lerp(Theme.Card, Theme.Accent2, 0.13f),
                Col.Lerp(Theme.Inset, Theme.Accent, 0.035f), LinearGradientMode.Horizontal))
            {
                g.FillPath(fill, path);
                using (var border = new Pen(Col.Alpha(Theme.Accent, live ? 168 : 92))) g.DrawPath(border, path);
            }

            using (var scan = new Pen(Col.Alpha(Color.White, 7)))
                for (int x = -Height; x < Width; x += Theme.S(24))
                    g.DrawLine(scan, x, 0, x + Height, Height);

            float pulse = live ? (float)((Math.Sin(phase) + 1d) * 0.5d) : 0f;
            int socket = Theme.S(70);
            int sx = Theme.S(24);
            int sy = (Height - socket) / 2;
            using (var glow = new SolidBrush(Col.Alpha(Theme.Accent, 18 + (int)(pulse * 20))))
                g.FillEllipse(glow, sx - Theme.S(9), sy - Theme.S(9), socket + Theme.S(18), socket + Theme.S(18));
            using (GraphicsPath socketPath = Theme.TechPath(new Rectangle(sx, sy, socket, socket), Theme.S(12)))
            {
                using (var fill = new SolidBrush(Col.Lerp(Theme.Inset, Theme.Sel, live ? 0.42f : 0.2f))) g.FillPath(fill, socketPath);
                using (var border = new Pen(Col.Alpha(Theme.Accent, live ? 210 : 100), Math.Max(1f, Theme.S(1)))) g.DrawPath(border, socketPath);
            }
            Glyphs.Draw(g, glyph, new Rectangle(sx + Theme.S(17), sy + Theme.S(17), Theme.S(36), Theme.S(36)), Theme.Accent);

            int tx = sx + socket + Theme.S(22);
            int rightX = Width - Theme.S(238);
            TextRenderer.DrawText(g, eyebrow, Theme.Mono(6.8f),
                new Rectangle(tx, Theme.S(15), rightX - tx - Theme.S(14), Theme.S(15)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, title, Theme.UI(14.5f, true),
                new Rectangle(tx, Theme.S(32), rightX - tx - Theme.S(14), Theme.S(31)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, subtitle, Theme.UI(7.8f, false),
                new Rectangle(tx, Theme.S(67), rightX - tx - Theme.S(14), Theme.S(38)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);

            using (var divider = new Pen(Col.Alpha(Theme.StrokeHi, 124)))
                g.DrawLine(divider, rightX, Theme.S(15), rightX, Height - Theme.S(15));
            using (var liveLine = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
                g.DrawLine(liveLine, rightX, Theme.S(23), rightX, Theme.S(52));

            Color stateColor = live ? Theme.Green : Theme.Faint;
            using (var dot = new SolidBrush(stateColor))
                g.FillEllipse(dot, rightX + Theme.S(18), Theme.S(17), Theme.S(7), Theme.S(7));
            TextRenderer.DrawText(g, state, Theme.UI(8f, true),
                new Rectangle(rightX + Theme.S(32), Theme.S(10), Theme.S(184), Theme.S(22)), stateColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, memoryLabel, Theme.Mono(6.4f),
                new Rectangle(rightX + Theme.S(18), Theme.S(42), Theme.S(194), Theme.S(15)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, memoryValue, Theme.UI(16f, true),
                new Rectangle(rightX + Theme.S(18), Theme.S(57), Theme.S(194), Theme.S(31)), Theme.Accent,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, processValue, Theme.Mono(6.6f),
                new Rectangle(rightX + Theme.S(18), Theme.S(91), Theme.S(194), Theme.S(17)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            using (var energy = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
            {
                g.DrawLine(energy, Theme.S(1), Theme.S(19), Theme.S(1), Height - Theme.S(24));
                g.DrawLine(energy, Theme.S(1), Height - Theme.S(24), Theme.S(14), Height - Theme.S(11));
            }
        }
    }

    internal sealed class ColumnTelemetryCell : Control
    {
        private string caption = "";
        private string value = "";
        private string detail = "";
        private Color stateColor = Theme.Faint;
        private string glyph = "lol";

        public ColumnTelemetryCell()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }

        public string Caption { get { return caption; } set { caption = value ?? ""; Invalidate(); } }
        public string Glyph { get { return glyph; } set { glyph = value ?? "lol"; Invalidate(); } }

        public void SetValue(string valueText, string detailText, Color color)
        {
            value = valueText ?? "";
            detail = detailText ?? "";
            stateColor = color;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var bg = new SolidBrush(Theme.Bg)) g.FillRectangle(bg, ClientRectangle);
            Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(7)))
            {
                using (var fill = new SolidBrush(Theme.Card)) g.FillPath(fill, path);
                using (var border = new Pen(Theme.Stroke)) g.DrawPath(border, path);
            }

            int icon = Theme.S(25);
            int ix = Theme.S(12);
            int iy = (Height - icon) / 2;
            using (GraphicsPath socket = Theme.TechPath(new Rectangle(ix, iy, icon, icon), Theme.S(5)))
            {
                using (var fill = new SolidBrush(Col.Alpha(stateColor, 22))) g.FillPath(fill, socket);
                using (var border = new Pen(Col.Alpha(stateColor, 112))) g.DrawPath(border, socket);
            }
            Glyphs.Draw(g, glyph, new Rectangle(ix + Theme.S(5), iy + Theme.S(5), Theme.S(15), Theme.S(15)), stateColor);

            int tx = Theme.S(48);
            TextRenderer.DrawText(g, caption, Theme.Mono(6.1f),
                new Rectangle(tx, Theme.S(7), Width - tx - Theme.S(10), Theme.S(13)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, value, Theme.UI(9.25f, true),
                new Rectangle(tx, Theme.S(20), Width - tx - Theme.S(10), Theme.S(21)), stateColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, detail, Theme.UI(6.7f, false),
                new Rectangle(tx, Theme.S(41), Width - tx - Theme.S(10), Height - Theme.S(46)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            using (var line = new Pen(Col.Alpha(stateColor, 132)))
                g.DrawLine(line, Theme.S(48), Height - Theme.S(3), Math.Min(Width - Theme.S(10), Theme.S(102)), Height - Theme.S(3));
        }
    }

    internal sealed class ColumnActionButton : PillButton
    {
        public ColumnActionButton(string text) : base(text)
        {
        }

        public ColumnActionButton(string text, BtnKind kind) : base(text, kind)
        {
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Enabled) return;
            using (var veil = new SolidBrush(Col.Alpha(Theme.Bg, 148)))
                e.Graphics.FillRectangle(veil, ClientRectangle);
            TextRenderer.DrawText(e.Graphics, Text, Theme.UI(9f, false), ClientRectangle, Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
