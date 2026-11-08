using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
		private void CoreDraftChanged()
		{
			ManualCorePlan manualCorePlan = CoreDraftPlan();
			string text = manualCorePlan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask);
			bool flag = manualCorePlan.Encode() != coreDraftExpected;
			coreEditorResult.Text = ((coreSaveError == "schedule.manual.changed") ? Lang.T(coreSaveError) : ((coreSaveError != null) ? Lang.F("schedule.manual.autofailed", Lang.T(coreSaveError)) : ((text == null) ? Lang.T(coreEnableToggle.Checked ? "schedule.manual.autosaved" : "schedule.manual.savedoff") : ((coreDraftExpected == null && coreEditMatrix.Selected == 0) ? Lang.T("schedule.manual.required") : Lang.F(flag ? "schedule.manual.unsaved" : "schedule.manual.invalid", Lang.T(text))))));
			coreEditorResult.ForeColor = ((coreSaveError != null || (text != null && (coreEditMatrix.Selected != 0 || coreDraftExpected != null))) ? Theme.Warning : Theme.Dim);
			coreEditorResult.Visible = coreSaveError != null || text != null;
			coreEditorHelp.Text = Lang.T("schedule.manual.help");
			SyncCoreRuntimePresentation();
			coreIsolationToggle.Enabled = coreIsolationToggle.Checked || new ManualCorePlan(coreEditMatrix.Selected, true, coreDraftTopology).ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask) == null;
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
			ManualCorePlan manualCorePlan = CoreDraftPlan();
			if (manualCorePlan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask) != null)
			{
				CoreDraftChanged();
				return;
			}
			if (manualCorePlan.Encode() == coreDraftExpected)
			{
				CoreDraftChanged();
				return;
			}
			GameProfile profile = FindLibraryProfile(coreDraftProfile);
			bool enabled = profile != null && ManualCorePlan.Parse(coreDraftExpected) != null && PolicyResolver.For(profile).AutoCoresAllowed;
			string text = SaveCoreConfigurationWithRender(profile, coreDraftExpected, manualCorePlan, enabled);
			if (text == null)
			{
				coreDraftExpected = manualCorePlan.Encode();
			}
			else
			{
				RestoreCoreSelection();
			}
			SyncCoreEditor(CorePageProfile());
			coreSaveError = text;
			CoreDraftChanged();
		}
		private static string CpuDisplayName()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
				{
					string text = ((registryKey == null) ? null : (registryKey.GetValue("ProcessorNameString") as string));
					if (!string.IsNullOrEmpty(text))
					{
						return Regex.Replace(text, "\\s+", " ").Trim();
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
			string text = cores.Length + PerformancePolicy.Text(" 物理核 · ", " physical cores · ") + logical
				+ PerformancePolicy.Text(" 逻辑核 · 当前 ", " logical CPUs · ") + smt
				+ PerformancePolicy.Text(" 个物理核含多个逻辑线程", " physical cores currently have multiple logical threads");
			if (!CpuTopology.CoreTypesConfirmed)
				text += PerformancePolicy.Text(" · 核心类型未确认", " · core types unconfirmed");
			return text;
		}
		private void ClearSuspectReservedCores()
		{
			ulong num = LegacyReservedCores.Suspect();
			if (num != 0 && PaviseDialog.Confirm(this, Lang.T("schedule.auto.reserved.clear"), Lang.F("schedule.auto.reserved.confirm", CpuTopology.DescribeMask(num)), DlgKind.Warn))
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
					SaveCoreDraft();
				}
			};
			coreQuickPanel.Controls.Add(pillButton2);
		}
		private ulong CoreShortcutTarget(CoreQuickChoice choice)
		{
			ulong num = CpuTopology.AllMask & ~(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask);
			if (choice.Threads != 0)
			{
				return CoreShortcutThreads(coreEditMatrix.Selected, num, CpuTopology.PhysicalCoreMasks(), choice.Threads == 2);
			}
			return choice.Mask & num;
		}
		internal static ulong CoreShortcutThreads(ulong selected, ulong available, ulong[] physical, bool includeSiblings)
		{
			ulong num = 0uL;
			foreach (ulong num2 in physical)
			{
				ulong num3 = num2 & selected & available;
				if (num3 != 0)
				{
					num |= (includeSiblings ? (num2 & available) : (num3 & (~num3 + 1)));
				}
			}
			return num;
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
		private CriticalThreadRule CriticalThreadSwitchRule(out string error)
		{
			if (criticalThreadAllowedError != null)
			{
				error = criticalThreadAllowedError;
				return null;
			}
			CriticalThreadRule criticalThreadRule = CriticalThreadRule.Auto(true);
			error = criticalThreadRule.Validate(criticalThreadAll, criticalThreadPhysical, criticalThreadAllowed, criticalThreadReserved, criticalThreadTopologyTrusted);
			if (error != null)
			{
				return null;
			}
			return criticalThreadRule;
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
			bool flag = criticalThreadUiState.SnapshotTicks > 0 && nowTicks >= criticalThreadUiState.SnapshotTicks && nowTicks - criticalThreadUiState.SnapshotTicks <= 65000000;
			CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(criticalThreadExpected);
			bool flag2 = criticalThreadRule != null && criticalThreadRule.Automatic && !criticalThreadUiState.Applied;
			string text = (criticalThreadUiState.Pending ? ThreadUi("恢复待办：新绑定被阻止。", "Recovery pending: new affinity is blocked.") : ((criticalThreadUiState.State == "off" || criticalThreadUiState.State == "waiting-game" || flag2) ? criticalThreadUiState.StateText : ((!flag) ? ThreadUi("运行快照待复核；当前硬绑定未确认。", "Runtime snapshot needs rechecking; current affinity is unconfirmed.") : criticalThreadUiState.StateText)));
			if (!flag && criticalThreadUiState.SnapshotTicks > 0)
			{
				text = text + "\n" + ThreadUi("上次状态：", "Previous status: ") + criticalThreadUiState.StateText;
			}
			if (flag && criticalThreadUiState.Applied)
			{
				text = text + "\n" + ThreadUi("最近原生回读已匹配；不代表性能收益。", "Last native readback matched; this does not prove a performance gain.");
			}
			string text2 = ThreadUi("运行状态（缓存快照）", "Runtime state (cached snapshot)") + "\n" + text;
			if (criticalThreadUiState.SessionActive && criticalThreadRule != null && criticalThreadRule.Encode() != criticalThreadUiState.RuleSignature)
			{
				text2 = text2 + "\n" + ThreadUi("本局策略与新保存规则不同；保存不等于已经应用。", "The session policy differs from the saved rule; saving does not mean it has been applied.");
			}
			object obj = text2;
			text2 = string.Concat(obj, "\n", ThreadUi("候选数：", "Candidates: "), criticalThreadUiState.Candidates);
			if (!string.IsNullOrEmpty(criticalThreadUiState.CoverageText))
			{
				text2 = text2 + "\n" + (flag ? "" : ThreadUi("上次覆盖：", "Previous coverage: ")) + criticalThreadUiState.CoverageText;
			}
			string text3 = ((criticalThreadUiState.SnapshotTicks > 0) ? criticalThreadUiState.CouplingText : "");
			if (!string.IsNullOrEmpty(text3))
			{
				text2 = text2 + "\n" + ((flag && !criticalThreadUiState.Pending && criticalThreadUiState.SessionActive) ? "" : ThreadUi("上次提优关系：", "Previous thread boost relation: ")) + text3;
			}
			if (criticalThreadUiState.RequestedWorkerAvoid)
			{
				text2 = text2 + "\n" + ((criticalThreadUiState.SnapshotTicks > 0) ? (ThreadUi("工作线程观测数（快照）：", "Observed workers (snapshot): ") + criticalThreadUiState.Workers) : ThreadUi("工作线程避让尚未核验。", "Worker avoidance has not been verified."));
				if (criticalThreadUiState.WorkerMask != 0)
				{
					text2 = text2 + "\n" + ThreadUi("工作线程目标范围（快照）：", "Worker target range (snapshot): ") + CpuTopology.DescribeMask(criticalThreadUiState.WorkerMask);
				}
			}
			else if (criticalThreadUiState.Coverage != CriticalThreadCoverageState.CandidatesOnly)
			{
				text2 = text2 + "\n" + ThreadUi("该游戏进程的其他线程：未请求限制。", "Other threads in this game process: no restrictions requested.");
			}
			string[] candidateLabels = criticalThreadUiState.CandidateLabels;
			ulong[] candidateMasks = criticalThreadUiState.CandidateMasks;
			for (int i = 0; i < candidateMasks.Length; i++)
			{
				object obj2 = text2;
				text2 = string.Concat(obj2, "\n", ThreadUi("候选 ", "Candidate "), i + 1, ": ", (i < candidateLabels.Length) ? candidateLabels[i] : "", " → ", CpuTopology.DescribeMask(candidateMasks[i]));
			}
			if (!string.IsNullOrEmpty(criticalThreadUiState.Detail))
			{
				text2 = text2 + "\n" + criticalThreadUiState.Detail;
			}
			return text2;
		}
		private void SaveCriticalThreadSwitch(bool enabled)
		{
			if (criticalThreadProfile == null || criticalThreadSyncing)
			{
				return;
			}
			string error = null;
			CriticalThreadRule criticalThreadRule = (enabled ? CriticalThreadSwitchRule(out error) : null);
			if (error == null)
			{
				error = gameMode.SaveCriticalThreadRule(criticalThreadProfile, criticalThreadExpected, criticalThreadRule);
				if (error == null)
				{
					criticalThreadExpected = CriticalThreadRule.Raw(FindLibraryProfile(criticalThreadProfile));
				}
			}
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
				int num = Math.Max(0, -coreMatrixViewport.AutoScrollPosition.Y);
				int val = num;
				int num2 = coreMatrixViewport.ClientSize.Height;
				int top = cell.Top;
				int bottom = cell.Bottom;
				if (top < num)
				{
					val = Math.Max(0, top - Theme.S(4));
				}
				else if (bottom > num + num2)
				{
					val = bottom - num2 + Theme.S(4);
				}
				val = Math.Min(val, DockScrollLimit(coreMatrixViewport));
				if (val != num)
				{
					coreMatrixViewport.AutoScrollPosition = new Point(0, val);
				}
			}
		}
		private CoreViewState CaptureCoreViewState()
		{
			if (curPage != pageCoreScheduling || coreMatrixViewport == null || coreRightRail == null || coreRightRail.IsDisposed)
			{
				return null;
			}
			float num = Math.Max(0.01f, coreLayoutScale);
			CoreViewState coreViewState = new CoreViewState();
			coreViewState.MatrixY = (float)Math.Max(0, -coreMatrixViewport.AutoScrollPosition.Y) / num;
			coreViewState.RailY = (float)Math.Max(0, -coreRightRail.AutoScrollPosition.Y) / num;
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
