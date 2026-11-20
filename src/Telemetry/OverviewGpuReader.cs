using System;
using System.Diagnostics;

namespace PaviseApp
{
    // 显示信息与控制身份分开 缺少驱动绑定既不能隐藏 GPU 也不能授予控制权限
    internal sealed class OverviewGpuReader
    {
        private static readonly OverviewGpuReader shared = new OverviewGpuReader(
            GpuInventory.Primary, OverviewGpuProbe.Read, Stopwatch.GetTimestamp, Stopwatch.Frequency * 30, OverviewGpuProbe.ReadResult);

        private readonly object gate = new object();
        private readonly Func<GpuAdapter> readInventory;
        private readonly Func<OverviewGpuInfo> readFallback;
        private readonly Func<OverviewGpuProbeResult> readClassifiedFallback;
        private readonly Func<long> clock;
        private readonly long retryTicks;
        private OverviewGpuInfo cached;
        private bool attempted;
        private long nextAttempt;
        private long failureCycle = -1;
        private int inventoryFailures, fallbackFailures;

        internal OverviewGpuReader(Func<GpuAdapter> readInventory, Func<OverviewGpuInfo> readFallback,
            Func<long> clock, long retryTicks)
            : this(readInventory, readFallback, clock, retryTicks, null) { }

        internal OverviewGpuReader(Func<GpuAdapter> readInventory, Func<OverviewGpuInfo> readFallback,
            Func<long> clock, long retryTicks, Func<OverviewGpuProbeResult> readClassifiedFallback)
        {
            this.readInventory = readInventory;
            this.readFallback = readFallback;
            this.readClassifiedFallback = readClassifiedFallback;
            this.clock = clock;
            this.retryTicks = retryTicks;
        }

        internal static OverviewGpuInfo Read() { return shared.ReadCached(); }

        internal OverviewGpuInfo ReadCached()
        {
            lock (gate)
            {
                if (cached != null) return cached;
                long cycle = PerformanceTuning.RecoveryCycleSerial;
                if (cycle > failureCycle)
                { failureCycle = cycle; inventoryFailures = fallbackFailures = 0; attempted = false; }
                if (attempted && clock() < nextAttempt) return null;
                if (inventoryFailures < 3) try
                {
                    GpuAdapter adapter = readInventory();
                    if (adapter != null && !string.IsNullOrWhiteSpace(adapter.Name))
                        cached = new OverviewGpuInfo(adapter.Name.Trim(),
                            adapter.VideoMemoryBytes > 0 ? (ulong)adapter.VideoMemoryBytes : 0);
                }
                catch (Exception error)
                {
                    inventoryFailures++;
                    Logger.Warn(Logger.Format("概览 GPU 清单读取失败，阶段={0} 尝试={1}/3: {2}", "Overview GPU inventory read failed phase={0} attempt={1}/3: {2}", cycle, inventoryFailures, error.GetType().Name));
                }
                if (cached == null && fallbackFailures < 3)
                {
                    string failure = null;
                    try
                    {
                        OverviewGpuProbeResult observation = readClassifiedFallback == null ? null : readClassifiedFallback();
                        OverviewGpuInfo fallback = readClassifiedFallback == null ? readFallback() : observation == null ? null : observation.Info;
                        if (fallback != null && !string.IsNullOrWhiteSpace(fallback.Name))
                            cached = new OverviewGpuInfo(fallback.Name.Trim(), fallback.MemoryBytes);
                        else if (observation != null && observation.State == OverviewGpuProbeState.NativeFailure)
                            failure = observation.Failure;
                    }
                    catch (Exception error)
                    { failure = error.GetType().Name; }
                    // 日志失败不属于第二次设备读取失败 扣次数与日志调用放在同一异常捕获范围之外
                    if (failure != null)
                    {
                        fallbackFailures++;
                        Logger.Warn(Logger.Format("概览 GPU 备用读取失败，阶段={0} 尝试={1}/3: {2}", "Overview GPU fallback read failed phase={0} attempt={1}/3: {2}", cycle, fallbackFailures, failure));
                    }
                }
                attempted = true;
                // 失败后仅重试 GPU 读取 不重复探测 CPU 拓扑或内存 WMI
                nextAttempt = clock() + retryTicks;
                return cached;
            }
        }
    }
}
