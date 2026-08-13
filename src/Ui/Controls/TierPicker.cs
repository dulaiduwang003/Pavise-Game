// @author bdth 2074055628@qq.com
// 文件用途 提供分段选择控件 默认三段压制档位 可用 Labels 泛化为任意段数

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class TierPicker : Control
    {
        private static readonly SuppressionLevel[] Order =
        {
            SuppressionLevel.Eco, SuppressionLevel.Restrained, SuppressionLevel.Isolated
        };
        private int idx = 2;
        private int hoverIdx = -1;
        private Motion slide;
        private Motion[] glow = new Motion[12];
        private bool slideReady;
        public Action<SuppressionLevel> Changed;
        public Action<int> IndexChanged;
        public string[] Labels;

        public TierPicker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
            slide.Speed = 0.30f;
            for (int i = 0; i < glow.Length; i++) glow[i].Speed = 0.26f;
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

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0) return;
            slide.Set(SegmentRect(idx).X);
            slideReady = true;
        }

        private void MoveSlide()
        {
            if (Width <= 0) { slideReady = false; return; }
            if (slideReady) slide.To(SegmentRect(idx).X);
            else { slide.Set(SegmentRect(idx).X); slideReady = true; }
            UiClock.Wake();
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

        private int Count
        {
            get { return Labels != null && Labels.Length >= 2 ? Labels.Length : 3; }
        }

        public SuppressionLevel Value
        {
            get { return Order[Math.Min(idx, Order.Length - 1)]; }
            set
            {
                int i = Array.IndexOf(Order, value);
                if (i >= 0 && i != idx) { idx = i; MoveSlide(); Invalidate(); }
            }
        }

        public int Index
        {
            get { return idx; }
            set { if (value >= 0 && value < Count && value != idx) { idx = value; MoveSlide(); Invalidate(); } }
        }

        private Rectangle SegmentRect(int index)
        {
            int count = Count;
            int gap = Theme.S(4);
            int w = (Width - gap * (count - 1)) / count;
            int x = index * (w + gap);
            if (index == count - 1) w = Width - x;
            return new Rectangle(x, 0, w, Height);
        }

        private int HitIndex(Point p)
        {
            for (int i = 0; i < Count; i++) if (SegmentRect(i).Contains(p)) return i;
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

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int hit = HitIndex(e.Location);
            if (hit < 0 || hit == idx) return;
            idx = hit;
            MoveSlide();
            Invalidate();
            if (Changed != null && idx < Order.Length) Changed(Order[idx]);
            if (IndexChanged != null) IndexChanged(idx);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int count = Count;
            if (!slideReady && Width > 0) { slide.Set(SegmentRect(idx).X); slideReady = true; }
            for (int i = 0; i < count; i++)
            {
                Rectangle r = SegmentRect(i);
                r.Width -= 1; r.Height -= 1;
                float h = GlowAt(i);
                using (GraphicsPath p = Theme.TechPath(r, Theme.S(6)))
                {
                    using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, h))) g.FillPath(b, p);
                    using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, h))) g.DrawPath(pen, p);
                }
            }

            Rectangle sr = SegmentRect(idx);
            sr.X = (int)slide.Value;
            sr.Width -= 1; sr.Height -= 1;
            using (GraphicsPath p = Theme.TechPath(sr, Theme.S(6)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.Accent, 0.18f))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Alpha(Theme.Accent, 215))) g.DrawPath(pen, p);
            }

            for (int i = 0; i < count; i++)
            {
                Rectangle r = SegmentRect(i);
                r.Width -= 1; r.Height -= 1;
                bool selected = i == idx;
                string label = Labels != null && i < Labels.Length ? Labels[i]
                    : LabelFor(Order[Math.Min(i, Order.Length - 1)]);
                TextRenderer.DrawText(g, label, Theme.UI(8.25f, selected), r,
                    selected ? Theme.Fg : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private static string LabelFor(SuppressionLevel level)
        {
            if (level == SuppressionLevel.Eco) return Lang.T("tame.lvl.eco");
            if (level == SuppressionLevel.Restrained) return Lang.T("tame.lvl.res");
            return Lang.T("tame.lvl.iso");
        }
    }
}
