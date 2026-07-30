// @author bdth 2074055628@qq.com
// 文件用途 实时 ETW 会话监听 DXGI 与 D3D9 的 Present 事件 提供逐帧时间戳

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace AegisApp
{
    internal sealed class EtwFrameTrace
    {
        private const string SessionName = "AegisEvidenceTrace";
        private static readonly Guid DxgiProvider = new Guid("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
        private static readonly Guid D3D9Provider = new Guid("783ACA0A-790E-4D7F-8451-AA850511C6B9");
        private const ushort DxgiPresentStart = 42;
        private const ushort D3D9PresentStart = 1;

        private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
        private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
        private const uint EVENT_TRACE_CONTROL_STOP = 1;
        private const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
        private const byte TRACE_LEVEL_INFORMATION = 4;
        private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
        private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
        private const ulong INVALID_PROCESSTRACE_HANDLE = 0xFFFFFFFFFFFFFFFF;

        private const int PropsSize = 120;
        private const int NameBufBytes = 2048;

        private readonly Action<int, long> onPresent;
        private readonly EventRecordCallback callback;
        private ulong sessionHandle;
        private ulong traceHandle = INVALID_PROCESSTRACE_HANDLE;
        private Thread consumer;
        private long dxgiHi, dxgiLo, d3d9Hi, d3d9Lo;

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void EventRecordCallback(IntPtr record);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct EventTraceLogfile
        {
            public IntPtr LogFileName;
            public IntPtr LoggerName;
            public long CurrentTime;
            public uint BuffersRead;
            public uint ProcessTraceMode;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 88)]
            public byte[] CurrentEvent;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 280)]
            public byte[] LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize;
            public uint Filled;
            public uint EventsLost;
            [MarshalAs(UnmanagedType.FunctionPtr)]
            public EventRecordCallback EventCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int StartTraceW(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int ControlTraceW(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")]
        private static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint code,
            byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr enableParams);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);
        [DllImport("advapi32.dll")]
        private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")]
        private static extern int CloseTrace(ulong handle);

        public EtwFrameTrace(Action<int, long> presentSink)
        {
            onPresent = presentSink;
            callback = HandleEvent;
            SplitGuid(DxgiProvider, out dxgiLo, out dxgiHi);
            SplitGuid(D3D9Provider, out d3d9Lo, out d3d9Hi);
        }

        private static void SplitGuid(Guid guid, out long lo, out long hi)
        {
            byte[] bytes = guid.ToByteArray();
            lo = BitConverter.ToInt64(bytes, 0);
            hi = BitConverter.ToInt64(bytes, 8);
        }

        public bool Start()
        {
            StopStaleSession();
            IntPtr props = BuildProperties();
            try
            {
                int status = StartTraceW(out sessionHandle, SessionName, props);
                if (status != 0)
                {
                    Logger.Log("证据采集：ETW 会话启动失败 (win32 " + status + ")");
                    return false;
                }
                Guid dxgi = DxgiProvider, d3d9 = D3D9Provider;
                EnableTraceEx2(sessionHandle, ref dxgi, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                    TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);
                EnableTraceEx2(sessionHandle, ref d3d9, EVENT_CONTROL_CODE_ENABLE_PROVIDER,
                    TRACE_LEVEL_INFORMATION, 0, 0, 0, IntPtr.Zero);

                var logfile = new EventTraceLogfile
                {
                    LoggerName = Marshal.StringToHGlobalUni(SessionName),
                    ProcessTraceMode = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD,
                    CurrentEvent = new byte[88],
                    LogfileHeader = new byte[280],
                    EventCallback = callback
                };
                try
                {
                    traceHandle = OpenTraceW(ref logfile);
                }
                finally { Marshal.FreeHGlobal(logfile.LoggerName); }
                if (traceHandle == INVALID_PROCESSTRACE_HANDLE)
                {
                    Logger.Log("证据采集：ETW 消费句柄打开失败");
                    StopSession();
                    return false;
                }
                consumer = new Thread(ConsumeLoop);
                consumer.IsBackground = true;
                consumer.Priority = ThreadPriority.BelowNormal;
                consumer.Start();
                return true;
            }
            finally { Marshal.FreeHGlobal(props); }
        }

        public void Stop()
        {
            StopSession();
            if (traceHandle != INVALID_PROCESSTRACE_HANDLE)
            {
                CloseTrace(traceHandle);
                traceHandle = INVALID_PROCESSTRACE_HANDLE;
            }
            if (consumer != null) { consumer.Join(3000); consumer = null; }
        }

        private void ConsumeLoop()
        {
            try { ProcessTrace(new[] { traceHandle }, 1, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        private void HandleEvent(IntPtr record)
        {
            long provLo = Marshal.ReadInt64(record, 24);
            long provHi = Marshal.ReadInt64(record, 32);
            ushort id = (ushort)Marshal.ReadInt16(record, 40);
            bool present =
                provLo == dxgiLo && provHi == dxgiHi && id == DxgiPresentStart
                || provLo == d3d9Lo && provHi == d3d9Hi && id == D3D9PresentStart;
            if (!present) return;
            int pid = Marshal.ReadInt32(record, 12);
            long qpc = Marshal.ReadInt64(record, 16);
            onPresent(pid, qpc);
        }

        private static IntPtr BuildProperties()
        {
            int total = PropsSize + NameBufBytes * 2;
            IntPtr props = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i += 8) Marshal.WriteInt64(props, i, 0);
            Marshal.WriteInt32(props, 0, total);
            Marshal.WriteInt32(props, 40, 1);
            Marshal.WriteInt32(props, 44, unchecked((int)WNODE_FLAG_TRACED_GUID));
            Marshal.WriteInt32(props, 48, 64);
            Marshal.WriteInt32(props, 52, 4);
            Marshal.WriteInt32(props, 56, 8);
            Marshal.WriteInt32(props, 64, unchecked((int)EVENT_TRACE_REAL_TIME_MODE));
            Marshal.WriteInt32(props, 68, 1);
            Marshal.WriteInt32(props, 112, PropsSize + NameBufBytes);
            Marshal.WriteInt32(props, 116, PropsSize);
            return props;
        }

        private void StopSession()
        {
            if (sessionHandle == 0) return;
            IntPtr props = BuildProperties();
            try { ControlTraceW(sessionHandle, null, props, EVENT_TRACE_CONTROL_STOP); }
            finally { Marshal.FreeHGlobal(props); }
            sessionHandle = 0;
        }

        public static void StopStaleSession()
        {
            IntPtr props = BuildProperties();
            try { ControlTraceW(0, SessionName, props, EVENT_TRACE_CONTROL_STOP); }
            catch { }
            finally { Marshal.FreeHGlobal(props); }
        }
    }
}
