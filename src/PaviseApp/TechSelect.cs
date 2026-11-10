using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal class TechSelect : FxControl
    {
        private sealed class SelectAccessibleObject : FxAccessibleObject
        {
            private readonly TechSelect owner;

            public override string Value
            {
                get
                {
                    return owner.SelectedText;
                }
            }

            public override string DefaultAction
            {
                get
                {
                    return DsLabels.Pick("展开选项", "Open choices");
                }
            }

            public override AccessibleStates State
            {
                get
                {
                    return base.State | (owner.DropDownVisible ? AccessibleStates.Expanded : AccessibleStates.Collapsed);
                }
            }

            internal SelectAccessibleObject(TechSelect select)
                : base(select)
            {
                owner = select;
            }

            public override void DoDefaultAction()
            {
                owner.OpenDropDown();
            }
        }

        private string[] items = new string[0];

        private int selectedIndex = -1;

        private string badgeText = "";

        private readonly DropdownHost dropdown;

        private bool roundedStyle;

        internal bool RoundedStyle
        {
            get { return roundedStyle; }
            set
            {
                if (roundedStyle == value) return;
                roundedStyle = value;
                Invalidate();
            }
        }

        public int SelectedIndex
        {
            get
            {
                return selectedIndex;
            }
        }

        public int Count
        {
            get
            {
                return items.Length;
            }
        }

        public string SelectedText
        {
            get
            {
                if (selectedIndex < 0 || selectedIndex >= items.Length)
                {
                    return "";
                }
                return items[selectedIndex];
            }
        }

        public bool DropDownCreated
        {
            get
            {
                return dropdown.IsCreated;
            }
        }

        public bool DropDownVisible
        {
            get
            {
                return dropdown.IsVisible;
            }
        }

        public ContextMenuStrip DropDown
        {
            get
            {
                return dropdown.Menu;
            }
        }

        public Rectangle LastPlacement
        {
            get
            {
                return dropdown.LastPlacement;
            }
        }

        public virtual string CurrentBadge
        {
            get
            {
                return badgeText;
            }
            set
            {
                badgeText = value ?? "";
                UpdateSelection();
            }
        }

        protected virtual bool ConsumeEscapeWithoutMenu
        {
            get
            {
                return false;
            }
        }

        protected virtual int MaximumVisibleItems { get { return Int32.MaxValue; } }


        public Rectangle TextBounds
        {
            get
            {
                return new Rectangle(Theme.S(12), 0, Math.Max(1, base.Width - Theme.S(46)), Math.Max(1, base.Height));
            }
        }

        public event Action<int> SelectionChanged;

        public TechSelect()
        {
            base.Size = new Size(Theme.S(220), Theme.S(32));
            Font = DsType.Font(DsText.Body);
            ForeColor = Theme.Fg;
            base.TabStop = true;
            base.AccessibleRole = AccessibleRole.ComboBox;
            SetStyle(ControlStyles.Selectable, true);
            dropdown = new DropdownHost(this);
        }

        public void SetItems(string[] values)
        {
            if (base.IsDisposed)
            {
                return;
            }
            string[] valuesOffset = values ?? new string[0];
            bool sameItemCount = valuesOffset.Length == items.Length;
            int processedCount = 0;
            while (sameItemCount && processedCount < valuesOffset.Length)
            {
                sameItemCount = (valuesOffset[processedCount] ?? "") == items[processedCount];
                processedCount++;
            }
            if (!sameItemCount)
            {
                dropdown.Close();
                items = new string[valuesOffset.Length];
                for (int i = 0; i < items.Length; i++)
                {
                    items[i] = valuesOffset[i] ?? "";
                }
                selectedIndex = ((items.Length == 0) ? (-1) : Math.Max(0, Math.Min(selectedIndex, items.Length - 1)));
                UpdateSelection();
            }
        }

        public void SetSilently(int index)
        {
            if (!base.IsDisposed && index >= 0 && index < items.Length && selectedIndex != index)
            {
                selectedIndex = index;
                UpdateSelection();
            }
        }

        public void SelectIndex(int index)
        {
            if (!base.IsDisposed && base.CanInteract && index >= 0 && index < items.Length)
            {
                bool valueChanged = selectedIndex != index;
                selectedIndex = index;
                dropdown.Close();
                UpdateSelection();
                Action<int> selectionChanged = this.SelectionChanged;
                if (valueChanged && selectionChanged != null)
                {
                    selectionChanged(index);
                }
            }
        }

        protected virtual MenuBadge BadgeFor(int index)
        {
            string currentBadge = CurrentBadge;
            if (index != selectedIndex || currentBadge.Length <= 0)
            {
                return null;
            }
            return new MenuBadge(currentBadge, true);
        }

        private void UpdateSelection()
        {
            Text = SelectedText;
            ContextMenuStrip menu = dropdown.Menu;
            if (menu != null)
            {
                for (int i = 0; i < menu.Items.Count; i++)
                {
                    menu.Items[i].Tag = BadgeFor(i);
                }
            }
            if (base.IsHandleCreated)
            {
                AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            }
            Invalidate();
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip currentContextMenuStrip = new ContextMenuStrip();
            currentContextMenuStrip.Renderer = new TechMenuRenderer();
            currentContextMenuStrip.BackColor = Theme.Card;
            currentContextMenuStrip.ForeColor = Theme.Fg;
            currentContextMenuStrip.Font = DsType.Font(DsText.Body);
            currentContextMenuStrip.ShowImageMargin = false;
            currentContextMenuStrip.ShowCheckMargin = false;
            currentContextMenuStrip.AutoSize = false;
            currentContextMenuStrip.AutoClose = true;
            currentContextMenuStrip.Padding = new Padding(Theme.S(6));
            currentContextMenuStrip.DropShadowEnabled = false;
            int popupWidth = Math.Max(base.Width, Theme.S(170));
            for (int i = 0; i < items.Length; i++)
            {
                MenuBadge badge = BadgeFor(i);
                string badgeText = badge == null ? "" : badge.Text;
                int badgeWidth = string.IsNullOrEmpty(badgeText) ? 0 : TextRenderer.MeasureText(badgeText, DsType.MonoFor(DsMono.Badge, badgeText)).Width + Theme.S(34);
                popupWidth = Math.Max(popupWidth, TextRenderer.MeasureText(items[i], currentContextMenuStrip.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width + badgeWidth + Theme.S(30));
            }
            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            popupWidth = DsPopup.Fit(new Size(popupWidth, 1), workingArea, Theme.S(20), Theme.S(170)).Width;
            if (items.Length > MaximumVisibleItems)
            {
                popupWidth = DsPopup.Fit(new Size(popupWidth + SystemInformation.VerticalScrollBarWidth, 1), workingArea, Theme.S(20), Theme.S(170)).Width;
                int height = Math.Min(Theme.S(34) * Math.Max(1, MaximumVisibleItems),
                    Math.Max(1, workingArea.Height - Theme.S(20) - currentContextMenuStrip.Padding.Vertical));
                var selectScrollList = new SelectScrollList(this, items, Math.Max(1, popupWidth - currentContextMenuStrip.Padding.Horizontal), height);
                var host = new ToolStripControlHost(selectScrollList) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = selectScrollList.Size };
                currentContextMenuStrip.Items.Add(host);
                currentContextMenuStrip.Size = new Size(popupWidth, height + currentContextMenuStrip.Padding.Vertical);
                currentContextMenuStrip.Opened += delegate { if (!selectScrollList.IsDisposed) { selectScrollList.SelectedIndex = selectedIndex; selectScrollList.Focus(); } };
                currentContextMenuStrip.PerformLayout();
                using (var path = Theme.TechPath(new Rectangle(0, 0, currentContextMenuStrip.Width, currentContextMenuStrip.Height), Theme.S(8)))
                    currentContextMenuStrip.Region = new Region(path);
                return currentContextMenuStrip;
            }
            int vertical = currentContextMenuStrip.Padding.Vertical;
            for (int j = 0; j < items.Length; j++)
            {
                int index = j;
                ToolStripMenuItem currentToolStripMenuItem = new ToolStripMenuItem(items[j]);
                currentToolStripMenuItem.AutoSize = false;
                currentToolStripMenuItem.Margin = Padding.Empty;
                currentToolStripMenuItem.Size = new Size(Math.Max(1, popupWidth - currentContextMenuStrip.Padding.Horizontal), Theme.S(34));
                currentToolStripMenuItem.ForeColor = Theme.Fg;
                currentToolStripMenuItem.Tag = BadgeFor(j);
                currentToolStripMenuItem.Click += delegate
                {
                    SelectIndex(index);
                };
                currentContextMenuStrip.Items.Add(currentToolStripMenuItem);
                vertical += currentToolStripMenuItem.Height;
            }
            int visibleRows = Math.Min(items.Length, Math.Max(1, MaximumVisibleItems));
            int boundedHeight = Math.Min(vertical, currentContextMenuStrip.Padding.Vertical + visibleRows * Theme.S(34));
            boundedHeight = Math.Min(boundedHeight, Math.Max(1, workingArea.Height - Theme.S(20)));
            currentContextMenuStrip.Size = new Size(popupWidth, Math.Max(1, boundedHeight));
            currentContextMenuStrip.PerformLayout();
            using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, currentContextMenuStrip.Width, currentContextMenuStrip.Height), Theme.S(8)))
            {
                currentContextMenuStrip.Region = new Region(path);
                return currentContextMenuStrip;
            }
        }

        protected ContextMenuStrip EnsureMenu()
        {
            ContextMenuStrip menu = dropdown.Menu;
            if (menu != null)
            {
                return menu;
            }
            dropdown.Attach(BuildMenu());
            return dropdown.Menu;
        }

        protected void OpenDropDown()
        {
            if (base.IsDisposed || !base.CanInteract || !base.Visible || items.Length == 0)
            {
                return;
            }
            if (dropdown.IsVisible)
            {
                dropdown.Close();
                return;
            }
            ContextMenuStrip contextMenuStrip = EnsureMenu();
            if (contextMenuStrip != null)
            {
                dropdown.Show(contextMenuStrip);
                if (dropdown.Menu == contextMenuStrip && selectedIndex >= 0 && selectedIndex < contextMenuStrip.Items.Count)
                {
                    contextMenuStrip.Items[selectedIndex].Select();
                }
                Invalidate();
            }
        }

        public void CloseDropDown()
        {
            dropdown.Close();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && base.CanInteract)
            {
                dropdown.NoteMouseDown();
                Focus();
            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left)
            {
                dropdown.NoteMouseUp();
            }
        }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (!dropdown.ConsumeCloseClick())
            {
                OpenDropDown();
            }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == Keys.Escape && ConsumeEscapeWithoutMenu)
            {
                dropdown.Close();
                return true;
            }
            if (dropdown.HandleCommandKey(keys, false, OpenDropDown))
            {
                return true;
            }
            return base.ProcessCmdKey(ref message, keys);
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new SelectAccessibleObject(this);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics graphics = e.Graphics;
            FillBg(graphics);
            if (base.Width < 2 || base.Height < 2)
            {
                return;
            }
            if (RoundedStyle)
            {
                PaintRounded(graphics);
                return;
            }
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rectangle = new Rectangle(0, 0, Math.Max(1, base.Width - 1), Math.Max(1, base.Height - 1));
            bool highlighted = base.Enabled && (Focused || DropDownVisible);
            float hoverIfEnabled = base.State.HoverIfEnabled;
            using (GraphicsPath graphicsPath = Theme.TechPath(rectangle, Theme.S(7)))
            {
                using (LinearGradientBrush brush = new LinearGradientBrush(DsPaint.GradientRect(rectangle), Col.Lerp(Theme.CardHover, Color.White, hoverIfEnabled * 0.025f), Theme.Card, LinearGradientMode.Vertical))
                {
                    graphics.FillPath(brush, graphicsPath);
                }
                using (Pen pen = new Pen(highlighted ? Col.Alpha(Theme.Accent, 180) : Col.Lerp(Theme.Stroke, Theme.StrokeHi, hoverIfEnabled)))
                {
                    graphics.DrawPath(pen, graphicsPath);
                }
                PaintBusy(graphics, rectangle, graphicsPath);
            }
            TextRenderer.DrawText(graphics, SelectedText, DsType.Font(DsText.Body), TextBounds, base.Enabled ? Theme.Fg : Theme.Faint, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            DsPaint.DropArrow(graphics, base.Width - Theme.S(19), base.Height / 2, Theme.S(4), DropDownVisible, (!base.Enabled) ? Theme.Faint : (highlighted ? Theme.Accent : Theme.Dim));
        }

        private void PaintRounded(Graphics graphics)
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Theme.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(7)))
            {
                using (var brush = new SolidBrush(Col.Lerp(Theme.Inset, Theme.CardHover, DropDownVisible ? 1f : State.HoverIfEnabled)))
                    graphics.FillPath(brush, path);
                using (var pen = new Pen((Enabled && (DropDownVisible || Focused)) ? Theme.Accent : Col.Lerp(Theme.Stroke, Theme.StrokeHi, State.HoverIfEnabled)))
                    graphics.DrawPath(pen, path);
                PaintBusy(graphics, ClientRectangle, path);
            }
            TextRenderer.DrawText(graphics, SelectedText, Theme.UI(9.5f, false),
                new Rectangle(Theme.S(11), 0, Math.Max(1, Width - Theme.S(40)), Height), Enabled ? Theme.Fg : Theme.Dim,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            int x = Width - Theme.S(21), y = Height / 2, delta = Theme.S(4), direction = DropDownVisible ? -1 : 1;
            using (var pen = new Pen(Enabled ? Theme.Accent : Theme.Dim, Theme.S(2)))
                graphics.DrawLines(pen, new[] {
                    new Point(x - delta, y - direction * delta / 2), new Point(x, y + direction * delta / 2),
                    new Point(x + delta, y - direction * delta / 2) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                dropdown.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
