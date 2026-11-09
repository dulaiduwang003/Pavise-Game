using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed partial class CriticalThreadHardAffinitySession
    {

        internal bool Restore()
        {
            return RestoreInternal(true);
        }

        private void SettleExitedProcess()
        {
            HardThreadJournal settled = journal.Clone();
            bool changed = false;
            foreach (HardThreadReceipt row in settled.Rows)
            {
                if (row.Terminal) continue;
                row.Phase = HardThreadPhase.Gone;
                changed = true;
            }
            if (changed) SaveJournal(settled);
        }

        private bool RestoreInternal(bool resetOperation)
        {
            if (disposed)
            {
                throw new ObjectDisposedException("hard thread session");
            }
            try
            {
                using (gate.EnterUse())
                {
                    try
                    {
                        if (disposed)
                        {
                            return false;
                        }
                        if (resetOperation)
                        {
                            BeginOperation();
                        }
                        lock (factsGate)
                        {
                            verified.Clear();
                        }
                        gate.RequireRecovery();
                        Reload();
                        HardThreadMachine hardThreadMachine = native.ReadMachine();
                        if (hardThreadMachine.Read == HardThreadRead.Unknown)
                        {
                            throw new InvalidOperationException("original process identity is unknown");
                        }
                        if (hardThreadMachine.Read == HardThreadRead.Gone) SettleExitedProcess();
                        for (int rowsIndex = journal.Rows.Count - 1; rowsIndex >= 0; rowsIndex--)
                        {
                            HardThreadReceipt hardThreadReceipt = journal.Rows[rowsIndex];
                            if (!hardThreadReceipt.Terminal)
                            {
                                hardThreadMachine = native.ReadMachine();
                                if (hardThreadMachine.Read != HardThreadRead.Unknown)
                                {
                                    if (hardThreadMachine.Read == HardThreadRead.Gone)
                                    {
                                        SettleExitedProcess();
                                        break;
                                    }
                                    else
                                    {
                                        HardThreadObserved hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
                                        if (hardThreadObserved.Read == HardThreadRead.Gone)
                                        {
                                            hardThreadReceipt.Phase = HardThreadPhase.Gone;
                                            Save();
                                        }
                                        else if (hardThreadObserved.Read == HardThreadRead.Known && hardThreadObserved.CpuSetsEmpty && hardThreadMachine.CpuSetsEmpty && hardThreadReceipt.PreviousKnown && (hardThreadReceipt.Phase != HardThreadPhase.RestoreIntent || hardThreadReceipt.RestorePreviousKnown))
                                        {
                                            if (hardThreadReceipt.RestorePreviousKnown)
                                            {
                                                bool sameRestoreMask = hardThreadReceipt.RestorePreviousGroup == hardThreadReceipt.Group && hardThreadReceipt.RestorePrevious == hardThreadReceipt.Applied;
                                                ulong previous = (sameRestoreMask ? hardThreadReceipt.Previous : hardThreadReceipt.RestorePrevious);
                                                ushort previousGroup2 = (sameRestoreMask ? hardThreadReceipt.PreviousGroup : hardThreadReceipt.RestorePreviousGroup);
                                                if (hardThreadObserved.Group == previousGroup2 && hardThreadObserved.Mask == previous)
                                                {
                                                    hardThreadReceipt.Phase = HardThreadPhase.Restored;
                                                    Save();
                                                }
                                            }
                                            else if (hardThreadObserved.Group == hardThreadReceipt.PreviousGroup && hardThreadObserved.Mask == hardThreadReceipt.Previous)
                                            {
                                                hardThreadReceipt.Phase = HardThreadPhase.Restored;
                                                Save();
                                            }
                                            else if (hardThreadObserved.Group == hardThreadReceipt.Group && hardThreadObserved.Mask == hardThreadReceipt.Applied && hardThreadReceipt.PreviousGroup == hardThreadMachine.Group && (hardThreadReceipt.Previous & ~hardThreadMachine.ProcessMask) == 0)
                                            {
                                                gate.RequireRecovery();
                                                hardThreadReceipt.Phase = HardThreadPhase.RestoreIntent;
                                                hardThreadReceipt.RestorePreviousKnown = false;
                                                Save();
                                                hardThreadReceipt = journal.Rows[rowsIndex];
                                                hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
                                                hardThreadMachine = native.ReadMachine();
                                                if (hardThreadMachine.Read == HardThreadRead.Gone)
                                                {
                                                    SettleExitedProcess();
                                                    break;
                                                }
                                                if (hardThreadObserved.Read != 0 || hardThreadObserved.Group != hardThreadReceipt.Group || hardThreadObserved.Mask != hardThreadReceipt.Applied || !hardThreadObserved.CpuSetsEmpty || hardThreadMachine.Read != 0 || hardThreadMachine.GroupCount != 1 || hardThreadMachine.Group != hardThreadReceipt.PreviousGroup || !hardThreadMachine.CpuSetsEmpty || (hardThreadReceipt.Previous & ~hardThreadMachine.ProcessMask) != 0)
                                                {
                                                    hardThreadReceipt.Phase = HardThreadPhase.Applied;
                                                    Save();
                                                }
                                                else
                                                {
                                                    lastOperationNativeAttempt = true;
                                                    ushort previousGroup;
                                                    ulong previousMask;
                                                    if (native.Set(hardThreadReceipt.Thread, hardThreadReceipt.PreviousGroup, hardThreadReceipt.Previous, out previousGroup, out previousMask))
                                                    {
                                                        hardThreadReceipt.RestorePreviousKnown = true;
                                                        hardThreadReceipt.RestorePreviousGroup = previousGroup;
                                                        hardThreadReceipt.RestorePrevious = previousMask;
                                                        hardThreadReceipt.Phase = HardThreadPhase.RestorePreviousRecorded;
                                                        Save();
                                                        hardThreadReceipt = journal.Rows[rowsIndex];
                                                        hardThreadObserved = native.ReadThread(hardThreadReceipt.Thread);
                                                        if (previousGroup == hardThreadReceipt.Group && previousMask == hardThreadReceipt.Applied && hardThreadObserved.Read == HardThreadRead.Known && hardThreadObserved.Group == hardThreadReceipt.PreviousGroup && hardThreadObserved.Mask == hardThreadReceipt.Previous)
                                                        {
                                                            hardThreadReceipt.Phase = HardThreadPhase.Restored;
                                                            Save();
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                        bool settled = !Pending();
                        Status = (settled ? HardThreadStatus.Stopped : HardThreadStatus.RecoveryPending);
                        Detail = (settled ? "original owned changes restored or exact actors exited" : "recovery pending; unknown previous or external conflict is not overwritten");
                        if (resetOperation)
                        {
                            lastFailureKind = ((!settled) ? HardThreadFailureKind.RecoveryPending : HardThreadFailureKind.None);
                        }
                        if (settled)
                        {
                            plan = null;
                            IHardThreadSettledStore hardThreadSettledStore = store as IHardThreadSettledStore;
                            if (guardian != null && hardThreadSettledStore != null)
                            {
                                guardian.NoteSettled(hardThreadSettledStore.CaptureSettled(gate.Target, journal.Revision));
                            }
                        }
                        return settled;
                    }
                    catch (Exception ex)
                    {
                        Detail = ex.Message;
                        Status = HardThreadStatus.RecoveryPending;
                        if (resetOperation)
                        {
                            lastFailureKind = HardThreadFailureKind.RecoveryPending;
                        }
                        return false;
                    }
                    finally
                    {
                        PruneTransientReads();
                    }
                }
            }
            catch (Exception ex2)
            {
                Detail = ex2.Message;
                Status = HardThreadStatus.RecoveryPending;
                if (resetOperation)
                {
                    lastFailureKind = HardThreadFailureKind.RecoveryPending;
                }
                return false;
            }
        }

        // 只观察已停用守护退出，不消耗清理尝试；仍需原生 Dispose 工作时返回 false。

        internal bool RefreshGuardianClosure()
        {
            using (gate.EnterUse())
            {
                if (resourcesClosed)
                {
                    return true;
                }
                if (!disposed || !nativeClosed || !storeClosed || guardian == null || guardianClosed)
                {
                    return false;
                }
                guardianClosed = guardian.ResourcesClosed;
                resourcesClosed = guardianClosed;
                return true;
            }
        }

        public void Dispose()
        {
            using (gate.EnterUse())
            {
                if (resourcesClosed)
                {
                    return;
                }
                disposed = true;
                lock (factsGate)
                {
                    verified.Clear();
                }
                if (Pending())
                {
                    Status = HardThreadStatus.RecoveryPending;
                    Detail = "unrestored original ledger retained on disposal";
                }
                List<Exception> list = new List<Exception>();
                if (!nativeClosed)
                {
                    try
                    {
                        native.Dispose();
                        nativeClosed = true;
                    }
                    catch (Exception item)
                    {
                        list.Add(item);
                    }
                }
                if (!storeClosed)
                {
                    try
                    {
                        store.Dispose();
                        storeClosed = true;
                    }
                    catch (Exception item2)
                    {
                        list.Add(item2);
                    }
                }
                if (guardian == null)
                {
                    guardianClosed = true;
                }
                else if (storeClosed && !guardianClosed)
                {
                    try
                    {
                        guardian.Dispose();
                        guardianClosed = guardian.ResourcesClosed;
                    }
                    catch (Exception item3)
                    {
                        list.Add(item3);
                    }
                }
                resourcesClosed = nativeClosed && storeClosed && guardianClosed;
                if (list.Count <= 0)
                {
                    return;
                }
                throw new AggregateException("original hard affinity resource closure is incomplete", list);
            }
        }

    }
}
