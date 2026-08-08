// @author bdth 2074055628@qq.com
// 文件用途 直接消费内核进程提供程序的 ETW 实时会话 绕开 WMI 转发以降低发现延迟

using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class EtwProcessWatcher : IDisposable
    {
        private static readonly Guid KernelProcessProvider =
            new Guid("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
        private const ulong KeywordProcess = 0x10;
        private const ulong KeywordThread = 0x20;
        private const ulong KeywordImage = 0x40;
        private const ushort EventIdProcessStart = 1;

        private long eventsSeen;

        public long EventsSeen { get { return Interlocked.Read(ref eventsSeen); } }
        public ulong Keywords = KeywordProcess;

        private readonly string sessionName;
        private ulong session;
        private ulong trace = Native.InvalidProcessTraceHandle;
        private IntPtr propsBuffer = IntPtr.Zero;
        private Thread pump;
        private Native.EventRecordCallback callback;
        private volatile bool stopping;

        public event Action<int, long> ProcessStarted;
        public string LastError { get; private set; }

        public EtwProcessWatcher(string name)
        {
            sessionName = string.IsNullOrEmpty(name) ? "Pavise.ProcWatch" : name;
        }

        public bool Start()
        {
            int structSize = Marshal.SizeOf(typeof(Native.EventTraceProperties));
            int total = structSize + (sessionName.Length + 1) * 2 + 2;

            StopStaleSession(structSize, total);

            propsBuffer = Marshal.AllocHGlobal(total);
            for (int i = 0; i < total; i++) Marshal.WriteByte(propsBuffer, i, 0);

            var props = new Native.EventTraceProperties();
            props.WnodeBufferSize = (uint)total;
            props.WnodeFlags = Native.WnodeFlagTracedGuid;
            props.WnodeClientContext = 1;
            props.LogFileMode = Native.EventTraceRealTimeMode;
            props.BufferSize = 4;
            props.MinimumBuffers = 2;
            props.MaximumBuffers = 8;
            props.FlushTimer = 1;
            props.LoggerNameOffset = (uint)structSize;
            Marshal.StructureToPtr(props, propsBuffer, false);

            int rc = Native.StartTrace(out session, sessionName, propsBuffer);
            if (rc != 0)
            {
                LastError = "StartTrace 失败 rc=" + rc;
                Cleanup();
                return false;
            }

            var enableParams = new Native.EnableTraceParameters();
            enableParams.Version = 2;
            Guid provider = KernelProcessProvider;
            rc = Native.EnableTraceEx2(session, ref provider,
                Native.EventControlCodeEnableProvider, 4, Keywords, 0, 0, ref enableParams);
            if (rc != 0)
            {
                LastError = "EnableTraceEx2 失败 rc=" + rc;
                Cleanup();
                return false;
            }

            callback = OnEvent;
            var logfile = new Native.EventTraceLogfile();
            logfile.LoggerName = Marshal.StringToHGlobalUni(sessionName);
            logfile.ProcessTraceMode = Native.ProcessTraceModeRealTime
                | Native.ProcessTraceModeEventRecord;
            logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback);
            logfile.LogfileHeader.TimeZone = new byte[172];

            trace = Native.OpenTrace(ref logfile);
            Marshal.FreeHGlobal(logfile.LoggerName);
            if (trace == Native.InvalidProcessTraceHandle)
            {
                LastError = "OpenTrace 失败 win32=" + Marshal.GetLastWin32Error();
                Cleanup();
                return false;
            }

            pump = new Thread(Pump);
            pump.IsBackground = true;
            pump.Start();
            return true;
        }

        private void StopStaleSession(int structSize, int total)
        {
            IntPtr buf = Marshal.AllocHGlobal(total);
            try
            {
                for (int i = 0; i < total; i++) Marshal.WriteByte(buf, i, 0);
                var p = new Native.EventTraceProperties();
                p.WnodeBufferSize = (uint)total;
                p.LoggerNameOffset = (uint)structSize;
                Marshal.StructureToPtr(p, buf, false);
                Native.ControlTrace(0, sessionName, buf, Native.EventTraceControlStop);
            }
            catch { }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void Pump()
        {
            var handles = new ulong[] { trace };
            try { Native.ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        private void OnEvent(ref Native.EventRecord record)
        {
            if (stopping) return;
            Interlocked.Increment(ref eventsSeen);
            if (record.EventHeader.EventDescriptor.Id != EventIdProcessStart) return;
            if (record.UserData == IntPtr.Zero || record.UserDataLength < 4) return;
            int pid;
            try { pid = Marshal.ReadInt32(record.UserData); }
            catch { return; }
            Action<int, long> h = ProcessStarted;
            if (h != null) h(pid, record.EventHeader.TimeStamp);
        }

        private void Cleanup()
        {
            if (trace != Native.InvalidProcessTraceHandle)
            {
                try { Native.CloseTrace(trace); }
                catch { }
                trace = Native.InvalidProcessTraceHandle;
            }
            if (session != 0 && propsBuffer != IntPtr.Zero)
            {
                try { Native.ControlTrace(session, sessionName, propsBuffer, Native.EventTraceControlStop); }
                catch { }
                session = 0;
            }
            if (propsBuffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(propsBuffer);
                propsBuffer = IntPtr.Zero;
            }
        }

        public void Dispose()
        {
            stopping = true;
            Cleanup();
            if (pump != null)
            {
                try { pump.Join(3000); }
                catch { }
                pump = null;
            }
            callback = null;
        }
    }
}
