// @author bdth 2074055628@qq.com
// 文件用途 游戏配置页运行模式覆盖条 跟随全局与四档模式彩色分段 选中块滑动变色

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class ModeStrip : Control
    {
        private static readonly PerformancePreset[] Order =
        {
            PerformancePreset.Standard, PerformancePreset.Competitive,
            PerformancePreset.Custom
        };

        private int idx;
        private int hoverIdx = -1;
        private Motion slideX;
        private Motion slideW;
        private Motion colorT;
        private readonly Motion[] glow = new Motion[4];
        private bool slideReady;
        private Color fromColor;
        private PerformancePreset global = PerformancePreset.Standard;
        public Action<int> IndexChanged;

        public ModeStrip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Cursor = Cursors.Hand;
            slideX.Speed = 0.30f; slideW.Speed = 0.30f; colorT.Speed = 0.24f;
            for (int i = 0; i < glow.Length; i++) glow[i].Speed = 0.26f;
            fromColor = Theme.Accent;
            colorT.Set(1f);
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
            bool moved = slideX.Step();
            if (moved && Math.Abs(slideX.Value - slideX.Target) < 0.75f) slideX.Set(slideX.Target);
            if (slideW.Step())
            {
                moved = true;
                if (Math.Abs(slideW.Value - slideW.Target) < 0.75f) slideW.Set(slideW.Target);
            }
            if (colorT.Step()) moved = true;
            for (int i = 0; i < glow.Length; i++) if (glow[i].Step()) moved = true;
            if (moved) Invalidate();
        }

        public int Index
        {
            get { return idx; }
            set { if (value >= 0 && value < 4 && value != idx) { idx = value; MoveSlide(); Invalidate(); } }
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

        // 跟随全局段吃掉三个模式段之外的全部余宽 所以比模式段宽 视觉上也强调默认态
        private Rectangle SegmentRect(int index)
        {
            int gap = Theme.S(6);
            int modeW = Theme.S(78);
            int followW = Width - (modeW + gap) * 3;
            if (index == 0) return new Rectangle(0, 0, followW, Height);
            int x = followW + gap + (index - 1) * (modeW + gap);
            int w = index == 3 ? Width - x : modeW;
            return new Rectangle(x, 0, w, Height);
        }

        private int HitIndex(Point p)
        {
            for (int i = 0; i < 4; i++) if (SegmentRect(i).Contains(p)) return i;
            return -1;
        }

        private void MoveSlide()
        {
            if (Width <= 0) { slideReady = false; return; }
            Rectangle r = SegmentRect(idx);
            if (slideReady) { slideX.To(r.X); slideW.To(r.Width); }
            else { slideX.Set(r.X); slideW.Set(r.Width); slideReady = true; }
            UiClock.Wake();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (Width <= 0) return;
            Rectangle r = SegmentRect(idx);
            slideX.Set(r.X); slideW.Set(r.Width);
            slideReady = true;
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
            fromColor = Col.Lerp(fromColor, SelColor(idx), colorT.Value);
            idx = hit;
            colorT.Set(0f); colorT.To(1f);
            MoveSlide();
            Invalidate();
            if (IndexChanged != null) IndexChanged(idx);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(BackColor)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (!slideReady && Width > 0)
            {
                Rectangle sr0 = SegmentRect(idx);
                slideX.Set(sr0.X); slideW.Set(sr0.Width);
                slideReady = true;
            }

            for (int i = 0; i < 4; i++)
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

            Color sel = Col.Lerp(fromColor, SelColor(idx), colorT.Value);
            var sr = new Rectangle((int)slideX.Value, 0, (int)slideW.Value - 1, Height - 1);
            using (GraphicsPath p = Theme.TechPath(sr, Theme.S(7)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, sel, 0.16f))) g.FillPath(b, p);
                using (var pen = new Pen(Col.Alpha(sel, 225))) g.DrawPath(pen, p);
            }

            for (int i = 0; i < 4; i++)
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
