using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class CriticalThreadHardAffinitySession
    {
        private int runtimeMaskRunning;
        private readonly ThreadRuntimeCorrectionPolicy runtimeMaskPolicy = new ThreadRuntimeCorrectionPolicy();
        private volatile bool lastRuntimePending, runtimeThreadHasExited, runtimeAdmissionRequired;
        private volatile string lastRuntimeDetail = "";
        private long runtimeNextDueTicks;
        private readonly HashSet<string> exitedClearWorkers = new HashSet<string>(StringComparer.Ordinal);
        private WorkerReclamation workerReclamation;
        private volatile bool workerReclamationPending;
        private volatile string workerReclamationDetail = "";

        internal bool LastRuntimePending { get { return lastRuntimePending; } }
        internal bool ThreadHasExited { get { return runtimeThreadHasExited; } }
        internal bool RuntimeAdmissionRequired { get { return runtimeAdmissionRequired; } }
        internal string LastRuntimeDetail { get { return lastRuntimeDetail; } }
        internal long RuntimeNextDueTicks { get { return Interlocked.Read(ref runtimeNextDueTicks); } }
        internal bool WorkerReclamationPending { get { return workerReclamationPending; } }
        internal string WorkerReclamationDetail { get { return workerReclamationDetail; } }

        private sealed class WorkerReclamation
        {
            internal HardThreadJournal Original, Next;
            internal readonly List<HardThreadIdentity> Removed = new List<HardThreadIdentity>();
        }

        private sealed class RuntimeMaskFact
        {
            internal HardThreadReceipt Row;
            internal HardThreadObserved Observed;
            internal bool Owned, Started, Verified, ExitedWorker;
            internal string Failure;
        }

        // 仅维护已接管身份；暂不可读时保留账本和句柄，不重走首次准入。
        internal bool MaintainApplied(long nowTicks)
        {
            if (disposed) throw new ObjectDisposedException("hard thread session");
            if (Interlocked.CompareExchange(ref runtimeMaskRunning, 1, 0) != 0) return false;
            var facts = new List<RuntimeMaskFact>();
            try
            {
                lastRuntimePending = false;
                runtimeAdmissionRequired = false;
                lastRuntimeDetail = "";
                Interlocked.Exchange(ref runtimeNextDueTicks, 0);
                if (nowTicks <= 0) return RuntimePending("运行期核验时钟不可用");
                if (runtimeThreadHasExited) return RuntimePending("原角色或进程已确认退出；不接管复用的线程身份");
                using (gate.EnterUse())
                {
                    BeginOperation();
                    RequireForwardAuthority();
                    if (plan == null || journal == null || journal.Target == null || !journal.Target.Same(gate.Target)
                        || !plan.Target.Same(gate.Target)
                        || (Status != HardThreadStatus.Applied && Status != HardThreadStatus.AppliedCandidates && Status != HardThreadStatus.CoveragePending))
                        return RuntimePending("没有可维护的原线程绑定");
                    if (!ResolveWorkerReclamation(nowTicks)) return RuntimePending(workerReclamationDetail);
                    if (plan.RequireWorkerCoverage)
                    {
                        // 新线程接纳也会把确认退出的普通线程记为 Gone；提交前仍须逐字段核对持久原版本。
                        foreach (HardThreadReceipt row in journal.Rows)
                            if (row.Phase == HardThreadPhase.Gone && !plan.IsCandidate(row.Thread) && !plan.IsWorkerPlacement(row.Thread))
                                exitedClearWorkers.Add(row.Thread.Key);
                    }
                    ReclaimExitedWorkers(nowTicks);
                    if (workerReclamationPending) return RuntimePending(workerReclamationDetail);
                    var identities = new HashSet<string>(StringComparer.Ordinal);
                    foreach (HardThreadReceipt row in journal.Rows)
                    {
                        if (row.Terminal) continue;
                        if (!RuntimeReceiptValid(row, true) || !identities.Add(row.Thread.Key))
                            return RuntimePending("运行期持久收据尚未结清或身份不符");
                        bool exited = exitedClearWorkers.Contains(row.Thread.Key);
                        facts.Add(new RuntimeMaskFact { Row = row, Owned = true, ExitedWorker = exited, Verified = exited });
                    }
                    foreach (HardThreadReceipt row in UnchangedPlacements())
                    {
                        if (!RuntimeReceiptValid(row, false) || !identities.Add(row.Thread.Key))
                            return RuntimePending("运行期只读覆盖身份不符");
                        facts.Add(new RuntimeMaskFact { Row = row });
                    }
                    if (facts.Count == 0 || facts.Count > 512) return RuntimePending("没有已接管的冻结线程范围");
                    foreach (HardThreadChoice choice in plan.Choices)
                        if (!identities.Contains(choice.Thread.Key)) return RuntimePending("原角色收据缺失；不替换线程身份");
                    int started = 0;
                    foreach (RuntimeMaskFact fact in facts)
                        if (!fact.ExitedWorker && (fact.Started = runtimeMaskPolicy.TryBegin(fact.Row.Thread.Key, nowTicks))) started++;
                    if (started == 0) return RuntimePending("等待原线程范围的维护周期或失败退避");

                    HardThreadMachine scope = ReadWriteScope();
                    if (scope != null && scope.Read == HardThreadRead.Gone)
                    {
                        runtimeThreadHasExited = true;
                        return RuntimePending("原游戏进程已退出；不写入复用的PID");
                    }
                    if (!RuntimeScopeKnown(scope)) return RuntimePending("原进程范围、处理器组或CPU Sets尚未核验；保留原收据");
                    IHardThreadPlacementBatch batch = null;
                    try
                    {
                        IHardThreadPlacementBatchReads batchReads = native as IHardThreadPlacementBatchReads;
                        IHardThreadPlacementReads reads = native as IHardThreadPlacementReads;
                        if (batchReads != null)
                        {
                            batch = batchReads.BeginPlacementBatch();
                            if (batch == null) return RuntimePending("运行期线程批次尚未建立");
                            reads = batch;
                        }
                        foreach (RuntimeMaskFact fact in facts)
                        {
                            if (!fact.Started) continue;
                            fact.Observed = reads == null ? native.ReadThread(fact.Row.Thread) : reads.ReadPlacement(fact.Row.Thread);
                        }
                        if (batch != null) batch.Complete();
                    }
                    finally { if (batch != null) batch.Dispose(); }
                    if (!RuntimeScopeKnown(ReadWriteScope()))
                        return RuntimePending("批次结束时进程范围无法确认；保留原收据");
                    RequireForwardAuthority();
                    int corrected = 0, retired = 0;
                    foreach (RuntimeMaskFact fact in facts)
                    {
                        if (!fact.Started) continue;
                        HardThreadReceipt row = fact.Row;
                        HardThreadObserved observed = fact.Observed;
                        if (observed != null && observed.Read == HardThreadRead.Gone)
                        {
                            Forget(row.Thread);
                            if (plan.RequireWorkerCoverage && !plan.IsCandidate(row.Thread) && !plan.IsWorkerPlacement(row.Thread))
                            {
                                // 已确认退出后停止轮询，持久提交完成前仍保留句柄。
                                fact.ExitedWorker = fact.Verified = true;
                                if (fact.Owned) exitedClearWorkers.Add(row.Thread.Key);
                                else ForgetUnchanged(row.Thread);
                                retired++;
                                continue;
                            }
                            if (plan.SelectedWorkersOnly && !plan.IsCandidate(row.Thread))
                            {
                                // 工作池成员退出不撤销其他冻结角色的维护资格，也不接入同号新线程。
                                HardThreadReceipt owned = Find(row.Thread);
                                if (owned != null) { owned.Phase = HardThreadPhase.Gone; Save(); }
                                else ForgetUnchanged(row.Thread);
                                fact.Verified = true;
                                retired++;
                                continue;
                            }
                            if (plan.IsCandidate(row.Thread) || plan.IsWorkerPlacement(row.Thread)) runtimeThreadHasExited = true;
                            fact.Failure = "原线程已退出；不接管同号新线程" + ThreadFact(row.Thread, observed);
                            continue;
                        }
                        string failure = RuntimePlacementReadFailure(row, observed);
                        if (failure != null)
                        {
                            Forget(row.Thread);
                            fact.Failure = failure + ThreadFact(row.Thread, observed);
                            continue;
                        }
                        if (!fact.Owned && FloatingCandidate(row.Thread))
                        {
                            // 本策略只设置理想处理器，不维护候选亲和性；读回范围变化由理想处理器自身的范围门禁处理。
                            RememberUnchanged(row.Thread, observed);
                            fact.Verified = true;
                            continue;
                        }
                        if (observed.Mask != row.Applied)
                        {
                            Forget(row.Thread);
                            if (!fact.Owned)
                            {
                                runtimeAdmissionRequired = true;
                                fact.Failure = "只读范围需要首次写入归属；等待有限准入，不在运行期无限重试" + ThreadFact(row.Thread, observed);
                                continue;
                            }
                            failure = CorrectRuntimeOwned(row, observed);
                            if (failure != null)
                            {
                                fact.Failure = failure + ThreadFact(row.Thread, observed);
                                continue;
                            }
                            corrected++;
                        }
                        Confirm(row);
                        fact.Verified = true;
                    }
                    RequireForwardAuthority();
                    ReclaimExitedWorkers(nowTicks);
                    if (workerReclamationPending) return RuntimePending(workerReclamationDetail);
                    if (corrected > 0) LastReassertion = "运行期已纠正原线程范围；线程数=" + corrected + "；原始恢复值未改写";
                    var failures = new List<string>();
                    foreach (RuntimeMaskFact fact in facts)
                    {
                        if (fact.ExitedWorker) continue;
                        if (!fact.Started) { if (failures.Count < 4) failures.Add("线程=" + fact.Row.Thread.Key + "；等待维护周期或失败退避"); }
                        else if (!fact.Verified && failures.Count < 4) failures.Add(fact.Failure ?? "原线程范围尚未核验");
                    }
                    if (failures.Count != 0) return RuntimePending(string.Join(" | ", failures.ToArray()));
                    lastRuntimeDetail = Detail = "本轮现存原线程范围均已读回核验；保留原角色和恢复归属"
                        + (retired == 0 ? "" : "；本轮已确认退出工作成员=" + retired)
                        + (workerReclamationDetail.Length == 0 ? "" : "；" + workerReclamationDetail);
                    lastFailureKind = HardThreadFailureKind.None;
                    if (retired != 0 && !workerReclamationPending) PruneTransientReads();
                    return true;
                }
            }
            catch (ObjectDisposedException) { throw; }
            catch (Exception error)
            {
                foreach (RuntimeMaskFact fact in facts) if (fact.Started) { fact.Verified = false; Forget(fact.Row.Thread); }
                return RuntimePending("运行期原线程核验暂停：" + error.GetType().Name + "：" + error.Message);
            }
            finally
            {
                long next = 0;
                foreach (RuntimeMaskFact fact in facts)
                {
                    if (fact.Started)
                    {
                        if (!fact.Verified) Forget(fact.Row.Thread);
                        runtimeMaskPolicy.Complete(fact.Row.Thread.Key, nowTicks, fact.Verified);
                    }
                    if (fact.ExitedWorker && !exitedClearWorkers.Contains(fact.Row.Thread.Key))
                        runtimeMaskPolicy.ForgetExited(fact.Row.Thread.Key);
                    long due = runtimeMaskPolicy.NextDue(fact.Row.Thread.Key);
                    if (due > 0 && (next == 0 || due < next)) next = due;
                }
                Interlocked.Exchange(ref runtimeNextDueTicks, next);
                Volatile.Write(ref runtimeMaskRunning, 0);
            }
        }

        // 压缩只删除原句柄已确认退出的普通清核线程，所有存活、未知及恢复中的行按原样保留。
        private void ReclaimExitedWorkers(long now)
        {
            if (workerReclamationPending || exitedClearWorkers.Count == 0 || pendingApplied != null
                || plan == null || !plan.RequireWorkerCoverage || !workerAdmissionBudget.ReclamationAvailable(now)) return;
            var candidate = new WorkerReclamation { Original = journal.Clone(), Next = journal.Clone() };
            for (int index = candidate.Next.Rows.Count - 1; index >= 0; index--)
            {
                HardThreadReceipt row = candidate.Next.Rows[index];
                if (!exitedClearWorkers.Contains(row.Thread.Key) || plan.IsCandidate(row.Thread) || plan.IsWorkerPlacement(row.Thread)
                    || row.Phase != HardThreadPhase.Applied && row.Phase != HardThreadPhase.Gone) continue;
                candidate.Removed.Add(row.Thread);
                candidate.Next.Rows.RemoveAt(index);
            }
            if (candidate.Removed.Count == 0) return;
            try
            {
                candidate.Next.Revision = checked(candidate.Original.Revision + 1);
                IndexReceipts(candidate.Next);
            }
            catch (Exception error) { WorkerReclamationFailed(now, error.Message); return; }
            workerReclamation = candidate;
            workerReclamationPending = true;
            try
            {
                // 不以旧内存覆盖外部新版本；这一轮只允许从已完整核对的原版本提交一次。
                HardThreadJournal current = store.Load(gate.Target);
                IndexReceipts(current);
                if (!SameReclamationJournal(current, candidate.Original))
                    throw new InvalidOperationException("普通线程回收前的持久账本与原版本不一致");
                RequireForwardAuthority();
                lastOperationWriteIntent = true;
                store.Save(candidate.Original.Revision, candidate.Next);
                current = store.Load(gate.Target);
                IndexReceipts(current);
                if (!SameReclamationJournal(current, candidate.Next))
                    throw new InvalidOperationException("普通线程回收提交读回不一致");
                CompleteWorkerReclamation(current);
            }
            catch (Exception error)
            {
                // Replace 报错后仍须完整读回拟提交版本；禁止用旧快照覆盖可能已完成的提交。
                bool confirmed = false;
                try { confirmed = ObserveWorkerReclamation(); } catch { }
                if (confirmed && !workerReclamationPending && workerReclamation == null
                    && SameReclamationJournal(journal, candidate.Next)) return;
                WorkerReclamationFailed(now, error.Message);
            }
        }

        private bool ResolveWorkerReclamation(long now)
        {
            if (!workerReclamationPending) return true;
            if (!workerAdmissionBudget.ReclamationAvailable(now)) return false;
            try
            {
                if (ObserveWorkerReclamation()) return true;
                WorkerReclamationFailed(now, "持久账本既不匹配原版本，也不匹配待核验提交");
            }
            catch (Exception error) { WorkerReclamationFailed(now, error.Message); }
            return false;
        }

        private bool ObserveWorkerReclamation()
        {
            WorkerReclamation candidate = workerReclamation;
            if (candidate == null) return !workerReclamationPending;
            HardThreadJournal current = store.Load(gate.Target);
            IndexReceipts(current);
            if (SameReclamationJournal(current, candidate.Next))
            {
                CompleteWorkerReclamation(current);
                return true;
            }
            if (!SameReclamationJournal(current, candidate.Original)) return false;
            workerReclamation = null;
            workerReclamationPending = false;
            workerReclamationDetail = "普通线程回收未提交，原记录及句柄保留";
            return true;
        }

        private void CompleteWorkerReclamation(HardThreadJournal committed)
        {
            WorkerReclamation candidate = workerReclamation;
            Dictionary<string, int> nextIndex = IndexReceipts(committed);
            // 先更新已核验的持久版本和索引，再释放退出身份，确保关闭时仍能恢复存活线程。
            journal = committed;
            receiptIndex = nextIndex;
            pendingApplied = null;
            foreach (HardThreadIdentity thread in candidate.Removed)
            {
                Forget(thread);
                ForgetUnchanged(thread);
                exitedClearWorkers.Remove(thread.Key);
                runtimeMaskPolicy.ForgetExited(thread.Key);
            }
            workerReclamation = null;
            workerReclamationPending = false;
            workerReclamationDetail = "已回收退出普通线程记录=" + candidate.Removed.Count + "；剩余记录=" + journal.Rows.Count;
            PruneTransientReads();
        }

        private void WorkerReclamationFailed(long now, string reason)
        {
            workerAdmissionBudget.ReclamationFailed(now);
            workerReclamationDetail = "普通线程账本回收失败=" + workerAdmissionBudget.ReclamationFailures + "/3；" + reason
                + (workerReclamationPending ? "；提交状态未知，暂停亲和性维护写入和新接纳，原记录及句柄保留" : "；原记录及句柄保留，存活线程继续维护")
                + (workerAdmissionBudget.ReclamationFailures >= 3 ? "；本阶段停止主动回收核验" : "；至少30秒后使用剩余次数");
        }

        private static bool SameReclamationJournal(HardThreadJournal left, HardThreadJournal right)
        {
            if (left == null || right == null || left.Target == null || right.Target == null || !left.Target.Same(right.Target)
                || left.Revision != right.Revision || left.SourceNonce != right.SourceNonce || left.SourceScopeHash != right.SourceScopeHash
                || left.ControlDeclared != right.ControlDeclared || left.Rows.Count != right.Rows.Count) return false;
            for (int index = 0; index < left.Rows.Count; index++)
            {
                HardThreadReceipt a = left.Rows[index], b = right.Rows[index];
                if (a == null || b == null || a.Thread == null || b.Thread == null || a.Thread.Key != b.Thread.Key
                    || a.Group != b.Group || a.PreviousGroup != b.PreviousGroup || a.RestorePreviousGroup != b.RestorePreviousGroup
                    || a.Captured != b.Captured || a.Applied != b.Applied || a.Previous != b.Previous || a.RestorePrevious != b.RestorePrevious
                    || a.PreviousKnown != b.PreviousKnown || a.RestorePreviousKnown != b.RestorePreviousKnown
                    || a.Phase != b.Phase || a.Detail != b.Detail) return false;
            }
            return true;
        }

        private bool RuntimePending(string detail)
        {
            lastRuntimePending = true;
            lastRuntimeDetail = Detail = detail;
            lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
            return false;
        }

        private bool RuntimeReceiptValid(HardThreadReceipt row, bool owned)
        {
            return row != null && row.Thread != null && row.Phase == HardThreadPhase.Applied && row.PreviousKnown
                && row.Group == plan.Group && row.PreviousGroup == row.Group && row.Applied != 0
                && (!owned && FloatingCandidate(row.Thread) || (row.Applied & ~gate.Target.AllowedMask) == 0) && OriginalMaskAdmissible(row.Captured)
                && OriginalMaskAdmissible(row.Previous) && row.Applied == plan.MaskFor(row.Thread, row.Captured)
                && (!owned || journal.Revision > 0);
        }

        private string CorrectRuntimeOwned(HardThreadReceipt row, HardThreadObserved current)
        {
            // 已有持久归属覆盖这次纠正；外部后写值不成为新的原值，也不消耗首次准入预算。
            HardThreadObserved runtimePreflight = native.ReadThread(row.Thread);
            string failure = RuntimePlacementReadFailure(row, runtimePreflight);
            if (failure != null || runtimePreflight.Mask != current.Mask)
                return "纠正前线程范围变化或暂不可读；" + (failure ?? "掩码再次变化");
            if (!RuntimeScopeKnown(ReadWriteScope())) return "纠正前实时核心范围不再包含冻结目标";
            RequireForwardAuthority();
            lastOperationNativeAttempt = true;
            ushort previousGroup;
            ulong previousMask;
            if (!native.Set(row.Thread, row.Group, row.Applied, out previousGroup, out previousMask))
                return "运行期线程范围纠正被拒绝";
            if (previousGroup != runtimePreflight.Group || previousMask != runtimePreflight.Mask)
                return "运行期纠正发生写前竞争；保留原始恢复值并等待核验；目标=0x" + row.Applied.ToString("X")
                    + " 写前观察=0x" + runtimePreflight.Mask.ToString("X") + " API返回前值=0x" + previousMask.ToString("X");
            HardThreadObserved readback = native.ReadThread(row.Thread);
            failure = RuntimePlacementReadFailure(row, readback);
            if (failure != null || readback.Mask != row.Applied)
                return "运行期纠正读回尚未确认；" + (failure ?? "掩码再次变化") + "；目标=0x" + row.Applied.ToString("X")
                    + " 写前观察=0x" + runtimePreflight.Mask.ToString("X") + " API返回前值=0x" + previousMask.ToString("X")
                    + " 写后读回=" + (readback == null ? "不可读" : readback.Read + "/0x" + readback.Mask.ToString("X"));
            RequireForwardAuthority();
            return null;
        }

        private bool RuntimeScopeKnown(HardThreadMachine machine)
        {
            if (!plan.SharedCandidate) return CorrectionScopeKnown(machine);
            if (machine == null || machine.Read != HardThreadRead.Known || machine.GroupCount != 1
                || machine.Group != plan.Group || !machine.CpuSetsEmpty || machine.ProcessMask == 0) return false;
            // 仅核验已选目标在当前范围内仍合法；原进程范围只作恢复基线，不扩展目标或授权。
            ulong available = machine.ProcessMask & gate.Target.AllowedMask;
            foreach (HardThreadChoice choice in plan.Choices)
                if ((choice.Mask & available) != choice.Mask) return false;
            return true;
        }

        private string RuntimePlacementReadFailure(HardThreadReceipt row, HardThreadObserved observed)
        {
            if (!plan.SharedCandidate) return PlacementReadFailure(row, observed);
            if (observed == null || observed.Read == HardThreadRead.Unknown) return "原生读取未确认";
            if (observed.Read == HardThreadRead.Gone) return "原线程已退出";
            if (observed.Read != HardThreadRead.Known) return "线程身份未确认";
            if (!observed.CpuSetsEmpty) return "线程存在CPU Sets分配";
            if (observed.Group != row.Group) return "处理器组发生变化";
            return observed.Mask == 0 ? "线程范围为空" : null;
        }

        private string PlacementReadFailure(HardThreadReceipt row, HardThreadObserved observed)
        {
            if (observed == null || observed.Read == HardThreadRead.Unknown) return "原生读取未确认";
            if (observed.Read == HardThreadRead.Gone) return "原线程已退出";
            if (observed.Read != HardThreadRead.Known) return "线程身份未确认";
            if (!observed.CpuSetsEmpty) return "thread CPU Sets assigned；线程存在CPU Sets分配";
            if (observed.Group != row.Group) return "processor group changed；处理器组发生变化";
            if (!OriginalMaskAdmissible(observed.Mask)) return "线程范围不属于原进程范围";
            return null;
        }

        private string PrepareReassertion(HardThreadReceipt row, HardThreadObserved current)
        {
            string failure = PlacementReadFailure(row, current);
            if (failure != null) return failure;
            if (row.Group != plan.Group || row.Applied == 0 || (row.Applied & ~gate.Target.AllowedMask) != 0
                || !OriginalMaskAdmissible(row.Captured) || row.Applied != plan.MaskFor(row.Thread, row.Captured))
                return "receipt cannot be written back";
            HardThreadMachine machine = native.ReadMachine();
            if (!CorrectionScopeKnown(machine)) return "process scope changed or became unavailable";
            RequireForwardAuthority();
            HardThreadObserved preflight = native.ReadThread(row.Thread);
            failure = PlacementReadFailure(row, preflight);
            if (failure != null || preflight.Mask != current.Mask)
                return "write-back preflight changed or became unavailable；原因=" + (failure ?? "掩码再次变化")
                    + ThreadFact(row.Thread, preflight);
            if (!reassertionAdmitted)
            {
                Func<bool> admit = BeforeReassertion;
                if (admit != null)
                {
                    try { if (!admit()) return "forward reassertion declined"; }
                    catch { return "forward reassertion policy unavailable"; }
                }
                reassertionAdmitted = true;
            }
            return null;
        }

        private bool CorrectionScopeKnown(HardThreadMachine machine)
        {
            return machine != null && machine.Read == HardThreadRead.Known && machine.GroupCount == 1
                && machine.Group == plan.Group && machine.ProcessMask == plan.ProcessMask && machine.CpuSetsEmpty;
        }

        private string CorrectionWriteAdmission(HardThreadReceipt row, HardThreadObserved current, HardThreadObserved before)
        {
            string failure = PlacementReadFailure(row, before);
            if (failure != null || before.Mask != current.Mask)
                return "thread changed before native write；原因=" + (failure ?? "掩码再次变化") + ThreadFact(row.Thread, before);
            if (!CorrectionScopeKnown(ReadWriteScope())) return "process write admission changed";
            RequireForwardAuthority();
            return null;
        }

        private HardThreadReceipt ReassertUnchanged(HardThreadReceipt observation, HardThreadObserved current, out bool attempted)
        {
            attempted = false;
            if (FloatingCandidate(observation.Thread))
                throw new InvalidOperationException("浮动渲染角色只读范围不能晋升为亲和性写入归属");
            string failure = PrepareReassertion(observation, current);
            if (failure != null)
                throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, failure, true);
            // Captured 保留已冻结的计划基准，Previous 单独记录首次本方写入前的真实范围。
            // 原生 API 尚未返回前不能把观察值标成已确认前值；失败后继续保留恢复债务。
            var row = new HardThreadReceipt { Thread = observation.Thread, Group = observation.Group,
                Captured = observation.Captured, Applied = observation.Applied, PreviousGroup = current.Group,
                Previous = current.Mask, PreviousKnown = false, Phase = HardThreadPhase.ApplyIntent,
                Detail = "只读覆盖纠正首次写入；冻结计划基准与真实写前范围分别保留" };
            AddReceipt(row);
            try
            {
                Save();
                row = Find(observation.Thread);
                ForgetUnchanged(observation.Thread);
                RequireForwardAuthority();
                HardThreadObserved before = native.ReadThread(row.Thread);
                failure = CorrectionWriteAdmission(row, current, before);
                if (failure != null)
                    throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, failure, true);
                attempted = true;
                lastOperationNativeAttempt = true;
                ushort previousGroup;
                ulong previousMask;
                if (!native.Set(row.Thread, row.Group, row.Applied, out previousGroup, out previousMask))
                    throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "write-back refused", true);
                row.PreviousGroup = previousGroup;
                row.Previous = previousMask;
                row.PreviousKnown = true;
                row.Phase = HardThreadPhase.PreviousRecorded;
                Save();
                row = Find(observation.Thread);
                if (previousGroup != before.Group || previousMask != before.Mask)
                    throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                        "write-back previous value changed during native apply", true);
                HardThreadObserved readback = native.ReadThread(row.Thread);
                if (PlacementReadFailure(row, readback) != null || readback.Mask != row.Applied)
                    throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                        "write-back readback unconfirmed" + ThreadFact(row.Thread, readback), true);
                RequireForwardAuthority();
                row.Phase = HardThreadPhase.Applied;
                Save();
                return Find(observation.Thread);
            }
            catch
            {
                if (!attempted)
                {
                    // 写前核验失败时结清确实未执行的意图；持久化失败则保留原账本供恢复核验。
                    try
                    {
                        Reload();
                        HardThreadReceipt pending = Find(observation.Thread);
                        if (pending != null && pending.Phase == HardThreadPhase.ApplyIntent && !pending.PreviousKnown)
                        {
                            pending.Phase = HardThreadPhase.NotApplied;
                            Save();
                        }
                    }
                    catch { }
                }
                throw;
            }
        }
    }
}
