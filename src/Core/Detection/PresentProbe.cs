// @author bdth 2074055628@qq.com
// File purpose Independent ETW session subscribing to Microsoft-Windows-DxgKrnl, taking only Present event 184
//   Records per frame the present's QPC time and submitting pid, as the foundation for present long-frame and DPC causal alignment
//   Fully independent of InterruptAttribution's interrupt session; only swaps the provider subscription from kernel EnableFlags
//   to EnableTraceEx2 by manifest provider GUID; the P/Invoke layout copies the already-verified code over there
//   The session uses ProcessModeRawTimestamp to keep raw QPC, same ruler as the interrupt session, so they align directly
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // One present submission, same QPC ruler as QueryPerformanceCounter
    internal struct PresentFrame
    {
        public long Qpc;   // QPC time of this present; raw QPC when the session uses RawTimestamp
        public int Pid;    // Process that submitted the present, used to filter down to the target game
    }

    internal sealed class PresentProbe
    {
        private const int WnodeFlagTracedGuid = 0x00020000;
        private const uint RealTimeMode = 0x00000100;               // EVENT_TRACE_REAL_TIME_MODE
        private const uint ProcessModeRealTime = 0x00000100;
        private const uint ProcessModeRawTimestamp = 0x00001000;    // Disable timestamp conversion, keep raw QPC
        private const uint ProcessModeEventRecord = 0x10000000;
        private const uint ControlStop = 1;
        private const int ErrorAlreadyExists = 183;

        // EnableTraceEx2 parameters
        private const uint EnableProvider = 1;      // EVENT_CONTROL_CODE_ENABLE_PROVIDER
        private const byte LevelInformation = 4;    // TRACE_LEVEL_INFORMATION
        // The Present keyword also covers VSync/HSync queue and sync signal events, so it cannot serve alone as a low-overhead filter
        // Must also use EVENT_FILTER_TYPE_EVENT_ID to restrict to 184 before ETW writes; the callback re-checks defensively
        private const ulong MatchAnyKeyword = 0x8000000;
        private const uint EnableTimeoutMs = 1000;

        // present event id, pinned from the DxgKrnl manifest
        private const ushort EventPresent = 184;

        // Independent session; name and GUID collide with no existing session (the interrupt session, PresentTrace in selftest)
        private const string SessionName = "Pavise_PresentProbe";
        private static readonly Guid SessionGuid = new Guid("a3f6d1c4-2b58-4e7a-9c31-6f0d8b2e5a17");
        private static readonly Guid DxgKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");

        // Per-frame timeline; the consumer thread's ProcessTrace callback appends alone; read only after Stop and worker.Join
        //   Single producer single consumer, no lock; unrelated to aggregation, raw landing only
        private const int FrameCap = 4000000;   // Cap of about 4M frames so a long capture cannot eat all memory; at the cap stop recording and set Truncated
        private readonly List<PresentFrame> frames = new List<PresentFrame>(1 << 16);

        private ulong sessionHandle;
        private ulong traceHandle;
        private Thread worker;
        private EventRecordCallback keepAlive;
        private long qpcFrequency;
        private volatile bool started;
        private volatile bool stopRequested;
        private volatile bool consumerExitedEarly;
        private volatile bool processTraceSucceeded;
        private bool drainCompleted = true;
        private readonly object stopGate = new object();
        private bool stopRequestIssued;
        private bool stopRequestSucceeded;
        private uint stopRequestError;
        private bool stopFinishing;

        public bool Truncated { get; private set; }
        public uint LastError { get; private set; }
        public uint EventsLost { get; private set; }
        public uint BuffersLost { get; private set; }
        public bool DrainCompleted { get { return drainCompleted; } }
        public bool ConsumerExitedEarly { get { return consumerExitedEarly; } }

        // QPC frequency used by the session, same ruler as the persisted present_qpc
        public long QpcFrequency { get { return qpcFrequency; } }

        // Call after Stop and worker.Join to fetch the per-frame timeline
        public List<PresentFrame> Frames { get { return frames; } }

        public bool Start()
        {
            if (started) return true;
            LastError = 0;

            IntPtr props = AllocProps();
            try
            {
                uint rc = StartTrace(out sessionHandle, SessionName, props);
                if (rc == ErrorAlreadyExists)
                {
                    // A stale session with the same name gets stopped and recreated, once
                    StopStale();
                    Marshal.FreeHGlobal(props);
                    props = AllocProps();
                    rc = StartTrace(out sessionHandle, SessionName, props);
                }
                if (rc != 0)
                {
                    LastError = rc;
                    Logger.Warn("PRESENT StartTrace 失败 win32=" + rc);
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(props); }

            // DxgKrnl is a manifest provider registered by a kernel driver; do not apply the PID scope that only fits user-mode providers
            // The filter parameters descriptor and variable-length payload are held by one HGlobal for the whole EnableTraceEx2 call
            // On configuration failure give up Present for this round; never fall back to a high-volume session subscribing to the whole event family
            Guid provider = DxgKrnl;
            uint erc;
            try
            {
                using (var filter = new EventIdFilterBuffer())
                    erc = EnableTraceEx2(sessionHandle, ref provider, EnableProvider, LevelInformation,
                        MatchAnyKeyword, 0, EnableTimeoutMs, filter.ParametersPointer);
            }
            catch (Exception ex)
            {
                LastError = uint.MaxValue;
                Logger.Warn("PRESENT 事件 ID 过滤配置异常，本轮跳过，不启用宽范围采集 " + ex.GetType().Name);
                StopStale();
                return false;
            }
            if (erc != 0)
            {
                LastError = erc;
                Logger.Warn("PRESENT 事件 ID 过滤启用失败，本轮跳过，不启用宽范围采集 win32=" + erc);
                StopStale();
                return false;
            }

            keepAlive = OnEvent;
            var logfile = new EventTraceLogfile();
            logfile.LoggerName = Marshal.StringToHGlobalUni(SessionName);
            // RawTimestamp keeps raw QPC, same ruler as the interrupt session
            logfile.ProcessTraceMode = ProcessModeRealTime | ProcessModeEventRecord | ProcessModeRawTimestamp;
            logfile.EventRecordCallbackPtr = Marshal.GetFunctionPointerForDelegate(keepAlive);
            traceHandle = OpenTrace(ref logfile);
            if (traceHandle == 0xFFFFFFFFFFFFFFFF || traceHandle == 0)
            {
                LastError = (uint)Marshal.GetLastWin32Error();
                Logger.Warn("PRESENT OpenTrace 失败 win32=" + LastError);
                StopStale();
                return false;
            }

            long freq = logfile.LogfileHeader.PerfFreq;
            if (freq <= 0) freq = Stopwatch.Frequency;
            qpcFrequency = freq;

            worker = new Thread(RunProcessTrace);
            worker.IsBackground = true;
            try { worker.Priority = ThreadPriority.AboveNormal; } catch { }
            drainCompleted = false;
            stopRequested = false;
            consumerExitedEarly = false;
            processTraceSucceeded = false;
            lock (stopGate)
            {
                stopRequestIssued = false;
                stopRequestSucceeded = false;
                stopRequestError = 0;
            }
            worker.Start();
            started = true;
            Logger.Log("PRESENT 会话已启动 " + SessionName + " ETW 前置过滤 event 184");
            return true;
        }

        private void RunProcessTrace()
        {
            try
            {
                ulong[] handles = { traceHandle };
                uint rc = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
                processTraceSucceeded = rc == 0;
                if (rc != 0)
                {
                    LastError = rc;
                    consumerExitedEarly = true;
                }
            }
            catch { if (!stopRequested) consumerExitedEarly = true; }
            finally
            {
                // On the normal path the consumer thread should return only after RequestStop issues ControlTrace; an early return, even if
                // Join succeeds and the dropped-event count is 0, covered only half the match and cannot produce negative evidence
                if (!stopRequested) consumerExitedEarly = true;
            }
        }

        // Only closes off the event source without waiting for the consumer thread to drain; the caller can close the PRESENT window first
        // to seal the fresh DPC proof immediately, then call Stop during exit teardown to take the full timeline
        public void RequestStop()
        {
            lock (stopGate)
            {
                if (!started || stopRequestIssued) return;
                stopRequestIssued = true;
                stopRequested = true;
                uint lost = 0, buffers = 0, error = 0;
                try { stopRequestSucceeded = StopStale(out lost, out buffers, out error); }
                catch { stopRequestSucceeded = false; error = uint.MaxValue; }
                stopRequestError = error;
                if (!stopRequestSucceeded && error != 0) LastError = error;
                EventsLost = lost;
                BuffersLost = buffers;
            }
        }

        public void Stop()
        {
            RequestStop();
            Thread drainWorker;
            ulong handleToClose;
            bool stopSucceeded;
            uint stopError;
            lock (stopGate)
            {
                // Multiple teardown callers share one drain; the wait releases the lock so RequestStop is never blocked by Join
                while (stopFinishing) Monitor.Wait(stopGate);
                if (!started) return;
                stopFinishing = true;
                drainWorker = worker;
                handleToClose = traceHandle;
                stopSucceeded = stopRequestSucceeded;
                stopError = stopRequestError;
            }
            bool workerDone = drainWorker == null;
            try
            {
                // After a normal session stop the consumer drains on its own; if stop failed, CloseTrace first to release the consumer
                // That path does not count as a complete capture even if Join succeeds
                if (!stopSucceeded)
                    try { if (handleToClose != 0) CloseTrace(handleToClose); } catch { }
                if (drainWorker != null)
                    try { workerDone = drainWorker.Join(10000); } catch { workerDone = false; }
            }
            finally
            {
                if (stopSucceeded)
                    try { if (handleToClose != 0) CloseTrace(handleToClose); } catch { }
                lock (stopGate)
                {
                    traceHandle = 0;
                    drainCompleted = stopSucceeded && workerDone && processTraceSucceeded;
                    started = false;
                    // Callbacks may still fire after the drain times out; keep the delegate alive until the consumer thread ends
                    if (workerDone) keepAlive = null;
                    stopFinishing = false;
                    Monitor.PulseAll(stopGate);
                }
            }
            Logger.Log("PRESENT 会话已停止 Present事件=" + frames.Count
                + (Truncated ? "(已截断)" : "") + " 丢事件=" + EventsLost + " 丢缓冲=" + BuffersLost);
            if (!workerDone) Logger.Warn("PRESENT 会话排空超时 本局时间线作废");
            if (!stopSucceeded) Logger.Warn("PRESENT 会话停止失败 本局时间线作废 win32=" + stopError);
            if (consumerExitedEarly) Logger.Warn("PRESENT 消费线程提前退出 本局时间线作废 win32=" + LastError);
        }

        // The only consumer point, runs on the ProcessTrace worker thread; append only, nothing else
        private void OnEvent(ref EventRecord record)
        {
            // Provider is already subscribed by GUID; re-check here in case another provider leaks into the same session
            if (record.EventHeader.ProviderId != DxgKrnl) return;
            if (record.EventHeader.EventDescriptor.Id != EventPresent) return;
            if (Truncated) return;
            if (frames.Count >= FrameCap) { Truncated = true; return; }
            frames.Add(new PresentFrame
            {
                Qpc = record.EventHeader.TimeStamp,
                Pid = (int)record.EventHeader.ProcessId
            });
        }

        private static IntPtr AllocProps()
        {
            int nameBytes = (SessionName.Length + 1) * 2;
            int size = Marshal.SizeOf(typeof(EventTraceProperties)) + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(props, i, 0);
            var p = new EventTraceProperties();
            p.Wnode.BufferSize = (uint)size;
            p.Wnode.Flags = WnodeFlagTracedGuid;
            p.Wnode.Guid = SessionGuid;
            p.Wnode.ClientContext = 1;   // QPC clock
            p.BufferSize = 256;
            p.MinimumBuffers = 16;
            p.MaximumBuffers = 128;
            // Plain real-time session, no kernel EnableFlags (those are kernel logger only); the provider attaches via EnableTraceEx2
            p.LogFileMode = RealTimeMode;
            p.FlushTimer = 1;
            p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
            Marshal.StructureToPtr(p, props, false);
            return props;
        }

        public static void CleanupStaleSession() { try { StopStale(); } catch { } }

        private static void StopStale()
        {
            uint lost, buffers, error;
            StopStale(out lost, out buffers, out error);
        }

        private static bool StopStale(out uint eventsLost, out uint buffersLost, out uint error)
        {
            eventsLost = 0; buffersLost = 0; error = 0;
            int nameBytes = (SessionName.Length + 1) * 2;
            int size = Marshal.SizeOf(typeof(EventTraceProperties)) + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(props, i, 0);
                var p = new EventTraceProperties();
                p.Wnode.BufferSize = (uint)size;
                p.Wnode.Guid = SessionGuid;
                p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
                Marshal.StructureToPtr(p, props, false);
                uint rc = ControlTrace(0, SessionName, props, ControlStop);
                if (rc != 0) { error = rc; return false; }
                var done = (EventTraceProperties)Marshal.PtrToStructure(props, typeof(EventTraceProperties));
                eventsLost = done.EventsLost;
                buffersLost = done.RealTimeBuffersLost + done.LogBuffersLost;
                return true;
            }
            catch { error = uint.MaxValue; return false; }
            finally { Marshal.FreeHGlobal(props); }
        }

        // The BOOLEAN in EVENT_FILTER_EVENT_ID is 1 byte, not the 4-byte default P/Invoke BOOL
        // Currently only one event is subscribed: sizeof(header) 4 + USHORT Events[1] 2 = 6 bytes
        [StructLayout(LayoutKind.Sequential)]
        internal struct EventIdFilterData
        {
            internal byte FilterIn;
            internal byte Reserved;
            internal ushort Count;
            internal ushort EventId;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct EventFilterDescriptor
        {
            internal ulong Ptr;  // ULONGLONG must be 8 bytes even in a 32-bit controller
            internal uint Size;
            internal uint Type;
        }

        // Only allocates and serializes memory, calls no ETW API; a pure self-test can verify the whole pointer chain and release path
        internal sealed class EventIdFilterBuffer : IDisposable
        {
            internal const uint EventIdFilterType = 0x80000200;
            private IntPtr memory;

            internal EventIdFilterBuffer()
            {
                int parametersSize = Marshal.SizeOf(typeof(Native.EnableTraceParameters));
                // The descriptor contains a ULONGLONG, so align explicitly to 8 bytes for x86/x64 controllers
                int descriptorOffset = (parametersSize + 7) & ~7;
                int payloadOffset = descriptorOffset + Marshal.SizeOf(typeof(EventFilterDescriptor));
                int payloadSize = Marshal.SizeOf(typeof(EventIdFilterData));
                int size = payloadOffset + payloadSize;
                memory = Marshal.AllocHGlobal(size);
                try
                {
                    for (int i = 0; i < size; i++) Marshal.WriteByte(memory, i, 0);
                    IntPtr descriptorPointer = IntPtr.Add(memory, descriptorOffset);
                    IntPtr payloadPointer = IntPtr.Add(memory, payloadOffset);
                    var data = new EventIdFilterData { FilterIn = 1, Reserved = 0, Count = 1, EventId = EventPresent };
                    Marshal.StructureToPtr(data, payloadPointer, false);
                    var descriptor = new EventFilterDescriptor
                    {
                        Ptr = IntPtr.Size == 8 ? unchecked((ulong)payloadPointer.ToInt64())
                            : unchecked((uint)payloadPointer.ToInt32()),
                        Size = (uint)payloadSize,
                        Type = EventIdFilterType
                    };
                    Marshal.StructureToPtr(descriptor, descriptorPointer, false);
                    var parameters = new Native.EnableTraceParameters
                    {
                        Version = 2,
                        SourceId = SessionGuid,
                        EnableFilterDesc = descriptorPointer,
                        FilterDescCount = 1
                    };
                    Marshal.StructureToPtr(parameters, memory, false);
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }

            internal IntPtr ParametersPointer
            {
                get
                {
                    if (memory == IntPtr.Zero) throw new ObjectDisposedException("EventIdFilterBuffer");
                    return memory;
                }
            }

            public void Dispose()
            {
                IntPtr owned = Interlocked.Exchange(ref memory, IntPtr.Zero);
                if (owned != IntPtr.Zero) Marshal.FreeHGlobal(owned);
                GC.SuppressFinalize(this);
            }

            ~EventIdFilterBuffer()
            {
                Dispose();
            }
        }

        // The interop structs and signatures below copy InterruptAttribution's verified layout; a separate copy, not shared
        [StructLayout(LayoutKind.Sequential)]
        private struct WnodeHeader
        {
            public uint BufferSize, ProviderId;
            public ulong HistoricalContext;
            public long TimeStamp;
            public Guid Guid;
            public uint ClientContext, Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTraceProperties
        {
            public WnodeHeader Wnode;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset, LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTraceHeader
        {
            public ushort Size, FieldTypeFlags;
            public byte Type, Level;
            public ushort Version;
            public uint ThreadId, ProcessId;
            public long TimeStamp;
            public Guid Guid;
            public uint KernelTime, UserTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventDescriptor
        {
            public ushort Id;
            public byte Version, Channel, Level, Opcode;
            public ushort Task;
            public ulong Keyword;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventHeader
        {
            public ushort Size, HeaderType, Flags, EventProperty;
            public uint ThreadId, ProcessId;
            public long TimeStamp;
            public Guid ProviderId;
            public EventDescriptor EventDescriptor;
            public ulong ProcessorTime;
            public Guid ActivityId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EtwBufferContext
        {
            public ushort ProcessorIndex;
            public ushort LoggerId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventRecord
        {
            public EventHeader EventHeader;
            public EtwBufferContext BufferContext;
            public ushort ExtendedDataCount, UserDataLength;
            public IntPtr ExtendedData, UserData, UserContext;
        }

        private delegate void EventRecordCallback(ref EventRecord record);

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTrace
        {
            public EventTraceHeader Header;
            public uint InstanceId, ParentInstanceId;
            public Guid ParentGuid;
            public IntPtr MofData;
            public uint MofLength, ClientContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemTime
        {
            public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TimeZoneInformation
        {
            public int Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
            public SystemTime StandardDate;
            public int StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
            public SystemTime DaylightDate;
            public int DaylightBias;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TraceLogfileHeader
        {
            public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
            public long EndTime;
            public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
            public Guid LogInstanceGuid;
            public IntPtr LoggerName, LogFileName;
            public TimeZoneInformation TimeZone;
            public long BootTime, PerfFreq, StartTime;
            public uint ReservedFlags, BuffersLost;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct EventTraceLogfile
        {
            public IntPtr LogFileName, LoggerName;
            public long CurrentTime;
            public uint BuffersRead, ProcessTraceMode;
            public EventTrace CurrentEvent;
            public TraceLogfileHeader LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize, Filled, EventsLost;
            public IntPtr EventRecordCallbackPtr;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint StartTrace(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint ControlTrace(ulong handle, string name, IntPtr props, uint code);
        // manifest provider subscription entry; the kernel session has no such step, the only substantive difference between this probe and the interrupt session
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint EnableTraceEx2(ulong handle, ref Guid provider, uint controlCode,
            byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ulong OpenTrace(ref EventTraceLogfile logfile);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint CloseTrace(ulong handle);
    }
}
