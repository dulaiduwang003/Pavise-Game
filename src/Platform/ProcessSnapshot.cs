// @author bdth 2074055628@qq.com
// 文件用途 一次系统调用取全部进程身份并缓存进程生命期内不变的镜像路径

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

        internal ProcessSnapshot(ProcEntry[] entries)
        {
            Entries = entries ?? new ProcEntry[0];
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
        private const int InitialBufferBytes = 1024 * 1024;
        private const int BufferHeadroomBytes = 256 * 1024;
        private const int MaxBufferBytes = 64 * 1024 * 1024;

        private static readonly object bufferSync = new object();
        private static IntPtr sharedBuffer;
        private static int sharedBufferBytes;

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
            ProcEntry[] entries = Enumerate();
            if (entries == null) return null;
            if (pathSession >= 0) ResolvePaths(entries, pathSession);
            else PruneCache(entries);
            return new ProcessSnapshot(entries);
        }

        private static ProcEntry[] Enumerate()
        {
            lock (bufferSync)
            {
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
                        if (status == 0) return Parse(sharedBuffer);
                        if (status != StatusInfoLengthMismatch) return null;
                        int next = need + BufferHeadroomBytes;
                        if (next <= sharedBufferBytes) next = sharedBufferBytes * 2;
                        if (next > MaxBufferBytes) return null;
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
                    return null;
                }
            }
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
                    if (entry.Pid <= 4 || entry.Session != pathSession) continue;
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
