// @author bdth 2074055628@qq.com
// 文件用途 在简洁主导航之外承载完整的深度调优入口
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AdvancedNavPanel : RoundPanel
    {
        public Action<int> Chosen;
        public Action Dismiss;

        public AdvancedNavPanel(string[] titles, string[] glyphs, int[] pageIds)
        {
            Fill = Theme.Card;
            Border = Theme.StrokeHi;
            BackColor = Theme.Bg;
            Radius = Theme.S(16);
            AccentEdge = true;

            var title = MakeLabel(Lang.T("v20.advanced.title"), 24, 19, 560, 30, 15f, true, Theme.Fg);
            var sub = MakeLabel(Lang.T("v20.advanced.sub"), 24, 50, 620, 22, 8.2f, false, Theme.Dim);
            var close = MakeLabel("×", 672, 14, 28, 30, 16f, false, Theme.Dim);
            close.TextAlign = ContentAlignment.MiddleCenter;
            close.AutoEllipsis = false;
            close.Cursor = Cursors.Hand;
            close.Click += delegate { if (Dismiss != null) Dismiss(); };
            Controls.AddRange(new Control[] { title, sub, close });

            int count = Math.Min(titles.Length, Math.Min(glyphs.Length, pageIds.Length));
            for (int i = 0; i < count; i++)
            {
                int col = i % 2;
                int row = i / 2;
                var item = new AdvancedNavItem(titles[i], glyphs[i], pageIds[i], i + 1);
                item.SetBounds(Theme.S(20 + col * 344), Theme.S(88 + row * 66), Theme.S(336), Theme.S(56));
                item.Chosen = Choose;
                Controls.Add(item);
            }
        }

        private Label MakeLabel(string text, int x, int y, int w, int h, float size, bool bold, Color color)
        {
            var label = new Label();
            label.Text = text;
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.Font = Theme.UI(size, bold);
            label.UseCompatibleTextRendering = false;
            label.AutoEllipsis = true;
            label.SetBounds(Theme.S(x), Theme.S(y), Theme.S(w), Theme.S(h));
            return label;
        }

        private void Choose(int pageId)
        {
            if (Chosen != null) Chosen(pageId);
        }
    }

    internal sealed class AdvancedNavItem : FxControl
    {
        private readonly string title;
        private readonly string glyph;
        private readonly int pageId;
        private readonly int channel;
        public Action<int> Chosen;

        public AdvancedNavItem(string text, string icon, int target, int number)
        {
            title = text;
            glyph = icon;
            pageId = target;
            channel = number;
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
            TabStop = false;
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Chosen != null) Chosen(pageId);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            FillBg(g);
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                using (var fill = new SolidBrush(Col.Lerp(Theme.Inset, Theme.CardHover, hover.Value * 0.58f)))
                    g.FillPath(fill, path);
                using (var border = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, hover.Value * 0.62f)))
                    g.DrawPath(border, path);
            }
            Glyphs.Draw(g, glyph, new Rectangle(Theme.S(16), Theme.S(16), Theme.S(24), Theme.S(24)),
                hover.Value > 0.02f ? Theme.Accent : Theme.Dim);
            TextRenderer.DrawText(g, title, Theme.UI(10f, true),
                new Rectangle(Theme.S(54), 0, Width - Theme.S(94), Height), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, channel.ToString("00"), Theme.Mono(6.5f),
                new Rectangle(Width - Theme.S(40), 0, Theme.S(24), Height), Theme.Faint,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
    }
}
