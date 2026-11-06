using System;
using System.Globalization;
using System.Management;
using System.Threading;

namespace PaviseApp
{
    internal static class DeliveryOptimizationActivity
    {
        private static readonly object sync = new object();
        private static readonly long interval = TimeSpan.FromSeconds(10).Ticks;
        private static long nextProbe, queuedAt, observedAt;
        private static bool probing, downloading;
        private static long failureCycle = -1;
        private static int readFailures, queueFailures;
        private static bool observationUnknown;

#if PAVISE_SELFTEST
        internal static Func<long> ClockForTest;
        internal static Func<bool> ReadForTest;
        internal static Action<Action> QueueForTest;
        internal static void ResetForTest()
        {
            lock (sync) { nextProbe = queuedAt = observedAt = 0; probing = downloading = observationUnknown = false;
                failureCycle = -1; readFailures = queueFailures = 0; }
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
                if (cycle > failureCycle) { failureCycle = cycle; readFailures = queueFailures = 0; nextProbe = 0; }
                current = CurrentLocked(now);
                if (probing || readFailures >= 3 || queueFailures >= 3 || (now >= queuedAt && now < nextProbe)) return current;
                probing = true;
                queuedAt = now;
                nextProbe = now + interval;
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
                        if (failure == null) { downloading = active; observedAt = Now(); observationUnknown = false; }
                        else
                        {
                            observationUnknown = true;
                            if (cycle == failureCycle)
                            {
                                readFailures++;
                                Logger.Warn("Delivery activity read failed phase=" + cycle + " attempt=" + readFailures + "/3: " + failure.GetType().Name);
                            }
                        }
                        nextProbe = Now() + interval;
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
                    probing = false; observationUnknown = true; nextProbe = Now() + interval;
                    if (cycle == failureCycle)
                    {
                        queueFailures++;
                        Logger.Warn("Delivery activity queue failed phase=" + cycle + " attempt=" + queueFailures + "/3: " + error.GetType().Name);
                    }
                }
            }
            lock (sync) return CurrentLocked(Now());
        }
        private static bool CurrentLocked(long now)
        { return downloading && now >= observedAt && (observationUnknown || now - observedAt < interval); }

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
                // Priority is synthesized by Get-DeliveryOptimizationStatus; the provider exposes IsBackground.
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
