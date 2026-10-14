// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private bool coreObservationOnly;
        private PerformancePreset corePreset;
        private bool coreAllowed;
        private long corePolicyCheckTicks;
        private int corePolicyDirty;
        private string corePolicyReason;
        private CoreIsolationClient coreIsolation;
        private AutoCorePlan corePlan;
        private ManualCorePlan coreManual;
        private AutoCoreGovernor coreGovernor;
        private int coreWatchPid;
        private long coreWatchCreation;
        private volatile bool coreEngaged;
        private volatile bool corePinPending;
        private long exclusiveCheckTicks;
        private AutoCoreSampling coreSampling = new AutoCoreSampling();
        private AutoCoreLoadGuard coreLoadGuard = new AutoCoreLoadGuard();
        private ulong coreGameMask;
        private ulong[] corePhysical;
        private bool coreCooperative, coreBackgroundOnly, coreManualPlacementYielded;
        private int coreExpansions;
        private int coreBackgroundMoved;
        private bool coreBackgroundPending;
        private string corePlacementEvidence;
        private readonly Dictionary<int, long> coreBackgroundRetry = new Dictionary<int, long>();

        private void GrowCoreCapacity(int active, double load)
        {
            if (coreManual != null || corePlan == null || !corePlan.Available || coreBackgroundOnly) return;
            int wanted = Math.Max(Math.Min(active, corePlan.Cores), (int)Math.Ceiling(load / 0.85));
            ulong expanded = AutoCores.Expand(corePlan, coreGameMask, allMask,
                corePhysical, CpuTopology.FavoredMask, wanted);
            if (expanded == coreGameMask) return;
            coreGameMask = expanded;
            coreGovernor.Grow(AutoCores.PhysicalCount(expanded, corePhysical), AutoCores.CountBits(expanded));
        }

        private AutoCoreGovernor NewCoreGovernor()
        {
            return corePlan != null && corePlan.Available ? new AutoCoreGovernor(corePlan.Cores, corePlan.Logical) : null;
        }

        private void ResetCoreWatch()
        {
            ResetCoreIntelligence();
            coreWatchPid = 0;
            coreWatchCreation = 0;
            coreEngaged = false;
            coreCooperative = coreBackgroundOnly = false;
            coreManualPlacementYielded = false;
            coreSampling = new AutoCoreSampling();
            coreLoadGuard = new AutoCoreLoadGuard();
            coreGameMask = coreManual != null ? coreManual.GameMask : corePlan != null ? corePlan.Exclusive : 0;
            corePhysical = CpuTopology.PhysicalCoreMasks();
            coreExpansions = coreBackgroundMoved = 0;
            coreBackgroundRetry.Clear();
            corePlacementEvidence = null;
        }

        private void BeginAutoCores(PolicySnapshot snap, bool isolationClean)
        {
            // 旧句柄仍需恢复完成，才允许建立新对局分配。
            bool pinRestored = RestoreAllHardPins();
            corePinPending = !pinRestored;
            ResetCoreWatch();
            // 本局使用入局快照，手动方案修改下局生效。
            coreManual = sessionPolicy != null ? sessionPolicy.ManualCores : snap != null ? snap.ManualCores : null;
            corePlan = coreManual != null ? coreManual.RuntimePlan()
                : new AutoCorePlan { Blocked = "schedule.manual.required" };
            corePreset = snap != null ? snap.Preset : PerformancePreset.Standard;
            coreAllowed = snap != null && snap.AutoCoresAllowed;
            corePolicyCheckTicks = AutoCores.NowTicks;
            coreObservationOnly = coreManual == null || snap == null || !snap.AutoCoresAllowed
                || !PresetValue.SupportsCoreScheduling(snap.Preset);
            corePolicyReason = snap != null && !snap.AutoCoresAllowed ? "schedule.auto.excluded" : "schedule.auto.observe";
            coreGovernor = NewCoreGovernor();
            coreGameMask = coreManual != null ? coreManual.GameMask : corePlan.Exclusive;
            if (coreGovernor != null && (!isolationClean || !pinRestored)) coreGovernor.Block("schedule.auto.pending");
            if (corePlan.Available)
                Logger.Log(Lang.F("schedule.manual.plan", CpuTopology.DescribeMask(coreManual.GameMask)));
            else Logger.Log(Lang.F("log.autocore.unavailable", Lang.T(corePlan.Blocked)));
            if (corePlan.Allocated != 0)
                Logger.Log(Lang.F("log.autocore.allocated", CpuTopology.DescribeMask(corePlan.Allocated)));
            PublishAutoCores(null);
        }

        private void EndAutoCores()
        {
            ResetCoreIntelligence();
            coreGovernor = null;
            corePlan = null;
            coreManual = null;
            ResetCoreWatch();
            lock (sync) corePinPending = hardPins.Count > 0;
            AutoCores.Publish(new AutoCoreStatus { Pending = CorePending });
        }

        private bool CorePending
        {
            get { return corePinPending || coreBackgroundPending || (coreThreadPlacement != null && (coreThreadPlacement.Pending || coreThreadPlacement.UnownedPending)) || CoreIsolationClient.State == "schedule.isolation.pending"; }
        }

        private void RefreshAutoCoreMode()
        {
            if (stopping) return;
            if (System.Threading.Interlocked.Exchange(ref corePolicyDirty, 0) == 0
                && AutoCores.NowTicks - corePolicyCheckTicks < 5 * TimeSpan.TicksPerSecond) return;
            corePolicyCheckTicks = AutoCores.NowTicks;
            GameProfile source = null;
            lock (sync)
            {
                if (activeDetection != null && activeDetection.Profile != null)
                    foreach (GameProfile p in profiles)
                        if (p.Id == activeDetection.Profile.Id) { source = p.Clone(); break; }
            }
            PolicySnapshot fresh = source == null ? PolicyResolver.Global() : PolicyResolver.For(source);
            if (fresh.AutoCoresAllowed && coreAllowed
                && PresetValue.SupportsCoreScheduling(fresh.Preset)
                && PresetValue.SupportsCoreScheduling(corePreset))
            {
                if (corePreset != fresh.Preset)
                {
                    StopCoreRefinement(); coreRefinementStopped = true; coreFeedback = "phase-changed";
                    coreLearning = null; corePeak = 0; coreLearningWindows = 0;
                }
                corePreset = fresh.Preset; return;
            }
            if (fresh.Preset != corePreset || fresh.AutoCoresAllowed != coreAllowed)
                BeginAutoCores(fresh, StopCoreIsolation());
        }

        private void StepAutoCores(BoostPass pass)
        {
            long started = AutoCores.NowTicks;
            try { StepAutoCoresImpl(pass); }
            finally { coreBudget.Record(started); }
        }

        private void StepAutoCoresImpl(BoostPass pass)
        {
            RefreshAutoCoreMode();
            AutoCoreGovernor governor = coreGovernor;
            if (coreManual == null) { PublishAutoCores(pass.RendererName); return; }
            if (governor == null || pass.RendererPid <= 0 || pass.RendererCreation <= 0)
            {
                if (governor != null) ApplyAutoCoreStep(governor.Pause("schedule.auto.foreground", AutoCores.NowTicks));
                PublishAutoCores(pass.RendererName);
                return;
            }
            if (pass.RendererPid != coreWatchPid || pass.RendererCreation != coreWatchCreation)
            {
                if (coreWatchPid > 0)
                {
                    bool clean = ReleaseExclusive();
                    governor = coreGovernor = NewCoreGovernor();
                    if (!clean) governor.Block("schedule.auto.pending");
                }
                ResetCoreWatch();
                coreWatchPid = pass.RendererPid;
                coreWatchCreation = pass.RendererCreation;
            }
            bool gaveUp;
            lock (sync)
            {
                gaveUp = placementGaveUp.Contains(pass.RendererPid);
            }
            if (gaveUp && !coreEngaged) BlockAutoCores("schedule.auto.placementunavailable");
            else if (coreEngaged && (coreManual == null || coreManual.Isolation) && !coreCooperative && !coreBackgroundOnly && CoreIsolationClient.State != "schedule.isolation.active") BlockAutoCores("schedule.auto.failed");
            long now = AutoCores.NowTicks;
            if (coreObservationOnly)
            {
                PublishAutoCores(pass.RendererName);
                return;
            }
            if (RendererForegroundPid() != pass.RendererPid)
            {
                if (coreFrames != null) { StopCoreRefinement(); coreRefinementStopped = true; coreFeedback = "phase-changed"; }
                if (!coreEngaged) ApplyAutoCoreStep(governor.Pause("schedule.auto.foreground", now));
                PublishAutoCores(pass.RendererName);
                return;
            }
            if (coreManual != null)
            {
                governor.EngageManual();
                PublishAutoCores(pass.RendererName);
                return;
            }
        }

        private void ClearBackgroundSqueezes()
        {
            if (core.SqueezedCount(SuppressReason.Background, false) > 0)
                core.ClearSqueezes(SuppressReason.Background);
            coreBackgroundMoved = core.SqueezedCount(SuppressReason.Background);
            coreBackgroundPending = core.SqueezedCount(SuppressReason.Background, false) > 0;
            if (!coreBackgroundPending) core.EnforceBackgroundAffinity = false;
        }

        private void StartBackgroundOnly()
        {
            // 手动操作被拒绝后不切换为自动方案。
            BlockAutoCores("schedule.auto.placementunavailable");
        }

        private void SyncCoreBackground(ProcessSnapshot snapshot, HashSet<int> protectedPids, int foreground)
        {
            long started = AutoCores.NowTicks;
            try { SyncCoreBackgroundImpl(snapshot, protectedPids, foreground); }
            finally { coreBudget.Record(started); }
        }

        private ulong CoreBackgroundTarget()
        {
            if (coreManual == null || stopping || !coreEngaged || coreObservationOnly || corePlan == null || !corePlan.Available || CorePending) return 0;
            if (!coreManual.Isolation || CoreIsolationClient.State != "schedule.isolation.active"
                || CoreIsolationClient.ActiveMask != corePlan.Exclusive) return 0;
            ulong target = allMask & ~corePlan.Exclusive & ~AutoCores.WholeCores(corePlan.Allocated, corePhysical);
            return AutoCores.CountBits(target) >= AutoCores.MinGameLogical ? target : 0;
        }

        private void SyncCoreBackgroundImpl(ProcessSnapshot snapshot, HashSet<int> protectedPids, int foreground)
        {
            SyncAutomaticBackgroundLayout(protectedPids, foreground);
            ulong target = CoreBackgroundTarget();
            if (target == 0) { ClearBackgroundSqueezes(); return; }
            core.EnforceBackgroundAffinity = coreManual != null;
            int attempts = 0;
            long now = AutoCores.NowTicks;
            // 手动硬亲和性覆盖闲置后台，自动避让仍需竞争证据。
            foreach (int pid in coreManual != null ? core.PidsWith(SuppressReason.Background) : RankCoreBackground(snapshot))
            {
                ProcEntry p = snapshot == null ? null : snapshot.Find(pid);
                long creation = coreManual != null ? core.CreationOf(pid) : p != null ? p.Creation : 0;
                string name = coreManual != null ? core.NameOf(pid) : p != null ? p.Name : null;
                if (creation <= 0 || string.IsNullOrEmpty(name)) continue;
                bool changed;
                if (pid == foreground || pid == coreWatchPid || protectedPids.Contains(pid)
                    || WeGameRuntimeProtection.IsProtectedRuntimeName(name) || PeripheralCatalog.IsInputAudioLike(name))
                { core.SetSqueeze(pid, creation, name, 0, SuppressReason.Background, out changed); continue; }
                if (target == 0 || core.IsSqueezed(pid)) continue;
                if (coreManual == null)
                {
                    if (!coreBudget.Admit(now)) continue;
                    long retry;
                    if (coreBackgroundRetry.TryGetValue(pid, out retry) && now < retry) continue;
                    if (++attempts > 8) break;
                }
                bool applied = core.SetSqueeze(pid, creation, name, target, SuppressReason.Background, out changed);
                if (!applied && coreManual == null) coreBackgroundRetry[pid] = now + 30 * TimeSpan.TicksPerSecond;
            }
            coreBackgroundMoved = core.SqueezedCount(SuppressReason.Background);
        }

        private void ApplyAutoCoreStep(AutoCoreStep step)
        {
            if (step == AutoCoreStep.Release) ReleaseExclusive();
        }

        private void BlockAutoCores(string reason)
        {
            AutoCoreGovernor governor = coreGovernor;
            if (governor == null) return;
            bool first = governor.Blocked == null;
            ApplyAutoCoreStep(governor.Block(reason));
            if (first) Logger.Log(Lang.F("log.autocore.blocked", Lang.T(reason)));
        }

        private bool ReleaseExclusive()
        {
            bool wasEngaged = coreEngaged;
            ClearBackgroundSqueezes();
            coreEngaged = false;
            bool clean = StopCoreIsolation();
            // 整个所有权表结算后才给新渲染进程分配核心。
            bool pinRestored = RestoreAllHardPins();
            corePinPending = !pinRestored;
            if ((!clean || !pinRestored) && coreGovernor != null && coreGovernor.Blocked == null)
                coreGovernor.Block("schedule.auto.pending");
            if (wasEngaged)
                Logger.Log(clean && pinRestored ? Lang.T("log.autocore.released") : Lang.T("log.autocore.releasepending"));
            return clean && pinRestored;
        }

        private bool ExclusiveDrifted(int pid, BoostPass pass, out ulong observed)
        {
            observed = 0;
            if (!pass.Exclusive || !coreEngaged || CpuTopology.MultiGroup || stopping) return false;
            long now = AutoCores.NowTicks;
            if (now - exclusiveCheckTicks < TimeSpan.TicksPerSecond) return false;
            exclusiveCheckTicks = now;
            lock (sync)
            {
                if (placementGaveUp.Contains(pid) || !gamePlacement.ContainsKey(pid)) return false;
                HardPin pin;
                if (!hardPins.TryGetValue(pid, out pin) || pin.Pid != pid || pin.RestoreHandle == IntPtr.Zero) return false;
                return Native.TryQueryAffinity(pin.RestoreHandle, out observed) && observed != pin.AppliedAffinity;
            }
        }

        private bool PlacementChangedOutside(IntPtr h, Snap original, out ulong observed, out bool unreadable)
        {
            observed = 0;
            unreadable = false;
            if (CpuTopology.MultiGroup) return false;
            ulong expected = original.Aff != 0 ? original.Aff : allMask;
            if (!Native.TryQueryAffinity(h, out observed)) { unreadable = true; return false; }
            if (observed != expected) return true;
            uint[] sets = Native.QueryCpuSets(h);
            if (sets == null) { unreadable = true; return false; }
            return !CoreThreadPlacement.Same(sets, original.CpuSets ?? new uint[0]);
        }

        private void YieldExclusive(int pid, ulong observed)
        {
            long creation = 0;
            lock (sync)
            {
                placementGaveUp.Add(pid);
                gamePlacement.Remove(pid);
                Snap snap;
                if (gameBoost.TryGetValue(pid, out snap))
                {
                    snap.PlacementYielded = true;
                    gameBoost[pid] = snap;
                    creation = snap.Creation;
                }
            }
            if (creation > 0) CrashGuard.YieldBoostPlacement(pid, creation);
            ForgetHardPin(pid);
            if (coreManual != null && coreManual.Isolation && coreEngaged && corePlan != null
                && CoreIsolationClient.State == "schedule.isolation.active"
                && CoversManualIsolation(observed, corePlan.Exclusive))
            {
                coreManualPlacementYielded = true;
                coreGameMask = observed;
                return;
            }
            coreEngaged = false;
            AutoCoreGovernor governor = coreGovernor;
            if (governor != null) governor.Block("schedule.auto.external");
            if (!StopCoreIsolation() && governor != null) governor.Block("schedule.auto.pending");
            Logger.Log(Lang.F("log.autocore.external", observed.ToString("X")));
        }

        internal static bool CoversManualIsolation(ulong observed, ulong isolated)
        { return isolated != 0 && (observed & isolated) == isolated; }

        private void AuditManualIsolationCoverage(BoostPass pass)
        {
            long now = AutoCores.NowTicks;
            if (now - exclusiveCheckTicks < TimeSpan.TicksPerSecond) return;
            exclusiveCheckTicks = now;
            IntPtr handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pass.RendererPid);
            if (handle == IntPtr.Zero) { YieldExclusive(pass.RendererPid, 0); return; }
            try
            {
                long creation, cpu; ulong io, observed;
                if (!Native.QueryProcessSample(handle, out creation, out cpu, out io)
                    || creation != pass.RendererCreation || !Native.TryQueryAffinity(handle, out observed)
                    || !CoversManualIsolation(observed, corePlan.Exclusive))
                    YieldExclusive(pass.RendererPid, 0);
                else coreGameMask = observed;
            }
            finally { Native.CloseHandle(handle); }
        }

        private bool PinExclusive(IntPtr h, int pid, BoostPass pass, Snap original)
        {
            if (CpuTopology.MultiGroup || original.Aff == 0 || pass.DesiredMask == 0 || (pass.DesiredMask & ~original.Aff) != 0)
            { LogCorePlacement(pass, "affinity-range", 0, h); return false; }
            IntPtr pinHandle = DuplicateRestoreHandle(h);
            if (pinHandle == IntPtr.Zero) { LogCorePlacement(pass, "restore-handle", Marshal.GetLastWin32Error(), h); return false; }
            bool remembered = false, written = false;
            lock (sync)
            {
                ulong current;
                if (!stopping)
                {
                    if (!Native.TryQueryAffinity(h, out current))
                        LogCorePlacement(pass, "read-original", Marshal.GetLastWin32Error(), h);
                    else if (current != original.Aff)
                        LogCorePlacement(pass, "original-changed", 0, h);
                    else if (!CrashGuard.TrackBoostAffinity(pid, original.Creation, pass.DesiredMask))
                        LogCorePlacement(pass, "journal", 0, h);
                    else if (!Native.SetProcessAffinityMask(h, (UIntPtr)pass.DesiredMask))
                        LogCorePlacement(pass, "set-affinity", Marshal.GetLastWin32Error(), h);
                    else
                    {
                        RememberHardPin(pid, original.Creation, original.Aff, pass.DesiredMask, pinHandle);
                        remembered = true;
                        bool read = Native.TryQueryAffinity(h, out current);
                        int readError = read ? 0 : Marshal.GetLastWin32Error();
                        written = read && current == pass.DesiredMask;
                        if (!written) LogCorePlacement(pass, read ? "readback-mismatch" : "readback", readError, h);
                    }
                }
            }
            if (!remembered) Native.CloseHandle(pinHandle);
            else if (!written) RestoreHardPin(pid);
            return written;
        }

        private bool EngageIsolation(int pid, BoostPass pass)
        {
            if (stopping || coreManual == null) return false;
            if (!coreManual.Isolation) { coreEngaged = true; return true; }
            if (corePlan != null && corePlan.Cooperative)
            { coreCooperative = coreEngaged = true; return true; }
            if (coreEngaged && (coreCooperative || CoreIsolationClient.State == "schedule.isolation.active")) return true;
            bool isolated;
#if PAVISE_SELFTEST
            if (CoreEngageForTest != null)
            {
                isolated = CoreEngageForTest();
                CoreIsolationClient.State = isolated ? "schedule.isolation.active" : "schedule.isolation.idle";
            }
            else
#endif
            {
                if (coreIsolation == null) coreIsolation = new CoreIsolationClient();
                isolated = coreIsolation.Ensure(corePlan != null ? corePlan.Exclusive : pass.DesiredMask,
                    pid, pass.RendererCreation, pass.DesiredMask, coreManual != null);
            }
            if (isolated)
            {
                coreEngaged = true;
                Logger.Log(Lang.F("log.autocore.engaged", CpuTopology.DescribeMask(corePlan != null ? corePlan.Exclusive : pass.DesiredMask)));
                return true;
            }
            if (!StopCoreIsolation()) return false;
            if (coreManual != null) return false;
            coreCooperative = coreEngaged = true;
            Logger.Log("AutoCore cooperative placement: kernel reservation unavailable; background avoidance enabled");
            return true;
        }

        private bool StopCoreIsolation()
        {
            bool threadsClean = StopCoreRefinement();
            ClearBackgroundSqueezes();
            coreEngaged = false;
            coreCooperative = coreBackgroundOnly = false;
            bool clean = coreIsolation == null || coreIsolation.Stop();
            if (clean) coreIsolation = null;
            return clean && !coreBackgroundPending && threadsClean;
        }

        private void PublishAutoCores(string game)
        {
            AutoCoreGovernor governor = coreGovernor;
            ulong actualMask = coreGameMask;
            lock (sync)
            {
                HardPin pin;
                if (coreEngaged && !coreBackgroundOnly && hardPins.TryGetValue(coreWatchPid, out pin)) actualMask = pin.AppliedAffinity;
            }
            AutoCores.Publish(new AutoCoreStatus
            {
                Plan = corePlan,
                Manual = coreManual != null, ManualIsolation = coreManual != null && coreManual.Isolation,
                Game = game,
                ProfileId = activeDetection != null && activeDetection.Profile != null ? activeDetection.Profile.Id : null,
                Watching = governor != null && coreWatchPid > 0,
                Exclusive = governor != null && governor.Exclusive,
                Engaged = coreEngaged,
                Cooperative = coreCooperative, BackgroundOnly = coreBackgroundOnly,
                BackgroundMoved = coreBackgroundMoved, GameMask = actualMask,
                Expansions = coreExpansions,
                SampleCount = coreSampling.Count, ReusedSamples = coreSampling.Reused,
                SampleIntervalMs = coreSampling.IntervalMs,
                SampleAverageMs = coreSampling.AverageMs, SampleMaxMs = coreSampling.MaxMs,
                ThreadCandidates = coreThreadPlacement.Candidates, ThreadState = coreThreadPlacement.State,
                Feedback = coreFeedback, Learned = coreLearning != null && coreLearning.Loaded,
                WorkMs = coreBudget.TotalMs + coreFrameParseMs, WorkMaxMs = coreBudget.MaxMs, BudgetDeferrals = coreBudget.Deferrals,
                CollectorCpuMs = coreCollectorCpuMs,
                CandidateMask = coreThreadPlacement.CandidateMask, WorkerMask = coreThreadPlacement.WorkerMask,
                BaselineP99 = coreFrameBaseline != null && coreFrameBaseline.Usable ? coreFrameBaseline.P99 : 0,
                LatestP99 = coreFrameLatestP99,
                Pending = CorePending,
                PlacementEvidence = corePlacementEvidence,
                Blocked = governor != null ? governor.Blocked
                    : coreManual != null && coreAllowed && corePlan != null ? corePlan.Blocked : null,
                Reason = coreObservationOnly ? corePolicyReason : governor != null ? governor.Reason : null,
                ObservationOnly = coreObservationOnly,
                CooldownSeconds = governor == null || governor.Blocked != null ? 0 : (int)Math.Max(0, (governor.CooldownUntil - AutoCores.NowTicks) / TimeSpan.TicksPerSecond),
                Active = governor != null ? governor.Active : 0,
                Load = governor != null ? governor.Load : 0,
                Capacity = coreEngaged && !coreBackgroundOnly ? AutoCores.PhysicalCount(coreManual != null ? AutoCores.WholeCores(actualMask, corePhysical) : actualMask, corePhysical) : governor != null ? governor.Capacity : 0,
                Logical = governor != null ? governor.Logical : 0,
                Windows = governor != null ? governor.Windows : 0,
                Releases = governor != null ? governor.Releases : 0
            });
        }

