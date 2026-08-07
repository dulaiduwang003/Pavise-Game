// @author bdth 2074055628@qq.com
// 文件用途 量化游戏提优与渲染主权域在已压制后台的基础上还有多少增量

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
        private static void RunBoostLab(string output, string secondsArg, string hogsArg,
            string roundsArg, string victimThreadsArg)
        {
            int seconds, hogs, rounds, victimThreads;
            if (!int.TryParse(secondsArg ?? "", out seconds) || seconds < 5) seconds = 15;
            if (!int.TryParse(hogsArg ?? "", out hogs) || hogs < 1) hogs = Environment.ProcessorCount * 2;
            if (!int.TryParse(roundsArg ?? "", out rounds) || rounds < 1) rounds = 5;
            if (!int.TryParse(victimThreadsArg ?? "", out victimThreads) || victimThreads < 1)
                victimThreads = 12;

            var sb = new StringBuilder();
            string self = Process.GetCurrentProcess().MainModule.FileName;
            var spawned = new List<Process>();
            var core = new SuppressionCore();
            var victim = new FrameVictim();
            Process me = Process.GetCurrentProcess();
            ProcessPriorityClass origPriority = me.PriorityClass;

            sb.AppendLine("=== 提优的增量价值 ===");
            sb.AppendLine("问题: 产品里后台压制默认开启，提优在此基础上还值多少");
            sb.AppendLine("逻辑处理器 " + Environment.ProcessorCount + " | 抢占进程 " + hogs
                + " | 受害者 " + victimThreads + " 线程 | 每段 " + seconds + "s | " + rounds + " 轮");
            sb.AppendLine();

            var boostAlone = new List<double>();
            var boostOnTop = new List<double>();
            var laneOnTop = new List<double>();
            var laneOnBoost = new List<double>();
            var suppressAlone = new List<double>();

            try
            {
                for (int i = 0; i < hogs; i++)
                {
                    var psi = new ProcessStartInfo(self, "--cpu-burn")
                    { UseShellExecute = false, CreateNoWindow = true };
                    try { spawned.Add(Process.Start(psi)); }
                    catch { }
                }
                Thread.Sleep(1500);
                victim.Start(victimThreads - 1);
                Thread.Sleep(2000);

                sb.AppendLine("轮次  段                    帧数     中位ms   1%最差ms");
                for (int r = 1; r <= rounds; r++)
                {
                    PhaseStat a = BoostPhase(victim, me, core, spawned, seconds,
                        ProcessPriorityClass.Normal, ThreadPriority.Normal, false);
                    sb.AppendLine(BoostRow(r, "A 常规 放任         ", a));

                    PhaseStat b = BoostPhase(victim, me, core, spawned, seconds,
                        ProcessPriorityClass.High, ThreadPriority.Normal, false);
                    sb.AppendLine(BoostRow(r, "B 仅提优 放任       ", b));

                    PhaseStat c = BoostPhase(victim, me, core, spawned, seconds,
                        ProcessPriorityClass.Normal, ThreadPriority.Normal, true);
                    sb.AppendLine(BoostRow(r, "C 仅压制            ", c));

                    PhaseStat d = BoostPhase(victim, me, core, spawned, seconds,
                        ProcessPriorityClass.High, ThreadPriority.Normal, true);
                    sb.AppendLine(BoostRow(r, "D 压制+提优         ", d));

                    PhaseStat e = BoostPhase(victim, me, core, spawned, seconds,
                        ProcessPriorityClass.Normal, ThreadPriority.Highest, true);
                    sb.AppendLine(BoostRow(r, "E 压制+仅主线程     ", e));

                    PhaseStat f = BoostPhase(victim, me, core, spawned, seconds,
                        ProcessPriorityClass.High, ThreadPriority.Highest, true);
                    sb.AppendLine(BoostRow(r, "F 压制+提优+主线程  ", f));

                    if (a.OnePercentLow > 0 && b.OnePercentLow > 0 && c.OnePercentLow > 0
                        && d.OnePercentLow > 0 && e.OnePercentLow > 0 && f.OnePercentLow > 0)
                    {
                        boostAlone.Add((a.OnePercentLow - b.OnePercentLow) / a.OnePercentLow * 100.0);
                        suppressAlone.Add((a.OnePercentLow - c.OnePercentLow) / a.OnePercentLow * 100.0);
                        boostOnTop.Add((c.OnePercentLow - d.OnePercentLow) / c.OnePercentLow * 100.0);
                        laneOnTop.Add((c.OnePercentLow - e.OnePercentLow) / c.OnePercentLow * 100.0);
                        laneOnBoost.Add((d.OnePercentLow - f.OnePercentLow) / d.OnePercentLow * 100.0);
                    }
                }

                sb.AppendLine();
                sb.AppendLine("=== 结论 ===");
                if (boostOnTop.Count < 2)
                {
                    sb.AppendLine("有效轮次不足。");
                }
                else
                {
                    sb.AppendLine("参照组  仅压制 vs 放任       : " + Med(suppressAlone).ToString("F1") + "%");
                    sb.AppendLine("        仅提优 vs 放任       : " + Med(boostAlone).ToString("F1")
                        + "%   各轮: " + Join(boostAlone));
                    sb.AppendLine();
                    double bt = Med(boostOnTop), lt = Med(laneOnTop);
                    sb.AppendLine("关键    提优在压制之上的增量 : " + bt.ToString("F1")
                        + "%   各轮: " + Join(boostOnTop));
                    sb.AppendLine("        主权域在压制之上增量 : " + lt.ToString("F1")
                        + "%   各轮: " + Join(laneOnTop));
                    sb.AppendLine();

                    int btPos = 0, ltPos = 0;
                    foreach (double g in boostOnTop) if (g > 2) btPos++;
                    foreach (double g in laneOnTop) if (g > 2) ltPos++;

                    if (Math.Abs(bt) < 5)
                        sb.AppendLine("判定 GmBoost: 无增量 —— 后台已被压制时，把游戏提到 High 优先级"
                            + "带来的尾部帧变化落在噪声内（" + bt.ToString("F1") + "%）。");
                    else if (btPos == boostOnTop.Count && bt > 5)
                        sb.AppendLine("判定 GmBoost: 有增量 —— 每轮为正，尾部再改善 "
                            + bt.ToString("F1") + "%，应当保留。");
                    else
                        sb.AppendLine("判定 GmBoost: 不稳定（" + btPos + "/" + boostOnTop.Count
                            + " 轮为正，中位 " + bt.ToString("F1") + "%）。");

                    double lb = Med(laneOnBoost);
                    int lbPos = 0;
                    foreach (double g in laneOnBoost) if (g > 2) lbPos++;
                    sb.AppendLine("        主权域在提优之上增量 : " + lb.ToString("F1")
                        + "%   各轮: " + Join(laneOnBoost));
                    sb.AppendLine("        （产品里 GmBoost 默认开，这一行才是 GmRenderLane 的真实处境）");
                    sb.AppendLine();

                    if (Math.Abs(lb) < 5)
                        sb.AppendLine("判定 GmRenderLane: 无增量 —— 整个进程已是 High 时，"
                            + "再单独抬计帧线程的变化落在噪声内（" + lb.ToString("F1")
                            + "%）。进程级提优已经覆盖了它。");
                    else if (lbPos == laneOnBoost.Count && lb > 5)
                        sb.AppendLine("判定 GmRenderLane: 有增量 —— 在提优之上每轮为正，尾部再改善 "
                            + lb.ToString("F1") + "%，应当保留。");
                    else
                        sb.AppendLine("判定 GmRenderLane: 不稳定（" + lbPos + "/" + laneOnBoost.Count
                            + " 轮为正，中位 " + lb.ToString("F1") + "%）。");
                }
            }
            catch (Exception ex) { sb.AppendLine("异常: " + ex); }
            finally
            {
                try { core.ReleaseReason(SuppressReason.Background); }
                catch { }
                try { me.PriorityClass = origPriority; }
                catch { }
                victim.SetFrameThreadPriority(ThreadPriority.Normal);
                victim.Stop();
                foreach (Process p in spawned)
                {
                    try { if (!p.HasExited) p.Kill(); }
                    catch { }
                    try { p.Dispose(); }
                    catch { }
                }
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Console.Write(sb.ToString());
            }
        }

        private static PhaseStat BoostPhase(FrameVictim victim, Process me, SuppressionCore core,
            List<Process> targets, int seconds, ProcessPriorityClass priority,
            ThreadPriority framePriority, bool suppress)
        {
            try { me.PriorityClass = priority; }
            catch { }
            victim.SetFrameThreadPriority(framePriority);
            if (suppress)
                foreach (Process p in targets)
                {
                    try { core.Acquire(p.Id, p.ProcessName, SuppressReason.Background, null,
                        SuppressionLevel.Isolated); }
                    catch { }
                }
            Thread.Sleep(1200);
            PhaseStat s = RunPhase(victim, seconds);
            if (suppress)
            {
                try { core.ReleaseReason(SuppressReason.Background); }
                catch { }
            }
            try { me.PriorityClass = ProcessPriorityClass.Normal; }
            catch { }
            victim.SetFrameThreadPriority(ThreadPriority.Normal);
            Thread.Sleep(1200);
            return s;
        }

        private static string BoostRow(int round, string phase, PhaseStat s)
        {
            return round.ToString().PadLeft(3) + "   " + phase
                + s.Count.ToString().PadLeft(7)
                + s.Median.ToString("F2").PadLeft(9)
                + s.OnePercentLow.ToString("F2").PadLeft(11);
        }
    }
}
