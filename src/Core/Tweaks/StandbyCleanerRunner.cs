using System;
using System.Threading;

namespace PaviseApp
{
    internal sealed class StandbyCleanerRunner
    {
        private sealed class Request
        {
            internal readonly int Generation;
            internal readonly StandbyCleanerOptions Options;
            internal readonly Func<bool> MayContinue;

            internal Request(int generation, StandbyCleanerOptions options, Func<bool> mayContinue)
            {
                Generation = generation;
                Options = options;
                MayContinue = mayContinue;
            }
        }

        // 清理成功后强制冷却，代次切换不清除冷却。
        internal const int PurgeCooldownFloorMilliseconds = 60000;
        internal const int PurgeCooldownIntervals = 8;

        private readonly object gate = new object();
        private readonly object operationGate = new object();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly StandbyCleanerEngine engine;
        private readonly Action<int, StandbyCleanerResult, int> onFault;
        private readonly Func<long> clock;
        private volatile Request current;
        private volatile bool closing;
        private bool closed;
        private bool hasFaultedGeneration;
        private int faultedGeneration;
        private int inFlight;
        private Thread worker;
        private bool purgeCooldownArmed;
        private long lastPurgeMilliseconds;

        internal StandbyCleanerRunner(StandbyCleanerEngine engine,
            Action<int, StandbyCleanerResult, int> onFault, Func<long> clock = null)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            this.engine = engine;
            this.onFault = onFault;
            this.clock = clock ?? MonotonicMilliseconds;
        }

