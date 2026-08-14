// @author bdth 2074055628@qq.com
// 文件用途 游戏专栏页的链路状态格与指令按钮

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
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
            TextRenderer.DrawText(g, caption, Theme.MonoFor(caption, 6.1f),
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
