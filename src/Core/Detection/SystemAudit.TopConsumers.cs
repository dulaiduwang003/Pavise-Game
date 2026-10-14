// 作者：bdth 2074055628@qq.com
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
                            // 两次采样的进程创建时间不同则丢弃，避免 PID 复用污染差值。
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
