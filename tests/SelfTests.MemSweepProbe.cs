// @author bdth 2074055628@qq.com
// 文件用途 逐项测量各类内存清理命令的耗时与实际释放量

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref int info, int length);

        [DllImport("ntdll.dll")]
        private static extern int NtSetSystemInformation(int infoClass, ref long info, int length);

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

        private const int SystemMemoryListInformation = 0x50;
        private const int SystemFileCacheInformation = 0x15;
        private const int SystemCombinePhysicalMemoryInformation = 0x82;

        private const int MemoryEmptyWorkingSets = 2;
        private const int MemoryFlushModifiedList = 3;
        private const int MemoryPurgeStandbyList = 4;
        private const int MemoryPurgeLowPriorityStandbyList = 5;

        private static double AvailableMb()
        {
            PerfInfo pi;
            if (!GetPerformanceInfo(out pi, Marshal.SizeOf(typeof(PerfInfo)))) return 0;
            return (double)pi.PhysicalAvailable.ToInt64() * pi.PageSize.ToInt64() / (1024.0 * 1024.0);
        }

        private static double CacheMb()
        {
            PerfInfo pi;
            if (!GetPerformanceInfo(out pi, Marshal.SizeOf(typeof(PerfInfo)))) return 0;
            return (double)pi.SystemCache.ToInt64() * pi.PageSize.ToInt64() / (1024.0 * 1024.0);
        }

        private static void RunMemSweepProbe(string output)
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== 内存清理命令逐项实测 ===");
            sb.AppendLine("逐项测量调用耗时、可用物理内存变化与系统缓存变化");
            sb.AppendLine("开局路径可接受的耗时上限为百毫秒级");
            sb.AppendLine();

            if (!Native.EnsureProfilePrivilege())
            {
                sb.AppendLine("失败：SeProfileSingleProcessPrivilege 不可用，需要管理员权限");
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Console.Write(sb.ToString());
                return;
            }

            PerfInfo baseline;
            GetPerformanceInfo(out baseline, Marshal.SizeOf(typeof(PerfInfo)));
            sb.AppendLine("物理内存总量 "
                + ((double)baseline.PhysicalTotal.ToInt64() * baseline.PageSize.ToInt64() / (1024.0 * 1024 * 1024)).ToString("F1")
                + " GB，当前可用 " + AvailableMb().ToString("F0") + " MB，系统缓存 "
                + CacheMb().ToString("F0") + " MB");
            sb.AppendLine();
            sb.AppendLine("命令                          耗时ms    可用内存变化MB   缓存变化MB   结果");

            MemCmd(sb, "清空所有进程工作集", MemoryEmptyWorkingSets);
            MemCmd(sb, "清待机列表(低优先级)", MemoryPurgeLowPriorityStandbyList);
            MemCmd(sb, "清待机列表(全部)", MemoryPurgeStandbyList);
            MemCmd(sb, "刷新修改页列表", MemoryFlushModifiedList);
            FileCacheCmd(sb);
            CombineCmd(sb);

            sb.AppendLine();
            sb.AppendLine("=== 备注 ===");
            sb.AppendLine("耗时高的项会拖慢游戏启动。");
            sb.AppendLine("清缓存类的项释放系统文件缓存，随后的游戏加载需重新读盘。");

            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            Console.Write(sb.ToString());
        }

        private static void MemCmd(StringBuilder sb, string name, int command)
        {
            double a0 = AvailableMb(), c0 = CacheMb();
            int cmd = command;
            var sw = Stopwatch.StartNew();
            int status = NtSetSystemInformation(SystemMemoryListInformation, ref cmd, sizeof(int));
            sw.Stop();
            Thread.Sleep(400);
            double a1 = AvailableMb(), c1 = CacheMb();
            sb.AppendLine(MemRow(name, sw.Elapsed.TotalMilliseconds, a1 - a0, c1 - c0,
                status == 0 ? "成功" : "NTSTATUS 0x" + status.ToString("X8")));
        }

        private static void FileCacheCmd(StringBuilder sb)
        {
            double a0 = AvailableMb(), c0 = CacheMb();
            long payload = -1;
            var sw = Stopwatch.StartNew();
            int status = NtSetSystemInformation(SystemFileCacheInformation, ref payload, sizeof(long));
            sw.Stop();
            Thread.Sleep(400);
            double a1 = AvailableMb(), c1 = CacheMb();
            sb.AppendLine(MemRow("清系统文件缓存", sw.Elapsed.TotalMilliseconds, a1 - a0, c1 - c0,
                status == 0 ? "成功" : "NTSTATUS 0x" + status.ToString("X8")));
        }

        private static void CombineCmd(StringBuilder sb)
        {
            double a0 = AvailableMb(), c0 = CacheMb();
            var payload = new long[4];
            IntPtr buf = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(long)) * 4);
            var sw = Stopwatch.StartNew();
            int status;
            try
            {
                Marshal.Copy(payload, 0, buf, 4);
                status = NtSetSystemInformationRaw(SystemCombinePhysicalMemoryInformation, buf,
                    Marshal.SizeOf(typeof(long)) * 4);
            }
            finally { Marshal.FreeHGlobal(buf); }
            sw.Stop();
            Thread.Sleep(400);
            double a1 = AvailableMb(), c1 = CacheMb();
            sb.AppendLine(MemRow("合并物理内存页", sw.Elapsed.TotalMilliseconds, a1 - a0, c1 - c0,
                status == 0 ? "成功" : "NTSTATUS 0x" + status.ToString("X8")));
        }

        [DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation")]
        private static extern int NtSetSystemInformationRaw(int infoClass, IntPtr info, int length);

        private static string MemRow(string name, double ms, double avail, double cache, string result)
        {
            return name.PadRight(26)
                + ms.ToString("F1").PadLeft(9)
                + (avail >= 0 ? "+" : "") + avail.ToString("F0").PadLeft(13)
                + (cache >= 0 ? "+" : "") + cache.ToString("F0").PadLeft(12)
                + "   " + result;
        }
    }
}
