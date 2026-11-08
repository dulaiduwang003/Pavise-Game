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
		private void BuildAuditPage()
		{
			Native.Dark(pageAudit);
			int num = PageHeader(pageAudit, Lang.T("nav.audit"), Lang.T("audit.sub"), 2, false);
			btnAuditRun = new PillButton(Lang.F("audit.rerun", 120), BtnKind.Primary);
			btnAuditRun.SetBounds(Theme.S(30), Theme.S(num), Theme.S(150), Theme.S(34));
			btnAuditRun.Click += delegate
			{
				StartAudit();
			};
			pageAudit.Controls.Add(btnAuditRun);
			int num2 = 158;
			int num3 = 150;
			lblAuditStatus = CardLabel(pageAudit, "", 30 + num2, num + 8, ContentW - num2 - num3 - 12, 20, 8f, false, Theme.Dim);
			btnAuditFixAll = new PillButton(Lang.T("audit.fixall.none"), BtnKind.Primary);
			btnAuditFixAll.SetBounds(Theme.S(30 + ContentW - num3), Theme.S(num), Theme.S(num3), Theme.S(34));
			btnAuditFixAll.Enabled = false;
			btnAuditFixAll.Click += delegate
			{
				OnAuditFixAllClick();
			};
			pageAudit.Controls.Add(btnAuditFixAll);
			btnAuditCancel = new PillButton(Lang.T("dlg.cancel"), BtnKind.Normal);
			btnAuditCancel.Name = "auditCancel";
			btnAuditCancel.Bounds = btnAuditFixAll.Bounds;
			btnAuditCancel.Visible = false;
			btnAuditCancel.Click += delegate
			{
				auditCancelRequested = true;
				btnAuditCancel.Enabled = false;
				auditScan.ReportProgress(0f, Lang.T("irqcheck.dialog.stopping"));
			};
			pageAudit.Controls.Add(btnAuditCancel);
			num += 44;
			auditScroll = new DBPanel();
			auditScroll.SetBounds(Theme.S(20), Theme.S(num), Theme.S(PageW - 40), Theme.S(Math.Max(180, PageH - num - 8)));
			auditScroll.BackColor = Theme.Bg;
			auditScroll.AutoScroll = true;
			auditScroll.Visible = false;
			Native.Dark(auditScroll);
			pageAudit.Controls.Add(auditScroll);
			auditScan = new ScanView();
			auditScan.SetBounds(Theme.S(30), Theme.S(num + 6), Theme.S(ContentW), Theme.S(Math.Max(380, PageH - num - 20)));
			pageAudit.Controls.Add(auditScan);
			btnAuditStart = new PillButton(Lang.T("audit.start"), BtnKind.Primary);
			btnAuditStart.Bg = Theme.Card;
			btnAuditStart.Click += delegate
			{
				StartAudit();
			};
			auditScan.Controls.Add(btnAuditStart);
			RestoreAuditPagePresentation();
			pageAudit.SizeChanged += delegate
			{
				LayoutAuditViewport();
			};
			LayoutAuditViewport();
		}
		private Dictionary<string, AuditFix> BuildAuditFixes()
		{
			Dictionary<string, AuditFix> dictionary = new Dictionary<string, AuditFix>(StringComparer.Ordinal);
			dictionary.Add("memcompress", new AuditFix
			{
				CanFix = () => auditReport != null && auditReport.MemoryCompressionCanEnable,
				CanRevert = () => MemCompressTweak.OwnsState,
				Fix = delegate
				{
					ApplyAuditMemoryCompression(false);
				},
				Revert = delegate
				{
					ApplyAuditMemoryCompression(true);
				},
				FixLabel = "audit.mem.enable"
			});
			dictionary.Add("gpuirq", new AuditFix
			{
				CanFix = () => auditReport != null && auditReport.GpuIrq != null && (auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsPlacement || auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsVerification) && !GpuIrqCheck.AwaitingReboot(GpuIrqCheck.LoadState()) && !gameMode.IsActive,
				CanRevert = () => false,
				Fix = delegate
				{
					auditIrqFix = RunIrqCheck();
				},
				FixLabel = ((auditReport != null && auditReport.GpuIrq != null && auditReport.GpuIrq.Outcome == GpuIrqOutcome.NeedsVerification) ? "irqcheck.btn.verify" : "audit.irq.apply")
			});
			dictionary.Add("gpuirqrestore", new AuditFix
			{
				CanFix = () => false,
				CanRevert = () => GpuIrqCheck.HasResidue || IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue,
				Revert = delegate
				{
					OnIrqRestore(null, EventArgs.Empty);
				}
			});
			dictionary.Add("msi", new AuditFix
			{
				CanFix = () => MsiModeTweak.Disabled().Count > 0,
				CanRevert = () => MsiModeTweak.EnabledByPavise,
				Fix = delegate
				{
					MsiModeTweak.Enable();
				},
				Revert = delegate
				{
					MsiModeTweak.Restore();
				},
				ConfirmKey = "audit.msi.confirm"
			});
			dictionary.Add("quantum", new AuditFix
			{
				CanFix = () => false,
				CanRevert = () => false,
				Open = delegate
				{
					ShowQuantumProfileSetting();
				},
				OpenText = () => PerformancePolicy.Text("前往时间片设置", "Open setting")
			});
			dictionary.Add("net", new AuditFix
			{
				CanFix = () => NetTweak.NeedsRepair(),
				CanRevert = () => NetTweak.RepairedByPavise,
				Fix = delegate
				{
					NetTweak.Repair();
				},
				Revert = delegate
				{
					NetTweak.Restore();
				}
			});
			dictionary.Add("linkmetric", new AuditFix
			{
				CanFix = () => LinkMetricTweak.NeedsRepair(),
				CanRevert = () => LinkMetricTweak.RepairedByPavise,
				Fix = delegate
				{
					LinkMetricTweak.Repair();
				},
				Revert = delegate
				{
					LinkMetricTweak.Restore();
				}
			});
			dictionary.Add("fth", new AuditFix
			{
				CanFix = () => FthTweak.NeedsRepair(),
				CanRevert = () => FthTweak.RepairedByPavise,
				Fix = delegate
				{
					FthTweak.Repair();
				},
				Revert = delegate
				{
					FthTweak.Restore();
				},
				ConfirmKey = "audit.fth.confirm"
			});
			return dictionary;
		}
		private void BuildPerformanceSystemOptions(Control scroll, ref int sy)
		{
			PerformanceRestartScope[] array = new PerformanceRestartScope[3]
			{
				PerformanceRestartScope.None,
				PerformanceRestartScope.ProcessRestart,
				PerformanceRestartScope.SystemRestart
			};
			foreach (PerformanceRestartScope performanceRestartScope in array)
			{
				bool flag = false;
				PrivacyAction[] all = PrivacyActionCatalog.All;
				foreach (PrivacyAction privacyAction in all)
				{
					if (privacyAction.Key != PrivacyActionCatalog.BackgroundGlobal && PerformanceRestartFor(privacyAction.Key) == performanceRestartScope)
					{
						flag = true;
						break;
					}
				}
				bool flag2 = PerformanceRestartFor("NvLowLat") == performanceRestartScope;
				if (flag || flag2 || performanceRestartScope == PerformanceRestartScope.SystemRestart || performanceRestartScope == PerformanceRestartScope.None)
				{
					AddAutomationSection(scroll, ref sy, PerformanceScopeTitle(performanceRestartScope), performanceRestartScope);
					if (performanceRestartScope == PerformanceRestartScope.None) BuildPerformanceQuantumEnvironment(scroll, ref sy);
					if (flag2)
					{
						swGameLowLatency = MakeSwitch(PerformancePolicy.GameLowLatency, OnGameLowLatencyToggle);
						swGameLowLatency.Name = "environmentGameLowLatency";
						swGameLowLatency.AccessibleName = Lang.T("set.lowlatency");
						string desc = PerformanceGraphicsDetail("NvLowLat");
						int used;
						cardGameLowLatency = MakeAutoCard(scroll, 6, sy, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swGameLowLatency, 82) + 17, Lang.T("set.lowlatency"), desc, swGameLowLatency, out used);
						sy += used + 8;
					}
					if (performanceRestartScope == PerformanceRestartScope.SystemRestart)
					{
						BuildPerformanceTuningEnvironment(scroll, ref sy);
						BuildPerformanceStorageEnvironment(scroll, ref sy);
						BuildEnvironmentTimerOptions(scroll, ref sy);
					}
					if (flag)
					{
						PrivacyOptionsPanel privacyOptionsPanel = BuildEnvironmentPrivacy(scroll, performanceRestartScope);
						if (performanceRestartScope == PerformanceRestartScope.SystemRestart && scroll is PerformanceFeatureGrid)
						{
							privacyOptionsPanel.PrependPerformanceCard((PerformanceFeatureCard)environmentPerformanceCards["AutoGlobalTimer"]);
						}
						privacyOptionsPanel.Top = Theme.S(sy);
						sy += Dpi.U(privacyOptionsPanel.Height) + 8;
					}
				}
			}
			if (scroll is PerformanceFeatureGrid)
			{
				return;
			}
			Dictionary<Control, int> dictionary = new Dictionary<Control, int>();
			foreach (Control control in scroll.Controls)
			{
				dictionary[control] = control.Top;
			}
			stackBase[scroll] = dictionary;
		}
		private void BuildPerformanceEnvironmentGraphics(Control scroll, ref int sy, PerformanceRestartScope scope)
		{
			int used;
			if (PerformanceRestartFor("Hags") == scope)
			{
				swHags = MakeSwitch(false, OnHagsToggle);
				swHags.Name = "environmentHags";
				cardHags = MakeAutoCard(scroll, 6, sy, ScrollContentW, 56, Lang.T("set.hags"), PerformanceGraphicsDetail("Hags"), swHags, out used);
				SyncHagsPresentation();
				sy += used + 8;
			}
			if (PerformanceRestartFor("WindowedOptimizations") == scope)
			{
				swWindowedOpt = MakeSwitch(false, delegate
				{
					ChangeDirectXPreference(DirectXGlobalPreference.Windowed, swWindowedOpt);
				});
				swWindowedOpt.Name = "environmentWindowedOpt";
				string desc = PerformanceGraphicsDetail("WindowedOptimizations");
				cardWindowedOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swWindowedOpt, 88), Lang.T("env.windowed"), desc, swWindowedOpt, out used);
				sy += used + 8;
			}
			if (PerformanceRestartFor("VariableRefreshRate") == scope)
			{
				swVrrOpt = MakeSwitch(false, delegate
				{
					ChangeDirectXPreference(DirectXGlobalPreference.Vrr, swVrrOpt);
				});
				swVrrOpt.Name = "environmentVrrOpt";
				string desc2 = PerformanceGraphicsDetail("VariableRefreshRate");
				cardVrrOpt = MakeAutoCard(scroll, 6, sy, ScrollContentW, FullTextCardHeight(desc2, ScrollContentW, swVrrOpt, 88), Lang.T("env.vrr"), desc2, swVrrOpt, out used);
				sy += used + 8;
			}
			if (PerformanceRestartFor("NvVrrWindowed") == scope)
			{
				swEnvNvVrr = MakeSwitch(gameMode.NvVrrWindowedEnabled, delegate
				{
					bool @checked = swEnvNvVrr.Checked;
					gameMode.NvVrrWindowedEnabled = @checked;
					swEnvNvVrr.SetSilently(gameMode.NvVrrWindowedEnabled);
					if (@checked != swEnvNvVrr.Checked)
					{
						PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.graphics.failed"));
					}
				});
				swEnvNvVrr.Name = "environmentNvVrr";
				swEnvNvVrr.Enabled = gameMode.NvVrrWindowedEnabled;
				string desc3 = PerformanceGraphicsDetail("NvVrrWindowed");
				MakeAutoCard(scroll, 6, sy, ScrollContentW, FullTextCardHeight(desc3, ScrollContentW, swEnvNvVrr, 88), Lang.T("set.nvvrr"), desc3, swEnvNvVrr, out used);
				sy += used + 8;
			}
			if (PerformanceRestartFor("AmdSam") == scope)
			{
				bool flag = Settings.LoadCached("AmdSamByPavise", false);
				swAmdSam = MakeSwitch(flag, OnAmdSamToggle);
				swAmdSam.Name = "environmentAmdSam";
				swAmdSam.Enabled = flag;
				cardAmdSam = MakeAutoCard(scroll, 6, sy, ScrollContentW, 76, Lang.T("set.amdsam"), PerformanceGraphicsDetail("AmdSam"), swAmdSam, out used);
				sy += used + 8;
			}
			if (PerformanceRestartFor("GameDvrOff") == scope)
			{
				BuildEnvironmentRecordingOption(scroll, ref sy);
			}
		}
		private void BuildEnvironmentPerformanceOptions(Control parent, ref int y)
		{
			environmentPerformanceSwitches.Clear();
			environmentPerformanceCards.Clear();
			AddEnvironmentPerformanceOption(parent, ref y, "GmCacheWarmV2", "gm.cachewarm", "env.cachewarm.manual");
			SyncEnvironmentPerformanceOptions();
		}
		private void BuildEnvironmentTimerOptions(Control parent, ref int y)
		{
			AddEnvironmentPerformanceOption(parent, ref y, "AutoTimerTick", "set.timertick", "env.timertick.manual");
			AddEnvironmentPerformanceOption(parent, ref y, "AutoGlobalTimer", "set.gtimer", "env.gtimer.manual");
			SyncEnvironmentPerformanceOptions();
		}
		private PrivacyOptionsPanel BuildEnvironmentPrivacy(Control parent, PerformanceRestartScope scope, Predicate<PrivacyAction> filter = null)
		{
			PrivacyControl.AdmissionAllowed = () => !base.IsDisposed && !base.Disposing && !runtimeShutdownBusy && wipeBusy == 0 && !gameMode.IsActive;
			PrivacyOptionsPanel privacyOptionsPanel = new PrivacyOptionsPanel(() => !runtimeShutdownBusy && wipeBusy == 0 && !gameMode.IsActive, delegate(string message)
			{
				if (!base.IsDisposed && !base.Disposing)
				{
					PaviseDialog.Warn(this, "PAVISE AUTO", message);
				}
			}, elevated, (PrivacyAction action) => PerformanceRestartFor(action.Key) == scope && (filter == null ? action.Key != PrivacyActionCatalog.BackgroundGlobal : filter(action)), true);
			privacyOptionsPanel.Name = ((performancePrivacyPanels.Count == 0) ? "environmentPrivacy" : ("environmentPrivacy" + scope));
			privacyOptionsPanel.SetBounds(Theme.S(6), Theme.S(2), Theme.S(ScrollContentW), Math.Max(Theme.S(4), privacyOptionsPanel.Height));
			performancePrivacyPanels.Add(privacyOptionsPanel);
			if (environmentPrivacy == null)
			{
				environmentPrivacy = privacyOptionsPanel;
			}
			parent.Controls.Add(privacyOptionsPanel);
			privacyOptionsPanel.SyncCached();
			return privacyOptionsPanel;
		}
		private void BuildEnvironmentRecordingOption(Control parent, ref int y)
		{
			swEnvironmentGameDvr = MakeSwitch(gameMode.KillGameDvr, OnEnvironmentGameDvrToggle);
			swEnvironmentGameDvr.Name = "environmentGameDvrOff";
			swEnvironmentGameDvr.AccessibleName = Lang.T("set.dvr");
			string text = PerformanceRecordingDetail();
			swEnvironmentGameDvr.AccessibleDescription = text;
			int used;
			cardEnvironmentGameDvr = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(text, ScrollContentW, swEnvironmentGameDvr, 88), Lang.T("set.dvr"), text, swEnvironmentGameDvr, out used);
			cardEnvironmentGameDvr.Height += Theme.S(17);
			y += used + 17 + 8;
			SyncEnvironmentGameDvr();
		}
		private void BuildPerformanceTuningEnvironment(Control parent, ref int y)
		{
			swPerformanceEnvironmentBundle = MakeSwitch(false, ChangePerformanceTuningBundle);
			swPerformanceEnvironmentBundle.Name = "environmentEnvPerformanceBundleV1";
			swPerformanceEnvironmentBundle.AccessibleName = PerformanceTuning.EnvironmentBundleTitle;
			string desc = EnvironmentBundlePerformanceDetail();
			int used;
			cardPerformanceEnvironmentBundle = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swPerformanceEnvironmentBundle, 100), PerformanceTuning.EnvironmentBundleTitle, desc, swPerformanceEnvironmentBundle, out used);
			cardPerformanceEnvironmentBundle.Height += Theme.S(17);
			y += used + 17 + 8;
			BuildPerformanceCpuMitigations(parent, ref y);
			SyncPerformanceTuningEnvironment();
		}
		private void BuildPerformanceQuantumEnvironment(Control parent, ref int y)
		{
			PerformanceAction performanceAction = PerformanceActionCatalog.Find("EnvQuantumProfileV1");
			string text = performanceAction.Title;
			string text2 = AutoText("游戏/日常选短可变 3:1，后台计算选长固定 1:1；比例不是帧率倍数。优先在线应用和恢复，系统拒绝或原值无法在线还原时提示重启电脑。“恢复原配置”也撤销旧版校正。", "Use short variable 3:1 for games/daily use or long fixed 1:1 for background work; these are turn ratios, not FPS multipliers. Apply and restore online where supported; a PC restart is shown if Windows rejects the update or the original cannot be restored online. Restore original also undoes legacy corrections.");
			int used;
			swPerformanceEnvironmentQuantum = new QuantumProfilePicker();
			swPerformanceEnvironmentQuantum.SetSilently(0);
			swPerformanceEnvironmentQuantum.SelectionChanged += ChangePerformanceTuningQuantum;
			swPerformanceEnvironmentQuantum.Name = "environment" + performanceAction.Key;
			swPerformanceEnvironmentQuantum.AccessibleName = text;
			swPerformanceEnvironmentQuantum.AccessibleDescription = text2;
			cardPerformanceEnvironmentQuantum = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(text2, ScrollContentW, swPerformanceEnvironmentQuantum, 88), text, text2, swPerformanceEnvironmentQuantum, out used);
			cardPerformanceEnvironmentQuantum.Height += Theme.S(17);
			y += used + 17 + 8;
			SyncPerformanceTuningEnvironment();
		}
		private void BuildPerformanceStorageEnvironment(Control parent, ref int y)
		{
			swPerformanceEnvironmentStorage = MakeSwitch(false, ChangePerformanceStorageEnvironment);
			swPerformanceEnvironmentStorage.Name = "environmentEnvStoragePowerGroupV1";
			swPerformanceEnvironmentStorage.AccessibleName = PerformanceTuning.StorageGroupTitle;
			string desc = StoragePerformanceDetail("off");
			int used;
			cardPerformanceEnvironmentStorage = MakeAutoCard(parent, 6, y, ScrollContentW, FullTextCardHeight(desc, ScrollContentW, swPerformanceEnvironmentStorage, 100), PerformanceTuning.StorageGroupTitle, desc, swPerformanceEnvironmentStorage, out used);
			cardPerformanceEnvironmentStorage.Height += Theme.S(17);
			y += used + 17 + 8;
			SyncPerformanceStorageEnvironment();
		}
		private void BuildSettingsPage()
		{
			Interlocked.Increment(ref slowVersion);
			InitializeSettingsWorkspace();
			Control scroll = settingsTabPanels[0];
			swAuto = MakeSwitch(TaskHelper.IsStartupTaskEnabledCached(), OnAutoToggle);
			AddSettingsTile(scroll, "SettingsStartup", Lang.T("set.autostart"), Lang.T("set.autostart.n"), "startup", swAuto);
			swAutoHide = MakeSwitch(Settings.Load("AutoHideOnGame", true), OnAutoHideToggle);
			AddSettingsTile(scroll, "SettingsAutoHide", Lang.T("set.autohide"), Lang.T("set.autohide.n"), "hide", swAutoHide);
			int[] exitGraceChoices = new int[6] { 0, 2, 5, 8, 15, 30 };
			TierPicker pickExitGrace = new TierPicker
			{
				Name = "GameExitGracePicker"
			};
			string[] array = new string[exitGraceChoices.Length];
			for (int i = 0; i < exitGraceChoices.Length; i++)
			{
				array[i] = Lang.F("set.exitgrace.seconds", exitGraceChoices[i]);
			}
			pickExitGrace.Labels = array;
			pickExitGrace.Index = Array.IndexOf(exitGraceChoices, gameMode.ExitGraceSeconds);
			pickExitGrace.Size = new Size(Theme.S(404), Theme.S(40));
			pickExitGrace.IndexChanged = delegate(int index)
			{
				if (index >= 0 && index < exitGraceChoices.Length && !gameMode.TrySetExitGraceSeconds(exitGraceChoices[index]))
				{
					pickExitGrace.Index = Array.IndexOf(exitGraceChoices, gameMode.ExitGraceSeconds);
					PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("set.exitgrace.fail"));
				}
			};
			AddSettingsTile(scroll, "SettingsExitGrace", Lang.T("set.exitgrace"), Lang.T("set.exitgrace.n"), "clock", pickExitGrace).StackHost = true;
			TierPicker tierPicker = new TierPicker();
			tierPicker.Name = "SettingsLanguagePicker";
			TierPicker tierPicker2 = tierPicker;
			tierPicker2.Labels = new string[2]
			{
				Lang.T("lang.zh"),
				Lang.T("lang.en")
			};
			tierPicker2.Index = Lang.Cur;
			tierPicker2.Size = new Size(Theme.S(232), Theme.S(40));
			tierPicker2.IndexChanged = delegate(int index)
			{
				Lang.Set(index);
				BeginInvoke(new MethodInvoker(RebuildUi));
			};
			AddSettingsTile(scroll, "SettingsLanguage", Lang.T("set.lang"), Lang.T("set.lang.n"), "language", tierPicker2);
			int sy = 0;
			BuildStandbyCleanerSettingsCard(scroll, ref sy);
			BuildAiTuningSettingsCard(scroll);
			RefreshStandbyCleanerPresentation();
			scroll = settingsTabPanels[1];
			PillButton pillButton = new PillButton(Lang.T("workflow.attention.action.logs"));
			pillButton.Name = "settingsOpenLogs";
			pillButton.Bg = Theme.Card;
			pillButton.Size = new Size(Theme.S(136), Theme.S(40));
			PillButton pillButton2 = pillButton;
			pillButton2.Click += delegate
			{
				nav.Select(7);
			};
			AddSettingsTile(scroll, "SettingsLogs", Lang.T("nav.log"), Lang.T("v20.log.sub"), "logs", pillButton2);
			btnWipeAll = new PillButton(Lang.T("btn.wipe"), BtnKind.Normal)
			{
				Bg = Theme.Card,
				Size = new Size(Theme.S(136), Theme.S(40))
			};
			btnWipeAll.Click += delegate
			{
				OnWipeAll();
			};
			AddSettingsTile(scroll, "SettingsReset", Lang.T("btn.wipe"), Lang.T("set.reset.desc"), "reset", btnWipeAll);
			btnUninstall = new PillButton(Lang.T("btn.uninstall"), BtnKind.Danger)
			{
				Bg = Theme.Card,
				Size = new Size(Theme.S(128), Theme.S(40))
			};
			btnUninstall.Click += delegate
			{
				OnUninstall();
			};
			AddSettingsTile(scroll, "SettingsUninstall", Lang.T("btn.uninstall"), Lang.T("set.uninstall.desc"), "uninstall", btnUninstall);
			btnShaderGo = new PillButton(Lang.T("btn.clean"))
			{
				Size = new Size(Theme.S(112), Theme.S(40))
			};
			btnShaderGo.Click += delegate(object s, EventArgs e)
			{
				OnShaderClean((PillButton)s);
			};
			cardShader = AddSettingsTile(scroll, "SettingsShader", Lang.T("btn.shader"), Lang.T("set.shader.n"), "shader", btnShaderGo);
			cardShader.Value = " ";
			SyncShaderCleanupPresentation();
			scroll = settingsTabPanels[2];
			sy = 2;
			BuildAppearanceSection(scroll, ref sy);
			LayoutSettingsWorkspace();
		}
		private void BuildStandbyCleanerSettingsCard(Control parent, ref int y)
		{
			StandbyCleanerActionHost standbyCleanerActionHost = new StandbyCleanerActionHost();
			standbyCleanerActionHost.Size = new Size(Theme.S(188), Theme.S(30));
			PillButton pillButton = new PillButton(Lang.T("standbycleaner.options"), BtnKind.Normal);
			pillButton.Name = "StandbyCleanerOptions";
			pillButton.SetBounds(0, 0, Theme.S(128), Theme.S(30));
			pillButton.AccessibleName = Lang.T("standbycleaner.options.title");
			pillButton.Click += delegate
			{
				EditStandbyCleanerOptions();
			};
			swPolicyStandbyCleaner = MakeSwitch(gameMode.StandbyCleanerEnabled, null);
			swPolicyStandbyCleaner.Name = "environmentStandbyCleaner";
			swPolicyStandbyCleaner.Location = new Point(Theme.S(142), Theme.S(3));
			swPolicyStandbyCleaner.AccessibleName = Lang.T("gm.standbycleaner");
			swPolicyStandbyCleaner.CheckedChanged += delegate
			{
				OnStandbyCleanerToggle(swPolicyStandbyCleaner.Checked);
			};
			standbyCleanerActionHost.Controls.Add(pillButton);
			standbyCleanerActionHost.Controls.Add(swPolicyStandbyCleaner);
			SettingsWorkspaceTile settingsWorkspaceTile = AddSettingsTile(parent, "SettingsStandbyCleanerCard", Lang.T("gm.standbycleaner"), SettingsStandbyCleanerDetail(), "memory", standbyCleanerActionHost);
			settingsWorkspaceTile.Summary = PerformancePolicy.Text("对局中 · 双阈值触发", "During matches · both thresholds");
			cardPolicyStandbyCleaner = settingsWorkspaceTile;
			cardPolicyStandbyCleaner.Name = "SettingsStandbyCleanerCard";
			cardPolicyStandbyCleaner.TrackChildHover(pillButton);
			cardPolicyStandbyCleaner.TrackChildHover(swPolicyStandbyCleaner);
			y += 120;
		}
	}
}
