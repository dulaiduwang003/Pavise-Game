using System;

namespace PaviseApp
{
    internal sealed class MonitoringSnapshot
    {
        internal readonly int Pid;
        internal readonly int ViewportWidth, ViewportHeight;
        internal readonly long Creation;
        internal readonly string TargetName, Status;
        internal readonly string GpuAdapterName;
        internal readonly DateTime TimeUtc;
        internal readonly bool FrameTimeFromGpu;
        internal readonly MonitoringMetrics RetainedHardwareMetrics;
        internal readonly double? Fps, FrameTimeMs, Low1, CpuUsage, GpuUsage,
            GpuTemperature, GpuClock, GpuPower, RamMiB, VramMiB, AverageFps, Low01,
            FrameTimeDeviation, GameRamMiB, RamUsage, VramUsage, GpuMemoryClock,
            GpuFanRpm, GpuFanPercent, CpuPower, CpuTemperature;
        internal MonitoringSnapshot(int pid, string targetName, DateTime timeUtc, string status,
            double? fps = null, double? frameTimeMs = null, double? low1 = null, double? cpuUsage = null,
            double? gpuUsage = null, double? gpuTemperature = null, double? gpuClock = null,
            double? gpuPower = null, double? ramMiB = null, double? vramMiB = null,
            double? averageFps = null, double? low01 = null, double? frameTimeDeviation = null,
            double? gameRamMiB = null, double? ramUsage = null, double? vramUsage = null,
            double? gpuMemoryClock = null, double? gpuFanRpm = null, double? gpuFanPercent = null,
            double? cpuPower = null, double? cpuTemperature = null, long creation = 0,
            bool frameTimeFromGpu = false, MonitoringMetrics retainedHardwareMetrics = MonitoringMetrics.None,
            string gpuAdapterName = "", int viewportWidth = 0, int viewportHeight = 0)
        {
            Pid = pid; Creation = creation; TargetName = targetName ?? ""; TimeUtc = timeUtc; Status = status ?? "";
            Fps = fps; FrameTimeMs = frameTimeMs; FrameTimeFromGpu = frameTimeFromGpu; Low1 = low1; CpuUsage = cpuUsage;
            GpuUsage = gpuUsage; GpuTemperature = gpuTemperature; GpuClock = gpuClock;
            GpuPower = gpuPower; RamMiB = ramMiB; VramMiB = vramMiB;
            AverageFps = averageFps; Low01 = low01; FrameTimeDeviation = frameTimeDeviation;
            GameRamMiB = gameRamMiB; RamUsage = ramUsage; VramUsage = vramUsage;
            GpuMemoryClock = gpuMemoryClock; GpuFanRpm = gpuFanRpm; GpuFanPercent = gpuFanPercent;
            CpuPower = cpuPower; CpuTemperature = cpuTemperature;
            RetainedHardwareMetrics = retainedHardwareMetrics;
            GpuAdapterName = gpuAdapterName ?? "";
            ViewportWidth = viewportWidth; ViewportHeight = viewportHeight;
        }
    }

    internal struct MonitoringFrameStatistics
    {
        internal readonly double? FrameTimeDeviation, AverageFps, Low1, Low01;
        internal MonitoringFrameStatistics(double? frameTimeDeviation, double? averageFps = null,
            double? low1 = null, double? low01 = null)
        { FrameTimeDeviation = frameTimeDeviation; AverageFps = averageFps; Low1 = low1; Low01 = low01; }
    }

    // 统计只接收逐帧间隔，不能使用每秒帧率样本。
    internal sealed class MonitoringFrameWindow
    {
        internal const int Capacity = 6000, MinimumFrames = 300;
        private readonly double[] frames = new double[Capacity];
        private double[] sorted;
        private bool sortDirty = true;
        private int next, count;
        private long sequence = -1;
        private double duration, squares;
        internal int Count { get { return count; } }
        internal void Reset() { next = count = 0; sequence = -1; duration = squares = 0; sortDirty = true; }
        internal void Add(double[] values, long frameSequence)
        {
            if (values == null || values.Length == 0 || frameSequence <= sequence) return;
            // 无效批次整体拒绝，避免漏掉卡顿帧抬高 Low 值。
            for (int i = 0; i < values.Length; i++)
                if (!Finite(values[i]) || values[i] <= 0 || values[i] > 10000)
                { Reset(); sequence = frameSequence; return; }
            sequence = frameSequence;
            int first = Math.Max(0, values.Length - Capacity);
            for (int i = first; i < values.Length; i++)
            {
                double value = values[i];
                if (count == Capacity)
                { double old = frames[next]; duration -= old; squares -= old * old; }
                else count++;
                frames[next] = value; duration += value; squares += value * value;
                sortDirty = true;
                next = (next + 1) % Capacity;
            }
        }
        internal MonitoringFrameStatistics Compute(MonitoringMetrics selected)
        {
            if ((selected & MonitoringMetrics.FrameStatistics) == 0
                || count < MinimumFrames || duration < 3000) return new MonitoringFrameStatistics();
            double mean = duration / count;
            double? deviation = (selected & MonitoringMetrics.FrameTimeDeviation) != 0
                ? (double?)Math.Sqrt(Math.Max(0, squares / count - mean * mean)) : null;
            double? average = (selected & MonitoringMetrics.AverageFps) != 0 ? (double?)(1000 / mean) : null;
            double? low1 = null, low01 = null;
            if ((selected & (MonitoringMetrics.Low1 | MonitoringMetrics.Low01)) != 0)
            {
                if (sorted == null) sorted = new double[Capacity];
                if (sortDirty)
                {
                    Array.Copy(frames, sorted, count);
                    Array.Sort(sorted, 0, count);
                    sortDirty = false;
                }
                if ((selected & MonitoringMetrics.Low1) != 0) low1 = Low(100);
                if ((selected & MonitoringMetrics.Low01) != 0 && count >= 1000) low01 = Low(1000);
            }
            return new MonitoringFrameStatistics(deviation, average, low1, low01);
        }
        private double Low(int denominator)
        {
            // 最近秩分位数：下标为 N - floor(N / d) - 1。
            int index = count - count / denominator - 1;
            return 1000.0 / sorted[index];
        }
        internal static bool Finite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
        internal static double? Valid(double? value, double minimum, double maximum)
        { return value.HasValue && Finite(value.Value) && value.Value >= minimum && value.Value <= maximum ? value : null; }
    }
}
