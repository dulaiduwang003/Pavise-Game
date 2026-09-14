// @author bdth 2074055628@qq.com
// File purpose Segmented picker control; segment count and text come from Labels
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class TierPicker : Control
    {
        private int idx = 2;
        private int hoverIdx = -1;
        private Motion[] glow = new Motion[12];
        public Action<int> IndexChanged;
        public string[] Labels;

        public TierPicker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
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
            bool moved = false;
            for (int i = 0; i < glow.Length; i++) if (glow[i].Step()) moved = true;
            if (moved) Invalidate();
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

        public int Index
        {
            get { return idx; }
            set { if (value >= 0 && value < Count && value != idx) { idx = value; Invalidate(); } }
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
            if (!Enabled || e.Button != MouseButtons.Left) return;
            int hit = HitIndex(e.Location);
            if (hit < 0 || hit == idx) return;
            idx = hit;
            Invalidate();
            if (IndexChanged != null) IndexChanged(idx);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            if (Backdrop.AppliesTo(this)) Backdrop.PaintOnCard(g, this, ClientRectangle);
            else using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int count = Count;
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

            if (idx < count)
            {
                Rectangle sr = SegmentRect(idx);
                sr.Width -= 1; sr.Height -= 1;
                using (GraphicsPath p = Theme.TechPath(sr, Theme.S(6)))
                {
                    using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.Accent, 0.18f))) g.FillPath(b, p);
                    using (var pen = new Pen(Col.Alpha(Theme.Accent, 215))) g.DrawPath(pen, p);
                }
            }

            for (int i = 0; i < count; i++)
            {
                Rectangle r = SegmentRect(i);
                r.Width -= 1; r.Height -= 1;
                bool selected = i == idx;
                string label = Labels != null && i < Labels.Length ? Labels[i] : "";
                TextRenderer.DrawText(g, label, Theme.UI(8.25f, selected), r,
                    !Enabled ? Theme.Faint : selected ? Theme.Fg : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            if (!Enabled)
                using (var veil = new SolidBrush(Col.Alpha(Theme.Card, Theme.LightMode ? 104 : 138)))
                    g.FillRectangle(veil, ClientRectangle);
        }
    }
}
