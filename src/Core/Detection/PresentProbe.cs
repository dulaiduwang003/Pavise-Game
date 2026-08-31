// @author bdth 2074055628@qq.com
// 文件用途 独立 ETW 会话订阅 Microsoft-Windows-DxgKrnl 只取 Present(event 184)
//   逐帧记录 present 的 QPC 时刻与提交进程 pid 作为 present 长帧↔DPC 因果对齐的地基
//   与 InterruptAttribution 的中断会话完全独立 只把 provider 订阅从内核 EnableFlags
//   换成 EnableTraceEx2 按 manifest provider GUID 订阅 P/Invoke 布局照抄那边已验证的写法
//   会话用 ProcessModeRawTimestamp 保持原始 QPC 与中断会话同一根尺子 可直接对齐
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    // 一帧 present 提交 与 QueryPerformanceCounter 同一根 QPC 尺子
    internal struct PresentFrame
    {
        public long Qpc;   // 这次 present 的 QPC 时刻 会话用 RawTimestamp 时是原始 QPC
        public int Pid;    // 提交 present 的进程 用来过滤到目标游戏
    }

    internal sealed class PresentProbe
    {
        private const int WnodeFlagTracedGuid = 0x00020000;
        private const uint RealTimeMode = 0x00000100;               // EVENT_TRACE_REAL_TIME_MODE
        private const uint ProcessModeRealTime = 0x00000100;
        private const uint ProcessModeRawTimestamp = 0x00001000;    // 关闭时间戳换算 保留原始 QPC
        private const uint ProcessModeEventRecord = 0x10000000;
        private const uint ControlStop = 1;
        private const int ErrorAlreadyExists = 183;

        // EnableTraceEx2 参数
        private const uint EnableProvider = 1;      // EVENT_CONTROL_CODE_ENABLE_PROVIDER
        private const byte LevelInformation = 4;    // TRACE_LEVEL_INFORMATION
        // Present 关键词还包含 VSync/HSync 队列和同步信号等事件 不能单独作为低开销过滤器
        // 必须同时用 EVENT_FILTER_TYPE_EVENT_ID 在 ETW 写入前限定 184 回调再作防御性复核
        private const ulong MatchAnyKeyword = 0x8000000;
        private const uint EnableTimeoutMs = 1000;

        // present 事件号 从 DxgKrnl manifest 钉死
        private const ushort EventPresent = 184;

        // 独立会话 名与 GUID 都不碰任何现有会话(中断会话 selftest 里的 PresentTrace)
        private const string SessionName = "Pavise_PresentProbe";
        private static readonly Guid SessionGuid = new Guid("a3f6d1c4-2b58-4e7a-9c31-6f0d8b2e5a17");
        private static readonly Guid DxgKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");

        // 逐帧时间线 消费线程(ProcessTrace 回调)单独 append Stop 且 worker.Join 之后再读
        //   单生产单消费 无需锁 与聚合无关 只做原始落点
        private const int FrameCap = 4000000;   // 上限约 4M 帧 防长采样把内存吃穿 到顶后停记并置 Truncated
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

        // 会话所用 QPC 频率 与落盘 present_qpc 同一根尺子
        public long QpcFrequency { get { return qpcFrequency; } }

        // Stop 且 worker.Join 之后调用 取回逐帧时间线
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
                    // 有同名残留会话 先停掉再重建 一次
                    StopStale();
                    Marshal.FreeHGlobal(props);
                    props = AllocProps();
                    rc = StartTrace(out sessionHandle, SessionName, props);
                }
                if (rc != 0)
                {
                    LastError = rc;
                    Logger.Log("PRESENT StartTrace 失败 win32=" + rc);
                    return false;
                }
            }
            finally { Marshal.FreeHGlobal(props); }

            // DxgKrnl 是内核驱动注册的 manifest provider 不套只适用于用户态 provider 的 PID scope
            // 过滤参数 descriptor 和变长载荷在整个 EnableTraceEx2 调用期间由同一个 HGlobal 持有
            // 配置失败就放弃本轮 Present 不回退成订阅整族事件的高流量会话
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
                Logger.Log("PRESENT 事件 ID 过滤配置异常，本轮跳过，不启用宽范围采集 " + ex.GetType().Name);
                StopStale();
                return false;
            }
            if (erc != 0)
            {
                LastError = erc;
                Logger.Log("PRESENT 事件 ID 过滤启用失败，本轮跳过，不启用宽范围采集 win32=" + erc);
                StopStale();
                return false;
            }

            keepAlive = OnEvent;
            var logfile = new EventTraceLogfile();
            logfile.LoggerName = Marshal.StringToHGlobalUni(SessionName);
            // RawTimestamp 保留原始 QPC 与中断会话同尺
            logfile.ProcessTraceMode = ProcessModeRealTime | ProcessModeEventRecord | ProcessModeRawTimestamp;
            logfile.EventRecordCallbackPtr = Marshal.GetFunctionPointerForDelegate(keepAlive);
            traceHandle = OpenTrace(ref logfile);
            if (traceHandle == 0xFFFFFFFFFFFFFFFF || traceHandle == 0)
            {
                LastError = (uint)Marshal.GetLastWin32Error();
                Logger.Log("PRESENT OpenTrace 失败 win32=" + LastError);
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
                // 正常路径只有 RequestStop 发出 ControlTrace 后消费线程才该返回 提前返回即使
                // Join 成功 丢事件计数为 0 也只覆盖了半局 不能生成负证据
                if (!stopRequested) consumerExitedEarly = true;
            }
        }

        // 只收口事件来源 不等待消费线程排空 调用方可先关闭 PRESENT 窗口
        // 立即封存 DPC 的新鲜 proof 再在退出收尾时调用 Stop 取完整时间线
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
                // 多个收尾调用共用一次排空 等待会释放锁 RequestStop 不会被 Join 阻塞
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
                // 正常停会话后消费者自行排空 停失败则先 CloseTrace 解除消费者
                // 这种路径即使 Join 成功也不能算完整采集
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
                    // 排空超时后仍可能回调 保留委托直到消费线程结束
                    if (workerDone) keepAlive = null;
                    stopFinishing = false;
                    Monitor.PulseAll(stopGate);
                }
            }
            Logger.Log("PRESENT 会话已停止 Present事件=" + frames.Count
                + (Truncated ? "(已截断)" : "") + " 丢事件=" + EventsLost + " 丢缓冲=" + BuffersLost);
            if (!workerDone) Logger.Log("PRESENT 会话排空超时 本局时间线作废");
            if (!stopSucceeded) Logger.Log("PRESENT 会话停止失败 本局时间线作废 win32=" + stopError);
            if (consumerExitedEarly) Logger.Log("PRESENT 消费线程提前退出 本局时间线作废 win32=" + LastError);
        }

        // 唯一的消费点 在 ProcessTrace 工作线程上执行 只 append 不做别的
        private void OnEvent(ref EventRecord record)
        {
            // provider 已按 GUID 订阅 这里再核一遍 防同会话混入其它 provider
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
            p.Wnode.ClientContext = 1;   // QPC 时钟
            p.BufferSize = 256;
            p.MinimumBuffers = 16;
            p.MaximumBuffers = 128;
            // 普通实时会话 不设内核 EnableFlags(那是内核 logger 专用) provider 靠 EnableTraceEx2 挂
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

        // EVENT_FILTER_EVENT_ID 的 BOOLEAN 是 1 字节 不是默认 P/Invoke BOOL 的 4 字节
        // 当前只订阅一个事件 sizeof(header) 4 + USHORT Events[1] 2 = 6 字节
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
            internal ulong Ptr;  // ULONGLONG，在 32 位控制器里也必须是 8 字节。
            internal uint Size;
            internal uint Type;
        }

        // 仅分配/序列化内存 不调用任何 ETW API 纯自测可核验整个指针链和释放路径
        internal sealed class EventIdFilterBuffer : IDisposable
        {
            internal const uint EventIdFilterType = 0x80000200;
            private IntPtr memory;

            internal EventIdFilterBuffer()
            {
                int parametersSize = Marshal.SizeOf(typeof(Native.EnableTraceParameters));
                // descriptor 含 ULONGLONG 显式按 8 字节对齐 兼容 x86/x64 控制器
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

        // 以下 interop 结构与签名照抄 InterruptAttribution 已验证的布局 只是复制一份不共享
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
        // manifest provider 订阅入口 内核会话没有这一步 是本探针与中断会话的唯一实质差别
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
