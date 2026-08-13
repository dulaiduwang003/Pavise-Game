// @author bdth 2074055628@qq.com
// 文件用途 ROG 风格切角标签条 页面内分组切换 选中态带 accent 刻线和 mono 序号

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class TechTabs : Control
    {
        private string[] labels = new string[0];
        private string[] hints = new string[0];
        private int idx;
        private int hoverIdx = -1;
        private Motion slide;
        private Motion[] glow = new Motion[0];
        public Action<int> IndexChanged;

        public TechTabs()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Cursor = Cursors.Hand;
            slide.Speed = 0.30f;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UiClock.Frame += OnFrame;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            UiClock.Frame -= OnFrame;
            base.OnHandleDestroyed(e);
        }

        private void OnFrame(object s, EventArgs e)
        {
            bool moved = slide.Step();
            if (moved && Math.Abs(slide.Value - slide.Target) < 0.75f) slide.Set(slide.Target);
            for (int i = 0; i < glow.Length; i++) if (glow[i].Step()) moved = true;
            if (moved) Invalidate();
        }

        public void SetTabs(string[] tabLabels, string[] tabHints)
        {
            labels = tabLabels ?? new string[0];
            hints = tabHints ?? new string[0];
            if (idx >= labels.Length) idx = 0;
            glow = new Motion[labels.Length];
            for (int i = 0; i < glow.Length; i++) glow[i].Speed = 0.26f;
            slide.Set(TabRect(idx).X);
            Invalidate();
        }

        public int Index
        {
            get { return idx; }
            set
            {
                if (value < 0 || value >= labels.Length || value == idx) return;
                idx = value;
                slide.To(TabRect(idx).X);
                UiClock.Wake();
                Invalidate();
                if (IndexChanged != null) IndexChanged(idx);
            }
        }

        private int TabW { get { return Theme.S(122); } }
        private int TabGap { get { return Theme.S(6); } }

        private Rectangle TabRect(int i)
        {
            return new Rectangle(i * (TabW + TabGap), Theme.S(2), TabW, Height - Theme.S(5));
        }

        private int HitIndex(Point p)
        {
            for (int i = 0; i < labels.Length; i++) if (TabRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitIndex(e.Location);
            if (hit != hoverIdx) { hoverIdx = hit; SyncGlow(); Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIdx != -1) { hoverIdx = -1; SyncGlow(); Invalidate(); }
        }

        private void SyncGlow()
        {
            for (int i = 0; i < glow.Length; i++) glow[i].To(i == hoverIdx ? 1f : 0f);
            UiClock.Wake();
        }

        private float GlowAt(int i)
        {
            return i >= 0 && i < glow.Length ? glow[i].Value : 0f;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int hit = HitIndex(e.Location);
            if (hit >= 0) Index = hit;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cut = Theme.S(9);
            for (int i = 0; i < labels.Length; i++)
            {
                Rectangle r = TabRect(i);
                r.Width -= 1; r.Height -= 1;
                float h = GlowAt(i);
                using (GraphicsPath p = Theme.TechPath(r, cut))
                {
                    using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, h))) g.FillPath(b, p);
                    using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, h))) g.DrawPath(pen, p);
                }
                using (var corner = new Pen(Col.Alpha(Theme.Accent, (int)(70 + 60 * h)),
                    Math.Max(1f, Theme.S(1))))
                    g.DrawLine(corner, r.Right - cut, r.Top, r.Right, r.Top + cut);
            }

            if (labels.Length > 0)
            {
                Rectangle sr = TabRect(idx);
                sr.X = (int)slide.Value;
                sr.Width -= 1; sr.Height -= 1;
                using (GraphicsPath p = Theme.TechPath(sr, cut))
                {
                    using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.Accent, 0.16f))) g.FillPath(b, p);
                    using (var pen = new Pen(Col.Alpha(Theme.Accent, 220))) g.DrawPath(pen, p);
                }
                using (var mark = new Pen(Theme.Accent, Math.Max(1f, Theme.S(2))))
                    g.DrawLine(mark, sr.Left, sr.Top + cut, sr.Left, sr.Bottom - cut);
                using (var corner = new Pen(Col.Alpha(Theme.Accent, 200), Math.Max(1f, Theme.S(1))))
                    g.DrawLine(corner, sr.Right - cut, sr.Top, sr.Right, sr.Top + cut);
            }

            for (int i = 0; i < labels.Length; i++)
            {
                Rectangle r = TabRect(i);
                r.Width -= 1; r.Height -= 1;
                bool selected = i == idx;
                string no = (i + 1).ToString("00");
                Font noFont = Theme.Mono(7.5f);
                Size noSize = TextRenderer.MeasureText(g, no, noFont, Size.Empty, TextFormatFlags.NoPadding);
                int textLeft = r.Left + Theme.S(14);
                TextRenderer.DrawText(g, no, noFont,
                    new Rectangle(textLeft, r.Top, noSize.Width, r.Height),
                    selected ? Theme.Accent : Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, labels[i], Theme.UI(8.25f, selected),
                    new Rectangle(textLeft + noSize.Width + Theme.S(7), r.Top,
                        r.Right - textLeft - noSize.Width - Theme.S(10), r.Height),
                    selected ? Theme.Fg : Theme.Dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                        | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            }
            if (idx < hints.Length && !string.IsNullOrEmpty(hints[idx]))
            {
                int stripRight = labels.Length * (TabW + TabGap);
                var hintRect = new Rectangle(stripRight + Theme.S(8), 0,
                    Width - stripRight - Theme.S(10), Height);
                if (hintRect.Width > Theme.S(60))
                    TextRenderer.DrawText(g, hints[idx], Theme.UI(7.8f, false), hintRect, Theme.Faint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right
                            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
