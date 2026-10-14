// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool UnboostGames()
        {
            return UnboostGames(0, 0, null);
        }

        private static bool IsKeptBoost(
            KeyValuePair<int, Snap> boosted, int keepPid, long keepCreation, string keepName)
        {
            return keepPid > 0
                && boosted.Key == keepPid
                && boosted.Value.Creation == keepCreation
                && string.Equals(boosted.Value.Name, keepName, StringComparison.OrdinalIgnoreCase);
        }

        private bool UnboostGames(int keepPid, long keepCreation, string keepName)
        {
            bool laneClean = RenderLane.ReleaseExcept(keepPid, keepCreation);
            laneClean &= FrameCoordination.ReleaseExcept(keepPid, keepCreation);
            bool threadsClean = keepPid == coreWatchPid && keepCreation == coreWatchCreation || StopCoreRefinement();
            bool isolationClean = keepPid > 0 || StopCoreIsolation();
            bool keepPin = coreEngaged && !corePinPending;
            bool pinsClean = RestoreHardPinsExcept(keepPin ? keepPid : 0, keepPin ? keepCreation : 0);
            corePinPending = !pinsClean;
            isolationClean &= threadsClean && laneClean && pinsClean;
            List<KeyValuePair<int, Snap>> boosts;
            Dictionary<int, int> gpus;
            lock (sync)
            {
                if (gameBoost.Count == 0 && gameGpu.Count == 0) return isolationClean;
                boosts = new List<KeyValuePair<int, Snap>>();
                foreach (KeyValuePair<int, Snap> boosted in gameBoost)
                    if (!IsKeptBoost(boosted, keepPid, keepCreation, keepName))
                        boosts.Add(boosted);
                gpus = new Dictionary<int, int>();
                foreach (KeyValuePair<int, int> gpu in gameGpu)
                    if (keepPid <= 0 || gpu.Key != keepPid)
                        gpus[gpu.Key] = gpu.Value;
                if (boosts.Count == 0 && gpus.Count == 0) return isolationClean;
                if (keepPid <= 0)
                {
                    boostFail.Clear(); boostDenied.Clear(); boostStateWarned.Clear();
                    boostStateVerified.Clear(); gameBoostNextAudit.Clear();
                    tweakApplied.Clear(); boostHandleStripped.Clear(); boostEcoGaveUp.Clear();
                    placementFail.Clear(); placementGaveUp.Clear(); boostStateFail.Clear();
                }
                else
                    foreach (KeyValuePair<int, Snap> stale in boosts)
                    {
                        boostFail.Remove(stale.Key); boostDenied.Remove(stale.Key);
                        boostStateWarned.Remove(stale.Key); boostStateVerified.Remove(stale.Key);
                        gameBoostNextAudit.Remove(stale.Key); tweakApplied.Remove(stale.Key);
                        boostHandleStripped.Remove(stale.Key); boostEcoGaveUp.Remove(stale.Key);
                        placementFail.Remove(stale.Key); placementGaveUp.Remove(stale.Key);
                        boostStateFail.Remove(stale.Key);
                    }
            }
            // 优先用原持有句柄恢复，避免反作弊拒绝新句柄后丢失恢复路径。
            foreach (var kv in boosts)
                RestoreHardPin(kv.Key);
            foreach (var kv in boosts)
            {
                int pid = kv.Key;
                bool done;
                IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero)
                {
                    IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                    if (hq == IntPtr.Zero)
                    {
                        done = Native.LastOpenProcessFailureWasNoSuchProcess();
                    }
                    else
                    {
                        try
                        {
                            string name = Native.ImageName(hq);
                            long creation, cpu; ulong disk;
                            bool sampled = Native.QueryProcessSample(
                                hq, out creation, out cpu, out disk);
                            bool identityKnown = name != null && sampled;
                            bool same = identityKnown
                                && string.Equals(
                                    name, kv.Value.Name,
                                    StringComparison.OrdinalIgnoreCase)
                                && creation == kv.Value.Creation;
                            done = identityKnown && !same;
                            if (same) Logger.Log(Lang.T("log.gamemodeboost.38") + kv.Value.Name + " pid " + pid + Lang.T("log.gamemodeboost.39"));
                        }
                        finally { Native.CloseHandle(hq); }
                    }
                }
                else
                {
                    try
                    {
                        string cur = Native.ImageName(h);
                        long creation, cpu; ulong disk;
                        bool sampled = Native.QueryProcessSample(
                            h, out creation, out cpu, out disk);
                        bool identityKnown = cur != null && sampled;
                        bool identity = identityKnown
                            && string.Equals(
                                cur, kv.Value.Name,
                                StringComparison.OrdinalIgnoreCase)
                            && creation == kv.Value.Creation;
                        if (!identityKnown)
                        {
                            done = !Native.StillActive(h);
                        }
                        else if (identity)
                        {
                            bool pinClean = RestoreHardPin(pid);
                            done = RestoreBoostSnapshot(h, pid, kv.Value) && pinClean;
                        }
                        else done = true;
                    }
                    finally { Native.CloseHandle(h); }
                }
                if (done)
                {
                    if (coreIsolation != null && !coreIsolation.Drop(pid, kv.Value.Creation)) isolationClean = false;
                    ForgetHardPin(pid);
                    CrashGuard.ReleaseBoostProcess(pid, kv.Value.Creation);
                    lock (sync)
                    {
                        gameBoost.Remove(pid); gameGpu.Remove(pid);
                        gamePlacement.Remove(pid);
                        gameBoostNextAudit.Remove(pid);
                        ForgetBoostActionTarget(pid);
                    }
                }
            }
            lock (sync) return isolationClean && gameBoost.Count == 0;
        }

        internal static void CurrentPlacement(IntPtr h, ref ulong affinity, ref uint[] cpuSets)
        {
            ulong current;
            if (!CpuTopology.MultiGroup && Native.TryQueryAffinity(h, out current) && current != 0) affinity = current;
            uint[] sets = Native.QueryCpuSets(h);
            if (sets != null) cpuSets = sets;
        }

        public bool PanicRestore()
        {
            Interlocked.Increment(ref standbyCleanerRestorePending);
            InvalidateCacheWarm();
            InvalidateStandbyCleanerWork();
            InvalidateEnglishInputWork();
            try { return PanicRestoreCore(); }
            finally
            {
                Interlocked.Decrement(ref standbyCleanerRestorePending);
            }
        }

        private bool PanicRestoreCore()
        {
            int cleared = SelfProtectedRoster.Clear();
            if (cleared > 0)
                Logger.Log(Lang.T("log.gamemodeboost.46") + cleared + Lang.T("log.gamemodeboost.47"));
            bool legacyOk = true;
            if (IfeoBoost.HasResidue())
                legacyOk &= IfeoBoost.RestoreAll();
            if (CfgOffTweak.HasResidue())
                legacyOk &= CfgOffTweak.RestoreAll();
            // Import historical fuses before recovery; panic restoration never grants apply retries.
            try { ActionFailureStore.Records(); }
            catch (Exception failure) { Logger.Warn("Failure isolation retained during panic recovery: " + failure.Message); }
            lock (panicCallGate)
            {
                int mine = Interlocked.Increment(ref panicSeq);
                panicDone.Reset();
                panicResult = false;
                lock (sync)
                {
                    panicReq = true;
                    InvalidateStandbyCleanerWork();
                    InvalidateEnglishInputWork();
                    InvalidateRendererHandoff();
                }
                kick.Set();

                long deadline = DateTime.UtcNow.Ticks + 12000L * TimeSpan.TicksPerMillisecond;
                while (true)
                {
                    long left = (deadline - DateTime.UtcNow.Ticks) / TimeSpan.TicksPerMillisecond;
                    if (left <= 0) return false;
                    if (!panicDone.WaitOne((int)left)) return false;
                    if (Volatile.Read(ref panicServed) == mine) return panicResult && legacyOk;
                    panicDone.Reset();
                }
            }
        }

        private bool Deactivate(string reason)
        {
            return Deactivate(reason, false);
        }

        private bool Deactivate(string reason, bool quiet)
        {
            EndAutomationEvidence();
            bool freezeClean = backgroundFreeze.Clear("game-session-ended");
            bool cpuCapsClean = backgroundCpuCap.Clear();
            InvalidateCacheWarm();
            SetAutoGpuSessionStamp(0);
            InvalidateStandbyCleanerWork();
            EndEnglishInputSession();
            InvalidateRendererHandoff();
            Exception reportFailure = null;
            try { ReportFinish(); }
            catch (Exception ex) { reportFailure = ex; }
            RestorePowerOverlay();
            SelfYield.Release();
            lock (sync)
            {
                active = false;
                activeGame = null;
                firstSweep = true;
            }
            SyncPerformanceAutomation();
            sessionPolicy = null;
            bool guardClean = true;
            if (enabled && !stopping && !ProfileStoreSaveFailed)
            {
                SyncGameDvr();
                guardClean = !GuardSchedulingRecoveryPending;
            }
            else guardClean = RestoreGuardScheduling();
            SuppressionCore.GpuDemoteEnabled = gpuDemoteOn;
            gameGoneSinceTicks = 0;
            cpuSaturation.Reset();
            boostPriorityTarget = Native.HIGH_PRIORITY_CLASS;
            ResetBoostIdentity();
            Interlocked.Exchange(ref boostFirstStampTicks, 0);
            Interlocked.Exchange(ref nvTweakRetryAtTicks, 0);
            ResetNvApplyAttempts();
            preStagedNvPath = null;
            autoGpuScanned = false;
            ResetAdaptiveGuard();

            bool clean = UnboostGames();
            EndAutoCores();
            slowEnvAtTicks = 0;
            List<int> background = core.PidsWith(SuppressReason.Background);
            int ok = core.ReleaseReason(SuppressReason.Background);
            bool backgroundClean = true;
            foreach (int pid in background) if (core.IsThrottled(pid)) { backgroundClean = false; break; }
            bool envClean = RestoreEnv();
            ClearEnvRetryState();
            if (clean) CrashGuard.ClearBoost();
            int restoredTotal = ok + gracePreReleased;
            gracePreReleased = 0;
            if (!quiet || restoredTotal > 0)
                Logger.Log(Lang.T("log.gamemodeboost.52") + reason + Lang.T("log.gamemodeboost.53") + restoredTotal
                    + Lang.T("log.gamemodeboost.54"));
            lock (sync)
            {
                activeDetection = null;
                transitionProbeRendererPid = 0;
                transitionProbeRendererCreation = 0;
            }
            ClearSticky();
            NotifyExtensionSession(false);
            bool standbyClean = standbyCleaner == null || standbyCleaner.Drain(8000);
            bool inputClean = DrainEnglishInput(8000);
            bool cacheWarmClean = cacheWarm == null || cacheWarm.Drain(8000);
            bool performanceClean = performanceActions.WaitForSessionRestore(8000);
            if (reportFailure != null) throw reportFailure;
            bool complete = clean && envClean && backgroundClean && standbyClean && inputClean && cacheWarmClean && cpuCapsClean && freezeClean && guardClean && performanceClean;
            RecordAutomation("session", complete ? "restored" : "restore-failed");
            return complete;
        }
    }
}
