// @author bdth 2074055628@qq.com
// 文件用途 查询并清理系统待机内存列表

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AegisApp
{
    // 待机列表是 Windows 拿空闲物理内存做的文件缓存。它涨得过大时，游戏中途
    // 系统可能突然回收一大块，表现为帧时间尖刺。可以主动清理，但要非常小心：
    // 待机列表本身就是缓存，在游戏跑起来之后清掉，等于把游戏刚缓存的资源扔了，
    // 接下来的流式加载要回硬盘重读——本来想消卡顿，结果制造卡顿。
    //
    // 因此这里的定位是：
    //   - 主用法是会话开始那一次全量清理（此时游戏自己的缓存还没建立，清掉的是旧数据）
    //   - 游戏中途只提供最保守的低优先级清理，且默认不启用
    //   - 全程不改任何系统设置，只是让内核回收缓存，所以没有"还原"这一说
    internal enum StandbyAction { None, PurgeFull, PurgeLowPriority }

    internal sealed class StandbyStats
    {
        public long PageSize;
        public long[] StandbyPagesByPriority = new long[8];
        public long FreePages;
        public long ZeroPages;
        public long ModifiedPages;

        public long StandbyPages
        {
            get
            {
                long n = 0;
                foreach (long v in StandbyPagesByPriority) n += v;
                return n;
            }
        }

        public long StandbyBytes { get { return StandbyPages * PageSize; } }

        // 优先级 0/1 是最低档，也就是 MemoryPurgeLowPriorityStandbyList 主要动的部分
        public long LowPriorityBytes
        {
            get { return (StandbyPagesByPriority[0] + StandbyPagesByPriority[1]) * PageSize; }
        }

        public long TrulyFreeBytes { get { return (FreePages + ZeroPages) * PageSize; } }

        // 系统整体内存压力百分比（0~100），由 GlobalMemoryStatusEx 提供
        public int MemoryLoadPercent;
    }

    internal static class StandbyMemory
    {
        private const int SystemMemoryListInformation = 80;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        // SYSTEM_MEMORY_LIST_INFORMATION：5 个计数 + 8 个按优先级页数
        // + 8 个 repurposed + 1 个 pagefile，全部是 ULONG_PTR
        private const int FieldCount = 22;
        private const int IdxZero = 0, IdxFree = 1, IdxModified = 2, IdxPriorityBase = 5;

        private static long ReadField(byte[] buf, int index)
        {
            int size = IntPtr.Size;
            int off = index * size;
            if (off + size > buf.Length) return 0;
            return size == 8 ? unchecked((long)BitConverter.ToUInt64(buf, off)) : BitConverter.ToUInt32(buf, off);
        }

        public static bool TryQuery(out StandbyStats stats)
        {
            stats = null;
            int bytes = FieldCount * IntPtr.Size;
            IntPtr buf = IntPtr.Zero;
            try
            {
                buf = Marshal.AllocHGlobal(bytes);
                int returned;
                int status = Native.NtQuerySystemInformation(SystemMemoryListInformation, buf, bytes, out returned);
                if (status != 0) return false;
                var raw = new byte[bytes];
                Marshal.Copy(buf, raw, 0, bytes);

                var s = new StandbyStats();
                s.PageSize = Native.MemoryPageSize();
                s.MemoryLoadPercent = Native.MemoryLoadPercent();
                s.ZeroPages = ReadField(raw, IdxZero);
                s.FreePages = ReadField(raw, IdxFree);
                s.ModifiedPages = ReadField(raw, IdxModified);
                for (int i = 0; i < 8; i++) s.StandbyPagesByPriority[i] = ReadField(raw, IdxPriorityBase + i);
                stats = s;
                return true;
            }
            catch { return false; }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        // 返回实际回收的字节数（清理前后各查一次算差值，不听内核汇报），失败返回 -1
        private static long Purge(int command, string label)
        {
            if (!Native.EnsureProfilePrivilege())
            {
                Logger.Log("待机内存清理：无法启用 SeProfileSingleProcessPrivilege，已跳过");
                return -1;
            }
            StandbyStats before;
            if (!TryQuery(out before))
            {
                Logger.Log("待机内存清理：无法读取待机列表状态，已跳过");
                return -1;
            }

            IntPtr buf = IntPtr.Zero;
            var sw = Stopwatch.StartNew();
            try
            {
                buf = Marshal.AllocHGlobal(sizeof(int));
                Marshal.WriteInt32(buf, command);
                int status = Native.NtSetSystemInformation(SystemMemoryListInformation, buf, sizeof(int));
                sw.Stop();
                if (status != 0)
                {
                    Logger.Log("待机内存清理（" + label + "）失败：NTSTATUS 0x" + status.ToString("X8"));
                    return -1;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("待机内存清理（" + label + "）异常：" + ex.Message);
                return -1;
            }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }

            StandbyStats after;
            if (!TryQuery(out after)) return 0;
            long freed = before.StandbyBytes - after.StandbyBytes;
            if (freed < 0) freed = 0;
            Logger.Log("待机内存清理（" + label + "）：回收 " + Mb(freed)
                + "，待机列表 " + Mb(before.StandbyBytes) + " → " + Mb(after.StandbyBytes)
                + "，耗时 " + sw.ElapsedMilliseconds + "ms");
            return freed;
        }

        private static string Mb(long bytes) { return (bytes / 1024 / 1024) + " MB"; }

        // 会话开始时用：此刻游戏自己的缓存还没建立，全量清理是安全的
        public static long PurgeAtSessionStart() { return Purge(MemoryPurgeStandbyList, "会话开始 · 全量"); }

        // 游戏中途用：只动最低优先级那部分，官方描述为"最不容易引发后续性能问题"，
        // 代价是通常回收不了多少。绝不在中途做全量清理。
        public static long PurgeLowPriority() { return Purge(MemoryPurgeLowPriorityStandbyList, "会话中 · 低优先级"); }

        // 中途清理的准入判断。
        // 不能用 free+zero 链表判断"内存见底"：Windows 的设计就是把空闲物理内存尽量塞进
        // 待机列表，健康系统上这两个链表长期只有几百 MB，拿它跟 1GB 比等于条件恒为真，
        // "只有真见底才动手"的克制会退化成每 60 秒清一次。
        // 系统整体内存压力（GlobalMemoryStatusEx 的 dwMemoryLoad）才是想表达的语义。
        internal static bool ShouldPurgeMidSession(StandbyStats s, int minMemoryLoadPercent, long minLowPriorityBytes)
        {
            if (s == null) return false;
            if (s.MemoryLoadPercent < minMemoryLoadPercent) return false;
            return s.LowPriorityBytes >= minLowPriorityBytes;
        }
    }
}
