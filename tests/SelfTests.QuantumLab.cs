// @author bdth 2074055628@qq.com
// 文件用途 量化 Win32PrioritySeparation 各取值对前台帧线程在满载竞争下的真实影响

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private const string PriorityControlKey = @"SYSTEM\CurrentControlSet\Control\PriorityControl";
        private const string PrioritySeparationValue = "Win32PrioritySeparation";

        private static void RunQuantumLab(string output, string secondsArg, string roundsArg)
        {
            int seconds, rounds;
            if (!int.TryParse(secondsArg ?? "", out seconds) || seconds < 3) seconds = 8;
            if (!int.TryParse(roundsArg ?? "", out rounds) || rounds < 1) rounds = 4;

            var sb = new StringBuilder();
            sb.AppendLine("=== 前台时间片（Win32PrioritySeparation）台架 ===");
            sb.AppendLine("时间 " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                + "  每段 " + seconds + "s × " + rounds + " 轮  竞争 " + Environment.ProcessorCount + " 进程");

            int original;
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PriorityControlKey, false))
            {
                object raw = key != null ? key.GetValue(PrioritySeparationValue) : null;
                original = raw is int ? (int)raw : 2;
            }
            var pool = new List<int> { original, 0x2, 0x26, 0x28 };
            var seen = new HashSet<int>();
            var picked = new List<int>();
            foreach (int c in pool) if (seen.Add(c)) picked.Add(c);
            int[] candidates = picked.ToArray();
            var labels = new List<string>();
            foreach (int c in candidates)
                labels.Add(c == original ? "原值 0x" + c.ToString("X") : "0x" + c.ToString("X"));
            sb.AppendLine("原值 0x" + original.ToString("X") + "，候选：" + string.Join("、", labels.ToArray()));
            sb.AppendLine();

            var samples = new List<double>[candidates.Length];
            for (int i = 0; i < samples.Length; i++) samples[i] = new List<double>();

            string self = Process.GetCurrentProcess().MainModule.FileName;
            var burners = new List<Process>();
            System.Windows.Forms.Form fg = null;
            var victim = new FrameVictim();
            try
            {
                System.Windows.Forms.Application.EnableVisualStyles();
                fg = new System.Windows.Forms.Form();
                fg.Text = "Pavise QuantumLab";
                fg.SetBounds(60, 60, 300, 120);
                fg.Show();
                fg.Activate();
                System.Windows.Forms.Application.DoEvents();

                for (int i = 0; i < Environment.ProcessorCount; i++)
                    burners.Add(Process.Start(new ProcessStartInfo(self, "--cpu-burn")
                    { UseShellExecute = false, CreateNoWindow = true }));
                victim.Start();
                Thread.Sleep(800);

                for (int r = 0; r < rounds; r++)
                {
                    for (int c = 0; c < candidates.Length; c++)
                    {
                        SetPrioritySeparation(candidates[c]);
                        Thread.Sleep(600);
                        System.Windows.Forms.Application.DoEvents();
                        victim.BeginPhase();
                        long until = Stopwatch.GetTimestamp() + (long)seconds * Stopwatch.Frequency;
                        while (Stopwatch.GetTimestamp() < until)
                        {
                            System.Windows.Forms.Application.DoEvents();
                            Thread.Sleep(30);
                        }
                        samples[c].AddRange(victim.EndPhase());
                    }
                    sb.AppendLine("第 " + (r + 1) + " 轮完成");
                }
            }
            finally
            {
                SetPrioritySeparation(original);
                victim.Stop();
                foreach (Process p in burners)
                {
                    try { if (!p.HasExited) p.Kill(); } catch { }
                    p.Dispose();
                }
                if (fg != null) fg.Dispose();
            }

            sb.AppendLine();
            sb.AppendLine("取值        帧数     中位ms    P99ms   1%最差ms");
            var stats = new PhaseStat[candidates.Length];
            for (int c = 0; c < candidates.Length; c++)
            {
                stats[c] = Summarize(samples[c].ToArray());
                sb.AppendLine(labels[c].PadRight(10) + Row(stats[c]));
            }
            sb.AppendLine();
            if (stats[0].Median <= 0) { sb.AppendLine("结论：样本异常"); Environment.ExitCode = 1; }
            else
            {
                bool anyWin = false;
                for (int c = 1; c < candidates.Length; c++)
                {
                    if (candidates[c] == original) continue;
                    double medGain = (stats[0].Median - stats[c].Median) / stats[0].Median * 100;
                    double lowGain = (stats[0].OnePercentLow - stats[c].OnePercentLow) / stats[0].OnePercentLow * 100;
                    sb.AppendLine(labels[c] + " 相对默认：中位帧 " + (medGain >= 0 ? "-" : "+")
                        + Math.Abs(medGain).ToString("F1") + "%，1% 最差 " + (lowGain >= 0 ? "-" : "+")
                        + Math.Abs(lowGain).ToString("F1") + "%"
                        + (medGain >= 3 || lowGain >= 3 ? "（改善）" : ""));
                    if (medGain >= 3 || lowGain >= 3) anyWin = true;
                }
                sb.AppendLine("结论：" + (anyWin
                    ? "存在超过 3% 门槛的取值，值得实现（游戏时切换、退出还原）"
                    : "各取值与默认差异不足 3% 门槛——民间偏方证伪，明确不做"));
                Environment.ExitCode = 0;
            }
            System.IO.File.WriteAllText(output, sb.ToString(), Encoding.UTF8);
        }

        private static void SetPrioritySeparation(int value)
        {
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(PriorityControlKey, true))
                if (key != null) key.SetValue(PrioritySeparationValue, value, RegistryValueKind.DWord);
        }
    }
}
