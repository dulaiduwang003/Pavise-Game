using System;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PaviseApp
{
    internal partial class PanelForm : Form
    {
        private void RestoreCoreSelection()
        {
            ManualCorePlan manualCorePlan = ManualCorePlan.Parse(coreDraftExpected);
            coreDraftTopology = ((manualCorePlan != null) ? manualCorePlan.Topology : ManualCorePlan.CurrentStamp());
            coreEditMatrix.ShowSelectionImmediately((manualCorePlan != null) ? manualCorePlan.GameMask : 0);
            coreIsolationToggle.SetSilently(manualCorePlan != null && manualCorePlan.Isolation);
        }
        private ManualCorePlan CoreDraftPlan()
        {
            ManualCorePlan manualCorePlan = ManualCorePlan.Parse(coreDraftExpected);
            return new ManualCorePlan(coreEditMatrix.Selected, coreIsolationToggle.Checked, coreDraftTopology, manualCorePlan != null && manualCorePlan.HardAffinity);
        }
        private bool CoreDraftHasChanges()
        {
            bool samePlan = coreDraftExpected == null && coreEditMatrix.Selected == 0 && !coreIsolationToggle.Checked
                || CoreDraftPlan().Encode() == coreDraftExpected;
            return !samePlan;
        }
        private CoreEditorDraftSnapshot CaptureCoreDraft()
        {
            if (coreDraftProfile == null || coreEditMatrix == null || coreEditMatrix.IsDisposed) return null;
            return new CoreEditorDraftSnapshot { Profile = coreDraftProfile, Expected = coreDraftExpected,
                Topology = coreDraftTopology, Error = coreSaveError, Mask = coreEditMatrix.Selected,
                Isolation = coreIsolationToggle.Checked,
                Baseline = coreDraftBaseline, EnabledValue = coreDraftEnabledValue };
        }
        private bool CoreDraftBaselineMatches(GameProfile current)
        {
            if (current == null || coreDraftBaseline == null || current.Id != coreDraftProfile) return false;
            string before, now;
            coreDraftBaseline.Overrides.TryGetValue(PolicyCatalog.KeyAutoCores, out before);
            current.Overrides.TryGetValue(PolicyCatalog.KeyAutoCores, out now);
            return ManualCorePlan.Raw(current) == coreDraftExpected && CriticalThreadRule.Raw(current) == CriticalThreadRule.Raw(coreDraftBaseline)
                && before == now && PolicyResolver.For(current).ValueOf(PolicyCatalog.KeyAutoCores) == coreDraftEnabledValue;
        }
        private void ReloadCoreDraft(GameProfile profile)
        {
            coreRebuildDraft = null;
            coreDraftProfile = profile == null ? null : profile.Id;
            coreDraftBaseline = profile == null ? null : profile.Clone();
            coreDraftExpected = ManualCorePlan.Raw(profile);
            coreDraftEnabledValue = profile == null ? "0" : PolicyResolver.For(profile).ValueOf(PolicyCatalog.KeyAutoCores);
            coreSaveError = null;
            if (profile == null) return;
            RestoreCoreSelection();
        }
        private void CoreDraftChanged()
        {
            if (coreDraftProfile == null || coreIsolationToggle == null || coreSaveButton == null) return;
            ManualCorePlan manualCorePlan = CoreDraftPlan();
            string validateCurrentResult = manualCorePlan.ValidateCurrent(coreEditMatrix.DisabledMask);
            bool draftChanged = CoreDraftHasChanges();
            bool conflict = !CoreDraftBaselineMatches(FindLibraryProfile(coreDraftProfile));
            if (conflict) coreSaveError = "schedule.manual.changed";
            coreEditorResult.Text = coreSaveError != null ? Lang.F("schedule.manual.autofailed", Lang.T(coreSaveError))
                : validateCurrentResult != null ? Lang.F("schedule.manual.unsaved", Lang.T(validateCurrentResult))
                : Lang.T(draftChanged ? "schedule.manual.draft" : coreDraftEnabledValue == "1" ? "schedule.manual.autosaved" : "schedule.manual.savedoff");
            coreEditorResult.ForeColor = coreSaveError != null || validateCurrentResult != null ? Theme.Warning : Theme.Dim;
            coreEditorResult.Visible = true;
            coreSaveButton.Enabled = draftChanged && validateCurrentResult == null;
            coreSaveButton.Kind = coreSaveButton.Enabled ? BtnKind.Primary : BtnKind.Normal;
            coreResetButton.Enabled = draftChanged || conflict || coreSaveError != null;
            coreEditorHelp.Text = Lang.T("schedule.manual.help");
            SyncCoreRuntimePresentation();
            coreIsolationToggle.Enabled = coreIsolationToggle.Checked || new ManualCorePlan(coreEditMatrix.Selected, true, coreDraftTopology).ValidateCurrent(coreEditMatrix.DisabledMask) == null;
            SyncCoreShortcuts();
            LayoutCoreEditor();
            if (coreStatusPanel != null)
            {
                LayoutCorePage();
            }
        }
        private void SaveCoreDraft()
        {
            if (coreDraftProfile == null)
            {
                return;
            }
            coreSaveError = null;
            coreEditMatrix.DisabledMask = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
            ManualCorePlan manualCorePlan = CoreDraftPlan();
            if (manualCorePlan.ValidateCurrent(coreEditMatrix.DisabledMask) != null)
            {
                CoreDraftChanged();
                return;
            }
            if (!CoreDraftHasChanges())
            {
                CoreDraftChanged();
                return;
            }
            string expectedOverride;
            coreDraftBaseline.Overrides.TryGetValue(PolicyCatalog.KeyAutoCores, out expectedOverride);
            // 启用状态统一由深度调优控制，保存选核时保留原有覆盖值及继承关系。
            string saveCoreConfigurationWithRenderResult = SaveCoreConfigurationWithRender(coreDraftBaseline, coreDraftExpected, manualCorePlan, coreDraftEnabledValue == "1",
                CriticalThreadRule.Raw(coreDraftBaseline), coreDraftEnabledValue,
                new CoreRangeSwitchDraft(expectedOverride, true));
            if (saveCoreConfigurationWithRenderResult == null)
            {
                ReloadCoreDraft(FindLibraryProfile(coreDraftProfile));
            }
            coreSaveError = saveCoreConfigurationWithRenderResult;
            CoreDraftChanged();
        }
        private static string CpuDisplayName()
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
                {
                    string getValueOffset = ((registryKey == null) ? null : (registryKey.GetValue("ProcessorNameString") as string));
                    if (!string.IsNullOrEmpty(getValueOffset))
                    {
                        return Regex.Replace(getValueOffset, "\\s+", " ").Trim();
                    }
                }
            }
            catch
            {
            }
            return Lang.T("core.topo.unknown");
        }
        private static string TopologyLine()
        {
            if (CpuTopology.MultiGroup)
                return PerformancePolicy.Text("多处理器组 · 全机逻辑核、物理核与核心类型未确认", "Multiple processor groups · total logical/physical core counts and types unconfirmed");
            int logical = CpuTopology.CountSetBits(CpuTopology.AllMask);
            if (!CoreMatrix.TopologyCountsConfirmed)
                return logical + PerformancePolicy.Text(" 个可用逻辑核 · 物理核数与 SMT 状态未确认 · 核心类型未确认",
                    " available logical CPUs · physical core count and SMT state unconfirmed · core types unconfirmed");
            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            int smt = 0;
            foreach (ulong core in cores)
                if (CpuTopology.CountSetBits(core & CpuTopology.AllMask) > 1) smt++;
            string textOffset = cores.Length + PerformancePolicy.Text(" 物理核 · ", " physical cores · ") + logical
                + PerformancePolicy.Text(" 逻辑核 · 当前 ", " logical CPUs · ") + smt
                + PerformancePolicy.Text(" 个物理核含多个逻辑线程", " physical cores currently have multiple logical threads");
            if (!CpuTopology.CoreTypesConfirmed)
                textOffset += PerformancePolicy.Text(" · 核心类型未确认", " · core types unconfirmed");
            return textOffset;
        }
        private void ClearSuspectReservedCores()
        {
            ulong suspectResult = LegacyReservedCores.Suspect();
            if (suspectResult != 0 && PaviseDialog.Confirm(this, Lang.T("schedule.auto.reserved.clear"), Lang.F("schedule.auto.reserved.confirm", CpuTopology.DescribeMask(suspectResult)), DlgKind.Warn))
            {
                if (!LegacyReservedCores.ClearSuspect())
                {
                    PaviseDialog.Warn(this, Lang.T("schedule.auto.reserved.clear"), Lang.T("schedule.auto.reserved.failed"));
                }
                SyncCorePage();
            }
        }
        private void AddCoreShortcut(string name, string title, ulong mask, int threads)
        {
            mask &= CpuTopology.AllMask;
            if (threads == 0 && mask == 0)
            {
                return;
            }
            foreach (Control control in coreQuickPanel.Controls)
            {
                CoreQuickChoice coreQuickChoice = (CoreQuickChoice)control.Tag;
                if (threads == 0 && coreQuickChoice.Threads == 0 && coreQuickChoice.Mask == mask)
                {
                    return;
                }
            }
            CoreQuickChoice choice = new CoreQuickChoice
            {
                Mask = mask,
                Threads = threads
            };
            PillButton pillButton = new PillButton(title);
            pillButton.Name = "coreQuick_" + name;
            pillButton.Tag = choice;
            pillButton.AccessibleName = title;
            PillButton pillButton2 = pillButton;
            pillButton2.Size = new Size(Math.Max(Theme.S(72), TextRenderer.MeasureText(title, Theme.UI(9f, true)).Width + Theme.S(28)), Theme.S(30));
            pillButton2.Click += delegate
            {
                if (coreDraftProfile != null)
                {
                    coreEditMatrix.Selected = CoreShortcutTarget(choice);
                    coreDraftTopology = ManualCorePlan.CurrentStamp();
                    coreSaveError = null;
                    CoreDraftChanged();
                }
            };
            coreQuickPanel.Controls.Add(pillButton2);
        }
        private ulong CoreShortcutTarget(CoreQuickChoice choice)
        {
            ulong availableCoreMask = CpuTopology.AllMask & ~coreEditMatrix.DisabledMask;
            if (choice.Threads != 0)
            {
                return CoreShortcutThreads(coreEditMatrix.Selected, availableCoreMask, CpuTopology.PhysicalCoreMasks(), choice.Threads == 2);
            }
            return choice.Mask & availableCoreMask;
        }
        internal static ulong CoreShortcutThreads(ulong selected, ulong available, ulong[] physical, bool includeSiblings)
        {
            ulong coreShortcutThreadsResult = 0uL;
            foreach (ulong numericValueEntry in physical)
            {
                ulong availableMask = numericValueEntry & selected & available;
                if (availableMask != 0)
                {
                    coreShortcutThreadsResult |= (includeSiblings ? (numericValueEntry & available) : (availableMask & (~availableMask + 1)));
                }
            }
            return coreShortcutThreadsResult;
        }
        private static string ThreadUi(string zh, string en)
        {
            return PerformancePolicy.Text(zh, en);
        }
        private static string CriticalThreadCompanionText(GameProfile profile, out bool coupled)
        {
            PolicySnapshot policySnapshot = PolicyResolver.For(profile);
            coupled = true;
            if (!policySnapshot.EffBoost)
            {
                coupled = false;
                return ThreadUi("本游戏的游戏资源优先级已关闭，线程提优不运行。", "Game resource priority is off for this game, so thread boost does not run.");
            }
            if (policySnapshot.FrameCoordinationOn)
            {
                return ThreadUi("本游戏选择了渲染关键线程提优。与此规则同用时，呈现关联或依赖关联候选可能同时被提优；组合效果未测。", "Render-critical thread boost is selected for this game. Used with this rule, presentation-related or dependency-related candidates may also be boosted; the combination is untested.");
            }
            if (policySnapshot.RenderLane && GameMode.LaneSupported(policySnapshot.Preset))
            {
                return ThreadUi("本游戏选择了高负载线程提优。与此规则同用时，可能作用于同一线程；组合效果未测。", "Busy-thread boost is selected for this game. Used with this rule, it may target the same thread; the combination is untested.");
            }
            coupled = false;
            return ThreadUi("本游戏不运行线程提优，不与此规则叠加。", "Thread boost does not run for this game and does not combine with this rule.");
        }
        private string CriticalThreadRuntimeText()
        {
            CriticalThreadUiState criticalThreadUiState = criticalThreadView;
            if (criticalThreadUiState == null)
            {
                return ThreadUi("尚无此游戏的运行快照。", "No runtime snapshot for this game.");
            }
            if (criticalThreadUiState.ProfileId != criticalThreadProfile)
            {
                if (!criticalThreadUiState.Pending)
                {
                    return ThreadUi("尚无此游戏的运行快照。", "No runtime snapshot for this game.");
                }
                return ThreadUi("原会话恢复待办：新绑定被阻止。", "An earlier session has recovery pending; new affinity is blocked.") + "\n" + criticalThreadUiState.Detail;
            }
            long nowTicks = AutoCores.NowTicks;
            bool snapshotFresh = criticalThreadUiState.SnapshotTicks > 0 && nowTicks >= criticalThreadUiState.SnapshotTicks && nowTicks - criticalThreadUiState.SnapshotTicks <= 65000000;
            CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(criticalThreadExpected);
            bool waitingForAutomaticChoice = criticalThreadRule != null && criticalThreadRule.Automatic && !criticalThreadUiState.Applied;
            string threadUiResult = (criticalThreadUiState.Pending ? ThreadUi("恢复待办：新绑定被阻止。", "Recovery pending: new affinity is blocked.") : ((criticalThreadUiState.State == "off" || criticalThreadUiState.State == "waiting-game" || waitingForAutomaticChoice) ? criticalThreadUiState.StateText : ((!snapshotFresh) ? ThreadUi("运行快照待复核；当前硬绑定未确认。", "Runtime snapshot needs rechecking; current affinity is unconfirmed.") : criticalThreadUiState.StateText)));
            if (!snapshotFresh && criticalThreadUiState.SnapshotTicks > 0)
            {
                threadUiResult = threadUiResult + "\n" + ThreadUi("上次状态：", "Previous status: ") + criticalThreadUiState.StateText;
            }
            if (snapshotFresh && criticalThreadUiState.Applied)
            {
                threadUiResult = threadUiResult + "\n" + ThreadUi("最近一次核验已通过。", "Last verification passed.");
            }
            string threadUiResultOffset = ThreadUi("运行状态（缓存快照）", "Runtime state (cached snapshot)") + "\n" + threadUiResult;
            CriticalThreadRule activeRule = CriticalThreadRule.ParseForRuntime(criticalThreadUiState.RuleSignature);
            if (criticalThreadUiState.SessionActive && criticalThreadRule != null && activeRule != null && criticalThreadRule.Encode() != activeRule.Encode())
            {
                threadUiResultOffset = threadUiResultOffset + "\n" + ThreadUi("新保存的规则下局生效。", "The saved rule applies next session.");
            }
            object instance = threadUiResultOffset;
            if (criticalThreadRule != null && !criticalThreadRule.IsUnified)
                threadUiResultOffset = string.Concat(instance, "\n", ThreadUi("候选数：", "Candidates: "), criticalThreadUiState.Candidates);
            if (!string.IsNullOrEmpty(criticalThreadUiState.CoverageText))
            {
                threadUiResultOffset = threadUiResultOffset + "\n" + (snapshotFresh ? "" : ThreadUi("上次覆盖：", "Previous coverage: ")) + criticalThreadUiState.CoverageText;
            }
            string couplingText = ((criticalThreadUiState.SnapshotTicks > 0) ? criticalThreadUiState.CouplingText : "");
            if (!string.IsNullOrEmpty(couplingText))
            {
                threadUiResultOffset = threadUiResultOffset + "\n" + ((snapshotFresh && !criticalThreadUiState.Pending && criticalThreadUiState.SessionActive) ? "" : ThreadUi("上次提优关系：", "Previous thread boost relation: ")) + couplingText;
            }
            if (criticalThreadUiState.RequestedWorkerAvoid)
            {
                threadUiResultOffset = threadUiResultOffset + "\n" + ((criticalThreadUiState.SnapshotTicks > 0) ? (ThreadUi("工作线程观测数（快照）：", "Observed workers (snapshot): ") + criticalThreadUiState.Workers) : ThreadUi("工作线程避让尚未核验。", "Worker avoidance has not been verified."));
                if (criticalThreadUiState.WorkerMask != 0)
                {
                    threadUiResultOffset = threadUiResultOffset + "\n" + ThreadUi("工作线程目标范围（快照）：", "Worker target range (snapshot): ") + CpuTopology.DescribeMask(criticalThreadUiState.WorkerMask);
                }
            }
            else if (criticalThreadUiState.Coverage != CriticalThreadCoverageState.CandidatesOnly)
            {
                threadUiResultOffset = threadUiResultOffset + "\n" + ThreadUi("该游戏进程的其他线程：未请求限制。", "Other threads in this game process: no restrictions requested.");
            }
            string[] candidateLabels = criticalThreadUiState.CandidateLabels;
            ulong[] candidateMasks = criticalThreadUiState.CandidateMasks;
            for (int i = 0; i < candidateMasks.Length; i++)
            {
                object instance2 = threadUiResultOffset;
                threadUiResultOffset = string.Concat(instance2, "\n", ThreadUi("候选 ", "Candidate "), i + 1, ": ", (i < candidateLabels.Length) ? candidateLabels[i] : "", " → ", CpuTopology.DescribeMask(candidateMasks[i]));
            }
            if (!string.IsNullOrEmpty(criticalThreadUiState.Detail))
            {
                threadUiResultOffset = threadUiResultOffset + "\n" + criticalThreadUiState.Detail;
            }
            return threadUiResultOffset;
        }
        // 界面台架用它替代模态确认；产品运行时保持为空。
        internal Func<bool> SchedulingRiskConfirmForTest;

        // 渲染线程调度逐游戏默认关闭。开启前说明效果需自行对照、可能变差；取消时不写入任何配置。
        private bool ConfirmSchedulingRisk()
        {
            if (SchedulingRiskConfirmForTest != null) return SchedulingRiskConfirmForTest();
            return PaviseDialog.Confirm(LibraryDialogOwner, Lang.T("lib.threads.title"), Lang.T("lib.threads.enable.warn"), DlgKind.Warn);
        }

        private void SaveCriticalThreadSwitch(bool enabled)
        {
            if (criticalThreadProfile == null || criticalThreadSyncing)
            {
                return;
            }
            // 确认窗口关闭后界面可能已切换或销毁；规则与开关的并发变化由下方的原值校验拦截。
            if (enabled && (!ConfirmSchedulingRisk() || IsDisposed || Disposing || criticalThreadProfile == null || criticalThreadSyncing))
            {
                return;
            }
            GameProfile profile = FindLibraryProfile(criticalThreadProfile);
            string expectedEnabled = null;
            string error = profile == null || CriticalThreadRule.Raw(profile) != criticalThreadExpected
                ? ThreadUi("游戏规则已变化，请重新打开", "The game rule changed; reopen the editor") : null;
            if (profile != null) profile.Overrides.TryGetValue(PolicyCatalog.KeyGameScheduling, out expectedEnabled);
            CriticalThreadRule stored = profile == null ? null : CriticalThreadRule.Parse(CriticalThreadRule.Raw(profile));
            if (error == null && enabled && stored != null && stored.IsComparison)
                error = ThreadUi("优先级对照已移除，请先选择一种正式策略", "Priority comparison was removed; choose a scheduling strategy first");
            bool changedMaster = false, originalMaster = false, deferred = false;
            int writtenGeneration = 0;
            // 仅显式打开外层开关可恢复隐藏的全局总开关。策略弹窗不改变开关。
            if (error == null && enabled && PolicyResolver.RawGlobalValue(PolicyCatalog.KeyGameScheduling) != "1")
            {
                changedMaster = Settings.SaveGameSchedulingMaster(true, out originalMaster, out writtenGeneration);
                if (!changedMaster)
                    error = ThreadUi("未能恢复渲染线程调度，请重试。", "Could not restore render thread scheduling. Please retry.");
                else
                {
                    gameMode.RefreshAutomaticPreferences();
                    RefreshAutomationPage();
                }
            }
            if (error == null)
            {
                error = gameMode.SaveCriticalThreadEnabled(criticalThreadProfile, criticalThreadExpected, expectedEnabled, enabled, out deferred);
                if (error == null)
                {
                    criticalThreadExpected = CriticalThreadRule.Raw(FindLibraryProfile(criticalThreadProfile));
                }
            }
            if (error != null && changedMaster)
            {
                bool superseded;
                bool restored = Settings.RestoreGameSchedulingMasterIfUnchanged(writtenGeneration, true, originalMaster, out superseded);
                if (restored)
                {
                    gameMode.RefreshAutomaticPreferences();
                    RefreshAutomationPage();
                }
                else
                {
                    string rollback = superseded
                        ? ThreadUi("全局设置随后已有修改，本次未覆盖；请核对当前开关。", "Global settings changed afterward and were retained; check the current switch.")
                        : ThreadUi("全局开关回滚未确认，请核对当前开关。", "Global switch rollback could not be confirmed; check the current switch.");
                    Logger.Warn("游戏调度 逐游戏开关保存失败；全局回滚=" + (superseded ? "保留后续设置修改" : "未确认") + "；游戏=" + criticalThreadProfile);
                    error += "\n" + rollback;
                }
            }
            if (error == null && deferred)
                error = ThreadUi("开关已开启；本局识别阶段已关闭，保留采集预算，下局应用已选策略。", "The switch is on; this session's capture stage was closed. Its budget is retained and the selected strategy starts next session.");
            criticalThreadError = error;
            SyncCriticalThreadEditor(FindLibraryProfile(criticalThreadProfile));
        }
        private PerformanceInfoButton CoreInfo(Control parent, string name, string title, Func<string> body)
        {
            PerformanceInfoButton performanceInfoButton = new PerformanceInfoButton();
            performanceInfoButton.Name = name;
            performanceInfoButton.AccessibleName = title;
            performanceInfoButton.Bg = Theme.Card;
            PerformanceInfoButton performanceInfoButton2 = performanceInfoButton;
            performanceInfoButton2.Click += delegate
            {
                ShowCoreInformation(title, body());
            };
            parent.Controls.Add(performanceInfoButton2);
            return performanceInfoButton2;
        }
        private string CoreWorkspaceLayoutKey()
        {
            AutoCoreStatus status = AutoCores.Status;
            return string.Concat(pageCoreScheduling.ClientSize, "|", Dpi.Scale, "|", coreDraftProfile, "|", coreEditMatrix.Selected, "|", coreEditorResult.Visible, "|", coreEditorResult.Text, "|", corePlanPreview.Height, "|", status.Pending, "|", status.Blocked, "|", CoreIsolationClient.State, "|", lblCoreReason.Text, "|", lblCoreReserved.Text, "|", btnCoreClearReserved.Visible);
        }
        private void RevealCoreCell(Rectangle cell)
        {
            if (!cell.IsEmpty && coreMatrixViewport != null)
            {
                int lowerBound = Math.Max(0, -coreMatrixViewport.AutoScrollPosition.Y);
                int desiredScroll = lowerBound;
                int height = coreMatrixViewport.ClientSize.Height;
                int top = cell.Top;
                int bottom = cell.Bottom;
                if (top < lowerBound)
                {
                    desiredScroll = Math.Max(0, top - Theme.S(4));
                }
                else if (bottom > lowerBound + height)
                {
                    desiredScroll = bottom - height + Theme.S(4);
                }
                desiredScroll = Math.Min(desiredScroll, DockScrollLimit(coreMatrixViewport));
                if (desiredScroll != lowerBound)
                {
                    coreMatrixViewport.AutoScrollPosition = new Point(0, desiredScroll);
                }
            }
        }
        private CoreViewState CaptureCoreViewState()
        {
            if (curPage != pageCoreScheduling || coreMatrixViewport == null || coreRightRail == null || coreRightRail.IsDisposed)
            {
                return null;
            }
            float coreLayoutScaleLimit = Math.Max(0.01f, coreLayoutScale);
            CoreViewState coreViewState = new CoreViewState();
            coreViewState.MatrixY = (float)Math.Max(0, -coreMatrixViewport.AutoScrollPosition.Y) / coreLayoutScaleLimit;
            coreViewState.RailY = (float)Math.Max(0, -coreRightRail.AutoScrollPosition.Y) / coreLayoutScaleLimit;
            return coreViewState;
        }
        private void RestoreCoreViewState(CoreViewState state)
        {
            if (state != null && coreMatrixViewport != null && coreRightRail != null && curPage == pageCoreScheduling)
            {
                coreMatrixViewport.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(state.MatrixY * Dpi.Scale), DockScrollLimit(coreMatrixViewport)));
                coreRightRail.AutoScrollPosition = new Point(0, Math.Min((int)Math.Round(state.RailY * Dpi.Scale), DockScrollLimit(coreRightRail)));
            }
        }
    }
}
