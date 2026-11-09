using System;
using System.Diagnostics;

namespace PaviseApp
{
    // 显示信息与控制身份分开；缺少驱动绑定既不能隐藏 GPU，也不能授予控制权限。
    internal sealed class OverviewGpuReader
    {
        private static readonly OverviewGpuReader shared = new OverviewGpuReader(
            GpuInventory.Primary, OverviewGpuProbe.Read, Stopwatch.GetTimestamp, Stopwatch.Frequency * 30);

        private readonly object gate = new object();
        private readonly Func<GpuAdapter> readInventory;
        private readonly Func<OverviewGpuInfo> readFallback;
        private readonly Func<long> clock;
        private readonly long retryTicks;
        private OverviewGpuInfo cached;
        private bool attempted;
        private long nextAttempt;
        private long failureCycle = -1;
        private int inventoryFailures, fallbackFailures;

        internal OverviewGpuReader(Func<GpuAdapter> readInventory, Func<OverviewGpuInfo> readFallback,
            Func<long> clock, long retryTicks)
        {
            this.readInventory = readInventory;
            this.readFallback = readFallback;
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
                    Logger.Warn("概览 GPU 清单读取失败，阶段=" + cycle + " 尝试=" + inventoryFailures + "/3: " + error.GetType().Name);
                }
                if (cached == null && fallbackFailures < 3)
                {
                    try
                    {
                        OverviewGpuInfo fallback = readFallback();
                        if (fallback != null && !string.IsNullOrWhiteSpace(fallback.Name))
                            cached = new OverviewGpuInfo(fallback.Name.Trim(), fallback.MemoryBytes);
                    }
                    catch (Exception error)
                    {
                        fallbackFailures++;
                        Logger.Warn("概览 GPU 备用读取失败，阶段=" + cycle + " 尝试=" + fallbackFailures + "/3: " + error.GetType().Name);
                    }
                }
                attempted = true;
                // 失败后仅重试 GPU 读取，不重复探测 CPU 拓扑或内存 WMI。
                nextAttempt = clock() + retryTicks;
                return cached;
            }
        }
    }
}
