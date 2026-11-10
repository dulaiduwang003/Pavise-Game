using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed class WindowsBackgroundTarget
    {
        internal ProcEntry Process;
        internal string Path, Category;
        internal string Identity { get { return Process.Pid + "/" + Process.Creation; } }
    }

    // 这是后台治理的内置名单，不按微软进程名称批量放行或批量接管。
    internal static class WindowsBackgroundPolicy
    {
        private static readonly HashSet<string> DiagnosticServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "DiagTrack", "DPS", "WdiServiceHost", "WdiSystemHost", "diagsvc" };

        internal static bool PotentialName(string name)
        {
            string normalized = WhitelistRule.NormalizeName(name).ToLowerInvariant();
            return normalized == "svchost" || normalized == "searchindexer" || normalized == "searchprotocolhost"
                || normalized == "searchfilterhost" || normalized == "widgets" || normalized == "widgetservice"
                || normalized == "msedgewebview2";
        }

        private static bool WindowsImage(string path, string windows, string image)
        {
            return WhitelistRule.PathEquals(path, Path.Combine(windows, "System32", image + ".exe"))
                || WhitelistRule.PathEquals(path, Path.Combine(windows, "SysWOW64", image + ".exe"));
        }

        internal static bool DelegatedImage(string name, string path, string windows, string programFiles)
        {
            string normalized = WhitelistRule.NormalizeName(name).ToLowerInvariant();
            if (normalized == "searchindexer" || normalized == "searchprotocolhost" || normalized == "searchfilterhost")
                return WindowsImage(path, windows, normalized);
            return WidgetImage(normalized, path, programFiles);
        }

        internal static bool ExplicitlyProtected(ProcEntry process, string path, IEnumerable<WhitelistRule> rules, Func<string, bool> preset)
        {
            foreach (WhitelistRule rule in rules)
            {
                if (rule == null) continue;
                if (rule.Kind == WhitelistRuleKind.LegacyName && preset(rule.Value)) continue;
                if (rule.MatchesNormalized(WhitelistRule.NormalizeName(process.Name), path)) return true;
            }
            return false;
        }

        internal static void AddExplicitFamilies(HashSet<int> protectedPids, IEnumerable<WhitelistRule> rules,
            IDictionary<string, HashSet<int>> evaluated, Func<string, bool> preset)
        {
            foreach (WhitelistRule rule in rules)
            {
                if (rule == null || (rule.Kind == WhitelistRuleKind.LegacyName && preset(rule.Value))) continue;
                HashSet<int> members;
                if (evaluated.TryGetValue(rule.Key, out members)) protectedPids.UnionWith(members);
            }
        }

        internal static bool WidgetImage(string name, string path, string programFiles)
        {
            string normalized = WhitelistRule.NormalizeName(name).ToLowerInvariant();
            if (string.IsNullOrEmpty(programFiles)) return false;
            if (normalized != "widgets" && normalized != "widgetservice") return false;
            string root = WhitelistRule.NormalizeImagePath(Path.Combine(programFiles, "WindowsApps")) + "\\";
            string image = WhitelistRule.NormalizeImagePath(path);
            if (!image.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            string relative = image.Substring(root.Length);
            int separator = relative.IndexOf('\\');
            if (separator <= 0) return false;
            string package = relative.Substring(0, separator), leaf = relative.Substring(separator + 1);
            if (!Path.GetFileNameWithoutExtension(leaf).Equals(normalized, StringComparison.OrdinalIgnoreCase)) return false;
            bool experience = package.StartsWith("MicrosoftWindows.Client.WebExperience_", StringComparison.OrdinalIgnoreCase)
                && package.EndsWith("_cw5n1h2txyewy", StringComparison.OrdinalIgnoreCase);
            bool runtime = package.StartsWith("Microsoft.WidgetsPlatformRuntime_", StringComparison.OrdinalIgnoreCase)
                && package.EndsWith("_8wekyb3d8bbwe", StringComparison.OrdinalIgnoreCase);
            return experience && (leaf.Equals("Dashboard\\Widgets.exe", StringComparison.OrdinalIgnoreCase)
                || leaf.Equals("Dashboard\\WidgetService.exe", StringComparison.OrdinalIgnoreCase))
                || runtime && leaf.Equals("WidgetService\\WidgetService.exe", StringComparison.OrdinalIgnoreCase);
        }

        private static bool WebViewImage(string path, string programFiles, string programFilesX86)
        {
            string image = WhitelistRule.NormalizeImagePath(path);
            foreach (string root in new[] { programFiles, programFilesX86 })
            {
                if (string.IsNullOrEmpty(root)) continue;
                string prefix = WhitelistRule.NormalizeImagePath(Path.Combine(root, "Microsoft\\EdgeWebView\\Application")) + "\\";
                if (!image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string tail = image.Substring(prefix.Length);
                int separator = tail.IndexOf('\\');
                Version version;
                if (separator > 0 && Version.TryParse(tail.Substring(0, separator), out version)
                    && tail.Substring(separator + 1).Equals("msedgewebview2.exe", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        internal static Dictionary<int, WindowsBackgroundTarget> Select(ProcessSnapshot snapshot, IList<KeyValuePair<string, int>> services,
            int session, string windows, string programFiles, string programFilesX86, Func<ProcEntry, string> readPath, HashSet<int> protectedPids)
        {
            var selected = new Dictionary<int, WindowsBackgroundTarget>();
            if (snapshot == null || string.IsNullOrEmpty(windows) || readPath == null) return selected;
            var hosts = new Dictionary<int, List<string>>();
            if (services != null) foreach (KeyValuePair<string, int> service in services)
            {
                if (service.Value <= 4) continue;
                List<string> names;
                if (!hosts.TryGetValue(service.Value, out names)) hosts[service.Value] = names = new List<string>();
                names.Add(service.Key);
            }
            foreach (KeyValuePair<int, List<string>> host in hosts)
            {
                ProcEntry process = snapshot.Find(host.Key);
                if (!Admissible(process, protectedPids) || process.Session != 0) continue;
                bool search = host.Value.Count == 1 && host.Value[0].Equals("WSearch", StringComparison.OrdinalIgnoreCase);
                bool diagnostic = host.Value.Count > 0;
                foreach (string name in host.Value) diagnostic &= DiagnosticServices.Contains(name);
                string image = WhitelistRule.NormalizeName(process.Name).ToLowerInvariant();
                if ((!search || image != "searchindexer") && (!diagnostic || image != "svchost")) continue;
                string path = readPath(process);
                bool valid = search ? WindowsImage(path, windows, "SearchIndexer") : WindowsImage(path, windows, "svchost");
                if (valid) selected[process.Pid] = new WindowsBackgroundTarget { Process = process, Path = path, Category = search ? "搜索索引" : "诊断服务" };
            }
            foreach (ProcEntry process in snapshot.Entries)
            {
                if (!Admissible(process, protectedPids)) continue;
                string name = WhitelistRule.NormalizeName(process.Name).ToLowerInvariant();
                if (name == "searchprotocolhost" || name == "searchfilterhost")
                {
                    if (process.Session != 0 && process.Session != session) continue;
                    WindowsBackgroundTarget parent;
                    if (!selected.TryGetValue(process.ParentPid, out parent) || parent.Category != "搜索索引"
                        || parent.Process.Creation >= process.Creation) continue;
                    string path = readPath(process);
                    if (WindowsImage(path, windows, name)) selected[process.Pid] = new WindowsBackgroundTarget { Process = process, Path = path, Category = "索引工作进程" };
                }
                else if (process.Session == session && (name == "widgets" || name == "widgetservice"))
                {
                    string path = readPath(process);
                    if (WidgetImage(name, path, programFiles)) selected[process.Pid] = new WindowsBackgroundTarget { Process = process, Path = path, Category = "小组件" };
                }
            }
            foreach (ProcEntry process in snapshot.Entries)
            {
                if (!Admissible(process, protectedPids) || process.Session != session
                    || !WhitelistRule.NormalizeName(process.Name).Equals("msedgewebview2", StringComparison.OrdinalIgnoreCase)) continue;
                ProcEntry current = process;
                var visited = new HashSet<int>();
                bool widget = false;
                for (int depth = 0; depth < 24 && current != null && visited.Add(current.Pid); depth++)
                {
                    WindowsBackgroundTarget root;
                    if (selected.TryGetValue(current.ParentPid, out root) && root.Category == "小组件"
                        && root.Process.Creation < current.Creation) { widget = true; break; }
                    ProcEntry parent = snapshot.Find(current.ParentPid);
                    if (parent == null || parent.Session != session || parent.Creation <= 0 || parent.Creation >= current.Creation) break;
                    current = parent;
                }
                if (!widget) continue;
                string path = readPath(process);
                if (WebViewImage(path, programFiles, programFilesX86)) selected[process.Pid] = new WindowsBackgroundTarget { Process = process, Path = path, Category = "小组件 WebView2" };
            }
            return selected;
        }

        private static bool Admissible(ProcEntry process, HashSet<int> protectedPids)
        { return process != null && process.Pid > 4 && process.Creation > 0 && (protectedPids == null || !protectedPids.Contains(process.Pid)); }

        internal static ulong SelectMask(ulong[] physicalCores, ulong allowed, ulong efficient, int mode)
        {
            ulong mask = 0;
            int wanted = mode == BackgroundGovernancePolicy.Aggressive ? 1 : 2;
            var chosen = new HashSet<ulong>();
            for (int pass = 0; pass < 2 && chosen.Count < wanted; pass++)
                for (int i = (physicalCores ?? new ulong[0]).Length - 1; i >= 0 && chosen.Count < wanted; i--)
                {
                    ulong core = physicalCores[i];
                    if (core == 0 || (core & allowed) != core || (pass == 0 && (core & efficient) != core) || !chosen.Add(core)) continue;
                    mask |= core;
                }
            return mask;
        }
    }
}
