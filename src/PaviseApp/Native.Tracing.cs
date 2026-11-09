using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
    internal static partial class Native
    {

#pragma warning disable 0649 // 这些互操作字段由 ETW 填充。
        public struct EventTraceProperties
        {
            public uint WnodeBufferSize;

            public uint WnodeProviderId;

            public ulong WnodeHistoricalContext;

            public long WnodeTimeStamp;

            public Guid WnodeGuid;

            public uint WnodeClientContext;

            public uint WnodeFlags;

            public uint BufferSize;

            public uint MinimumBuffers;

            public uint MaximumBuffers;

            public uint MaximumFileSize;

            public uint LogFileMode;

            public uint FlushTimer;

            public uint EnableFlags;

            public int AgeLimit;

            public uint NumberOfBuffers;

            public uint FreeBuffers;

            public uint EventsLost;

            public uint BuffersWritten;

            public uint LogBuffersLost;

            public uint RealTimeBuffersLost;

            public IntPtr LoggerThreadId;

            public uint LogFileNameOffset;

            public uint LoggerNameOffset;
        }
#pragma warning restore 0649

        public struct EventTraceHeader
        {
            public ushort Size;

            public ushort FieldTypeFlags;

            public uint Version;

            public uint ThreadId;

            public uint ProcessId;

            public long TimeStamp;

            public Guid Guid;

            public ulong ProcessorTime;
        }

        public struct EventTraceItem
        {
            public EventTraceHeader Header;

            public uint InstanceId;

            public uint ParentInstanceId;

            public Guid ParentGuid;

            public IntPtr MofData;

            public uint MofLength;

            public uint ClientContext;
        }

        public struct TraceLogfileHeader
        {
            public uint BufferSize;

            public uint Version;

            public uint ProviderVersion;

            public uint NumberOfProcessors;

            public long EndTime;

            public uint TimerResolution;

            public uint MaximumFileSize;

            public uint LogFileMode;

            public uint BuffersWritten;

            public Guid LogInstanceGuid;

            public IntPtr LoggerName;

            public IntPtr LogFileName;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 172)]
            public byte[] TimeZone;

            public long BootTime;

            public long PerfFreq;

            public long StartTime;

            public uint ReservedFlags;

            public uint BuffersLost;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct EventTraceLogfile
        {
            public IntPtr LogFileName;

            public IntPtr LoggerName;

            public long CurrentTime;

            public uint BuffersRead;

            public uint ProcessTraceMode;

            public EventTraceItem CurrentEvent;

            public TraceLogfileHeader LogfileHeader;

            public IntPtr BufferCallback;

            public uint BufferSize;

            public uint Filled;

            public uint EventsLost;

            public IntPtr EventRecordCallback;

            public uint IsKernelTrace;

            public IntPtr Context;
        }

        public struct EventDescriptor
        {
            public ushort Id;

            public byte Version;

            public byte Channel;

            public byte Level;

            public byte Opcode;

            public ushort Task;

            public ulong Keyword;
        }

        public struct EventHeader
        {
            public ushort Size;

            public ushort HeaderType;

            public ushort Flags;

            public ushort EventProperty;

            public uint ThreadId;

            public uint ProcessId;

            public long TimeStamp;

            public Guid ProviderId;

            public EventDescriptor EventDescriptor;

            public ulong ProcessorTime;

            public Guid ActivityId;
        }

        public struct EventRecord
        {
            public EventHeader EventHeader;

            public uint BufferContext;

            public ushort ExtendedDataCount;

            public ushort UserDataLength;

            public IntPtr ExtendedData;

            public IntPtr UserData;

            public IntPtr UserContext;
        }

        public struct EnableTraceParameters
        {
            public uint Version;

            public uint EnableProperty;

            public uint ControlFlags;

            public Guid SourceId;

            public IntPtr EnableFilterDesc;

            public uint FilterDescCount;
        }

        public delegate void EventRecordCallback(ref EventRecord record);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartTraceW", SetLastError = true)]
        public static extern int StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW", SetLastError = true)]
        public static extern int ControlTrace(ulong sessionHandle, string sessionName, IntPtr properties, uint controlCode);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, ref EnableTraceParameters enableParameters);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW", SetLastError = true)]
        public static extern ulong OpenTrace(ref EventTraceLogfile logfile);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int ProcessTrace(ulong[] handles, uint handleCount, IntPtr startTime, IntPtr endTime);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int CloseTrace(ulong handle);

    }
}
