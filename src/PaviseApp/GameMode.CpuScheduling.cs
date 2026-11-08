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
		internal AntiCheatAffinityGameContext GetAntiCheatAffinityContext()
		{
			lock (sync)
			{
				GameDetection detection = activeDetection;
				if (!active || !enabled || stopping || panicReq || ProfileStoreSaveFailed || gameGoneSinceTicks != 0
					|| detection == null || !detection.RendererCandidateSelected || detection.RendererSafetyOnly
					|| detection.RequiresGpuConfirm || detection.RendererPid <= 0 || detection.RendererCreation <= 0)
				{
					AntiCheatContextStable(null, 0);
					return null;
				}
				if (!AntiCheatContextStable(detection, AutoCores.NowTicks)) return null;
				ulong reserved = 0, domain = 0;
				if (!CpuTopology.MultiGroup)
				{
					if (corePlan != null) reserved |= corePlan.Allocated;
					HardPin pin;
					if (coreEngaged && hardPins.TryGetValue(detection.RendererPid, out pin) && pin.Creation == detection.RendererCreation)
					{
						if (pin.AppliedAffinity != allMask) domain = pin.AppliedAffinity;
						if (coreManual != null && coreManual.Isolation && CoreIsolationClient.State == "schedule.isolation.active") reserved |= pin.AppliedAffinity;
					}
					CriticalThreadUiState threads = criticalThreadUi;
					if (threads.TargetPid == detection.RendererPid && threads.TargetBirth == detection.RendererCreation && (threads.Applied || threads.Pending))
						foreach (ulong mask in threads.CandidateMasks) reserved |= mask;
				}
				HashSet<int> ancestors = VerifiedAntiCheatAncestors(antiCheatProcessSnapshot, detection);
				if (ancestors == null) return null;
				return new AntiCheatAffinityGameContext(detection.RendererPid, detection.RendererCreation, gameSessionStarts,
					CpuTopology.MultiGroup ? -1 : 0, reserved, domain, ancestors);
			}
		}
		private AutoCoreGovernor NewCoreGovernor()
		{
			if (corePlan == null || !corePlan.Available)
			{
				return null;
			}
			return new AutoCoreGovernor(corePlan.Cores, corePlan.Logical);
		}
		private void ResetCoreWatch()
		{
			ResetCoreIntelligence();
			coreWatchPid = 0;
			coreWatchCreation = 0L;
			coreEngaged = false;
			coreAffinityObserved = 0uL;
			coreAffinityRead = (coreAffinityVerified = false);
			exclusiveCheckTicks = 0L;
			exclusiveAuditRetry.Reset();
			exclusiveAuditDeferred = false;
			coreGameMask = ((coreManual != null) ? coreManual.GameMask : ((corePlan != null) ? corePlan.Exclusive : 0));
			corePhysical = CpuTopology.PhysicalCoreMasks();
			coreBackgroundMoved = 0;
			corePlacementEvidence = null;
		}
		private void BeginAutoCores(PolicySnapshot snap, bool isolationClean)
		{
			bool restoreAllHardPinsSucceeded = RestoreAllHardPins();
			corePinPending = !restoreAllHardPinsSucceeded;
			ResetCoreWatch();
			coreManual = ((coreSessionPolicy != null) ? coreSessionPolicy.ManualCores : ((snap != null) ? snap.ManualCores : null));
			corePlan = ((coreManual != null) ? coreManual.RuntimePlan() : new AutoCorePlan
			{
				Blocked = "schedule.manual.required"
			});
			corePreset = ((snap != null) ? snap.Preset : PerformancePreset.Standard);
			coreAllowed = snap != null && snap.AutoCoresAllowed;
			corePolicyCheckTicks = AutoCores.NowTicks;
			coreObservationOnly = coreManual == null || snap == null || !snap.AutoCoresAllowed || !PresetValue.SupportsCoreScheduling(snap.Preset);
			coreGovernor = NewCoreGovernor();
			coreGameMask = ((coreManual != null) ? coreManual.GameMask : corePlan.Exclusive);
			if (coreGovernor != null && (!isolationClean || !restoreAllHardPinsSucceeded))
			{
				coreGovernor.Block("schedule.auto.pending");
			}
			if (corePlan.Available)
			{
				Logger.Log(Lang.F("schedule.manual.plan", CpuTopology.DescribeMask(coreManual.GameMask)));
			}
			else
			{
				Logger.Log(Lang.F("log.autocore.unavailable", Lang.T(corePlan.Blocked)));
			}
			if (corePlan.Allocated != 0)
			{
				Logger.Log(Lang.F("log.autocore.allocated", CpuTopology.DescribeMask(corePlan.Allocated)));
			}
			PublishAutoCores(null);
		}
		private void EndAutoCores()
		{
			ResetCoreIntelligence();
			coreGovernor = null;
			corePlan = null;
			coreManual = null;
			corePlacementStopReason = null;
			corePlacementOnly = false;
			ResetCoreWatch();
			lock (sync)
			{
				corePinPending = hardPins.Count > 0;
			}
			AutoCoreStatus autoCoreStatus = new AutoCoreStatus();
			autoCoreStatus.Pending = CorePending;
			AutoCores.Publish(autoCoreStatus);
		}
		private void RefreshAutoCoreMode()
		{
			if (stopping || (Interlocked.Exchange(ref corePolicyDirty, 0) == 0 && AutoCores.NowTicks - corePolicyCheckTicks < 50000000))
			{
				return;
			}
			corePolicyCheckTicks = AutoCores.NowTicks;
			GameProfile gameProfile = null;
			lock (sync)
			{
				if (activeDetection != null && activeDetection.Profile != null)
				{
					foreach (GameProfile profile in profiles)
					{
						if (profile.Id == activeDetection.Profile.Id)
						{
							gameProfile = profile.Clone();
							break;
						}
					}
				}
			}
			PolicySnapshot policySnapshot = coreSessionPolicy ?? ((gameProfile == null) ? PolicyResolver.Global() : PolicyResolver.For(gameProfile));
			if (policySnapshot.AutoCoresAllowed && coreAllowed && PresetValue.SupportsCoreScheduling(policySnapshot.Preset) && PresetValue.SupportsCoreScheduling(corePreset))
			{
				if (corePreset != policySnapshot.Preset)
				{
					StopCoreRefinement();
				}
				corePreset = policySnapshot.Preset;
			}
			else if (policySnapshot.Preset != corePreset || policySnapshot.AutoCoresAllowed != coreAllowed)
			{
				BeginAutoCores(policySnapshot, StopCoreIsolation());
			}
		}
		private void StepAutoCores(BoostPass pass)
		{
			long nowTicks = AutoCores.NowTicks;
			try
			{
				StepAutoCoresImpl(pass);
			}
			finally
			{
				coreBudget.Record(nowTicks);
			}
		}
		private void StepAutoCoresImpl(BoostPass pass)
		{
			RefreshAutoCoreMode();
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			if (coreManual == null)
			{
				PublishAutoCores(pass.RendererName);
				return;
			}
			if (corePlacementStopReason != null)
			{
				if (CorePending)
				{
					ReleaseExclusive();
				}
				if (autoCoreGovernor != null)
				{
					autoCoreGovernor.Block(corePlacementStopReason);
				}
				PublishAutoCores(pass.RendererName);
				return;
			}
			if (autoCoreGovernor == null || pass.RendererPid <= 0 || pass.RendererCreation <= 0)
			{
				if (autoCoreGovernor != null)
				{
					ApplyAutoCoreStep(autoCoreGovernor.Pause());
				}
				PublishAutoCores(pass.RendererName);
				return;
			}
			if (pass.RendererPid != coreWatchPid || pass.RendererCreation != coreWatchCreation)
			{
				if (coreWatchPid > 0)
				{
					bool releaseExclusiveSucceeded = ReleaseExclusive();
					autoCoreGovernor = (coreGovernor = NewCoreGovernor());
					if (!releaseExclusiveSucceeded)
					{
						autoCoreGovernor.Block("schedule.auto.pending");
					}
				}
				ResetCoreWatch();
				coreWatchPid = pass.RendererPid;
				coreWatchCreation = pass.RendererCreation;
			}
			bool isInPlacementGaveUp;
			lock (sync)
			{
				isInPlacementGaveUp = placementGaveUp.Contains(pass.RendererPid);
			}
			if (isInPlacementGaveUp && !coreEngaged)
			{
				BlockAutoCores("schedule.auto.placementunavailable");
			}
			else if (coreEngaged && coreManual.Isolation && CoreIsolationClient.State != "schedule.isolation.active")
			{
				BlockAutoCores("schedule.auto.failed");
			}
			if (coreObservationOnly)
			{
				PublishAutoCores(pass.RendererName);
			}
			else if (RendererForegroundPid() != pass.RendererPid)
			{
				if (!coreEngaged)
				{
					ApplyAutoCoreStep(autoCoreGovernor.Pause());
				}
				PublishAutoCores(pass.RendererName);
			}
			else
			{
				autoCoreGovernor.EngageManual();
				PublishAutoCores(pass.RendererName);
			}
		}
		private void SyncCoreBackground(HashSet<int> protectedPids, int foreground)
		{
			long nowTicks = AutoCores.NowTicks;
			try
			{
				SyncCoreBackgroundImpl(protectedPids, foreground);
			}
			finally
			{
				coreBudget.Record(nowTicks);
			}
		}
		private ulong CoreBackgroundTarget()
		{
			if (coreManual == null || stopping || !coreEngaged || coreObservationOnly || corePlan == null || !corePlan.Available || CorePending)
			{
				return 0uL;
			}
			if (!coreManual.Isolation || CoreIsolationClient.State != "schedule.isolation.active" || CoreIsolationClient.ActiveMask != corePlan.Exclusive)
			{
				return 0uL;
			}
			ulong combinedMask = allMask & ~corePlan.Exclusive & ~AutoCores.WholeCores(corePlan.Allocated, corePhysical);
			if (AutoCores.CountBits(combinedMask) < 2)
			{
				return 0uL;
			}
			return combinedMask;
		}
		private void SyncCoreBackgroundImpl(HashSet<int> protectedPids, int foreground)
		{
			core.RetryRetiredAutomaticLayouts();
			if (core.BackgroundAffinityPending)
			{
				ClearBackgroundSqueezes();
				return;
			}
			ulong coreBackgroundTargetResult = CoreBackgroundTarget();
			if (coreBackgroundTargetResult == 0)
			{
				ClearBackgroundSqueezes();
				return;
			}
			core.EnforceBackgroundAffinity = true;
			foreach (int item in core.PidsWith(SuppressReason.Background))
			{
				long creationOfResult = core.CreationOf(item);
				string nameOfResult = core.NameOf(item);
				if (creationOfResult > 0 && !string.IsNullOrEmpty(nameOfResult))
				{
					bool changed;
					if (IsCriticalThreadGuardian(item, creationOfResult))
					{
						ReleaseCriticalGuardianBackground();
					}
					else if (GameAffinityJobLease.IsGuardian(item, creationOfResult))
					{
						ReleaseJobGuardianBackground(item, creationOfResult, nameOfResult);
					}
					else if (item == foreground || item == coreWatchPid || protectedPids.Contains(item) || WeGameRuntimeProtection.IsProtectedRuntimeName(nameOfResult) || PeripheralCatalog.IsInputAudioLike(nameOfResult))
					{
						core.SetSqueeze(item, creationOfResult, nameOfResult, 0uL, SuppressReason.Background, out changed);
					}
					else if (!core.IsSqueezed(item))
					{
						core.SetSqueeze(item, creationOfResult, nameOfResult, coreBackgroundTargetResult, SuppressReason.Background, out changed);
					}
				}
			}
			coreBackgroundMoved = core.SqueezedCount(SuppressReason.Background);
		}
		private void ApplyAutoCoreStep(AutoCoreStep step)
		{
			if (step == AutoCoreStep.Release)
			{
				ReleaseExclusive();
			}
		}
		private void BlockAutoCores(string reason)
		{
			if (coreManual != null)
			{
				corePlacementStopReason = reason;
			}
			coreAffinityVerified = false;
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			if (autoCoreGovernor != null)
			{
				bool matchesBlocked = autoCoreGovernor.Blocked == null;
				ApplyAutoCoreStep(autoCoreGovernor.Block(reason));
				if (matchesBlocked)
				{
					Logger.Log(Lang.F("log.autocore.blocked", Lang.T(reason)));
				}
			}
		}
		private bool ReleaseExclusive()
		{
			bool coreEngagedSnapshot = coreEngaged;
			exclusiveAuditRetry.Reset();
			exclusiveAuditDeferred = false;
			coreAffinityVerified = false;
			ClearBackgroundSqueezes();
			coreEngaged = false;
			bool stopCoreIsolationSucceeded = StopCoreIsolation();
			bool restoreAllHardPinsSucceeded = RestoreAllHardPins();
			corePinPending = !restoreAllHardPinsSucceeded;
			if ((!stopCoreIsolationSucceeded || !restoreAllHardPinsSucceeded) && coreGovernor != null && coreGovernor.Blocked == null)
			{
				coreGovernor.Block("schedule.auto.pending");
			}
			if (coreEngagedSnapshot)
			{
				Logger.Log((stopCoreIsolationSucceeded && restoreAllHardPinsSucceeded) ? Lang.T("log.autocore.released") : Lang.T("log.autocore.releasepending"));
			}
			if (stopCoreIsolationSucceeded)
			{
				return restoreAllHardPinsSucceeded;
			}
			return false;
		}
		private bool ExclusiveDrifted(int pid, BoostPass pass, out ulong observed, out bool unreadable)
		{
			observed = 0uL;
			unreadable = false;
			if (!pass.Exclusive || !coreEngaged || CpuTopology.MultiGroup || stopping)
			{
				return false;
			}
			long nowTicks = AutoCores.NowTicks;
			if (nowTicks - exclusiveCheckTicks < 10000000)
			{
				return false;
			}
			exclusiveCheckTicks = nowTicks;
			exclusiveAuditDeferred = false;
			lock (sync)
			{
				if (placementGaveUp.Contains(pid))
				{
					return false;
				}
				HardPin value = null;
				int exclusiveDriftedValue;
				switch ((gamePlacement.ContainsKey(pid) && hardPins.TryGetValue(pid, out value) && value.Pid == pid && value.Creation == pass.RendererCreation) ? HardPinHandleStateOf(value) : HardPinHandleState.Unknown)
				{
				case HardPinHandleState.Gone:
					return false;
				case HardPinHandleState.Match:
					string jobReason;
					if (value.JobBound && !coreAffinityJobs.Verify(pid, value.Creation, pass.DesiredMask, out jobReason))
					{
						corePlacementEvidence = "pid=" + pid + " stage=job-audit reason=" + jobReason;
						exclusiveDriftedValue = 0;
						break;
					}
					exclusiveDriftedValue = (ReadCoreAffinity(value.RestoreHandle, out observed) ? 1 : 0);
					if (exclusiveDriftedValue == 0)
					{
						int error = Marshal.GetLastWin32Error();
						exclusiveAuditDeferred = DeferExclusiveAuditFailure(pass, "audit-read", error);
					}
					break;
				default:
					corePlacementEvidence = "pid=" + pid + " creation=" + pass.RendererCreation + " stage=audit-identity-or-ownership-unavailable";
					exclusiveDriftedValue = 0;
					break;
				}
				bool matchesExclusiveDriftedValue = (byte)exclusiveDriftedValue != 0;
				RecordCoreAffinity(pass.DesiredMask, observed, matchesExclusiveDriftedValue);
				if (coreAffinityVerified) exclusiveAuditRetry.Reset();
				unreadable = !matchesExclusiveDriftedValue;
				return !matchesExclusiveDriftedValue || !coreAffinityVerified;
			}
		}
		private bool ReadCoreAffinity(IntPtr handle, out ulong observed)
		{
			return Native.TryQueryAffinity(handle, out observed);
		}
		private bool DeferExclusiveAuditFailure(BoostPass pass, string stage, int error)
		{
			coreAffinityVerified = false;
			bool retry = exclusiveAuditRetry.ShouldRetry(error, AutoCores.NowTicks);
			corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation
				+ " stage=" + stage + " win32=" + error + " target=0x" + pass.DesiredMask.ToString("X")
				+ " attempt=" + exclusiveAuditRetry.Failures + "/3 action=" + (retry ? "retry" : "stop");
			Logger.Warn("Game affinity maintenance " + corePlacementEvidence);
			return retry;
		}
		private void RecordCoreAffinity(ulong expected, ulong observed, bool readable)
		{
			coreAffinityObserved = (readable ? observed : 0);
			coreAffinityRead = readable;
			coreAffinityVerified = readable && ManualAffinityMatches(observed, expected);
		}
		internal static bool ManualAffinityMatches(ulong observed, ulong expected)
		{
			if (expected != 0)
			{
				return observed == expected;
			}
			return false;
		}
		private bool PlacementChangedOutside(IntPtr h, Snap original, out ulong observed, out bool unreadable)
		{
			observed = 0uL;
			unreadable = false;
			if (CpuTopology.MultiGroup)
			{
				return false;
			}
			ulong aff = ((original.Aff != 0) ? original.Aff : allMask);
			if (!Native.TryQueryAffinity(h, out observed))
			{
				unreadable = true;
				return false;
			}
			if (observed != aff)
			{
				return true;
			}
			if (coreManual != null)
			{
				return false;
			}
			uint[] cpuSets = Native.QueryCpuSets(h);
			if (cpuSets == null)
			{
				unreadable = true;
				return false;
			}
			return !CoreThreadPlacement.Same(cpuSets, original.CpuSets ?? new uint[0]);
		}
		private bool HoldExclusive(int pid, BoostPass pass, ulong observed)
		{
			switch (WriteBackExclusive(pid, pass, true))
			{
			case ExclusiveWriteBack.Busy:
			case ExclusiveWriteBack.Deferred:
				return false;
			case ExclusiveWriteBack.Failed:
				YieldExclusive(pid, observed);
				return false;
			default:
			{
				int exclusiveWriteBacksSnapshot = ++exclusiveWriteBacks;
				if (exclusiveWriteBacksSnapshot <= 3 || exclusiveWriteBacksSnapshot % 20 == 0)
				{
					Logger.Log(PerformancePolicy.Text("其他程序改动了游戏亲和性 读回 0x", "Another program changed the game's affinity (read back 0x") + observed.ToString("X") + PerformancePolicy.Text(" 已改回 0x", "); written back to 0x") + pass.DesiredMask.ToString("X") + PerformancePolicy.Text(" 本局第 ", " this session: ") + exclusiveWriteBacksSnapshot + PerformancePolicy.Text(" 次", ""));
				}
				return true;
			}
			}
		}
		private ExclusiveWriteBack WriteBackExclusive(int pid, BoostPass pass, bool retryMaintenance = false)
		{
			if (stopping || pass == null || !pass.Exclusive || CpuTopology.MultiGroup || pass.DesiredMask == 0)
			{
				return ExclusiveWriteBack.Failed;
			}
			if (!Monitor.TryEnter(criticalProcessGate, 0))
			{
				return ExclusiveWriteBack.Busy;
			}
			try
			{
				lock (sync)
				{
					HardPin value;
					if (CorePending || placementGaveUp.Contains(pid) || !hardPins.TryGetValue(pid, out value)
						|| value.RestoreRequested || value.RestoreInFlight || value.Creation != pass.RendererCreation
						|| value.AppliedAffinity != pass.DesiredMask || HardPinHandleStateOf(value) != 0)
					{
						return ExclusiveWriteBack.Failed;
					}
					if (value.JobBound)
					{
						string jobReason;
						bool verified = coreAffinityJobs.Verify(pid, value.Creation, pass.DesiredMask, out jobReason);
						RecordCoreAffinity(pass.DesiredMask, verified ? pass.DesiredMask : 0, verified);
						if (!verified) corePlacementEvidence = "pid=" + pid + " stage=job-audit reason=" + jobReason;
						return verified ? ExclusiveWriteBack.Written : ExclusiveWriteBack.Failed;
					}
					ulong currentMask;
					if (!Native.TryQueryAffinity(value.RestoreHandle, out currentMask)) return ExclusiveWriteBack.Failed;
					if (currentMask == pass.DesiredMask)
					{
						RecordCoreAffinity(pass.DesiredMask, currentMask, true);
						return ExclusiveWriteBack.Written;
					}
					string threadReason;
					if (!GameThreadAffinityAdmission.Check(value.RestoreHandle, pid, value.Creation, currentMask, out threadReason))
					{
						corePlacementEvidence = "pid=" + pid + " stage=thread-affinity-refused reason=" + threadReason;
						return ExclusiveWriteBack.Failed;
					}
					bool setProcessAffinityMaskSucceeded = Native.SetProcessAffinityMask(value.RestoreHandle, (UIntPtr)pass.DesiredMask);
					int writeError = setProcessAffinityMaskSucceeded ? 0 : Marshal.GetLastWin32Error();
					if (setProcessAffinityMaskSucceeded) criticalNextSample = 0L;
					ulong mask;
					bool tryQueryAffinity = Native.TryQueryAffinity(value.RestoreHandle, out mask);
					int readError = tryQueryAffinity ? 0 : Marshal.GetLastWin32Error();
					RecordCoreAffinity(pass.DesiredMask, mask, tryQueryAffinity);
					if (!setProcessAffinityMaskSucceeded || !tryQueryAffinity || mask != pass.DesiredMask)
					{
						if (!retryMaintenance) return ExclusiveWriteBack.Failed;
						return DeferExclusiveAuditFailure(pass, !setProcessAffinityMaskSucceeded ? "audit-write" : (!tryQueryAffinity ? "audit-write-readback" : "audit-write-mismatch"), !setProcessAffinityMaskSucceeded ? writeError : readError)
							? ExclusiveWriteBack.Deferred : ExclusiveWriteBack.Failed;
					}
					exclusiveAuditRetry.Reset();
					criticalNextSample = 0L;
					return ExclusiveWriteBack.Written;
				}
			}
			finally
			{
				Monitor.Exit(criticalProcessGate);
			}
		}
		private void YieldExclusive(int pid, ulong observed)
		{
			long numericValue = 0L;
			bool jobOwned = false;
			lock (sync)
			{
				HardPin pin;
				jobOwned = hardPins.TryGetValue(pid, out pin) && coreAffinityJobs.HasLease(pid, pin.Creation);
			}
			// Process restoration must follow critical-thread restoration under its gate.
			if (jobOwned && !RestoreHardPin(pid))
			{
				BlockAutoCores("schedule.auto.pending");
				return;
			}
			lock (sync)
			{
				placementGaveUp.Add(pid);
				gamePlacement.Remove(pid);
				Snap value;
				if (gameBoost.TryGetValue(pid, out value))
				{
					value.PlacementYielded = true;
					gameBoost[pid] = value;
					numericValue = value.Creation;
				}
			}
			if (numericValue > 0)
			{
				CrashGuard.YieldBoostPlacement(pid, numericValue);
			}
			ForgetHardPin(pid);
			coreAffinityVerified = false;
			if (coreManual != null)
			{
				corePlacementStopReason = "schedule.auto.external";
			}
			coreEngaged = false;
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			if (autoCoreGovernor != null)
			{
				autoCoreGovernor.Block("schedule.auto.external");
			}
			if (!StopCoreIsolation() && autoCoreGovernor != null)
			{
				autoCoreGovernor.Block("schedule.auto.pending");
			}
			Logger.Log(Lang.F("log.autocore.external", observed.ToString("X")));
		}
		private bool PinExclusive(IntPtr h, int pid, BoostPass pass, Snap original)
		{
			if (!Monitor.TryEnter(criticalProcessGate, 0))
			{
				return false;
			}
			try
			{
				if (!PrepareCriticalThreadProcessMaskMutation(pid, original.Creation))
				{
					return false;
				}
				return PinExclusiveLocked(h, pid, pass, original);
			}
			finally
			{
				Monitor.Exit(criticalProcessGate);
			}
		}
		private bool PinExclusiveLocked(IntPtr h, int pid, BoostPass pass, Snap original)
		{
			if (CpuTopology.MultiGroup || original.Aff == 0 || pass.DesiredMask == 0 || (pass.DesiredMask & ~allMask) != 0 || (coreManual == null && (pass.DesiredMask & ~original.Aff) != 0))
			{
				LogCorePlacement(pass, "affinity-range", 0, h);
				return false;
			}
			string threadAdmissionReason;
			if (!GameThreadAffinityAdmission.Check(h, pid, original.Creation, original.Aff, out threadAdmissionReason))
			{
				LogCorePlacement(pass, threadAdmissionReason, 0, h);
				return false;
			}
			IntPtr intPtr = DuplicateRestoreHandle(h);
			if (intPtr == IntPtr.Zero)
			{
				LogCorePlacement(pass, "restore-handle", Marshal.GetLastWin32Error(), h);
				return false;
			}
			bool retained = false;
			bool verified = false;
			try
			{
				ulong mask;
				lock (sync)
				{
					if (stopping) return false;
					if (!Native.TryQueryAffinity(h, out mask))
					{
						LogCorePlacement(pass, "read-original", Marshal.GetLastWin32Error(), h);
						return false;
					}
					if (mask != original.Aff)
					{
						LogCorePlacement(pass, "original-changed", 0, h);
						return false;
					}
					// Transfer recovery ownership from the legacy claim to the Job receipt before changing affinity.
					if (!CrashGuard.TrackBoostAffinity(pid, original.Creation, 0uL))
					{
						LogCorePlacement(pass, "journal", 0, h);
						return false;
					}
					// Retain the handle before the Job backend can change the target.
					if (!RememberHardPin(pid, original.Creation, original.Aff, pass.DesiredMask, intPtr)) return false;
					hardPins[pid].JobRecoveryOnly = true;
					retained = true;
				}
				string jobReason;
				coreAffinityJobs.ThreadRecoveryRoot = CriticalThreadStoreRoot;
				bool jobBound = coreAffinityJobs.Ensure(pid, original.Creation, pass.RendererName, original.Aff, pass.DesiredMask, out jobReason);
				lock (sync)
				{
					HardPin pin;
					if (hardPins.TryGetValue(pid, out pin) && pin.Creation == original.Creation)
					{
						pin.JobBound = jobBound;
						pin.JobFallbackReason = jobBound ? null : jobReason;
						if (pin.RestoreRequested || pin.RestoreInFlight) return false;
					}
				}
				if (stopping || coreAffinityJobs.HasPending || (!jobBound && coreAffinityJobs.IsActive(pid, original.Creation)))
				{
					LogCorePlacement(pass, "job-recovery", 0, h);
					return false;
				}
				if (!jobBound)
				{
					// A game-owned thread-placement refusal also blocks process-affinity fallback.
					if (GameThreadAffinityAdmission.IsRefusal(jobReason))
					{
						LogCorePlacement(pass, jobReason, 0, h);
						return false;
					}
					ulong fallbackMask;
					if (!Native.TryQueryAffinity(h, out fallbackMask)
						|| !GameThreadAffinityAdmission.Check(h, pid, original.Creation, fallbackMask, out threadAdmissionReason))
					{
						LogCorePlacement(pass, threadAdmissionReason ?? GameThreadAffinityAdmission.RefusalPrefix + "process-mask-unreadable", 0, h);
						return false;
					}
					Logger.Warn("Game affinity Job protection unavailable pid=" + pid + " reason=" + jobReason + "; using process-affinity maintenance");
					// After Job settlement, transfer recovery ownership before process-affinity fallback writes.
					lock (sync)
					{
						HardPin fallbackPin;
						if (stopping || !hardPins.TryGetValue(pid, out fallbackPin) || fallbackPin.Creation != original.Creation
							|| fallbackPin.RestoreRequested || fallbackPin.RestoreInFlight
							|| !CrashGuard.TrackBoostAffinity(pid, original.Creation, pass.DesiredMask))
						{
							LogCorePlacement(pass, "fallback-journal", 0, h);
							return false;
						}
						fallbackPin.JobRecoveryOnly = false;
						if (fallbackMask != pass.DesiredMask && !Native.SetProcessAffinityMask(h, (UIntPtr)pass.DesiredMask))
						{
							LogCorePlacement(pass, "set-affinity", Marshal.GetLastWin32Error(), h);
							return false;
						}
					}
				}
				exclusiveWriteBacks = 0;
				exclusiveAuditRetry.Reset();
				bool readable = Native.TryQueryAffinity(h, out mask);
				int readError = readable ? 0 : Marshal.GetLastWin32Error();
				verified = readable && mask == pass.DesiredMask;
				if (verified && jobBound) verified = coreAffinityJobs.Verify(pid, original.Creation, pass.DesiredMask, out jobReason);
				RecordCoreAffinity(pass.DesiredMask, mask, readable);
				coreAffinityVerified = verified;
				if (!verified) LogCorePlacement(pass, readable ? "readback-mismatch" : "readback", readError, h);
				return verified;
			}
			finally
			{
				if (!retained) Native.CloseHandle(intPtr);
				else if (!verified) RestoreHardPin(pid);
			}
		}
		private bool EngageIsolation(int pid, BoostPass pass)
		{
			if (stopping || coreManual == null)
			{
				return false;
			}
			if (!coreManual.Isolation)
			{
				coreEngaged = true;
				return true;
			}
			if (CriticalCoreIsolationRecoveryOwner != null && !StopCriticalThreads(0)) return false;
			lock (coreIsolationGate)
			{
			if (criticalOwnsCoreIsolation) return false;
			if (coreEngaged && CoreIsolationClient.State == "schedule.isolation.active")
			{
				return true;
			}
			if (coreIsolation == null)
			{
				coreIsolation = new CoreIsolationClient();
			}
			if (coreIsolation.Ensure((corePlan != null) ? corePlan.Exclusive : pass.DesiredMask, pid, pass.RendererCreation, pass.DesiredMask, true))
			{
				coreEngaged = true;
				Logger.Log(Lang.F("log.autocore.engaged", CpuTopology.DescribeMask((corePlan != null) ? corePlan.Exclusive : pass.DesiredMask)));
				return true;
			}
			}
			StopCoreIsolation();
			return false;
		}
		private bool StopCoreIsolation()
		{
			if (HasCriticalCoreIsolationLease && CriticalCoreIsolationRecoveryOwner == null && !StopCriticalThreads(0)) return false;
			coreAffinityVerified = false;
			bool result = StopCoreRefinement();
			ClearBackgroundSqueezes();
			coreEngaged = false;
			lock (coreIsolationGate)
			{
			if (criticalOwnsCoreIsolation) return result && !coreBackgroundPending;
			if (criticalBorrowsCoreIsolation) return false;
			bool matchesStop = coreIsolation == null || coreIsolation.Stop();
			if (matchesStop)
			{
				coreIsolation = null;
			}
			if (matchesStop && !coreBackgroundPending)
			{
				return result;
			}
			return false;
			}
		}
		private void PublishAutoCores(string game)
		{
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			ulong appliedAffinity = coreGameMask;
			bool jobBound = false;
			string jobFallback = null;
			lock (sync)
			{
				HardPin value;
				if (coreEngaged && hardPins.TryGetValue(coreWatchPid, out value))
				{
					appliedAffinity = value.AppliedAffinity;
					jobBound = value.JobBound;
					jobFallback = value.JobFallbackReason;
				}
			}
			AutoCoreStatus autoCoreStatus = new AutoCoreStatus();
			autoCoreStatus.Plan = corePlan;
			autoCoreStatus.Manual = coreManual != null;
			autoCoreStatus.ManualIsolation = coreManual != null && coreManual.Isolation;
			autoCoreStatus.Game = game;
			autoCoreStatus.ProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
			autoCoreStatus.Watching = autoCoreGovernor != null && coreWatchPid > 0;
			autoCoreStatus.Exclusive = autoCoreGovernor != null && autoCoreGovernor.Exclusive;
			autoCoreStatus.Engaged = coreEngaged;
			autoCoreStatus.BackgroundMoved = coreBackgroundMoved;
			autoCoreStatus.BackgroundAffinityDetail = core == null ? null : core.BackgroundAffinityDetail();
			autoCoreStatus.GameMask = appliedAffinity;
			autoCoreStatus.ExpectedMask = ((coreManual != null) ? coreManual.GameMask : coreGameMask);
			autoCoreStatus.ObservedMask = coreAffinityObserved;
			autoCoreStatus.AffinityRead = coreAffinityRead;
			autoCoreStatus.AffinityVerified = coreEngaged && coreAffinityVerified && !CorePending;
			autoCoreStatus.JobAffinity = jobBound && autoCoreStatus.AffinityVerified;
			autoCoreStatus.JobFallbackReason = jobFallback;
			autoCoreStatus.WorkMs = coreBudget.TotalMs;
			autoCoreStatus.WorkMaxMs = coreBudget.MaxMs;
			autoCoreStatus.Pending = CorePending;
			autoCoreStatus.PlacementEvidence = corePlacementEvidence;
			autoCoreStatus.Blocked = ((autoCoreGovernor != null) ? autoCoreGovernor.Blocked : ((coreManual != null && coreAllowed && corePlan != null) ? corePlan.Blocked : null));
			autoCoreStatus.ObservationOnly = coreObservationOnly;
			AutoCores.Publish(autoCoreStatus);
		}
		private void Boost(ProcessSnapshot all)
		{
			if (FrameCoordinationEnabled || EffLane) RefreshThreadBoostRecoveryAdmission();
			BoostPass boostPass = PrepareBoostPass();
			SyncFrameCoordination(boostPass);
			if (boostPass.Exclusive)
			{
				boostPass.DesiredMask = coreGameMask;
			}
			DropStaleBoosts(boostPass);
			RunIndependentCorePlacement(boostPass);
			if (all.ByPid.Count == all.Entries.Length)
			{
				ProcEntry renderer;
				if (all.ByPid.TryGetValue(boostPass.RendererPid, out renderer))
					BoostEntry(renderer, boostPass, null);
				PruneDeadBoosts(all.ByPid.Keys);
				return;
			}
			// Preserve entry order when an unusual snapshot contains duplicate PIDs.
			HashSet<int> hashSet = new HashSet<int>();
			ProcEntry[] entries = all.Entries;
			foreach (ProcEntry procEntry in entries)
				BoostEntry(procEntry, boostPass, hashSet);
			PruneDeadBoosts(hashSet);
		}
		private void BoostEntry(ProcEntry procEntry, BoostPass boostPass, HashSet<int> live)
		{
			try
			{
				int pid = procEntry.Pid;
				if (live != null) live.Add(pid);
				bool known;
				bool needTweak;
				bool needPlacement;
				if (boostPass.RendererPid <= 0 || pid != boostPass.RendererPid || !ComputeAuditDue(pid, boostPass, out known, out needTweak, out needPlacement))
				{
					return;
				}
				IntPtr intPtr = OpenBoostHandle(pid, boostPass);
				if (intPtr == IntPtr.Zero)
				{
					return;
				}
				try
				{
					long currentCreation;
					if (VerifyRendererIdentity(intPtr, pid, boostPass, out currentCreation) && RefreshBoostPriority(boostPass))
					{
						HandlePidReuse(pid, currentCreation, ref known, ref needPlacement);
						bool newlyTracked;
						bool gpuOk;
						bool stateOk;
						bool firstVerified;
						if (CaptureAndTrack(intPtr, pid, currentCreation, boostPass, known, out newlyTracked, out gpuOk) && ApplyBoostStateStage(intPtr, pid, boostPass, needTweak, out stateOk, out firstVerified))
						{
							bool matchesCoreAffinityVerified = boostPass.Exclusive && coreEngaged && coreAffinityVerified;
							string placementText = (matchesCoreAffinityVerified ? (Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(boostPass.DesiredMask) + Lang.T("t.gamemodeboost.16")) : Lang.T("t.gamemodeboost.17"));
							bool ecoCleared = ClearEfficiencyMode(intPtr, pid, boostPass);
							EngageLaneAndReport(intPtr, pid, currentCreation, boostPass, stateOk, firstVerified, gpuOk, ecoCleared, placementText, matchesCoreAffinityVerified);
							ApplyGameTweaks(intPtr, pid, boostPass, needTweak);
						}
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			catch
			{
			}
		}
		private BoostPass PrepareBoostPass()
		{
			BoostPass boostPass = new BoostPass();
			PolicySnapshot policySnapshot = sessionPolicy;
			PrepareResourcePriorities(boostPass, policySnapshot);
			boostPass.NvMaxPerf = ((policySnapshot != null) ? policySnapshot.NvMaxPerf : nvMaxPerf);
			boostPass.NvLowLat = ((policySnapshot != null) ? policySnapshot.NvLowLatMode : nvLowLatMode);
			boostPass.NvSmooth = ((policySnapshot != null) ? policySnapshot.NvSmoothMotion : nvSmoothMotion);
			boostPass.NvShader = ((policySnapshot != null) ? policySnapshot.NvShaderCacheMax : nvShaderCacheMax);
			boostPass.NvRebar = ((policySnapshot != null) ? policySnapshot.NvRebar : nvRebarOn);
			boostPass.NvDlss = ((policySnapshot != null) ? policySnapshot.NvDlssMode : nvDlssMode);
			boostPass.NvVrr = nvVrrWindowedOn;
			NvGamePlan nvGamePlan = RefreshAutomaticNvPlan(boostPass.NvMaxPerf, boostPass.NvShader, Native.OnAcPower(), boostPass.NvLowLat, boostPass.NvVrr, boostPass.NvRebar);
			boostPass.NvMaxPerf = nvGamePlan.MaxPerf;
			boostPass.RendererPid = -1;
			boostPass.RendererCreation = 0L;
			boostPass.RendererName = null;
			lock (sync)
			{
				if (activeDetection != null && activeDetection.RendererCandidateSelected)
				{
					boostPass.RendererPid = activeDetection.RendererPid;
					boostPass.RendererCreation = activeDetection.RendererCreation;
					boostPass.RendererName = activeDetection.RendererName;
				}
			}
			boostPass.WriteDenied = boostPass.RendererPid > 0 && ProtectedGameRoster.Contains(boostPass.RendererName);
			RenderLane.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), boostPass.RendererPid, boostPass.RendererCreation, "lane:" + EffLane);
			StepAutoCores(boostPass);
			AutoCorePlan autoCorePlan = corePlan;
			AutoCoreGovernor autoCoreGovernor = coreGovernor;
			boostPass.Exclusive = !coreObservationOnly && !CorePending && autoCoreGovernor != null && autoCoreGovernor.Exclusive && autoCorePlan != null && autoCorePlan.Available;
			boostPass.DesiredMask = (boostPass.Exclusive ? coreGameMask : allMask);
			ResolvePriorityTarget(boostPass);
			return boostPass;
		}
		internal static uint BoostPriorityTarget()
		{
			return Native.HIGH_PRIORITY_CLASS;
		}
		private void ResolvePriorityTarget(BoostPass pass)
		{
			if (pass.RendererPid != boostIdentityPid || pass.RendererCreation != boostIdentityCreation)
			{
				ResetBoostIdentity();
			}
			boostIdentityPid = pass.RendererPid;
			boostIdentityCreation = pass.RendererCreation;
			pass.IdentityGeneration = boostIdentityGeneration;
			if (boostIdentityNeedsAudit && pass.RendererPid > 0)
			{
				lock (sync)
				{
					boostStateVerified.Remove(pass.RendererPid);
					gameBoostNextAudit.Remove(pass.RendererPid);
					boostIdentityNeedsAudit = false;
				}
			}
			RefreshBoostPriority(pass);
		}
		private void ResetBoostIdentity()
		{
			boostIdentityPid = 0;
			boostIdentityCreation = 0L;
			boostIdentityGeneration++;
			boostIdentityNeedsAudit = true;
			boostLaneReleased = false;
		}
		private bool RefreshBoostPriority(BoostPass pass)
		{
			if (pass.IdentityGeneration != boostIdentityGeneration || pass.RendererPid <= 0 || pass.RendererCreation <= 0 || pass.RendererPid != boostIdentityPid || pass.RendererCreation != boostIdentityCreation)
			{
				return false;
			}
			pass.LaneAllowed = EffLane && LaneEligible && !pass.WriteDenied && LaneFallbackFor(pass.RendererPid, pass.RendererCreation)
				&& ThreadBoostRecoveryAllowed(pass.RendererPid, pass.RendererCreation);
			if (!pass.LaneAllowed)
			{
				if (!boostLaneReleased)
				{
					boostLaneReleased = RenderLane.Release();
				}
			}
			else
			{
				boostLaneReleased = false;
			}
			pass.PriorityTarget = BoostPriorityTarget();
			return true;
		}
		private void DropStaleBoosts(BoostPass pass)
		{
			bool dropStaleBoostsReady = false;
			lock (sync)
			{
				foreach (KeyValuePair<int, Snap> item in gameBoost)
				{
					if (item.Key != pass.RendererPid || item.Value.Creation != pass.RendererCreation || !string.Equals(item.Value.Name, pass.RendererName, StringComparison.OrdinalIgnoreCase))
					{
						dropStaleBoostsReady = true;
						break;
					}
				}
			}
			if (dropStaleBoostsReady)
			{
				UnboostGames(pass.RendererPid, pass.RendererCreation, pass.RendererName);
			}
		}
		private IntPtr OpenBoostHandle(int pid, BoostPass pass)
		{
			IntPtr intPtr = Native.OpenProcess(12800, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				bool lastOpenProcessFailureWasNoSuchProcessSucceeded = Native.LastOpenProcessFailureWasNoSuchProcess();
				bool wasAdded;
				lock (sync)
				{
					wasAdded = boostDenied.Add(pid);
				}
				if (wasAdded)
				{
					Logger.Log(Lang.T("log.gamemodeboost.3") + pass.RendererName + " pid " + pid + Lang.T("log.gamemodeboost.4"));
				}
				if (!lastOpenProcessFailureWasNoSuchProcessSucceeded)
				{
					ProtectedGameRoster.Remember(pass.RendererName);
				}
			}
			return intPtr;
		}
		private bool ApplyBoostStateStage(IntPtr h, int pid, BoostPass pass, bool needTweak, out bool stateOk, out bool firstVerified)
		{
			firstVerified = false;
			if (pass.WriteDenied)
			{
				stateOk = false;
				lock (sync)
				{
					boostStateVerified.Remove(pid);
					gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
				}
				return true;
			}
			bool applySelectedBoostActionSucceeded = ApplySelectedBoostAction(h, pid, pass, "GamePriority", pass.CpuBoost, (long value) => value == pass.PriorityTarget, () => WriteBoostAction("GamePriority", h, pass.PriorityTarget));
			bool applySelectedBoostActionSucceeded2 = ApplySelectedBoostAction(h, pid, pass, "GameIoPriority", pass.IoBoost, (long value) => value == 3, () => WriteBoostAction("GameIoPriority", h, 3L));
			bool applySelectedBoostActionSucceeded3 = ApplySelectedBoostAction(h, pid, pass, "GameMemoryPriority", pass.MemoryBoost, (long value) => value >= 5, () => WriteBoostAction("GameMemoryPriority", h, 5L));
			stateOk = applySelectedBoostActionSucceeded && applySelectedBoostActionSucceeded2 && applySelectedBoostActionSucceeded3;
			lock (sync)
			{
				if (stateOk)
				{
					firstVerified = boostStateVerified.Add(pid);
					boostStateWarned.Remove(pid);
				}
				else
				{
					boostStateVerified.Remove(pid);
				}
				boostStateFail.Remove(pid);
				gameBoostNextAudit[pid] = DateTime.UtcNow.AddSeconds(20 + Math.Abs(pid % 11)).Ticks;
			}
			return true;
		}
		private bool PlacementMatches(IntPtr h, BoostPass pass, out bool unreadable, out ulong observed)
		{
			unreadable = false;
			observed = 0uL;
			if (h == IntPtr.Zero || pass == null)
			{
				return false;
			}
			if (!pass.Exclusive && pass.DesiredMask == allMask)
			{
				return true;
			}
			if (pass.DesiredMask == 0 || (pass.DesiredMask & ~allMask) != 0 || CpuTopology.MultiGroup || pass.RendererPid <= 0 || pass.RendererCreation <= 0)
			{
				return false;
			}
			if (!Native.TryQueryAffinity(h, out observed))
			{
				observed = 0uL;
				unreadable = true;
				return false;
			}
			return observed == pass.DesiredMask;
		}
		private static HardPinHandleState HardPinHandleStateOf(HardPin pin)
		{
			if (pin == null || pin.RestoreHandle == IntPtr.Zero)
			{
				return HardPinHandleState.Unknown;
			}
			uint processId = GetProcessId(pin.RestoreHandle);
			if (processId != 0 && processId != (uint)pin.Pid)
			{
				return HardPinHandleState.Mismatch;
			}
			if (!Native.StillActive(pin.RestoreHandle))
			{
				return HardPinHandleState.Gone;
			}
			long creation;
			long cpu;
			ulong io;
			if (!Native.QueryProcessSample(pin.RestoreHandle, out creation, out cpu, out io))
			{
				return HardPinHandleState.Unknown;
			}
			if (processId == 0 || creation != pin.Creation)
			{
				return HardPinHandleState.Mismatch;
			}
			return HardPinHandleState.Match;
		}
		private bool RememberHardPin(int pid, long creation, ulong originalAffinity, ulong appliedAffinity, IntPtr restoreHandle)
		{
			if (pid <= 0 || creation <= 0 || originalAffinity == 0 || restoreHandle == IntPtr.Zero)
			{
				return false;
			}
			lock (sync)
			{
				HardPin value;
				if (hardPins.TryGetValue(pid, out value))
				{
					if (value.RestoreRequested || value.RestoreInFlight || (value.Creation != creation && coreAffinityJobs.HasLease(pid, value.Creation)))
					{
						RequestHardPinRestoreLocked(value);
						return false;
					}
					if (value.Creation == creation && value.RestoreHandle != IntPtr.Zero)
					{
						Native.CloseHandle(restoreHandle);
						return true;
					}
					hardPins.Remove(pid);
					if (value.RestoreHandle != IntPtr.Zero)
					{
						Native.CloseHandle(value.RestoreHandle);
					}
				}
				hardPins[pid] = new HardPin
				{
					Pid = pid,
					Creation = creation,
					OriginalAffinity = originalAffinity,
					AppliedAffinity = appliedAffinity,
					RestoreHandle = restoreHandle
				};
				return true;
			}
		}
		private void ForgetHardPin(int pid)
		{
			bool restore = false;
			lock (sync)
			{
				HardPin value;
				if (hardPins.TryGetValue(pid, out value))
				{
					if (value.RestoreInFlight || value.RestoreRequested || coreAffinityJobs.HasLease(pid, value.Creation))
					{
						RequestHardPinRestoreLocked(value);
						restore = true;
					}
					else
					{
						hardPins.Remove(pid);
						if (value.RestoreHandle != IntPtr.Zero) Native.CloseHandle(value.RestoreHandle);
					}
				}
			}
			if (restore && !Monitor.IsEntered(sync)) RestoreHardPin(pid);
		}
		private static int BoostActionIdentity(IntPtr handle, long creation)
		{
			return Native.QueryProcessIdentityState(handle, creation);
		}
		private static string BoostActionTarget(IntPtr handle)
		{
			string imagePathResult = Native.ImagePath(handle);
			try
			{
				return (string.IsNullOrEmpty(imagePathResult) || !Path.IsPathRooted(imagePathResult)) ? null : Path.GetFullPath(imagePathResult);
			}
			catch
			{
				return null;
			}
		}
		private static long QosValue(int control, int state)
		{
			return (long)(((ulong)(uint)control << 32) | (uint)state);
		}
		private static long? ReadBoostAction(string action, IntPtr handle)
		{
			switch (action)
			{
			case "GamePriority":
			{
				uint priorityClass = Native.GetPriorityClass(handle);
				if (priorityClass != 0)
				{
					return priorityClass;
				}
				return null;
			}
			case "GameIoPriority":
			{
				int ioPriority = Native.QueryIoPriority(handle);
				if (ioPriority >= 0)
				{
					return ioPriority;
				}
				return null;
			}
			case "GameMemoryPriority":
			{
				int pagePriority = Native.QueryPagePriority(handle);
				if (pagePriority >= 1)
				{
					return pagePriority;
				}
				return null;
			}
			case "GameGpuPriority":
			{
				int cls;
				if (Native.D3DKMTGetProcessSchedulingPriorityClass(handle, out cls) != 0)
				{
					return null;
				}
				return cls;
			}
			case "GameEcoQoS":
			{
				int controlMask;
				int stateMask;
				if (!Native.PowerThrottlingSupported || !Native.TryQueryPowerThrottling(handle, out controlMask, out stateMask))
				{
					return null;
				}
				return QosValue(controlMask, stateMask);
			}
			default:
				return null;
			}
		}
		private static int WriteBoostAction(string action, IntPtr handle, long value)
		{
			switch (action)
			{
			case "GamePriority":
				if (!Native.SetPriorityClass(handle, (uint)value))
				{
					return Marshal.GetLastWin32Error();
				}
				return 0;
			case "GameIoPriority":
			{
				if (value == 3 && !Native.EnsureBoostPrivilege())
				{
					return 1314;
				}
				int status2;
				if (!Native.TrySetIoPriority(handle, (int)value, out status2))
				{
					return status2;
				}
				return 0;
			}
			case "GameGpuPriority":
				return Native.D3DKMTSetProcessSchedulingPriorityClass(handle, (int)value);
			case "GameMemoryPriority":
			{
				int status;
				if (!Native.TrySetPagePriority(handle, (int)value, out status))
				{
					return status;
				}
				return 0;
			}
			case "GameEcoQoS":
				if (!Native.RestorePowerThrottling(handle, (int)(value >> 32), (int)value))
				{
					return Marshal.GetLastWin32Error();
				}
				return 0;
			default:
				return 50;
			}
		}
		private static int WriteHighQoSBoost(IntPtr handle)
		{
			bool timerExemptDropped;
			bool applyHighQoSSucceeded = Native.ApplyHighQoS(handle, Native.TimerExemptWanted, out timerExemptDropped);
			if (timerExemptDropped)
			{
				Logger.Log(Lang.T("log.gamemodeboost.59"));
			}
			if (!applyHighQoSSucceeded)
			{
				return Marshal.GetLastWin32Error();
			}
			return 0;
		}
		private static bool BoostActionSkipStatus(int status)
		{
			if (status != 5 && status != 50 && status != 87 && status != 120 && status != 127 && status != 1314 && status != -1073741790 && status != -1073741637 && status != -1073741821)
			{
				return status == -1073741811;
			}
			return true;
		}
		private bool TryOriginalBoostAction(int pid, string action, out long original)
		{
			original = 0L;
			lock (sync)
			{
				Snap value;
				if (!gameBoost.TryGetValue(pid, out value))
				{
					return false;
				}
				switch (action)
				{
				case "GamePriority":
					original = value.Pri;
					return value.Pri != 0 && value.Pri != uint.MaxValue;
				case "GameIoPriority":
					original = value.Io;
					return value.Io >= 0;
				case "GameMemoryPriority":
					original = value.Pg;
					return value.Pg >= 1;
				case "GameGpuPriority":
				{
					int value2;
					if (!gameGpu.TryGetValue(pid, out value2) || value2 < 0)
					{
						return false;
					}
					original = value2;
					return true;
				}
				case "GameEcoQoS":
					original = QosValue(value.QoSControl, value.QoSState);
					return value.QoSControl >= 0 && value.QoSState >= 0;
				default:
					return false;
				}
			}
		}
		private bool ApplyIsolatedBoostAction(IntPtr handle, int pid, BoostPass pass, string action, Func<long, bool> desired, Func<int> write)
		{
			if (action == "GamePriority")
			{
				Func<int> requestedWrite = write;
				write = () => WriteOwnedGamePriority(handle, pid, pass, requestedWrite);
			}
			string actionTarget = pass.ActionTarget;
			if (string.IsNullOrEmpty(actionTarget) || pass.WriteDenied || stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration || BoostActionIdentity(handle, pass.RendererCreation) != 0 || !string.Equals(BoostActionTarget(handle), actionTarget, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			ActionFailureStore.BeginAutomaticCycle(action, actionTarget, AutomaticPhaseKey(action));
			lock (sync)
			{
				boostActionTargets[pid] = actionTarget;
			}
			long original;
			if (!TryOriginalBoostAction(pid, action, out original))
			{
				return false;
			}
			long? boostAction = ReadBoostAction(action, handle);
			if (!boostAction.HasValue)
			{
				return false;
			}
			bool matchesTryGetValue;
			lock (sync)
			{
				HashSet<string> value;
				matchesTryGetValue = boostActionRestorePending.TryGetValue(pid, out value) && value.Contains(action);
			}
			if (matchesTryGetValue)
			{
				if (!RestoreBoostActionTarget(handle, pid, actionTarget, action))
				{
					return false;
				}
				if (matchesTryGetValue)
				{
					ActionFailureStore.CancelApply(action, actionTarget);
				}
				boostAction = ReadBoostAction(action, handle);
				if (!boostAction.HasValue)
				{
					return false;
				}
			}
			if (ActionFailureStore.IsBlocked(action, actionTarget))
			{
				return false;
			}
			if (desired(boostAction.Value))
			{
				return true;
			}
			string reason;
			if (!ActionFailureStore.TryBeginApply(action, actionTarget, out reason))
			{
				return false;
			}
			bool applyIsolatedBoostActionReady = false;
			try
			{
				boostAction = ReadBoostAction(action, handle);
				if (!boostAction.HasValue || pass.WriteDenied || stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration || BoostActionIdentity(handle, pass.RendererCreation) != 0)
				{
					ActionFailureStore.CancelApply(action, actionTarget);
					return false;
				}
				int applyIsolatedBoostActionValue = 0;
				if (!desired(boostAction.Value))
				{
					applyIsolatedBoostActionReady = true;
					SetBoostActionRecovery(pid, action, true);
					applyIsolatedBoostActionValue = write();
				}
				switch (BoostActionIdentity(handle, pass.RendererCreation))
				{
				case 1:
					SetBoostActionRecovery(pid, action, false);
					ActionFailureStore.CancelApply(action, actionTarget);
					return false;
				default:
					if (!applyIsolatedBoostActionReady)
					{
						ActionFailureStore.CancelApply(action, actionTarget);
					}
					else
					{
						ActionFailureStore.Trip(action, actionTarget, "Game action completion identity unreadable; recovery pending");
					}
					return false;
				case 0:
				{
					if (stopping || panicReq || pass.IdentityGeneration != boostIdentityGeneration)
					{
						if (!applyIsolatedBoostActionReady || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
						{
							ActionFailureStore.CancelApply(action, actionTarget);
						}
						else
						{
							ActionFailureStore.Trip(action, actionTarget, "Cancelled game action recovery remains incomplete");
						}
						return false;
					}
					long? boostAction2 = ReadBoostAction(action, handle);
					if (applyIsolatedBoostActionValue == 0 && boostAction2.HasValue && desired(boostAction2.Value))
					{
						if (ActionFailureStore.CompleteApply(action, actionTarget))
						{
							SetBoostActionRecovery(pid, action, false);
							return true;
						}
						if (RestoreBoostAction(handle, pid, pass.RendererCreation, action))
						{
							ActionFailureStore.CancelApply(action, actionTarget);
						}
						return false;
					}
					bool restoreBoostActionSucceeded = RestoreBoostAction(handle, pid, pass.RendererCreation, action);
					if (BoostActionSkipStatus(applyIsolatedBoostActionValue) || (action == "GamePriority" && applyIsolatedBoostActionValue == 1237))
					{
						if (restoreBoostActionSucceeded)
						{
							ActionFailureStore.CancelApply(action, actionTarget);
						}
						else
						{
							ActionFailureStore.Trip(action, actionTarget, "Skipped game action still requires verified recovery");
						}
						return false;
					}
					reason = "Game action apply/readback failed; native status " + applyIsolatedBoostActionValue + (restoreBoostActionSucceeded ? "; original restored" : "; recovery pending");
					ActionFailureStore.Trip(action, actionTarget, reason);
					Logger.Warn(action + " | " + actionTarget + " | " + reason);
					return false;
				}
				}
			}
			catch (NotSupportedException)
			{
				if (!applyIsolatedBoostActionReady || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
				{
					ActionFailureStore.CancelApply(action, actionTarget);
				}
				else
				{
					ActionFailureStore.Trip(action, actionTarget, "Unsupported game action recovery remains incomplete");
				}
				return false;
			}
			catch (OperationCanceledException)
			{
				if (!applyIsolatedBoostActionReady || RestoreBoostAction(handle, pid, pass.RendererCreation, action))
				{
					ActionFailureStore.CancelApply(action, actionTarget);
				}
				else
				{
					ActionFailureStore.Trip(action, actionTarget, "Cancelled game action recovery remains incomplete");
				}
				return false;
			}
			catch (Exception ex3)
			{
				if (!applyIsolatedBoostActionReady)
				{
					ActionFailureStore.CancelApply(action, actionTarget);
					return false;
				}
				if (applyIsolatedBoostActionReady)
				{
					RestoreBoostAction(handle, pid, pass.RendererCreation, action);
				}
				ActionFailureStore.Trip(action, actionTarget, ex3.GetType().Name + ": " + ex3.Message);
				return false;
			}
		}
		private void ForgetBoostActionTarget(int pid)
		{
			lock (sync)
			{
				boostActionTargets.Remove(pid);
				boostActionRestorePending.Remove(pid);
				boostResourceSelections.Remove(pid);
			}
		}
		private void RunIndependentCorePlacement(BoostPass pass)
		{
			long nowTicks = AutoCores.NowTicks;
			try
			{
				RunIndependentCorePlacementImpl(pass);
			}
			finally
			{
				coreBudget.Record(nowTicks);
			}
		}
		private void RunIndependentCorePlacementImpl(BoostPass pass)
		{
			if (coreManual == null || pass == null || !pass.Exclusive || CorePending || stopping)
			{
				return;
			}
			IntPtr intPtr = IntPtr.Zero;
			try
			{
				ulong observed;
				bool unreadable;
				if (ExclusiveDrifted(pass.RendererPid, pass, out observed, out unreadable))
				{
					if (!unreadable)
					{
						corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation + " stage=audit-mismatch actual=0x" + observed.ToString("X") + " target=0x" + pass.DesiredMask.ToString("X");
					}
					if (unreadable)
					{
						if (!exclusiveAuditDeferred) BlockAutoCores("schedule.auto.affinityread");
					}
					else
					{
						HoldExclusive(pass.RendererPid, pass, observed);
					}
				}
				else
				{
					if (coreEngaged)
					{
						return;
					}
					if (RendererForegroundPid() != pass.RendererPid)
					{
						if (coreGovernor != null)
						{
							ApplyAutoCoreStep(coreGovernor.Pause());
						}
						return;
					}
					intPtr = Native.OpenAffinityProcess(pass.RendererPid);
					int error = ((intPtr == IntPtr.Zero) ? Marshal.GetLastWin32Error() : 0);
					if (intPtr == IntPtr.Zero)
					{
						LogCorePlacement(pass, "open", error, intPtr);
						BlockAutoCores("schedule.auto.placementunavailable");
						return;
					}
					long currentCreation;
					if (!VerifyRendererIdentity(intPtr, pass.RendererPid, pass, out currentCreation))
					{
						LogCorePlacement(pass, "identity", 0, intPtr);
						BlockAutoCores("schedule.auto.identity");
						return;
					}
					uint granted;
					if (Native.TryQueryGrantedAccess(intPtr, out granted) && (granted & 0x200) == 0)
					{
						LogCorePlacement(pass, "access", 5, intPtr);
						BlockAutoCores("schedule.auto.placementunavailable");
						return;
					}
					bool needPlacement = true;
					bool known;
					lock (sync)
					{
						known = gameBoost.ContainsKey(pass.RendererPid);
					}
					HandlePidReuse(pass.RendererPid, currentCreation, ref known, ref needPlacement);
					bool newlyTracked;
					bool gpuOk;
					if (!CaptureAndTrack(intPtr, pass.RendererPid, currentCreation, pass, known, out newlyTracked, out gpuOk, false))
					{
						LogCorePlacement(pass, "snapshot", 0, intPtr);
						BlockAutoCores("schedule.auto.capture");
						return;
					}
					string placementText;
					bool placementVerified;
					if (!ApplyPlacementStage(intPtr, pass.RendererPid, pass, true, newlyTracked, out placementText, out placementVerified))
					{
						BlockAutoCores("schedule.auto.capture");
					}
					if (placementVerified)
					{
						LogCorePlacement(pass, "verified", 0, intPtr);
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Warn("AutoCore placement exception pid=" + pass.RendererPid + " type=" + ex.GetType().Name);
				BlockAutoCores("schedule.auto.placement");
			}
			finally
			{
				if (intPtr != IntPtr.Zero)
				{
					Native.CloseHandle(intPtr);
				}
				pass.Exclusive = coreGovernor != null && coreGovernor.Exclusive && !CorePending;
				if (!pass.Exclusive)
				{
					pass.DesiredMask = allMask;
				}
				PublishAutoCores(pass.RendererName);
			}
		}
		private void LogCorePlacement(BoostPass pass, string stage, int error, IntPtr handle)
		{
			pass.PlacementStage = stage;
			pass.PlacementError = error;
			uint granted;
			string toStringOffset = (Native.TryQueryGrantedAccess(handle, out granted) ? ("0x" + granted.ToString("X")) : "unknown");
			ulong mask;
			string toStringOffset2 = ((handle != IntPtr.Zero && Native.TryQueryAffinity(handle, out mask)) ? ("0x" + mask.ToString("X")) : "unknown");
			ulong numericValue = 0uL;
			lock (sync)
			{
				Snap value;
				if (gameBoost.TryGetValue(pass.RendererPid, out value))
				{
					numericValue = value.Aff;
				}
			}
			corePlacementEvidence = "pid=" + pass.RendererPid + " creation=" + pass.RendererCreation + " stage=" + stage + " win32=" + error + " granted=" + toStringOffset + " original=0x" + numericValue.ToString("X") + " actual=" + toStringOffset2 + " target=0x" + pass.DesiredMask.ToString("X") + " cachedBoostRestriction=" + pass.WriteDenied;
		}
		private static string PlacementFailureReason(BoostPass pass)
		{
			switch (pass.PlacementStage)
			{
			case "affinity-range":
				return "schedule.auto.affinityrange";
			case "journal":
				return "schedule.auto.capture";
			case "restore-handle":
				return "schedule.auto.restorehandle";
			case "read-original":
			case "read-placement":
			case "readback":
				return "schedule.auto.affinityread";
			case "set-affinity":
				return "schedule.auto.affinitywrite";
			default:
				return "schedule.auto.placement";
			}
		}
		private bool ApplyPlacementStage(IntPtr h, int pid, BoostPass pass, bool needPlacement, bool newlyTracked, out string placementText, out bool placementVerified)
		{
			placementText = "";
			placementVerified = false;
			bool unreadable = false;
			ulong observed = 0uL;
			if (!needPlacement)
			{
				placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
				if (pass.Exclusive && !placementVerified && !unreadable)
				{
					bool matchesContainsKey;
					lock (sync)
					{
						matchesContainsKey = !placementGaveUp.Contains(pid) && gameBoost.ContainsKey(pid);
					}
					if (matchesContainsKey)
					{
						placementVerified = HoldExclusive(pid, pass, observed);
					}
				}
				return true;
			}
			if (pass.Exclusive && RendererForegroundPid() != pid)
			{
				if (coreGovernor != null)
				{
					ApplyAutoCoreStep(coreGovernor.Pause());
				}
				PublishAutoCores(pass.RendererName);
				return true;
			}
			Snap value;
			lock (sync)
			{
				if (!gameBoost.TryGetValue(pid, out value))
				{
					return false;
				}
			}
			if (pass.Exclusive)
			{
				bool unreadable2;
				bool placementChangedOutsideSucceeded = PlacementChangedOutside(h, value, out observed, out unreadable2);
				if (unreadable2)
				{
					LogCorePlacement(pass, "read-placement", 0, h);
					BlockAutoCores("schedule.auto.affinityread");
					return true;
				}
				if (placementChangedOutsideSucceeded)
				{
					YieldExclusive(pid, observed);
					return true;
				}
			}
			bool applyPlacementStageReady = true;
			if (pass.Exclusive)
			{
				bool matchesApplyPlacementStageReady = applyPlacementStageReady && PinExclusive(h, pid, pass, value);
				if (!matchesApplyPlacementStageReady && pass.PlacementStage == "set-affinity" && pass.PlacementError == 5)
				{
					if (!Native.TryQueryAffinity(h, out observed) || observed != value.Aff)
					{
						BlockAutoCores("schedule.auto.affinityread");
					}
					else if (!CrashGuard.TrackBoostAffinity(pid, value.Creation, 0uL))
					{
						BlockAutoCores("schedule.auto.capture");
					}
					else
					{
						BlockAutoCores("schedule.auto.placementunavailable");
					}
					return true;
				}
				placementVerified = matchesApplyPlacementStageReady && PlacementMatches(h, pass, out unreadable, out observed);
				string placementFailureReasonResult = ((!placementVerified) ? PlacementFailureReason(pass) : ((!EngageIsolation(pid, pass)) ? "schedule.auto.failed" : null));
				if (placementFailureReasonResult != null)
				{
					placementVerified = false;
					BlockAutoCores(placementFailureReasonResult);
					if (coreGovernor == null)
					{
						ReleaseExclusive();
					}
					return true;
				}
				placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
				RecordCoreAffinity(pass.DesiredMask, observed, !unreadable);
				if (!placementVerified && !unreadable && WriteBackExclusive(pid, pass) == ExclusiveWriteBack.Written)
				{
					placementVerified = true;
				}
				if (!placementVerified)
				{
					if (unreadable)
					{
						BlockAutoCores("schedule.auto.affinityread");
					}
					else
					{
						YieldExclusive(pid, observed);
					}
					return true;
				}
				placementText = Lang.T("t.gamemodeboost.18") + CpuTopology.CountSetBits(pass.DesiredMask) + Lang.T("t.gamemodeboost.16");
			}
			else
			{
				placementText = Lang.T("t.gamemodeboost.17");
				placementVerified = PlacementMatches(h, pass, out unreadable, out observed);
				if (!placementVerified)
				{
					applyPlacementStageReady = false;
				}
			}
			int value2 = 0;
			bool applyPlacementStageReady2 = false;
			bool applyPlacementStageReady3 = false;
			lock (sync)
			{
				if (applyPlacementStageReady)
				{
					gamePlacement[pid] = pass.DesiredMask;
					placementFail.Remove(pid);
					placementGaveUp.Remove(pid);
				}
				else
				{
					gamePlacement.Remove(pid);
					placementFail.TryGetValue(pid, out value2);
					value2++;
					if (value2 >= 3)
					{
						placementFail.Remove(pid);
						applyPlacementStageReady2 = placementGaveUp.Add(pid);
					}
					else
					{
						placementFail[pid] = value2;
						applyPlacementStageReady3 = value2 == 1;
					}
				}
			}
			if (applyPlacementStageReady2 || (!applyPlacementStageReady && applyPlacementStageReady3))
			{
				Logger.Log(PlacementWarningLine(pass.RendererName, pid, unreadable, applyPlacementStageReady2));
			}
			if (!newlyTracked && applyPlacementStageReady)
			{
				Logger.Log(Lang.T("log.gamemodeboost.19") + pass.RendererName + " pid " + pid + placementText);
			}
			if (pass.Exclusive && placementVerified && applyPlacementStageReady)
			{
				Logger.Info("GAME placement verified pid=" + pid + " birth=" + pass.RendererCreation
					+ " mask=0x" + pass.DesiredMask.ToString("X")
					+ " source=" + (coreManual != null ? "manual" : "auto") + " name=" + pass.RendererName);
			}
			return true;
		}
		private void EngageLaneAndReport(IntPtr h, int pid, long currentCreation, BoostPass pass, bool stateOk, bool firstVerified, bool gpuOk, bool ecoCleared, string placementText, bool placementVerified)
		{
			if (pass.LaneAllowed && pass.LaneRestorationReady && EffLane && LaneEligible && pass.PriorityTarget == 128 && !pass.WriteDenied && !RenderLane.IsActiveFor(pid, currentCreation))
			{
				RenderLane.EnsureForGame(pid, currentCreation, pass.RendererName,
					() => ThreadBoostRecoveryAllowed(pid, currentCreation));
			}
			if (stateOk && firstVerified)
			{
				long exchangeResult = Interlocked.Exchange(ref boostFirstStampTicks, 0L);
				if (exchangeResult != 0)
				{
					Logger.Log(Lang.T("log.gamemodeboost.60") + (DateTime.UtcNow.Ticks - exchangeResult) / 10000 + Lang.T("log.gamemodeboost.61"));
				}
				Logger.Log(BoostSuccessLine(pass.RendererName, pid, (!pass.CpuBoost) ? "" : ((pass.PriorityTarget == 128) ? Lang.T("log.gamemodeboost.28") : Lang.T("log.gamemodeboost.29")), placementText, placementVerified, (pass.IoBoost ? Lang.T("log.gamemodeboost.30") : "") + (pass.MemoryBoost ? (" " + Lang.T("gm.boost.memory")) : "") + ((gpuOk && pass.GpuBoost) ? Lang.T("log.gamemodeboost.31") : "") + ((!Native.PowerThrottlingSupported) ? "" : (ecoCleared ? Lang.T("t.gamemodeboost.32") : EcoStateText(h)))));
			}
		}
		internal static string BoostSuccessLine(string renderer, int pid, string priority, string placement, bool placementVerified, string otherStates)
		{
			return "PASS " + Lang.T("log.gamemodeboost.27") + renderer + "(pid " + pid + ") " + priority + (placementVerified ? placement : "") + otherStates;
		}
		internal static string PlacementWarningLine(string renderer, int pid, bool unreadable, bool gaveUp)
		{
			return "WARN " + Lang.T("log.gamemodeboost.23") + renderer + " pid " + pid + " " + Lang.T(unreadable ? "log.placement.unreadable" : "log.placement.unconfirmed") + Lang.T(gaveUp ? "log.placement.stopped" : "log.gamemodeboost.24");
		}
		private bool UnboostGames()
		{
			return UnboostGames(0, 0L, null);
		}
		private static bool IsKeptBoost(KeyValuePair<int, Snap> boosted, int keepPid, long keepCreation, string keepName)
		{
			if (keepPid > 0 && boosted.Key == keepPid && boosted.Value.Creation == keepCreation)
			{
				return string.Equals(boosted.Value.Name, keepName, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		private bool UnboostGames(int keepPid, long keepCreation, string keepName)
		{
			bool releaseExceptSucceeded = RenderLane.ReleaseExcept(keepPid, keepCreation);
			releaseExceptSucceeded &= FrameCoordination.ReleaseExcept(keepPid, keepCreation);
			bool matchesStopCoreRefinement = (keepPid == coreWatchPid && keepCreation == coreWatchCreation) || StopCoreRefinement();
			bool matchesStopCoreIsolation = keepPid > 0 || StopCoreIsolation();
			bool matchesCoreEngaged = coreEngaged && !corePinPending;
			bool restoreHardPinsExceptSucceeded = RestoreHardPinsExcept(matchesCoreEngaged ? keepPid : 0, matchesCoreEngaged ? keepCreation : 0);
			corePinPending = !restoreHardPinsExceptSucceeded;
			matchesStopCoreIsolation = matchesStopCoreIsolation && matchesStopCoreRefinement && releaseExceptSucceeded && restoreHardPinsExceptSucceeded;
			List<KeyValuePair<int, Snap>> list;
			lock (sync)
			{
				if (gameBoost.Count == 0 && gameGpu.Count == 0)
				{
					return matchesStopCoreIsolation;
				}
				list = new List<KeyValuePair<int, Snap>>();
				foreach (KeyValuePair<int, Snap> item in gameBoost)
				{
					if (!IsKeptBoost(item, keepPid, keepCreation, keepName))
					{
						list.Add(item);
					}
				}
				Dictionary<int, int> dictionary = new Dictionary<int, int>();
				foreach (KeyValuePair<int, int> item2 in gameGpu)
				{
					if (keepPid <= 0 || item2.Key != keepPid)
					{
						dictionary[item2.Key] = item2.Value;
					}
				}
				if (list.Count == 0 && dictionary.Count == 0)
				{
					return matchesStopCoreIsolation;
				}
				if (keepPid <= 0)
				{
					boostFail.Clear();
					boostDenied.Clear();
					boostStateWarned.Clear();
					boostStateVerified.Clear();
					gameBoostNextAudit.Clear();
					ForgetNvTweaksLocked();
					boostHandleStripped.Clear();
					boostEcoGaveUp.Clear();
					placementFail.Clear();
					placementGaveUp.Clear();
					boostStateFail.Clear();
				}
				else
				{
					foreach (KeyValuePair<int, Snap> item3 in list)
					{
						boostFail.Remove(item3.Key);
						boostDenied.Remove(item3.Key);
						boostStateWarned.Remove(item3.Key);
						boostStateVerified.Remove(item3.Key);
						gameBoostNextAudit.Remove(item3.Key);
						ForgetNvTweakLocked(item3.Key);
						boostHandleStripped.Remove(item3.Key);
						boostEcoGaveUp.Remove(item3.Key);
						placementFail.Remove(item3.Key);
						placementGaveUp.Remove(item3.Key);
						boostStateFail.Remove(item3.Key);
					}
				}
			}
			foreach (KeyValuePair<int, Snap> item4 in list)
			{
				RestoreHardPin(item4.Key);
			}
			foreach (KeyValuePair<int, Snap> item5 in list)
			{
				int key = item5.Key;
				bool runBoostRecoverySucceeded = RunBoostRecovery(key, item5.Value.Creation, "snapshot", () => RestoreBoostProcess(key, item5.Value));
				if (runBoostRecoverySucceeded)
				{
					lock (coreIsolationGate)
					if ((criticalBorrowsCoreIsolation && criticalCoreIsolationPid == key && criticalCoreIsolationBirth == item5.Value.Creation)
						|| (!criticalOwnsCoreIsolation && coreIsolation != null && !coreIsolation.Drop(key, item5.Value.Creation)))
					{
						matchesStopCoreIsolation = false;
					}
					ForgetHardPin(key);
					CrashGuard.ReleaseBoostProcess(key, item5.Value.Creation);
					lock (sync)
					{
						gameBoost.Remove(key);
						gameGpu.Remove(key);
						gamePlacement.Remove(key);
						gameBoostNextAudit.Remove(key);
						ForgetBoostActionTarget(key);
					}
				}
			}
			lock (sync)
			{
				return matchesStopCoreIsolation && gameBoost.Count == 0;
			}
		}
		internal static void CurrentPlacement(IntPtr h, ref ulong affinity, ref uint[] cpuSets)
		{
			ulong mask;
			if (!CpuTopology.MultiGroup && Native.TryQueryAffinity(h, out mask) && mask != 0)
			{
				affinity = mask;
			}
			uint[] cpuSets2 = Native.QueryCpuSets(h);
			if (cpuSets2 != null)
			{
				cpuSets = cpuSets2;
			}
		}
		private static BoostTarget ProbeBoostTarget(int pid, long creation)
		{
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				if (Marshal.GetLastWin32Error() != 87)
				{
					return BoostTarget.Unopenable;
				}
				return BoostTarget.Gone;
			}
			try
			{
				long creation2;
				long cpu;
				ulong io;
				if (!Native.QueryProcessSample(intPtr, out creation2, out cpu, out io))
				{
					return BoostTarget.Unopenable;
				}
				if (creation > 0 && creation2 != creation)
				{
					return BoostTarget.Gone;
				}
				return (!Native.StillActive(intPtr)) ? BoostTarget.Gone : BoostTarget.Alive;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}
		internal int DropVanishedBoosts()
		{
			List<KeyValuePair<int, Snap>> list = new List<KeyValuePair<int, Snap>>();
			lock (sync)
			{
				foreach (KeyValuePair<int, Snap> item in gameBoost)
				{
					list.Add(item);
				}
			}
			if (list.Count == 0)
			{
				return 0;
			}
			int processedCount = 0;
			int processedCount2 = 0;
			foreach (KeyValuePair<int, Snap> item2 in list)
			{
				BoostTarget boostTarget = ProbeBoostTarget(item2.Key, item2.Value.Creation);
				if (boostTarget == BoostTarget.Alive)
				{
					lock (sync)
					{
						boostVanishTries.Remove(item2.Key);
					}
					continue;
				}
				bool matchesBoostTarget = boostTarget == BoostTarget.Unopenable;
				if (matchesBoostTarget)
				{
					int value;
					lock (sync)
					{
						boostVanishTries.TryGetValue(item2.Key, out value);
						value++;
						boostVanishTries[item2.Key] = value;
					}
					if (value < 20)
					{
						continue;
					}
					processedCount2++;
				}
				if (RestoreHardPin(item2.Key))
				{
					if (!matchesBoostTarget)
					{
						CrashGuard.ReleaseBoostProcess(item2.Key, item2.Value.Creation);
					}
					lock (sync)
					{
						boostVanishTries.Remove(item2.Key);
						gameBoost.Remove(item2.Key);
						gameGpu.Remove(item2.Key);
						gamePlacement.Remove(item2.Key);
						boostFail.Remove(item2.Key);
						boostStateWarned.Remove(item2.Key);
						boostStateVerified.Remove(item2.Key);
						boostHandleStripped.Remove(item2.Key);
						boostEcoGaveUp.Remove(item2.Key);
						placementGaveUp.Remove(item2.Key);
						placementFail.Remove(item2.Key);
						boostStateFail.Remove(item2.Key);
						gameBoostNextAudit.Remove(item2.Key);
						boostDenied.Remove(item2.Key);
						ForgetNvTweakLocked(item2.Key);
						ForgetBoostActionTarget(item2.Key);
					}
					processedCount++;
				}
			}
			if (processedCount2 > 0)
			{
				Logger.Warn(Lang.T("log.gamemodeboost.58") + processedCount);
			}
			else if (processedCount > 0)
			{
				Logger.Log(Lang.T("log.gamemodeboost.57") + processedCount);
			}
			return processedCount;
		}
		private void PruneDeadBoosts(ICollection<int> live)
		{
			lock (sync)
			{
				boostDenied.RemoveWhere((int x) => !live.Contains(x));
				boostStateWarned.RemoveWhere((int x) => !live.Contains(x));
				boostStateVerified.RemoveWhere((int x) => !live.Contains(x));
				boostHandleStripped.RemoveWhere((int x) => !live.Contains(x));
				boostEcoGaveUp.RemoveWhere((int x) => !live.Contains(x));
				placementGaveUp.RemoveWhere((int x) => !live.Contains(x));
				tweakApplied.RemoveWhere((int x) => !live.Contains(x));
				List<int> list = null;
				foreach (int key in gameBoost.Keys)
				{
					if (!live.Contains(key))
					{
						if (list == null)
						{
							list = new List<int>();
						}
						list.Add(key);
					}
				}
				if (list == null)
				{
					return;
				}
				foreach (int item in list)
				{
					// A reused or incomplete process snapshot is not proof of exit.
					if (ProbeBoostTarget(item, gameBoost[item].Creation) != BoostTarget.Gone) continue;
					if (RestoreHardPin(item))
					{
						CrashGuard.ReleaseBoostProcess(item, gameBoost[item].Creation);
						gameBoost.Remove(item);
						gameGpu.Remove(item);
						gamePlacement.Remove(item);
						boostFail.Remove(item);
						boostStateWarned.Remove(item);
						boostStateVerified.Remove(item);
						gameBoostNextAudit.Remove(item);
						placementFail.Remove(item);
						boostStateFail.Remove(item);
						ForgetBoostActionTarget(item);
					}
				}
			}
		}
		internal static string QoSDump(IntPtr process)
		{
			int controlMask;
			int stateMask;
			if (!Native.TryQueryPowerThrottling(process, out controlMask, out stateMask))
			{
				return Lang.T("t.gamemodeboost.37");
			}
			return "(control=0x" + controlMask.ToString("X") + " state=0x" + stateMask.ToString("X") + ")";
		}
		internal static bool HighQoSVerified(IntPtr process)
		{
			if (!Native.PowerThrottlingSupported)
			{
				return true;
			}
			int controlMask;
			int stateMask;
			if (!Native.TryQueryPowerThrottling(process, out controlMask, out stateMask))
			{
				return false;
			}
			return Native.HighQoSMasksOk(controlMask, stateMask);
		}
		private void ResetCoreIntelligence()
		{
			coreBudget = new CoreWorkBudget();
		}
		private bool StopCoreRefinement()
		{
			if (CoreThreadPlacement.UnownedPending)
			{
				return CoreThreadPlacement.Recover();
			}
			return true;
		}
		private void RevokeCriticalThreadIntent([CallerMemberName] string reason = null)
		{
			Interlocked.Increment(ref criticalIntentEpoch);
			CriticalThreadTargetLease criticalThreadTargetLease = criticalOriginalTarget;
			if (criticalThreadTargetLease != null)
			{
				if (!criticalThreadTargetLease.Revoked)
				{
					LogCriticalThreadState("revoked", reason);
				}
				criticalThreadTargetLease.Revoke();
			}
			CriticalThreadUiState criticalThreadUiState = criticalThreadUi;
			if (criticalThreadUiState.Applied || criticalThreadUiState.Pending)
			{
				criticalThreadUi = new CriticalThreadUiState(criticalThreadUiState.ProfileId, "recovery-pending", PerformancePolicy.Text("已停止新增绑定，正在核验原设置恢复", "Further bindings stopped; verifying restoration"), criticalThreadUiState.Enabled, false, true, criticalThreadUiState.Workers, criticalThreadUiState.SnapshotTicks, criticalThreadUiState.WorkerMask, criticalThreadUiState.CandidateMasks, criticalThreadUiState.CandidateLabels, criticalThreadUiState.SessionActive, criticalThreadUiState.RequestedWorkerAvoid, criticalThreadUiState.Coverage, criticalThreadUiState.SessionToken, criticalThreadUiState.GameSerial, criticalThreadUiState.IntentEpoch, criticalThreadUiState.TargetPid, criticalThreadUiState.TargetBirth, criticalThreadUiState.RuleSignature);
			}
		}
		private CriticalThreadTargetLease CreateCriticalThreadTarget(BoostPass pass, IntPtr process, CriticalThreadRule expectedRule, string expectedProfile, string expectedRawRule, ulong currentProcessMask, long expectedEpoch, long expectedSerial)
		{
			if (!Monitor.IsEntered(criticalProcessGate) || pass == null || process == IntPtr.Zero || expectedRule == null || currentProcessMask == 0 || GetProcessId(process) != (uint)pass.RendererPid || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
			{
				return null;
			}
			long criticalIntentEpochSnapshot = Interlocked.Read(ref criticalIntentEpoch);
			if (criticalIntentEpochSnapshot != expectedEpoch || Interlocked.Read(ref gameSessionStarts) != expectedSerial)
			{
				return null;
			}
			long currentCreation;
			if (!VerifyRendererIdentity(process, pass.RendererPid, pass, out currentCreation) || currentCreation != pass.RendererCreation)
			{
				return null;
			}
			string imagePathResult = Native.ImagePath(process);
			ulong mask;
			if (string.IsNullOrEmpty(imagePathResult) || !GamePerformanceTargetPolicy.AllowsGameTarget(imagePathResult) || !Native.TryQueryAffinity(process, out mask) || mask != currentProcessMask)
			{
				return null;
			}
			lock (sync)
			{
				GameDetection gameDetection = activeDetection;
				GameProfile gameProfile = CriticalPreferenceProfileLocked(expectedProfile);
				if (!enabled || stopping || panicReq || !active || smartSwitchedOut || ProfileStoreSaveFailed || gameDetection == null || gameDetection.Profile == null || gameProfile == null || gameDetection.Profile.Id != gameProfile.Id || !gameDetection.RendererCandidateSelected || gameDetection.RendererSafetyOnly || gameDetection.RequiresGpuConfirm || gameDetection.RendererPid != pass.RendererPid || gameDetection.RendererCreation != currentCreation || !string.Equals(gameDetection.RendererPath, imagePathResult, StringComparison.OrdinalIgnoreCase) || CriticalThreadRule.Raw(gameProfile) != expectedRawRule || !StoredRuleAdmits(expectedRawRule, expectedRule) || Interlocked.Read(ref criticalIntentEpoch) != expectedEpoch || gameSessionStarts != expectedSerial)
				{
					return null;
				}
				CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(gameProfile);
				if (criticalThreadAllowedRange.Error != null)
				{
					return null;
				}
				ulong currentProcessMaskMask = currentProcessMask & criticalThreadAllowedRange.Mask;
				ulong wholeCoresResult = AutoCores.WholeCores(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, CpuTopology.PhysicalCoreMasks());
				string validateResult = expectedRule.Validate(allMask, CpuTopology.PhysicalCoreMasks(), currentProcessMaskMask, wholeCoresResult, true);
				if (validateResult != null)
				{
					return null;
				}
				criticalMintInFlight = new CriticalThreadMint(pass.RendererPid, currentCreation, imagePathResult, currentProcessMaskMask & ~wholeCoresResult, criticalIntentEpochSnapshot, gameSessionStarts, expectedRawRule);
				try
				{
					return criticalOriginalTarget = CriticalThreadTargetLease.Issue(this, criticalMintInFlight);
				}
				finally
				{
					criticalMintInFlight = null;
				}
			}
		}
		private bool CriticalRolesWanted(int pid, long birth)
		{
			if (pid > 0 && birth > 0 && Volatile.Read(ref criticalRolesPid) == pid)
			{
				return Interlocked.Read(ref criticalRolesBirth) == birth;
			}
			return false;
		}
		private void WantCriticalRoles(CriticalRuntimeSource source, ThreadTimes snapshot)
		{
			if (FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks)
				.Any(fact => fact.Role == "presentation-associated")) return;
			criticalRolesTouched = true;
			int pid = source.Pid;
			long birth = source.Birth;
			Interlocked.Exchange(ref criticalRolesBirth, birth);
			Volatile.Write(ref criticalRolesPid, pid);
			Func<bool> admitted = delegate
			{
				GameDetection gameDetection = Volatile.Read(ref activeDetection);
				return enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed && CriticalRolesWanted(pid, birth) && gameDetection != null && gameDetection.RendererCandidateSelected && !gameDetection.RendererSafetyOnly && !gameDetection.RequiresGpuConfirm && gameDetection.RendererPid == pid && gameDetection.RendererCreation == birth && RendererForegroundPid() == pid;
			};
			FrameCoordination.SyncAutomaticPhase(Interlocked.Read(ref gameSessionStarts), pid, birth, FrameAutomaticConfiguration());
			FrameCoordination.EnsureObserver(pid, birth, source.Name, admitted);
		}
		private void SettleCriticalRoles()
		{
			if (criticalRolesTouched)
			{
				criticalRolesTouched = false;
			}
			else if (Volatile.Read(ref criticalRolesPid) != 0)
			{
				Volatile.Write(ref criticalRolesPid, 0);
				Interlocked.Exchange(ref criticalRolesBirth, 0L);
				FrameCoordination.ReleaseObserver();
			}
		}
		private void ResetCriticalAuto()
		{
			criticalRolesTouched = false;
			SettleCriticalRoles();
		}
		private void BeginAutomaticCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
		{
			CriticalThreadRoleFact[] roles = FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks);
			CriticalAutoTopology topology;
			string refusal;
			if (!ReadCriticalAutoTopology(source, out topology, out refusal))
			{
				criticalAutoSelectionKey = "";
				criticalAutoSelectionStable = 0;
				PublishCriticalThreadState(source, "unsupported", refusal, false, false, 0, 0L);
				return;
			}
			int capacity = CriticalThreadAutoPolicy.Capacity(topology.Physical, topology.Allowed, topology.Reserved, topology.Performance);
			if (capacity < 1)
			{
				criticalAutoSelectionKey = "";
				criticalAutoSelectionStable = 0;
				PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("可用物理核心不足：至少需要三个不同物理核，其中两个留给其他线程", "Not enough available physical cores: at least three distinct physical cores are needed, two of them left for other threads"), false, false, 0, 0L);
				return;
			}
			CriticalThreadHotspot[] hotspots = criticalObservation.Hotspots;
			bool pooled;
			HardThreadIdentity primary = CriticalThreadAutoPolicy.SelectPrimary(hotspots, roles, out pooled);
			if (primary == null)
			{
				criticalAutoSelectionKey = "";
				criticalAutoSelectionStable = 0;
				string waiting = (hotspots.Length == 0)
					? PerformancePolicy.Text("游戏线程尚未持续占用至少 30% 单核，暂不绑定", "No game thread keeps at least 30% of one core busy yet; nothing is bound")
					: (pooled
						? PerformancePolicy.Text("最忙的几个线程负载接近，像可互换的工作线程，交给系统调度，不硬绑定", "The busiest threads carry similar loads like an interchangeable worker pool; they are left to the scheduler and not bound")
						: ((roles.Length == 0)
							? PerformancePolicy.Text("正在识别渲染线程；识别不到时，最忙的线程持续 30 秒后再绑定", "Identifying the render thread; without it, the busiest thread is bound after 30 seconds of sustained load")
							: PerformancePolicy.Text("等待线程持续占用至少 30% 单核", "Waiting for a thread to keep at least 30% of one core busy")));
				PublishCriticalThreadState(source, "observing", waiting, false, false, 0, 0L);
				return;
			}
			HardThreadIdentity[] secondary = CriticalThreadAutoPolicy.SelectSecondary(hotspots, primary, capacity - 1);
			HardThreadIdentity[] threads = new HardThreadIdentity[1] { primary }.Concat(secondary).ToArray();
			string key = source.Pid + ":" + source.Birth + "|" + primary.Key + "|" + string.Join(",", secondary.Select((HardThreadIdentity x) => x.Key).OrderBy((string x) => x, StringComparer.Ordinal).ToArray());
			if (key == criticalAutoSelectionKey)
			{
				criticalAutoSelectionStable++;
			}
			else
			{
				criticalAutoSelectionKey = key;
				criticalAutoSelectionStable = 1;
			}
			if (criticalAutoSelectionStable < 2)
			{
				PublishCriticalThreadState(source, "observing", (secondary.Length == 0)
					? PerformancePolicy.Text("已选定主线程，确认稳定后硬绑定", "Main thread chosen; it is bound once the choice is stable")
					: PerformancePolicy.Text("已选定主线程，另有 " + secondary.Length + " 个接近跑满的线程将设为优先核（本机最多 " + capacity + " 个），确认稳定后一并施加", "Main thread chosen, plus " + secondary.Length + " near-saturated thread(s) for preferred cores (up to " + capacity + " on this PC); applied together once stable"), false, false, 0, 0L);
				return;
			}
			criticalAutoSelectionKey = "";
			criticalAutoSelectionStable = 0;
			CriticalThreadRule rule;
			HardThreadChoice[] choices;
			HardThreadChoice[] hints;
			if (!ResolveCriticalAutoRule(source, threads, topology, out rule, out choices, out hints, out refusal))
			{
				PublishCriticalThreadState(source, "unsupported", refusal, false, false, 0, 0L);
				return;
			}
			BindCriticalThreads(source, snapshot, rule, choices, roles);
			if (criticalSession != null && criticalPlan != null && criticalThreadUi.Applied)
			{
				string soft = (hints.Length == 0) ? "" : criticalIdealHints.Apply(source.Pid, source.Birth, hints);
				Logger.Info("THREAD " + (source.Rule.ManualTarget
					? PerformancePolicy.Text("关键线程已手动选核：", "Critical thread placed on the manually chosen CPU: ")
					: PerformancePolicy.Text("关键线程已自动选核：", "Critical threads placed on automatically chosen cores: ")) + source.Name
					+ " hard " + string.Join(", ", choices.Select((HardThreadChoice x) => "TID " + x.Thread.Tid + " -> " + CpuTopology.DescribeMask(x.Mask)).ToArray())
					+ (hints.Length == 0 ? "" : " | soft " + string.Join(", ", hints.Select((HardThreadChoice x) => "TID " + x.Thread.Tid + " -> " + CpuTopology.DescribeMask(x.Mask)).ToArray()) + " [" + soft + "]")
					+ (source.Rule.WorkerAvoidance ? " workers-avoid" : " candidates-only core-clearing=off") + " hard=" + choices.Length + " soft=" + criticalIdealHints.Count + "/" + hints.Length + " capacity=" + capacity);
			}
		}
		private bool ReadCriticalAutoTopology(CriticalRuntimeSource source, out CriticalAutoTopology topology, out string refusal)
		{
			topology = null;
			refusal = null;
			if (IntPtr.Size != 8 || CpuTopology.MultiGroup || !CpuTopology.TopologySourcesAgree || CpuTopology.AllMaskReconciled)
			{
				refusal = PerformancePolicy.Text("处理器拓扑或处理器组暂不支持", "CPU topology or processor groups are unsupported");
				return false;
			}
			GameProfile profile;
			lock (sync)
			{
				profile = CriticalPreferenceProfileLocked(source.Profile);
				if (profile != null) profile = profile.Clone();
			}
			CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(profile);
			if (criticalThreadAllowedRange.Error != null)
			{
				refusal = criticalThreadAllowedRange.Error;
				return false;
			}
			ulong mask;
			if (!ReadCriticalProcessMask(source.Pid, source.Birth, out mask))
			{
				refusal = PerformancePolicy.Text("无法读取游戏进程的处理器亲和性；未施加线程硬绑定", "The game's processor affinity cannot be read; no hard thread binding is applied");
				return false;
			}
			topology = new CriticalAutoTopology
			{
				Physical = CpuTopology.PhysicalCoreMasks(),
				Domains = CpuTopology.L3Masks(),
				Allowed = mask & criticalThreadAllowedRange.Mask,
				Reserved = AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask,
				Performance = (CpuTopology.Hybrid ? CpuTopology.PerfMask : 0),
				PreferredDomain = (CpuTopology.AsymCache ? CpuTopology.BigL3Mask : 0)
			};
			return true;
		}
		private bool ResolveCriticalAutoRule(CriticalRuntimeSource source, HardThreadIdentity[] threads, CriticalAutoTopology topology, out CriticalThreadRule rule, out HardThreadChoice[] choices, out HardThreadChoice[] hints, out string refusal)
		{
			rule = null;
			choices = null;
			hints = null;
			refusal = null;
			refusal = source.Rule.Validate(allMask, topology.Physical, topology.Allowed, topology.Reserved, true);
			if (refusal != null) return false;
			// Preserve thread priority order when cores are scarce; discard lower-priority soft hints first.
			ulong[] destinationsResult = null;
			for (int count = Math.Min(threads.Length, CriticalThreadAutoPolicy.MaximumIsolated); count >= 1 && destinationsResult == null; count--)
			{
				destinationsResult = CriticalThreadAutoPolicy.Destinations(source.Rule, count, topology.Physical, topology.Allowed, topology.Reserved, CpuTopology.FavoredMask, topology.Performance, topology.Domains, topology.PreferredDomain, CpuTopology.ThrottleMask);
			}
			if (destinationsResult == null)
			{
				refusal = PerformancePolicy.Text("可用物理核心不足：至少选中三个不同物理核，每核可只选一个逻辑线程", "Not enough available physical cores: select at least three distinct physical cores; one logical processor per core is enough");
				return false;
			}
			CriticalThreadRule criticalThreadRule = new CriticalThreadRule(ManualCorePlan.Stamp(allMask, topology.Physical), source.Rule.WorkerAvoidance, new ulong[1] { destinationsResult[0] });
			refusal = criticalThreadRule.Validate(allMask, topology.Physical, topology.Allowed, topology.Reserved, true);
			if (refusal != null)
			{
				return false;
			}
			HardThreadIdentity[] chosen = threads;
			rule = criticalThreadRule;
			choices = new HardThreadChoice[1] { new HardThreadChoice(chosen[0], destinationsResult[0]) };
			hints = destinationsResult.Skip(1).Select((ulong mask, int index) => new HardThreadChoice(chosen[index + 1], mask)).ToArray();
			return true;
		}
		private static bool ReadCriticalProcessMask(int pid, long birth, out ulong mask)
		{
			mask = 0uL;
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				return Native.QueryProcessIdentityState(intPtr, birth) == 0 && Native.TryQueryAffinity(intPtr, out mask) && mask != 0;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}
		private bool DrainHistoricalCriticalThreads(int timeoutMs)
		{
			if (Monitor.IsEntered(sync) || Monitor.IsEntered(criticalProcessGate))
			{
				return false;
			}
			return criticalHistoricalIdle.WaitOne(Math.Max(0, timeoutMs));
		}
		internal CriticalThreadUiState CriticalThreadView(string profileId)
		{
			CriticalThreadUiState criticalThreadUiState = criticalThreadUi;
			if (criticalThreadUiState.Pending && (criticalThreadResidue || criticalHistoricalDebt))
			{
				return criticalThreadUiState;
			}
			GameProfile profile;
			lock (sync)
			{
				if (!criticalThreadUiState.Pending && CriticalThreadSnapshotCurrent(criticalThreadUiState, profileId))
				{
					return criticalThreadUiState;
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				profile = ((gameProfile == null) ? null : gameProfile.Clone());
			}
			CriticalThreadRule criticalThreadRule = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(profile));
			if (criticalThreadRule == null)
			{
				return new CriticalThreadUiState(profileId, "off", PerformancePolicy.Text("未启用线程硬绑定", "Thread hard affinity is off"), false, false, false, 0, 0L, 0uL, null, null, false, false, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);
			}
			return new CriticalThreadUiState(profileId, "waiting-game", criticalThreadRule.Automatic
				? PerformancePolicy.Text("下次游戏运行时自动识别关键线程并绑定独立物理核心，其他接近满载的线程设为优先核", "The next game session binds the critical thread to its own physical core and gives other near-saturated threads a preferred core")
				: PerformancePolicy.Text("保存的规则在下次有效游戏会话中核验", "The saved rule is checked in the next valid game session"), true, false, false, 0, 0L, 0uL, null, null, false, criticalThreadRule.WorkerAvoidance, CriticalThreadCoverageState.None, null, 0L, 0L, 0, 0L);
		}
		internal string CriticalThreadDiagnosticLine()
		{
			CriticalThreadUiState view = criticalThreadUi;
			if (view == null) return "unavailable";
			long now = AutoCores.NowTicks;
			long age = view.SnapshotTicks > 0 && now >= view.SnapshotTicks ? (now - view.SnapshotTicks) / 10000L : -1;
			return "cached=" + view.State + " applied=" + view.Applied + " pending=" + view.Pending
				+ " pid=" + view.TargetPid + " birth=" + view.TargetBirth + " serial=" + view.GameSerial
				+ " rule=" + view.RuleSignature + " candidates=" + string.Join(",", view.CandidateMasks.Select(mask => "0x" + mask.ToString("X")))
				+ " workers=" + view.Workers + " workerAvoid=" + view.RequestedWorkerAvoid + " workerMask=0x" + view.WorkerMask.ToString("X")
				+ " corrections=" + Volatile.Read(ref criticalReassertions) + " ageMs=" + age + " coupling=" + view.Coupling
				+ (view.Detail.Length == 0 ? "" : " detail=" + view.Detail);
		}
		internal string CriticalThreadRuntimeDiagnosticLine()
		{
			CriticalThreadRuntimeTelemetry telemetry = Volatile.Read(ref criticalRuntimeTelemetry);
			return (telemetry == null ? "no samples" : telemetry.Describe())
				+ " | lastCandidate=" + Volatile.Read(ref criticalLastAuditDetail);
		}
		private void TraceCriticalRuntime(string stage, string result, int pid, long started, bool forwardAttempt = false)
		{
			if (criticalRuntimeTelemetry == null) criticalRuntimeTelemetry = new CriticalThreadRuntimeTelemetry();
			criticalRuntimeTelemetry.Record(stage, result, pid, started, forwardAttempt);
		}
		internal string SaveCriticalThreadRule(string profileId, string expected, CriticalThreadRule rule)
		{
			if (rule != null && rule.Automatic && !rule.ManualTarget) rule = CriticalThreadRule.Auto(true);
			bool affectsSession = false;
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed)
				{
					return PerformancePolicy.Text("游戏库暂时无法保存", "The game library cannot be saved right now");
				}
				GameProfile gameProfile = FindProfileLocked(profileId);
				if (gameProfile == null || CriticalThreadRule.Raw(gameProfile) != expected)
				{
					return PerformancePolicy.Text("游戏规则已变化，请重新打开", "The game rule changed; reopen the editor");
				}
				CriticalThreadRule savedRule = CriticalThreadRule.ParseForRuntime(CriticalThreadRule.Raw(gameProfile));
				if (rule != null && savedRule != null && rule.Encode() == savedRule.Encode()) return null;
				if (rule != null)
				{
					CriticalThreadAllowedRange criticalThreadAllowedRange = CriticalThreadAllowedRange.For(gameProfile);
					if (criticalThreadAllowedRange.Error != null)
					{
						return criticalThreadAllowedRange.Error;
					}
					string validateResult = rule.Validate(allMask, CpuTopology.PhysicalCoreMasks(), criticalThreadAllowedRange.Mask, AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask, IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled);
					if (validateResult != null)
					{
						return validateResult;
					}
				}
				GameProfile gameProfile2 = gameProfile.Clone();
				if (rule == null)
				{
					gameProfile2.Overrides.Remove("GameCriticalThreadHardRuleV1");
				}
				else
				{
					gameProfile2.Overrides["GameCriticalThreadHardRuleV1"] = rule.Encode();
				}
				if (CriticalThreadRule.Raw(gameProfile2) == CriticalThreadRule.Raw(gameProfile)) return null;
				List<GameProfile> list = new List<GameProfile>(profiles);
				list[list.IndexOf(gameProfile)] = gameProfile2;
				if (!SaveProfileSnapshotLocked(list))
				{
					return PerformancePolicy.Text("保存失败，原规则保留", "Save failed; the original rule was retained");
				}
				profiles[profiles.IndexOf(gameProfile)] = gameProfile2;
				if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
				{
					activeDetection.Profile = gameProfile2.Clone();
				}
				if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
				{
					stickyDetection.Profile = gameProfile2.Clone();
				}
				CriticalRuntimeSource source = Volatile.Read(ref criticalSource);
				affectsSession = rule == null && ((source != null && source.Profile == profileId)
					|| (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId));
				if (rule == null) DisableSessionCriticalPreferenceLocked(profileId);
				if (affectsSession) RevokeCriticalThreadIntent("SaveCriticalThreadRule");
			}
			if (affectsSession) RequestPolicyApply("SaveCriticalThreadRule");
			RaiseLibraryChanged();
			return null;
		}
		private bool CriticalThreadSnapshotCurrent(CriticalThreadUiState view, string profileId)
		{
			if (!Monitor.IsEntered(sync) || view.ProfileId != profileId || !view.SessionActive || view.SessionToken == Guid.Empty || view.TargetPid <= 0 || view.TargetBirth <= 0 || view.GameSerial != gameSessionStarts || view.IntentEpoch != Interlocked.Read(ref criticalIntentEpoch) || !active || stopping || !enabled || activeDetection == null || activeDetection.Profile == null || activeDetection.Profile.Id != profileId || activeDetection.RendererPid != view.TargetPid || activeDetection.RendererCreation != view.TargetBirth)
			{
				return false;
			}
			GameProfile gameProfile = CriticalPreferenceProfileLocked(profileId);
			if (gameProfile != null)
			{
				return CriticalThreadRule.Raw(gameProfile) == view.RuleSignature;
			}
			return false;
		}
		private CriticalRuntimeSource CaptureCriticalThreadSource(out string missing)
		{
			lock (sync)
			{
				GameDetection gameDetection = activeDetection;
				missing = ((!enabled || stopping || panicReq) ? "game mode stopped" : ((!active) ? "session inactive" : (ProfileStoreSaveFailed ? "profile store unavailable" : ((gameDetection == null || gameDetection.Profile == null) ? "no game detection" : ((!gameDetection.RendererCandidateSelected) ? "renderer candidate not selected" : (gameDetection.RendererSafetyOnly ? "renderer is safety-only" : (gameDetection.RequiresGpuConfirm ? "renderer awaits GPU confirmation" : ((gameDetection.RendererPid <= 0 || gameDetection.RendererCreation <= 0 || string.IsNullOrEmpty(gameDetection.RendererPath)) ? "renderer identity incomplete" : null))))))));
				if (missing != null)
				{
					return null;
				}
				GameProfile gameProfile = CriticalPreferenceProfileLocked(gameDetection.Profile.Id);
				if (gameProfile == null) { missing = "game removed"; return null; }
				string rawResult = CriticalThreadRule.Raw(gameProfile);
				CriticalRuntimeSource criticalRuntimeSource = criticalSource;
				if (criticalRuntimeSource != null && criticalRuntimeSource.RuleText == rawResult && criticalRuntimeSource.Profile == gameProfile.Id && criticalRuntimeSource.Pid == gameDetection.RendererPid && criticalRuntimeSource.Birth == gameDetection.RendererCreation && criticalRuntimeSource.Serial == gameSessionStarts && criticalRuntimeSource.Epoch == Interlocked.Read(ref criticalIntentEpoch) && string.Equals(criticalRuntimeSource.Path, gameDetection.RendererPath, StringComparison.OrdinalIgnoreCase))
				{
					return criticalRuntimeSource;
				}
				if (CriticalThreadRule.Parse(rawResult) == null)
				{
					missing = "rule unreadable";
					return null;
				}
				return new CriticalRuntimeSource(gameDetection, rawResult, gameSessionStarts, Interlocked.Read(ref criticalIntentEpoch));
			}
		}
		private void StepCriticalThreadRuntime()
		{
			string missing;
			CriticalRuntimeSource criticalRuntimeSource = CaptureCriticalThreadSource(out missing);
			if (!Monitor.TryEnter(criticalProcessGate, 0))
			{
				return;
			}
			try
			{
				if (criticalStability == null) criticalStability = new CriticalThreadStabilityPolicy();
				if (criticalSession == null && criticalPriorityReservation != null && !RestoreCriticalThreadsLocked()) return;
				if (Interlocked.Exchange(ref criticalForegroundAuditPending, 0) != 0)
				{
					criticalNextSample = 0L;
				}
				if (criticalRestorationConfirmed)
				{
					if (!RestoreCriticalThreadsLocked())
					{
						return;
					}
					criticalObservation.Reset();
					criticalNextSample = 0L;
				}
				if (criticalSource != null && !criticalSource.Same(criticalRuntimeSource))
				{
					if (!WithdrawCriticalThreadsLocked((criticalRuntimeSource == null) ? missing : criticalSource.Difference(criticalRuntimeSource)))
					{
						return;
					}
					criticalSource = null;
					criticalObservation.Reset();
					criticalNextSample = 0L;
				}
				if (criticalRuntimeSource == null)
				{
					WithdrawCriticalThreadsLocked(missing);
					return;
				}
				if (criticalSource == null)
				{
					criticalSource = criticalRuntimeSource;
					criticalReassertions = 0;
					criticalObservationMisses = 0;
				}
				CriticalRuntimeSource criticalRuntimeSource2 = criticalSource;
				if (criticalStability.IsBlocked(criticalSource.Profile, criticalSource.Pid, criticalSource.Birth, criticalSource.Serial, criticalSource.RuleText))
				{
					if (WithdrawCriticalThreadsLocked("repeated thread placement conflict"))
						PublishCriticalThreadState(criticalSource, "conflict", PerformancePolicy.Text("本局线程覆盖或归属未能确认，已恢复原设置并停止重新绑定", "Thread coverage or ownership could not be confirmed; original settings were restored and binding is stopped for this session"), false, false, 0, 0L);
					return;
				}
				if (criticalSession == null && CriticalThreadHasRecoveryDebt(false))
				{
					PublishCriticalThreadState(criticalRuntimeSource2, "recovery-pending", PerformancePolicy.Text("旧线程恢复记录尚未结清；未新增绑定", "Previous thread recovery is unresolved; no new binding is applied"), false, true, 0, 0L);
					return;
				}
				if (ProtectedGameRoster.Contains(criticalRuntimeSource2.Name))
				{
					if (WithdrawCriticalThreadsLocked("the game is on the protected roster"))
					{
						criticalObservation.Reset();
						PublishCriticalThreadState(criticalRuntimeSource2, "unsupported", PerformancePolicy.Text("该游戏此前拒绝或剥夺过写入权限；未施加线程硬绑定", "This game previously denied or stripped write access; no hard thread binding is applied"), false, false, 0, 0L);
					}
					return;
				}
				if (criticalGuardianLaunchBlocked)
				{
					if (WithdrawCriticalThreadsLocked("independent guardian launch unavailable"))
					{
						criticalObservation.Reset();
						ResetCriticalAuto();
						PublishCriticalThreadState(criticalRuntimeSource2, "unsupported", CriticalGuardianLaunchBlockedDetail(), false, false, 0, 0L);
					}
					return;
				}
				if (smartSwitchedOut || RendererForegroundPid() != criticalRuntimeSource2.Pid)
				{
					// Minimization can invalidate samples; retain the current lease and observation history until return.
					if (criticalSession == null)
					{
						PublishCriticalThreadState(criticalRuntimeSource2, "observing", PerformancePolicy.Text("等待游戏回到前台后重新观察", "Observations restart when the game returns to the foreground"), false, false, 0, 0L);
					}
					return;
				}
				bool observerRequested = false;
				if (criticalRuntimeSource2.Rule.Automatic && criticalSession == null && criticalObservation.Hotspots.Length > 0)
				{
					criticalRolesTouched = true;
					if (!FrameCoordination.HasCriticalPresenterCandidate(criticalRuntimeSource2.Pid, criticalRuntimeSource2.Birth, AutoCores.NowTicks))
					{
						WantCriticalRoles(criticalRuntimeSource2, null);
						observerRequested = true;
					}
				}
				long nowTicks = AutoCores.NowTicks;
				if (nowTicks < criticalNextSample)
				{
					return;
				}
				criticalNextSample = nowTicks + 50000000;
				if (criticalRuntimeSource2.Rule.Automatic && criticalSession != null && criticalPlan != null
					&& criticalPlan.WorkerMode == HardThreadWorkerMode.CandidatesOnly)
				{
					AuditAutomaticCriticalThread(criticalRuntimeSource2);
					return;
				}
				long sampleStarted = Stopwatch.GetTimestamp();
				ThreadTimes threadTimes = ProcessSnapshotSource.SampleThreads(criticalRuntimeSource2.Pid, criticalRuntimeSource2.Birth, 1500);
				bool observed = criticalObservation.Observe(threadTimes, criticalRuntimeSource2.Birth, AutoCores.NowTicks);
				TraceCriticalRuntime("sample", observed ? "ok" : "unavailable", criticalRuntimeSource2.Pid, sampleStarted);
				if (!observed)
				{
					if (criticalSession != null && ++criticalObservationMisses < 3)
					{
						PublishCriticalThreadState(criticalRuntimeSource2, "coverage-pending", PerformancePolicy.Text("线程观测暂不可用，暂停新增与纠偏，等待重新核验", "Thread observations are temporarily unavailable; new bindings and corrections are paused until verification resumes"), false, false, 0, 0L);
						return;
					}
					if (criticalSession != null) criticalStability.Block(criticalSource.Profile, criticalSource.Pid, criticalSource.Birth, criticalSource.Serial, criticalSource.RuleText);
					if (WithdrawCriticalThreadsLocked("thread observation unavailable"))
					{
						PublishCriticalThreadState(criticalRuntimeSource2, "observing", PerformancePolicy.Text("线程观测暂不可用；未继续新增绑定", "Thread observations are unavailable; further bindings are stopped"), false, false, 0, 0L);
					}
				}
				else if (criticalSession != null)
				{
					AuditCriticalThreadSession(criticalRuntimeSource2, threadTimes);
				}
				else if (criticalRuntimeSource2.Rule.Automatic)
				{
					if (!observerRequested && criticalObservation.Hotspots.Length > 0) WantCriticalRoles(criticalRuntimeSource2, threadTimes);
					BeginAutomaticCriticalThreadSession(criticalRuntimeSource2, threadTimes);
				}
				else
				{
					BeginCriticalThreadSession(criticalRuntimeSource2, threadTimes);
				}
			}
			catch (OperationCanceledException ex)
			{
				WithdrawCriticalThreadsLocked(ex.Message);
			}
			catch (HardThreadOperationException ex2)
			{
				if (ex2.Kind == HardThreadFailureKind.RecoveryPending)
				{
					criticalHistoricalDebt = true;
					criticalDebtNextRead = 0L;
					WithdrawCriticalThreadsLocked(ex2.Message);
					PublishCriticalThreadState(criticalSource, "recovery-pending", ex2.Message, false, true, 0, 0L);
				}
				else if (WithdrawCriticalThreadsLocked(string.Concat(ex2.Kind, ": ", ex2.Message)))
				{
					PublishCriticalThreadState(criticalSource, "unsupported", ex2.Message, false, false, 0, 0L);
				}
			}
			catch (HardThreadStorageDomainException ex3)
			{
				if (WithdrawCriticalThreadsLocked("storage domain: " + ex3.Message))
				{
					PublishCriticalThreadState(criticalSource, "unsupported", PerformancePolicy.Text("关键线程硬绑定需要本地数据目录；当前目录未获支持", "Hard thread placement requires a supported local data directory"), false, false, 0, 0L);
				}
				Logger.RepeatedError("critical-thread-storage-domain", ex3.Message, -1L);
				criticalNextSample = AutoCores.NowTicks + 300000000;
			}
			catch (Exception ex4)
			{
				if (HardThreadGuardianLaunchBlockedException.Contains(ex4))
				{
					criticalGuardianLaunchBlocked = true;
					bool withdrawCriticalThreadsLockedSucceeded = WithdrawCriticalThreadsLocked("independent guardian launch unavailable");
					criticalObservation.Reset();
					ResetCriticalAuto();
					if (criticalSource != null && withdrawCriticalThreadsLockedSucceeded)
					{
						PublishCriticalThreadState(criticalSource, "unsupported", CriticalGuardianLaunchBlockedDetail(), false, false, 0, 0L);
					}
					Logger.RepeatedError("critical-thread-guardian-launch", "Hard thread affinity: " + ex4.Message, -1L);
				}
				else
				{
					bool withdrawCriticalThreadsLockedSucceeded2 = WithdrawCriticalThreadsLocked(ex4.GetType().Name + ": " + ex4.Message);
					if (criticalSource != null && withdrawCriticalThreadsLockedSucceeded2)
					{
						PublishCriticalThreadState(criticalSource, "unsupported", PerformancePolicy.Text("本次未应用：", "Not applied: ") + ex4.Message, false, false, 0, 0L);
					}
					Logger.RepeatedError("critical-thread-runtime", "Hard thread affinity: " + ex4.GetType().Name + ": " + ex4.Message, -1L);
					criticalNextSample = AutoCores.NowTicks + 300000000;
				}
			}
			finally
			{
				SettleCriticalRoles();
				Monitor.Exit(criticalProcessGate);
			}
		}
		private static string CriticalGuardianLaunchBlockedDetail()
		{
			return PerformancePolicy.Text("Pavise 被启动它的程序放进了禁止脱离的作业（例如计划任务），独立恢复器无法启动；本次运行停止重试。退出 Pavise 后从桌面或开始菜单直接打开即可恢复", "Pavise was started inside a job that forbids breakaway (for example by Task Scheduler), so the independent recovery helper cannot start; retries are stopped for this run. Exit Pavise and open it directly from the desktop or Start menu to restore it.");
		}
		private void BeginCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
		{
			CriticalThreadRoleFact[] roles = FrameCoordination.ReadCriticalRoles(source.Pid, source.Birth, snapshot, AutoCores.NowTicks);
			HardThreadChoice[] selectResult = CriticalThreadRuntimePolicy.Select(source.Rule, criticalObservation.Candidates, roles);
			if (selectResult.Length != source.Rule.CandidateCount)
			{
				PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("确认持续高负载线程后再绑定", "Binding waits for sustained CPU hotspot observations"), false, false, 0, 0L);
			}
			else
			{
				BindCriticalThreads(source, snapshot, source.Rule, selectResult, roles);
			}
		}
		private void BindCriticalThreads(CriticalRuntimeSource source, ThreadTimes snapshot, CriticalThreadRule rule, HardThreadChoice[] choices, CriticalThreadRoleFact[] roles)
		{
			string launchKey = "critical-launch:" + source.Serial + ":" + source.Profile + ":" + source.Pid + ":" + source.Birth + ":" + source.Rule.Encode();
			if (!AutomaticWorkAllowed(launchKey)) return;
			if (criticalSession == null && criticalPriorityReservation != null && !RestoreCriticalThreadsLocked()) return;
			criticalFailureTarget = source.Profile + "|" + source.Path;
			ActionFailureStore.BeginAutomaticCycle("game-critical-thread-hard-affinity", criticalFailureTarget,
				source.Serial + "\n" + source.Profile + ":" + source.Pid + ":" + source.Birth + ":" + source.Rule.Encode());
			if (ActionFailureStore.IsBlocked("game-critical-thread-hard-affinity", criticalFailureTarget))
			{
				PublishCriticalThreadState(source, "conflict", ActionFailureStore.FailureReason("game-critical-thread-hard-affinity", criticalFailureTarget), false, false, 0, 0L);
				return;
			}
			IntPtr intPtr = Native.OpenProcess(4096, false, source.Pid);
			if (intPtr == IntPtr.Zero)
			{
				RecordAutomaticWorkFailure(launchKey, "process-open-failed");
				PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("无法核验该游戏的进程权限与身份", "The game's process identity and permissions cannot be verified"), false, false, 0, 0L);
				return;
			}
			bool launchAttempted = false;
			try
			{
				ulong mask;
				if (!Native.TryQueryAffinity(intPtr, out mask))
				{
					RecordAutomaticWorkFailure(launchKey, "affinity-read-failed");
					PublishCriticalThreadState(source, "unsupported", PerformancePolicy.Text("无法读取游戏进程的处理器亲和性；未施加线程硬绑定", "The game's processor affinity cannot be read; no hard thread binding is applied"), false, false, 0, 0L);
					return;
				}
				BoostPass boostPass = new BoostPass();
				boostPass.RendererPid = source.Pid;
				boostPass.RendererCreation = source.Birth;
				boostPass.RendererName = source.Name;
				BoostPass pass = boostPass;
				CriticalThreadTargetLease criticalThreadTargetLease = CreateCriticalThreadTarget(pass, intPtr, rule, source.Profile, source.RuleText, mask, source.Epoch, source.Serial);
				if (criticalThreadTargetLease == null)
				{
					PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("绑定前核对游戏身份与规则未通过；本次未绑定，稍后重新核对", "Game identity and rule checks before binding did not pass; nothing was bound and they will be checked again"), false, false, 0, 0L);
					return;
				}
				string priorityReason;
				if (!TryReserveCriticalThreadPriority(source.Pid, source.Birth, choices.Select(x => x.Thread).ToArray(), out priorityReason))
				{
					criticalOriginalTarget.Revoke();
					RestoreCriticalThreadsLocked();
					PublishCriticalThreadState(source, "recovery-pending", priorityReason, false, true, 0, 0L);
					return;
				}
				launchAttempted = true;
				criticalSession = CriticalThreadHardAffinitySession.OpenNative(criticalThreadTargetLease);
				criticalRestorationConfirmed = false;
				criticalThreadResidue = true;
				criticalGuardianActor = criticalSession.GuardianActor;
				if (!ReleaseCriticalGuardianBackground())
				{
					throw new InvalidOperationException("Guardian background restoration is unconfirmed");
				}
				criticalPlan = criticalSession.CreatePlan(choices, source.Rule.Automatic && source.Rule.WorkerAvoidance);
				criticalChoiceLabels = choices.Select(delegate(HardThreadChoice choice)
				{
					CriticalThreadRoleFact criticalThreadRoleFact = roles.FirstOrDefault((CriticalThreadRoleFact x) => x.Tid == choice.Thread.Tid && x.Creation == choice.Thread.Creation);
					return "TID " + choice.Thread.Tid + ((criticalThreadRoleFact == null) ? PerformancePolicy.Text(" · CPU 热点", " · CPU hotspot") : ((criticalThreadRoleFact.Role == "presentation-associated") ? PerformancePolicy.Text(" · 呈现关联", " · Presentation-associated") : PerformancePolicy.Text(" · 依赖关联", " · Dependency-associated")));
				}).ToArray();
				criticalChoiceFrameAssociated = CriticalThreadBoostCouplingPolicy.FrameAssociated(choices, roles);
				criticalWorkerBudgetReached = false;
				ThreadTimes admitted = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 0);
				if (!CriticalThreadObservation.Valid(admitted, source.Birth, AutoCores.NowTicks) || choices.Any((HardThreadChoice x) => !CriticalThreadRuntimePolicy.Contains(admitted, x.Thread)))
				{
					RestoreCriticalThreadsLocked();
					criticalObservation.Reset();
					LogCriticalThreadState("observing", "the snapshot after the guardian handshake was stale or lost a candidate");
				}
				else
				{
					criticalThreadTargetLease.RequireActive();
					ApplyCriticalThreadBatch(source, admitted, true);
				}
			}
			catch (Exception ex)
			{
				if (launchAttempted) RecordAutomaticWorkFailure(launchKey, ex.GetType().Name + ": " + ex.Message);
				throw;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}
		private void AuditCriticalThreadSession(CriticalRuntimeSource source, ThreadTimes snapshot)
		{
			if (criticalPlan == null || criticalPlan.Choices.Any((HardThreadChoice x) => !CriticalThreadRuntimePolicy.Contains(snapshot, x.Thread)))
			{
				if (WithdrawCriticalThreadsLocked((criticalPlan == null) ? "no binding plan" : "a candidate thread is missing from the sample"))
				{
					criticalObservation.Reset();
					PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("原候选线程已退出；恢复后重新观察", "An original candidate exited; observing again after restoration"), false, false, 0, 0L);
				}
			}
			else
			{
				ApplyCriticalThreadBatch(source, snapshot, false);
			}
		}
		private void AuditAutomaticCriticalThread(CriticalRuntimeSource source)
		{
			long started = Stopwatch.GetTimestamp();
			string detail;
			HardThreadCandidateAudit result = criticalSession.AuditCandidates(out detail);
			Volatile.Write(ref criticalLastAuditDetail, result + ": " + detail);
			TraceCriticalRuntime("candidate-audit", result.ToString(), source.Pid, started);
			if (result == HardThreadCandidateAudit.Verified)
			{
				criticalObservationMisses = 0;
				string hintChanges = criticalIdealHints.Audit();
				if (hintChanges.Length != 0)
				{
					Logger.Info("THREAD soft placement audit: " + source.Name + " " + hintChanges + " remaining=" + criticalIdealHints.Count);
				}
				PublishCriticalThreadState(source, "active", "", true, false, 0, AutoCores.NowTicks, CriticalThreadCoverageState.CandidatesOnly);
				return;
			}
			if (result == HardThreadCandidateAudit.Unavailable && ++criticalObservationMisses < 3)
			{
				PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("原候选暂不可核验，保留已有绑定并暂停写入：", "The original candidate is temporarily unverifiable; the existing binding is retained and writes are paused: ") + detail, false, false, 0, 0L);
				return;
			}
			if (result == HardThreadCandidateAudit.Gone)
			{
				if (!WithdrawCriticalThreadsLocked("original candidate exited: " + detail)) return;
				bool blocked = criticalStability.RecordRediscovery(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				criticalObservation.Reset();
				PublishCriticalThreadState(source, blocked ? "conflict" : "observing", blocked
					? PerformancePolicy.Text("候选线程反复更换，本局停止重新绑定", "Candidate threads changed repeatedly; rebinding is stopped for this session")
					: PerformancePolicy.Text("原候选已确认退出，恢复后重新观察", "The original candidate has exited; observing again after restoration"), false, false, 0, 0L);
				return;
			}
			criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
			if (WithdrawCriticalThreadsLocked("automatic thread ownership yielded: " + detail))
				PublishCriticalThreadState(source, "conflict", result == HardThreadCandidateAudit.Changed
					? PerformancePolicy.Text("游戏已重新指定线程分配，已让出绑定，本局不再改回", "The game changed thread placement; the binding has been yielded and will not be reasserted this session")
					: PerformancePolicy.Text("原候选连续无法核验，已恢复并停止本局重绑", "The original candidate could not be verified repeatedly; original settings were restored and rebinding is stopped for this session"), false, false, 0, 0L);
		}
		private void ApplyCriticalThreadBatch(CriticalRuntimeSource source, ThreadTimes snapshot, bool initial)
		{
			if (criticalStability == null) criticalStability = new CriticalThreadStabilityPolicy();
			criticalSession.BeforeReassertion = delegate
			{
				if (source.Rule.Automatic)
				{
					criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
					return false;
				}
				return !criticalStability.RecordReassertion(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
			};
			HardThreadOwnership[] activeReceipts = criticalSession.ActiveReceipts;
			HardThreadCoverage coverage;
			HardThreadIdentity[] batchResult = CriticalThreadRuntimePolicy.Batch(snapshot, criticalPlan, activeReceipts, out coverage);
			if (criticalWorkerBudgetReached)
			{
				batchResult = criticalPlan.Choices.Select((HardThreadChoice x) => x.Thread).ToArray();
				coverage = HardThreadCoverage.Partial;
			}
			HashSet<string> owned = new HashSet<string>(activeReceipts.Select((HardThreadOwnership x) => x.Thread.Key), StringComparer.Ordinal);
			if (batchResult.Any((HardThreadIdentity x) => !owned.Contains(x.Key)) && !criticalFailureAdmitted)
			{
				string reason;
				if (!ActionFailureStore.TryBeginApply("game-critical-thread-hard-affinity", criticalFailureTarget, out reason))
				{
					RestoreCriticalThreadsLocked();
					PublishCriticalThreadState(source, "conflict", reason, false, false, 0, 0L);
					return;
				}
				criticalFailureAdmitted = true;
			}
			long nowTicks = AutoCores.NowTicks;
			long ticks = DateTime.UtcNow.Ticks;
			long batchStarted = Stopwatch.GetTimestamp();
			bool applySucceeded = (initial ? criticalSession.Apply(criticalPlan, batchResult, coverage) : criticalSession.AdmitNewThreads(batchResult, coverage));
			TraceCriticalRuntime("batch", applySucceeded ? "ok" : criticalSession.LastFailureKind.ToString(), source.Pid, batchStarted, criticalSession.LastOperationNativeAttempt);
			HardThreadFailureKind lastFailureKind = criticalSession.LastFailureKind;
			if (!applySucceeded && !criticalPlan.RequireWorkerCoverage && lastFailureKind == HardThreadFailureKind.InvalidRequest && !criticalSession.LastOperationWriteIntent && !criticalSession.LastOperationNativeAttempt && !initial)
			{
				criticalWorkerBudgetReached = true;
				PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("保留原候选绑定；本局不再增加工作线程", "Original candidate bindings retained; no more worker bindings this session"), false, false, 0, 0L);
				return;
			}
			if (!applySucceeded)
			{
				string detail = criticalSession.Detail;
				if (criticalPlan.RequireWorkerCoverage)
					detail += " [snapshotThreads=" + snapshot.Ids.Length + " batchThreads=" + batchResult.Length
						+ " requestedCoverage=" + coverage + " ownedBefore=" + activeReceipts.Length
						+ " unknownBirth=" + snapshot.Created.Count(birth => birth <= 0) + "]";
				if (criticalPlan.RequireWorkerCoverage)
					criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				if (initial && source.Rule.Automatic && lastFailureKind == HardThreadFailureKind.ObservationUnavailable)
					criticalStability.RecordRediscovery(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				if (lastFailureKind == HardThreadFailureKind.WriteOrDurabilityFault)
				{
					ActionFailureStore.Trip("game-critical-thread-hard-affinity", criticalFailureTarget, detail);
					criticalFailureAdmitted = false;
				}
				if (WithdrawCriticalThreadsLocked(string.Concat(initial ? "apply failed " : "worker admission failed ", lastFailureKind, ": ", detail)))
				{
					criticalObservation.Reset();
					criticalNextSample = AutoCores.NowTicks + 100000000;
					PublishCriticalThreadState(source, (lastFailureKind == HardThreadFailureKind.WriteOrDurabilityFault) ? "conflict" : "observing", detail, false, false, 0, 0L);
				}
				return;
			}
			string lastReassertion = (initial ? null : criticalSession.LastReassertion);
			if (lastReassertion != null)
			{
				int criticalReassertionsSnapshot = ++criticalReassertions;
				if (criticalReassertionsSnapshot <= 3 || criticalReassertionsSnapshot % 20 == 0)
				{
					LogCriticalThreadState("reasserted", lastReassertion + " (this session: " + criticalReassertionsSnapshot + ")");
				}
			}
			criticalOriginalTarget.RequireActive();
			int candidates;
			int workers;
			bool complete;
			ThreadTimes verifiedSnapshot = criticalPlan.RequireWorkerCoverage
				? ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 0) : snapshot;
			if (!CriticalThreadObservation.Valid(verifiedSnapshot, source.Birth, AutoCores.NowTicks))
			{
				verifiedSnapshot = ProcessSnapshotSource.SampleThreads(source.Pid, source.Birth, 1500);
				if (!CriticalThreadObservation.Valid(verifiedSnapshot, source.Birth, AutoCores.NowTicks))
				{
					if (++criticalObservationMisses >= 3)
					{
						criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
						if (WithdrawCriticalThreadsLocked("fresh thread verification repeatedly unavailable"))
							PublishCriticalThreadState(source, "conflict", PerformancePolicy.Text("线程状态连续无法核验，已恢复原设置并停止本局重绑", "Thread state could not be verified repeatedly; original settings were restored and rebinding is stopped for this session"), false, false, 0, 0L);
						return;
					}
					PublishCriticalThreadState(source, "coverage-pending", PerformancePolicy.Text("写入已回读，等待新的线程快照；暂停新增与纠偏", "Writes were read back; a fresh thread snapshot is pending and further bindings and corrections are paused"), false, false, 0, 0L);
					return;
				}
			}
			bool verifiedSucceeded = CriticalThreadRuntimePolicy.Verified(verifiedSnapshot, criticalPlan, criticalSession.ActiveReceipts, ticks, out candidates, out workers, out complete);
			if (!verifiedSucceeded)
			{
				if (criticalPlan.RequireWorkerCoverage)
					criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				if (WithdrawCriticalThreadsLocked("thread coverage unconfirmed (candidates=" + candidates + "/" + criticalPlan.Choices.Length
					+ " workers=" + workers + " snapshotThreads=" + verifiedSnapshot.Ids.Length + " complete=" + complete + ")"))
				{
					PublishCriticalThreadState(source, "observing", PerformancePolicy.Text("绑定回读或采样时效未确认，已撤回", "Readback or sample freshness was not confirmed; bindings were withdrawn"), false, false, 0, 0L);
				}
				return;
			}
			criticalObservationMisses = 0;
			if (criticalPlan.RequireWorkerCoverage && !EnsureCriticalCoreIsolation(source, criticalPlan))
			{
				string isolationFailure = criticalCoreIsolationError;
				criticalStability.Block(source.Profile, source.Pid, source.Birth, source.Serial, source.RuleText);
				if (WithdrawCriticalThreadsLocked("critical core isolation unavailable: " + isolationFailure))
					PublishCriticalThreadState(source, "conflict", PerformancePolicy.Text("核心隔离未确认，已撤回线程绑定：", "Core isolation was not confirmed; thread bindings were withdrawn: ") + isolationFailure, false, false, 0, 0L);
				return;
			}
			if (initial && criticalPlan.RequireWorkerCoverage)
				Logger.Info("THREAD exclusive verified: " + source.Name + " pid=" + source.Pid
					+ " physical=0x" + criticalCoreIsolationMask.ToString("X") + " workers=" + workers
					+ " workerMask=0x" + criticalPlan.Workers.ToString("X")
					+ " activationMs=" + ((Stopwatch.GetTimestamp() - batchStarted) * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture)
					+ " scope=snapshot; new threads await admission; interrupts are not isolated");
			if (criticalFailureAdmitted && (!source.Rule.WorkerAvoidance || complete))
			{
				if (!ActionFailureStore.CompleteApply("game-critical-thread-hard-affinity", criticalFailureTarget))
				{
					RestoreCriticalThreadsLocked();
					LogCriticalThreadState("withdrawn", "the apply completion could not be recorded");
					return;
				}
				criticalFailureAdmitted = false;
			}
			CriticalThreadCoverageState coverage2 = ((!source.Rule.WorkerAvoidance) ? CriticalThreadCoverageState.CandidatesOnly : (complete ? CriticalThreadCoverageState.WorkersSnapshotComplete : CriticalThreadCoverageState.WorkersPartial));
			PublishCriticalThreadState(source, (source.Rule.WorkerAvoidance && !complete) ? "coverage-pending" : "active", "", true, false, workers, nowTicks, coverage2);
		}
		private bool WithdrawCriticalThreadsLocked(string reason)
		{
			bool track = criticalSession != null || criticalPriorityReservation != null || criticalRestorationConfirmed;
			long started = Stopwatch.GetTimestamp();
			if (criticalSession != null && !criticalRestorationConfirmed)
			{
				LogCriticalThreadState("withdrawn", reason);
			}
			bool restored = RestoreCriticalThreadsLocked();
			if (track) TraceCriticalRuntime("restore", restored ? "ok" : "pending", criticalSource == null ? 0 : criticalSource.Pid, started);
			return restored;
		}
		private bool StopCriticalThreads(int timeoutMs, [CallerMemberName] string caller = null)
		{
			if (Monitor.IsEntered(sync))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			if (!Monitor.TryEnter(criticalProcessGate, Math.Max(0, timeoutMs)))
			{
				RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1));
				return false;
			}
			try
			{
				bool withdrawCriticalThreadsLockedSucceeded = WithdrawCriticalThreadsLocked("stopped by " + caller);
				if (withdrawCriticalThreadsLockedSucceeded)
				{
					criticalSource = null;
					criticalObservation.Reset();
				}
				ResetCriticalAuto();
				return withdrawCriticalThreadsLockedSucceeded;
			}
			finally
			{
				Monitor.Exit(criticalProcessGate);
			}
		}
		private bool PrepareCriticalThreadProcessMaskMutation(int pid, long birth)
		{
			if (!Monitor.IsEntered(criticalProcessGate))
			{
				throw new InvalidOperationException("Original placement gate required");
			}
			CriticalThreadTargetLease criticalThreadTargetLease = criticalOriginalTarget;
			if (criticalSession == null)
			{
				return !CriticalThreadHasRecoveryDebt(true);
			}
			if (criticalThreadTargetLease == null || criticalThreadTargetLease.ProcessId != pid || criticalThreadTargetLease.ProcessCreation != birth)
			{
				return true;
			}
			RevokeCriticalThreadIntent("PrepareCriticalThreadProcessMaskMutation");
			if (!RestoreCriticalThreadsLocked())
			{
				return false;
			}
			criticalSource = null;
			criticalObservation.Reset();
			criticalNextSample = 0L;
			return true;
		}
		private void PublishCriticalThreadState(CriticalRuntimeSource source, string state, string detail, bool applied = false, bool pending = false, int workers = 0, long observed = 0L, CriticalThreadCoverageState coverage = CriticalThreadCoverageState.None)
		{
			if (source == null)
			{
				return;
			}
			LogCriticalThreadState(state, detail);
			int sameTid;
			CriticalThreadBoostCoupling coupling = CriticalThreadCoupling(source.Pid, source.Birth, applied, out sameTid);
			CriticalThreadUiState view = new CriticalThreadUiState(source.Profile, state, detail, true, applied, pending, workers, observed, (criticalPlan == null) ? 0 : criticalPlan.Workers, (applied && criticalPlan != null) ? criticalPlan.Choices.Select((HardThreadChoice x) => x.Mask).ToArray() : null, applied ? criticalChoiceLabels : null, true, source.Rule.WorkerAvoidance, coverage, source.Token, source.Serial, source.Epoch, source.Pid, source.Birth, source.RuleText, coupling, sameTid);
			lock (sync)
			{
				if (pending || CriticalThreadSnapshotCurrent(view, source.Profile))
				{
					criticalThreadUi = view;
				}
			}
		}
		private void LogCriticalThreadState(string state, string detail)
		{
			if (state == "observing") return;
			string detailOffset = state + "|" + detail;
			long nowTicks = AutoCores.NowTicks;
			lock (criticalStateLogGate)
			{
				if (criticalStateLogged == null || criticalStateLogged.Count > 64)
				{
					criticalStateLogged = new Dictionary<string, long>(StringComparer.Ordinal);
				}
				long value;
				if (detailOffset == criticalStateLast || (criticalStateLogged.TryGetValue(detailOffset, out value) && nowTicks - value < 1200000000))
				{
					return;
				}
				criticalStateLogged[detailOffset] = nowTicks;
				criticalStateLast = detailOffset;
			}
			Logger.Info("THREAD [" + AutomationSession + "] result=" + state + (string.IsNullOrEmpty(detail) ? "" : (" detail=" + detail)));
		}
		private CriticalThreadBoostCoupling CriticalThreadCoupling(int pid, long birth, bool applied, out int sameTid)
		{
			PolicySnapshot policySnapshot = sessionPolicy;
			if (policySnapshot == null)
			{
				sameTid = 0;
				return CriticalThreadBoostCoupling.Unknown;
			}
			string value = policySnapshot.ValueOf("GmThreadBoostStrategyV1");
			string mode = ((!ThreadBoostSelection.IsOn(value)) ? "off" : (LaneFallbackFor(pid, birth) ? "candidate" : "frame"));
			int pid2;
			long processBirth;
			int tid;
			long threadBirth;
			CriticalThreadLaneThread lane = (RenderLane.TryGetAppliedThread(out pid2, out processBirth, out tid, out threadBirth) ? new CriticalThreadLaneThread(pid2, processBirth, tid, threadBirth) : null);
			HardThreadPlan hardThreadPlan = criticalPlan;
			return CriticalThreadBoostCouplingPolicy.Classify(policySnapshot.EffBoost, mode, LaneSupported(policySnapshot.Preset), pid, birth, lane, (applied && hardThreadPlan != null) ? hardThreadPlan.Choices.Select((HardThreadChoice x) => x.Thread).ToArray() : null, criticalChoiceFrameAssociated, out sameTid);
		}
		private bool IsCriticalThreadGuardian(int pid, long birth)
		{
			HardThreadGuardianActor hardThreadGuardianActor = criticalGuardianActor;
			if (hardThreadGuardianActor != null && hardThreadGuardianActor.Pid == pid && birth > 0)
			{
				return hardThreadGuardianActor.Creation == birth;
			}
			return false;
		}
		private bool LaneFallbackFor(int pid, long birth)
		{
			LaneFallbackTarget laneFallbackTarget = laneFallback;
			if (laneFallbackTarget != null && pid > 0 && laneFallbackTarget.Pid == pid)
			{
				return laneFallbackTarget.Birth == birth;
			}
			return false;
		}
		private void UpdateLaneFallback(int pid, long birth, string name)
		{
			bool matchesFrameCoordinationEnabled = FrameCoordinationEnabled && FrameCoordination.IdentificationExhausted(pid, birth, name);
			LaneFallbackTarget laneFallbackTarget = laneFallback;
			if (!matchesFrameCoordinationEnabled)
			{
				if (laneFallbackTarget != null)
				{
					laneFallback = null;
				}
			}
			else if (laneFallbackTarget == null || laneFallbackTarget.Pid != pid || laneFallbackTarget.Birth != birth)
			{
				laneFallback = new LaneFallbackTarget(pid, birth);
			}
		}
		internal string ThreadBoostExecutionState()
		{
			string automaticExecutionStateResult = AutomaticExecutionState("GmRenderLane");
			string automaticExecutionStateResult2 = AutomaticExecutionState("FrameCoordination");
			string[] textEntries = new string[2] { automaticExecutionStateResult, automaticExecutionStateResult2 };
			foreach (string entryText in textEntries)
			{
				switch (entryText)
				{
				case "failed":
				case "restore-failed":
				case "stopped":
				case "cooldown":
					return entryText;
				}
			}
			if (!AutomationPolicySnapshot.ThreadBoostOn)
			{
				return "off";
			}
			if (laneFallback == null)
			{
				return automaticExecutionStateResult2;
			}
			return automaticExecutionStateResult;
		}
		private bool AddGameExecutableCore(string name, string executablePath, string preferredRoot, out string error)
		{
			error = null;
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
				{
					return false;
				}
				List<GameProfile> next = GetProfiles();
				HashSet<string> nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				if (!StageGameExecutable(name, executablePath, preferredRoot, next, nextIgnore, out error) || !CommitLibraryLocked(next, nextIgnore))
				{
					return false;
				}
			}
			KickLibraryChanged();
			return true;
		}
		internal string SaveManualCorePlan(string profileId, string expected, ManualCorePlan plan)
		{
			string rawRule, enabled;
			lock (sync)
			{
				GameProfile current = FindProfileLocked(profileId);
				if (current == null || ManualCorePlan.Raw(current) != expected) return "schedule.manual.changed";
				rawRule = CriticalThreadRule.Raw(current);
				enabled = PolicyResolver.For(current).ValueOf("GmAutoCoresV2");
			}
			if (rawRule != null && CriticalThreadRule.Parse(rawRule) == null) return "schedule.manual.changed";
			return SaveCoreConfiguration(profileId, expected, rawRule, enabled, plan,
				plan != null && ManualCorePlan.Parse(expected) != null && enabled == "1", CriticalThreadRule.Parse(rawRule));
		}
		private static string ResourcePriorityKey(string action)
		{
			switch (action)
			{
			case "GamePriority":
				return "GmBoostCpuV1";
			case "GameMemoryPriority":
				return "GmBoostMemoryV1";
			case "GameIoPriority":
				return "GmBoostIoV1";
			case "GameGpuPriority":
				return "GmBoostGpuV1";
			default:
				throw new ArgumentException("Unknown resource priority", "action");
			}
		}
		private bool ApplySelectedBoostAction(IntPtr handle, int pid, BoostPass pass, string action, bool enabled, Func<long, bool> desired, Func<int> write)
		{
			string key = ResourcePriorityKey(action);
			HashSet<string> value;
			if (!enabled)
			{
				bool anyConditionMet;
				lock (sync)
				{
					HashSet<string> value2;
					anyConditionMet = (boostResourceSelections.TryGetValue(pid, out value) && value.Contains(action)) || (boostActionRestorePending.TryGetValue(pid, out value2) && value2.Contains(action));
				}
				bool matchesRestoreBoostAction = !anyConditionMet || RestoreBoostAction(handle, pid, pass.RendererCreation, action);
				if (matchesRestoreBoostAction)
				{
					lock (sync)
					{
						if (boostResourceSelections.TryGetValue(pid, out value))
						{
							value.Remove(action);
						}
					}
					if (!string.IsNullOrEmpty(pass.ActionTarget))
					{
						ActionFailureStore.CancelApply(action, pass.ActionTarget);
					}
				}
				RecordAutomation(key, (!matchesRestoreBoostAction) ? "restore-failed" : (anyConditionMet ? "restored" : "unchanged"));
				return matchesRestoreBoostAction;
			}
			bool applyIsolatedBoostActionSucceeded = ApplyIsolatedBoostAction(handle, pid, pass, action, desired, write);
			if (applyIsolatedBoostActionSucceeded)
			{
				lock (sync)
				{
					if (!boostResourceSelections.TryGetValue(pid, out value))
					{
						value = (boostResourceSelections[pid] = new HashSet<string>(StringComparer.Ordinal));
					}
					value.Add(action);
				}
			}
			RecordAutomation(key, applyIsolatedBoostActionSucceeded ? "applied" : (pass.WriteDenied ? "unsupported" : "unverified"));
			return applyIsolatedBoostActionSucceeded;
		}
		private static bool CpuTicks(int pid, out long ticks, out long creation)
		{
			ticks = 0L;
			creation = 0L;
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return false;
			}
			try
			{
				long exit;
				long kernel;
				long user;
				if (!GetProcessTimes(intPtr, out creation, out exit, out kernel, out user))
				{
					return false;
				}
				ticks = kernel + user;
				return true;
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}
		private static long CurrentProcessCpuTicks()
		{
			try
			{
				using (Process process = Process.GetCurrentProcess())
				{
					return process.TotalProcessorTime.Ticks;
				}
			}
			catch
			{
				return 0L;
			}
		}
		internal static double AverageCpuPercent(long cpuTicks, TimeSpan duration)
		{
			if (cpuTicks <= 0 || duration.Ticks <= 0)
			{
				return 0.0;
			}
			int processorCountLimit = Math.Max(1, Environment.ProcessorCount);
			double val = (double)cpuTicks * 100.0 / ((double)duration.Ticks * (double)processorCountLimit);
			return Math.Max(0.0, Math.Min(100.0, val));
		}
		internal static bool LaneSupported(PerformancePreset mode)
		{
			return !IsHandheld(mode);
		}
		internal static bool ReanchorToStickyInstance(GameDetection fresh, GameDetection sticky, IList<int> verifiedStickyPids)
		{
			if (fresh == null || sticky == null || sticky.RendererPid <= 0 || verifiedStickyPids == null)
			{
				return false;
			}
			bool reanchorToStickyInstanceReady = false;
			foreach (int verifiedStickyPid in verifiedStickyPids)
			{
				if (verifiedStickyPid == sticky.RendererPid)
				{
					reanchorToStickyInstanceReady = true;
					break;
				}
			}
			if (!reanchorToStickyInstanceReady)
			{
				return false;
			}
			fresh.RendererPid = sticky.RendererPid;
			fresh.RendererCreation = sticky.RendererCreation;
			fresh.RendererName = sticky.RendererName;
			fresh.RendererPath = sticky.RendererPath;
			fresh.RendererForeground = sticky.RendererForeground;
			fresh.RendererCandidateSelected = true;
			fresh.RendererUserSelected = sticky.RendererUserSelected;
			fresh.RendererLearnable = sticky.RendererLearnable;
			fresh.RequiresGpuConfirm = false;
			fresh.RendererSafetyOnly = false;
			fresh.RendererGpuProofExpiresMs = 0L;
			fresh.Evidence = sticky.Evidence;
			fresh.FamilyPids.Clear();
			foreach (int verifiedStickyPid2 in verifiedStickyPids)
			{
				if (verifiedStickyPid2 > 0)
				{
					fresh.FamilyPids.Add(verifiedStickyPid2);
				}
			}
			fresh.FamilyNames.Clear();
			foreach (string familyName in sticky.FamilyNames)
			{
				fresh.FamilyNames.Add(familyName);
			}
			return true;
		}
		private bool RememberSticky(GameDetection hit)
		{
			if (hit == null)
			{
				ClearSticky();
				return false;
			}
			Dictionary<int, GameId> dictionary = new Dictionary<int, GameId>();
			foreach (int familyPid in hit.FamilyPids)
			{
				string name;
				long creation;
				if (TryStickyIdentity(familyPid, out name, out creation) && (familyPid != hit.RendererPid || StickyIdentityMatches(hit, name, creation)))
				{
					dictionary[familyPid] = new GameId
					{
						Name = name,
						Creation = creation
					};
				}
			}
			string name2;
			long creation2;
			if (hit.RendererPid > 0 && !dictionary.ContainsKey(hit.RendererPid) && TryStickyIdentity(hit.RendererPid, out name2, out creation2) && StickyIdentityMatches(hit, name2, creation2))
			{
				dictionary[hit.RendererPid] = new GameId
				{
					Name = name2,
					Creation = creation2
				};
			}
			GameId value;
			if (hit.RendererCreation <= 0 && dictionary.TryGetValue(hit.RendererPid, out value))
			{
				hit.RendererCreation = value.Creation;
			}
			if (hit.RendererPid <= 0 || !dictionary.ContainsKey(hit.RendererPid))
			{
				return false;
			}
			stickyIds.Clear();
			foreach (KeyValuePair<int, GameId> item in dictionary)
			{
				stickyIds[item.Key] = item.Value;
			}
			stickyDetection = hit;
			return true;
		}
		private static bool StickyIdentityMatches(GameDetection hit, string name, long creation)
		{
			if (creation > 0 && (hit.RendererCreation <= 0 || hit.RendererCreation == creation))
			{
				return string.Equals(hit.RendererName, name, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
		private void ClearSticky()
		{
			stickyDetection = null;
			stickyIds.Clear();
			stickyMiss = 0;
			stickyGraceOnly = false;
		}
		private bool AnyStickyReused()
		{
			foreach (KeyValuePair<int, GameId> stickyId in stickyIds)
			{
				string name;
				long creation;
				if (TryStickyIdentity(stickyId.Key, out name, out creation) && (creation != stickyId.Value.Creation || !string.Equals(name, stickyId.Value.Name, StringComparison.OrdinalIgnoreCase)))
				{
					return true;
				}
			}
			return false;
		}
		private bool TryStickyIdentity(int pid, out string name, out long creation)
		{
			return TryIdentity(pid, out name, out creation);
		}
	}
}
