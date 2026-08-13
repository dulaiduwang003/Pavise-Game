// @author bdth 2074055628@qq.com
// 文件用途 用内核 ETW 会话抓 DPC 与 ISR 例程地址 映射到驱动模块 找出中断来源

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
        public double DpcShare;
        public double IsrShare;
    }

    internal sealed class InterruptAttributionResult
    {
        public bool Ok;
        public string Error;
        public long DpcTotal;
        public long IsrTotal;
        public readonly List<DriverInterrupt> Drivers = new List<DriverInterrupt>();

        public string TopDpc { get { return Drivers.Count > 0 ? Drivers[0].Driver : null; } }
    }

    internal sealed class InterruptAttribution
    {
        private const int WnodeFlagTracedGuid = 0x00020000;
        private const uint RealTimeMode = 0x00000100;
        private const uint ProcessModeRealTime = 0x00000100;
        private const uint ProcessModeEventRecord = 0x10000000;
        private const uint ControlStop = 1;
        private const uint FlagDpc = 0x00000020;
        private const uint FlagInterrupt = 0x00000040;
        private const int ErrorAlreadyExists = 183;

        private static readonly Guid SystemTraceControlGuid = new Guid("9e814aad-3204-11d2-9a82-006008a86939");
        private static readonly Guid PerfInfoGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
        private const string KernelLoggerName = "NT Kernel Logger";

        private readonly object gate = new object();
        private readonly Dictionary<ulong, long> dpcHits = new Dictionary<ulong, long>();
        private readonly Dictionary<ulong, long> isrHits = new Dictionary<ulong, long>();
        private readonly List<Module> modules = new List<Module>();
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
                if (!Native.TryEnableDebugPrivilege()) { }
                LoadModules();

                IntPtr props = AllocProps();
                try
                {
                    ulong session;
                    uint rc = StartTrace(out session, KernelLoggerName, props);
                    if (rc == ErrorAlreadyExists)
                    {
                        StopStale();
                        Marshal.FreeHGlobal(props);
                        props = AllocProps();
                        rc = StartTrace(out session, KernelLoggerName, props);
                    }
                    if (rc != 0) { Logger.Log("中断来源 会话创建失败 " + rc); return false; }
                }
                finally { Marshal.FreeHGlobal(props); }

                keepAlive = OnEvent;
                var logfile = new EventTraceLogfile();
                logfile.LoggerName = Marshal.StringToHGlobalUni(KernelLoggerName);
                logfile.ProcessTraceMode = ProcessModeRealTime | ProcessModeEventRecord;
                logfile.EventRecordCallbackPtr = Marshal.GetFunctionPointerForDelegate(keepAlive);
                traceHandle = OpenTrace(ref logfile);
                if (traceHandle == 0xFFFFFFFFFFFFFFFF || traceHandle == 0)
                {
                    Logger.Log("中断来源 打开实时会话失败 " + Marshal.GetLastWin32Error());
                    StopStale();
                    return false;
                }

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
                if (!started) { result.Error = "未启动"; return result; }
                StopStale();
                try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                if (worker != null) { try { worker.Join(2000); } catch { } }
                started = false;
                keepAlive = null;

                result.DpcTotal = dpcTotal;
                result.IsrTotal = isrTotal;
                var byMod = new Dictionary<string, DriverInterrupt>();
                Fold(byMod, dpcHits, true);
                Fold(byMod, isrHits, false);
                foreach (KeyValuePair<string, DriverInterrupt> kv in byMod)
                {
                    DriverInterrupt d = kv.Value;
                    d.DpcShare = dpcTotal > 0 ? (double)d.Dpc / dpcTotal : 0;
                    d.IsrShare = isrTotal > 0 ? (double)d.Isr / isrTotal : 0;
                    result.Drivers.Add(d);
                }
                result.Drivers.Sort(delegate(DriverInterrupt a, DriverInterrupt b)
                {
                    long ta = a.Dpc + a.Isr, tb = b.Dpc + b.Isr;
                    return tb.CompareTo(ta);
                });
                result.Ok = dpcTotal + isrTotal > 0;
                if (!result.Ok) result.Error = "窗口内没有采到中断事件";
                return result;
            }
        }

        private void Fold(Dictionary<string, DriverInterrupt> byMod, Dictionary<ulong, long> hits, bool dpc)
        {
            foreach (KeyValuePair<ulong, long> kv in hits)
            {
                string mod = Resolve(kv.Key);
                if (mod == null) continue;
                DriverInterrupt d;
                if (!byMod.TryGetValue(mod, out d)) { d = new DriverInterrupt { Driver = mod }; byMod[mod] = d; }
                if (dpc) d.Dpc += kv.Value; else d.Isr += kv.Value;
            }
        }

        private void OnEvent(ref EventRecord record)
        {
            if (record.EventHeader.ProviderId != PerfInfoGuid) return;
            byte op = record.EventHeader.EventDescriptor.Opcode;
            bool isr = op == 67;
            bool dpc = op == 66 || op == 68 || op == 69;
            if (!isr && !dpc) return;
            if (record.UserData == IntPtr.Zero || record.UserDataLength < 16) return;
            ulong routine = (ulong)Marshal.ReadInt64(new IntPtr(record.UserData.ToInt64() + 8));
            if (isr) { isrTotal++; long v; isrHits.TryGetValue(routine, out v); isrHits[routine] = v + 1; }
            else { dpcTotal++; long v; dpcHits.TryGetValue(routine, out v); dpcHits[routine] = v + 1; }
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
            int nameBytes = (KernelLoggerName.Length + 1) * 2;
            int size = Marshal.SizeOf(typeof(EventTraceProperties)) + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(props, i, 0);
            var p = new EventTraceProperties();
            p.Wnode.BufferSize = (uint)size;
            p.Wnode.Flags = WnodeFlagTracedGuid;
            p.Wnode.Guid = SystemTraceControlGuid;
            p.Wnode.ClientContext = 1;
            p.BufferSize = 128;
            p.MinimumBuffers = 8;
            p.MaximumBuffers = 32;
            p.LogFileMode = RealTimeMode;
            p.FlushTimer = 1;
            p.EnableFlags = FlagDpc | FlagInterrupt;
            p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
            Marshal.StructureToPtr(p, props, false);
            return props;
        }

        private static void StopStale()
        {
            int nameBytes = (KernelLoggerName.Length + 1) * 2;
            int size = Marshal.SizeOf(typeof(EventTraceProperties)) + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(props, i, 0);
                var p = new EventTraceProperties();
                p.Wnode.BufferSize = (uint)size;
                p.Wnode.Guid = SystemTraceControlGuid;
                p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
                Marshal.StructureToPtr(p, props, false);
                ControlTrace(0, KernelLoggerName, props, ControlStop);
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
            public byte ProcessorNumber, Alignment;
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
