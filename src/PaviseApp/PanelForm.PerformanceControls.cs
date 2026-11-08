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
		private static int PerformanceActionCategory(PerformanceAction action)
		{
			if (!(action.Key == "AutoBackgroundFpsV1"))
			{
				if (!(action.Key == "AutoGameMitigationsV1") && !(action.Key == "AutoNtfsShortNamesV1"))
				{
					return 2;
				}
				return 0;
			}
			return 1;
		}
		private static bool HasAutomationInventory(int category, PerformanceRestartScope scope)
		{
			if ((category == 0 && PerformanceRestartFor("GmMmcss") == scope) || (category == 2 && (PerformanceRestartFor("GmEnglishInputV1") == scope || PerformanceRestartFor("GpuPrefStageOn") == scope)))
			{
				return true;
			}
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (!performanceAction.Environment && performanceAction.Key != "MatchVisualEffectsV1" && PerformanceActionCategory(performanceAction) == category && PerformanceRestartFor(performanceAction.Key) == scope)
				{
					return true;
				}
			}
			return false;
		}
		private void AddAutomationInventory(Control parent, ref int y, int category, PerformanceRestartScope scope)
		{
			if (category == 0 && PerformanceRestartFor("GmMmcss") == scope)
			{
				AddManualPerformanceToggle(parent, ref y, "GmMmcss", AutoText("多媒体调度", "Multimedia scheduling"), AutoText("调整已注册的游戏多媒体线程，目标需重新读取配置。", "Adjusts registered game multimedia threads; the target must reload the configuration."), () => Settings.LoadCached("GmMmcss", true), (bool value) => SaveManualPerformancePreference("GmMmcss", value));
			}
			if (category == 2 && PerformanceRestartFor("GmEnglishInputV1") == scope)
			{
				AddManualPerformanceToggle(parent, ref y, "GmEnglishInputV1", AutoText("游戏窗口切换英文输入", "Switch the game window to English input"), AutoText("入局时尝试一次，仅作用于确认的游戏窗口。", "Attempts once on session entry, only for the confirmed game window."), () => Settings.LoadCached("GmEnglishInputV1", false), (bool value) => SaveManualPerformancePreference("GmEnglishInputV1", value));
			}
			if (category == 2 && PerformanceRestartFor("GpuPrefStageOn") == scope)
			{
				AddManualPerformanceToggle(parent, ref y, "GpuPrefStageOn", AutoText("游戏高性能显卡预选", "Prefer the high-performance GPU for games"), AutoText("为游戏登记显卡偏好，下次启动生效。", "Registers the game GPU preference for the next launch."), () => Settings.LoadCached("GpuPrefStageOn", true), (bool value) => SaveManualPerformancePreference("GpuPrefStageOn", value), () => Native.OsBuild() >= 17134);
			}
			PerformanceAction[] all = PerformanceActionCatalog.All;
			foreach (PerformanceAction performanceAction in all)
			{
				if (!performanceAction.Environment && !(performanceAction.Key == "MatchVisualEffectsV1") && PerformanceActionCategory(performanceAction) == category && PerformanceRestartFor(performanceAction.Key) == scope)
				{
					PerformanceAction captured = performanceAction;
					AddManualPerformanceToggle(parent, ref y, performanceAction.Key, performanceAction.Title, ManualPerformanceActionDetail(performanceAction), () => Settings.LoadCached(captured.Key, false), (bool value) => SaveManualPerformanceAction(captured, value), () => ManualPerformanceActionSupported(captured.Key), null, () => gameMode.PerformanceExecutionState(captured.Key));
				}
			}
		}
		private void AddPerformanceRelatedSettings(Control parent, ref int y, int category)
		{
			if (category != 2)
			{
				AddAutomationSection(parent, ref y, AutoText("相关设置", "Related settings"));
			}
			if (category == 0)
			{
				AddAutomationDestination(parent, ref y, "GameSettings", "逐游戏设置", "Per-game settings", "核心范围、家族后台、ReBAR、禁用全屏优化与入库设置。", "Core selection, game-family control, ReBAR, fullscreen optimization and auto-add.", PageId.Library);
			}
			if (category == 1)
			{
				AddAutomationDestination(parent, ref y, "WhitelistSettings", "白名单", "Whitelist", "为需要保持正常运行的后台程序添加保护。", "Protect background applications that need to keep running normally.", PageId.Whitelist);
				AddAutomationDestination(parent, ref y, "AntiCheatSettings", "反作弊进程", "Anti-cheat processes", "兼容性专项，按明确的手动选择设置。", "Compatibility-specific options controlled by your explicit choices.", PageId.AntiCheat);
			}
			if (category == 0)
			{
				AddAutomationDestination(parent, ref y, "ProtectionSettings", "故障与恢复", "Faults & recovery", "查看动作故障与恢复记录，未完成的恢复不会因关闭功能丢失。", "Review action faults and recovery records; disabling a feature keeps unfinished recovery records.", PageId.Settings);
			}
		}
		private bool SaveManualPerformanceAction(PerformanceAction action, bool value)
		{
			if (action.Key == "MatchVisualEffectsV1")
			{
				return gameMode.SetVisualEffectsReduction(value);
			}
			if ((action.Key == "AutoGameMitigationsV1" || action.Key == "AutoNtfsShortNamesV1") && !elevated)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
				return true;
			}
			if (value && action.Key == "AutoGameMitigationsV1" && !PaviseDialog.Confirm(this, action.Title, AutoText("这会为游戏库程序关闭部分漏洞利用防护，降低相应防护能力。下次启动游戏生效。\n\n确定手动开启？", "This disables selected exploit protections for library games and reduces their protection. Effective on the next game launch.\n\nEnable this manual option?"), DlgKind.Warn))
			{
				return true;
			}
			return SaveManualPerformancePreference(action.Key, value);
		}
		private static bool ManualPerformanceActionSupported(string key)
		{
			if (key == "AutoBackgroundFpsV1" || key == "AutoOpenGlThreadsV1")
			{
				return NvApi.Available;
			}
			return true;
		}
		private static string ManualPerformanceActionDetail(PerformanceAction action)
		{
			if (action.Key == "AutoGameMitigationsV1")
			{
				return AutoText("手动关闭游戏的部分漏洞利用防护；降低防护，下次启动生效。", "Manually disables selected game exploit protections; reduces protection, effective on next launch.");
			}
			if (action.Key == "AutoNtfsShortNamesV1")
			{
				return AutoText("游戏所在 NTFS 卷不再生成新的 8.3 短文件名，保留已有名称。", "Stops new 8.3 names on eligible NTFS game volumes while keeping existing names.");
			}
			if (action.Key == "AutoBackgroundFpsV1")
			{
				return AutoText("随后台治理，对无可见窗口的合格 NVIDIA 3D 程序限帧。竞技或智能升档不扩大范围，也不限制游戏前台。", "With background control active, caps eligible windowless NVIDIA 3D apps. Competitive and Smart escalation do not widen scope or cap the foreground game.");
			}
			if (action.Key == "AutoOpenGlThreadsV1")
			{
				return AutoText("仅供 NVIDIA OpenGL 游戏手动选择，下次启动生效。", "Manual choice for NVIDIA OpenGL games only; effective on the next launch.");
			}
			return action.Detail;
		}
		private void AddAutomationDestination(Control parent, ref int y, string id, string zh, string en, string detailZh, string detailEn, PageId target)
		{
			PillButton pillButton = new PillButton(AutoText("前往设置", "Configure"));
			pillButton.Name = "manualDestination_" + id;
			pillButton.Size = new Size(Theme.S(124), Theme.S(32));
			PillButton pillButton2 = pillButton;
			pillButton2.Click += delegate
			{
				nav.Select((int)target);
			};
			string desc = AutoText(detailZh, detailEn);
			int used;
			MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, pillButton2, 82), AutoText(zh, en), desc, pillButton2, out used);
			y += used + 8;
		}
		private string AutomationPreferenceStatus(string key)
		{
			string value = gameMode.AutomaticExecutionState(key);
			if (!string.IsNullOrEmpty(value))
			{
				return GameMode.AutomationStateLabel(value);
			}
			if (!PerformancePolicy.EnvironmentPreference(key))
			{
				return AutoText("已关闭", "Off");
			}
			return AutoText("已开启", "Enabled");
		}
		private static string AutoText(string zh, string en)
		{
			return PerformancePolicy.Text(zh, en);
		}
		private PillButton AddAutomationLink(Control parent, string zh, string en, PageId target, int x, int y, int width)
		{
			PillButton pillButton = new PillButton(AutoText(zh, en));
			pillButton.SetBounds(Theme.S(x), Theme.S(y), Theme.S(width), Theme.S(34));
			pillButton.Click += delegate
			{
				nav.Select((int)target);
			};
			parent.Controls.Add(pillButton);
			return pillButton;
		}
		private static int ManualPolicyCategory(PolicyItem item)
		{
			string key = item.Key;
			switch (key)
			{
			case "GmSuppress":
			case "GmPauseDl":
			case "GmPauseUpdate":
			case "GmPauseMaintenanceV1":
				return 1;
			default:
				if (item.GroupKey == "cfg.group.gpu" || key == "GmDwmBoost")
				{
					return 2;
				}
				return 0;
			}
		}
		private static bool PrimaryPerformancePolicy(string key)
		{
			return key == "GmBoost";
		}
		internal static PerformanceRestartScope PerformanceRestartFor(string key)
		{
			if (key == PerformanceActionCatalog.Quantum) return PerformanceRestartScope.None;
			if (key == PrivacyActionCatalog.BackgroundGlobal) return PerformanceRestartScope.ProcessRestart;
			if (PerformancePolicy.IsIndependentManual(key))
			{
				return PerformanceRestartScope.SystemRestart;
			}
			switch (key)
			{
			case "GameNvRebarV1":
			case "GameDisableFullscreenOptimizationsV1":
			case "NvMaxPerf":
			case "NvShaderCache":
			case "NvLowLat":
			case "AutoOpenGlThreadsV1":
			case "AutoGameMitigationsV1":
			case "GpuPrefStageOn":
			case "WindowedOptimizations":
			case "VariableRefreshRate":
			case "NvVrrWindowed":
				return PerformanceRestartScope.ProcessRestart;
			default:
				return PerformanceRestartScope.None;
			}
		}
		private static string PerformanceScopeTitle(PerformanceRestartScope scope)
		{
			return PerformanceFeatureCard.RestartLabel(scope);
		}
		internal static PerformanceLifetime PerformanceLifetimeFor(string key)
		{
			switch (key)
			{
			case "GmBoost":
			case "GmThreadBoostStrategyV1":
			case "GmPauseUpdate":
			case "GmPauseMaintenanceV1":
			case "GmPauseDl":
			case "GmAwake":
			case "GmDwmBoost":
			case "GmIntelEnduranceV1":
			case "GmEnglishInputV1":
			case "NvMaxPerf":
			case "NvShaderCache":
			case "NvLowLat":
			case "NvVrrWindowed":
			case "GpuPrefStageOn":
			case "LabEppYieldV1":
			case "LabGpuClockMaxV1":
			case "LabGpuPowerMaxV1":
			case "LabAudioLowBufferV1":
			case "LabCpuIdleStrategyV1":
				return PerformanceLifetime.Session;
			default:
				return PerformanceLifetime.Persistent;
			}
		}
		private static bool PerformanceControlLifetime(Control control, out PerformanceLifetime lifetime)
		{
			lifetime = PerformanceLifetime.Session;
			bool found = false;
			PerformanceFeatureCard card = control as PerformanceFeatureCard;
			if (card != null)
			{
				lifetime = card.Lifetime;
				return true;
			}
			foreach (Control child in control.Controls)
			{
				PerformanceLifetime nested;
				if (PerformanceControlLifetime(child, out nested))
				{
					found = true;
					if (nested == PerformanceLifetime.Persistent)
					{
						lifetime = PerformanceLifetime.Persistent;
					}
				}
			}
			if (!found)
			{
				lifetime = PerformanceLifetime.Persistent;
			}
			return found;
		}
		private static void GroupPerformanceLifetimes(Control grid)
		{
			List<Control> session = new List<Control>();
			List<Control> persistent = new List<Control>();
			List<Control> headings = new List<Control>();
			foreach (Control control in grid.Controls)
			{
				if (control is PerformanceSectionHeading)
				{
					headings.Add(control);
					continue;
				}
				PerformanceLifetime lifetime;
				PerformanceControlLifetime(control, out lifetime);
				(lifetime == PerformanceLifetime.Session ? session : persistent).Add(control);
			}
			grid.SuspendLayout();
			try
			{
				foreach (Control heading in headings)
				{
					grid.Controls.Remove(heading);
					heading.Dispose();
				}
				int index = 0;
				PerformanceLifetime[] order = new PerformanceLifetime[2] { PerformanceLifetime.Session, PerformanceLifetime.Persistent };
				foreach (PerformanceLifetime lifetime in order)
				{
					if (((lifetime == PerformanceLifetime.Session) ? session : persistent).Count > 0)
					{
						PerformanceSectionHeading heading = new PerformanceSectionHeading
						{
							Name = "performanceLifetime" + lifetime,
							Text = PerformanceSectionHeading.LifetimeTitle(lifetime),
							Lifetime = lifetime
						};
						grid.Controls.Add(heading);
						grid.Controls.SetChildIndex(heading, index++);
					}
					foreach (Control control in (lifetime == PerformanceLifetime.Session) ? session : persistent)
					{
						grid.Controls.SetChildIndex(control, index++);
					}
				}
			}
			finally
			{
				grid.ResumeLayout(false);
			}
		}
		private static void ConfigurePerformanceCards(Control parent)
		{
			foreach (Control control in parent.Controls)
			{
				PerformanceFeatureCard performanceFeatureCard = control as PerformanceFeatureCard;
				if (performanceFeatureCard != null && performanceFeatureCard.Hosted != null)
				{
					string performanceFeatureKeyResult = PerformanceFeatureKey(performanceFeatureCard.Hosted.Name);
					performanceFeatureCard.RestartScope = ((performanceFeatureKeyResult == "EnvPerformanceBundleV1" || performanceFeatureKeyResult == "EnvStoragePowerGroupV1") ? PerformanceRestartScope.SystemRestart : PerformanceRestartFor(performanceFeatureKeyResult));
					performanceFeatureCard.Lifetime = PerformanceLifetimeFor(performanceFeatureKeyResult);
					performanceFeatureCard.RestartText = ((performanceFeatureKeyResult == PrivacyActionCatalog.BackgroundGlobal) ? AutoText("重启相关应用", "Restart affected apps") : ((performanceFeatureKeyResult == "NvLowLat") ? AutoText("部分游戏需重启", "Game restart may apply") : ""));
					performanceFeatureCard.Title = performanceFeatureCard.Title.Replace(" · 需重启", "").Replace(" · restart required", "");
					if (performanceFeatureKeyResult == "EnvQuantumProfileV1")
					{
						performanceFeatureCard.Title = PerformanceActionCatalog.Find("EnvQuantumProfileV1").Title;
						performanceFeatureCard.RestartText = AutoText("优先在线应用", "Online update preferred");
					}
					ConfigurePerformanceConsole(performanceFeatureCard, performanceFeatureKeyResult);
					performanceFeatureCard.ArrangePresentation();
				}
				ConfigurePerformanceCards(control);
			}
		}
		private void AddAutomationSection(Control parent, ref int y, string text, PerformanceRestartScope scope = PerformanceRestartScope.None)
		{
		}
		private void AddManualPolicyControl(Control parent, ref int y, PolicyItem item)
		{
			string key = item.Key;
			if (key == BackgroundGovernancePolicy.SettingKey || key == "GmSuppress") return;
			string autoTextResult = ((key == "GmSuppress") ? AutoText("后台治理", "Background protection") : ((key == "GmDwmBoost") ? AutoText("桌面合成调度", "Desktop compositor scheduling") : ((key == "GmThreadBoostStrategyV1") ? AutoText("游戏线程提优", "Game thread boost") : Lang.T(item.LangKey))));
			string manualPolicyDescriptionResult = ManualPolicyDescription(key);
			if (item.Kind == PolicyValueKind.Bool)
			{
				AddManualPerformanceToggle(parent, ref y, key, autoTextResult, manualPolicyDescriptionResult, () => PolicyResolver.RawGlobalValue(key) == "1", (bool value) => SaveManualPolicy(item, value ? "1" : "0"), () => ManualPolicySupported(key), () => gameMode.AutomationPolicySnapshot.ValueOf(key) == "1");
				return;
			}
			if (key == "GmThreadBoostStrategyV1")
			{
				AddManualPerformanceToggle(parent, ref y, key, autoTextResult, manualPolicyDescriptionResult, () => ThreadBoostSelection.IsOn(PolicyResolver.RawGlobalValue(key)), (bool value) => SaveManualPolicy(item, value ? "on" : "off"), null, null, gameMode.ThreadBoostExecutionState);
				return;
			}
			string[] choices = (string[])item.Choices.Clone();
			string[] textEntries = new string[choices.Length];
			for (int i = 0; i < choices.Length; i++)
			{
				textEntries[i] = ((choices[i] == "off") ? AutoText("关闭", "Off") : ((choices[i] == "on") ? AutoText("开启", "On") : choices[i]));
			}
			TierPicker picker = new TierPicker
			{
				Name = "manualPerformance_" + key,
				Labels = textEntries,
				AccessibleName = autoTextResult,
				AccessibleDescription = manualPolicyDescriptionResult,
				Size = new Size(Theme.S(180), Theme.S(30))
			};
			int used;
			SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(manualPolicyDescriptionResult, ScrollContentW, picker, 82), autoTextResult, manualPolicyDescriptionResult, picker, out used);
			y += used + 8;
			Action sync = delegate
			{
				string rawGlobalValueResult = PolicyResolver.RawGlobalValue(key);
				int matchIndex = Array.IndexOf(choices, rawGlobalValueResult);
				picker.Index = ((matchIndex >= 0) ? matchIndex : 0);
				bool allConditionsMet = rawGlobalValueResult != "0" && rawGlobalValueResult != "off";
				bool manualPolicySupportedSucceeded = ManualPolicySupported(key);
				picker.Enabled = manualPolicySupportedSucceeded || allConditionsMet;
				SyncManualPerformanceStatus(card, key, allConditionsMet, manualPolicySupportedSucceeded, null);
			};
			picker.IndexChanged = delegate(int index)
			{
				if (!automationUpdating)
				{
					if (index < 0 || index >= choices.Length)
					{
						sync();
					}
					else
					{
						bool saveManualPolicySucceeded = SaveManualPolicy(item, choices[index]);
						sync();
						if (!saveManualPolicySucceeded)
						{
							ShowManualPerformanceSaveFailure();
						}
					}
				}
			};
			automationSync.Add(sync);
		}
		private bool SaveManualPolicy(PolicyItem item, string value)
		{
			string canonicalResult = PolicyCatalog.Canonical(item.Key, value);
			if (canonicalResult == null)
			{
				return false;
			}
			bool saveSucceeded = ((item.Key == "GmThreadBoostStrategyV1") ? ThreadBoostSelection.Save(canonicalResult) : ((item.Kind == PolicyValueKind.Bool) ? Settings.Save(item.Key, canonicalResult == "1") : Settings.SaveStr(item.Key, canonicalResult)));
			if (saveSucceeded)
			{
				gameMode.RefreshAutomaticPreferences();
				RefreshAutomationPage();
			}
			return saveSucceeded;
		}
		private bool SaveManualPerformancePreference(string key, bool value)
		{
			if (!Settings.Save(key, value))
			{
				return false;
			}
			gameMode.RefreshAutomaticPreferences();
			return true;
		}
		private void AddManualPerformanceToggle(Control parent, ref int y, string key, string title, string detail, Func<bool> read, Func<bool, bool> write, Func<bool> supported = null, Func<bool> automaticValue = null, Func<string> execution = null)
		{
			Toggle toggle = MakeSwitch(read(), null);
			toggle.Name = "manualPerformance_" + key;
			toggle.AccessibleName = title;
			toggle.AccessibleDescription = detail;
			int used;
			SettingCard card = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(detail, ScrollContentW, toggle, 82), title, detail, toggle, out used);
			y += used + 8;
			Action sync = delegate
			{
				bool readSucceeded = read();
				bool matchesSupported = supported == null || supported();
				toggle.SetSilently(readSucceeded);
				toggle.Enabled = matchesSupported || readSucceeded;
				SyncManualPerformanceStatus(card, key, readSucceeded, matchesSupported, execution);
			};
			toggle.CheckedChanged += delegate
			{
				if (!automationUpdating)
				{
					if (toggle.Checked && supported != null && !supported())
					{
						sync();
					}
					else
					{
						bool writeSucceeded = write(toggle.Checked);
						sync();
						if (!writeSucceeded)
						{
							ShowManualPerformanceSaveFailure();
						}
					}
				}
			};
			automationSync.Add(sync);
		}
		private bool ManualPolicySupported(string key)
		{
			switch (key)
			{
			case "NvMaxPerf":
			case "NvShaderCache":
			case "NvLowLat":
				return NvApi.Available;
			case "GmIntelEnduranceV1":
				if (environmentObservation.Snapshot != null)
				{
					return environmentObservation.Snapshot.IntelEnduranceSupported;
				}
				return false;
			case "PowerPlanOn":
				return !PowerPlan.NoSwitchSelected;
			default:
				return true;
			}
		}
		private static string ManualPolicyDescription(string key)
		{
			switch (key)
			{
			case "GmSuppress":
				return Lang.T("background.governance.detail");
			case "GmBoost":
				return AutoText("统一调整游戏的 CPU、内存、磁盘 I/O 与 GPU 优先级，切出游戏保持，退局恢复。", "Adjusts game CPU, memory, disk I/O and GPU priorities together. Kept when you switch away; restored when the session ends.");
			case "GmBoostCpuV1":
				return AutoText("对局期间保持游戏 CPU 高优先级，持续满载或无线程提优也不降档。由资源优先级总开关控制，退局恢复原值。", "Keeps game CPU priority High during the session, including sustained full load or inactive thread boosting. Requires game resource priorities; restores the original priority on exit.");
			case "GmBoostMemoryV1":
				return AutoText("保持游戏内存优先级为标准 5，通常已是默认值。不增加内存，也不锁定页面。", "Keeps game memory priority at Normal 5, usually the default. Adds no RAM and does not lock pages.");
			case "GmBoostIoV1":
				return AutoText("请求游戏磁盘 I/O 高优先级，需权限及读回确认。失败不影响其他优先级。", "Requests High game disk I/O priority after privilege and readback checks. Failure leaves other priorities unchanged.");
			case "GmBoostGpuV1":
				return AutoText("提高游戏 GPU 调度优先级，需驱动支持及读回确认。退局恢复原值。", "Raises game GPU scheduling priority with driver support and readback checks. Restores on exit.");
			case "GmThreadBoostStrategyV1":
				return AutoText("开启后按游戏自动选择：认得出画面提交线程就提优渲染关键线程，认不出（如 Vulkan、OpenGL）改提最忙线程。仅手动执行，退局恢复。可与逐游戏关键线程硬绑定同时生效，组合效果未测。", "Picks per game: boosts the render-critical threads when the frame-submission thread is recognized, otherwise (e.g. Vulkan, OpenGL) the busiest thread. Manual only; restores on exit. Can run alongside per-game candidate thread affinity; the combination is untested.");
			case "PowerPlanOn":
				return AutoText("对局使用所选电源计划，“不切换”时停用。托管在智能／竞技中共用参数，仍按交流／电池、CPU 拓扑及设备特征适配，场景、自动升档和切出不改变参数；掌机不接管。指定已有计划只切换，退局恢复原计划；实验室覆盖独立恢复。", "Uses the selected match power plan unless Don't switch is chosen. Smart and Competitive share managed parameters, adapted to AC/battery power, CPU topology and device characteristics. Scene changes, automatic escalation and switching away do not change those parameters. Handhelds bypass power management. Existing plans are only activated and restored after the session; laboratory overrides retain separate recovery.");
			case "GmPauseDl":
				return AutoText("对局暂停 Windows 传递优化下载，结束恢复。", "Pauses Windows Delivery Optimization downloads during a match, then restores.");
			case "GmPauseUpdate":
				return AutoText("对局暂停 Windows 更新活动，结束恢复。", "Pauses Windows Update activity during a match, then restores.");
			case "GmPauseMaintenanceV1":
				return AutoText("对局暂停自动维护，结束恢复。", "Pauses automatic maintenance during a match, then restores.");
			case "GmAwake":
				return AutoText("游戏前台时阻止屏幕与电脑自动休眠。", "Prevents automatic display and system sleep while the game is foreground.");
			case "GmDwmBoost":
				return AutoText("调整桌面合成调度，效果依显示场景而异。", "Adjusts desktop compositor scheduling; results depend on the display workload.");
			case "GmIntelEnduranceV1":
				return AutoText("支持的 Intel 笔记本关闭图形续航限制，可能增加功耗。", "Disables supported Intel laptop graphics endurance limits; may increase power use.");
			case "NvMaxPerf":
				return AutoText("为游戏配置 NVIDIA 最高性能偏好，下次启动核实。", "Configures NVIDIA maximum performance for games; verify on next launch.");
			case "NvShaderCache":
				return AutoText("配置 NVIDIA 着色器缓存容量，下次启动核实。", "Configures NVIDIA shader cache capacity; verify on next launch.");
			case "NvLowLat":
				return AutoText("NVIDIA 预渲染限制；适用性与帧率取舍因游戏而异。", "NVIDIA pre-render limit; support and throughput tradeoffs vary by game.");
			default:
				return AutoText("由你决定是否启用，按适用条件执行。", "Choose whether to enable this feature; it runs when eligible.");
			}
		}
		private static string PerformanceGraphicsDetail(string key)
		{
			switch (key)
			{
			case "Hags":
				return AutoText("启用 HAGS，需重启电脑；不保证提升平均帧率。", "Enables HAGS; a PC restart is required. Average FPS may not improve.");
			case "AmdSam":
				return AutoText("启用 AMD SAM，需主板和显卡支持。重启生效，关闭即关闭。", "Enables AMD SAM with motherboard/GPU support. Takes effect after restart; Off disables it.");
			case "WindowedOptimizations":
				return AutoText("面向 Win11 22H2+ 的兼容 DX10/11 窗口或无边框游戏，更改后重启游戏。开关直接设置开启或关闭；系统还原入口可恢复接管前值。", "For compatible DX10/11 windowed or borderless games on Windows 11 22H2+; restart the game after changes. The switch explicitly enables or disables it; system restoration restores the original value.");
			case "VariableRefreshRate":
				return AutoText("补充部分 DX11 全屏游戏的 VRR，需兼容显示器/驱动；不替代 FreeSync/G-SYNC 开关。更改后重启游戏，开关直接设置开启或关闭；系统还原入口保留原值恢复。", "Adds VRR for some DX11 fullscreen games with compatible displays/drivers; does not replace FreeSync/G-SYNC. Restart the game after changes; the switch explicitly enables or disables it and system restoration preserves the original value.");
			case "NvVrrWindowed":
				return AutoText("对局中为已开启全屏 G-SYNC 的机器补充窗口 VRR，所有模式共用。退局还原，部分面板可能闪烁。", "Adds windowed VRR during matches when fullscreen G-SYNC is enabled, shared by all game modes. Restores on exit; some panels may flicker.");
			default:
				return AutoText("对局启用 NVIDIA 驱动低延迟，关闭或退局还原；可能影响帧率。需驱动支持，部分游戏需重启。", "Enables NVIDIA low latency during matches; Off or exit restores it and FPS may be affected. Requires driver support; some games need a restart.");
			}
		}
		private static string PerformanceRecordingDetail()
		{
			return AutoText("守护期间关闭 Game DVR/Xbox 后台录制，即时回放与录制热键不可用。关闭开关、停守护或退出后还原；需要录屏时请关闭此项。", "Disables Game DVR/Xbox capture, instant replay and recording hotkeys while the guard runs. Off, guard stop or exit restores originals; turn this off to record.");
		}
		internal static string ConcisePerformanceDetail(string original, string explanation, bool keepStates = true)
		{
			int matchIndex = ((original == null) ? (-1) : original.IndexOf('\n'));
			if (matchIndex > 0 && original[matchIndex - 1] == '\r')
			{
				matchIndex--;
			}
			return explanation + ((keepStates && matchIndex >= 0) ? original.Substring(matchIndex) : "");
		}
		private void ChangePerformanceTuningBundle(object sender, EventArgs args)
		{
			if (!RequireElevationFor(swPerformanceEnvironmentBundle, PerformanceTuning.EnvironmentBundleEnabled))
			{
				return;
			}
			bool @checked = swPerformanceEnvironmentBundle.Checked;
			bool setEnvironmentBundleSucceeded = PerformanceTuning.SetEnvironmentBundle(@checked, delegate
			{
				try
				{
					if (!base.IsDisposed && base.IsHandleCreated)
					{
						BeginInvoke((Action)delegate
						{
							RefreshPerformanceEnvironmentObservation();
						});
					}
				}
				catch
				{
				}
			});
			if (setEnvironmentBundleSucceeded)
			{
				environmentBundleOverride = @checked;
				gameMode.Poke();
			}
			RefreshPerformanceEnvironmentObservation();
			if (!setEnvironmentBundleSucceeded)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
			}
		}
		private void ChangePerformanceTuningQuantum(object sender, EventArgs args)
		{
			int selectedIndex = swPerformanceEnvironmentQuantum.SelectedIndex;
			if (!elevated)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.needadmin"));
				SyncPerformanceTuningEnvironment();
				return;
			}
			bool setQuantumProfileSucceeded = PerformanceTuning.SetQuantumProfile(selectedIndex, delegate
			{
				try
				{
					if (!base.IsDisposed && base.IsHandleCreated)
					{
						BeginInvoke((Action)delegate
						{
							RefreshPerformanceEnvironmentObservation();
						});
					}
				}
				catch
				{
				}
			});
			if (setQuantumProfileSucceeded)
			{
				environmentQuantumOverride = selectedIndex;
				gameMode.Poke();
			}
			RefreshPerformanceEnvironmentObservation();
			if (!setQuantumProfileSucceeded)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
			}
		}
		private static string PerformanceFeatureKey(string name)
		{
			string nameOffset = name ?? "";
			string[] textEntries = new string[4] { "manualPerformance_", "privacyToggle", "environment", "laboratory_" };
			foreach (string entryText in textEntries)
			{
				if (nameOffset.StartsWith(entryText, StringComparison.Ordinal))
				{
					nameOffset = nameOffset.Substring(entryText.Length);
					break;
				}
			}
			switch (nameOffset)
			{
			case "laboratoryCpuIdle":
				return "LabCpuIdleStrategyV1";
            case "backgroundGovernanceMode":
                return "GmSuppress";
			case "WindowedOpt":
				return "WindowedOptimizations";
			case "VrrOpt":
				return "VariableRefreshRate";
			case "NvVrr":
				return "NvVrrWindowed";
			case "GameLowLatency":
				return "NvLowLat";
			default:
				return nameOffset;
			}
		}
		private static void ConfigurePerformanceConsole(PerformanceFeatureCard card, string key)
		{
			card.IconKey = key;
			switch (key)
			{
			case "GmBoost":
				card.Summary = AutoText("优先分配 CPU、GPU 与 I/O 资源", "Prioritize CPU, GPU and I/O resources");
				break;
			case "GmAwake":
				card.DisplayTitle = AutoText("对局保持唤醒", "Keep the match awake");
				card.Summary = AutoText("游戏前台时阻止息屏与睡眠", "Prevent display sleep while the game is foreground");
				break;
			case "GmThreadBoostStrategyV1":
				card.Summary = AutoText("按游戏自动提优渲染或最忙线程", "Boosts the render or busiest thread per game");
				break;
			case "AutoNtfsShortNamesV1":
				card.Summary = AutoText("停止生成新的 8.3 短文件名", "Stop creating new 8.3 short file names");
				break;
			case "GmCacheWarmV2":
				card.Summary = AutoText("空闲时预读，入局即取消", "Pre-read while idle; cancel on match entry");
				break;
			case "AutoGameMitigationsV1":
				card.Summary = AutoText("降低部分游戏防护，减少额外开销", "Reduce selected game protections and their overhead");
				break;
			case "GmMmcss":
				card.Summary = AutoText("调整游戏多媒体线程调度", "Adjust game multimedia thread scheduling");
				break;
			case LaptopPerfMode.PreferenceKey:
				card.Summary = AutoText("插电对局切换厂商性能档，退局恢复原档", "Use the vendor performance profile on AC; restore after matches");
				break;
			case "GmSuppress":
				card.Summary = Lang.T("background.governance.summary");
				break;
			case "GmPauseDl":
				card.Summary = AutoText("对局期间暂停支持的后台下载", "Pause supported background downloads during matches");
				break;
			case "GmPauseUpdate":
				card.Summary = AutoText("对局期间暂缓更新，退局恢复", "Pause updates during matches, restore on exit");
				break;
			case "GmPauseMaintenanceV1":
				card.Summary = AutoText("暂缓维护任务，减少资源竞争", "Defer maintenance to reduce resource contention");
				break;
			case "AutoBackgroundFpsV1":
				card.Summary = AutoText("限制合格后台渲染，不限制游戏前台", "Cap eligible background rendering, leave foreground uncapped");
				break;
			case "GmDwmBoost":
				card.Summary = AutoText("调整对局期间的桌面合成调度", "Adjust desktop compositor scheduling during matches");
				break;
			case "NvMaxPerf":
				card.Summary = AutoText("为游戏配置 NVIDIA 电源管理偏好", "Set NVIDIA power-management preferences for games");
				break;
			case "NvShaderCache":
				card.Summary = AutoText("为游戏配置 NVIDIA 着色器缓存", "Configure NVIDIA shader caching for games");
				break;
			case "GmIntelEnduranceV1":
				card.Summary = AutoText("关闭 Intel 图形续航限制，可能增加功耗", "Disable Intel graphics endurance limits; power use may rise");
				break;
			case "GmEnglishInputV1":
				card.Summary = AutoText("入局时为确认的游戏窗口切换英文", "Switch the confirmed game window to English on entry");
				break;
			case "GpuPrefStageOn":
				card.Summary = AutoText("为游戏登记高性能显卡偏好", "Prefer the high-performance GPU for games");
				break;
			case "AutoOpenGlThreadsV1":
				card.Summary = AutoText("为 NVIDIA OpenGL 游戏配置线程优化", "Configure threading for NVIDIA OpenGL games");
				break;
			case "Hags":
				card.Summary = AutoText("使用硬件加速 GPU 调度，视设备支持", "Use hardware-accelerated GPU scheduling where supported");
				break;
			case "WindowedOptimizations":
				card.Summary = AutoText("优化兼容的窗口与无边框游戏", "Optimize compatible windowed and borderless games");
				break;
			case "VariableRefreshRate":
				card.Summary = AutoText("为兼容的 DX11 全屏游戏补充 VRR", "Add VRR for compatible DX11 fullscreen games");
				break;
			case "NvVrrWindowed":
				card.Summary = AutoText("对局中补充窗口 VRR，退局还原", "Add windowed VRR during matches; restore on exit");
				break;
			case "AmdSam":
				card.Summary = AutoText("为受支持的设备启用 AMD SAM", "Enable AMD SAM on supported hardware");
				break;
			case "GameDvrOff":
				card.Summary = AutoText("守护期间关闭录制，停守护后还原", "Disable capture while guarding; restore when guarding stops");
				break;
			case "NvLowLat":
				card.Summary = AutoText("对局启用驱动低延迟，退局还原", "Use driver low latency during matches; restore on exit");
				break;
			case "EnvPerformanceBundleV1":
				card.Summary = AutoText("关闭 VBS 与 CPU 缓解，降低相应防护", "Disable VBS and CPU mitigations, reducing those protections");
				break;
			case "EnvCpuMitigationsV1":
				card.Summary = AutoText("关闭适用的软件缓解，降低相应防护", "Disable applicable software mitigations, reducing those protections");
				break;
			case "EnvStoragePowerGroupV1":
				card.Summary = AutoText("减少存储设备省电等待，视驱动支持", "Reduce storage power-saving delays where drivers support it");
				break;
			case "EnvQuantumProfileV1":
				card.Summary = AutoText("选择前台与后台任务的时间片策略", "Choose foreground and background scheduling quanta");
				break;
			case "AutoTimerTick":
				card.Summary = AutoText("调整启动时钟与节拍参数", "Adjust boot clock and tick parameters");
				break;
			case "AutoGlobalTimer":
				card.Summary = AutoText("仅对局请求 0.5 ms，实际精度由系统决定", "Request 0.5 ms in matches; Windows determines the result");
				break;
			case "LabEppYieldV1":
				card.Summary = AutoText("插电笔记本按功耗与瓶颈验证，失败即恢复", "Verify laptop power and bottlenecks on AC; revert on failure");
				break;
			case "LabGpuClockMaxV1":
				card.Summary = AutoText("NVIDIA 不保留原锁频，退局改回自动", "NVIDIA: exit restores auto clocks; existing locks are lost");
				break;
			case "LabGpuPowerMaxV1":
				card.Summary = AutoText("请求驱动最大功耗上限，可能增加发热；退局恢复", "Request the driver maximum power limit; heat may rise; restore on exit");
				break;
			case "LabAudioLowBufferV1":
				card.Summary = AutoText("申请较短共享音频周期，可能增加唤醒或爆音", "Request a shorter shared audio period; wakeups or crackles may increase");
				break;
			case "LabCpuIdleStrategyV1":
				card.Summary = AutoText("插电时作用当前计划，退局恢复；禁止空闲可能增热", "Current plan on AC; restore on exit; disabled idle may increase heat");
				break;
			case "EnvPrivacyBackgroundGlobalV1":
				card.Summary = AutoText("限制 Windows 应用后台活动，可能影响通知与同步", "Restrict Windows app background activity; notifications and sync may be affected");
				break;
			case "EnvPrivacyNtfsMemoryV1":
				card.Summary = AutoText("内存充足时扩大文件系统缓存", "Expand filesystem caching when memory allows");
				break;
			}
		}
		private void OrderPerformanceConsole()
		{
            string[] textEntries = new string[6] { "GmBoost", "GmThreadBoostStrategyV1", "AutoNtfsShortNamesV1", "GmCacheWarmV2", "AutoGameMitigationsV1", "GmMmcss" };
			Control control = performanceTabPanels[0];
			List<PerformanceFeatureCard> list = new List<PerformanceFeatureCard>();
			foreach (Control control2 in control.Controls)
			{
				if (control2 is PerformanceFeatureCard)
				{
					list.Add((PerformanceFeatureCard)control2);
				}
			}
			control.SuspendLayout();
			try
			{
				for (int textEntriesIndex = textEntries.Length - 1; textEntriesIndex >= 0; textEntriesIndex--)
				{
					foreach (PerformanceFeatureCard item in list)
					{
						if (item.IconKey == textEntries[textEntriesIndex])
						{
							control.Controls.SetChildIndex(item, 0);
						}
					}
				}
			}
			finally
			{
				control.ResumeLayout(false);
			}
		}
	}
}
