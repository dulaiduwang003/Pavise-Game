// 作者：bdth 2074055628@qq.com
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{

    internal sealed class MenuBadge
    {
        public readonly string Text;
        public readonly bool Strong;
        public MenuBadge(string text, bool strong) { Text = text; Strong = strong; }
    }

    // 菜单渲染器：切角面、模式色顶线、选中条与右侧徽章，徽章绘制与设置卡共用。
    internal sealed class TechMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = e.AffectedBounds;
            r.Width -= 1; r.Height -= 1;
            DsPaint.TechFrame(g, r, Theme.S(DsMetrics.Menu.Cut), Theme.Card, Theme.Stroke);
            using (var p = new Pen(Theme.Accent, Math.Max(1.6f, Theme.S(2))))
                g.DrawLine(p, r.Left + Theme.S(DsMetrics.Menu.AccentLineStart), r.Top,
                    r.Left + Theme.S(DsMetrics.Menu.AccentLineEnd), r.Top);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e) { }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = Theme.S(DsMetrics.Menu.SeparatorInset);
            using (var pen = new Pen(Theme.Stroke))
                e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            var r = new Rectangle(0, 0, e.Item.Width, e.Item.Height);
            using (var b = new SolidBrush(Col.Alpha(Theme.Accent, 40))) g.FillRectangle(b, r);
            using (var b = new SolidBrush(Theme.Accent))
                g.FillRectangle(b, new Rectangle(0, 0, Theme.S(DsMetrics.Menu.SelectionBar), r.Height));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            var badge = e.Item.Tag as MenuBadge;
            var r = new Rectangle(Theme.S(DsMetrics.Menu.TextLeft), 0,
                e.Item.Width - Theme.S(DsMetrics.Menu.TextRightPad), e.Item.Height);

            const TextFormatFlags Line = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis;

            if (badge == null || string.IsNullOrEmpty(badge.Text))
            {
                TextRenderer.DrawText(g, e.Text, e.TextFont, r, e.TextColor, Line);
                return;
            }

            Font badgeFont = DsType.MonoFor(DsMono.Badge, badge.Text);
            int bw = DsPaint.BadgeWidth(g, badge.Text, badgeFont);
            int bh = Theme.S(DsMetrics.Badge.Height);
            var pill = new Rectangle(r.Right - bw, (e.Item.Height - bh) / 2, bw, bh);
            r.Width -= bw + Theme.S(DsMetrics.Menu.BadgeGap);
            TextRenderer.DrawText(g, e.Text, e.TextFont, r, e.TextColor, Line);
            DsPaint.Badge(g, pill, badge.Text, badgeFont, badge.Strong);
        }
    }

    // 下拉选择器：菜单由 DropdownHost 托管并缓存到关闭为止；只有索引变化才回调一次；禁用或忙碌时不展开也不选择。
    internal class TechSelect : FxControl
    {
        private string[] items = new string[0];
        private int selectedIndex = -1;
        private string badgeText = "";
        private readonly DropdownHost dropdown;
        public event Action<int> SelectionChanged;

        public TechSelect()
        {
            Size = new Size(Theme.S(DsMetrics.Select.Width), Theme.S(DsMetrics.Select.Height));
            Font = DsType.Font(DsText.Body); ForeColor = Theme.Fg;
            TabStop = true; AccessibleRole = AccessibleRole.ComboBox;
            SetStyle(ControlStyles.Selectable, true);
            dropdown = new DropdownHost(this);
        }

        public int SelectedIndex { get { return selectedIndex; } }
        public int Count { get { return items.Length; } }
        public string SelectedText
        {
            get { return selectedIndex >= 0 && selectedIndex < items.Length ? items[selectedIndex] : ""; }
        }
        public bool DropDownCreated { get { return dropdown.IsCreated; } }
        public bool DropDownVisible { get { return dropdown.IsVisible; } }
        public ContextMenuStrip DropDown { get { return dropdown.Menu; } }
        public Rectangle LastPlacement { get { return dropdown.LastPlacement; } }

        // 当前项右侧徽章文本，空则不显示；子类可按语言实时提供。
        public virtual string CurrentBadge
        {
            get { return badgeText; }
            set { badgeText = value ?? ""; UpdateSelection(); }
        }

        // Escape 在没有菜单时是否也吞掉；默认放行给窗体。
        protected virtual bool ConsumeEscapeWithoutMenu { get { return false; } }

        public Rectangle TextBounds
        {
            get
            {
                return new Rectangle(Theme.S(DsMetrics.Select.TextLeft), 0,
                    Math.Max(1, Width - Theme.S(DsMetrics.Select.TextRightPad)), Math.Max(1, Height));
            }
        }

        public void SetItems(string[] values)
        {
            if (IsDisposed) return;
            string[] next = values ?? new string[0];
            bool same = next.Length == items.Length;
            for (int i = 0; same && i < next.Length; i++) same = (next[i] ?? "") == items[i];
            if (same) return;
            dropdown.Close();
            items = new string[next.Length];
            for (int i = 0; i < items.Length; i++) items[i] = next[i] ?? "";
            selectedIndex = items.Length == 0 ? -1 : Math.Max(0, Math.Min(selectedIndex, items.Length - 1));
            UpdateSelection();
        }

        public void SetSilently(int index)
        {
            if (IsDisposed || index < 0 || index >= items.Length || selectedIndex == index) return;
            selectedIndex = index;
            UpdateSelection();
        }

        public void SelectIndex(int index)
        {
            if (IsDisposed || !CanInteract || index < 0 || index >= items.Length) return;
            bool changed = selectedIndex != index;
            selectedIndex = index;
            dropdown.Close();
            UpdateSelection();
            Action<int> selected = SelectionChanged;
            if (changed && selected != null) selected(index);
        }

        private MenuBadge BadgeFor(int index)
        {
            string text = CurrentBadge;
            return index == selectedIndex && text.Length > 0 ? new MenuBadge(text, true) : null;
        }

        private void UpdateSelection()
        {
            Text = SelectedText;
            ContextMenuStrip menu = dropdown.Menu;
            if (menu != null)
                for (int i = 0; i < menu.Items.Count; i++) menu.Items[i].Tag = BadgeFor(i);
            if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            Invalidate();
        }

        private ContextMenuStrip BuildMenu()
        {
            var next = new ContextMenuStrip {
                Renderer = new TechMenuRenderer(), BackColor = Theme.Card, ForeColor = Theme.Fg,
                Font = DsType.Font(DsText.Body), ShowImageMargin = false, ShowCheckMargin = false,
                AutoSize = false, AutoClose = true, Padding = new Padding(Theme.S(DsMetrics.Menu.Padding)),
                DropShadowEnabled = false
            };
            int width = Math.Max(Width, Theme.S(DsMetrics.Menu.MinWidth));
            string badge = CurrentBadge;
            int badgeWidth = badge.Length == 0 ? 0
                : TextRenderer.MeasureText(badge, DsType.MonoFor(DsMono.Badge, badge)).Width + Theme.S(34);
            foreach (string value in items)
                width = Math.Max(width, TextRenderer.MeasureText(value, next.Font, Size.Empty,
                    TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width + badgeWidth + Theme.S(30));
            Rectangle work = Screen.FromControl(this).WorkingArea;
            width = DsPopup.Fit(new Size(width, 1), work, Theme.S(DsMetrics.Menu.ScreenMargin), Theme.S(DsMetrics.Menu.MinWidth)).Width;
            int height = next.Padding.Vertical;
            for (int i = 0; i < items.Length; i++)
            {
                int index = i;
                var item = new ToolStripMenuItem(items[i]) {
                    AutoSize = false, Margin = Padding.Empty,
                    Size = new Size(Math.Max(1, width - next.Padding.Horizontal), Theme.S(DsMetrics.Menu.ItemHeight)),
                    ForeColor = Theme.Fg, Tag = BadgeFor(i)
                };
                item.Click += delegate { SelectIndex(index); };
                next.Items.Add(item); height += item.Height;
            }
            next.Size = new Size(width, Math.Max(next.Padding.Vertical, height));
            next.PerformLayout();
            using (var frame = Theme.TechPath(new Rectangle(0, 0, next.Width, next.Height), Theme.S(DsMetrics.Menu.Cut)))
                next.Region = new Region(frame);
            return next;
        }

        // 已托管则复用，否则构建并托管；不定位不显示，禁用或忙碌时也可预建。
        protected ContextMenuStrip EnsureMenu()
        {
            ContextMenuStrip existing = dropdown.Menu;
            if (existing != null) return existing;
            dropdown.Attach(BuildMenu());
            return dropdown.Menu;
        }

        // 展开或收起：已可见则关闭；禁用、忙碌、隐藏或无项时不动作。
        protected void OpenDropDown()
        {
            if (IsDisposed || !CanInteract || !Visible || items.Length == 0) return;
            if (dropdown.IsVisible) { dropdown.Close(); return; }
            ContextMenuStrip menu = EnsureMenu();
            if (menu == null) return;
            dropdown.Show(menu);
#if !PAVISE_SELFTEST
            if (dropdown.Menu == menu && selectedIndex >= 0 && selectedIndex < menu.Items.Count)
                menu.Items[selectedIndex].Select();
#endif
            Invalidate();
        }

        public void CloseDropDown() { dropdown.Close(); }

#if PAVISE_SELFTEST
        internal void OpenDropDownForTest() { OpenDropDown(); }
#endif

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && CanInteract) { dropdown.NoteMouseDown(); Focus(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) dropdown.NoteMouseUp();
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (dropdown.ConsumeCloseClick()) return;
            OpenDropDown();
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape && ConsumeEscapeWithoutMenu) { dropdown.Close(); return true; }
            if (dropdown.HandleCommandKey(keys, false, OpenDropDown)) return true;
            return base.ProcessCmdKey(ref message, keys);
        }

        protected override AccessibleObject CreateAccessibilityInstance() { return new SelectAccessibleObject(this); }

        private sealed class SelectAccessibleObject : FxAccessibleObject
        {
            private readonly TechSelect owner;
            internal SelectAccessibleObject(TechSelect select) : base(select) { owner = select; }
            public override string Value { get { return owner.SelectedText; } }
            public override string DefaultAction { get { return DsLabels.Pick("展开选项", "Open choices"); } }
            public override AccessibleStates State
            {
                get { return base.State | (owner.DropDownVisible ? AccessibleStates.Expanded : AccessibleStates.Collapsed); }
            }
            public override void DoDefaultAction() { owner.OpenDropDown(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            FillBg(g);
            if (Width < 2 || Height < 2) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            bool active = Enabled && (Focused || DropDownVisible);
            float value = State.HoverIfEnabled;
            using (var path = Theme.TechPath(bounds, Theme.S(DsMetrics.Select.Cut)))
            {
                using (var fill = new LinearGradientBrush(DsPaint.GradientRect(bounds),
                    Col.Lerp(Theme.CardHover, Color.White, value * .025f), Theme.Card, LinearGradientMode.Vertical))
                    g.FillPath(fill, path);
                using (var edge = new Pen(active ? Col.Alpha(Theme.Accent, 180) : Col.Lerp(Theme.Stroke, Theme.StrokeHi, value)))
                    g.DrawPath(edge, path);
                PaintBusy(g, bounds, path);
            }
            TextRenderer.DrawText(g, SelectedText, DsType.Font(DsText.Body), TextBounds, Enabled ? Theme.Fg : Theme.Faint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding
                    | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            DsPaint.DropArrow(g, Width - Theme.S(DsMetrics.Select.ArrowRight), Height / 2, Theme.S(DsMetrics.Select.ArrowHalf),
                DropDownVisible, Enabled ? active ? Theme.Accent : Theme.Dim : Theme.Faint);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) dropdown.Dispose();
            base.Dispose(disposing);
        }
    }
}
