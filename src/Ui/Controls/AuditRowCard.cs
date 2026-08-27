// @author bdth 2074055628@qq.com
// 文件用途 体检行 ROG 风格自绘卡片 标题+状态徽标+证据 说明最多三行超出省略 修复按钮垂直居中且避让证据行
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class AuditRowCard : FxControl
    {
        private const int PadL = 16;
        private const int PadR = 16;
        private const int BtnW = 120;
        private const int BtnH = 32;
        private const int RightCol = 132;
        private const int NoteMaxLines = 3;
        private const float NameSize = 9.5f;
        private const float NoteSize = 8.0f;
        private const float MetaSize = 7.4f;

        private readonly string name;
        private readonly string value;
        private readonly string note;
        private readonly string evidence;
        private readonly bool warn;
        private readonly bool hasButton;
        private readonly string btnText;
        private readonly bool btnPrimary;
        private readonly Action onClick;

        private Rectangle btnRect;
        private Motion btnHover;
        private Motion btnPress;
        private bool btnDown;

        public int LogicalHeight { get; private set; }

        public AuditRowCard(string name, string value, string note, string evidence, bool warn,
            bool hasButton, string btnText, bool btnPrimary, Action onClick, int logicalWidth)
        {
            this.name = name ?? "";
            this.value = value ?? "";
            this.note = note ?? "";
            this.evidence = evidence ?? "";
            this.warn = warn;
            this.hasButton = hasButton;
            this.btnText = btnText ?? "";
            this.btnPrimary = btnPrimary;
            this.onClick = onClick;
            Bg = Theme.Bg;
            Cursor = Cursors.Default;
            btnHover.Speed = 0.30f;
            btnPress.Speed = 0.42f;
            LogicalHeight = ComputeHeight(logicalWidth);
        }

        private int ComputeHeight(int logicalWidth)
        {
            int noteWLogical = logicalWidth - PadL - PadR - (hasButton ? RightCol : 0);
            if (noteWLogical < 40) noteWLogical = 40;
            int lineH = TextRenderer.MeasureText("Ag", Theme.UI(NoteSize, false)).Height;
            if (lineH <= 0) lineH = Theme.S(15);
            int wantPx = note.Length == 0 ? 0 : TextRenderer.MeasureText(note, Theme.UI(NoteSize, false),
                new Size(Theme.S(noteWLogical), int.MaxValue), TextFormatFlags.WordBreak).Height;
            int lines = lineH > 0 ? (wantPx + lineH - 1) / lineH : 1;
            if (lines < 1) lines = 1;
            if (lines > NoteMaxLines) lines = NoteMaxLines;
            int noteLogical = (int)Math.Ceiling(lines * lineH / Dpi.Scale);
            int h = 12 + 22 + 6 + noteLogical + 12;
            int floor = hasButton ? 78 : 66;
            return Math.Max(floor, h);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool on = hasButton && btnRect.Contains(e.Location);
            Cursor = on ? Cursors.Hand : Cursors.Default;
            if (on != (btnHover.Target > 0.5f)) { btnHover.To(on ? 1f : 0f); UiClock.Wake(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            btnDown = false; btnPress.To(0f); btnHover.To(0f); UiClock.Wake();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (hasButton && e.Button == MouseButtons.Left && btnRect.Contains(e.Location))
            {
                btnDown = true; btnPress.Set(1f); UiClock.Wake(); Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            bool was = btnDown; btnDown = false; btnPress.To(0f); UiClock.Wake();
            if (was && e.Button == MouseButtons.Left && btnRect.Contains(e.Location) && onClick != null)
                onClick();
        }

        protected override bool StepAll()
        {
            bool a = base.StepAll();
            bool b = btnHover.Step(); bool c = btnPress.Step();
            return a || b || c;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics; FillBg(g); g.SmoothingMode = SmoothingMode.AntiAlias;
            float h = hover.Value;
            var shell = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Theme.TechPath(shell, Theme.S(9)))
            {
                using (var b = new SolidBrush(Backdrop.CardFill(Col.Lerp(Theme.Card, Theme.CardHover, h * 0.5f)))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Lerp(Theme.Stroke, warn ? Col.Alpha(Theme.Accent, 150) : Theme.StrokeHi,
                    warn ? 0.55f + h * 0.35f : h))) g.DrawPath(pen, p);
            }
            using (var edge = new Pen(warn ? Theme.Accent : Col.Lerp(Theme.Stroke, Theme.Accent, h * 0.6f),
                Math.Max(1f, Theme.S(2))))
                g.DrawLine(edge, 0, Theme.S(12), 0, Height - Theme.S(13));

            int evW = evidence.Length > 0 ? Theme.S(118) : 0;
            int rightColPx = hasButton ? Theme.S(RightCol) : 0;
            int headRight = Width - Theme.S(PadR) - Math.Max(evW, rightColPx);
            int noteRight = Width - Theme.S(PadR) - rightColPx;

            int nameW = TextRenderer.MeasureText(g, name, Theme.UI(NameSize, true)).Width;
            var nameRect = new Rectangle(Theme.S(PadL), Theme.S(11), Math.Min(nameW + Theme.S(4),
                headRight - Theme.S(PadL)), Theme.S(22));
            TextRenderer.DrawText(g, name, Theme.UI(NameSize, true), nameRect, Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            DrawValueBadge(g, nameRect.Right + Theme.S(8), Theme.S(13), headRight);

            if (evW > 0)
            {
                var evRect = new Rectangle(Width - Theme.S(PadR) - evW, Theme.S(12), evW, Theme.S(18));
                TextRenderer.DrawText(g, evidence, Theme.UI(MetaSize, false), evRect, Theme.Faint,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            if (note.Length > 0)
            {
                int noteW = noteRight - Theme.S(PadL);
                if (noteW < Theme.S(40)) noteW = Theme.S(40);
                int lineH = TextRenderer.MeasureText(g, "Ag", Theme.UI(NoteSize, false)).Height;
                int noteTop = Theme.S(39);
                int noteH = Math.Max(lineH, Height - noteTop - Theme.S(10));
                if (lineH > 0) noteH = Math.Min(noteH, lineH * NoteMaxLines);
                var noteRect = new Rectangle(Theme.S(PadL), noteTop, noteW, noteH);
                TextRenderer.DrawText(g, note, Theme.UI(NoteSize, false), noteRect, Theme.Dim,
                    TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            }

            if (hasButton) DrawButton(g);
        }

        private void DrawValueBadge(Graphics g, int x, int y, int rightLimit)
        {
            if (value.Length == 0) return;
            Font vf = Theme.UI(8.0f, true);
            if (warn)
            {
                int tw = TextRenderer.MeasureText(g, value, vf).Width;
                int bw = tw + Theme.S(16);
                if (x + bw > rightLimit) bw = Math.Max(Theme.S(20), rightLimit - x);
                var pill = new Rectangle(x, y, bw, Theme.S(18));
                using (GraphicsPath p = Theme.TechPath(pill, Theme.S(4)))
                {
                    using (var b = new SolidBrush(Col.Alpha(Theme.Accent, 235))) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Accent)) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, value, vf, pill, Theme.OnAccent,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                        | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            else
            {
                var vr = new Rectangle(x, y - Theme.S(1), rightLimit - x, Theme.S(20));
                TextRenderer.DrawText(g, value, vf, vr, Theme.Green,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawButton(Graphics g)
        {
            int bw = Theme.S(BtnW), bh = Theme.S(BtnH);
            int btnTop = (Height - bh) / 2 + Theme.S(4);
            int minTop = Theme.S(12 + 18 + 6);
            if (evidence.Length > 0 && btnTop < minTop) btnTop = minTop;
            btnRect = new Rectangle(Width - Theme.S(PadR) - bw, btnTop, bw, bh);
            float bh2 = btnHover.Value;
            int cut = Math.Max(Theme.S(6), bh / 4);
            using (GraphicsPath p = Theme.TechPath(btnRect, cut))
            {
                if (btnPrimary)
                {
                    using (var lg = new LinearGradientBrush(btnRect,
                        Col.Lerp(Theme.Accent, Color.White, bh2 * 0.14f),
                        Col.Lerp(Theme.Accent2, Color.White, bh2 * 0.14f), LinearGradientMode.Horizontal))
                        g.FillPath(lg, p);
                    using (var hi = new Pen(Col.Alpha(Color.White, 80)))
                        g.DrawLine(hi, btnRect.X + Theme.S(2), btnRect.Y + Theme.S(1),
                            btnRect.Right - cut - Theme.S(1), btnRect.Y + Theme.S(1));
                }
                else
                {
                    Color top = Col.Lerp(Theme.CardHover, Color.White, 0.02f + bh2 * 0.05f);
                    Color bottom = Col.Lerp(Theme.Card, Color.Black, 0.05f);
                    using (var lg = new LinearGradientBrush(btnRect, top, bottom, LinearGradientMode.Vertical))
                        g.FillPath(lg, p);
                    using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.Accent, 0.3f + bh2 * 0.5f)))
                        g.DrawPath(pen, p);
                }
                if (btnPress.Value > 0.01f)
                    using (var dk = new SolidBrush(Col.Alpha(Color.Black, (int)(50 * btnPress.Value))))
                        g.FillPath(dk, p);
            }
            Color ink = btnPrimary ? Theme.OnAccent
                : Col.Lerp(Theme.Fg, Theme.Accent, bh2 * 0.5f);
            TextRenderer.DrawText(g, btnText, Theme.UI(8.75f, btnPrimary), btnRect, ink,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
