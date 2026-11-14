using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
    internal sealed class UnifiedWorkloadPlan
    {
        internal readonly bool Valid;
        internal readonly string Reason;
        internal readonly HardThreadChoice Primary, Secondary;
        internal readonly ulong WorkerMask, ReservedPhysicalMask;
        internal readonly HardThreadIdentity[] PriorityRoles, ObservedRoles;
        internal readonly HardThreadChoice[] SelectedWorkers;

        private UnifiedWorkloadPlan(bool valid, string reason, UnifiedWorkloadSet set, HardThreadChoice primary,
            HardThreadChoice secondary, ulong workers, ulong reserved)
        {
            Valid = valid; Reason = reason; Primary = primary; Secondary = secondary;
            WorkerMask = workers; ReservedPhysicalMask = reserved;
            PriorityRoles = set == null ? new HardThreadIdentity[0] : set.Critical.Select(node => node.Thread).ToArray();
            ObservedRoles = set == null ? new HardThreadIdentity[0] : set.Roles;
            // 宽池仅分配给已确认候选，未导出线程不计入清核覆盖。
            SelectedWorkers = set == null || reserved == 0 ? new HardThreadChoice[0]
                : set.Observed.Where(node => node.CpuShare >= .10 && (primary == null || node.Thread.Key != primary.Thread.Key)
                    && (secondary == null || node.Thread.Key != secondary.Thread.Key))
                    .Select(node => new HardThreadChoice(node.Thread, workers)).ToArray();
        }

        internal static UnifiedWorkloadPlan Create(UnifiedWorkloadSet set, ulong allowed, ulong[] physicalCores,
            ulong performanceMask, ulong favoredMask)
        {
            if (set == null || !set.Valid) return new UnifiedWorkloadPlan(false, set == null ? "尚无工作组" : set.Reason, set, null, null, 0, 0);
            if (allowed == 0 || physicalCores == null || physicalCores.Length == 0)
                return new UnifiedWorkloadPlan(false, "进程范围或物理拓扑未知", set, null, null, 0, 0);
            ulong union = 0;
            var cores = new List<ulong>();
            foreach (ulong core in physicalCores)
            {
                if (core == 0 || (union & core) != 0)
                    return new UnifiedWorkloadPlan(false, "物理核心范围为空或重叠", set, null, null, 0, 0);
                union |= core;
                if ((core & allowed) != 0) cores.Add(core);
            }
            if ((allowed & ~union) != 0)
                return new UnifiedWorkloadPlan(false, "授权范围包含未知物理核心", set, null, null, 0, 0);
            if (set.Main == null || cores.Count < 3 || set.LedgerCoverage < .90 || !set.LedgerCapacityComplete)
                return new UnifiedWorkloadPlan(true, set.Main == null ? "工作组已分类，无可信主候选；保持宽池及已确认依赖组提优"
                    : cores.Count < 3 ? "可用物理核不足三个；未预留主副核，保持并行容量"
                    : set.LedgerCoverage < .90 ? "全量CPU账本覆盖不足90%；保留宽池，未缩小工作池范围"
                    : "容量账本存在身份负载缺口" + (set.CapacityGap.Length == 0 ? "" : "（" + set.CapacityGap + "）")
                        + "；保留宽池，未缩小工作池范围", set, null, null, allowed, 0);

            ulong primaryCore = ChooseCore(cores, set.Main.Metrics, performanceMask, favoredMask);
            string capacityReason;
            if (!Capacity(set, cores, allowed, union, primaryCore, performanceMask, set.Main, null, out capacityReason))
                return new UnifiedWorkloadPlan(true, "主核预留未通过容量核验：" + capacityReason + "；保留宽池", set, null, null, allowed, 0);
            HardThreadChoice primary = new HardThreadChoice(set.Main.Thread, ChooseLogical(primaryCore & allowed, set.Main.Metrics, favoredMask));
            ulong reserved = primaryCore;
            HardThreadChoice secondary = null;
            if (set.Secondary != null && cores.Count >= 6)
            {
                var remaining = cores.Where(core => (core & reserved) == 0).ToList();
                ulong secondaryCore = ChooseCore(remaining, set.Secondary.Metrics, performanceMask, favoredMask);
                string secondaryReason;
                if (Capacity(set, cores, allowed, union, reserved | secondaryCore, performanceMask, set.Main, set.Secondary, out secondaryReason))
                {
                    secondary = new HardThreadChoice(set.Secondary.Thread,
                        ChooseLogical(secondaryCore & allowed, set.Secondary.Metrics, favoredMask));
                    reserved |= secondaryCore;
                    capacityReason = secondaryReason;
                }
            }
            ulong workerMask = allowed & ~reserved;
            return new UnifiedWorkloadPlan(true, "稳定放置主候选" + (secondary == null ? "；未预留独立副核" : "及不同物理副核")
                + "；" + capacityReason + "；并行候选使用剩余宽池；仅已覆盖游戏线程避让，不声明系统级独占", set, primary, secondary, workerMask, reserved);
        }

        private static bool Capacity(UnifiedWorkloadSet set, List<ulong> cores, ulong allowed, ulong knownMask,
            ulong reserved, ulong performanceMask, UnifiedWorkloadNode primary, UnifiedWorkloadNode secondary, out string reason)
        {
            int remaining = cores.Count(core => (core & reserved) == 0);
            reason = "剩余可用物理核不足两个";
            if (remaining < 2) return false;
            reason = "全量CPU运行分布或窗口总账不可核验";
            if (!set.LedgerCapacityComplete || set.WindowMs <= 0 || double.IsNaN(set.WindowMs) || double.IsInfinity(set.WindowMs)) return false;
            double performanceTime = 0, efficiencyTime = 0, total = 0;
            foreach (ThreadRelationMetrics metric in set.Ledger)
            {
                Dictionary<int, double> distribution;
                if (metric.StateConflicts != 0 || !TryCpuRunning(metric, out distribution)) return false;
                if (metric.RunningMs == 0 && metric.KnownMs < set.WindowMs - .000001 && !metric.CpuZeroGrowthVerified)
                { reason = "零运行线程的状态窗口不完整，且没有同身份CPU零增量证明"; return false; }
                total += metric.RunningMs;
                bool fixedRole = metric.Tid == primary.Thread.Tid && metric.Creation == primary.Thread.Creation
                    || secondary != null && metric.Tid == secondary.Thread.Tid && metric.Creation == secondary.Thread.Creation;
                foreach (KeyValuePair<int, double> cpu in distribution)
                {
                    if (cpu.Value == 0) continue;
                    if (cpu.Key < 0 || cpu.Key >= 64 || (knownMask & (1UL << cpu.Key)) == 0) return false;
                    if (fixedRole) continue;
                    if (performanceMask == 0 || (performanceMask & (1UL << cpu.Key)) != 0) performanceTime += cpu.Value;
                    else efficiencyTime += cpu.Value;
                }
            }
            if (Math.Abs(total - set.TotalRunningCores * set.WindowMs) > .000001) return false;
            ulong pool = allowed & ~reserved;
            int performanceSlots = LogicalCount(performanceMask == 0 ? pool : pool & performanceMask);
            int efficiencySlots = performanceMask == 0 ? 0 : LogicalCount(pool & ~performanceMask);
            // 按同类逻辑 CPU 时间比较容量，不将 SMT 折算为物理核或跨核型换算吞吐。
            // E 核原有负载仍由 E 池承担，核型运行证据缺失时不跨池迁移。
            double performanceDemand = performanceTime / set.WindowMs, efficiencyDemand = efficiencyTime / set.WindowMs;
            reason = "同类逻辑CPU时间需求/可用=" + performanceDemand.ToString("F2", CultureInfo.InvariantCulture) + "/" + performanceSlots
                + (performanceMask == 0 ? "（同构）" : "（P），" + efficiencyDemand.ToString("F2", CultureInfo.InvariantCulture) + "/" + efficiencySlots + "（E）")
                + "；各池保留10%时间余量，未折算跨核型吞吐；容量按已观测运行时间判断";
            return performanceDemand <= performanceSlots * .90 && efficiencyDemand <= efficiencySlots * .90;
        }

        private static int LogicalCount(ulong mask)
        { int count = 0; while (mask != 0) { mask &= mask - 1; count++; } return count; }

        private static bool TryCpuRunning(ThreadRelationMetrics metric, out Dictionary<int, double> result)
        {
            result = new Dictionary<int, double>();
            if (metric.CpuRunningMs != null)
            {
                foreach (KeyValuePair<int, double> value in metric.CpuRunningMs)
                {
                    if (double.IsNaN(value.Value) || double.IsInfinity(value.Value) || value.Value < 0 || result.ContainsKey(value.Key)) return false;
                    result.Add(value.Key, value.Value);
                }
            }
            else if (metric.RunningMs > 0)
            {
                // 兼容已有内存证据；生产采集直接提供精确数值，不从三位小数诊断文本反算容量。
                foreach (string item in (metric.CpuDistribution ?? "").Split(','))
                {
                    int colon = item.IndexOf(':'); int cpu; double time;
                    if (colon < 4 || !item.StartsWith("CPU", StringComparison.Ordinal) || !item.EndsWith("ms", StringComparison.Ordinal)
                        || !int.TryParse(item.Substring(3, colon - 3), NumberStyles.None, CultureInfo.InvariantCulture, out cpu)
                        || !double.TryParse(item.Substring(colon + 1, item.Length - colon - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out time)
                        || double.IsNaN(time) || double.IsInfinity(time) || time < 0 || result.ContainsKey(cpu)) return false;
                    result.Add(cpu, time);
                }
            }
            return Math.Abs(result.Values.Sum() - metric.RunningMs) <= (metric.CpuRunningMs == null ? .001 * Math.Max(1, result.Count) : .000001);
        }

        private static ulong ChooseCore(List<ulong> cores, ThreadRelationMetrics metrics, ulong performanceMask, ulong favoredMask)
        {
            return cores.OrderByDescending(core => performanceMask != 0 && (core & performanceMask) != 0)
                .ThenByDescending(core => favoredMask != 0 && (core & favoredMask) != 0)
                .ThenByDescending(core => RunningOn(core, metrics)).ThenBy(core => core).First();
        }

        private static ulong ChooseLogical(ulong mask, ThreadRelationMetrics metrics, ulong favored)
        {
            var logical = new List<ulong>();
            for (int index = 0; index < 64; index++) if ((mask & (1UL << index)) != 0) logical.Add(1UL << index);
            return logical.OrderByDescending(cpu => (cpu & favored) != 0)
                .ThenByDescending(cpu => RunningOn(cpu, metrics)).ThenBy(cpu => cpu).First();
        }

        internal static double RunningOn(ulong mask, ThreadRelationMetrics metrics)
        {
            double total = 0;
            foreach (string item in (metrics.CpuDistribution ?? "").Split(','))
            {
                int colon = item.IndexOf(':'); int cpu; double time;
                if (colon < 4 || !item.StartsWith("CPU", StringComparison.Ordinal) || !item.EndsWith("ms", StringComparison.Ordinal)
                    || !int.TryParse(item.Substring(3, colon - 3), NumberStyles.None, CultureInfo.InvariantCulture, out cpu)
                    || cpu < 0 || cpu >= 64 || (mask & (1UL << cpu)) == 0
                    || !double.TryParse(item.Substring(colon + 1, item.Length - colon - 3), NumberStyles.Float, CultureInfo.InvariantCulture, out time)
                    || double.IsNaN(time) || double.IsInfinity(time) || time < 0) continue;
                total += time;
            }
            return total;
        }

        internal string Describe()
        {
            return "主核=" + (Primary == null ? "未预留" : Primary.Thread.Key + "/0x" + Primary.Mask.ToString("X"))
                + " 副核=" + (Secondary == null ? "未预留" : Secondary.Thread.Key + "/0x" + Secondary.Mask.ToString("X"))
                + " 并行池=0x" + WorkerMask.ToString("X") + " 已确认池成员=" + SelectedWorkers.Length
                + " 提优角色=" + PriorityRoles.Length + "；" + Reason;
        }
    }
}
