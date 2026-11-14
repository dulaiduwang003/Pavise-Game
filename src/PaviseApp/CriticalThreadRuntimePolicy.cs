using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PaviseApp
{
    internal static class CriticalThreadRuntimePolicy
    {
        private struct SampleThreadIdentity : IEquatable<SampleThreadIdentity>
        {
            private readonly long tid, creation;

            internal SampleThreadIdentity(long tid, long creation)
            { this.tid = tid; this.creation = creation; }

            public bool Equals(SampleThreadIdentity other)
            { return tid == other.tid && creation == other.creation; }

            public override bool Equals(object value)
            { return value is SampleThreadIdentity && Equals((SampleThreadIdentity)value); }

            public override int GetHashCode()
            { unchecked { return tid.GetHashCode() * 397 ^ creation.GetHashCode(); } }
        }

        internal static HardThreadChoice[] Select(CriticalThreadRule rule, CriticalThreadHotspot[] hotspots, CriticalThreadRoleFact[] roles)
        {
            if (rule == null || hotspots == null)
            {
                return new HardThreadChoice[0];
            }
            roles = roles ?? new CriticalThreadRoleFact[0];
            CriticalThreadHotspot[] items = (from x in hotspots
                where x != null && x.Tid > 0 && x.Creation > 0 && x.Windows >= 3 && x.CpuShare >= 0.35
                orderby roles.Any((CriticalThreadRoleFact y) => y.Tid == x.Tid && y.Creation == x.Creation && y.Role == "presentation-associated") descending, roles.Any((CriticalThreadRoleFact y) => y.Tid == x.Tid && y.Creation == x.Creation) descending, x.CpuShare descending, x.Tid
                select x).ToArray();
            if (items.Length < rule.CandidateCount || items.Select((CriticalThreadHotspot x) => x.Tid).Distinct().Count() != items.Length)
            {
                return new HardThreadChoice[0];
            }
            ulong[] masks = rule.Targets;
            return items.Take(masks.Length).Select((CriticalThreadHotspot x, int index) => new HardThreadChoice(new HardThreadIdentity(x.Tid, x.Creation), masks[index])).ToArray();
        }

        internal static bool Contains(ThreadTimes sample, HardThreadIdentity thread)
        {
            int processedCount = 0;
            while (sample != null && processedCount < sample.Ids.Length)
            {
                if (sample.Ids[processedCount] == thread.Tid && sample.Created[processedCount] == thread.Creation)
                {
                    return true;
                }
                processedCount++;
            }
            return false;
        }

        internal static HardThreadIdentity[] Batch(ThreadTimes sample, HardThreadPlan plan, HardThreadOwnership[] receipts, out HardThreadCoverage coverage)
        {
            if (plan.WorkerMode == HardThreadWorkerMode.CandidatesOnly)
            {
                coverage = HardThreadCoverage.CandidatesOnly;
                return plan.Choices.Select((HardThreadChoice x) => x.Thread).ToArray();
            }
            if (plan.RequireWorkerCoverage)
            {
                coverage = HardThreadCoverage.Partial;
                if (sample == null || !CoreThreadTracker.ValidShape(sample) || sample.Ids.Length > 512
                    || sample.Ids.Any((long tid) => tid <= 0 || tid > int.MaxValue) || sample.Created.Any((long birth) => birth <= 0))
                    return new HardThreadIdentity[0];
                HashSet<SampleThreadIdentity> identities = new HashSet<SampleThreadIdentity>();
                for (int index = 0; index < sample.Ids.Length; index++)
                    if (!identities.Add(new SampleThreadIdentity(sample.Ids[index], sample.Created[index])))
                        return new HardThreadIdentity[0];
                HardThreadChoice[] selected = plan.Choices;
                if (selected.Any((HardThreadChoice x) => !Contains(sample, x.Thread)))
                    return new HardThreadIdentity[0];
                HardThreadIdentity[] ordered = new HardThreadIdentity[sample.Ids.Length];
                HardThreadIdentity[] candidates = new HardThreadIdentity[selected.Length];
                int workerCount = 0, candidateCount = 0;
                for (int index = 0; index < sample.Ids.Length; index++)
                {
                    HardThreadIdentity thread = new HardThreadIdentity((int)sample.Ids[index], sample.Created[index]);
                    if (plan.IsCandidate(thread)) candidates[candidateCount++] = thread;
                    else ordered[workerCount++] = thread;
                }
                Array.Copy(candidates, 0, ordered, workerCount, candidateCount);
                coverage = HardThreadCoverage.Complete;
                return ordered;
            }
            HashSet<string> hashSet = new HashSet<string>((receipts ?? new HardThreadOwnership[0]).Select((HardThreadOwnership x) => x.Thread.Key), StringComparer.Ordinal);
            List<HardThreadIdentity> list = new List<HardThreadIdentity>();
            int processedCount = 0;
            HardThreadChoice[] choices = plan.Choices;
            foreach (HardThreadChoice hardThreadChoice in choices)
            {
                list.Add(hardThreadChoice.Thread);
            }
            for (int j = 0; j < sample.Ids.Length; j++)
            {
                HardThreadIdentity hardThreadIdentity = new HardThreadIdentity((int)sample.Ids[j], sample.Created[j]);
                if (plan.IsCandidate(hardThreadIdentity))
                {
                    continue;
                }
                bool isInHashSet = hashSet.Contains(hardThreadIdentity.Key);
                if ((isInHashSet || processedCount < 8) && list.Count < 512)
                {
                    list.Add(hardThreadIdentity);
                    if (!isInHashSet)
                    {
                        processedCount++;
                    }
                }
            }
            coverage = ((list.Count != sample.Ids.Length) ? HardThreadCoverage.Partial : HardThreadCoverage.Complete);
            return list.ToArray();
        }

        internal static bool Verified(ThreadTimes sample, HardThreadPlan plan, HardThreadOwnership[] receipts, long auditStartedUtc, out int candidates, out int workers, out bool complete)
        {
            candidates = (workers = 0);
            complete = false;
            if (plan == null || sample == null || !CoreThreadTracker.ValidShape(sample) || auditStartedUtc <= 0 || receipts == null || receipts.Length > 512)
            {
                return false;
            }
            HashSet<SampleThreadIdentity> hashSet = new HashSet<SampleThreadIdentity>();
            int eligibleReceipts = 0;
            foreach (HardThreadOwnership hardThreadOwnership in receipts)
            {
                if (hardThreadOwnership == null || hardThreadOwnership.Thread == null
                    || !hashSet.Add(new SampleThreadIdentity(hardThreadOwnership.Thread.Tid, hardThreadOwnership.Thread.Creation)))
                {
                    return false;
                }
                if (hardThreadOwnership.Group == plan.Group && hardThreadOwnership.VerifiedUtcTicks >= auditStartedUtc)
                    eligibleReceipts++;
            }
            HashSet<SampleThreadIdentity> sampleIdentities = null;
            if (eligibleReceipts >= 8 && sample.Ids.Length >= 16 && eligibleReceipts * 2 >= sample.Ids.Length)
            {
                sampleIdentities = new HashSet<SampleThreadIdentity>();
                for (int index = 0; index < sample.Ids.Length; index++)
                    sampleIdentities.Add(new SampleThreadIdentity(sample.Ids[index], sample.Created[index]));
            }
            HardThreadOwnership[] whereItems = receipts.Where((HardThreadOwnership x) => x != null && x.Group == plan.Group
                && x.VerifiedUtcTicks >= auditStartedUtc && (sampleIdentities == null ? Contains(sample, x.Thread)
                    : sampleIdentities.Contains(new SampleThreadIdentity(x.Thread.Tid, x.Thread.Creation)))).ToArray();
            HardThreadOwnership[] hardThreadOwnershipItems = whereItems;
            foreach (HardThreadOwnership hardThreadOwnership2 in hardThreadOwnershipItems)
            {
                if (plan.IsCandidate(hardThreadOwnership2.Thread))
                {
                    if (hardThreadOwnership2.Mask == plan.MaskFor(hardThreadOwnership2.Thread))
                    {
                        candidates++;
                    }
                }
                else if (plan.WorkerMode == HardThreadWorkerMode.AvoidWorkers && (plan.IsWorkerPlacement(hardThreadOwnership2.Thread)
                    ? hardThreadOwnership2.Mask == plan.MaskFor(hardThreadOwnership2.Thread)
                    : plan.PreserveOriginalMasks
                        ? hardThreadOwnership2.Mask != 0 && (hardThreadOwnership2.Mask & ~plan.MaskFor(hardThreadOwnership2.Thread)) == 0
                        : hardThreadOwnership2.Mask == plan.MaskFor(hardThreadOwnership2.Thread)))
                {
                    workers++;
                }
            }
            complete = plan.WorkerMode == HardThreadWorkerMode.AvoidWorkers && candidates == plan.Choices.Length && candidates + workers == sample.Ids.Length;
            return candidates == plan.Choices.Length && (!plan.RequireWorkerCoverage || complete);
        }

        internal static bool NewWorkersPending(ThreadTimes sample, HardThreadPlan plan, HardThreadOwnership[] receipts,
            long auditStartedUtc, HardThreadIdentity[] appliedBatch, out int candidates, out int workers, out int pendingWorkers)
        {
            candidates = workers = pendingWorkers = 0;
            if (plan == null || plan.Target == null || plan.WorkerMode != HardThreadWorkerMode.AvoidWorkers || !plan.RequireWorkerCoverage
                || !CoreThreadTracker.ValidShape(sample) || sample.Creation != plan.Target.ProcessCreation || sample.Creation <= 0
                || sample.Ticks <= 0 || sample.ProcessCpu < 0 || sample.Ids.Length == 0 || sample.Ids.Length > 512
                || auditStartedUtc <= 0 || receipts == null || receipts.Length > 512 || appliedBatch == null || appliedBatch.Length == 0 || appliedBatch.Length > 512)
                return false;

            // 本方法只分辨缺口归属；快照的单调时钟新鲜度由调用方在同一轮先行核验。
            // 任意来源出现同 TID 不同创建时间，都不能把身份冲突解释成普通新线程。
            var knownBirths = new Dictionary<int, long>();
            var batchTids = new HashSet<int>();
            foreach (HardThreadIdentity thread in appliedBatch)
                if (!RememberPendingIdentity(thread, knownBirths) || thread.Creation < sample.Creation || !batchTids.Add(thread.Tid)) return false;

            var receiptsByTid = new Dictionary<int, HardThreadOwnership>();
            foreach (HardThreadOwnership receipt in receipts)
            {
                if (receipt == null || !RememberPendingIdentity(receipt.Thread, knownBirths) || receipt.Thread.Creation < sample.Creation
                    || receiptsByTid.ContainsKey(receipt.Thread.Tid) || ReceiptCoverageFailure(plan, receipt, auditStartedUtc) != null)
                    return false;
                receiptsByTid.Add(receipt.Thread.Tid, receipt);
            }

            var sampleTids = new HashSet<int>();
            var coveredIds = new List<long>();
            var coveredBirths = new List<long>();
            var coveredCpu = new List<long>();
            int pending = 0;
            for (int index = 0; index < sample.Ids.Length; index++)
            {
                long tid = sample.Ids[index], birth = sample.Created[index];
                if (tid <= 0 || tid > int.MaxValue || birth < sample.Creation || sample.Cpu[index] < 0) return false;
                var thread = new HardThreadIdentity((int)tid, birth);
                if (!sampleTids.Add(thread.Tid) || !RememberPendingIdentity(thread, knownBirths)) return false;
                HardThreadOwnership receipt;
                if (receiptsByTid.TryGetValue(thread.Tid, out receipt))
                {
                    coveredIds.Add(tid); coveredBirths.Add(birth); coveredCpu.Add(sample.Cpu[index]);
                }
                else
                {
                    if (batchTids.Contains(thread.Tid) || plan.IsCandidate(thread) || plan.IsWorkerPlacement(thread)) return false;
                    pending++;
                }
            }

            var requiredTids = new HashSet<int>();
            foreach (HardThreadChoice choice in plan.Choices.Concat(plan.WorkerPlacements))
                if (choice == null || !RememberPendingIdentity(choice.Thread, knownBirths) || !requiredTids.Add(choice.Thread.Tid)
                    || !sampleTids.Contains(choice.Thread.Tid) || !batchTids.Contains(choice.Thread.Tid)
                    || !receiptsByTid.ContainsKey(choice.Thread.Tid)) return false;
            if (pending == 0) return false;

            // 仅剔除完全没有收据的批次外普通成员，再用完整核验确认已有成员。
            // 不放宽 Verified，也不把待准入成员计入已覆盖数或补充任何失败预算。
            var covered = new ThreadTimes { Ticks = sample.Ticks, Creation = sample.Creation, ProcessCpu = sample.ProcessCpu,
                Ids = coveredIds.ToArray(), Created = coveredBirths.ToArray(), Cpu = coveredCpu.ToArray() };
            int verifiedCandidates, verifiedWorkers;
            bool complete;
            if (!Verified(covered, plan, receipts, auditStartedUtc, out verifiedCandidates, out verifiedWorkers, out complete) || !complete)
                return false;
            candidates = verifiedCandidates;
            workers = verifiedWorkers;
            pendingWorkers = pending;
            return true;
        }

        private static bool RememberPendingIdentity(HardThreadIdentity thread, Dictionary<int, long> knownBirths)
        {
            if (thread == null || thread.Tid <= 0 || thread.Creation <= 0) return false;
            long knownBirth;
            if (knownBirths.TryGetValue(thread.Tid, out knownBirth)) return knownBirth == thread.Creation;
            knownBirths.Add(thread.Tid, thread.Creation);
            return true;
        }

        internal static string CoverageFailureDetails(ThreadTimes sample, HardThreadPlan plan, HardThreadOwnership[] receipts,
            long auditStartedUtc, HardThreadIdentity[] appliedBatch)
        {
            if (plan == null || sample == null || !CoreThreadTracker.ValidShape(sample) || auditStartedUtc <= 0 || receipts == null)
                return "；覆盖诊断=输入不可核验";
            if (sample.Ids.Length > 512 || receipts.Length > 512 || (appliedBatch != null && appliedBatch.Length > 512))
                return "；覆盖诊断=输入超过诊断上限；快照线程数=" + sample.Ids.Length + "；收据数=" + receipts.Length;

            var byIdentity = new Dictionary<SampleThreadIdentity, HardThreadOwnership>();
            var duplicateReceipts = new HashSet<SampleThreadIdentity>();
            int invalidReceipts = 0;
            foreach (HardThreadOwnership receipt in receipts)
            {
                if (receipt == null || receipt.Thread == null) { invalidReceipts++; continue; }
                var identity = new SampleThreadIdentity(receipt.Thread.Tid, receipt.Thread.Creation);
                if (byIdentity.ContainsKey(identity)) { duplicateReceipts.Add(identity); invalidReceipts++; }
                else byIdentity.Add(identity, receipt);
            }
            var batch = new HashSet<SampleThreadIdentity>();
            if (appliedBatch != null)
                foreach (HardThreadIdentity thread in appliedBatch)
                    if (thread != null) batch.Add(new SampleThreadIdentity(thread.Tid, thread.Creation));

            int missing = 0, newlySeen = 0, shown = 0;
            var details = new StringBuilder();
            var present = new HashSet<SampleThreadIdentity>();
            for (int index = 0; index < sample.Ids.Length; index++)
            {
                long tid = sample.Ids[index], creation = sample.Created[index];
                var identity = new SampleThreadIdentity(tid, creation);
                bool first = present.Add(identity);
                HardThreadIdentity thread = tid > 0 && tid <= int.MaxValue && creation > 0 ? new HardThreadIdentity((int)tid, creation) : null;
                if (thread != null && !plan.IsCandidate(thread) && plan.WorkerMode == HardThreadWorkerMode.CandidatesOnly) continue;
                HardThreadOwnership receipt;
                string reason = thread == null ? "线程身份不可核验" : !first ? "快照身份重复"
                    : duplicateReceipts.Contains(identity) ? "收据身份重复"
                    : !byIdentity.TryGetValue(identity, out receipt) ? "无收据"
                    : ReceiptCoverageFailure(plan, receipt, auditStartedUtc);
                if (reason == null) continue;
                bool afterBatch = appliedBatch != null && !batch.Contains(identity);
                missing++;
                if (afterBatch) newlySeen++;
                // 只展开前八个身份；总数仍遍历完整快照，不把省略的线程当作已经核验。
                if (shown++ < 8) AppendCoverageDetail(details, tid, creation, appliedBatch == null ? "未知" : afterBatch ? "是" : "否", reason);
            }
            foreach (HardThreadChoice candidate in plan.Choices)
            {
                if (present.Contains(new SampleThreadIdentity(candidate.Thread.Tid, candidate.Thread.Creation))) continue;
                missing++;
                if (shown++ < 8) AppendCoverageDetail(details, candidate.Thread.Tid, candidate.Thread.Creation, "不适用", "候选未出现在核验快照");
            }
            return "；覆盖诊断=未核验线程=" + missing + "；批次后新见=" + (appliedBatch == null ? "未知" : newlySeen.ToString())
                + "；异常收据数=" + invalidReceipts + "；身份明细=" + (details.Length == 0 ? "无" : details.ToString())
                + (shown > 8 ? "；其余明细已省略=" + (shown - 8) : "");
        }

        private static string ReceiptCoverageFailure(HardThreadPlan plan, HardThreadOwnership receipt, long auditStartedUtc)
        {
            if (receipt.Group != plan.Group) return "处理器组不符";
            if (receipt.VerifiedUtcTicks < auditStartedUtc) return "收据过期";
            bool matches = plan.IsCandidate(receipt.Thread) || plan.IsWorkerPlacement(receipt.Thread) ? receipt.Mask == plan.MaskFor(receipt.Thread)
                : plan.WorkerMode == HardThreadWorkerMode.AvoidWorkers && (plan.PreserveOriginalMasks
                    ? receipt.Mask != 0 && (receipt.Mask & ~plan.MaskFor(receipt.Thread)) == 0 : receipt.Mask == plan.MaskFor(receipt.Thread));
            return matches ? null : "掩码不符";
        }

        private static void AppendCoverageDetail(StringBuilder text, long tid, long creation, string afterBatch, string reason)
        {
            if (text.Length != 0) text.Append(';');
            text.Append("[tid=").Append(tid).Append(" birth=").Append(creation)
                .Append(" 批次后新见=").Append(afterBatch).Append(" 原因=").Append(reason).Append(']');
        }
    }
}
