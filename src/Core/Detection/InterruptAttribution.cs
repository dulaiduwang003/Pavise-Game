// @author bdth 2074055628@qq.com
// 文件用途 内核中断与 DPC 归属采样的启动与时间线
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
        public bool Incomplete;
        public bool Lossy { get { return EventsLost > 0 || BuffersLost > 0; } }
    }

    internal sealed partial class InterruptAttribution
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
        private const int ErrorAccessDenied = 5;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorInvalidParameter = 87;

        private static readonly Guid SessionGuid = new Guid("7d1f8c2a-64b3-4c5e-9a1d-2e8f0b6c4d3a");
        private static readonly Guid PerfInfoGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
        private const string SessionName = "PaviseInterruptProbe";

        private const string AliveEventName = @"Global\Pavise_InterruptProbeAlive";
        private EventWaitHandle aliveOwned;

        public bool Busy { get; private set; }

        // Start() 有四条各不相同的失败路径 调用方只拿到一个 false 会把它们说成同一个原因
        //   历史上全部报成 需要管理员权限 而权限在进这个函数之前就已经查过了 只会误导
        //   Busy 单独一路 其余三路把真实原因连错误码放这里 由调用方原样呈现
        public string FailDetail { get; private set; }

        // Start() 失败后给用户看的那句话 探针被占和真失败要分开说
        public static string StartFailureText(InterruptAttribution ia)
        {
            if (ia == null) return Lang.T("irqmove.nosession");
            if (ia.Busy) return Lang.T("irq.probe.busy");
            return string.IsNullOrEmpty(ia.FailDetail)
                ? Lang.T("irqmove.nosession") : ia.FailDetail;
        }

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
        private volatile bool stopRequested;
        private volatile bool consumerExitedEarly;
        private volatile bool processTraceSucceeded;

        private sealed class Module { public ulong Base; public ulong End; public string Name; public string ImagePath; }

        // 逐 DPC 事件时间线 release 可用 由运行时开关控制 默认关 关时零开销
        //   present 长帧↔DPC 因果对齐要的原料 与聚合路径完全并行 聚合逻辑一行不改
        //   写入发生在消费线程 OnEvent 里 只 append 一个极轻的 struct(不做模块解析)
        //   读取只在 Stop 且 worker.Join 之后 单生产单消费无需锁
        //   曾是 #if PAVISE_SELFTEST 的证伪实验台出口 现提升为 release 常规能力
        internal struct DpcTimelineEntry
        {
            public long StartQpc; // DPC 开始时刻，用于与长帧区间做真正的重叠判定
            public long EndQpc;   // DPC 结束时刻 与 QueryPerformanceCounter 同一根 QPC 尺子
            public string Module; // Stop 之后统一解析 热路径不碰
            public ushort Cpu;
            public double DpcUs;  // 本次 DPC 时长 微秒
        }

        // 热路径只落这个更轻的原始标记 模块地址留到 Stop 后再解析 避免每个 DPC 都线性扫模块表
        private struct DpcMark
        {
            public long StartQpc;
            public long EndQpc;
            public ulong Routine;
            public ushort Cpu;
            public double DpcUs;
        }

        // 上限封顶止损 照 PresentProbe.FrameCap 的做法 到顶置 Truncated 停记 防长局把内存吃穿
        //   DpcMark 约 32 字节 200 万条约 64MB 会话期占用 结束即释放
        private const int DpcTimelineCap = 2000000;
        private volatile bool captureTimeline;      // 运行时开关 默认关
        private bool timelineTruncated;
        private List<DpcMark> dpcMarks;
        private List<DpcTimelineEntry> dpcTimeline;

        // 采集前调用(Start 之前) 打开逐事件 DPC 时间线记录
        internal void EnableDpcTimeline()
        {
            captureTimeline = true;
            timelineTruncated = false;
            dpcMarks = new List<DpcMark>(1 << 18);
        }
        // Stop() 之后取回本局逐事件 DPC 时间线(已解析模块名) 未采到返回 null
        internal List<DpcTimelineEntry> DpcTimeline { get { return dpcTimeline; } }
        // 时间线是否因到达上限被截断
        internal bool DpcTimelineTruncated { get { return timelineTruncated; } }
        // 会话所用 QPC 频率 与 present 会话同一根尺子
        internal long QpcFrequencyValue { get { return qpcFrequency; } }

        // Stop 且 worker.Join 之后调用 把原始标记按模块地址解析成时间线
        //   单次解析用小缓存 独立例程地址很少 均摊 O(条数)
        private void BuildDpcTimeline()
        {
            dpcTimeline = null;
            List<DpcMark> marks = dpcMarks;
            dpcMarks = null;
            if (!captureTimeline || marks == null) return;
            var cache = new Dictionary<ulong, string>();
            var tl = new List<DpcTimelineEntry>(marks.Count);
            for (int i = 0; i < marks.Count; i++)
            {
                DpcMark m = marks[i];
                string mod;
                if (!cache.TryGetValue(m.Routine, out mod))
                {
                    mod = Resolve(m.Routine) ?? "?";
                    cache[m.Routine] = mod;
                }
                tl.Add(new DpcTimelineEntry
                    { StartQpc = m.StartQpc, EndQpc = m.EndQpc, Module = mod, Cpu = m.Cpu, DpcUs = m.DpcUs });
            }
            dpcTimeline = tl;
        }

        // 只订阅 DPC 不订阅 ISR 事件量大约减半 内核侧和消费线程的负担同步减半
        //   对局观测的台账和裁决只用 DPC 证据 ISR 计数只有体检类一次性扫描用得上
        //   必须在 Start 之前调用
        public void EnableDpcOnly()
        {
            lock (gate) if (!started) dpcOnly = true;
        }

        private bool dpcOnly;

        public bool Start()
        {
            lock (gate)
            {
                if (started) return true;
                Busy = false;
                FailDetail = null;
                if (!TakeOwnership())
                {
                    Busy = true;
                    Logger.Log(Lang.T("log.interruptattribution.9"));
                    return false;
                }
                if (!Native.TryEnableDebugPrivilege()) { }
                string moduleError;
                if (!LoadModules(out moduleError))
                {
                    FailDetail = moduleError;
                    Logger.Warn("IRQ " + moduleError);
                    ReleaseOwnership();
                    return false;
                }

                uint enableFlags = dpcOnly ? FlagDpc : FlagDpc | FlagInterrupt;
                IntPtr props = AllocProps(enableFlags);
                try
                {
                    ulong session;
                    uint rc = StartTrace(out session, SessionName, props);
                    if (rc == ErrorAlreadyExists)
                    {
                        StopStale();
                        Marshal.FreeHGlobal(props);
                        props = AllocProps(enableFlags);
                        rc = StartTrace(out session, SessionName, props);
                    }
                    if (rc == ErrorInvalidParameter)
                    {
                        Logger.Log(Lang.T("log.interruptattribution.1"));
                        FailDetail = Lang.T("irq.fail.nokernel");
                        ReleaseOwnership();
                        return false;
                    }
                    if (rc != 0)
                    {
                        Logger.Warn(Lang.T("log.interruptattribution.2") + rc);
                        // 代码 5 两个来源要分开说 用户拿裸代码没法行动
                        //   未提升是最常见的 开关状态存注册表 上次提升会话开的开关
                        //   这次普通权限启动仍显示已开启 采样一启动就被拒
                        bool elevated = false;
                        try { elevated = Native.IsElevated(); } catch { }
                        FailDetail = rc == ErrorAccessDenied
                            ? (elevated ? Lang.T("irq.fail.denied") : Lang.T("irq.state.needadmin"))
                            : Lang.T("irq.fail.start") + rc;
                        ReleaseOwnership();
                        return false;
                    }
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
                    int openErr = Marshal.GetLastWin32Error();
                    Logger.Warn(Lang.T("log.interruptattribution.3") + openErr);
                    FailDetail = Lang.T("irq.fail.open") + openErr;
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
                // 对局中的设备中断可能短时爆发 消费回调若被调度压后 缓冲回收会变慢并丢事件
                //   排空线程只负责读取 ETW 缓冲 大部分时间阻塞等待 提高优先级可减少采样自身造成的丢失
                try { worker.Priority = ThreadPriority.Highest; } catch { }
                stopRequested = false;
                consumerExitedEarly = false;
                processTraceSucceeded = false;
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
                uint rc = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
                processTraceSucceeded = rc == 0;
                if (rc != 0) consumerExitedEarly = true;
            }
            catch { consumerExitedEarly = true; }
            finally { if (!stopRequested) consumerExitedEarly = true; }
        }
    }
}
