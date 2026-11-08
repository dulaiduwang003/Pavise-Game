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
		private void ApplyGameProcessActions(ProcessSnapshot all)
		{
			if (EffBoost)
			{
				corePlacementOnly = false;
				Boost(all);
				return;
			}
			if (!corePlacementOnly)
			{
				if (!UnboostGames())
				{
					return;
				}
				corePlacementOnly = true;
			}
			BoostPass pass = PrepareBoostPass();
			DropStaleBoosts(pass);
			RunIndependentCorePlacement(pass);
		}
		private bool SyncGuardScheduling()
		{
			lock (sync)
			{
				SyncAutomationPolicy();
				if (!enabled || stopping || ProfileStoreSaveFailed)
				{
					return SetGuardScheduling(false, false);
				}
				guardDvrActive = EnvStep("gamedvr", killGameDvr, guardDvrActive || (!killGameDvr && GameDvr.HasResidue), GameDvr.Activate, GameDvr.Restore);
				guardMmcssActive = EnvStep("mmcss", mmcssOn, guardMmcssActive || (!mmcssOn && Mmcss.HasResidue()), Mmcss.Activate, Mmcss.Restore);
				bool flag = guardDvrActive == killGameDvr && !envFused.Contains("gamedvr") && !envRestorePending.Contains("gamedvr");
				bool flag2 = guardMmcssActive == mmcssOn && !envFused.Contains("mmcss") && !envRestorePending.Contains("mmcss");
				if (flag)
				{
					RecordAutomation("gamedvr", killGameDvr ? "applied" : "restored");
				}
				if (flag2)
				{
					RecordAutomation("mmcss", mmcssOn ? "applied" : "restored");
				}
				return flag && flag2;
			}
		}
		private bool SetGuardScheduling(bool dvr, bool mmcss)
		{
			bool skipped; string reason;
			bool flag = dvr ? RunGuarded(GameDvr.Activate) : RunAutomaticRestoreCore("gamedvr", GameDvr.Restore, out skipped, out reason);
			bool flag2 = mmcss ? RunGuarded(Mmcss.Activate) : RunAutomaticRestoreCore("mmcss", Mmcss.Restore, out skipped, out reason);
			if (flag)
			{
				guardDvrActive = dvr;
				envRestorePending.Remove("gamedvr");
				envFused.Remove("gamedvr");
				envNextAttempt.Remove("gamedvr");
				envFailures.Remove("gamedvr");
			}
			if (flag2)
			{
				guardMmcssActive = mmcss;
				envRestorePending.Remove("mmcss");
				envFused.Remove("mmcss");
				envNextAttempt.Remove("mmcss");
				envFailures.Remove("mmcss");
			}
			RecordAutomation("gamedvr", (!flag) ? (dvr ? "failed" : "restore-failed") : (dvr ? "applied" : "restored"));
			RecordAutomation("mmcss", (!flag2) ? (mmcss ? "failed" : "restore-failed") : (mmcss ? "applied" : "restored"));
			if (flag)
			{
				return flag2;
			}
			return false;
		}
		private static bool RunGuarded(Func<bool> action)
		{
			try
			{
				return action != null && action();
			}
			catch
			{
				return false;
			}
		}
		private bool ProcessorTuningRequested(PolicySnapshot effective)
		{
			return effective != null && effective.ProcessorTuning;
		}
		private void NotifyExtensionSession(bool active)
		{
			GameProfile gameProfile = null;
			int rendererPid = 0;
			long rendererCreation = 0L;
			GameDetection renderer = null;
			if (active)
			{
				lock (sync)
				{
					if (this.active && activeDetection != null && activeDetection.RendererCandidateSelected
						&& !activeDetection.RendererSafetyOnly && !activeDetection.RequiresGpuConfirm)
					{
						renderer = RendererHandoffTracker.Copy(activeDetection);
					}
				}
			}
			if (renderer != null && GameExtension.Current != null
				&& GameExtension.Current.AppliesTo(renderer.Profile) && HasRendererObservation(renderer.Profile))
			{
				RendererObservationStore observations = rendererObservations;
				if (observations != null)
				{
					observations.Validate(renderer.Profile.Id, renderer.RendererPath);
					if (observations.Has(renderer.Profile.Id, renderer.RendererPath))
					{
						lock (sync)
						{
							if (this.active && !stopping && enabled && !panicReq && !ProfileStoreSaveFailed
								&& RendererHandoffTracker.SameIdentity(renderer, activeDetection)
								&& RendererHandoffTracker.SameProfile(renderer.Profile, activeDetection.Profile))
							{
								gameProfile = renderer.Profile;
								rendererPid = renderer.RendererPid;
								rendererCreation = renderer.RendererCreation;
							}
						}
					}
				}
			}
			GameExtension.NotifySession(gameProfile, rendererPid, rendererCreation, gameProfile != null);
		}
		private bool SessionServiceSuspended(string envKey, long nowTicks)
		{
			if (!smartSwitchedOut)
			{
				return false;
			}
			if (ForegroundConsumesService(switchedOutForegroundName, envKey))
			{
				return true;
			}
			return SwitchOutDebounceElapsed(nowTicks);
		}
		private bool UpdateSessionForeground(ProcessSnapshot all, int rendererPid, HashSet<int> gamePids)
		{
			int num = RendererForegroundPid();
			bool flag = num > 4;
			bool flag2 = flag && rendererPid > 0 && (num == rendererPid || (gamePids != null && gamePids.Contains(num)));
			long ticks = DateTime.UtcNow.Ticks;
			bool returning = sessionForeground.State == SessionForegroundState.SwitchedOut;
			bool flag3 = sessionForeground.Observe(flag2, flag, ticks);
			if (flag && !flag2)
			{
				switchedOutForegroundPid = num;
				ProcEntry procEntry = ((all == null) ? null : all.Find(num));
				switchedOutForegroundName = ((procEntry == null || string.IsNullOrEmpty(procEntry.Name)) ? "" : procEntry.Name);
			}
			bool flag4 = sessionForeground.State == SessionForegroundState.SwitchedOut;
			bool smart = flag4 && EffPreset == PerformancePreset.Standard;
			sessionSwitchedOut = flag4;
			smartSwitchedOut = smart;
			if (flag3)
			{
				if (flag4)
				{
					OnSessionSwitchedOut(smart);
				}
				else
				{
					OnSessionSwitchedBack(returning);
				}
			}
			return !flag4;
		}
		private void OnSessionSwitchedOut(bool smart)
		{
			Interlocked.Increment(ref optionalServiceGeneration);
			string text = " foregroundPid=" + switchedOutForegroundPid + " foregroundExe=" + switchedOutForegroundName;
			if (!smart)
			{
				Logger.Info("FOCUS [" + AutomationSession + "] event=switched-out scene=competitive backgroundPolicy=retain powerPolicy=retain affinityPolicy=retain" + text);
				return;
			}
			Logger.Info("FOCUS [" + AutomationSession + "] event=switched-out scene=smart backgroundPolicy=release powerPolicy=retain affinityPolicy=retain" + text);
			ResetAdaptiveGuard();
		}
		private void OnSessionSwitchedBack(bool returning)
		{
			Interlocked.Increment(ref optionalServiceGeneration);
			lock (sync)
			{
				firstSweep = true;
				if (returning && activeDetection != null)
				{
					gameBoostNextAudit.Remove(activeDetection.RendererPid);
				}
			}
			if (returning)
			{
				exclusiveCheckTicks = 0L;
				Interlocked.Exchange(ref criticalForegroundAuditPending, 1);
				Logger.Info("FOCUS [" + AutomationSession + "] event=switched-back scene=" + (EffPreset == PerformancePreset.Standard ? "smart" : "competitive"));
			}
			Interlocked.Exchange(ref urgentProcessScan, 1);
			try
			{
				kick.Set();
			}
			catch
			{
			}
		}
		private void ResetSessionForeground()
		{
			sessionForeground.Reset();
			Interlocked.Exchange(ref criticalForegroundAuditPending, 0);
			smartSwitchedOut = false;
			sessionSwitchedOut = false;
			switchedOutForegroundName = "";
			switchedOutForegroundPid = 0;
		}
		public int AddScannedGames(IList<ScanHit> hits, out string lastError)
		{
			lastError = null;
			int num = 0;
			if (hits == null)
			{
				return 0;
			}
			lock (sync)
			{
				if (stopping || ProfileStoreSaveFailed || !EnsureLibraryReadyLocked())
				{
					return 0;
				}
				List<GameProfile> next = GetProfiles();
				HashSet<string> nextIgnore = new HashSet<string>(autoAddIgnore, StringComparer.OrdinalIgnoreCase);
				foreach (ScanHit hit in hits)
				{
					if (hit != null && !string.IsNullOrEmpty(hit.Exe))
					{
						string error;
						if (StageGameExecutable(hit.Name, hit.Exe, hit.Root, next, nextIgnore, out error))
						{
							num++;
						}
						else if (!string.IsNullOrEmpty(error))
						{
							lastError = error;
						}
					}
				}
				if (num > 0 && !CommitLibraryLocked(next, nextIgnore))
				{
					return 0;
				}
			}
			if (num > 0)
			{
				KickLibraryChanged();
			}
			return num;
		}
		public void Start()
		{
			shutdownWritersStopped = false;
			worker = new Thread(Loop);
			worker.IsBackground = true;
			worker.Start();
		}
		public bool Stop()
		{
			shutdownWritersStopped = false;
			Stopwatch elapsed = Stopwatch.StartNew();
			stopping = true;
			InvalidatePowerPlanSession();
			SyncLaboratory();
			RevokeCriticalThreadIntent("Stop");
			SyncPerformanceAutomation();
			InvalidateCacheWarm();
			InvalidateStandbyCleanerWork();
			InvalidateEnglishInputWork();
			kick.Set();
			if (Monitor.IsEntered(sync) || !Monitor.TryEnter(sync, 8000))
			{
				return false;
			}
			try
			{
				ClearFamilyDiscovery();
				InvalidateRendererHandoff();
			}
			finally
			{
				Monitor.Exit(sync);
			}
			Thread thread = worker;
			if (thread != null && (thread == Thread.CurrentThread || !thread.Join(RemainingShutdownMs(elapsed, 8000))))
			{
				return false;
			}
			if (!DrainAsyncShutdown(RemainingShutdownMs(elapsed, 8000)))
			{
				return false;
			}
			bool manualRestored = false;
			try { manualRestored = LaboratoryManualActions.CloseAndRestoreRuntime(8000); }
			catch { }
			// A stopped writer may retain debt; only a running writer blocks unrelated cleanup.
			if (!manualRestored && LaboratoryManualActions.Busy) return false;
			bool flag = true;
			try { if (laboratory != null && !laboratory.Close(8000)) flag = false; }
			catch { flag = false; }
			try
			{
				if (!FrameCoordination.Close(8000))
				{
					flag = false;
				}
			}
			catch
			{
				flag = false;
			}
			try
			{
				if (!RenderLane.CloseForShutdown(8000))
				{
					flag = false;
				}
			}
			catch
			{
				flag = false;
			}
			try
			{
				if (standbyCleaner != null && !standbyCleaner.Close(8000))
				{
					flag = false;
				}
			}
			catch
			{
				flag = false;
			}
			if (!flag)
			{
				return false;
			}
			if (!performanceActions.Close(8000))
			{
				return false;
			}
			bool flag2 = StopGameNvDelivery(RemainingShutdownMs(elapsed, 8000));
			bool flag3 = DrainHistoricalCriticalThreads(RemainingShutdownMs(elapsed, 8000));
			bool flag4 = StopCriticalThreads(RemainingShutdownMs(elapsed, 8000), "Stop");
			if (!flag2 || !flag3 || !flag4)
			{
				return false;
			}
			bool recovered = FinishShutdownRecovery(core.RestoreAutomaticLayouts,
				() => { bool isolationClean = StopCoreIsolation(); bool backgroundClean = core.StopBackgroundAffinityJobs(); return coreAffinityJobs.Stop() && backgroundClean && isolationClean; }, RestoreAllHardPins, RestoreGuardScheduling);
			SelfYield.Release();
			return recovered && manualRestored && !SelfYield.HasResidue;
		}
		private bool DrainAsyncShutdown(int timeoutMs)
		{
			if (!stopping || timeoutMs < 0)
			{
				return false;
			}
			Stopwatch elapsed = Stopwatch.StartNew();
			if (cacheWarm != null && !cacheWarm.Drain(RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainShutdownGate(driverStageGate, RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainLaptopPerformanceWork(RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainShutdownGate(powerApplyGate, RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainShutdownGate(whiteEvalSync, RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (!DrainEnglishInput(RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			if (standbyCleaner != null && !standbyCleaner.Close(RemainingShutdownMs(elapsed, timeoutMs)))
			{
				return false;
			}
			RendererObservationStore rendererObservationStore = rendererObservations;
			if (rendererObservationStore != null)
			{
				return rendererObservationStore.Close(RemainingShutdownMs(elapsed, timeoutMs));
			}
			return true;
		}
		private static int RemainingShutdownMs(Stopwatch elapsed, int timeoutMs)
		{
			return (int)Math.Max(0L, timeoutMs - elapsed.ElapsedMilliseconds);
		}
		private static bool DrainShutdownGate(object gate, int timeoutMs)
		{
			if (Monitor.IsEntered(gate) || !Monitor.TryEnter(gate, timeoutMs))
			{
				return false;
			}
			try
			{
				return true;
			}
			finally
			{
				Monitor.Exit(gate);
			}
		}
		public void Poke()
		{
			RequestPolicyApply("Poke");
		}
		private void Loop()
		{
			while (!stopping)
			{
				if (manualLaboratoryMutation) { kick.WaitOne(100); continue; }
				IDisposable disposable = PolicyResolver.BeginReadScope();
				try
				{
					RetryPendingHardPinRestores();
					ApplyEnvironmentTimerOptions();
					if (panicReq)
					{
						int value = Volatile.Read(ref panicSeq);
						panicReq = false;
						panicResult = Deactivate(Lang.T("t.gamemode.42"));
						Volatile.Write(ref panicServed, value);
						panicDone.Set();
						kick.WaitOne(4000);
						continue;
					}
					PrivacyControl.TryRestoreRetired(() => !active && !stopping && !panicReq);
					SyncPerformanceAutomation();
					SyncAutomaticFullscreenOptimizations(false, -1L);
					SyncGameNvDelivery();
					MaybeRecoverHistoricalCriticalThreads();
					if (!enabled || ProfileStoreSaveFailed)
					{
						InvalidateRendererHandoff();
						bool flag;
						lock (sync)
						{
							flag = active || gameBoost.Count > 0;
						}
						if (flag || EnvActive() || core.AnyWith(SuppressReason.Background))
						{
							RetryDeactivate(Lang.T("t.gamemode.43"));
						}
						goto RecordHealthyLoop;
					}
					bool fallbackOnly;
					if (!ShouldRunProcessScan(out fallbackOnly))
					{
						kick.WaitOne(LoopWaitMs(ProcessScanWaitMs()));
						continue;
					}
					ProcessSnapshot processSnapshot = null;
					CountProcessScan();
					int maxAgeMs = ((fallbackOnly && ProcessEventsAvailable && IsActive) ? 5000 : 0);
					try
					{
						processSnapshot = ProcessSnapshotSource.Capture(selfSession, maxAgeMs);
					}
					catch
					{
						processSnapshot = null;
					}
					if (processSnapshot == null)
					{
						RequeueProcessScanAfterFailure();
						goto RecordHealthyLoop;
					}
					PruneWhitelistFamilyMembersIfDue(processSnapshot);
					antiCheatProcessSnapshot = processSnapshot;
					HashSet<int> gamePids;
					string text = FindRunningGame(processSnapshot, out gamePids);
					if (!enabled || stopping || panicReq || ProfileStoreSaveFailed)
					{
						continue;
					}
					if (text != null)
					{
						string detectedProfileId;
						lock (sync)
						{
							detectedProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
						}
						if (gameGoneSinceTicks != 0)
						{
							gameGoneSinceTicks = 0L;
							if (active)
							{
								Logger.Log(Lang.T("log.gamemode.44"));
								lock (sync)
								{
									firstSweep = true;
								}
							}
						}
						if (!active)
						{
							lock (sync)
							{
								if (manualLaboratoryMutation) continue;
								active = true;
								activeGame = text;
								firstSweep = true;
							}
							Logger.Log(Lang.T("log.gamemode.45") + text);
							ResetAdaptiveGuard();
							Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
							BeginSessionPolicy();
							ReportBegin(text);
							NotifyExtensionSession(true);
							slowEnvAtTicks = DateTime.UtcNow.AddSeconds(20.0).Ticks;
						}
						else if (!SameReportedProfile(repProfileId, detectedProfileId))
						{
							lock (sync)
							{
								activeGame = text;
							}
							Logger.Log(Lang.T("log.gamemode.46") + text);
							ResetAdaptiveGuard();
							Interlocked.Exchange(ref boostFirstStampTicks, DateTime.UtcNow.Ticks);
							ReportFinish();
							BeginSessionPolicy();
							ReportBegin(text);
							NotifyExtensionSession(true);
						}
						else if (!string.Equals(activeGame, text, StringComparison.Ordinal))
						{
							lock (sync)
							{
								activeGame = text;
							}
						}
						int num;
						long birth;
						lock (sync)
						{
							num = ((activeDetection != null) ? activeDetection.RendererPid : 0);
							birth = ((activeDetection != null) ? activeDetection.RendererCreation : 0);
						}
						UpdateSessionForeground(processSnapshot, num, gamePids);
						bool smartSceneSwitchedOut = SmartSceneSwitchedOut;
						StepEnglishInputSession();
						ApplyEnv();
						SyncPerformanceAutomation();
						ApplyStandbyCleanerPolicy();
						ApplyCacheWarmPolicy();
						RefreshAutoCoreMode();
						if (EffSuppress && !smartSceneSwitchedOut)
						{
							Sweep(processSnapshot, gamePids);
						}
						else
						{
							ReleaseBackground(smartSceneSwitchedOut ? SwitchOutReleaseText : Lang.T("t.gamemodeenv.41"));
						}
						SelfYield.TrackHelperRenderer(num, birth);
						SelfYield.Engage();
						ApplyGameProcessActions(processSnapshot);
						StepCriticalThreadRuntime();
						if (!smartSceneSwitchedOut)
						{
							StepAdaptiveGuard();
						}
					}
					else if (active)
					{
						StepDisplayAwake(false);
						long ticks = DateTime.UtcNow.Ticks;
						if (gameGoneSinceTicks == 0)
						{
							gameGoneSinceTicks = ticks;
							InvalidatePowerPlanSession();
							RevokeCriticalThreadIntent("session-unconfirmed/exit-grace");
							StopCriticalThreads(0, "session-unconfirmed/exit-grace");
							InvalidateStandbyCleanerWork();
							InvalidateCacheWarm();
							InvalidateEnglishInputWork();
							Logger.Log(Lang.T("log.gamemode.47") + ExitGraceSeconds + Lang.T("log.gamemode.48"));
							gracePreReleased += ReleaseBackground(Lang.T("t.gamemode.49"));
						}
						if (ExitGraceElapsed(gameGoneSinceTicks, ticks, ExitGraceSeconds))
						{
							Deactivate(Lang.T("t.gamemode.50"));
						}
						if (gameGoneSinceTicks != 0)
						{
							Interlocked.Exchange(ref transitionScanPending, 1);
						}
					}
					else
					{
						DropVanishedBoosts();
						bool flag2;
						lock (sync)
						{
							flag2 = gameBoost.Count > 0;
						}
						if (flag2 || EnvActive() || core.AnyWith(SuppressReason.Background))
						{
							RetryDeactivate(Lang.T("t.gamemode.51"));
						}
						TryAutoAddForegroundGame();
					}
					goto RecordHealthyLoop;
					RecordHealthyLoop:
					Logger.RepeatedRecovered("GameMode");
					goto WaitForNextScan;
				}
				catch (Exception ex)
				{
					RevokeCriticalThreadIntent("loop-exception");
					StopCriticalThreads(0, "loop-exception");
					Logger.RepeatedError("GameMode", Lang.T("log.gamemode.52") + ex.GetType().Name + ": " + ex.Message, -1L);
					goto WaitForNextScan;
				}
				finally
				{
					disposable.Dispose();
				}
				WaitForNextScan:
				kick.WaitOne(LoopWaitMs(enabled ? ProcessScanWaitMs() : 4000));
			}
			bool flag3;
			lock (sync)
			{
				flag3 = active || gameBoost.Count > 0;
			}
			bool flag4 = true;
			if (flag3 || core.AnyWith(SuppressReason.Background) || EnvActive())
			{
				flag4 = Deactivate(Lang.T("t.gamemode.53"));
			}
			if (panicReq)
			{
				int value2 = Volatile.Read(ref panicSeq);
				panicReq = false;
				panicResult = flag4;
				Volatile.Write(ref panicServed, value2);
				panicDone.Set();
			}
			try
			{
				RestoreGuardScheduling();
			}
			catch
			{
			}
		}
		public bool NeedsGameProcessIdentity(string name, int session)
		{
			if (session != selfSession || string.IsNullOrEmpty(name))
			{
				return false;
			}
			lock (sync)
			{
				foreach (GameProfile profile in profiles)
				{
					if (GameSessionDetector.IsProfileEntryName(profile, name))
					{
						return true;
					}
				}
			}
			return false;
		}
		private void CountProcessScan()
		{
			Interlocked.Increment(ref processScanCount);
		}
		public void NotifyProcessChanges(ProcessChangeBatch batch)
		{
			if (batch == null || stopping)
			{
				return;
			}
			ObserveWhitelistProcessChanges(batch);
			ObserveGameFamilyChanges(batch);
			bool flag = batch.Overflowed;
			bool gameSessionBoundary = false;
			bool flag2 = false;
			lock (sync)
			{
				flag |= active || armedAwaitingElection;
				ProcessChange[] changes = batch.Changes;
				foreach (ProcessChange processChange in changes)
				{
					if (processChange == null)
					{
						continue;
					}
					if (processChange.Kind == ProcessChangeKind.Stopped)
					{
						if (activeDetection != null && activeDetection.RendererPid == processChange.Pid)
						{
							gameSessionBoundary = true;
						}
						continue;
					}
					if (IsActiveFamilyChildStart(activeDetection, processChange, selfSession))
					{
						flag = true;
						long rendererCreation = activeDetection.RendererCreation;
						if (!IsSameTransitionEpoch(transitionProbeRendererPid, transitionProbeRendererCreation, activeDetection.RendererPid, rendererCreation))
						{
							transitionProbeRendererPid = activeDetection.RendererPid;
							transitionProbeRendererCreation = rendererCreation;
							flag2 = true;
						}
					}
					if (string.IsNullOrEmpty(processChange.Name))
					{
						continue;
					}
					foreach (GameProfile profile in profiles)
					{
						if (GameSessionDetector.IsProfileEntryProcess(profile, processChange.Name, processChange.Path))
						{

							flag = true;
							gameSessionBoundary = true;
							break;
						}
					}
				}
			}
			if (batch.Overflowed)
			{
				Interlocked.Exchange(ref gameDetectionDirty, 1);
			}
			if (flag2)
			{
				Interlocked.Exchange(ref gameDetectionDirty, 1);
				Interlocked.Exchange(ref transitionScanPending, 1);
			}
			if (ProcessEventNeedsImmediateScan(gameSessionBoundary))
			{
				Interlocked.Exchange(ref gameDetectionDirty, 1);
				Interlocked.Exchange(ref urgentProcessScan, 1);
			}
			if (flag)
			{
				Interlocked.Exchange(ref processSetDirty, 1);
				kick.Set();
			}
		}
		internal static bool IsSameTransitionEpoch(int storedPid, long storedCreation, int currentPid, long currentCreation)
		{
			if (storedPid <= 0 || storedPid != currentPid)
			{
				return false;
			}
			if (storedCreation > 0 && currentCreation > 0)
			{
				return storedCreation == currentCreation;
			}
			return true;
		}
		internal static bool ShouldRearmLauncherTransition(GameDetection previous, GameDetection next)
		{
			if (next != null)
			{
				if (previous != null)
				{
					return !RendererHandoffTracker.SameIdentity(previous, next);
				}
				return true;
			}
			return false;
		}
		internal static bool ProcessEventNeedsImmediateScan(bool gameSessionBoundary)
		{
			return gameSessionBoundary;
		}
		internal static int ProcessScanIntervalMs(bool eventsAvailable)
		{
			if (!eventsAvailable)
			{
				return 4000;
			}
			return 20000;
		}
		internal static int ProcessScanIntervalMs(bool eventsAvailable, bool gameActive)
		{
			if (gameActive)
			{
				if (!eventsAvailable)
				{
					return 500;
				}
				return 1000;
			}
			return ProcessScanIntervalMs(eventsAvailable);
		}
		internal static bool DirtyScanDue(bool eventsAvailable, bool dirty, long elapsedMs)
		{
			if (!eventsAvailable || !dirty)
			{
				return false;
			}
			if (elapsedMs >= 0)
			{
				return elapsedMs >= 500;
			}
			return true;
		}
		private bool ShouldRunProcessScan(out bool fallbackOnly)
		{
			fallbackOnly = false;
			long ticks = DateTime.UtcNow.Ticks;
			long num = Interlocked.Read(ref processScanRetryAfterTicks);
			if (num > ticks)
			{
				return false;
			}
			if (num > 0)
			{
				Interlocked.CompareExchange(ref processScanRetryAfterTicks, 0L, num);
			}
			long num2 = Interlocked.Read(ref lastProcessScanTicks);
			long num3 = ticks - num2;
			bool flag = Interlocked.Exchange(ref urgentProcessScan, 0) != 0;
			bool flag2 = Interlocked.Exchange(ref processSetDirty, 0) != 0;
			if (flag)
			{
				Interlocked.Exchange(ref transitionScanPending, 0);
				Interlocked.Exchange(ref lastProcessScanTicks, ticks);
				return true;
			}
			if (Interlocked.CompareExchange(ref transitionScanPending, 0, 0) != 0 && (num2 <= 0 || num3 < 0 || num3 >= 50000000))
			{
				Interlocked.Exchange(ref transitionScanPending, 0);
				Interlocked.Exchange(ref lastProcessScanTicks, ticks);
				return true;
			}
			if (DirtyScanDue(ProcessEventsAvailable, flag2, ElapsedMsOrMax(num2, num3)))
			{
				Interlocked.Exchange(ref transitionScanPending, 0);
				Interlocked.Exchange(ref lastProcessScanTicks, ticks);
				return true;
			}
			int num4 = ProcessScanIntervalMs(ProcessEventsAvailable, IsActive);
			if (armedAwaitingElection)
			{
				num4 = Math.Min(num4, 4000);
			}
			long num5 = (long)num4 * 10000L;
			if (num2 <= 0 || num3 < 0 || num3 >= num5)
			{
				fallbackOnly = !armedAwaitingElection;
				Interlocked.Exchange(ref transitionScanPending, 0);
				Interlocked.Exchange(ref lastProcessScanTicks, ticks);
				return true;
			}
			if (flag2)
			{
				Interlocked.Exchange(ref processSetDirty, 1);
			}
			return false;
		}
		private int ProcessScanWaitMs()
		{
			long ticks = DateTime.UtcNow.Ticks;
			long num = Interlocked.Read(ref processScanRetryAfterTicks);
			if (num > ticks)
			{
				return TicksToWaitMilliseconds(num - ticks);
			}
			if (Interlocked.CompareExchange(ref urgentProcessScan, 0, 0) != 0)
			{
				return 1;
			}
			int num2 = ProcessScanIntervalMs(ProcessEventsAvailable, IsActive);
			if (armedAwaitingElection)
			{
				num2 = Math.Min(num2, 4000);
			}
			long num3 = Interlocked.Read(ref lastProcessScanTicks);
			if (num3 <= 0 || ticks < num3)
			{
				return 1;
			}
			long num4 = (ticks - num3) / 10000;
			long num5 = num2 - num4;
			if (Interlocked.CompareExchange(ref transitionScanPending, 0, 0) != 0)
			{
				num5 = Math.Min(num5, 5000 - num4);
			}
			if (ProcessEventsAvailable && Interlocked.CompareExchange(ref processSetDirty, 0, 0) != 0)
			{
				num5 = Math.Min(num5, 500 - num4);
			}
			if (num5 <= 0)
			{
				return 1;
			}
			return (int)Math.Min(num2, num5);
		}
		private void RequeueProcessScanAfterFailure()
		{
			Interlocked.Exchange(ref urgentProcessScan, 1);
			Interlocked.Exchange(ref processSetDirty, 1);
			Interlocked.Exchange(ref processScanRetryAfterTicks, DateTime.UtcNow.AddMilliseconds(1000.0).Ticks);
		}
		private void RequestPolicyApply([CallerMemberName] string caller = null)
		{
			RevokeCriticalThreadIntent("RequestPolicyApply/" + caller);
			Interlocked.Exchange(ref corePolicyDirty, 1);
			Interlocked.Exchange(ref gameDetectionDirty, 1);
			Interlocked.Exchange(ref urgentProcessScan, 1);
			Interlocked.Exchange(ref processSetDirty, 1);
			try
			{
				kick.Set();
			}
			catch
			{
			}
		}
		private void BeginSessionPolicy()
		{
			powerSessionEnding = true;
			InvalidatePowerPlanSession();
			corePlacementStopReason = null;
			corePlacementOnly = false;
			lock (sync)
			{
				RememberModeSwitchLockLocked(activeDetection);
			}
			ResetSessionForeground();
			BeginRetrySession();
			BeginLaptopPerformanceSession();
			bool isolationClean = StopCoreIsolation();
			InvalidateCacheWarm();
			Interlocked.Increment(ref optionalServiceGeneration);
			InvalidateStandbyCleanerWork();
			GameProfile gameProfile;
			lock (sync)
			{
				gameProfile = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Clone() : null);
			}
			PolicySnapshot policySnapshot;
			lock (sync)
			{
				policySnapshot = sessionPolicy = ((gameProfile != null) ? PolicyResolver.For(gameProfile) : PolicyResolver.Global());
				CaptureSessionCorePreferencesLocked(gameProfile, policySnapshot);
			}
			SyncAutomationPolicy();
			BeginAutomationEvidence();
			BeginEnglishInputSession();
			if (!policySnapshot.IsGlobal)
			{
				Logger.Log(Lang.T("log.gamemode.34") + policySnapshot.ProfileName + Lang.T("log.gamemode.35") + policySnapshot.OverrideCount + Lang.T("log.gamemode.36"));
			}
			BeginAutoCores(policySnapshot, isolationClean);
			lock (sync)
			{
				lastPowerPolicyKey = -1;
				powerSessionEnding = false;
			}
		}
		private void ReportBegin(string game)
		{
			BeginCacheWarmSession();
			ResetBoostIdentity();
			long num = CurrentProcessCpuTicks();
			lock (sync)
			{
				repCpu.Clear();
				repCreation.Clear();
				repProc.Clear();
				repSealed.Clear();
				repGame = game;
				repStart = Stopwatch.GetTimestamp();
				repPaviseCpuStart = num;
				repProfileId = ((activeDetection != null && activeDetection.Profile != null) ? activeDetection.Profile.Id : null);
			}
		}
		private void ReportUntrack(int pid)
		{
			lock (sync)
			{
				repCpu.Remove(pid);
				repCreation.Remove(pid);
				repProc.Remove(pid);
				repSealed.Remove(pid);
			}
		}
		private void ReportSeal(int pid)
		{
			long value;
			long value2;
			lock (sync)
			{
				if (!repCpu.TryGetValue(pid, out value))
				{
					return;
				}
				repCreation.TryGetValue(pid, out value2);
				repCpu.Remove(pid);
				repCreation.Remove(pid);
			}
			long num = 0L;
			long ticks;
			long creation;
			if (CpuTicks(pid, out ticks, out creation) && creation == value2 && ticks > value)
			{
				num = ticks - value;
			}
			lock (sync)
			{
				long value3;
				repSealed.TryGetValue(pid, out value3);
				repSealed[pid] = value3 + num;
			}
		}
		private void ReportTrack(int pid, string name)
		{
			lock (sync)
			{
				if (repGame == null || repCpu.ContainsKey(pid))
				{
					return;
				}
			}
			long ticks;
			long creation;
			if (!CpuTicks(pid, out ticks, out creation))
			{
				return;
			}
			lock (sync)
			{
				if (!repCpu.ContainsKey(pid))
				{
					repCpu[pid] = ticks;
					repCreation[pid] = creation;
					repProc[pid] = name;
				}
			}
		}
		private void ReportFinish()
		{
			string text;
			long num;
			Dictionary<int, long> dictionary;
			Dictionary<int, string> dictionary2;
			Dictionary<int, long> dictionary3;
			Dictionary<int, long> dictionary4;
			long num2;
			lock (sync)
			{
				text = repGame;
				num = repStart;
				dictionary = new Dictionary<int, long>(repCpu);
				dictionary2 = new Dictionary<int, string>(repProc);
				dictionary3 = new Dictionary<int, long>(repCreation);
				dictionary4 = new Dictionary<int, long>(repSealed);
				num2 = repPaviseCpuStart;
				repCpu.Clear();
				repCreation.Clear();
				repProc.Clear();
				repSealed.Clear();
				repGame = null;
				repPaviseCpuStart = 0L;
				repProfileId = null;
			}
			if (text == null)
			{
				return;
			}
			TimeSpan timeSpan = TimeSpan.FromSeconds((double)(Stopwatch.GetTimestamp() - num) / (double)Stopwatch.Frequency);
			foreach (KeyValuePair<int, long> item in dictionary)
			{
				long value;
				if (!dictionary4.TryGetValue(item.Key, out value))
				{
					value = 0L;
					dictionary4[item.Key] = 0L;
				}
				long ticks;
				long creation;
				long value2;
				if (CpuTicks(item.Key, out ticks, out creation) && dictionary3.TryGetValue(item.Key, out value2) && creation == value2)
				{
					long num3 = ticks - item.Value;
					if (num3 >= 0)
					{
						dictionary4[item.Key] = value + num3;
					}
				}
			}
			long num4 = 0L;
			long num5 = 0L;
			string text2 = null;
			foreach (KeyValuePair<int, long> item2 in dictionary4)
			{
				num4 += item2.Value;
				if (item2.Value > num5)
				{
					num5 = item2.Value;
					string value3;
					if (dictionary2.TryGetValue(item2.Key, out value3))
					{
						text2 = value3;
					}
				}
			}
			string text3 = Lang.F("rep.done", text, FmtDur(timeSpan), dictionary4.Count, FmtCpu(num4));
			if (text2 != null && num5 >= 10000000)
			{
				text3 += Lang.F("rep.top", text2, FmtCpu(num5));
			}
			long num6 = CurrentProcessCpuTicks();
			long cpuTicks = ((num2 > 0 && num6 >= num2) ? (num6 - num2) : 0);
			double num7 = AverageCpuPercent(cpuTicks, timeSpan);
			text3 += Lang.F("rep.pavise.cpu", num7.ToString("0.00", CultureInfo.InvariantCulture));
			Logger.Log(Lang.T("log.gamemodesession.1") + text3);
			if (!(timeSpan.TotalSeconds >= 60.0))
			{
				return;
			}
			string text4 = Lang.F("rep.brief", text, FmtDur(timeSpan), dictionary4.Count);
			try
			{
				Settings.SaveStr("LastSessionBrief", text4);
			}
			catch
			{
			}
			Action<string> sessionBriefed = this.SessionBriefed;
			if (sessionBriefed != null)
			{
				try
				{
					sessionBriefed(text4);
				}
				catch
				{
				}
			}
			Action<string> sessionEnded = this.SessionEnded;
			if (sessionEnded == null)
			{
				return;
			}
			try
			{
				sessionEnded(text3);
			}
			catch
			{
			}
		}
		internal static bool IsExitGraceChoice(int seconds)
		{
			if (seconds != 0 && seconds != 2 && seconds != 5 && seconds != 8 && seconds != 15)
			{
				return seconds == 30;
			}
			return true;
		}
		public bool TrySetExitGraceSeconds(int seconds)
		{
			if (!IsExitGraceChoice(seconds))
			{
				return false;
			}
			lock (sync)
			{
				if (stopping || !Settings.SaveStr("GameExitGraceSeconds", seconds.ToString(CultureInfo.InvariantCulture)))
				{
					return false;
				}
				exitGraceSeconds = seconds;
			}
			KickGameDetectionNow();
			return true;
		}
		internal static bool ExitGraceElapsed(long since, long now, int seconds)
		{
			if (since > 0 && now >= since)
			{
				return now - since >= (long)seconds * 10000000L;
			}
			return false;
		}
		internal static bool IsTrustedPresetProcessPath(string name, string imagePath)
		{
			string text = WhitelistRule.NormalizeImagePath(imagePath);
			if (text.Length == 0)
			{
				return false;
			}
			string text2 = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
			if (string.IsNullOrEmpty(text2))
			{
				text2 = "C:\\Windows";
			}
			text2 = text2.TrimEnd('\\');
			if (string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase) && WhitelistRule.PathEquals(text, Path.Combine(text2, "explorer.exe")))
			{
				return true;
			}
			if (FamilyBoundary.UnderRoot(text, Path.Combine(text2, "System32")) || FamilyBoundary.UnderRoot(text, Path.Combine(text2, "SysWOW64")) || FamilyBoundary.UnderRoot(text, Path.Combine(text2, "SystemApps")))
			{
				return true;
			}
			if (string.Equals(name, "wslservice", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "vmmem", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "vmmemwsl", StringComparison.OrdinalIgnoreCase))
			{
				string folderPath = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				if (string.IsNullOrEmpty(folderPath))
				{
					return false;
				}
				if (FamilyBoundary.UnderRoot(text, Path.Combine(folderPath, "WindowsApps")))
				{
					return true;
				}
				if (string.Equals(name, "wslservice", StringComparison.OrdinalIgnoreCase))
				{
					return WhitelistRule.PathEquals(text, Path.Combine(folderPath, "WSL", "wslservice.exe"));
				}
				return false;
			}
			return false;
		}
		private static Dictionary<int, long> CaptureStoppedProcessIdentity(IEnumerable<int> pids)
		{
			Dictionary<int, long> dictionary = new Dictionary<int, long>();
			if (pids == null)
			{
				return dictionary;
			}
			foreach (int pid in pids)
			{
				IntPtr intPtr = Native.OpenProcess(4096, false, pid);
				if (intPtr == IntPtr.Zero)
				{
					if (Native.LastOpenProcessFailureWasNoSuchProcess())
					{
						dictionary[pid] = 0L;
					}
					continue;
				}
				try
				{
					long creation;
					long cpu;
					ulong io;
					if (Native.QueryProcessSample(intPtr, out creation, out cpu, out io) && creation > 0)
					{
						dictionary[pid] = creation;
					}
				}
				finally
				{
					Native.CloseHandle(intPtr);
				}
			}
			return dictionary;
		}
	}
}
