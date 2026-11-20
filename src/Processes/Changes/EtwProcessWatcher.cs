using System;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;

namespace PaviseApp
{
    internal sealed class EtwProcessWatcher : IDisposable
    {
        private const ulong KeywordProcess = 16uL;

        private const ushort EventIdProcessStart = 1;

        private static readonly Guid KernelProcessProvider = new Guid("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");

        private long eventsSeen;

        public ulong Keywords = KeywordProcess;

        private readonly string sessionName;

        private ulong session;

        private ulong trace = ulong.MaxValue;

        private IntPtr propsBuffer = IntPtr.Zero;

        private Thread pump;

        private Native.EventRecordCallback callback;

        private volatile bool stopping;
        private readonly object lifecycleSync = new object();
        private Timer cleanupTimer;
        private bool startAttempted, pumpStarted, pumpCompleted, consumerCloseAccepted, sessionStopAccepted, drainWarningReported;
        private int consumerCloseFailures, sessionStopFailures;
        private long consumerCloseNext, sessionStopNext;
        private const int CleanupAttempts = 3, DisposeWaitMs = 3000;
        private const int ClosePending = 7007, InvalidHandle = 6, SessionNotFound = 4201;

        internal bool IsStopped
        {
            get
            {
                // 正在清理不等于已停止 状态查询不能反过来等待慢原生调用
                if (!Monitor.TryEnter(lifecycleSync, 0)) return false;
                try { return IsStoppedLocked(); }
                finally { Monitor.Exit(lifecycleSync); }
            }
        }

        private bool IsStoppedLocked()
        { return stopping && (!pumpStarted || pumpCompleted) && trace == ulong.MaxValue && session == 0 && propsBuffer == IntPtr.Zero; }

        private static long NowMilliseconds()
        { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }

        public long EventsSeen
        {
            get
            {
                return Interlocked.Read(ref eventsSeen);
            }
        }

        public string LastError { get; private set; }

        public event Action<int, long> ProcessStarted;

        public EtwProcessWatcher(string name)
        {
            sessionName = (string.IsNullOrEmpty(name) ? "Pavise.ProcWatch" : name);
        }

        public bool Start()
        {
            lock (lifecycleSync)
            {
                // 一个对象只启动一次 未结清的消费者不能用重建本对象补充清理预算
                if (startAttempted || stopping) return false;
                startAttempted = true;
                try { return StartCore(); }
                catch (Exception error)
                {
                    LastError = Logger.Text("进程事件监听启动失败：", "Process event listener start failed: ") + error.GetType().Name;
                    Cleanup();
                    return false;
                }
            }
        }

        private bool StartCore()
        {
            int sizeOfResult = Marshal.SizeOf(typeof(Native.EventTraceProperties));
            int startValue = sizeOfResult + (sessionName.Length + 1) * 2 + 2;
            if (!StopStaleSession(sizeOfResult, startValue)) { Cleanup(); return false; }
            propsBuffer = Marshal.AllocHGlobal(startValue);
            for (int i = 0; i < startValue; i++)
            {
                Marshal.WriteByte(propsBuffer, i, 0);
            }
            Native.EventTraceProperties structure = default(Native.EventTraceProperties);
            structure.WnodeBufferSize = (uint)startValue;
            structure.WnodeFlags = 131072u;
            structure.WnodeClientContext = 1u;
            structure.LogFileMode = 256u;
            structure.BufferSize = 4u;
            structure.MinimumBuffers = 2u;
            structure.MaximumBuffers = 8u;
            structure.FlushTimer = 1u;
            structure.LoggerNameOffset = (uint)sizeOfResult;
            Marshal.StructureToPtr(structure, propsBuffer, false);
            ulong startedSession;
            int startTraceResult = Native.StartTrace(out startedSession, sessionName, propsBuffer);
            if (startTraceResult != 0)
            {
                LastError = Lang.T("t.etwprocesswatcher.1") + startTraceResult;
                Cleanup();
                return false;
            }
            session = startedSession;
            Native.EnableTraceParameters enableParameters = default(Native.EnableTraceParameters);
            enableParameters.Version = 2u;
            Guid providerId = KernelProcessProvider;
            startTraceResult = Native.EnableTraceEx2(session, ref providerId, 1u, 4, Keywords, 0uL, 0u, ref enableParameters);
            if (startTraceResult != 0)
            {
                LastError = Lang.T("t.etwprocesswatcher.2") + startTraceResult;
                Cleanup();
                return false;
            }
            callback = OnEvent;
            Native.EventTraceLogfile logfile = default(Native.EventTraceLogfile);
            logfile.LoggerName = Marshal.StringToHGlobalUni(sessionName);
            logfile.ProcessTraceMode = 268435712u;
            logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback);
            logfile.LogfileHeader.TimeZone = new byte[172];
            try { trace = Native.OpenTrace(ref logfile); }
            finally { Marshal.FreeHGlobal(logfile.LoggerName); }
            if (trace == ulong.MaxValue)
            {
                LastError = Lang.T("t.etwprocesswatcher.3") + Marshal.GetLastWin32Error();
                Cleanup();
                return false;
            }
            pump = new Thread(Pump);
            pump.IsBackground = true;
            pump.Start();
            pumpStarted = true;
            return true;
        }

