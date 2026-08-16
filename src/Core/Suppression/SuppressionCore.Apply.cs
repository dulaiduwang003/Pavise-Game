// @author bdth 2074055628@qq.com
// 文件用途 压制核心施加分部 句柄级写入 核验 还原与不可写中和

using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        private bool ThrottleMatches(IntPtr h, SuppressionLevel level, uint originalPriority, ulong originalAffinity,
            uint[] originalCpuSets, int desiredGpu)
        {
            uint desiredPriority = DesiredPriority(level, originalPriority);
            if (Native.GetPriorityClass(h) != desiredPriority) return false;
            if (desiredGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0
                    && gpuCur != desiredGpu) return false;
            }
            int desiredIo = level >= SuppressionLevel.Isolated ? 0 : 1;
            int desiredPage = level >= SuppressionLevel.Isolated ? 1 : 3;
            if (Native.QueryIoPriority(h) != desiredIo || Native.QueryPagePriority(h) != desiredPage) return false;

            if (Native.PowerThrottlingSupported)
            {
                int qosControl, qosState;
                if (!Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                    || (qosControl & 1) == 0 || (qosState & 1) == 0) return false;
            }

            if (level >= SuppressionLevel.Isolated)
            {
                if (CpuTopology.HasEffectiveBackgroundPartition())
                {
                    uint[] backgroundCpuSets = CpuTopology.EffectiveBackgroundCpuSetIds();
                    bool cpuSetsMatch = backgroundCpuSets != null && backgroundCpuSets.Length > 0
                        && Native.CpuSetsMatch(h, backgroundCpuSets);
                    bool affinityFallback = !CpuTopology.MultiGroup && Native.QueryAffinity(h) == throttleMask;
                    if (!cpuSetsMatch && !affinityFallback) return false;
                }
                if (SqueezeBackground && !CpuTopology.MultiGroup)
                {
                    ulong squeeze = CpuTopology.BackgroundSqueezeMask();
                    if (squeeze != 0 && Native.QueryAffinity(h) != squeeze) return false;
                }
            }
            else
            {
                if (!Native.CpuSetsMatch(h, originalCpuSets ?? new uint[0])) return false;
                if (!CpuTopology.MultiGroup)
                {
                    ulong desiredAffinity = originalAffinity != 0 ? originalAffinity : allMask;
                    if (Native.QueryAffinity(h) != desiredAffinity) return false;
                }
            }
            return true;
        }

        internal static uint DesiredPriority(SuppressionLevel level, uint originalPriority)
        {
            uint desired = originalPriority == 0 || originalPriority == uint.MaxValue
                ? Native.NORMAL_PRIORITY_CLASS : originalPriority;
            if (level >= SuppressionLevel.Restrained)
                desired = level >= SuppressionLevel.Isolated
                    ? Native.IDLE_PRIORITY_CLASS : Native.BELOW_NORMAL_PRIORITY_CLASS;
            return desired;
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

        private static int DesiredGpu(Entry e)
        {
            return DesiredGpuClass(GpuDemoteEnabled, e.Reasons, e.BackgroundLevel, e.OrigGpu);
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
            uint[] originalCpuSets, int desiredGpu)
        {
            Interlocked.Increment(ref applyOperations);
            var failed = new List<string>();
            uint desiredPriority = DesiredPriority(level, originalPriority);
            if (Native.GetPriorityClass(h) != desiredPriority)
                if (!Native.SetPriorityClass(h, desiredPriority)) failed.Add("priority-write");
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

            if (level >= SuppressionLevel.Isolated)
            {
                if (CpuTopology.HasEffectiveBackgroundPartition())
                {
                    uint[] backgroundCpuSets = CpuTopology.EffectiveBackgroundCpuSetIds();
                    if (!Native.CpuSetsMatch(h, backgroundCpuSets))
                    {
                        bool soft = Native.TrySetCpuSets(h, backgroundCpuSets);
                        if (soft && !Native.CpuSetsMatch(h, backgroundCpuSets))
                            failed.Add("cpu-sets-readback");
                        if (!soft && !CpuTopology.MultiGroup)
                        {
                            if (Native.QueryAffinity(h) != throttleMask
                                && !Native.SetProcessAffinityMask(h, (UIntPtr)throttleMask))
                                failed.Add("affinity-write");
                            if (Native.QueryAffinity(h) != throttleMask)
                                failed.Add("affinity-readback");
                        }
                        else if (!soft)
                            failed.Add("cpu-sets-write");
                    }
                }

                if (SqueezeBackground && !CpuTopology.MultiGroup)
                {
                    ulong squeeze = CpuTopology.BackgroundSqueezeMask();
                    if (squeeze != 0)
                    {
                        if (Native.QueryAffinity(h) != squeeze
                            && !Native.SetProcessAffinityMask(h, (UIntPtr)squeeze))
                            failed.Add("affinity-write");
                        if (Native.QueryAffinity(h) != squeeze)
                            failed.Add("affinity-readback");
                    }
                }
            }
            else
            {
                if (!Native.CpuSetsMatch(h, originalCpuSets)
                    && !Native.RestoreCpuSetsVerified(h, originalCpuSets))
                    failed.Add("cpu-sets-restore");
                if (!CpuTopology.MultiGroup)
                {
                    ulong desiredAffinity = originalAffinity != 0 ? originalAffinity : allMask;
                    if (Native.QueryAffinity(h) != desiredAffinity
                        && !Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity))
                        failed.Add("affinity-restore");
                    if (Native.QueryAffinity(h) != desiredAffinity)
                        failed.Add("affinity-restore-readback");
                }
            }
            int io = level >= SuppressionLevel.Isolated ? 0 : 1;
            if (Native.QueryIoPriority(h) != io
                && !Native.TrySetIoPriority(h, io))
                failed.Add("io-write");
            int pg = level >= SuppressionLevel.Isolated ? 1 : 3;
            if (Native.QueryPagePriority(h) != pg
                && !Native.TrySetPagePriority(h, pg))
                failed.Add("page-write");
            if (Native.PowerThrottlingSupported)
            {
                int qosControl;
                int qosState;
                if ((!Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                        || (qosControl & 1) == 0 || (qosState & 1) == 0)
                    && !Native.ApplyEcoQoS(h))
                    failed.Add("eco-write");
            }
            if (Native.GetPriorityClass(h) != desiredPriority) failed.Add("priority-readback");
            if (Native.QueryIoPriority(h) != io) failed.Add("io-readback");
            if (Native.QueryPagePriority(h) != pg) failed.Add("page-readback");
            if (Native.PowerThrottlingSupported && !EcoStateVisible(h)) failed.Add("eco-readback");
            LastApplyError = string.Join(",", failed.ToArray());
            return failed.Count == 0;
        }

        private static bool EcoStateVisible(IntPtr h)
        {
            for (int attempt = 0; ; attempt++)
            {
                int qosControl, qosState;
                if (Native.TryQueryPowerThrottling(h, out qosControl, out qosState)
                    && (qosControl & 1) != 0 && (qosState & 1) != 0) return true;
                if (attempt >= 80) return false;
                if (attempt < 77) Thread.SpinWait(1000);
                else Thread.Sleep(1);
            }
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
            bool ok = Native.RestoreCpuSetsVerified(h, cpuSets);
            uint desiredPriority = pri == 0 || pri == uint.MaxValue ? Native.NORMAL_PRIORITY_CLASS : pri;
            ok &= Native.SetPriorityClass(h, desiredPriority);
            ulong desiredAffinity = aff != 0 ? aff : allMask;
            if (!CpuTopology.MultiGroup) ok &= Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity);
            int rio = io >= 0 ? io : 2; ok &= Native.TrySetIoPriority(h, rio);
            int rpg = pg >= 0 ? pg : 5; ok &= Native.TrySetPagePriority(h, rpg);
            if (Native.PowerThrottlingSupported)
                ok &= Native.RestorePowerThrottling(h, qosControl, qosState);
            ok &= Native.GetPriorityClass(h) == desiredPriority;
            ok &= Native.QueryIoPriority(h) == rio;
            ok &= Native.QueryPagePriority(h) == rpg;
            if (!CpuTopology.MultiGroup) ok &= Native.QueryAffinity(h) == desiredAffinity;
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
                if (RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg, allMask, e.OrigCpuSets,
                        e.OrigQoSControl, e.OrigQoSState, e.OrigGpu))
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
            if (e.OrigGpu >= 0)
            {
                int gpuCur;
                if (Native.D3DKMTGetProcessSchedulingPriorityClass(h, out gpuCur) == 0 && gpuCur != e.OrigGpu)
                    Native.D3DKMTSetProcessSchedulingPriorityClass(h, e.OrigGpu);
            }
            if (!SnapshotMatchesCurrent(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg,
                    e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu))
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
