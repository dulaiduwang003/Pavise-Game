// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
#if PAVISE_SELFTEST
        internal Func<IntPtr, long, int> EntryIdentityForTest;
        internal Func<IntPtr, bool> EntryApplyForTest, EntryRestoreForTest, EntryMatchesForTest;
        internal bool ApplyIsolationForTest(int pid, string target, long creation)
        {
            lock (sync)
            {
                Entry entry;
                if (!map.TryGetValue(pid, out entry))
                {
                    entry = new Entry { Name = System.IO.Path.GetFileName(target), FailureTarget = target, Creation = creation,
                        OrigPri = Native.NORMAL_PRIORITY_CLASS, OrigAff = 3, OrigIo = 2, OrigPg = 5, OrigCpuSets = new uint[] { 1 },
                        OrigQoSControl = 0, OrigQoSState = 0, OrigGpu = 0, OrigBoost = 0, Level = SuppressionLevel.Restrained,
                        BackgroundLevel = SuppressionLevel.Restrained, Reasons = SuppressReason.Background, Journaled = true };
                    map[pid] = entry;
                }
                entry.Applied = ApplyEntryLocked(new IntPtr(pid), entry, pid); return entry.Applied;
            }
        }
        internal bool[] IsolationFlagsForTest(int pid)
        { lock (sync) { Entry e = map[pid]; return new[] { e.GaveUp, e.GaveUpRestored, e.Journaled, e.Applied }; } }
