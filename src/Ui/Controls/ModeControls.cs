// @author bdth 2074055628@qq.com
// 文件用途 提供性能模式选择控件

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ModeButton : FxControl
    {
        private PerformancePreset mode;
        public Action Clicked;

        public ModeButton() { Bg = Theme.Bg; }

        public void SetMode(PerformancePreset value)
        {
            if (mode == value) return;
            mode = value; Invalidate();
        }

        public void PerformClick()
        {
            if (Enabled) OnClick(EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Clicked != null) Clicked();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; FillBg(g); g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 2, Width - 1, Height - 5);
            using (GraphicsPath p = Theme.TechPath(r, Theme.S(9)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, hover.Value))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.38f + hover.Value * 0.45f))) g.DrawPath(pen, p);
            }
            using (var edge = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2)))) g.DrawLine(edge, 0, Theme.S(13), 0, Height - Theme.S(14));
            Rectangle glyph = new Rectangle(Theme.S(12), Theme.S(12), Theme.S(16), Theme.S(16));
            Glyphs.Draw(g, "game", glyph, Theme.Accent);
            TextRenderer.DrawText(g, ModeName(mode), Theme.UI(9.25f, true),
                new Rectangle(Theme.S(38), Theme.S(5), Width - Theme.S(68), Theme.S(22)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Lang.T("mode.source.global"),
                Theme.UI(7.25f, false), new Rectangle(Theme.S(39), Theme.S(24), Width - Theme.S(72), Theme.S(15)),
                Theme.Dim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            PointF[] chevron = {
                new PointF(Width - Theme.S(22), Theme.S(18)),
                new PointF(Width - Theme.S(17), Theme.S(23)),
                new PointF(Width - Theme.S(12), Theme.S(18)) };
            using (var pen = new Pen(Theme.Dim, Math.Max(1f, Theme.S(1)))) g.DrawLines(pen, chevron);
        }

        internal static string ModeName(PerformancePreset value)
        {
            return value == PerformancePreset.Extreme ? Lang.T("preset.extreme")
                : value == PerformancePreset.Competitive ? Lang.T("preset.competitive")
                : value == PerformancePreset.Custom ? Lang.T("preset.custom") : Lang.T("preset.standard");
        }
    }

    internal sealed class ModeChoice : FxControl
    {
        private readonly PerformancePreset mode;
        private bool selected;
        public Action<PerformancePreset> Chosen;

        public ModeChoice(PerformancePreset value) { mode = value; Bg = Theme.Card; }
        public PerformancePreset Mode { get { return mode; } }
        public void SetSelected(bool value) { if (selected != value) { selected = value; Invalidate(); } }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (Chosen != null) Chosen(mode);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; FillBg(g); g.SmoothingMode = SmoothingMode.AntiAlias;
            Color accent = Theme.ModeColor(mode);
            Rectangle r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.TechPath(r, Theme.S(8)))
            {
                Color fill = selected ? Col.Lerp(Theme.Card, accent, 0.12f) : Col.Lerp(Theme.Card, Theme.CardHover, hover.Value * 0.7f);
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(selected ? Col.Alpha(accent, 210) : Col.Lerp(Theme.Stroke, Theme.StrokeHi, hover.Value))) g.DrawPath(pen, p);
            }
            using (var b = new SolidBrush(accent)) g.FillEllipse(b, Theme.S(15), Theme.S(15), Theme.S(8), Theme.S(8));
            TextRenderer.DrawText(g, ModeButton.ModeName(mode), Theme.UI(9.75f, true),
                new Rectangle(Theme.S(34), Theme.S(7), Theme.S(100), Theme.S(24)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, DetailKey(mode), Theme.UI(7.9f, false),
                new Rectangle(Theme.S(34), Theme.S(28), Width - Theme.S(50), Height - Theme.S(32)), Theme.Dim,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            if (selected)
            {
                Rectangle check = new Rectangle(Width - Theme.S(31), Theme.S(15), Theme.S(16), Theme.S(16));
                using (var pen = new Pen(accent, Math.Max(1.4f, Theme.S(1))))
                    g.DrawLines(pen, new[] { new Point(check.X + Theme.S(2), check.Y + Theme.S(8)), new Point(check.X + Theme.S(6), check.Y + Theme.S(12)), new Point(check.X + Theme.S(14), check.Y + Theme.S(3)) });
            }
        }

        private string DetailKey(PerformancePreset value)
        {
            if (value == PerformancePreset.Extreme) return Lang.T("mode.pick.extreme");
            if (value == PerformancePreset.Competitive) return Lang.T("mode.pick.competitive");
            if (value == PerformancePreset.Custom) return Lang.T("mode.pick.custom");
            return Lang.T("mode.pick.standard");
        }
    }

    internal sealed class ModePickerPanel : RoundPanel
    {
        private readonly ModeChoice[] choices;
        private readonly Label source;
        public Action<PerformancePreset> ModeChosen;

        public ModePickerPanel()
        {
            Fill = Theme.Card; Border = Theme.StrokeHi; BackColor = Theme.Bg; Radius = Theme.S(14); AccentEdge = true;
            var title = new Label
            {
                Text = Lang.T("mode.global.title"), ForeColor = Theme.Fg, BackColor = Theme.Card,
                Font = Theme.UI(11f, true), AutoEllipsis = true
            };
            title.UseCompatibleTextRendering = false;
            title.SetBounds(Theme.S(18), Theme.S(13), Theme.S(356), Theme.S(25));
            source = new Label
            {
                Text = Lang.T("mode.global.sub"), ForeColor = Theme.Dim, BackColor = Theme.Card,
                Font = Theme.UI(7.8f, false), AutoEllipsis = true
            };
            source.UseCompatibleTextRendering = false;
            source.SetBounds(Theme.S(18), Theme.S(38), Theme.S(356), Theme.S(19));
            Controls.AddRange(new Control[] { title, source });

            choices = new[] {
                new ModeChoice(PerformancePreset.Standard),
                new ModeChoice(PerformancePreset.Competitive),
                new ModeChoice(PerformancePreset.Extreme),
                new ModeChoice(PerformancePreset.Custom)
            };
            for (int i = 0; i < choices.Length; i++)
            {
                choices[i].SetBounds(Theme.S(14), Theme.S(66 + i * 70), Theme.S(368), Theme.S(62));
                choices[i].Chosen = Choose;
                Controls.Add(choices[i]);
            }
        }

        private void Choose(PerformancePreset mode) { if (ModeChosen != null) ModeChosen(mode); }

        public void Sync(PerformancePreset global)
        {
            for (int i = 0; i < choices.Length; i++) choices[i].SetSelected(choices[i].Mode == global);
            source.Text = Lang.T("mode.global.sub");
        }
    }

}
