// @author bdth 2074055628@qq.com
// 文件用途 为高级子页面提供统一的 ROG 模块状态舱
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ModuleBanner : RoundPanel
    {
        private string code = "MODULE // 00";
        private string title = "";
        private string detail = "";
        private string state = "READY";
        private string glyph = "settings";
        private Color stateColor = Theme.Accent;

        public string Code { get { return code; } set { code = value ?? ""; Invalidate(); } }
        public string TitleText { get { return title; } set { title = value ?? ""; Invalidate(); } }
        public string Detail { get { return detail; } set { detail = value ?? ""; Invalidate(); } }
        public string State { get { return state; } set { state = value ?? ""; Invalidate(); } }
        public string Glyph { get { return glyph; } set { glyph = value ?? "settings"; Invalidate(); } }
        public Color StateColor { get { return stateColor; } set { stateColor = value; Invalidate(); } }

        public ModuleBanner()
        {
            Radius = Theme.S(13);
            Fill = Theme.Card;
            Border = Theme.StrokeHi;
            BackColor = Theme.Bg;
            AccentEdge = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Rectangle frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath clip = Theme.TechPath(frame, Theme.S(8)))
            {
                GraphicsState saved = g.Save();
                g.SetClip(clip);
                int bladeX = Width - Theme.S(280);
                Point[] blade = {
                    new Point(bladeX, 0), new Point(Width, 0), new Point(Width, Height),
                    new Point(bladeX + Theme.S(88), Height), new Point(bladeX + Theme.S(36), Height / 2)
                };
                using (var wash = new LinearGradientBrush(
                    new Rectangle(Math.Max(0, bladeX), 0, Math.Max(1, Width - bladeX), Height),
                    Col.Alpha(Theme.Accent, 0), Col.Alpha(Theme.Accent, Theme.LightMode ? 18 : 11),
                    LinearGradientMode.Horizontal))
                    g.FillPolygon(wash, blade);
                using (var etch = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 20 : 13)))
                    for (int x = bladeX - Height; x < Width + Height; x += Theme.S(18))
                        g.DrawLine(etch, x, 0, x + Height, Height);
                g.Restore(saved);
            }

            int socket = Theme.S(42);
            Rectangle iconBox = new Rectangle(Theme.S(16), (Height - socket) / 2, socket, socket);
            using (GraphicsPath iconPath = Theme.TechPath(iconBox, Theme.S(6)))
            {
                using (var fill = new SolidBrush(Col.Lerp(Theme.Inset, Theme.Sel, 0.28f))) g.FillPath(fill, iconPath);
                using (var border = new Pen(Col.Alpha(Theme.Accent, 118))) g.DrawPath(border, iconPath);
            }
            Glyphs.Draw(g, glyph,
                new Rectangle(iconBox.Left + Theme.S(11), iconBox.Top + Theme.S(11), Theme.S(20), Theme.S(20)),
                Theme.Accent);

            int textX = Theme.S(74);
            int stateReserve = Theme.S(260);
            TextRenderer.DrawText(g, code, Theme.Mono(6.2f),
                new Rectangle(textX, Theme.S(9), Width - textX - stateReserve, Theme.S(14)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, title, Theme.UI(10.2f, true),
                new Rectangle(textX, Theme.S(24), Width - textX - stateReserve, Theme.S(23)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, detail, Theme.UI(7.7f, false),
                new Rectangle(textX, Theme.S(48), Width - textX - Theme.S(40), Theme.S(18)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

            int stateX = Width - Theme.S(236);
            using (var halo = new SolidBrush(Col.Alpha(stateColor, 28)))
                g.FillEllipse(halo, stateX, Theme.S(18), Theme.S(20), Theme.S(20));
            using (var dot = new SolidBrush(stateColor))
                g.FillEllipse(dot, stateX + Theme.S(6), Theme.S(24), Theme.S(8), Theme.S(8));
            TextRenderer.DrawText(g, state.ToUpperInvariant(), Theme.MonoFor(state, 7.2f),
                new Rectangle(stateX + Theme.S(28), Theme.S(15), Theme.S(190), Theme.S(28)), stateColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            using (var rail = new Pen(Col.Alpha(stateColor, 128)))
                g.DrawLine(rail, stateX + Theme.S(28), Theme.S(48), Width - Theme.S(18), Theme.S(48));
        }
    }
}
