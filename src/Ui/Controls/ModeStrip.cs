// @author bdth 2074055628@qq.com
// File purpose Run-mode override strip on the game config page: follow-global plus four color-coded tier segments, selected state gives instant feedback
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ModeStrip : Control
    {
        // Tier order is fixed at construction; tiers this machine does not support are not in it; changes go through RebuildUi, which rebuilds this control
        //   Indices must share a source with the value array AddCfgModeRow passes to the callback; both sides take PresetValue.VisibleChoices
        private readonly PerformancePreset[] Order = PresetValue.VisibleOrder();

        private int idx;
        private int hoverIdx = -1;
        private readonly Motion[] glow;
        private PerformancePreset global = PerformancePreset.Standard;
        public Action<int> IndexChanged;

        public ModeStrip()
        {
            glow = new Motion[Order.Length + 1];
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

        // Upper bound follows the tier count: 0 is follow-global, 1..Order.Length are the tiers
        //   With a hard-coded 4 the Custom tier write-back got rejected and the selected block stayed at its previous position after a page refresh
        public int Index
        {
            get { return idx; }
            set { if (value >= 0 && value <= Order.Length && value != idx) { idx = value; Invalidate(); } }
        }

        public void SetGlobal(PerformancePreset value)
        {
            if (global == value) return;
            global = value;
            Invalidate();
        }

        private Color SelColor(int index)
        {
            return index == 0 ? Theme.Accent : Theme.ModeColor(Order[index - 1]);
        }

        // Segment width is split by tier count, never hard-code it again; adding a tier squeezed the follow-global segment out
        //   Leave the follow-global segment room for two lines of text, split the rest evenly, and floor each segment at a minimum
        private Rectangle SegmentRect(int index)
        {
            int gap = Theme.S(6);
            int n = Order.Length;
            int modeW = Math.Max(Theme.S(50), (Width - Theme.S(92)) / n - gap);
            int followW = Width - (modeW + gap) * n;
            if (index == 0) return new Rectangle(0, 0, followW, Height);
            int x = followW + gap + (index - 1) * (modeW + gap);
            int w = index == n ? Width - x : modeW;
            return new Rectangle(x, 0, w, Height);
        }

        private int HitIndex(Point p)
        {
            for (int i = 0; i <= Order.Length; i++) if (SegmentRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = HitIndex(e.Location);
            if (hit == hoverIdx) return;
            hoverIdx = hit;
            for (int i = 0; i < glow.Length; i++) glow[i].To(i == hoverIdx ? 1f : 0f);
            UiClock.Wake();
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIdx == -1) return;
            hoverIdx = -1;
            for (int i = 0; i < glow.Length; i++) glow[i].To(0f);
            UiClock.Wake();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
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

            for (int i = 0; i <= Order.Length; i++)
            {
                Rectangle r = SegmentRect(i);
                r.Width -= 1; r.Height -= 1;
                float h = glow[i].Value;
                using (GraphicsPath p = Theme.TechPath(r, Theme.S(7)))
                {
                    using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.CardHover, h))) g.FillPath(b, p);
                    using (var pen = new Pen(Col.Lerp(Theme.Stroke, Theme.StrokeHi, h))) g.DrawPath(pen, p);
                }
            }

            Color sel = SelColor(idx);
            Rectangle sr = SegmentRect(idx);
            sr.Width -= 1; sr.Height -= 1;
            using (GraphicsPath p = Theme.TechPath(sr, Theme.S(7)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, sel, 0.16f))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Alpha(sel, 225))) g.DrawPath(pen, p);
            }

            for (int i = 0; i <= Order.Length; i++)
            {
                Rectangle r = SegmentRect(i);
                r.Width -= 1; r.Height -= 1;
                bool on = i == idx;
                if (i == 0) DrawFollow(g, r, on);
                else DrawMode(g, r, Order[i - 1], on);
            }
        }

        private void DrawFollow(Graphics g, Rectangle r, bool on)
        {
            TextRenderer.DrawText(g, Lang.T("cfg.follow"), Theme.UI(8.25f, on),
                new Rectangle(r.X, r.Y + Theme.S(4), r.Width, Theme.S(16)),
                on ? Theme.Fg : Theme.Dim,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, Lang.F("cfg.state.follow", ModeButton.ModeName(global)),
                Theme.UI(7f, false),
                new Rectangle(r.X, r.Y + Theme.S(20), r.Width, Theme.S(14)),
                on ? Theme.Dim : Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }

        private void DrawMode(Graphics g, Rectangle r, PerformancePreset mode, bool on)
        {
            Color mc = Theme.ModeColor(mode);
            string label = ModeButton.ModeName(mode);
            Font f = Theme.UI(8.25f, on);
            Size ts = TextRenderer.MeasureText(g, label, f);
            int dot = Theme.S(7);
            int gap = Theme.S(6);
            int x = r.X + (r.Width - dot - gap - ts.Width) / 2;
            int cy = r.Y + r.Height / 2;
            using (var b = new SolidBrush(on ? mc : Col.Alpha(mc, 135)))
                g.FillEllipse(b, x, cy - dot / 2, dot, dot);
            TextRenderer.DrawText(g, label, f, new Point(x + dot + gap, cy - ts.Height / 2),
                on ? Theme.Fg : Theme.Dim);
        }
    }
}
