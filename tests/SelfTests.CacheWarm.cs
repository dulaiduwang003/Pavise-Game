// All power, memory, disk probing and file reads are injected. No native disk
// access, registry or windows are used.
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int cacheWarmChecks;
        private const long WarmMiB = 1024L * 1024L;
        private const ulong WarmGiB = 1024UL * 1024UL * 1024UL;

        internal static int RunCacheWarmRegressionTests()
        {
            Action[] tests =
            {
                CacheWarmSelectionOrdersByAccessThenSizeAndCapsBudget,
                CacheWarmSkipsOnBattery,
                CacheWarmSkipsWhenMemoryLow,
                CacheWarmSkipsOnSeekPenaltyOrUnknownDisk,
                CacheWarmSkipsWhenNoFiles,
                CacheWarmWarmsPicksAndReports,
                CacheWarmPriorityFailureSkipsWithoutReads,
                CacheWarmAbortStopsBetweenFiles,
                CacheWarmFileFailureSkipsFileAndContinues,
                CacheWarmChunkLoopHonorsAbort,
                CacheWarmChunkLoopStopsWhenMemoryFloorBreached,
                CacheWarmChunkLoopStopsWhenPowerUnplugged,
                CacheWarmForcedPathSkipsNvme,
                CacheWarmChunkLoopToleratesReadFailure
            };
            cacheWarmChecks = 0;
            foreach (Action test in tests)
            {
                Settings.UseTransientStoreForCurrentProcess();
                Lang.Cur = 0;
                CacheWarm.ResetForTest();
                try { test(); }
                finally { CacheWarm.ResetForTest(); }
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS cache-warm assertions=" + cacheWarmChecks
                + " native_disk=mocked settings=transient windows_shown=false");
            return tests.Length;
        }

        private static void WarmCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Cache warm-up regression: " + message);
            Interlocked.Increment(ref cacheWarmChecks);
        }

        private static CacheWarm.WarmCandidate WarmFile(string path, long size, long access)
        {
            return new CacheWarm.WarmCandidate { Path = path, Size = size, AccessTicks = access };
        }

        // 健康机器的默认注入：交流供电 内存充裕 固态盘 优先级组合可用
        private static void WarmDefaults()
        {
            CacheWarm.PowerForTest = delegate { return true; };
            CacheWarm.MemoryStatusForTest = delegate(out ulong total, out ulong avail)
            { total = 16UL * WarmGiB; avail = 8UL * WarmGiB; return true; };
            CacheWarm.SeekPenaltyForTest = delegate { return (bool?)false; };
            CacheWarm.PoliteReadForTest = delegate { return true; };
            CacheWarm.SleepForTest = delegate { };
        }

        private static void CacheWarmSelectionOrdersByAccessThenSizeAndCapsBudget()
        {
            var files = new List<CacheWarm.WarmCandidate>
            {
                WarmFile("old-big", 3L * 1024 * WarmMiB, 100),
                WarmFile("tiny", 1 * WarmMiB, 900),
                WarmFile("hot-small", 8 * WarmMiB, 800),
                WarmFile("hot-large", 512 * WarmMiB, 800),
                WarmFile("warm", 1024 * WarmMiB, 500)
            };
            List<CacheWarm.WarmPick> picks = CacheWarm.SelectFiles(files, CacheWarm.WarmBudgetBytes);
            WarmCheck(picks.Count == 4, "expected four picks, got " + picks.Count);
            WarmCheck(picks[0].Path == "hot-large", "hottest large file must come first");
            WarmCheck(picks[1].Path == "hot-small", "同热度按大小排序 hot-small second");
            WarmCheck(picks[2].Path == "warm", "recency outranks size across heat levels");
            WarmCheck(picks[3].Path == "old-big", "coldest file last");
            long total = 0;
            foreach (CacheWarm.WarmPick pick in picks) total += pick.Take;
            WarmCheck(total == CacheWarm.WarmBudgetBytes, "picks must fill exactly the budget");
            WarmCheck(picks[3].Take < 3L * 1024 * WarmMiB, "last pick must be truncated to the budget");
            foreach (CacheWarm.WarmPick pick in picks)
                WarmCheck(pick.Path != "tiny", "files under the size floor must be filtered");
            WarmCheck(CacheWarm.SelectFiles(null, CacheWarm.WarmBudgetBytes).Count == 0,
                "null file list must select nothing");
        }

        private static void CacheWarmSkipsOnBattery()
        {
            WarmDefaults();
            CacheWarm.PowerForTest = delegate { return false; };
            // 枚举和读取故意不注入 走到那一步会立刻抛 电池跳过必须发生在这之前
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(!result.Ran && result.SkipKey == "log.cachewarm.5", "battery must skip the match");
        }

        private static void CacheWarmSkipsWhenMemoryLow()
        {
            WarmDefaults();
            CacheWarm.MemoryStatusForTest = delegate(out ulong total, out ulong avail)
            { total = 16UL * WarmGiB; avail = CacheWarm.MinAvailStartBytes - 1; return true; };
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(!result.Ran && result.SkipKey == "log.cachewarm.3", "low memory must skip the match");
        }

        private static void CacheWarmSkipsOnSeekPenaltyOrUnknownDisk()
        {
            WarmDefaults();
            CacheWarm.SeekPenaltyForTest = delegate { return (bool?)true; };
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(!result.Ran && result.SkipKey == "log.cachewarm.2", "seek penalty must skip the match");
            // 判断不了盘的类型必须按机械盘处理 宁可不做
            CacheWarm.SeekPenaltyForTest = delegate { return (bool?)null; };
            result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(!result.Ran && result.SkipKey == "log.cachewarm.2", "unknown disk must skip the match");
        }

        private static void CacheWarmForcedPathSkipsNvme()
        {
            WarmDefaults();
            CacheWarm.NvmeForTest = delegate { return (bool?)true; };
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null, true);
            WarmCheck(!result.Ran && result.SkipKey == "log.cachewarm.10", "the forced path must skip NVMe");
            CacheWarm.NvmeForTest = delegate { return (bool?)null; };
            result = CacheWarm.Run("C:\\Games\\Demo", null, true);
            WarmCheck(!result.Ran && result.SkipKey == "log.cachewarm.10", "an unknown bus is skipped on the forced path");
        }

        private static void CacheWarmSkipsWhenNoFiles()
        {
            WarmDefaults();
            CacheWarm.EnumerateForTest = delegate { return new List<CacheWarm.WarmCandidate>(); };
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(!result.Ran && result.SkipKey == "log.cachewarm.8", "no files must skip the match");
        }

        private static void CacheWarmWarmsPicksAndReports()
        {
            WarmDefaults();
            CacheWarm.EnumerateForTest = delegate
            {
                return new List<CacheWarm.WarmCandidate>
                {
                    WarmFile("C:\\Games\\Demo\\a.pak", 512 * WarmMiB, 900),
                    WarmFile("C:\\Games\\Demo\\b.pak", 256 * WarmMiB, 800)
                };
            };
            var warmed = new List<string>();
            CacheWarm.WarmFileForTest = delegate(string path, long take, out bool stop)
            { stop = false; warmed.Add(path); return take; };
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(result.Ran && !result.Aborted && result.SkipKey == null, "a healthy run must complete");
            WarmCheck(result.WarmedFiles == 2 && warmed.Count == 2, "both files must be warmed");
            WarmCheck(result.WarmedBytes == 768 * WarmMiB, "reported bytes must match the takes");
            WarmCheck(warmed[0].EndsWith("a.pak"), "hotter file must be warmed first");
        }

        private static void CacheWarmPriorityFailureSkipsWithoutReads()
        {
            WarmDefaults();
            CacheWarm.PoliteReadForTest = delegate { return false; };
            CacheWarm.EnumerateForTest = delegate
            {
                return new List<CacheWarm.WarmCandidate>
                { WarmFile("C:\\Games\\Demo\\a.pak", 512 * WarmMiB, 900) };
            };
            int reads = 0;
            CacheWarm.WarmFileForTest = delegate(string path, long take, out bool stop)
            { stop = false; reads++; return take; };
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(!result.Ran && result.SkipKey == "log.cachewarm.7",
                "failing the priority combo must skip the match");
            WarmCheck(reads == 0, "no file may be read without the low-priority combo");
        }

        private static void CacheWarmAbortStopsBetweenFiles()
        {
            WarmDefaults();
            CacheWarm.EnumerateForTest = delegate
            {
                return new List<CacheWarm.WarmCandidate>
                {
                    WarmFile("C:\\Games\\Demo\\a.pak", 512 * WarmMiB, 900),
                    WarmFile("C:\\Games\\Demo\\b.pak", 512 * WarmMiB, 800)
                };
            };
            int calls = 0;
            CacheWarm.WarmFileForTest = delegate(string path, long take, out bool stop)
            { calls++; stop = true; return 64 * WarmMiB; };
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(result.Ran && result.Aborted, "a mid-warm stop must be reported as aborted");
            WarmCheck(calls == 1 && result.WarmedFiles == 1, "no further file may start after a stop");
            WarmCheck(result.WarmedBytes == 64 * WarmMiB, "partial bytes must still be counted");
        }

        private static void CacheWarmFileFailureSkipsFileAndContinues()
        {
            WarmDefaults();
            CacheWarm.EnumerateForTest = delegate
            {
                return new List<CacheWarm.WarmCandidate>
                {
                    WarmFile("C:\\Games\\Demo\\locked.pak", 512 * WarmMiB, 900),
                    WarmFile("C:\\Games\\Demo\\b.pak", 256 * WarmMiB, 800)
                };
            };
            CacheWarm.WarmFileForTest = delegate(string path, long take, out bool stop)
            { stop = false; return path.EndsWith("locked.pak") ? 0 : take; };
            CacheWarm.WarmResult result = CacheWarm.Run("C:\\Games\\Demo", null);
            WarmCheck(result.Ran && !result.Aborted, "one unreadable file must not abort the run");
            WarmCheck(result.WarmedFiles == 1 && result.WarmedBytes == 256 * WarmMiB,
                "only the readable file may be counted");
        }

        private static void CacheWarmChunkLoopHonorsAbort()
        {
            WarmDefaults();
            var buffer = new byte[64 * 1024];
            int chunks = 0;
            bool stop;
            long done = CacheWarm.WarmChunkLoop(buffer, 10L * buffer.Length,
                delegate(byte[] buf, int count) { chunks++; return count; },
                delegate { return chunks >= 2; }, out stop);
            WarmCheck(stop, "abort must be reported as a stop");
            WarmCheck(done == 2L * buffer.Length, "no chunk may be read after the abort");
        }

        private static void CacheWarmChunkLoopStopsWhenMemoryFloorBreached()
        {
            WarmDefaults();
            var buffer = new byte[64 * 1024];
            int chunks = 0;
            CacheWarm.MemoryStatusForTest = delegate(out ulong total, out ulong avail)
            {
                total = 16UL * WarmGiB;
                avail = chunks >= 3 ? CacheWarm.MinAvailFloorBytes - 1 : 8UL * WarmGiB;
                return true;
            };
            bool stop;
            long done = CacheWarm.WarmChunkLoop(buffer, 10L * buffer.Length,
                delegate(byte[] buf, int count) { chunks++; return count; },
                delegate { return false; }, out stop);
            WarmCheck(stop, "breaching the memory floor must stop the warm");
            WarmCheck(done == 3L * buffer.Length, "reads must stop at the breach");
        }

        private static void CacheWarmChunkLoopStopsWhenPowerUnplugged()
        {
            WarmDefaults();
            var buffer = new byte[64 * 1024];
            int chunks = 0;
            // 交流供电是纪律条件 对局中拔电源预热必须当场收手
            CacheWarm.PowerForTest = delegate { return chunks < 2; };
            bool stop;
            long done = CacheWarm.WarmChunkLoop(buffer, 10L * buffer.Length,
                delegate(byte[] buf, int count) { chunks++; return count; },
                delegate { return false; }, out stop);
            WarmCheck(stop, "unplugging AC power must stop the warm");
            WarmCheck(done == 2L * buffer.Length, "reads must stop at the unplug");
        }

        private static void CacheWarmChunkLoopToleratesReadFailure()
        {
            WarmDefaults();
            var buffer = new byte[64 * 1024];
            int chunks = 0;
            bool stop;
            long done = CacheWarm.WarmChunkLoop(buffer, 10L * buffer.Length,
                delegate(byte[] buf, int count)
                {
                    if (++chunks > 2) throw new InvalidOperationException("read failure");
                    return count;
                },
                delegate { return false; }, out stop);
            WarmCheck(!stop, "a read failure ends the file but is not a run-level stop");
            WarmCheck(done == 2L * buffer.Length, "bytes before the failure must be kept");
        }
    }
}
#endif
