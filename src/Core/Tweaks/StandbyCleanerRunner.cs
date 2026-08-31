// 文件用途 待机列表清理的会话内调度 不重叠也不补发漏掉的轮询
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
            internal int Failures;

            internal Request(int generation, StandbyCleanerOptions options, Func<bool> mayContinue)
            {
                Generation = generation;
                Options = options;
                MayContinue = mayContinue;
            }
        }

        // 成功清理后的强制冷却 清理调用会让游戏卡一下且无法中断 列表被系统
        //   工作集撑住或缓存被高速回填时 不允许逐轮连清 失败的清理不进入
        //   冷却 保留"连续失败尽快熔断"的既有语义 冷却跨代数保留
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
        // 仅工作线程读写
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

        // 每次意图或配置变化 调用方都要给一个新的代号
        // 反复的游戏扫描不能重启倒计时 也不能让故障复活
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
                if (hasFaultedGeneration && faultedGeneration == generation) return false;
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
                // 每次轮询或变更之后都等满一个间隔 慢的原生调用
                // 既不会排队补发漏掉的 tick 也不会和另一次并发
                if (wake.WaitOne(request.Options.PollingMilliseconds)) continue;
                if (!Admitted(request))
                {
                    lock (gate)
                        if (ReferenceEquals(current, request)) current = null;
                    continue;
                }
                // 冷却期内整轮跳过 不查询也不清理 到期后按正常节奏继续
                if (purgeCooldownArmed
                    && clock() - lastPurgeMilliseconds < CooldownMilliseconds(request.Options))
                    continue;

                StandbyCleanerResult result;
                int status;
                lock (operationGate)
                {
                    Interlocked.Exchange(ref inFlight, 1);
                    try
                    {
                        StandbyMemorySnapshot snapshot;
                        result = engine.Poll(request.Options,
                            delegate { return Admitted(request); }, out snapshot, out status);
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
                }

                bool fault = false;
                lock (gate)
                {
                    if (closing || !ReferenceEquals(current, request)) continue;
                    if (result == StandbyCleanerResult.Cancelled) current = null;
                    else if (result == StandbyCleanerResult.QueryFailed
                        || result == StandbyCleanerResult.PurgeFailed
                        || result == StandbyCleanerResult.InvalidOptions
                        || (result == StandbyCleanerResult.Purged && status != 0))
                    {
                        request.Failures++;
                        if (request.Failures >= 2 || result == StandbyCleanerResult.InvalidOptions
                            || result == StandbyCleanerResult.Purged)
                        {
                            current = null;
                            hasFaultedGeneration = true;
                            faultedGeneration = request.Generation;
                            fault = true;
                        }
                    }
                    else request.Failures = 0;
                }
                // 回调进 GameMode 或者设置时 两道闸都不许持有
                if (fault && onFault != null)
                    try { onFault(request.Generation, result, status); } catch { }
            }
        }

        internal bool HasInFlight { get { return Volatile.Read(ref inFlight) != 0; } }

        // 暂停会撤销待准入的请求 这里排干的是已经进去的那次调用
        // Windows 没给正在进行的待机清理提供取消手段
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
            // 超时或者自连接时 句柄和事件都留着 后面的 Close 必须
            // 仍然能观察到并排干同一个工作线程 重置才允许删数据
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
