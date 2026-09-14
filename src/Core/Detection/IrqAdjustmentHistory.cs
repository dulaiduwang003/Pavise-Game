using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // A recent attempt and the adjustment whose configuration is still active are distinct
    internal static class IrqAdjustmentHistory
    {
        internal static IrqAdjustment Latest(IList<IrqAdjustment> records, string device)
        {
            if (records != null)
                for (int i = records.Count - 1; i >= 0; i--)
                    if (Matches(records[i], device)) return records[i];
            return null;
        }

        internal static IrqAdjustment Current(IList<IrqAdjustment> records, string device)
        {
            IrqAdjustment latest = null;
            if (records != null)
                for (int i = records.Count - 1; i >= 0; i--)
                {
                    var a = records[i];
                    if (!Matches(a, device)) continue;
                    if (latest == null) latest = a;
                    bool noChange = a.Phase == "unchanged" && (a.NoDeviceWrite || !a.PriorityRequested)
                        || a.Phase == "failed" && a.NoDeviceWrite
                            && a.AffinityResult == "failed" && a.PriorityResult == "skipped"
                            && a.RollbackResult == "skipped";
                    // An attempted write can restore the original baseline not the previous
                    // adjustment Unknown outcomes and restoration always end the search
                    if (!noChange || a.RestoredUtc > 0) return a;
                }
            // Keep an unverified record instead of falling back to legacy placement evidence
            return latest;
        }

        private static bool Matches(IrqAdjustment a, string device)
        { return a != null && !string.IsNullOrEmpty(device)
            && string.Equals(a.Device, device, StringComparison.OrdinalIgnoreCase); }

        internal static void RecordWrite(IrqAdjustment a, IrqChangeResult result,
            IrqWriteResult write, bool priorityUnchanged)
        {
            a.AffinityResult = result.Affinity ? "ok" : "failed";
            a.PriorityResult = !result.PriorityAttempted ? "skipped" : result.Priority ? "ok" : "failed";
            a.RollbackResult = write.RollbackFailed > 0 ? "failed" : write.RolledBack > 0 ? "ok" : "skipped";
            a.Phase = result.Success ? "written" : result.Affinity ? "partial" : "failed";
            if (result.Success && write.Unchanged > 0 && write.Attempted == 0
                && (!result.PriorityRequested || priorityUnchanged)) a.Phase = "unchanged";
            a.NoDeviceWrite = a.Phase == "unchanged" || !result.Affinity && result.AffinityCompleted
                && !result.PriorityAttempted && write.Attempted == 0 && write.Succeeded == 0
                && write.RolledBack == 0 && write.RollbackFailed == 0;
        }

        internal static List<IrqAdjustment> RecordRestore(IList<IrqAdjustment> records,
            IrqRestoreResult result, long restoredUtc)
        {
            var changed = new List<IrqAdjustment>();
            if (records == null || result == null || !result.ScopeReadSucceeded) return changed;
            foreach (var a in records)
            {
                if (a == null || a.Phase == "restored") continue;
                var outcome = result.ForDevice(a.Device);
                if (outcome == null) continue;
                // Retrying only the remaining receipt must retain the successful first step
                if (outcome.AffinityRequested || a.RestoreAffinityResult != "ok")
                    a.RestoreAffinityResult = outcome.AffinityResult;
                if (outcome.PriorityRequested || a.RestorePriorityResult != "ok")
                    a.RestorePriorityResult = outcome.PriorityResult;
                a.Phase = outcome.Success ? "restored" : "restorepartial";
                if (a.RestoredUtc == 0) a.RestoredUtc = restoredUtc;
                changed.Add(a);
            }
            return changed;
        }
    }
}
