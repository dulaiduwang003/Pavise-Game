// @author bdth 2074055628@qq.com
// 文件用途 量化后台进程爆发时 压制延迟对帧时间的伤害

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
        private struct BurstStat
        {
            public int Count;
            public double Median;
            public double OnePercentLow;
            public int Hitches;
        }

        private static void RunBurstLab(string output, string secondsArg, string hogsArg,
            string roundsArg, string delayArg)
        {
            int seconds, hogs, rounds, delayMs;
            if (!int.TryParse(secondsArg ?? "", out seconds) || seconds < 4) seconds = 10;
            if (!int.TryParse(hogsArg ?? "", out hogs) || hogs < 1) hogs = Environment.ProcessorCount;
            if (!int.TryParse(roundsArg ?? "", out rounds) || rounds < 1) rounds = 5;
            if (!int.TryParse(delayArg ?? "", out delayMs) || delayMs < 0) delayMs = 2300;

            var sb = new StringBuilder();
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var victim = new FrameVictim();
            var core = new SuppressionCore();

            sb.AppendLine("=== 压制延迟的代价 ===");
            sb.AppendLine("场景: 游戏运行中，后台突然启动 " + hogs + " 个满载进程");
            sb.AppendLine("变量: 从进程启动到施加压制之间的延迟");
            sb.AppendLine("窗口: 每段 " + seconds + " 秒，从进程启动瞬间开始计");
            sb.AppendLine("现状延迟 " + delayMs + " ms = WMI 实测中位 1560 + 合并窗口 750");
            sb.AppendLine();

            var lateVsInstant = new List<double>();
            var lateVsNever = new List<double>();
            var hitchDelta = new List<double>();
            double hitchThreshold = 0;

            try
            {
                victim.Start(11);
                Thread.Sleep(2500);

                BurstStat warm = Measure(victim, 3);
                hitchThreshold = warm.Median * 2;
                sb.AppendLine("空载中位帧 " + warm.Median.ToString("F2") + " ms，卡顿阈值取其两倍 "
                    + hitchThreshold.ToString("F2") + " ms");
                sb.AppendLine();
                sb.AppendLine("轮次  段              帧数     中位ms   1%最差ms   卡顿数");

                for (int r = 1; r <= rounds; r++)
                {
                    BurstStat a = RunBurst(victim, core, self, hogs, seconds, 0, hitchThreshold, false);
                    sb.AppendLine(BurstRow(r, "A 立即压制    ", a));

                    BurstStat b = RunBurst(victim, core, self, hogs, seconds, delayMs, hitchThreshold, false);
                    sb.AppendLine(BurstRow(r, "B 延迟后压制  ", b));

                    BurstStat c = RunBurst(victim, core, self, hogs, seconds, 0, hitchThreshold, true);
                    sb.AppendLine(BurstRow(r, "C 从不压制    ", c));

                    if (a.OnePercentLow > 0 && b.OnePercentLow > 0 && c.OnePercentLow > 0)
                    {
                        lateVsInstant.Add((b.OnePercentLow - a.OnePercentLow) / a.OnePercentLow * 100.0);
                        lateVsNever.Add((c.OnePercentLow - b.OnePercentLow) / c.OnePercentLow * 100.0);
                        hitchDelta.Add(b.Hitches - a.Hitches);
                    }
                }

                sb.AppendLine();
                sb.AppendLine("=== 结论 ===");
                if (lateVsInstant.Count < 2)
                {
                    sb.AppendLine("有效轮次不足。");
                }
                else
                {
                    double li = Med(lateVsInstant);
                    double ln = Med(lateVsNever);
                    double hd = Med(hitchDelta);
                    sb.AppendLine("延迟 " + delayMs + "ms 相对立即压制:");
                    sb.AppendLine("  1% 最差帧恶化 " + li.ToString("F1") + "%   各轮: " + Join(lateVsInstant));
                    sb.AppendLine("  卡顿数多出 " + hd.ToString("F0") + " 次   各轮: " + Join(hitchDelta));
                    sb.AppendLine();
                    sb.AppendLine("延迟压制相对从不压制，挽回了 " + ln.ToString("F1") + "% 的尾部帧");
                    sb.AppendLine();
                    if (Math.Abs(li) < 10 && Math.Abs(hd) < 3)
                        sb.AppendLine("判定: 压制延迟的代价很小 —— 现状的 " + delayMs
                            + "ms 发现延迟不值得为它付出 CPU 开销去优化。"
                            + "ETW、高频轮询、自适应分档全部不必做。");
                    else if (li > 25 || hd > 10)
                        sb.AppendLine("判定: 压制延迟代价显著 —— 尾部帧恶化 " + li.ToString("F0")
                            + "%，多出 " + hd.ToString("F0") + " 次卡顿。"
                            + "缩短发现延迟值得付出 CPU 开销，自适应轮询应当做。");
                    else
                        sb.AppendLine("判定: 代价中等 —— 尾部帧恶化 " + li.ToString("F1")
                            + "%，多出 " + hd.ToString("F0") + " 次卡顿。是否值得取决于愿意付多少 CPU。");
                }
            }
            catch (Exception ex) { sb.AppendLine("异常: " + ex); }
            finally
            {
                try { core.ReleaseReason(SuppressReason.Background); }
                catch { }
                victim.Stop();
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Console.Write(sb.ToString());
            }
        }

        private static BurstStat RunBurst(FrameVictim victim, SuppressionCore core, string self,
            int hogs, int seconds, int delayMs, double hitchThreshold, bool never)
        {
            var spawned = new List<Process>();
            try
            {
                victim.BeginPhase();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < hogs; i++)
                {
                    var psi = new ProcessStartInfo(self, "--cpu-burn")
                    { UseShellExecute = false, CreateNoWindow = true };
                    try { spawned.Add(Process.Start(psi)); }
                    catch { }
                }
                if (!never)
                {
                    int wait = delayMs - (int)sw.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep(wait);
                    foreach (Process p in spawned)
                    {
                        try { core.Acquire(p.Id, p.ProcessName, SuppressReason.Background, null,
                            SuppressionLevel.Isolated); }
                        catch { }
                    }
                }
                int remain = seconds * 1000 - (int)sw.ElapsedMilliseconds;
                if (remain > 0) Thread.Sleep(remain);
                return Summarize(victim.EndPhase(), hitchThreshold);
            }
            finally
            {
                try { core.ReleaseReason(SuppressReason.Background); }
                catch { }
                foreach (Process p in spawned)
                {
                    try { if (!p.HasExited) p.Kill(); }
                    catch { }
                    try { p.WaitForExit(2000); }
                    catch { }
                    try { p.Dispose(); }
                    catch { }
                }
                Thread.Sleep(1500);
            }
        }

        private static BurstStat Measure(FrameVictim victim, int seconds)
        {
            victim.BeginPhase();
            Thread.Sleep(seconds * 1000);
            return Summarize(victim.EndPhase(), double.MaxValue);
        }

        private static BurstStat Summarize(double[] samples, double hitchThreshold)
        {
            var s = new BurstStat();
            if (samples == null || samples.Length == 0) return s;
            var sorted = (double[])samples.Clone();
            Array.Sort(sorted);
            s.Count = sorted.Length;
            s.Median = sorted[sorted.Length / 2];
            int worst = Math.Max(1, sorted.Length / 100);
            double sum = 0;
            for (int i = sorted.Length - worst; i < sorted.Length; i++) sum += sorted[i];
            s.OnePercentLow = sum / worst;
            foreach (double v in samples) if (v > hitchThreshold) s.Hitches++;
            return s;
        }

        private static string BurstRow(int round, string phase, BurstStat s)
        {
            return round.ToString().PadLeft(3) + "   " + phase
                + s.Count.ToString().PadLeft(7)
                + s.Median.ToString("F2").PadLeft(9)
                + s.OnePercentLow.ToString("F2").PadLeft(11)
                + s.Hitches.ToString().PadLeft(9);
        }
    }
}
