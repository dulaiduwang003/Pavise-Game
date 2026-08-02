// @author bdth 2074055628@qq.com
// 文件用途 采样游戏各线程的 CPU 占用找出帧关键线程 并探测其调度句柄 全程只读不写

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class ThreadLaneProbe
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        internal struct Lane
        {
            public int Tid;
            public long Cpu100Ns;
            public double SharePercent;
            public bool CanQuery;
            public bool CanSet;
            public int QueryError;
            public int SetError;
        }

        internal struct Report
        {
            public int Pid;
            public string Name;
            public int ThreadCount;
            public int SampledThreads;
            public double WallSeconds;
            public double TotalCores;
            public List<Lane> Top;
            public int LeaderStableRounds;
            public int Rounds;
            public string Error;
        }

        private static long CpuOf(int tid, out bool canQuery, out int queryError)
        {
            canQuery = false; queryError = 0;
            IntPtr h = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, tid);
            if (h == IntPtr.Zero) { queryError = Marshal.GetLastWin32Error(); return -1; }
            try
            {
                long c, e, k, u;
                if (!GetThreadTimes(h, out c, out e, out k, out u))
                {
                    queryError = Marshal.GetLastWin32Error();
                    return -1;
                }
                canQuery = true;
                return k + u;
            }
            finally { Native.CloseHandle(h); }
        }

        private static bool CanOpenForSet(int tid, out int error)
        {
            error = 0;
            IntPtr h = Native.OpenThread(Native.THREAD_SET_LIMITED_INFORMATION, false, tid);
            if (h == IntPtr.Zero) { error = Marshal.GetLastWin32Error(); return false; }
            Native.CloseHandle(h);
            return true;
        }

        public static Report Run(int pid, int rounds, int intervalMs)
        {
            var report = new Report { Pid = pid, Top = new List<Lane>(), Rounds = rounds };
            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    report.Name = target.ProcessName;
                }
            }
            catch (Exception ex) { report.Error = ex.Message; return report; }

            var first = new Dictionary<int, long>();
            var last = new Dictionary<int, long>();
            var errors = new Dictionary<int, int>();
            int leaderStable = 0, prevLeader = 0;
            long startTicks = DateTime.UtcNow.Ticks;

            for (int round = 0; round < rounds; round++)
            {
                var now = new Dictionary<int, long>();
                try
                {
                    using (Process target = Process.GetProcessById(pid))
                    {
                        ProcessThreadCollection threads = target.Threads;
                        report.ThreadCount = threads.Count;
                        foreach (ProcessThread t in threads)
                        {
                            bool canQuery; int err;
                            long cpu = CpuOf(t.Id, out canQuery, out err);
                            if (!canQuery) { errors[t.Id] = err; continue; }
                            now[t.Id] = cpu;
                            if (!first.ContainsKey(t.Id)) first[t.Id] = cpu;
                        }
                    }
                }
                catch (Exception ex) { report.Error = ex.Message; break; }

                if (last.Count > 0)
                {
                    int leader = 0; long best = -1;
                    foreach (KeyValuePair<int, long> kv in now)
                    {
                        long prev;
                        if (!last.TryGetValue(kv.Key, out prev)) continue;
                        long delta = kv.Value - prev;
                        if (delta > best) { best = delta; leader = kv.Key; }
                    }
                    if (leader != 0 && leader == prevLeader) leaderStable++;
                    prevLeader = leader;
                }
                last = now;
                if (round < rounds - 1) System.Threading.Thread.Sleep(intervalMs);
            }

            report.WallSeconds = (DateTime.UtcNow.Ticks - startTicks) / (double)TimeSpan.TicksPerSecond;
            report.LeaderStableRounds = leaderStable;
            report.SampledThreads = last.Count;

            var lanes = new List<Lane>();
            long totalDelta = 0;
            foreach (KeyValuePair<int, long> kv in last)
            {
                long begin;
                if (!first.TryGetValue(kv.Key, out begin)) continue;
                long delta = kv.Value - begin;
                if (delta < 0) delta = 0;
                totalDelta += delta;
                lanes.Add(new Lane { Tid = kv.Key, Cpu100Ns = delta });
            }
            lanes.Sort(delegate(Lane a, Lane b) { return b.Cpu100Ns.CompareTo(a.Cpu100Ns); });

            double wall100Ns = report.WallSeconds * TimeSpan.TicksPerSecond;
            report.TotalCores = wall100Ns > 0 ? totalDelta / wall100Ns : 0;
            for (int i = 0; i < lanes.Count && i < 8; i++)
            {
                Lane lane = lanes[i];
                lane.SharePercent = totalDelta > 0 ? lane.Cpu100Ns * 100.0 / totalDelta : 0;
                lane.CanQuery = true;
                lane.CanSet = CanOpenForSet(lane.Tid, out lane.SetError);
                report.Top.Add(lane);
            }
            foreach (KeyValuePair<int, int> kv in errors)
            {
                if (report.Top.Count >= 12) break;
                report.Top.Add(new Lane { Tid = kv.Key, QueryError = kv.Value });
            }
            return report;
        }

        public static string Format(Report r)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== Render Lane 线程探针（只读，不写入任何调度参数）===");
            sb.AppendLine("目标：" + (r.Name ?? "?") + " (pid " + r.Pid + ")");
            if (!string.IsNullOrEmpty(r.Error)) sb.AppendLine("错误：" + r.Error);
            sb.AppendLine("线程总数：" + r.ThreadCount + "，成功采样：" + r.SampledThreads
                + "，采样轮次：" + r.Rounds + "，实际时长：" + r.WallSeconds.ToString("F1") + "s");
            sb.AppendLine("进程整体占用：" + r.TotalCores.ToString("F2") + " 核");
            sb.AppendLine("主导线程连续稳定轮次：" + r.LeaderStableRounds + " / " + Math.Max(0, r.Rounds - 2));
            sb.AppendLine();
            sb.AppendLine("按 CPU 占用排序的线程（Top）：");
            sb.AppendLine("  TID      占进程CPU%   读句柄   写句柄(THREAD_SET_LIMITED_INFORMATION)");
            foreach (Lane lane in r.Top)
            {
                if (!lane.CanQuery)
                {
                    sb.AppendLine("  " + lane.Tid.ToString().PadRight(9) + "（读句柄被拒，错误 " + lane.QueryError + "）");
                    continue;
                }
                sb.AppendLine("  " + lane.Tid.ToString().PadRight(9)
                    + lane.SharePercent.ToString("F1").PadLeft(8) + "%"
                    + "      可".PadRight(9)
                    + (lane.CanSet ? "可" : "拒（错误 " + lane.SetError + "）"));
            }
            sb.AppendLine();
            bool anySet = false, anyDenied = false;
            foreach (Lane lane in r.Top) { if (lane.CanSet) anySet = true; if (lane.CanQuery && !lane.CanSet) anyDenied = true; }
            sb.AppendLine("结论：" + (anySet && !anyDenied
                ? "线程调度句柄可获取，Render Lane 在本游戏上技术可行"
                : anySet ? "部分线程可写、部分被拒，需要逐线程降级处理"
                : "线程写句柄被拒，本游戏无法做线程级提优"));
            return sb.ToString();
        }
    }
}
