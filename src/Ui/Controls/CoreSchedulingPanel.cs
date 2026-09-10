using System;
using System.Drawing;
using System.Windows.Forms;

namespace PaviseApp
{
    internal sealed class CoreSchedulingPanel : DBPanel
    {
        internal readonly CoreMatrix Matrix;
        // 独占范围由游戏选核推出 不再有第二张选核图
        internal readonly Toggle IsolationToggle, FollowToggle;
        internal readonly PillButton SaveButton, PhysicalOnlyButton, TrimForExclusiveButton;
        internal CoreSchedulingPlan Draft;
        private CoreSchedulingPlan baseline;
        private string globalVersion, profileVersion, lastMessage, matrixTopology;
        private bool syncing, savedFollow;
        private readonly bool perGame;
        private readonly Label summary, status;
        private readonly SettingCard followCard, gameCard, exclusiveCard, hardCard;
        internal readonly Toggle HardAffinityToggle;
        // 硬亲和不进核心方案 它是独立的全局设置 由宿主读写
        internal Action<bool> HardAffinityChanged;
        internal Func<bool> HardAffinityState;
        private readonly RoundPanel footer;
        private bool lightTheme;
        private int InnerWidth { get { return Width - Theme.S(60); } }
        private readonly FlowLayoutPanel shortcuts;
        private readonly PillButton reloadButton;
        private readonly Func<GameProfile> readProfile;
        private readonly Func<CoreSchedulingPlan, string, string, bool, string> save;
        private readonly Func<bool> active;
        private readonly Timer runtimeRefresh;

        internal sealed class EditorState
        {
            internal CoreSchedulingPlan Draft, Baseline;
            internal string GlobalVersion, ProfileVersion, LastMessage;
            internal bool FollowGlobal, SavedFollow;
        }

        internal EditorState CaptureEditorState()
        {
            return new EditorState { Draft = Draft.Clone(), Baseline = baseline.Clone(),
                GlobalVersion = globalVersion, ProfileVersion = profileVersion, LastMessage = lastMessage,
                FollowGlobal = FollowToggle.Checked, SavedFollow = savedFollow };
        }

        internal void RestoreEditorState(EditorState state)
        {
            if (state == null) return;
            Draft = state.Draft.Clone(); baseline = state.Baseline.Clone();
            globalVersion = state.GlobalVersion; profileVersion = state.ProfileVersion;
            lastMessage = state.LastMessage; savedFollow = state.SavedFollow;
            syncing = true; FollowToggle.SetSilently(state.FollowGlobal); syncing = false;
            RefreshRestoredProfileIsolation();
            RefreshView();
        }

