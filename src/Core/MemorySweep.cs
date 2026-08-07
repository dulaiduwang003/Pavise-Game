// @author bdth 2074055628@qq.com
// 文件用途 手动触发的内存清理 逐项执行并如实回报可用内存与系统缓存两侧的变化
//
// 只在用户主动点击时执行 不接入对局流程 不定时
// 本机实测各命令的代价见 README 内存清理一节 清整个待机列表会丢弃十几 GB 缓存
// 因此界面必须同时显示缓存损失 让用户自己判断值不值 而不是只报释放了多少

using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal struct MemoryState
    {
        public double AvailableMb;
        public double CacheMb;
        public double TotalMb;
    }

    internal sealed class MemorySweepResult
    {
        public MemoryState Before;
        public MemoryState After;
        public double ElapsedMs;
        public int Applied;
        public int Failed;

        public double AvailableDeltaMb { get { return After.AvailableMb - Before.AvailableMb; } }
        public double CacheDeltaMb { get { return After.CacheMb - Before.CacheMb; } }
    }

    internal static class MemorySweep
    {
        private const int SystemMemoryListInformation = 0x50;
        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [StructLayout(LayoutKind.Sequential)]
        private struct PerfInfo
        {
            public int cb;
            public IntPtr CommitTotal, CommitLimit, CommitPeak;
            public IntPtr PhysicalTotal, PhysicalAvailable;
            public IntPtr SystemCache, KernelTotal, KernelPaged, KernelNonpaged;
            public IntPtr PageSize;
            public int HandleCount, ProcessCount, ThreadCount;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetPerformanceInfo(out PerfInfo info, int size);

        public static MemoryState Snapshot()
        {
            var s = new MemoryState();
            try
            {
                PerfInfo pi;
                if (!GetPerformanceInfo(out pi, Marshal.SizeOf(typeof(PerfInfo)))) return s;
                double page = pi.PageSize.ToInt64();
                s.AvailableMb = pi.PhysicalAvailable.ToInt64() * page / (1024.0 * 1024.0);
                s.CacheMb = pi.SystemCache.ToInt64() * page / (1024.0 * 1024.0);
                s.TotalMb = pi.PhysicalTotal.ToInt64() * page / (1024.0 * 1024.0);
            }
            catch { }
            return s;
        }

        public static MemorySweepResult Run(bool workingSets, bool lowPriorityStandby,
            bool allStandby, bool modifiedList)
        {
            var r = new MemorySweepResult();
            r.Before = Snapshot();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (!Native.EnsureProfilePrivilege())
            {
                sw.Stop();
                r.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                r.Failed = 1;
                r.After = r.Before;
                Logger.Log("内存清理：SeProfileSingleProcessPrivilege 不可用，未执行");
                return r;
            }
            if (workingSets) Apply(MemoryEmptyWorkingSets, "清空进程工作集", r);
            if (lowPriorityStandby) Apply(MemoryPurgeLowPriorityStandbyList, "清低优先级待机页", r);
            if (allStandby) Apply(MemoryPurgeStandbyList, "清整个待机列表", r);
            if (modifiedList) Apply(MemoryFlushModifiedList, "刷新修改页列表", r);
            sw.Stop();
            r.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            r.After = Snapshot();
            Logger.Log("内存清理完成：可用 " + Fmt(r.AvailableDeltaMb) + " MB，系统缓存 "
                + Fmt(r.CacheDeltaMb) + " MB，耗时 " + r.ElapsedMs.ToString("F0") + " ms，成功 "
                + r.Applied + " 项，失败 " + r.Failed + " 项");
            return r;
        }

        private static void Apply(int command, string name, MemorySweepResult r)
        {
            int cmd = command;
            int status;
            try { status = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int)); }
            catch (Exception ex) { Logger.Log("内存清理：" + name + " 异常 " + ex.Message); r.Failed++; return; }
            if (status == 0) r.Applied++;
            else { r.Failed++; Logger.Log("内存清理：" + name + " 失败 NTSTATUS 0x" + status.ToString("X8")); }
        }

        private static string Fmt(double v)
        {
            return (v >= 0 ? "+" : "") + v.ToString("F0");
        }
    }
}