        private bool StopStaleSession(int structSize, int total)
        {
            IntPtr intPtr = Marshal.AllocHGlobal(total);
            try
            {
                for (int i = 0; i < total; i++)
                {
                    Marshal.WriteByte(intPtr, i, 0);
                }
                Native.EventTraceProperties structure = default(Native.EventTraceProperties);
                structure.WnodeBufferSize = (uint)total;
                structure.LoggerNameOffset = (uint)structSize;
                Marshal.StructureToPtr(structure, intPtr, false);
                int result = Native.ControlTrace(0uL, sessionName, intPtr, 1u);
                if (result == 0 || result == SessionNotFound) return true;
                LastError = Logger.Text("旧进程事件会话停止未确认：", "Old process event session stop unconfirmed: ") + result;
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(intPtr);
            }
        }

        private void Pump()
        {
            ulong[] handles = new ulong[1] { trace };
            try
            {
                Native.ProcessTrace(handles, 1u, IntPtr.Zero, IntPtr.Zero);
            }
            catch
            {
            }
            finally
            {
                lock (lifecycleSync)
                {
                    // ProcessTrace 返回才证明原生端不再调用委托 ClosePending 和 Join 超时都不能替代它
                    pumpCompleted = true;
                    stopping = true;
                    CleanupLocked();
                }
            }
        }

        private void OnEvent(ref Native.EventRecord record)
        {
            if (stopping)
            {
                return;
            }
            Interlocked.Increment(ref eventsSeen);
            if (record.EventHeader.EventDescriptor.Id == EventIdProcessStart && record.UserData != IntPtr.Zero && record.UserDataLength >= 4)
            {
                int arg;
                try
                {
                    arg = Marshal.ReadInt32(record.UserData);
                }
                catch
                {
                    return;
                }
                Action<int, long> processStarted = this.ProcessStarted;
                if (processStarted != null && !stopping)
                {
                    processStarted(arg, record.EventHeader.TimeStamp);
                }
            }
        }

        private void Cleanup()
        {
            lock (lifecycleSync) { stopping = true; CleanupLocked(); }
        }

