// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 切角页签：Index 赋值立即生效并触发回调，指示条按帧滑动；键盘 Left/Right/Home/End 切换。
    internal sealed class TechTabs : FxControl
    {
        private string[] labels = new string[0];
        private string[] hints = new string[0];
        private bool[] hot = new bool[0];
        private int idx;
        private int hoverIdx = -1;
        private Motion selection;
        private Motion[] glow = new Motion[0];
        public Action<int> IndexChanged;

        public TechTabs()
        {
            BackColor = Theme.Bg;
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTabList;
            selection.Speed = 0.40f;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SnapToSelection();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            selection.Set(idx);
            base.OnHandleDestroyed(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SnapToSelection();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            SnapToSelection();
        }

        private bool CanAnimateSelection
        {
            get { return IsHandleCreated && Visible && !UiClock.Frozen && !UiClock.Suspended; }
        }

        internal void SnapToSelection()
        {
            selection.Set(idx);
            Invalidate();
        }

        protected override void OnFrame(object s, EventArgs e)
        {
            bool moved = StepBusy();
            if (selection.Value != selection.Target)
            {
                if (!CanAnimateSelection) { selection.Set(idx); moved = true; }
                else
                {
                    if (selection.Step()) moved = true;
                    if (Math.Abs(selection.Value - selection.Target) * (TabW + TabGap) < 0.75f)
                        selection.Set(selection.Target);
                }
            }
            for (int i = 0; i < glow.Length; i++) if (glow[i].Step()) moved = true;
            if (moved) Invalidate();
        }

        public void SetTabs(string[] tabLabels, string[] tabHints)
        {
            labels = tabLabels ?? new string[0];
            hints = tabHints ?? new string[0];
            hot = new bool[labels.Length];
            if (idx >= labels.Length) idx = 0;
            glow = new Motion[labels.Length];
            for (int i = 0; i < glow.Length; i++) glow[i].Speed = 0.26f;
            SnapToSelection();
        }

        public void SetHot(bool[] value)
        {
            bool changed = false;
            for (int i = 0; i < hot.Length; i++)
            {
                bool next = value != null && i < value.Length && value[i];
                if (hot[i] != next) { hot[i] = next; changed = true; }
            }
            if (changed) Invalidate();
        }

        public int Index
        {
            get { return idx; }
            set
            {
                if (value < 0 || value >= labels.Length || value == idx) return;
                idx = value;
                if (CanAnimateSelection) { selection.To(idx); UiClock.Wake(); }
                else selection.Set(idx);
                Invalidate();
                if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
                if (IndexChanged != null) IndexChanged(idx);
            }
        }

        public int Count { get { return labels.Length; } }

        public string SelectedLabel { get { return idx < labels.Length ? labels[idx] : ""; } }

        private int TabW { get { return Theme.S(DsMetrics.Tabs.TabWidth); } }
        private int TabGap { get { return Theme.S(DsMetrics.Tabs.Gap); } }
        internal int TabsWidth
        {
            get { return labels.Length == 0 ? 0 : labels.Length * TabW + (labels.Length - 1) * TabGap; }
        }

        private Rectangle TabRect(int i)
        {
            return new Rectangle(i * (TabW + TabGap), Theme.S(DsMetrics.Tabs.Top), TabW,
                Height - Theme.S(DsMetrics.Tabs.BottomPad));
        }

        private int HitIndex(Point p)
        {
            for (int i = 0; i < labels.Length; i++) if (TabRect(i).Contains(p)) return i;
            return -1;
        }

        // 用户激活：禁用或忙碌时拒绝；有效索引交给 Index 统一处理。
        private void Activate(int hit)
        {
            if (!CanInteract || hit < 0) return;
            Index = hit;
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
            int next = DsKeys.NavigateHorizontal(e.KeyCode, idx, labels.Length);
            if (next < 0) return;
            e.Handled = true; e.SuppressKeyPress = true;
            Activate(next);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TabsAccessibleObject(this);
        }

        private sealed class TabsAccessibleObject : FxAccessibleObject
        {
            private readonly TechTabs owner;
            internal TabsAccessibleObject(TechTabs tabs) : base(tabs) { owner = tabs; }
            public override string Value { get { return owner.SelectedLabel; } }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!CanAnimateSelection) selection.Set(idx);
            Graphics g = e.Graphics;
            DsPaint.FillBackground(g, this, BackColor);
            if (Width < 2 || Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int cut = Theme.S(DsMetrics.Tabs.Cut);
            for (int i = 0; i < labels.Length; i++)
            {
                Rectangle r = TabRect(i);
                r.Width -= 1; r.Height -= 1;
                float h = GlowAt(i);
                DsPaint.TechFrame(g, r, cut, Backdrop.CardFill(this, Col.Lerp(Theme.Card, Theme.CardHover, h)),
                    Col.Lerp(Theme.Stroke, Theme.StrokeHi, h));
                using (var corner = new Pen(Col.Alpha(Theme.Accent, (int)(70 + 60 * h)), DsSpace.Line(1)))
                    g.DrawLine(corner, r.Right - cut, r.Top, r.Right, r.Top + cut);
            }

            Rectangle sr = Rectangle.Empty;
            if (labels.Length > 0)
            {
                sr = TabRect(idx);
                sr.X = (int)Math.Round(selection.Value * (TabW + TabGap));
                sr.Width -= 1; sr.Height -= 1;
                DsPaint.TechFrame(g, sr, cut, Backdrop.CardFill(this, Col.Lerp(Theme.Card, Theme.Accent, 0.16f)),
                    Col.Alpha(Theme.Accent, 220));
                using (var mark = new Pen(Theme.Accent, DsSpace.Line(2)))
                    g.DrawLine(mark, sr.Left, sr.Top + cut, sr.Left, sr.Bottom - cut);
                using (var corner = new Pen(Col.Alpha(Theme.Accent, 200), DsSpace.Line(1)))
                    g.DrawLine(corner, sr.Right - cut, sr.Top, sr.Right, sr.Top + cut);
            }

            for (int i = 0; i < labels.Length; i++)
            {
                Rectangle r = TabRect(i);
                r.Width -= 1; r.Height -= 1;
                bool selected = i == idx;
                string no = (i + 1).ToString("00");
                Font noFont = DsType.Mono(DsMono.TabNumber);
                Size noSize = TextRenderer.MeasureText(g, no, noFont, Size.Empty, TextFormatFlags.NoPadding);
                int textLeft = r.Left + Theme.S(DsMetrics.Tabs.TextLeft);
                TextRenderer.DrawText(g, no, noFont,
                    new Rectangle(textLeft, r.Top, noSize.Width, r.Height),
                    selected ? Theme.Accent : Theme.Faint,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, labels[i], DsType.Font(DsText.Label, selected),
                    new Rectangle(textLeft + noSize.Width + Theme.S(DsMetrics.Tabs.NumberGap), r.Top,
                        r.Right - textLeft - noSize.Width - Theme.S(DsMetrics.Tabs.RightPad), r.Height),
                    selected ? Theme.Fg : Theme.Dim,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine
                        | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                if (i < hot.Length && hot[i])
                {
                    var dot = new Rectangle(r.Left + Theme.S(DsMetrics.Tabs.HotDotX), r.Top + Theme.S(DsMetrics.Tabs.HotDotY),
                        Theme.S(DsMetrics.Tabs.HotDot), Theme.S(DsMetrics.Tabs.HotDot));
                    using (var hb = new SolidBrush(Theme.Accent)) g.FillEllipse(hb, dot);
                }
            }
            if (idx < hints.Length && !string.IsNullOrEmpty(hints[idx]))
            {
                int stripRight = labels.Length * (TabW + TabGap);
                var hintRect = new Rectangle(stripRight + Theme.S(DsMetrics.Tabs.HintGap), 0,
                    Width - stripRight - Theme.S(DsMetrics.Tabs.HintRightPad), Height);
                if (hintRect.Width > Theme.S(DsMetrics.Tabs.HintMinWidth))
                    TextRenderer.DrawText(g, hints[idx], DsType.Font(DsText.Hint), hintRect, Theme.Faint,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Right
                            | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            PaintBusy(g, new Rectangle(0, 0, Math.Max(1, TabsWidth), Height), null);
            if (WantsFocusCue && !sr.IsEmpty)
                DsFocus.Draw(g, DsFocus.Inset(TabRect(idx), DsMetrics.Tabs.FocusInset), Theme.Fg, Theme.Card);
        }
    }
}
