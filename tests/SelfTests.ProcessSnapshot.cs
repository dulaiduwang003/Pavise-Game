// @author bdth 2074055628@qq.com
// 文件用途 校验统一进程快照与逐句柄查询结果一致 并确认路径缓存命中不膨胀

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestSnapshotFieldsMatchHandleQuery()
        {
            ProcessSnapshot snap = ProcessSnapshotSource.Capture();
            if (snap == null) throw new Exception("snapshot capture returned null");
            Eq(true, snap.Count > 10);

            int selfPid;
            int selfSession;
            using (Process self = Process.GetCurrentProcess())
            {
                selfPid = self.Id;
                selfSession = self.SessionId;
            }

            ProcEntry mine = snap.Find(selfPid);
            if (mine == null) throw new Exception("snapshot is missing the current process");
            Eq(selfSession, mine.Session);
            Eq(true, mine.Creation > 0);
            Eq(true, mine.Threads > 0);

            Process[] live = Process.GetProcesses();
            try
            {
                int compared = 0, nameMatch = 0, sessionMatch = 0;
                foreach (Process p in live)
                {
                    int pid;
                    try { pid = p.Id; } catch { continue; }
                    ProcEntry entry;
                    if (!snap.ByPid.TryGetValue(pid, out entry)) continue;
                    string name;
                    int session;
                    try { name = p.ProcessName; session = p.SessionId; }
                    catch { continue; }
                    compared++;
                    if (string.Equals(name, entry.Name, StringComparison.OrdinalIgnoreCase)) nameMatch++;
                    if (session == entry.Session) sessionMatch++;
                }
                Eq(true, compared > 10);
                Eq(compared, nameMatch);
                Eq(compared, sessionMatch);
            }
            finally { foreach (Process p in live) p.Dispose(); }

            ProcessSnapshot withPaths = ProcessSnapshotSource.Capture(selfSession);
            if (withPaths == null) throw new Exception("path-resolving capture returned null");
            ProcEntry mineWithPath = withPaths.Find(selfPid);
            if (mineWithPath == null) throw new Exception("path snapshot is missing the current process");
            Eq(false, string.IsNullOrEmpty(mineWithPath.Path));

            IntPtr handle = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION, false, selfPid);
            Eq(true, handle != IntPtr.Zero);
            try
            {
                long creation, cpu;
                ulong io;
                Eq(true, Native.QueryProcessSample(handle, out creation, out cpu, out io));
                Eq(creation, mineWithPath.Creation);
                Eq(Native.ImagePath(handle), mineWithPath.Path);
                Eq(Native.ParentProcessId(handle), mineWithPath.ParentPid);
            }
            finally { Native.CloseHandle(handle); }
        }

        private static void TestSnapshotPathCacheStaysWarm()
        {
            int selfSession;
            using (Process self = Process.GetCurrentProcess()) selfSession = self.SessionId;

            ProcessSnapshotSource.ResetPathCache();
            ProcessSnapshotSource.Capture(selfSession);
            long afterCold = ProcessSnapshotSource.PathQueryCount;
            int cacheAfterCold = ProcessSnapshotSource.PathCacheSize;
            Eq(true, cacheAfterCold > 0);

            ProcessSnapshotSource.Capture(selfSession);
            long afterWarm = ProcessSnapshotSource.PathQueryCount;
            int cacheAfterWarm = ProcessSnapshotSource.PathCacheSize;

            long newQueries = afterWarm - afterCold;
            int allowed = Math.Max(8, cacheAfterCold / 4);
            if (newQueries >= allowed)
                throw new Exception("warm capture re-queried " + newQueries
                    + " paths, expected fewer than " + allowed);
            Eq(true, cacheAfterWarm <= cacheAfterCold + 16);

            ProcessSnapshot noPaths = ProcessSnapshotSource.Capture();
            if (noPaths == null) throw new Exception("plain capture returned null");
            Eq(true, ProcessSnapshotSource.PathCacheSize <= noPaths.Count);
        }
    }
}
