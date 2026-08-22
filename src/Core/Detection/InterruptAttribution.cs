// @author bdth 2074055628@qq.com
// 文件用途 用内核 ETW 会话抓 DPC 与 ISR 的例程地址与单次时长 映射到驱动模块
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class DriverInterrupt
    {
        public string Driver;
        public long Dpc;
        public long Isr;
        public double DpcTotalUs;
        public double DpcMaxUs;
        public long DpcOver500Us;
        public long DpcOver1Ms;
        public ulong CpuMask;
        public bool CpuMaskTruncated;
        public long BadDuration;
        public long[] DpcBuckets;
    }

    internal sealed class InterruptAttributionResult
    {
        public bool Ok;
        public string Error;
        public readonly List<DriverInterrupt> Drivers = new List<DriverInterrupt>();

        public uint EventsLost;
        public uint BuffersLost;
        public bool Lossy { get { return EventsLost > 0 || BuffersLost > 0; } }
    }

    internal sealed class InterruptAttribution
    {
        private const int WnodeFlagTracedGuid = 0x00020000;
        private const uint RealTimeMode = 0x00000100;
        private const uint SystemLoggerMode = 0x02000000;
        private const uint IndependentSessionMode = 0x08000000;
        private const uint ProcessModeRealTime = 0x00000100;
        private const uint ProcessModeRawTimestamp = 0x00001000;
        private const uint ProcessModeEventRecord = 0x10000000;
        private const uint ControlStop = 1;
        private const uint FlagDpc = 0x00000020;
        private const uint FlagInterrupt = 0x00000040;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorInvalidParameter = 87;

        private static readonly Guid SessionGuid = new Guid("7d1f8c2a-64b3-4c5e-9a1d-2e8f0b6c4d3a");
        private static readonly Guid PerfInfoGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
        private const string SessionName = "PaviseInterruptProbe";

        private const string AliveEventName = @"Global\Pavise_InterruptProbeAlive";
        private EventWaitHandle aliveOwned;

        public bool Busy { get; private set; }

        private bool TakeOwnership()
        {
            try
            {
                EventWaitHandle existing;
                if (EventWaitHandle.TryOpenExisting(AliveEventName, out existing))
                {
                    using (existing) { }
                    return false;
                }
            }
            catch { }
            try
            {
                bool createdNew;
                var h = new EventWaitHandle(false, EventResetMode.ManualReset, AliveEventName, out createdNew);
                if (!createdNew) { h.Close(); return false; }
                aliveOwned = h;
                return true;
            }
            catch { return true; }
        }

        private void ReleaseOwnership()
        {
            EventWaitHandle h = aliveOwned;
            aliveOwned = null;
            if (h != null) try { h.Close(); } catch { }
        }

        internal static readonly double[] BucketUpperUs =
            { 1, 2, 5, 10, 25, 50, 100, 250, 500, 1000, 2000, 5000, double.MaxValue };
        internal const int BucketCount = 13;
        private const int BucketOver500 = 9;
        private const int BucketOver1Ms = 10;


        private sealed class RoutineStat
        {
            public long Count;
            public long TotalTicks;
            public long MaxTicks;
            public long BadDuration;
            public ulong CpuMask;
            public bool CpuMaskTruncated;
            public readonly long[] Buckets = new long[BucketCount];
        }

        private const ushort HeaderFlag32Bit = 0x0020;
        private const ushort HeaderFlag64Bit = 0x0040;

        private readonly object gate = new object();
        private readonly Dictionary<ulong, RoutineStat> dpcHits = new Dictionary<ulong, RoutineStat>();
        private readonly Dictionary<ulong, RoutineStat> isrHits = new Dictionary<ulong, RoutineStat>();
        private readonly List<Module> modules = new List<Module>();
        private long qpcFrequency;
        private double usPerTick;
        private long sanityMaxTicks;
        private long dpcTotal, isrTotal;
        private ulong traceHandle;
        private Thread worker;
        private EventRecordCallback keepAlive;
        private volatile bool started;

        private sealed class Module { public ulong Base; public ulong End; public string Name; }

        public bool Start()
        {
            lock (gate)
            {
                if (started) return true;
                Busy = false;
                if (!TakeOwnership())
                {
                    Busy = true;
                    Logger.Log(Lang.T("log.interruptattribution.9"));
                    return false;
                }
                if (!Native.TryEnableDebugPrivilege()) { }
                LoadModules();

                IntPtr props = AllocProps();
                try
                {
                    ulong session;
                    uint rc = StartTrace(out session, SessionName, props);
                    if (rc == ErrorAlreadyExists)
                    {
                        StopStale();
                        Marshal.FreeHGlobal(props);
                        props = AllocProps();
                        rc = StartTrace(out session, SessionName, props);
                    }
                    if (rc == ErrorInvalidParameter)
                    {
                        Logger.Log(Lang.T("log.interruptattribution.1"));
                        ReleaseOwnership();
                        return false;
                    }
                    if (rc != 0) { Logger.Log(Lang.T("log.interruptattribution.2") + rc); ReleaseOwnership(); return false; }
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
                    Logger.Log(Lang.T("log.interruptattribution.3") + Marshal.GetLastWin32Error());
                    StopStale();
                    ReleaseOwnership();
                    return false;
                }

                long freq = logfile.LogfileHeader.PerfFreq;
                if (freq <= 0) freq = System.Diagnostics.Stopwatch.Frequency;
                qpcFrequency = freq;
                usPerTick = 1000000.0 / freq;
                sanityMaxTicks = freq;

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

        public InterruptAttributionResult Stop()
        {
            var result = new InterruptAttributionResult();
            lock (gate)
            {
                if (!started) { result.Error = Lang.T("t.interruptattribution.4"); return result; }
                uint lost, lostBuffers;
                StopStale(out lost, out lostBuffers);
                result.EventsLost = lost;
                result.BuffersLost = lostBuffers;
                try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                bool workerDone = true;
                if (worker != null) { try { workerDone = worker.Join(2000); } catch { workerDone = false; } }
                started = false;
                if (!workerDone)
                {
                    result.Error = Lang.T("t.interruptattribution.5");
                    return result;
                }
                ReleaseOwnership();
                keepAlive = null;

                var byMod = new Dictionary<string, DriverInterrupt>();
                var dpcBuckets = new Dictionary<string, long[]>();
                Fold(byMod, dpcBuckets, dpcHits, true);
                Fold(byMod, dpcBuckets, isrHits, false);
                foreach (KeyValuePair<string, DriverInterrupt> kv in byMod)
                {
                    DriverInterrupt d = kv.Value;
                    long[] b;
                    if (dpcBuckets.TryGetValue(kv.Key, out b))
                    {
                        d.DpcOver500Us = SumFrom(b, BucketOver500);
                        d.DpcOver1Ms = SumFrom(b, BucketOver1Ms);
                        d.DpcBuckets = (long[])b.Clone();
                    }
                    result.Drivers.Add(d);
                }
                result.Drivers.Sort(delegate(DriverInterrupt a, DriverInterrupt b)
                {
                    long ta = a.Dpc + a.Isr, tb = b.Dpc + b.Isr;
                    return tb.CompareTo(ta);
                });
                result.Ok = dpcTotal + isrTotal > 0;
                if (!result.Ok) result.Error = Lang.T("t.interruptattribution.6");
                else if (result.Lossy)
                {
                    result.Ok = false;
                    result.Error = Lang.F("t.interruptattribution.7", result.EventsLost, result.BuffersLost);
                    Logger.Log(Lang.F("log.interruptattribution.lossy", result.EventsLost, result.BuffersLost));
                }
                return result;
            }
        }

        private void Fold(Dictionary<string, DriverInterrupt> byMod,
            Dictionary<string, long[]> dpcBuckets, Dictionary<ulong, RoutineStat> hits, bool dpc)
        {
            foreach (KeyValuePair<ulong, RoutineStat> kv in hits)
            {
                string mod = Resolve(kv.Key);
                if (mod == null) continue;
                DriverInterrupt d;
                if (!byMod.TryGetValue(mod, out d)) { d = new DriverInterrupt { Driver = mod }; byMod[mod] = d; }
                RoutineStat st = kv.Value;
                d.CpuMask |= st.CpuMask;
                d.CpuMaskTruncated |= st.CpuMaskTruncated;
                d.BadDuration += st.BadDuration;
                double totalUs = st.TotalTicks * usPerTick;
                double maxUs = st.MaxTicks * usPerTick;
                if (dpc)
                {
                    d.Dpc += st.Count;
                    d.DpcTotalUs += totalUs;
                    if (maxUs > d.DpcMaxUs) d.DpcMaxUs = maxUs;
                    long[] b;
                    if (!dpcBuckets.TryGetValue(mod, out b)) { b = new long[BucketCount]; dpcBuckets[mod] = b; }
                    for (int i = 0; i < BucketCount; i++) b[i] += st.Buckets[i];
                }
                else
                {
                    d.Isr += st.Count;
                }
            }
        }

        internal static long SumFrom(long[] b, int startIndex)
        {
            long n = 0;
            if (b == null) return 0;
            for (int i = startIndex; i < BucketCount; i++) n += b[i];
            return n;
        }

        private void OnEvent(ref EventRecord record)
        {
            if (record.EventHeader.ProviderId != PerfInfoGuid) return;
            byte op = record.EventHeader.EventDescriptor.Opcode;
            bool isr = op == 67;
            bool dpc = op == 66 || op == 68 || op == 69;
            if (!isr && !dpc) return;
            if (record.UserData == IntPtr.Zero) return;

            ushort flags = record.EventHeader.Flags;
            int ptr = (flags & HeaderFlag64Bit) != 0 ? 8
                : (flags & HeaderFlag32Bit) != 0 ? 4 : IntPtr.Size;
            if (record.UserDataLength < 8 + ptr) return;

            long payload = record.UserData.ToInt64();
            long startQpc = Marshal.ReadInt64(new IntPtr(payload));
            ulong routine = ptr == 8
                ? (ulong)Marshal.ReadInt64(new IntPtr(payload + 8))
                : (uint)Marshal.ReadInt32(new IntPtr(payload + 8));
            long endQpc = record.EventHeader.TimeStamp;
            long ticks = endQpc - startQpc;
            bool timed = ticks >= 0 && ticks <= sanityMaxTicks;

            Dictionary<ulong, RoutineStat> map = isr ? isrHits : dpcHits;
            RoutineStat st;
            if (!map.TryGetValue(routine, out st)) { st = new RoutineStat(); map[routine] = st; }
            st.Count++;
            ushort cpu = record.BufferContext.ProcessorIndex;
            if (cpu < 64) st.CpuMask |= 1UL << cpu; else st.CpuMaskTruncated = true;
            if (!timed) st.BadDuration++;
            else
            {
                st.TotalTicks += ticks;
                if (ticks > st.MaxTicks) st.MaxTicks = ticks;
                st.Buckets[BucketOf(ticks)]++;
            }
            if (isr) isrTotal++; else dpcTotal++;
        }

        private int BucketOf(long ticks)
        {
            double us = ticks * usPerTick;
            for (int i = 0; i < BucketCount - 1; i++) if (us < BucketUpperUs[i]) return i;
            return BucketCount - 1;
        }

        private string Resolve(ulong addr)
        {
            for (int i = 0; i < modules.Count; i++)
                if (addr >= modules[i].Base && addr < modules[i].End) return modules[i].Name;
            return null;
        }

        private void LoadModules()
        {
            modules.Clear();
            int len = 0;
            NtQuerySystemInformation(11, IntPtr.Zero, 0, out len);
            len = Math.Max(len, 1 << 20) + 65536;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                int ret;
                if (NtQuerySystemInformation(11, buf, len, out ret) != 0) return;
                int count = Marshal.ReadInt32(buf);
                long p = buf.ToInt64() + IntPtr.Size;
                int stride = 16 + 8 + 4 + 4 + 2 + 2 + 2 + 2 + 256;
                for (int i = 0; i < count; i++)
                {
                    long rec = p + (long)i * stride;
                    ulong imgBase = (ulong)Marshal.ReadInt64(new IntPtr(rec + 16));
                    uint imgSize = (uint)Marshal.ReadInt32(new IntPtr(rec + 24));
                    if (imgBase == 0 || imgSize == 0) continue;
                    string full = Marshal.PtrToStringAnsi(new IntPtr(rec + 40));
                    if (string.IsNullOrEmpty(full)) continue;
                    int slash = full.LastIndexOf('\\');
                    string name = slash >= 0 ? full.Substring(slash + 1) : full;
                    modules.Add(new Module { Base = imgBase, End = imgBase + imgSize, Name = name });
                }
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
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
            p.Wnode.ClientContext = 1;
            p.BufferSize = 128;
            p.MinimumBuffers = 8;
            p.MaximumBuffers = 32;
            p.LogFileMode = RealTimeMode | SystemLoggerMode | IndependentSessionMode;
            p.FlushTimer = 1;
            p.EnableFlags = FlagDpc | FlagInterrupt;
            p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
            Marshal.StructureToPtr(p, props, false);
            return props;
        }

        public static void CleanupStaleSession()
        {
            try { StopStale(); } catch { }
        }

        public static void HealFromCrash() { StopStale(); }

        private static void StopStale()
        {
            uint lost, buffers;
            StopStale(out lost, out buffers);
        }

        private static void StopStale(out uint eventsLost, out uint buffersLost)
        {
            eventsLost = 0; buffersLost = 0;
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
                if (ControlTrace(0, SessionName, props, ControlStop) != 0) return;
                var done = (EventTraceProperties)Marshal.PtrToStructure(props, typeof(EventTraceProperties));
                eventsLost = done.EventsLost;
                buffersLost = done.RealTimeBuffersLost + done.LogBuffersLost;
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
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ulong OpenTrace(ref EventTraceLogfile logfile);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint CloseTrace(ulong handle);
        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    }
}
