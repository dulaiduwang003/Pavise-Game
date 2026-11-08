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
		private void ResetEnvironmentObservation()
		{
			environmentObservation = new EnvironmentObservationCycle();
			environmentPreferenceOverrides.Clear();
			environmentBundleOverride = (environmentStorageOverride = (environmentStorageOverride = null));
			environmentQuantumOverride = null;
			environmentCpuMitigationsOverride = null;
		}
		private static EnvironmentPreferenceObservation ReadEnvironmentPreference(DirectXGlobalPreference preference)
		{
			bool on;
			bool readable = preference.TryReadState(out on);
			EnvironmentPreferenceObservation environmentPreferenceObservation = new EnvironmentPreferenceObservation();
			environmentPreferenceObservation.Readable = readable;
			environmentPreferenceObservation.On = on;
			environmentPreferenceObservation.Owned = preference.HasResidue;
			environmentPreferenceObservation.Supported = preference.Supported;
			return environmentPreferenceObservation;
		}
		private static EnvironmentGraphicsObservation ReadEnvironmentGraphics()
		{
			EnvironmentGraphicsObservation environmentGraphicsObservation = new EnvironmentGraphicsObservation();
			environmentGraphicsObservation.HagsKnown = HagsTweak.TryQueryState(out environmentGraphicsObservation.HagsSupported, out environmentGraphicsObservation.HagsOn);
			environmentGraphicsObservation.HagsConfigured = HagsTweak.ConfiguredMode();
			environmentGraphicsObservation.Windowed = ReadEnvironmentPreference(DirectXGlobalPreference.Windowed);
			environmentGraphicsObservation.Vrr = ReadEnvironmentPreference(DirectXGlobalPreference.Vrr);
			environmentGraphicsObservation.AmdAvailable = AdlxTweaks.Available;
			environmentGraphicsObservation.AmdReadable = environmentGraphicsObservation.AmdAvailable && AdlxTweaks.SamGet(out environmentGraphicsObservation.AmdSupported, out environmentGraphicsObservation.AmdOn);
			environmentGraphicsObservation.NvVrrApplicable = NvVrrWindowed.Applicable();
			environmentGraphicsObservation.NvVrrOwned = NvVrrWindowed.HasResidue;
			environmentGraphicsObservation.IntelEnduranceSupported = IntelGraphicsApi.HasAvailable && IntelEndurance.Supported();
			environmentGraphicsObservation.CacheWarmOn = PerformancePolicy.EnvironmentPreference("GmCacheWarmV2");
			environmentGraphicsObservation.TimerTickOn = PerformancePolicy.EnvironmentPreference("AutoTimerTick");
			environmentGraphicsObservation.GlobalTimerOn = PerformancePolicy.EnvironmentPreference("AutoGlobalTimer");
			environmentGraphicsObservation.GlobalTimerOwned = GlobalTimerResTweak.OwnsState;
			environmentGraphicsObservation.Tuning = new EnvironmentTuningObservation
			{
				BundleOn = PerformanceTuning.EnvironmentBundleEnabled,
				BundleState = PerformanceTuning.EnvironmentBundleState(),
				BundleLabel = PerformanceTuning.EnvironmentBundleLabel(),
				BundleDetail = EnvironmentBundlePerformanceDetail(PerformanceTuning.EnvironmentBundleDetail),
				StorageOn = PerformanceTuning.StorageGroupEnabled,
				StorageState = PerformanceTuning.StorageGroupState(),
				StorageLabel = PerformanceTuning.StorageGroupLabel(),
				QuantumOn = Settings.LoadCached("EnvQuantumProfileV1", false),
				CpuMitigationsOn = Settings.LoadCached(PerformanceActionCatalog.CpuMitigations, false),
				CpuMitigationsExpanded = PerformanceTuning.CpuMitigationsExpanded,
				CpuMitigationsState = PerformanceTuning.State(PerformanceActionCatalog.CpuMitigations),
				QuantumState = PerformanceTuning.State("EnvQuantumProfileV1"),
				QuantumSelection = QuantumProfiles.Selected,
				LegacyQuantum = QuantumRecovery.LegacyRecordsForDisplay
			};
			environmentGraphicsObservation.Tuning.StorageDetail = StoragePerformanceDetail(environmentGraphicsObservation.Tuning.StorageState, PerformanceTuning.StorageGroupDetail, new string[3]
			{
				PerformanceTuning.State("EnvStorageIdleV1"),
				PerformanceTuning.State("EnvStorageRuntimeV1"),
				PerformanceTuning.State("EnvStorageD3ColdV1")
			});
			return environmentGraphicsObservation;
		}
		private string EnvironmentObservationConfiguration()
		{
			// MutationGeneration invalidates the cache; only normalized preference values identify the phase.
			int version = Settings.MutationGeneration;
			if (environmentConfigurationSignature != null && environmentConfigurationGeneration == version) return environmentConfigurationSignature;
			StringBuilder value = new StringBuilder();
			foreach (PerformanceAction action in PerformanceActionCatalog.All)
				if (action.Environment) value.Append(action.Key).Append('=').Append(Settings.LoadCached(action.Key, false) ? '1' : '0').Append(';');
			foreach (string key in new[] { "EnvVbsDisabledV1", "EnvCpuMitigationsExpandedV1", "GmCacheWarmV2", "AutoTimerTick", "AutoGlobalTimer", "GmIntelEnduranceV1" })
				value.Append(key).Append('=').Append(Settings.LoadCached(key, false) ? '1' : '0').Append(';');
			value.Append("quantum=").Append(QuantumProfiles.Selected);
			environmentConfigurationSignature = value.ToString();
			environmentConfigurationGeneration = version;
			return environmentConfigurationSignature;
		}
		private void QueueEnvironmentObservation(bool force)
		{
			if (!UiActive || base.IsDisposed || base.Disposing || !base.IsHandleCreated)
			{
				return;
			}
			EnvironmentObservationCycle cycle = environmentObservation;
			if (force)
			{
				cycle.Version++;
				cycle.StartedUtc = DateTime.MinValue;
			}
			if (cycle.Busy != 0 || (!force && cycle.SettingsVersion == Settings.MutationGeneration && DateTime.UtcNow - cycle.StartedUtc < TimeSpan.FromSeconds(15.0)))
			{
				return;
			}
			long observationSession = gameMode == null ? 0 : gameMode.AutomaticSessionSerial;
			string observationConfiguration = EnvironmentObservationConfiguration();
			if (!environmentObservationFailures.Allowed(0, observationSession, observationConfiguration, "environment")) return;
			cycle.Busy = 1;
			cycle.StartedUtc = DateTime.UtcNow;
			int version = cycle.Version;
			int settingsVersion = Settings.MutationGeneration;
			cycle.SettingsVersion = settingsVersion;
			Action work = delegate
			{
				EnvironmentGraphicsObservation observed = null;
				try
				{
					observed = ReadEnvironmentGraphics();
				}
				catch (Exception ex)
				{
					int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
					Logger.Warn("Environment observation failure=" + failures + "/3: " + ex.Message);
				}
				if (observed != null) environmentObservationFailures.Succeeded(0, observationSession, observationConfiguration, "environment");
				Action apply = delegate
				{
					cycle.Busy = 0;
					if (!base.IsDisposed && !base.Disposing && object.ReferenceEquals(environmentObservation, cycle))
					{
						if (cycle.Version != version || Settings.MutationGeneration != settingsVersion)
						{
							cycle.StartedUtc = DateTime.MinValue;
							QueueEnvironmentObservation(false);
						}
						else
						{
							if (observed != null)
							{
								cycle.Snapshot = observed;
								environmentPreferenceOverrides.Clear();
								environmentBundleOverride = (environmentStorageOverride = (environmentStorageOverride = null));
								environmentQuantumOverride = null;
								environmentCpuMitigationsOverride = null;
							}
							ApplyEnvironmentObservation();
							RefreshAutomationPage();
						}
					}
				};
				try
				{
					if (!base.IsDisposed && base.IsHandleCreated)
					{
						BeginInvoke((MethodInvoker)delegate
						{
							apply();
						});
					}
				}
				catch (InvalidOperationException)
				{
					Interlocked.Exchange(ref cycle.Busy, 0);
				}
				catch (Win32Exception)
				{
					Interlocked.Exchange(ref cycle.Busy, 0);
				}
			};
			try
			{
				if (!ThreadPool.QueueUserWorkItem(delegate
				{
					work();
				}))
				{
					cycle.Busy = 0;
					int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
					Logger.Warn("Environment observation queue failure=" + failures + "/3");
				}
			}
			catch
			{
				cycle.Busy = 0;
				int failures = environmentObservationFailures.Failed(0, observationSession, observationConfiguration, "environment");
				Logger.Warn("Environment observation queue exception=" + failures + "/3");
			}
		}
		private static void ApplyEnvironmentPreference(EnvironmentPreferenceObservation value, SettingCard card, Toggle toggle)
		{
			if (card != null && toggle != null)
			{
				if (value == null)
				{
					toggle.Enabled = false;
					card.SetStatus(Lang.T("env.graphics.unknown"), Theme.Dim);
					return;
				}
				toggle.SetSilently(value.Readable && value.On);
				toggle.Enabled = value.Readable && value.Supported;
				LockEnvCard(card, toggle, false);
				string status = !value.Readable ? Lang.T("env.graphics.unknown") : !value.Supported ? Lang.T("lock.na")
                    : value.Owned ? AutoText(value.On ? "已由 Pavise 开启" : "已由 Pavise 关闭", value.On ? "Enabled by Pavise" : "Disabled by Pavise")
                    : Lang.T(value.On ? "lock.external" : "env.graphics.off");
                card.SetStatus(status, value.Owned && value.Readable ? Theme.Accent : Theme.Dim);
			}
		}
		private void ApplyEnvironmentObservation()
		{
			EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
			ApplyEnvironmentPreference((snapshot == null) ? null : snapshot.Windowed, cardWindowedOpt, swWindowedOpt);
			ApplyEnvironmentPreference((snapshot == null) ? null : snapshot.Vrr, cardVrrOpt, swVrrOpt);
			SyncHagsPresentation();
			if (swAmdSam != null && cardAmdSam != null)
			{
				bool flag = Settings.LoadCached("AmdSamByPavise", false);
				bool flag2 = snapshot != null && snapshot.AmdReadable && snapshot.AmdSupported;
				bool flag3 = snapshot != null && snapshot.AmdOn;
				swAmdSam.SetSilently(snapshot != null && snapshot.AmdReadable && flag3);
				swAmdSam.Enabled = flag2;
				LockEnvCard(cardAmdSam, swAmdSam, false);
				cardAmdSam.Desc = ((snapshot == null || (snapshot.AmdAvailable && !snapshot.AmdReadable) || flag2 || flag) ? PerformanceGraphicsDetail("AmdSam") : Lang.T("set.amd.nosup"));
				string text = ((snapshot == null || (snapshot.AmdAvailable && !snapshot.AmdReadable)) ? Lang.T("env.graphics.unknown") : ((!flag2) ? Lang.T("t.amdsam.unsupported") : (flag3 ? Lang.T(flag ? "t.amdsam.byus" : "t.amdsam.external") : Lang.T(flag ? "t.amdsam.reverted" : "t.amdsam.off"))));
				cardAmdSam.SetStatus(text, StatusInk(snapshot != null && !flag3, flag && flag3));
				if (cardAmdSam.LockText == Lang.T("lock.na") && text == Lang.T("t.amdsam.unsupported"))
				{
					cardAmdSam.SetLock("", false);
				}
			}
			if (swEnvNvVrr != null)
			{
				swEnvNvVrr.SetSilently(gameMode.NvVrrWindowedEnabled);
				swEnvNvVrr.Enabled = (snapshot != null && snapshot.NvVrrApplicable) || gameMode.NvVrrWindowedEnabled || (snapshot != null && snapshot.NvVrrOwned);
				SettingCard settingCard = swEnvNvVrr.Parent as SettingCard;
				if (settingCard != null)
				{
					settingCard.Desc = PerformanceGraphicsDetail("NvVrrWindowed") + ((snapshot != null && !swEnvNvVrr.Enabled) ? ("\n" + Lang.T("set.nvvrr.na")) : "");
				}
			}
		}
		private bool CachedEnvironmentPreference(string key)
		{
			bool value;
			if (environmentPreferenceOverrides.TryGetValue(key, out value))
			{
				return value;
			}
			EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
			if (snapshot == null)
			{
				return Settings.LoadCached(key, false);
			}
			if (!(key == "GmCacheWarmV2"))
			{
				if (!(key == "AutoTimerTick"))
				{
					return snapshot.GlobalTimerOn;
				}
				return snapshot.TimerTickOn;
			}
			return snapshot.CacheWarmOn;
		}
		private static bool HasPerformanceEnvironmentGraphics(PerformanceRestartScope scope)
		{
			string[] array = new string[6] { "Hags", "WindowedOptimizations", "VariableRefreshRate", "NvVrrWindowed", "AmdSam", "GameDvrOff" };
			foreach (string key in array)
			{
				if (PerformanceRestartFor(key) == scope)
				{
					return true;
				}
			}
			return false;
		}
		private void ChangeDirectXPreference(DirectXGlobalPreference preference, Toggle toggle)
		{
			bool flag = (toggle.Checked ? preference.Enable() : preference.Disable());
			Logger.Log("Environment graphics " + ((preference == DirectXGlobalPreference.Windowed) ? "windowed" : "VRR") + " request=" + toggle.Checked + " success=" + flag);
			QueueEnvironmentObservation(true);
			SyncEnvStatus();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.graphics.failed"));
			}
		}
		internal static HagsUiPresentation DescribeHags(bool known, bool supported, bool on, bool owned, int? configured)
		{
			bool flag = configured == 2;
			bool flag2 = configured == 1;
			HagsUiPresentation hagsUiPresentation = new HagsUiPresentation();
			hagsUiPresentation.Checked = flag2 ? false : (owned || flag || (known && on));
			hagsUiPresentation.External = !owned && (flag || (known && on));
			hagsUiPresentation.Ink = Theme.Dim;
			HagsUiPresentation hagsUiPresentation2 = hagsUiPresentation;
			hagsUiPresentation2.Editable = known && supported;
			if (owned && !flag)
			{
				hagsUiPresentation2.Status = (flag2 ? AutoText("保存请求已改为关闭", "Saved request changed to off") : AutoText("保存请求未核验", "Saved request unverified"));
				hagsUiPresentation2.Status += ((!known) ? AutoText(" · 当前未核验", " · current state unverified") : (on ? AutoText(" · 当前已开启", " · currently on") : AutoText(" · 当前已关闭", " · currently off")));
				hagsUiPresentation2.Ink = Theme.Warning;
				return hagsUiPresentation2;
			}
			if (!known)
			{
				hagsUiPresentation2.Status = ((owned || flag) ? AutoText("已保存开启请求 · 当前未核验", "On request saved · current state unverified") : (flag2 ? AutoText("已配置关闭 · 当前未核验", "Off configured · current state unverified") : AutoText("当前状态未核验", "Current state unverified")));
				if (owned || flag)
				{
					hagsUiPresentation2.Ink = Theme.Warning;
				}
			}
			else if (!supported)
			{
				hagsUiPresentation2.Status = ((owned || flag) ? AutoText("已保存请求 · 当前设备不支持", "Request saved · current device unsupported") : AutoText("当前设备不支持", "Current device unsupported"));
				if (owned || flag)
				{
					hagsUiPresentation2.Ink = Theme.Warning;
				}
			}
			else if (on)
			{
				hagsUiPresentation2.Status = (flag2 ? AutoText("当前已开启 · 已请求关闭", "Currently on · off requested") : AutoText("当前已开启", "Currently on"));
				hagsUiPresentation2.Ink = (flag2 ? Theme.Warning : Theme.Accent);
			}
			else
			{
				hagsUiPresentation2.Status = ((owned || flag) ? AutoText("已请求开启 · 待重启核验", "On requested · verify after restart") : AutoText("当前已关闭", "Currently off"));
				if (owned || flag)
				{
					hagsUiPresentation2.Ink = Theme.Warning;
				}
			}
			return hagsUiPresentation2;
		}
		private HagsUiPresentation CachedHagsPresentation()
		{
			EnvironmentGraphicsObservation snapshot = environmentObservation.Snapshot;
			return DescribeHags(snapshot != null && snapshot.HagsKnown, snapshot != null && snapshot.HagsSupported, snapshot != null && snapshot.HagsOn, Settings.LoadCached("HagsOnByPavise", false), (snapshot == null) ? null : snapshot.HagsConfigured);
		}
		private void AddEnvironmentPerformanceOption(Control parent, ref int y, string key, string title, string description)
		{
			Toggle toggle = MakeSwitch(CachedEnvironmentPreference(key), delegate
			{
				ChangeEnvironmentPerformanceOption(key);
			});
			toggle.Name = "environment" + key;
			toggle.AccessibleName = Lang.T(title);
			environmentPerformanceSwitches.Add(key, toggle);
			Control host = toggle;
			string desc = PerformanceEnvironmentExplanation(key, description);
			int num = FullTextCardHeight(desc, ScrollContentW, host, 88);
			if (key == "AutoGlobalTimer")
			{
				string readback = Lang.F("env.gtimer.readback", "0.5000", "15.6250", "0.5000");
				string[] array = new string[14]
				{
					"ready", "timer-locked", "timer-limited", "timer-unverified", "pending-reboot", "restore-pending-reboot", "unverified", "failed", "stopped", "restore-failed",
					"restored", "unsupported", "timer-requested", ""
				};
				foreach (string text in array)
				{
					num = Math.Max(num, FullTextCardHeight(GlobalTimerDescription(text, true, readback), ScrollContentW, host, 88));
					num = Math.Max(num, FullTextCardHeight(GlobalTimerDescription(text, false, readback), ScrollContentW, host, 88));
				}
			}
			int used;
			SettingCard settingCard = MakeAutoCard(parent, 6, y, ScrollContentW, num, Lang.T(title), desc, host, out used);
			settingCard.Height += Theme.S(17);
			y += used + 17 + 8;
			environmentPerformanceCards.Add(key, settingCard);
		}
		private void ChangeEnvironmentPerformanceOption(string key)
		{
			Toggle toggle = environmentPerformanceSwitches[key];
			bool restoredState = PerformancePolicy.EnvironmentPreference(key);
			if (!(key != "GmCacheWarmV2") || RequireElevationFor(toggle, restoredState))
			{
				bool flag = gameMode.SetEnvironmentPreference(key, toggle.Checked);
				if (flag)
				{
					environmentPreferenceOverrides[key] = toggle.Checked;
				}
				QueueEnvironmentObservation(true);
				SyncEnvironmentPerformanceOptions();
				if (!flag)
				{
					PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
				}
			}
		}
		private static string PerformanceEnvironmentExplanation(string key, string fallback)
		{
			switch (key)
			{
			case "AutoGlobalTimer":
				return AutoText("开启或还原后需重启；仅对局请求 0.5 ms，退局释放。故障后可点重试，无需反复切换开关。实际精度由系统决定，可能增加功耗。", "Restart after enabling or restoring; requests 0.5 ms only during matches, then releases it. Use Retry after a failure. Windows decides the actual resolution; power use may rise.");
			case "AutoTimerTick":
				return AutoText("手动设置时钟与节拍启动参数，开关后需重启。切换模式不改动此项，关闭恢复原配置。", "Manual clock/tick boot settings; restart after changes. Mode switches leave this choice alone; Off restores originals.");
			case "GmCacheWarmV2":
				return AutoText("仅游戏待命且桌面空闲时预读，入局或关闭即取消。需交流电、足够内存与 SSD，不保证提帧。", "Pre-reads only while a game is armed and the desktop is idle; cancels on entry or Off. Requires AC, ample RAM and an SSD; no FPS gain is guaranteed.");
			default:
				return Lang.T(fallback);
			}
		}
		private static string EnvironmentBundlePerformanceDetail(string original = null)
		{
			return ConcisePerformanceDetail(original, AutoText("关闭 VBS/内存完整性和 CPU 缓解，调整 NTFS 访问时间与音频电源。需重启；降低防护并影响 WSL2/Hyper-V，关闭恢复原配置。", "Disables VBS/Memory Integrity and CPU mitigations; adjusts NTFS access times and audio power. Restart required; reduces protection and may affect WSL2/Hyper-V; Off restores originals."));
		}
		internal static string CondenseStoragePerformanceDetail(string original, string state, string[] memberStates)
		{
			bool flag = memberStates != null && memberStates.Length == 3 && (state == "off" || state == "restored" || state == "ready");
			if (flag)
			{
				foreach (string text in memberStates)
				{
					flag &= ((state == "ready") ? (text == "ready" || text == "applied" || text == "configured") : (text == "" || text == "off" || (state == "restored" && text == "restored")));
				}
			}
			return ConcisePerformanceDetail(original, AutoText("关闭 StorPort 设备的空闲、运行时与 D3Cold 节能；需重启，可能增加功耗。关闭恢复原配置，不适用项跳过。", "Disables StorPort idle/runtime/D3Cold power saving. Restart required; power use may rise; Off restores originals and unsupported devices are skipped."), !flag);
		}
		private static string StoragePerformanceDetail(string state, string original = null, string[] memberStates = null)
		{
			return CondenseStoragePerformanceDetail(original, state, memberStates);
		}
		private void ChangePerformanceStorageEnvironment(object sender, EventArgs args)
		{
			if (!RequireElevationFor(swPerformanceEnvironmentStorage, PerformanceTuning.StorageGroupEnabled))
			{
				return;
			}
			bool @checked = swPerformanceEnvironmentStorage.Checked;
			bool flag = PerformanceTuning.SetStorageGroup(@checked, delegate
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
			if (flag)
			{
				environmentStorageOverride = @checked;
				gameMode.Poke();
			}
			RefreshPerformanceEnvironmentObservation();
			if (!flag)
			{
				PaviseDialog.Warn(this, "PAVISE AUTO", Lang.T("env.performance.savefail"));
			}
		}
		internal static int NvllIndexOf(string mode)
		{
			if (!(mode == "on"))
			{
				if (!(mode == "ultra"))
				{
					return 0;
				}
				return 2;
			}
			return 1;
		}
		internal static string NvllModeOf(int index)
		{
			switch (index)
			{
			default:
				return "off";
			case 2:
				return "ultra";
			case 1:
				return "on";
			}
		}
		internal static int DlssIndexOf(string mode)
		{
			switch (mode)
			{
			default:
				return 0;
			case "k":
				return 3;
			case "j":
				return 2;
			case "latest":
				return 1;
			}
		}
		internal static string DlssModeOf(int index)
		{
			switch (index)
			{
			default:
				return "off";
			case 3:
				return "k";
			case 2:
				return "j";
			case 1:
				return "latest";
			}
		}
	}
}