        private void RefreshRestoredProfileIsolation()
        {
            if (!perGame || readProfile() == null
                || profileVersion != CoreScheduling.ProfileToken(readProfile())) return;
            string currentVersion = CoreScheduling.GlobalToken();
            if (currentVersion == globalVersion || currentVersion == "unreadable") return;
            CoreSchedulingPlan global = CoreScheduling.LoadGlobal();
            if (global.ReadFailed || global.Topology != CoreScheduling.CurrentStamp
                || currentVersion != CoreScheduling.GlobalToken()
                || profileVersion != CoreScheduling.ProfileToken(readProfile())) return;

            // 逐游戏只保存选核，隔离始终取全局；更新引用时保留本地草稿与跟随选择。
            // 基线也同步隔离字段，避免仅全局隔离变化就把逐游戏页标成未保存。
            globalVersion = currentVersion;
            Draft.IsolationOn = baseline.IsolationOn = global.IsolationOn;
            Draft.IsolationMask = baseline.IsolationMask = global.IsolationMask;
            lastMessage = null;
        }

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
            // 默认是全选 那样独占之外一颗核都不剩 这个按钮把选核收到刚好能独占
            TrimForExclusiveButton = AddShortcut(shortcuts, Lang.T("core.preset.sparesystem"), delegate
            {
                ulong trimmed = CoreScheduling.TrimForExclusive(Draft.GameMask, CpuTopology.PhysicalCoreMasks());
                if (trimmed != 0) SelectMask(trimmed);
            });
            AddDieShortcuts(shortcuts);
            Matrix = NewMatrix(gameCard); Matrix.SelectionChanged = SelectMask;
            summary = MakeLabel(gameCard, null);
            // 独占是游戏选核的附加项 放在同一页选核图下方 不再单开一页也不再选第二遍
            exclusiveCard = Card(perGame ? 3 : 2, "schedule.exclusive", "");
            IsolationToggle = Switch("schedule.exclusive");
            exclusiveCard.Host(IsolationToggle);
            // 独占只有全局一份 逐游戏页只读展示 免得看起来能按游戏设
            IsolationToggle.Visible = !perGame;
            IsolationToggle.CheckedChanged += delegate
            {
                if (syncing || perGame) return;
                Draft.IsolationOn = IsolationToggle.Checked;
                SyncExclusiveMask();
                Edited();
            };
            // 硬亲和是独占的附加项 只有全局一份 逐游戏页不显示
            hardCard = Card(perGame ? 4 : 3, "schedule.hardaffinity", "");
            HardAffinityToggle = Switch("schedule.hardaffinity");
            hardCard.Host(HardAffinityToggle);
            HardAffinityToggle.Visible = !perGame;
            hardCard.Visible = !perGame;
            HardAffinityToggle.CheckedChanged += delegate
            {
                if (syncing || perGame || HardAffinityChanged == null) return;
                HardAffinityChanged(HardAffinityToggle.Checked);
                RefreshView();
            };
            footer = Surface();
            SaveButton = new PillButton(Lang.T(perGame ? "schedule.save" : "schedule.save.global"), BtnKind.Primary);
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
            if (disposing)
            {
                if (runtimeRefresh != null) runtimeRefresh.Dispose();
            }
            base.Dispose(disposing);
        }

        private Toggle Switch(string key)
        {
            return new Toggle { Bg = Theme.Card, AccessibleName = Lang.T(key),
                Size = new Size(Theme.S(46), Theme.S(30)) };
        }

        private SettingCard Card(int channel, string key, string desc, Control parent = null)
        {
            var card = new SettingCard { Channel = channel, Title = Lang.T(key), Desc = desc,
                Width = Width, HostTop = true };
            (parent ?? this).Controls.Add(card); return card;
        }

        private RoundPanel Surface(Control parent = null)
        {
            var panel = new RoundPanel { Width = Width, Fill = Theme.Card, Border = Theme.Stroke,
                BackColor = Theme.Bg, Radius = Theme.S(12), AccentEdge = true };
            (parent ?? this).Controls.Add(panel); return panel;
        }

        private Label MakeLabel(Control parent, string key)
        {
            var l = new Label { Text = key == null ? "" : Lang.T(key), Width = InnerWidth, Left = Theme.S(42),
                BackColor = Color.Transparent, ForeColor = Theme.Dim, Font = Theme.UI(8.4f, false),
                UseCompatibleTextRendering = false };
            parent.Controls.Add(l); return l;
        }

