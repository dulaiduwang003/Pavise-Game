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
                        if (!RestoreGameThreadControlBeforeAffinity())
                        {
                            Status = HardThreadStatus.RecoveryPending;
                            Detail = "统一线程属性恢复尚未结清，保留原守护与亲和性收据";
                            return false;
                        }
                        Reload();
                        HardThreadMachine hardThreadMachine = null;
                        // 优先级已独立结清且没有亲和性恢复义务时，不用选核拓扑阻止空账本关闭。
                        if (Pending())
                        {
                            hardThreadMachine = native.ReadMachine();
                            if (hardThreadMachine.Read == HardThreadRead.Unknown)
                                throw new InvalidOperationException("original process identity is unknown");
                            if (hardThreadMachine.Read == HardThreadRead.Gone) SettleExitedProcess();
                        }
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
                        Detail = settled ? "原归属写入已恢复，或原目标已退出"
                            : "恢复尚未结清；原值未知或外部冲突时保留现场；" + PendingRecoveryFacts();
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

        private string PendingRecoveryFacts()
        {
            var facts = new List<string>();
            int pending = 0;
            // 只展示已保存的恢复证据，不为诊断额外查询目标，也不扩大恢复次数。
            foreach (HardThreadReceipt row in journal.Rows)
            {
                if (row.Terminal) continue;
                pending++;
                if (facts.Count >= 3) continue;
                facts.Add("线程=" + row.Thread.Tid + "/" + row.Thread.Creation + " 阶段=" + row.Phase
                    + " 采集原值=0x" + row.Captured.ToString("X") + " 本方目标=0x" + row.Applied.ToString("X")
                    + " 写前原值=" + (row.PreviousKnown ? row.PreviousGroup + "/0x" + row.Previous.ToString("X") : "未知")
                    + " 恢复写前值=" + (row.RestorePreviousKnown ? row.RestorePreviousGroup + "/0x" + row.RestorePrevious.ToString("X") : "未确认"));
            }
            return "待恢复=" + pending + "；" + string.Join(" | ", facts.ToArray());
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
                if (!RestoreGameThreadControlBeforeAffinity())
                {
                    Status = HardThreadStatus.RecoveryPending;
                    Detail = "统一线程恢复尚未结清，禁止释放独立守护与原权限";
                    throw new InvalidOperationException(Detail);
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
