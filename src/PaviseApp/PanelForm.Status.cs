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
		private void RefreshAboutUpdate()
		{
			if (lblAboutUpdate != null && !lblAboutUpdate.IsDisposed)
			{
				bool flag = latestUpdate != null && UpdateChecker.IsNewer(latestUpdate.Latest, App.Version);
				lblAboutUpdate.Text = (flag ? Lang.F("site.about.found", latestUpdate.Latest) : ((latestUpdate != null) ? Lang.T("site.about.latest") : Lang.T("site.about.wait")));
				lblAboutUpdate.ForeColor = (flag ? Theme.Green : Theme.Faint);
			}
		}
		internal void RefreshAboutIcon()
		{
			if (aboutIcon == null || aboutIcon.IsDisposed)
			{
				return;
			}
			Image image = aboutIcon.Image;
			aboutIcon.Image = IconArt.Render(Theme.S(108), Theme.CurrentMode, true);
			if (image == null)
			{
				return;
			}
			try
			{
				image.Dispose();
			}
			catch
			{
			}
		}
		private void SyncAcRoster()
		{
			if (acRosterEntry == null)
			{
				return;
			}
			string[] array = ProtectedGameRoster.Names();
			((AcDrawerButton)acRosterEntry).Count = array.Length;
			acRosterEntry.AccessibleDescription = ((array.Length == 0) ? Lang.T("ac.roster.empty") : string.Join(" · ", array));
			lblAcRoster.Text = array.Length + AutoText(" 项", " entries");
			acClear.Enabled = array.Length > 0;
			bool flag = array.Length != acPresentedRoster.Length;
			int num = 0;
			while (!flag && num < array.Length)
			{
				flag = array[num] != acPresentedRoster[num];
				num++;
			}
			if (!flag && acRosterRows.Count != 0)
			{
				return;
			}
			foreach (Control acRosterRow in acRosterRows)
			{
				acRosterRow.Dispose();
			}
			acRosterRows.Clear();
			acPresentedRoster = array;
			if (array.Length == 0)
			{
				Label label = new Label();
				label.Name = "AntiCheatRosterEmpty";
				label.Text = AutoText("暂无兼容名单", "The compatibility list is empty");
				label.BackColor = Theme.Card;
				label.ForeColor = Theme.Dim;
				label.Font = Theme.UI(10f, false);
				label.TextAlign = ContentAlignment.MiddleLeft;
				label.Visible = acDrawerKind == "roster";
				Label label2 = label;
				acDrawerBody.Controls.Add(label2);
				acRosterRows.Add(label2);
			}
			else
			{
				string[] array2 = array;
				foreach (string text in array2)
				{
					AcReadOnlyButton acReadOnlyButton = new AcReadOnlyButton(text, "", false);
					acReadOnlyButton.Name = "AntiCheatRosterItem";
					acReadOnlyButton.Visible = acDrawerKind == "roster";
					acReadOnlyButton.AccessibleDescription = text;
					AcReadOnlyButton acReadOnlyButton2 = acReadOnlyButton;
					string captured = text;
					acReadOnlyButton2.Click += delegate
					{
						ShowAcInformation(AutoText("兼容名单", "Compatibility list"), captured);
					};
					acDrawerBody.Controls.Add(acReadOnlyButton2);
					acRosterRows.Add(acReadOnlyButton2);
				}
			}
			if (acDrawerKind == "roster")
			{
				LayoutAntiCheatViewport();
			}
		}
		private void RefreshAcGroupStates()
		{
			if (acModePicker != null && acModePicker.Index != (int)tamer.Mode)
			{
				acModePicker.Index = (int)tamer.Mode;
			}
			if (acModePicker != null)
			{
				acModePicker.AccessibleDescription = Lang.T("ac.mode." + AntiCheatModes.Token(tamer.Mode) + ".d");
			}
			for (int i = 0; i < acGroups.Count && i < acCards.Count; i++)
			{
				int num = tamer.GroupState(acGroups[i].Key);
				acCards[i].SetStatus(tamer.GroupStatus(acGroups[i].Key), (num == 1) ? Theme.Green : Theme.Dim);
				AntiCheatModuleRow row = acCards[i] as AntiCheatModuleRow;
				if (row != null)
				{
					string detail = tamer.AffinityDetails(acGroups[i].Key);
					if (row.AdditionalDetails != detail) { row.AdditionalDetails = detail; row.Invalidate(); }
				}
				acToggles[i].SetSilently(tamer.IsGroupEnabled(acGroups[i].Key));
			}
			if (acDrawerBody != null)
			{
				SyncAcRoster();
			}
		}
		private void RefreshAcList()
		{
			acArranging = true;
			try
			{
				while (acList.Controls.Count > 0)
				{
					acList.Controls[0].Dispose();
				}
				foreach (Control acReadOnlyCard in acReadOnlyCards)
				{
					acReadOnlyCard.Dispose();
				}
				acGroups.Clear();
				acCards.Clear();
				acToggles.Clear();
				acReadOnlyCards.Clear();
				AcGroup[] groups = AntiCheatCatalog.Groups;
				foreach (AcGroup acGroup in groups)
				{
					if (!acGroup.Suppressible)
					{
						AddAcReadOnly(acGroup.Name, Lang.T("ac." + acGroup.Key + ".d"), acGroup.Procs, acGroup.RecordOnly);
						continue;
					}
					AcGroup captured = acGroup;
					Toggle toggle = MakeSwitch(tamer.IsGroupEnabled(acGroup.Key), null);
					toggle.Name = "anticheat_" + acGroup.Key;
					toggle.AccessibleName = acGroup.Name;
					toggle.Bg = Theme.Card;
					toggle.CheckedChanged += delegate
					{
						tamer.SetGroupEnabled(captured.Key, toggle.Checked);
						RefreshAcGroupStates();
					};
					AntiCheatModuleRow card = new AntiCheatModuleRow
					{
						Name = "AntiCheatCard_" + acGroup.Key,
						Title = acGroup.Name,
						Provider = ((acGroup.Key == "ace") ? AutoText("腾讯", "Tencent") : ((acGroup.Key == "eac") ? "Epic" : "")),
						IconKey = acGroup.Key,
						Desc = Lang.T("ac." + acGroup.Key + ".d"),
						AdditionalDetails = string.Join(" · ", acGroup.Procs) + "\r\n\r\n" + Lang.T("ac.mode.disclaimer")
					};
					if (acGroup.Key == "ace")
					{
						card.Title = AutoText("ACE 反作弊专家", "ACE Anti-Cheat Expert");
					}
					if (acGroup.Key == "eac")
					{
						card.Title = "EasyAntiCheat";
					}
					card.Host(toggle);
					card.DetailsRequested = delegate
					{
						ShowAcInformation(captured.Name, card.DetailsText);
					};
					card.ContentSizeChanged = LayoutAntiCheatViewport;
					acList.Controls.Add(card);
					acGroups.Add(acGroup);
					acCards.Add(card);
					acToggles.Add(toggle);
				}
				AcProtectionGroup[] protectionOnlyGroups = AntiCheatCatalog.ProtectionOnlyGroups;
				foreach (AcProtectionGroup acProtectionGroup in protectionOnlyGroups)
				{
					AddAcReadOnly(acProtectionGroup.Name, Lang.T("ac." + acProtectionGroup.Key + ".d"), acProtectionGroup.PatternsForDisplay(), false);
				}
				((AcDrawerButton)acOtherToggle).Count = acReadOnlyCards.Count;
				RefreshAcGroupStates();
			}
			finally
			{
				acArranging = false;
			}
			LayoutAntiCheatViewport();
		}
		private void RefreshAuditAfterFix()
		{
			AuditReport previous = auditReport;
			if (previous == null)
			{
				auditIrqFix = null;
				StartAudit();
			}
			else
			{
				if (runtimeShutdownBusy || base.IsDisposed || Interlocked.CompareExchange(ref auditBusy, 1, 0) != 0)
				{
					return;
				}
				GpuIrqResult irqFix = auditIrqFix;
				auditIrqFix = null;
				int version = Interlocked.Increment(ref auditRunVersion);
				SetAuditButtons(false);
				lblAuditStatus.Text = PerformancePolicy.Text("正在刷新检查结果…", "Refreshing the results…");
				try
				{
					QueueUiStateWork(delegate
					{
						AuditReport report = null;
						try
						{
							report = SystemAudit.Refresh(previous, irqFix);
						}
						catch
						{
						}
						try
						{
							PostUiStateResult(delegate
							{
								CompleteAuditRefresh(version, report);
							});
						}
						catch
						{
							if (version == Volatile.Read(ref auditRunVersion))
							{
								Interlocked.Exchange(ref auditBusy, 0);
							}
						}
					});
				}
				catch
				{
					CompleteAuditRefresh(version, null);
				}
			}
		}
		private void UpdateFixAllState()
		{
			int num = 0;
			foreach (AuditFix value in BuildAuditFixes().Values)
			{
				try
				{
					if (value.CanFix())
					{
						num++;
					}
				}
				catch
				{
				}
			}
			btnAuditFixAll.Enabled = num > 0;
			btnAuditFixAll.Text = ((num > 0) ? Lang.F("audit.fixall", num) : Lang.T("audit.fixall.none"));
			btnAuditFixAll.Invalidate();
		}
		private void SyncManualPerformanceStatus(SettingCard card, string key, bool on, bool supported, Func<string> execution)
		{
			string text = ((execution != null) ? execution() : gameMode.AutomaticExecutionState(key));
			if (key == LaptopPerfMode.PreferenceKey)
			{
				card.SetStatus(text, gameMode.LaptopPerformanceState == "failed" || gameMode.LaptopPerformanceState == "restore-failed"
					? Theme.Warning : gameMode.LaptopPerformanceState == "applied" ? Theme.Accent : Theme.Dim);
				return;
			}
			int num;
			switch (text)
			{
			default:
				num = ((text == "cooldown") ? 1 : 0);
				break;
			case "failed":
			case "restore-failed":
			case "stopped":
				num = 1;
				break;
			}
			bool flag = (byte)num != 0;
			string text2;
			if (flag)
			{
				text2 = GameMode.AutomationStateLabel(text);
			}
			else if (!supported)
			{
				text2 = AutoText("本机不适用", "Not supported here");
			}
			else if (PerformancePolicy.IsIndependentManual(key))
			{
				object obj;
				switch (text)
				{
				default:
					obj = (on ? AutoText("已开启", "On") : AutoText("已关闭", "Off"));
					break;
				case "applied":
				case "configured":
				case "pending-reboot":
				case "restore-pending-reboot":
				case "unverified":
				case "unsupported":
					obj = GameMode.AutomationStateLabel(text);
					break;
				}
				string text3 = (string)obj;
				text2 = text3;
			}
			else
			{
				switch (text)
				{
				case "applied":
				case "configured":
				case "pending-reboot":
				case "restore-pending-reboot":
				case "unverified":
				case "unsupported":
					text2 = GameMode.AutomationStateLabel(text);
					break;
				default:
					text2 = (on ? AutoText("已开启 · 按适用条件执行", "Enabled · runs when eligible") : AutoText("已关闭", "Off"));
					break;
				}
			}
			if (PerformancePolicy.IsGamePriorityOption(key) && !gameMode.AutomationPolicySnapshot.BoostGame && !flag)
			{
				text2 = AutoText("游戏资源优先级总开关已关闭", "Game resource priorities are switched off");
			}
			bool flag2 = flag || text == "pending-reboot" || text == "restore-pending-reboot" || text == "unverified";
			card.SetStatus(text2, flag2 ? Theme.Warning : ((text == "applied" && supported) ? Theme.Accent : Theme.Dim));
		}
		private void RefreshAutomationPage()
		{
			if (automationUpdating || performanceTabs == null || performanceTabs.IsDisposed || performanceTabs.Parent != pagePolicy)
			{
				return;
			}
			automationUpdating = true;
			try
			{
				using (PolicyResolver.BeginReadScope())
				{
					foreach (Action item in automationSync)
					{
						item();
					}
					SyncPerformanceEnvironmentStatus();
				}
				SyncConfigurationPresetControls();
				LayoutPerformanceViewport();
			}
			finally
			{
				automationUpdating = false;
			}
		}
		private void SyncCoreEditor(GameProfile profile)
		{
			coreEditor.Visible = profile != null;
			if (profile == null)
			{
				coreDraftProfile = null;
				coreRebuildDraft = null;
				return;
			}
			if (coreRebuildDraft != null && curPage == pageCoreScheduling)
			{
				CoreEditorDraftSnapshot coreEditorDraftSnapshot = coreRebuildDraft;
				coreRebuildDraft = null;
				if (string.Equals(coreEditorDraftSnapshot.Profile, profile.Id, StringComparison.OrdinalIgnoreCase))
				{
					coreDraftProfile = profile.Id;
					coreDraftExpected = coreEditorDraftSnapshot.Expected;
					coreDraftTopology = coreEditorDraftSnapshot.Topology;
					coreSaveError = coreEditorDraftSnapshot.Error;
					coreEditMatrix.ShowSelectionImmediately(coreEditorDraftSnapshot.Mask);
					coreIsolationToggle.SetSilently(coreEditorDraftSnapshot.Isolation);
				}
			}
			coreEnableToggle.SetSilently(PolicyResolver.For(profile).AutoCoresAllowed);
			coreEnableToggle.Enabled = PolicyResolver.For(profile).ManualCores != null;
			coreEditorTitle.Text = PerformancePolicy.Text("游戏核心范围", "Game CPU range");
			coreEditMatrix.DisabledMask = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
			string text = ManualCorePlan.Raw(profile);
			if (coreDraftProfile != profile.Id || coreDraftExpected != text)
			{
				bool flag = coreDraftProfile == profile.Id;
				coreDraftProfile = profile.Id;
				coreDraftExpected = text;
				RestoreCoreSelection();
				coreSaveError = (flag ? "schedule.manual.changed" : null);
			}
			CoreDraftChanged();
		}
		private void SyncCoreRuntimePresentation()
		{
			AutoCoreStatus status = AutoCores.Status;
			bool flag = status.ProfileId == coreDraftProfile && status.Watching;
			ManualCorePlan manualCorePlan = ManualCorePlan.Parse(coreDraftExpected);
			string text = ((manualCorePlan == null) ? PerformancePolicy.Text("尚未保存游戏核心范围。", "No game CPU range saved.") : (PerformancePolicy.Text("已保存：", "Saved: ") + CpuTopology.DescribeMask(manualCorePlan.GameMask)));
			if (!flag)
			{
				text = text + "\n" + PerformancePolicy.Text("本局硬绑定：等待游戏进程验证。", "Session hard affinity: awaiting game-process verification.");
			}
			else if (status.Blocked != null)
			{
				text = text + "\n" + PerformancePolicy.Text("本局硬绑定已停用：", "Session hard affinity stopped: ") + Lang.T(status.Blocked);
			}
			else if (!status.Manual || status.ExpectedMask == 0)
			{
				text = text + "\n" + PerformancePolicy.Text("本局未采用手动选核；已保存的修改下次启动游戏生效。", "This session has no manual CPU range; saved changes apply on the next game launch.");
			}
			else
			{
				text = text + "\n" + PerformancePolicy.Text("本局目标：", "Session target: ") + CpuTopology.DescribeMask(status.ExpectedMask);
				string text2 = text;
				text = text2 + "\n" + PerformancePolicy.Text("最近回读：", "Last readback: ") + (status.AffinityRead ? CpuTopology.DescribeMask(status.ObservedMask) : PerformancePolicy.Text("尚未确认", "Unconfirmed")) + (status.AffinityVerified ? " · " + AutoCores.AffinityProtectionText(status) : PerformancePolicy.Text(" · 硬绑定未生效", " · hard affinity not active"));
				if (manualCorePlan == null || manualCorePlan.GameMask != status.ExpectedMask || manualCorePlan.Isolation != status.ManualIsolation)
				{
					text = text + "\n" + PerformancePolicy.Text("新保存的范围／隔离设置下次启动游戏生效。", "New CPU/isolation settings apply on the next game launch.");
				}
			}
			coreEditorRuntime.Text = text;
			coreEditorRuntime.ForeColor = ((flag && (status.Blocked != null || status.Pending)) ? Theme.Warning : ((flag && status.AffinityVerified) ? Theme.Accent : Theme.Dim));
			Color currentTone = Theme.Dim;
			string currentValue;
			string currentDetail;
			if (!flag)
			{
				currentValue = PerformancePolicy.Text("尚未确认", "Not verified yet");
				currentDetail = PerformancePolicy.Text("游戏运行后，读取并核对实际核心范围。", "The actual CPU range is checked once the game runs.");
			}
			else if (status.Blocked != null)
			{
				currentValue = PerformancePolicy.Text("硬绑定已停用", "Hard affinity stopped");
				currentDetail = Lang.T(status.Blocked);
				currentTone = Theme.Warning;
			}
			else if (!status.Manual || status.ExpectedMask == 0)
			{
				currentValue = PerformancePolicy.Text("本局未采用手动范围", "No manual range this session");
				currentDetail = PerformancePolicy.Text("保存的方案不会追改本局。", "Saved changes do not replace this session's plan.");
			}
			else if (status.AffinityRead && status.AffinityVerified)
			{
				currentValue = CpuTopology.DescribeMask(status.ObservedMask);
				currentDetail = AutoCores.AffinityProtectionText(status);
				currentTone = Theme.Accent;
			}
			else
			{
				currentValue = PerformancePolicy.Text("尚未确认生效", "Activation not verified");
				currentDetail = PerformancePolicy.Text("目标：", "Target: ") + CpuTopology.DescribeMask(status.ExpectedMask);
				currentTone = Theme.Warning;
			}
			string savedValue = ((manualCorePlan == null) ? PerformancePolicy.Text("尚未选择", "No range selected") : CpuTopology.DescribeMask(manualCorePlan.GameMask));
			string savedDetail = ((manualCorePlan == null) ? PerformancePolicy.Text("点击下方核心，选择会自动保存。", "Select cores below to save a range.") : ((!coreEnableToggle.Checked) ? PerformancePolicy.Text("方案已保存，核心调度当前关闭。", "Plan saved; core scheduling is switched off.") : (PerformancePolicy.Text("下次启动游戏使用此范围", "Applied on the next game launch") + (manualCorePlan.Isolation ? PerformancePolicy.Text(" · 含核心隔离", " · with isolation") : ""))));
			string text3 = ((manualCorePlan == null) ? null : manualCorePlan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask));
			if (text3 != null)
			{
				savedDetail = PerformancePolicy.Text("需调整：", "Needs adjustment: ") + Lang.T(text3);
			}
			corePlanPreview.SetState(PerformancePolicy.Text("本局实际范围", "Current session"), currentValue, currentDetail, currentTone, PerformancePolicy.Text("已保存范围", "Saved range"), savedValue, savedDetail, (text3 != null) ? Theme.Warning : Theme.Fg);
		}
		private void SyncCorePage()
		{
			if (lblCoreState != null && !lblCoreState.IsDisposed)
			{
				coreWorkspaceSyncing = true;
				try
				{
					AutoCoreStatus status = AutoCores.Status;
					GameProfile gameProfile = CorePageProfile();
					SyncCoreEditor(gameProfile);
					bool flag = status.Pending || CoreIsolationClient.State == "schedule.isolation.pending";
					bool flag2 = gameProfile != null && status.ProfileId == gameProfile.Id;
					lblCoreState.Text = Lang.T(flag ? "schedule.auto.state.pending" : ((flag2 && status.Blocked != null) ? "schedule.auto.state.blocked" : "schedule.manual.mode"));
					lblCoreReason.Text = Lang.T(flag ? "schedule.isolation.pending" : ((flag2 && status.Blocked != null) ? status.Blocked : ((gameProfile == null) ? "schedule.manual.pickgame" : "schedule.manual.required")));
					lblCoreRule.Text = Lang.T("schedule.manual.rule");
					lblCoreTelemetry.Text = "";
					ulong num = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
					ulong num2 = ((num != 0) ? LegacyReservedCores.PendingReboot(num) : 0);
					ulong num3 = ((num2 == 0) ? LegacyReservedCores.Suspect() : 0);
					bool flag3 = num3 != 0 && (num3 & ~num) == 0;
					lblCoreReserved.Text = ((num2 != 0) ? Lang.F("schedule.auto.reserved.legacy", CpuTopology.DescribeMask(num2)) : (flag3 ? Lang.F("schedule.auto.reserved.suspect", CpuTopology.DescribeMask(num3)) : ((num != 0) ? Lang.F("schedule.auto.reserved.other", CpuTopology.DescribeMask(num)) : "")));
					btnCoreClearReserved.Visible = flag3;
					ManualCorePlan manualCorePlan = ((gameProfile != null) ? PolicyResolver.For(gameProfile).ManualCores : null);
					coreMatrix.PrimaryTag = Lang.T("schedule.manual.selected");
					coreMatrix.DisabledMask = num;
					coreMatrix.Selected = ((manualCorePlan != null) ? manualCorePlan.GameMask : 0);
					coreMatrix.SchedulingGameMask = ((manualCorePlan != null) ? manualCorePlan.GameMask : 0);
					coreMatrix.SchedulingIsolationMask = ((flag2 && status.Manual && status.ManualIsolation && status.Engaged && status.Plan != null) ? status.Plan.Exclusive : 0);
					lblCorePlan.Text = ((manualCorePlan != null) ? Lang.F("schedule.manual.plan", CpuTopology.DescribeMask(manualCorePlan.GameMask)) : Lang.T("schedule.manual.required"));
				}
				finally
				{
					coreWorkspaceSyncing = false;
				}
				LayoutCorePage();
			}
		}
		private void SyncCoreShortcuts()
		{
			if (coreQuickPanel == null)
			{
				return;
			}
			foreach (PillButton control in coreQuickPanel.Controls)
			{
				CoreQuickChoice coreQuickChoice = (CoreQuickChoice)control.Tag;
				ulong num = CoreShortcutTarget(coreQuickChoice);
				control.Enabled = CpuTopology.CountSetBits(num) >= 1 && (coreQuickChoice.Threads == 0 || num != coreEditMatrix.Selected);
				BtnKind btnKind = ((coreQuickChoice.Threads == 0 && num != 0 && num == coreEditMatrix.Selected) ? BtnKind.Primary : BtnKind.Normal);
				if (control.Kind != btnKind)
				{
					control.Kind = btnKind;
					control.Invalidate();
				}
			}
		}
		private void SyncCriticalThreadEditor(GameProfile profile)
		{
			if (criticalThreadEditor == null || criticalThreadEditor.IsDisposed)
			{
				return;
			}
			criticalThreadEditor.Visible = profile != null;
			if (profile == null)
			{
				criticalThreadProfile = null;
				return;
			}
			criticalThreadAll = CpuTopology.AllMask;
			criticalThreadPhysical = CpuTopology.PhysicalCoreMasks();
			criticalThreadReserved = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask;
			CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(profile);
			criticalThreadAllowed = criticalThreadAllowedRange.Mask;
			criticalThreadAllowedError = criticalThreadAllowedRange.Error;
			criticalThreadTopologyTrusted = IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled;
			string text = CriticalThreadRule.Raw(profile);
			if (criticalThreadProfile != profile.Id || criticalThreadExpected != text)
			{
				bool flag = criticalThreadProfile == profile.Id;
				criticalThreadProfile = profile.Id;
				criticalThreadExpected = text;
				criticalThreadError = (flag ? ThreadUi("配置已从外部变化，已重新载入。", "Configuration changed externally and was reloaded.") : null);
				if (text != null && CriticalThreadRule.Parse(text) == null)
				{
					criticalThreadError = ThreadUi("已存规则无法解析；请关闭开关后重新打开。", "The saved rule is invalid; turn the switch off and on again.");
				}
			}
			criticalThreadView = gameMode.CriticalThreadView(profile.Id);
			bool coupled;
			criticalThreadCompanion.Text = CriticalThreadCompanionText(profile, out coupled);
			criticalThreadCompanion.ForeColor = (coupled ? Theme.Warning : Theme.Dim);
			criticalThreadSyncing = true;
			try
			{
				criticalThreadEnabled.Checked = text != null;
			}
			finally
			{
				criticalThreadSyncing = false;
			}
			criticalThreadEnabled.AccessibleName = profile.Name + " · " + criticalThreadEnabled.Text;
			criticalThreadEnabled.AccessibleDescription = criticalThreadHelp.Text;
			RefreshCriticalThreadCard();
		}
		private void RefreshCriticalThreadCard()
		{
			string error;
			CriticalThreadRule criticalThreadRule = CriticalThreadSwitchRule(out error);
			CriticalThreadRule criticalThreadRule2 = CriticalThreadRule.ParseForRuntime(criticalThreadExpected);
			criticalThreadMode.AccessibleDescription = criticalThreadRule2 != null && criticalThreadRule2.ManualTarget
				? ThreadUi("手动 · ", "Manual · ") + CriticalThreadRule.ProcessorName(criticalThreadRule2.Targets[0])
				: ThreadUi("自动选核", "Automatic CPU");
			string text = ((criticalThreadExpected == null) ? ThreadUi("默认关闭；打开右上角开关即可启用。", "Off by default. Turn on the switch above to enable it.") : ((criticalThreadRule2 != null && !criticalThreadRule2.Automatic) ? ThreadUi("此游戏保存的是手动指定核心的旧规则，仍按原规则执行；关闭再打开开关即改为自动选核。", "This game keeps an older rule with manually chosen cores, which still applies; turn the switch off and on to choose cores automatically.") : ThreadUi("已启用；实际状态见下方。", "On; see the runtime state below.")));
			string text2 = ((criticalThreadExpected == null) ? error : criticalThreadAllowedError);
			criticalThreadResult.Text = ((criticalThreadError == null) ? "" : (criticalThreadError + "\n")) + (text2 ?? text);
			criticalThreadResult.ForeColor = ((criticalThreadError != null || text2 != null) ? Theme.Warning : Theme.Dim);
			criticalThreadEnabled.Enabled = criticalThreadExpected != null || criticalThreadRule != null;
			bool flag = criticalThreadView != null && criticalThreadView.Pending;
			if (flag && criticalThreadError == null && text2 == null)
			{
				Label label = criticalThreadResult;
				label.Text = label.Text + "\n" + ThreadUi("偏好可以保存；恢复完成前不会应用新规则。", "Preferences can be saved; new rules wait until recovery completes.");
			}
			criticalThreadRuntime.Text = CriticalThreadRuntimeText();
			criticalThreadIssue = flag || criticalThreadError != null || text2 != null || (criticalThreadRule2 != null && !criticalThreadRule2.Automatic);
			criticalThreadResult.Visible = criticalThreadIssue;
			LayoutCriticalThreadEditor();
		}
		private void RefreshPerformanceEnvironmentObservation()
		{
			QueueEnvironmentObservation(true);
			SyncPerformanceTuningEnvironment();
			SyncEnvironmentPerformanceOptions();
		}
		private void SyncGameLowLatencyControl()
		{
			if (swGameLowLatency != null && cardGameLowLatency != null)
			{
				bool gameLowLatency = PerformancePolicy.GameLowLatency;
				swGameLowLatency.SetSilently(gameLowLatency);
				SyncManualPerformanceStatus(cardGameLowLatency, "NvLowLat", gameLowLatency, NvApi.Available, null);
			}
		}
		private void SyncEnvStatus()
		{
			ApplyEnvironmentObservation();
			QueueEnvironmentObservation(false);
		}
		private void RefreshEnvironmentStateAsync()
		{
			if (UiActive)
			{
				SyncEnvironmentToggles();
			}
		}
		private void SyncEnvironmentToggles()
		{
			SyncPerformanceEnvironmentStatus();
			SyncEnvironmentPrivacy();
			SyncEnvStatus();
		}
		private void SyncHagsPresentation()
		{
			if (swHags != null && cardHags != null)
			{
				HagsUiPresentation hagsUiPresentation = CachedHagsPresentation();
				swHags.SetSilently(hagsUiPresentation.Checked);
				swHags.Enabled = hagsUiPresentation.Editable;
				cardHags.Desc = PerformanceGraphicsDetail("Hags");
				cardHags.SetStatus(hagsUiPresentation.Status, hagsUiPresentation.Ink);
				cardHags.SetLock("", false);
			}
		}
		private void SyncPerformanceEnvironmentStatus()
		{
			SyncEnvironmentPerformanceOptions();
			SyncPerformanceTuningEnvironment();
			SyncEnvironmentGameDvr();
			SyncGameLowLatencyControl();
			QueueEnvironmentObservation(false);
		}
		private void SyncEnvironmentPerformanceOptions()
		{
			foreach (KeyValuePair<string, Toggle> environmentPerformanceSwitch in environmentPerformanceSwitches)
			{
				string key = environmentPerformanceSwitch.Key;
				bool flag = CachedEnvironmentPreference(key);
				EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
				bool flag2 = snapshot != null && snapshot.GlobalTimerOwned;
				environmentPerformanceSwitch.Value.SetSilently(flag);
				environmentPerformanceSwitch.Value.Enabled = (key == "GmCacheWarmV2" || snapshot != null) && (key != "AutoGlobalTimer" || Native.OsBuild() >= 20348 || flag || flag2);
				SettingCard settingCard = environmentPerformanceCards[key];
				string text = gameMode.AutomaticExecutionState(key);
				object obj;
				if (key == "GmCacheWarmV2")
				{
					obj = (flag ? gameMode.CacheWarmStatus : Lang.T("env.performance.off"));
				}
				else
				{
					switch (text)
					{
					default:
						obj = (((key == "AutoGlobalTimer" && Native.OsBuild() < 20348 && !flag && !flag2) || text == "unsupported") ? Lang.T("lock.na") : (flag ? Lang.T("env.performance.pending") : Lang.T("env.performance.off")));
						break;
					case "restored":
						obj = PerformanceTuning.Label(text);
						break;
					case "ready":
						obj = Lang.T("env.performance.ready");
						break;
					case "timer-locked":
					case "timer-limited":
					case "timer-unverified":
						obj = GameMode.AutomationStateLabel(text);
						break;
					case "timer-requested":
						obj = Lang.T("env.performance.requested");
						break;
					case "failed":
					case "restore-failed":
						obj = Lang.T("env.performance.failed");
						break;
					case "stopped":
						obj = PerformanceTuning.Label(text);
						break;
					case "restore-pending-reboot":
						obj = Lang.T("env.performance.restore.reboot");
						break;
					case "pending-reboot":
						obj = Lang.T("env.performance.reboot");
						break;
					case "unverified":
						obj = Lang.T("env.performance.unverified");
						break;
					}
				}
				string text2 = (string)obj;
				Color ink;
				switch (text)
				{
				default:
					ink = (flag ? Theme.Accent : Theme.Dim);
					break;
				case "failed":
				case "restore-failed":
				case "stopped":
					ink = Theme.Warning;
					break;
				}
				settingCard.SetStatus(text2, ink);
				if (key == "AutoGlobalTimer")
				{
					string globalTimerResolutionDetail = gameMode.GlobalTimerResolutionDetail;
					settingCard.Desc = GlobalTimerDescription(text, flag, globalTimerResolutionDetail);
					settingCard.AccessibleDescription = settingCard.Desc;
				}
			}
		}
		private void SyncEnvironmentPrivacy()
		{
			foreach (PrivacyOptionsPanel performancePrivacyPanel in performancePrivacyPanels)
			{
				if (!performancePrivacyPanel.IsDisposed)
				{
					performancePrivacyPanel.SyncCached();
					performancePrivacyPanel.RefreshIfNeeded();
				}
			}
		}
		private void SyncEnvironmentGameDvr()
		{
			if (swEnvironmentGameDvr != null && cardEnvironmentGameDvr != null)
			{
				swEnvironmentGameDvr.SetSilently(gameMode.KillGameDvr);
				string text = gameMode.AutomaticExecutionState("GameDvrOff");
				bool flag = text == "failed" || text == "restore-failed" || text == "stopped";
				cardEnvironmentGameDvr.SetStatus(EnvironmentGameDvrStatus(), flag ? Theme.Warning : (gameMode.KillGameDvr ? Theme.Accent : Theme.Dim));
			}
		}
		private void SyncPerformanceTuningEnvironment()
		{
			if (swPerformanceEnvironmentBundle == null || swPerformanceEnvironmentBundle.IsDisposed || cardPerformanceEnvironmentBundle == null || cardPerformanceEnvironmentBundle.IsDisposed)
			{
				return;
			}
			EnvironmentTuningObservation cachedEnvironmentTuning = CachedEnvironmentTuning;
			bool flag = PerformanceTuning.EnvironmentBundleBusy();
			bool flag2 = environmentBundleOverride ?? (cachedEnvironmentTuning != null && cachedEnvironmentTuning.BundleOn);
			string text = (flag ? "working" : ((cachedEnvironmentTuning == null) ? "unverified" : cachedEnvironmentTuning.BundleState));
			swPerformanceEnvironmentBundle.SetSilently(flag2);
			swPerformanceEnvironmentBundle.Enabled = cachedEnvironmentTuning != null && !flag;
			string text2 = ((cachedEnvironmentTuning == null) ? EnvironmentBundlePerformanceDetail() : cachedEnvironmentTuning.BundleDetail);
			if (cardPerformanceEnvironmentBundle.Desc != text2)
			{
				SettingCard settingCard = cardPerformanceEnvironmentBundle;
				settingCard.Desc = text2;
				if (settingCard is PerformanceFeatureCard)
				{
					((PerformanceFeatureGrid)settingCard.Parent).Reflow();
				}
				else
				{
					int num = Theme.S(FullTextCardHeight(text2, ScrollContentW, swPerformanceEnvironmentBundle, 100) + 17);
					int num2 = (settingCard.Collapsible ? settingCard.ExpandedHeight : settingCard.Height);
					Dictionary<Control, int> value;
					if (settingCard.Parent != null && stackBase.TryGetValue(settingCard.Parent, out value))
					{
						int num3 = value[settingCard];
						foreach (Control item in new List<Control>(value.Keys))
						{
							if (value[item] > num3)
							{
								value[item] += num - num2;
							}
						}
					}
					settingCard.ExpandedHeight = num;
					settingCard.Height = ((settingCard.Collapsible && !settingCard.Expanded) ? settingCard.CollapsedHeight : num);
					if (settingCard.Parent != null)
					{
						RestackCards(settingCard.Parent);
					}
				}
			}
			SettingCard settingCard2 = cardPerformanceEnvironmentBundle;
			string obj = ((flag || cachedEnvironmentTuning == null) ? PerformanceTuning.Label(text) : cachedEnvironmentTuning.BundleLabel);
			Color ink;
			switch (text)
			{
			default:
				ink = (flag2 ? Theme.Accent : Theme.Dim);
				break;
			case "failed":
			case "stopped":
			case "restore-failed":
			case "partial":
				ink = Theme.Warning;
				break;
			}
			settingCard2.SetStatus(obj, ink);
			SyncPerformanceCpuMitigations();
			SyncPerformanceStorageEnvironment();
			if (swPerformanceEnvironmentQuantum != null && !swPerformanceEnvironmentQuantum.IsDisposed && cardPerformanceEnvironmentQuantum != null && !cardPerformanceEnvironmentQuantum.IsDisposed)
			{
				bool flag3 = (environmentQuantumOverride.HasValue ? (environmentQuantumOverride.Value != 0) : (cachedEnvironmentTuning != null && cachedEnvironmentTuning.QuantumOn));
				string text3 = (flag ? "working" : ((cachedEnvironmentTuning == null) ? "unverified" : cachedEnvironmentTuning.QuantumState));
				int num4 = environmentQuantumOverride ?? ((cachedEnvironmentTuning != null) ? cachedEnvironmentTuning.QuantumSelection : 0);
				swPerformanceEnvironmentQuantum.SetSilently(num4);
				swPerformanceEnvironmentQuantum.AccessibleDescription = QuantumProfiles.Description(num4);
				swPerformanceEnvironmentQuantum.Enabled = cachedEnvironmentTuning != null && !flag;
				bool flag4 = text3 == "failed" || text3 == "stopped" || text3 == "restore-failed";
				bool flag5 = !flag4 && text3 != "working" && cachedEnvironmentTuning != null && cachedEnvironmentTuning.LegacyQuantum;
				string text4 = (flag5 ? PerformancePolicy.Text("旧版校正记录待恢复 · 选择「恢复原配置」", "Legacy correction pending · choose Restore original") : ((text3.Length == 0) ? Lang.T(flag3 ? "env.performance.pending" : "env.performance.off") : PerformanceTuning.Label(text3)));
				cardPerformanceEnvironmentQuantum.SetStatus(text4, (flag4 || flag5) ? Theme.Warning : (flag3 ? Theme.Accent : Theme.Dim));
				PerformanceFeatureCard quantumCard = cardPerformanceEnvironmentQuantum as PerformanceFeatureCard;
				if (quantumCard != null)
				{
					bool restart = text3 == "pending-reboot" || text3 == "restore-pending-reboot";
					quantumCard.RestartScope = restart ? PerformanceRestartScope.SystemRestart : PerformanceRestartScope.None;
					quantumCard.RestartText = restart ? AutoText("需重启电脑", "PC restart required") : AutoText("优先在线应用", "Online update preferred");
				}
			}
		}
		private void SyncPerformanceStorageEnvironment()
		{
			if (swPerformanceEnvironmentStorage == null || swPerformanceEnvironmentStorage.IsDisposed || cardPerformanceEnvironmentStorage == null || cardPerformanceEnvironmentStorage.IsDisposed)
			{
				return;
			}
			EnvironmentTuningObservation cachedEnvironmentTuning = CachedEnvironmentTuning;
			bool flag = PerformanceTuning.EnvironmentBundleBusy();
			bool flag2 = environmentStorageOverride ?? (cachedEnvironmentTuning != null && cachedEnvironmentTuning.StorageOn);
			string text = (flag ? "working" : ((cachedEnvironmentTuning == null) ? "unverified" : cachedEnvironmentTuning.StorageState));
			swPerformanceEnvironmentStorage.SetSilently(flag2);
			swPerformanceEnvironmentStorage.Enabled = cachedEnvironmentTuning != null && !flag;
			SettingCard settingCard = cardPerformanceEnvironmentStorage;
			string text2 = ((cachedEnvironmentTuning == null) ? StoragePerformanceDetail(text) : cachedEnvironmentTuning.StorageDetail);
			if (settingCard.Desc != text2)
			{
				settingCard.Desc = text2;
				if (settingCard is PerformanceFeatureCard)
				{
					((PerformanceFeatureGrid)settingCard.Parent).Reflow();
				}
				else
				{
					int num = Theme.S(FullTextCardHeight(text2, ScrollContentW, swPerformanceEnvironmentStorage, 100) + 17);
					int num2 = (settingCard.Collapsible ? settingCard.ExpandedHeight : settingCard.Height);
					Dictionary<Control, int> value;
					int value2;
					if (settingCard.Parent != null && stackBase.TryGetValue(settingCard.Parent, out value) && value.TryGetValue(settingCard, out value2))
					{
						foreach (Control item in new List<Control>(value.Keys))
						{
							if (value[item] > value2)
							{
								value[item] += num - num2;
							}
						}
					}
					settingCard.ExpandedHeight = num;
					settingCard.Height = ((settingCard.Collapsible && !settingCard.Expanded) ? settingCard.CollapsedHeight : num);
					if (settingCard.Parent != null)
					{
						RestackCards(settingCard.Parent);
					}
				}
			}
			string obj = ((flag || cachedEnvironmentTuning == null) ? PerformanceTuning.Label(text) : cachedEnvironmentTuning.StorageLabel);
			Color ink;
			switch (text)
			{
			default:
				ink = (flag2 ? Theme.Accent : Theme.Dim);
				break;
			case "failed":
			case "stopped":
			case "restore-failed":
			case "partial":
			case "recovery-pending":
				ink = Theme.Warning;
				break;
			}
			settingCard.SetStatus(obj, ink);
		}
		private void RefreshGames()
		{
			if (base.IsDisposed || lstGames == null || lstGames.IsDisposed)
			{
				return;
			}
			Interlocked.Increment(ref runningVersion);
			if (swAutoAdd != null)
			{
				swAutoAdd.Checked = gameMode.AutoAddFullscreen;
			}
			string text = null;
			GameLibraryItem selectedItem = lstGames.SelectedItem;
			if (selectedItem != null && selectedItem.Profile != null)
			{
				text = selectedItem.Profile.Id;
			}
			List<GameProfile> profiles = gameMode.GetProfiles();
			Dictionary<string, bool> dictionary = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			foreach (GameLibraryItem item in lstGames.Items)
			{
				if (item != null && item.Profile != null && !string.IsNullOrEmpty(item.Profile.ExecutablePath))
				{
					dictionary[item.Profile.ExecutablePath] = item.Running;
				}
			}
			List<GameLibraryItem> list = new List<GameLibraryItem>();
			foreach (GameProfile item2 in profiles)
			{
				list.Add(new GameLibraryItem(item2, RunningIn(dictionary, item2.ExecutablePath), gameMode.HasRendererObservation(item2)));
			}
			lstGames.SetItems(list);
			if (text != null)
			{
				SelectProfile(text);
			}
			bool showEmpty = lstGames.Items.Count == 0;
			lstGames.Visible = true;
			if (gameListPanel != null)
			{
				gameListPanel.ShowEmpty = showEmpty;
				gameListPanel.Invalidate();
			}
			if (lblLibraryCount != null)
			{
				lblLibraryCount.Text = AutoText(profiles.Count + " 款游戏", profiles.Count + " games");
			}
			SyncLibraryHint();
			RefreshLibraryInspector();
			if (UiActive && curPage == pageLibrary)
			{
				RefreshGameRunningStates(true);
			}
		}
		private void SyncLibraryHint()
		{
			if (lblLibraryHint != null)
			{
				lblLibraryHint.Text = Lang.T("v15.library.drop");
				lblLibraryHint.ForeColor = Theme.Dim;
			}
		}
		private void RefreshGameRunningStates(bool force = false)
		{
			if (base.IsDisposed || !UiActive || lstGames == null || lstGames.IsDisposed)
			{
				return;
			}
			long ticks = DateTime.UtcNow.Ticks;
			if (!force && ticks < Interlocked.Read(ref nextRunningProbeTicks))
			{
				return;
			}
			if (Interlocked.Exchange(ref runningBusy, 1) == 1)
			{
				if (force)
				{
					Interlocked.Exchange(ref runningPending, 1);
					Interlocked.Exchange(ref nextRunningProbeTicks, 0L);
				}
				return;
			}
			Interlocked.Exchange(ref runningPending, 0);
			Interlocked.Exchange(ref nextRunningProbeTicks, ticks + RunningProbeIntervalTicks);
			int version = Volatile.Read(ref runningVersion);
			GameLibraryGallery list = lstGames;
			List<string> paths = new List<string>();
			foreach (GameLibraryItem item in list.Items)
			{
				GameLibraryItem gameLibraryItem = item as GameLibraryItem;
				if (gameLibraryItem != null && gameLibraryItem.Profile != null)
				{
					paths.Add(gameLibraryItem.Profile.ExecutablePath);
				}
			}
			paths.Sort(StringComparer.OrdinalIgnoreCase);
			string observationKey = string.Join("\n", paths.ToArray()).ToLowerInvariant();
			long observationSession = gameMode == null ? 0 : gameMode.AutomaticSessionSerial;
			if (!runningObservationFailures.Allowed(0, observationSession, observationKey, "running"))
			{
				Interlocked.Exchange(ref runningBusy, 0);
				return;
			}
			try
			{
				QueueUiStateWork(delegate
				{
					Dictionary<string, bool> states = null;
					try
					{
						states = QueryLibraryRunningState(paths);
						if (states != null) runningObservationFailures.Succeeded(0, observationSession, observationKey, "running");
					}
					catch
					{
						int failures = runningObservationFailures.Failed(0, observationSession, observationKey, "running");
						Logger.Warn("Library running observation failure=" + failures + "/3");
					}
					try
					{
						PostUiStateResult(delegate
						{
							try
							{
								if (!base.IsDisposed && UiActive && states != null && object.ReferenceEquals(list, lstGames) && !list.IsDisposed && version == Volatile.Read(ref runningVersion))
								{
									ApplyRunningStates(states);
								}
							}
							finally
							{
								FinishGameRunningRefresh();
							}
						});
					}
					catch
					{
						ReleaseGameRunningRefresh();
					}
				});
			}
			catch
			{
				int failures = runningObservationFailures.Failed(0, observationSession, observationKey, "running");
				Logger.Warn("Library running observation queue failure=" + failures + "/3");
				ReleaseGameRunningRefresh();
			}
		}
		private void RefreshLibraryInspector()
		{
			if (lstGames == null)
			{
				return;
			}
			lstGames.RefreshConfiguration();
			AdjustLibraryCardDensity();
			GameConfigurationDialog gameConfigurationDialog = libraryConfigurationDialog;
			if (gameConfigurationDialog != null && !gameConfigurationDialog.IsDisposed && gameConfigurationDialog.Visible)
			{
				GameLibraryItem gameLibraryItem = ConfigurationItem(gameConfigurationDialog.ProfileId);
				if (gameLibraryItem == null)
				{
					gameConfigurationDialog.Close();
					return;
				}
				((LibraryGameConfiguration)gameConfigurationDialog.ConfigurationContent).RefreshItem(gameLibraryItem);
				gameConfigurationDialog.RefreshGame(gameLibraryItem, GameIcon(gameLibraryItem.Profile.ExecutablePath));
			}
		}
		private void RefreshLog()
		{
			RefreshLog(false);
		}
		private void RefreshLog(bool force)
		{
			if (logStream == null)
			{
				return;
			}
			ConfigureLogView();
			SyncLogHint();
			logStream.SessionToken = gameMode.AutomationSession;
			long version = Logger.Version;
			if (force || version != logSeenVersion || !object.ReferenceEquals(logSeenStream, logStream))
			{
				logSeenVersion = version;
				logSeenStream = logStream;
				bool flag = logStream.SetText(Logger.Tail(2000));
				if (force && !flag)
				{
					logStream.Invalidate();
				}
			}
		}
		private void SyncLogHint()
		{
			if (lblLogStreamHint != null)
			{
				bool writesEnabled = Logger.WritesEnabled;
				lblLogStreamHint.Text = writesEnabled ? Lang.T(logDiagnostics ? "log.diagnostics.hint" : "log.actions.hint") : Lang.T("v20.log.paused");
				lblLogStreamHint.ForeColor = (writesEnabled ? Theme.Faint : LogStreamView.SeverityColor(LogEventSeverity.Warning));
			}
		}
		private void RefreshOverviewAttention()
		{
			if (lblStatus != null && !lblStatus.IsDisposed && gameMode != null)
			{
				string text = gameMode.ActiveGame ?? Lang.T("workflow.game.waiting");
				if (lblStatus.Text != text)
				{
					lblStatus.Text = text;
					FitLabelFont(lblStatus, true, 14.5f, 9.5f);
				}
				bool flag = gameMode.Enabled && gameMode.IsActive;
				statusDot.Color = ((!gameMode.Enabled) ? Theme.Dim : (flag ? Theme.Green : Theme.Accent));
				statusDot.Pulse = flag;
				swGame.SetSilently(gameMode.Enabled);
				swGame.AccessibleDescription = gameMode.StatusText;
				RefreshOverviewGuardActivity();
				paviseCore.AccessibleName = ModeButton.ModeName(gameMode.ActivePreset);
				paviseCore.AccessibleDescription = gameMode.StatusText;
				if (overviewLogVersion != Logger.Version)
				{
					overviewLogVersion = Logger.Version;
					int warnings;
					LogStreamView.CountIssues(Logger.Tail(220), out warnings, out overviewErrors);
				}
				overviewAction = ((!elevated) ? "admin" : (gameMode.ProfileStoreSaveFailed ? "library" : ((gameMode.Enabled && overviewErrors > 0) ? "logs" : "")));
			}
		}
		private void RefreshUpdatePresentation()
		{
			bool flag = latestUpdate != null && UpdateChecker.IsNewer(latestUpdate.Latest, App.Version);
			if (btnUpdateHint != null && !btnUpdateHint.IsDisposed)
			{
				btnUpdateHint.Text = (flag ? Lang.F("site.update.available", latestUpdate.Latest) : "");
				btnUpdateHint.AccessibleName = btnUpdateHint.Text;
				btnUpdateHint.Visible = flag;
			}
			if (updateBadge != null && !updateBadge.IsDisposed)
			{
				updateBadge.Visible = flag;
			}
			RefreshAboutUpdate();
		}
		private void RefreshDonateButton()
		{
			if (btnDonate != null && !btnDonate.IsDisposed)
			{
				btnDonate.Text = Lang.T(donateLoading ? "donate.loading" : "donate.entry");
				btnDonate.AccessibleName = btnDonate.Text;
				btnDonate.Enabled = !donateLoading;
				btnDonate.Cursor = (donateLoading ? Cursors.WaitCursor : Cursors.Hand);
				btnDonate.Invalidate();
			}
		}
		private void RefreshBoostPresentation()
		{
			if (lblOverviewBoost != null)
			{
				string boostStatusText = gameMode.BoostStatusText;
				if (lblOverviewBoost.Text != boostStatusText)
				{
					lblOverviewBoost.Text = boostStatusText;
				}
				lblOverviewBoost.ForeColor = (gameMode.BoostStateVerified ? Theme.Green : (gameMode.BoostHandleProtected ? Theme.Dim : Theme.Fg));
			}
		}
		private void RefreshPolicyPresentation()
		{
			RefreshStandbyCleanerPresentation();
		}
		private void SyncBackdropAppearance()
		{
			bool active = Backdrop.Active;
			if (btnBackdrop != null)
			{
				btnBackdrop.Text = Lang.T(active ? "btn.backdrop.change" : "btn.backdrop.set");
			}
			if (btnBackdropReset != null)
			{
				btnBackdropReset.Enabled = active;
			}
			if (pickBackdropDim != null)
			{
				pickBackdropDim.Enabled = active;
				pickBackdropDim.Index = (active ? (Backdrop.Dim + 1) : 0);
			}
			if (lblBackdropState != null)
			{
				string text = ((active && pickBackdropDim != null && pickBackdropDim.Labels != null) ? pickBackdropDim.Labels[Backdrop.Dim + 1] : "");
				lblBackdropState.Text = (active ? Lang.F("backdrop.status.on", text) : Lang.T("backdrop.status.off"));
				lblBackdropState.ForeColor = (active ? Theme.Accent : Theme.Dim);
			}
			if (backdropPreview != null)
			{
				backdropPreview.Invalidate();
			}
		}
		private void RefreshModeAccentLabels()
		{
			for (int i = 0; i < modeAccentLabels.Length; i++)
			{
				Label label = modeAccentLabels[i];
				if (label != null && !label.IsDisposed)
				{
					PerformancePreset performancePreset = (PerformancePreset)i;
					bool flag = performancePreset == visualMode;
					label.ForeColor = (flag ? Theme.ModeColor(performancePreset) : Theme.Fg);
					label.Font = Theme.UI(8.6f, flag);
				}
			}
		}
		private void SyncShaderCleanupPresentation()
		{
			bool shaderCleanupBusy = ShaderCleanupBusy;
			if (btnShaderGo != null && !btnShaderGo.IsDisposed)
			{
				btnShaderGo.Enabled = !shaderCleanupBusy;
			}
			if (shaderCleanupBusy && cardShader != null && !cardShader.IsDisposed)
			{
				cardShader.Value = Lang.T("shader.busy");
			}
		}
		private void RefreshSlowStateAsync()
		{
			if (base.IsDisposed || !UiActive)
			{
				return;
			}

			if (Interlocked.CompareExchange(ref slowBusy, 1, 0) != 0)
			{
				Interlocked.Exchange(ref slowPending, 1);
				return;
			}
			Interlocked.Exchange(ref slowPending, 0);
			int version = Volatile.Read(ref slowVersion);
			Toggle auto = swAuto;
			SettingCard shader = cardShader;
			try
			{
				QueueUiStateWork(delegate
				{
					bool task = false;
					bool taskKnown = false;
					long shaderBytes = -1L;
					try
					{
						task = QueryStartupTaskState();
						taskKnown = true;
					}
					catch
					{
					}
					try
					{
						if (!ShaderCleanupBusy)
						{
							shaderBytes = MeasureSettingsShaderCache();
						}
					}
					catch
					{
					}
					try
					{
						PostUiStateResult(delegate
						{
							try
							{
								if (!base.IsDisposed && UiActive && object.ReferenceEquals(auto, swAuto) && object.ReferenceEquals(shader, cardShader))
								{
									if (version == Volatile.Read(ref slowVersion) && auto != null && !auto.IsDisposed && taskKnown)
									{
										auto.SetSilently(task);
									}
									if (shader != null && !shader.IsDisposed && !ShaderCleanupBusy && shaderBytes >= 0)
									{
										shader.Value = CacheSweep.FmtBytes(shaderBytes);
									}
								}
							}
							finally
							{
								FinishSlowStateRefresh();
							}
						});
					}
					catch
					{
						Interlocked.Exchange(ref slowBusy, 0);
					}
				});
			}
			catch
			{
				Interlocked.Exchange(ref slowBusy, 0);
			}
		}
		private void RefreshStandbyCleanerPresentation()
		{
			if (swPolicyStandbyCleaner != null)
			{
				swPolicyStandbyCleaner.SetSilently(gameMode.StandbyCleanerEnabled);
				swPolicyStandbyCleaner.Enabled = elevated || gameMode.StandbyCleanerEnabled;
			}
			if (cardPolicyStandbyCleaner != null)
			{
				cardPolicyStandbyCleaner.Desc = ((!gameMode.StandbyCleaningOptionsValid) ? Lang.T("standbycleaner.config.invalid") : (elevated ? SettingsStandbyCleanerDetail() : Lang.T("gm.standbycleaner.needadmin")));
				SettingsWorkspaceTile settingsWorkspaceTile = cardPolicyStandbyCleaner as SettingsWorkspaceTile;
				if (settingsWorkspaceTile != null)
				{
					settingsWorkspaceTile.Summary = ((!gameMode.StandbyCleaningOptionsValid) ? Lang.T("standbycleaner.config.invalid") : ((!elevated) ? Lang.T("gm.standbycleaner.needadmin") : PerformancePolicy.Text("对局中 · 双阈值触发", "During matches · both thresholds")));
				}
			}
		}
		internal void SyncWhitelistPlatformHint()
		{
			if (lblWhiteHint != null && !lblWhiteHint.IsDisposed)
			{
				if (whiteFilters != null)
				{
					whiteFilters.AccessibleDescription = Lang.T("white.platform.hint");
				}
				AppendDetectedPlatformHint();
				SyncWhitelistRemoveAction();
			}
		}
		private void SyncWhitelistRemoveAction()
		{
			if (btnRemoveWhitelist != null && !btnRemoveWhitelist.IsDisposed)
			{
				btnRemoveWhitelist.Enabled = CanRemoveSelectedWhitelist();
			}
			if (lblWhiteHint != null && !lblWhiteHint.IsDisposed)
			{
				WhitelistItem whitelistItem = ((lstWhite == null) ? null : (lstWhite.SelectedItem as WhitelistItem));
				lblWhiteHint.Text = ((whitelistItem == null) ? AutoText("拖入 EXE 或快捷方式即可添加", "Drop an EXE or shortcut to add it") : (whitelistItem.Automatic ? AutoText("点击查看匹配范围", "Select to view matching rules") : ((whitelistItem.View != null && whitelistItem.View.Required) ? AutoText("系统依赖，保持保护", "System dependency · protected") : ((whitelistItem.View != null && whitelistItem.View.PlatformId != null) ? AutoText("移除后按普通后台规则处理", "Removal applies normal background rules") : AutoText("保护此应用与已验证的子进程", "Protects this app and verified descendants")))));
			}
		}
		internal void RefreshWhitelist(bool deep)
		{
			if (base.IsDisposed || lstWhite == null || lstWhite.IsDisposed || whitePanel == null || whitePanel.IsDisposed)
			{
				return;
			}
			int version = Interlocked.Increment(ref whiteVersion);
			List<WhitelistRuleView> views = QueryWhitelistState(false);
			FillWhitelist(views);
			if (!deep || !UiActive)
			{
				return;
			}
			if (Interlocked.CompareExchange(ref whiteBusy, 1, 0) != 0)
			{
				Interlocked.Exchange(ref whitePending, 1);
				return;
			}
			Interlocked.Exchange(ref whitePending, 0);
			ListBox list = lstWhite;
			EmptyStatePanel panel = whitePanel;
			try
			{
				QueueUiStateWork(delegate
				{
					List<WhitelistRuleView> full = null;
					try
					{
						full = QueryWhitelistState(true);
					}
					catch
					{
					}
					try
					{
						PostUiStateResult(delegate
						{
							try
							{
								if (!base.IsDisposed && UiActive && full != null && version == Volatile.Read(ref whiteVersion) && object.ReferenceEquals(list, lstWhite) && object.ReferenceEquals(panel, whitePanel) && !list.IsDisposed && !panel.IsDisposed)
								{
									FillWhitelist(full);
								}
							}
							finally
							{
								FinishWhitelistRefresh();
							}
						});
					}
					catch
					{
						Interlocked.Exchange(ref whiteBusy, 0);
					}
				});
			}
			catch
			{
				Interlocked.Exchange(ref whiteBusy, 0);
			}
		}
		private void RefreshBackdrop()
		{
			StopPageReveal();
			ApplyBackdropLabels(this);
			Invalidate(true);
			Update();
		}
		private void RefreshStrategyControls()
		{
			if (modeFlyout != null && !modeFlyout.IsDisposed)
			{
				modeFlyout.Scene.Index = ((gameMode.Preset == PerformancePreset.Competitive) ? 1 : 0);
				modeFlyout.Scene.Enabled = !gameMode.ModeSwitchLocked;
				RefreshBackgroundGovernanceControl();
				modeFlyout.SetLaptopPerformance(gameMode.LaptopPerformanceSupportKnown && gameMode.LaptopPerformanceSupported);
				modeFlyout.LaptopPerformance.SetSilently(gameMode.LaptopPerformanceEnabled);
				if (PowerPlan.HandheldBypass && (powerPlanChoices.Count != 1 || powerPlanChoices[0] != "off"))
					LoadStrategyPowerChoices();
				powerButton.Enabled = !PowerPlan.HandheldBypass && (!gameMode.Enabled || !gameMode.IsActive);
				if (!powerButton.Enabled)
				{
					powerButton.CloseDropDown();
				}
				int num = powerPlanChoices.FindIndex((string id) => string.Equals(id, PowerPlan.EffectivePlanId, StringComparison.OrdinalIgnoreCase));
				if (num >= 0)
				{
					powerButton.SetSilently(num);
				}
				if (modeFlyout.Visible) LayoutModeFlyout();
			}
		}
		public void SyncAllToggles()
		{
			if (base.InvokeRequired)
			{
				BeginInvoke(new MethodInvoker(SyncAllToggles));
			}
			else if (!base.IsDisposed && UiActive)
			{
				if (builtLang != Lang.Cur)
				{
					RebuildUi();
					return;
				}
				SyncToggleValues();
				RefreshSlowStateAsync();
				RefreshEnvironmentStateAsync();
			}
		}
		private void SyncToggleValues()
		{
			if (gameMode != null && tamer != null)
			{
				if (swGame != null)
				{
					swGame.SetSilently(gameMode.Enabled);
				}
				if (swAutoHide != null)
				{
					swAutoHide.SetSilently(Settings.Load("AutoHideOnGame", true));
				}
				if (swLogWrites != null)
				{
					swLogWrites.SetSilently(Settings.Load("LogWritesEnabled", true));
				}
				for (int i = 0; i < policySync.Count; i++)
				{
					policySync[i]();
				}
				RefreshStrategyControls();
				SyncEnvironmentToggles();
				UpdateModePresentation(false);
				for (int j = 0; j < acGroups.Count && j < acToggles.Count; j++)
				{
					acToggles[j].SetSilently(tamer.IsGroupEnabled(acGroups[j].Key));
				}
			}
		}
		private void UpdateModePresentation(bool animate)
		{
			RefreshStrategyControls();
			PerformancePreset activePreset = gameMode.ActivePreset;
			bool enabled = gameMode.Enabled;
			bool isActive = gameMode.IsActive;
			string sessionPolicySourceName = gameMode.SessionPolicySourceName;
			bool flag = !modeVisualInitialized;
			bool flag2 = flag || activePreset != visualMode || enabled != visualEnabled;
			bool flag3 = flag || isActive != visualActive || !string.Equals(sessionPolicySourceName, visualPolicySource, StringComparison.Ordinal);
			if (flag2)
			{
				if (modeButton != null)
				{
					modeButton.SetMode(activePreset);
				}
				if (lblHeroMode != null)
				{
					lblHeroMode.Text = ModeButton.ModeName(activePreset);
				}
			}
			if (flag3)
			{
				if (modeButton != null)
				{
					modeButton.SetSource(ModeSourceText(sessionPolicySourceName, true));
				}
				if (lblHeroSource != null)
				{
					lblHeroSource.Text = ModeSourceText(sessionPolicySourceName, false);
				}
			}
			if (paviseCore != null)
			{
				paviseCore.SetState(activePreset, enabled, isActive);
			}
			SyncMotionAppearanceActivity();
			if (activePreset != visualMode)
			{
				visualMode = activePreset;
				Theme.SetMode(activePreset, animate);
			}
			visualEnabled = enabled;
			visualActive = isActive;
			visualPolicySource = sessionPolicySourceName;
			modeVisualInitialized = true;
			if (root != null)
			{
				Control control = root.Controls["WorkspaceTopBar"];
				WorkspaceBrand workspaceBrand = ((control == null) ? null : (control.Controls["WorkspaceBrand"] as WorkspaceBrand));
				if (workspaceBrand != null)
				{
					workspaceBrand.SetState(activePreset, enabled);
				}
			}
			if (flag2)
			{
				RefreshModeAccentLabels();
				if (nav != null)
				{
					nav.SetMode(activePreset, enabled);
				}
				if (tuningNav != null)
				{
					tuningNav.SetMode(activePreset, enabled);
				}
				using (Icon runtimeIcon = IconArt.MakeMultiIcon(activePreset, enabled))
				{
					SetRuntimeIcon(runtimeIcon);
				}
				if (curPage == pagePolicy)
				{
					RefreshAutomationPage();
				}
			}
		}
		private static void RefreshMenuPalette(ToolStrip menu, UiPalette previous, UiPalette next)
		{
			menu.BackColor = previous.Remap(menu.BackColor, next);
			menu.ForeColor = previous.Remap(menu.ForeColor, next);
			foreach (ToolStripItem item in menu.Items)
			{
				item.BackColor = previous.Remap(item.BackColor, next);
				item.ForeColor = previous.Remap(item.ForeColor, next);
				ToolStripDropDownItem toolStripDropDownItem = item as ToolStripDropDownItem;
				if (toolStripDropDownItem != null && toolStripDropDownItem.HasDropDownItems)
				{
					RefreshMenuPalette(toolStripDropDownItem.DropDown, previous, next);
				}
			}
		}
		private void RefreshAccentLabels()
		{
			for (int num = accentLabels.Count - 1; num >= 0; num--)
			{
				Label label = accentLabels[num];
				if (label == null || label.IsDisposed)
				{
					accentLabels.RemoveAt(num);
				}
				else
				{
					label.ForeColor = Theme.Accent;
					LinkLabel linkLabel = label as LinkLabel;
					if (linkLabel != null)
					{
						linkLabel.LinkColor = Theme.Accent;
						linkLabel.VisitedLinkColor = Theme.Accent;
						linkLabel.ActiveLinkColor = Theme.Fg;
					}
				}
			}
		}
		private void SyncUiForeground(bool foreground)
		{
			SyncMotionAppearanceActivity(foreground);
			if (paviseCore != null)
			{
				paviseCore.SetForeground(foreground);
			}
			if (foreground && UiActive)
			{
				UiClock.Wake();
				UiClock.WakeSlow();
			}
		}
		private void SyncUiActivity()
		{
			bool flag = ShouldRunUi(base.IsHandleCreated && !base.IsDisposed && base.Visible, base.WindowState);
			if (uiActivityKnown && uiActive == flag)
			{
				return;
			}
			uiActivityKnown = true;
			uiActive = flag;
			bool gameActive = gameMode != null && gameMode.Enabled && gameMode.IsActive;
			SyncAutoHideBaseline(gameActive, ref lastGameActive, ref autoHideArmed);
			if (!flag)
			{
				SetOverviewTelemetryActive(false);
				if (motionAppearance != null && !motionAppearance.IsDisposed) motionAppearance.SetActivity(false, motionAppearanceForeground, gameActive);

				if (uiTimer != null)
				{
					uiTimer.Stop();
				}
				CancelAutoHide();
				UiClock.Suspended = true;
				if (paviseCore != null)
				{
					paviseCore.SetAnimationEnabled(false);
				}
			}
			else if (builtLang != Lang.Cur)
			{
				RebuildUi();
			}
			else
			{
				RefreshLightweightUiState();
				SyncToggleValues();
				UiClock.Suspended = false;
				if (uiTimer != null)
				{
					uiTimer.Start();
				}
				UiClock.Wake();
				UiClock.WakeSlow();
				NotifyPageActivation();
			}
		}
		private void RefreshLightweightUiState()
		{
			if (gameMode == null)
			{
				return;
			}
			if (lblStatus != null)
			{
				string text = gameMode.ActiveGame ?? Lang.T("workflow.game.waiting");
				if (lblStatus.Text != text)
				{
					lblStatus.Text = text;
					FitLabelFont(lblStatus, true, 14.5f, 9.5f);
				}
			}
			bool flag = gameMode.Enabled && gameMode.IsActive;
			if (curPage == pageOverview)
			{
				RefreshOverviewAttention();
			}
			UiClock.Frozen = flag;
			SyncMotionAppearanceActivity();
			if (statusDot != null)
			{
				statusDot.Color = ((!gameMode.Enabled) ? Theme.Dim : (flag ? Theme.Green : Theme.Accent));
				statusDot.Pulse = flag;
			}
			if (paviseCore != null)
			{
				paviseCore.SetState(gameMode.ActivePreset, gameMode.Enabled, flag);
			}
			if (lblSub != null && elevated)
			{
				string activeGame = gameMode.ActiveGame;
				string text2 = Lang.T("title.admin") + " " + ((activeGame != null) ? Lang.F("title.guard", activeGame) : Lang.T("v20.admin.ready"));
				if (lblSub.Text != text2)
				{
					lblSub.Text = text2;
				}
				lblSub.ForeColor = ((activeGame != null) ? Theme.Green : Theme.Faint);
			}
			RefreshBoostPresentation();
		}
		internal static void SyncAutoHideBaseline(bool gameActive, ref bool lastActive, ref bool armed)
		{
			lastActive = gameActive;
			armed = gameActive;
		}
		private void UpdateAutoHide(bool gameActive)
		{
			switch (NextAutoHide(gameActive, ref lastGameActive, ref autoHideArmed, Settings.LoadCached("AutoHideOnGame", true), UiActive))
			{
			case AutoHideAction.Cancel:
				CancelAutoHide();
				break;
			case AutoHideAction.Schedule:
				CancelAutoHide();
				autoHideTimer = new System.Windows.Forms.Timer();
				autoHideTimer.Interval = 10000;
				autoHideTimer.Tick += OnAutoHideTick;
				autoHideTimer.Start();
				break;
			}
		}
	}
}
