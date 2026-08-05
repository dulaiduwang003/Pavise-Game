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
        private Motion ind;
        private Image logo;
        private PerformancePreset mode = PerformancePreset.Standard;
        private bool modeEnabled = true;
        public Action<int> SelectionChanged;

        public int Selected { get { return sel; } }

        internal int ItemCount { get { return labels.Length; } }

        internal int ItemAtSlot(int slot) { return slot >= 0 && slot < order.Length ? order[slot] : -1; }

        private int TopPad { get { return Dpi.S(94); } }
        private int ItemH { get { return Dpi.S(36); } }
        private int Gap { get { return Dpi.S(5); } }
        private int Pad { get { return Dpi.S(12); } }
        private int Pitch { get { return ItemH + Gap; } }
        private int GroupH { get { return Dpi.S(30); } }
        private int BottomPad { get { return Dpi.S(18); } }

        public NavRail(string[] names, string[] icons)
            : this(names, icons, null, null, null, 0)
        {
        }

        public NavRail(string[] names, string[] icons, int[] displayOrder,
            int[] groupBeforeSlots, string[] groupTitles, int bottomAnchored)
        {
            labels = names; glyphs = icons;
            if (displayOrder != null && displayOrder.Length == names.Length) order = displayOrder;
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
            ind.Speed = 0.30f; ind.Set(SlotY(SlotOfItem(0)));
            logo = IconArt.Render(Dpi.S(34), mode, modeEnabled);
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
            return item;
        }

        private int SlotY(int slot)
        {
            int flowCount = order.Length - anchorCount;
            if (slot >= flowCount && Height > 0)
                return Height - BottomPad - ItemH - (order.Length - 1 - slot) * Pitch;
            return TopPad + slot * Pitch + GroupsAbove(slot) * GroupH;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            if (logo != null) { try { logo.Dispose(); } catch { } logo = null; }
            base.OnHandleDestroyed(e);
        }

        public void Select(int i)
        {
            if (i < 0 || i >= labels.Length) return;
            sel = i;
            ind.To(SlotY(SlotOfItem(i)));
            UiClock.Wake(); Invalidate();
            if (SelectionChanged != null) SelectionChanged(i);
        }

        public void SnapToSelection() { ind.Set(SlotY(SlotOfItem(sel))); Invalidate(); }

        public void SetMode(PerformancePreset value, bool enabled)
        {
            if (mode == value && modeEnabled == enabled) return;
            mode = value; modeEnabled = enabled;
            Image old = logo;
            logo = IconArt.Render(Dpi.S(34), mode, modeEnabled);
            if (old != null) old.Dispose();
            Invalidate();
        }

        protected override bool StepAll() { return ind.Step(); }

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
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); int i = HitTest(e.Y); if (i >= 0) Select(i); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var bg = new SolidBrush(Theme.Nav)) g.FillRectangle(bg, ClientRectangle);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var accentTop = new Pen(Theme.Accent, Math.Max(1f, Dpi.S(1)))) g.DrawLine(accentTop, 0, 0, Width, 0);
            using (var hatch = new Pen(Color.FromArgb(9, 210, 220, 235)))
                for (int x = -Height; x < Width; x += Dpi.S(22)) g.DrawLine(hatch, x, 0, x + Dpi.S(62), Dpi.S(62));

            if (logo != null) g.DrawImage(logo, Dpi.S(16), Dpi.S(15), Dpi.S(36), Dpi.S(36));
            TextRenderer.DrawText(g, App.DisplayName, Theme.UI(13f, true),
                new Rectangle(Dpi.S(56), Dpi.S(14), Width - Dpi.S(60), Dpi.S(24)), Theme.Fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, "CORE CONTROL  ·  " + App.VersionTag, Theme.Mono(6.5f),
                new Rectangle(Dpi.S(57), Dpi.S(38), Width - Dpi.S(60), Dpi.S(14)), Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

            using (var hp = new Pen(Theme.Stroke))
            {
                g.DrawLine(hp, Dpi.S(16), Dpi.S(64), Width - Dpi.S(16), Dpi.S(64));
                g.DrawLine(hp, Width - 1, 0, Width - 1, Height);
            }

            var pill = new Rectangle(Pad, (int)ind.Value, Width - Pad * 2, ItemH);
            using (var path = Theme.TechPath(pill, Dpi.S(8)))
            {
                using (var b = new SolidBrush(Col.Alpha(Theme.Accent, 22))) g.FillPath(b, path);
                using (var p = new Pen(Col.Alpha(Theme.Accent, 76))) g.DrawPath(p, path);
            }
            var bar = new Rectangle(Pad, (int)ind.Value + Dpi.S(11), Dpi.S(3), ItemH - Dpi.S(22));
            using (var bp = Theme.Rounded(bar, Dpi.S(1)))
            using (var bb = new SolidBrush(Theme.Accent)) g.FillPath(bb, bp);

            for (int gi = 0; gi < groupSlots.Length; gi++)
            {
                int slot = groupSlots[gi];
                if (slot < 0 || slot >= order.Length) continue;
                string text = groupTexts[gi] ?? "";
                int gy = SlotY(slot) - GroupH;
                int textW = TextRenderer.MeasureText(g, text, Theme.Mono(6.5f)).Width;
                int lineY = gy + GroupH / 2;
                TextRenderer.DrawText(g, text, Theme.Mono(6.5f),
                    new Rectangle(Pad + Dpi.S(13), gy, Width - Pad * 2 - Dpi.S(13), GroupH), Theme.Faint,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using (var gp = new Pen(Theme.Stroke))
                    g.DrawLine(gp, Pad + Dpi.S(17) + textW + Dpi.S(8), lineY, Width - Pad - Dpi.S(2), lineY);
            }

            for (int s = 0; s < order.Length; s++)
            {
                int i = order[s];
                int y = SlotY(s);
                bool on = (i == sel);
                if (!on && i == hoverIdx)
                {
                    var hr = new Rectangle(Pad, y, Width - Pad * 2, ItemH);
                    using (var b = new SolidBrush(Col.Alpha(Theme.Fg, 9)))
                    using (var path = Theme.TechPath(hr, Dpi.S(8))) g.FillPath(b, path);
                }
                Color c = on ? (Theme.LightMode ? Theme.Accent : Color.White)
                    : (i == hoverIdx ? Theme.Fg : Theme.Dim);
                var iconBox = new Rectangle(Pad + Dpi.S(13), y + (ItemH - Dpi.S(18)) / 2, Dpi.S(18), Dpi.S(18));
                Glyphs.Draw(g, glyphs[i], iconBox, on ? Theme.Accent : c);
                var tr = new Rectangle(iconBox.Right + Dpi.S(12), y, Width - iconBox.Right - Dpi.S(14), ItemH);
                TextRenderer.DrawText(g, labels[i], Theme.UI(10f, on), tr, c, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
        }
    }

}
