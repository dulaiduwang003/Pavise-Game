// @author bdth 2074055628@qq.com
// 文件用途 验证作业对象 CPU 硬配额能否施加 是否精确 以及受害者帧时间的即时反应

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
        private struct QuotaPhase
        {
            public double CpuPercent;
            public PhaseStat Frames;
        }

        private static void RunQuotaProbe(string output, string hogsArg, string secondsArg)
        {
            int hogs, seconds;
            if (!int.TryParse(hogsArg ?? "", out hogs) || hogs < 1) hogs = Environment.ProcessorCount;
            if (!int.TryParse(secondsArg ?? "", out seconds) || seconds < 3) seconds = 8;

            var sb = new StringBuilder();
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var spawned = new List<Process>();
            var victim = new FrameVictim();
            var quota = new JobQuota();

            sb.AppendLine("=== 作业对象 CPU 硬配额 生效性验证 ===");
            sb.AppendLine("逻辑处理器: " + Environment.ProcessorCount
                + " | 抢占进程: " + hogs + " | 每段: " + seconds + "s");
            sb.AppendLine("配额语义: CpuRate 相对系统总 CPU，5% 即该作业内所有进程合计不超过总量的 5%");
            sb.AppendLine();

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

                if (!quota.Open())
                {
                    sb.AppendLine("失败: 无法创建作业对象 —— " + quota.LastError);
                    Finish(output, sb, victim, spawned, quota);
                    return;
                }

                int joined = 0;
                var joinErrors = new List<string>();
                foreach (Process p in spawned)
                {
                    if (quota.Add(p.Id)) joined++;
                    else joinErrors.Add(quota.LastError);
                }
                sb.AppendLine("加入作业: " + joined + "/" + spawned.Count);
                foreach (string e in joinErrors) sb.AppendLine("  失败: " + e);
                if (joined == 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("判定: 硬配额路线不可行 —— 一个进程都塞不进作业。");
                    Finish(output, sb, victim, spawned, quota);
                    return;
                }
                sb.AppendLine();

                sb.AppendLine("段        配额    回读    实测占用   帧数     中位ms   1%最差ms");
                QuotaPhase baseline = RunQuotaPhase(victim, spawned, seconds);
                sb.AppendLine(QuotaRow("基线    ", "-", "-", baseline));

                var caps = new double[] { 50, 20, 5 };
                var results = new List<QuotaPhase>();
                var capOk = new List<bool>();
                foreach (double cap in caps)
                {
                    bool applied = quota.SetCap(cap);
                    double readBack = 0;
                    bool verified = applied && quota.VerifyCap(out readBack);
                    if (!applied) sb.AppendLine("  设 " + cap + "% 失败: " + quota.LastError);
                    Thread.Sleep(1200);
                    QuotaPhase ph = RunQuotaPhase(victim, spawned, seconds);
                    results.Add(ph);
                    capOk.Add(verified);
                    sb.AppendLine(QuotaRow("硬配额  ", cap.ToString("F0") + "%",
                        verified ? readBack.ToString("F0") + "%" : "失败", ph));
                }

                quota.Clear();
                Thread.Sleep(1200);
                QuotaPhase released = RunQuotaPhase(victim, spawned, seconds);
                sb.AppendLine(QuotaRow("解除后  ", "-", "-", released));

                sb.AppendLine();
                sb.AppendLine("=== 结论 ===");
                bool allVerified = capOk.Count > 0;
                foreach (bool ok in capOk) if (!ok) allVerified = false;
                sb.AppendLine("配额回读: " + (allVerified ? "全部一致" : "存在不一致，见上表"));

                sb.AppendLine();
                sb.AppendLine("配额精确度（实测占用 vs 设定值）:");
                for (int i = 0; i < caps.Length && i < results.Count; i++)
                {
                    double err = results[i].CpuPercent - caps[i];
                    sb.AppendLine("  设 " + caps[i].ToString("F0").PadLeft(3) + "%  实测 "
                        + results[i].CpuPercent.ToString("F1").PadLeft(6) + "%  偏差 "
                        + (err >= 0 ? "+" : "") + err.ToString("F1") + "%");
                }

                sb.AppendLine();
                sb.AppendLine("受害者帧时间（相对基线）:");
                for (int i = 0; i < caps.Length && i < results.Count; i++)
                {
                    if (baseline.Frames.Median <= 0 || baseline.Frames.OnePercentLow <= 0) break;
                    double dMed = (baseline.Frames.Median - results[i].Frames.Median)
                        / baseline.Frames.Median * 100.0;
                    double dLow = (baseline.Frames.OnePercentLow - results[i].Frames.OnePercentLow)
                        / baseline.Frames.OnePercentLow * 100.0;
                    sb.AppendLine("  配额 " + caps[i].ToString("F0").PadLeft(3) + "%  中位帧改善 "
                        + dMed.ToString("F1").PadLeft(6) + "%   1%最差帧改善 "
                        + dLow.ToString("F1").PadLeft(6) + "%");
                }

                sb.AppendLine();
                if (released.CpuPercent > baseline.CpuPercent * 0.8)
                    sb.AppendLine("解除验证: 通过 —— 清空控制标志后占用回到 "
                        + released.CpuPercent.ToString("F1") + "%（基线 "
                        + baseline.CpuPercent.ToString("F1") + "%）。");
                else
                    sb.AppendLine("解除验证: 未通过 —— 清空标志后占用仍只有 "
                        + released.CpuPercent.ToString("F1") + "%，限制可能没有真正解除。");
            }
            catch (Exception ex) { sb.AppendLine("异常: " + ex); }

            Finish(output, sb, victim, spawned, quota);
        }

        private static void Finish(string output, StringBuilder sb, FrameVictim victim,
            List<Process> spawned, JobQuota quota)
        {
            try { quota.Dispose(); } catch { }
            victim.Stop();
            foreach (Process p in spawned)
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { p.Dispose(); } catch { }
            }
            File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
            Console.Write(sb.ToString());
        }

        private static QuotaPhase RunQuotaPhase(FrameVictim victim, List<Process> targets, int seconds)
        {
            var result = new QuotaPhase();
            var before = new List<TimeSpan>();
            foreach (Process p in targets)
            {
                try { p.Refresh(); before.Add(p.TotalProcessorTime); }
                catch { before.Add(TimeSpan.Zero); }
            }
            var sw = Stopwatch.StartNew();
            victim.BeginPhase();
            Thread.Sleep(seconds * 1000);
            result.Frames = Summarize(victim.EndPhase());
            sw.Stop();

            double busy = 0;
            for (int i = 0; i < targets.Count; i++)
            {
                try
                {
                    targets[i].Refresh();
                    busy += (targets[i].TotalProcessorTime - before[i]).TotalMilliseconds;
                }
                catch { }
            }
            double wall = sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount;
            result.CpuPercent = wall > 0 ? busy / wall * 100.0 : 0;
            return result;
        }

        private static string QuotaRow(string phase, string cap, string readBack, QuotaPhase p)
        {
            return phase + cap.PadLeft(6) + readBack.PadLeft(8)
                + (p.CpuPercent.ToString("F1") + "%").PadLeft(11)
                + p.Frames.Count.ToString().PadLeft(8)
                + p.Frames.Median.ToString("F2").PadLeft(9)
                + p.Frames.OnePercentLow.ToString("F2").PadLeft(11);
        }
    }
}
