// @author bdth 2074055628@qq.com
// 文件用途 量化 SMT 兄弟核干扰对帧线程的真实代价 判定帧线程独占物理核是否值得做

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        [DllImport("kernel32.dll")]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr thread, UIntPtr mask);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

        private static List<ulong> PhysicalCoreMasks()
        {
            var cores = new List<ulong>();
            int len = 0;
            GetLogicalProcessorInformationEx(0, IntPtr.Zero, ref len);
            if (len <= 0) return cores;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                if (!GetLogicalProcessorInformationEx(0, buf, ref len)) return cores;
                long pos = 0;
                while (pos + 8 <= len)
                {
                    IntPtr rec = (IntPtr)((long)buf + pos);
                    int rel = Marshal.ReadInt32(rec, 0);
                    int size = Marshal.ReadInt32(rec, 4);
                    if (size <= 0 || pos + size > len) break;
                    if (rel == 0)
                    {
                        IntPtr u = (IntPtr)((long)rec + 8);
                        int gc = Marshal.ReadInt16(u, 22);
                        for (int i = 0; i < gc && i < 1; i++)
                        {
                            IntPtr ga = (IntPtr)((long)u + 24 + i * 16);
                            if (Marshal.ReadInt16(ga, 8) == 0)
                                cores.Add((ulong)Marshal.ReadInt64(ga, 0));
                        }
                    }
                    pos += size;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return cores;
        }

        private static ulong LowestBit(ulong mask)
        {
            return mask & (ulong)(-(long)mask);
        }

        private sealed class PinnedVictim
        {
            private readonly List<double> frames = new List<double>();
            private readonly object gate = new object();
            private volatile bool stop;
            private volatile bool collect;
            private Thread worker;
            private readonly ulong affinity;

            private const int WorkPerFrame = 260000;

            public PinnedVictim(ulong mask) { affinity = mask; }

            public void Start()
            {
                worker = new Thread(delegate ()
                {
                    SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)affinity);
                    var sw = new Stopwatch();
                    double sink = 0;
                    while (!stop)
                    {
                        sw.Restart();
                        for (int i = 1; i <= WorkPerFrame; i++) sink += 1.0 / i;
                        sw.Stop();
                        if (collect)
                        {
                            double ms = sw.Elapsed.TotalMilliseconds;
                            lock (gate) frames.Add(ms);
                        }
                    }
                    if (sink < 0) Console.Write("");
                });
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.Highest;
                worker.Start();
            }

            public void BeginPhase() { lock (gate) frames.Clear(); collect = true; }

            public double[] EndPhase()
            {
                collect = false;
                lock (gate) return frames.ToArray();
            }

            public void Stop()
            {
                stop = true;
                if (worker != null) worker.Join(3000);
            }
        }

        private sealed class PinnedStress
        {
            private volatile bool stop;
            private Thread worker;

            public void Start(ulong mask)
            {
                stop = false;
                worker = new Thread(delegate ()
                {
                    SetThreadAffinityMask(GetCurrentThread(), (UIntPtr)mask);
                    double sink = 0;
                    while (!stop) for (int i = 1; i <= 260000; i++) sink += 1.0 / i;
                    if (sink < 0) Console.Write("");
                });
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.Normal;
                worker.Start();
            }

            public void Stop()
            {
                stop = true;
                if (worker != null) worker.Join(3000);
                worker = null;
            }
        }

        private static void RunSmtLab(string output, string secondsArg, string roundsArg)
        {
            int seconds, rounds;
            if (!int.TryParse(secondsArg ?? "", out seconds) || seconds < 3) seconds = 8;
            if (!int.TryParse(roundsArg ?? "", out rounds) || rounds < 1) rounds = 5;

            var sb = new StringBuilder();
            sb.AppendLine("=== SMT 兄弟核干扰台架 ===");
            sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "  每段 " + seconds + "s × " + rounds + " 轮");

            List<ulong> cores = PhysicalCoreMasks();
            sb.AppendLine("物理核 " + cores.Count + " 个");
            ulong pairCore = 0, otherCore = 0;
            foreach (ulong core in cores)
            {
                ulong low = LowestBit(core);
                if (core != low && pairCore == 0) { pairCore = core; continue; }
                if (pairCore != 0 && core != pairCore) { otherCore = core; break; }
                if (otherCore == 0 && pairCore == 0) otherCore = core;
            }
            if (pairCore == 0 || otherCore == 0)
            {
                sb.AppendLine("结论：找不到 SMT 逻辑核对（超线程关闭或拓扑异常），本机不适用该优化");
                System.IO.File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Environment.ExitCode = 1;
                return;
            }
            ulong a0 = LowestBit(pairCore);
            ulong a1 = LowestBit(pairCore & ~a0);
            ulong b0 = LowestBit(otherCore);
            sb.AppendLine("帧线程绑 0x" + a0.ToString("X") + "，兄弟核 0x" + a1.ToString("X")
                + "，远端对照核 0x" + b0.ToString("X"));
            sb.AppendLine();

            var idle = new List<double>();
            var sibling = new List<double>();
            var remote = new List<double>();
            var victim = new PinnedVictim(a0);
            victim.Start();
            Thread.Sleep(500);
            try
            {
                for (int r = 0; r < rounds; r++)
                {
                    victim.BeginPhase();
                    Thread.Sleep(seconds * 1000);
                    idle.AddRange(victim.EndPhase());

                    var s1 = new PinnedStress();
                    s1.Start(a1);
                    Thread.Sleep(300);
                    victim.BeginPhase();
                    Thread.Sleep(seconds * 1000);
                    sibling.AddRange(victim.EndPhase());
                    s1.Stop();

                    var s2 = new PinnedStress();
                    s2.Start(b0);
                    Thread.Sleep(300);
                    victim.BeginPhase();
                    Thread.Sleep(seconds * 1000);
                    remote.AddRange(victim.EndPhase());
                    s2.Stop();
                    sb.AppendLine("第 " + (r + 1) + " 轮完成");
                }
            }
            finally { victim.Stop(); }

            PhaseStat si = Summarize(idle.ToArray());
            PhaseStat ss = Summarize(sibling.ToArray());
            PhaseStat sr = Summarize(remote.ToArray());
            sb.AppendLine();
            sb.AppendLine("段        帧数     中位ms    P99ms   1%最差ms");
            sb.AppendLine("空载      " + Row(si));
            sb.AppendLine("兄弟核压  " + Row(ss));
            sb.AppendLine("远端核压  " + Row(sr));
            sb.AppendLine();
            if (si.Median <= 0 || ss.Median <= 0 || sr.Median <= 0)
            {
                sb.AppendLine("结论：样本异常，无法判定");
                Environment.ExitCode = 1;
            }
            else
            {
                double medCost = (ss.Median - sr.Median) / sr.Median * 100;
                double lowCost = (ss.OnePercentLow - sr.OnePercentLow) / sr.OnePercentLow * 100;
                double remoteCost = (sr.Median - si.Median) / si.Median * 100;
                sb.AppendLine("SMT 兄弟干扰代价（兄弟压 vs 远端压）：中位帧 +" + medCost.ToString("F1")
                    + "%，1% 最差 +" + lowCost.ToString("F1") + "%");
                sb.AppendLine("跨核旁路影响（远端压 vs 空载）：中位帧 " + (remoteCost >= 0 ? "+" : "")
                    + remoteCost.ToString("F1") + "%（共享缓存/功耗预算的部分，独占物理核救不了）");
                bool worth = medCost >= 5 || lowCost >= 8;
                sb.AppendLine(worth
                    ? "结论：SMT 兄弟干扰显著，帧线程独占物理核（兄弟核留空）值得实现"
                    : "结论：本机 SMT 兄弟干扰不足 5%/8% 门槛，不值得实现");
                Environment.ExitCode = 0;
            }
            System.IO.File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
        }

        private static string Row(PhaseStat s)
        {
            return s.Count.ToString().PadLeft(6)
                + s.Median.ToString("F2").PadLeft(10)
                + s.P99.ToString("F2").PadLeft(9)
                + s.OnePercentLow.ToString("F2").PadLeft(10);
        }
    }
}
