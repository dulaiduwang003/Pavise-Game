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
		private void MigrateLegacyLowLatency()
		{
			bool value;
			if (!Settings.TryLoadStoredBool("GameLowLatencyOn", out value))
			{
				return;
			}
			if (value && !PolicyResolver.HasExplicitGlobal("NvLowLat"))
			{
				if (!Settings.SaveStr("NvLowLat", "on"))
				{
					return;
				}
				nvLowLatMode = "on";
			}
			Settings.Remove("GameLowLatencyOn");
		}
		internal bool SetGameLowLatency(bool value)
		{
			string val = (value ? "on" : "off");
			lock (sync)
			{
				if (stopping || !Settings.SaveStr("NvLowLat", val))
				{
					return false;
				}
				Settings.Remove("GameLowLatencyOn");
				nvLowLatMode = val;
				SyncAutomationPolicy();
				automaticNvPlanKey = -1;
				ForgetNvTweaksLocked();
			}
			if (!value)
			{
				NvDrsTweaks.RestoreKinds(NvDrsTweaks.UltraKeys);
			}
			else
			{
				NvDrsTweaks.RestoreKinds(new string[2] { "ullenable", "lowlatcpl" });
				SaveCounter("NvFailStreak_prerender", 0);
			}
			RequestPolicyApply("SetGameLowLatency");
			return true;
		}
		private bool ComputeAuditDue(int pid, BoostPass pass, out bool known, out bool needTweak, out bool needPlacement)
		{
			lock (sync)
			{
				known = gameBoost.ContainsKey(pid);
				needTweak = !tweakApplied.Contains(pid);
				ulong value;
				needPlacement = pass.Exclusive && !placementGaveUp.Contains(pid) && (!gamePlacement.TryGetValue(pid, out value) || value != pass.DesiredMask);
				long value2;
				return !known || needTweak || needPlacement || !gameBoostNextAudit.TryGetValue(pid, out value2) || DateTime.UtcNow.Ticks >= value2;
			}
		}
		private void HandlePidReuse(int pid, long currentCreation, ref bool known, ref bool needPlacement)
		{
			if (!known)
			{
				return;
			}
			bool handlePidReuseReady = false;
			Snap value;
			lock (sync)
			{
				if (gameBoost.TryGetValue(pid, out value) && value.Creation > 0 && value.Creation != currentCreation)
				{
					gameBoost.Remove(pid);
					gameGpu.Remove(pid);
					gamePlacement.Remove(pid);
					boostFail.Remove(pid);
					ForgetHardPin(pid);
					boostStateWarned.Remove(pid);
					boostStateVerified.Remove(pid);
					gameBoostNextAudit.Remove(pid);
					boostStateFail.Remove(pid);
					boostHandleStripped.Remove(pid);
					boostEcoGaveUp.Remove(pid);
					placementFail.Remove(pid);
					placementGaveUp.Remove(pid);
					ForgetNvTweakLocked(pid);
					handlePidReuseReady = true;
					known = false;
					ForgetBoostActionTarget(pid);
				}
			}
			if (handlePidReuseReady)
			{
				needPlacement = true;
				CrashGuard.ReleaseBoostProcess(pid, value.Creation);
			}
		}
		private bool CaptureAndTrack(IntPtr h, int pid, long currentCreation, BoostPass pass, bool known, out bool newlyTracked, out bool gpuOk, bool applyGpu = true)
		{
			newlyTracked = false;
			gpuOk = false;
			pass.ActionTarget = BoostActionTarget(h);
			if (known)
			{
				lock (sync)
				{
					if (!string.IsNullOrEmpty(pass.ActionTarget))
					{
						boostActionTargets[pid] = pass.ActionTarget;
					}
				}
			}
			else
			{
				uint priValue = Native.GetPriorityClass(h);
				if (priValue == 0)
				{
					return false;
				}
				ulong affValue = Native.QueryAffinity(h);
				uint[] cpuSetsValue = Native.QueryCpuSets(h);
				if (cpuSetsValue == null)
				{
					Logger.Warn(Lang.T("log.gamemodeboost.9") + pass.RendererName + " pid " + pid);
					return false;
				}
				int ioValue = Native.QueryIoPriority(h);
				int pgValue = Native.QueryPagePriority(h);
				if (ioValue < 0 || pgValue < 0)
				{
					return false;
				}
				int cls;
				bool matchesD3DKMTGetProcessSchedulingPriorityClass = Native.D3DKMTGetProcessSchedulingPriorityClass(h, out cls) == 0;
				if (!matchesD3DKMTGetProcessSchedulingPriorityClass)
				{
					cls = -1;
				}
				int controlMask;
				int stateMask;
				if (!Native.TryQueryPowerThrottling(h, out controlMask, out stateMask))
				{
					if (Native.PowerThrottlingSupported)
					{
						return false;
					}
					controlMask = -1;
					stateMask = -1;
				}
				CrashGuard.OriginalBoostState recovered;
				if (!CrashGuard.MarkBoostProcess(pid, currentCreation, pass.RendererName, priValue, affValue, ioValue, pgValue, cls, cpuSetsValue, controlMask, stateMask, out recovered))
				{
					Logger.Log(Lang.T("log.gamemodeboost.10") + pass.RendererName + " pid " + pid);
					return false;
				}
				if (recovered != null)
				{
					priValue = recovered.Priority;
					affValue = recovered.Affinity;
					ioValue = recovered.Io;
					pgValue = recovered.Page;
					cls = recovered.Gpu;
					matchesD3DKMTGetProcessSchedulingPriorityClass = cls >= 0;
					cpuSetsValue = recovered.CpuSets;
					controlMask = recovered.QoSControl;
					stateMask = recovered.QoSState;
				}
				Snap snap = default(Snap);
				snap.Pri = priValue;
				snap.Aff = affValue;
				snap.Io = ioValue;
				snap.Pg = pgValue;
				snap.Name = pass.RendererName;
				snap.Creation = currentCreation;
				snap.CpuSets = cpuSetsValue;
				snap.QoSControl = controlMask;
				snap.QoSState = stateMask;
				Snap value2 = snap;
				lock (sync)
				{
					gameBoost[pid] = value2;
					if (!string.IsNullOrEmpty(pass.ActionTarget))
					{
						boostActionTargets[pid] = pass.ActionTarget;
					}
					if (matchesD3DKMTGetProcessSchedulingPriorityClass)
					{
						gameGpu[pid] = cls;
					}
				}
				newlyTracked = true;
			}
			uint granted;
			if (Native.HandleWriteAccessStripped(h, out granted))
			{
				bool wasAdded;
				lock (sync)
				{
					wasAdded = boostHandleStripped.Add(pid);
				}
				pass.WriteDenied = true;
				if (wasAdded)
				{
					OnGameHandleStripped(pid, pass.RendererName, granted);
				}
			}
			if (applyGpu)
			{
				gpuOk = ApplySelectedBoostAction(h, pid, pass, "GameGpuPriority", pass.GpuBoost, (long value) => value == 4, () => WriteBoostAction("GameGpuPriority", h, 4L));
			}
			return true;
		}
		private bool ClearEfficiencyMode(IntPtr h, int pid, BoostPass pass)
		{
			if (!Native.PowerThrottlingSupported)
			{
				return true;
			}
			return ApplyIsolatedBoostAction(h, pid, pass, "GameEcoQoS", (long value) => Native.HighQoSMasksOk((int)(value >> 32), (int)value), () => WriteHighQoSBoost(h));
		}
		private void OnGameHandleStripped(int pid, string rendererName, uint granted)
		{
			string describeForLogResult = KernelAntiCheat.DescribeForLog(rendererName);
			Logger.Log(Lang.T("log.gamemodeboost.3") + rendererName + " pid " + pid + Lang.T("log.gamemodeboost.34") + ((describeForLogResult == null) ? Lang.T("nav.tame") : describeForLogResult) + Lang.T("log.gamemodeboost.35") + granted.ToString("X") + Lang.T("log.gamemodeboost.36"));
			ProtectedGameRoster.Remember(rendererName);
		}
		private static string EcoStateText(IntPtr process)
		{
			int controlMask;
			int stateMask;
			if (!Native.TryQueryPowerThrottling(process, out controlMask, out stateMask))
			{
				return Lang.T("t.gamemodeboost.33") + QoSDump(process);
			}
			return (Native.EcoClearedMasksOk(controlMask, stateMask) ? Lang.T("t.gamemodeboost.38") : Lang.T("t.gamemodeboost.33")) + QoSDump(process);
		}
		private static bool StoredRuleAdmits(string storedRaw, CriticalThreadRule bound)
		{
			CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(storedRaw);
			if (criticalThreadRule != null)
			{
				return criticalThreadRule.Admits(bound);
			}
			return false;
		}
		private static void PurgeRetiredIrqObservation(string dir)
		{
			Settings.Remove("IrqSessionProbe");
			Settings.Remove("IrqObserveSkipsV1");
			Settings.Remove("IrqLastCaptureResultV1");
			if (string.IsNullOrEmpty(dir))
			{
				return;
			}
			try
			{
				if (!Directory.Exists(dir))
				{
					return;
				}
				string[] retiredIrqObservationFiles = RetiredIrqObservationFiles;
				foreach (string searchPattern in retiredIrqObservationFiles)
				{
					string[] files = Directory.GetFiles(dir, searchPattern);
					foreach (string path in files)
					{
						try
						{
							File.Delete(path);
						}
						catch
						{
						}
					}
				}
			}
			catch
			{
			}
		}
		private static string StripExe(string s)
		{
			if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
			{
				return s;
			}
			return s.Substring(0, s.Length - 4);
		}
		private void IsolateOptionalServiceAudit(string reason)
		{
			ActionFailureStore.Trip(EnvFailureAction("services"), "system", reason ?? "Service pause verification failed");
			lock (sync)
			{
				envRestorePending.Add("services");
			}
		}
		private Func<bool> CaptureOptionalServicesAdmission()
		{
			int generation = Volatile.Read(ref optionalServiceGeneration);
			return () => generation == Volatile.Read(ref optionalServiceGeneration) && !stopping && !panicReq && enabled && !ProfileStoreSaveFailed && OptionalServicesRequested;
		}
		private bool StepOptionalServices(bool want, bool applied, Func<bool> hasResidue, Func<bool> isApplied, Func<bool> activate, Func<bool> restore)
		{
			lock (sync)
			{
				if (optionalServicesWanted != want)
				{
					optionalServicesWanted = want;
					if (!envRestorePending.Contains("services"))
					{
						envNextAttempt.Remove("services");
						envFailures.Remove("services");
					}
				}
			}
			if (want)
			{
				applied = isApplied();
			}
			else if (hasResidue())
			{
				applied = true;
			}
			bool envStepSucceeded = EnvStep("services", want, applied, activate, restore);
			if (!want)
			{
				return envStepSucceeded;
			}
			if (envStepSucceeded)
			{
				return isApplied();
			}
			return false;
		}
		internal static bool ForegroundConsumesService(string foregroundName, string envKey)
		{
			if (string.IsNullOrEmpty(foregroundName) || string.IsNullOrEmpty(envKey))
			{
				return false;
			}
			string toLowerInvariantResult = WhitelistRule.NormalizeName(foregroundName).ToLowerInvariant();
			string[][] foregroundServiceConsumers = ForegroundServiceConsumers;
			foreach (string[] textEntriesEntry in foregroundServiceConsumers)
			{
				if (textEntriesEntry[0] == toLowerInvariantResult && textEntriesEntry[1] == envKey)
				{
					return true;
				}
			}
			return false;
		}
		private bool SwitchOutDebounceElapsed(long nowTicks)
		{
			if (smartSwitchedOut)
			{
				return sessionForeground.DebounceElapsed(nowTicks, SwitchOutDebounceTicks);
			}
			return false;
		}
		private void SyncFrameCoordination(BoostPass pass)
		{
			bool criticalRolesWantedSucceeded = CriticalRolesWanted(pass.RendererPid, pass.RendererCreation);
			UpdateLaneFallback(pass.RendererPid, pass.RendererCreation, pass.RendererName);
			bool matchesFrameCoordinationEnabled = FrameCoordinationEnabled && !LaneFallbackFor(pass.RendererPid, pass.RendererCreation);
			if (matchesFrameCoordinationEnabled && (!RenderLane.Release() || RenderLane.HasResidue()))
			{
				pass.LaneRestorationReady = false;
				if (criticalRolesWantedSucceeded)
				{
					FrameCoordination.ReleaseBoost(pass.RendererPid, pass.RendererCreation);
				}
				else
				{
					FrameCoordination.ReleaseExcept(0, 0L);
				}
				return;
			}
			int pid = pass.RendererPid;
			long birth = pass.RendererCreation;
			int epoch = Volatile.Read(ref rendererHandoffEpoch);
			Func<bool> func = delegate
			{
				GameDetection gameDetection = Volatile.Read(ref activeDetection);
				return FrameCoordinationEnabled && enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed && EffBoost && !pass.WriteDenied && epoch == Volatile.Read(ref rendererHandoffEpoch) && gameDetection != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameDetection.RendererPid == pid && gameDetection.RendererCreation == birth
					&& ThreadBoostRecoveryAllowed(pid, birth);
			};
			if (matchesFrameCoordinationEnabled && pid > 0 && birth > 0 && func())
			{
				FrameCoordination.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), pid, birth, FrameAutomaticConfiguration());
				FrameCoordination.Ensure(pid, birth, pass.RendererName, func);
			}
			else
			{
				pass.LaneRestorationReady = (criticalRolesWantedSucceeded ? FrameCoordination.ReleaseBoost(pid, birth) : FrameCoordination.ReleaseExcept(0, 0L));
			}
		}
		private void InvalidateOverrideWorkLocked(string key)
		{
			if (key == "GmCacheWarmV2" || key == "GmStandbyCleanerV2")
			{
				InvalidateCacheWarm();
			}
			if (key == "GmStandbyCleanerV2")
			{
				InvalidateStandbyCleanerWork();
			}
			if (key == "GmEnglishInputV1")
			{
				InvalidateEnglishInputWork();
			}
		}
		private void NotifyOverridePolicyChanged(string key, bool effectiveOn)
		{
			if (key == "GmAutoCoresV2" || key == "GmManualCorePlanV2")
			{
				RaiseLibraryChanged();
				return;
			}
			if (key == "GameDisableFullscreenOptimizationsV1")
			{
				InvalidateAutomaticFullscreenOptimizations();
				SyncAutomaticFullscreenOptimizations(true, -1L);
				RaiseLibraryChanged();
				return;
			}
			if (key == "GameNvRebarV1")
			{
				lock (sync)
				{
					if (sessionPolicy != null)
					{
						sessionPolicy = PolicyResolver.For(FindProfileLocked(sessionPolicy.ProfileId));
					}
				}
			}
			switch (key)
			{
			case "GmPauseServices":
				PauseServicesPolicyChanged(effectiveOn);
				break;
			case "GmStandbyCleanerV2":
				StandbyCleanerPolicyChanged(effectiveOn);
				break;
			case "PowerPlanOn":
			case "GmEnglishInputV1":
			case "GmCacheWarmV2":
			case "GameNvRebarV1":
			case "GmAutoCoresV2":
			case "GmManualCorePlanV2":
			case "PerformancePreset":
				RequestPolicyApply("NotifyOverridePolicyChanged");
				break;
			}
		}
		private bool GlobalPolicyOn(string key)
		{
			switch (key)
			{
			case "GmPauseServices":
				return pauseServicesOn;
			case "GmStandbyCleanerV2":
				return standbyCleanerOn;
			case "GmCacheWarmV2":
				return cacheWarmOn;
			default:
				return false;
			}
		}
		internal static WhitelistRuleKind ResolveAutoKind(string executablePath)
		{
			if (!WhitelistRule.IsUnsafeFamilyAnchor(executablePath))
			{
				return WhitelistRuleKind.ApplicationFamily;
			}
			return WhitelistRuleKind.ExactPath;
		}
		private static string DisplayName(string executablePath, string fallback)
		{
			if (!string.IsNullOrWhiteSpace(fallback))
			{
				return fallback.Trim();
			}
			try
			{
				FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executablePath);
				string fileDescription = ((!string.IsNullOrWhiteSpace(versionInfo.FileDescription)) ? versionInfo.FileDescription : versionInfo.ProductName);
				if (!string.IsNullOrWhiteSpace(fileDescription))
				{
					return fileDescription.Trim();
				}
			}
			catch
			{
			}
			return Path.GetFileNameWithoutExtension(executablePath);
		}
		private static string NormalizeGameRoot(string root)
		{
			if (string.IsNullOrWhiteSpace(root))
			{
				return null;
			}
			try
			{
				string getFullPathTrimmed = Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
				return FamilyBoundary.SafeFamilyDir(getFullPathTrimmed) ? getFullPathTrimmed : null;
			}
			catch
			{
				return null;
			}
		}
		private Dictionary<int, PlatformProcessOwner> CollectPlatformMembers(ProcessSnapshot snapshot)
		{
			HashSet<int> hashSet = new HashSet<int>();
			HashSet<string> hashSet2 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			lock (sync)
			{
				if (activeDetection != null && activeDetection.RendererPid > 4)
				{
					hashSet.Add(activeDetection.RendererPid);
				}
				foreach (GameProfile profile in profiles)
				{
					if (!string.IsNullOrEmpty(profile.ExecutablePath))
					{
						hashSet2.Add(profile.ExecutablePath);
					}
					if (!string.IsNullOrEmpty(profile.LearnedExecutablePath))
					{
						hashSet2.Add(profile.LearnedExecutablePath);
					}
				}
			}
			if (snapshot != null)
			{
				ProcEntry[] entries = snapshot.Entries;
				foreach (ProcEntry procEntry in entries)
				{
					if (procEntry != null && (hashSet2.Contains(procEntry.Path) || (PlatformRuntimePolicy.RuntimePlatform(procEntry.Name) == null && FamilyBoundary.CatalogProtected(procEntry.Pid, procEntry.Creation, procEntry.Name, procEntry.Path))))
					{
						hashSet.Add(procEntry.Pid);
					}
				}
			}
			platformMembers = PlatformRuntimePolicy.Collect(snapshot, selfPid, selfSession, hashSet, platformMembers);
			return platformMembers;
		}
		private static bool PlatformRuleEnabled(PlatformProcessOwner owner)
		{
			if (owner.Platform == "wegame")
			{
				return PlatformRuntimePolicy.Enabled(owner.Platform);
			}
			return false;
		}
		private void AppendPlatformRuleViews(List<WhitelistRuleView> views, Dictionary<int, PlatformProcessOwner> members)
		{
			string[] ids = PlatformRuntimePolicy.Ids;
			foreach (string entryText in ids)
			{
				if (!PlatformRuntimePolicy.Enabled(entryText))
				{
					continue;
				}
				int processedCount = ((members == null) ? (-1) : 0);
				if (members != null)
				{
					foreach (PlatformProcessOwner value in members.Values)
					{
						if (value.Platform == entryText)
						{
							processedCount++;
						}
					}
				}
				WhitelistRule rule;
				if (WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, entryText, out rule))
				{
					views.Add(new WhitelistRuleView(rule, processedCount, false, entryText));
				}
			}
		}
		private bool RemovePlatformProtection(string id)
		{
			lock (whiteEvalSync)
			{
				lock (sync)
				{
					if (stopping)
					{
						return false;
					}
					if (!PlatformRuntimePolicy.TryRemove(id))
					{
						whitelistLastError = Lang.T("white.save.failed");
						return false;
					}
					whiteRevision++;
					whitelistLastError = "";
				}
			}
			RequestPolicyApply("RemovePlatformProtection");
			return true;
		}
		private void UpdateArmedStatus(string name, string via, bool engaged)
		{
			armedGameName = name;
			if (!string.Equals(name, lastArmedLogged, StringComparison.OrdinalIgnoreCase))
			{
				if (name != null)
				{
					Logger.Log(Lang.T("log.gamemode.54") + name + Lang.T("log.gamemode.55") + (string.IsNullOrEmpty(via) ? "" : (Lang.T("log.gamemode.62") + via)));
				}
				else if (lastArmedLogged != null && !engaged)
				{
					Logger.Log(Lang.T("log.gamemode.56") + lastArmedLogged + Lang.T("log.gamemode.57"));
					ReleasePreStagedTuning();
				}
				lastArmedLogged = name;
			}
		}
		public bool NeedsLauncherChildParentIdentity(int eventParentPid, string eventName, int session)
		{
			if (session != selfSession || eventParentPid <= 0)
			{
				return false;
			}
			lock (sync)
			{
				return ShouldCaptureLauncherParentIdentity(activeDetection, eventParentPid);
			}
		}
		internal static bool ShouldCaptureLauncherParentIdentity(GameDetection detection, int eventParentPid)
		{
			if (detection != null && eventParentPid > 0 && detection.RendererPid == eventParentPid)
			{
				return detection.RendererCreation > 0;
			}
			return false;
		}
		private static long ElapsedMsOrMax(long last, long elapsedTicks)
		{
			if (last <= 0 || elapsedTicks < 0)
			{
				return long.MaxValue;
			}
			return elapsedTicks / 10000;
		}
		private static int TicksToWaitMilliseconds(long ticks)
		{
			if (ticks <= 0)
			{
				return 1;
			}
			long val = (ticks + 10000 - 1) / 10000;
			return (int)Math.Min(2147483647L, Math.Max(1L, val));
		}
		internal static string SeverityTagFor(string reason)
		{
			switch (reason)
			{
			case "Error":
			case "QueueFailed":
			case "SaveFailed":
				return "FAIL ";
			case "GpuUnavailable":
			case "FileUnavailable":
			case "InvalidEvidence":
			case "FileChanged":
				return "WARN ";
			default:
				return "INFO ";
			}
		}
		private void PrepareResourcePriorities(BoostPass pass, PolicySnapshot policy)
		{
			policy = policy ?? PolicyResolver.Global();
			pass.CpuBoost = pass.MemoryBoost = pass.IoBoost = pass.GpuBoost = policy.BoostGame;
			int combinedMask = (pass.CpuBoost ? 1 : 0) | (pass.MemoryBoost ? 2 : 0) | (pass.IoBoost ? 4 : 0) | (pass.GpuBoost ? 8 : 0);
			lock (sync)
			{
				if (combinedMask != boostResourceSelection)
				{
					boostResourceSelection = combinedMask;
					gameBoostNextAudit.Clear();
					boostStateVerified.Clear();
				}
			}
		}
		private void RecordRuntimeProtection(ProcEntry process, BackgroundReleaseState backgroundState)
		{
			string source = "platform-runtime-" + process.Pid + "-" + process.Creation;
			if (!RuntimeBackgroundExempt(backgroundState))
			{
				string getFileNameOffset = "pid=" + process.Pid + " born=" + process.Creation + " exe=" + Path.GetFileName(process.Path ?? "");
				string backgroundStateOffset = " background=" + backgroundState;
				Logger.RepeatedError(source, "AUTO [" + AutomationSession + "] [PLATFORM] restore-pending " + getFileNameOffset + backgroundStateOffset, -1L);
			}
			else
			{
				Logger.RepeatedRecovered(source);
			}
		}
		private static string FmtDur(TimeSpan t)
		{
			if (t.TotalHours >= 1.0)
			{
				return (int)t.TotalHours + "h" + t.Minutes.ToString("00") + "m";
			}
			if (t.TotalMinutes >= 1.0)
			{
				return t.Minutes + "m" + t.Seconds.ToString("00") + "s";
			}
			return t.Seconds + "s";
		}
		private static string FmtCpu(long ticks)
		{
			TimeSpan timeSpan = TimeSpan.FromTicks(ticks);
			if (timeSpan.TotalSeconds < 1.0)
			{
				return "<1s";
			}
			if (timeSpan.TotalMinutes >= 1.0)
			{
				return (int)timeSpan.TotalMinutes + "m" + timeSpan.Seconds.ToString("00") + "s";
			}
			return timeSpan.TotalSeconds.ToString("0.0") + "s";
		}
		private void PauseServicesPolicyChanged(bool enabledValue)
		{
			Interlocked.Increment(ref optionalServiceGeneration);
			if (!enabledValue)
			{
				lock (sync)
				{
					envNextAttempt.Remove("services");
					envFailures.Remove("services");
				}
			}
			else
			{
				ClearEnvFuse("services");
			}
			RequestPolicyApply("PauseServicesPolicyChanged");
		}
		private GameDetection ApplyStickiness(GameDetection hit)
		{
			if (hit != null)
			{
				stickyGraceOnly = false;
				stickyMiss = 0;
				if (stickyDetection != null && stickyDetection.Profile != null && hit.Profile != null && string.Equals(stickyDetection.Profile.Id, hit.Profile.Id, StringComparison.OrdinalIgnoreCase) && stickyDetection.RendererPid > 0 && AliveWithIdentity(stickyDetection.RendererPid))
				{
					bool applyStickinessReady = false;
					GameId value;
					string name;
					long creation;
					if (hit.RendererPid > 0 && hit.RendererPid != stickyDetection.RendererPid && stickyIds.TryGetValue(stickyDetection.RendererPid, out value) && TryStickyIdentity(hit.RendererPid, out name, out creation) && FreshRendererMayReplaceSticky(hit, name, creation, value.Creation, RendererForegroundHeldMs(hit.RendererPid)))
					{
						applyStickinessReady = true;
					}
					if (hit.RendererPid != stickyDetection.RendererPid && !applyStickinessReady)
					{
						List<int> verifiedStickyPids = LiveStickyFamily();
						if (!ReanchorToStickyInstance(hit, stickyDetection, verifiedStickyPids))
						{
							ClearSticky();
						}
					}
				}
				if (!RememberSticky(hit))
				{
					return ApplyStickiness(null);
				}
				{
					foreach (int item in LiveStickyFamily())
					{
						hit.FamilyPids.Add(item);
					}
					return hit;
				}
			}
			if (stickyDetection != null && stickyDetection.RendererPid > 0 && AliveWithIdentity(stickyDetection.RendererPid))
			{
				stickyGraceOnly = false;
				stickyMiss = 0;
				GameDetection gameDetection = new GameDetection();
				gameDetection.Profile = stickyDetection.Profile;
				gameDetection.RendererPid = stickyDetection.RendererPid;
				gameDetection.RendererCreation = stickyDetection.RendererCreation;
				gameDetection.RendererName = stickyDetection.RendererName;
				gameDetection.RendererPath = stickyDetection.RendererPath;
				gameDetection.RendererForeground = stickyDetection.RendererForeground;
				gameDetection.RendererCandidateSelected = true;
				gameDetection.RendererUserSelected = stickyDetection.RendererUserSelected;
				gameDetection.RendererLearnable = stickyDetection.RendererLearnable;
				gameDetection.Evidence = stickyDetection.Evidence;
				GameDetection gameDetection2 = gameDetection;
				gameDetection2.FamilyPids.Add(stickyDetection.RendererPid);
				foreach (int item2 in LiveStickyFamily())
				{
					gameDetection2.FamilyPids.Add(item2);
				}
				foreach (string familyName in stickyDetection.FamilyNames)
				{
					gameDetection2.FamilyNames.Add(familyName);
				}
				stickyDetection = gameDetection2;
				return gameDetection2;
			}
			if (stickyDetection != null && stickyMiss < 1 && !AnyStickyReused())
			{
				stickyMiss++;
				stickyGraceOnly = true;
				RequestFullGameDetection();
				try
				{
					kick.Set();
				}
				catch
				{
				}
				return CloneWithAnchoredFamily(stickyDetection);
			}
			ClearSticky();
			return null;
		}
		private bool AliveWithIdentity(int pid)
		{
			GameId value;
			if (!stickyIds.TryGetValue(pid, out value))
			{
				return false;
			}
			string name;
			long creation;
			if (TryStickyIdentity(pid, out name, out creation) && creation == value.Creation)
			{
				return string.Equals(name, value.Name, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		private static bool TryIdentity(int pid, out string name, out long creation)
		{
			name = null;
			creation = 0L;
			if (pid <= 0)
			{
				return false;
			}
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				name = Native.ImageName(intPtr);
				long cpu;
				ulong io;
				return Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && !string.IsNullOrEmpty(name);
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}
		internal static bool IsHandheld(PerformancePreset mode)
		{
			return false;
		}
		private string ServiceImagePath(ProcEntry p, PerformancePreset mode)
		{
			if (p.Session != 0 || !BackgroundServicePolicy.SupportedMode(mode) || !string.IsNullOrEmpty(p.Path))
			{
				return p.Path;
			}
			if (serviceImagePaths == null)
			{
				serviceImagePaths = new Dictionary<int, KeyValuePair<long, string>>();
			}
			KeyValuePair<long, string> value;
			if (serviceImagePaths.TryGetValue(p.Pid, out value) && value.Key == p.Creation)
			{
				return value.Value;
			}
			string imagePathResult = null;
			IntPtr intPtr = Native.OpenProcess(4096, false, p.Pid);
			if (intPtr != IntPtr.Zero)
			{
				try
				{
					long creation;
					long cpu;
					ulong io;
					if (Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation == p.Creation && !Native.IsProtectedProcess(intPtr))
					{
						imagePathResult = Native.ImagePath(intPtr);
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			if (serviceImagePaths.Count > 1024)
			{
				serviceImagePaths.Clear();
			}
			serviceImagePaths[p.Pid] = new KeyValuePair<long, string>(p.Creation, imagePathResult);
			return imagePathResult;
		}
		private bool TryProtectOverlayHost(int pid, long creation, string name, string imagePath, bool familyExempt)
		{
			if (pid <= 4 || !OverlayHostCatalog.ShouldProtectProcess(name, imagePath, familyExempt))
			{
				return false;
			}
			if (creation > 0 && core.ReleaseIfCreation(pid, SuppressReason.Background, creation))
			{
				ReportUntrack(pid);
			}
			return true;
		}
		internal static bool ServiceRuleMatches(IList<WhitelistRule> rules, string name, string imagePath)
		{
			if (rules == null)
			{
				return false;
			}
			string normalizedName = WhitelistRule.NormalizeName(name);
			string normalizedImagePath = WhitelistRule.NormalizeImagePath(imagePath);
			foreach (WhitelistRule rule in rules)
			{
				if (rule.MatchesNormalized(normalizedName, normalizedImagePath) && (rule.Kind != 0 || !IsPresetWhitelistName(rule.Value) || IsTrustedPresetProcessPath(rule.Value, imagePath)))
				{
					return true;
				}
			}
			return false;
		}
	}
}
