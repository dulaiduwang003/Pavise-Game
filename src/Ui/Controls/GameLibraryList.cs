// @author bdth 2074055628@qq.com
// 文件用途 游戏库卡片列表 独立后台策略 渲染观察标签及可访问开关
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class GameLibraryItem
    {
        public GameProfile Profile;
        public bool Running;
        public bool RendererObserved;
        public GameLibraryItem(GameProfile profile, bool running, bool observed)
        {
            Profile = profile; Running = running; RendererObserved = observed;
        }
        public override string ToString() { return Profile == null ? "" : Profile.Name; }
    }

    internal sealed class GameLibraryEventArgs : EventArgs
    {
        public readonly GameLibraryItem Item;
        public GameLibraryEventArgs(GameLibraryItem item) { Item = item; }
    }

    // 卡片拥有真正的 CheckBox 保留键盘与辅助功能语义 内容未变时不重建控件
    internal sealed class GameLibraryList : Panel
    {
        private readonly List<GameLibraryItem> items = new List<GameLibraryItem>();
        private readonly List<GameLibraryRow> rows = new List<GameLibraryRow>();
        private readonly ToolTip tips = new ToolTip();
        private int selectedIndex = -1;
        private bool arranging;
        public Func<string, Bitmap> IconProvider;
        public event EventHandler SelectedIndexChanged;
        public event EventHandler ItemActivated;
        public event EventHandler<GameLibraryEventArgs> FamilyToggleRequested;

        public GameLibraryList()
        {
            DoubleBuffered = true; AutoScroll = true; TabStop = false;
            BackColor = Theme.Card; AccessibleRole = AccessibleRole.List;
            AccessibleName = Lang.T("nav.library"); Native.Dark(this);
            tips.InitialDelay = 500; tips.ReshowDelay = 120; tips.AutoPopDelay = 18000;
            tips.ShowAlways = true;
        }
        public IList<GameLibraryItem> Items { get { return items.AsReadOnly(); } }
        public GameLibraryItem SelectedItem
        {
            get { return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null; }
        }
        public int SelectedIndex
        {
            get { return selectedIndex; }
            set { SelectIndex(value, false); }
        }

        public void SetItems(List<GameLibraryItem> fresh)
        {
            string keepId = SelectedItem == null ? null : SelectedItem.Profile.Id;
            int keepScroll = -AutoScrollPosition.Y;
            bool structural = fresh.Count != items.Count;
            if (!structural)
                for (int i = 0; i < fresh.Count; i++)
                    if (!string.Equals(fresh[i].Profile.Id, items[i].Profile.Id, StringComparison.OrdinalIgnoreCase))
                    { structural = true; break; }
            SuspendLayout();
            try
            {
                if (structural)
                {
                    foreach (GameLibraryRow oldRow in rows) oldRow.Dispose();
                    rows.Clear(); items.Clear(); selectedIndex = -1;
                    foreach (GameLibraryItem item in fresh)
                    {
                        var row = new GameLibraryRow(item, tips);
                        row.IconProvider = IconProvider; row.TabIndex = rows.Count;
                        row.SelectionRequested += delegate { SelectIndex(rows.IndexOf(row), false); };
                        row.Activated += delegate
                        {
                            SelectIndex(rows.IndexOf(row), false);
                            if (ItemActivated != null) ItemActivated(this, EventArgs.Empty);
                        };
                        row.FamilyRequested += delegate
                        {
                            SelectIndex(rows.IndexOf(row), false);
                            if (FamilyToggleRequested != null)
                                FamilyToggleRequested(this, new GameLibraryEventArgs(row.Item));
                        };
                        row.KeyDown += delegate(object sender, KeyEventArgs e) { HandleRowKey(row, e); };
                        row.FamilySwitch.KeyDown += delegate(object sender, KeyEventArgs e) { HandleRowKey(row, e); };
                        rows.Add(row); items.Add(item); Controls.Add(row);
                    }
                }
                else for (int i = 0; i < fresh.Count; i++)
                {
                    items[i] = fresh[i]; rows[i].SetItem(fresh[i]);
                }
            }
            finally { ResumeLayout(true); }
            int next = -1;
            if (keepId != null)
                for (int i = 0; i < items.Count; i++)
                    if (string.Equals(items[i].Profile.Id, keepId, StringComparison.OrdinalIgnoreCase))
                    { next = i; break; }
            SelectIndex(next, false);
            if (structural) AutoScrollPosition = new Point(0, keepScroll);
            RefreshRows();
        }

        public void RefreshRows()
        {
            foreach (GameLibraryRow row in rows) row.RefreshModel();
            Invalidate();
        }
        private void SelectIndex(int value, bool focus)
        {
            if (value < -1 || value >= items.Count) value = -1;
            bool changed = value != selectedIndex;
            selectedIndex = value;
            for (int i = 0; i < rows.Count; i++) rows[i].Selected = i == selectedIndex;
            if (focus && value >= 0) { ScrollControlIntoView(rows[value]); rows[value].Focus(); }
            if (changed && SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty);
        }
        private void HandleRowKey(GameLibraryRow row, KeyEventArgs e)
        {
            int next = rows.IndexOf(row);
            switch (e.KeyCode)
            {
                case Keys.Up: next--; break;
                case Keys.Down: next++; break;
                case Keys.Home: next = 0; break;
                case Keys.End: next = rows.Count - 1; break;
                case Keys.PageUp: next -= Math.Max(1, ClientSize.Height / Math.Max(1, row.Height)); break;
                case Keys.PageDown: next += Math.Max(1, ClientSize.Height / Math.Max(1, row.Height)); break;
                default: OnKeyDown(e); return;
            }
            e.Handled = true; e.SuppressKeyPress = true;
            SelectIndex(Math.Max(0, Math.Min(rows.Count - 1, next)), true);
        }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (arranging) return;
            arranging = true;
            try
            {
                int width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                int rowH = GameLibraryRowLayout.HeightForWidth(width), gap = Theme.S(10);
                int extent = rows.Count == 0 ? 0 : rows.Count * (rowH + gap) - gap + Padding.Vertical;
                if (AutoScrollMinSize.Height != extent) AutoScrollMinSize = new Size(0, extent);
                int y = Padding.Top + AutoScrollPosition.Y;
                foreach (GameLibraryRow row in rows)
                {
                    row.SetBounds(Padding.Left, y, width, rowH); y += rowH + gap;
                }
            }
            finally { arranging = false; }
        }
        protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); PerformLayout(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.AppliesTo(this)) Backdrop.PaintOnCard(e.Graphics, this, e.ClipRectangle);
            else using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.ClipRectangle);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) tips.Dispose(); base.Dispose(disposing);
        }
    }

    internal sealed class GameLibraryRow : Control
    {
        private readonly ToolTip tips;
        private bool selected, hovered;
        public GameLibraryItem Item { get; private set; }
        public Func<string, Bitmap> IconProvider;
        public readonly FamilySuppressionSwitch FamilySwitch;
        public event EventHandler SelectionRequested;
        public event EventHandler Activated;
        public event EventHandler FamilyRequested;

        public GameLibraryRow(GameLibraryItem item, ToolTip toolTip)
        {
            Item = item; tips = toolTip;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
            TabStop = true; Cursor = Cursors.Hand; BackColor = Theme.Card;
            AccessibleRole = AccessibleRole.ListItem;
            FamilySwitch = new FamilySuppressionSwitch();
            FamilySwitch.Click += delegate { if (FamilyRequested != null) FamilyRequested(this, EventArgs.Empty); };
            FamilySwitch.Enter += delegate { if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); };
            Controls.Add(FamilySwitch); RefreshModel();
        }
        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value; FamilySwitch.Surface = Surface; Invalidate(); FamilySwitch.Invalidate();
            }
        }
        private Color Surface
        {
            get { return selected ? Col.Lerp(Theme.Card, Theme.Accent, 0.075f) : hovered ? Theme.CardHover : Theme.Card; }
        }
        internal static int OrdinaryOverrideCount(GameProfile profile)
        {
            return profile == null ? 0 : profile.Overrides.Count
                - (profile.Overrides.ContainsKey(PolicyCatalog.KeySuppressFamily) ? 1 : 0);
        }
        private string MetadataText
        {
            get
            {
                int count = OrdinaryOverrideCount(Item.Profile);
                string text = count > 0 ? Lang.F("lib.config.tag", count) : "";
                if (Item.Profile.ForceTrigger) text = Lang.T("v15.library.forced.tag") + (text.Length > 0 ? " · " + text : "");
                return text;
            }
        }
        public void SetItem(GameLibraryItem item) { Item = item; RefreshModel(); }
        public void RefreshModel()
        {
            if (Item == null || Item.Profile == null) return;
            FamilySwitch.Checked = Item.Profile.SuppressFamilyBackground;
            FamilySwitch.Surface = Surface;
            string observed = Lang.T(Item.RendererObserved ? "lib.renderer.observed" : "lib.renderer.pending");
            string hint = Lang.T(Item.RendererObserved ? "lib.renderer.observed.tip" : "lib.renderer.pending.tip");
            string path = Item.Profile.ExecutablePath ?? Item.Profile.Root ?? "";
            AccessibleName = Item.Profile.Name;
            AccessibleDescription = observed + " · " + MetadataText + "\r\n" + path + "\r\n" + hint;
            FamilySwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("lib.family.suppress");
            FamilySwitch.AccessibleDescription = hint + "\r\n" + Lang.T("lib.family.tip");
            tips.SetToolTip(this, Item.Profile.Name + "\r\n" + path + "\r\n" + MetadataText + "\r\n\r\n" + hint);
            tips.SetToolTip(FamilySwitch, Lang.T("lib.family.tip") + "\r\n\r\n" + hint);
            Invalidate(); FamilySwitch.Invalidate();
        }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (FamilySwitch != null) FamilySwitch.Bounds = GameLibraryRowLayout.ForSize(ClientSize).Policy;
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End
                || key == Keys.PageUp || key == Keys.PageDown) return true;
            return base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true; e.SuppressKeyPress = true;
                if (Activated != null) Activated(this, EventArgs.Empty);
            }
            base.OnKeyDown(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { Focus(); if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); }
            base.OnMouseDown(e);
        }
        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e); if (Activated != null) Activated(this, EventArgs.Empty);
        }
        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e); if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); Invalidate();
        }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e); hovered = true; FamilySwitch.Surface = Surface; Invalidate(); FamilySwitch.Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e); hovered = false; FamilySwitch.Surface = Surface; Invalidate(); FamilySwitch.Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Item == null || Item.Profile == null || Width < 2 || Height < 2) return;
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            GameLibraryRowLayout r = GameLibraryRowLayout.ForSize(ClientSize);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(8)))
            {
                using (var fill = new LinearGradientBrush(frame, Surface, Col.Lerp(Surface, Theme.Inset, 0.16f), LinearGradientMode.Horizontal))
                    g.FillPath(fill, path);
                using (var border = new Pen(selected ? Col.Alpha(Theme.Accent, 164) : hovered ? Theme.StrokeHi : Theme.Stroke))
                    g.DrawPath(border, path);
            }
            using (var top = new Pen(Col.Alpha(Theme.Accent, selected ? 220 : 80), Math.Max(1, Theme.S(1))))
                g.DrawLine(top, Theme.S(1), Theme.S(1), Theme.S(selected ? 62 : 32), Theme.S(1));
            if (selected)
                using (var active = new SolidBrush(Theme.Accent)) g.FillRectangle(active, 0, Theme.S(16), Theme.S(3), Theme.S(26));
            using (GraphicsPath socket = Theme.TechPath(Rectangle.Inflate(r.Icon, Theme.S(4), Theme.S(4)), Theme.S(6)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.Card, Theme.Inset, 0.5f))) g.FillPath(b, socket);
                using (var p = new Pen(Theme.Stroke)) g.DrawPath(p, socket);
            }
            Bitmap bitmap = null;
            try { if (IconProvider != null) bitmap = IconProvider(Item.Profile.ExecutablePath); } catch { }
            if (bitmap == null) Glyphs.Draw(g, "game", Rectangle.Inflate(r.Icon, -Theme.S(6), -Theme.S(6)), Theme.Accent);
            else try { g.DrawImage(bitmap, r.Icon); } catch { }

            const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, Item.Profile.Name, Theme.UI(10.2f, true), r.Name, Theme.Fg, line);
            TextRenderer.DrawText(g, Item.Profile.ExecutablePath ?? Item.Profile.Root ?? "", Theme.UI(7.9f, false),
                r.Path, Theme.Dim, (line & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.PathEllipsis);
            TextRenderer.DrawText(g, Lang.T(Item.Running ? "v15.library.running" : "v15.library.ready"),
                Theme.UI(7.1f, true), r.Running, Item.Running ? Theme.Green : Theme.Faint,
                line | TextFormatFlags.HorizontalCenter);

            string status = Lang.T(Item.RendererObserved ? "lib.renderer.observed" : "lib.renderer.pending");
            Color badgeColor = Item.RendererObserved ? Theme.Green : Theme.Dim;
            int measured = TextRenderer.MeasureText(status, Theme.UI(7.7f, true), Size.Empty, TextFormatFlags.NoPadding).Width;
            int badgeW = Math.Min(r.Metadata.Width, measured + Theme.S(25));
            var badge = new Rectangle(r.Metadata.X, r.Metadata.Y, Math.Max(1, badgeW), r.Metadata.Height);
            using (GraphicsPath bp = Theme.TechPath(badge, Theme.S(4)))
            {
                using (var b = new SolidBrush(Col.Alpha(badgeColor, Theme.LightMode ? 13 : 20))) g.FillPath(b, bp);
                using (var p = new Pen(Col.Alpha(badgeColor, Item.RendererObserved ? 82 : 55))) g.DrawPath(p, bp);
            }
            int dot = Theme.S(4);
            using (var b = new SolidBrush(badgeColor)) g.FillEllipse(b, badge.Left + Theme.S(8), badge.Top + (badge.Height - dot) / 2, dot, dot);
            TextRenderer.DrawText(g, status, Theme.UI(7.7f, true),
                new Rectangle(badge.Left + Theme.S(17), badge.Top, Math.Max(1, badge.Width - Theme.S(22)), badge.Height), badgeColor, line);

            string metadata = MetadataText;
            int metaX = badge.Right + Theme.S(10);
            if (r.Metadata.Right > metaX)
                TextRenderer.DrawText(g, metadata, Theme.UI(7.4f, false),
                    new Rectangle(metaX, r.Metadata.Top, r.Metadata.Right - metaX, r.Metadata.Height), Theme.Dim, line);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(frame, -Theme.S(5), -Theme.S(5)), Theme.Accent, Surface);
        }
    }

    internal sealed class FamilySuppressionSwitch : CheckBox
    {
        public Color Surface = Theme.Card;
        public FamilySuppressionSwitch()
        {
            AutoCheck = false; AutoSize = false; TabStop = true;
            Text = Lang.T("lib.family.suppress"); Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.CheckButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            if (key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End
                || key == Keys.PageUp || key == Keys.PageDown) return true;
            return base.IsInputKey(keyData);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.Handled = true; e.SuppressKeyPress = true; OnClick(EventArgs.Empty); }
            base.OnKeyDown(e);
        }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); Invalidate(); }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Surface)) e.Graphics.FillRectangle(b, ClientRectangle);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Checked ? Col.Lerp(Surface, Theme.Accent, Theme.LightMode ? 0.08f : 0.095f) : Col.Lerp(Surface, Theme.Inset, 0.38f);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(6)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(Checked ? Col.Alpha(Theme.Accent, 118) : Theme.Stroke)) g.DrawPath(p, path);
            }
            int tx = Theme.S(12), tw = Math.Max(1, Width - Theme.S(77));
            const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            Font titleFont = Theme.UI(8.3f, true);
            bool wrap = TextRenderer.MeasureText(g, Text, titleFont, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width > tw;
            TextRenderer.DrawText(g, Text, titleFont,
                new Rectangle(tx, Theme.S(wrap ? 8 : 14), tw, Theme.S(wrap ? 31 : 21)), Enabled ? Theme.Fg : Theme.Faint,
                wrap ? TextFormatFlags.WordBreak | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix : line);
            TextRenderer.DrawText(g, Lang.T(Checked ? "lib.family.on" : "lib.family.off"), Theme.UI(7.6f, false),
                new Rectangle(tx, Theme.S(40), tw, Theme.S(18)), Checked ? Theme.Accent : Theme.Dim, line);
            var track = new Rectangle(Width - Theme.S(56), (Height - Theme.S(22)) / 2, Theme.S(42), Theme.S(22));
            using (GraphicsPath path = Theme.TechPath(track, Theme.S(5)))
            {
                using (var b = new SolidBrush(Checked ? Theme.Accent : Theme.TrackOff)) g.FillPath(b, path);
                using (var p = new Pen(Checked ? Theme.Accent : Theme.StrokeHi)) g.DrawPath(p, path);
            }
            int knob = Theme.S(14), pad = Theme.S(4);
            var thumb = new Rectangle(Checked ? track.Right - pad - knob : track.Left + pad, track.Top + pad, knob, knob);
            using (GraphicsPath path = Theme.TechPath(thumb, Theme.S(3)))
            using (var b = new SolidBrush(Checked ? Theme.OnAccent : Theme.Fg)) g.FillPath(b, path);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(frame, -Theme.S(4), -Theme.S(4)), Theme.Accent, fill);
        }
    }

    internal struct GameLibraryRowLayout
    {
        public Rectangle Icon, Running, Name, Path, Metadata, Policy;
        public static int HeightForWidth(int width) { return Theme.S(width < Theme.S(560) ? 184 : 116); }
        public static GameLibraryRowLayout ForSize(Size size)
        {
            var r = new GameLibraryRowLayout();
            bool stack = size.Width < Theme.S(560);
            int inset = Theme.S(14), textX = Theme.S(74);
            int policyW = Math.Max(1, Math.Min(Theme.S(218), size.Width - inset * 2));
            r.Policy = new Rectangle(Math.Max(inset, size.Width - inset - policyW), Theme.S(stack ? 106 : 19), policyW, Theme.S(72));
            int textRight = stack ? size.Width - inset : r.Policy.Left - Theme.S(14);
            int textW = Math.Max(1, textRight - textX);
            r.Icon = new Rectangle(Theme.S(16), Theme.S(19), Theme.S(42), Theme.S(42));
            r.Running = new Rectangle(Theme.S(8), Theme.S(76), Theme.S(60), Theme.S(18));
            r.Name = new Rectangle(textX, Theme.S(14), textW, Theme.S(25));
            r.Path = new Rectangle(textX, Theme.S(44), textW, Theme.S(20));
            r.Metadata = new Rectangle(textX, Theme.S(76), textW, Theme.S(22));
            return r;
        }
    }
}
