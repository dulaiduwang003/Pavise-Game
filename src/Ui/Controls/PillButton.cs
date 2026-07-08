// @author bdth 2074055628@qq.com
// 文件用途 提供圆角胶囊按钮控件

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AegisApp
{
    internal enum BtnKind { Normal, Primary, Danger }

    internal class PillButton : FxControl
    {
        public BtnKind Kind = BtnKind.Normal;

        public PillButton(string text) { Text = text; Height = Dpi.S(34); Font = Theme.UI(9f, false); ForeColor = Theme.Fg; }
        public PillButton(string text, BtnKind kind) : this(text) { Kind = kind; }

        public void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            FillBg(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Value;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            int cut = Math.Max(Theme.S(6), Height / 4);

            using (var path = Theme.TechPath(r, cut))
            {
                if (Kind == BtnKind.Primary)
                {
                    var gr = r; if (gr.Width < 1) gr.Width = 1;
                    using (var lg = new LinearGradientBrush(gr,
                        Col.Lerp(Theme.Accent, Color.White, h * 0.12f),
                        Col.Lerp(Theme.Accent2, Color.White, h * 0.12f), LinearGradientMode.Horizontal))
                        g.FillPath(lg, path);
                    using (var hi = new Pen(Col.Alpha(Color.White, 72)))
                        g.DrawLine(hi, Theme.S(2), Theme.S(1), Width - cut - Theme.S(1), Theme.S(1));
                }
                else if (Kind == BtnKind.Danger)
                {
                    Color top = Col.Lerp(Color.FromArgb(38, 19, 24), Theme.Danger, 0.10f + h * 0.13f);
                    Color bottom = Col.Lerp(Color.FromArgb(21, 15, 18), Theme.Danger, 0.04f + h * 0.10f);
                    using (var lg = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical)) g.FillPath(lg, path);
                    using (var pen = new Pen(Col.Alpha(Theme.Danger, (int)(125 + 80 * h)))) g.DrawPath(pen, path);
                    using (var hi = new Pen(Col.Alpha(Color.White, (int)(24 + 24 * h))))
                        g.DrawLine(hi, Theme.S(2), Theme.S(1), Width - cut - Theme.S(1), Theme.S(1));
                    using (var energy = new Pen(Theme.Danger, Math.Max(1f, Theme.S(2))))
                        g.DrawLine(energy, Theme.S(1), Theme.S(10), Theme.S(1), Height - Theme.S(10));
                }
                else
                {
                    Color top = Col.Lerp(Theme.CardHover, Color.White, 0.015f + h * 0.035f);
                    Color bottom = Col.Lerp(Theme.Card, Color.Black, 0.06f - h * 0.02f);
                    using (var lg = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical)) g.FillPath(lg, path);
                    using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, h))) g.DrawPath(pen, path);
                    using (var hi = new Pen(Col.Alpha(Color.White, (int)(18 + 24 * h))))
                        g.DrawLine(hi, Theme.S(2), Theme.S(1), Width - cut - Theme.S(1), Theme.S(1));
                }
                if (pressed) using (var dk = new SolidBrush(Col.Alpha(Color.Black, 45))) g.FillPath(dk, path);
            }

            Color txt = Kind == BtnKind.Primary ? Theme.OnAccent
                : Kind == BtnKind.Danger ? Col.Lerp(Theme.Danger, Color.White, h * 0.45f)
                : Col.Lerp(Theme.Fg, Color.White, h * 0.2f);
            TextRenderer.DrawText(g, Text, Kind == BtnKind.Primary ? Theme.UI(9f, true) : Font, ClientRectangle, txt,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }


}
