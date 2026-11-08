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
		internal List<string> ApplyNvGamePlan(string path, NvGamePlan plan, out bool retry, long now = -1L, bool preStage = false)
		{
			SyncNvAutomaticPhase(path, plan);
			List<string> list = NvDrsTweaks.ApplyForGame(path, plan, out retry);
			if (list == null && retry && plan != null)
			{
				if (plan.MaxPerf)
				{
					RecordNvAutomation("NvMaxPerf", "failed", preStage);
				}
				if (plan.ShaderCacheMax)
				{
					RecordNvAutomation("NvShaderCache", "failed", preStage);
				}
				if (plan.LowLatMode == "on")
				{
					RecordNvAutomation("NvLowLat", "failed", preStage);
				}
			}
			return list;
		}
		internal void ResetNvApplyAttempts()
		{
		}
		internal NvGamePlan RefreshAutomaticNvPlan(bool maxPerf, bool cache, bool onAc, string lowLatency = "off", bool windowedVrr = false, bool rebar = false)
		{
			NvGamePlan nvGamePlan = NvGamePlan.ForAutomatic(maxPerf, cache, onAc, lowLatency, windowedVrr, rebar);
			int automaticKey = nvGamePlan.AutomaticKey;
			lock (sync)
			{
				if (automaticNvPlanKey != automaticKey)
				{
					automaticNvPlanKey = automaticKey;
					ForgetNvTweaksLocked();
					Interlocked.Exchange(ref nvTweakRetryAtTicks, 0L);
				}
			}
			return nvGamePlan;
		}
		internal bool SetEnvironmentPreference(string key, bool value)
		{
			if (key == "AutoTimerTick" && value && LaboratoryClockRepair.HasReceipt) return false;
			if (!PerformancePolicy.IsEnvironment(key))
			{
				return false;
			}
			if (key == "GmStandbyCleanerV2")
			{
				return TrySetStandbyCleanerEnabled(value);
			}
			lock (timerMode.Gate)
			{
				if (key == "AutoTimerTick" && value && LaboratoryClockRepair.HasReceipt) return false;
				lock (sync)
				{
					if (stopping || !Settings.Save(key, value))
					{
						return false;
					}
					if (key == "GmCacheWarmV2")
					{
						cacheWarmOn = value;
						InvalidateCacheWarm();
					}
				}
			}
			RequestPolicyApply("SetEnvironmentPreference");
			return true;
		}
		private void ForgetNvTweaksLocked()
		{
			tweakApplied.Clear();
			nvTweakGeneration++;
		}
		private void ForgetNvTweakLocked(int pid)
		{
			tweakApplied.Remove(pid);
			nvTweakGeneration++;
		}
		private bool NvTweakTargetCurrentLocked(int pid, long creation, long generation)
		{
			if (active && enabled && !stopping && !panicReq && generation == nvTweakGeneration && activeDetection != null && activeDetection.RendererPid == pid)
			{
				return activeDetection.RendererCreation == creation;
			}
			return false;
		}
		private void InvalidateCacheWarm()
		{
			Interlocked.Increment(ref cacheWarmGeneration);
			if (cacheWarm != null)
			{
				cacheWarm.Cancel(true);
			}
		}
		private void BeginCacheWarmSession()
		{
			lock (sync)
			{
				Interlocked.Increment(ref cacheWarmSessionId);
				InvalidateCacheWarm();
			}
		}
		private Func<bool> CaptureCacheWarmAdmission(out string key, out string executable, out string root)
		{
			string profileId;
			string rendererPath;
			string installRoot;
			string profileEntry;
			long session;
			int generation;
			lock (sync)
			{
				GameDetection gameDetection = activeDetection;
				profileId = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Id : null);
				executable = ((gameDetection != null) ? gameDetection.RendererPath : null);
				root = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Root : null);
				rendererPath = executable;
				installRoot = root;
				profileEntry = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.ExecutablePath : null);
				session = Interlocked.Read(ref cacheWarmSessionId);
				generation = cacheWarmGeneration;
				key = profileId + ":" + session + ":" + generation + "|" + installRoot + "|" + rendererPath + "|" + profileEntry;
			}
			return delegate
			{
				lock (sync)
				{
					GameDetection gameDetection2 = activeDetection;
					GameProfile gameProfile = FindProfileLocked(profileId);
					return generation == cacheWarmGeneration && active && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed && !stickyGraceOnly && gameGoneSinceTicks == 0 && session > 0 && Volatile.Read(ref standbyCleanerRestorePending) == 0 && session == Interlocked.Read(ref cacheWarmSessionId) && !string.IsNullOrEmpty(profileId) && gameDetection2 != null && gameDetection2.Profile != null && gameDetection2.Profile.Id == profileId && string.Equals(gameDetection2.RendererPath, rendererPath, StringComparison.OrdinalIgnoreCase) && string.Equals(gameDetection2.Profile.Root, installRoot, StringComparison.OrdinalIgnoreCase) && gameProfile != null && string.Equals(gameProfile.Root, installRoot, StringComparison.OrdinalIgnoreCase) && string.Equals(gameProfile.ExecutablePath, profileEntry, StringComparison.OrdinalIgnoreCase) && LiveBoolPreferenceLocked("GmCacheWarmV2", cacheWarmOn) && !LiveBoolPreferenceLocked("GmStandbyCleanerV2", standbyCleanerOn);
				}
			};
		}
		private void ApplyCacheWarmPolicy()
		{
			if (cacheWarm != null)
			{
				cacheWarm.Cancel(false);
			}
		}
		private void InitializeEnglishInput()
		{
			Interlocked.Exchange(ref englishInputCreationFloor, DateTime.UtcNow.ToFileTimeUtc());
			englishInputOn = Settings.Load("GmEnglishInputV1", false);
			englishInputOnce = new EnglishInputOnce(new GameInputLanguage());
		}
		private bool LiveEnglishInputPreference()
		{
			return LiveBoolPreferenceLocked("GmEnglishInputV1", englishInputOn);
		}
		private Func<bool> CaptureEnglishInputAdmission(bool entry, out string profileId, out string profileName, out GameInputProcess process, out bool wanted)
		{
			int generation;
			long creationFloor;
			string id;
			GameInputProcess renderer;
			bool preference;
			lock (sync)
			{
				generation = Volatile.Read(ref englishInputGeneration);
				creationFloor = Volatile.Read(ref englishInputCreationFloor);
				GameDetection gameDetection = activeDetection;
				profileId = (id = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Id : null));
				profileName = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Name : "");
				bool flag = gameDetection != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm;
				process = (renderer = (flag ? new GameInputProcess(gameDetection.RendererPid, gameDetection.RendererCreation) : default(GameInputProcess)));
				PolicySnapshot policySnapshot = sessionPolicy;
				wanted = (preference = LiveEnglishInputPreference() && !string.IsNullOrEmpty(id) && creationFloor > 0 && (!renderer.IsValid || renderer.Creation >= creationFloor) && (policySnapshot == null || string.IsNullOrEmpty(policySnapshot.ProfileId) || string.Equals(policySnapshot.ProfileId, id, StringComparison.OrdinalIgnoreCase)));
			}
			return delegate
			{
				if (!preference || creationFloor != Volatile.Read(ref englishInputCreationFloor) || generation != Volatile.Read(ref englishInputGeneration) || !Volatile.Read(ref active) || !enabled || stopping || panicReq || Volatile.Read(ref standbyCleanerRestorePending) != 0 || ProfileStoreSaveFailed || Volatile.Read(ref stickyGraceOnly) || Volatile.Read(ref gameGoneSinceTicks) != 0)
				{
					return false;
				}
				GameDetection gameDetection2 = Volatile.Read(ref activeDetection);
				if (gameDetection2 == null || gameDetection2.Profile == null || !string.Equals(gameDetection2.Profile.Id, id, StringComparison.OrdinalIgnoreCase))
				{
					return false;
				}
				bool flag2 = gameDetection2.RendererCandidateSelected && !gameDetection2.RendererSafetyOnly && !gameDetection2.RequiresGpuConfirm;
				if (flag2 && gameDetection2.RendererCreation > 0 && gameDetection2.RendererCreation < creationFloor)
				{
					return false;
				}
				return entry || (flag2 && renderer.IsValid && renderer.Creation >= creationFloor && gameDetection2.RendererPid == renderer.Pid && gameDetection2.RendererCreation == renderer.Creation);
			};
		}
		private void BeginEnglishInputSession()
		{
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce != null)
			{
				string profileId;
				string profileName;
				GameInputProcess process;
				bool wanted;
				Func<bool> admission = CaptureEnglishInputAdmission(true, out profileId, out profileName, out process, out wanted);
				englishInputOnce.Begin(profileId, process, wanted, admission);
			}
		}
		private void StepEnglishInputSession()
		{
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce == null)
			{
				return;
			}
			string profileId;
			string profileName;
			GameInputProcess process;
			bool wanted;
			Func<bool> admission = CaptureEnglishInputAdmission(false, out profileId, out profileName, out process, out wanted);
			EnglishInputOutcome englishInputOutcome = englishInputOnce.Step(profileId, process, admission);
			if (englishInputOutcome != null)
			{
				string key;
				switch (englishInputOutcome.Result)
				{
				case EnglishInputResult.Changed:
					key = "englishinput.changed";
					break;
				case EnglishInputResult.AlreadyEnglish:
					key = "englishinput.already";
					break;
				case EnglishInputResult.Unconfirmed:
					key = "englishinput.unconfirmed";
					break;
				case EnglishInputResult.Denied:
					key = "englishinput.denied";
					break;
				case EnglishInputResult.NoEnglishLayout:
					key = "englishinput.nolayout";
					break;
				case EnglishInputResult.TargetChanged:
					key = "englishinput.targetchanged";
					break;
				case EnglishInputResult.Expired:
					key = "englishinput.expired";
					break;
				case EnglishInputResult.Canceled:
					key = "englishinput.canceled";
					break;
				case EnglishInputResult.HistoryFull:
					key = "englishinput.historyfull";
					break;
				default:
					key = "englishinput.unavailable";
					break;
				}
				Logger.Log(Lang.F("log.englishinput.result", profileName, Lang.T(key), englishInputOutcome.Error));
			}
		}
		private void InvalidateEnglishInputWork()
		{
			Interlocked.Increment(ref englishInputGeneration);
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce != null)
			{
				englishInputOnce.Cancel();
			}
		}
		private void EndEnglishInputSession()
		{
			InvalidateEnglishInputWork();
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce != null)
			{
				englishInputOnce.End();
			}
		}
		private bool DrainEnglishInput(int timeoutMs)
		{
			EnglishInputOnce englishInputOnce = this.englishInputOnce;
			if (englishInputOnce != null)
			{
				return englishInputOnce.Drain(timeoutMs);
			}
			return true;
		}
		private static string EnvLabel(string key)
		{
			switch (key)
			{
			case "do":
				return Lang.T("t.gamemodeenv.1");
			case "wu":
				return Lang.T("t.gamemodeenv.3");
			case "services":
				return Lang.T("gm.pausesvc");
			case "standby":
				return Lang.T("gm.standbycleaner");
			case "pqos":
				return Lang.T("t.gamemodeenv.4");
			case "awake":
				return Lang.T("t.gamemodeenv.5");
			case "dwmboost":
				return Lang.T("gm.dwmboost");
			case "rsr":
				return Lang.T("set.rsr");
			case "gpupower":
				return Lang.T("t.gamemodeenv.6");
			case "maint":
				return Lang.T("gm.pausemaint");
			case "nvvrr":
				return Lang.T("set.nvvrr");
			case "intelend":
				return Lang.T("set.intel.endurance");
			case "laptopperf":
				return Lang.T("gm.laptopperf");
			case "amdalag":
				return "AMD Anti-Lag";
			case "amdafmf":
				return Lang.T("t.gamemodeenv.8");
			case "GameDvrOff":
				return Lang.T("set.dvr");
			case "GmMmcss":
				return Lang.T("auto.mmcss");
			case "gamedvr":
				return Lang.T("set.dvr");
			case "mmcss":
				return Lang.T("auto.mmcss");
			default:
				return key;
			}
		}
		private bool EnvStep(string key, bool want, bool active, Func<bool> activate, Func<bool> restore)
		{
			long session = Interlocked.Read(ref gameSessionStarts);
			bool originalActive = active;
			string action = EnvFailureAction(key);
			bool flag;
			lock (sync)
			{
				if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
				flag = SyncEnvFailureIsolation(key);
				if (envRestorePending.Contains(key))
				{
					want = false;
					active = true;
				}
			}
			if (want && flag)
			{
				want = false;
			}
			if (want == active)
			{
				return active;
			}
			long ticks = DateTime.UtcNow.Ticks;
			lock (sync)
			{
				long value;
				if (envNextAttempt.TryGetValue(key, out value) && ticks < value)
				{
					if (!want) { if (value == long.MaxValue) RecoveryAttemptTrace.Exhausted(); else RecoveryAttemptTrace.DeferredUntil(value); }
					return active;
				}
			}
			string reason;
			if (want && (activate == null || !EnvironmentApplyReady(key))) return active;
			lock (sync)
			{
				if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
				if (want && !ActionFailureStore.TryBeginApply(action, "system", out reason)) return active;
			}
			bool skipped;
			bool flag2 = want ? RunEnvironmentAction(activate, out skipped, out reason) : RunEnvStepRestore(session, key, restore, out skipped, out reason);
			if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
			if (want && skipped)
			{
				bool skipped2;
				string reason2;
				bool flag3 = RunEnvStepRestore(session, key, restore, out skipped2, out reason2);
				lock (sync)
				{
					if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
					if (flag3)
					{
						envRestorePending.Remove(key);
					}
					else
					{
						envRestorePending.Add(key);
					}
					if (flag3)
					{
						ActionFailureStore.CancelApply(action, "system");
					}
					else
					{
						ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("取消或跳过后的恢复失败：", "Recovery after cancellation or skip failed: ") + reason2);
					}
				}
				return !flag3;
			}
			lock (sync)
			{
				if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
				RecordAutomation(key, (!flag2) ? (want ? "failed" : "restore-failed") : ((!want) ? "restored" : ((key == "services" && OptionalServicePause.PendingStopCount > 0) ? "unverified" : "applied")));
			}
			if (want)
			{
				if (flag2)
				{
					bool flag4;
					lock (sync)
					{
						if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
						flag4 = ActionFailureStore.CompleteApply(action, "system");
					}
					if (!flag4)
					{
						bool skipped3;
						string reason3;
						bool flag5 = RunEnvStepRestore(session, key, restore, out skipped3, out reason3);
						lock (sync)
						{
							if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
							RecordAutomation(key, flag5 ? "restored" : "restore-failed");
							if (flag5)
							{
								envRestorePending.Remove(key);
							}
							else
							{
								envRestorePending.Add(key);
							}
							if (flag5)
							{
								ActionFailureStore.CancelApply(action, "system");
							}
							else
							{
								ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("完成记录失败且回退失败：", "Completion persistence and rollback failed: ") + reason3);
							}
							SyncEnvFailureIsolation(key);
						}
						return !flag5;
					}
					lock (sync)
					{
						if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
						envNextAttempt.Remove(key);
						envFailures.Remove(key);
						SyncEnvFailureIsolation(key);
						if (flag4 && Settings.Load("EnvFuse_" + key, false))
						{
							Settings.Save("EnvFuse_" + key, false);
						}
					}
					return true;
				}
				lock (sync)
				{
					if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
					ActionFailureStore.Trip(action, "system", reason);
					envFused.Add(key);
				}
				Logger.Warn(EnvLabel(key) + PerformancePolicy.Text("：执行失败，本阶段停止应用并核验恢复。", ": apply failed; this phase is stopped and restoration is being verified."));
				bool skipped4;
				string reason4;
				bool flag6 = RunEnvStepRestore(session, key, restore, out skipped4, out reason4);
				lock (sync)
				{
					if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
					RecordAutomation(key, flag6 ? "restored" : "restore-failed");
					if (flag6)
					{
						envRestorePending.Remove(key);
						envNextAttempt.Remove(key);
						envFailures.Remove(key);
					}
					else
					{
						envRestorePending.Add(key);
					}
					if (flag6)
					{
						ActionFailureStore.CancelApply(action, "system");
					}
					else
					{
						ActionFailureStore.Trip(action, "system", reason + PerformancePolicy.Text("；回退失败：", "; rollback failed: ") + reason4);
					}
				}
				return !flag6;
			}
			lock (sync)
			{
				if (session != Interlocked.Read(ref gameSessionStarts)) return originalActive;
				if (flag2)
				{
					ActionFailureStore.CancelApply(action, "system");
					envRestorePending.Remove(key);
					envNextAttempt.Remove(key);
					envFailures.Remove(key);
				}
				else
				{
					envRestorePending.Add(key);
					if (!skipped)
					{
						ActionFailureStore.Trip(action, "system", PerformancePolicy.Text("恢复失败：", "Recovery failed: ") + reason);
					}
				}
			}
			if (!flag2)
			{
				return active;
			}
			return false;
		}
		private static string EnvFailureAction(string key)
		{
			string text = EnvPolicyKey(key);
			if (text != null)
			{
				return text;
			}
			switch (key)
			{
			case "rsr":
				return "GmRsr";
			case "nvvrr":
				return "NvVrrWindowed";
			case "pqos":
				return "PresenceQos";
			default:
				return key;
			}
		}
		private bool SyncEnvFailureIsolation(string key)
		{
			bool flag = ActionFailureStore.IsBlocked(EnvFailureAction(key), "system");
			lock (sync)
			{
				if (flag)
				{
					envFused.Add(key);
				}
				else
				{
					envFused.Remove(key);
				}
			}
			return flag;
		}
		private bool EnvironmentApplyReady(string key)
		{
			switch (key)
			{
			case "maint":
			case "pqos":
			case "do":
			case "wu":
			case "services":
				if (!Native.IsElevated())
				{
					return false;
				}
				break;
			}
			if (key == "do" && Native.IsDomainJoined() && SvcState.Query("DoSvc") == 1 && !DoTweak.HasResidue)
			{
				return false;
			}
			switch (key)
			{
			case "maint":
				return Native.IsElevated();
			case "rsr":
				return AdlxTweaks.RsrSupported();
			case "amdafmf":
				return AdlxTweaks.AfmfSupported();
			default:
				return true;
			}
		}
		private static bool RunEnvironmentAction(Func<bool> action, out bool skipped, out string reason)
		{
			skipped = action == null;
			reason = "";
			if (skipped)
			{
				return false;
			}
			try
			{
				if (action())
				{
					return true;
				}
				reason = PerformancePolicy.Text("执行返回失败", "The action returned failure");
			}
			catch (NotSupportedException)
			{
				skipped = true;
			}
			catch (OperationCanceledException)
			{
				skipped = true;
			}
			catch (Exception ex3)
			{
				reason = ex3.GetType().Name + ": " + ex3.Message;
			}
			return false;
		}
		private void DisableEnvSwitch(string key)
		{
			ActionFailureStore.Trip(EnvFailureAction(key), "system", PerformancePolicy.Text("执行失败，已停止后续应用", "Apply failed; later attempts isolated"));
			lock (sync)
			{
				envFused.Add(key);
			}
		}
		private static string EnvPolicyKey(string key)
		{
			switch (key)
			{
			case "do":
				return "GmPauseDl";
			case "wu":
				return "GmPauseUpdate";
			case "dwmboost":
				return "GmDwmBoost";
			case "gpupower":
				return "GmGpuPowerMax";
			case "services":
				return "GmPauseServices";
			case "standby":
				return "GmStandbyCleanerV2";
			case "awake":
				return "GmAwake";
			case "maint":
				return "GmPauseMaintenanceV1";
			case "intelend":
				return "GmIntelEnduranceV1";
			case "laptopperf":
				return LaptopPerfMode.PreferenceKey;
			case "amdalag":
				return "AmdAntiLag";
			case "amdafmf":
				return "AmdAfmf";
			case "gamedvr":
				return "GameDvrOff";
			case "mmcss":
				return "GmMmcss";
			default:
				return null;
			}
		}
		private void ClearEnvFuse(string key)
		{
			SyncEnvFailureIsolation(key);
		}
		private void ApplyEnv()
		{
			SyncTimerAutomaticCycle();
			long ticks = DateTime.UtcNow.Ticks;
			SyncAutomationPolicy();
			PolicySnapshot policySnapshot = sessionPolicy;
			PolicySnapshot policySnapshot2 = policySnapshot ?? PolicyResolver.Global();
			if (policySnapshot2.GlobalTimerLease && timerMode.GlobalReady)
			{
				RunGuarded(() => timerLease.Update(true, DateTime.UtcNow.Ticks));
			}
			else
			{
				timerLease.Update(false, DateTime.UtcNow.Ticks);
			}
			RecordAutomation("AutoGlobalTimer", AutomaticExecutionState("AutoGlobalTimer"));
			SyncGameDvr();
			PerformancePreset mode = policySnapshot2.Preset;
			bool pauseDownloads = BackgroundSessionPolicy.PauseDownloads(policySnapshot2, enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed, activeDetection);
			bool pauseUpdate = policySnapshot2.PauseUpdate;
			bool pauseMaintenance = policySnapshot2.PauseMaintenance;
			bool awake = BackgroundSessionPolicy.KeepAwake(enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed, activeDetection);
			bool powerPlanOn = policySnapshot2.PowerPlanOn;
			bool flag = false;
			bool dwmBoost = policySnapshot2.DwmBoost;
			bool flag3 = slowEnvAtTicks == 0 || ticks >= slowEnvAtTicks;
			bool want = pauseDownloads && flag3 && Native.IsElevated() && !SessionServiceSuspended("do", ticks)
				&& (doActive || DoTweak.HasResidue || DeliveryOptimizationActivity.IsDownloading());
			bool want2 = pauseUpdate && flag3 && !SessionServiceSuspended("wu", ticks);
			bool want3 = pauseMaintenance && flag3 && !SessionServiceSuspended("maint", ticks);
			bool flag4 = ResolvePowerPlanEnabled(mode, powerPlanOn);
			bool flag6 = flag4;
			SuppressionCore.GpuDemoteEnabled = policySnapshot2.GpuDemote;
			doActive = EnvStep("do", want, doActive, DoTweak.Activate, DoTweak.Restore);
			wuActive = EnvStep("wu", want2, wuActive, UpdatePause.Activate, UpdatePause.Restore);
			maintActive = EnvStep("maint", want3, maintActive, MaintenancePause.Activate, MaintenancePause.Restore);
			RestoreRetiredOptionalServices();
			pqosActive = EnvStep("pqos", true, pqosActive, PresenceQos.Activate, PresenceQos.Restore);
			StepDisplayAwake(awake);
			dwmBoostActive = EnvStep("dwmboost", dwmBoost, dwmBoostActive, DwmBoost.Activate, DwmBoost.Restore);
			rsrActive = EnvStep("rsr", rsrOn, rsrActive, AdlxTweaks.ActivateRsr, AdlxTweaks.RestoreRsr);
			gpwActive = EnvStep("gpupower", false, gpwActive || GpuPowerMax.HasResidue(), null, GpuPowerMax.Restore);
			nvVrrActive = EnvStep("nvvrr", nvVrrWindowedOn && NvApi.Available, nvVrrActive || (!nvVrrWindowedOn && NvVrrWindowed.HasResidue), NvVrrWindowed.Activate, NvVrrWindowed.Restore);
			intelEndActive = EnvStep("intelend", EffIntelEnduranceOff && IntelGraphicsApi.HasAvailable && IntelEndurance.Supported(), intelEndActive, IntelEndurance.Activate, IntelEndurance.Restore);
			amdAlagActive = EnvStep("amdalag", false, amdAlagActive || AdlxTweaks.HasAntiLagResidue(), null, RestoreAmdAntiLagEnv);
			amdAfmfActive = EnvStep("amdafmf", flag && AdlxTweaks.AfmfSupported(), amdAfmfActive, AdlxTweaks.ActivateAfmf, AdlxTweaks.RestoreAfmf);
			StepLaptopPerformancePolicy();
			StepPowerPlanMaintenance();
			bool flag7 = ProcessorTuningRequested(policySnapshot2);
			int num = PowerPolicyKey(flag7, flag6);
			long ticks2 = DateTime.UtcNow.Ticks;
			bool flag8 = RestoreFailedPowerPlan(ticks2);
			if (flag6 && !stopping && flag8)
			{
				if (PowerPlanStopped(num) || !AutomaticWorkAllowed("power-queue") || (planActive && num == lastPowerPolicyKey && ticks2 < Interlocked.Read(ref nextPowerAuditTicks)) || Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) != 0)
				{
					return;
				}
				int num2 = num;
				bool aggrShot = flag7;
				int genShot = Volatile.Read(ref powerSessionGen);
				planActive = true;
				lastPowerPolicyKey = num2;
				Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
				bool flag9 = false;
				try
				{
					flag9 = ThreadPool.QueueUserWorkItem(delegate
					{
						RunPowerPlanApply(genShot, () => PowerPlan.Enforce(aggrShot));
					});
				}
				catch
				{
				}
				if (!flag9)
				{
					RecordAutomaticWorkFailure("power-queue", "queue-rejected");
					planActive = false;
					lastPowerPolicyKey = -1;
					Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
					Interlocked.Exchange(ref powerApplyInFlight, 0);
				}
			}
			else
			{
				if (!planActive || !flag8)
				{
					return;
				}
				Interlocked.Increment(ref powerSessionGen);
				lock (powerApplyGate)
				{
					if (RunAutomaticRestore("powerplan", () => RestoreLaptopPerformanceForPowerPlan() && PowerPlan.Restore()))
					{
						planActive = false;
						lastPowerPolicyKey = -1;
						Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
					}
				}
			}
		}
		private void HandleNvTweakOutcome(List<string> failed, NvGamePlan plan, bool preStage = false)
		{
			if (failed == null || plan == null)
			{
				return;
			}
			if (plan.Automatic)
			{
				if (plan.LowLatMode == "on")
				{
					RecordNvAutomation("NvLowLat", failed.Contains("prerender") ? "failed" : "configured", preStage);
				}
				if (plan.MaxPerf)
				{
					RecordNvAutomation("NvMaxPerf", failed.Contains("pstate") ? "failed" : "configured", preStage);
				}
				if (plan.ShaderCacheMax)
				{
					RecordNvAutomation("NvShaderCache", (failed.Contains("shadercache") || failed.Contains("shaderon")) ? "failed" : "configured", preStage);
				}
			}
			NoteNvKey("pstate", plan.MaxPerf, failed.Contains("pstate"));
			bool wanted = plan.LowLatMode == "on" || plan.LowLatMode == "ultra";
			NoteNvKey("prerender", wanted, (plan.LowLatMode == "ultra") ? NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.UltraKeys) : failed.Contains("prerender"));
			NoteNvKey("smooth", plan.SmoothMotion && NvDrsTweaks.SmoothMotionSupported(), failed.Contains("smooth"));
			NoteNvKey("shadercache", plan.ShaderCacheMax, failed.Contains("shadercache") || failed.Contains("shaderon"));
			NoteNvKey("rebarfeat", plan.Rebar, NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.RebarKeys));
			bool wanted2 = (plan.DlssMode == "latest" || plan.DlssMode == "j" || plan.DlssMode == "k") && NvDrsTweaks.DlssOverrideSupported();
			NoteNvKey("dlssovr", wanted2, NvDrsTweaks.ContainsAny(failed, NvDrsTweaks.DlssKeys));
		}
		private void NoteNvKey(string key, bool wanted, bool didFail)
		{
		}
		private bool EnvActive()
		{
			if (SessionRecoveryPending) return true;
			if (SelfYield.HasResidue) return true;
			if (laboratory != null && !laboratory.IsRestored) return true;
			if (GameNvDeliveryPending)
			{
				return true;
			}
			if (CriticalThreadHasRecoveryDebt(false))
			{
				return true;
			}
			if (FrameCoordination.HasResidue)
			{
				return true;
			}
			if (core.AutomaticLayoutPending)
			{
				return true;
			}
			if (GuardSchedulingNeedsRestore)
			{
				return true;
			}
			lock (sync)
			{
				if (envRestorePending.Count > 0)
				{
					return true;
				}
			}
			if (CoreThreadPlacement.UnownedPending)
			{
				return true;
			}
			if (doActive || wuActive || maintActive || optionalServicesActive || pqosActive || awakeActive || dwmBoostActive || gpwActive || nvVrrActive || intelEndActive || laptopPerfActive || Volatile.Read(ref laptopPerfWorker) != 0 || planActive || timerRaised || rsrActive || amdAlagActive || amdAfmfActive || (standbyCleaner != null && standbyCleaner.HasInFlight))
			{
				return true;
			}
			int mutationGeneration = Settings.MutationGeneration;
			if (mutationGeneration != envResidueGeneration)
			{
				envResidueOther = LaboratoryPower.HasResidue || LaboratoryGpu.HasResidue || DoTweak.HasResidue || UpdatePause.HasResidue || MaintenancePause.HasResidue || OptionalServicePause.HasResidue || PresenceQos.HasResidue || GpuPowerMax.HasResidue() || GpuClockLock.HasResidue || AdlxTweaks.HasResidue() || NvVrrWindowed.HasResidue || IntelEndurance.HasResidue || LaptopPerfMode.HasResidue || PowerPlan.HasResidue || PowerPlan.HasParkResidue() || PowerOverlay.HasResidue || DisplaySolo.HasResidue();
				envResidueNvList = NvDrsTweaks.HasGameResidue;
				envResidueGpuPref = GpuPrefStage.HasResidue;
				envResidueGeneration = mutationGeneration;
			}
			if (!envResidueOther && !NvGameResidueGate(envResidueNvList))
			{
				return GpuPrefResidueGate(envResidueGpuPref);
			}
			return true;
		}
		private bool NvGameResidueGate(bool hasResidue)
		{
			if (hasResidue)
			{
				if (enabled)
				{
					return preStagedNvPath == null;
				}
				return true;
			}
			return false;
		}
		private void PublishLegacyNvPending()
		{
			Interlocked.Exchange(ref gameNvPending, (gameNvWorker || !gameNvRecoveryChecked || !gameNvRootClear) ? 1 : 0);
		}
		private void InitializeGameNvDeliveryMetadata()
		{
			lock (gameNvGate)
			{
				if (!gameNvRecoveryChecked)
				{
					gameNvRootClear = GameNvCanonicalStore.RootConfirmedAbsent(dataDir);
					gameNvRecoveryChecked = true;
					PublishLegacyNvPending();
				}
			}
		}
		private bool StopGameNvDelivery(int timeoutMs)
		{
			if (Monitor.IsEntered(sync))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			lock (gameNvGate)
			{
				gameNvRecoveryStopped = true;
			}
			if (!gameNvIdle.WaitOne(Math.Max(0, timeoutMs)))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			try
			{
				return RestoreLegacyGameNv();
			}
			catch
			{
				lock (gameNvGate)
				{
					gameNvRootClear = false;
					PublishLegacyNvPending();
				}
				return false;
			}
		}
		internal bool SetPerformanceActionSelected(string key, bool value)
		{
			PerformanceAction performanceAction = PerformanceActionCatalog.Find(key);
			if (performanceAction == null || performanceAction.Environment)
			{
				return false;
			}
			if (key == "MatchVisualEffectsV1")
			{
				return SetVisualEffectsReduction(value);
			}
			lock (sync)
			{
				if (stopping || !Settings.Save(key, value))
				{
					return false;
				}
			}
			SyncPerformanceAutomation();
			RequestPolicyApply("SetPerformanceActionSelected");
			return true;
		}
		internal static bool PerformanceActionSelected(string key)
		{
			return PerformanceTuning.ActionSelected(key);
		}
		private void AddGamePerformancePath(HashSet<string> games, string path)
		{
			if (!string.IsNullOrEmpty(path))
			{
				games.Add(path);
			}
		}
		internal string PerformanceExecutionState(string key)
		{
			return performanceActions.State(key);
		}
		private void InitializeStandbyCleaner()
		{
			standbyCleanerOn = Settings.Load("GmStandbyCleanerV2", false);
			string value;
			bool flag = Settings.TryLoadStr("GmStandbyCleanerOptionsV1", out value);
			StandbyCleanerOptions options;
			standbyCleanerOptionsValid = flag && (value.Length == 0 || StandbyCleanerOptions.TryParse(value, out options));
			if (flag && value.Length != 0 && StandbyCleanerOptions.TryParse(value, out options))
			{
				standbyCleanerOptions = options;
			}
			if (!standbyCleanerOptionsValid)
			{
				standbyCleanerOn = false;
				Logger.Log(Lang.T("standbycleaner.config.invalid"));
			}
			standbyCleaner = new StandbyCleanerRunner(new StandbyCleanerEngine(new StandbyMemoryControl()), OnStandbyCleanerFault);
		}
		internal bool TrySetStandbyCleanerEnabled(bool value)
		{
			bool flag;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				InvalidateCacheWarm();
				InvalidateStandbyCleanerWork();
				if (value && !standbyCleanerOptionsValid)
				{
					return false;
				}
				flag = Settings.Save("GmStandbyCleanerV2", value);
				standbyCleanerOn = value && flag;
				if (standbyCleanerOn)
				{
					ClearEnvFuse("standby");
				}
			}
			RequestPolicyApply("TrySetStandbyCleanerEnabled");
			return flag;
		}
		public bool TrySetStandbyCleanerOptions(StandbyCleanerOptions value)
		{
			if (value == null || !value.IsValid)
			{
				return false;
			}
			bool flag;
			lock (sync)
			{
				if (stopping)
				{
					return false;
				}
				InvalidateStandbyCleanerWork();
				if (!standbyCleanerOptionsValid)
				{
					standbyCleanerOn = false;
				}
				flag = Settings.SaveStr("GmStandbyCleanerOptionsV1", value.Serialize());
				if (flag)
				{
					standbyCleanerOptions = value;
					standbyCleanerOptionsValid = true;
				}
			}
			RequestPolicyApply("TrySetStandbyCleanerOptions");
			return flag;
		}
		private void InvalidateStandbyCleanerWork()
		{
			Interlocked.Increment(ref standbyCleanerGeneration);
			StandbyCleanerRunner standbyCleanerRunner = standbyCleaner;
			if (standbyCleanerRunner != null)
			{
				standbyCleanerRunner.Pause();
			}
		}
		private void StandbyCleanerPolicyChanged(bool enabledValue)
		{
			if (enabledValue)
			{
				ClearEnvFuse("standby");
			}
			RequestPolicyApply("StandbyCleanerPolicyChanged");
		}
		private bool LiveStandbyCleanerPreference()
		{
			if (standbyCleanerOn)
			{
				return PerformancePolicy.EnvironmentPreference("GmStandbyCleanerV2");
			}
			return false;
		}
		private Func<bool> CaptureStandbyCleanerAdmission(out StandbyCleanerOptions options, out int generation)
		{
			int capturedGeneration;
			string profileId;
			bool wanted;
			lock (sync)
			{
				generation = (capturedGeneration = Volatile.Read(ref standbyCleanerGeneration));
				options = standbyCleanerOptions;
				if (options != null && options.IsValid)
					ActionFailureStore.BeginAutomaticCycle("GmStandbyCleanerV2", "system", AutomaticPhaseKey(options.Serialize()));
				GameDetection gameDetection = activeDetection;
				profileId = ((gameDetection != null && gameDetection.Profile != null) ? gameDetection.Profile.Id : null);
				PolicySnapshot policySnapshot = sessionPolicy;
				wanted = standbyCleanerOptionsValid && LiveStandbyCleanerPreference() && !ActionFailureStore.IsBlocked("GmStandbyCleanerV2", "system") && !string.IsNullOrEmpty(profileId) && (policySnapshot == null || string.IsNullOrEmpty(policySnapshot.ProfileId) || string.Equals(policySnapshot.ProfileId, profileId, StringComparison.OrdinalIgnoreCase));
			}
			return delegate
			{
				if (!wanted || capturedGeneration != Volatile.Read(ref standbyCleanerGeneration) || !Volatile.Read(ref active) || !enabled || stopping || panicReq || Volatile.Read(ref standbyCleanerRestorePending) != 0 || ProfileStoreSaveFailed || Volatile.Read(ref stickyGraceOnly) || Volatile.Read(ref gameGoneSinceTicks) != 0)
				{
					return false;
				}
				GameDetection gameDetection2 = Volatile.Read(ref activeDetection);
				return gameDetection2 != null && gameDetection2.Profile != null && string.Equals(gameDetection2.Profile.Id, profileId, StringComparison.OrdinalIgnoreCase);
			};
		}
		private void ApplyStandbyCleanerPolicy()
		{
			StandbyCleanerRunner standbyCleanerRunner = standbyCleaner;
			if (standbyCleanerRunner != null)
			{
				StandbyCleanerOptions options;
				int generation;
				Func<bool> func = CaptureStandbyCleanerAdmission(out options, out generation);
				if (func())
				{
					standbyCleanerRunner.Update(generation, options, func);
				}
				else
				{
					standbyCleanerRunner.Pause();
				}
			}
		}
		private void OnStandbyCleanerFault(int generation, StandbyCleanerResult result, int nativeStatus)
		{
			lock (sync)
			{
				if (generation != Volatile.Read(ref standbyCleanerGeneration) || stopping || !active || !enabled || panicReq || ProfileStoreSaveFailed || !envFused.Add("standby"))
				{
					return;
				}
				ActionFailureStore.Trip("GmStandbyCleanerV2", "system", string.Concat(result, " | 0x", nativeStatus.ToString("X8", CultureInfo.InvariantCulture)));
			}
			Logger.Warn(string.Concat(Lang.T("log.gamemodeenv.9"), EnvLabel("standby"), Lang.T("log.gamemodeenv.10"), "1", Lang.T("log.gamemodeenv.11"), " [", result, ", 0x", nativeStatus.ToString("X8", CultureInfo.InvariantCulture), "]"));
			RequestPolicyApply("OnStandbyCleanerFault");
		}
		private bool PerformanceScopeAllows(string imagePath)
		{
			return true;
		}
	}
}
