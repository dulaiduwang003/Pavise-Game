// @author bdth 2074055628@qq.com
// 文件用途 占用最高的后台进程采样
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        public sealed class LoadEntry
        {
            public string Name;
            public double Ratio;
        }

        private sealed class Sample
        {
            public string Name;
            public long Started;
            public TimeSpan Cpu;
        }

        // 取两次进程 CPU 时间求差 中间睡一个窗口 不用性能计数器
        //   计数器要建查询还要预热 体检是一次性的用不上
        //   分母乘了逻辑核数 所以比值是占整机而不是占单核
        // 取两次进程 CPU 时间求差 中间睡一个窗口 不用性能计数器
        //   计数器要建查询还要预热 体检是一次性的用不上
        //   分母乘了逻辑核数 所以比值是占整机而不是占单核
        public static List<LoadEntry> TopConsumers(int windowMs, int take)
        {
            var result = new List<LoadEntry>();
            try
            {
                var before = new Dictionary<int, Sample>();
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            // 0 和 4 是空闲进程和 System 它们的 CPU 时间没有参考意义
                            // 0 和 4 是空闲进程和 System 它们的 CPU 时间没有参考意义
                            if (p.Id <= 4) continue;
                            before[p.Id] = new Sample
                            {
                                Name = p.ProcessName, Started = p.StartTime.Ticks, Cpu = p.TotalProcessorTime
                            };
                        }
                        catch { }
                    }
                }
                if (before.Count == 0) return result;
                System.Threading.Thread.Sleep(windowMs);
                double span = windowMs / 1000.0 * Environment.ProcessorCount;
                if (span <= 0) return result;
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            Sample old;
                            if (!before.TryGetValue(p.Id, out old)) continue;
                            // 两次采样之间 pid 可能被回收给了新进程 启动时间对不上就丢掉
                            //   否则会拿新进程的累计时间去减旧进程的 算出个离谱的占用
                            // 两次采样之间 pid 可能被回收给了新进程 启动时间对不上就丢掉
                            //   否则会拿新进程的累计时间去减旧进程的 算出个离谱的占用
                            if (p.StartTime.Ticks != old.Started) continue;
                            double delta = (p.TotalProcessorTime - old.Cpu).TotalSeconds;
                            if (delta <= 0) continue;
                            result.Add(new LoadEntry { Name = old.Name, Ratio = delta / span });
                        }
                        catch { }
                    }
                }
            }
            catch { return result; }
            result.Sort(delegate(LoadEntry a, LoadEntry b) { return b.Ratio.CompareTo(a.Ratio); });
            if (result.Count > take) result.RemoveRange(take, result.Count - take);
            return result;
        }
    }
}