        private void CleanupLocked()
        {
            long now = NowMilliseconds();
            if (trace != ulong.MaxValue && !consumerCloseAccepted && consumerCloseFailures < CleanupAttempts && now >= consumerCloseNext)
            {
                int result;
                try { result = Native.CloseTrace(trace); }
                catch { result = -1; }
                // 句柄不存在只在消费者已返回或从未启动时结清 不能据此释放仍在回调的委托
                if (result == 0 || result == ClosePending || result == InvalidHandle && (!pumpStarted || pumpCompleted))
                    consumerCloseAccepted = true;
                else
                {
                    consumerCloseFailures++;
                    consumerCloseNext = now + (1000L << (consumerCloseFailures - 1));
                    ReportCleanupFailure(Logger.Text("关闭消费者", "close consumer"), result, consumerCloseFailures);
                }
            }
            if (session != 0 && !sessionStopAccepted && sessionStopFailures < CleanupAttempts && now >= sessionStopNext)
            {
                int result;
                try { result = Native.ControlTrace(session, sessionName, propsBuffer, 1u); }
                catch { result = -1; }
                if (result == 0 || result == SessionNotFound) sessionStopAccepted = true;
                else
                {
                    sessionStopFailures++;
                    sessionStopNext = now + (1000L << (sessionStopFailures - 1));
                    ReportCleanupFailure(Logger.Text("停止会话", "stop session"), result, sessionStopFailures);
                }
            }
            if (!pumpStarted || pumpCompleted)
            {
                if (consumerCloseAccepted) trace = ulong.MaxValue;
                if (sessionStopAccepted) session = 0;
                if (trace == ulong.MaxValue && session == 0)
                {
                    if (propsBuffer != IntPtr.Zero) Marshal.FreeHGlobal(propsBuffer);
                    propsBuffer = IntPtr.Zero;
                    pump = null;
                    callback = null;
                }
            }
            ScheduleCleanupLocked(now);
        }

        private void ReportCleanupFailure(string stage, int error, int failures)
        {
            string detail = Logger.Text("进程事件监听清理未确认：阶段=", "Process event listener cleanup unconfirmed: phase=") + stage + Logger.Text(" 返回码=", " rc=") + error + Logger.Text(" 尝试=", " attempt=") + failures + "/" + CleanupAttempts
                + (failures >= CleanupAttempts ? Logger.Text("；停止主动重试，保留原句柄及委托", "; active retries stopped; original handle and delegate retained") : Logger.Text("；保留原归属，等待重试", "; original ownership retained, awaiting retry"));
            LastError = string.IsNullOrEmpty(LastError) ? detail : LastError + Logger.Text("；", "; ") + detail;
            Logger.Warn(detail);
        }

        private void ScheduleCleanupLocked(long now)
        {
            long due = long.MaxValue;
            if (trace != ulong.MaxValue && !consumerCloseAccepted && consumerCloseFailures < CleanupAttempts) due = consumerCloseNext;
            if (session != 0 && !sessionStopAccepted && sessionStopFailures < CleanupAttempts) due = Math.Min(due, sessionStopNext);
            if (due == long.MaxValue)
            {
                if (cleanupTimer != null) cleanupTimer.Dispose();
                cleanupTimer = null;
                return;
            }
            // 一只定时器只推进已有清理预算 消费者仍在排空但原生关闭已接受时 不额外轮询它
            if (cleanupTimer == null) cleanupTimer = new Timer(delegate { lock (lifecycleSync) CleanupLocked(); }, null, -1, -1);
            cleanupTimer.Change((int)Math.Min(int.MaxValue, Math.Max(1L, due - now)), -1);
        }

        public void Dispose()
        {
            // 先撤销通知 再等清理锁 另一个清理回调变慢时也不能继续送出新通知
            stopping = true;
            long deadline = NowMilliseconds() + DisposeWaitMs;
            Thread draining;
            if (!Monitor.TryEnter(lifecycleSync, DisposeWaitMs)) return;
            try
            {
                CleanupLocked();
                draining = pump;
            }
            finally { Monitor.Exit(lifecycleSync); }
            // 锁等待和 Join 共用等待额度 Pump 的 finally 也用此锁确认原生消费者已退出
            int remaining = (int)Math.Max(0L, deadline - NowMilliseconds());
            if (draining != null && draining != Thread.CurrentThread)
            {
                try { draining.Join(remaining); } catch { }
            }
            remaining = (int)Math.Max(0L, deadline - NowMilliseconds());
            if (!Monitor.TryEnter(lifecycleSync, remaining)) return;
            try
            {
                CleanupLocked();
                if (pumpStarted && !pumpCompleted && !drainWarningReported)
                {
                    drainWarningReported = true;
                    Logger.Warn(Logger.Text("进程事件消费者尚未退出；通知资格已撤销，保留原句柄和委托等待排空", "Process event consumer has not exited; notification eligibility revoked, original handle and delegate kept until drained"));
                }
            }
            finally { Monitor.Exit(lifecycleSync); }
        }
    }
}
