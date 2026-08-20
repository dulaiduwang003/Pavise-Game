// @author bdth 2074055628@qq.com
// 文件用途 给帧率小窗取整机 CPU 游戏进程 CPU 与 GPU 占用显存温度 只在小窗开着时采样
// 游戏进程的 CPU 走系统进程表 全程不打开游戏进程句柄
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static class SystemLoadProbe
    {
        private const int IntervalMs = 1000;

        private static readonly object lk = new object();
        private static long lastIdle, lastKernel, lastUser;
        private static bool primed;
        private static long nextDueTicks;

        private static int gamePid;
        private static long lastGameCpu, lastGameCreation, lastGameTicks;
        private static bool gamePrimed;

        private static double cpuPercent = -1;
        private static double gameCpuPercent = -1;
        private static double gpuPercent = -1;
        private static double gpuTempC = -1;
        private static double vramUsedMb = -1;
        private static double vramTotalMb = -1;

        // -1 表示这一项取不到 调用方不要画它 而不是画个 0
        public static double CpuPercent { get { lock (lk) return cpuPercent; } }
        public static double GameCpuPercent { get { lock (lk) return gameCpuPercent; } }
        public static double GpuPercent { get { lock (lk) return gpuPercent; } }
        public static double GpuTempC { get { lock (lk) return gpuTempC; } }
        public static double VramUsedMb { get { lock (lk) return vramUsedMb; } }
        public static double VramTotalMb { get { lock (lk) return vramTotalMb; } }

        public static void Reset()
        {
            lock (lk)
            {
                primed = false;
                lastIdle = 0; lastKernel = 0; lastUser = 0;
                gamePid = 0; gamePrimed = false;
                lastGameCpu = 0; lastGameCreation = 0; lastGameTicks = 0;
                nextDueTicks = 0;
                cpuPercent = -1;
                gameCpuPercent = -1;
                gpuPercent = -1;
                gpuTempC = -1;
                vramUsedMb = -1;
                vramTotalMb = -1;
            }
        }

        public static void SampleIfDue(int rendererPid)
        {
            lock (lk)
            {
                long now = Stopwatch.GetTimestamp();
                if (now < nextDueTicks) return;
                nextDueTicks = now + Stopwatch.Frequency * IntervalMs / 1000;
                SampleCpu();
                SampleGame(rendererPid, now);
                SampleGpu();
            }
        }

        private static void SampleCpu()
        {
            long idle, kernel, user;
            if (!GetSystemTimes(out idle, out kernel, out user)) { cpuPercent = -1; return; }

            if (primed)
            {
                // kernel 里已经含 idle 所以总量就是 kernel+user 不需要核数也不需要墙上时间
                long total = (kernel - lastKernel) + (user - lastUser);
                long busy = total - (idle - lastIdle);
                if (total > 0)
                {
                    double v = (double)busy / total * 100;
                    cpuPercent = v < 0 ? 0 : v > 100 ? 100 : v;
                }
            }
            lastIdle = idle; lastKernel = kernel; lastUser = user;
            primed = true;
        }

        private static void SampleGame(int pid, long now)
        {
            if (pid <= 0) { gameCpuPercent = -1; gamePrimed = false; gamePid = 0; return; }
            if (pid != gamePid) { gamePid = pid; gamePrimed = false; gameCpuPercent = -1; }

            ProcessSnapshot snap = ProcessSnapshotSource.Capture();
            ProcEntry e = snap != null ? snap.Find(pid) : null;
            if (e == null) { gameCpuPercent = -1; gamePrimed = false; return; }

            // pid 会被回收 创建时间对不上就是换了一个进程 重新起账不要把差值算成占用
            if (gamePrimed && e.Creation == lastGameCreation
                && now > lastGameTicks && e.Cpu >= lastGameCpu)
            {
                double wall100Ns = (double)(now - lastGameTicks) / Stopwatch.Frequency * 10000000.0;
                double capacity = wall100Ns * Environment.ProcessorCount;
                if (capacity > 0)
                {
                    double v = (e.Cpu - lastGameCpu) / capacity * 100;
                    gameCpuPercent = v < 0 ? 0 : v > 100 ? 100 : v;
                }
            }
            lastGameCpu = e.Cpu;
            lastGameCreation = e.Creation;
            lastGameTicks = now;
            gamePrimed = true;
        }

        private static void SampleGpu()
        {
            GpuReading r = GpuLoadProbe.Read();
            gpuPercent = r.Percent;
            gpuTempC = r.TempC;
            vramUsedMb = r.VramUsedMb;
            vramTotalMb = r.VramTotalMb;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    }
}
