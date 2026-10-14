// 作者：bdth 2074055628@qq.com
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
        public readonly Control Anchor;
        public GameLibraryEventArgs(GameLibraryItem item, Control anchor = null) { Item = item; Anchor = anchor; }
    }

    internal sealed class GameLibraryList : Panel
    {
        private readonly List<GameLibraryItem> items = new List<GameLibraryItem>();
        private readonly List<GameLibraryRow> rows = new List<GameLibraryRow>();
        private readonly ToolTip tips = new ToolTip();
        private int selectedIndex = -1;
        private bool arranging;
        public Func<string, Bitmap> IconProvider;
        public event EventHandler SelectedIndexChanged;
        public event EventHandler<GameLibraryEventArgs> FamilyToggleRequested;
        public event EventHandler<GameLibraryEventArgs> CoreSchedulingRequested;
        public event EventHandler<GameLibraryEventArgs> CoreToggleRequested;
        public event EventHandler<GameLibraryEventArgs> RebarToggleRequested;
        public event EventHandler<GameLibraryEventArgs> FullscreenOptimizationsToggleRequested;
        public event EventHandler<GameLibraryEventArgs> MoreRequested;

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
                        row.FamilyRequested += delegate
                        {
                            SelectIndex(rows.IndexOf(row), false);
                            if (FamilyToggleRequested != null)
                                FamilyToggleRequested(this, new GameLibraryEventArgs(row.Item));
                        };
                        row.CoreRequested += delegate
                        {
                            SelectIndex(rows.IndexOf(row), false);
                            if (CoreSchedulingRequested != null)
                                CoreSchedulingRequested(this, new GameLibraryEventArgs(row.Item));
                        };
                        row.CoreToggleRequested += delegate
                        {
                            SelectIndex(rows.IndexOf(row), false);
                            if (CoreToggleRequested != null) CoreToggleRequested(this, new GameLibraryEventArgs(row.Item));
                        };
                        row.CoreSwitch.KeyDown += delegate(object sender, KeyEventArgs e) { HandleRowKey(row, e); };
                        row.RebarRequested += delegate
                        {
                            SelectIndex(rows.IndexOf(row), false);
                            if (RebarToggleRequested != null) RebarToggleRequested(this, new GameLibraryEventArgs(row.Item));
                        };
                        row.RebarSwitch.KeyDown += delegate(object sender, KeyEventArgs e) { HandleRowKey(row, e); };
                        row.FullscreenOptimizationsRequested += delegate
                        {
                            SelectIndex(rows.IndexOf(row), false);
                            if (FullscreenOptimizationsToggleRequested != null)
                                FullscreenOptimizationsToggleRequested(this, new GameLibraryEventArgs(row.Item));
                        };
                        row.FullscreenOptimizationsSwitch.KeyDown += delegate(object sender, KeyEventArgs e) { HandleRowKey(row, e); };
                        row.CoreButton.KeyDown += delegate(object sender, KeyEventArgs e) { HandleRowKey(row, e); };
                        row.MoreRequested += delegate {
                            if (MoreRequested != null) MoreRequested(this, new GameLibraryEventArgs(row.Item, row.MoreButton));
                        };
                        row.MoreButton.KeyDown += delegate(object sender, KeyEventArgs e) { HandleRowKey(row, e); };
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
            if (arranging) return;
            arranging = true;
            try
            {
                // 滚动条出现会改变视口，稳定布局后再缓存行宽。
                for (int pass = 0; pass < 3; pass++)
                {
                    base.OnLayout(levent);
                    int width = Math.Max(1, ClientSize.Width - Padding.Horizontal);
                    int rowH = GameLibraryRowLayout.HeightForWidth(width), gap = Theme.S(8);
                    int extent = rows.Count == 0 ? 0 : Padding.Vertical - gap;
                    foreach (GameLibraryRow row in rows) extent += rowH + row.ExtraHeightForWidth(width) + gap;
                    if (AutoScrollMinSize.Height != extent) AutoScrollMinSize = new Size(0, extent);
                    int y = Padding.Top + AutoScrollPosition.Y;
                    foreach (GameLibraryRow row in rows)
                    {
                        int h = rowH + row.ExtraHeightForWidth(width);
                        row.SetBounds(Padding.Left, y, width, h); y += h + gap;
                    }
                    base.OnLayout(levent);
                    if (width == ClientSize.Width - Padding.Horizontal && !HorizontalScroll.Visible) break;
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
        public readonly FamilySuppressionSwitch CoreSwitch;
        public readonly FamilySuppressionSwitch RebarSwitch;
        public readonly FamilySuppressionSwitch FullscreenOptimizationsSwitch;
        public readonly PillButton CoreButton;
        public readonly PillButton MoreButton;
        public readonly GameCardExtension Extension;
        public event EventHandler SelectionRequested;
        public event EventHandler FamilyRequested;
        public event EventHandler CoreRequested;
        public event EventHandler CoreToggleRequested;
        public event EventHandler RebarRequested;
        public event EventHandler FullscreenOptimizationsRequested;
        public event EventHandler MoreRequested;

        public GameLibraryRow(GameLibraryItem item, ToolTip toolTip)
        {
            Item = item; tips = toolTip;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                | ControlStyles.Selectable | ControlStyles.StandardDoubleClick, true);
            TabStop = true; Cursor = Cursors.Hand; BackColor = Theme.Card; Margin = Padding.Empty;
            AccessibleRole = AccessibleRole.ListItem;
            CoreSwitch = new FamilySuppressionSwitch(true);
            CoreSwitch.Name = "gameCoreSwitch"; CoreSwitch.TabIndex = 0;
            CoreSwitch.Click += delegate { if (CoreToggleRequested != null) CoreToggleRequested(this, EventArgs.Empty); };
            CoreSwitch.Enter += delegate { if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); };
            Controls.Add(CoreSwitch);
            CoreButton = new GameCardDetailsButton();
            CoreButton.Name = "gameCoreScheduling"; CoreButton.TabIndex = 0;
            CoreButton.Click += delegate { if (CoreRequested != null) CoreRequested(this, EventArgs.Empty); };
            CoreButton.Enter += delegate { if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); };
            CoreSwitch.Controls.Add(CoreButton);
            FamilySwitch = new FamilySuppressionSwitch();
            FamilySwitch.TabIndex = 1;
            FamilySwitch.Click += delegate { if (FamilyRequested != null) FamilyRequested(this, EventArgs.Empty); };
            FamilySwitch.Enter += delegate { if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); };
            Controls.Add(FamilySwitch);
            RebarSwitch = new FamilySuppressionSwitch();
            RebarSwitch.Name = "gameRebarSwitch"; RebarSwitch.TabIndex = 2;
            RebarSwitch.Text = Lang.T("lib.rebar");
            RebarSwitch.Click += delegate { if (RebarRequested != null) RebarRequested(this, EventArgs.Empty); };
            RebarSwitch.Enter += delegate { if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); };
            Controls.Add(RebarSwitch);
            FullscreenOptimizationsSwitch = new FamilySuppressionSwitch();
            FullscreenOptimizationsSwitch.Name = "gameFullscreenOptimizationsSwitch";
            FullscreenOptimizationsSwitch.TabIndex = 3;
            FullscreenOptimizationsSwitch.Text = Lang.T("lib.fso");
            FullscreenOptimizationsSwitch.Click += delegate { if (FullscreenOptimizationsRequested != null) FullscreenOptimizationsRequested(this, EventArgs.Empty); };
            FullscreenOptimizationsSwitch.Enter += delegate { if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); };
            Controls.Add(FullscreenOptimizationsSwitch);
            MoreButton = new GameCardMenuButton { Name = "gameMore", TabIndex = 4 };
            MoreButton.Click += delegate { if (MoreRequested != null) MoreRequested(this, EventArgs.Empty); };
            Controls.Add(MoreButton);
            Extension = GameExtension.CreateCard(item != null ? item.Profile : null);
            if (Extension != null)
            {
                Extension.SetSurface(Surface);
                Extension.Enter += delegate { if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); };
                Controls.Add(Extension);
            }
            RefreshModel();
        }
        public int ExtraHeight { get { return ExtraHeightForWidth(ClientSize.Width); } }
        internal int ExtraHeightForWidth(int width)
        { return Extension == null ? 0 : Extension.PreferredHeightForWidth(Math.Max(1, width - Theme.S(28))); }
        public bool Selected
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value; PushSurface(); Invalidate();
            }
        }
        private void PushSurface()
        {
            FamilySwitch.Surface = Surface; FamilySwitch.Invalidate();
            RebarSwitch.Surface = Surface; RebarSwitch.Invalidate();
            FullscreenOptimizationsSwitch.Surface = Surface; FullscreenOptimizationsSwitch.Invalidate();
            CoreSwitch.Surface = Surface; CoreSwitch.Invalidate();
            CoreButton.Bg = Surface; CoreButton.Invalidate();
            MoreButton.Bg = Surface; MoreButton.Invalidate();
            if (Extension != null) Extension.SetSurface(Surface);
        }
        private Color Surface
        {
            get { return selected ? Col.Lerp(Theme.Card, Theme.Accent, 0.025f) : hovered ? Theme.CardHover : Theme.Card; }
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
            CoreSwitch.Checked = PolicyResolver.For(Item.Profile).AutoCoresAllowed;
            CoreSwitch.Surface = Surface;
            CoreSwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("schedule.auto.allow");
            CoreSwitch.AccessibleDescription = Lang.T("lib.cores.toggle.tip");
            tips.SetToolTip(CoreSwitch, Lang.T("lib.cores.toggle.tip"));
            FamilySwitch.Checked = Item.Profile.SuppressFamilyBackground;
            RebarSwitch.Checked = PolicyResolver.For(Item.Profile).NvRebar;
            RebarSwitch.Surface = Surface;
            RebarSwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("lib.rebar");
            RebarSwitch.AccessibleDescription = Lang.T("lib.rebar.tip");
            tips.SetToolTip(RebarSwitch, Lang.T("lib.rebar.tip"));
            FullscreenOptimizationsSwitch.Checked = Item.Profile.DisableFullscreenOptimizations;
            FullscreenOptimizationsSwitch.Surface = Surface;
            FullscreenOptimizationsSwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("lib.fso");
            FullscreenOptimizationsSwitch.AccessibleDescription = Lang.T("lib.fso.tip");
            tips.SetToolTip(FullscreenOptimizationsSwitch, Lang.T("lib.fso.tip"));
            FamilySwitch.Gated = !Item.RendererObserved;
            FamilySwitch.Surface = Surface;
            string observed = Lang.T(Item.RendererObserved ? "lib.renderer.observed" : "lib.renderer.pending");
            string hint = Lang.T(Item.RendererObserved ? "lib.renderer.observed.tip" : "lib.renderer.pending.tip");
            string path = Item.Profile.ExecutablePath ?? Item.Profile.Root ?? "";
            AccessibleName = Item.Profile.Name;
            AccessibleDescription = observed + " · " + MetadataText + "\r\n" + path + "\r\n" + hint;
            CoreButton.Bg = Surface;
            MoreButton.Bg = Surface;
            MoreButton.AccessibleName = Item.Profile.Name + " · " + Lang.T("workflow.more");
            MoreButton.AccessibleDescription = Lang.T("lib.rename") + " · " + Lang.T("v15.library.force") + " · " + Lang.T("btn.remove");
            tips.SetToolTip(MoreButton, Lang.T("workflow.more"));
            CoreButton.AccessibleName = Item.Profile.Name + " · " + Lang.T("nav.corescheduling");
            CoreButton.AccessibleDescription = Lang.T("lib.cores.tip");
            tips.SetToolTip(CoreButton, Lang.T("lib.cores.tip"));
            FamilySwitch.AccessibleName = Item.Profile.Name + " · " + Lang.T("lib.family.suppress");
            FamilySwitch.AccessibleDescription = hint + "\r\n" + Lang.T("lib.family.tip");
            tips.SetToolTip(this, Item.Profile.Name + "\r\n" + path + "\r\n" + MetadataText + "\r\n\r\n" + hint);
            tips.SetToolTip(FamilySwitch, (FamilySwitch.Gated && !FamilySwitch.Checked
                ? Lang.T("lib.family.gated.body") + "\r\n\r\n" : "") + Lang.T("lib.family.tip") + "\r\n\r\n" + hint);
            Invalidate(); FamilySwitch.Invalidate();
        }
        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            GameLibraryRowLayout layout = GameLibraryRowLayout.ForSize(ClientSize);
            if (FamilySwitch != null) FamilySwitch.Bounds = layout.Policy;
            if (RebarSwitch != null) RebarSwitch.Bounds = layout.Rebar;
            if (FullscreenOptimizationsSwitch != null) FullscreenOptimizationsSwitch.Bounds = layout.FullscreenOptimizations;
            if (CoreSwitch != null) CoreSwitch.Bounds = layout.Scheduling;
            if (MoreButton != null) MoreButton.Bounds = layout.More;
            if (CoreButton != null) CoreButton.SetBounds(Math.Max(0, CoreSwitch.Width - Theme.S(25)), Theme.S(2), Theme.S(24), Math.Max(1, CoreSwitch.Height - Theme.S(4)));
            if (Extension != null)
            {
                int baseH = GameLibraryRowLayout.HeightForWidth(ClientSize.Width);
                Extension.Bounds = new Rectangle(Theme.S(14), baseH, Math.Max(1, ClientSize.Width - Theme.S(28)),
                    Math.Max(1, ExtraHeight - Theme.S(9)));
            }
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
            base.OnKeyDown(e);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { Focus(); if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); }
            base.OnMouseDown(e);
        }
        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e); if (SelectionRequested != null) SelectionRequested(this, EventArgs.Empty); Invalidate();
        }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e); hovered = true; PushSurface(); Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e); hovered = false; PushSurface(); Invalidate();
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
                using (var border = new Pen(selected ? Col.Alpha(Theme.Accent, 95) : hovered ? Theme.StrokeHi : Theme.Stroke))
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
            TextRenderer.DrawText(g, Item.Profile.Name, Theme.UI(10f, true), r.Name, Theme.Fg, line);
            TextRenderer.DrawText(g, Item.Profile.ExecutablePath ?? Item.Profile.Root ?? "", Theme.UI(7.5f, false),
                r.Path, Theme.Dim, (line & ~TextFormatFlags.EndEllipsis) | TextFormatFlags.PathEllipsis);
            using (var statePath = Theme.TechPath(r.Running, Theme.S(4)))
            {
                using (var fill = new SolidBrush(Item.Running ? Col.Lerp(Theme.Inset, Theme.Green, 0.12f) : Theme.Inset)) g.FillPath(fill, statePath);
                using (var edge = new Pen(Item.Running ? Col.Alpha(Theme.Green, 100) : Theme.Stroke)) g.DrawPath(edge, statePath);
            }
            TextRenderer.DrawText(g, Lang.T(Item.Running ? "v15.library.running" : "v15.library.ready"),
                Theme.UI(7.1f, true), r.Running, Item.Running ? Theme.Green : Theme.Dim,
                line | TextFormatFlags.HorizontalCenter);

            string status = Lang.T(Item.RendererObserved ? "lib.renderer.observed" : "lib.renderer.pending");
            Color badgeColor = Item.RendererObserved ? Theme.Green : Theme.Dim;
            int measured = TextRenderer.MeasureText(status, Theme.UI(7.2f, false), Size.Empty, TextFormatFlags.NoPadding).Width;
            int badgeW = Math.Min(r.Metadata.Width, measured + Theme.S(12));
            var badge = new Rectangle(r.Metadata.X, r.Metadata.Y, Math.Max(1, badgeW), r.Metadata.Height);
            int dot = Math.Max(2, Theme.S(3));
            using (var b = new SolidBrush(badgeColor)) g.FillEllipse(b, badge.Left, badge.Top + (badge.Height - dot) / 2, dot, dot);
            TextRenderer.DrawText(g, status, Theme.UI(7.2f, false),
                new Rectangle(badge.Left + Theme.S(10), badge.Top, Math.Max(1, badge.Width - Theme.S(10)), badge.Height), badgeColor, line);

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
        public bool Gated;
        private readonly bool corePolicy;
        private bool Locked { get { return Gated && !Checked; } }
        private Motion switchMotion, hoverMotion;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e); switchMotion.Set(Checked ? 1f : 0f); UiClock.Frame += Animate;
        }
        protected override void OnHandleDestroyed(EventArgs e) { UiClock.Frame -= Animate; base.OnHandleDestroyed(e); }
        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (IsHandleCreated && Visible && !UiClock.Suspended && !UiClock.Frozen)
            { switchMotion.To(Checked ? 1f : 0f); UiClock.Wake(); }
            else switchMotion.Set(Checked ? 1f : 0f);
            Invalidate();
        }
        private void Animate(object sender, EventArgs e)
        {
            if (!Visible) return;
            bool a = switchMotion.Step(), b = hoverMotion.Step();
            if (a || b) Invalidate();
        }
        public FamilySuppressionSwitch() : this(false) { }
        public FamilySuppressionSwitch(bool isCorePolicy)
        {
            corePolicy = isCorePolicy;
            switchMotion.Speed = 0.28f; hoverMotion.Speed = 0.24f;
            AutoCheck = false; AutoSize = false; TabStop = true;
            Text = Lang.T(corePolicy ? "nav.corescheduling" : "lib.family.compact"); Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.CheckButton;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        internal static int PreferredWidth(string text, bool isCorePolicy)
        {
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            int textWidth = TextRenderer.MeasureText(text, Theme.UI(8f, true), Size.Empty, flags).Width;
            return textWidth + Theme.S(32) + Theme.S(8) + Theme.S(44)
                + (isCorePolicy ? Theme.S(26) : 0);
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            return new Size(PreferredWidth(Text, corePolicy), Theme.S(32));
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
        protected override void OnMouseEnter(EventArgs e)
        { base.OnMouseEnter(e); hoverMotion.To(1f); UiClock.Wake(); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e)
        { base.OnMouseLeave(e); hoverMotion.To(0f); UiClock.Wake(); Invalidate(); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (var b = new SolidBrush(Surface)) e.Graphics.FillRectangle(b, ClientRectangle);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            Graphics g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float progress = switchMotion.Value;
            var frame = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = Col.Lerp(Theme.Inset, Theme.Accent, (Checked ? 0.065f : 0.015f) + hoverMotion.Value * 0.05f);
            using (GraphicsPath path = Theme.TechPath(frame, Theme.S(6)))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
                using (var p = new Pen(Checked ? Col.Alpha(Theme.Accent, 118) : Col.Lerp(Theme.Stroke, Theme.StrokeHi, hoverMotion.Value))) g.DrawPath(p, path);
            }
            int detailsW = corePolicy ? Theme.S(26) : 0;
            var track = new Rectangle(Width - detailsW - Theme.S(44), (Height - Theme.S(16)) / 2, Theme.S(32), Theme.S(16));
            Glyphs.Draw(g, corePolicy ? "chip" : "tiles", new Rectangle(Theme.S(10), (Height - Theme.S(14)) / 2, Theme.S(14), Theme.S(14)), Checked ? Theme.Accent : Theme.Dim);
            int tx = Theme.S(32), tw = Math.Max(1, track.Left - tx - Theme.S(8));
            const TextFormatFlags line = TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            bool locked = Locked;
            TextRenderer.DrawText(g, Text, Theme.UI(8f, true),
                new Rectangle(tx, 0, tw, Height), Enabled ? Theme.Fg : Theme.Faint, line);
            using (GraphicsPath path = Theme.TechPath(track, Theme.S(4)))
            {
                using (var b = new SolidBrush(Col.Lerp(Theme.TrackOff, Col.Lerp(Surface, Theme.Accent, 0.24f), progress))) g.FillPath(b, path);
                using (var p = new Pen(Checked ? Theme.Accent : locked ? Theme.Stroke : Theme.StrokeHi)) g.DrawPath(p, path);
            }
            int knob = Theme.S(10), pad = Theme.S(3);
            var thumb = new Rectangle(track.Left + pad + (int)((track.Width - pad * 2 - knob) * progress), track.Top + pad, knob, knob);
            using (GraphicsPath path = Theme.TechPath(thumb, Theme.S(3)))
            using (var b = new SolidBrush(Col.Lerp(Theme.Dim, Theme.Accent, progress))) g.FillPath(b, path);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(frame, -Theme.S(4), -Theme.S(4)), Theme.Accent, fill);
        }
    }

    internal sealed class GameCardDetailsButton : PillButton
    {
        internal GameCardDetailsButton() : base("›") { Font = Theme.UI(13f, true); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var policy = Parent as FamilySuppressionSwitch;
            Color surface = Col.Lerp(Theme.Inset, Theme.Accent, policy != null && policy.Checked ? 0.065f : 0.015f);
            using (var b = new SolidBrush(Col.Lerp(surface, Theme.Accent, hover.Value * 0.12f))) e.Graphics.FillRectangle(b, ClientRectangle);
            using (var line = new Pen(Theme.Stroke)) e.Graphics.DrawLine(line, 0, Theme.S(5), 0, Height - Theme.S(5));
            int cx = Width / 2, cy = Height / 2;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var arrow = new Pen(hover.Value > 0.1f ? Theme.Accent : Theme.Dim, Math.Max(1f, Theme.S(1))))
                e.Graphics.DrawLines(arrow, new[] { new Point(cx - Theme.S(2), cy - Theme.S(4)), new Point(cx + Theme.S(2), cy), new Point(cx - Theme.S(2), cy + Theme.S(4)) });
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3), Theme.Accent, surface);
        }
    }

    internal struct GameLibraryRowLayout
    {
        public Rectangle Icon, Running, Name, Path, Metadata, Policy, Scheduling, Rebar, FullscreenOptimizations, More;
        private static float measuredScale;
        private static int measuredLanguage = -1, schedulingWidth, familyWidth, rebarWidth, fullscreenWidth;
        private static void MeasurePolicies()
        {
            if (measuredLanguage != Lang.Cur || measuredScale != Dpi.Scale)
            {
                measuredLanguage = Lang.Cur; measuredScale = Dpi.Scale;
                schedulingWidth = FamilySuppressionSwitch.PreferredWidth(Lang.T("nav.corescheduling"), true);
                familyWidth = FamilySuppressionSwitch.PreferredWidth(Lang.T("lib.family.compact"), false);
                rebarWidth = FamilySuppressionSwitch.PreferredWidth(Lang.T("lib.rebar"), false);
                fullscreenWidth = FamilySuppressionSwitch.PreferredWidth(Lang.T("lib.fso"), false);
            }
        }
        private static Rectangle PlacePolicy(int preferredWidth, int available, ref int x, ref int y)
        {
            int inset = Theme.S(14), width = Math.Min(preferredWidth, available);
            if (x > inset && x + width > inset + available)
            {
                x = inset; y += Theme.S(32) + Theme.S(8);
            }
            var bounds = new Rectangle(x, y, width, Theme.S(32));
            x = bounds.Right + Theme.S(10);
            return bounds;
        }
        private static GameLibraryRowLayout PolicyLayout(int width)
        {
            MeasurePolicies();
            var r = new GameLibraryRowLayout();
            int x = Theme.S(14), y = Theme.S(72), available = Math.Max(1, width - Theme.S(14) * 2);
            r.Scheduling = PlacePolicy(schedulingWidth, available, ref x, ref y);
            r.Policy = PlacePolicy(familyWidth, available, ref x, ref y);
            r.Rebar = PlacePolicy(rebarWidth, available, ref x, ref y);
            r.FullscreenOptimizations = PlacePolicy(fullscreenWidth, available, ref x, ref y);
            return r;
        }
        public static int HeightForWidth(int width)
        {
            return PolicyLayout(width).FullscreenOptimizations.Bottom + Theme.S(8);
        }
        public static GameLibraryRowLayout ForSize(Size size)
        {
            var r = PolicyLayout(size.Width);
            int inset = Theme.S(14), textX = Theme.S(70);
            int textRight = size.Width - inset;
            int textW = Math.Max(1, textRight - textX);
            r.Icon = new Rectangle(Theme.S(18), Theme.S(16), Theme.S(36), Theme.S(36));
            r.More = new Rectangle(textRight - Theme.S(28), Theme.S(10), Theme.S(28), Theme.S(22));
            r.Running = new Rectangle(r.More.Left - Theme.S(80), Theme.S(12), Theme.S(72), Theme.S(20));
            r.Name = new Rectangle(textX, Theme.S(10), Math.Max(1, r.Running.Left - textX - Theme.S(10)), Theme.S(22));
            r.Path = new Rectangle(textX, Theme.S(32), textW, Theme.S(18));
            r.Metadata = new Rectangle(textX, Theme.S(52), textW, Theme.S(16));
            return r;
        }
    }
}
