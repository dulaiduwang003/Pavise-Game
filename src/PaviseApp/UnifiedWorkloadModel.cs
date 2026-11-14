using System;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
    internal sealed class UnifiedWorkloadEdge
    {
        internal readonly int Source, Target, First, Second;
        internal UnifiedWorkloadEdge(int source, int target, int first, int second)
        { Source = source; Target = target; First = first; Second = second; }
        internal bool Stable { get { return First >= 3 && Second >= 3; } }
    }

    internal enum UnifiedWorkloadKind { Unknown, SerialCandidate, Presenter, Dependency, ParallelWorker }

    internal sealed class UnifiedWorkloadNode
    {
        internal readonly HardThreadIdentity Thread;
        internal readonly UnifiedWorkloadKind Kind;
        internal readonly ThreadRelationMetrics Metrics;
        internal readonly double CpuShare;
        internal readonly bool Priority;
        internal readonly int PriorityLimit;
        internal readonly string Reason;
        internal UnifiedWorkloadNode(ThreadRelationMetrics metrics, UnifiedWorkloadKind kind, double share, bool priority, string reason)
        {
            Metrics = metrics; Thread = new HardThreadIdentity(metrics.Tid, metrics.Creation);
            Kind = kind; CpuShare = share; Priority = priority; PriorityLimit = 1; Reason = reason;
        }
        internal string Describe()
        {
            return "线程=" + Thread.Key + " 分类=" + Kind + " 单核占用="
                + (100 * CpuShare).ToString("F1", CultureInfo.InvariantCulture) + "% 迁移=" + Metrics.ProcessorMigrations
                + " Ready毫秒=" + Metrics.ReadyMs.ToString("F3", CultureInfo.InvariantCulture) + " 依据=" + Reason;
        }
    }

    internal sealed class UnifiedWorkloadSet
    {
        internal readonly bool Valid;
        internal readonly string Reason;
        internal readonly UnifiedWorkloadNode Main, Secondary;
        internal readonly UnifiedWorkloadNode[] Critical, Pool, Observed;
        internal readonly HardThreadIdentity[] Roles;
        internal readonly double CandidateCoverage, TotalRunningCores;
        internal readonly ThreadRelationMetrics[] Ledger;
        internal readonly double LedgerCoverage, WindowMs;
        internal readonly bool LedgerComplete;
        internal readonly bool LedgerCapacityComplete;
        internal readonly string CapacityGap;
        internal readonly bool Complete;
        internal readonly int TrackedThreads;
        internal UnifiedWorkloadSet(bool valid, string reason, UnifiedWorkloadNode main, UnifiedWorkloadNode secondary,
            UnifiedWorkloadNode[] observed, double coverage, double totalCores, bool complete, int tracked,
            ThreadRelationMetrics[] ledger = null, double ledgerCoverage = 0, double windowMs = 0, bool ledgerComplete = false,
            bool? ledgerCapacityComplete = null, string capacityGap = null)
        {
            CapacityGap = capacityGap ?? "";
            Valid = valid; Reason = reason ?? ""; Main = main; Secondary = secondary;
            Observed = observed ?? new UnifiedWorkloadNode[0];
            Critical = Observed.Where(node => node.Priority).ToArray();
            Pool = Observed.Where(node => node != main && node != secondary && node.CpuShare >= .10).ToArray();
            Roles = Observed.Select(node => node.Thread).ToArray();
            CandidateCoverage = coverage; TotalRunningCores = totalCores; Complete = complete; TrackedThreads = tracked;
            Ledger = ledger == null ? new ThreadRelationMetrics[0] : (ThreadRelationMetrics[])ledger.Clone();
            LedgerCoverage = ledgerCoverage; WindowMs = windowMs; LedgerComplete = ledgerComplete;
            LedgerCapacityComplete = ledgerCapacityComplete ?? ledgerComplete;
        }
        internal string Describe()
        {
            return "全线程工作图；固定身份=" + TrackedThreads + " 深入候选=" + Observed.Length
                + " CPU贡献覆盖=" + (CandidateCoverage * 100).ToString("F1", CultureInfo.InvariantCulture) + "%"
                + " 已观测CPU时间账本覆盖=" + (LedgerCoverage * 100).ToString("F1", CultureInfo.InvariantCulture) + "%"
                + " 账本身份完整=" + LedgerComplete
                + " 容量账本无身份负载缺口=" + LedgerCapacityComplete + (CapacityGap.Length == 0 ? "" : "（" + CapacityGap + "）")
                + " 累计运行核数=" + TotalRunningCores.ToString("F2", CultureInfo.InvariantCulture)
                + " 主候选=" + (Main == null ? "无" : Main.Thread.Key)
                + " 副候选=" + (Secondary == null ? "无" : Secondary.Thread.Key)
                + " 关键组=" + Critical.Length + " 并行池=" + Pool.Length + "；" + Reason
                + "；分类为调度候选，不等于已证明引擎主线程或收益；账本覆盖仅指已记录运行时间，不证明全窗实际负载或吞吐";
        }
    }
}
