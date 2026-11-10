using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class ThreadLaneProbe
    {
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);

        private static long CpuOf(int tid, out bool canQuery, out int queryError)
        {
            canQuery = false;
            queryError = 0;
            IntPtr threadHandle = Native.OpenThread(2048, false, tid);
            if (threadHandle == IntPtr.Zero)
            {
                queryError = Marshal.GetLastWin32Error();
                return -1L;
            }
            try
            {
                long creation;
                long exit;
                long kernel;
                long user;
                if (!GetThreadTimes(threadHandle, out creation, out exit, out kernel, out user))
                {
                    queryError = Marshal.GetLastWin32Error();
                    return -1L;
                }
                canQuery = true;
                return kernel + user;
            }
            finally
            {
                Native.CloseHandle(threadHandle);
            }
        }

        private static bool CanOpenForSet(int tid, out int error)
        {
            error = 0;
            IntPtr threadHandle = Native.OpenThread(1024, false, tid);
            if (threadHandle == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }
            Native.CloseHandle(threadHandle);
            return true;
        }

        public static Report Run(int pid, int rounds, int intervalMs)
        {
            Report result = default(Report);
            result.Pid = pid;
            result.Top = new List<Lane>();
            result.Rounds = rounds;
            try
            {
                using (Process process = Process.GetProcessById(pid))
                {
                    result.Name = process.ProcessName;
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            Dictionary<int, long> initialCpuTicksByTid = new Dictionary<int, long>();
            Dictionary<int, long> previousCpuTicksByTid = new Dictionary<int, long>();
            Dictionary<int, int> queryErrorsById = new Dictionary<int, int>();
            int leaderStableRoundsValue = 0;
            int previousLeaderTid = 0;
            long ticks = DateTime.UtcNow.Ticks;
            for (int i = 0; i < rounds; i++)
            {
                Dictionary<int, long> currentCpuTicksByTid = new Dictionary<int, long>();
                try
                {
                    using (Process processById = Process.GetProcessById(pid))
                    {
                        ProcessThreadCollection threads = processById.Threads;
                        result.ThreadCount = threads.Count;
                        foreach (ProcessThread thread in threads)
                        {
                            bool canQuery;
                            int queryError;
                            long cpuOf = CpuOf(thread.Id, out canQuery, out queryError);
                            if (!canQuery)
                            {
                                queryErrorsById[thread.Id] = queryError;
                                continue;
                            }
                            currentCpuTicksByTid[thread.Id] = cpuOf;
                            if (!initialCpuTicksByTid.ContainsKey(thread.Id))
                            {
                                initialCpuTicksByTid[thread.Id] = cpuOf;
                            }
                        }
                    }
                }
                catch (Exception ex2)
                {
                    result.Error = ex2.Message;
                    break;
                }
                if (previousCpuTicksByTid.Count > 0)
                {
                    int leaderTid = 0;
                    long largestCpuDelta = -1L;
                    foreach (KeyValuePair<int, long> currentCpuTickEntry in currentCpuTicksByTid)
                    {
                        long previousCpuTick;
                        if (previousCpuTicksByTid.TryGetValue(currentCpuTickEntry.Key, out previousCpuTick))
                        {
                            long remainingValue = currentCpuTickEntry.Value - previousCpuTick;
                            if (remainingValue > largestCpuDelta)
                            {
                                largestCpuDelta = remainingValue;
                                leaderTid = currentCpuTickEntry.Key;
                            }
                        }
                    }
                    if (leaderTid != 0 && leaderTid == previousLeaderTid)
                    {
                        leaderStableRoundsValue++;
                    }
                    previousLeaderTid = leaderTid;
                }
                previousCpuTicksByTid = currentCpuTicksByTid;
                if (i < rounds - 1)
                {
                    Thread.Sleep(intervalMs);
                }
            }
            result.WallSeconds = (double)(DateTime.UtcNow.Ticks - ticks) / 10000000.0;
            result.LeaderStableRounds = leaderStableRoundsValue;
            result.SampledThreads = previousCpuTicksByTid.Count;
            List<Lane> lanes = new List<Lane>();
            long totalCpuTicks = 0L;
            foreach (KeyValuePair<int, long> previousCpuTickEntry in previousCpuTicksByTid)
            {
                long initialCpuTick;
                if (initialCpuTicksByTid.TryGetValue(previousCpuTickEntry.Key, out initialCpuTick))
                {
                    long remainingValue2 = previousCpuTickEntry.Value - initialCpuTick;
                    if (remainingValue2 < 0)
                    {
                        remainingValue2 = 0L;
                    }
                    totalCpuTicks += remainingValue2;
                    lanes.Add(new Lane
                    {
                        Tid = previousCpuTickEntry.Key,
                        Cpu100Ns = remainingValue2
                    });
                }
            }
            lanes.Sort((Lane a, Lane b) => b.Cpu100Ns.CompareTo(a.Cpu100Ns));
            double scaledWallSeconds = result.WallSeconds * 10000000.0;
            result.TotalCores = ((scaledWallSeconds > 0.0) ? ((double)totalCpuTicks / scaledWallSeconds) : 0.0);
            for (int j = 0; j < lanes.Count && j < 8; j++)
            {
                Lane lane = lanes[j];
                lane.SharePercent = ((totalCpuTicks > 0) ? ((double)lane.Cpu100Ns * 100.0 / (double)totalCpuTicks) : 0.0);
                lane.CanQuery = true;
                lane.CanSet = CanOpenForSet(lane.Tid, out lane.SetError);
                result.Top.Add(lane);
            }
            foreach (KeyValuePair<int, int> queryErrorEntry in queryErrorsById)
            {
                if (result.Top.Count < 12)
                {
                    result.Top.Add(new Lane
                    {
                        Tid = queryErrorEntry.Key,
                        QueryError = queryErrorEntry.Value
                    });
                    continue;
                }
                break;
            }
            return result;
        }

        public static string Format(Report r)
        {
            StringBuilder stringBuilder = new StringBuilder();
            stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.1"));
            stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.2") + (r.Name ?? "?") + " pid " + r.Pid);
            if (!string.IsNullOrEmpty(r.Error))
            {
                stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.3") + r.Error);
            }
            stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.4") + r.ThreadCount + Lang.T("t.threadlaneprobe.5") + r.SampledThreads + Lang.T("t.threadlaneprobe.6") + r.Rounds + Lang.T("t.threadlaneprobe.7") + r.WallSeconds.ToString("F1") + "s");
            stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.8") + r.TotalCores.ToString("F2") + Lang.T("t.threadlaneprobe.9"));
            stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.10") + r.LeaderStableRounds + Lang.T("t.threadlaneprobe.11") + Math.Max(0, r.Rounds - 2));
            stringBuilder.AppendLine();
            stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.12"));
            stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.13"));
            foreach (Lane top in r.Top)
            {
                if (!top.CanQuery)
                {
                    object[] instanceItems = new object[5] { "  ", null, null, null, null };
                    int tid = top.Tid;
                    instanceItems[1] = tid.ToString().PadRight(9);
                    instanceItems[2] = Lang.T("t.threadlaneprobe.14");
                    instanceItems[3] = top.QueryError;
                    instanceItems[4] = " ";
                    stringBuilder.AppendLine(string.Concat(instanceItems));
                }
                else
                {
                    string[] formattedParts = new string[5] { "  ", null, null, null, null };
                    int tid2 = top.Tid;
                    formattedParts[1] = tid2.ToString().PadRight(9);
                    double sharePercent = top.SharePercent;
                    formattedParts[2] = sharePercent.ToString("F1").PadLeft(8);
                    formattedParts[3] = Lang.T("t.threadlaneprobe.15").PadRight(9);
                    formattedParts[4] = (top.CanSet ? Lang.T("t.threadlaneprobe.16") : (Lang.T("t.threadlaneprobe.17") + top.SetError + " "));
                    stringBuilder.AppendLine(string.Concat(formattedParts));
                }
            }
            stringBuilder.AppendLine();
            bool hasWritableLane = false;
            bool hasReadOnlyLane = false;
            foreach (Lane currentTop in r.Top)
            {
                if (currentTop.CanSet)
                {
                    hasWritableLane = true;
                }
                if (currentTop.CanQuery && !currentTop.CanSet)
                {
                    hasReadOnlyLane = true;
                }
            }
            stringBuilder.AppendLine(Lang.T("t.threadlaneprobe.18") + ((hasWritableLane && !hasReadOnlyLane) ? Lang.T("t.threadlaneprobe.19") : (hasWritableLane ? Lang.T("t.threadlaneprobe.20") : Lang.T("t.threadlaneprobe.21"))));
            return stringBuilder.ToString();
        }
    }
}
