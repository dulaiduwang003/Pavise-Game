// @author bdth 2074055628@qq.com
// File purpose Interop declarations for ETW session stop and kernel sampling
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class InterruptAttribution
    {
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
