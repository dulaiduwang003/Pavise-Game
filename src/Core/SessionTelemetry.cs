// @author bdth 2074055628@qq.com
// 文件用途 会话期低频只读采样 GPU 每核 CPU 与内存并在结束时归因汇总

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AegisApp
{
    internal sealed class SessionTelemetry
    {
        private const int SampleIntervalMs = 5000;
        private const double CoreSaturation = 0.95;

        private Thread worker;
        private volatile bool running;
        private readonly object stateSync = new object();

        private int samples;
        private int gpuSamples;
        private long gpuUtilSum;
        private int gpuTempMax;
        private int thermalThrottleSamples;
        private int powerThrottleSamples;
        private int coreSaturatedSamples;
        private double topCoreBusySum;
        private ulong minAvailMb = ulong.MaxValue;

        private long[] prevIdle;
        private long[] prevBusy;

        public void Begin()
        {
            lock (stateSync)
            {
                if (running) return;
                samples = 0; gpuSamples = 0; gpuUtilSum = 0; gpuTempMax = 0;
                thermalThrottleSamples = 0; powerThrottleSamples = 0;
                coreSaturatedSamples = 0; topCoreBusySum = 0;
                minAvailMb = ulong.MaxValue;
                prevIdle = null; prevBusy = null;
                running = true;
                worker = new Thread(Loop);
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.Lowest;
                worker.Start();
            }
        }

        public string Finish()
        {
            Thread t;
            lock (stateSync)
            {
                if (!running) return null;
                running = false;
                t = worker;
                worker = null;
            }
            if (t != null) t.Join(2000);
            lock (stateSync)
            {
                if (samples < 2) return null;
                if (gpuSamples > 0 && PowerWallSeen(
                        thermalThrottleSamples * 100 / gpuSamples,
                        powerThrottleSamples * 100 / gpuSamples)
                    && !Settings.Load("PowerWallSeen", false))
                {
                    Settings.Save("PowerWallSeen", true);
                    Logger.Log("检测到本局 GPU 明显受功耗/温度墙限制，下局竞技电源档回退保守（不再禁用空闲与激进睿频；重开电源计划开关可重置）");
                }
                return BuildSummary(
                    gpuSamples, gpuUtilSum, gpuTempMax,
                    thermalThrottleSamples, powerThrottleSamples,
                    samples, topCoreBusySum, coreSaturatedSamples,
                    minAvailMb, Nvml.Available);
            }
        }

        internal const int PowerWallArmPercent = 5;

        internal static bool PowerWallSeen(int thermalPct, int powerPct)
        {
            return thermalPct >= PowerWallArmPercent || powerPct >= PowerWallArmPercent;
        }

        private void Loop()
        {
            while (running)
            {
                try { SampleOnce(); }
                catch { }
                for (int waited = 0; waited < SampleIntervalMs && running; waited += 250)
                    Thread.Sleep(250);
            }
        }

        private void SampleOnce()
        {
            long[] idle, busy;
            bool cpuOk = QueryProcessorTimes(out idle, out busy);
            int gpuUtil, tempC; ulong reasons;
            bool gpuOk = Nvml.TrySample(out gpuUtil, out tempC, out reasons);
            ulong availMb = QueryAvailableMb();

            lock (stateSync)
            {
                if (!running) return;
                if (cpuOk && prevIdle != null && prevIdle.Length == idle.Length)
                {
                    double top = 0;
                    bool saturated = false;
                    for (int i = 0; i < idle.Length; i++)
                    {
                        long dIdle = idle[i] - prevIdle[i];
                        long dBusy = busy[i] - prevBusy[i];
                        long dTotal = dIdle + dBusy;
                        if (dTotal <= 0) continue;
                        double fraction = (double)dBusy / dTotal;
                        if (fraction > top) top = fraction;
                        if (fraction >= CoreSaturation) saturated = true;
                    }
                    topCoreBusySum += top;
                    if (saturated) coreSaturatedSamples++;
                    samples++;
                }
                if (cpuOk) { prevIdle = idle; prevBusy = busy; }
                if (gpuOk)
                {
                    gpuSamples++;
                    gpuUtilSum += gpuUtil;
                    if (tempC > gpuTempMax) gpuTempMax = tempC;
                    if ((reasons & (Nvml.ReasonSwThermal | Nvml.ReasonHwThermal)) != 0) thermalThrottleSamples++;
                    if ((reasons & (Nvml.ReasonSwPowerCap | Nvml.ReasonHwPowerBrake)) != 0) powerThrottleSamples++;
                }
                if (availMb > 0 && availMb < minAvailMb) minAvailMb = availMb;
            }
        }

        internal static string BuildSummary(
            int gpuSamples, long gpuUtilSum, int gpuTempMax,
            int thermalSamples, int powerSamples,
            int cpuSamples, double topCoreBusySum, int saturatedSamples,
            ulong minAvailMb, bool nvmlAvailable)
        {
            var parts = new System.Collections.Generic.List<string>();
            if (gpuSamples > 0)
            {
                string gpu = Lang.F("ev.gpu",
                    (gpuUtilSum / gpuSamples).ToString(), gpuTempMax.ToString());
                int thermalPct = thermalSamples * 100 / gpuSamples;
                int powerPct = powerSamples * 100 / gpuSamples;
                if (thermalPct > 0) gpu += Lang.F("ev.gpu.thermal", thermalPct.ToString());
                if (powerPct > 0) gpu += Lang.F("ev.gpu.power", powerPct.ToString());
                parts.Add(gpu);
            }
            else if (!nvmlAvailable) parts.Add(Lang.T("ev.gpu.none"));
            if (cpuSamples > 0)
            {
                int topAvg = (int)(topCoreBusySum * 100 / cpuSamples);
                string cpu = Lang.F("ev.cpu", topAvg.ToString());
                int satPct = saturatedSamples * 100 / cpuSamples;
                if (satPct > 0) cpu += Lang.F("ev.cpu.sat", satPct.ToString());
                parts.Add(cpu);
            }
            if (minAvailMb != ulong.MaxValue)
                parts.Add(Lang.F("ev.mem",
                    (minAvailMb / 1024.0).ToString("0.0",
                        System.Globalization.CultureInfo.InvariantCulture)));
            return parts.Count == 0 ? null : string.Join(" | ", parts.ToArray());
        }

        private const int SystemProcessorPerformanceInformation = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorPerf
        {
            public long IdleTime;
            public long KernelTime;
            public long UserTime;
            public long DpcTime;
            public long InterruptTime;
            public uint InterruptCount;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int infoClass, IntPtr info, int length, out int returnLength);

        private static bool QueryProcessorTimes(out long[] idle, out long[] busy)
        {
            idle = null; busy = null;
            int count = Environment.ProcessorCount;
            int one = Marshal.SizeOf(typeof(ProcessorPerf));
            IntPtr buffer = Marshal.AllocHGlobal(one * count);
            try
            {
                int returned;
                if (NtQuerySystemInformation(
                        SystemProcessorPerformanceInformation, buffer, one * count, out returned) != 0)
                    return false;
                int actual = Math.Min(count, returned / one);
                if (actual == 0) return false;
                idle = new long[actual];
                busy = new long[actual];
                for (int i = 0; i < actual; i++)
                {
                    var perf = (ProcessorPerf)Marshal.PtrToStructure(
                        new IntPtr(buffer.ToInt64() + (long)i * one), typeof(ProcessorPerf));
                    idle[i] = perf.IdleTime;
                    busy[i] = perf.KernelTime - perf.IdleTime + perf.UserTime;
                }
                return true;
            }
            catch { return false; }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhys;
            public ulong AvailPhys;
            public ulong TotalPageFile;
            public ulong AvailPageFile;
            public ulong TotalVirtual;
            public ulong AvailVirtual;
            public ulong AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

        private static ulong QueryAvailableMb()
        {
            try
            {
                var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                if (!GlobalMemoryStatusEx(ref status)) return 0;
                return status.AvailPhys / (1024 * 1024);
            }
            catch { return 0; }
        }
    }

    internal static class EvidenceStore
    {
        public const string FileName = "Aegis.evidence.log";
        private static readonly object FileSync = new object();

        public static void Append(string dataDir, string line)
        {
            try
            {
                lock (FileSync)
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(dataDir, FileName),
                        line + Environment.NewLine, new System.Text.UTF8Encoding(false));
            }
            catch (Exception error)
            {
                Logger.LogFailure("证据记录写入失败", error);
            }
        }

        public static string ReadTail(string dataDir, int maxLines)
        {
            try
            {
                lock (FileSync)
                {
                    string path = System.IO.Path.Combine(dataDir, FileName);
                    if (!System.IO.File.Exists(path)) return "";
                    string[] all = System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8);
                    int take = Math.Min(maxLines, all.Length);
                    var sb = new System.Text.StringBuilder();
                    for (int i = all.Length - take; i < all.Length; i++)
                        if (all[i].Length > 0) sb.AppendLine(all[i]);
                    return sb.ToString().TrimEnd('\r', '\n');
                }
            }
            catch { return ""; }
        }

        public static void ClearAll(string dataDir)
        {
            try
            {
                lock (FileSync)
                {
                    string path = System.IO.Path.Combine(dataDir, FileName);
                    if (System.IO.File.Exists(path))
                        System.IO.File.WriteAllText(path, "", new System.Text.UTF8Encoding(false));
                }
            }
            catch { }
        }

        public static bool DeleteNear(string dataDir, DateTime stamp, string game)
        {
            if (game == null) return false;
            try
            {
                lock (FileSync)
                {
                    string path = System.IO.Path.Combine(dataDir, FileName);
                    if (!System.IO.File.Exists(path)) return false;
                    string[] all = System.IO.File.ReadAllLines(path, System.Text.Encoding.UTF8);
                    var kept = new System.Collections.Generic.List<string>();
                    bool removed = false;
                    foreach (string line in all)
                    {
                        string[] f = line.Split(new[] { " | " }, StringSplitOptions.None);
                        DateTime lineStamp;
                        bool match = f.Length >= 3
                            && string.Equals(f[1], game, StringComparison.OrdinalIgnoreCase)
                            && DateTime.TryParseExact(f[0], "yyyy-MM-dd HH:mm:ss",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out lineStamp)
                            && Math.Abs((lineStamp - stamp).TotalSeconds) <= 10;
                        if (match) { removed = true; continue; }
                        kept.Add(line);
                    }
                    if (removed)
                        System.IO.File.WriteAllLines(path, kept.ToArray(), new System.Text.UTF8Encoding(false));
                    return removed;
                }
            }
            catch { return false; }
        }
    }
}