        private static long MonotonicMilliseconds()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp()
                * (1000.0 / System.Diagnostics.Stopwatch.Frequency));
        }

        internal static long CooldownMilliseconds(StandbyCleanerOptions options)
        {
            return Math.Max((long)PurgeCooldownFloorMilliseconds,
                (long)options.PollingMilliseconds * PurgeCooldownIntervals);
        }

        // 配置改变才更新代次，普通巡检不重启倒计时。
        internal bool Update(int generation, StandbyCleanerOptions options, Func<bool> mayContinue)
        {
            lock (gate)
            {
                if (closing) return false;
                if (options == null || !options.IsValid)
                {
                    current = null;
                    wake.Set();
                    return false;
                }
                if (ActionFailureStore.IsBlocked(PolicyCatalog.KeyStandbyCleaner, "system")) return false;
                if (hasFaultedGeneration && faultedGeneration == generation && !ActionFailureStore.IsRetryPending(PolicyCatalog.KeyStandbyCleaner, "system")) return false;
                Request previous = current;
                if (previous != null && previous.Generation == generation
                    && SameOptions(previous.Options, options)) return true;
                hasFaultedGeneration = false;
                current = new Request(generation, options, mayContinue);
                if (worker == null)
                {
                    var next = new Thread(Loop);
                    next.IsBackground = true;
                    next.Name = "Pavise.StandbyCleaner";
                    worker = next;
                    try { next.Start(); }
                    catch
                    {
                        worker = null;
                        current = null;
                        return false;
                    }
                }
                wake.Set();
                return true;
            }
        }

        private static bool SameOptions(StandbyCleanerOptions left, StandbyCleanerOptions right)
        {
            return left.ListMegabytes == right.ListMegabytes
                && left.FreeMegabytes == right.FreeMegabytes
                && left.PollingMilliseconds == right.PollingMilliseconds;
        }

        internal void Pause()
        {
            lock (gate)
            {
                current = null;
                if (!closed) wake.Set();
            }
        }

        private bool Admitted(Request request)
        {
            return !closing && ReferenceEquals(current, request)
                && StandbyCleanerEngine.MayContinue(request.MayContinue);
        }

        private void Loop()
        {
            while (!closing)
            {
                Request request = current;
                if (request == null)
                {
                    wake.WaitOne();
                    continue;
                }
                // 慢调用结束后重新等待完整间隔，不补跑漏掉的轮次。
                if (wake.WaitOne(request.Options.PollingMilliseconds)) continue;
                if (!Admitted(request))
                {
                    lock (gate)
                        if (ReferenceEquals(current, request)) current = null;
                    continue;
                }
                if (purgeCooldownArmed
                    && clock() - lastPurgeMilliseconds < CooldownMilliseconds(request.Options))
                    continue;

                StandbyCleanerResult result;
                int status;
                bool admittedPurge = false;
                if (ActionFailureStore.IsBlocked(PolicyCatalog.KeyStandbyCleaner, "system"))
                { lock (gate) if (ReferenceEquals(current, request)) current = null; continue; }
                lock (operationGate)
                {
                    Interlocked.Exchange(ref inFlight, 1);
                    try
                    {
                        StandbyMemorySnapshot snapshot;
                        result = engine.Poll(request.Options,
                            delegate { return Admitted(request); }, out snapshot, out status,
                            delegate
                            {
                                string blocked;
                                admittedPurge = Admitted(request) && ActionFailureStore.TryBeginApply(PolicyCatalog.KeyStandbyCleaner, "system", out blocked);
                                return admittedPurge;
                            });
                    }
                    catch
                    {
                        result = StandbyCleanerResult.QueryFailed;
                        status = StandbyCleanerEngine.StatusUnsuccessful;
                    }
                    finally { Interlocked.Exchange(ref inFlight, 0); }
                }
                if (result == StandbyCleanerResult.Purged && status == 0)
                {
                    purgeCooldownArmed = true;
                    lastPurgeMilliseconds = clock();
                    if (!ActionFailureStore.CompleteApply(PolicyCatalog.KeyStandbyCleaner, "system"))
                    { result = StandbyCleanerResult.PurgeFailed; status = StandbyCleanerEngine.StatusUnsuccessful; }
                }
                else if (result == StandbyCleanerResult.Cancelled || result == StandbyCleanerResult.BelowThreshold
                    || result == StandbyCleanerResult.InvalidOptions)
                { if (admittedPurge) ActionFailureStore.CancelApply(PolicyCatalog.KeyStandbyCleaner, "system"); }
                else ActionFailureStore.Trip(PolicyCatalog.KeyStandbyCleaner, "system", result + " | 0x" + status.ToString("X8"));

                bool fault = false;
                lock (gate)
                {
                    if (closing || !ReferenceEquals(current, request)) continue;
                    if (result == StandbyCleanerResult.Cancelled) current = null;
                    else if (result == StandbyCleanerResult.QueryFailed
                        || result == StandbyCleanerResult.PurgeFailed
                        || (result == StandbyCleanerResult.Purged && status != 0))
                    {
                        current = null;
                        hasFaultedGeneration = true;
                        faultedGeneration = request.Generation;
                        fault = true;
                    }
                    else if (result == StandbyCleanerResult.InvalidOptions) current = null;
                }
                if (fault && onFault != null)
                    try { onFault(request.Generation, result, status); } catch { }
            }
        }

        internal bool HasInFlight { get { return Volatile.Read(ref inFlight) != 0; } }

        // 暂停只撤销未获准请求，系统不支持取消进行中的待机页清理。
        internal bool Drain(int timeoutMs)
        {
            if (timeoutMs < 0 || Monitor.IsEntered(operationGate)
                || !Monitor.TryEnter(operationGate, timeoutMs)) return false;
            Monitor.Exit(operationGate);
            return true;
        }

        internal bool Close(int timeoutMs)
        {
            Thread pending;
            lock (gate)
            {
                if (closed) return true;
                closing = true;
                current = null;
                wake.Set();
                pending = worker;
            }
            // 排空超时后保留线程句柄和事件，供后续关闭重试。
            if (timeoutMs < 0 || pending == Thread.CurrentThread
                || (pending != null && !pending.Join(timeoutMs))) return false;
            lock (gate)
            {
                if (!closed)
                {
                    closed = true;
                    worker = null;
                    wake.Close();
                }
            }
            return true;
        }

#if PAVISE_SELFTEST
        internal bool HasWorkerForTest { get { lock (gate) return worker != null; } }
#endif
    }
}