        private CoreMatrix NewMatrix(Control parent)
        {
            var m = new CoreMatrix { MarkExclusive = false, SchedulingOverlay = true,
                SelectWholeCore = false, SelectionColor = CoreMatrix.GameColor,
                PrimaryTag = Lang.T("core.tag.game"),
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

        private void AddDieShortcuts(FlowLayoutPanel panel)
        {
            ulong[] dies = CpuTopology.DieMasks();
            for (int d = 0; d < dies.Length; d++)
            {
                ulong mask = dies[d];
                AddShortcut(panel, Lang.F("core.preset.ccd", d), delegate { SelectMask(mask); }).Tag = "ccd";
            }
        }

        private void RebuildDieShortcuts(FlowLayoutPanel panel)
        {
            for (int i = panel.Controls.Count - 1; i >= 0; i--)
                if (object.Equals(panel.Controls[i].Tag, "ccd")) panel.Controls[i].Dispose();
            AddDieShortcuts(panel);
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
            Draft.GameMask = mask;
            SyncExclusiveMask();
            Edited(true);
        }

        // 独占范围永远由当前游戏选核推出 推不出来就当没勾 免得留下一个开着却空的范围
        private void SyncExclusiveMask()
        {
            if (perGame) return;
            ulong exclusive = CoreScheduling.ExclusiveMaskFor(Draft.GameMask, CpuTopology.PhysicalCoreMasks());
            Draft.IsolationMask = exclusive;
            if (exclusive == 0) Draft.IsolationOn = false;
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
            if (Draft == null || IsDisposed) return;
            string topology = CoreScheduling.CurrentStamp;
            if (matrixTopology != topology)
            {
                matrixTopology = topology;
                Matrix.Height = Matrix.LayoutFor(InnerWidth);
                RebuildDieShortcuts(shortcuts);
            }
            bool follow = perGame && FollowToggle.Checked;
            CoreSchedulingPlan display = follow ? CoreScheduling.LoadGlobal() : Draft;
            syncing = true; IsolationToggle.SetSilently(display.IsolationOn); syncing = false;
            RefreshSurfaces();
            Matrix.SelectionColor = CoreMatrix.GameColor;
            summary.ForeColor = Theme.Accent;
            bool editable = !follow && !CpuTopology.MultiGroup;
            Matrix.Enabled = editable; shortcuts.Enabled = editable;
            // 推不出独占范围时开关不可用 免得勾了一个空范围
            ulong exclusive = CoreScheduling.ExclusiveMaskFor(display.GameMask, CpuTopology.PhysicalCoreMasks());
            bool selectedSiblings = false;
            foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                if (CpuTopology.CountSetBits(display.GameMask & core) > 1) { selectedSiblings = true; break; }
            PhysicalOnlyButton.Enabled = editable && selectedSiblings;
            TrimForExclusiveButton.Enabled = editable && exclusive == 0
                && CoreScheduling.TrimForExclusive(display.GameMask, CpuTopology.PhysicalCoreMasks()) != 0;
            IsolationToggle.Enabled = !perGame && exclusive != 0
                && (CoreScheduling.IsolationSupported || Draft.IsolationOn);
            Matrix.Selected = display.GameMask;
            Matrix.SchedulingGameMask = display.GameMask;
            Matrix.SchedulingIsolationMask = display.IsolationOn ? display.IsolationMask : 0;
            Matrix.Invalidate();
            summary.Text = Lang.F("schedule.summary", CpuTopology.DescribeMask(display.GameMask));
            exclusiveCard.Desc = perGame
                ? Lang.F("schedule.exclusive.global", Lang.T(display.IsolationOn ? "schedule.on" : "schedule.off"),
                    CpuTopology.DescribeMask(display.IsolationMask))
                : !CoreScheduling.IsolationSupported ? Lang.T("schedule.error.isolationunsupported")
                : exclusive == 0 ? ExclusiveBlockedText()
                : display.IsolationOn ? Lang.F("schedule.exclusive.on", CpuTopology.DescribeMask(display.IsolationMask))
                : Lang.T("schedule.exclusive.off");
            string validation = CoreScheduling.Validate(Draft);
            bool dirty = Draft.Encode() != baseline.Encode() || FollowToggle.Checked != savedFollow;
            bool globalChanged = globalVersion != CoreScheduling.GlobalToken()
                || perGame && profileVersion != CoreScheduling.ProfileToken(readProfile());
            SaveButton.Enabled = dirty && (follow || validation == null) && !globalChanged && globalVersion != "unreadable";
            string state = globalChanged ? Lang.T("schedule.error.changed")
                : lastMessage ?? (validation != null && !follow ? Lang.T(validation)
                : Lang.T(dirty ? (perGame ? "schedule.dirty" : "schedule.dirty.global")
                    : active() ? "schedule.stored.active" : "schedule.stored"));
            string prerequisite = CoreScheduling.PrerequisiteText(perGame ? readProfile() : null);
            string runtime = Lang.T(CoreIsolationClient.State);
            if (CoreIsolationClient.State == "schedule.isolation.active")
                runtime += " " + CpuTopology.DescribeMask(CoreIsolationClient.ActiveMask);
            status.Text = state + "\r\n" + runtime + (prerequisite.Length == 0 ? "" : "\r\n" + prerequisite);
            status.ForeColor = globalChanged || lastMessage != null && !lastMessage.StartsWith(Lang.T("schedule.saved.prefix"))
                || CoreIsolationClient.State == "schedule.isolation.pending" || CoreIsolationClient.State == "schedule.isolation.failed"
                || validation != null && !follow ? Theme.Danger : Theme.Dim;
            bool hardOn = HardAffinityState != null && HardAffinityState();
            syncing = true; HardAffinityToggle.SetSilently(hardOn); syncing = false;
            HardAffinityToggle.Enabled = !perGame && display.IsolationOn;
            hardCard.Desc = Lang.T(!display.IsolationOn ? "schedule.hardaffinity.needsexclusive"
                : hardOn ? "schedule.hardaffinity.on" : "schedule.hardaffinity.off");
            LayoutContent();
        }

        // 说清为什么推不出来 以及照着做什么 光说需要一颗核在全选时会让人莫名其妙
        private string ExclusiveBlockedText()
        {
            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            ulong candidate = CoreScheduling.WholeCores(Draft.GameMask, cores);
            if (candidate == 0) return Lang.T("schedule.exclusive.needcore");
            int spare = CoreScheduling.SpareCoresOutside(candidate, cores);
            return Lang.F("schedule.exclusive.needspare", cores.Length, cores.Length - spare, 2 - spare);
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
            foreach (RoundPanel card in new RoundPanel[] { followCard, gameCard, exclusiveCard, hardCard, footer })
            {
                card.BackColor = Theme.Bg; card.Fill = Theme.Card; card.Border = Theme.Stroke; card.Invalidate();
            }
            foreach (FxControl control in new FxControl[] { FollowToggle, IsolationToggle, SaveButton, reloadButton })
            { control.Bg = Theme.Card; control.Invalidate(); }
            foreach (PillButton button in shortcuts.Controls) { button.Bg = Theme.Card; button.Invalidate(); }
            summary.ForeColor = Theme.Dim;
            Matrix.BackColor = Theme.Card;
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
            exclusiveCard.Top = gameCard.Bottom + gap;
            exclusiveCard.Height = CardHeaderHeight(exclusiveCard, perGame ? 0 : IsolationToggle.Width);
            int afterExclusive = exclusiveCard.Bottom + gap;
            if (!perGame)
            {
                hardCard.Top = afterExclusive;
                hardCard.Height = CardHeaderHeight(hardCard, HardAffinityToggle.Width);
                afterExclusive = hardCard.Bottom + gap;
            }
            LayoutFooter(footer, status, SaveButton, reloadButton, afterExclusive);
            Height = footer.Bottom + Theme.S(4);
            ResumeLayout();
        }

        private void LayoutFooter(RoundPanel surface, Label label, PillButton saveButton, PillButton readButton, int top)
        {
            surface.Top = top;
            surface.Height = Math.Max(Theme.S(76), PlaceLabel(label, Theme.S(14), 48) + Theme.S(14));
            saveButton.Location = new Point(Width - Theme.S(18) - saveButton.Width, (surface.Height - saveButton.Height) / 2);
            readButton.Location = new Point(saveButton.Left - Theme.S(10) - readButton.Width, saveButton.Top);
        }
    }
}
