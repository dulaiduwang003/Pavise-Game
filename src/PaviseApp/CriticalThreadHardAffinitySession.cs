using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal interface IHardThreadPlacementReads
    {
        HardThreadObserved ReadPlacement(HardThreadIdentity thread);
    }

    internal interface IHardThreadPlacementBatch : IHardThreadPlacementReads, IDisposable
    {
        void Complete();
    }

    internal interface IHardThreadPlacementBatchReads
    {
        IHardThreadPlacementBatch BeginPlacementBatch();
    }

    internal sealed partial class CriticalThreadHardAffinitySession : IDisposable
    {

        private readonly IHardThreadGate gate;

        private readonly IHardThreadNative native;

        private readonly IHardThreadStore store;

        private readonly IHardThreadGuardian guardian;

        private HardThreadJournal journal;

        private Dictionary<string, int> receiptIndex;

        private HardThreadIdentity pendingApplied;

        private HardThreadPlan plan;

        private volatile bool disposed;

        private volatile bool resourcesClosed;

        private bool nativeClosed;

        private bool storeClosed;

        private bool guardianClosed;

        private readonly object factsGate = new object();

        private readonly Dictionary<string, HardThreadOwnership> verified = new Dictionary<string, HardThreadOwnership>(StringComparer.Ordinal);

        private volatile HardThreadFailureKind lastFailureKind;

        private volatile bool lastOperationWriteIntent;

        private volatile bool lastOperationNativeAttempt;

        private bool reassertionAdmitted;

        private volatile HardThreadStatus currentStatus;

        private volatile string currentDetail = "";

        internal bool ResourcesClosed
        {
            get
            {
                return resourcesClosed;
            }
        }

        internal HardThreadFailureKind LastFailureKind
        {
            get
            {
                return lastFailureKind;
            }
        }

        internal bool LastOperationWriteIntent
        {
            get
            {
                return lastOperationWriteIntent;
            }
        }

        internal bool LastOperationNativeAttempt
        {
            get
            {
                return lastOperationNativeAttempt;
            }
        }

        internal HardThreadOwnership[] ActiveReceipts
        {
            get
            {
                lock (factsGate)
                {
                    HardThreadOwnership[] hardThreadOwnershipItems = new HardThreadOwnership[verified.Count];
                    verified.Values.CopyTo(hardThreadOwnershipItems, 0);
                    return hardThreadOwnershipItems;
                }
            }
        }

        internal HardThreadGuardianActor GuardianActor
        {
            get
            {
                if (guardian != null)
                {
                    return guardian.Actor;
                }
                return null;
            }
        }

        internal HardThreadStatus Status
        {
            get
            {
                return currentStatus;
            }
            private set
            {
                currentStatus = value;
            }
        }

        internal string Detail
        {
            get
            {
                return currentDetail;
            }
            private set
            {
                currentDetail = value;
            }
        }

        internal string LastReassertion { get; private set; }

        internal Func<bool> BeforeReassertion { get; set; }

        private CriticalThreadHardAffinitySession(IHardThreadGate gate, IHardThreadNative native, IHardThreadStore store, IHardThreadGuardian guardian = null)
        {
            this.gate = gate;
            this.native = native;
            this.store = store;
            this.guardian = guardian;
            Reload();
            Status = (Pending() ? HardThreadStatus.RecoveryPending : HardThreadStatus.Ready);
            if (guardian == null)
            {
                return;
            }
            guardian.OnLost(delegate
            {
                if (!disposed)
                {
                    try
                    {
                        Restore();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch
                    {
                        Status = HardThreadStatus.RecoveryPending;
                    }
                }
            });
        }

        internal static CriticalThreadHardAffinitySession OpenNative()
        {
            throw new NotSupportedException("original GameMode target lease bridge is required");
        }

        internal static CriticalThreadHardAffinitySession OpenNative(GameMode.CriticalThreadTargetLease original)
        {
            NativeGate nativeGate = new NativeGate(original);
            using (nativeGate.EnterUse())
            {
                nativeGate.RequireRecovery();
                HardThreadFileStore originalStore;
                IHardThreadGuardian hardThreadGuardian = CriticalThreadGuardian.Arm(original, out originalStore);
                CriticalThreadHardAffinityNative criticalThreadHardAffinityNative = null;
                try
                {
                    criticalThreadHardAffinityNative = CriticalThreadHardAffinityNative.Open(nativeGate.Target);
                    return new CriticalThreadHardAffinitySession(nativeGate, criticalThreadHardAffinityNative, originalStore, hardThreadGuardian);
                }
                catch
                {
                    try
                    {
                        HardThreadJournal hardThreadJournal = originalStore.Load(nativeGate.Target);
                        hardThreadGuardian.NoteSettled(originalStore.CaptureSettled(nativeGate.Target, hardThreadJournal.Revision));
                    }
                    catch
                    {
                    }
                    try
                    {
                        if (criticalThreadHardAffinityNative != null)
                        {
                            criticalThreadHardAffinityNative.Dispose();
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (originalStore != null)
                            {
                                originalStore.Dispose();
                            }
                        }
                        finally
                        {
                            hardThreadGuardian.Dispose();
                        }
                    }
                    throw;
                }
            }
        }

        internal static CriticalThreadHardAffinitySession OpenRecovery(CriticalThreadGuardian.StopProof original)
        {
            RecoveryGate recoveryGate = new RecoveryGate(original);
            using (recoveryGate.EnterUse())
            {
                recoveryGate.RequireRecovery();
                CriticalThreadHardAffinityNative criticalThreadHardAffinityNative = CriticalThreadHardAffinityNative.Open(original);
                HardThreadFileStore hardThreadFileStore = null;
                try
                {
                    hardThreadFileStore = new HardThreadFileStore(original.CanonicalStore);
                    hardThreadFileStore.BindOriginalScope(original.Target, original.SourceNonce, original.SourceScopeHash);
                    return new CriticalThreadHardAffinitySession(recoveryGate, criticalThreadHardAffinityNative, hardThreadFileStore);
                }
                catch
                {
                    try
                    {
                        criticalThreadHardAffinityNative.Dispose();
                    }
                    finally
                    {
                        if (hardThreadFileStore != null)
                        {
                            hardThreadFileStore.Dispose();
                        }
                    }
                    throw;
                }
            }
        }

        private bool Pending()
        {
            foreach (HardThreadReceipt row in journal.Rows)
            {
                if (!row.Terminal)
                {
                    return true;
                }
            }
            return false;
        }

        private void Save()
        {
            SaveJournal(journal.Clone());
        }

        private void SaveJournal(HardThreadJournal next)
        {
            long revision = journal.Revision;
            next.Revision = checked(revision + 1);
            lastOperationWriteIntent = true;
            store.Save(revision, next);
            // 克隆保留行顺序和线程身份，原位置编号也对应新行。
            journal = next;
            if (pendingApplied != null)
            {
                HardThreadReceipt completed = Find(pendingApplied);
                if (completed != null && completed.Phase == HardThreadPhase.Applied) Confirm(completed);
                else Forget(pendingApplied);
                pendingApplied = null;
            }
        }

        private void Confirm(HardThreadReceipt row)
        {
            lock (factsGate)
            {
                verified[row.Thread.Key] = new HardThreadOwnership(row.Thread, row.Group, row.Applied, DateTime.UtcNow.Ticks);
            }
        }

        private void Forget(HardThreadIdentity thread)
        {
            lock (factsGate)
            {
                verified.Remove(thread.Key);
            }
        }

        private void BeginOperation()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("hard thread session");
            }
            lastFailureKind = HardThreadFailureKind.None;
            lastOperationWriteIntent = false;
            lastOperationNativeAttempt = false;
            LastReassertion = null;
            reassertionAdmitted = false;
        }

        private void RequireForwardAuthority()
        {
            try
            {
                gate.RequireActive();
                if (guardian != null)
                {
                    guardian.RequireForward();
                }
            }
            catch (Exception ex)
            {
                throw new HardThreadOperationException(HardThreadFailureKind.AuthorityUnavailable, ex.Message, true);
            }
        }

        private void Reload()
        {
            HardThreadJournal loaded = store.Load(gate.Target);
            Dictionary<string, int> loadedIndex = IndexReceipts(loaded);
            journal = loaded;
            receiptIndex = loadedIndex;
            pendingApplied = null;
        }

        private Dictionary<string, int> IndexReceipts(HardThreadJournal source)
        {
            if (source == null || source.Target == null || !source.Target.Same(gate.Target)
                || source.Revision < 0 || source.Rows.Count > 512)
                throw new InvalidDataException("journal identity or bounds");
            var index = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int position = 0; position < source.Rows.Count; position++)
            {
                HardThreadReceipt row = source.Rows[position];
                if (row == null || row.Thread == null || (uint)row.Phase > (uint)HardThreadPhase.NotApplied
                    || row.Captured == 0 || row.Applied == 0 || (row.Applied & ~source.Target.AllowedMask) != 0
                    || (row.PreviousKnown && row.Previous == 0)
                    || (row.RestorePreviousKnown && (!row.PreviousKnown || row.RestorePrevious == 0)))
                    throw new InvalidDataException("journal receipt contract");
                if ((row.Phase == HardThreadPhase.PreviousRecorded || row.Phase == HardThreadPhase.Applied
                    || row.Phase == HardThreadPhase.RestoreIntent || row.Phase == HardThreadPhase.RestorePreviousRecorded
                    || row.Phase == HardThreadPhase.Restored) && !row.PreviousKnown)
                    throw new InvalidDataException("journal missing native previous");
                if (row.Phase == HardThreadPhase.RestorePreviousRecorded && !row.RestorePreviousKnown)
                    throw new InvalidDataException("journal missing restore previous");
                string key = row.Thread.Key;
                if (index.ContainsKey(key)) throw new InvalidDataException("overlapping thread receipts");
                // 同一执行者的终结历史行之后，仍可能出现新的存活记录。
                if (!row.Terminal) index.Add(key, position);
            }
            return index;
        }

        private HardThreadReceipt Find(HardThreadIdentity thread)
        {
            int position;
            if (thread == null || !receiptIndex.TryGetValue(thread.Key, out position)) return null;
            if (position < 0 || position >= journal.Rows.Count)
                throw new InvalidDataException("thread receipt position changed");
            HardThreadReceipt row = journal.Rows[position];
            if (row == null || row.Thread == null || row.Thread.Tid != thread.Tid || row.Thread.Creation != thread.Creation)
                throw new InvalidDataException("thread receipt identity changed");
            return row.Terminal ? null : row;
        }

        private void AddReceipt(HardThreadReceipt row)
        {
            if (row == null || row.Thread == null || journal.Rows.Count >= 512 || Find(row.Thread) != null)
                throw new InvalidDataException("duplicate or unbounded thread receipt");
            journal.Rows.Add(row);
            receiptIndex[row.Thread.Key] = journal.Rows.Count - 1;
        }
    }
}
