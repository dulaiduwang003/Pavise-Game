using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace PaviseApp
{
    internal static class GamePerformanceTargetPolicy
    {
        internal sealed class Metadata
        { internal string Company, Product, Description, Original; }
        private sealed class Cached
        { internal bool Denied; internal long Until; }
        private static readonly object gate = new object();
        private static readonly string windowsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\', '/') + "\\";
        private static readonly Dictionary<string, Cached> cache = new Dictionary<string, Cached>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> helpers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "UnityCrashHandler32", "UnityCrashHandler64", "crashpad_handler", "WerFault", "WerFaultSecure",
            "QtWebEngineProcess", "cef_subprocess", "msedgewebview2", "DXSETUP", "vcredist_x86", "vcredist_x64" };
#if PAVISE_SELFTEST
        internal static Func<string, Metadata> MetadataForTest;
        internal static Func<long> ClockForTest;
        internal static void ResetForTest() { lock (gate) cache.Clear(); MetadataForTest = null; ClockForTest = null; }
#endif
        private static long Now()
        {
#if PAVISE_SELFTEST
            if (ClockForTest != null) return ClockForTest();
#endif
            return DateTime.UtcNow.Ticks;
        }
        internal static bool KnownNonGame(string name, string path)
        {
            name = WhitelistRule.NormalizeName(name);
            string normalized = WhitelistRule.NormalizeImagePath(path);
            if (helpers.Contains(name) || HardwareControlCatalog.IsHardwareControlProcess(name)) return true;
            if (normalized.Length == 0) return false;
            // Java 宿主可以渲染游戏，不能仅因家族边界不安全而拒绝识别。
            if (KnownWorkPackage(normalized)
                || (WhitelistRule.IsUnsafeFamilyAnchor(normalized) && !WhitelistRule.IsJavaRuntimeHost(normalized))
                || PlatformRuntimePolicy.RuntimePlatform(name) != null
                || OverlayHostCatalog.ShouldProtectProcess(name, normalized, true)) return true;
            long now = Now();
            lock (gate)
            {
                Cached known;
                if (cache.TryGetValue(normalized, out known) && now < known.Until) return known.Denied;
            }
            Metadata facts = null;
            try
            {
#if PAVISE_SELFTEST
                if (MetadataForTest != null) facts = MetadataForTest(normalized);
#else
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(normalized);
                facts = new Metadata { Company = info.CompanyName, Product = info.ProductName,
                    Description = info.FileDescription, Original = info.OriginalFilename };
#endif
            }
            catch { }
            bool denied = KnownWorkMetadata(facts);
            lock (gate)
            {
                if (cache.Count >= 256 && !cache.ContainsKey(normalized)) cache.Clear();
                cache[normalized] = new Cached { Denied = denied,
                    Until = now + (facts == null ? TimeSpan.FromSeconds(30).Ticks : TimeSpan.FromMinutes(5).Ticks) };
            }
            return denied;
        }
        private static bool KnownWorkPackage(string path)
        {
            string lower = path.Replace('/', '\\').ToLowerInvariant();
            const string boundary = "\\windowsapps\\";
            int index = lower.IndexOf(boundary, StringComparison.Ordinal);
            if (index < 0) return false;
            index += boundary.Length;
            int end = lower.IndexOf('\\', index);
            if (end < 0) return false;
            string package = lower.Substring(index, end - index);
            return package.StartsWith("openai.codex_", StringComparison.Ordinal)
                || package.StartsWith("openai.chatgpt_", StringComparison.Ordinal);
        }
        internal static bool KnownWorkMetadata(Metadata facts)
        {
            if (facts == null) return false;
            string company = (facts.Company ?? "").Trim(), product = (facts.Product ?? "").Trim();
            if ((Equal(company, "OpenAI OpCo, LLC") || Equal(company, "OpenAI"))
                && (Equal(product, "Codex") || Equal(product, "ChatGPT"))) return true;
            if ((Equal(company, "Google LLC") || Equal(company, "Google Inc."))
                && (Equal(product, "Google Chrome") || Equal(product, "Chrome"))) return true;
            if ((Equal(company, "Mozilla Corporation") || Equal(company, "Mozilla Foundation"))
                && Equal(product, "Firefox")) return true;
            if (Equal(company, "Microsoft Corporation"))
                return Equal(product, "Microsoft Edge") || Equal(product, "Microsoft Edge WebView2")
                    || Equal(product, "Visual Studio Code") || Equal(product, "Microsoft Visual Studio")
                    || product.StartsWith("Microsoft Office", StringComparison.OrdinalIgnoreCase)
                    || Equal(product, "Microsoft OneDrive");
            if (Equal(company, "Adobe Inc.") || Equal(company, "Adobe Systems Incorporated"))
                return product.StartsWith("Adobe Acrobat", StringComparison.OrdinalIgnoreCase)
                    || product.StartsWith("Adobe Photoshop", StringComparison.OrdinalIgnoreCase)
                    || product.StartsWith("Adobe Premiere", StringComparison.OrdinalIgnoreCase)
                    || product.StartsWith("Adobe After Effects", StringComparison.OrdinalIgnoreCase);
            return false;
        }
        private static bool Equal(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        internal static bool AllowsGameTarget(string path)
        {
            string normalized = WhitelistRule.NormalizeImagePath(path);
            if (normalized.Length == 0 || !normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return false;
            // Java 游戏只允许按进程调度，持久名称设置会影响共享运行时。
            if (WhitelistRule.IsJavaRuntimeHost(normalized)) return false;
            return GameSessionDetector.IsLibraryCandidate(Path.GetFileNameWithoutExtension(normalized), normalized, windowsRoot);
        }
        internal static string[] FilterGameTargets(string[] paths)
        {
            var accepted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (paths != null) foreach (string path in paths) if (AllowsGameTarget(path)) accepted.Add(path);
            return new List<string>(accepted).ToArray();
        }
    }
}
