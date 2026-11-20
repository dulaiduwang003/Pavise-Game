using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class QuantumAudit
    {
        internal sealed class Finding
        {
            internal string Value;

            internal string Note;

            internal bool Warn;

            // 前台时间片已下线 只剩 Pavise 自己留下的记录可以在体检里一键恢复
            internal bool CanRestore;
        }

        internal static bool HasRestorableRecord()
        {
            if (QuantumRecovery.LegacyRecordsForDisplay) return true;
            try
            {
                foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
                {
                    if (item.Id == PerformanceActionCatalog.Quantum && item.Committed) return true;
                }
            }
            catch
            {
            }
            return false;
        }

        internal static Finding Inspect()
        {
            string owned = null;
            bool legacyRecordsForDisplay = QuantumRecovery.LegacyRecordsForDisplay;
            string current;
            try
            {
                current = QuantumRecovery.Normalize(PerformanceActionBackend.Read("EnvQuantumProfileV1", QuantumRecovery.Target));
            }
            catch
            {
                current = null;
            }
            try
            {
                foreach (PerformanceActionStore.Receipt item in PerformanceActionStore.All())
                {
                    if (item.Id == "EnvQuantumProfileV1" && item.Committed && string.Equals(item.Target, QuantumRecovery.Target, StringComparison.OrdinalIgnoreCase))
                    {
                        owned = QuantumRecovery.Normalize(item.Expected);
                    }
                }
            }
            catch
            {
                owned = null;
            }
            return Classify(current, owned, legacyRecordsForDisplay);
        }

        internal static Finding Classify(string current, string owned, bool legacyRecords)
        {
            Finding finding = new Finding();
            if (legacyRecords)
            {
                finding.Value = PerformancePolicy.Text("旧版校正待恢复", "Legacy correction pending");
                finding.Note = PerformancePolicy.Text("健康检查已不再修改此值；旧版校正的原值记录仍在，可点「恢复」还原。", "Health check no longer changes this value. A legacy correction record remains; choose Restore to undo it.") + ((current == null || current == "absent") ? "" : (" " + Shown(current)));
                finding.CanRestore = true;
                return finding;
            }
            if (current == null)
            {
                finding.Value = PerformancePolicy.Text("无法读取", "Unreadable");
                finding.Note = PerformancePolicy.Text("未能读取 Win32PrioritySeparation，本次不做判断。", "Win32PrioritySeparation could not be read; no assessment this time.");
                return finding;
            }
            if (current == "absent")
            {
                finding.Value = PerformancePolicy.Text("系统默认", "System default");
                finding.Note = Lang.T("quantum.absent");
                return finding;
            }
            int parseResult = int.Parse(current.Substring(2), CultureInfo.InvariantCulture);
            if (owned != null && owned == current)
            {
                finding.Value = PerformancePolicy.Text("待恢复原值", "Original pending restore");
                finding.Note = Shown(current) + PerformancePolicy.Text("由已下线的「前台时间片」设置，Pavise 会自动恢复原值，也可点「恢复」立即还原。", " Set by the retired Foreground quantum option. Pavise restores the original automatically, or choose Restore now.");
                finding.CanRestore = true;
                return finding;
            }
            if (QuantumDecoder.Decode(parseResult).RawPrioSep > 2)
            {
                finding.Value = PerformancePolicy.Text("未文档化取值", "Undocumented value");
                finding.Note = Shown(current) + PerformancePolicy.Text("前台加权位为 3，微软未定义此取值，多半是其他优化工具改的；可在 Windows「系统属性 → 高级 → 性能设置 → 高级」选择「程序」改回默认。", " The foreground boost bits are 3, which Microsoft does not define; another tuning tool most likely set it. In Windows System Properties > Advanced > Performance Settings > Advanced, choose Programs to return to the default.");
                finding.Warn = true;
                return finding;
            }
            finding.Value = PerformancePolicy.Text("有效配置", "Valid setting");
            finding.Note = Shown(current) + PerformancePolicy.Text("这是文档化的有效配置，无需处理。", " This is a documented, valid configuration; no action needed.");
            return finding;
        }

        private static string Shown(string current)
        {
            int raw = int.Parse(current.Substring(2), CultureInfo.InvariantCulture);
            return PerformancePolicy.Text("当前值 0x", "Current value 0x") + raw.ToString("X", CultureInfo.InvariantCulture) + PerformancePolicy.Text("：", ": ") + QuantumDecoder.Decode(raw).Describe() + PerformancePolicy.Text("（前台:后台）。", " (foreground:background).");
        }
    }
}
