// @author bdth 2074055628@qq.com
// File purpose Recognizes other builds of the same product so Pavise doesn't suppress another executable of itself
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    // The single-instance lock doesn't catch runtime-mode child processes; TryHandleRuntimeMode returns before taking the lock
    //   so --selftest --cpu-burn and friends coexist with the main instance under a different file name
    //   The scan excludes self only by process name == selfName; a different file name slips through, observed being suppressed to the isolated level
    //   Worse than annoying: these children exist to measure, and suppressed measurements are wrong
    //
    // Criterion is ProductName from version info; every build is AssemblyProduct("Pavise")
    //   Renaming the file, moving the directory or changing the version doesn't affect it; sturdier than a file-name prefix
    // The name prefix is only a pre-filter, not the criterion, so hundreds of unrelated processes don't each get a version-resource read
    //   Cost: a build renamed to something without the base prefix goes unrecognized; that's a deliberate rename, ignored
    internal static class SelfBuildGuard
    {
        private const int MaxCached = 512;
        private static readonly object sync = new object();
        private static readonly Dictionary<string, bool> cache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static string selfProduct;
        private static string selfPrefix;

        internal static string SelfProduct
        {
            get
            {
                if (selfProduct != null) return selfProduct;
                string p = "";
                try
                {
                    object[] a = Assembly.GetExecutingAssembly()
                        .GetCustomAttributes(typeof(AssemblyProductAttribute), false);
                    if (a.Length > 0) p = ((AssemblyProductAttribute)a[0]).Product ?? "";
                }
                catch { }
                selfProduct = p;
                return selfProduct;
            }
        }

        // Segment of our own process name before the first dot; Pavise.dev and Pavise.selftest.work both map to Pavise
        internal static string PrefixOf(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return "";
            int dot = processName.IndexOf('.');
            return dot > 0 ? processName.Substring(0, dot) : processName;
        }

        internal static string SelfPrefix
        {
            get
            {
                if (selfPrefix != null) return selfPrefix;
                string n = "";
                try { using (Process p = Process.GetCurrentProcess()) n = p.ProcessName; }
                catch { }
                selfPrefix = PrefixOf(n);
                return selfPrefix;
            }
        }

        internal static bool NameCouldBeOurs(string processName, string selfPrefixValue)
        {
            if (string.IsNullOrEmpty(processName) || string.IsNullOrEmpty(selfPrefixValue)) return false;
            return processName.StartsWith(selfPrefixValue, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool ProductMatches(string product)
        {
            string mine = SelfProduct;
            return mine.Length > 0 && string.Equals(mine, product, StringComparison.Ordinal);
        }

        public static bool IsOwnBuild(string processName, string imagePath)
        {
            if (string.IsNullOrEmpty(imagePath)) return false;
            if (!NameCouldBeOurs(processName, SelfPrefix)) return false;
            if (SelfProduct.Length == 0) return false;
            lock (sync)
            {
                bool hit;
                if (cache.TryGetValue(imagePath, out hit)) return hit;
                hit = false;
                try { hit = ProductMatches(FileVersionInfo.GetVersionInfo(imagePath).ProductName); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                if (cache.Count >= MaxCached) cache.Clear();
                cache[imagePath] = hit;
                return hit;
            }
        }
    }
}
