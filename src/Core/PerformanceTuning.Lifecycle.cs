using System;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal static partial class PerformanceTuning
    {
        internal static volatile bool EnvironmentWorkClosing;
#if PAVISE_SELFTEST
        internal static Func<WaitCallback, bool> EnvironmentQueueForTest;
#endif

        internal static bool QueueEnvironmentWork(WaitCallback work)
        {
#if PAVISE_SELFTEST
            if (EnvironmentQueueForTest != null) return EnvironmentQueueForTest(work);
#endif
            return ThreadPool.QueueUserWorkItem(work);
        }

        // 先关闭准入再排空，超时保留写入和收据。
        internal static bool CloseEnvironmentWork(int timeoutMs)
        {
            if (timeoutMs < 0) return false;
            EnvironmentWorkClosing = true;
            var elapsed = Stopwatch.StartNew();
            bool entered = false;
            try
            {
                entered = Monitor.TryEnter(gate, timeoutMs);
                if (!entered) return false;
                while (busy.Count > 0)
                {
                    int remaining = (int)Math.Max(0L, timeoutMs - elapsed.ElapsedMilliseconds);
                    if (remaining <= 0) return false;
                    Monitor.Wait(gate, remaining);
                }
                return true;
            }
            finally { if (entered) Monitor.Exit(gate); }
        }
    }
}
