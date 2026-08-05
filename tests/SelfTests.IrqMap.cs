// @author bdth 2074055628@qq.com
// 文件用途 诊断工具 测量每个核心的中断负载分布 判断本机是否存在中断异常

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static string CoreLabel(ulong mask)
        {
            var parts = new List<string>();
            for (int i = 0; i < 64; i++) if (((mask >> i) & 1UL) != 0) parts.Add(i.ToString());
            return string.Join("/", parts.ToArray());
        }

        private static void RunIrqMap(string output, string windowArg, string passesArg)
        {
            int window, passes;
            if (!int.TryParse(windowArg ?? "", out window) || window < 200) window = 3000;
            if (!int.TryParse(passesArg ?? "", out passes) || passes < 1) passes = 3;

            var sb = new StringBuilder();
            sb.AppendLine("=== 中断负载分布测量 ===");
            sb.AppendLine("逻辑处理器: " + Environment.ProcessorCount
                + " | 物理核: " + CpuTopology.PhysicalCoreCount
                + " | 每轮窗口: " + window + "ms | 轮数: " + passes);
            sb.AppendLine("测量项: DPC 时间 + 中断时间 占墙钟的比例（NtQuerySystemInformation）");
            sb.AppendLine();

            ulong[] cores = CpuTopology.PhysicalCoreMasks();
            if (cores.Length == 0)
            {
                sb.AppendLine("SKIP  未能解析处理器拓扑，无法按物理核聚合。");
                File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
                Console.WriteLine(sb.ToString());
                return;
            }

            ulong primary = CpuTopology.PrimaryCoreMask;
            var perPassPrimary = new List<double>();
            var perPassWorst = new List<double>();
            var picks = new List<ulong>();

            for (int pass = 1; pass <= passes; pass++)
            {
                double[] rates = DpcSampler.MeasureInterruptRates(window);
                if (rates == null)
                {
                    sb.AppendLine("SKIP  第 " + pass + " 轮测量失败，接口不可用。");
                    continue;
                }

                sb.AppendLine("--- 第 " + pass + " 轮 ---");
                sb.AppendLine("物理核          中断占比");
                double worstRate = 0;
                foreach (ulong core in cores)
                {
                    double rate = CpuPartitionPolicy.CoreInterruptRate(rates, core);
                    if (rate > worstRate) worstRate = rate;
                    string tag = core == primary ? "  <= 承载 CPU 0" : "";
                    sb.AppendLine("  " + CoreLabel(core).PadRight(14)
                        + (rate * 100.0).ToString("F4").PadLeft(10) + " %" + tag);
                }

                double primaryRate = CpuPartitionPolicy.CoreInterruptRate(rates, primary);
                ulong pick = CpuPartitionPolicy.FindInterruptCore(
                    rates, cores, CpuTopology.PhysicalCoreCount);

                sb.AppendLine("  最脏的核占比    : " + (worstRate * 100.0).ToString("F4") + " %");
                sb.AppendLine("  承载 CPU 0 的核 : " + (primaryRate * 100.0).ToString("F4") + " %"
                    + (worstRate > 0 ? "（是最脏核的 " + (primaryRate / worstRate * 100.0).ToString("F1") + "%）" : ""));
                sb.AppendLine("  本轮离群判定    : " + (pick == 0 ? "无离群核" : "核 " + CoreLabel(pick)));
                sb.AppendLine();

                perPassPrimary.Add(primaryRate);
                perPassWorst.Add(worstRate);
                picks.Add(pick);
            }

            sb.AppendLine("=== 结论 ===");
            if (picks.Count == 0)
            {
                sb.AppendLine("测量全部失败，无法判定。");
            }
            else
            {
                sb.AppendLine("当前阈值: 物理核 >= " + CpuPartitionPolicy.InterruptAvoidMinPhysicalCores
                    + " 且 最脏核占比 >= " + (CpuPartitionPolicy.InterruptAvoidMinRate * 100.0).ToString("F2")
                    + " % 且 >= 第二脏核的 " + CpuPartitionPolicy.InterruptAvoidRatio.ToString("F1") + " 倍");
                sb.AppendLine("最脏核占比各轮  : " + JoinPercent(perPassWorst));
                sb.AppendLine("CPU 0 所在核各轮: " + JoinPercent(perPassPrimary));

                int picked = 0, primaryPicked = 0;
                ulong stable = picks[0];
                foreach (ulong p in picks)
                {
                    if (p != 0) picked++;
                    if (p != 0 && p == primary) primaryPicked++;
                    if (p != stable) stable = 0;
                }
                var labels = new List<string>();
                foreach (ulong p in picks) labels.Add(p == 0 ? "无" : CoreLabel(p));
                sb.AppendLine("各轮离群核      : " + string.Join("  ", labels.ToArray()));
                sb.AppendLine();

                if (stable != 0)
                {
                    ulong partition = CpuTopology.StrictBoostMask;
                    sb.AppendLine("竞技游戏分区    : 0x" + partition.ToString("X"));
                    sb.AppendLine("离群核是否在分区内: "
                        + ((partition & stable) != 0 ? "是，游戏线程会被调度到它上面" : "否，游戏线程不会碰到它"));
                    sb.AppendLine();
                }

                if (picked == 0)
                    sb.AppendLine("判定: 本机没有明显离群的中断核，中断分布正常，无需处理。");
                else if (stable != 0 && picked == picks.Count)
                {
                    double topRate = Med(perPassWorst);
                    sb.AppendLine("判定: 中断稳定集中在核 " + CoreLabel(stable)
                        + "（中位占比 " + (topRate * 100.0).ToString("F2") + "%）。");
                    sb.AppendLine("      中断的单次干扰是微秒级，5% 以下通常不足以造成可感知的卡顿；"
                        + "超过 5% 建议排查该核上的设备（系统环境页的中断亲和可将其引走）。");
                    if (primaryPicked != picks.Count)
                        sb.AppendLine("      注意: 该核不是承载 CPU 0 的核。本机上"
                            + "「屏蔽线程 0/1」的民间做法躲错了核。");
                }
                else
                    sb.AppendLine("判定: 结果不稳定（" + picked + "/" + picks.Count
                        + " 轮出现离群核，且目标核在轮次间变化），中断分布随负载漂移，"
                        + "建议在有真实外设和网络活动时重测。");
            }

            string text = sb.ToString();
            File.WriteAllText(output, text, Encoding.UTF8);
            Console.WriteLine(text);
        }

        private static string JoinPercent(List<double> v)
        {
            var parts = new List<string>();
            foreach (double d in v) parts.Add((d * 100.0).ToString("F4") + "%");
            return string.Join("  ", parts.ToArray());
        }
    }
}
