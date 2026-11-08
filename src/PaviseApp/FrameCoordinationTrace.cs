using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
namespace PaviseApp
{
    internal sealed class FrameCoordinationTrace
    {
        internal const string TraceKey = "FrameCoordinationTraceV1";
        internal const string IdentityTraceKey = Settings.GpuIdentityReceiptKey;
        // Recover only receipts matching each owned channel prefix, including retired telemetry.
        internal const string FrameSessionPrefix = "Pavise.FrameCoordination.", IdentitySessionPrefix = "Pavise.GpuIdentity.";
        internal const string PresentSessionPrefix = "Pavise.PresentProbe.";
        internal const string PresentProbeReceiptKey = "PresentProbeTraceV1";
        internal const string PresentProbeSessionPattern = @"\APavise\.PresentProbe\.[0-9]+\.[0-9a-f]{32}\z";
        internal static readonly Guid DxgKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");
        // Microsoft-Windows-DxgKrnl "Present" keyword (local manifest); DXGI/D3D9 use Analytic|Events.
        internal const ulong DxgKrnlPresentKeyword = 0x8000000UL, RuntimePresentKeyword = 0x8000000000000002UL;
        // ERROR_SHUTDOWN_IN_PROGRESS: process shutdown closed the GPU identity channel.
        internal const int IdentityClosedError = 1115;
        private const int IdentityHealWaitMs = 3500;
        private static readonly object ledgerGate = new object();
        private sealed class Channel
        {
            internal string Key, Pattern;
            internal bool Identity, Present;
            internal FrameCoordinationTrace Active;
            // Bounded telemetry channels recover their own stale receipt and are not restore debt while healthy.
            internal bool Telemetry { get { return Identity || Present; } }
        }
        private static readonly Channel frameChannel = new Channel { Key = TraceKey, Pattern = @"\APavise\.FrameCoordination\.[0-9]+\.[0-9a-f]{32}\z" },
            identityChannel = new Channel { Key = IdentityTraceKey, Pattern = Settings.GpuIdentitySessionPattern, Identity = true },
            presentChannel = new Channel { Key = PresentProbeReceiptKey, Pattern = PresentProbeSessionPattern, Present = true };
        // Set only by process shutdown (CloseIdentity); guarded by ledgerGate.
        private static bool identitySealed, presentSealed;
        private Channel channel = frameChannel;
        private readonly object memory = new object();
        private readonly object cleanup = new object();
        private IntPtr properties, loggerName;
        private ulong session, consumer = Native.InvalidProcessTraceHandle;
        private Native.EventRecordCallback callback;
        private Thread pump;
        private bool started, claimed;
        // stopRequested marks recovery debt; handlesClosed requires both native handles to close.
        // The stopper or late pump that finishes last settles the trace.
        private volatile bool stopRequested, handlesClosed;
        private bool receiptSettled;
        private string name = SessionName(FrameSessionPrefix);
        internal int Error;
        internal uint Lost;
        internal int PumpTid;
        internal double CpuMs;
        internal bool Drained { get { Thread current = pump; return current == null || !current.IsAlive; } }
        internal static bool IdentityClosing { get { lock (ledgerGate) return identitySealed; } }
        private static string SessionName(string prefix)
        { return prefix + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N"); }
        internal static bool HasResidue
        {
            get
            {
                lock (ledgerGate)
                {
                    string receipt;
                    bool frame = frameChannel.Active != null || !Settings.TryLoadStr(TraceKey, out receipt) || receipt.Length > 0;
                    // Healthy bounded telemetry is not recovery debt; failed/stopping or previous-process receipts are.
                    return frame || TelemetryResidueLocked(identityChannel) || TelemetryResidueLocked(presentChannel);
                }
            }
        }
        // Caller holds ledgerGate.
        private static bool TelemetryResidueLocked(Channel channel)
        {
            string receipt;
            var active = channel.Active;
            return active != null ? active.Error != 0 || active.stopRequested
                : !Settings.TryLoadStr(channel.Key, out receipt) || receipt.Length > 0;
        }
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
        [StructLayout(LayoutKind.Sequential)] private struct Filter { internal ulong Ptr; internal uint Size, Type; }
#if PAVISE_SELFTEST
        internal delegate int StartTraceHook(string traceName, IntPtr props, out ulong handle);
        internal static Func<ulong, string, IntPtr, uint, int> ControlForTest;
        internal static Func<ulong, int> CloseForTest;
        // Doubles for the full Start/pump/Stop path: no ETW session, consumer or provider is created.
        internal static StartTraceHook StartForTest;
        internal static Func<ulong> OpenForTest;
        internal static Func<ulong, Native.EventRecordCallback, int> ProcessForTest;
        internal static int StopWaitMsForTest = 3000;
        internal static FrameCoordinationTrace CleanupFixtureForTest(Thread runningPump = null, bool identity = false)
        {
            var trace = new FrameCoordinationTrace();
            if (identity) { trace.channel = identityChannel; trace.name = SessionName(IdentitySessionPrefix); }
            trace.properties = Props(trace.name, true); trace.session = 11; trace.consumer = 22; trace.pump = runningPump;
            trace.started = trace.claimed = true;
            WriteReceipt(trace.channel, trace.name);
            lock (ledgerGate) trace.channel.Active = trace;
            return trace;
        }
        internal static bool IdentitySealedForTest { get { return IdentityClosing; } }
        internal static void UnsealIdentityForTest() { lock (ledgerGate) identitySealed = false; }
#endif
        private static int Control(ulong handle, string traceName, IntPtr props, uint command)
        {
#if PAVISE_SELFTEST
            if (ControlForTest != null) return ControlForTest(handle, traceName, props, command);
#endif
            return Native.ControlTrace(handle, traceName, props, command);
        }
        private static int CloseConsumer(ulong handle)
        {
#if PAVISE_SELFTEST
            if (CloseForTest != null) return CloseForTest(handle);
#endif
            return Native.CloseTrace(handle);
        }
        private static int StartSession(out ulong handle, string traceName, IntPtr props)
        {
#if PAVISE_SELFTEST
            if (StartForTest != null) return StartForTest(traceName, props, out handle);
#endif
            return Native.StartTrace(out handle, traceName, props);
        }
        private static ulong OpenConsumer(ref Native.EventTraceLogfile log)
        {
#if PAVISE_SELFTEST
            if (OpenForTest != null) return OpenForTest();
#endif
            return Native.OpenTrace(ref log);
        }
        private int ProcessConsumer(ulong handle)
        {
#if PAVISE_SELFTEST
            if (ProcessForTest != null) return ProcessForTest(handle, callback);
#endif
            return Native.ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
        }
        // The identity receipt has its own strict writer that does not advance the user-setting generation.
        private static bool WriteReceipt(Channel channel, string value)
        {
            if (channel.Identity) return Settings.SaveGpuIdentityReceipt(value);
            if (channel.Present) return Settings.SavePresentProbeReceipt(value);
            return Settings.SaveStr(channel.Key, value);
        }

        private static IntPtr Props(string name, bool start)
        { return Props(name, start, false); }
        private static IntPtr Props(string name, bool start, bool presenterOnly)
        {
            int bytes = Marshal.SizeOf(typeof(Native.EventTraceProperties));
            byte[] bytes2 = System.Text.Encoding.Unicode.GetBytes(name + "\0");
            IntPtr p = Marshal.AllocHGlobal(bytes + bytes2.Length);
            Marshal.Copy(new byte[bytes + bytes2.Length], 0, p, bytes + bytes2.Length);
            var value = new Native.EventTraceProperties { WnodeBufferSize = (uint)(bytes + bytes2.Length),
                WnodeFlags = Native.WnodeFlagTracedGuid, WnodeClientContext = 1, WnodeGuid = Guid.NewGuid(),
                BufferSize = 64, MinimumBuffers = 8, MaximumBuffers = 32, FlushTimer = 1,
                LogFileMode = Native.EventTraceRealTimeMode | (presenterOnly ? 0u : 0x02000000u),
                EnableFlags = start && !presenterOnly ? 0x0812u : 0u,
                LoggerNameOffset = (uint)bytes };
            Marshal.StructureToPtr(value, p, false); Marshal.Copy(bytes2, 0, IntPtr.Add(p, bytes), bytes2.Length); return p;
        }
        internal bool Start(int pid, Native.EventRecordCallback receive)
        { return Start(pid, receive, false); }
        internal bool Start(int pid, Native.EventRecordCallback receive, bool presenterOnly)
        { lock (cleanup) return StartLocked(pid, receive, presenterOnly); }
        // Hold the cleanup lock throughout initialisation so a stop never interleaves with half-opened handles.
        private bool StartLocked(int pid, Native.EventRecordCallback receive, bool presenterOnly)
        {
            if (started) { Error = 1247; return false; }
            started = true;
            if (!presenterOnly && GetActiveProcessorGroupCount() != 1) { Error = 50; return false; }
            int refusal;
            lock (ledgerGate) refusal = ClaimLocked();
            if (refusal != 0) { Error = refusal; return false; }
            properties = Props(name, true, presenterOnly);
            Error = StartSession(out session, name, properties);
            if (Error != 0) { session = 0; Stop(); return false; }
            IntPtr pidPointer = Marshal.AllocHGlobal(4), filterPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Filter)));
            try
            {
                Marshal.WriteInt32(pidPointer, pid);
                Marshal.StructureToPtr(new Filter { Ptr = (ulong)pidPointer.ToInt64(), Size = 4, Type = 0x80000004 }, filterPointer, false);
                var parameters = new Native.EnableTraceParameters { Version = 2, EnableFilterDesc = filterPointer, FilterDescCount = 1 };
                Guid dxgi = FrameCoordinationSampler.Dxgi, d3d9 = FrameCoordinationSampler.D3d9;
                Error = Native.EnableTraceEx2(session, ref dxgi, 1, 5, 0x8000000000000002UL, 0, 0, ref parameters);
                if (Error == 0) Error = Native.EnableTraceEx2(session, ref d3d9, 1, 5, 0x8000000000000002UL, 0, 0, ref parameters);
            }
            finally { Marshal.FreeHGlobal(pidPointer); Marshal.FreeHGlobal(filterPointer); }
            if (Error != 0) { Stop(); return false; }
            return OpenAndPumpLocked(receive);
        }
        // System-wide present events only: no pid filter, no kernel thread flags, three present providers.
        internal bool StartPresentProbe(Native.EventRecordCallback receive)
        {
            lock (cleanup)
            {
                if (started) { Error = 1247; return false; }
                channel = presentChannel; name = SessionName(PresentSessionPrefix);
                started = true;
                int refusal;
                lock (ledgerGate) refusal = ClaimLocked();
                if (refusal != 0) { Error = refusal; return false; }
                properties = Props(name, true, true);
                Error = StartSession(out session, name, properties);
                if (Error != 0) { session = 0; Stop(); return false; }
                var parameters = new Native.EnableTraceParameters { Version = 2 };
                Guid dxgi = FrameCoordinationSampler.Dxgi, d3d9 = FrameCoordinationSampler.D3d9, kernel = DxgKrnl;
                Error = Native.EnableTraceEx2(session, ref dxgi, 1, 5, RuntimePresentKeyword, 0, 0, ref parameters);
                if (Error == 0) Error = Native.EnableTraceEx2(session, ref d3d9, 1, 5, RuntimePresentKeyword, 0, 0, ref parameters);
                if (Error == 0) Error = Native.EnableTraceEx2(session, ref kernel, 1, 5, DxgKrnlPresentKeyword, 0, 0, ref parameters);
                if (Error != 0) { Stop(); return false; }
                return OpenAndPumpLocked(receive);
            }
        }
        // Caller holds the cleanup lock; the session is started and its providers are enabled.
        private bool OpenAndPumpLocked(Native.EventRecordCallback receive)
        {
            callback = receive; loggerName = Marshal.StringToHGlobalUni(name);
            var log = new Native.EventTraceLogfile { LoggerName = loggerName,
                ProcessTraceMode = Native.ProcessTraceModeRealTime | Native.ProcessTraceModeEventRecord | 0x1000,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback) };
            consumer = OpenConsumer(ref log);
            if (consumer == Native.InvalidProcessTraceHandle) { Error = Marshal.GetLastWin32Error(); if (Error == 0) Error = 6; Stop(); return false; }
            ulong opened = consumer;
            pump = new Thread(delegate() {
                PumpTid = Native.GetCurrentThreadId(); LoopProfile.Tag("FrameTracePump");
                try { int rc = ProcessConsumer(opened); if (rc != 0 && rc != 1223) Error = rc; }
                finally {
                    IntPtr thread = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, PumpTid);
                    if (thread != IntPtr.Zero) { try { long b,e,k,u; if (Native.GetThreadTimes(thread,out b,out e,out k,out u)) CpuMs=(k+u)/10000.0; } finally { Native.CloseHandle(thread); } }
                    // ProcessTrace has returned, so no callbacks remain; the late pump settles without taking cleanup or joining itself.
                    if (handlesClosed) Settle();
                }
            });
            pump.IsBackground = true; pump.Priority = ThreadPriority.BelowNormal;
            pump.Name = "Pavise.FrameEvents"; pump.Start();
            return true;
        }
        // Caller holds ledgerGate. Returns 0 after this trace owns the channel and its receipt.
        private int ClaimLocked()
        {
            if (channel.Identity && identitySealed) return IdentityClosedError;
            if (channel.Present && presentSealed) return IdentityClosedError;
            if (channel.Active != null) return 170;
            string previous, written;
            if (!Settings.TryLoadStr(channel.Key, out previous)) return 5;
            if (previous.Length != 0)
            {
                // Only telemetry channels self-recover, and only their own stale receipt; no live trace owns it.
                if (!channel.Telemetry) return 5;
                int recovered = RecoverReceiptLocked(channel, previous);
                if (recovered != 0) return recovered;
            }
            if (!WriteReceipt(channel, name) || !Settings.TryLoadStr(channel.Key, out written) || written != name) return 5;
            FrameRecoveryBudget.Own(channel.Key, 0, 0, name);
            claimed = true; channel.Active = this;
            return 0;
        }
        // Caller holds ledgerGate and has verified that no live trace of this process owns the channel.
        private static int RecoverReceiptLocked(Channel channel, string receipt)
        {
            int result = 5;
            return FrameRecoveryBudget.Run(channel.Key, delegate { result = RecoverReceiptCore(channel, receipt); return result == 0; }) ? 0 : result;
        }
        private static int RecoverReceiptCore(Channel channel, string receipt)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(receipt, channel.Pattern)) return 5;
            IntPtr p = Props(receipt, false);
            try
            {
                int rc = Control(0, receipt, p, Native.EventTraceControlStop);
                if (rc != 0 && rc != 4201 && rc != 234) return rc;
                return ClearLedger(channel, receipt) ? 0 : 5;
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        internal bool Stop()
        { lock (cleanup) return StopLocked(StopWaitMs()); }
        // If initialization holds the cleanup lock, report recovery debt instead of blocking shutdown.
        private bool StopWithin(int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            if (!Monitor.TryEnter(cleanup, Math.Max(0, timeoutMs))) return false;
            try { return StopLocked(Math.Max(0, Math.Min(StopWaitMs(), timeoutMs - (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds)))); }
            finally { Monitor.Exit(cleanup); }
        }
        private static int StopWaitMs()
        {
            int waitMs = 3000;
#if PAVISE_SELFTEST
            waitMs = StopWaitMsForTest;
#endif
            return waitMs;
        }
        private bool StopLocked(int waitMs)
        {
            if (receiptSettled) return true;
            bool stopped = false;
            bool released = FrameRecoveryBudget.Run(channel.Key, delegate { stopped = StopCore(waitMs); return receiptSettled; });
            return released && stopped;
        }
        private bool StopCore(int waitMs)
        {
            int rc = 0;
            stopRequested = true;
            if (session != 0)
            {
                rc = Control(session, name, properties, Native.EventTraceControlStop);
                var value = (Native.EventTraceProperties)Marshal.PtrToStructure(properties, typeof(Native.EventTraceProperties));
                Lost = value.EventsLost + value.RealTimeBuffersLost;
                // 停止失败时保留会话句柄，由所属预算控制后续恢复。
                if (rc == 0 || rc == 4201 || rc == 234) session = 0;
            }
            if (consumer != Native.InvalidProcessTraceHandle)
            {
                int close = CloseConsumer(consumer);
                // 实时追踪返回关闭待完成仍需排空回调。
                if (close == 0 || close == 7007 || close == 6) consumer = Native.InvalidProcessTraceHandle;
                if (close != 0 && close != 7007) Error = close;
            }
            if (rc != 0 && rc != 4201) Error = rc;
            bool closedHandles = session == 0 && consumer == Native.InvalidProcessTraceHandle;
            if (closedHandles) handlesClosed = true;
            bool settled = closedHandles && PumpFinished(waitMs);
            bool cleared = settled && Settle();
            return settled && cleared && Error == 0 && Lost == 0;
        }
        private bool PumpFinished(int waitMs)
        {
            Thread current = pump;
            if (current == null || !current.IsAlive) return true;
            // A stop issued from a callback must not join its own pump; that pump settles once it returns.
            if (current == Thread.CurrentThread) return false;
            return current.Join(Math.Max(0, waitMs));
        }
        // Settle after both handles close and ProcessTrace returns; stopper and late pump may race here.
        // A failed receipt clear retains recovery debt.
        private bool Settle()
        {
            bool cleared = true;
            lock (ledgerGate)
            {
                // A late stopper must not clear the receipt of a newer channel owner.
                if (!receiptSettled)
                {
                    if (channel.Active == this) channel.Active = null;
                    if (claimed) cleared = ClearLedger(channel, name);
                    if (cleared)
                    {
                        receiptSettled = true;
                        if (claimed) FrameRecoveryBudget.Settled(channel.Key, name);
                    }
                }
            }
            if (!cleared) Error = 5;
            lock (memory) FreeMemory();
            return cleared;
        }
        private static bool ClearLedger(Channel channel, string expected)
        {
            string current;
            if (!Settings.TryLoadStr(channel.Key, out current)) return false;
            if (current.Length == 0) return true;
            return current == expected && WriteReceipt(channel, "")
                && Settings.TryLoadStr(channel.Key, out current) && current.Length == 0;
        }
        private void FreeMemory()
        {
            if (properties != IntPtr.Zero) { Marshal.FreeHGlobal(properties); properties = IntPtr.Zero; }
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
            GC.KeepAlive(callback);
        }
        internal static bool Heal()
        { bool frame = Heal(frameChannel); bool identity = Heal(identityChannel); return Heal(presentChannel) && identity && frame; }
        private static bool Heal(Channel channel)
        {
            FrameCoordinationTrace pending;
            lock (ledgerGate)
            {
                pending = channel.Active;
                // Healthy bounded telemetry is not recovery debt: ordinary healing never interrupts it.
                if (channel.Telemetry && pending != null && pending.Error == 0 && !pending.stopRequested) return true;
            }
            if (pending != null)
            {
                // 持有 ledgerGate 时不能获取清理锁。
                if (channel.Telemetry)
                {
                    // A healthy stopping identity trace settles itself; failed traces use the bounded STOP recovery path.
                    if (pending.Error == 0 && !pending.Drained) return true;
                    pending.StopWithin(IdentityHealWaitMs);
                }
                else
                {
                    if (!pending.Drained) return false;
                    pending.Stop();
                }
            }
            lock (ledgerGate)
            {
                if (channel.Active != null) return false;
                string name; if (!Settings.TryLoadStr(channel.Key, out name)) return false; if (name.Length == 0) return true;
                return RecoverReceiptLocked(channel, name) == 0;
            }
        }
        // Process shutdown also retries the retired telemetry receipt. A failed stop/clear leaves recovery debt.
        internal static bool CloseIdentity(int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            bool identity = CloseSealed(identityChannel, timeoutMs);
            int remaining = Math.Max(0, timeoutMs - (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds));
            return CloseSealed(presentChannel, remaining) && identity;
        }
        private static bool CloseSealed(Channel channel, int timeoutMs)
        {
            FrameCoordinationTrace pending;
            lock (ledgerGate)
            {
                if (channel.Identity) identitySealed = true; else presentSealed = true;
                pending = channel.Active;
            }
            if (pending != null) pending.StopWithin(timeoutMs);
            lock (ledgerGate)
            {
                if (channel.Active != null) return false;
                string receipt;
                if (!Settings.TryLoadStr(channel.Key, out receipt)) return false;
                return receipt.Length == 0 || RecoverReceiptLocked(channel, receipt) == 0;
            }
        }
    }
}
