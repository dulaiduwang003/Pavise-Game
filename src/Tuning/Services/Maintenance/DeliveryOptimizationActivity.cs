using System;
using System.Globalization;
using System.Management;
using System.Threading;

namespace PaviseApp
{
    // 每局只确认一次是否有后台下载 查询经 WMI 代价不在本进程 对局中不反复轮询
    // 结果在本局内保持到下一局 读取或入队失败仍按每局三次的预算间隔重试
    internal static class DeliveryOptimizationActivity
    {
        private static readonly object sync = new object();
        private static readonly long interval = TimeSpan.FromSeconds(10).Ticks;
        private static long nextProbe, queuedAt, observedAt;
        private static bool probing, downloading;
        private static long failureCycle = -1;
        private static int readFailures, queueFailures;
        private static long observedCycle = -1;

#if PAVISE_SELFTEST
        internal static Func<long> ClockForTest;
        internal static Func<bool> ReadForTest;
        internal static Action<Action> QueueForTest;
        internal static void ResetForTest()
        {
            lock (sync) { nextProbe = queuedAt = observedAt = 0; probing = downloading = false;
                failureCycle = -1; observedCycle = -1; readFailures = queueFailures = 0; }
            ClockForTest = null; ReadForTest = null; QueueForTest = null;
        }
#endif

        private static long Now()
        {
#if PAVISE_SELFTEST
            if (ClockForTest != null) return ClockForTest();
#endif
            return DateTime.UtcNow.Ticks;
        }

        internal static bool IsDownloading()
        {
            long now = Now();
            long cycle = PerformanceTuning.RecoveryCycleSerial;
            bool current;
            lock (sync)
            {
                if (cycle > failureCycle)
                {
                    failureCycle = cycle; readFailures = queueFailures = 0; nextProbe = 0;
                    downloading = false; observedCycle = -1;
                }
                current = CurrentLocked(cycle);
                if (probing || observedCycle == cycle || readFailures >= 3 || queueFailures >= 3 || (now >= queuedAt && now < nextProbe)) return current;
                probing = true;
                queuedAt = now;
                nextProbe = long.MaxValue;
            }
            try
            {
                Action read = delegate
                {
                    bool active = false;
                    Exception failure = null;
                    try
                    {
#if PAVISE_SELFTEST
                        active = ReadForTest != null ? ReadForTest() : ReadActivity();
#else
                        active = ReadActivity();
#endif
                    }
                    catch (Exception error) { failure = error; }
                    lock (sync)
                    {
                        if (failure == null) { downloading = active; observedAt = Now(); observedCycle = cycle; }
                        else
                        {
                            if (cycle == failureCycle)
                            {
                                readFailures++;
                                Logger.Warn(Logger.Format("传递优化活动读取失败，阶段={0} 尝试={1}/3: {2}", "Delivery activity read failed phase={0} attempt={1}/3: {2}", cycle, readFailures, failure.GetType().Name));
                            }
                        }
                        if (failure != null) nextProbe = Now() + interval;
                        probing = false;
                    }
                };
#if PAVISE_SELFTEST
                if (QueueForTest != null) QueueForTest(read);
                else
#endif
                if (!ThreadPool.QueueUserWorkItem(delegate { read(); })) throw new InvalidOperationException("Activity query could not be queued.");
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    probing = false; nextProbe = Now() + interval;
                    if (cycle == failureCycle)
                    {
                        queueFailures++;
                        Logger.Warn(Logger.Format("传递优化活动入队失败，阶段={0} 尝试={1}/3: {2}", "Delivery activity queue failed phase={0} attempt={1}/3: {2}", cycle, queueFailures, error.GetType().Name));
                    }
                }
            }
            lock (sync) return CurrentLocked(cycle);
        }
        private static bool CurrentLocked(long cycle)
        { return downloading && observedCycle == cycle; }

        internal static bool IsBackgroundDownload(object status, object isBackground)
        {
            uint state;
            return status != null && isBackground is bool && (bool)isBackground
                && uint.TryParse(Convert.ToString(status, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out state)
                && state == 0;
        }

        private static bool ReadActivity()
        {
            var options = new EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(1) };
            using (var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\Microsoft\Windows\DeliveryOptimization"),
                // Priority 由 Get-DeliveryOptimizationStatus 合成 提供程序实际暴露的是 IsBackground
                new ObjectQuery("SELECT Status, IsBackground FROM MSFT_DeliveryOptimizationFile WHERE Status = 0 AND IsBackground = TRUE"), options))
            using (var results = searcher.Get())
            {
                foreach (ManagementObject file in results)
                    using (file) if (IsBackgroundDownload(file["Status"], file["IsBackground"])) return true;
            }
            return false;
        }
    }
}
