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
            return Logger.Text("线程=", "thread=") + Thread.Key + Logger.Text(" 分类=", " kind=") + Kind + Logger.Text(" 单核占用=", " singleCoreUsage=")
                + (100 * CpuShare).ToString("F1", CultureInfo.InvariantCulture) + Logger.Text("% 迁移=", "% migrations=") + Metrics.ProcessorMigrations
                + Logger.Text(" Ready毫秒=", " readyMs=") + Metrics.ReadyMs.ToString("F3", CultureInfo.InvariantCulture) + Logger.Text(" 依据=", " basis=") + Reason;
        }
    }

    internal sealed class UnifiedWorkloadSet
    {
        internal readonly bool Valid;
        internal readonly string Reason;
        internal readonly UnifiedWorkloadNode Main, Secondary;
        // 第三条帧关键串行线程（如虚幻引擎的 RHI 线程） 三者合计超过两核时才选出
        internal readonly UnifiedWorkloadNode Tertiary;
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
            bool? ledgerCapacityComplete = null, string capacityGap = null, UnifiedWorkloadNode tertiary = null)
        {
            Tertiary = secondary == null ? null : tertiary;
            CapacityGap = capacityGap ?? "";
            Valid = valid; Reason = reason ?? ""; Main = main; Secondary = secondary;
            Observed = observed ?? new UnifiedWorkloadNode[0];
            Critical = Observed.Where(node => node.Priority).ToArray();
            Pool = Observed.Where(node => node != main && node != secondary && node != Tertiary && node.CpuShare >= .10).ToArray();
            Roles = Observed.Select(node => node.Thread).ToArray();
            CandidateCoverage = coverage; TotalRunningCores = totalCores; Complete = complete; TrackedThreads = tracked;
            Ledger = ledger == null ? new ThreadRelationMetrics[0] : (ThreadRelationMetrics[])ledger.Clone();
            LedgerCoverage = ledgerCoverage; WindowMs = windowMs; LedgerComplete = ledgerComplete;
            LedgerCapacityComplete = ledgerCapacityComplete ?? ledgerComplete;
        }
        internal string Describe()
        {
            return Logger.Text("全线程工作图；固定身份=", "full-thread workload graph; trackedIdentities=") + TrackedThreads + Logger.Text(" 深入候选=", " deepCandidates=") + Observed.Length
                + Logger.Text(" CPU贡献覆盖=", " cpuContributionCoverage=") + (CandidateCoverage * 100).ToString("F1", CultureInfo.InvariantCulture) + "%"
                + Logger.Text(" 已观测CPU时间账本覆盖=", " observedCpuLedgerCoverage=") + (LedgerCoverage * 100).ToString("F1", CultureInfo.InvariantCulture) + "%"
                + Logger.Text(" 账本身份完整=", " ledgerIdentitiesComplete=") + LedgerComplete
                + Logger.Text(" 容量账本无身份负载缺口=", " capacityLedgerGapFree=") + LedgerCapacityComplete + (CapacityGap.Length == 0 ? "" : Logger.Text("（", " (") + CapacityGap + Logger.Text("）", ")"))
                + Logger.Text(" 累计运行核数=", " totalRunningCores=") + TotalRunningCores.ToString("F2", CultureInfo.InvariantCulture)
                + Logger.Text(" 主候选=", " main=") + (Main == null ? Logger.Text("无", "none") : Main.Thread.Key)
                + Logger.Text(" 副候选=", " secondary=") + (Secondary == null ? Logger.Text("无", "none") : Secondary.Thread.Key)
                + Logger.Text(" 第三候选=", " tertiary=") + (Tertiary == null ? Logger.Text("无", "none") : Tertiary.Thread.Key)
                + Logger.Text(" 关键组=", " criticalGroup=") + Critical.Length + Logger.Text(" 并行池=", " parallelPool=") + Pool.Length + Logger.Text("；", "; ") + Reason
                + Logger.Text("；分类为调度候选，不等于已证明引擎主线程或收益；账本覆盖仅指已记录运行时间，不证明全窗实际负载或吞吐", "; classified as scheduling candidates, not a proven engine main thread or benefit; ledger coverage only counts recorded running time and does not prove actual full-window load or throughput");
        }
    }
}
