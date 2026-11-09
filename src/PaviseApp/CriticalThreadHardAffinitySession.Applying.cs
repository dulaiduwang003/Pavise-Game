using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class CriticalThreadHardAffinitySession
    {

        internal bool Apply(HardThreadPlan requested, HardThreadIdentity[] threads, HardThreadCoverage coverage)
        {
            if (threads != null)
            {
                threads = (HardThreadIdentity[])threads.Clone();
            }
            return RunForward(delegate
            {
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
                plan = requested;
                return ApplyThreads(threads, coverage);
            });
        }

        internal HardThreadPlan CreatePlan(HardThreadChoice[] choices, bool requireWorkerCoverage = false)
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
                    HardThreadMachine hardThreadMachine = native.ReadMachine();
                    if (hardThreadMachine == null || hardThreadMachine.Read != 0)
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original process observation unavailable");
                    }
                    return HardThreadPlan.Create(gate.Target, hardThreadMachine, choices, gate.AllowWorkers ? HardThreadWorkerMode.AvoidWorkers : HardThreadWorkerMode.CandidatesOnly, requireWorkerCoverage);
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
                        || Status != HardThreadStatus.AppliedCandidates || journal == null || journal.Revision <= 0
                        || journal.Target == null || !journal.Target.Same(gate.Target) || !plan.Target.Same(gate.Target))
                        return AuditResult(HardThreadCandidateAudit.Unavailable, "no supported applied candidate-only scope", out detail);
                    HardThreadChoice[] choices = plan.Choices;
                    var owned = new Dictionary<string, HardThreadReceipt>(StringComparer.Ordinal);
                    foreach (HardThreadReceipt row in journal.Rows)
                    {
                        if (row.Terminal) continue;
                        if (row.Thread == null || row.Phase != HardThreadPhase.Applied || !row.PreviousKnown
                            || !plan.IsCandidate(row.Thread) || row.Group != plan.Group || row.Applied != plan.MaskFor(row.Thread)
                            || row.Captured != plan.ProcessMask || row.PreviousGroup != row.Group || row.Previous != row.Captured
                            || owned.ContainsKey(row.Thread.Key))
                            return AuditResult(HardThreadCandidateAudit.Unavailable, "candidate ownership is not durably applied", out detail);
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
                RequireForwardAuthority();
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
                            HardThreadObserved hardThreadObserved = placementReads == null
                                ? native.ReadThread(hardThreadReceipt.Thread) : placementReads.ReadPlacement(hardThreadReceipt.Thread);
                            if (hardThreadReceipt.Phase != HardThreadPhase.Applied || hardThreadObserved == null
                                || (hardThreadObserved.Read != HardThreadRead.Known && hardThreadObserved.Read != HardThreadRead.Gone))
                            {
                                Forget(hardThreadReceipt.Thread);
                                throw new HardThreadOperationException(HardThreadFailureKind.RecoveryPending, "owned thread observation is unavailable");
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
                                    string messageOffset = message ?? DescribeExternalChange(hardThreadReceipt, hardThreadObserved, i);
                                    if (plan.RequireWorkerCoverage)
                                    {
                                        Forget(hardThreadReceipt.Thread);
                                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                                            "exclusive thread placement changed externally: " + messageOffset, true);
                                    }
                                    string reassertOwnedResult = ReassertOwned(hardThreadReceipt, hardThreadObserved);
                                    if (reassertOwnedResult != null)
                                    {
                                        Forget(hardThreadReceipt.Thread);
                                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "owned thread state changed externally (" + reassertOwnedResult + "): " + messageOffset, true);
                                    }
                                    message = messageOffset;
                                    processedCount++;
                                }
                                if (batch == null) Confirm(hardThreadReceipt);
                            }
                        }
                    }
                    if (batch != null)
                    {
                        batch.Complete();
                        foreach (HardThreadReceipt row in journal.Rows)
                            if (!row.Terminal) Confirm(row);
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

        private bool CompleteOwnedInput(HardThreadIdentity[] threads)
        {
            foreach (HardThreadIdentity thread in threads)
            {
                HardThreadReceipt row = Find(thread);
                if (row == null || row.Phase != HardThreadPhase.Applied || !row.PreviousKnown
                    || row.Group != plan.Group || row.Applied != plan.MaskFor(thread)) return false;
            }
            return true;
        }

        private string ReassertOwned(HardThreadReceipt row, HardThreadObserved current)
        {
            if (!current.CpuSetsEmpty)
            {
                return "thread CPU Sets assigned";
            }
            if (current.Group != row.Group)
            {
                return "processor group changed";
            }
            if (!row.PreviousKnown || (row.Applied & ~gate.Target.AllowedMask) != 0)
            {
                return "receipt cannot be written back";
            }
            HardThreadMachine hardThreadMachine = native.ReadMachine();
            if (hardThreadMachine == null || hardThreadMachine.Read != 0 || hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != plan.Group || hardThreadMachine.ProcessMask != plan.ProcessMask || !hardThreadMachine.CpuSetsEmpty)
            {
                return "process scope changed";
            }
            RequireForwardAuthority();
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
            lastOperationNativeAttempt = true;
            ushort previousGroup;
            ulong previousMask;
            if (!native.Set(row.Thread, row.Group, row.Applied, out previousGroup, out previousMask))
            {
                return "write-back refused";
            }
            HardThreadObserved hardThreadObserved = native.ReadThread(row.Thread);
            if (hardThreadObserved == null || hardThreadObserved.Read != 0 || !hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != row.Group || hardThreadObserved.Mask != row.Applied)
            {
                return "write-back readback unconfirmed";
            }
            return null;
        }

        private string DescribeExternalChange(HardThreadReceipt row, HardThreadObserved current, int index)
        {
            int processedCount = 0;
            int processedCount2 = 1;
            for (int i = 0; i < journal.Rows.Count; i++)
            {
                HardThreadReceipt hardThreadReceipt = journal.Rows[i];
                if (hardThreadReceipt.Terminal)
                {
                    continue;
                }
                processedCount++;
                if (i <= index || hardThreadReceipt.Phase != HardThreadPhase.Applied)
                {
                    continue;
                }
                try
                {
                    HardThreadObserved hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
                    if (hardThreadObserved != null && hardThreadObserved.Read == HardThreadRead.Known && (!hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != hardThreadReceipt.Group || hardThreadObserved.Mask != hardThreadReceipt.Applied))
                    {
                        processedCount2++;
                    }
                }
                catch
                {
                }
            }
            return (plan.IsCandidate(row.Thread) ? "candidate" : "worker") + " TID " + row.Thread.Tid + " applied=0x" + row.Applied.ToString("X") + " observed=0x" + current.Mask.ToString("X") + ((current.Mask == plan.ProcessMask) ? " (process mask)" : "") + ((current.Group != row.Group) ? (" group " + row.Group + "->" + current.Group) : "") + (current.CpuSetsEmpty ? "" : " thread CPU Sets assigned") + "; " + processedCount2 + "/" + processedCount + " owned threads changed";
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
            foreach (HardThreadIdentity hardThreadIdentity in threads)
            {
                if (hardThreadIdentity == null || !hashSet.Add(hardThreadIdentity.Key))
                {
                    throw InvalidRequest("duplicate new thread identity");
                }
                if (!plan.IsCandidate(hardThreadIdentity))
                {
                    if (coverage == HardThreadCoverage.CandidatesOnly)
                    {
                        throw InvalidRequest("candidate coverage cannot include workers");
                    }
                    if (!gate.AllowWorkers || plan.WorkerMode != HardThreadWorkerMode.AvoidWorkers)
                    {
                        throw InvalidRequest("original candidate-only scope refuses worker identities");
                    }
                    if (Find(hardThreadIdentity) == null)
                    {
                        processedCount++;
                    }
                }
                if (Find(hardThreadIdentity) == null)
                {
                    processedCount2++;
                }
            }
            if ((!plan.RequireWorkerCoverage && processedCount > 8) || journal.Rows.Count + processedCount2 > 512)
            {
                throw InvalidRequest("bounded worker batch required (input=" + threads.Length + " rows=" + journal.Rows.Count + " new=" + processedCount2 + " workers=" + processedCount + ")");
            }
            if (plan.RequireWorkerCoverage && (coverage != HardThreadCoverage.Complete || processedCount2 + journal.Rows.Count > 512))
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
                hardThreadPlan = HardThreadPlan.Create(gate.Target, hardThreadMachine, plan.Choices, plan.WorkerMode, plan.RequireWorkerCoverage);
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
            if (plan.RequireWorkerCoverage)
            {
                var ordered = new List<HardThreadIdentity>(threads.Length);
                foreach (HardThreadIdentity thread in threads)
                {
                    if (Find(thread) == null)
                    {
                        HardThreadObserved observed = native.ReadThread(thread);
                        if (RetireConfirmedWorkerExit(thread, observed)) continue;
                        if (observed == null || observed.Read != HardThreadRead.Known || !observed.CpuSetsEmpty
                            || observed.Group != plan.Group || observed.Mask != plan.ProcessMask)
                            throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                                "complete worker preflight unavailable or externally constrained", Pending());
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
                if (Find(hardThreadChoice.Thread) == null)
                {
                    HardThreadObserved hardThreadObserved = native.ReadThread(hardThreadChoice.Thread);
                    if (hardThreadObserved == null || hardThreadObserved.Read != 0 || !hardThreadObserved.CpuSetsEmpty || hardThreadObserved.Group != plan.Group || hardThreadObserved.Mask != plan.ProcessMask)
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "original candidate is unavailable or externally constrained", Pending());
                    }
                }
            }
            bool fullCoverageRequired = coverage != HardThreadCoverage.Complete && coverage != HardThreadCoverage.CandidatesOnly;
            bool workersVerified = !plan.RequireWorkerCoverage;
            foreach (HardThreadIdentity thread in threads)
            {
                if (!workersVerified && plan.IsCandidate(thread))
                {
                    foreach (HardThreadIdentity worker in threads)
                    {
                        if (plan.IsCandidate(worker)) continue;
                        HardThreadReceipt owned = Find(worker);
                        HardThreadObserved observed = native.ReadThread(worker);
                        if (RetireConfirmedWorkerExit(worker, observed)) continue;
                        if (owned == null || owned.Phase != HardThreadPhase.Applied || observed == null
                            || observed.Read != HardThreadRead.Known || !observed.CpuSetsEmpty
                            || observed.Group != plan.Group || observed.Mask != plan.Workers)
                            throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable,
                                "worker exclusion changed before candidate admission", Pending());
                    }
                    workersVerified = true;
                }
                if (Find(thread) != null)
                {
                    continue;
                }
                RequireForwardAuthority();
                HardThreadObserved hardThreadObserved2 = native.ReadThread(thread);
                bool isCandidate = plan.IsCandidate(thread);
                if (RetireConfirmedWorkerExit(thread, hardThreadObserved2)) continue;
                if (hardThreadObserved2 == null || hardThreadObserved2.Read != 0 || !hardThreadObserved2.CpuSetsEmpty || hardThreadObserved2.Group != plan.Group || hardThreadObserved2.Mask != plan.ProcessMask)
                {
                    if (isCandidate || plan.RequireWorkerCoverage)
                    {
                        throw new HardThreadOperationException(HardThreadFailureKind.ObservationUnavailable, "candidate changed before admission", Pending());
                    }
                    fullCoverageRequired = true;
                    lastFailureKind = HardThreadFailureKind.ObservationUnavailable;
                    continue;
                }
                ulong appliedValue = plan.MaskFor(thread);
                if (appliedValue == hardThreadObserved2.Mask)
                {
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
                    if (!nativeWriteAttempted && unappliedRecorded && !isCandidate && !plan.RequireWorkerCoverage && ex3 != null && ex3.Kind == HardThreadFailureKind.ObservationUnavailable)
                    {
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
            return CompleteApply(fullCoverageRequired);
        }

        private bool CompleteApply(bool partial)
        {
            Status = ((plan.WorkerMode == HardThreadWorkerMode.CandidatesOnly) ? HardThreadStatus.AppliedCandidates : ((!partial) ? HardThreadStatus.Applied : HardThreadStatus.CoveragePending));
            Detail = ((Status == HardThreadStatus.AppliedCandidates) ? "only original candidates are bound; worker placement is unchanged" : ((!partial) ? "provided snapshot threads verified; future workers are not instantly exclusive" : "thread lifecycle coverage is incomplete"));
            return true;
        }

    }
}
