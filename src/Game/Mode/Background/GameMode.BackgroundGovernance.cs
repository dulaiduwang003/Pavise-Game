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
                    // 总开关仍关闭时先保存请求档位 家族锁防止扫描看到只完成一半的修改
                    if (!TrySetBackgroundGovernanceMode(selection - 1)) return false;
                    if (enabled && !backgroundGovernanceSaveUnverified) return true;
                }
                bool next = selection > 0, confirmed;
                saved = Settings.SaveDurable(PolicyCatalog.KeySuppress, next)
                    && Settings.TryLoadStoredBool(PolicyCatalog.KeySuppress, out confirmed) && confirmed == next;
                backgroundGovernanceSaveUnverified = !saved;
                InvalidateFamilyPolicy();
                if (!saved || selection == 0) core.SetResourceJobMode(-1, null);
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
            enabled = true; // 只有确认旧总开关不存在后 才使用原有默认值
            return true;
        }

        internal bool TrySetBackgroundGovernanceMode(int mode)
        {
            if (!BackgroundGovernancePolicy.Valid(mode)) return false;
            bool saved;
            lock (familyPolicyGate)
            {
                lock (sync)
                {
                    if (stopping || ProfileStoreSaveFailed) return false;
                    string current;
                    if (!Settings.TryLoadStr(BackgroundGovernancePolicy.SettingKey, out current)) return false;
                    if (current == BackgroundGovernancePolicy.Text(mode)) return true;
                    saved = BackgroundGovernancePolicy.Save(mode);
                    backgroundGovernanceSaveUnverified = !saved;
                    InvalidateFamilyPolicy();
                    firstSweep = true;
                }
                if (!saved || mode == BackgroundGovernancePolicy.Normal)
                    core.SetResourceJobMode(saved ? mode : -1, null);
            }
            RequestPolicyApply("TrySetBackgroundGovernanceMode");
            return saved;
        }
    }
}
