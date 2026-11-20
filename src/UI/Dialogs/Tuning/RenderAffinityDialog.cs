using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace PaviseApp
{
    // 每个游戏单独保存渲染线程亲和性 自动（推荐） 渲染单核或手动选核
    // 优先级在深度调优里逐游戏设置 这里没有资源或提优开关
    internal sealed class RenderAffinityDialog : Form
    {
        // 第三项“手动”也落在渲染单核的固定路径上 用位置区分
        private static readonly SchedulingStrategy[] ModeStrategies =
            { SchedulingStrategy.Orchestration, SchedulingStrategy.RenderCore, SchedulingStrategy.RenderCore };
        private const int AutomaticMode = 0, SingleCoreMode = 1, ManualMode = 2;

        // 推荐标签只标在自动上 未保存模式时的缺省选中项也是它
        internal static bool IsRecommended(SchedulingStrategy strategy) { return strategy == CriticalThreadRule.RecommendedStrategy; }

        private readonly Func<CriticalThreadRule, bool?, string> save;
        private readonly ulong all, allowed, reserved;
        private readonly ulong[] physical;
        private readonly bool trusted;
        private readonly string scopeError;
        private readonly PillButton[] modes;
        private readonly Label title, game, statusHint, placementHint, conflictHint, preview, sessionHint, error;
        private readonly Label choicesTitle, choicesHint, observationHint, cpuTitle, cpuCount, cpuHelp;
        // 缺少上局读数时隐藏观测说明
        private readonly SchedulingBottleneckEvidence observation;
        private readonly StrategyDetails details;
        private readonly LibraryDialogButton apply, cancel, close;
        private const int VisibleChoices = 3;
        private readonly DBPanel body, choiceList;
        private readonly FamilySuppressionSwitch clearCore;
        // 手动模式的选核卡片 矩阵与核心调度页是同一个控件 按物理核分组 点核编号选整颗核
        // 不在游戏范围内或被其他调度占用的 CPU 画成不可选
        private readonly CpuPicker cpuCard;
        private readonly CoreMatrix cpuMatrix;
        private readonly CoreLegend cpuLegend;
        private readonly Label cpuReason;
        private readonly PillButton cpuSuggest, cpuClear;
        private readonly ulong cpuUsable;
        private bool cpuLimitHit;
        private ulong manualSelection;
        private int mode;
        private int layoutMode = -1;
        private bool arranging;
        private bool clearCoreRequested;
        private int footerTop;

        internal RenderAffinityDialog(GameProfile profile, CriticalThreadRule initial, Func<CriticalThreadRule, string> saveRule, string conflict = null, string status = null)
            : this(profile, initial, saveRule == null ? null : new Func<CriticalThreadRule, bool?, string>((rule, resource) => saveRule(rule)), conflict, status)
        {
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
            // 容量检查与保存共用范围 扣除其他功能预留的物理核
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
            bool retiredComparison = IsRetiredComparison(storedRaw) || initial != null && IsRetiredComparison(initial.Encode());
            bool retiredStrategy = stored != null && stored.IsRetiredStrategy || initial != null && initial.IsRetiredStrategy;
            // 旧的只调优先级方案（游戏优先 渲染优先 依赖协同）没有放置内容 运行时关闭
            CriticalThreadRule source = initial ?? stored;
            bool priorityOnly = source != null && source.IsUnified && !source.IsOff && !source.IsComparison
                && (source.IsGamePriority || source.IsDependencyBoost || source.IsRenderStrategy && !source.PinsRenderThread);
            if (initial != null) initial = CriticalThreadRule.ParseForRuntime(initial.Encode());
            // 缺省选中自动 已有模式按换算后的运行规则预选 手动规则在拓扑未变时预选原来的 CPU
            string stamp = ManualCorePlan.Stamp(all, physical);
            if (initial != null && initial.IsManualPin && initial.Topology == stamp) manualSelection = initial.ManualMask & allowed;
            mode = retiredComparison || retiredStrategy || initial == null || initial.IsOff ? AutomaticMode
                : initial.IsManualPin ? ManualMode : initial.PinsRenderThread ? SingleCoreMode : AutomaticMode;

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
            choicesTitle = LabelOf(T("亲和性模式", "Affinity mode"), Theme.UI(11f, true), Theme.Fg);
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
                modes[i] = new StrategyChoice(ModeCaption(i), ModeStrategies[i], i == ManualMode) { Name = "RenderAffinityMode" + i, TabIndex = i };
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
                "保存后使用所选模式。",
                "Save to use the selected mode.");
            if (priorityOnly) conflictHint.Text += (conflictHint.Text.Length == 0 ? "" : "\n") + T(
                "原方案只调优先级，已交给深度调优的逐游戏开关；选一种模式保存后生效。",
                "The old strategy only adjusted priority, which now comes from the per-game switches in Advanced tuning; choose a mode and save.");
            if (retiredComparison) conflictHint.Text += (conflictHint.Text.Length == 0 ? "" : "\n") + T(
                "旧对照已停用，保存模式后用外层开关开启。",
                "Old comparison is off. Save a mode, then enable the outer switch.");
            if (retiredStrategy) conflictHint.Text += (conflictHint.Text.Length == 0 ? "" : "\n") + T(
                "温和提优已下架，本游戏的亲和性暂按关闭处理；选择模式并保存后用外层开关开启。",
                "Gentle boost was removed, so affinity is off for this game. Choose and save a mode, then enable the outer switch.");
            if (initial != null && initial.IsManualPin && initial.Topology != stamp) conflictHint.Text += (conflictHint.Text.Length == 0 ? "" : "\n") + T(
                "处理器拓扑已变化，原来手动选的 CPU 作废，请重新选择。",
                "The processor topology changed, so the previously chosen CPUs no longer apply. Choose again.");
            observationHint = LabelOf("", Theme.UI(9f, false), Theme.Dim);
            observationHint.Name = "RenderAffinityObservation";
            clearCoreRequested = initial != null && initial.ClearCore && !initial.IsOrchestration;
            clearCore = new FamilySuppressionSwitch
            {
                Name = "RenderAffinityClearCore", Surface = Theme.Bg, TabIndex = ModeStrategies.Length + 1, IconKey = "core",
                Text = T("整核避让", "Whole-core avoidance"),
                Summary = T("其他游戏线程避开渲染线程所在的整颗物理核", "Other game threads stay off the render thread's physical core"),
                Checked = clearCoreRequested,
                AccessibleName = T("整核避让", "Whole-core avoidance")
            };
            clearCore.Click += delegate { if (clearCore.Enabled) { clearCoreRequested = !clearCore.Checked; RefreshChoice(); } };
            cpuUsable = trusted ? all & allowed & ~reserved : 0;
            cpuCard = new CpuPicker { Name = "RenderAffinityCpuCard", TabIndex = ModeStrategies.Length };
            cpuTitle = LabelOf(T("渲染线程使用的 CPU", "CPUs for the render thread"), Theme.UI(11f, true), Theme.Fg);
            cpuTitle.Name = "RenderAffinityCpuTitle";
            cpuCount = LabelOf("", Theme.UI(9f, false), Theme.Dim);
            cpuCount.Name = "RenderAffinityCpuCount";
            cpuCount.TextAlign = ContentAlignment.MiddleRight;
            // 核心范围受限时写明范围和可选数量 斜纹的格子不可选 点一下能看到原因
            bool rangeLimited = cpuUsable != all;
            cpuHelp = LabelOf(T("点数字选单个逻辑 CPU，点核编号选整颗核，最多四个。", "Click a number for one logical CPU, or a core label for the whole core. Up to four.")
                + (rangeLimited ? T("游戏核心范围：" + (scopeError == null && trusted ? CpuTopology.DescribeRanges(allowed) : "暂不可用") + "，当前可选 " + Bits(cpuUsable) + " 个；斜纹的不可选，点一下看原因。",
                    " Game CPU range: " + (scopeError == null && trusted ? CpuTopology.DescribeRanges(allowed) : "unavailable") + ", " + Bits(cpuUsable) + " available now; hatched CPUs are unavailable, click one to see why.") : ""),
                Theme.UI(9f, false), Theme.Dim);
            cpuHelp.Name = "RenderAffinityCpuHelp";
            cpuSuggest = new PillButton(T("推荐一颗核", "Suggest a core")) { Name = "RenderAffinityCpuSuggest", TabIndex = 0 };
            cpuSuggest.Enabled = SuggestedCore() != 0;
            cpuSuggest.Click += delegate { SetManualSelection(SuggestedCore()); };
            cpuClear = new PillButton(T("清空", "Clear")) { Name = "RenderAffinityCpuClear", TabIndex = 1 };
            cpuClear.Click += delegate { SetManualSelection(0); };
            cpuMatrix = new CoreMatrix
            {
                Name = "RenderAffinityCpus", Editable = cpuUsable != 0, MarkExclusive = false, ShowDisabledPattern = true,
                PrimaryTag = Lang.T("schedule.manual.selected"), BackColor = CpuPicker.Fill, TabIndex = 2,
                AccessibleName = cpuTitle.Text, DisabledMask = all & ~cpuUsable
            };
            // 不可选的 CPU 照常画出来（斜纹） 悬停和点击都说明原因 用户能看到核心调度里关掉了哪些核
            cpuMatrix.CpuHint = CpuExplanation;
            cpuMatrix.DisabledCpuClicked += delegate(int cpu)
            {
                cpuReason.Text = CpuExplanation(cpu);
                UpdateAccessibleDescription();
                LayoutChoice();
            };
            cpuMatrix.ShowSelectionImmediately(manualSelection);
            cpuMatrix.SelectionChanged += OnCpuSelection;
            cpuLegend = new CoreLegend(new[] { T("已选", "Selected"), T("可选", "Available"), T("不可选", "Unavailable") });
            cpuReason = LabelOf("", Theme.UI(9f, false), Theme.Warning);
            cpuReason.Name = "RenderAffinityCpuReason";
            cpuCard.Controls.AddRange(new Control[] { cpuTitle, cpuCount, cpuHelp, cpuSuggest, cpuClear, cpuLegend, cpuMatrix, cpuReason });
            preview = LabelOf("", Theme.UI(9f, false), Theme.Dim);
            preview.Name = "RenderAffinityPreview";
            sessionHint = LabelOf(T("保存后下局生效", "Saved changes apply next session"), Theme.UI(9f, false), Theme.Dim);
            error = LabelOf("", Theme.UI(9f, false), Theme.Warning);
            error.Name = "RenderAffinityError";
            cancel = new LibraryDialogButton(T("取消", "Cancel"), false) { Name = "RenderAffinityCancel", DialogResult = DialogResult.Cancel, TabIndex = 8 };
            apply = new LibraryDialogButton(T("保存", "Save"), true) { Name = "RenderAffinitySave", TabIndex = 9 };
            apply.Click += delegate { SaveChoice(); };
            Controls.AddRange(new Control[] { title, game, statusHint, close, body, sessionHint, error, cancel, apply });
            body.Controls.AddRange(new Control[] { choicesTitle, choicesHint, choiceList, details, conflictHint, observationHint, cpuCard, clearCore, preview });
            AcceptButton = apply;
            CancelButton = cancel;
            RefreshChoice();
        }

        private static bool IsRetiredComparison(string raw)
        { return raw == "7|compare|original" || raw == "7|compare|1" || raw == "7|compare|2"; }

        private static string T(string zh, string en) { return PerformancePolicy.Text(zh, en); }

        private static string ModeCaption(int index)
        {
            return index == ManualMode ? T("手动", "Manual") : ThreadLayoutDescriptions.StrategyName(ModeStrategies[index]);
        }

        private static string ChoiceSummary(SchedulingStrategy strategy, bool manual)
        {
            return manual ? T("渲染线程固定到你选的 CPU 上", "Pin the render thread to the CPUs you choose")
                : strategy == SchedulingStrategy.RenderCore ? T("渲染线程固定到最合适的一颗核", "Pin the render thread to the best core")
                : T("按实测给关键线程指定优先核心，不锁定", "Measure and point critical threads at preferred cores without locking them");
        }

        private static string CompactStrategyHint(SchedulingStrategy strategy, bool manual)
        {
            return manual ? ThreadLayoutDescriptions.ManualHint() : ThreadLayoutDescriptions.StrategyHint(strategy);
        }

        // 逻辑 CPU 的核型说明 大小核标性能核/能效核 缓存不对称时标大缓存组
        private static string CpuKind(ulong bit)
        {
            if (CpuTopology.Hybrid && (CpuTopology.PerfMask & bit) != 0) return T("性能核", "Performance core");
            if (CpuTopology.Hybrid && (CpuTopology.EffMask & bit) != 0) return T("能效核", "Efficiency core");
            if (CpuTopology.AsymCache && (CpuTopology.BigL3Mask & bit) != 0) return T("大三级缓存组", "Large L3 cache group");
            return T("可选", "Available");
        }

        // 推荐一颗核 和渲染单核同样的偏好 先首选范围（X3D 取大缓存组）和性能核 再避开 CPU 0 所在核
        // 只在可用范围里挑 选中这颗核里可用的全部逻辑 CPU 核心调度去掉了超线程时就只选剩下的那个线程
        private ulong SuggestedCore()
        {
            if (cpuUsable == 0) return 0;
            ulong preferred = CpuTopology.CriticalPlacementPreferredMask;
            ulong best = 0;
            int bestScore = int.MinValue;
            foreach (ulong core in physical)
            {
                ulong logical = core & all & cpuUsable;
                if (logical == 0 || Bits(logical) > CriticalThreadAutoPolicy.MaximumIsolated) continue;
                int score = (preferred != 0 && (logical & preferred) != 0 ? 4 : 0)
                    + (CpuTopology.Hybrid && (logical & CpuTopology.PerfMask) == logical ? 2 : 0)
                    + ((logical & 1UL) == 0 ? 1 : 0);
                if (score > bestScore) { bestScore = score; best = logical; }
            }
            return best;
        }

        // 逐个 CPU 的说明 悬停提示和点到不可选格子时的原因都用它
        private string CpuExplanation(int cpu)
        {
            string name = "CPU" + cpu.ToString(System.Globalization.CultureInfo.InvariantCulture) + T("：", ": ");
            ulong bit = 1UL << cpu;
            if (!trusted) return name + T("处理器拓扑或处理器组暂未确认，当前不能手动选核。", "CPU topology or processor groups are unconfirmed; manual selection is unavailable.");
            if (scopeError != null) return name + T("这款游戏的核心范围暂不可用：", "This game's CPU range is unavailable: ") + Lang.T(scopeError);
            if ((allowed & bit) == 0) return name + T("不在这款游戏的核心范围内（在游戏库深度调优的核心调度里关掉了），改了核心范围才能选。",
                "Outside this game's CPU range (turned off in core scheduling in Library advanced tuning); change the CPU range to use it.");
            if ((reserved & bit) != 0) return name + T("所在物理核正被其他功能占用，当前不可选。", "Its physical core is in use by another feature and cannot be chosen now.");
            if ((manualSelection & bit) != 0) return name + T("已选。", "Selected.");
            string kind = CpuKind(bit);
            return name + (kind == T("可选", "Available") ? T("可选。", "Available.") : kind + T("，可选。", ", available."));
        }

        private void SetManualSelection(ulong mask)
        {
            cpuReason.Text = "";
            manualSelection = mask & cpuUsable;
            cpuLimitHit = false;
            cpuMatrix.ShowSelectionImmediately(manualSelection);
            RefreshChoice(false);
        }

        // 超过四个时退回上一次的选择并提示到了上限 不悄悄截掉
        private void OnCpuSelection(ulong mask)
        {
            cpuReason.Text = "";
            mask &= cpuUsable;
            if (Bits(mask) > CriticalThreadAutoPolicy.MaximumIsolated)
            {
                cpuLimitHit = true;
                cpuMatrix.ShowSelectionImmediately(manualSelection);
            }
            else
            {
                cpuLimitHit = false;
                manualSelection = mask;
            }
            RefreshChoice(false);
        }

        // 选核图例 已选 可选 不可选三种格子的样子 和矩阵的画法一致（不可选是斜纹）
        private sealed class CoreLegend : Control
        {
            private readonly string[] captions;
            internal CoreLegend(string[] values)
            {
                captions = values;
                Name = "RenderAffinityLegend";
                AccessibleName = string.Join(" / ", values);
                BackColor = CpuPicker.Fill;
                TabStop = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            }
            private int ItemWidth(string caption)
            {
                return TextRenderer.MeasureText(caption, Theme.UI(8f, false), Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding).Width + Theme.S(34);
            }
            internal int PreferredWidth
            {
                get { int width = 0; foreach (string caption in captions) width += ItemWidth(caption); return width; }
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.Clear(BackColor);
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int x = 0;
                for (int i = 0; i < captions.Length; i++)
                {
                    Rectangle sample = new Rectangle(x + Theme.S(2), (Height - Theme.S(12)) / 2, Theme.S(12), Theme.S(12));
                    using (Brush brush = i == 0 ? (Brush)new SolidBrush(Theme.Accent)
                        : i == 1 ? (Brush)new SolidBrush(Theme.Card) : new HatchBrush(HatchStyle.ForwardDiagonal, Theme.Faint, Theme.Card))
                        e.Graphics.FillRectangle(brush, sample);
                    using (Pen pen = new Pen(i == 0 ? Theme.Accent : Theme.StrokeHi)) e.Graphics.DrawRectangle(pen, sample);
                    int width = ItemWidth(captions[i]);
                    TextRenderer.DrawText(e.Graphics, captions[i], Theme.UI(8f, false),
                        new Rectangle(sample.Right + Theme.S(6), 0, Math.Max(1, width - Theme.S(20)), Height),
                        Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
                    x += width;
                }
            }
        }

        // 选核卡片 和方案说明同样的切角框 里面的底色和矩阵一致
        private sealed class CpuPicker : DBPanel
        {
            internal static Color Fill { get { return Col.Lerp(Theme.Bg, Theme.Card, 0.22f); } }
            internal CpuPicker()
            {
                BackColor = Fill;
                AccessibleRole = AccessibleRole.Grouping;
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(Theme.Bg);
                if (Width < 2 || Height < 2) return;
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                DsPaint.TechFrame(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(8), Fill, Col.Lerp(Theme.Bg, Theme.Stroke, 0.8f));
            }
        }

        private static int Bits(ulong mask)
        { int count = 0; while (mask != 0) { mask &= mask - 1; count++; } return count; }

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
            internal readonly bool Manual;
            internal Action<Keys> Navigate;

            internal StrategyChoice(string caption, SchedulingStrategy strategy, bool manual) : base(caption)
            {
                Strategy = strategy;
                Manual = manual;
                Font = Theme.UI(10.5f, true);
                AccessibleRole = AccessibleRole.RadioButton;
                AccessibleDescription = (!manual && IsRecommended(strategy) ? RecommendedText + T("。", ". ") : "")
                    + ChoiceSummary(strategy, manual) + " " + CompactStrategyHint(strategy, manual);
            }

            private string ChoiceText { get { return ChoiceSummary(Strategy, Manual); } }

            internal static string RecommendedText { get { return T("推荐", "Recommended"); } }

            // 英文 Recommended 在高缩放下挤不进名称后面 放不下时依次换小一号的短词 都放不下才不画
            private static string[] RecommendedCandidates { get { return new[] { RecommendedText, T("推荐", "Suggested"), T("推荐", "Best") }; } }

            // 推荐标签紧跟方案名 放不下时不画 不挤压名称和摘要 绘制和测量共用这里选出的文字与字体
            private Rectangle RecommendedLayout(out string text, out Font font)
            {
                text = null; font = null;
                if (Manual || !IsRecommended(Strategy)) return Rectangle.Empty;
                Rectangle caption = CaptionBounds;
                TextFormatFlags flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                Size name = TextRenderer.MeasureText(Text, Font, new Size(int.MaxValue, int.MaxValue), flags);
                string[] candidates = RecommendedCandidates;
                for (int step = 0; step < candidates.Length; step++)
                {
                    bool compact = Tile || step > 0;
                    string candidate = candidates[step];
                    Font candidateFont = Theme.UI(compact ? 7.5f : 8f, true);
                    Size label = TextRenderer.MeasureText(candidate, candidateFont, new Size(int.MaxValue, int.MaxValue), flags);
                    int width = label.Width + Theme.S(compact ? 8 : 12), height = Math.Max(label.Height + Theme.S(4), Theme.S(18));
                    int left = caption.Left + name.Width + Theme.S(compact ? 6 : 8);
                    if (left + width > caption.Right) continue;
                    text = candidate; font = candidateFont;
                    return new Rectangle(left, caption.Top + Math.Max(0, (name.Height - height) / 2), width, height);
                }
                return Rectangle.Empty;
            }

            internal Rectangle RecommendedBounds
            {
                get { string text; Font font; return RecommendedLayout(out text, out font); }
            }

            // 并排小卡片 三个模式排成一行时用更小的边距 图标和名称字号 名称和摘要在中间 单选标记在右侧
            private bool tile;
            internal bool Tile
            {
                get { return tile; }
                set
                {
                    if (tile == value) return;
                    tile = value;
                    Font = Theme.UI(value ? 10f : 10.5f, true);
                }
            }

            internal Rectangle IconBounds
            {
                get
                {
                    int size = Tile ? Theme.S(24) : Theme.S(32);
                    return new Rectangle(Tile ? Theme.S(10) : Theme.S(16), (Height - size) / 2, size, size);
                }
            }
            internal Rectangle RadioBounds
            {
                get { return new Rectangle(Width - Theme.S(Tile ? 28 : 34), (Height - Theme.S(18)) / 2, Theme.S(18), Theme.S(18)); }
            }
            private int TextLeft { get { return IconBounds.Right + Theme.S(Tile ? 8 : 22); } }
            private int TextWidth { get { return Math.Max(1, RadioBounds.Left - Theme.S(Tile ? 6 : 14) - TextLeft); } }
            internal Rectangle CaptionBounds
            {
                get
                {
                    int captionHeight = TextHeight(Text, Font, TextWidth);
                    int summaryHeight = TextHeight(ChoiceText, Theme.UI(8.5f, false), TextWidth);
                    return new Rectangle(TextLeft, (Height - captionHeight - summaryHeight - Theme.S(4)) / 2, TextWidth, captionHeight);
                }
            }
            internal Rectangle SummaryBounds
            {
                get { return new Rectangle(TextLeft, CaptionBounds.Bottom + Theme.S(4), TextWidth, TextHeight(ChoiceText, Theme.UI(8.5f, false), TextWidth)); }
            }
            internal int MinimumHeight(int width)
            {
                int textWidth = Math.Max(1, width - Theme.S(Tile ? 76 : 118));
                return Math.Max(Theme.S(Tile ? 64 : 68), TextHeight(Text, Font, textWidth) + TextHeight(ChoiceText, Theme.UI(8.5f, false), textWidth) + Theme.S(Tile ? 26 : 28));
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
                return key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
            }
            protected override void OnKeyDown(KeyEventArgs e)
            {
                // 并排时左右键和上下键一样切换相邻模式
                Keys key = e.KeyCode == Keys.Left ? Keys.Up : e.KeyCode == Keys.Right ? Keys.Down : e.KeyCode;
                if (e.Modifiers == Keys.None && Navigate != null && (key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End))
                {
                    e.Handled = e.SuppressKeyPress = true;
                    Navigate(key);
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
                DrawStrategyIcon(graphics, icon, Manual ? SchedulingStrategy.RenderBoost : Strategy, selected ? Theme.Accent : Theme.Fg);
                if (!Tile)
                    using (Pen divider = new Pen(Col.Lerp(fill, Theme.Stroke, 0.8f)))
                        graphics.DrawLine(divider, icon.Right + Theme.S(10), Height / 2 - Theme.S(20), icon.Right + Theme.S(10), Height / 2 + Theme.S(20));
                TextRenderer.DrawText(graphics, Text, Font, CaptionBounds, Theme.Fg, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                string badgeText; Font badgeFont;
                Rectangle badge = RecommendedLayout(out badgeText, out badgeFont);
                if (!badge.IsEmpty)
                {
                    DsPaint.TechFrame(graphics, badge, Theme.S(3), Col.Lerp(fill, Theme.Accent, Theme.LightMode ? 0.10f : 0.18f), Theme.Accent);
                    TextRenderer.DrawText(graphics, badgeText, badgeFont, badge, Theme.Accent,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                }
                TextRenderer.DrawText(graphics, ChoiceText, Theme.UI(8.5f, false), SummaryBounds, Theme.Dim, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
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

            private static string[] Facts(int choice)
            {
                if (choice == ManualMode) return new string[] {
                    T("适用情况", "Use case"), T("你已知道哪颗核最适合渲染", "You know which core suits rendering best"),
                    T("调整内容", "Changes"), T("渲染线程只在所选 CPU 上运行", "The render thread runs only on the chosen CPUs"),
                    T("使用限制", "Limits"), T("换处理器或改游戏核心范围后需重选", "Choose again after changing the CPU or the game's range") };
                if (choice == SingleCoreMode) return new string[] {
                    T("适用情况", "Use case"), T("渲染线程频繁换核", "The render thread migrates frequently"),
                    T("调整内容", "Changes"), T("固定到最合适的一颗物理核", "Pins it to the most suitable physical core"),
                    T("使用限制", "Limits"), T("不开整核避让时不独占", "Not exclusive without whole-core avoidance") };
                return new string[] {
                    T("适用情况", "Use case"), T("CPU 瓶颈、1% low 偏低", "CPU-bound, with weak 1% lows"),
                    T("调整内容", "Changes"), T("关键线程优先在选定的物理核上运行，不锁定", "Critical threads prefer chosen cores without being locked"),
                    T("使用限制", "Limits"), T("余量不够时少放或不放", "Places fewer or none without headroom") };
            }

            internal void SetChoice(int choice)
            {
                heading.Text = ModeCaption(choice);
                string[] facts = Facts(choice);
                for (int i = 0; i < 3; i++) { factTitles[i].Text = facts[i * 2]; factDescriptions[i].Text = facts[i * 2 + 1]; }
                AccessibleName = heading.Text;
                AccessibleDescription = CompactStrategyHint(ModeStrategies[choice], choice == ManualMode) + " " + string.Join(" · ", facts);
                Invalidate();
            }

            // 说明区放在模式下方占满整宽 够宽时三条要点横排成三列 否则逐行竖排
            private static bool FactColumns(int width) { return width >= Theme.S(600); }
            private static int FactTextWidth(int inner, bool columns)
            {
                int column = columns ? (inner - Theme.S(16) * 2) / 3 : inner;
                return Math.Max(1, column - Theme.S(columns ? 38 : 42));
            }
            private static int FactHeight(string title, string description, int textWidth)
            {
                return Math.Max(Theme.S(42), TextHeight(title, Theme.UI(9.5f, true), textWidth)
                    + Theme.S(4) + TextHeight(description, Theme.UI(8.5f, false), textWidth)) + Theme.S(14);
            }
            private static int ContentHeight(int width, int choice)
            {
                int inner = Math.Max(1, width - Theme.S(36));
                bool columns = FactColumns(width);
                int textWidth = FactTextWidth(inner, columns);
                int height = Theme.S(18) + Theme.S(20) + Theme.S(8)
                    + TextHeight(ModeCaption(choice), Theme.UI(13f, true), inner) + Theme.S(10)
                    + TextHeight(CompactStrategyHint(ModeStrategies[choice], choice == ManualMode), Theme.UI(9f, false), inner) + Theme.S(10);
                string[] facts = Facts(choice);
                int tallest = 0;
                for (int i = 0; i < 3; i++)
                {
                    int fact = FactHeight(facts[i * 2], facts[i * 2 + 1], textWidth);
                    tallest = Math.Max(tallest, fact);
                    if (!columns) height += fact;
                }
                return height + (columns ? tallest + Theme.S(4) : 0) + Theme.S(8);
            }
            internal int MinimumHeight(int width)
            {
                int height = 0;
                for (int choice = 0; choice < ModeStrategies.Length; choice++) height = Math.Max(height, ContentHeight(width, choice));
                return height;
            }
            internal void ArrangeContent()
            {
                int pad = Theme.S(18), inner = Math.Max(1, Width - pad * 2);
                bool columns = FactColumns(Width);
                eyebrow.SetBounds(pad, pad, inner, Theme.S(20));
                heading.SetBounds(pad, eyebrow.Bottom + Theme.S(8), inner, TextHeight(heading.Text, heading.Font, inner));
                summary.SetBounds(pad, heading.Bottom + Theme.S(10), inner, TextHeight(summary.Text, summary.Font, inner));
                dividerTop = summary.Bottom + Theme.S(10);
                int top = dividerTop;
                int textWidth = FactTextWidth(inner, columns);
                int columnGap = Theme.S(16), columnWidth = columns ? (inner - columnGap * 2) / 3 : inner;
                int[] titleHeights = new int[3], descriptionHeights = new int[3], rowHeights = new int[3];
                int minimumHeight = 0;
                for (int i = 0; i < 3; i++)
                {
                    titleHeights[i] = TextHeight(factTitles[i].Text, factTitles[i].Font, textWidth);
                    descriptionHeights[i] = TextHeight(factDescriptions[i].Text, factDescriptions[i].Font, textWidth);
                    rowHeights[i] = Math.Max(Theme.S(42), titleHeights[i] + Theme.S(4) + descriptionHeights[i]) + Theme.S(14);
                    minimumHeight = columns ? Math.Max(minimumHeight, rowHeights[i]) : minimumHeight + rowHeights[i];
                }
                int extra = Math.Max(0, Height - pad - top - minimumHeight);
                for (int i = 0; i < 3; i++)
                {
                    int titleHeight = titleHeights[i], descriptionHeight = descriptionHeights[i];
                    int left = columns ? pad + i * (columnWidth + columnGap) : pad;
                    int width = columns ? (i == 2 ? pad + inner - left : columnWidth) : inner;
                    int rowHeight = columns ? minimumHeight + extra : rowHeights[i] + extra / 3 + (i < extra % 3 ? 1 : 0);
                    factRows[i] = new Rectangle(left, top, width, rowHeight);
                    int iconSize = Theme.S(28), textLeft = left + Theme.S(columns ? 38 : 42);
                    int textTop = top + (rowHeight - titleHeight - Theme.S(4) - descriptionHeight) / 2;
                    factTitles[i].SetBounds(textLeft, textTop, textWidth, titleHeight);
                    factDescriptions[i].SetBounds(textLeft, factTitles[i].Bottom + Theme.S(4), textWidth, descriptionHeight);
                    icons[i] = new Rectangle(left, top + (rowHeight - iconSize) / 2, iconSize, iconSize);
                    if (!columns) top += rowHeight;
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
                        // 竖排时行与行之间画横线 横排三列时列与列之间画竖线
                        if (i > 0 && factRows[i].Top == factRows[i - 1].Top)
                        {
                            int x = (factRows[i - 1].Right + factRows[i].Left) / 2;
                            e.Graphics.DrawLine(divider, x, factRows[i].Top + Theme.S(12), x, factRows[i].Bottom - Theme.S(12));
                        }
                        else if (i > 0) e.Graphics.DrawLine(divider, factRows[i].Left, factRows[i].Top, factRows[i].Right, factRows[i].Top);
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
                        // 整组上移 左侧上箭头 右侧三条代表游戏的全部线程
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
        private bool Manual { get { return mode == ManualMode; } }

        private CriticalThreadRule Draft(out string failure)
        {
            failure = null;
            if (!FormalMode)
            { failure = T("请选择一种亲和性模式。", "Choose an affinity mode."); return null; }
            // 自动保存时沿用带避让的编码 运行时不看这一位 渲染单核和手动按开关决定
            bool avoid = mode == AutomaticMode || clearCore != null && clearCore.Checked;
            if (Manual)
            {
                if (!trusted) { failure = T("处理器拓扑不可验证，暂不能手动选核", "CPU topology could not be verified; manual selection is unavailable"); return null; }
                if (manualSelection == 0) { failure = T("请至少选择一个逻辑 CPU。", "Choose at least one logical CPU."); return null; }
                CriticalThreadRule manual = CriticalThreadRule.UnifiedManualSet(ManualCorePlan.Stamp(all, physical), manualSelection, avoid);
                failure = scopeError ?? manual.Validate(all, physical, allowed, reserved, trusted);
                return manual;
            }
            CriticalThreadRule rule = CriticalThreadRule.UnifiedStrategy(SelectedStrategy, avoid);
            // 自动在运行时按实测容量决定放几条 余量不够就不放 保存时不拦 渲染单核开避让时才先核对容量
            if (rule.ClearCore && mode != AutomaticMode) failure = scopeError ?? rule.ClearCoreCapacityError(all, physical, allowed, reserved, trusted);
            return rule;
        }
        private void SelectMode(int index, bool moveFocus)
        {
            mode = Math.Max(0, Math.Min(modes.Length - 1, index));
            // 第一次切到手动时先填上推荐的一颗核 用户在此基础上改 清空后再切回来也同样处理
            if (mode == ManualMode && manualSelection == 0)
            {
                manualSelection = SuggestedCore();
                cpuMatrix.ShowSelectionImmediately(manualSelection);
            }
            RefreshChoice();
            if (moveFocus) modes[mode].Focus();
            RevealChoice();
            body.ScrollControlIntoView(modes[mode]);
        }
        // 只把选中项滚到完整可见 其余位置不动 子控件的 Top 已扣除滚动量 先换回内容坐标再比较
        private void RevealChoice()
        {
            int scroll = Math.Max(0, -choiceList.AutoScrollPosition.Y);
            int top = modes[mode].Top + scroll, bottom = top + modes[mode].Height, view = choiceList.ClientSize.Height;
            if (top < scroll) scroll = top;
            else if (bottom > scroll + view) scroll = bottom - view;
            choiceList.AutoScrollPosition = new Point(0, Math.Max(0, scroll));
        }
        // relayout 为 false 用于只改了所选 CPU 的情况 整窗重排会把正文滚动先归零再还原 屏幕像素来回平移就是点格子时的闪烁
        private void RefreshChoice(bool relayout = true)
        {
            string errorBefore = error.Text;
            for (int i = 0; i < modes.Length; i++) { modes[i].Kind = i == mode ? BtnKind.Primary : BtnKind.Normal; ((StrategyChoice)modes[i]).RefreshSelection(); }
            // 自动不用整核避让 开关显示为关且不可改 回到渲染单核或手动时还原原来的勾选
            bool automatic = mode == AutomaticMode;
            clearCore.Enabled = FormalMode && !automatic;
            clearCore.Checked = clearCore.Enabled && clearCoreRequested;
            clearCore.Summary = automatic ? T("自动只设优先核心，不需要避让", "Not used in Automatic, which only sets preferred cores")
                : T("其他游戏线程避开渲染线程所在的整颗物理核", "Other game threads stay off the render thread's physical core");
            int picked = Bits(manualSelection), limit = CriticalThreadAutoPolicy.MaximumIsolated;
            cpuCount.Text = T("已选 " + picked + " / " + limit, "Selected " + picked + " / " + limit)
                + (cpuLimitHit ? T(" · 已到上限", " · limit reached") : "");
            cpuCount.ForeColor = cpuLimitHit ? Theme.Warning : picked > 0 ? Theme.Accent : Theme.Dim;
            cpuClear.Enabled = picked > 0;
            cpuMatrix.AccessibleDescription = cpuCount.Text + " " + cpuHelp.Text;
            string failure;
            Draft(out failure);
            error.Text = failure == null ? "" : Lang.T(failure);
            apply.Enabled = failure == null;
            placementHint.Text = FormalMode ? CompactStrategyHint(SelectedStrategy, Manual) : T("请选择亲和性模式。", "Choose an affinity mode.");
            details.SetChoice(Math.Max(0, mode));
            observationHint.Text = observation == null || !FormalMode ? "" : observation.Hint(SelectedStrategy);
            preview.Text = !FormalMode ? ""
                : automatic ? T("只做核心放置，不改优先级；条件不满足时本局少放或不放。", "Only places cores and never changes priority; reserves fewer or none when conditions are not met.")
                : Manual ? T("整核避让可能影响非常依赖多线程的游戏；系统任务仍可使用这些核心。", "Whole-core avoidance may slow heavily threaded games; system tasks can still use these cores.")
                : T("整核避让可能影响非常依赖多线程的游戏；系统任务仍可使用这颗核。", "Whole-core avoidance may slow heavily threaded games; system tasks can still use the core.");
            clearCore.AccessibleDescription = FormalMode ? ThreadLayoutDescriptions.ClearCoreHint(automatic ? SchedulingStrategy.Orchestration : SchedulingStrategy.RenderCore, clearCore.Checked) : "";
            UpdateAccessibleDescription();
            clearCore.Invalidate();
            // 错误提示或不可选原因那一行出现 消失或换了内容会改变高度 计数变长挤得标题换行也要重排 这几种情况退回完整重排
            if (relayout || !Manual || error.Text != errorBefore || cpuReason.Visible != (cpuReason.Text.Length > 0) || !PlaceCpuCount())
                LayoutChoice();
        }
        // 只重摆选核卡片标题行的计数 摆法与 LayoutBody 一致 标题高度变了返回 false 交给完整重排
        private bool PlaceCpuCount()
        {
            int pad = Theme.S(16), inner = Math.Max(Theme.S(160), cpuCard.Width - pad * 2);
            int countWidth = Math.Min(inner / 2, TextRenderer.MeasureText(cpuCount.Text, cpuCount.Font).Width + Theme.S(8));
            int titleHeight = Math.Max(Measure(cpuTitle, inner - countWidth - Theme.S(8)), Theme.S(24));
            if (titleHeight != cpuTitle.Height) return false;
            cpuTitle.Width = inner - countWidth - Theme.S(8);
            cpuCount.SetBounds(pad + inner - countWidth, pad, countWidth, titleHeight);
            return true;
        }
        private void UpdateAccessibleDescription()
        {
            AccessibleDescription = (FormalMode ? modes[mode].Text : T("请选择模式", "Choose a mode")) + " " + statusHint.Text + " " + placementHint.Text + " " + conflictHint.Text
                + (observationHint.Text.Length == 0 ? "" : " " + observationHint.Text)
                + (Manual && cpuReason != null && cpuReason.Text.Length > 0 ? " " + cpuReason.Text : "")
                + (FormalMode ? " " + clearCore.Text + (clearCore.Checked ? T(" 开", " on") : T(" 关", " off")) : "")
                + " " + preview.Text + " " + sessionHint.Text + " " + error.Text;
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
                    // 方案列表自身的滚动位置跨重排保留 先归零再摆放各行 摆完按原位置还原
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
                    // 换了选中项（含首次打开）才把它滚到可见 只是重排时不动用户的滚动位置
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
            int gap = Theme.S(8);
            // 够宽时三个模式并排成一行小卡片 否则逐行竖排 两种都按内容取最小高度
            int visible = Math.Min(VisibleChoices, modes.Length);
            bool tiles = width >= Theme.S(600) && modes.Length == visible;
            int tileWidth = tiles ? (width - gap * (modes.Length - 1)) / modes.Length : width;
            int rowHeight = 0;
            foreach (StrategyChoice choice in modes)
            {
                choice.Tile = tiles;
                rowHeight = Math.Max(rowHeight, choice.MinimumHeight(tileWidth));
            }
            int listHeight = tiles ? rowHeight : visible * rowHeight + (visible - 1) * gap;
            choicesTitle.SetBounds(0, 0, Math.Max(1, width - Theme.S(100)), Theme.S(26));
            choicesHint.SetBounds(Math.Max(0, width - Theme.S(100)), 0, Math.Min(width, Theme.S(100)), Theme.S(26));
            int rowTop = Theme.S(34);
            choiceList.SetBounds(0, rowTop, width, listHeight);
            for (int i = 0; i < modes.Length; i++)
            {
                if (tiles) modes[i].SetBounds(i * (tileWidth + gap), 0, tileWidth, rowHeight);
                else modes[i].SetBounds(0, i * (rowHeight + gap), width, rowHeight);
                modes[i].Invalidate();
            }
            choiceList.AutoScrollMinSize = new Size(0, listHeight);
            int listBottom = choiceList.Bottom;
            details.SetBounds(0, listBottom + Theme.S(16), width, details.MinimumHeight(width));
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
            // 手动模式才显示选核卡片 标题和已选计数一行 说明一行 快捷按钮一行 下面是按物理核分组的矩阵
            cpuCard.Visible = Manual;
            if (Manual)
            {
                int pad = Theme.S(16), inner = Math.Max(Theme.S(160), width - pad * 2);
                int countWidth = Math.Min(inner / 2, TextRenderer.MeasureText(cpuCount.Text, cpuCount.Font).Width + Theme.S(8));
                int titleHeight = Math.Max(Measure(cpuTitle, inner - countWidth - Theme.S(8)), Theme.S(24));
                cpuTitle.SetBounds(pad, pad, inner - countWidth - Theme.S(8), titleHeight);
                cpuCount.SetBounds(pad + inner - countWidth, pad, countWidth, titleHeight);
                cpuHelp.SetBounds(pad, cpuTitle.Bottom + Theme.S(2), inner, Measure(cpuHelp, inner));
                int x = pad, y = cpuHelp.Bottom + Theme.S(10), buttonHeight = Theme.S(30);
                foreach (PillButton button in new[] { cpuSuggest, cpuClear })
                {
                    int buttonWidth = TextRenderer.MeasureText(button.Text, button.Font).Width + Theme.S(32);
                    button.SetBounds(x, y, buttonWidth, buttonHeight);
                    x += buttonWidth + Theme.S(8);
                }
                // 图例放在快捷按钮同一行的右侧 放不下就单独占一行
                int legendWidth = Math.Min(inner, cpuLegend.PreferredWidth);
                if (x + Theme.S(16) + legendWidth <= pad + inner) cpuLegend.SetBounds(pad + inner - legendWidth, y, legendWidth, buttonHeight);
                else { y += buttonHeight + Theme.S(6); cpuLegend.SetBounds(pad, y, legendWidth, Theme.S(24)); y -= buttonHeight - Theme.S(24); }
                y += buttonHeight + Theme.S(14);
                cpuMatrix.SetBounds(pad, y, inner, cpuMatrix.LayoutFor(inner));
                int cardBottom = cpuMatrix.Bottom;
                cpuReason.Visible = cpuReason.Text.Length > 0;
                if (cpuReason.Visible)
                {
                    cpuReason.SetBounds(pad, cardBottom + Theme.S(8), inner, Measure(cpuReason, inner));
                    cardBottom = cpuReason.Bottom;
                }
                cpuCard.SetBounds(0, bottom + Theme.S(20), width, cardBottom + pad);
                bottom = cpuCard.Bottom;
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
            return bottom + Theme.S(4);
        }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            LayoutChoice();
            // 构造阶段句柄尚未创建 滚动位置无法落地 显示后再把已保存的方案滚到可见
            RevealChoice();
        }
        private void SaveChoice()
        {
            string failure;
            CriticalThreadRule rule = Draft(out failure);
            // 亲和性窗口只保存模式 性能优先级在深度调优的逐游戏行里设置
            if (failure == null) failure = save(rule, null);
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
