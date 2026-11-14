using System;

namespace PaviseApp
{
    // 进程负载持续高于初始低负载时，允许使用剩余采集额度重判；不枚举线程。
    internal sealed class SchedulingLoadWatch
    {
        // 采用相对增幅与绝对增量中较高的阈值，连续采样排除加载期短峰值。
        internal const double RiseFactor = 1.5, RiseFloorCores = 0.5;
        internal const int SustainedSamples = 4;
        // 间隔过短或过长的读数不构成连续负载证据。
        internal const int MinimumGapSeconds = 5, MaximumGapSeconds = 60;
        private long lastCpu = -1, lastTicks;
        private double lowest = -1, latest = -1;
        private int streak;

        internal double LowestCores { get { return lowest; } }
        internal double LatestCores { get { return latest; } }
        internal int Streak { get { return streak; } }

        // CPU 时间与时刻均以 100 纳秒计；未知或非前台读数中断连续计数。
        internal bool Observe(bool known, long processCpu, long now)
        {
            if (!known || processCpu < 0)
            {
                lastCpu = -1; streak = 0;
                return false;
            }
            long previousCpu = lastCpu, previousTicks = lastTicks;
            lastCpu = processCpu; lastTicks = now;
            if (previousCpu < 0) return false;
            long elapsed = now - previousTicks;
            if (elapsed < MinimumGapSeconds * TimeSpan.TicksPerSecond || elapsed > MaximumGapSeconds * TimeSpan.TicksPerSecond
                || processCpu < previousCpu)
            {
                streak = 0;
                return false;
            }
            double cores = (processCpu - previousCpu) / (double)elapsed;
            latest = cores;
            if (lowest < 0) { lowest = cores; return false; }
            if (cores >= Math.Max(lowest * RiseFactor, lowest + RiseFloorCores)) streak++;
            else
            {
                streak = 0;
                if (cores < lowest) lowest = cores;
            }
            return streak >= SustainedSamples;
        }

        internal string Describe()
        {
            if (lowest < 0) return "负载观察：等待读数";
            return "负载观察：最低" + lowest.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "核 当前"
                + latest.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) + "核 连续升高" + streak + "/" + SustainedSamples;
        }
    }
}
