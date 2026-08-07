// @author bdth 2074055628@qq.com
// 文件用途 对比只枚举 PID 与全量进程快照的成本 量化高频轮询的可行区间

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
        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool EnumProcesses(uint[] buffer, uint size, out uint needed);

        private static void RunPidScanBench(string output, string secondsArg)
        {
            int seconds;
            if (!int.TryParse(secondsArg ?? "", out seconds) || seconds < 1) seconds = 3;

            var sb = new StringBuilder();
            sb.AppendLine("=== 新进程检测的两种成本 ===");
            sb.AppendLine("目标: 高频轮询要付多少 CPU，取决于每次轮询做什么");
            sb.AppendLine();

            var pids = new uint[4096];
            uint needed;
            EnumProcesses(pids, (uint)(pids.Length * 4), out needed);
            int pidCount = (int)(needed / 4);
            ProcessSnapshot warm = ProcessSnapshotSource.Capture();
            sb.AppendLine("本机进程数: " + pidCount + "（快照口径 " + warm.Count + "）");
            sb.AppendLine();

            double enumMs = BenchLoop(seconds, delegate
            {
                uint n;
                EnumProcesses(pids, (uint)(pids.Length * 4), out n);
            });

            double snapMs = BenchLoop(seconds, delegate
            {
                ProcessSnapshot s = ProcessSnapshotSource.Capture();
                if (s.Count < 0) Console.Write("");
            });

            sb.AppendLine("单次耗时（" + seconds + " 秒内反复调用取平均）:");
            sb.AppendLine("  EnumProcesses 仅 PID 列表 : " + enumMs.ToString("F3") + " ms");
            sb.AppendLine("  全量快照 NtQSI            : " + snapMs.ToString("F3") + " ms");
            if (enumMs > 0)
                sb.AppendLine("  倍差                      : " + (snapMs / enumMs).ToString("F1") + "x");
            sb.AppendLine();

            sb.AppendLine("轮询开销对照（单核占比 = 单次耗时 / 间隔）:");
            sb.AppendLine("  间隔     全量快照      仅枚举 PID");
            int[] intervals = { 100, 200, 300, 500, 1000 };
            foreach (int iv in intervals)
                sb.AppendLine("  " + (iv + "ms").PadRight(9)
                    + (snapMs / iv * 100).ToString("F2").PadLeft(8) + "%"
                    + (enumMs / iv * 100).ToString("F2").PadLeft(14) + "%");
            sb.AppendLine();

            int calls512, calls4m;
            double ms512 = BenchNtqsi(seconds, 512 * 1024, false, out calls512);
            double ms4m = BenchNtqsi(seconds, 4 * 1024 * 1024, false, out calls4m);
            int callsReuse;
            double msReuse = BenchNtqsi(seconds, 4 * 1024 * 1024, true, out callsReuse);

            sb.AppendLine("=== 缓冲区策略对成本的影响 ===");
            sb.AppendLine("  512KB 起步每次分配（现状）: " + ms512.ToString("F3")
                + " ms，平均内核调用 " + calls512 + " 次/轮");
            sb.AppendLine("  4MB 起步每次分配          : " + ms4m.ToString("F3")
                + " ms，平均内核调用 " + calls4m + " 次/轮");
            sb.AppendLine("  4MB 复用缓冲区            : " + msReuse.ToString("F3")
                + " ms，平均内核调用 " + callsReuse + " 次/轮");
            if (calls512 > calls4m)
                sb.AppendLine("  → 现状每轮多一次内核调用，512KB 不够装下本机 "
                    + pidCount + " 个进程的线程数组");
            if (ms512 > 0)
                sb.AppendLine("  → 复用大缓冲区相对现状省 "
                    + ((ms512 - msReuse) / ms512 * 100).ToString("F0") + "%");
            sb.AppendLine();

            sb.AppendLine("=== 结论 ===");
            if (enumMs <= 0)
            {
                sb.AppendLine("EnumProcesses 计时为零，需加大采样时长重测。");
            }
            else
            {
                double at200 = enumMs / 200 * 100;
                sb.AppendLine("按 200ms 间隔：全量快照 " + (snapMs / 200 * 100).ToString("F2")
                    + "% → 仅枚举 PID " + at200.ToString("F2") + "%");
                sb.AppendLine("发现新 PID 后才付一次全量快照的钱，稳态下新进程为零，");
                sb.AppendLine("因此平均开销就是上面这个数。");
                if (at200 < 0.3)
                    sb.AppendLine("判定: 可行 —— 200ms 轮询的开销低于 Pavise 现有自身开销（0.243%），");
                else if (at200 < 0.6)
                    sb.AppendLine("判定: 勉强 —— 200ms 轮询与 Pavise 现有自身开销同量级，需要权衡。");
                else
                    sb.AppendLine("判定: 不可行 —— 仅枚举 PID 仍然太贵，需另寻检测手段。");
            }

            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            Console.Write(sb.ToString());
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int cls, IntPtr buffer, int len, out int ret);

        private static double BenchNtqsi(int seconds, int initialBytes, bool reuse, out int avgCalls)
        {
            const int SystemProcessInformation = 5;
            const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
            IntPtr persistent = reuse ? Marshal.AllocHGlobal(initialBytes) : IntPtr.Zero;
            int persistentSize = initialBytes;
            long totalCalls = 0;
            int iterations = 0;
            var sw = Stopwatch.StartNew();
            try
            {
                while (sw.ElapsedMilliseconds < seconds * 1000L)
                {
                    int size = reuse ? persistentSize : initialBytes;
                    IntPtr buf = reuse ? persistent : IntPtr.Zero;
                    try
                    {
                        while (true)
                        {
                            if (!reuse)
                            {
                                if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
                                buf = Marshal.AllocHGlobal(size);
                            }
                            int need;
                            totalCalls++;
                            int st = NtQuerySystemInformation(SystemProcessInformation, buf, size, out need);
                            if (st == 0) break;
                            if (st != StatusInfoLengthMismatch) break;
                            size = need + 128 * 1024;
                            if (reuse)
                            {
                                Marshal.FreeHGlobal(persistent);
                                persistent = Marshal.AllocHGlobal(size);
                                persistentSize = size;
                                buf = persistent;
                            }
                        }
                    }
                    finally { if (!reuse && buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
                    iterations++;
                }
            }
            finally { if (persistent != IntPtr.Zero) Marshal.FreeHGlobal(persistent); }
            sw.Stop();
            avgCalls = iterations > 0 ? (int)Math.Round(totalCalls / (double)iterations) : 0;
            return iterations > 0 ? sw.Elapsed.TotalMilliseconds / iterations : 0;
        }

        private static double BenchLoop(int seconds, Action body)
        {
            body();
            var sw = Stopwatch.StartNew();
            long deadline = seconds * 1000L;
            int iterations = 0;
            while (sw.ElapsedMilliseconds < deadline)
            {
                body();
                iterations++;
            }
            sw.Stop();
            return iterations > 0 ? sw.Elapsed.TotalMilliseconds / iterations : 0;
        }
    }
}
