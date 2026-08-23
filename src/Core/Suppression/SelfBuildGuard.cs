// @author bdth 2074055628@qq.com
// 文件用途 认出同一产品的其它构建 免得 Pavise 压制自己的另一个可执行文件
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    // 单实例锁挡不住运行模式子进程 TryHandleRuntimeMode 在拿锁之前就 return 了
    //   所以 --selftest --irq-checkup --cpu-burn 这些会以另一个文件名和主实例并存
    //   扫描里只按进程名等于 selfName 排除自己 换个文件名就排不掉 实测被压到隔离档
    //   后果不只是烦 这些子进程本来就是拿来测量的 被压了测出来的数就是错的
    //
    // 判据取版本信息的 ProductName 所有构建都是 AssemblyProduct("Pavise")
    //   改文件名 改目录 改版本号都不影响 比按文件名前缀硬
    // 名字前缀只当预筛 不当判据 为的是别给几百个无关进程都读一次版本资源
    //   代价是把构建改名成完全不含本名前缀的样子就认不出来 那是刻意改名 不管
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

        // 自己进程名第一个点之前的那段 Pavise.dev 和 Pavise.selftest.work 都归到 Pavise
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
