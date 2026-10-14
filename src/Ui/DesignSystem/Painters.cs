// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 公共绘制：背景、切角框、按钮面、遮罩、徽章、箭头、忙碌轨。颜色全部实时取自 Theme。
    internal static class DsPaint
    {
        // 渐变画刷的矩形必须有正尺寸，极小控件下避免 GDI+ 抛异常。
        public static Rectangle GradientRect(Rectangle r)
        {
            if (r.Width < 1) r.Width = 1;
            if (r.Height < 1) r.Height = 1;
            return r;
        }

        // 背景：有壁纸时按窗口坐标切片，否则纯色填充。
        public static void FillBackground(Graphics g, Control control, Color fill)
        {
            if (Backdrop.AppliesTo(control)) { Backdrop.PaintOnCard(g, control, control.ClientRectangle); return; }
            using (var b = new SolidBrush(fill)) g.FillRectangle(b, control.ClientRectangle);
        }

        public static void TechFrame(Graphics g, Rectangle r, int cut, Color fill, Color stroke)
        {
            using (GraphicsPath path = Theme.TechPath(r, cut))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                if (!stroke.IsEmpty) using (var pen = new Pen(stroke)) g.DrawPath(pen, path);
            }
        }

        // 按钮面：三种类型的底色、描边、高光线、按压压暗与禁用遮罩。
        public static void ButtonFace(Graphics g, GraphicsPath path, Rectangle r, int cut, Size size,
            BtnKind kind, DsState state, Color effBg)
        {
            float h = state.HoverIfEnabled;
            int width = size.Width;
            if (kind == BtnKind.Primary)
            {
                using (var lg = new LinearGradientBrush(GradientRect(r),
                    Col.Lerp(Theme.Accent, Color.White, h * 0.12f),
                    Col.Lerp(Theme.Accent2, Color.White, h * 0.12f), LinearGradientMode.Horizontal))
                    g.FillPath(lg, path);
                using (var hi = new Pen(Col.Alpha(Color.White, 72)))
                    g.DrawLine(hi, Theme.S(2), Theme.S(1), width - cut - Theme.S(1), Theme.S(1));
            }
            else if (kind == BtnKind.Danger)
            {
                Color top = Theme.LightMode
                    ? Col.Lerp(Color.White, Theme.Danger, 0.05f + h * 0.05f)
                    : Col.Lerp(DsPalette.DangerTopDark, Theme.Danger, 0.10f + h * 0.13f);
                Color bottom = Theme.LightMode
                    ? Col.Lerp(Color.White, Theme.Danger, 0.11f + h * 0.07f)
                    : Col.Lerp(DsPalette.DangerBottomDark, Theme.Danger, 0.04f + h * 0.10f);
                using (var lg = new LinearGradientBrush(GradientRect(r), top, bottom, LinearGradientMode.Vertical))
                    g.FillPath(lg, path);
                using (var pen = new Pen(Col.Alpha(Theme.Danger, (int)(Theme.LightMode ? 150 + 70 * h : 125 + 80 * h))))
                    g.DrawPath(pen, path);
                if (!Theme.LightMode)
                    using (var hi = new Pen(Col.Alpha(Color.White, (int)(24 + 24 * h))))
                        g.DrawLine(hi, Theme.S(2), Theme.S(1), width - cut - Theme.S(1), Theme.S(1));
                using (var energy = new Pen(Theme.Danger, DsSpace.Line(2)))
                    g.DrawLine(energy, Theme.S(1), Theme.S(DsMetrics.Button.EnergyInset),
                        Theme.S(1), size.Height - Theme.S(DsMetrics.Button.EnergyInset));
            }
            else
            {
                Color top = Col.Lerp(Theme.CardHover, Color.White, 0.015f + h * 0.035f);
                Color bottom = Col.Lerp(Theme.Card, Color.Black, 0.06f - h * 0.02f);
                using (var lg = new LinearGradientBrush(GradientRect(r), top, bottom, LinearGradientMode.Vertical))
                    g.FillPath(lg, path);
                using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, h))) g.DrawPath(pen, path);
                using (var hi = new Pen(Col.Alpha(Color.White, (int)(18 + 24 * h))))
                    g.DrawLine(hi, Theme.S(2), Theme.S(1), width - cut - Theme.S(1), Theme.S(1));
            }
            if (state.Press > 0.01f)
                using (var dk = new SolidBrush(Col.Alpha(Color.Black, (int)(DsAlpha.PressDarken * state.Press))))
                    g.FillPath(dk, path);
            if (!state.Enabled) Veil(g, path, Col.Alpha(effBg, DsAlpha.VeilButton));
        }

        public static Color ButtonInk(BtnKind kind, DsState state)
        {
            float h = state.HoverIfEnabled;
            if (!state.Enabled) return Theme.Faint;
            if (kind == BtnKind.Primary) return Theme.OnAccent;
            if (kind == BtnKind.Danger) return Col.Lerp(Theme.Danger, Color.White, Theme.LightMode ? 0f : h * 0.45f);
            return Theme.LightMode ? Theme.Fg : Col.Lerp(Theme.Fg, Color.White, h * 0.2f);
        }

        public static void Veil(Graphics g, GraphicsPath path, Color color)
        {
            using (var veil = new SolidBrush(color)) g.FillPath(veil, path);
        }

        public static void Veil(Graphics g, Rectangle rect, Color color)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return;
            using (var veil = new SolidBrush(color)) g.FillRectangle(veil, rect);
        }

        public static int BadgeWidth(Graphics g, string text, Font font)
        {
            return TextRenderer.MeasureText(g, text, font).Width + Theme.S(DsMetrics.Badge.Pad);
        }

        // 徽章：强调态用模式色填充，普通态用内嵌底色。
        public static void Badge(Graphics g, Rectangle pill, string text, Font font, bool strong)
        {
            if (pill.Width <= 0 || pill.Height <= 0) return;
            Color fill = strong ? Theme.Accent : Theme.Inset;
            Color edge = strong ? Col.Alpha(Theme.Accent, 235) : Theme.Stroke;
            Color ink = strong ? Theme.OnAccent : Theme.Faint;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            TechFrame(g, pill, Theme.S(DsMetrics.Badge.Cut), fill, edge);
            TextRenderer.DrawText(g, text, font, pill, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                    | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        // 折叠箭头：pointUp 为真时顶点朝上（已展开）。
        public static void Chevron(Graphics g, float x, float cy, float arm, bool pointUp, Color color)
        {
            float top = pointUp ? cy + arm / 2f : cy - arm / 2f;
            float bottom = pointUp ? cy - arm / 2f : cy + arm / 2f;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(color, Math.Max(1.4f, Theme.S(1))))
            {
                p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                g.DrawLines(p, new[] { new PointF(x, top), new PointF(x + arm, bottom), new PointF(x + arm * 2, top) });
            }
        }

        // 下拉箭头：展开时翻转朝上。
        public static void DropArrow(Graphics g, int x, int y, int delta, bool open, Color color)
        {
            int sign = open ? -1 : 1;
            using (var arrow = new Pen(color, DsSpace.Line(1)))
                g.DrawLines(arrow, new[] { new Point(x - delta, y - sign * delta / 2),
                    new Point(x, y + sign * delta / 2), new Point(x + delta, y - sign * delta / 2) });
        }

        // 忙碌轨：底部细轨加一段随相位移动的亮带，相位 0..1。
        public static void BusyRail(Graphics g, Rectangle bounds, float phase, Color accent)
        {
            int inset = Theme.S(DsSpace.M);
            int y = bounds.Bottom - Theme.S(3);
            int left = bounds.Left + inset, right = bounds.Right - inset;
            if (right - left < Theme.S(DsSpace.M) || y <= bounds.Top) return;
            float thick = DsSpace.Line(2);
            using (var track = new Pen(Col.Alpha(accent, DsAlpha.BusyRail), thick)) g.DrawLine(track, left, y, right, y);
            int span = right - left;
            int band = Math.Max(Theme.S(DsSpace.XL), span / 3);
            if (phase < 0f) phase = 0f; if (phase > 1f) phase = 1f;
            int start = left - band + (int)Math.Round(phase * (span + band));
            int a = Math.Max(left, start), b = Math.Min(right, start + band);
            if (b <= a) return;
            using (var sweep = new Pen(Col.Alpha(accent, DsAlpha.BusySweep), thick)) g.DrawLine(sweep, a, y, b, y);
        }
    }

    // 焦点环：仅在键盘导航进入且控件可交互时显示。
    internal static class DsFocus
    {
#if PAVISE_SELFTEST
        internal static bool ForceCuesForTest;
#endif

        public static bool Wants(bool focused, bool showCues, bool interactive)
        {
#if PAVISE_SELFTEST
            if (ForceCuesForTest) return interactive;
#endif
            return focused && showCues && interactive;
        }

        public static Rectangle Inset(Rectangle r, int logicalInset)
        {
            int px = Theme.S(logicalInset);
            if (r.Width <= px * 2 + 2 || r.Height <= px * 2 + 2) return Rectangle.Empty;
            return Rectangle.Inflate(r, -px, -px);
        }

        public static void Draw(Graphics g, Rectangle r, Color ink, Color back)
        {
            if (r.Width <= 2 || r.Height <= 2) return;
            ControlPaint.DrawFocusRectangle(g, r, ink, back);
        }
    }
}
