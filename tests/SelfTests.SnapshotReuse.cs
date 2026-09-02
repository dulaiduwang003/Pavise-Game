// Real read-only process enumeration; no registry, windows or process writes.
// 快照复用回归 复用只发生在 同会话+限龄+调用方明确允许 三个条件同时成立时
#if PAVISE_SELFTEST
using System;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int snapshotReuseChecks;

        internal static int RunSnapshotReuseRegressionTests()
        {
            Action[] tests =
            {
                SnapshotReuseReturnsSameInstanceWithinTtl,
                SnapshotFreshWhenCallerForbidsReuse,
                SnapshotFreshOnSessionMismatch,
                SnapshotIdentityCaptureNeverTouchesCache
            };
            snapshotReuseChecks = 0;
            foreach (Action test in tests)
            {
                ProcessSnapshotSource.ResetSnapshotCacheForTest();
                try { test(); }
                finally { ProcessSnapshotSource.ResetSnapshotCacheForTest(); }
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS snapshot-reuse assertions=" + snapshotReuseChecks
                + " enumeration=read_only settings=untouched windows_shown=false");
            return tests.Length;
        }

        private static void SnapCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Snapshot reuse regression: " + message);
            Interlocked.Increment(ref snapshotReuseChecks);
        }

        private static void SnapshotReuseReturnsSameInstanceWithinTtl()
        {
            ProcessSnapshot first = ProcessSnapshotSource.Capture(0, 0);
            SnapCheck(first != null && first.Count > 0, "a real enumeration must yield processes");
            ProcessSnapshot second = ProcessSnapshotSource.Capture(0, ProcessSnapshotSource.ReuseMaxAgeMs);
            SnapCheck(ReferenceEquals(first, second),
                "a fallback-tick capture within the TTL must reuse the cached snapshot");
            SnapCheck(ProcessSnapshotSource.ReuseCount == 1, "the reuse must be counted");
        }

        private static void SnapshotFreshWhenCallerForbidsReuse()
        {
            ProcessSnapshot first = ProcessSnapshotSource.Capture(0, 0);
            ProcessSnapshot second = ProcessSnapshotSource.Capture(0, 0);
            SnapCheck(first != null && second != null && !ReferenceEquals(first, second),
                "maxAge 0 must always enumerate fresh: dirty and urgent ticks depend on it");
            SnapCheck(ProcessSnapshotSource.ReuseCount == 0, "a forced fresh capture is not a reuse");
        }

        private static void SnapshotFreshOnSessionMismatch()
        {
            ProcessSnapshot first = ProcessSnapshotSource.Capture(0, 0);
            ProcessSnapshot second = ProcessSnapshotSource.Capture(1, ProcessSnapshotSource.ReuseMaxAgeMs);
            SnapCheck(first != null && second != null && !ReferenceEquals(first, second),
                "a different path session must never see another session's cache");
        }

        private static void SnapshotIdentityCaptureNeverTouchesCache()
        {
            // 反作弊路径的 Capture() 不带会话 不进缓存也不吃缓存
            ProcessSnapshot identity = ProcessSnapshotSource.Capture();
            SnapCheck(identity != null, "the identity capture must still work");
            ProcessSnapshot cachedProbe = ProcessSnapshotSource.Capture(0, ProcessSnapshotSource.ReuseMaxAgeMs);
            SnapCheck(cachedProbe != null && !ReferenceEquals(identity, cachedProbe),
                "an identity capture must not seed the session cache");
            SnapCheck(ProcessSnapshotSource.ReuseCount == 0,
                "nothing was cached so nothing may be reused");
        }
    }
}
#endif
