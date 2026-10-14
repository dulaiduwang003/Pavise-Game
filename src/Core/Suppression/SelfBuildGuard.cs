// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    // 按产品信息保护本项目运行模式，避免测试副本被误压制。
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
