using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
    internal static class UnifiedWorkloadDiscovery
    {
        internal const int MaximumCandidates = 32, MaximumEdges = 128;

        internal static UnifiedWorkloadSet Select(UnifiedThreadCaptureEvidence evidence, ThreadTimes current,
            int pid, long birth, long now)
        {
            ThreadRelationSnapshot relation = evidence == null ? null : evidence.Relation;
            string failure = evidence == null || relation == null ? Logger.Text("尚未取得全线程工作图", "full-thread workload graph not yet available")
                : evidence.Pid != pid || evidence.Birth != birth || relation.Pid != pid || relation.Birth != birth ? Logger.Text("工作图进程身份不匹配", "workload graph process identity mismatch")
                : now < evidence.Observed || now - evidence.Observed > TimeSpan.FromSeconds(65).Ticks ? Logger.Text("工作图已超过准入新鲜度", "workload graph exceeds admission freshness")
                : !SnapshotKnown(current, birth, now) ? Logger.Text("当前全线程身份快照不可核验", "current full-thread identity snapshot cannot be verified")
                : !relation.WorkloadValid || relation.LostEvents != 0 ? Logger.Text("工作图不完整：", "workload graph incomplete: ") + relation.WorkloadReason
                : relation.WorkloadThreads.Length > MaximumCandidates || relation.WorkloadEdges.Length > MaximumEdges
                    || relation.WorkloadLedger.Length > 16384 ? Logger.Text("工作图超过候选、身份或关系预算", "workload graph exceeds the candidate, identity or relation budget")
                : !Finite(relation.DurationMs) || relation.DurationMs <= 0 || relation.FirstHalfDurationMs <= 0
                    || relation.SecondHalfDurationMs <= 0 || !Finite(relation.WorkloadTotalRunningMs)
                    || relation.WorkloadTotalRunningMs < 0 ? Logger.Text("工作图窗口或CPU总账不可核验", "workload graph window or CPU ledger total cannot be verified") : null;
            if (failure != null) return Empty(failure);
            var currentIdentities = new Dictionary<int, long>();
            for (int index = 0; index < current.Ids.Length; index++) currentIdentities.Add((int)current.Ids[index], current.Created[index]);
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var metrics = new List<ThreadRelationMetrics>();
            foreach (ThreadRelationMetrics value in relation.WorkloadThreads)
            {
                if (!MetricKnown(value, relation.DurationMs))
                    return Empty(Logger.Text("工作图包含无效线程指标", "workload graph contains invalid thread metrics"));
                var identity = new HardThreadIdentity(value.Tid, value.Creation);
                if (!identities.Add(identity.Key)) return Empty(Logger.Text("工作图包含重复线程身份", "workload graph contains duplicate thread identities"));
                long currentBirth;
                if (currentIdentities.TryGetValue(identity.Tid, out currentBirth) && currentBirth == identity.Creation) metrics.Add(value);
            }
            if (metrics.Count == 0) return Empty(Logger.Text("工作图候选均已退出或无法确认", "all workload graph candidates exited or cannot be confirmed"));
            if (metrics.Sum(value => value.RunningMs) > relation.WorkloadTotalRunningMs + .000001)
                return Empty(Logger.Text("详细候选CPU时间超过全线程总账", "detailed candidate CPU time exceeds the full-thread ledger total"));
            var ledger = new List<ThreadRelationMetrics>(relation.WorkloadLedger.Length);
            var ledgerIds = new HashSet<int>();
            var ledgerIdentities = new Dictionary<int, long>();
            bool ledgerCapacityComplete = relation.WorkloadLedgerCapacityComplete;
            // 按来源统计容量缺口 仅用于诊断 不参与准入
            int exitedGapThreads = 0, newcomerGapThreads = 0;
            double exitedGapRunning = 0;
            foreach (ThreadRelationMetrics value in relation.WorkloadLedger)
            {
                if (!MetricKnown(value, relation.DurationMs) || !ledgerIds.Add(value.Tid)) return Empty(Logger.Text("全量CPU账本指标或线程身份重复", "full CPU ledger metric or thread identity duplicated"));
                ledgerIdentities.Add(value.Tid, value.Creation);
                long currentBirth;
                if (currentIdentities.TryGetValue(value.Tid, out currentBirth) && currentBirth == value.Creation) ledger.Add(value);
                else if (!value.KnownZeroRunning(relation.DurationMs, relation.FirstHalfDurationMs, relation.SecondHalfDurationMs))
                { ledgerCapacityComplete = false; exitedGapThreads++; exitedGapRunning += value.RunningMs; }
            }
            bool newIdentity = false;
            for (int index = 0; index < current.Ids.Length; index++)
            {
                long sampledBirth;
                if (ledgerIdentities.TryGetValue((int)current.Ids[index], out sampledBirth) && sampledBirth == current.Created[index]) continue;
                newIdentity = true;
                // 新身份累计 CPU 非零但缺少窗口账本时 不能计为空闲容量
                if (current.Cpu[index] > 0) ledgerCapacityComplete = false;
                if (current.Cpu[index] > 0) newcomerGapThreads++;
            }
            string capacityGap = ledgerCapacityComplete ? "" : Logger.Text("采集时身份未核验=", "unverifiedAtCapture=") + relation.WorkloadCapacityGapThreads + Logger.Text("线程/", " threads/")
                + relation.WorkloadCapacityGapRunningMs.ToString("F1", CultureInfo.InvariantCulture) + Logger.Text("ms 采集后退出=", "ms exitedAfterCapture=") + exitedGapThreads + Logger.Text("线程/", " threads/")
                + exitedGapRunning.ToString("F1", CultureInfo.InvariantCulture) + Logger.Text("ms 采集后新建且已运行=", "ms newAndRunningAfterCapture=") + newcomerGapThreads + Logger.Text("线程", " threads")
                + (relation.OverflowedThreads > 0 ? Logger.Text(" 单线程记录溢出=", " threadRecordOverflow=") + relation.OverflowedThreads + Logger.Text("线程", " threads") : "");
            double ledgerRunning = ledger.Sum(value => value.RunningMs);
            if (ledgerRunning > relation.WorkloadTotalRunningMs + .000001) return Empty(Logger.Text("全量CPU账本超过窗口总账", "full CPU ledger exceeds the window total"));
            var ids = new HashSet<int>(metrics.Select(value => value.Tid));
            UnifiedWorkloadEdge[] edges = relation.WorkloadEdges.Where(edge => edge != null && edge.Stable
                && edge.Source != edge.Target && ids.Contains(edge.Source) && ids.Contains(edge.Target)).ToArray();
            int presenter = evidence.Valid && evidence.StreamsKnown && evidence.Streams == 1 && evidence.Presenter != null
                && ids.Contains(evidence.Presenter.Tid) ? evidence.Presenter.Tid : 0;
            var connected = new HashSet<int>();
            if (presenter != 0) connected.Add(presenter);
            // 有界传播只提供依赖线索 不把传递唤醒当作已经证明的帧关键路径
            for (int depth = 0; depth < 2 && presenter != 0; depth++)
            {
                var prior = new HashSet<int>(connected);
                foreach (UnifiedWorkloadEdge edge in edges)
                    if (prior.Contains(edge.Source) || prior.Contains(edge.Target))
                    { connected.Add(edge.Source); connected.Add(edge.Target); }
            }
            var nodes = new List<UnifiedWorkloadNode>();
            foreach (ThreadRelationMetrics value in metrics)
            {
                double share = value.RunningMs / relation.DurationMs;
                bool stable = Stable(relation, value);
                bool hot = stable && Hot(relation, value);
                bool anchor = value.Tid == presenter;
                bool linked = connected.Contains(value.Tid) || presenter == 0 && edges.Any(edge => edge.Source == value.Tid || edge.Target == value.Tid);
                int peers = metrics.Count(peer => peer.Tid != presenter && Stable(relation, peer) && Hot(relation, peer)
                    && Math.Abs(peer.RunningMs / relation.DurationMs - share) <= .10);
                bool pooled = !anchor && hot && peers >= 3;
                UnifiedWorkloadKind kind = anchor ? UnifiedWorkloadKind.Presenter
                    : pooled ? UnifiedWorkloadKind.ParallelWorker
                    : hot ? UnifiedWorkloadKind.SerialCandidate
                    : stable && linked ? UnifiedWorkloadKind.Dependency : UnifiedWorkloadKind.Unknown;
                bool priority = stable && linked && (hot || value.ReadyMs > 0 || edges.Any(edge => edge.Source == value.Tid));
                string nodeReason = !stable ? Logger.Text("两半状态覆盖或身份稳定性不足，仅保留观察", "insufficient state coverage or identity stability in both halves, observation only")
                    : pooled ? Logger.Text("多条相近持续负载，作为并行工作组候选保留宽运行池，不逐条独占", "multiple similar sustained loads, kept as parallel worker group candidates with a wide run pool, not pinned one by one")
                    : anchor ? Logger.Text("单一来源呈现身份；它只是候选之一，不固定为主核", "single-source presenter identity; only one of the candidates, not pinned to the main core")
                    : hot && linked ? Logger.Text("两半持续运行并存在稳定关系线索，可作为非呈现串行候选", "sustained running in both halves with stable relation hints, eligible as a non-presenter serial candidate")
                    : hot ? Logger.Text("持续高运行量，帧依赖尚未确认；不凭CPU排名接管主副核", "sustained high running time, frame dependency not yet confirmed; main/secondary cores are not taken by CPU rank")
                    : linked ? Logger.Text("稳定唤醒依赖候选，低CPU占用不作为忽略理由", "stable wake dependency candidate, low CPU usage is not a reason to ignore it") : Logger.Text("未知或阶段性工作，保留原优先级", "unknown or phased work, original priority retained");
                nodes.Add(new UnifiedWorkloadNode(value, kind, share, priority, nodeReason));
            }
            UnifiedWorkloadNode[] eligible = nodes.Where(node => Stable(relation, node.Metrics) && Hot(relation, node.Metrics)
                && (node.Thread.Tid == presenter || connected.Contains(node.Thread.Tid)
                    || presenter == 0 && edges.Any(edge => edge.Source == node.Thread.Tid || edge.Target == node.Thread.Tid))
                && node.Kind != UnifiedWorkloadKind.ParallelWorker)
                .OrderByDescending(node => node.CpuShare + (node.Thread.Tid == presenter ? .02 : .08))
                .ThenBy(node => node.Thread.Tid).ToArray();
            UnifiedWorkloadNode main = eligible.FirstOrDefault();
            // 两角色平均运行量之和超过一核 才有直接的并行容量线索 避免为串行交接强占两核
            UnifiedWorkloadNode secondary = main == null ? null : eligible.Skip(1)
                .FirstOrDefault(node => main.CpuShare + node.CpuShare >= 1.05);
            // 第三条同理 三条关键串行线程合计超过两核 放在两颗核上必然互相排队 才值得再独占一颗
            // 只到三条为止 常见引擎没有第四条帧关键串行线程 其余并行工作线程需要的是更多核而不是绑核
            UnifiedWorkloadNode tertiary = secondary == null ? null : eligible
                .Where(node => node != main && node != secondary)
                .FirstOrDefault(node => main.CpuShare + secondary.CpuShare + node.CpuShare >= 2.05);
            double observed = nodes.Sum(node => node.Metrics.RunningMs);
            double total = relation.WorkloadTotalRunningMs;
            double coverage = total > 0 ? Math.Min(1, observed / total) : 0;
            bool complete = !relation.WorkloadTruncated && metrics.Count == relation.WorkloadThreads.Length
                && relation.WorkloadThreads.Length == relation.WorkloadLedger.Length;
            bool ledgerComplete = relation.WorkloadLedgerComplete && ledger.Count == relation.WorkloadLedger.Length && !newIdentity;
            string reason = main == null ? Logger.Text("没有同时满足持续运行与角色关系的主候选；工作组仍完整列出，不强行挑CPU第一名", "no main candidate meets both sustained running and role relation; the workload group is still listed in full, the top CPU thread is not forced")
                : Logger.Text("持续运行与稳定关系共同选出主候选；副候选=", "main candidate selected by sustained running and stable relations; secondary=") + (secondary == null ? Logger.Text("无", "none") : secondary.Thread.Key)
                    + Logger.Text("；第三候选=", "; tertiary=") + (tertiary == null ? Logger.Text("无", "none") : tertiary.Thread.Key) + Logger.Text("；并行工作组保留宽核心池", "; parallel worker group keeps a wide core pool");
            return new UnifiedWorkloadSet(true, reason, main, secondary, nodes.ToArray(), coverage,
                total / relation.DurationMs, complete, relation.WorkloadThreadCount, ledger.ToArray(),
                total > 0 ? Math.Min(1, ledgerRunning / total) : 0, relation.DurationMs, ledgerComplete, ledgerCapacityComplete, capacityGap, tertiary);
        }

        private static UnifiedWorkloadSet Empty(string reason)
        { return new UnifiedWorkloadSet(false, reason, null, null, null, 0, 0, false, 0); }

        private static bool Hot(ThreadRelationSnapshot relation, ThreadRelationMetrics value)
        { return value.RunningFirstMs >= relation.FirstHalfDurationMs * .30 && value.RunningSecondMs >= relation.SecondHalfDurationMs * .30; }

        internal static bool Stable(ThreadRelationSnapshot relation, ThreadRelationMetrics value)
        {
            return value.StateConflicts == 0 && Finite(value.KnownMs) && Finite(value.RunningFirstMs) && Finite(value.RunningSecondMs)
                && Finite(value.KnownFirstMs) && Finite(value.KnownSecondMs)
                && value.KnownFirstMs >= relation.FirstHalfDurationMs * .90
                && value.KnownSecondMs >= relation.SecondHalfDurationMs * .90
                && value.KnownFirstMs <= relation.FirstHalfDurationMs + .000001
                && value.KnownSecondMs <= relation.SecondHalfDurationMs + .000001
                && value.RunningFirstMs >= 0 && value.RunningSecondMs >= 0;
        }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        private static bool MetricKnown(ThreadRelationMetrics value, double duration)
        {
            return value != null && value.Tid > 0 && value.Creation > 0 && Finite(value.RunningMs)
                && Finite(value.ReadyMs) && Finite(value.KnownMs) && value.RunningMs >= 0 && value.ReadyMs >= 0
                && value.KnownMs >= 0 && value.StateConflicts >= 0 && value.ProcessorMigrations >= 0
                && value.RunningMs + value.ReadyMs <= value.KnownMs + .000001
                && value.RunningMs <= duration + .000001 && value.KnownMs <= duration + .000001;
        }

        private static bool SnapshotKnown(ThreadTimes current, long birth, long now)
        {
            if (current == null || birth <= 0 || current.Creation != birth || current.Ticks <= 0 || now < current.Ticks
                || now - current.Ticks > TimeSpan.FromMilliseconds(1500).Ticks || current.ProcessCpu < 0
                || current.Ids == null || current.Created == null || current.Cpu == null || current.Ids.Length == 0
                || current.Ids.Length > 16384 || current.Ids.Length != current.Created.Length || current.Ids.Length != current.Cpu.Length) return false;
            var ids = new HashSet<long>();
            for (int index = 0; index < current.Ids.Length; index++)
                if (current.Ids[index] <= 0 || current.Ids[index] > int.MaxValue || current.Created[index] < birth
                    || current.Cpu[index] < 0 || !ids.Add(current.Ids[index])) return false;
            return true;
        }

    }
}
