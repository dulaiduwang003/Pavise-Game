// @author bdth 2074055628@qq.com
// 文件用途 用受控争抢负载量化后台压制的真实收益 A-B-A 三段自带漂移检验

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // PerfLab 的合成渲染器由 DwmFlush 节流，对 CPU 争抢不敏感，测不出压制收益。
        // 这里的受害者是纯 CPU 帧循环：每帧固定工作量、不节流、不等垂直同步，
        // 帧时间直接反映它拿到多少 CPU 时间片，因此对争抢高度敏感。
        private sealed class FrameVictim
        {
            private readonly List<double> frames = new List<double>();
            private readonly object gate = new object();
            private volatile bool stop;
            private volatile bool collect;
            private Thread worker;

            // 每帧的固定工作量。数值本身不重要，重要的是它在各阶段完全一致。
            private const int WorkPerFrame = 260000;

            public void Start()
            {
                worker = new Thread(delegate ()
                {
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
                worker.Priority = ThreadPriority.Normal;
                worker.Start();
            }

            public void BeginPhase() { lock (gate) frames.Clear(); collect = true; }

            public double[] EndPhase()
            {
                collect = false;
                lock (gate) return frames.ToArray();
            }

            public void Stop() { stop = true; if (worker != null) worker.Join(3000); }
        }

        private struct PhaseStat
        {
            public int Count;
            public double Median;
            public double P99;
            public double OnePercentLow;
        }

        // 帧时间用中位数与高分位描述：均值会被个别长帧带偏，
        // 而争抢造成的伤害恰恰集中在尾部。
        private static PhaseStat Summarize(double[] samples)
        {
            var s = new PhaseStat();
            if (samples == null || samples.Length == 0) return s;
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            s.Count = sorted.Length;
            s.Median = sorted[sorted.Length / 2];
            s.P99 = sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.99))];
            int worst = Math.Max(1, sorted.Length / 100);
            double sum = 0;
            for (int i = sorted.Length - worst; i < sorted.Length; i++) sum += sorted[i];
            s.OnePercentLow = sum / worst;
            return s;
        }

        // 用法：--contention-lab <输出文件> [每段秒数] [抢占进程数] [轮数]
        // 多轮 A/B 交替：压制的主战场是尾部帧而非中位帧，而尾部指标噪声大，
        // 单轮对照不足以定论，必须靠配对重复观察改善方向是否稳定。
        private static void RunContentionLab(string output, string secondsArg, string hogsArg, string roundsArg)
        {
            int seconds, hogs, rounds;
            if (!int.TryParse(secondsArg ?? "", out seconds) || seconds < 5) seconds = 15;
            if (!int.TryParse(hogsArg ?? "", out hogs) || hogs < 1) hogs = Environment.ProcessorCount;
            if (!int.TryParse(roundsArg ?? "", out rounds) || rounds < 1) rounds = 5;

            var sb = new StringBuilder();
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var spawned = new List<Process>();
            var core = new SuppressionCore();
            var victim = new FrameVictim();

            sb.AppendLine("=== 后台压制收益实测（多轮 A/B 配对）===");
            sb.AppendLine("逻辑处理器: " + Environment.ProcessorCount
                + " | 抢占进程: " + hogs + " | 每段: " + seconds + "s | 轮数: " + rounds);
            sb.AppendLine("受害者: 单线程定量帧循环（不节流，帧时间直接反映所得 CPU 时间片）");
            sb.AppendLine();

            var lowGains = new List<double>();
            var medGains = new List<double>();
            var partGains = new List<double>();
            try
            {
                for (int i = 0; i < hogs; i++)
                {
                    var psi = new ProcessStartInfo(self, "--cpu-burn")
                    { UseShellExecute = false, CreateNoWindow = true };
                    spawned.Add(Process.Start(psi));
                }
                Thread.Sleep(1500);
                victim.Start();
                Thread.Sleep(2000);

                sb.AppendLine("本机分区: 后台核 " + MaskText(CpuTopology.ThrottleMask)
                    + " | 竞技游戏核 " + MaskText(CpuTopology.StrictBoostMask)
                    + " | 分区可用=" + CpuTopology.HasSafeBackgroundPartition());
                sb.AppendLine();
                sb.AppendLine("轮次  段            帧数     中位ms   p99ms    1%最差ms");
                for (int r = 1; r <= rounds; r++)
                {
                    PhaseStat a = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "A 放任      ", a));

                    int nPri = Apply(core, spawned, SuppressionLevel.Restrained);
                    Thread.Sleep(1200);
                    PhaseStat b = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "B 仅降优先级", b));
                    core.ReleaseReason(SuppressReason.Background);
                    Thread.Sleep(1200);

                    int nIso = Apply(core, spawned, SuppressionLevel.Isolated);
                    Thread.Sleep(1200);
                    PhaseStat c = RunPhase(victim, seconds);
                    sb.AppendLine(Row(r, "C 降级+分区 ", c));
                    core.ReleaseReason(SuppressReason.Background);
                    Thread.Sleep(1200);

                    if (a.OnePercentLow > 0 && b.OnePercentLow > 0 && c.OnePercentLow > 0
                        && nPri == hogs && nIso == hogs)
                    {
                        lowGains.Add((a.OnePercentLow - b.OnePercentLow) / a.OnePercentLow * 100.0);
                        medGains.Add((a.OnePercentLow - c.OnePercentLow) / a.OnePercentLow * 100.0);
                        partGains.Add((b.OnePercentLow - c.OnePercentLow) / b.OnePercentLow * 100.0);
                    }
                }
                sb.AppendLine();
                sb.AppendLine("=== 结论 ===");
                if (lowGains.Count < 2)
                {
                    sb.AppendLine("有效配对不足（" + lowGains.Count + "），无法判定。");
                }
                else
                {
                    sb.AppendLine("以 1% 最差帧为准，各轮改善的中位值：");
                    sb.AppendLine("  仅降优先级 vs 放任 : " + Med(lowGains).ToString("F1") + "%");
                    sb.AppendLine("  降级+分区 vs 放任  : " + Med(medGains).ToString("F1") + "%");
                    sb.AppendLine("  分区的额外贡献     : " + Med(partGains).ToString("F1") + "%");
                    sb.AppendLine();
                    sb.AppendLine("  仅降优先级各轮: " + Join(lowGains));
                    sb.AppendLine("  降级+分区各轮 : " + Join(medGains));
                    sb.AppendLine("  分区增量各轮  : " + Join(partGains));
                    sb.AppendLine();
                    int partPositive = 0;
                    foreach (double g in partGains) if (g > 0) partPositive++;
                    double partMed = Med(partGains);
                    if (partPositive == partGains.Count && partMed > 10)
                        sb.AppendLine("判定: 分区有额外收益 —— 每一轮都优于仅降优先级，中位再改善 "
                            + partMed.ToString("F0") + "%。");
                    else if (partMed < -10)
                        sb.AppendLine("判定: 分区有害 —— 把后台挤进少数核心后，受害者反而更差，"
                            + "说明本机核心数下分区的代价超过收益。");
                    else if (partPositive * 2 < partGains.Count)
                        sb.AppendLine("判定: 分区无额外收益 —— 多数轮次不优于仅降优先级，"
                            + "收益基本来自降优先级本身。");
                    else
                        sb.AppendLine("判定: 分区增量方向不稳定 —— " + partPositive + "/" + partGains.Count
                            + " 轮为正，需要更多轮次才能定论。");
                }
            }
            catch (Exception ex) { sb.AppendLine("异常: " + ex); }
            finally
            {
                try { core.ReleaseReason(SuppressReason.Background); } catch { }
                victim.Stop();
                foreach (Process p in spawned)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    try { p.Dispose(); } catch { }
                }
            }

            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            Console.Write(sb.ToString());
        }

        private static int Apply(SuppressionCore core, List<Process> targets, SuppressionLevel level)
        {
            int ok = 0;
            foreach (Process p in targets)
            {
                try
                {
                    if (core.Acquire(p.Id, p.ProcessName, SuppressReason.Background, null, level)
                        != AcquireResult.ApplyFailed) ok++;
                }
                catch { }
            }
            return ok;
        }

        private static double Med(List<double> v)
        {
            var a = v.ToArray(); Array.Sort(a);
            return a.Length == 0 ? 0 : a[a.Length / 2];
        }

        private static string Join(List<double> v)
        {
            return string.Join(", ", Array.ConvertAll(v.ToArray(), g => g.ToString("F0") + "%"));
        }

        private static string MaskText(ulong mask)
        {
            var cores = new List<string>();
            for (int i = 0; i < 64; i++) if (((mask >> i) & 1UL) != 0) cores.Add(i.ToString());
            return "[" + string.Join(",", cores.ToArray()) + "]";
        }

        private static string Row(int round, string phase, PhaseStat s)
        {
            return round.ToString().PadLeft(3) + "   " + phase + "  "
                + s.Count.ToString().PadLeft(6)
                + s.Median.ToString("F2").PadLeft(9)
                + s.P99.ToString("F2").PadLeft(9)
                + s.OnePercentLow.ToString("F2").PadLeft(11);
        }

        private static PhaseStat RunPhase(FrameVictim victim, int seconds)
        {
            victim.BeginPhase();
            Thread.Sleep(seconds * 1000);
            return Summarize(victim.EndPhase());
        }

    }
}
