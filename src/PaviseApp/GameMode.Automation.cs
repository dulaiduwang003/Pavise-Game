using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
	internal partial class GameMode
	{
		internal void InitializeAutomation()
		{
			Settings.Remove("AutomaticPolicyV1");
			Settings.Remove("AutoFrameCoordinationV1");
			MigrateLegacyPreset();
			MigrateLegacyLowLatency();
			RetireSceneRulePreferences();
			SyncAutomationPolicy();
		}
		private void SyncAutomationPolicy()
		{
			PolicySnapshot policySnapshot = sessionPolicy ?? PolicyResolver.Global();
			bgSuppressOn = policySnapshot.SuppressBackground;
			boostOn = policySnapshot.BoostGame;
			pauseUpdateOn = policySnapshot.PauseUpdate;
			pauseMaintOn = policySnapshot.PauseMaintenance;
			pauseServicesOn = policySnapshot.PauseServices;
			planSwitch = policySnapshot.PowerPlanOn;
			renderLaneOn = policySnapshot.RenderLane;
			cacheWarmOn = policySnapshot.CacheWarm;
			englishInputOn = policySnapshot.EnglishInput;
			nvMaxPerf = policySnapshot.NvMaxPerf;
			nvLowLatMode = policySnapshot.NvLowLatMode;
			nvSmoothMotion = false;
			nvShaderCacheMax = policySnapshot.NvShaderCacheMax;
			nvRebarOn = false;
			nvDlssMode = "off";
			amdAfmf = false;
			intelEnduranceOn = policySnapshot.IntelEnduranceOff;
			dwmBoostOn = policySnapshot.DwmBoost;
			nvVrrWindowedOn = Settings.LoadCached("NvVrrWindowed", false);
			rsrOn = false;
			gpuPrefStageOn = Settings.LoadCached("GpuPrefStageOn", true) && Native.OnAcPower();
			lock (sync)
			{
				if (!autoAddPreferenceUnsaved)
				{
					autoAddOn = Settings.LoadCached("GmAutoAdd", false);
				}
			}
			killGameDvr = Settings.LoadCached("GameDvrOff", true);
			mmcssOn = Settings.LoadCached("GmMmcss", true);
			SuppressionCore.GpuDemoteEnabled = bgSuppressOn;
		}
		internal void RefreshAutomaticPreferences()
		{
			lock (familyPolicyGate)
			{
				InvalidateFamilyPolicy();
				SyncAutomationPolicy();
			}
			RequestPolicyApply("RefreshAutomaticPreferences");
		}
		private void RecordAutomation(string key, string state)
		{
			lock (sync)
			{
				string value;
				automationEvidence.TryGetValue(key, out value);
				bool allConditionsMet = key == "awake" && state == "restored" && automationSessionEnding && !awakeFinalRestoreLogged;
				string powerDetail = key == "PowerPlanOn" ? PowerPlanFailureDetail(state) : "";
				string powerSignature = powerDetail.Length == 0 ? "" : state + "|" + powerDetail;
				bool powerFailureChanged = powerSignature.Length > 0 && powerFailureLogSignature != powerSignature;
				string timerDetail = key == "AutoGlobalTimer" ? GlobalTimerFailureDetail(state) : "";
				string timerSignature = timerDetail.Length == 0 ? "" : state + "|" + timerDetail;
				bool timerFailureChanged = timerSignature.Length > 0 && timerFailureLogSignature != timerSignature;
				if (value == state && !allConditionsMet && !powerFailureChanged && !timerFailureChanged)
				{
					return;
				}
				if (key == "PowerPlanOn") powerFailureLogSignature = powerSignature;
				if (key == "AutoGlobalTimer") timerFailureLogSignature = timerSignature;
				automationEvidence[key] = state;
				if ((key == "awake" && !ShouldLogAwakeTransition(state, value)) || string.IsNullOrEmpty(state) || state == "unchanged")
				{
					return;
				}
				if (key == "session" && !string.IsNullOrEmpty(automationSession))
				{
					string stateSnapshot = ((state == "started") ? "start" : state);
					string automationSessionNameOffset = "SESSION [" + automationSession + "] " + stateSnapshot + " | " + automationSessionName;
					if (state == "restore-failed")
					{
						Logger.Warn(automationSessionNameOffset);
					}
					else if (state == "restored")
					{
						Logger.Log("PASS " + automationSessionNameOffset);
					}
					else
					{
						Logger.Info(automationSessionNameOffset);
					}
					if (state == "started") Logger.RuntimeIdentity();
					return;
				}
				string stateOffset = "AUTO [" + automationSession + "] " + key + " | " + state;
				if (key == "PowerPlanOn")
				{
					if (powerDetail.Length > 0) stateOffset += " | " + powerDetail;
					if (state == "stopped")
					{
						Logger.Warn(stateOffset);
						return;
					}
				}
				if (key == "AutoGlobalTimer")
				{
					bool verified; uint actual, finest;
					bool leaseActive = timerLease.Readback(out verified, out actual, out finest);
					stateOffset += " | desired100ns=" + TimerResolutionLease.Desired + " active=" + leaseActive
						+ " verified=" + verified + " actual100ns=" + actual + " finest100ns=" + finest;
					if (timerDetail.Length > 0) stateOffset += " | " + timerDetail;
					if (state == "stopped") { Logger.Warn(stateOffset); return; }
				}
				switch (state)
				{
				case "failed":
				case "restore-failed":
					Logger.Warn(stateOffset);
					break;
				case "applied":
				case "restored":
				case "started":
				case "timer-locked":
					Logger.Log("PASS " + stateOffset);
					break;
				default:
					Logger.Info(stateOffset);
					break;
				}
			}
		}
		internal static string AutomationActionLabel(string key)
		{
			if (key == "session")
			{
				return Lang.T("auto.log.session");
			}
			if (key == "AutoDisableFullscreenOptimizations")
			{
				return Lang.T("auto.fso");
			}
			PolicyItem policyItem = PolicyCatalog.ItemOf(key);
			if (policyItem == null)
			{
				return EnvLabel(key);
			}
			return Lang.T(policyItem.LangKey);
		}
		internal static string AutomationStateLabel(string state)
		{
			switch (state ?? "")
			{
			case "": return Lang.T("auto.log.empty");
			case "started": return Lang.T("auto.log.started");
			case "applied": return Lang.T("auto.log.applied");
			case "configured": return Lang.T("auto.log.configured");
			case "fso-configured": return Lang.T("auto.fso.configured");
			case "restored": return Lang.T("auto.log.restored");
			case "failed": return Lang.T("auto.log.failed");
			case "restore-failed": return Lang.T("auto.log.restore-failed");
			case "pending-reboot": return Lang.T("auto.log.pending-reboot");
			case "restore-pending-reboot": return Lang.T("auto.log.restore-pending-reboot");
			case "ready": return Lang.T("auto.log.ready");
			case "unverified": return Lang.T("env.performance.unverified");
			case "timer-requested": return Lang.T("auto.log.timer-requested");
			case "timer-locked": return Lang.T("auto.log.timer-locked");
			case "timer-limited": return Lang.T("auto.log.timer-limited");
			case "timer-unverified": return Lang.T("auto.log.timer-unverified");
			case "unsupported": return Lang.T("auto.log.unsupported");
			case "unchanged": return Lang.T("auto.log.unchanged");
			case "stopped": return Lang.T("auto.log.stopped");
			case "restoring": return Lang.T("auto.log.restoring");
			default: return state;
			}
		}
		internal string AutomaticExecutionState(string key)
		{
			bool matchesActive = key == "GmPauseServices" && OptionalServicePause.Active;
			int pendingStopCount = (matchesActive ? OptionalServicePause.PendingStopCount : 0);
			lock (sync)
			{
				switch (key)
				{
				case "PowerPlanOn":
					return PowerPlanExecutionState;
				case "AutoDisableFullscreenOptimizations":
					return automaticFsoState;
				case "AutoTimerTick":
					return timerMode.TickState;
				case "AutoGlobalTimer":
					return GlobalTimerExecutionState();
				default:
				{
					string message = null;
					string[] envKeys = EnvKeys;
					foreach (string entryKey in envKeys)
					{
						if (EnvPolicyKey(entryKey) == key)
						{
							message = entryKey;
							break;
						}
					}
					if (message != null && envRestorePending.Contains(message))
					{
						return "restore-failed";
					}
					if (message != null && envFused.Contains(message))
					{
						return "stopped";
					}
					if (matchesActive)
					{
						return (pendingStopCount > 0) ? "unverified" : "applied";
					}
					string value;
					if (automationEvidence.TryGetValue(message ?? key, out value))
					{
						return value;
					}
					return "";
				}
				}
			}
		}
		private void BeginAutomationEvidence()
		{
			lock (sync)
			{
				EndAutomationEvidence();
				automationEvidence.Clear();
				automationSession = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
				automationSessionName = activeGame ?? ((sessionPolicy == null) ? "" : sessionPolicy.ProfileName) ?? "";
				automationSessionEnding = false;
				awakeAppliedLogTicks = (awakeRestoredLogTicks = 0L);
				awakeFinalRestoreLogged = false;
				awakeRecoveryState = null;
			}
			RecordAutomation("session", "started");
			PolicySnapshot automationPolicySnapshot = AutomationPolicySnapshot;
			foreach (PolicyItem item in PolicyCatalog.All)
			{
				if (!PerformancePolicy.IsIntent(item.Key) && !PerformancePolicy.IsRetired(item.Key))
				{
					string valueOfResult = automationPolicySnapshot.ValueOf(item.Key);
					if (valueOfResult == "0" || valueOfResult == "off")
					{
						RecordAutomation(item.Key, "unchanged");
					}
				}
			}
		}
		private void EndAutomationEvidence()
		{
			lock (sync)
			{
				if (!string.IsNullOrEmpty(automationSession) && !automationSessionEnding)
				{
					automationSessionEnding = true;
					Logger.Info("SESSION [" + automationSession + "] end | " + automationSessionName);
				}
			}
		}
		private static bool AutomaticQuietDesktop()
		{
			AutomationLastInput automationLastInput = default(AutomationLastInput);
			automationLastInput.Size = (uint)Marshal.SizeOf(typeof(AutomationLastInput));
			AutomationLastInput input = automationLastInput;
			if (GetLastInputInfo(ref input))
			{
				return (uint)(Environment.TickCount - (int)input.Time) >= 30000u;
			}
			return false;
		}
		private void PrewarmAutomatic(GameProfile profile)
		{
			if (cacheWarm == null || profile == null)
			{
				return;
			}
			if (!PolicyResolver.For(profile).CacheWarm)
			{
				cacheWarm.Cancel(false);
				return;
			}
			int foreground = RendererForegroundPid();
			string id = profile.Id;
			string exe = profile.PreferredExecutablePath;
			string root = profile.Root;
			int generation = cacheWarmGeneration;
			Func<bool> allowed = delegate
			{
				if (!active && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && generation == Volatile.Read(ref cacheWarmGeneration) && foreground > 0 && RendererForegroundPid() == foreground && AutomaticQuietDesktop())
				{
					lock (sync)
					{
						GameProfile gameProfile = FindProfileLocked(id);
						return gameProfile != null && string.Equals(armedGameName, gameProfile.Name, StringComparison.OrdinalIgnoreCase) && gameProfile.PreferredExecutablePath == exe && gameProfile.Root == root && PolicyResolver.For(gameProfile).CacheWarm;
					}
				}
				return false;
			};
			cacheWarm.Update("prelaunch:" + id + ":" + generation, exe, root, allowed);
		}
		private void SyncGameDvr()
		{
			SyncGuardScheduling();
		}
		private void RecordNvAutomation(string key, string state, bool preStage)
		{
			if (!preStage)
			{
				RecordAutomation(key, state);
				return;
			}
			string msg = "PRESTAGE " + Lang.T("auto.log.prestage") + " | " + AutomationActionLabel(key) + " | " + AutomationStateLabel(state);
			if (state == "failed")
			{
				Logger.Warn(msg);
			}
			else
			{
				Logger.Info(msg);
			}
		}
		private void InvalidateAutomaticFullscreenOptimizations()
		{
			lock (sync)
			{
				automaticFsoCheckAt = 0L;
				foreach (AutomaticFsoEntry value in automaticFsoEntries.Values)
				{
					value.CheckAt = 0L;
				}
			}
		}
		internal void SyncAutomaticFullscreenOptimizations(bool discoverTargets = false, long nowTicks = -1L)
		{
			if (nowTicks < 0)
			{
				nowTicks = DateTime.UtcNow.Ticks;
			}
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || autoIgnoreLoadFailed || (libraryIgnoreTransaction != null && libraryIgnoreTransaction.RecoveryPending))
				{
					return;
				}
				GameDetection gameDetection = activeDetection;
				GameProfile gameProfile = ((gameDetection == null || gameDetection.Profile == null) ? null : FindProfileLocked(gameDetection.Profile.Id));
				bool matchesSameProfile = gameDetection != null && gameDetection.Profile != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameProfile != null && gameProfile.DisableFullscreenOptimizations && RendererHandoffTracker.SameProfile(gameDetection.Profile, gameProfile);
				if ((!discoverTargets || !matchesSameProfile || string.IsNullOrEmpty(gameDetection.RendererPath) || automaticFsoEntries.ContainsKey(gameDetection.RendererPath)) && nowTicks < automaticFsoCheckAt)
				{
					return;
				}
				HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (GameProfile profile in profiles)
				{
					if (profile.DisableFullscreenOptimizations)
					{
						AddAutomaticFsoPath(hashSet, profile.ExecutablePath);
						AddAutomaticFsoPath(hashSet, profile.LearnedExecutablePath);
					}
				}
				if (matchesSameProfile)
				{
					AddAutomaticFsoPath(hashSet, gameDetection.RendererPath);
				}
				HashSet<string> hashSet2 = new HashSet<string>(hashSet, StringComparer.OrdinalIgnoreCase);
				bool syncAutomaticFullscreenOptimizationsReady = false;
				try
				{
					string[] trackedExesResult = FsoTweak.TrackedExes();
					foreach (string item in trackedExesResult)
					{
						hashSet2.Add(item);
						if (!hashSet.Contains(item))
						{
							syncAutomaticFullscreenOptimizationsReady = true;
						}
					}
				}
				catch
				{
					automaticFsoCheckAt = nowTicks + 600000000;
					automaticFsoState = "failed";
					RecordAutomation("AutoDisableFullscreenOptimizations", automaticFsoState);
					return;
				}
				foreach (string item2 in new List<string>(automaticFsoEntries.Keys))
				{
					if (!hashSet2.Contains(item2))
					{
						automaticFsoEntries.Remove(item2);
					}
				}
				bool syncAutomaticFullscreenOptimizationsReady2 = true;
				automaticFsoCheckAt = nowTicks + 600000000;
				foreach (string item3 in hashSet2)
				{
					if (stopping || ProfileStoreSaveFailed)
					{
						return;
					}
					bool disableValue = hashSet.Contains(item3);
					AutomaticFsoEntry value;
					if (!automaticFsoEntries.TryGetValue(item3, out value))
					{
						AutomaticFsoEntry automaticFsoEntry = new AutomaticFsoEntry();
						automaticFsoEntry.Disable = disableValue;
						value = automaticFsoEntry;
						automaticFsoEntries[item3] = value;
					}
					if (value.Disable != disableValue)
					{
						value.Disable = disableValue;
						value.Configured = false;
						value.Failures = 0;
						value.CheckAt = 0L;
					}
					if (nowTicks >= value.CheckAt)
					{
						value.Configured = FsoTweak.SetForExe(item3, disableValue);
						value.Failures = ((!value.Configured) ? Math.Min(value.Failures + 1, 5) : 0);
						int upperBound = (value.Configured ? 60 : Math.Min(300, 30 << value.Failures - 1));
						value.CheckAt = nowTicks + (long)upperBound * 10000000L;
					}
					syncAutomaticFullscreenOptimizationsReady2 &= value.Configured;
					automaticFsoCheckAt = Math.Min(automaticFsoCheckAt, value.CheckAt);
				}
				automaticFsoState = ((!syncAutomaticFullscreenOptimizationsReady2) ? "failed" : ((hashSet.Count > 0) ? "fso-configured" : (syncAutomaticFullscreenOptimizationsReady ? "restored" : "")));
				RecordAutomation("AutoDisableFullscreenOptimizations", automaticFsoState);
			}
		}
		private void AddAutomaticFsoPath(HashSet<string> paths, string path)
		{
			if (!string.IsNullOrEmpty(path) && Path.IsPathRooted(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && string.Equals(GameProfileStore.NormalizePath(path), path, StringComparison.OrdinalIgnoreCase) && GamePerformanceTargetPolicy.AllowsGameTarget(path) && File.Exists(path))
			{
				paths.Add(path);
			}
		}
		private void SyncPerformanceAutomation()
		{
			SyncLaboratory();
			string[] games = null;
			bool allConditionsMet;
			bool matchesAllConditionsMet;
			bool visualMatch;
			lock (sync)
			{
				allConditionsMet = enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && !autoIgnoreLoadFailed && (libraryIgnoreTransaction == null || !libraryIgnoreTransaction.RecoveryPending);
				matchesAllConditionsMet = allConditionsMet && active;
                visualMatch = false; // Desktop visual simplification is retired; only restore old records.
				if (allConditionsMet && !matchesAllConditionsMet && (PerformanceTuning.ActionSelected("AutoGameMitigationsV1") || PerformanceTuning.LegacyActionSelected("AutoOpenGlThreadsV1") || PerformanceTuning.ActionSelected("AutoNtfsShortNamesV1")))
				{
					HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
					foreach (GameProfile profile in profiles)
					{
						AddGamePerformancePath(hashSet, profile.ExecutablePath);
						AddGamePerformancePath(hashSet, profile.LearnedExecutablePath);
					}
					GameDetection gameDetection = activeDetection;
					if (gameDetection != null && gameDetection.Profile != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && RendererHandoffTracker.SameProfile(gameDetection.Profile, FindProfileLocked(gameDetection.Profile.Id)))
					{
						AddGamePerformancePath(hashSet, gameDetection.RendererPath);
					}
					games = new List<string>(hashSet).ToArray();
				}
			}
			performanceActions.Queue(allConditionsMet, matchesAllConditionsMet, games, visualMatch, true);
		}
		internal bool SetVisualEffectsReduction(bool value)
		{
			lock (sync)
			{
                if (value || stopping || !Settings.Save("MatchVisualEffectsV1", false))
				{
					return false;
				}
			}
			SyncPerformanceAutomation();
			RequestPolicyApply("SetVisualEffectsReduction");
			return true;
		}
		internal static NvGamePlan AutomaticPreStagePlan(PolicySnapshot policy, bool onAc, bool windowedVrr)
		{
			return NvGamePlan.ForAutomatic(policy.NvMaxPerf, policy.NvShaderCacheMax, onAc, policy.NvLowLatMode, windowedVrr, policy.NvRebar);
		}
		private bool RunPreStagedMutation(Action mutation)
		{
			return RunPreStagedWork(mutation, false);
		}
		private bool RunPreStagedWork(Action mutation, bool restoring)
		{
			lock (driverStageGate)
			{
				if (stopping || IsActive || mutation == null || (!restoring && sessionRecoveryCaptured && sessionRecoveryRoutes != null && !sessionRecoveryRoutes.Complete))
				{
					return false;
				}
				mutation();
				return true;
			}
		}
		private void ReleasePreStagedTuning()
		{
			InvalidateCacheWarm();
			if (stopping || preStagedNvPath == null)
			{
				return;
			}
			preStagedNvPath = null;
			ThreadPool.QueueUserWorkItem(delegate
			{
				try
				{
					RunPreStagedWork(delegate
					{
						NvDrsTweaks.RestoreAllGames();
						GpuPrefStage.Restore();
					}, true);
				}
				catch
				{
				}
			});
		}
	}
}
