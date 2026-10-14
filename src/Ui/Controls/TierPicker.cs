// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 分段选择器：鼠标抬起或 Left/Right/Home/End 激活；Index 赋值静默，不触发回调。
    internal sealed class TierPicker : FxControl
    {
        private int idx = 2;
        private int hoverIdx = -1;
        private Motion[] glow = new Motion[12];
        public Action<int> IndexChanged;
        public string[] Labels;

        public TierPicker()
        {
            BackColor = Theme.Card;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTabList;
            for (int i = 0; i < glow.Length; i++) glow[i].Speed = 0.26f;
        }

        protected override void OnFrame(object s, EventArgs e)
        {
            bool moved = StepBusy();
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

        public string SelectedLabel
        {
            get { return Labels != null && idx < Labels.Length ? Labels[idx] : ""; }
        }

        private Rectangle SegmentRect(int index)
        {
            int count = Count;
            int gap = Theme.S(DsMetrics.Tier.Gap);
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

        // 用户激活统一入口：越界、重复、禁用或忙碌一律忽略。
        private void Activate(int hit)
        {
            if (!CanInteract || hit < 0 || hit >= Count || hit == idx) return;
            idx = hit;
            Invalidate();
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            if (IndexChanged != null) IndexChanged(idx);
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
            Activate(HitIndex(e.Location));
        }

        protected override bool IsInputKey(Keys keyData)
        {
            if (DsKeys.IsHorizontalNavigation(keyData & Keys.KeyCode)) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || e.Modifiers != Keys.None) return;
            int next = DsKeys.NavigateHorizontal(e.KeyCode, idx, Count);
            if (next < 0) return;
            e.Handled = true; e.SuppressKeyPress = true;
            Activate(next);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TierAccessibleObject(this);
        }

        private sealed class TierAccessibleObject : FxAccessibleObject
        {
            private readonly TierPicker owner;
            internal TierAccessibleObject(TierPicker picker) : base(picker) { owner = picker; }
            public override string Value { get { return owner.SelectedLabel; } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            DsPaint.FillBackground(g, this, BackColor);
            if (Width < 2 || Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int count = Count;
            int cut = Theme.S(DsMetrics.Tier.Cut);
            for (int i = 0; i < count; i++)
            {
                Rectangle r = SegmentRect(i);
                r.Width -= 1; r.Height -= 1;
                float h = GlowAt(i);
                DsPaint.TechFrame(g, r, cut, Col.Lerp(Theme.Card, Theme.CardHover, h),
                    Col.Lerp(Theme.Stroke, Theme.StrokeHi, h));
            }

            Rectangle selected = Rectangle.Empty;
            if (idx < count)
            {
                selected = SegmentRect(idx);
                selected.Width -= 1; selected.Height -= 1;
                DsPaint.TechFrame(g, selected, cut, Col.Lerp(Theme.Card, Theme.Accent, 0.18f),
                    Col.Alpha(Theme.Accent, 215));
            }

            for (int i = 0; i < count; i++)
            {
                Rectangle r = SegmentRect(i);
                r.Width -= 1; r.Height -= 1;
                bool isSelected = i == idx;
                string label = Labels != null && i < Labels.Length ? Labels[i] : "";
                TextRenderer.DrawText(g, label, DsType.Font(DsText.Label, isSelected), r,
                    !Enabled ? Theme.Faint : isSelected ? Theme.Fg : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            PaintBusy(g, ClientRectangle, null);
            if (!Enabled) DsPaint.Veil(g, ClientRectangle, Col.Alpha(Theme.Card, DsAlpha.VeilTier));
            if (WantsFocusCue && !selected.IsEmpty)
                DsFocus.Draw(g, DsFocus.Inset(selected, DsMetrics.Tier.FocusInset), Theme.Fg, Theme.Card);
        }
    }
}
