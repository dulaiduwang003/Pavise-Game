using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    // 通用服务宿主不参与压制。
    internal static class BackgroundServicePolicy
    {
        internal static bool CandidateName(string name)
        {
            return string.Equals(name, "SearchIndexer", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "spoolsv", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool SupportedMode(PerformancePreset mode)
        { return mode == PerformancePreset.Competitive || mode == PerformancePreset.Extreme; }

        internal static bool Eligible(PerformancePreset mode, ProcEntry process,
            IList<KeyValuePair<string, int>> services, string windowsRoot)
        {
            if (!SupportedMode(mode) || process == null || process.Pid <= 4
                || process.Session != 0 || process.Creation <= 0 || services == null
                || string.IsNullOrEmpty(windowsRoot) || !CandidateName(process.Name)) return false;
            string expected = string.Equals(process.Name, "SearchIndexer", StringComparison.OrdinalIgnoreCase)
                ? "WSearch" : "Spooler";
            string image = Path.Combine(windowsRoot, "System32", process.Name + ".exe");
            if (!WhitelistRule.PathEquals(process.Path, image)) return false;
            bool found = false;
            foreach (var service in services)
            {
                if (service.Value != process.Pid) continue;
                if (!string.Equals(service.Key, expected, StringComparison.OrdinalIgnoreCase)) return false;
                found = true;
            }
            return found;
        }
    }
}
