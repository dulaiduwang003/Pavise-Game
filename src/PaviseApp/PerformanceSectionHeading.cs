using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class PerformanceSectionHeading : Label
    {
        internal PerformanceLifetime Lifetime { get; set; }

        internal PerformanceSectionKind? SectionKind { get; set; }

        internal int FeatureCount;

        internal int SelectedCount;

        internal int AttentionCount;

        private Rectangle countBounds;

        internal Rectangle TitleBounds { get; private set; }

        internal Rectangle ScopeBounds { get; private set; }

        internal string ScopeText
        {
            get
            {
                return SectionKind.HasValue ? "" : LifetimeScope(Lifetime);
            }
        }

        private string CountText
        {
            get
            {
                return FeatureCount + PerformancePolicy.Text(" 项", (FeatureCount == 1) ? " item" : " items") + ((AttentionCount > 0) ? (" · " + AttentionCount + PerformancePolicy.Text(" 项需处理", " need attention")) : "");
            }
        }

        internal static string SectionTitle(PerformanceSectionKind section)
        {
            return section == PerformanceSectionKind.Restart
                ? PerformancePolicy.Text("需重启电脑", "PC restart required")
                : PerformancePolicy.Text("普通功能", "General features");
        }

        internal static string LifetimeTitle(PerformanceLifetime lifetime)
        {
            switch (lifetime)
            {
            case PerformanceLifetime.Preparation:
                return PerformancePolicy.Text("游戏前准备", "Before the match");
            case PerformanceLifetime.Entry:
                return PerformancePolicy.Text("入局执行", "On match entry");
            case PerformanceLifetime.Session:
                return PerformancePolicy.Text("游戏自动调整", "Automatic game tuning");
            case PerformanceLifetime.Protection:
                return PerformancePolicy.Text("保护期间保持", "While protection is on");
            default:
                return PerformancePolicy.Text("长期设置", "Persistent settings");
            }
        }

        internal static string LifetimeScope(PerformanceLifetime lifetime)
        {
            switch (lifetime)
            {
            case PerformanceLifetime.Preparation:
                return PerformancePolicy.Text("入局后停止", "Stops on match entry");
            case PerformanceLifetime.Entry:
                return PerformancePolicy.Text("入局时最多尝试一次", "At most once on match entry");
            case PerformanceLifetime.Session:
                return PerformancePolicy.Text("退局时恢复", "Restored after the match");
            case PerformanceLifetime.Protection:
                return PerformancePolicy.Text("关闭保护或退出时恢复", "Restored when protection stops or Pavise exits");
            default:
                return PerformancePolicy.Text("退局保留配置", "Configuration kept after the match");
            }
        }

        internal PerformanceSectionHeading()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
            base.TabStop = false;
            base.AccessibleRole = AccessibleRole.StaticText;
        }

        private int Arrange(int width)
        {
            int pad = Math.Min(Theme.S(18), Math.Max(0, (width - 1) / 4));
            int inner = Math.Max(1, width - pad * 2);
            int scaledDimension = Theme.S(8);
            int scaledDimension2 = Theme.S(8);
            int headingHeight = Theme.S(22);
            if (ScopeText.Length == 0)
            {
                int countWidth = Math.Min(inner, TextRenderer.MeasureText(CountText, Theme.UI(8.5f, false), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(4));
                bool inlineCount = inner >= countWidth + scaledDimension + Theme.S(180);
                int titleWidth = Math.Max(1, inlineCount ? inner - countWidth - scaledDimension : inner);
                int titleHeight = Math.Max(headingHeight, TextRenderer.MeasureText(Text, Theme.UI(10f, true), new Size(titleWidth, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
                TitleBounds = new Rectangle(pad, scaledDimension2, titleWidth, titleHeight);
                ScopeBounds = Rectangle.Empty;
                countBounds = new Rectangle(inlineCount ? pad + inner - countWidth : pad, inlineCount ? scaledDimension2 : TitleBounds.Bottom + Theme.S(4), countWidth, headingHeight);
                return Math.Max(TitleBounds.Bottom, countBounds.Bottom) + Theme.S(8);
            }
            int widthOffset = TextRenderer.MeasureText(ScopeText, Theme.UI(8f, false), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(20);
            int widthOffset2 = TextRenderer.MeasureText(CountText, Theme.UI(8.5f, false), Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(4);
            bool fitsSummary = inner >= widthOffset + widthOffset2 + scaledDimension * 2 + Theme.S(180);
            int lowerBound = Math.Max(1, fitsSummary ? (inner - widthOffset - widthOffset2 - scaledDimension * 2) : inner);
            int heightLimit = Math.Max(headingHeight, TextRenderer.MeasureText(Text, Theme.UI(10f, true), new Size(lowerBound, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height);
            TitleBounds = new Rectangle(pad, scaledDimension2, lowerBound, heightLimit);
            int bottomOffset = (fitsSummary ? scaledDimension2 : (TitleBounds.Bottom + Theme.S(6)));
            int padOffset = pad + (fitsSummary ? (inner - widthOffset) : 0);
            ScopeBounds = new Rectangle(padOffset, bottomOffset, Math.Min(inner, widthOffset), headingHeight);
            countBounds = new Rectangle(fitsSummary ? (pad + lowerBound + scaledDimension) : (ScopeBounds.Right + scaledDimension), bottomOffset, Math.Max(0, fitsSummary ? widthOffset2 : (pad + inner - ScopeBounds.Right - scaledDimension)), headingHeight);
            return Math.Max(TitleBounds.Bottom, ScopeBounds.Bottom) + Theme.S(8);
        }

        internal int MeasureHeight(int width)
        {
            return Arrange(Math.Max(1, width));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            DsPaint.CardBackground(graphics, this, Theme.Card);
            Arrange(base.Width);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int mark = Theme.S(3);
            if (TitleBounds.Left >= mark * 3)
            {
                using (SolidBrush accent = new SolidBrush(Theme.Accent))
                {
                    graphics.FillRectangle(accent, TitleBounds.Left - mark * 3, TitleBounds.Top + Theme.S(4), mark, Math.Max(mark, TitleBounds.Height - Theme.S(8)));
                }
            }
            TextRenderer.DrawText(graphics, Text, Theme.UI(10f, true), TitleBounds, Theme.Accent, TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            Color color = Theme.Dim;
            if (!ScopeBounds.IsEmpty)
            {
                using (GraphicsPath path = Theme.Rounded(ScopeBounds, Theme.S(5)))
                {
                    using (SolidBrush brush = new SolidBrush(Col.Lerp(Theme.Bg, color, Theme.LightMode ? 0.08f : 0.12f)))
                    {
                        graphics.FillPath(brush, path);
                    }
                }
                TextRenderer.DrawText(graphics, ScopeText, Theme.UI(8f, false), ScopeBounds, color, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            TextRenderer.DrawText(graphics, CountText, Theme.UI(8.5f, false), countBounds, (AttentionCount > 0) ? Theme.Warning : Theme.Faint, TextFormatFlags.NoPrefix | TextFormatFlags.Right | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
