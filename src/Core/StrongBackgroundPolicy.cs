using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    // 普通后台准入不授权挂起，仅识别明确可延后的更新任务。
    internal static class StrongBackgroundPolicy
    {
        private static readonly object gate = new object();
        private static readonly Dictionary<string, bool> identities = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        internal static bool CandidateName(string name)
        {
            switch (WhitelistRule.NormalizeName(name).ToLowerInvariant())
            {
                case "googleupdate": case "googleupdater": case "microsoftedgeupdate":
                case "onedrivestandaloneupdater": case "adobearm": case "jusched": return true;
                default: return false;
            }
        }
        internal static bool MetadataMatches(string name, string original, string company, string product)
        {
            name = WhitelistRule.NormalizeName(name);
            if (!CandidateName(name) || !string.Equals(name, WhitelistRule.NormalizeName(original), StringComparison.OrdinalIgnoreCase)) return false;
            company = (company ?? "").Trim(); product = product ?? "";
            switch (name.ToLowerInvariant())
            {
                case "googleupdate": case "googleupdater":
                    return (company == "Google LLC" || company == "Google Inc.") && product.IndexOf("Google", StringComparison.OrdinalIgnoreCase) >= 0;
                case "microsoftedgeupdate":
                    return company == "Microsoft Corporation" && product.IndexOf("Microsoft Edge", StringComparison.OrdinalIgnoreCase) >= 0;
                case "onedrivestandaloneupdater":
                    return company == "Microsoft Corporation" && product.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0;
                case "adobearm":
                    return (company == "Adobe Inc." || company == "Adobe Systems Incorporated") && product.IndexOf("Adobe", StringComparison.OrdinalIgnoreCase) >= 0;
                case "jusched":
                    return company == "Oracle Corporation" && product.IndexOf("Java", StringComparison.OrdinalIgnoreCase) >= 0;
                default: return false;
            }
        }
        internal static bool ApprovedImage(string path)
        {
            path = WhitelistRule.NormalizeImagePath(path);
            if (path.Length == 0 || !CandidateName(Path.GetFileNameWithoutExtension(path))) return false;
            try
            {
                var file = new FileInfo(path);
                string key = path + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
                lock (gate) { bool cached; if (identities.TryGetValue(key, out cached)) return cached; }
                var version = FileVersionInfo.GetVersionInfo(path);
                bool valid = MetadataMatches(Path.GetFileNameWithoutExtension(path), version.OriginalFilename, version.CompanyName, version.ProductName);
                lock (gate) { if (identities.Count >= 256) identities.Clear(); identities[key] = valid; }
                return valid;
            }
            catch { return false; }
        }
        internal static bool Admit(PerformancePreset mode, long creation, bool protectedFamily,
            bool hasChildren, bool service, bool approvedImage)
        {
            return mode == PerformancePreset.Extreme && creation > 0 && !protectedFamily
                && (service || (!hasChildren && approvedImage));
        }
    }
}
