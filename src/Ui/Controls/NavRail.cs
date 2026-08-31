// @author bdth 2074055628@qq.com
// 文件用途 绘制并管理左侧导航栏
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class NavRail : FxControl
    {
        private readonly string[] labels;
        private readonly string[] glyphs;
        private readonly int[] order;
        private readonly int[] groupSlots;
        private readonly string[] groupTexts;
        private readonly int anchorCount;
        private int sel;
        private int hoverIdx = -1;
        private Motion indicator;
        private Image logo;
        private bool showBranding = true;
        private PerformancePreset mode = PerformancePreset.Standard;
        private bool modeEnabled = true;
        public Action<int> SelectionChanged;
        public Action<int> ItemInvoked;
        public string SectionTitle;

        public bool ShowBranding
        {
            get { return showBranding; }
            set
            {
                if (showBranding == value) return;
                showBranding = value;
                RefreshLogo();
            }
        }

        public int Selected { get { return sel; } }

        internal int ItemCount { get { return labels.Length; } }

        internal int ItemAtSlot(int slot) { return slot >= 0 && slot < order.Length ? order[slot] : -1; }

        private int TopPad { get { return Dpi.S(137); } }
        private int ItemH { get { return Dpi.S(47); } }
        private int Gap { get { return Dpi.S(13); } }
        private int Pad { get { return Dpi.S(14); } }
        private int Pitch { get { return ItemH + Gap; } }
        private int GroupH { get { return Dpi.S(42); } }
        private int BottomPad { get { return Dpi.S(24); } }

        public NavRail(string[] names, string[] icons)
            : this(names, icons, null, null, null, 0)
        {
        }

        public NavRail(string[] names, string[] icons, int[] displayOrder,
            int[] groupBeforeSlots, string[] groupTitles, int bottomAnchored)
        {
            labels = names; glyphs = icons;
            if (displayOrder != null && displayOrder.Length > 0 && displayOrder.Length <= names.Length) order = displayOrder;
            else
            {
                order = new int[names.Length];
                for (int i = 0; i < names.Length; i++) order[i] = i;
            }
            if (groupTitles != null && groupBeforeSlots != null && groupTitles.Length == groupBeforeSlots.Length)
            {
                groupSlots = groupBeforeSlots; groupTexts = groupTitles;
            }
            else { groupSlots = new int[0]; groupTexts = new string[0]; }
            anchorCount = bottomAnchored < 0 ? 0 : (bottomAnchored > order.Length ? order.Length : bottomAnchored);
            Cursor = Cursors.Default;
            indicator.Speed = 0.40f;
            indicator.Set(SlotY(SlotOfItem(sel)));
            logo = IconArt.Render(Dpi.S(46), mode, modeEnabled);
        }

        internal static int GroupsAbove(int slot, int[] groupBeforeSlots)
        {
            if (groupBeforeSlots == null) return 0;
            int n = 0;
            for (int i = 0; i < groupBeforeSlots.Length; i++) if (slot >= groupBeforeSlots[i]) n++;
            return n;
        }

        private int GroupsAbove(int slot) { return GroupsAbove(slot, groupSlots); }

        private int SlotOfItem(int item)
        {
            for (int s = 0; s < order.Length; s++) if (order[s] == item) return s;
            return -1;
        }

        private int SlotY(int slot)
        {
            if (slot < 0) return TopPad;
            int flowCount = order.Length - anchorCount;
            if (slot >= flowCount && Height > 0)
                return Height - BottomPad - ItemH - (order.Length - 1 - slot) * Pitch;
            return TopPad + slot * Pitch + GroupsAbove(slot) * GroupH;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SnapToSelection();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            SnapToSelection();
            if (logo != null) { try { logo.Dispose(); } catch { } logo = null; }
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

        private bool CanAnimateIndicator
        {
            get { return IsHandleCreated && Visible && !UiClock.Frozen && !UiClock.Suspended; }
        }

        public void Select(int i)
        {
            if (i < 0 || i >= labels.Length) return;
            SelectSilently(i);
            if (SelectionChanged != null) SelectionChanged(i);
        }

        internal void SelectSilently(int i)
        {
            if (i < 0 || i >= labels.Length) return;
            bool changed = sel != i;
            sel = i;
            int slot = SlotOfItem(i);
            int targetY = SlotY(slot);
            // 页面与文字立即激活 仅缓动选中框 重复同步同一项不打断正在运行的动画
            if (changed && slot >= 0 && CanAnimateIndicator)
            {
                indicator.To(targetY);
                UiClock.Wake();
            }
            else if (!CanAnimateIndicator || indicator.Target != targetY) indicator.Set(targetY);
            Invalidate();
        }

        internal void InvokeItem(int i)
        {
            if (SlotOfItem(i) < 0) return;
            if (ItemInvoked != null) ItemInvoked(i);
            else Select(i);
        }

        public void SnapToSelection()
        {
            // 构造期的尺寸通知可能早于导航顺序初始化
            if (order == null) return;
            indicator.Set(SlotY(SlotOfItem(sel)));
            Invalidate();
        }

        public void SetMode(PerformancePreset value, bool enabled)
        {
            if (mode == value && modeEnabled == enabled) return;
            mode = value; modeEnabled = enabled;
            RefreshLogo();
        }

        public void RefreshLogo()
        {
            Image old = logo;
            logo = showBranding ? IconArt.Render(Dpi.S(46), mode, modeEnabled) : null;
            if (old != null) old.Dispose();
            Invalidate();
        }

        protected override bool StepAll()
        {
            bool moved = base.StepAll();
            int targetY = SlotY(SlotOfItem(sel));
            if (!CanAnimateIndicator || indicator.Target != targetY)
            {
                bool changed = indicator.Value != targetY || indicator.Target != targetY;
                indicator.Set(targetY);
                return moved || changed;
            }
            if (indicator.Value == indicator.Target) return moved;
            moved |= indicator.Step();
            if (Math.Abs(indicator.Value - indicator.Target) < 0.75f) indicator.Set(indicator.Target);
            return moved;
        }

        private int HitTest(int y)
        {
            for (int s = 0; s < order.Length; s++)
            {
                int t = SlotY(s);
                if (y >= t && y < t + ItemH) return order[s];
            }
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = HitTest(e.Y);
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            if (i != hoverIdx) { hoverIdx = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hoverIdx != -1) { hoverIdx = -1; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (e.Button != MouseButtons.Left) return;
            int i = HitTest(e.Y);
            if (i >= 0) { Focus(); InvokeItem(i); }
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int slot = SlotOfItem(hoverIdx >= 0 ? hoverIdx : sel);
            if (slot < 0) slot = 0;
            if (e.KeyCode == Keys.Up) slot = Math.Max(0, slot - 1);
            else if (e.KeyCode == Keys.Down) slot = Math.Min(order.Length - 1, slot + 1);
            else if (e.KeyCode == Keys.Home) slot = 0;
            else if (e.KeyCode == Keys.End) slot = order.Length - 1;
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) InvokeItem(order[slot]);
            else return;
            hoverIdx = order[slot]; Invalidate();
            e.Handled = true; e.SuppressKeyPress = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (Backdrop.AppliesTo(this))
            {
                Backdrop.Paint(g, this, ClientRectangle);
                using (var bg = new SolidBrush(Backdrop.NavFill(this, Theme.Nav)))
                    g.FillRectangle(bg, ClientRectangle);
            }
            else if (Theme.LightMode)
            {
                using (var bg = new LinearGradientBrush(ClientRectangle, Theme.Nav,
                    Col.Lerp(Theme.Nav, Theme.Bg, 0.58f), LinearGradientMode.Horizontal))
                    g.FillRectangle(bg, ClientRectangle);
            }
            else using (var bg = new SolidBrush(Theme.Nav)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            DrawTechTexture(g);
            using (var accentTop = new Pen(Theme.Accent, Math.Max(1f, Dpi.S(1)))) g.DrawLine(accentTop, 0, 0, Width, 0);
            if (showBranding)
            {
                if (logo != null) g.DrawImage(logo, Dpi.S(20), Dpi.S(26), Dpi.S(46), Dpi.S(46));
                TextRenderer.DrawText(g, App.DisplayName, Theme.UI(16.5f, true),
                    new Rectangle(Dpi.S(78), Dpi.S(24), Width - Dpi.S(84), Dpi.S(30)), Theme.Fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, "CORE CONTROL " + App.Version, Theme.Mono(7.5f),
                    new Rectangle(Dpi.S(79), Dpi.S(57), Width - Dpi.S(84), Dpi.S(18)), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            if (!string.IsNullOrEmpty(SectionTitle))
                TextRenderer.DrawText(g, SectionTitle, Theme.UI(10f, true),
                    new Rectangle(Pad + Dpi.S(12), Dpi.S(103), Width - Pad * 2 - Dpi.S(24), Dpi.S(24)), Theme.Dim,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            using (var hp = new Pen(Theme.Stroke))
            {
                g.DrawLine(hp, Width - 1, 0, Width - 1, Height);
            }

            int selectedSlot = SlotOfItem(sel);
            int targetY = SlotY(selectedSlot);
            if (!CanAnimateIndicator || indicator.Target != targetY) indicator.Set(targetY);
            if (selectedSlot >= 0)
            {
                int selectedY = (int)Math.Round(indicator.Value);
                var pill = new Rectangle(Pad, selectedY, Width - Pad * 2, ItemH);
                using (var path = Theme.TechPath(pill, Dpi.S(8)))
                {
                    using (var b = new SolidBrush(Col.Alpha(Theme.Accent, Theme.LightMode ? 38 : 22))) g.FillPath(b, path);
                    using (var p = new Pen(Col.Alpha(Theme.Accent, Theme.LightMode ? 124 : 76))) g.DrawPath(p, path);
                }
                var bar = new Rectangle(Pad, selectedY + Dpi.S(12), Dpi.S(4), ItemH - Dpi.S(24));
                using (var bp = Theme.Rounded(bar, Dpi.S(1)))
                using (var bb = new SolidBrush(Theme.Accent)) g.FillPath(bb, bp);
            }

            for (int gi = 0; gi < groupSlots.Length; gi++)
            {
                int slot = groupSlots[gi];
                if (slot < 0 || slot >= order.Length) continue;
                string text = groupTexts[gi] ?? "";
                int gy = SlotY(slot) - GroupH;
                Font gf = Theme.MonoFor(text, 6.5f);
                int textW = TextRenderer.MeasureText(g, text, gf).Width;
                int lineY = gy + GroupH / 2;
                using (var gp = new Pen(Theme.Stroke))
                {
                    if (text.Length > 0)
                    {
                        TextRenderer.DrawText(g, text, gf,
                            new Rectangle(Pad + Dpi.S(13), gy, Width - Pad * 2 - Dpi.S(13), GroupH), Theme.Faint,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                        g.DrawLine(gp, Pad + Dpi.S(17) + textW + Dpi.S(8), lineY, Width - Pad - Dpi.S(2), lineY);
                    }
                    else g.DrawLine(gp, Pad + Dpi.S(6), lineY, Width - Pad - Dpi.S(6), lineY);
                }
            }

            for (int s = 0; s < order.Length; s++)
            {
                int i = order[s];
                int y = SlotY(s);
                bool on = (i == sel);
                if (!on && i == hoverIdx)
                {
                    var hr = new Rectangle(Pad, y, Width - Pad * 2, ItemH);
                    using (var b = new SolidBrush(Col.Alpha(Theme.Fg, Theme.LightMode ? 16 : 9)))
                    using (var path = Theme.TechPath(hr, Dpi.S(8))) g.FillPath(b, path);
                }
                Color c = on ? (Theme.LightMode ? Theme.Accent : Color.White)
                    : (i == hoverIdx ? Theme.Fg : Theme.Dim);
                var iconBox = new Rectangle(Pad + Dpi.S(20), y + (ItemH - Dpi.S(20)) / 2, Dpi.S(20), Dpi.S(20));
                Glyphs.Draw(g, glyphs[i], iconBox, on ? Theme.Accent : c);
                int textX = iconBox.Right + Dpi.S(16);
                var tr = new Rectangle(textX, y, Width - Pad - Dpi.S(10) - textX, ItemH);
                Font itemFont = Theme.UI(11f, on);
                for (float size = 11f; size > 8.5f && TextRenderer.MeasureText(g, labels[i], itemFont).Width > tr.Width; size -= 0.5f)
                    itemFont = Theme.UI(size - 0.5f, on);
                TextRenderer.DrawText(g, labels[i], itemFont, tr, c,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawTechTexture(Graphics g)
        {
            RogSurface.Draw(g, ClientRectangle, true);
        }

    }

}
