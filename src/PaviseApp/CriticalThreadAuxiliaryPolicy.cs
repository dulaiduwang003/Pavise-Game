using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace PaviseApp
{
    internal sealed class CriticalThreadAuxiliaryCandidate
    {
        internal HardThreadIdentity Thread;
        internal int Windows;
        internal double MeanShare, PeakShare;
        internal ulong AllowedMask;
    }

    internal sealed class CriticalThreadAuxiliaryEvidence
    {
        internal ThreadRelationSnapshot Relation;
        internal long ObservedTicks;
    }

    internal sealed class CriticalThreadAuxiliaryInput
    {
        internal int Pid;
        internal long Birth, Now;
        internal HardThreadIdentity Primary;
        internal ulong PrimaryMask, Allowed, Reserved, Favored, Performance, Crowded;
        internal ulong[] Physical, Domains;
        internal CriticalThreadAuxiliaryCandidate[] Candidates;
        internal CriticalThreadAuxiliaryEvidence[] Evidence;
        internal bool ConcurrencyKnown;
        internal int MaximumConcurrentRunnable;
    }

    internal sealed class CriticalThreadAuxiliaryPlan
    {
        internal readonly HardThreadChoice[] WorkerPlacements;
        internal readonly ulong[] PreferredMasks;
        internal readonly ulong AuxiliaryPhysicalMask, PoolMask;
        internal readonly double MeanDemand, PeakDemand;
        internal readonly string Detail;
        internal bool Admitted { get { return WorkerPlacements.Length != 0; } }

        internal CriticalThreadAuxiliaryPlan(HardThreadChoice[] placements, ulong[] preferred, ulong physical,
            double mean, double peak, string detail)
        {
            WorkerPlacements = placements; PreferredMasks = preferred; AuxiliaryPhysicalMask = physical;
            PoolMask = preferred.Aggregate(0UL, (mask, value) => mask | value);
            MeanDemand = mean; PeakDemand = peak; Detail = detail;
        }
    }

    // 辅助成员共享双核允许范围，保留池内迁移；普通工作线程仍可使用辅助核。
    internal static class CriticalThreadAuxiliaryPolicy
    {
        internal const int MaximumCandidates = 3, PreferredCoreCount = 2, MinimumWindows = 3;
        internal const long MaximumEvidenceAge = 65L * TimeSpan.TicksPerSecond;
        internal const double MinimumShare = 0.01;
        internal const double MaximumPoolMean = 1.20, MaximumPoolDemand = 1.60;

        private sealed class Qualified
        {
            internal CriticalThreadAuxiliaryCandidate Candidate;
            internal ThreadRelationSnapshot Relation;
            internal double Demand;
            internal long Strength;
        }

        internal static CriticalThreadAuxiliaryPlan Select(CriticalThreadAuxiliaryInput input)
        {
            if (input == null || input.Pid <= 0 || input.Birth <= 0 || input.Now <= 0 || input.Primary == null
                || input.PrimaryMask == 0 || (input.PrimaryMask & (input.PrimaryMask - 1)) != 0)
                return Empty("辅助池未准入：主线程身份或目标未核验");
            string topologyReason;
            ulong[] physical, preferred;
            ulong auxiliaryPhysical;
            if (!SelectCores(input, out physical, out preferred, out auxiliaryPhysical, out topologyReason))
                return Empty("辅助池未准入：" + topologyReason);
            if (input.Candidates == null || input.Candidates.Length == 0 || input.Candidates.Length > 512
                || input.Evidence == null || input.Evidence.Length == 0 || input.Evidence.Length > 16)
                return Empty("辅助池未准入：缺少有界候选或直接关系观察", preferred, auxiliaryPhysical);
            if (input.ConcurrencyKnown && (input.MaximumConcurrentRunnable < 1 || input.MaximumConcurrentRunnable > PreferredCoreCount))
                return Empty("辅助池未准入：已核验同时就绪需求超过双核容量", preferred, auxiliaryPhysical);

            // 较新的无效观察不能被旧成功样本掩盖；同一输入最多保留十六份有界观察。
            CriticalThreadAuxiliaryEvidence latest = input.Evidence.Where(x => x != null)
                .OrderByDescending(x => x.ObservedTicks).FirstOrDefault();
            string evidenceReason;
            if (latest == null) return Empty("辅助池未准入：最新关系观察缺失", preferred, auxiliaryPhysical);
            if (!EvidenceUsable(input, latest, out evidenceReason))
                return Empty("辅助池未准入：最新观察" + evidenceReason, preferred, auxiliaryPhysical);

            var rejected = new List<string>();
            int rejectedCount = 0;
            var qualified = new List<Qualified>();
            var duplicateTids = new HashSet<int>(input.Candidates.Where(x => x != null && x.Thread != null)
                .GroupBy(x => x.Thread.Tid).Where(x => x.Count() != 1).Select(x => x.Key));
            foreach (CriticalThreadAuxiliaryCandidate candidate in input.Candidates)
            {
                string reason = CandidateFailure(input, candidate, duplicateTids);
                ThreadRelationSnapshot relation = null;
                if (reason == null)
                {
                    foreach (CriticalThreadAuxiliaryEvidence evidence in input.Evidence.OrderByDescending(x => x == null ? 0 : x.ObservedTicks))
                    {
                        string ignored;
                        if (evidence == null || !EvidenceUsable(input, evidence, out ignored)) continue;
                        ThreadRelationMetrics worker = evidence.Relation.Worker;
                        if (worker != null && worker.Tid == candidate.Thread.Tid && worker.Creation == candidate.Thread.Creation)
                        { relation = evidence.Relation; break; }
                    }
                    if (relation == null) reason = "没有当前主线程的有效直接交接证据";
                }
                if (reason != null)
                { Reject(rejected, ref rejectedCount, candidate, reason); continue; }
                double runnable = (relation.Worker.RunningMs + relation.Worker.ReadyMs) / relation.DurationMs;
                double demand = Math.Max(candidate.PeakShare, Math.Max(candidate.MeanShare, runnable));
                qualified.Add(new Qualified { Candidate = candidate, Relation = relation, Demand = demand,
                    Strength = (long)relation.ForwardFirst + relation.ForwardSecond + relation.ReverseFirst + relation.ReverseSecond });
            }

            var placements = new List<HardThreadChoice>();
            var accepted = new List<string>();
            ulong poolMask = preferred[0] | preferred[1];
            double poolMean = 0, poolDemand = 0;
            foreach (Qualified entry in qualified.OrderByDescending(x => x.Strength)
                .ThenByDescending(x => x.Candidate.MeanShare).ThenBy(x => x.Candidate.Thread.Tid))
            {
                CriticalThreadAuxiliaryCandidate candidate = entry.Candidate;
                if (placements.Count >= MaximumCandidates)
                { Reject(rejected, ref rejectedCount, candidate, "候选数量达到三条上限"); continue; }
                // 缺少组级并发证据时，按所有成员同时就绪计算容量。
                if (!input.ConcurrencyKnown && placements.Count >= PreferredCoreCount)
                { Reject(rejected, ref rejectedCount, candidate, "辅助组同时就绪上界未知，按成员数保守减额"); continue; }
                if (poolMean + candidate.MeanShare > MaximumPoolMean || poolDemand + entry.Demand > MaximumPoolDemand)
                { Reject(rejected, ref rejectedCount, candidate, "辅助域保守需求超出余量"); continue; }
                if ((candidate.AllowedMask & poolMask) != poolMask)
                { Reject(rejected, ref rejectedCount, candidate, "原线程范围不包含完整双核辅助池"); continue; }
                placements.Add(new HardThreadChoice(candidate.Thread, poolMask));
                poolMean += candidate.MeanShare; poolDemand += entry.Demand;
                accepted.Add("线程=" + candidate.Thread.Tid + "/" + candidate.Thread.Creation
                    + "；允许池=0x" + poolMask.ToString("X") + "；均值=" + Percent(candidate.MeanShare)
                    + "；保守需求=" + Percent(entry.Demand) + "；证据=直接双向交接");
            }
            string detail = "辅助池候选=" + placements.Count + "/" + MaximumCandidates + "；辅助物理核=0x"
                + physical[0].ToString("X") + ",0x" + physical[1].ToString("X") + "；" + topologyReason
                + "；总均值=" + Percent(poolMean) + "；总保守需求=" + Percent(poolDemand)
                + "；同时就绪上界=" + (input.ConcurrencyKnown ? input.MaximumConcurrentRunnable : placements.Count)
                + "；并发来源=" + (input.ConcurrencyKnown ? "已核验组上界" : "按成员数保守估计")
                + "；范围=共享双核允许范围，普通工作线程仍共享；成员可在池内迁移"
                + "；准入=" + (accepted.Count == 0 ? "无" : string.Join(" | ", accepted.ToArray()))
                + "；拒绝=" + rejectedCount + (rejected.Count == 0 ? "" : "；明细=" + string.Join(" | ", rejected.ToArray()));
            return new CriticalThreadAuxiliaryPlan(placements.ToArray(), preferred, auxiliaryPhysical, poolMean, poolDemand, detail);
        }

        private static bool SelectCores(CriticalThreadAuxiliaryInput input, out ulong[] selectedPhysical,
            out ulong[] preferred, out ulong selectedMask, out string reason)
        {
            selectedPhysical = preferred = new ulong[0]; selectedMask = 0; reason = "拓扑或游戏范围未核验";
            if (input.Physical == null || input.Physical.Length == 0 || input.Physical.Length > 64 || input.Allowed == 0
                || (input.PrimaryMask & input.Allowed) != input.PrimaryMask) return false;
            ulong all = 0;
            foreach (ulong core in input.Physical)
            {
                if (core == 0 || (all & core) != 0) return false;
                all |= core;
            }
            if ((input.Allowed & ~all) != 0) return false;
            ulong primaryCore = input.Physical.FirstOrDefault(x => (x & input.PrimaryMask) != 0);
            if (primaryCore == 0) return false;
            ulong seen = 0, primaryDomain = 0;
            foreach (ulong domain in input.Domains ?? new ulong[0])
            {
                if (domain == 0 || (domain & seen) != 0 || (domain & ~all) != 0
                    || input.Physical.Any(x => (x & domain) != 0 && (x & domain) != x))
                { reason = "缓存域拓扑冲突"; return false; }
                seen |= domain;
                if ((primaryCore & domain) == primaryCore) primaryDomain = domain;
            }
            ulong[] available = input.Physical.Where(x => (x & input.Allowed) != 0
                && (x & (input.Reserved | primaryCore)) == 0).ToArray();
            if (available.Length < PreferredCoreCount + 2)
            { reason = "主核及辅助双核之外不足两个普通物理核"; return false; }
            ulong[] eligible = available.Where(x => input.Performance == 0 || (x & input.Performance) == x).ToArray();
            if (eligible.Length < PreferredCoreCount)
            { reason = "可用性能物理核不足两个"; return false; }
            selectedPhysical = eligible.OrderByDescending(x => primaryDomain != 0 && (x & primaryDomain) == x)
                .ThenByDescending(x => (x & input.Crowded) == 0).ThenByDescending(x => (x & input.Favored) != 0)
                .ThenByDescending(x => (x & 1UL) == 0).ThenByDescending(x => x).Take(PreferredCoreCount).ToArray();
            preferred = selectedPhysical.Select(x => x & input.Allowed)
                .Select(x => (x & input.Favored) != 0 ? x & input.Favored : x)
                .Select(x => x & (~x + 1)).ToArray();
            selectedMask = selectedPhysical[0] | selectedPhysical[1];
            reason = primaryDomain != 0 && selectedPhysical.All(x => (x & primaryDomain) == x)
                ? "辅助核与主核同L3" : primaryDomain == 0 ? "缓存域未知，按独立物理核分散" : "同L3容量不足，辅助核跨域";
            return true;
        }

        private static string CandidateFailure(CriticalThreadAuxiliaryInput input, CriticalThreadAuxiliaryCandidate candidate, HashSet<int> duplicateTids)
        {
            if (candidate == null || candidate.Thread == null) return "辅助线程身份缺失";
            if (candidate.Thread.Tid == input.Primary.Tid) return "不能将主线程或其复用线程号列为辅助";
            if (duplicateTids.Contains(candidate.Thread.Tid)) return "候选线程号或出生身份重复";
            if (candidate.Windows < MinimumWindows) return "稳定观察窗口不足三次";
            if (!Finite(candidate.MeanShare) || !Finite(candidate.PeakShare) || candidate.MeanShare < MinimumShare
                || candidate.PeakShare + 1e-9 < candidate.MeanShare || candidate.PeakShare > 1.1) return "CPU需求摘要未核验";
            if (candidate.AllowedMask == 0 || (candidate.AllowedMask & ~input.Allowed) != 0) return "原线程允许范围未核验";
            return null;
        }

        private static bool EvidenceUsable(CriticalThreadAuxiliaryInput input, CriticalThreadAuxiliaryEvidence evidence, out string reason)
        {
            reason = "时间无效或已经过期";
            if (evidence == null || evidence.ObservedTicks <= 0 || evidence.ObservedTicks > input.Now
                || input.Now - evidence.ObservedTicks > MaximumEvidenceAge) return false;
            ThreadRelationSnapshot relation = evidence.Relation;
            reason = "未通过关系采样有效性核验";
            if (relation == null || !relation.Valid || relation.LostEvents != 0) return false;
            reason = "进程或主线程身份不匹配";
            if (relation.Pid != input.Pid || relation.Birth != input.Birth || relation.Presenter == null || relation.Worker == null
                || relation.Presenter.Tid != input.Primary.Tid || relation.Presenter.Creation != input.Primary.Creation
                || relation.Worker.Tid <= 0 || relation.Worker.Creation <= 0 || relation.Worker.Tid == input.Primary.Tid) return false;
            reason = "缺少两半窗口的直接双向交接";
            if (Math.Min(Math.Min(relation.ForwardFirst, relation.ForwardSecond), Math.Min(relation.ReverseFirst, relation.ReverseSecond)) < 3) return false;
            reason = "呈现或固定时间窗口证据不足";
            if (relation.PresentIntervals < 16 || !Finite(relation.DurationMs) || relation.DurationMs < 1000 || relation.DurationMs > 5000)
                return false;
            reason = "角色状态覆盖不足或存在冲突";
            if (!MetricsUsable(relation.Presenter, relation.DurationMs) || !MetricsUsable(relation.Worker, relation.DurationMs)) return false;
            reason = null; return true;
        }

        private static bool MetricsUsable(ThreadRelationMetrics metrics, double duration)
        {
            return metrics.StateConflicts == 0 && Finite(metrics.RunningMs) && Finite(metrics.ReadyMs) && Finite(metrics.KnownMs)
                && metrics.RunningMs > 0 && metrics.ReadyMs >= 0 && metrics.KnownMs >= duration * 0.90
                && metrics.KnownMs <= duration * 1.001 && metrics.RunningMs + metrics.ReadyMs <= metrics.KnownMs + 0.001;
        }

        private static void Reject(List<string> details, ref int count, CriticalThreadAuxiliaryCandidate candidate, string reason)
        {
            count++;
            if (details.Count < 8) details.Add("线程=" + (candidate == null || candidate.Thread == null ? "未知" : candidate.Thread.Tid + "/" + candidate.Thread.Creation)
                + "；原因=" + reason);
        }

        private static CriticalThreadAuxiliaryPlan Empty(string reason, ulong[] preferred = null, ulong physical = 0)
        { return new CriticalThreadAuxiliaryPlan(new HardThreadChoice[0], preferred ?? new ulong[0], physical, 0, 0, reason); }
        private static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        private static string Percent(double value) { return (value * 100).ToString("F2", CultureInfo.InvariantCulture) + "%"; }
    }
}