#endif
        // 0: same live process, 1: confirmed exit/reuse, 2: identity cannot be confirmed.
        private int EntryIdentity(IntPtr handle, Entry entry)
        {
#if PAVISE_SELFTEST
            if (EntryIdentityForTest != null) return EntryIdentityForTest(handle, entry.Creation);
#endif
            int state = Native.QueryProcessIdentityState(handle, entry.Creation);
            return state < 0 ? 2 : state;
        }
        private bool RestoreEntryValues(IntPtr handle, Entry entry)
        {
            try
            {
                if (EntryIdentity(handle, entry) != 0) return false;
#if PAVISE_SELFTEST
                if (EntryRestoreForTest != null) return EntryRestoreForTest(handle);
#endif
                return RestoreValues(handle, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg, allMask,
                    entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, entry.OrigGpu, entry.OrigBoost);
            }
            catch (Exception error) { LastApplyError = error.Message; return false; }
        }
        private bool EntrySnapshotMatches(IntPtr handle, Entry entry)
        {
#if PAVISE_SELFTEST
            if (EntryMatchesForTest != null) return EntryMatchesForTest(handle);
#endif
            return SnapshotMatchesCurrent(handle, entry.OrigPri, entry.OrigAff, entry.OrigIo, entry.OrigPg,
                entry.OrigCpuSets, entry.OrigQoSControl, entry.OrigQoSState, entry.OrigGpu, entry.OrigBoost);
        }
        private bool ApplyEntryValues(IntPtr handle, Entry entry)
        {
#if PAVISE_SELFTEST
            if (EntryApplyForTest != null) return EntryApplyForTest(handle);
#endif
            DetectBackgroundPlacementChange(handle, entry);
            return ApplyThrottle(handle, entry.Level, entry.OrigPri, entry.OrigAff, AutomaticLayoutDesired(handle, entry), DesiredGpu(entry),
                entry.OrigBoost, AntiCheatThrottled(entry), DesiredAffinityOf(entry), PlacementIsProtected(entry));
        }
        private static void StopEntry(Entry entry, bool restored)
        { entry.GaveUp = true; entry.GaveUpRestored = restored; entry.Applied = false; entry.NextReconcileTicks = long.MaxValue; }
        private bool ThrottleMatches(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity,
            uint[] originalCpuSets, int desiredGpu, bool antiCheat, ulong desiredAffinity, bool affinityOwned = false)
        {
            uint desiredPriority = DesiredPriority(level, originalPriority, antiCheat);
            if (Native.GetPriorityClass(h) != desiredPriority) return false;
            if (desiredGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0
                    && gpuCur != desiredGpu) return false;
            }
            int desiredIo = DesiredIoPriority(level);
            int desiredPage = DesiredPagePriority(level, antiCheat);
            if (Native.QueryIoPriority(h) != desiredIo || Native.QueryPagePriority(h) != desiredPage) return false;

            if (Native.PowerThrottlingSupported)
            {
                int qosControl, qosState;
                if (!Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                    || (qosControl & 1) == 0 || (qosState & 1) == 0) return false;
            }

            if (!affinityOwned && !Native.CpuSetsMatch(h, originalCpuSets ?? new uint[0])) return false;
            if (!CpuTopology.MultiGroup && !affinityOwned && Native.QueryAffinity(h) != desiredAffinity) return false;
            return true;
        }

        // 反作弊不使用 IDLE，避免扫描游戏内存时拖长停顿。
        internal static uint DesiredPriority(SuppressionLevel level, uint originalPriority, bool antiCheat)
        {
            uint desired = originalPriority == 0 || originalPriority == uint.MaxValue
                ? Native.NORMAL_PRIORITY_CLASS : originalPriority;
            if (level >= SuppressionLevel.Restrained)
                desired = level >= SuppressionLevel.Isolated && !antiCheat
                    ? Native.IDLE_PRIORITY_CLASS : Native.BELOW_NORMAL_PRIORITY_CLASS;
            return desired;
        }

        internal static int DesiredIoPriority(SuppressionLevel level)
        {
            return level >= SuppressionLevel.Isolated ? 0 : 1;
        }

        internal static int DesiredPagePriority(SuppressionLevel level, bool antiCheat)
        {
            return level >= SuppressionLevel.Isolated && !antiCheat ? 1 : 3;
        }

        internal static bool DesiredTimerSeal(SuppressionLevel level, bool antiCheat)
        {
            return level >= SuppressionLevel.Isolated && !antiCheat;
        }

        internal static int DesiredGpuClass(bool demoteEnabled, SuppressReason reasons,
            SuppressionLevel backgroundLevel, int origGpu)
        {
            if (origGpu < 0) return -1;
            if (!demoteEnabled || (reasons & SuppressReason.Background) == 0
                || backgroundLevel < SuppressionLevel.Restrained) return origGpu;
            return backgroundLevel >= SuppressionLevel.Isolated
                ? Native.GpuPriorityIdle : Native.GpuPriorityBelowNormal;
        }

        private ulong DesiredAffinityOf(Entry e)
        {
            return SuppressionAffinityPolicy.DesiredAffinity(e.Reasons,
                e.BackgroundRestorePending && !AntiCheatThrottled(e) ? 0 : e.SqueezeAff, e.OrigAff, allMask);
        }

        private bool ApplyEntryLocked(IntPtr h, Entry e, int pid)
        {
            string target = FailureTargetOf(h, e);
            int identity = EntryIdentity(h, e);
            if (identity != 0) { StopEntry(e, identity == 1); return false; }
            bool retry = ActionFailureStore.IsRetryPending(FailureAction, target);
            if (retry)
            {
                if (!RestoreEntryValues(h, e))
                {
                    if (EntryIdentity(h, e) == 1) { StopEntry(e, true); return false; }
                    ActionFailureStore.Trip(FailureAction, target, "Background recovery incomplete before explicit retry");
                    StopEntry(e, false); return false;
                }
                e.GaveUp = false;
            }
            string blocked;
            if (!ActionFailureStore.TryBeginApply(FailureAction, target, out blocked))
            {
                StopEntry(e, EntrySnapshotMatches(h, e));
                return false;
            }
            bool applied;
            LastApplyError = "";
            try
            {
                identity = EntryIdentity(h, e);
                if (identity != 0) { ActionFailureStore.CancelApply(FailureAction, target); StopEntry(e, identity == 1); return false; }
                applied = ApplyEntryValues(h, e);
            }
            catch (NotSupportedException) { return CancelSkippedEntry(h, e, target, retry); }
            catch (OperationCanceledException) { return CancelSkippedEntry(h, e, target, retry); }
            catch (Exception error) { LastApplyError = error.Message; applied = false; }
            identity = EntryIdentity(h, e);
            if (identity == 1)
            { ActionFailureStore.CancelApply(FailureAction, target); if (retry) ActionFailureStore.WithdrawRetry(FailureAction, target); StopEntry(e, true); return false; }
            if (identity == 2) { LastApplyError = "Background identity unreadable after apply; recovery retained"; applied = false; }
            if (applied)
            {
                if (ActionFailureStore.CompleteApply(FailureAction, target)) { e.GaveUp = false; e.GaveUpRestored = false; return true; }
                LastApplyError = "Background completion record could not be persisted";
            }
            ActionFailureStore.Trip(FailureAction, target, string.IsNullOrEmpty(LastApplyError)
                ? "Background process apply or verification failed" : LastApplyError);
            StopEntry(e, RestoreEntryValues(h, e));
            return false;
        }
        private bool CancelSkippedEntry(IntPtr handle, Entry entry, string target, bool retry)
        {
            bool restored = EntryIdentity(handle, entry) == 1 || RestoreEntryValues(handle, entry);
            if (restored)
            { ActionFailureStore.CancelApply(FailureAction, target); if (retry) ActionFailureStore.WithdrawRetry(FailureAction, target); }
            else ActionFailureStore.Trip(FailureAction, target, "Background recovery incomplete after skipped apply");
            StopEntry(entry, restored); return false;
        }

        private static string FailureTargetOf(IntPtr h, Entry entry)
        {
            if (string.IsNullOrEmpty(entry.FailureTarget))
                entry.FailureTarget = Native.ImagePath(h) ?? entry.Name ?? "unknown-process";
            return entry.FailureTarget;
        }

        // 反作弊拒绝写入后停止巡检改写，原值退局再恢复。
        private bool GiveUpAntiCheatLocked(IntPtr h, int pid, Entry e)
        {
            if (e == null || e.GaveUp || e.OrigPri == uint.MaxValue) return false;
            if ((e.Reasons & SuppressReason.AntiCheat) == 0 || (e.Reasons & SuppressReason.Background) != 0) return false;
            string detail = LastApplyError;
            bool restored = RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg, allMask,
                e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu, e.OrigBoost);
            e.GaveUp = true;
            e.GaveUpRestored = restored;
            e.Applied = false;
            e.SqueezeAff = 0;
            e.NextReconcileTicks = long.MaxValue;
            Logger.Log(Lang.T("log.suppressioncore.giveup.1") + e.Name + " pid " + pid
                + Lang.T("log.suppressioncore.giveup.2") + (string.IsNullOrEmpty(detail) ? "" : detail)
                + Lang.T(restored ? "log.suppressioncore.giveup.3" : "log.suppressioncore.giveup.4"));
            return true;
        }

        internal static bool AffinityOnlyFailure(string detail)
        {
            if (string.IsNullOrEmpty(detail)) return false;
            foreach (string step in detail.Split(','))
                if (step != "affinity-write" && step != "affinity-readback") return false;
            return true;
        }

        // 硬件调度开启时，GPU 调度类由显卡管理。
        private static int DesiredGpu(Entry e)
        {
            return DesiredGpuClass(GpuDemoteEnabled && !HagsTweak.SchedulingActiveCached(),
                e.Reasons, e.BackgroundLevel, e.OrigGpu);
        }

        private static void ScheduleAfterMatch(Entry e, int pid)
        {
            e.ReconcileFailures = 0;
            if (e.FastReconcileRemaining > 0)
            {
                e.FastReconcileRemaining--;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(
                    e.FastReconcileRemaining > 0 ? ReconcileFastSeconds
                    : ReconcileStableBaseSeconds + PositiveMod(pid, ReconcileStableJitterSeconds)).Ticks;
                return;
            }
            e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(
                ReconcileStableBaseSeconds + PositiveMod(pid, ReconcileStableJitterSeconds)).Ticks;
        }

        private static void ScheduleAfterApply(Entry e, bool applied, int pid)
        {
            if (applied)
            {
                e.ReconcileFailures = 0;
                e.FastReconcileRemaining = 2;
                e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(ReconcileFastSeconds).Ticks;
                return;
            }
            if (e.ReconcileFailures < 8) e.ReconcileFailures++;
            int seconds = ReconcileFastSeconds;
            for (int i = 1; i < e.ReconcileFailures && seconds < ReconcileFailureCapSeconds; i++)
                seconds = Math.Min(ReconcileFailureCapSeconds, seconds * 2);
            e.NextReconcileTicks = DateTime.UtcNow.AddSeconds(seconds).Ticks;
        }

        private static int PositiveMod(int value, int modulo)
        {
            int result = value % modulo;
            return result < 0 ? result + modulo : result;
        }

        private bool ApplyThrottle(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity,
            uint[] originalCpuSets, int desiredGpu, int origBoost, bool antiCheat, ulong desiredAffinity,
            bool affinityOwned = false)
        {
            Interlocked.Increment(ref applyOperations);
            var failed = new List<string>();
            uint desiredPriority = DesiredPriority(level, originalPriority, antiCheat);
            if (Native.GetPriorityClass(h) != desiredPriority)
                if (!Native.SetPriorityClass(h, desiredPriority)) failed.Add("priority-write");
            if (failed.Count != 0) return StopFailedApply(failed);
            if (desiredGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != desiredGpu)
                {
                    if (Native.D3DKMTSetProcessSchedulingPriorityClass(h, desiredGpu) != 0) failed.Add("gpu-write");
                    else if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) != 0
                        || gpuCur != desiredGpu) failed.Add("gpu-readback");
                }
            }

            if (failed.Count != 0) return StopFailedApply(failed);

            if (!affinityOwned && !Native.CpuSetsMatch(h, originalCpuSets)
                && !Native.RestoreCpuSetsVerified(h, originalCpuSets))
                failed.Add("cpu-sets-restore");
            if (failed.Count != 0) return StopFailedApply(failed);
            if (!CpuTopology.MultiGroup && !affinityOwned)
            {
                ulong originalAllowed = originalAffinity != 0 ? originalAffinity : allMask;
                bool squeezing = desiredAffinity != originalAllowed;
                if (Native.QueryAffinity(h) != desiredAffinity
                    && !Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity))
                    failed.Add(squeezing ? "affinity-write" : "affinity-restore");
                if (Native.QueryAffinity(h) != desiredAffinity)
                    failed.Add(squeezing ? "affinity-readback" : "affinity-restore-readback");
            }
            if (failed.Count != 0) return StopFailedApply(failed);
            int io = DesiredIoPriority(level);
            if (Native.QueryIoPriority(h) != io
                && !Native.TrySetIoPriority(h, io))
                failed.Add("io-write");
            if (failed.Count != 0) return StopFailedApply(failed);
            int pg = DesiredPagePriority(level, antiCheat);
            if (Native.QueryPagePriority(h) != pg
                && !Native.TrySetPagePriority(h, pg))
                failed.Add("page-write");
            if (failed.Count != 0) return StopFailedApply(failed);
            if (Native.PowerThrottlingSupported)
            {
                bool sealTimer = DesiredTimerSeal(level, antiCheat);
                uint want = Native.EcoQoSWantMask(sealTimer);
                int qosControl;
                int qosState;
                bool queried = Native.TryQueryPowerThrottling(h, out qosControl, out qosState);
                bool overSealed = queried && !sealTimer && ((uint)qosControl & 4u) != 0;
                if ((!queried || ((uint)qosControl & want) != want || ((uint)qosState & want) != want || overSealed)
                    && !Native.ApplyEcoQoS(h, sealTimer))
                    failed.Add("eco-write");
            }
            if (failed.Count != 0) return StopFailedApply(failed);
            // 仅撤销本程序禁用的唤醒提升，原始禁用或未知状态保持不变。
            if (origBoost == 0 && Native.QueryBoostDisabled(h) == 1
                && !Native.TrySetBoostDisabled(h, false)) failed.Add("boost-unwrite");
            if (failed.Count != 0) return StopFailedApply(failed);
            if (Native.GetPriorityClass(h) != desiredPriority) failed.Add("priority-readback");
            if (Native.QueryIoPriority(h) != io) failed.Add("io-readback");
            if (Native.QueryPagePriority(h) != pg) failed.Add("page-readback");
            if (Native.PowerThrottlingSupported && !EcoStateVisible(h)) failed.Add("eco-readback");
            LastApplyError = string.Join(",", failed.ToArray());
            // 不裁剪反作弊工作集，避免重新缺页延长扫描。
            if (failed.Count == 0 && !antiCheat) WsTrim.MaybeTrim(h);
            return failed.Count == 0;
        }

        private bool StopFailedApply(List<string> failed)
        { LastApplyError = string.Join(",", failed.ToArray()); return false; }

        private static bool EcoStateVisible(IntPtr h)
        {
            return ConfirmEcoState(delegate
            {
                int qosControl, qosState;
                return Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                    && (qosControl & 1) != 0 && (qosState & 1) != 0;
            }, delegate { Thread.Sleep(1); });
        }

        internal static bool ConfirmEcoState(Func<bool> query, Action wait)
        {
            // 回读最多短暂让出三次，失败后走普通退避。
            for (int attempt = 0; attempt < 4; attempt++)
            {
                if (query()) return true;
                if (attempt < 3) wait();
            }
            return false;
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, new uint[0]);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets)
        {

            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, -1, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState, int gpu)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
        {
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, boost, true);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState, int gpu, int boost, bool restorePlacement)
        {
            bool ok = !restorePlacement || Native.RestoreCpuSetsVerified(h, cpuSets);
            uint desiredPriority = pri == 0 || pri == uint.MaxValue ? Native.NORMAL_PRIORITY_CLASS : pri;
            ok &= Native.SetPriorityClass(h, desiredPriority);
            ulong desiredAffinity = aff != 0 ? aff : allMask;
            if (restorePlacement && !CpuTopology.MultiGroup && Native.QueryAffinity(h) != desiredAffinity)
                ok &= Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity);
            int rio = io >= 0 ? io : 2; ok &= Native.TrySetIoPriority(h, rio);
            int rpg = pg >= 0 ? pg : 5; ok &= Native.TrySetPagePriority(h, rpg);
            if (Native.PowerThrottlingSupported)
                ok &= Native.RestorePowerThrottling(h, qosControl, qosState);
            if (boost >= 0)
            {
                if (Native.QueryBoostDisabled(h) != boost) ok &= Native.TrySetBoostDisabled(h, boost == 1);
                ok &= Native.QueryBoostDisabled(h) == boost;
            }
            ok &= Native.GetPriorityClass(h) == desiredPriority;
            ok &= Native.QueryIoPriority(h) == rio;
            ok &= Native.QueryPagePriority(h) == rpg;
            if (restorePlacement && !CpuTopology.MultiGroup) ok &= Native.QueryAffinity(h) == desiredAffinity;
            if (gpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != gpu)
                {
                    Native.D3DKMTSetProcessSchedulingPriorityClass(h, gpu);
                    ok &= Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur == gpu;
                }
            }
            return ok;
        }

        private RestoreResult RestoreOne(int pid, Entry e)
        {
            IntPtr h = Native.OpenProcess(Native.PROCESS_SET_INFORMATION | Native.PROCESS_SET_LIMITED_INFORMATION
                | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                IntPtr hq = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (hq == IntPtr.Zero)
                    return Native.LastOpenProcessFailureWasNoSuchProcess()
                        ? RestoreResult.Gone
                        : RestoreResult.Protected;
                try
                {
                    if (!Native.StillActive(hq)) return RestoreResult.Gone;
                    string nm = Native.ImageName(hq);
                    if (nm != null && !SameName(nm, e.Name)) return RestoreResult.Gone;
                    long creation, cpu; ulong io;
                    if (e.Creation > 0
                        && Native.QueryProcessSample(hq, out creation, out cpu, out io)
                        && creation != e.Creation)
                        return RestoreResult.Gone;
                    return RestoreResult.Protected;
                }
                finally { Native.CloseHandle(hq); }
            }
            try
            {
                if (!Native.StillActive(h)) return RestoreResult.Gone;
                if (e.Creation <= 0) return RestoreResult.Protected;
                if (e.Name != null)
                {
                    string cur = Native.ImageName(h);
                    if (cur == null) return RestoreResult.Protected;
                    if (!SameName(cur, e.Name)) return RestoreResult.Gone;
                }
                long creation, cpu; ulong io;
                if (e.Creation > 0)
                {
                    if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return RestoreResult.Protected;
                    if (creation != e.Creation) return RestoreResult.Gone;
                }
                if (RestoreBackgroundPlacement(h, e) && RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg,
                        allMask, e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState,
                        e.OrigGpu, e.OrigBoost, !e.BackgroundPlacementOwned && !e.AutomaticLayoutYielded))
                    return RestoreResult.Restored;
                return Native.StillActive(h) ? RestoreResult.Protected : RestoreResult.Gone;
            }
            finally { Native.CloseHandle(h); }
        }

        private static bool SameProcess(IntPtr h, Entry e)
        {
            if (e.Creation <= 0) return false;
            if (e.Name != null)
            {
                string cur = Native.ImageName(h);
                if (cur == null || !SameName(cur, e.Name)) return false;
            }
            if (e.Creation > 0)
            {
                long creation, cpu; ulong io;
                if (!Native.QueryProcessSample(h, out creation, out cpu, out io)) return false;
                if (creation != e.Creation) return false;
            }
            return true;
        }

        private static bool SameName(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        internal const string SelfProtectedDetail = "self-protected";

        internal static bool FullyBlockedDetail(string detail)
        {
            return detail != null
                && detail.Contains("priority-write")
                && detail.Contains("io-write")
                && detail.Contains("page-write");
        }

        internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg,
            uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
        {
            if (!SnapshotMatchesCurrent(h, pri, aff, io, pg, cpuSets, qosControl, qosState, gpu)) return false;
            if (boost >= 0)
            {
                int now = Native.QueryBoostDisabled(h);
                if (now != boost) return false;
            }
            return true;
        }

        internal static bool SnapshotMatchesCurrent(IntPtr h, uint pri, ulong aff, int io, int pg,
            uint[] cpuSets, int qosControl, int qosState, int gpu)
        {
            if (pri == 0 || pri == uint.MaxValue || io < 0 || pg < 0) return false;
            if (Native.GetPriorityClass(h) != pri) return false;
            if (Native.QueryIoPriority(h) != io) return false;
            if (Native.QueryPagePriority(h) != pg) return false;
            if (!CpuTopology.MultiGroup && Native.QueryAffinity(h) != aff) return false;
            if (!Native.CpuSetsMatch(h, cpuSets ?? new uint[0])) return false;
            if (qosControl >= 0 && Native.PowerThrottlingSupported)
            {
                int qc, qs;
                if (!Native.TryQueryPowerThrottling(h, out qc, out qs)
                    || qc != qosControl || qs != qosState) return false;
            }
            if (gpu >= 0)
            {
                int g;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out g) != 0 || g != gpu) return false;
            }
            return true;
        }

        private bool TryNeutralizeUnwritableLocked(IntPtr h, int pid, Entry e)
        {
            if (e == null || e.OrigPri == uint.MaxValue) return false;
            if (!FullyBlockedDetail(LastApplyError)) return false;
            if (Native.PowerThrottlingSupported)
                Native.RestorePowerThrottling(h, e.OrigQoSControl, e.OrigQoSState);
            if (e.OrigBoost == 0) Native.TrySetBoostDisabled(h, false);
            if (e.OrigGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != e.OrigGpu)
                    Native.D3DKMTSetProcessSchedulingPriorityClass(h, e.OrigGpu);
            }
            if (!SnapshotMatchesCurrent(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg,
                    e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu, e.OrigBoost))
                return false;

            e.OrigPri = uint.MaxValue;
            e.Applied = false;
            e.NextRetryTicks = 0;
            PersistJournalLocked();
            bool newlyListed = SelfProtectedRoster.Mark(e.Name);
            if (newlyListed || ShouldLogProtected(e.Name + "-unwritable"))
                Logger.Log(Lang.T("log.suppressioncoreapply.1") + e.Name + " pid " + pid
                    + Lang.T("log.suppressioncoreapply.2")
                    + (newlyListed ? Lang.T("log.suppressioncoreapply.3") : ""));
            return true;
        }
    }
}
