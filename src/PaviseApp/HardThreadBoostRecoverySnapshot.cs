using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class HardThreadBoostRecoverySnapshot
    {
        internal static readonly HardThreadBoostRecoverySnapshot Unknown = new HardThreadBoostRecoverySnapshot(false, new HashSet<string>());
        internal static readonly HardThreadBoostRecoverySnapshot Clear = new HardThreadBoostRecoverySnapshot(true, new HashSet<string>());
        private readonly bool known;
        private readonly HashSet<string> blocked;

        private HardThreadBoostRecoverySnapshot(bool known, HashSet<string> blocked)
        { this.known = known; this.blocked = blocked; }

        private static string Identity(int pid, long birth)
        { return pid.ToString(CultureInfo.InvariantCulture) + ":" + birth.ToString(CultureInfo.InvariantCulture); }

        internal bool Allows(int pid, long birth)
        { return known && pid > 0 && birth > 0 && !blocked.Contains(Identity(pid, birth)); }

        internal static HardThreadBoostRecoverySnapshot FromJournals(HardThreadJournal[] journals, bool controllerDebt)
        {
            if (journals == null || journals.Length > 1024 || controllerDebt) return Unknown;
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (HardThreadJournal journal in journals)
            {
                if (journal == null || journal.Target == null || journal.Revision <= 0 || journal.Target.ProcessId <= 0
                    || journal.Target.ProcessCreation <= 0 || journal.Rows.Count > 512) return Unknown;
                // 统一属性账本由同一守护承载；空亲和性记录不表示旧提优器可以重新接管。
                if (journal.ControlDeclared) targets.Add(Identity(journal.Target.ProcessId, journal.Target.ProcessCreation));
                foreach (HardThreadReceipt row in journal.Rows)
                {
                    if (row == null || row.Thread == null || row.Thread.Tid <= 0 || row.Thread.Creation <= 0
                        || row.Captured == 0 || row.Applied == 0 || (row.PreviousKnown && row.Previous == 0)
                        || (row.Applied & ~journal.Target.AllowedMask) != 0 || (row.RestorePreviousKnown && (!row.PreviousKnown || row.RestorePrevious == 0))
                        || !Enum.IsDefined(typeof(HardThreadPhase), row.Phase)) return Unknown;
                    if (!row.Terminal) targets.Add(Identity(journal.Target.ProcessId, journal.Target.ProcessCreation));
                }
            }
            return new HardThreadBoostRecoverySnapshot(true, targets);
        }

        internal static HardThreadBoostRecoverySnapshot Read(string root)
        {
            try
            {
                if (!HardThreadFileStore.CanonicalRootHasRecoveryDebt(root)) return Clear;
                if (CriticalThreadGuardian.HasControllerDebt(root)) return Unknown;
                HardThreadBoostRecoverySnapshot snapshot = FromJournals(HardThreadFileStore.ObserveHistoricalJournals(root), false);
                return CriticalThreadGuardian.HasControllerDebt(root) ? Unknown : snapshot;
            }
            catch { return Unknown; }
        }
    }
}
