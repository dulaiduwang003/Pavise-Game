// @author bdth 2074055628@qq.com
// 文件用途 抓游戏线程被磁盘挡住的精确区间 硬缺页与同步磁盘 I O 两类
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal enum StallKind : byte
    {
        HardFault = 0,
        DiskIo = 1
    }

    internal struct StallSpan
    {
        public long StartQpc;
        public long EndQpc;
        public uint Tid;
        public StallKind Kind;
    }

    internal sealed class DiskStallResult
    {
        public bool Ok;
        public string Error;
        public long QpcFrequency;
        public long HardFaultCount;
        public long DiskIoCount;
        public long EventsSeen;
        public double HardFaultMs;
        public double DiskIoMs;
        public StallSpan[] Stalls = new StallSpan[0];
        public long StallsDropped;
        public long UnpairedIo;
    }

    internal sealed class DiskStallProbe
    {
        private const int WnodeFlagTracedGuid = 0x00020000;
        private const uint RealTimeMode = 0x00000100;
        private const uint SystemLoggerMode = 0x02000000;
        private const uint IndependentSessionMode = 0x08000000;
        private const uint ProcessModeRealTime = 0x00000100;
        private const uint ProcessModeEventRecord = 0x10000000;
        private const uint ProcessModeRawTimestamp = 0x00001000;
        private const uint ControlStop = 1;
        private const uint FlagThread = 0x00000002;
        private const uint FlagDiskIo = 0x00000100;
        private const uint FlagDiskIoInit = 0x00000400;
        private const uint FlagMemoryHardFaults = 0x00002000;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorInvalidParameter = 87;

        private static readonly Guid SessionGuid = new Guid("6a1c93e8-77b4-4d29-9c0e-2b8f5a41d073");
        private static readonly Guid ThreadGuid = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
        private static readonly Guid PageFaultGuid = new Guid("3d6fa8d3-fe05-11d0-9dda-00c04fd7ba7c");
        private static readonly Guid DiskIoGuid = new Guid("3d6fa8d4-fe05-11d0-9dda-00c04fd7ba7c");
        private const string SessionName = "PaviseDiskStall";

        private const byte OpHardFault = 32;
        private const byte OpDiskRead = 10;
        private const byte OpDiskWrite = 11;
        private const byte OpDiskReadInit = 12;
        private const byte OpDiskWriteInit = 13;

        private const int StallRingSize = 16384;
        private const int MaxPending = 4096;
        private const int HeaderFlag32Bit = 0x0020;
        private const int HeaderFlag64Bit = 0x0040;

        private readonly object gate = new object();
        private readonly StallSpan[] stalls = new StallSpan[StallRingSize];
        private readonly long[] stallSeq = new long[StallRingSize];

        private long stallWritten;
        private long hardFaultCount;
        private long diskIoCount;
        private long eventsSeen;
        private long unpairedIo;
        private long hardFaultTicks;
        private long diskIoTicks;
        private long qpcFrequency = System.Diagnostics.Stopwatch.Frequency;

        private uint targetPid;
        private readonly HashSet<uint> ourTids = new HashSet<uint>();
        private readonly Dictionary<ulong, PendingIo> pending = new Dictionary<ulong, PendingIo>();
        private readonly Queue<PendingKey> pendingOrder = new Queue<PendingKey>();
        private long pendingGen;

        private struct PendingIo
        {
            public long StartQpc;
            public uint Tid;
            public long Gen;
        }

        private struct PendingKey
        {
            public ulong Irp;
            public long Gen;
        }

        private ulong sessionHandle;
        private ulong traceHandle;
        private Thread worker;
        private EventRecordCallback keepAlive;
        private volatile bool started;

        public bool Started { get { return started; } }
        public long QpcFrequency { get { return qpcFrequency; } }

        internal bool VerifyMode = false;
        internal long VfHardFault, VfInit, VfComplete, VfPaired;
        internal readonly HashSet<uint> VfHardFaultTids = new HashSet<uint>();
        internal readonly HashSet<uint> VfInitTids = new HashSet<uint>();
        private const int MaxVerifyTids = 4096;

        internal long VfHfUserVa, VfHfKernelVa, VfHfKernelFileObj, VfHfSaneBytes, VfHfAlignedTid;

        public bool Start(int pid)
        {
            lock (gate)
            {
                if (started) return true;
                if (pid <= 0) return false;
                targetPid = (uint)pid;
                stallWritten = 0; hardFaultCount = 0; diskIoCount = 0;
                eventsSeen = 0; unpairedIo = 0; hardFaultTicks = 0; diskIoTicks = 0;
                ourTids.Clear(); pending.Clear(); pendingOrder.Clear(); pendingGen = 0;

                IntPtr props = AllocProps();
                try
                {
                    uint rc = StartTrace(out sessionHandle, SessionName, props);
                    if (rc == ErrorAlreadyExists)
                    {
                        StopStale();
                        Marshal.FreeHGlobal(props);
                        props = AllocProps();
                        rc = StartTrace(out sessionHandle, SessionName, props);
                    }
                    if (rc == ErrorInvalidParameter) { Logger.Log(Lang.T("log.diskstall.1")); return false; }
                    if (rc != 0) { Logger.Log(Lang.T("log.diskstall.2") + rc); return false; }
                }
                finally { Marshal.FreeHGlobal(props); }

                keepAlive = OnEvent;
                var logfile = new EventTraceLogfile();
                logfile.LoggerName = Marshal.StringToHGlobalUni(SessionName);
                logfile.ProcessTraceMode = ProcessModeRealTime | ProcessModeEventRecord | ProcessModeRawTimestamp;
                logfile.EventRecordCallbackPtr = Marshal.GetFunctionPointerForDelegate(keepAlive);
                traceHandle = OpenTrace(ref logfile);
                if (traceHandle == 0xFFFFFFFFFFFFFFFF || traceHandle == 0)
                {
                    Logger.Log(Lang.T("log.diskstall.3") + Marshal.GetLastWin32Error());
                    StopStale();
                    return false;
                }

                long freq = logfile.LogfileHeader.PerfFreq;
                if (freq <= 0) freq = System.Diagnostics.Stopwatch.Frequency;
                qpcFrequency = freq;

                try
                {
                    using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                        foreach (System.Diagnostics.ProcessThread t in proc.Threads)
                            ourTids.Add((uint)t.Id);
                }
                catch { }

                worker = new Thread(RunProcessTrace);
                worker.IsBackground = true;
                worker.Start();
                started = true;
                return true;
            }
        }

        private void RunProcessTrace()
        {
            try
            {
                ulong[] handles = { traceHandle };
                ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private void OnEvent(ref EventRecord record)
        {
            Guid provider = record.EventHeader.ProviderId;
            byte op = record.EventHeader.EventDescriptor.Opcode;

            if (provider == PageFaultGuid)
            {
                if (op != OpHardFault) return;
                eventsSeen++;
                int ptr = PointerSize(record.EventHeader.Flags);
                int tidOff = 16 + ptr * 2;
                if (record.UserDataLength < tidOff + 8) return;
                long p = record.UserData.ToInt64();
                uint tid = (uint)Marshal.ReadInt32(new IntPtr(p + tidOff));
                if (VerifyMode)
                {
                    VfHardFault++;
                    if (VfHardFaultTids.Count < MaxVerifyTids) VfHardFaultTids.Add(tid);
                    ulong va = ReadPointer(p + 16, ptr);
                    ulong fo = ReadPointer(p + 16 + ptr, ptr);
                    uint bytes = (uint)Marshal.ReadInt32(new IntPtr(p + tidOff + 4));
                    if (va >= 0x10000 && va < 0x00007FFFFFFFFFFFUL) VfHfUserVa++;
                    else if (ptr == 4 ? va >= 0x80000000UL : va >= 0xFFFF000000000000UL) VfHfKernelVa++;
                    if (ptr == 4 ? fo >= 0x80000000UL : fo >= 0xFFFF000000000000UL) VfHfKernelFileObj++;
                    if (bytes >= 512 && bytes <= 16 * 1024 * 1024 && (bytes % 512) == 0) VfHfSaneBytes++;
                    if (tid != 0 && (tid % 4) == 0) VfHfAlignedTid++;
                }
                else if (!ourTids.Contains(tid)) return;
                long start = Marshal.ReadInt64(new IntPtr(p));
                long end = record.EventHeader.TimeStamp;
                if (start <= 0 || end <= start) return;
                hardFaultCount++;
                hardFaultTicks += end - start;
                PushStall(start, end, tid, StallKind.HardFault);
                return;
            }

            if (provider == DiskIoGuid)
            {
                int ptr = PointerSize(record.EventHeader.Flags);
                long p = record.UserData.ToInt64();
                if (op == OpDiskReadInit || op == OpDiskWriteInit)
                {
                    eventsSeen++;
                    if (record.UserDataLength < ptr + 4) return;
                    uint tid = (uint)Marshal.ReadInt32(new IntPtr(p + ptr));
                    if (VerifyMode)
                    {
                        VfInit++;
                        if (VfInitTids.Count < MaxVerifyTids) VfInitTids.Add(tid);
                    }
                    else if (!ourTids.Contains(tid)) return;
                    ulong irp = ReadPointer(p, ptr);
                    if (irp == 0) return;
                    var e = new PendingIo();
                    e.StartQpc = record.EventHeader.TimeStamp;
                    e.Tid = tid;
                    e.Gen = ++pendingGen;
                    if (pending.ContainsKey(irp)) { pending.Remove(irp); unpairedIo++; return; }
                    pending[irp] = e;
                    var key = new PendingKey();
                    key.Irp = irp; key.Gen = e.Gen;
                    pendingOrder.Enqueue(key);
                    while (pendingOrder.Count > MaxPending)
                    {
                        PendingKey old = pendingOrder.Dequeue();
                        PendingIo cur;
                        if (pending.TryGetValue(old.Irp, out cur) && cur.Gen == old.Gen)
                        { pending.Remove(old.Irp); unpairedIo++; }
                    }
                    return;
                }
                if (op == OpDiskRead || op == OpDiskWrite)
                {
                    eventsSeen++;
                    int irpOff = 24 + ptr;
                    if (record.UserDataLength < irpOff + ptr) return;
                    ulong irp = ReadPointer(p + irpOff, ptr);
                    PendingIo e;
                    if (VerifyMode) VfComplete++;
                    if (irp == 0 || !pending.TryGetValue(irp, out e)) return;
                    if (VerifyMode) VfPaired++;
                    pending.Remove(irp);
                    if (!VerifyMode && !ourTids.Contains(e.Tid)) { unpairedIo++; return; }
                    long end = record.EventHeader.TimeStamp;
                    if (end > e.StartQpc)
                    {
                        diskIoCount++;
                        diskIoTicks += end - e.StartQpc;
                        PushStall(e.StartQpc, end, e.Tid, StallKind.DiskIo);
                    }
                    return;
                }
                return;
            }

            if (provider != ThreadGuid) return;
            if (op == 1 || op == 3)
            {
                if (record.UserDataLength < 8) return;
                long q = record.UserData.ToInt64();
                uint pid = (uint)Marshal.ReadInt32(new IntPtr(q));
                uint tid = (uint)Marshal.ReadInt32(new IntPtr(q + 4));
                if (tid == 0) return;
                if (pid == targetPid) ourTids.Add(tid);
                else ourTids.Remove(tid);
                return;
            }
            if (op == 2 || op == 4)
            {
                if (record.UserDataLength < 8) return;
                long q = record.UserData.ToInt64();
                uint tid = (uint)Marshal.ReadInt32(new IntPtr(q + 4));
                if (tid != 0) ourTids.Remove(tid);
            }
        }

        private static int PointerSize(ushort flags)
        {
            if ((flags & HeaderFlag64Bit) != 0) return 8;
            if ((flags & HeaderFlag32Bit) != 0) return 4;
            return IntPtr.Size;
        }

        private static ulong ReadPointer(long addr, int ptr)
        {
            return ptr == 8 ? (ulong)Marshal.ReadInt64(new IntPtr(addr))
                            : (uint)Marshal.ReadInt32(new IntPtr(addr));
        }

        private void PushStall(long start, long end, uint tid, StallKind kind)
        {
            long seq = stallWritten;
            int i = (int)(seq % StallRingSize);
            Volatile.Write(ref stallSeq[i], -1);
            stalls[i].StartQpc = start;
            stalls[i].EndQpc = end;
            stalls[i].Tid = tid;
            stalls[i].Kind = kind;
            Volatile.Write(ref stallSeq[i], seq);
            Volatile.Write(ref stallWritten, seq + 1);
        }

        public DiskStallResult Snapshot()
        {
            var r = new DiskStallResult();
            r.QpcFrequency = qpcFrequency;
            r.HardFaultCount = Volatile.Read(ref hardFaultCount);
            r.DiskIoCount = Volatile.Read(ref diskIoCount);
            r.EventsSeen = Volatile.Read(ref eventsSeen);
            r.UnpairedIo = Volatile.Read(ref unpairedIo);
            double msPerTick = qpcFrequency > 0 ? 1000.0 / qpcFrequency : 0;
            r.HardFaultMs = Volatile.Read(ref hardFaultTicks) * msPerTick;
            r.DiskIoMs = Volatile.Read(ref diskIoTicks) * msPerTick;

            long w = Volatile.Read(ref stallWritten);
            int n = (int)Math.Min(w, StallRingSize);
            long first = w > StallRingSize ? w - StallRingSize : 0;
            var o = new StallSpan[n];
            int k = 0;
            for (int j = 0; j < n; j++)
            {
                long want = first + j;
                int i = (int)(want % StallRingSize);
                if (Volatile.Read(ref stallSeq[i]) != want) continue;
                StallSpan c = stalls[i];
                if (Volatile.Read(ref stallSeq[i]) != want) continue;
                o[k++] = c;
            }
            if (k != o.Length) { var t = new StallSpan[k]; Array.Copy(o, t, k); o = t; }
            r.Stalls = o;
            r.StallsDropped = first;
            r.Ok = started || w > 0;
            if (!r.Ok) r.Error = Lang.T("t.diskstall.1");
            return r;
        }

        public DiskStallResult Stop()
        {
            lock (gate)
            {
                if (!started) { var e = new DiskStallResult(); e.Error = Lang.T("t.diskstall.2"); return e; }
                DiskStallResult r = Snapshot();
                StopStale();
                try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                if (worker != null) { try { worker.Join(2000); } catch { } }
                started = false;
                keepAlive = null;
                worker = null;
                traceHandle = 0;
                sessionHandle = 0;
                r.Ok = true;
                return r;
            }
        }

        public static void HealFromCrash() { StopStale(); }

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
            p.Wnode.ClientContext = 1;
            p.BufferSize = 128;
            p.MinimumBuffers = 16;
            p.MaximumBuffers = 64;
            p.LogFileMode = RealTimeMode | SystemLoggerMode | IndependentSessionMode;
            p.FlushTimer = 1;
            p.EnableFlags = FlagThread | FlagDiskIo | FlagDiskIoInit | FlagMemoryHardFaults;
            p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
            Marshal.StructureToPtr(p, props, false);
            return props;
        }

        private static void StopStale()
        {
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
                ControlTrace(0, SessionName, props, ControlStop);
            }
            catch { }
            finally { Marshal.FreeHGlobal(props); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WnodeHeader
        {
            public uint BufferSize, ProviderId;
            public ulong HistoricalContext;
            public long TimeStamp;
            public Guid Guid;
            public uint ClientContext, Flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTrace
        {
            public EventTraceHeaderStub Header;
            public uint InstanceId, ParentInstanceId;
            public Guid ParentGuid;
            public IntPtr MofData;
            public uint MofLength, ClientContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTraceHeaderStub
        {
            public ushort Size, FieldTypeFlags;
            public uint Version, ThreadId, ProcessId;
            public long TimeStamp;
            public Guid Guid;
            public uint KernelTime, UserTime;
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

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
        private static extern uint StartTrace(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
        private static extern uint ControlTrace(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW")]
        private static extern ulong OpenTrace(ref EventTraceLogfile logfile);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint CloseTrace(ulong handle);
    }
}
