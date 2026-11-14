using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 每个游戏单独保存策略；依赖协同不使用清核。
    internal sealed class RenderAffinityDialog : Form
    {
        private static readonly SchedulingStrategy[] ModeStrategies =
            { SchedulingStrategy.GamePriority, SchedulingStrategy.RenderBoost, SchedulingStrategy.RenderCore, SchedulingStrategy.Orchestration, SchedulingStrategy.DependencyBoost, SchedulingStrategy.RenderCoreTimeCritical };

        private readonly Func<CriticalThreadRule, bool?, string> save;
        private readonly ulong all, allowed, reserved;
        private readonly ulong[] physical;
        private readonly bool trusted;
        private readonly string scopeError;
        private readonly PillButton[] modes;
        private readonly Label title, game, statusHint, placementHint, conflictHint, preview, resourceHint, sessionHint, error;
        private readonly Label choicesTitle, choicesHint, observationHint;
        // 缺少上局读数时隐藏观测说明。
        private readonly SchedulingBottleneckEvidence observation;
        private readonly StrategyDetails details;
        private readonly LibraryDialogButton apply, cancel, close;
        // 方案列表自身滚动，只露出固定行数；方案再增加时弹窗高度不变。
        private const int VisibleChoices = 4;
        private readonly DBPanel body, choiceList;
        private readonly FamilySuppressionSwitch clearCore;
        private readonly FamilySuppressionSwitch resources;
        private readonly bool initialResources;
        private int mode;
        private int layoutMode = -1;
        private bool arranging;
        private bool clearCoreRequested;
        private int footerTop;

        internal RenderAffinityDialog(GameProfile profile, CriticalThreadRule initial, Func<CriticalThreadRule, string> saveRule, string conflict = null, string status = null)
            : this(profile, initial, saveRule == null ? null : new Func<CriticalThreadRule, bool?, string>((rule, resource) => saveRule(rule)), conflict, status)
        {
            // 旧保存接口不支持资源设置。
            resources.Enabled = false;
        }

        internal RenderAffinityDialog(GameProfile profile, CriticalThreadRule initial, Func<CriticalThreadRule, bool?, string> saveRule, string conflict = null, string status = null,
            SchedulingBottleneckEvidence lastObservation = null)
        {
            if (profile == null || saveRule == null) throw new ArgumentNullException();
            save = saveRule;
            observation = lastObservation;
            Name = "RenderAffinityDialog";
            Text = Lang.T("lib.threads.title");
            AccessibleName = Text;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = MaximizeBox = false;
            DoubleBuffered = true;
            BackColor = Theme.Bg;
            ForeColor = Theme.Fg;
            Font = Theme.UI(9f, false);
            // 容量检查与保存共用范围，扣除其他功能预留的物理核。
            all = CpuTopology.AllMask;
            physical = CpuTopology.PhysicalCoreMasks();
            reserved = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, physical);
            trusted = IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled;
            CriticalThreadAllowedRange range = CriticalThreadAllowedRange.For(profile);
            allowed = range.Mask;
            scopeError = range.Error;
            string storedRaw = CriticalThreadRule.Raw(profile);
            CriticalThreadRule stored = CriticalThreadRule.Parse(storedRaw);
            bool legacyLayout = initial != null && !initial.IsUnified || stored != null && !stored.IsUnified;
            bool legacyManual = CriticalThreadRule.IsLegacyManual(storedRaw);
            bool retiredComparison = IsRetiredComparison(storedRaw) || initial != null && IsRetiredComparison(initial.Encode());
            if (initial != null) initial = CriticalThreadRule.ParseForRuntime(initial.Encode());
            mode = retiredComparison || initial == null || initial.IsOff ? 0 : Math.Max(0, Array.IndexOf(ModeStrategies, initial.Strategy));

            title = LabelOf(Text, Theme.UI(17f, true), Theme.Fg);
            title.Name = "RenderAffinityTitle";
            game = LabelOf(profile.Name, Theme.UI(9f, true), Theme.Dim);
            game.Name = "RenderAffinityGame";
            game.TextAlign = ContentAlignment.MiddleCenter;
            game.AutoEllipsis = true;
            statusHint = LabelOf(status ?? "", Theme.UI(9f, false), Theme.Warning);
            statusHint.Name = "RenderAffinityStatus";
            close = new LibraryDialogButton("×", false)
            {
                Name = "RenderAffinityClose", TabIndex = 10,
                AccessibleName = T("关闭窗口，不保存", "Close without saving"),
                DialogResult = DialogResult.Cancel
            };
            close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            body = new DBPanel { Name = "RenderAffinityBody", BackColor = Theme.Bg, AutoScroll = true, TabIndex = 0 };
            Native.Dark(body);
            choicesTitle = LabelOf(T("调度方案", "Scheduling strategy"), Theme.UI(11f, true), Theme.Fg);
            choicesTitle.Name = "RenderAffinityChoicesTitle";
            choicesHint = LabelOf(T("选择一项", "Choose one"), Theme.UI(8.5f, false), Theme.Dim);
            choicesHint.Name = "RenderAffinityChoicesHint";
            choicesHint.TextAlign = ContentAlignment.MiddleRight;
            choiceList = new DBPanel { Name = "RenderAffinityChoices", BackColor = Theme.Bg, AutoScroll = true, TabIndex = 0,
                AccessibleRole = AccessibleRole.Grouping, AccessibleName = choicesTitle.Text };
            Native.Dark(choiceList);
            modes = new PillButton[ModeStrategies.Length];
            for (int i = 0; i < modes.Length; i++)
            {
                int selectedMode = i;
                modes[i] = new StrategyChoice(StrategyCaption(ModeStrategies[i]), ModeStrategies[i]) { Name = "RenderAffinityMode" + i, TabIndex = i };
                modes[i].Click += delegate { SelectMode(selectedMode, false); };
                ((StrategyChoice)modes[i]).Navigate = delegate(Keys key)
                {
                    SelectMode(key == Keys.Home ? 0 : key == Keys.End ? modes.Length - 1 : selectedMode + (key == Keys.Up ? -1 : 1), true);
                };
                choiceList.Controls.Add(modes[i]);
            }
            placementHint = LabelOf("", Theme.UI(9f, false), Theme.Dim);
            placementHint.Name = "RenderAffinityStrategyHint";
            details = new StrategyDetails(placementHint) { Name = "RenderAffinityDetails" };
            conflictHint = LabelOf(string.IsNullOrEmpty(conflict) ? "" : Lang.T(conflict), Theme.UI(9f, false), Theme.Warning);
            if (legacyLayout) conflictHint.Text += (conflictHint.Text.Length == 0 ? "" : "\n") + T(
                "保存后使用所选策略。",
                "Save to use the selected strategy.");
            if (legacyManual) conflictHint.Text += (conflictHint.Text.Length == 0 ? "" : "\n") + T(
                "原手动规则改为自动选核，清核选择保留。",
                "The old manual rule now picks its core automatically; clear core is kept.");
            if (retiredComparison) conflictHint.Text += (conflictHint.Text.Length == 0 ? "" : "\n") + T(
                "旧对照已停用，保存策略后用外层开关开启。",
                "Old comparison is off. Save a strategy, then enable the outer switch.");
            observationHint = LabelOf("", Theme.UI(9f, false), Theme.Dim);
            observationHint.Name = "RenderAffinityObservation";
            // 切换到依赖协同时保留清核草案，切回后还原勾选。
            clearCoreRequested = initial != null && initial.ClearCore;
            clearCore = new FamilySuppressionSwitch
            {
                Name = "RenderAffinityClearCore", Surface = Theme.Bg, TabIndex = ModeStrategies.Length, IconKey = "core",
                Text = T("清核", "Clear core"),
                Summary = T("其他游戏线程避开关键核心", "Make room for critical threads"),
                Checked = clearCoreRequested,
                AccessibleName = T("清核", "Clear core")
            };
            clearCore.Click += delegate { if (clearCore.Enabled) { clearCoreRequested = !clearCore.Checked; RefreshChoice(); } };
            preview = LabelOf("", Theme.UI(9f, false), Theme.Dim);
            preview.Name = "RenderAffinityPreview";
            initialResources = PolicyResolver.For(profile).BoostGame;
            resources = new FamilySuppressionSwitch
            {
                Name = "RenderAffinityResources", Surface = Theme.Bg, TabIndex = ModeStrategies.Length + 1,
                Text = Lang.T("gm.scheduling.resources.title"), Checked = initialResources,
                Summary = T("GPU 与磁盘调度", "GPU and disk scheduling"),
                AccessibleName = Lang.T("gm.scheduling.resources.title"), AccessibleDescription = Lang.T("gm.scheduling.resources.detail")
            };
            resources.Click += delegate { resources.Checked = !resources.Checked; UpdateAccessibleDescription(); };
            resourceHint = LabelOf(T("需开启渲染线程调度，下局生效。", "Requires render thread scheduling; applies next session."), Theme.UI(9f, false), Theme.Dim);
            resourceHint.Name = "RenderAffinityResourceHint";
            sessionHint = LabelOf(T("保存后下局生效", "Saved changes apply next session"), Theme.UI(9f, false), Theme.Dim);
            error = LabelOf("", Theme.UI(9f, false), Theme.Warning);
            error.Name = "RenderAffinityError";
            cancel = new LibraryDialogButton(T("取消", "Cancel"), false) { Name = "RenderAffinityCancel", DialogResult = DialogResult.Cancel, TabIndex = 8 };
            apply = new LibraryDialogButton(T("保存", "Save"), true) { Name = "RenderAffinitySave", TabIndex = 9 };
            apply.Click += delegate { SaveChoice(); };
            Controls.AddRange(new Control[] { title, game, statusHint, close, body, sessionHint, error, cancel, apply });
            body.Controls.AddRange(new Control[] { choicesTitle, choicesHint, choiceList, details, conflictHint, observationHint, clearCore, preview, resources, resourceHint });
            AcceptButton = apply;
            CancelButton = cancel;
            RefreshChoice();
        }

        private static bool IsRetiredComparison(string raw)
        { return raw == "7|compare|original" || raw == "7|compare|1" || raw == "7|compare|2"; }

        private static string T(string zh, string en) { return PerformancePolicy.Text(zh, en); }

        private static string StrategyCaption(SchedulingStrategy strategy)
        {
            return ThreadLayoutDescriptions.StrategyName(strategy);
        }

        private static string ChoiceSummary(SchedulingStrategy strategy)
        {
            return strategy == SchedulingStrategy.GamePriority ? T("整个游戏优先，渲染线程固定核心并提优", "Prioritize the whole game; pin and prioritize rendering")
                : strategy == SchedulingStrategy.RenderBoost ? T("优先安排渲染线程", "Prioritize rendering")
                : strategy == SchedulingStrategy.RenderCore ? T("固定渲染核心，减少换核", "Fix the render core to reduce migration")
                : strategy == SchedulingStrategy.DependencyBoost ? T("渲染等待关联任务完成", "Rendering waits for related tasks")
                : strategy == SchedulingStrategy.RenderCoreTimeCritical ? T("提高固定核心上的渲染优先级", "Raise render priority on a fixed core")
                : T("分散关键任务，减少同核竞争", "Separate critical tasks across cores");
        }

        private static string CompactStrategyHint(SchedulingStrategy strategy)
        {
            return strategy == SchedulingStrategy.GamePriority
                ? T("提高整个游戏进程的优先级，同时固定并优先安排渲染线程。CPU 满载时其他程序可能变慢。", "Raises the whole game process priority, and pins and prioritizes the render thread. Other apps may slow down when the CPU is saturated.")
                : strategy == SchedulingStrategy.RenderBoost
                ? T("优先安排已识别的渲染线程。不开清核时保留原有核心分配。", "Prioritizes the identified render thread. Keeps CPU placement with clear core off.")
                : strategy == SchedulingStrategy.RenderCore
                ? T("固定渲染核心以减少换核开销，清核可进一步减少游戏线程竞争。", "Fixes the render core to reduce migration. Clear core can further reduce game-thread contention.")
                : strategy == SchedulingStrategy.DependencyBoost
                ? T("同时提高渲染与已识别关联任务的优先级，保留原有核心分配。", "Prioritizes rendering and identified dependencies, keeping existing CPU placement.")
                : strategy == SchedulingStrategy.RenderCoreTimeCritical
                ? T("在固定核心上进一步提高渲染优先级，须通过运行条件核验。", "Raises rendering priority further on a fixed core; requires verified operating conditions.")
                : T("将已识别的关键任务分配到不同核心，为其他任务保留并行空间。", "Separates identified critical tasks across cores, leaving room for other work.");
        }

        private sealed class StrategyChoice : PillButton
        {
            private sealed class ChoiceAccessibility : ControlAccessibleObject
            {
                private readonly StrategyChoice owner;
                internal ChoiceAccessibility(StrategyChoice choice) : base(choice) { owner = choice; }
                public override AccessibleStates State
                {
                    get { return base.State | AccessibleStates.Selectable | (owner.Kind == BtnKind.Primary ? AccessibleStates.Checked | AccessibleStates.Selected : 0); }
                }
                public override string DefaultAction { get { return T("选择", "Select"); } }
                public override void DoDefaultAction() { owner.PerformClick(); }
            }

            internal readonly SchedulingStrategy Strategy;
            internal Action<Keys> Navigate;

            internal StrategyChoice(string caption, SchedulingStrategy strategy) : base(caption)
            {
                Strategy = strategy;
                Font = Theme.UI(10.5f, true);
                AccessibleRole = AccessibleRole.RadioButton;
                AccessibleDescription = ChoiceSummary(strategy) + " " + ThreadLayoutDescriptions.StrategyHint(strategy);
            }

            internal Rectangle IconBounds
            {
                get { return new Rectangle(Theme.S(16), (Height - Theme.S(32)) / 2, Theme.S(32), Theme.S(32)); }
            }
            internal Rectangle RadioBounds
            {
                get { return new Rectangle(Width - Theme.S(34), (Height - Theme.S(18)) / 2, Theme.S(18), Theme.S(18)); }
            }
            private int TextLeft { get { return IconBounds.Right + Theme.S(22); } }
            private int TextWidth { get { return Math.Max(1, RadioBounds.Left - Theme.S(14) - TextLeft); } }
            internal Rectangle CaptionBounds
            {
                get
                {
                    int captionHeight = TextHeight(Text, Font, TextWidth);
                    int summaryHeight = TextHeight(ChoiceSummary(Strategy), Theme.UI(8.5f, false), TextWidth);
                    return new Rectangle(TextLeft, (Height - captionHeight - summaryHeight - Theme.S(4)) / 2, TextWidth, captionHeight);
                }
            }
            internal Rectangle SummaryBounds
            {
                get { return new Rectangle(TextLeft, CaptionBounds.Bottom + Theme.S(4), TextWidth, TextHeight(ChoiceSummary(Strategy), Theme.UI(8.5f, false), TextWidth)); }
            }
            internal int MinimumHeight(int width)
            {
                int textWidth = Math.Max(1, width - Theme.S(118));
                return Math.Max(Theme.S(68), TextHeight(Text, Font, textWidth) + TextHeight(ChoiceSummary(Strategy), Theme.UI(8.5f, false), textWidth) + Theme.S(28));
            }
            internal void RefreshSelection()
            {
                if (IsHandleCreated) AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
                Invalidate();
            }
            protected override AccessibleObject CreateAccessibilityInstance() { return new ChoiceAccessibility(this); }
            protected override bool IsInputKey(Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.Modifiers == Keys.None && Navigate != null && (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End))
                {
                    e.Handled = e.SuppressKeyPress = true;
                    Navigate(e.KeyCode);
                    return;
                }
                base.OnKeyDown(e);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics graphics = e.Graphics;
                FillBg(graphics);
                if (Width < 2 || Height < 2) return;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                bool selected = Kind == BtnKind.Primary;
                Color fill = selected ? Col.Lerp(Theme.Card, Theme.Accent, Theme.LightMode ? 0.06f : 0.11f)
                    : Col.Lerp(Theme.Card, Theme.CardHover, hover.Value);
                Color border = selected ? Theme.Accent : Col.Lerp(Theme.Card, Theme.Stroke, 0.75f);
                Rectangle face = new Rectangle(0, 0, Width - 1, Height - 1);
                DsPaint.TechFrame(graphics, face, Theme.S(7), fill, border);
                if (selected)
                {
                    using (Pen edge = new Pen(Theme.Accent, Math.Max(1f, Theme.S(3))))
                        graphics.DrawLine(edge, Theme.S(2), Theme.S(10), Theme.S(2), Height - Theme.S(10));
                }
                Rectangle icon = IconBounds;
                DrawStrategyIcon(graphics, icon, Strategy, selected ? Theme.Accent : Theme.Fg);
                using (Pen divider = new Pen(Col.Lerp(fill, Theme.Stroke, 0.8f)))
                    graphics.DrawLine(divider, icon.Right + Theme.S(10), Height / 2 - Theme.S(20), icon.Right + Theme.S(10), Height / 2 + Theme.S(20));
                TextRenderer.DrawText(graphics, Text, Font, CaptionBounds, Theme.Fg, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, ChoiceSummary(Strategy), Theme.UI(8.5f, false), SummaryBounds, Theme.Dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                Rectangle radio = RadioBounds;
                using (SolidBrush brush = new SolidBrush(selected ? Theme.Accent : Theme.Card)) graphics.FillEllipse(brush, radio);
                using (Pen pen = new Pen(selected ? Theme.Accent : Theme.StrokeHi, Math.Max(1f, Dpi.Scale))) graphics.DrawEllipse(pen, radio);
                if (selected)
                {
                    using (Pen check = new Pen(Theme.OnAccent, Math.Max(1f, Dpi.Scale * 1.5f)))
                    {
                        check.StartCap = check.EndCap = LineCap.Round;
                        graphics.DrawLines(check, new Point[] {
                            new Point(radio.Left + Theme.S(4), radio.Top + Theme.S(9)),
                            new Point(radio.Left + Theme.S(8), radio.Top + Theme.S(12)),
                            new Point(radio.Left + Theme.S(14), radio.Top + Theme.S(6)) });
                    }
                }
                PaintBusy(graphics, face, null);
                if (WantsFocusCue) DsFocus.Draw(graphics, DsFocus.Inset(ClientRectangle, 5), Theme.Accent, fill);
            }
        }

        private sealed class StrategyDetails : DBPanel
        {
            private readonly Label eyebrow, heading, summary;
            private readonly Label[] factTitles = new Label[3], factDescriptions = new Label[3];
            private readonly Rectangle[] icons = new Rectangle[3];
            private readonly Rectangle[] factRows = new Rectangle[3];
            private int dividerTop;

            internal StrategyDetails(Label strategySummary)
            {
                BackColor = Theme.Bg;
                TabStop = false;
                AccessibleRole = AccessibleRole.Grouping;
                eyebrow = LabelOf(T("方案说明", "Strategy details"), Theme.UI(9f, false), Theme.Dim);
                eyebrow.Name = "RenderAffinitySelectedLabel";
                heading = LabelOf("", Theme.UI(13f, true), Theme.Fg);
                heading.Name = "RenderAffinitySelectedTitle";
                summary = strategySummary;
                Controls.AddRange(new Control[] { eyebrow, heading, summary });
                for (int i = 0; i < 3; i++)
                {
                    factTitles[i] = LabelOf("", Theme.UI(9.5f, true), Theme.Fg);
                    factTitles[i].Name = "RenderAffinityFactTitle" + i;
                    factDescriptions[i] = LabelOf("", Theme.UI(8.5f, false), Theme.Dim);
                    factDescriptions[i].Name = "RenderAffinityFactDescription" + i;
                    Controls.Add(factTitles[i]); Controls.Add(factDescriptions[i]);
                }
            }

            private static string[] Facts(SchedulingStrategy choice)
            {
                if (choice == SchedulingStrategy.GamePriority) return new string[] {
                    T("适用情况", "Use case"), T("游戏与其他程序争用 CPU", "The game competes with other apps for CPU"),
                    T("调整内容", "Changes"), T("提高游戏进程优先级，固定并提优渲染线程", "Raises game process priority; pins and prioritizes rendering"),
                    T("使用限制", "Limits"), T("CPU 满载时其他程序可能变慢", "Other apps may slow when the CPU is saturated") };
                if (choice == SchedulingStrategy.RenderBoost) return new string[] {
                    T("适用情况", "Use case"), T("渲染线程等待 CPU", "Rendering waits for CPU time"),
                    T("调整内容", "Changes"), T("提高渲染线程优先级", "Raises render-thread priority"),
                    T("使用限制", "Limits"), T("GPU 满载时效果有限", "Limited when GPU-bound") };
                if (choice == SchedulingStrategy.RenderCore) return new string[] {
                    T("适用情况", "Use case"), T("渲染频繁换核", "Rendering migrates frequently"),
                    T("调整内容", "Changes"), T("固定渲染核心", "Fixes the render core"),
                    T("使用限制", "Limits"), T("固定核心不等于独占", "Fixed placement is not exclusive") };
                if (choice == SchedulingStrategy.DependencyBoost) return new string[] {
                    T("适用情况", "Use case"), T("渲染等待关联任务", "Rendering waits for dependencies"),
                    T("调整内容", "Changes"), T("提高已识别关联任务优先级", "Prioritizes identified dependencies"),
                    T("使用限制", "Limits"), T("不使用清核", "Clear core is unavailable") };
                if (choice == SchedulingStrategy.RenderCoreTimeCritical) return new string[] {
                    T("适用情况", "Use case"), T("固定核心后仍有调度等待", "Scheduling waits remain on a fixed core"),
                    T("调整内容", "Changes"), T("进一步提高渲染优先级", "Raises render priority further"),
                    T("使用限制", "Limits"), T("运行条件核验通过才生效", "Requires verified operating conditions") };
                return new string[] {
                    T("适用情况", "Use case"), T("关键任务集中在同一核心", "Critical tasks crowd one core"),
                    T("调整内容", "Changes"), T("为关键任务分配不同核心", "Assigns separate cores to critical tasks"),
                    T("使用限制", "Limits"), T("只调整已识别的任务", "Identified tasks only") };
            }

            internal void SetChoice(SchedulingStrategy choice)
            {
                heading.Text = StrategyCaption(choice);
                string[] facts = Facts(choice);
                for (int i = 0; i < 3; i++) { factTitles[i].Text = facts[i * 2]; factDescriptions[i].Text = facts[i * 2 + 1]; }
                AccessibleName = heading.Text;
                AccessibleDescription = CompactStrategyHint(choice) + " " + string.Join(" · ", facts);
                Invalidate();
            }

            private static int ContentHeight(int width, SchedulingStrategy choice)
            {
                int inner = Math.Max(1, width - Theme.S(36));
                int factWidth = Math.Max(1, inner - Theme.S(42));
                int height = Theme.S(18) + Theme.S(20) + Theme.S(8)
                    + TextHeight(StrategyCaption(choice), Theme.UI(13f, true), inner) + Theme.S(10)
                    + TextHeight(CompactStrategyHint(choice), Theme.UI(9f, false), inner) + Theme.S(20);
                string[] facts = Facts(choice);
                for (int i = 0; i < 3; i++)
                    height += Math.Max(Theme.S(42), TextHeight(facts[i * 2], Theme.UI(9.5f, true), factWidth)
                        + Theme.S(4) + TextHeight(facts[i * 2 + 1], Theme.UI(8.5f, false), factWidth)) + Theme.S(14);
                return height + Theme.S(8);
            }
            internal int MinimumHeight(int width)
            {
                int height = 0;
                foreach (SchedulingStrategy choice in ModeStrategies) height = Math.Max(height, ContentHeight(width, choice));
                return height;
            }
            internal void ArrangeContent()
            {
                int pad = Theme.S(18), inner = Math.Max(1, Width - pad * 2);
                eyebrow.SetBounds(pad, pad, inner, Theme.S(20));
                heading.SetBounds(pad, eyebrow.Bottom + Theme.S(8), inner, TextHeight(heading.Text, heading.Font, inner));
                summary.SetBounds(pad, heading.Bottom + Theme.S(10), inner, TextHeight(summary.Text, summary.Font, inner));
                dividerTop = summary.Bottom + Theme.S(10);
                int top = dividerTop;
                int textLeft = pad + Theme.S(42), textWidth = Math.Max(1, inner - Theme.S(42));
                int[] titleHeights = new int[3], descriptionHeights = new int[3], rowHeights = new int[3];
                int minimumHeight = 0;
                for (int i = 0; i < 3; i++)
                {
                    titleHeights[i] = TextHeight(factTitles[i].Text, factTitles[i].Font, textWidth);
                    descriptionHeights[i] = TextHeight(factDescriptions[i].Text, factDescriptions[i].Font, textWidth);
                    rowHeights[i] = Math.Max(Theme.S(42), titleHeights[i] + Theme.S(4) + descriptionHeights[i]) + Theme.S(14);
                    minimumHeight += rowHeights[i];
                }
                int extra = Math.Max(0, Height - pad - top - minimumHeight);
                for (int i = 0; i < 3; i++)
                {
                    int titleHeight = titleHeights[i], descriptionHeight = descriptionHeights[i];
                    int rowHeight = rowHeights[i] + extra / 3 + (i < extra % 3 ? 1 : 0);
                    factRows[i] = new Rectangle(pad, top, inner, rowHeight);
                    int textTop = top + (rowHeight - titleHeight - Theme.S(4) - descriptionHeight) / 2;
                    factTitles[i].SetBounds(textLeft, textTop, textWidth, titleHeight);
                    factDescriptions[i].SetBounds(textLeft, factTitles[i].Bottom + Theme.S(4), textWidth, descriptionHeight);
                    icons[i] = new Rectangle(pad, top + (rowHeight - Theme.S(28)) / 2, Theme.S(28), Theme.S(28));
                    top += rowHeight;
                }
            }
            protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); if (eyebrow != null) ArrangeContent(); }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Bg);
                if (Width < 2 || Height < 2) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DsPaint.TechFrame(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(8),
                    Col.Lerp(Theme.Bg, Theme.Card, 0.22f), Col.Lerp(Theme.Bg, Theme.Stroke, 0.8f));
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                ArrangeContent();
                using (Pen divider = new Pen(Col.Lerp(Theme.Bg, Theme.Stroke, 0.8f)))
                {
                    e.Graphics.DrawLine(divider, Theme.S(18), dividerTop, Width - Theme.S(18), dividerTop);
                    for (int i = 0; i < 3; i++)
                    {
                        if (i > 0) e.Graphics.DrawLine(divider, factRows[i].Left, factRows[i].Top, factRows[i].Right, factRows[i].Top);
                        DrawStrategyIcon(e.Graphics, icons[i], i == 0 ? SchedulingStrategy.RenderCore : i == 1 ? SchedulingStrategy.RenderCoreTimeCritical : SchedulingStrategy.DependencyBoost, Theme.Fg);
                    }
                }
            }
        }

        internal static void DrawStrategyIcon(Graphics graphics, Rectangle bounds, SchedulingStrategy strategy, Color ink)
        {
            if (graphics == null || bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SetClip(bounds, CombineMode.Intersect);
                graphics.TranslateTransform(bounds.Left, bounds.Top);
                graphics.ScaleTransform(bounds.Width / 32f, bounds.Height / 32f);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (Pen pen = new Pen(ink, 1.65f))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(22, ink)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    pen.LineJoin = LineJoin.Round;
                    if (strategy == SchedulingStrategy.GamePriority)
                    {
                        // 整组上移：左侧上箭头，右侧三条代表游戏的全部线程。
                        graphics.DrawLine(pen, 8, 27, 8, 6);
                        graphics.DrawLines(pen, new[] { new PointF(3, 11), new PointF(8, 5), new PointF(13, 11) });
                        for (int top = 5; top <= 21; top += 8)
                        {
                            graphics.FillRectangle(fill, 17, top, 12, 5);
                            graphics.DrawRectangle(pen, 17, top, 12, 5);
                        }
                    }
                    else if (strategy == SchedulingStrategy.RenderBoost)
                    {
                        graphics.FillRectangle(fill, 3, 5, 26, 18);
                        graphics.DrawRectangle(pen, 3, 5, 26, 18);
                        graphics.DrawLines(pen, new[] { new PointF(7, 18), new PointF(12, 13), new PointF(17, 16), new PointF(24, 9) });
                        graphics.DrawLines(pen, new[] { new PointF(19, 9), new PointF(24, 9), new PointF(24, 14) });
                        graphics.DrawLine(pen, 16, 23, 16, 28);
                        graphics.DrawLine(pen, 10, 28, 22, 28);
                    }
                    else if (strategy == SchedulingStrategy.RenderCore)
                    {
                        graphics.FillRectangle(fill, 7, 7, 18, 18);
                        graphics.DrawRectangle(pen, 7, 7, 18, 18);
                        graphics.DrawRectangle(pen, 12, 12, 8, 8);
                        for (int position = 11; position <= 21; position += 5)
                        {
                            graphics.DrawLine(pen, position, 3, position, 7);
                            graphics.DrawLine(pen, position, 25, position, 29);
                            graphics.DrawLine(pen, 3, position, 7, position);
                            graphics.DrawLine(pen, 25, position, 29, position);
                        }
                    }
                    else if (strategy == SchedulingStrategy.RenderCoreTimeCritical)
                    {
                        graphics.FillRectangle(fill, 5, 5, 19, 19);
                        graphics.DrawRectangle(pen, 5, 5, 19, 19);
                        graphics.DrawLine(pen, 10, 1, 10, 5);
                        graphics.DrawLine(pen, 18, 1, 18, 5);
                        graphics.DrawLine(pen, 1, 10, 5, 10);
                        graphics.DrawLine(pen, 1, 18, 5, 18);
                        graphics.FillEllipse(fill, 13, 13, 16, 16);
                        graphics.DrawEllipse(pen, 13, 13, 16, 16);
                        graphics.DrawLines(pen, new[] { new PointF(21, 16), new PointF(21, 21), new PointF(25, 23) });
                    }
                    else if (strategy == SchedulingStrategy.DependencyBoost)
                    {
                        graphics.DrawLine(pen, 9, 22, 16, 12);
                        graphics.DrawLine(pen, 23, 22, 16, 12);
                        graphics.FillEllipse(fill, 3, 20, 9, 9);
                        graphics.FillEllipse(fill, 20, 20, 9, 9);
                        graphics.DrawEllipse(pen, 3, 20, 9, 9);
                        graphics.DrawEllipse(pen, 20, 20, 9, 9);
                        graphics.DrawLines(pen, new[] { new PointF(10, 10), new PointF(16, 4), new PointF(22, 10) });
                        graphics.DrawLine(pen, 16, 4, 16, 17);
                    }
                    else
                    {
                        graphics.FillRectangle(fill, 4, 5, 14, 14);
                        graphics.FillRectangle(fill, 20, 20, 9, 9);
                        graphics.DrawRectangle(pen, 4, 5, 14, 14);
                        graphics.DrawRectangle(pen, 8, 9, 6, 6);
                        graphics.DrawRectangle(pen, 20, 20, 9, 9);
                        graphics.DrawLines(pen, new[] { new PointF(18, 12), new PointF(25, 12), new PointF(25, 20) });
                        graphics.DrawLines(pen, new[] { new PointF(11, 19), new PointF(11, 25), new PointF(20, 25) });
                        for (int position = 8; position <= 14; position += 6)
                        {
                            graphics.DrawLine(pen, position, 2, position, 5);
                            graphics.DrawLine(pen, 1, position + 1, 4, position + 1);
                        }
                    }
                }
            }
            finally { graphics.Restore(state); }
        }
        private static Label LabelOf(string text, Font font, Color ink)
        {
            return new Label { Text = text, Font = font, ForeColor = ink, BackColor = Color.Transparent, UseMnemonic = false, UseCompatibleTextRendering = false };
        }
        private static int Measure(Label label, int width)
        {
            return TextRenderer.MeasureText(label.Text, label.Font, new Size(Math.Max(1, width), int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height + Theme.S(4);
        }
        private static int TextHeight(string text, Font font, int width)
        {
            return Math.Max(Theme.S(18), TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Height);
        }
        private bool FormalMode { get { return mode >= 0; } }
        private SchedulingStrategy SelectedStrategy { get { return ModeStrategies[Math.Max(0, Math.Min(mode, ModeStrategies.Length - 1))]; } }

        private CriticalThreadRule Draft(out string failure)
        {
            failure = null;
            if (!FormalMode)
            { failure = T("请选择一种调度策略。", "Choose a scheduling strategy."); return null; }
            CriticalThreadRule rule = CriticalThreadRule.UnifiedStrategy(SelectedStrategy, clearCore != null && clearCore.Checked);
            if (rule.ClearCore) failure = scopeError ?? rule.ClearCoreCapacityError(all, physical, allowed, reserved, trusted);
            return rule;
        }
        private void SelectMode(int index, bool moveFocus)
        {
            mode = Math.Max(0, Math.Min(modes.Length - 1, index));
            RefreshChoice();
            if (moveFocus) modes[mode].Focus();
            RevealChoice();
            body.ScrollControlIntoView(modes[mode]);
        }
        // 只把选中项滚到完整可见，其余位置不动。子控件的 Top 已扣除滚动量，先换回内容坐标再比较。
        private void RevealChoice()
        {
            int scroll = Math.Max(0, -choiceList.AutoScrollPosition.Y);
            int top = modes[mode].Top + scroll, bottom = top + modes[mode].Height, view = choiceList.ClientSize.Height;
            if (top < scroll) scroll = top;
            else if (bottom > scroll + view) scroll = bottom - view;
            choiceList.AutoScrollPosition = new Point(0, Math.Max(0, scroll));
        }
        private void RefreshChoice()
        {
            for (int i = 0; i < modes.Length; i++) { modes[i].Kind = i == mode ? BtnKind.Primary : BtnKind.Normal; ((StrategyChoice)modes[i]).RefreshSelection(); }
            clearCore.Enabled = FormalMode && SelectedStrategy != SchedulingStrategy.DependencyBoost;
            clearCore.Checked = clearCore.Enabled && clearCoreRequested;
            clearCore.Summary = clearCore.Enabled ? T("其他游戏线程避开关键核心", "Make room for critical threads")
                : T("此模式保留原范围，不使用清核", "This mode keeps CPU ranges; clear core is unavailable");
            string failure;
            Draft(out failure);
            error.Text = failure == null ? "" : Lang.T(failure);
            apply.Enabled = failure == null;
            placementHint.Text = FormalMode ? CompactStrategyHint(SelectedStrategy) : T(
                "请选择调度策略。",
                "Choose a scheduling strategy.");
            details.SetChoice(SelectedStrategy);
            observationHint.Text = observation == null || !FormalMode ? "" : observation.Hint(SelectedStrategy);
            preview.Text = !FormalMode ? "" : SelectedStrategy == SchedulingStrategy.DependencyBoost
                ? T("兼顾已识别的关联任务，保留游戏原有核心分配。", "Support identified related tasks while keeping the game's CPU placement.")
                : SelectedStrategy == SchedulingStrategy.RenderCoreTimeCritical
                ? T("清核可能影响多线程游戏；专核强化仅在运行条件核验通过后生效。", "Clear core may slow heavily threaded games. Enhanced mode requires verified operating conditions.")
                : T("清核可能影响多线程游戏；系统任务仍可使用这些核心。", "Clear core may slow heavily threaded games; system tasks can still use these cores.");
            clearCore.AccessibleDescription = FormalMode ? ThreadLayoutDescriptions.ClearCoreHint(SelectedStrategy, clearCore.Checked) : "";
            UpdateAccessibleDescription();
            clearCore.Invalidate();
            LayoutChoice();
        }
        private void UpdateAccessibleDescription()
        {
            AccessibleDescription = (FormalMode ? modes[mode].Text : T("请选择策略", "Choose a strategy")) + " " + statusHint.Text + " " + placementHint.Text + " " + conflictHint.Text
                + (observationHint.Text.Length == 0 ? "" : " " + observationHint.Text)
                + (FormalMode ? " " + clearCore.Text + (!clearCore.Enabled ? T(" 不适用", " unavailable") : clearCore.Checked ? T(" 开", " on") : T(" 关", " off")) : "")
                + " " + preview.Text + " " + resources.Text + (resources.Checked ? T(" 开", " on") : T(" 关", " off"))
                + " " + resourceHint.Text + " " + sessionHint.Text + " " + error.Text;
        }
        private void LayoutChoice()
        {
            if (arranging) return;
            arranging = true;
            try
            {
                bool modeChanged = layoutMode != mode;
                int scroll = modeChanged ? 0 : Math.Max(0, -body.AutoScrollPosition.Y);
                layoutMode = mode;
                Rectangle previousBounds = Bounds;
                Rectangle screen = Screen.FromControl(Owner ?? this).WorkingArea;
                int margin = Theme.S(16), pad = Theme.S(24), gap = Theme.S(8);
                int width = Math.Min(Theme.S(860), Math.Max(1, screen.Width - margin * 2));
                int inner = Math.Max(1, width - pad * 2);
                int maximumHeight = Math.Max(1, screen.Height - margin * 2);
                int closeWidth = Theme.S(32);
                int titleWidth = Math.Min(TextRenderer.MeasureText(title.Text, title.Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + Theme.S(4), Math.Max(1, inner - closeWidth - gap));
                title.SetBounds(pad, Theme.S(20), titleWidth, Math.Max(Theme.S(32), TextHeight(title.Text, title.Font, titleWidth)));
                close.SetBounds(width - pad - closeWidth, Theme.S(20), closeWidth, Theme.S(32));
                int gameLeft = title.Right + Theme.S(18);
                int gameRoom = close.Left - gap - gameLeft;
                bool inlineGame = gameRoom >= Theme.S(64);
                int gameWidth = Math.Min(Theme.S(250), TextRenderer.MeasureText(game.Text, game.Font, Size.Empty, TextFormatFlags.NoPadding).Width + Theme.S(16));
                game.SetBounds(inlineGame ? gameLeft : pad, inlineGame ? title.Top + Theme.S(4) : title.Bottom + Theme.S(6),
                    Math.Max(1, Math.Min(gameWidth, inlineGame ? gameRoom : inner)), Theme.S(24));
                int bodyTop = Math.Max(title.Bottom, game.Bottom) + Theme.S(20);
                statusHint.Visible = statusHint.Text.Length > 0;
                if (statusHint.Visible)
                {
                    statusHint.SetBounds(pad, bodyTop, inner, Measure(statusHint, inner));
                    bodyTop = statusHint.Bottom + gap;
                }
                error.Visible = error.Text.Length > 0;
                int errorHeight = error.Visible ? Measure(error, inner) : 0;
                int actionWidth = Math.Min(Theme.S(108), Math.Max(1, (inner - gap) / 2));
                bool inlineFooter = inner >= Theme.S(600);
                int hintWidth = Math.Max(1, inner - Theme.S(26) - (inlineFooter ? actionWidth * 2 + Theme.S(28) : 0));
                int hintHeight = Measure(sessionHint, hintWidth);
                int actionHeight = Theme.S(38);
                int footerRowHeight = inlineFooter ? Math.Max(hintHeight, actionHeight) : hintHeight + Theme.S(12) + actionHeight;
                int footerHeight = Theme.S(16) + Theme.S(18) + (error.Visible ? errorHeight + gap : 0) + footerRowHeight + pad;
                int bodyLimit = Math.Max(1, maximumHeight - bodyTop - footerHeight);
                body.SuspendLayout();
                try
                {
                    body.AutoScrollPosition = Point.Empty;
                    body.AutoScrollMinSize = Size.Empty;
                    body.HorizontalScroll.Enabled = false;
                    body.HorizontalScroll.Visible = false;
                    // 方案列表自身的滚动位置跨重排保留；先归零再摆放各行，摆完按原位置还原。
                    int listScroll = Math.Max(0, -choiceList.AutoScrollPosition.Y);
                    choiceList.AutoScrollPosition = Point.Empty;
                    choiceList.AutoScrollMinSize = Size.Empty;
                    choiceList.HorizontalScroll.Enabled = false;
                    choiceList.HorizontalScroll.Visible = false;
                    clearCore.Visible = FormalMode;
                    preview.Visible = preview.Text.Length > 0;
                    int contentHeight = LayoutBody(inner);
                    if (contentHeight > bodyLimit)
                        contentHeight = LayoutBody(Math.Max(1, inner - SystemInformation.VerticalScrollBarWidth - Theme.S(4)));
                    body.SetBounds(pad, bodyTop, inner, Math.Min(contentHeight, bodyLimit));
                    body.AutoScrollMinSize = new Size(0, contentHeight);
                    body.AutoScrollPosition = new Point(0, Math.Min(scroll, Math.Max(0, contentHeight - body.ClientSize.Height)));
                    choiceList.AutoScrollPosition = new Point(0, Math.Min(listScroll, Math.Max(0, choiceList.AutoScrollMinSize.Height - choiceList.ClientSize.Height)));
                    // 换了选中项（含首次打开）才把它滚到可见；只是重排时不动用户的滚动位置。
                    if (modeChanged) RevealChoice();
                    footerTop = body.Bottom + Theme.S(16);
                    int footerContentTop = footerTop + Theme.S(18);
                    error.SetBounds(pad, footerContentTop, inner, errorHeight);
                    if (error.Visible) footerContentTop = error.Bottom + gap;
                    int actionTop = inlineFooter ? footerContentTop + (footerRowHeight - actionHeight) / 2 : footerContentTop + hintHeight + Theme.S(12);
                    sessionHint.SetBounds(pad + Theme.S(26), inlineFooter ? footerContentTop + (footerRowHeight - hintHeight) / 2 : footerContentTop, hintWidth, hintHeight);
                    apply.SetBounds(width - pad - actionWidth, actionTop, actionWidth, actionHeight);
                    cancel.SetBounds(apply.Left - gap - actionWidth, actionTop, actionWidth, actionHeight);
                    ClientSize = new Size(width, Math.Min(maximumHeight, footerContentTop + footerRowHeight + pad));
                }
                finally { body.ResumeLayout(); }
                if (Visible && previousBounds.IntersectsWith(screen))
                {
                    int left = Left, top = Top;
                    if (modeChanged)
                    {
                        Rectangle anchor = Owner != null && !Owner.IsDisposed && Owner.Visible && Owner.WindowState != FormWindowState.Minimized ? Owner.Bounds : screen;
                        left = anchor.Left + (anchor.Width - Width) / 2;
                        top = anchor.Top + (anchor.Height - Height) / 2;
                    }
                    Location = new Point(Math.Max(screen.Left + margin, Math.Min(left, screen.Right - margin - Width)),
                        Math.Max(screen.Top + margin, Math.Min(top, screen.Bottom - margin - Height)));
                }
                Invalidate();
            }
            finally { arranging = false; }
        }
        private int LayoutBody(int width)
        {
            int gap = Theme.S(8), columnGap = Theme.S(20);
            bool columns = width >= Theme.S(730);
            int listWidth = columns ? (int)Math.Round((width - columnGap) * 0.62) : width;
            int detailWidth = columns ? width - listWidth - columnGap : width;
            // 列表只露出固定行数，其余方案在列表内滚动；需要滚动时每行让出滚动条的宽度。
            int visible = Math.Min(VisibleChoices, modes.Length);
            int rowWidth = modes.Length > visible ? Math.Max(1, listWidth - SystemInformation.VerticalScrollBarWidth - Theme.S(4)) : listWidth;
            int rowHeight = 0;
            foreach (StrategyChoice choice in modes) rowHeight = Math.Max(rowHeight, choice.MinimumHeight(rowWidth));
            int detailHeight = details.MinimumHeight(Math.Max(1, detailWidth));
            // 双栏时让露出的几行正好与右侧说明等高。
            if (columns) rowHeight = Math.Max(rowHeight, (int)Math.Ceiling((detailHeight - gap * (visible - 1)) / (double)visible));
            int listHeight = visible * rowHeight + (visible - 1) * gap;
            choicesTitle.SetBounds(0, 0, Math.Max(1, listWidth - Theme.S(100)), Theme.S(26));
            choicesHint.SetBounds(Math.Max(0, listWidth - Theme.S(100)), 0, Math.Min(listWidth, Theme.S(100)), Theme.S(26));
            int rowTop = Theme.S(34);
            choiceList.SetBounds(0, rowTop, listWidth, listHeight);
            for (int i = 0; i < modes.Length; i++) modes[i].SetBounds(0, i * (rowHeight + gap), rowWidth, rowHeight);
            choiceList.AutoScrollMinSize = new Size(0, modes.Length * rowHeight + (modes.Length - 1) * gap);
            int listBottom = choiceList.Bottom;
            details.SetBounds(columns ? listWidth + columnGap : 0, columns ? rowTop : listBottom + Theme.S(16),
                Math.Max(1, detailWidth), columns ? listHeight : detailHeight);
            details.ArrangeContent();
            int bottom = Math.Max(listBottom, details.Bottom);
            conflictHint.Visible = conflictHint.Text.Length > 0;
            if (conflictHint.Visible)
            {
                conflictHint.SetBounds(0, bottom + Theme.S(12), width, Measure(conflictHint, width));
                bottom = conflictHint.Bottom;
            }
            observationHint.Visible = observationHint.Text.Length > 0;
            if (observationHint.Visible)
            {
                observationHint.SetBounds(0, bottom + Theme.S(12), width, Measure(observationHint, width));
                bottom = observationHint.Bottom;
            }
            if (FormalMode)
            {
                clearCore.SetBounds(0, bottom + Theme.S(20), width, Theme.S(48));
                bottom = clearCore.Bottom;
            }
            if (preview.Visible)
            {
                preview.SetBounds(0, bottom + Theme.S(8), width, Measure(preview, width));
                bottom = preview.Bottom;
            }
            resources.SetBounds(0, bottom + Theme.S(16), width, Theme.S(48));
            resourceHint.SetBounds(0, resources.Bottom + Theme.S(8), width, Measure(resourceHint, width));
            bottom = resourceHint.Bottom;
            return bottom + Theme.S(4);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutChoice();
            // 构造阶段句柄尚未创建，滚动位置无法落地；显示后再把已保存的方案滚到可见。
            RevealChoice();
        }
        private void SaveChoice()
        {
            string failure;
            CriticalThreadRule rule = Draft(out failure);
            if (failure == null) failure = save(rule, resources.Checked == initialResources ? (bool?)null : resources.Checked);
            if (failure != null) { error.Text = Lang.T(failure); UpdateAccessibleDescription(); LayoutChoice(); return; }
            DialogResult = DialogResult.OK;
            Close();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (game.Visible)
            {
                Rectangle badge = Rectangle.Inflate(game.Bounds, Theme.S(3), Theme.S(1));
                using (GraphicsPath badgePath = Theme.TechPath(badge, Theme.S(3)))
                using (Pen border = new Pen(Theme.StrokeHi)) e.Graphics.DrawPath(border, badgePath);
            }
            using (Pen line = new Pen(Theme.Stroke)) e.Graphics.DrawLine(line, Theme.S(24), footerTop, Width - Theme.S(24), footerTop);
            if (sessionHint.Visible)
            {
                Rectangle info = new Rectangle(Theme.S(26), sessionHint.Top + (sessionHint.Height - Theme.S(16)) / 2, Theme.S(16), Theme.S(16));
                using (Pen pen = new Pen(Theme.Dim, Math.Max(1f, Dpi.Scale))) e.Graphics.DrawEllipse(pen, info);
                TextRenderer.DrawText(e.Graphics, "i", Theme.UI(8f, true), info, Theme.Dim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            }
            using (GraphicsPath path = Theme.TechPath(new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(12)))
            using (Pen pen = new Pen(Theme.StrokeHi)) e.Graphics.DrawPath(pen, path);
        }
    }
}
