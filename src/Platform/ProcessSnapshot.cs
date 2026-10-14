// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class ProcEntry
    {
        public int Pid;
        public int ParentPid;
        public int Session = -1;
        public int Threads;
        public long Creation;
        public long Cpu;
        public ulong Io;
        public string Name;
        public string Path;
    }

    internal sealed class ProcessSnapshot
    {
        public readonly ProcEntry[] Entries;
        public readonly Dictionary<int, ProcEntry> ByPid;
        public readonly long TakenTicks;
        public readonly long MonotonicTicks;

        internal ProcessSnapshot(ProcEntry[] entries)
        {
            Entries = entries ?? new ProcEntry[0];
            TakenTicks = DateTime.UtcNow.Ticks;
            MonotonicTicks = AutoCores.NowTicks;
            ByPid = new Dictionary<int, ProcEntry>(Entries.Length);
            foreach (ProcEntry entry in Entries) ByPid[entry.Pid] = entry;
        }

        public int Count { get { return Entries.Length; } }

        public ProcEntry Find(int pid)
        {
            ProcEntry entry;
            return ByPid.TryGetValue(pid, out entry) ? entry : null;
        }
    }

    internal static class ProcessSnapshotSource
    {
        private const int SystemProcessInformation = 5;
        private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
        private const int OffsetNextEntry = 0x00;
        private const int OffsetThreadCount = 0x04;
        private const int OffsetCreateTime = 0x20;
        private const int OffsetUserTime = 0x28;
        private const int OffsetKernelTime = 0x30;
        private const int OffsetImageNameLength = 0x38;
        private const int OffsetImageNameBuffer = 0x40;
        private const int OffsetUniqueProcessId = 0x50;
        private const int OffsetParentProcessId = 0x58;
        private const int OffsetSessionId = 0x64;
        private const int OffsetReadTransferCount = 0xE8;
        private const int OffsetWriteTransferCount = 0xF0;
        private const int ProcessRecordBytes = 0x100;
        private const int ThreadRecordBytes = 0x50;
        private const int OffsetThreadKernelTime = 0x00;
        private const int OffsetThreadUserTime = 0x08;
        private const int OffsetThreadCreateTime = 0x10;
        private const int OffsetThreadProcessId = 0x28;
        private const int OffsetThreadId = 0x30;
        private const int MaxSampledThreads = 16384;
        private const int InitialBufferBytes = 1024 * 1024;
        private const int BufferHeadroomBytes = 256 * 1024;
        private const int MaxBufferBytes = 64 * 1024 * 1024;

        private static readonly object bufferSync = new object();
        private static IntPtr sharedBuffer;
        private static int sharedBufferBytes;
        private static int sharedBufferUsed;
        private static long sharedBufferTakenTicks;
        private static long threadSampleReuses;
        internal static long ThreadSampleReuses { get { return System.Threading.Interlocked.Read(ref threadSampleReuses); } }

        private sealed class PathCacheEntry
        {
            public long Creation;
            public string Path;
        }

        private static readonly object cacheSync = new object();
        private static readonly Dictionary<int, PathCacheEntry> pathCache =
            new Dictionary<int, PathCacheEntry>();
        private static long pathQueryCount;

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int cls, IntPtr buffer, int len, out int ret);

        internal static long PathQueryCount { get { return System.Threading.Interlocked.Read(ref pathQueryCount); } }

        internal static int PathCacheSize
        {
            get { lock (cacheSync) return pathCache.Count; }
        }

        internal static void ResetPathCache()
        {
            lock (cacheSync) pathCache.Clear();
        }

        public static ProcessSnapshot Capture()
        {
            return Capture(-1);
        }

        public static ProcessSnapshot Capture(int pathSession)
        {
            return Capture(pathSession, 0);
        }

        // 进程集未变化时最多复用快照五秒。
        internal const int ReuseMaxAgeMs = 5000;

        private static ProcessSnapshot cachedSnapshot;
        private static long cachedSnapshotTicks;
        private static int cachedSnapshotSession = -1;
        private static long reuseCount;

        internal static long ReuseCount { get { return System.Threading.Interlocked.Read(ref reuseCount); } }

        public static ProcessSnapshot Capture(int pathSession, int maxAgeMs)
        {
            if (maxAgeMs > 0 && pathSession >= 0)
                lock (cacheSync)
                {
                    long age = (DateTime.UtcNow.Ticks - cachedSnapshotTicks)
                        / TimeSpan.TicksPerMillisecond;
                    if (cachedSnapshot != null && cachedSnapshotSession == pathSession
                        && age >= 0 && age <= maxAgeMs)
                    {
                        System.Threading.Interlocked.Increment(ref reuseCount);
                        return cachedSnapshot;
                    }
                }
            ProcEntry[] entries = Enumerate();
            if (entries == null) return null;
            if (pathSession >= 0) ResolvePaths(entries, pathSession);
            else PruneCache(entries);
            var snapshot = new ProcessSnapshot(entries);
            if (pathSession >= 0)
                lock (cacheSync)
                {
                    cachedSnapshot = snapshot;
                    cachedSnapshotTicks = DateTime.UtcNow.Ticks;
                    cachedSnapshotSession = pathSession;
                }
            return snapshot;
        }

        internal static void ResetSnapshotCacheForTest()
        {
            lock (cacheSync)
            {
                cachedSnapshot = null;
                cachedSnapshotTicks = 0;
                cachedSnapshotSession = -1;
                System.Threading.Interlocked.Exchange(ref reuseCount, 0);
            }
        }

        private static ProcEntry[] Enumerate()
        {
            lock (bufferSync)
            {
                try { return QuerySharedBufferLocked() ? Parse(sharedBuffer) : null; }
                catch { return null; }
            }
        }

        private static bool QuerySharedBufferLocked()
        {
            // 原生查询失败可能污染缓冲区，不能复用旧快照。
            sharedBufferTakenTicks = 0;
            sharedBufferUsed = 0;
            try
            {
                if (sharedBuffer == IntPtr.Zero)
                {
                    sharedBufferBytes = InitialBufferBytes;
                    sharedBuffer = Marshal.AllocHGlobal(sharedBufferBytes);
                }
                while (true)
                {
                    int need;
                    int status = NtQuerySystemInformation(
                        SystemProcessInformation, sharedBuffer, sharedBufferBytes, out need);
                    if (status == 0)
                    {
                        sharedBufferUsed = need > 0 && need <= sharedBufferBytes ? need : sharedBufferBytes;
                        sharedBufferTakenTicks = AutoCores.NowTicks;
                        return true;
                    }
                    if (status != StatusInfoLengthMismatch) return false;
                    int next = need + BufferHeadroomBytes;
                    if (next <= sharedBufferBytes) next = sharedBufferBytes * 2;
                    if (next > MaxBufferBytes) return false;
                    Marshal.FreeHGlobal(sharedBuffer);
                    sharedBuffer = IntPtr.Zero;
                    sharedBuffer = Marshal.AllocHGlobal(next);
                    sharedBufferBytes = next;
                }
            }
            catch
            {
                if (sharedBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(sharedBuffer);
                    sharedBuffer = IntPtr.Zero;
                    sharedBufferBytes = 0;
                }
                return false;
            }
        }

        internal static ThreadTimes SampleThreads(int pid, long creation)
        { return SampleThreads(pid, creation, 1500); }

        internal static ThreadTimes SampleThreads(int pid, long creation, int maxAgeMs)
        {
            if (IntPtr.Size != 8 || pid <= 0 || creation <= 0) return null;
            lock (bufferSync)
            {
                try
                {
                    long age = AutoCores.NowTicks - sharedBufferTakenTicks;
                    bool reuse = maxAgeMs > 0 && sharedBuffer != IntPtr.Zero && sharedBufferTakenTicks > 0
                        && age >= 0 && age <= (long)Math.Min(maxAgeMs, 1500) * TimeSpan.TicksPerMillisecond;
                    if (!reuse && !QuerySharedBufferLocked()) return null;
                    if (reuse) System.Threading.Interlocked.Increment(ref threadSampleReuses);
                    return ParseThreads(sharedBuffer, sharedBufferUsed, pid, creation, sharedBufferTakenTicks);
                }
                catch { return null; }
            }
        }

        internal static ThreadTimes ParseThreads(IntPtr buffer, int used, int pid, long creation, long taken)
        {
            long offset = 0;
            while (offset >= 0 && offset + ProcessRecordBytes <= used)
            {
                IntPtr cursor = new IntPtr(buffer.ToInt64() + offset);
                int nextOffset = Marshal.ReadInt32(cursor, OffsetNextEntry);
                if (Marshal.ReadIntPtr(cursor, OffsetUniqueProcessId).ToInt64() == pid)
                {
                    if (Marshal.ReadInt64(cursor, OffsetCreateTime) != creation) return null;
                    int count = Marshal.ReadInt32(cursor, OffsetThreadCount);
                    long end = offset + ProcessRecordBytes + (long)count * ThreadRecordBytes;
                    if (count < 0 || count > MaxSampledThreads || end > used
                        || nextOffset > 0 && end > offset + nextOffset) return null;
                    var times = new ThreadTimes { Ticks = taken, Creation = creation,
                        ProcessCpu = Marshal.ReadInt64(cursor, OffsetUserTime) + Marshal.ReadInt64(cursor, OffsetKernelTime),
                        Ids = new long[count], Created = new long[count], Cpu = new long[count] };
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr thread = new IntPtr(cursor.ToInt64() + ProcessRecordBytes + (long)i * ThreadRecordBytes);
                        if (Marshal.ReadIntPtr(thread, OffsetThreadProcessId).ToInt64() != pid) return null;
                        times.Ids[i] = Marshal.ReadIntPtr(thread, OffsetThreadId).ToInt64();
                        times.Created[i] = Marshal.ReadInt64(thread, OffsetThreadCreateTime);
                        times.Cpu[i] = Marshal.ReadInt64(thread, OffsetThreadKernelTime)
                            + Marshal.ReadInt64(thread, OffsetThreadUserTime);
                        if (times.Cpu[i] < 0 || times.Created[i] < 0) return null;
                    }
                    return times;
                }
                if (nextOffset <= 0) return null;
                offset += nextOffset;
            }
            return null;
        }

        private static ProcEntry[] Parse(IntPtr buffer)
        {
            var list = new List<ProcEntry>(320);
            IntPtr cursor = buffer;
            while (true)
            {
                int nextOffset = Marshal.ReadInt32(cursor, OffsetNextEntry);
                int pid = Marshal.ReadIntPtr(cursor, OffsetUniqueProcessId).ToInt32();
                if (pid > 0)
                {
                    ushort nameLength = (ushort)Marshal.ReadInt16(
                        cursor, OffsetImageNameLength);
                    IntPtr namePtr = Marshal.ReadIntPtr(cursor, OffsetImageNameBuffer);
                    var entry = new ProcEntry
                    {
                        Pid = pid,
                        ParentPid = Marshal.ReadIntPtr(cursor, OffsetParentProcessId).ToInt32(),
                        Session = Marshal.ReadInt32(cursor, OffsetSessionId),
                        Threads = Marshal.ReadInt32(cursor, OffsetThreadCount),
                        Creation = Marshal.ReadInt64(cursor, OffsetCreateTime),
                        Cpu = Marshal.ReadInt64(cursor, OffsetUserTime)
                            + Marshal.ReadInt64(cursor, OffsetKernelTime),
                        Io = (ulong)Marshal.ReadInt64(cursor, OffsetReadTransferCount)
                            + (ulong)Marshal.ReadInt64(cursor, OffsetWriteTransferCount),
                        Name = ReadName(namePtr, nameLength),
                    };
                    list.Add(entry);
                }
                if (nextOffset <= 0) break;
                cursor = new IntPtr(cursor.ToInt64() + nextOffset);
            }
            return list.ToArray();
        }

        private static string ReadName(IntPtr namePtr, ushort byteLength)
        {
            if (namePtr == IntPtr.Zero || byteLength == 0) return "";
            string raw;
            try { raw = Marshal.PtrToStringUni(namePtr, byteLength / 2); }
            catch { return ""; }
            if (string.IsNullOrEmpty(raw)) return "";
            return raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? raw.Substring(0, raw.Length - 4) : raw;
        }

        private static void ResolvePaths(ProcEntry[] entries, int pathSession)
        {
            var live = new HashSet<int>();
            lock (cacheSync)
            {
                foreach (ProcEntry entry in entries)
                {
                    live.Add(entry.Pid);
                    if (entry.Pid <= 4 || (entry.Session != pathSession
                        && !(entry.Session == 0 && BackgroundServicePolicy.CandidateName(entry.Name)))) continue;
                    PathCacheEntry cached;
                    if (pathCache.TryGetValue(entry.Pid, out cached)
                        && cached.Creation == entry.Creation)
                    {
                        entry.Path = cached.Path;
                        continue;
                    }
                    string path = QueryPath(entry.Pid);
                    entry.Path = path;
                    if (path == null) { pathCache.Remove(entry.Pid); continue; }
                    pathCache[entry.Pid] = new PathCacheEntry
                    {
                        Creation = entry.Creation,
                        Path = path,
                    };
                }
                DropDeadLocked(live);
            }
        }

        private static void PruneCache(ProcEntry[] entries)
        {
            var live = new HashSet<int>();
            foreach (ProcEntry entry in entries) live.Add(entry.Pid);
            lock (cacheSync) DropDeadLocked(live);
        }

        private static void DropDeadLocked(HashSet<int> live)
        {
            if (pathCache.Count == 0) return;
            List<int> dead = null;
            foreach (int pid in pathCache.Keys)
            {
                if (live.Contains(pid)) continue;
                if (dead == null) dead = new List<int>();
                dead.Add(pid);
            }
            if (dead == null) return;
            foreach (int pid in dead) pathCache.Remove(pid);
        }

        private static string QueryPath(int pid)
        {
            IntPtr handle = Native.OpenProcess(
                Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            System.Threading.Interlocked.Increment(ref pathQueryCount);
            try { return Native.ImagePath(handle); }
            finally { Native.CloseHandle(handle); }
        }
    }
}
