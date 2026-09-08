using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class CoreSchedulingPanel : DBPanel
    {
        internal readonly CoreMatrix Matrix, IsolationMatrix;
        internal readonly Toggle IsolationToggle, FollowToggle;
        internal readonly PillButton SaveButton, PhysicalOnlyButton, IsolationDetailsButton;
        internal CoreSchedulingPlan Draft;
        internal bool IsolationExpanded { get; private set; }
        private CoreSchedulingPlan baseline;
        private string globalVersion, profileVersion, lastMessage, matrixTopology;
        private bool syncing, savedFollow;
        private readonly bool perGame;
        private readonly Label summary, isolationHelp, legend, status;
        private readonly SettingCard followCard, gameCard, isolationCard;
        private readonly RoundPanel isolationBody, footer;
        private readonly ActionHost isolationActions;
        private bool lightTheme;
        private int InnerWidth { get { return Width - Theme.S(60); } }
        private readonly FlowLayoutPanel shortcuts, isolationShortcuts;
        private readonly PillButton reloadButton;
        private readonly Func<GameProfile> readProfile;
        private readonly Func<CoreSchedulingPlan, string, string, bool, string> save;
        private readonly Func<bool> active;
        private readonly Timer runtimeRefresh;

        internal CoreSchedulingPanel(int width, Func<GameProfile> profile,
            Func<CoreSchedulingPlan, string, string, bool, string> savePlan, Func<bool> isActive)
        {
            perGame = profile != null; readProfile = profile; save = savePlan; active = isActive;
            BackColor = Theme.Bg; Width = Theme.S(width); lightTheme = Theme.LightMode;
            followCard = Card(1, "schedule.follow.title", Lang.T("schedule.follow"));
            followCard.Visible = perGame;
            FollowToggle = Switch("schedule.follow.title"); followCard.Host(FollowToggle);
            FollowToggle.CheckedChanged += delegate { if (!syncing) { lastMessage = null; RefreshView(); } };
            gameCard = Card(perGame ? 2 : 1, "schedule.game", Lang.T("schedule.help.game"));
            shortcuts = Shortcuts(gameCard);
            AddShortcut(shortcuts, Lang.T("core.preset.all"), delegate { SelectMask(CpuTopology.AllMask); });
            PhysicalOnlyButton = AddShortcut(shortcuts, Lang.T("core.preset.physical"), SelectSingleThreadPerCore);
            AddShortcut(shortcuts, Lang.T("schedule.clear"), delegate { SelectMask(0); });
            AddShortcut(shortcuts, Lang.T("core.preset.invert"), delegate { SelectMask(~Draft.GameMask & CpuTopology.AllMask); });
            AddDieShortcuts(shortcuts, false);
            Matrix = NewMatrix(gameCard, false); Matrix.SelectionChanged = SelectMask;
            summary = MakeLabel(gameCard, null);
            isolationCard = Card(perGame ? 3 : 2, "schedule.isolation.enable", "");
            isolationActions = new ActionHost { Height = Theme.S(30),
                Width = perGame ? Theme.S(46) : Theme.S(256), BackColor = Theme.Card };
            isolationCard.HostTop = true; isolationCard.Host(isolationActions);
            IsolationToggle = Switch("schedule.isolation.enable");
            isolationActions.Controls.Add(IsolationToggle);
            IsolationToggle.Left = isolationActions.Width - IsolationToggle.Width;
            IsolationToggle.CheckedChanged += delegate
            {
                if (syncing) return;
                Draft.IsolationOn = IsolationToggle.Checked;
                if (Draft.IsolationOn && Draft.IsolationMask == 0) IsolationExpanded = true;
                Edited();
            };
            IsolationDetailsButton = new PillButton(Lang.T("schedule.isolation.expand"));
            IsolationDetailsButton.Size = new Size(Theme.S(194), Theme.S(30));
            IsolationDetailsButton.Visible = !perGame;
            IsolationDetailsButton.Click += delegate { SetIsolationExpanded(!IsolationExpanded); };
            isolationActions.Controls.Add(IsolationDetailsButton);
            isolationBody = Surface();
            isolationHelp = MakeLabel(isolationBody, "schedule.help.isolation");
            isolationShortcuts = Shortcuts(isolationBody);
            AddShortcut(isolationShortcuts, Lang.T("schedule.clear"), delegate { SelectIsolationMask(0); });
            AddShortcut(isolationShortcuts, Lang.T("core.preset.invert"),
                delegate { SelectIsolationMask(~Draft.IsolationMask & CpuTopology.AllMask); });
            AddDieShortcuts(isolationShortcuts, true);
            IsolationMatrix = NewMatrix(isolationBody, true); IsolationMatrix.SelectionChanged = SelectIsolationMask;
            legend = MakeLabel(isolationBody, "schedule.legend");
            footer = Surface();
            SaveButton = new PillButton(Lang.T("schedule.save"), BtnKind.Primary);
            SaveButton.Size = new Size(Theme.S(150), Theme.S(36));
            SaveButton.Bg = Theme.Card;
            SaveButton.Click += delegate { SaveDraft(); }; footer.Controls.Add(SaveButton);
            reloadButton = new PillButton(Lang.T("schedule.reload"));
            reloadButton.Size = new Size(Theme.S(142), Theme.S(36));
            reloadButton.Bg = Theme.Card;
            reloadButton.Click += delegate { Reload(); }; footer.Controls.Add(reloadButton);
            status = MakeLabel(footer, null); status.Left = Theme.S(18);
            status.Width = Width - Theme.S(358);
            Reload();
            runtimeRefresh = new Timer { Interval = 1000 };
            runtimeRefresh.Tick += delegate { if (Visible) RefreshView(); };
            runtimeRefresh.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && runtimeRefresh != null) runtimeRefresh.Dispose();
            base.Dispose(disposing);
        }

        private Toggle Switch(string key)
        {
            return new Toggle { Bg = Theme.Card, AccessibleName = Lang.T(key),
                Size = new Size(Theme.S(46), Theme.S(30)) };
        }

        private SettingCard Card(int channel, string key, string desc)
        {
            var card = new SettingCard { Channel = channel, Title = Lang.T(key), Desc = desc,
                Width = Width, HostTop = true };
            Controls.Add(card); return card;
        }

        private RoundPanel Surface()
        {
            var panel = new RoundPanel { Width = Width, Fill = Theme.Card, Border = Theme.Stroke,
                BackColor = Theme.Bg, Radius = Theme.S(12), AccentEdge = true };
            Controls.Add(panel); return panel;
        }

        // Like the existing settings action groups, share the card surface without adding another frame.
        private sealed class ActionHost : RoundPanel
        {
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                var card = Parent as RoundPanel;
                Fill = card == null ? Theme.Card : card.Fill;
                if (Backdrop.AppliesTo(this)) Backdrop.PaintOnCard(e.Graphics, this, e.ClipRectangle);
                else using (var brush = new SolidBrush(Fill)) e.Graphics.FillRectangle(brush, e.ClipRectangle);
            }
            protected override void OnPaint(PaintEventArgs e) { }
        }

        private Label MakeLabel(Control parent, string key)
        {
            var l = new Label { Text = key == null ? "" : Lang.T(key), Width = InnerWidth, Left = Theme.S(42),
                BackColor = Color.Transparent, ForeColor = Theme.Dim, Font = Theme.UI(8.4f, false),
                UseCompatibleTextRendering = false };
            parent.Controls.Add(l); return l;
        }

        private CoreMatrix NewMatrix(Control parent, bool isolation)
        {
            var m = new CoreMatrix { MarkExclusive = false, SchedulingOverlay = true,
                SelectWholeCore = isolation, SelectionColor = isolation ? CoreMatrix.IsolationColor : CoreMatrix.GameColor,
                PrimaryTag = Lang.T(isolation ? "schedule.isolation.short" : "core.tag.game"),
                Width = InnerWidth, Left = Theme.S(42), BackColor = Theme.Card };
            parent.Controls.Add(m); return m;
        }

        private FlowLayoutPanel Shortcuts(Control parent)
        {
            var p = new FlowLayoutPanel { BackColor = Color.Transparent, WrapContents = true,
                Width = InnerWidth, Left = Theme.S(42) };
            parent.Controls.Add(p); return p;
        }

        private PillButton AddShortcut(FlowLayoutPanel panel, string text, Action action)
        {
            var b = new PillButton(text) { Bg = Theme.Card, Width = Theme.S(108), Height = Theme.S(30),
                Margin = new Padding(0, 0, Theme.S(6), Theme.S(5)) };
            b.Click += delegate { action(); }; panel.Controls.Add(b); return b;
        }

        private void AddDieShortcuts(FlowLayoutPanel panel, bool isolation)
        {
            ulong[] dies = CpuTopology.DieMasks();
            for (int d = 0; d < dies.Length; d++)
            {
                ulong mask = dies[d];
                AddShortcut(panel, "CCD " + d, delegate
                { if (isolation) SelectIsolationMask(mask); else SelectMask(mask); }).Tag = "ccd";
            }
        }

        private void RebuildDieShortcuts(FlowLayoutPanel panel, bool isolation)
        {
            for (int i = panel.Controls.Count - 1; i >= 0; i--)
                if (object.Equals(panel.Controls[i].Tag, "ccd")) panel.Controls[i].Dispose();
            AddDieShortcuts(panel, isolation);
        }

        private void SelectSingleThreadPerCore()
        {
            if (!PhysicalOnlyButton.Enabled) return;
            ulong mask = 0;
            foreach (ulong core in CpuTopology.PhysicalCoreMasks())
            {
                ulong selected = Draft.GameMask & core;
                if (selected != 0) mask |= selected & (~selected + 1UL);
            }
            SelectMask(mask);
        }

        internal void Reload()
        {
            CoreSchedulingPlan global = CoreScheduling.LoadGlobal();
            globalVersion = CoreScheduling.GlobalToken();
            GameProfile profile = perGame ? readProfile() : null;
            profileVersion = CoreScheduling.ProfileToken(profile);
            Draft = CoreScheduling.ForProfile(profile, global);
            baseline = Draft.Clone();
            savedFollow = perGame && !HasOverride(profile);
            syncing = true; FollowToggle.SetSilently(savedFollow); syncing = false;
            lastMessage = null;
            if (!perGame && Draft.IsolationOn && Draft.IsolationMask == 0) IsolationExpanded = true;
            RefreshView();
        }

        private static bool HasOverride(GameProfile p)
        {
            if (p == null) return false;
            if (p.Overrides.ContainsKey(CoreScheduling.Key)) return true;
            foreach (string key in CoreScheduling.PlacementKeys)
                if (!CoreScheduling.IsRetiredKey(key) && p.Overrides.ContainsKey(key)) return true;
            return false;
        }

        internal void SelectMask(ulong mask)
        {
            if (CpuTopology.MultiGroup || perGame && FollowToggle.Checked) return;
            Draft.GameMask = mask; Edited(true);
        }

        internal void SelectIsolationMask(ulong mask)
        {
            if (perGame || CpuTopology.MultiGroup) return;
            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            Draft.IsolationMask = CoreScheduling.WholeCores(mask, cores) & ~CoreScheduling.WholeCores(1, cores);
            Edited(true);
        }

        internal void SetIsolationExpanded(bool expanded)
        {
            IsolationExpanded = !perGame && expanded; RefreshView();
        }

        private void Edited(bool selectionChanged = false)
        {
            if (selectionChanged) { Draft.Topology = CoreScheduling.CurrentStamp; Draft.ReadFailed = false; }
            lastMessage = null; RefreshView();
        }

        internal void SaveDraft()
        {
            if (!SaveButton.Enabled) return;
            string error = save(Draft.Clone(), globalVersion, profileVersion, perGame && FollowToggle.Checked);
            if (error != null) { lastMessage = error; RefreshView(); return; }
            Reload();
            lastMessage = Lang.T(active() ? "schedule.saved.next" : "schedule.saved"); RefreshView();
        }

        internal void RefreshView()
        {
            if (Draft == null) return;
            string topology = CoreScheduling.CurrentStamp;
            if (matrixTopology != topology)
            {
                matrixTopology = topology;
                Matrix.Height = Matrix.LayoutFor(InnerWidth); IsolationMatrix.Height = IsolationMatrix.LayoutFor(InnerWidth);
                RebuildDieShortcuts(shortcuts, false); RebuildDieShortcuts(isolationShortcuts, true);
            }
            bool follow = perGame && FollowToggle.Checked;
            CoreSchedulingPlan display = follow ? CoreScheduling.LoadGlobal() : Draft;
            syncing = true; IsolationToggle.SetSilently(display.IsolationOn); syncing = false;
            RefreshSurfaces();
            Matrix.SelectionColor = CoreMatrix.GameColor; IsolationMatrix.SelectionColor = CoreMatrix.IsolationColor;
            summary.ForeColor = Theme.Accent;
            bool editable = !follow && !CpuTopology.MultiGroup;
            Matrix.Enabled = editable; shortcuts.Enabled = editable;
            bool selectedSiblings = false;
            foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                if (CpuTopology.CountSetBits(display.GameMask & core) > 1) { selectedSiblings = true; break; }
            PhysicalOnlyButton.Enabled = editable && selectedSiblings;
            IsolationToggle.Enabled = !perGame && (CoreScheduling.IsolationSupported || Draft.IsolationOn);
            IsolationMatrix.Enabled = isolationShortcuts.Enabled = !perGame && !CpuTopology.MultiGroup;
            IsolationMatrix.DisabledMask = CoreScheduling.WholeCores(1, CpuTopology.PhysicalCoreMasks());
            Matrix.Selected = display.GameMask; IsolationMatrix.Selected = display.IsolationMask;
            Matrix.SchedulingGameMask = IsolationMatrix.SchedulingGameMask = display.GameMask;
            Matrix.SchedulingIsolationMask = display.IsolationOn ? display.IsolationMask : 0;
            IsolationMatrix.SchedulingIsolationMask = display.IsolationMask;
            Matrix.Invalidate(); IsolationMatrix.Invalidate();
            summary.Text = Lang.F("schedule.summary", CpuTopology.DescribeMask(display.GameMask));
            isolationCard.Desc = perGame ? Lang.F("schedule.isolation.global",
                Lang.T(display.IsolationOn ? "schedule.on" : "schedule.off"), CpuTopology.DescribeMask(display.IsolationMask))
                : display.IsolationOn ? Lang.F("schedule.isolation.range", CpuTopology.DescribeMask(display.IsolationMask))
                : Lang.T("schedule.isolation.off");
            IsolationDetailsButton.Text = Lang.T(IsolationExpanded ? "schedule.isolation.collapse" : "schedule.isolation.expand");
            string validation = CoreScheduling.Validate(Draft);
            bool dirty = Draft.Encode() != baseline.Encode() || FollowToggle.Checked != savedFollow;
            bool globalChanged = globalVersion != CoreScheduling.GlobalToken()
                || perGame && profileVersion != CoreScheduling.ProfileToken(readProfile());
            SaveButton.Enabled = dirty && (follow || validation == null) && !globalChanged && globalVersion != "unreadable";
            string state = globalChanged ? Lang.T("schedule.error.changed")
                : lastMessage ?? (validation != null && !follow ? Lang.T(validation)
                : Lang.T(dirty ? "schedule.dirty" : active() ? "schedule.stored.active" : "schedule.stored"));
            string prerequisite = CoreScheduling.PrerequisiteText(perGame ? readProfile() : null);
            string runtime = Lang.T(CoreIsolationClient.State);
            if (CoreIsolationClient.State == "schedule.isolation.active")
                runtime += " " + CpuTopology.DescribeMask(CoreIsolationClient.ActiveMask);
            status.Text = state + "\r\n" + runtime + (prerequisite.Length == 0 ? "" : "\r\n" + prerequisite);
            status.ForeColor = globalChanged || lastMessage != null && !lastMessage.StartsWith(Lang.T("schedule.saved.prefix"))
                || CoreIsolationClient.State == "schedule.isolation.pending" || CoreIsolationClient.State == "schedule.isolation.failed"
                || validation != null && !follow ? Theme.Danger : Theme.Dim;
            LayoutContent();
        }

        private int PlaceLabel(Label label, int y, int minHeight)
        {
            label.Top = y;
            label.Height = Math.Max(Theme.S(minHeight), TextRenderer.MeasureText(label.Text, label.Font,
                new Size(label.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl).Height + Theme.S(4));
            return label.Bottom;
        }

        private int PlaceShortcuts(FlowLayoutPanel panel, int y)
        {
            panel.Top = y; panel.Height = panel.GetPreferredSize(new Size(panel.Width, 0)).Height;
            return panel.Bottom + Theme.S(4);
        }

        private void RefreshSurfaces()
        {
            if (lightTheme == Theme.LightMode) return;
            lightTheme = Theme.LightMode; BackColor = Theme.Bg;
            foreach (RoundPanel card in new RoundPanel[] { followCard, gameCard, isolationCard, isolationBody, footer })
            {
                card.BackColor = Theme.Bg; card.Fill = Theme.Card; card.Border = Theme.Stroke; card.Invalidate();
            }
            foreach (FxControl control in new FxControl[] { FollowToggle, IsolationToggle, IsolationDetailsButton, SaveButton, reloadButton })
            { control.Bg = Theme.Card; control.Invalidate(); }
            foreach (FlowLayoutPanel row in new[] { shortcuts, isolationShortcuts })
                foreach (PillButton button in row.Controls) { button.Bg = Theme.Card; button.Invalidate(); }
            foreach (Label label in new[] { summary, isolationHelp, legend }) label.ForeColor = Theme.Dim;
            Matrix.BackColor = IsolationMatrix.BackColor = Theme.Card;
        }

        private int CardHeaderHeight(SettingCard card, int hostWidth)
        {
            int textWidth = Width - Theme.S(84) - (hostWidth == 0 ? 0 : hostWidth + Theme.S(14));
            int textHeight = TextRenderer.MeasureText(card.Desc, Theme.UI(8.5f, false),
                new Size(Math.Max(1, textWidth), int.MaxValue), TextFormatFlags.WordBreak).Height;
            return Math.Max(Theme.S(64), Theme.S(36) + textHeight + Theme.S(12));
        }

        private void LayoutContent()
        {
            SuspendLayout();
            int gap = Theme.S(10), y = 0;
            followCard.Top = 0;
            if (perGame)
            {
                followCard.Height = CardHeaderHeight(followCard, FollowToggle.Width);
                y = followCard.Bottom + gap;
            }
            gameCard.Top = y;
            int gy = CardHeaderHeight(gameCard, 0);
            gy = PlaceShortcuts(shortcuts, gy);
            Matrix.Top = gy;
            gameCard.Height = PlaceLabel(summary, Matrix.Bottom + Theme.S(8), 24) + Theme.S(12);
            isolationCard.Top = gameCard.Bottom + gap;
            isolationCard.Height = CardHeaderHeight(isolationCard, isolationActions.Width);
            y = isolationCard.Bottom;
            bool expanded = IsolationExpanded && !perGame;
            isolationBody.Visible = expanded;
            if (expanded)
            {
                isolationBody.Top = y + Theme.S(6);
                int iy = PlaceLabel(isolationHelp, Theme.S(14), 32) + Theme.S(8);
                iy = PlaceShortcuts(isolationShortcuts, iy);
                IsolationMatrix.Top = iy;
                isolationBody.Height = PlaceLabel(legend, IsolationMatrix.Bottom + Theme.S(8), 24) + Theme.S(12);
                y = isolationBody.Bottom;
            }
            footer.Top = y + gap;
            footer.Height = Math.Max(Theme.S(76), PlaceLabel(status, Theme.S(14), 48) + Theme.S(14));
            SaveButton.Location = new Point(Width - Theme.S(18) - SaveButton.Width, (footer.Height - SaveButton.Height) / 2);
            reloadButton.Location = new Point(SaveButton.Left - Theme.S(10) - reloadButton.Width, SaveButton.Top);
            Height = footer.Bottom + Theme.S(4);
            ResumeLayout();
        }
    }
}
