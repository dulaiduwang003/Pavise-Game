using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 未写入线程的准入次数归属规则阶段，不随承载重建或配置切换重置。
    internal sealed class HardThreadWorkerAdmissionBudget
    {
        private readonly Dictionary<string, int> failures = new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly HashSet<string> Excluded = new HashSet<string>(StringComparer.Ordinal);
        // 压缩及提交核验共用累计失败额度；成功、重建承载和切回同一规则都不补充。
        internal int ReclamationFailures { get; private set; }
        internal long ReclamationNextTicks { get; private set; }
        internal bool ReclamationAvailable(long now)
        { return ReclamationFailures < 3 && now >= ReclamationNextTicks; }
        internal void ReclamationFailed(long now)
        {
            if (ReclamationFailures < 3) ReclamationFailures++;
            long delay = 30L * TimeSpan.TicksPerSecond;
            ReclamationNextTicks = now > long.MaxValue - delay ? long.MaxValue : now + delay;
        }
        internal bool Blocked(HardThreadIdentity thread)
        { return Excluded.Contains(thread.Key) || failures.Count >= 512 && !failures.ContainsKey(thread.Key); }
        internal void Failed(HardThreadIdentity thread)
        {
            int count;
            failures.TryGetValue(thread.Key, out count);
            failures[thread.Key] = ++count;
            if (count >= 3) Excluded.Add(thread.Key);
        }
    }

    internal sealed partial class CriticalThreadHardAffinitySession
    {
        // 已经避让的线程只保留当前覆盖证据；它们没有本方写入，不进入持久恢复账本。
        private readonly Dictionary<string, HardThreadReceipt> unchangedPlacements = new Dictionary<string, HardThreadReceipt>(StringComparer.Ordinal);

        internal int SkippedThreads { get; private set; }

        private string skipNote;

        // 原只读基线失效的线程不再重捕获；从未接管的普通线程暂不可读时保留三次机会，用尽才排除。
        private HardThreadWorkerAdmissionBudget workerAdmissionBudget = new HardThreadWorkerAdmissionBudget();
        private HashSet<string> excludedWorkers { get { return workerAdmissionBudget.Excluded; } }

        internal void UseWorkerAdmissionBudget(HardThreadWorkerAdmissionBudget budget)
        {
            if (budget == null || plan != null) throw new InvalidOperationException("首次计划建立前才可指定普通线程准入账本");
            workerAdmissionBudget = budget;
        }

        private void RejectUnwrittenWorker(HardThreadIdentity thread)
        {
            if (plan.RequireWorkerCoverage && !plan.IsCandidate(thread) && !plan.IsWorkerPlacement(thread))
                workerAdmissionBudget.Failed(thread);
        }

        private bool FloatingCandidate(HardThreadIdentity thread)
        { return plan != null && plan.FloatingCandidates && plan.IsCandidate(thread); }

        private void NoteSkip(string reason, HardThreadIdentity thread, HardThreadObserved observed)
        {
            if (skipNote == null) skipNote = reason + ThreadFact(thread, observed);
        }

        // 最多保留 512 条记录；确认退出且持久提交核验通过后才可回收。
        internal int JournalRowCount
        {
            get { HardThreadJournal current = journal; return current == null ? 0 : current.Rows.Count; }
        }

        internal int ObservedUnchangedCount
        {
            get { lock (factsGate) return plan == null ? 0 : unchangedPlacements.Count; }
        }

        private HardThreadReceipt FindPlacement(HardThreadIdentity thread)
        {
            HardThreadReceipt row = Find(thread);
            if (row != null) return row;
            lock (factsGate) return unchangedPlacements.TryGetValue(thread.Key, out row) ? row : null;
        }

        private void RememberUnchanged(HardThreadIdentity thread, HardThreadObserved observed, bool deferConfirmation = false)
        {
            var row = new HardThreadReceipt { Thread = thread, Group = observed.Group, PreviousGroup = observed.Group,
                Captured = observed.Mask, Previous = observed.Mask, Applied = observed.Mask, PreviousKnown = true, Phase = HardThreadPhase.Applied };
            lock (factsGate)
            {
                unchangedPlacements[thread.Key] = row;
                if (deferConfirmation) verified.Remove(thread.Key);
                else verified[thread.Key] = new HardThreadOwnership(thread, observed.Group, observed.Mask, DateTime.UtcNow.Ticks);
            }
        }

        private void ForgetUnchanged(HardThreadIdentity thread)
        {
            lock (factsGate)
            {
                unchangedPlacements.Remove(thread.Key);
                verified.Remove(thread.Key);
            }
        }

        private void ResetUnchanged()
        {
            lock (factsGate)
            {
                foreach (string key in unchangedPlacements.Keys) verified.Remove(key);
                unchangedPlacements.Clear();
            }
        }

        private HardThreadReceipt[] UnchangedPlacements()
        {
            lock (factsGate)
            {
                var observations = new HardThreadReceipt[unchangedPlacements.Count];
                unchangedPlacements.Values.CopyTo(observations, 0);
                return observations;
            }
        }

        internal bool Apply(HardThreadPlan requested, HardThreadIdentity[] threads, HardThreadCoverage coverage)
        {
            if (threads != null)
            {
                threads = (HardThreadIdentity[])threads.Clone();
            }
            return RunForward(delegate
            {
                skipNote = null;
                RequireForwardAuthority();
                ReloadForForward();
                if (Pending())
                {
                    throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original recovery is pending");
                }
                if (requested == null || threads == null)
                {
                    throw InvalidRequest("plan or threads are missing");
                }
                if (!requested.Target.Same(gate.Target))
                {
                    throw InvalidRequest("plan belongs to another original target");
                }
                if (requested.WorkerMode == HardThreadWorkerMode.AvoidWorkers && !gate.AllowWorkers)
                {
                    throw InvalidRequest("original lease does not authorize worker avoidance");
                }
                HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
                HardThreadIdentity[] threadsSnapshot = threads;
                foreach (HardThreadIdentity hardThreadIdentity in threadsSnapshot)
                {
                    if (hardThreadIdentity == null || !hashSet.Add(hardThreadIdentity.Key))
                    {
                        throw InvalidRequest("duplicate thread identity");
                    }
                }
                HardThreadChoice[] choices = requested.Choices;
                foreach (HardThreadChoice hardThreadChoice in choices)
                {
                    if (!hashSet.Contains(hardThreadChoice.Thread.Key))
                    {
                        throw InvalidRequest("candidate absent from original thread input");
                    }
                }
                foreach (HardThreadChoice placement in requested.WorkerPlacements)
                    if (!hashSet.Contains(placement.Thread.Key))
                        throw InvalidRequest("辅助成员未出现在首次完整线程快照中");
                ResetUnchanged();
                plan = requested;
                return ApplyThreads(threads, coverage);
            });
        }

        internal HardThreadPlan CreatePlan(HardThreadChoice[] choices, bool requireWorkerCoverage = false, bool preserveOriginalMasks = false, HardThreadChoice[] workerPlacements = null, bool sharedCandidate = false, bool selectedWorkersOnly = false, bool floatingCandidates = false)
        {
            if (disposed)
            {
                throw new ObjectDisposedException("hard thread session");
            }
            using (gate.EnterUse())
            {
                BeginOperation();
                try
                {
                    RequireForwardAuthority();
                    if (gate.Target.AllowedMask == 0)
                        throw new InvalidOperationException("此目标没有亲和性写入权限");
                    HardThreadMachine hardThreadMachine = native.ReadMachine();
                    if (hardThreadMachine == null || hardThreadMachine.Read != 0)
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original process observation unavailable");
                    }
                    return HardThreadPlan.Create(gate.Target, hardThreadMachine, choices, gate.AllowWorkers ? HardThreadWorkerMode.AvoidWorkers : HardThreadWorkerMode.CandidatesOnly, requireWorkerCoverage, preserveOriginalMasks, workerPlacements, sharedCandidate, selectedWorkersOnly, floatingCandidates);
                }
                catch (Exception ex)
                {
                    HardThreadOperationException ex2 = ex as HardThreadOperationException;
                    lastFailureKind = ((ex2 == null) ? HardThreadFailureKind.InvalidRequest : ex2.Kind);
                    if (ex is NotSupportedException)
                    {
                        Status = HardThreadStatus.Unsupported;
                    }
                    Detail = ex.Message;
                    throw;
                }
            }
        }

        internal HardThreadCandidateAudit AuditCandidates(out string detail)
        {
            detail = "";
            try
            {
                using (gate.EnterUse())
                {
                    BeginOperation();
                    RequireForwardAuthority();
                    IHardThreadCandidateReads reads = native as IHardThreadCandidateReads;
                    if (reads == null || plan == null || plan.WorkerMode != HardThreadWorkerMode.CandidatesOnly
                        || Status != HardThreadStatus.AppliedCandidates || journal == null || (journal.Revision <= 0 && !plan.PreserveOriginalMasks)
                        || journal.Target == null || !journal.Target.Same(gate.Target) || !plan.Target.Same(gate.Target))
                        return AuditResult(HardThreadCandidateAudit.Unavailable, "no supported applied candidate-only scope", out detail);
                    HardThreadChoice[] choices = plan.Choices;
                    var owned = new Dictionary<string, HardThreadReceipt>(StringComparer.Ordinal);
                    foreach (HardThreadReceipt row in journal.Rows)
                    {
                        if (row.Terminal) continue;
                        if (row.Thread == null || row.Phase != HardThreadPhase.Applied || !row.PreviousKnown
                            || !plan.IsCandidate(row.Thread) || row.Group != plan.Group || row.Applied != plan.MaskFor(row.Thread)
                            || !OriginalMaskAdmissible(row.Captured) || !OriginalMaskAdmissible(row.Previous)
                            || row.PreviousGroup != row.Group || row.Applied != plan.MaskFor(row.Thread, row.Captured)
                            || owned.ContainsKey(row.Thread.Key))
                            return AuditResult(HardThreadCandidateAudit.Unavailable, "candidate ownership is not durably applied", out detail);
                        owned.Add(row.Thread.Key, row);
                    }
                    foreach (HardThreadReceipt row in UnchangedPlacements())
                    {
                        if (!plan.PreserveOriginalMasks || row.Thread == null || !plan.IsCandidate(row.Thread)
                            || row.Group != plan.Group || row.Applied != plan.MaskFor(row.Thread, row.Captured)
                            || owned.ContainsKey(row.Thread.Key))
                            return AuditResult(HardThreadCandidateAudit.Unavailable, "unchanged candidate coverage is invalid", out detail);
                        owned.Add(row.Thread.Key, row);
                    }
                    if (owned.Count != choices.Length)
                        return AuditResult(HardThreadCandidateAudit.Unavailable, "candidate ownership is incomplete", out detail);
                    HardThreadMachine scope = reads.ReadProcessScope();
                    if (scope == null || scope.Read == HardThreadRead.Unknown)
                        return AuditResult(HardThreadCandidateAudit.Unavailable, "original process scope is unavailable", out detail);
                    if (scope.Read == HardThreadRead.Gone)
                        return AuditResult(HardThreadCandidateAudit.Gone, "original process exited", out detail);
                    if (scope.Read != HardThreadRead.Known)
                        return AuditResult(HardThreadCandidateAudit.Unavailable, "original process scope is unreadable", out detail);
                    if (scope.GroupCount != 1 || scope.Group != plan.Group || scope.ProcessMask != plan.ProcessMask
                        || !scope.CpuSetsEmpty || (gate.Target.AllowedMask & ~scope.ProcessMask) != 0)
                        return AuditResult(HardThreadCandidateAudit.Changed, "original process range, group or CPU Sets changed", out detail);
                    var candidateFacts = new List<string>();
                    foreach (HardThreadChoice choice in choices)
                    {
                        HardThreadReceipt row;
                        if (!owned.TryGetValue(choice.Thread.Key, out row))
                            return AuditResult(HardThreadCandidateAudit.Unavailable, "original candidate receipt is missing", out detail);
                        HardThreadObserved observed = native.ReadThread(choice.Thread);
                        if (observed == null || observed.Read == HardThreadRead.Unknown)
                            return AuditResult(HardThreadCandidateAudit.Unavailable, "original candidate is unavailable", out detail);
                        if (observed.Read == HardThreadRead.Gone)
                            return AuditResult(HardThreadCandidateAudit.Gone, "original candidate exited", out detail);
                        if (observed.Read != HardThreadRead.Known)
                            return AuditResult(HardThreadCandidateAudit.Unavailable, "original candidate is unreadable", out detail);
                        if (observed.Group != row.Group || observed.Mask != row.Applied || !observed.CpuSetsEmpty)
                            return AuditResult(HardThreadCandidateAudit.Changed, "original candidate range, group or CPU Sets changed", out detail);
                        candidateFacts.Add("TID=" + choice.Thread.Tid + " affinity=0x" + observed.Mask.ToString("X") + " group=" + observed.Group
                            + " priorityLevel(raw)=" + (observed.ThreadPriorityKnown ? observed.ThreadPriorityLevel.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unreadable"));
                    }
                    RequireForwardAuthority();
                    foreach (HardThreadChoice choice in choices) Confirm(owned[choice.Thread.Key]);
                    return AuditResult(HardThreadCandidateAudit.Verified, string.Join("; ", candidateFacts.ToArray())
                        + "; processPriorityClass(raw)=" + (scope.ProcessPriorityClassKnown ? "0x" + scope.ProcessPriorityClass.ToString("X") : "unreadable"), out detail);
                }
            }
            catch (Exception error)
            {
                return AuditResult(HardThreadCandidateAudit.Unavailable, "candidate audit unavailable: " + error.GetType().Name + ": " + error.Message, out detail);
            }
        }

        private HardThreadCandidateAudit AuditResult(HardThreadCandidateAudit result, string reason, out string detail)
        {
            detail = Detail = reason;
            lastFailureKind = result == HardThreadCandidateAudit.Verified ? HardThreadFailureKind.None : HardThreadFailureKind.ObservationUnavailable;
            return result;
        }

        internal bool AdmitNewThreads(HardThreadIdentity[] threads, HardThreadCoverage coverage)
        {
            if (threads != null)
            {
                threads = (HardThreadIdentity[])threads.Clone();
            }
            return RunForward(delegate
            {
                skipNote = null;
                RequireForwardAuthority();
                if (WorkerReclamationPending)
                {
                    Detail = "已退出普通线程的账本提交尚未核验，暂停新线程接纳；" + WorkerReclamationDetail;
                    lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
                    return false;
                }
                ReloadForForward();
                if (Status == HardThreadStatus.RecoveryPending)
                {
                    throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original owned recovery is pending");
                }
                if (plan == null || (Status != HardThreadStatus.Applied && Status != HardThreadStatus.CoveragePending && Status != HardThreadStatus.AppliedCandidates))
                {
                    throw InvalidRequest("no active original plan");
                }
                ValidateInput(threads, coverage);
                IHardThreadPlacementReads placementReads = plan.RequireWorkerCoverage ? native as IHardThreadPlacementReads : null;
                var exitedWorkers = new HashSet<string>(StringComparer.Ordinal);
                // 普通线程维护失败仅标记覆盖不足，保留其恢复归属，不影响其他成员。
                var uncovered = new HashSet<string>(StringComparer.Ordinal);
                string message = null;
                int processedCount = 0;
                IHardThreadPlacementBatchReads batchReads = plan.RequireWorkerCoverage && coverage == HardThreadCoverage.Complete
                    && CompleteOwnedInput(threads) ? native as IHardThreadPlacementBatchReads : null;
                // 只有完整稳定的检查才能共用身份核验；新写入及恢复必须分别完整读取。
                IHardThreadPlacementBatch batch = null;
                try
                {
                    if (batchReads != null)
                    {
                        batch = batchReads.BeginPlacementBatch();
                        if (batch == null) throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "placement audit scope unavailable");
                        placementReads = batch;
                    }
                    for (int i = 0; i < journal.Rows.Count; i++)
                    {
                        HardThreadReceipt hardThreadReceipt = journal.Rows[i];
                        if (!hardThreadReceipt.Terminal)
                        {
                            if (exitedClearWorkers.Contains(hardThreadReceipt.Thread.Key))
                            {
                                exitedWorkers.Add(hardThreadReceipt.Thread.Key);
                                continue;
                            }
                            HardThreadObserved hardThreadObserved = placementReads == null
                                ? native.ReadThread(hardThreadReceipt.Thread) : placementReads.ReadPlacement(hardThreadReceipt.Thread);
                            if (hardThreadReceipt.Phase != HardThreadPhase.Applied || hardThreadObserved == null
                                || (hardThreadObserved.Read != HardThreadRead.Known && hardThreadObserved.Read != HardThreadRead.Gone))
                            {
                                Forget(hardThreadReceipt.Thread);
                                if (ClearCoreWorker(hardThreadReceipt))
                                {
                                    uncovered.Add(hardThreadReceipt.Thread.Key);
                                    NoteSkip("owned thread observation is unavailable", hardThreadReceipt.Thread, hardThreadObserved);
                                    continue;
                                }
                                throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending,
                                    "owned thread observation is unavailable" + ThreadFact(hardThreadReceipt.Thread, hardThreadObserved));
                            }
                            if (hardThreadObserved.Read == HardThreadRead.Gone)
                            {
                                Forget(hardThreadReceipt.Thread);
                                hardThreadReceipt.Phase = HardThreadPhase.Gone;
                                Save();
                                if (plan.IsCandidate(hardThreadReceipt.Thread))
                                {
                                    throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original candidate exited", true);
                                }
                                if (plan.RequireWorkerCoverage) exitedWorkers.Add(hardThreadReceipt.Thread.Key);
                                else coverage = HardThreadCoverage.Partial;
                            }
                            else
                            {
                                if (!hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != hardThreadReceipt.Group || hardThreadObserved.Mask != hardThreadReceipt.Applied)
                                {
                                    string messageOffset = message ?? DescribeExternalChange(hardThreadReceipt, hardThreadObserved);
                                    string reassertOwnedResult = ReassertOwned(hardThreadReceipt, hardThreadObserved);
                                    if (reassertOwnedResult != null)
                                    {
                                        Forget(hardThreadReceipt.Thread);
                                        if (ClearCoreWorker(hardThreadReceipt))
                                        {
                                            uncovered.Add(hardThreadReceipt.Thread.Key);
                                            message = messageOffset + "；写回未完成，标为覆盖不足：" + reassertOwnedResult;
                                            NoteSkip("owned thread write-back incomplete (" + reassertOwnedResult + ")", hardThreadReceipt.Thread, hardThreadObserved);
                                            continue;
                                        }
                                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                                            "owned thread state changed externally (" + reassertOwnedResult + "): " + messageOffset
                                            + ThreadFact(hardThreadReceipt.Thread, hardThreadObserved), true);
                                    }
                                    message = messageOffset;
                                    processedCount++;
                                }
                                if (batch == null) Confirm(hardThreadReceipt);
                            }
                        }
                    }
                    processedCount += AuditUnchangedPlacements(threads, placementReads, exitedWorkers, batch != null, ref message);
                    if (batch != null)
                    {
                        batch.Complete();
                        foreach (HardThreadReceipt row in journal.Rows)
                            if (!row.Terminal && !uncovered.Contains(row.Thread.Key) && !exitedWorkers.Contains(row.Thread.Key)) Confirm(row);
                        lock (factsGate)
                            foreach (HardThreadReceipt row in unchangedPlacements.Values) Confirm(row);
                    }
                }
                catch
                {
                    if (batchReads != null) lock (factsGate) verified.Clear();
                    throw;
                }
                finally { if (batch != null) batch.Dispose(); }
                if (message != null)
                {
                    LastReassertion = message + "; written back " + processedCount;
                }
                if (exitedWorkers.Count != 0)
                    threads = Array.FindAll(threads, thread => !exitedWorkers.Contains(thread.Key));
                if (uncovered.Count != 0)
                {
                    threads = Array.FindAll(threads, thread => !uncovered.Contains(thread.Key));
                    coverage = HardThreadCoverage.Partial;
                }
                if (plan.RequireWorkerCoverage && coverage == HardThreadCoverage.Complete && CompleteOwnedInput(threads))
                {
                    ValidateInput(threads, coverage);
                    ReadValidatedMachine();
                    RequireForwardAuthority();
                    return CompleteApply(false);
                }
                return ApplyThreads(threads, coverage);
            });
        }

        private bool ClearCoreWorker(HardThreadReceipt row)
        {
            return plan.RequireWorkerCoverage && !plan.IsCandidate(row.Thread) && !plan.IsWorkerPlacement(row.Thread)
                && row.Phase == HardThreadPhase.Applied && row.PreviousKnown;
        }

        private bool CompleteOwnedInput(HardThreadIdentity[] threads)
        {
            foreach (HardThreadIdentity thread in threads)
            {
                HardThreadReceipt row = FindPlacement(thread);
                if (row == null || row.Phase != HardThreadPhase.Applied || !row.PreviousKnown
                    || row.Group != plan.Group || row.Applied != plan.MaskFor(thread, row.Captured)) return false;
            }
            return true;
        }

        private int AuditUnchangedPlacements(HardThreadIdentity[] threads, IHardThreadPlacementReads reads, HashSet<string> exitedWorkers, bool deferConfirmation, ref string message)
        {
            int corrected = 0;
            HardThreadReceipt[] observations = UnchangedPlacements();
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (HardThreadIdentity thread in threads) present.Add(thread.Key);
            foreach (HardThreadReceipt row in observations)
            {
                if (!present.Contains(row.Thread.Key))
                {
                    ForgetUnchanged(row.Thread);
                    continue;
                }
                HardThreadObserved observed = reads == null ? native.ReadThread(row.Thread) : reads.ReadPlacement(row.Thread);
                if (observed != null && observed.Read == HardThreadRead.Gone && !plan.IsCandidate(row.Thread))
                {
                    ForgetUnchanged(row.Thread);
                    exitedWorkers.Add(row.Thread.Key);
                    continue;
                }
                string unavailable = PlacementReadFailure(row, observed);
                if (unavailable != null && plan.RequireWorkerCoverage && !plan.IsCandidate(row.Thread))
                {
                    // 无需写入的普通线程没有本方归属，读不到时从覆盖名单去掉即可，下一轮按新线程重新接纳。
                    ForgetUnchanged(row.Thread);
                    excludedWorkers.Add(row.Thread.Key);
                    NoteSkip("unchanged thread coverage changed or became unavailable；原因=" + unavailable, row.Thread, observed);
                    continue;
                }
                if (unavailable != null)
                {
                    if (!plan.SelectedWorkersOnly) ForgetUnchanged(row.Thread);
                    throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                        "unchanged thread coverage changed or became unavailable；原因=" + unavailable
                        + ThreadFact(row.Thread, observed), !plan.SelectedWorkersOnly);
                }
                if (FloatingCandidate(row.Thread))
                {
                    // 浮动角色的范围仅读取，不将游戏后改的值纳入写入归属。
                    RememberUnchanged(row.Thread, observed, deferConfirmation);
                    continue;
                }
                if (observed.Mask != row.Applied)
                {
                    if (!plan.IsCandidate(row.Thread) && !plan.IsWorkerPlacement(row.Thread)
                        && observed.Mask != 0 && (observed.Mask & ~plan.Workers) == 0)
                    {
                        // 已自行避让的线程只更新覆盖状态，不建立恢复归属。
                        RememberUnchanged(row.Thread, observed, deferConfirmation);
                        continue;
                    }
                    HardThreadReceipt promoted;
                    bool attempted = false;
                    try { promoted = ReassertUnchanged(row, observed, out attempted); }
                    catch (Exception error)
                    {
                        HardThreadOperationException operation = error as HardThreadOperationException;
                        bool qualificationOnly = !attempted && operation != null
                            && (operation.Kind == HardThreadFailureKind.ObservationUnavailable || operation.Kind == HardThreadFailureKind.AuthorityUnavailable);
                        // 未调用原生接口且意图已结清时，仅跳过该普通线程；否则仍走失败结算。
                        if (plan.RequireWorkerCoverage && !plan.IsCandidate(row.Thread) && !plan.IsWorkerPlacement(row.Thread)
                            && qualificationOnly && Find(row.Thread) == null)
                        {
                            ForgetUnchanged(row.Thread);
                            excludedWorkers.Add(row.Thread.Key);
                            NoteSkip("unchanged thread coverage changed or became unavailable；纠正失败=" + error.Message, row.Thread, observed);
                            continue;
                        }
                        if (plan.SelectedWorkersOnly && qualificationOnly)
                        {
                            // 尚未调用原生设置时保留冻结的只读基线；不能为补接纳而重捕获外部新值。
                            if (Find(row.Thread) == null) lock (factsGate)
                            { unchangedPlacements[row.Thread.Key] = row; verified.Remove(row.Thread.Key); }
                        }
                        else ForgetUnchanged(row.Thread);
                        throw new HardThreadOperationException(operation == null ? HardThreadFailureKind.WriteOrDurabilityFault : operation.Kind,
                            "unchanged thread coverage changed or became unavailable；纠正失败=" + error.Message
                            + ThreadFact(row.Thread, observed), !plan.SelectedWorkersOnly || !qualificationOnly);
                    }
                    message = message ?? "unchanged thread TID " + row.Thread.Tid + " applied=0x" + row.Applied.ToString("X")
                        + " observed=0x" + observed.Mask.ToString("X");
                    corrected++;
                    if (!deferConfirmation) Confirm(promoted);
                    continue;
                }
                if (!deferConfirmation) Confirm(row);
            }
            return corrected;
        }

        private string ReassertOwned(HardThreadReceipt row, HardThreadObserved current)
        {
            if (!row.PreviousKnown) return "receipt cannot be written back";
            string failure = PrepareReassertion(row, current);
            if (failure != null) return failure;
            HardThreadObserved before = native.ReadThread(row.Thread);
            failure = CorrectionWriteAdmission(row, current, before);
            if (failure != null) return failure;
            lastOperationNativeAttempt = true;
            ushort previousGroup;
            ulong previousMask;
            if (!native.Set(row.Thread, row.Group, row.Applied, out previousGroup, out previousMask)) return "write-back refused";
            if (previousGroup != before.Group || previousMask != before.Mask) return "write-back previous value changed during native apply";
            HardThreadObserved readback = native.ReadThread(row.Thread);
            if (PlacementReadFailure(row, readback) != null || readback.Mask != row.Applied)
                return "write-back readback unconfirmed" + ThreadFact(row.Thread, readback);
            RequireForwardAuthority();
            return null;
        }

        private string DescribeExternalChange(HardThreadReceipt row, HardThreadObserved current)
        {
            // 日志只使用本轮已有事实，不为统计变化总数额外读取后续线程。
            return (plan.IsCandidate(row.Thread) ? "关键线程" : "工作线程") + " TID " + row.Thread.Tid
                + " 已应用=0x" + row.Applied.ToString("X") + " 实际=0x" + current.Mask.ToString("X")
                + (current.Mask == plan.ProcessMask ? "（进程范围）" : "")
                + (current.Group != row.Group ? " 处理器组=" + row.Group + "->" + current.Group : "")
                + (current.CpuSetsEmpty ? "" : " 已设置线程 CPU Sets");
        }

        private bool RunForward(Func<bool> action)
        {
            if (disposed)
            {
                throw new ObjectDisposedException("hard thread session");
            }
            try
            {
                using (gate.EnterUse())
                {
                    BeginOperation();
                    try
                    {
                        return action();
                    }
                    catch (ObjectDisposedException)
                    {
                        throw;
                    }
                    catch (Exception error)
                    {
                        return FailOperation(error);
                    }
                    finally
                    {
                        PruneTransientReads();
                    }
                }
            }
            catch (ObjectDisposedException)
            {
                throw;
            }
            catch (Exception ex3)
            {
                lastFailureKind = HardThreadFailureKind.AuthorityUnavailable;
                lastOperationWriteIntent = false;
                lastOperationNativeAttempt = false;
                Detail = "original coordination gate unavailable: " + ex3.Message;
                return false;
            }
        }

        private static HardThreadOperationException InvalidRequest(string message)
        {
            return new HardThreadOperationException(HardThreadFailureKind.InvalidRequest, message);
        }

        private void PruneTransientReads()
        {
            if (plan == null) ResetUnchanged();
            IHardThreadTransientReads hardThreadTransientReads = native as IHardThreadTransientReads;
            if (hardThreadTransientReads == null)
            {
                return;
            }
            try
            {
                List<string> list = new List<string>();
                foreach (HardThreadReceipt row in journal.Rows)
                {
                    if (!row.Terminal)
                    {
                        list.Add(row.Thread.Key);
                    }
                }
                lock (factsGate)
                    foreach (string key in unchangedPlacements.Keys) list.Add(key);
                if (!hardThreadTransientReads.ReleaseUnowned(list.ToArray()))
                {
                    Detail += "; some transient query handles did not close";
                }
            }
            catch
            {
                Detail += "; transient query cleanup unavailable";
            }
        }

        private void ReloadForForward()
        {
            try
            {
                Reload();
            }
            catch (Exception ex)
            {
                throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "original ledger cannot be observed: " + ex.Message);
            }
        }

        private bool FailOperation(Exception error)
        {
            HardThreadOperationException ex = error as HardThreadOperationException;
            HardThreadFailureKind hardThreadFailureKind = ((ex == null) ? HardThreadFailureKind.WriteOrDurabilityFault : ex.Kind);
            bool recoveryRequired = ex == null || ex.Recover;
            string message = error.Message;
            if (recoveryRequired)
            {
                if (RestoreInternal(false) && hardThreadFailureKind == HardThreadFailureKind.WriteOrDurabilityFault)
                {
                    Status = HardThreadStatus.Conflict;
                }
                Detail = message + "; " + Detail;
            }
            else
            {
                if (hardThreadFailureKind == HardThreadFailureKind.RecoveryPending)
                {
                    Status = HardThreadStatus.RecoveryPending;
                }
                else if (!Pending())
                {
                    Status = ((hardThreadFailureKind == HardThreadFailureKind.InvalidRequest) ? HardThreadStatus.Conflict : HardThreadStatus.Stopped);
                }
                Detail = message;
            }
            lastFailureKind = hardThreadFailureKind;
            return false;
        }

        private bool RetireConfirmedWorkerExit(HardThreadIdentity thread, HardThreadObserved observed)
        {
            if (!plan.RequireWorkerCoverage || plan.IsCandidate(thread) || observed == null || observed.Read != HardThreadRead.Gone)
                return false;
            Forget(thread);
            HardThreadReceipt owned = Find(thread);
            if (owned != null)
            {
                owned.Phase = HardThreadPhase.Gone;
                Save();
            }
            return true;
        }

        private void ValidateInput(HardThreadIdentity[] threads, HardThreadCoverage coverage)
        {
            if (threads == null || threads.Length > 512 || !Enum.IsDefined(typeof(HardThreadCoverage), coverage))
            {
                throw InvalidRequest("bounded known thread input required");
            }
            if (plan.WorkerMode == HardThreadWorkerMode.AvoidWorkers && !gate.AllowWorkers)
            {
                throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "original worker authority revoked", true);
            }
            HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
            int processedCount = 0;
            int processedCount2 = 0;
            int unchangedInput = 0;
            foreach (HardThreadIdentity hardThreadIdentity in threads)
            {
                if (hardThreadIdentity == null || !hashSet.Add(hardThreadIdentity.Key))
                {
                    throw InvalidRequest("duplicate new thread identity");
                }
                if (!plan.IsCandidate(hardThreadIdentity))
                {
                    if (plan.SelectedWorkersOnly && !plan.IsWorkerPlacement(hardThreadIdentity))
                        throw InvalidRequest("显式工作组输入包含未获准的线程身份");
                    if (coverage == HardThreadCoverage.CandidatesOnly)
                    {
                        throw InvalidRequest("candidate coverage cannot include workers");
                    }
                    if (!gate.AllowWorkers || plan.WorkerMode != HardThreadWorkerMode.AvoidWorkers)
                    {
                        throw InvalidRequest("original candidate-only scope refuses worker identities");
                    }
                    if (FindPlacement(hardThreadIdentity) == null)
                    {
                        processedCount++;
                    }
                }
                if (FindPlacement(hardThreadIdentity) == null)
                {
                    processedCount2++;
                }
                else if (Find(hardThreadIdentity) == null) unchangedInput++;
            }
            int workerLimit = plan.SelectedWorkersOnly ? HardThreadPlan.MaximumSelectedWorkerPlacements : 8;
            if ((!plan.RequireWorkerCoverage && processedCount > workerLimit) || journal.Rows.Count + unchangedInput + processedCount2 > 512)
            {
                throw InvalidRequest("bounded worker batch required (input=" + threads.Length + " rows=" + journal.Rows.Count + " new=" + processedCount2 + " workers=" + processedCount + ")");
            }
            // 超出批次容量的线程留待后续扫描，不要求一次覆盖全量。
            if (plan.RequireWorkerCoverage && (coverage == HardThreadCoverage.CandidatesOnly || processedCount2 + journal.Rows.Count + unchangedInput > 512))
            {
                throw InvalidRequest("complete bounded worker snapshot required (coverage=" + coverage + " input=" + threads.Length + " rows=" + journal.Rows.Count + " new=" + processedCount2 + ")");
            }
            if (plan.RequireWorkerCoverage)
            {
                foreach (HardThreadChoice choice in plan.Choices)
                    if (!hashSet.Contains(choice.Thread.Key)) throw InvalidRequest("complete snapshot is missing an original candidate");
            }
        }

        private HardThreadMachine ReadValidatedMachine()
        {
            HardThreadMachine hardThreadMachine = native.ReadMachine();
            if (hardThreadMachine == null || hardThreadMachine.Read != 0)
            {
                throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original process observation unavailable", Pending());
            }
            if (hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != plan.Group || !hardThreadMachine.CpuSetsEmpty || hardThreadMachine.ProcessMask != plan.ProcessMask || (gate.Target.AllowedMask & ~hardThreadMachine.ProcessMask) != 0)
            {
                throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "original process scope changed", Pending());
            }
            HardThreadPlan hardThreadPlan;
            try
            {
                hardThreadPlan = HardThreadPlan.Create(gate.Target, hardThreadMachine, plan.Choices, plan.WorkerMode, plan.RequireWorkerCoverage, plan.PreserveOriginalMasks, plan.WorkerPlacements, plan.SharedCandidate, plan.SelectedWorkersOnly, plan.FloatingCandidates);
            }
            catch (Exception ex)
            {
                throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "native topology cannot admit the original plan: " + ex.Message, Pending());
            }
            if (hardThreadPlan.Workers != plan.Workers || hardThreadPlan.ProcessMask != plan.ProcessMask)
            {
                throw InvalidRequest("plan physical topology differs from the native admission");
            }
            return hardThreadMachine;
        }

        private HardThreadMachine ReadWriteScope()
        {
            IHardThreadCandidateReads reads = native as IHardThreadCandidateReads;
            return reads == null ? native.ReadMachine() : reads.ReadProcessScope();
        }

        private bool ApplyThreads(HardThreadIdentity[] threads, HardThreadCoverage coverage)
        {
            ValidateInput(threads, coverage);
            HardThreadMachine hardThreadMachine = ReadValidatedMachine();
            // 普通线程无法核验或收紧时标记覆盖不足；关键角色失败仍须整体结算。
            var skipped = new HashSet<string>(StringComparer.Ordinal);
            if (plan.RequireWorkerCoverage || plan.PreserveOriginalMasks)
            {
                var ordered = new List<HardThreadIdentity>(threads.Length);
                foreach (HardThreadIdentity thread in threads)
                {
                    if (FindPlacement(thread) == null && workerAdmissionBudget.Blocked(thread) && !plan.IsCandidate(thread))
                    {
                        skipped.Add(thread.Key);
                        NoteSkip("普通线程首次准入预算已耗尽，本阶段不再尝试", thread, null);
                        continue;
                    }
                    if (FindPlacement(thread) == null)
                    {
                        HardThreadObserved observed = native.ReadThread(thread);
                        if (RetireConfirmedWorkerExit(thread, observed)) continue;
                        if (observed == null || observed.Read != HardThreadRead.Known || !observed.CpuSetsEmpty
                            || observed.Group != plan.Group || !OriginalMaskAdmissible(observed.Mask)
                            || plan.MaskFor(thread, observed.Mask) == 0)
                        {
                            // 已退役的弹性辅助池要求完整保留两个处理器，成员缺失时仍整体拒绝，不退化成单核排队。
                            if (plan.IsCandidate(thread) || plan.IsWorkerPlacement(thread) && !plan.SelectedWorkersOnly)
                                throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                                    (plan.IsCandidate(thread) ? "candidate" : "auxiliary worker") + " preflight unavailable or externally constrained" + ThreadFact(thread, observed), Pending());
                            skipped.Add(thread.Key);
                            RejectUnwrittenWorker(thread);
                            NoteSkip("worker preflight unavailable or externally constrained", thread, observed);
                            lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
                            continue;
                        }
                    }
                    if (!plan.IsCandidate(thread)) ordered.Add(thread);
                }
                foreach (HardThreadIdentity thread in threads)
                    if (plan.IsCandidate(thread)) ordered.Add(thread);
                threads = ordered.ToArray();
            }
            HardThreadChoice[] choices = plan.Choices;
            foreach (HardThreadChoice hardThreadChoice in choices)
            {
                if (FindPlacement(hardThreadChoice.Thread) == null)
                {
                    HardThreadObserved hardThreadObserved = native.ReadThread(hardThreadChoice.Thread);
                    if (hardThreadObserved == null || hardThreadObserved.Read != 0 || !hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != plan.Group || !OriginalMaskAdmissible(hardThreadObserved.Mask)
                        || plan.MaskFor(hardThreadChoice.Thread, hardThreadObserved.Mask) == 0)
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original candidate is unavailable or externally constrained" + ThreadFact(hardThreadChoice.Thread, hardThreadObserved), Pending());
                    }
                }
            }
            bool fullCoverageRequired = !plan.SelectedWorkersOnly && coverage != HardThreadCoverage.Complete && coverage != HardThreadCoverage.CandidatesOnly;
            bool workersVerified = !plan.RequireWorkerCoverage;
            foreach (HardThreadIdentity thread in threads)
            {
                if (!workersVerified && plan.IsCandidate(thread))
                {
                    foreach (HardThreadIdentity worker in threads)
                    {
                        if (plan.IsCandidate(worker) || skipped.Contains(worker.Key)) continue;
                        HardThreadReceipt owned = FindPlacement(worker);
                        HardThreadObserved observed = native.ReadThread(worker);
                        if (RetireConfirmedWorkerExit(worker, observed)) continue;
                        if (owned == null || owned.Phase != HardThreadPhase.Applied || observed == null
                            || observed.Read != HardThreadRead.Known || !observed.CpuSetsEmpty
                            || observed.Group != plan.Group || observed.Mask != owned.Applied
                            || owned.Applied != plan.MaskFor(worker, owned.Captured))
                            throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                                "worker exclusion changed before candidate admission" + WorkerAdmissionFact(worker, owned, observed), Pending());
                    }
                    workersVerified = true;
                }
                if (FindPlacement(thread) != null)
                {
                    continue;
                }
                RequireForwardAuthority();
                HardThreadObserved hardThreadObserved2 = native.ReadThread(thread);
                bool isCandidate = plan.IsCandidate(thread);
                if (RetireConfirmedWorkerExit(thread, hardThreadObserved2)) continue;
                if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != plan.Group || !OriginalMaskAdmissible(hardThreadObserved2.Mask))
                {
                    if (isCandidate || plan.IsWorkerPlacement(thread) && !plan.SelectedWorkersOnly)
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "candidate changed before admission", Pending());
                    }
                    skipped.Add(thread.Key);
                    RejectUnwrittenWorker(thread);
                    NoteSkip("worker changed before admission", thread, hardThreadObserved2);
                    fullCoverageRequired = true;
                    lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
                    continue;
                }
                ulong appliedValue = plan.MaskFor(thread, hardThreadObserved2.Mask);
                if (appliedValue == 0)
                {
                    if (isCandidate || plan.IsWorkerPlacement(thread) && !plan.SelectedWorkersOnly)
                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                            "original thread range has no processor compatible with this layout", Pending());
                    skipped.Add(thread.Key);
                    RejectUnwrittenWorker(thread);
                    NoteSkip("original thread range has no processor compatible with this layout", thread, hardThreadObserved2);
                    fullCoverageRequired = true;
                    lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
                    continue;
                }
                if (appliedValue == hardThreadObserved2.Mask && !plan.SharedCandidate)
                {
                    if (plan.PreserveOriginalMasks) RememberUnchanged(thread, hardThreadObserved2);
                    continue;
                }
                HardThreadReceipt hardThreadReceipt = new HardThreadReceipt();
                hardThreadReceipt.Thread = thread;
                hardThreadReceipt.Group = hardThreadObserved2.Group;
                hardThreadReceipt.Captured = hardThreadObserved2.Mask;
                hardThreadReceipt.Applied = appliedValue;
                hardThreadReceipt.Phase = HardThreadPhase.ApplyIntent;
                HardThreadReceipt item = hardThreadReceipt;
                AddReceipt(item);
                bool nativeWriteAttempted = false;
                try
                {
                    Save();
                    item = journal.Rows[journal.Rows.Count - 1];
                    RequireForwardAuthority();
                    hardThreadObserved2 = native.ReadThread(thread);
                    hardThreadMachine = ReadWriteScope();
                    if (RetireConfirmedWorkerExit(thread, hardThreadObserved2)) continue;
                    if (hardThreadMachine == null || hardThreadMachine.Read != 0 || hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != plan.Group || hardThreadMachine.ProcessMask != plan.ProcessMask || !hardThreadMachine.CpuSetsEmpty || (item.Applied & ~gate.Target.AllowedMask) != 0)
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, "process write admission changed", true);
                    }
                    if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != item.Group || hardThreadObserved2.Mask != item.Captured)
                    {
                            throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "thread changed before native write", isCandidate || plan.RequireWorkerCoverage);
                    }
                    RequireForwardAuthority();
                    nativeWriteAttempted = true;
                    lastOperationNativeAttempt = true;
                    ushort previousGroup;
                    ulong previousMask;
                    if (!native.Set(thread, item.Group, item.Applied, out previousGroup, out previousMask))
                    {
                        if (plan.RequireWorkerCoverage && !isCandidate && RetireConfirmedWorkerExit(thread, native.ReadThread(thread))) continue;
                        throw new InvalidOperationException("native apply is unconfirmed");
                    }
                    item.PreviousKnown = true;
                    item.PreviousGroup = previousGroup;
                    item.Previous = previousMask;
                    item.Phase = HardThreadPhase.PreviousRecorded;
                    Save();
                    item = journal.Rows[journal.Rows.Count - 1];
                    if (previousGroup != item.Group || previousMask != item.Captured)
                    {
                        item.Detail = "external race at native apply";
                        Save();
                        throw new InvalidOperationException("apply previous differs from captured value");
                    }
                    hardThreadObserved2 = native.ReadThread(thread);
                    if (RetireConfirmedWorkerExit(thread, hardThreadObserved2)) continue;
                    if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != item.Group || hardThreadObserved2.Mask != item.Applied)
                    {
                        throw new InvalidOperationException("native apply readback is unconfirmed");
                    }
                    item.Phase = HardThreadPhase.Applied;
                    // 原生前值已经持久化，下一条意图也会提交本次完成状态。
                    pendingApplied = thread;
                }
                catch (Exception ex2)
                {
                    bool unappliedRecorded = false;
                    if (!nativeWriteAttempted)
                    {
                        try
                        {
                            Reload();
                            HardThreadReceipt hardThreadReceipt2 = Find(thread);
                            if (hardThreadReceipt2 != null && hardThreadReceipt2.Phase == HardThreadPhase.ApplyIntent && !hardThreadReceipt2.PreviousKnown)
                            {
                                hardThreadReceipt2.Phase = HardThreadPhase.NotApplied;
                                Save();
                                unappliedRecorded = true;
                            }
                        }
                        catch
                        {
                        }
                    }
                    HardThreadOperationException ex3 = ex2 as HardThreadOperationException;
                    if (!nativeWriteAttempted && unappliedRecorded && !isCandidate && !(plan.IsWorkerPlacement(thread) && !plan.SelectedWorkersOnly)
                        && ex3 != null && ex3.Kind == HardThreadFailureKind.ObservationUnavailable)
                    {
                        skipped.Add(thread.Key);
                        RejectUnwrittenWorker(thread);
                        fullCoverageRequired = true;
                        lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
                        continue;
                    }
                    if (!nativeWriteAttempted && !unappliedRecorded && ex3 != null && ex3.Kind == HardThreadFailureKind.ObservationUnavailable)
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.WriteOrDurabilityFault, "unwritten thread intent could not be settled", true);
                    }
                    throw;
                }
            }
            if (pendingApplied != null) Save();
            SkippedThreads = skipped.Count;
            return CompleteApply(fullCoverageRequired || skipped.Count != 0);
        }

        // 登记进程范围内的线程原掩码；CPU Sets 与跨组状态无法仅靠掩码恢复，不予接管。
        private bool OriginalMaskAdmissible(ulong mask)
        {
            return mask != 0 && (mask & ~plan.ProcessMask) == 0;
        }

        private string ThreadFact(HardThreadIdentity thread, HardThreadObserved observed)
        {
            if (thread == null) return "";
            string identity = " [tid=" + thread.Tid + " birth=" + thread.Creation;
            if (observed == null) return identity + " read=none]";
            string detail = (observed.Detail ?? "").Replace('\r', ' ').Replace('\n', ' ');
            if (detail.Length > 240) detail = detail.Substring(0, 240);
            return identity + " read=" + observed.Read + " mask=0x" + observed.Mask.ToString("X") + " process=0x" + plan.ProcessMask.ToString("X")
                + " group=" + observed.Group + " cpuSetsEmpty=" + observed.CpuSetsEmpty + " detail=" + detail + "]";
        }

        private string WorkerAdmissionFact(HardThreadIdentity thread, HardThreadReceipt placement, HardThreadObserved observed)
        {
            string reason = placement == null ? "缺少放置记录"
                : placement.Phase != HardThreadPhase.Applied ? "放置阶段未完成"
                : observed == null || observed.Read != HardThreadRead.Known ? "原生读取未确认"
                : !observed.CpuSetsEmpty ? "线程存在CPU Sets分配"
                : observed.Group != plan.Group ? "处理器组发生变化"
                : observed.Mask != placement.Applied ? "当前范围与放置记录不符"
                : "放置记录与当前方案不符";
            string result = "；工作线程避让核验失败 原因=" + reason + ThreadFact(thread, observed);
            if (placement == null) return result;
            // 同值观测没有恢复归属；记录来源有助于区分外部变化和本方未完成的写入。
            return result + " [来源=" + (Find(thread) == null ? "只读覆盖" : "持久恢复记录")
                + " phase=" + placement.Phase + " captured=0x" + placement.Captured.ToString("X")
                + " applied=0x" + placement.Applied.ToString("X")
                + " expected=0x" + plan.MaskFor(thread, placement.Captured).ToString("X")
                + " expectedGroup=" + plan.Group + "]";
        }

        private bool CompleteApply(bool partial)
        {
            if (plan.SelectedWorkersOnly)
            {
                Status = partial ? HardThreadStatus.CoveragePending : HardThreadStatus.Applied;
                Detail = (partial ? "显式工作组部分成员尚未核验，保留已接管字段"
                    : "显式工作组成员已逐线程读回；未声明全游戏线程避让或系统独占") + (partial && skipNote != null ? "；" + skipNote : "");
                return true;
            }
            Status = ((plan.WorkerMode == HardThreadWorkerMode.CandidatesOnly) ? HardThreadStatus.AppliedCandidates : ((!partial) ? HardThreadStatus.Applied : HardThreadStatus.CoveragePending));
            Detail = ((Status == HardThreadStatus.AppliedCandidates) ? "only original candidates are bound; worker placement is unchanged" : ((!partial) ? "provided snapshot threads verified; future workers are not instantly exclusive" : "thread lifecycle coverage is incomplete"))
                + (partial && skipNote != null ? "；" + skipNote : "");
            return true;
        }

    }
}
