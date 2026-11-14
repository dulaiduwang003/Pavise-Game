using System;

namespace PaviseApp
{
    internal sealed partial class SuppressionCore
    {
        private BackgroundRecoveryLedger backgroundRecoveryLedger;

        internal BackgroundRecoveryLedger BeginBackgroundRecoveryAccounting(long session)
        {
            lock (sync)
            {
                backgroundRecoveryLedger = new BackgroundRecoveryLedger(session);
                foreach (var pair in map)
                {
                    Entry entry = pair.Value;
                    if ((entry.Reasons & SuppressReason.Background) == 0) continue;
                    // 仍在治理的对象随新会话继续；旧恢复中的对象仍持有原会话票据。
                    RecordBackgroundOutcome(entry.BackgroundRecoveryTicket, BackgroundRecoveryLedger.State.OtherGovernance);
                    entry.BackgroundRecoveryTicket = null;
                    EnsureBackgroundAccountingLocked(pair.Key, entry);
                }
                return backgroundRecoveryLedger;
            }
        }

        private void EnsureBackgroundAccountingLocked(int pid, Entry entry)
        {
            if (backgroundRecoveryLedger == null || (entry.Reasons & SuppressReason.Background) == 0
                || pid <= 0 || entry.Creation <= 0 || entry.OrigPri == uint.MaxValue) return;
            BackgroundRecoveryLedger.Ticket ticket = entry.BackgroundRecoveryTicket;
            if (ticket == null || !object.ReferenceEquals(ticket.Owner, backgroundRecoveryLedger)
                || ticket.Pid != pid || ticket.Birth != entry.Creation)
            {
                entry.BackgroundRecoveryTicket = backgroundRecoveryLedger.Admit(pid, entry.Creation);
                if (entry.AccountingForwardEffects) backgroundRecoveryLedger.ObserveForwardEffects(entry.BackgroundRecoveryTicket);
                if (entry.AccountingForwardEffectsUnknown) backgroundRecoveryLedger.ObserveUnknownForwardEffects(entry.BackgroundRecoveryTicket);
                if (entry.AccountingExitedBirth == entry.Creation)
                    RecordBackgroundOutcome(entry.BackgroundRecoveryTicket, BackgroundRecoveryLedger.State.Exited);
            }
        }

        private void AccountBackgroundReasonsLocked(Entry entry, SuppressReason previous)
        {
            bool before = (previous & SuppressReason.Background) != 0;
            bool after = (entry.Reasons & SuppressReason.Background) != 0;
            if (!before && after)
            {
                RecordBackgroundOutcome(entry.BackgroundRecoveryTicket, BackgroundRecoveryLedger.State.OtherGovernance);
                entry.BackgroundRecoveryTicket = null;
            }
            EnsureBackgroundAccountingLocked(entry.AccountingPid, entry);
            if (!after && previous != entry.Reasons)
                RecordBackgroundOutcome(entry.BackgroundRecoveryTicket, entry.Reasons == SuppressReason.None
                    ? BackgroundRecoveryLedger.State.Pending : BackgroundRecoveryLedger.State.OtherGovernance);
        }

        private static void RecordBackgroundOutcome(BackgroundRecoveryLedger.Ticket ticket, BackgroundRecoveryLedger.State state)
        {
            if (ticket != null) ticket.Owner.Record(ticket, state);
        }

        private static void ObserveBackgroundAccountingExit(Entry entry)
        {
            entry.AccountingExitedBirth = entry.Creation;
            RecordBackgroundOutcome(entry.BackgroundRecoveryTicket, BackgroundRecoveryLedger.State.Exited);
        }

        private bool DiscardChangedTrackedNameLocked(int pid, string name, long verifiedCreation, Entry entry)
        {
            if (entry == null || SameName(entry.Name, name)) return false;
            // 名称只是索引变化；只有句柄已核实的创建时间不同才能结清旧进程。
            if (entry.Creation > 0 && verifiedCreation > 0 && entry.Creation != verifiedCreation)
                ObserveBackgroundAccountingExit(entry);
            else RecordBackgroundOutcome(entry.BackgroundRecoveryTicket, BackgroundRecoveryLedger.State.Pending);
            RemoveEntryLocked(pid);
            return true;
        }

        private static bool AccountBackgroundWrite(Entry entry, bool succeeded)
        {
            if (succeeded && entry != null)
            {
                entry.AccountingForwardEffects = true;
                BackgroundRecoveryLedger.Ticket ticket = entry.BackgroundRecoveryTicket;
                if (ticket != null) ticket.Owner.ObserveForwardEffects(ticket);
            }
            return succeeded;
        }

        private static bool RestoreCpuSetsDuringApply(IntPtr handle, uint[] cpuSets, Entry entry)
        {
            // 读回失败不能抹掉前一步已经发生的写入。
            return AccountBackgroundWrite(entry, Native.RestoreCpuSets(handle, cpuSets)) && Native.CpuSetsMatch(handle, cpuSets);
        }

        private static void AccountBackgroundUnknownWrite(Entry entry)
        {
            entry.AccountingForwardEffectsUnknown = true;
            BackgroundRecoveryLedger.Ticket ticket = entry.BackgroundRecoveryTicket;
            if (ticket != null) ticket.Owner.ObserveUnknownForwardEffects(ticket);
        }

        private static void CompleteBackgroundRestoration(BackgroundRecoveryLedger.Ticket ticket, RestoreResult result)
        {
            if (ticket == null) return;
            if (result == RestoreResult.Restored) ticket.Owner.RecordVerifiedRestore(ticket);
            else RecordBackgroundOutcome(ticket, result == RestoreResult.Gone ? BackgroundRecoveryLedger.State.Exited : BackgroundRecoveryLedger.State.Pending);
        }
    }
}
