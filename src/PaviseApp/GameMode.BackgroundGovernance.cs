using System;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private volatile bool backgroundGovernanceSaveUnverified;

        internal bool TrySetBackgroundGovernanceSelection(int selection)
        {
            if (selection < 0 || selection > 2) return false;
            bool saved;
            lock (familyPolicyGate)
            {
                lock (sync) { if (stopping || ProfileStoreSaveFailed) return false; }
                bool enabled;
                if (!TryReadBackgroundGovernanceMaster(out enabled)) return false;
                if (selection == 0 && !enabled) return true;
                if (selection > 0)
                {
                    // Persist the requested level while the master is still off;
                    // the family gate prevents a scan from applying half a change.
                    if (!TrySetBackgroundGovernanceMode(selection - 1)) return false;
                    if (enabled && !backgroundGovernanceSaveUnverified) return true;
                }
                bool next = selection > 0, confirmed;
                saved = Settings.SaveDurable(PolicyCatalog.KeySuppress, next)
                    && Settings.TryLoadStoredBool(PolicyCatalog.KeySuppress, out confirmed) && confirmed == next;
                backgroundGovernanceSaveUnverified = !saved;
                InvalidateFamilyPolicy();
                SyncAutomationPolicy();
                lock (sync) firstSweep = true;
            }
            RequestPolicyApply("TrySetBackgroundGovernanceSelection");
            return saved;
        }

        private static bool TryReadBackgroundGovernanceMaster(out bool enabled)
        {
            if (Settings.TryLoadStoredBool(PolicyCatalog.KeySuppress, out enabled)) return true;
            string[] stored;
            if (!Settings.TryLoadStoredNames(PolicyCatalog.KeySuppress, out stored)) return false;
            foreach (string key in stored) if (string.Equals(key, PolicyCatalog.KeySuppress, StringComparison.OrdinalIgnoreCase)) return false;
            enabled = true; // Existing master default, only after verified absence.
            return true;
        }

        internal bool TrySetBackgroundGovernanceMode(int mode)
        {
            if (mode != BackgroundGovernancePolicy.Normal && mode != BackgroundGovernancePolicy.Aggressive) return false;
            bool saved;
            lock (familyPolicyGate)
            {
                lock (sync)
                {
                    if (stopping || ProfileStoreSaveFailed) return false;
                    string current;
                    if (!Settings.TryLoadStr(BackgroundGovernancePolicy.SettingKey, out current)) return false;
                    if (current == (mode == BackgroundGovernancePolicy.Aggressive ? "1" : "0")) return true;
                    saved = BackgroundGovernancePolicy.Save(mode);
                    backgroundGovernanceSaveUnverified = !saved;
                    InvalidateFamilyPolicy();
                    firstSweep = true;
                }
            }
            RequestPolicyApply("TrySetBackgroundGovernanceMode");
            return saved;
        }
    }
}
