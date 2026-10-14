using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal sealed class MonitoringHardwareReadHealth
    {
        private bool hasSource;
        private long sourceGeneration;
        private MonitoringMetrics provided;

        internal bool Observe(long generation, MonitoringMetrics selected, MonitoringSensorReading reading, bool fresh)
        {
            if (!hasSource || generation != sourceGeneration)
            { Reset(); hasSource = true; sourceGeneration = generation; }
            if (!fresh || reading == null) return false;
            selected &= MonitoringNativeSensors.HardwareMetrics;
            MonitoringMetrics present = Present(reading) & selected;
            MonitoringMetrics knownSelected = provided & selected;
            bool healthy = (present & knownSelected) == knownSelected;
            provided |= present;
            return healthy;
        }
        internal void Reset()
        { hasSource = false; sourceGeneration = 0; provided = 0; }
        private static MonitoringMetrics Present(MonitoringSensorReading reading)
        {
            MonitoringMetrics present = 0;
            if (reading.CpuPower.HasValue) present |= MonitoringMetrics.CpuPower;
            if (reading.CpuTemperature.HasValue) present |= MonitoringMetrics.CpuTemperature;
            if (reading.GpuUsage.HasValue) present |= MonitoringMetrics.GpuUsage;
            if (reading.GpuTemperature.HasValue) present |= MonitoringMetrics.GpuTemperature;
            if (reading.GpuClock.HasValue) present |= MonitoringMetrics.GpuClock;
            if (reading.GpuMemoryClock.HasValue) present |= MonitoringMetrics.GpuMemoryClock;
            if (reading.GpuPower.HasValue) present |= MonitoringMetrics.GpuPower;
            if (reading.GpuFanRpm.HasValue || reading.GpuFanPercent.HasValue) present |= MonitoringMetrics.GpuFan;
            if (reading.VramMiB.HasValue) present |= MonitoringMetrics.Vram;
            if (reading.VramUsage.HasValue) present |= MonitoringMetrics.VramUsage;
            return present;
        }
    }

    internal sealed class MonitoringNativeSensors : IMonitoringSensors
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            internal uint Size, PageFaultCount;
            internal UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage,
                QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage,
                PagefileUsage, PeakPagefileUsage;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatus
        {
            internal uint Length, Load;
            internal ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile,
                TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
        }
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
        [DllImport("kernel32.dll")] private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);
        [DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr handle, ref ProcessMemoryCounters value, uint size);
        private IntPtr process;
        private int pid;
        private long creation, lastCpu = -1, lastStamp;
        private RtssHardwareProvider hardwareProvider;
        private MonitoringRtssHardwareSensors hardware;
        private MonitoringGpuRouting gpuRouting;
        private long gpuRouteVersion = -1, firstHardwareRead = -1, lastHardwareGeneration = -1;
        private readonly MonitoringHardwareReadHealth hardwareHealth = new MonitoringHardwareReadHealth();
        private readonly MonitoringHardwareDisplayCache hardwareDisplay = new MonitoringHardwareDisplayCache();
        internal const MonitoringMetrics GpuMetrics = MonitoringMetrics.GpuUsage | MonitoringMetrics.GpuTemperature
            | MonitoringMetrics.GpuClock | MonitoringMetrics.GpuPower | MonitoringMetrics.Vram
            | MonitoringMetrics.VramUsage | MonitoringMetrics.GpuMemoryClock | MonitoringMetrics.GpuFan;
        internal const MonitoringMetrics HardwareMetrics = GpuMetrics | MonitoringMetrics.CpuPower | MonitoringMetrics.CpuTemperature;

        public bool Read(RtssFrameSample target, MonitoringMetrics metrics, out MonitoringSensorReading reading)
        {
            reading = new MonitoringSensorReading();
            if (target.Pid <= 4 || target.Creation <= 0 || GameSessionDetector.ForegroundPid() != target.Pid) return false;
            if (pid != target.Pid || creation != target.Creation)
            {
                ResetTarget(); pid = target.Pid; creation = target.Creation;
                process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            }
            long born, exit, kernel, user;
            if (process == IntPtr.Zero || !GetProcessTimes(process, out born, out exit, out kernel, out user)
                || born != creation || exit != 0) return false;
            if ((metrics & MonitoringMetrics.CpuUsage) != 0)
            {
                long cpu = kernel + user, stamp = Stopwatch.GetTimestamp();
                reading.CpuUsage = CpuPercent(lastCpu, cpu, stamp - lastStamp, Stopwatch.Frequency, Environment.ProcessorCount);
                lastCpu = cpu; lastStamp = stamp;
            }
            else { lastCpu = -1; lastStamp = 0; }
            if ((metrics & MonitoringMetrics.GameRam) != 0 && !MonitoringFrameWindow.Valid(target.GameRamMiB, 0, double.MaxValue).HasValue)
                reading.GameRamMiB = ReadWorkingSet(process);
            ReadSystemMemory(metrics, reading);
            long now = (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency));
            MonitoringMetrics hardwareMetrics = metrics & HardwareMetrics;
            string routeStatus = "";
            if ((metrics & GpuMetrics) != 0)
            {
                if (gpuRouting == null) gpuRouting = new MonitoringGpuRouting();
                gpuRouting.Update(pid, creation, target.AdapterLuid, now);
                reading.GpuAdapterName = gpuRouting.AdapterName;
                if (gpuRouteVersion != gpuRouting.Version)
                {
                    gpuRouteVersion = gpuRouting.Version;
                    if (hardware != null) hardware.Reset();
                    hardwareDisplay.Reset(); hardwareHealth.Reset(); firstHardwareRead = -1;
                }
                routeStatus = GpuRouteStatus(gpuRouting.State);
                if (gpuRouting.SensorName.Length == 0) hardwareMetrics &= ~GpuMetrics;
            }
            else if (gpuRouting != null) { gpuRouting.Dispose(); gpuRouting = null; gpuRouteVersion = -1; }
            if (hardwareMetrics != 0)
            {
                if (hardwareProvider == null) hardwareProvider = new RtssHardwareProvider();
                if (hardware == null) hardware = new MonitoringRtssHardwareSensors(
                    delegate { return hardwareProvider.IsResponsive(); }, "",
                    delegate { return hardwareProvider.SourceGeneration; });
                hardware.SetGpuRoute(gpuRouting == null ? "" : gpuRouting.SensorName);
                bool running = hardwareProvider.EnsureRunning();
                if (!running) hardware.Reset();
                bool available = running && hardware.Read(hardwareMetrics, reading);
                // 旧数据不能证明采集器健康。
                bool fresh = available && hardware.LastReadStatus == "ready" && !hardwareProvider.UsingRecentHeartbeat;
                long sampledGeneration = hardwareProvider.SourceGeneration;
                if (lastHardwareGeneration != sampledGeneration) { firstHardwareRead = -1; lastHardwareGeneration = sampledGeneration; }
                if (fresh && firstHardwareRead < 0) firstHardwareRead = now;
                hardwareProvider.ReportReadResult(hardwareHealth.Observe(sampledGeneration,
                    hardwareMetrics, reading, fresh));
                long currentGeneration = hardwareProvider.SourceGeneration;
                if (currentGeneration != sampledGeneration)
                {
                    hardwareDisplay.Reset();
                    fresh = available = false;
                }
                // 显示缓存不参与能力判断或恢复退避。
                reading.RetainedHardwareMetrics = hardwareDisplay.Apply(currentGeneration,
                    hardwareMetrics, reading, now, fresh);
                if (reading.RetainedHardwareMetrics != MonitoringMetrics.None)
                {
                    reading.Status = AutomaticPolicy.Text("显示最近读数 · 硬件数据刷新中", "Recent readings · refreshing hardware data");
                }
                else if (!available)
                {
                    reading.Status = AutomaticPolicy.Text("等待 RTSS 硬件数据", "Waiting for RTSS hardware data");
                }
                else
                {
                    reading.Status = hardware.LastReadStatus == "refresh-pending" || hardwareProvider.UsingRecentHeartbeat
                        ? AutomaticPolicy.Text("硬件数据暂缓刷新", "Hardware refresh pending")
                        : HardwareAvailabilityStatus(hardwareMetrics, reading, firstHardwareRead >= 0 && now - firstHardwareRead >= 3000);
                }
            }
            if (routeStatus.Length > 0) reading.Status = routeStatus + (reading.Status.Length > 0 ? " · " + reading.Status : "");
            // 发布前再次确认进程身份及焦点。
            return GameSessionDetector.ForegroundPid() == pid && Native.QueryProcessIdentityState(process, creation) == 0;
        }
        internal static string GpuRouteStatus(string state)
        {
            if (state == "ready") return "";
            if (state == "ambiguous" || state == "name-ambiguous")
                return AutomaticPolicy.Text("无法唯一确认游戏显卡，GPU 数据暂不显示", "Game GPU is ambiguous; GPU readings withheld");
            if (state == "inventory-unavailable" || state == "counter-unavailable" || state == "adapter-unavailable")
                return AutomaticPolicy.Text("游戏显卡识别暂不可用，将自动重试", "Game GPU detection unavailable; retrying automatically");
            return AutomaticPolicy.Text("正在识别游戏使用的显卡", "Detecting the game's rendering GPU");
        }
        internal static string HardwareAvailabilityStatus(MonitoringMetrics metrics, MonitoringSensorReading reading, bool initialized)
        {
            string missing = MissingHardwareStatus(metrics, reading);
            if (missing.Length == 0) return "";
            if (!initialized) return AutomaticPolicy.Text("硬件传感器初始化中", "Initializing hardware sensors");
            if ((metrics & GpuMetrics) != 0 && !reading.GpuSourceMatched)
                return AutomaticPolicy.Text("RTSS 数据源未提供当前游戏显卡的传感器", "RTSS has no sensors for the game's current GPU");
            return missing;
        }
        internal static string MissingHardwareStatus(MonitoringMetrics metrics, MonitoringSensorReading reading)
        {
            var missing = new List<string>();
            AddMissing(missing, metrics, MonitoringMetrics.CpuTemperature, reading.CpuTemperature, "CPU 温度", "CPU temperature");
            AddMissing(missing, metrics, MonitoringMetrics.CpuPower, reading.CpuPower, "CPU 功耗", "CPU power");
            AddMissing(missing, metrics, MonitoringMetrics.GpuUsage, reading.GpuUsage, "GPU 利用率", "GPU usage");
            AddMissing(missing, metrics, MonitoringMetrics.GpuTemperature, reading.GpuTemperature, "GPU 温度", "GPU temperature");
            AddMissing(missing, metrics, MonitoringMetrics.GpuClock, reading.GpuClock, "GPU 频率", "GPU clock");
            AddMissing(missing, metrics, MonitoringMetrics.GpuMemoryClock, reading.GpuMemoryClock, "显存频率", "VRAM clock");
            AddMissing(missing, metrics, MonitoringMetrics.GpuPower, reading.GpuPower, "GPU 功耗", "GPU power");
            AddMissing(missing, metrics, MonitoringMetrics.GpuFan, reading.GpuFanRpm ?? reading.GpuFanPercent, "GPU 风扇", "GPU fan");
            AddMissing(missing, metrics, MonitoringMetrics.Vram, reading.VramMiB, "显存用量", "VRAM used");
            AddMissing(missing, metrics, MonitoringMetrics.VramUsage, reading.VramUsage, "显存占用率", "VRAM percentage");
            return missing.Count == 0 ? "" : AutomaticPolicy.Text("硬件数据源未提供：", "Hardware source unavailable: ") + String.Join(" / ", missing);
        }
        private static void AddMissing(List<string> missing, MonitoringMetrics metrics, MonitoringMetrics field,
            double? value, string chinese, string english)
        { if ((metrics & field) != 0 && !value.HasValue) missing.Add(AutomaticPolicy.Text(chinese, english)); }
        internal static void ReadSystemMemory(MonitoringMetrics metrics, MonitoringSensorReading reading)
        {
            if ((metrics & (MonitoringMetrics.Ram | MonitoringMetrics.RamUsage)) == 0) return;
            var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (GlobalMemoryStatusEx(ref memory) && memory.AvailablePhysical <= memory.TotalPhysical)
                SetMemoryReading(metrics, reading, memory.TotalPhysical - memory.AvailablePhysical, memory.TotalPhysical, false);
        }
        internal static double? CpuPercent(long before, long after, long elapsed, long frequency, int logical)
        {
            if (before < 0 || after < before || elapsed <= 0 || frequency <= 0 || logical <= 0) return null;
            double value = (after - before) * (double)frequency * 100.0 / elapsed / TimeSpan.TicksPerSecond / logical;
            return MonitoringFrameWindow.Finite(value) ? (double?)Math.Max(0, Math.Min(100, value)) : null;
        }
        // 使用量与容量必须来自同一次设备查询。
        internal static double? MemoryPercent(ulong used, ulong total)
        { return total > 0 && used <= total ? (double?)(used * 100.0 / total) : null; }
        internal static void SetMemoryReading(MonitoringMetrics selected, MonitoringSensorReading reading,
            ulong used, ulong total, bool video)
        {
            double? percent = MemoryPercent(used, total);
            if (!percent.HasValue) return;
            if (video)
            {
                if ((selected & MonitoringMetrics.Vram) != 0) reading.VramMiB = used / 1048576.0;
                if ((selected & MonitoringMetrics.VramUsage) != 0) reading.VramUsage = percent;
            }
            else
            {
                if ((selected & MonitoringMetrics.Ram) != 0) reading.RamMiB = used / 1048576.0;
                if ((selected & MonitoringMetrics.RamUsage) != 0) reading.RamUsage = percent;
            }
        }
        internal static double? ReadWorkingSet(IntPtr verifiedHandle)
        {
            if (verifiedHandle == IntPtr.Zero || verifiedHandle == new IntPtr(-1)) return null;
            var memory = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters)) };
            try { return GetProcessMemoryInfo(verifiedHandle, ref memory, memory.Size)
                ? (double?)(memory.WorkingSetSize.ToUInt64() / 1048576.0) : null; }
            catch { return null; }
        }
        public void ResetTarget()
        {
            if (process != IntPtr.Zero) { Native.CloseHandle(process); process = IntPtr.Zero; }
            if (hardware != null) hardware.Reset();
            if (gpuRouting != null) gpuRouting.Reset();
            gpuRouteVersion = -1; firstHardwareRead = lastHardwareGeneration = -1;
            hardwareDisplay.Reset();
            hardwareHealth.Reset();
            pid = 0; creation = 0; lastCpu = -1; lastStamp = 0;
        }
        public void Dispose()
        {
            ResetTarget();
            if (gpuRouting != null) { gpuRouting.Dispose(); gpuRouting = null; }
            if (hardware != null) { hardware.Dispose(); hardware = null; }
            if (hardwareProvider != null) { hardwareProvider.Dispose(); hardwareProvider = null; }
        }
    }

}
