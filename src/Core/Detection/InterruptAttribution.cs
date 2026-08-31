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
        public bool Incomplete;
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
                    Logger.Log("IRQ " + moduleError);
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
                        Logger.Log(Lang.T("log.interruptattribution.2") + rc);
                        FailDetail = Lang.T("irq.fail.start") + rc;
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
                    Logger.Log(Lang.T("log.interruptattribution.3") + openErr);
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

        internal static bool CaptureComplete(bool stopSucceeded, bool workerDone,
            bool processSucceeded, bool exitedEarly)
        {
            return stopSucceeded && workerDone && processSucceeded && !exitedEarly;
        }

        internal static long SafeTimelineStart(long startQpc, long endQpc, long maxTicks)
        {
            long ticks = endQpc - startQpc;
            return ticks >= 0 && ticks <= maxTicks ? startQpc : endQpc;
        }

        internal static bool HasMappedEvents(long rawDpc, long rawIsr, int mappedDrivers)
        {
            return rawDpc >= 0 && rawIsr >= 0 && (rawDpc > 0 || rawIsr > 0) && mappedDrivers > 0;
        }

        public InterruptAttributionResult Stop()
        {
            var result = new InterruptAttributionResult();
            lock (gate)
            {
                if (!started) { result.Error = Lang.T("t.interruptattribution.4"); return result; }
                stopRequested = true;
                uint lost, lostBuffers, stopError;
                bool stopSucceeded = StopStale(out lost, out lostBuffers, out stopError);
                result.EventsLost = lost;
                result.BuffersLost = lostBuffers;
                // 控制器成功停会话后 实时 ProcessTrace 会排空并自行返回 其后再 CloseTrace
                // 停止失败只能先关消费句柄解除阻塞 这种样本必须标不完整
                if (!stopSucceeded)
                    try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                //   高事件量对局收尾时仍可能需要排空积压 2 秒会把正常收尾误判成卡死
                bool workerDone = true;
                if (worker != null) { try { workerDone = worker.Join(10000); } catch { workerDone = false; } }
                if (stopSucceeded)
                    try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                result.Incomplete = !CaptureComplete(
                    stopSucceeded, workerDone, processTraceSucceeded, consumerExitedEarly);
                started = false;
                // 无论排空成功与否都要交还探针所有权
                //   早先这里直接 return 把 aliveOwned 一路留着
                //   否则一次异常就会让后续每局都被判 观测被占 只能重启进程才恢复
                ReleaseOwnership();
                if (!workerDone)
                {
                    // worker 还在写 dpcHits 这轮数据不能读 但下一轮可以正常重来
                    result.Error = Lang.T("t.interruptattribution.5");
                    return result;
                }
                keepAlive = null;

                // worker 已 Join 原始标记稳定 单线程内解析出逐事件 DPC 时间线
                BuildDpcTimeline();

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
                bool unmapped = (dpcTotal > 0 || isrTotal > 0) && result.Drivers.Count == 0;
                string mappingError = unmapped
                    ? "已采集中断事件，但无法映射到驱动模块，本局中断归因不可用"
                        + " DPC=" + dpcTotal + " ISR=" + isrTotal + " 模块=" + modules.Count
                    : null;
                if (unmapped) Logger.Log("IRQ " + mappingError);
                result.Ok = HasMappedEvents(dpcTotal, isrTotal, result.Drivers.Count)
                    && !result.Incomplete && !result.Lossy;
                if (result.Lossy)
                {
                    Logger.Log(Lang.F("log.interruptattribution.lossy", result.EventsLost, result.BuffersLost));
                }
                // 归因失败不能掩盖更高优先级的采集不完整/丢失 三种情况都不能提供有效样本
                if (result.Incomplete)
                    result.Error = "ETW 消费或停止未完整 win32=" + stopError;
                else if (result.Lossy)
                    result.Error = Lang.F("t.interruptattribution.7", result.EventsLost, result.BuffersLost);
                else if (unmapped)
                    result.Error = mappingError;
                else if (!result.Ok)
                    result.Error = Lang.T("t.interruptattribution.6");
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

            // 逐事件 DPC 时间线 额外多存一条 极轻 append(不解析模块 模块地址留到 Stop 后再解析)
            //   聚合逻辑上面一行未动 这里只是并行追加 开关关时(默认)整段被首个 bool 短路 零开销
            if (dpc && captureTimeline && !timelineTruncated && dpcMarks != null)
            {
                if (dpcMarks.Count >= DpcTimelineCap) timelineTruncated = true;
                else dpcMarks.Add(new DpcMark
                {
                    // ETW 已判为坏时长时不能再把不可信 start 当成一个可能横跨数秒的
                    // 区间参与长帧对齐 退化成结束时刻的点事件 保留归因但不制造假重叠
                    StartQpc = SafeTimelineStart(startQpc, endQpc, sanityMaxTicks),
                    EndQpc = endQpc,
                    Routine = routine,
                    Cpu = cpu,
                    DpcUs = timed ? ticks * usPerTick : 0.0
                });
            }
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

        private bool LoadModules(out string error)
        {
            modules.Clear();
            List<Module> loaded;
            if (!TryReadLoadedModules(out loaded, out error)) return false;
            modules.AddRange(loaded);
            return true;
        }

        // 一份新鲜的只读快照同时提供驱动映像的真实路径给版本查询
        // 不要凭名字去猜 DriverStore 里的包
        internal static List<string> LoadedModuleImagePaths()
        {
            List<Module> loaded;
            string error;
            if (!TryReadLoadedModules(out loaded, out error)) return null;
            var paths = new List<string>(loaded.Count);
            foreach (Module module in loaded) paths.Add(module.ImagePath);
            return paths;
        }

        private static bool TryReadLoadedModules(out List<Module> loaded, out string error)
        {
            loaded = new List<Module>();
            error = null;
            IntPtr buf = IntPtr.Zero;
            try
            {
                int len = 0;
                NtQuerySystemInformation(11, IntPtr.Zero, 0, out len);
                len = checked(Math.Max(len, 1 << 20) + 65536);
                buf = Marshal.AllocHGlobal(len);
                int ret;
                int status = NtQuerySystemInformation(11, buf, len, out ret);
                if (status != 0)
                {
                    error = "驱动模块枚举失败，无法启动中断归因 NTSTATUS=0x"
                        + unchecked((uint)status).ToString("X8");
                    return false;
                }
                int count = Marshal.ReadInt32(buf);
                long p = buf.ToInt64() + IntPtr.Size;
                int stride = 16 + 8 + 4 + 4 + 2 + 2 + 2 + 2 + 256;
                if (count < 0 || count > (len - IntPtr.Size) / stride)
                {
                    error = "驱动模块枚举返回的长度无效，无法启动中断归因";
                    return false;
                }
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
                    loaded.Add(new Module { Base = imgBase, End = imgBase + imgSize, Name = name, ImagePath = full });
                }
                if (loaded.Count == 0)
                {
                    error = "驱动模块枚举未返回可用地址，无法启动中断归因";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                loaded.Clear();
                error = "驱动模块枚举异常，无法启动中断归因 " + ex.GetType().Name;
                return false;
            }
            finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
        }

        private static IntPtr AllocProps(uint enableFlags)
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
            // 池子从 4MB(32×128KB)扩到 32MB 以容纳对局中 DPC/ISR 的短时爆发
            //   旧池耗尽时内核没有空闲缓冲只能丢事件 表现为 EventsLost 高而 BuffersLost 为 0
            //   32MB 约可缓冲 25 万个事件 会话仅在真实对局观测时占用 结束即释放
            p.BufferSize = 128;
            p.MinimumBuffers = 64;
            p.MaximumBuffers = 256;
            p.LogFileMode = RealTimeMode | SystemLoggerMode | IndependentSessionMode;
            p.FlushTimer = 1;
            p.EnableFlags = enableFlags;
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
