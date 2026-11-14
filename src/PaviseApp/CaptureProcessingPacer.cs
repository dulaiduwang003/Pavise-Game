using System;

namespace PaviseApp
{
    // 只在 ETW 消费者退出后的摘要阶段让出执行机会；继续处理原样本，不重新采集或消耗新次数。
    internal sealed class CaptureProcessingPacer
    {
        internal const double SliceCpuMs = 8;
        private readonly Func<double> cpuClock, wallClock;
        private readonly Action pause;
        private double lastCpu, lastWall;

        internal CaptureProcessingPacer(Func<double> cpuClock, Func<double> wallClock, Action pause)
        {
            this.cpuClock = cpuClock; this.wallClock = wallClock; this.pause = pause;
            lastCpu = cpuClock(); lastWall = wallClock();
        }

        internal void Continue()
        {
            double cpu = cpuClock(), wall = wallClock();
            // 自身 CPU 计时不影响证据有效性；读数未知或倒退时，改用单调墙钟安排处理节奏。
            double elapsed = Known(cpu) && Known(lastCpu) && cpu >= lastCpu ? cpu - lastCpu
                : Known(wall) && Known(lastWall) && wall >= lastWall ? wall - lastWall : SliceCpuMs;
            if (elapsed < SliceCpuMs) return;
            pause();
            lastCpu = cpuClock(); lastWall = wallClock();
        }

        private static bool Known(double value)
        { return !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0; }
    }
}
