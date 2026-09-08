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

            // 反作弊按显式落点运行；后台不再绑核，只恢复旧版留下的限制。
            if (!Native.CpuSetsMatch(h, originalCpuSets ?? new uint[0])) return false;
            // 落点被拒过的条目亲和归进程自己管 不再拿它判断是否漂移
            if (!CpuTopology.MultiGroup && !affinityOwned && Native.QueryAffinity(h) != desiredAffinity) return false;
            return true;
        }

        // 反作弊压制的强力档把饿死那部分去掉了 ACE 这类扫描型反作弊扫游戏内存时会挂起游戏线程
        //   IDLE 优先级让它在 CPU 满载时几乎分不到时间片 挂起窗口从几百毫秒拖到几秒 玩家看到的就是卡死
        //   定时器精度封顶把它的节流睡眠放大十几倍 页优先级 1 让扫描一路缺页 都是同一个放大器
        //   Apply 里保留的优先级提升只救锁等待 救不了被挂起的线程 所以不能喂这么狠
        //   极低磁盘 IO 留着 扫盘才是主要伤害 EcoQoS 小核限频也留着 压制的核心成分没变
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

        // 反作弊使用显式落点，普通后台及旧重压记录回到原亲和性
        private ulong DesiredAffinityOf(Entry e)
        {
            return SuppressionAffinityPolicy.DesiredAffinity(e.Reasons, e.SqueezeAff, e.OrigAff, allMask);
        }

        // 巡检重写走这里 落点被拒且只有亲和这一环失败时放弃落点 其余旋钮照常
        //   反作弊进程会自己把亲和改回去再拒绝写入 不放弃就会每轮退避到顶后一分钟一条异常
        private bool ApplyEntryLocked(IntPtr h, Entry e, int pid)
        {
            bool applied = ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e),
                e.OrigBoost, AntiCheatThrottled(e), DesiredAffinityOf(e), e.SqueezeRefused);
            if (applied || e.SqueezeAff == 0 || !AffinityOnlyFailure(LastApplyError)) return applied;
            e.SqueezeAff = 0;
            e.SqueezeRefused = true;
            Logger.Log(Lang.T("log.suppressioncore.squeeze.1") + e.Name + " pid " + pid
                + Lang.T("log.suppressioncore.squeeze.2"));
            return ApplyThrottle(h, e.Level, e.OrigPri, e.OrigAff, e.OrigCpuSets, DesiredGpu(e),
                e.OrigBoost, AntiCheatThrottled(e), DesiredAffinityOf(e), true);
        }

        // 反作弊条目写不进去就放弃 反作弊有自保护 反复重试只会刷日志 已写进去的按原值退回 退不回的退局再试
        private bool GiveUpAntiCheatLocked(IntPtr h, int pid, Entry e)
        {
            if (e == null || e.GaveUp || e.OrigPri == uint.MaxValue) return false;
            if ((e.Reasons & SuppressReason.AntiCheat) == 0 || (e.Reasons & SuppressReason.Background) != 0) return false;
            string detail = LastApplyError;
            bool restored = RunMutation(delegate
            {
                return RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg, allMask,
                    e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState, e.OrigGpu, e.OrigBoost);
            });
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

        // 硬件调度开着时进程调度类归 GPU 管 写了没用还会记一次 gpu-write 失败
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
            BeginMutation();
            try
            {
            Interlocked.Increment(ref applyOperations);
            var failed = new List<string>();
            uint desiredPriority = DesiredPriority(level, originalPriority, antiCheat);
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

            // 反作弊按显式落点运行；后台不再绑核，只恢复旧版留下的限制。
            if (!Native.CpuSetsMatch(h, originalCpuSets)
                && !Native.RestoreCpuSetsVerified(h, originalCpuSets))
                failed.Add("cpu-sets-restore");
            // 落点被拒过的条目亲和归进程自己管 写了只会再被拒 其余旋钮照常
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
            int io = DesiredIoPriority(level);
            if (Native.QueryIoPriority(h) != io
                && !Native.TrySetIoPriority(h, io))
                failed.Add("io-write");
            int pg = DesiredPagePriority(level, antiCheat);
            if (Native.QueryPagePriority(h) != pg
                && !Native.TrySetPagePriority(h, pg))
                failed.Add("page-write");
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
            // 2.0.1 起隔离不再关闭动态优先级提升 只把早前版本关掉的还原回来
            //   那是 Windows 对优先级反转的快速救济 游戏等被隔离进程放锁时靠它瞬间抬人跑完
            //   关掉之后只剩每秒一轮的反饥饿兜底 用户看到的就是偶发约一秒的整帧冻结
            //   提升幅度一到八级 从 IDLE 抬完仍低于游戏的 HIGH 抢不走游戏正在用的核
            if (origBoost == 0)
            {
                int boostNow = Native.QueryBoostDisabled(h);
                if (boostNow == 1 && !Native.TrySetBoostDisabled(h, false))
                    failed.Add("boost-unwrite");
            }
            if (Native.GetPriorityClass(h) != desiredPriority) failed.Add("priority-readback");
            if (Native.QueryIoPriority(h) != io) failed.Add("io-readback");
            if (Native.QueryPagePriority(h) != pg) failed.Add("page-readback");
            if (Native.PowerThrottlingSupported && !EcoStateVisible(h)) failed.Add("eco-readback");
            LastApplyError = string.Join(",", failed.ToArray());
            // 修剪放在整套旋钮全部落位之后 反作弊进程不修剪
            //   扫描进程的页面被清掉后重新缺页 只会把扫描拖长
            if (failed.Count == 0 && !antiCheat) WsTrim.MaybeTrim(h);
            return failed.Count == 0;
            }
            finally { EndMutation(); }
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
            return RestoreValues(h, pri, aff, io, pg, allMask, cpuSets, qosControl, qosState, gpu, -1);
        }

        public static bool RestoreValues(IntPtr h, uint pri, ulong aff, int io, int pg, ulong allMask,
            uint[] cpuSets, int qosControl, int qosState, int gpu, int boost)
        {
            bool ok = Native.RestoreCpuSetsVerified(h, cpuSets);
            uint desiredPriority = pri == 0 || pri == uint.MaxValue ? Native.NORMAL_PRIORITY_CLASS : pri;
            ok &= Native.SetPriorityClass(h, desiredPriority);
            ulong desiredAffinity = aff != 0 ? aff : allMask;
            // 已经在原值上就不写 自己改回亲和并拒写的进程不该因此判成还原失败
            if (!CpuTopology.MultiGroup && Native.QueryAffinity(h) != desiredAffinity)
                ok &= Native.SetProcessAffinityMask(h, (UIntPtr)desiredAffinity);
            int rio = io >= 0 ? io : 2; ok &= Native.TrySetIoPriority(h, rio);
            int rpg = pg >= 0 ? pg : 5; ok &= Native.TrySetPagePriority(h, rpg);
            if (Native.PowerThrottlingSupported)
                ok &= Native.RestorePowerThrottling(h, qosControl, qosState);
            if (boost >= 0) ok &= Native.TrySetBoostDisabled(h, boost == 1);
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
                if (RunMutation(delegate
                    {
                        return RestoreValues(h, e.OrigPri, e.OrigAff, e.OrigIo, e.OrigPg,
                            allMask, e.OrigCpuSets, e.OrigQoSControl, e.OrigQoSState,
                            e.OrigGpu, e.OrigBoost);
                    }))
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
                if (now >= 0 && now != boost) return false;
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
            BeginMutation();
            try
            {
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
            finally { EndMutation(); }
        }
    }
}