#if PAVISE_SELFTEST
        internal Func<bool> CoreEngageForTest;
        internal Func<int, IntPtr> CorePlacementOpenForTest;

        internal void ProbeUseIsolationWorker()
        {
            coreIsolation = new CoreIsolationClient();
            coreIsolation.UseTestWorker();
        }

        internal bool ProbeStopIsolationWorker() { return StopCoreIsolation(); }

        internal void ProbeBeginAutoCores(AutoCorePlan plan)
        {
            corePlan = plan;
            coreObservationOnly = false;
            corePreset = PerformancePreset.Competitive;
            coreAllowed = true;
            corePolicyCheckTicks = AutoCores.NowTicks;
            coreGovernor = NewCoreGovernor();
            ResetCoreWatch();
        }

        internal AutoCoreGovernor ProbeGovernor { get { return coreGovernor; } }

        internal bool ProbeCoreEngaged { get { return coreEngaged; } }

        internal bool ProbeExclusivePlacement(IntPtr handle, int pid, long creation, ulong original, ulong target)
        {
            RendererTestForeground = delegate { return pid; };
            uint[] ids = Native.QueryCpuSets(handle);
            if (ids == null) return false;
            CrashGuard.MarkBoostProcess(pid, creation, Native.ImageName(handle), Native.NORMAL_PRIORITY_CLASS, original, 2, 5, -1, ids);
            gameBoost[pid] = new Snap { Creation = creation, Aff = original, CpuSets = ids, Name = "core-test-child" };
            var pass = new BoostPass { Exclusive = true, DesiredMask = target,
                RendererPid = pid, RendererCreation = creation, RendererName = "core-test-child" };
            string text; bool verified;
            return ApplyPlacementStage(handle, pid, pass, true, true, out text, out verified) && verified;
        }

        internal bool ProbeSharedPlacement(IntPtr handle, int pid, long creation, ulong original)
        {
            gameBoost[pid] = new Snap { Creation = creation, Aff = original, CpuSets = new uint[0], Name = Native.ImageName(handle) };
            var pass = new BoostPass { DesiredMask = allMask, RendererPid = pid, RendererCreation = creation };
            string text; bool verified;
            return ApplyPlacementStage(handle, pid, pass, true, true, out text, out verified) && verified;
        }

        internal bool ProbePinExclusive(IntPtr handle, int pid, long creation, ulong original, ulong target)
        {
            if (!CrashGuard.MarkBoostProcess(pid, creation, Native.ImageName(handle), Native.NORMAL_PRIORITY_CLASS,
                original, 2, 5, -1, Native.QueryCpuSets(handle))) return false;
            var snap = new Snap { Creation = creation, Aff = original, Name = "core-test-child" };
            var pass = new BoostPass { Exclusive = true, DesiredMask = target,
                RendererPid = pid, RendererCreation = creation, RendererName = "core-test-child" };
            return PinExclusive(handle, pid, pass, snap);
        }

        internal bool ProbeRestoreHardPin(int pid)
        {
            return RestoreHardPin(pid);
        }

        internal bool ProbePlacementYielded(int pid)
        {
            Snap snap;
            lock (sync) return gameBoost.TryGetValue(pid, out snap) && snap.PlacementYielded;
        }
#endif
    }
}
