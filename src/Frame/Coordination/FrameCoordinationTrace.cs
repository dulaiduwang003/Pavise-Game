using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class FrameCoordinationTrace
    {
        internal const string TraceKey = "FrameCoordinationTraceV1";
        internal const string IdentityTraceKey = Settings.GpuIdentityReceiptKey;
        // 只恢复与本方各通道前缀匹配的记录 包括已退役的遥测通道
        internal const string FrameSessionPrefix = "Pavise.FrameCoordination.", IdentitySessionPrefix = "Pavise.GpuIdentity.";
        internal const string PresentSessionPrefix = "Pavise.PresentProbe.";
        internal const string PresentProbeReceiptKey = "PresentProbeTraceV1";
        internal const string PresentProbeSessionPattern = @"\APavise\.PresentProbe\.[0-9]+\.[0-9a-f]{32}\z";
        internal static readonly Guid DxgKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");
        // 微软 DxgKrnl 的 Present 关键字以本机清单为准 DXGI D3D9 使用 Analytic 与 Events
        internal const ulong DxgKrnlPresentKeyword = 0x8000000UL, RuntimePresentKeyword = 0x8000000000000002UL;
        // 按事件标识过滤 避免同关键字下的无关事件占满缓冲 与 PresentActivityCapture.Classify 保持一致
        internal static readonly ushort[] DxgiPresentIds = { 42, 55 }, D3d9PresentIds = { 1 }, DxgKrnlPresentIds = { 171, 184, 215 };
        // Windows 10 的提交事件位于 Base 关键字下 仅带事件标识过滤时启用 Base
        internal const ulong DxgKrnlBaseKeyword = 0x1UL;
        internal const int FirstBuildWithPresentKeyword = 22000;
        internal static ulong DxgKrnlFilteredKeyword(int build)
        { return build < FirstBuildWithPresentKeyword ? DxgKrnlPresentKeyword | DxgKrnlBaseKeyword : DxgKrnlPresentKeyword; }
        private static int SystemBuild()
        {
#if PAVISE_SELFTEST
            if (BuildForTest > 0) return BuildForTest;
#endif
            return Environment.OSVersion.Version.Build;
        }
        internal static bool PresentIdListed(Guid provider, ushort id)
        {
            ushort[] ids = provider == FrameCoordinationSampler.Dxgi ? DxgiPresentIds : provider == FrameCoordinationSampler.D3d9 ? D3d9PresentIds
                : provider == DxgKrnl ? DxgKrnlPresentIds : null;
            return ids != null && Array.IndexOf(ids, id) >= 0;
        }
        // EVENT_FILTER_TYPE_EVENT_ID Windows 8.1 起可用
        internal const uint EventIdFilterType = 0x80000200;
        // 提前分配缓冲池 避免短探测窗口内扩容
        internal const uint PresentProbeBuffers = 32;
        // ERROR_SHUTDOWN_IN_PROGRESS 表示进程退出时已关闭 GPU 身份通道
        internal const int IdentityClosedError = 1115;
        private const int IdentityHealWaitMs = 3500;
        private const uint SchedulerFlags = 0x0812, SystemLoggerMode = 0x02000000;
        private const uint TraceQuery = 0, TraceUpdate = 2, TraceFlush = 3;
        private const int TraceMissing = 4201, TraceChanged = 13;

        internal sealed class Reservation
        {
            private const uint BufferKB = 64, MaximumBuffers = 512, FlushSeconds = 1;
            private const uint Mode = Native.EventTraceRealTimeMode | SystemLoggerMode;
            private const uint FileModes = 0x0000000f | 0x00000020 | 0x00000200 | 0x00003000;
            private const uint PrivateOrBufferedModes = 0x00020800 | 0x00000400;
            private const uint IncompatibleModes = FileModes | PrivateOrBufferedModes | 0x40 | 0x10000
                | 0x01000000 | 0x10000000 | 0x80000000;
            private const int OutputNameBytes = 2048;

            private struct Readback
            {
                internal Native.EventTraceProperties Properties;
                internal string LogFileName;
#if PAVISE_SELFTEST
                internal string LoggerName;
#endif
            }

            internal readonly string Name;
            private readonly Guid id;
            private ulong handle;
            private Readback baseline;
#if PAVISE_SELFTEST
            private Native.EventTraceProperties requested;
            private Readback lastReadback;
            private int? lastQueryStatus;

            internal string ReadbackDetails
            {
                get
                {
                    return "logger=" + Name + " request " + FormatReadback(requested, Name, "")
                        + (lastQueryStatus.HasValue ? " QUERY rc=" + lastQueryStatus.Value + " "
                            + FormatReadback(lastReadback.Properties, lastReadback.LoggerName, lastReadback.LogFileName) : " QUERY not run");
                }
            }

            private static string FormatReadback(Native.EventTraceProperties value, string logger, string file)
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "wnodeBytes={0} providerId={1} handle=0x{2:X} timestamp={3} guid={4} clock={5} wnodeFlags=0x{6:X}"
                    + " bufferKiB={7} min={8} max={9} maxFileSize={10} mode=0x{11:X8} flush={12} flags=0x{13:X} ageLimit={14}"
                    + " allocated={15} free={16} eventsLost={17} written={18} logLost={19} realtimeLost={20}"
                    + " loggerThread={21} fileOffset={22} loggerOffset={23} logger={24} file={25}",
                    value.WnodeBufferSize, value.WnodeProviderId, value.WnodeHistoricalContext, value.WnodeTimeStamp, value.WnodeGuid,
                    value.WnodeClientContext, value.WnodeFlags, value.BufferSize, value.MinimumBuffers, value.MaximumBuffers,
                    value.MaximumFileSize, value.LogFileMode, value.FlushTimer, value.EnableFlags, value.AgeLimit,
                    value.NumberOfBuffers, value.FreeBuffers, value.EventsLost, value.BuffersWritten, value.LogBuffersLost,
                    value.RealTimeBuffersLost, value.LoggerThreadId, value.LogFileNameOffset, value.LoggerNameOffset,
                    logger ?? "<unavailable>", file ?? "<unavailable>");
            }
#endif

            internal Reservation(string name, Guid id) { Name = name; this.id = id; }
            internal bool OwnsSession { get { return handle != 0; } }
            internal bool NeedsRebuild { get; private set; }

            internal int Start()
            {
                if (OwnsSession) return VerifyIdle();
                var value = new Native.EventTraceProperties { WnodeGuid = id, WnodeClientContext = 1,
                    WnodeFlags = Native.WnodeFlagTracedGuid, BufferSize = BufferKB, MinimumBuffers = 0,
                    MaximumBuffers = MaximumBuffers, LogFileMode = Mode, FlushTimer = FlushSeconds };
                IntPtr pointer = ReserveProperties(Name, value, false);
                try
                {
#if PAVISE_SELFTEST
                    requested = (Native.EventTraceProperties)Marshal.PtrToStructure(pointer, typeof(Native.EventTraceProperties));
                    lastQueryStatus = null;
#endif
                    ulong opened;
                    int status = StartSession(out opened, Name, pointer);
                    if (status != 0) return status;
                    handle = opened;
                }
                finally { Marshal.FreeHGlobal(pointer); }
                Readback readback;
                int result = QueryOwned(out readback);
                if (result != 0) return Invalidate(result);
                value = readback.Properties;
                if ((value.LogFileMode & Mode) != Mode || (value.LogFileMode & IncompatibleModes) != 0
                    || readback.LogFileName != "" || value.EnableFlags != 0
                    || (ulong)value.BufferSize * value.MaximumBuffers < BufferKB * MaximumBuffers)
                    return Invalidate(TraceChanged);
                baseline = readback;
                NeedsRebuild = false;
                return 0;
            }

            internal int VerifyIdle()
            {
                Native.EventTraceProperties value;
                return Verify(0, out value);
            }

            internal int Verify(uint flags, out Native.EventTraceProperties value)
            {
                Readback readback;
                int result = QueryOwned(out readback);
                value = readback.Properties;
                if (result != 0) return Invalidate(result);

                // 只比较稳定配置 累计计数和查询头会变化
                var initial = baseline.Properties;
                if (value.BufferSize != initial.BufferSize || value.MinimumBuffers != initial.MinimumBuffers
                    || value.MaximumBuffers != initial.MaximumBuffers || value.MaximumFileSize != initial.MaximumFileSize
                    || value.LogFileMode != initial.LogFileMode || value.FlushTimer != initial.FlushTimer
                    || value.WnodeClientContext != initial.WnodeClientContext || value.AgeLimit != initial.AgeLimit
                    || readback.LogFileName != baseline.LogFileName || value.EnableFlags != flags)
                    return Invalidate(TraceChanged);
                return 0;
            }

            internal int SetFlags(uint flags)
            {
                Readback ignored;
                int result = Change(handle, null, id, TraceUpdate, flags, out ignored);
                if (result != 0) return Invalidate(result);
                Native.EventTraceProperties value;
                return Verify(flags, out value);
            }

            internal int Flush()
            {
                Readback ignored;
                int result = Change(handle, null, id, TraceFlush, 0, out ignored);
                return result == 0 ? 0 : Invalidate(result);
            }

            internal int Invalidate(int error)
            {
                NeedsRebuild = true;
                return error;
            }

            private int QueryOwned(out Readback readback)
            {
                readback = default(Readback);
                if (!OwnsSession) return TraceMissing;
                int result = Change(0, Name, id, TraceQuery, 0, out readback);
#if PAVISE_SELFTEST
                lastQueryStatus = result;
                lastReadback = readback;
#endif
                if (result != 0) return result;
                var value = readback.Properties;
                return value.WnodeGuid == id && value.WnodeHistoricalContext == handle ? 0 : TraceMissing;
            }

            internal int Stop()
            {
                if (!OwnsSession) return 0;
                int result = StopOwned(Name, id, handle);
                if (result == 0) handle = 0;
                return result;
            }

            internal static int StopRecorded(string name, Guid id)
            { return StopOwned(name, id, 0); }

            private static int StopOwned(string name, Guid id, ulong expected)
            {
                Readback readback;
                int result = Change(0, name, id, TraceQuery, 0, out readback);
                if (result == TraceMissing) return 0;
                if (result != 0) return result;
                var value = readback.Properties;
                ulong owner = value.WnodeHistoricalContext;
                if (value.WnodeGuid != id || owner == 0 || (expected != 0 && owner != expected)) return 0;
                result = Change(owner, null, id, Native.EventTraceControlStop, 0, out readback);
                return result == TraceMissing ? 0 : result;
            }

            private static int Change(ulong owner, string name, Guid id, uint command, uint flags, out Readback readback)
            {
                var requested = new Native.EventTraceProperties {
                    WnodeGuid = command == TraceQuery ? Guid.Empty : id,
                    WnodeFlags = Native.WnodeFlagTracedGuid,
                    LogFileMode = command == TraceUpdate ? Native.EventTraceRealTimeMode : 0,
                    EnableFlags = flags };
                IntPtr pointer = ReserveProperties(name, requested, command != TraceUpdate);
                try
                {
                    int result = Control(owner, name, pointer, command);
                    var value = (Native.EventTraceProperties)Marshal.PtrToStructure(pointer, typeof(Native.EventTraceProperties));
                    bool query = result == 0 && command == TraceQuery;
                    readback = new Readback { Properties = value, LogFileName = query ? ReadOutputString(pointer, value.LogFileNameOffset) : null };
#if PAVISE_SELFTEST
                    if (query) readback.LoggerName = ReadOutputString(pointer, value.LoggerNameOffset);
#endif
                    return result;
                }
                finally { Marshal.FreeHGlobal(pointer); }
            }

            private static string ReadOutputString(IntPtr pointer, uint offset)
            {
                if (offset == 0) return "";
                int size = Marshal.SizeOf(typeof(Native.EventTraceProperties));
                int end = size + 2 * OutputNameBytes;
                if ((offset & 1) != 0 || offset < size || offset >= end) return null;
                IntPtr start = IntPtr.Add(pointer, (int)offset);
                if (Marshal.ReadInt16(start) == 0) return "";
                string text = Marshal.PtrToStringUni(start, (end - (int)offset) / 2);
                int terminator = text.IndexOf('\0');
                return terminator < 0 ? null : text.Substring(0, terminator);
            }

            private static IntPtr ReserveProperties(string name, Native.EventTraceProperties value, bool output)
            {
                int size = Marshal.SizeOf(typeof(Native.EventTraceProperties));
                int nameBytes = output || name == null ? OutputNameBytes : (name.Length + 1) * 2;
                int total = size + nameBytes + (output ? OutputNameBytes : 0);
                IntPtr pointer = Marshal.AllocHGlobal(total);
                Marshal.Copy(new byte[total], 0, pointer, total);
                value.WnodeBufferSize = (uint)total;
                if (nameBytes != 0) value.LoggerNameOffset = (uint)size;
                if (output) value.LogFileNameOffset = (uint)(size + nameBytes);
                Marshal.StructureToPtr(value, pointer, false);
                if (name != null && !output)
                {
                    byte[] bytes = System.Text.Encoding.Unicode.GetBytes(name);
                    Marshal.Copy(bytes, 0, IntPtr.Add(pointer, size), bytes.Length);
                }
                return pointer;
            }
        }
        private static readonly object ledgerGate = new object();
        private sealed class Channel
        {
            internal string Key, Pattern;
            internal bool Identity, Present;
            internal FrameCoordinationTrace Active;
            // 有界遥测通道自行处理过期记录 健康运行时不视为待恢复债务
            internal bool Telemetry { get { return Identity || Present; } }
        }
        private static readonly Channel frameChannel = new Channel { Key = TraceKey, Pattern = @"\APavise\.FrameCoordination\.[0-9]+\.[0-9a-f]{32}\z" },
            identityChannel = new Channel { Key = IdentityTraceKey, Pattern = Settings.GpuIdentitySessionPattern, Identity = true },
            presentChannel = new Channel { Key = PresentProbeReceiptKey, Pattern = PresentProbeSessionPattern, Present = true };
        // 仅在进程退出的 CloseIdentity 中设置 由 ledgerGate 保护
        private static bool identitySealed, presentSealed;
        private Channel channel = frameChannel;
        private readonly object memory = new object();
        private Action drainedCleanup;
        private bool drainedCleanupRegistered, consumerReturned;
        private readonly object cleanup = new object();
        private readonly object failureGate = new object();
        private IntPtr properties, loggerName;
        private ulong session, consumer = Native.InvalidProcessTraceHandle;
        private Native.EventRecordCallback callback;
        // 原生前置处理的回调地址与其上下文 上下文由 EVENT_RECORD.UserContext 带回回调
        private IntPtr nativeCallback, nativeContext;
        private Thread pump;
        private bool started, claimed;
        private Reservation reservation;
        private Native.EventTraceProperties initialReservedStats;
        private bool reservedAttempt, reservedActive, reservedQuiesced;
        private volatile bool reservedTeardownComplete;
        private Action reservedClosed;
        // stopRequested 保留恢复债务 两个原生句柄均关闭后设置 handlesClosed 由最后完成的线程结清通道

        private volatile bool stopRequested, handlesClosed;
        private volatile bool receiptSettled;
        private string name = SessionName(FrameSessionPrefix);
        internal int Error;
        // 错误码须结合阶段解读 系统调用与回执账本可能返回相同错误码
        internal string Stage = "";
        // 启动或消费失败与清理失败各保留首次证据 后来的恢复不能把失败采样改成成功
        internal int PrimaryError, CleanupError;
        internal string PrimaryStage = "", CleanupStage = "";
        internal string FailureDetail
        {
            get
            {
                lock (failureGate)
                {
                    bool pending = reservation == null ? claimed && !receiptSettled : reservation.NeedsRebuild && reservation.OwnsSession;
                    return "primary=" + PrimaryError + "@" + PrimaryStage + " cleanup=" + CleanupError + "@" + CleanupStage
                        + " recovery=" + (pending ? "pending" : "settled");
                }
            }
        }
        private void RecordPrimaryFailure(int code, string stage)
        {
            if (code == 0) return;
            lock (failureGate)
            {
                if (PrimaryError == 0) { PrimaryError = code; PrimaryStage = stage; }
                Error = PrimaryError;
                Stage = PrimaryStage;
            }
        }
        private void RecordCleanupFailure(int code, string stage)
        {
            if (code == 0) return;
            lock (failureGate)
            {
                if (CleanupError == 0) { CleanupError = code; CleanupStage = stage; }
                Error = PrimaryError != 0 ? PrimaryError : CleanupError;
                Stage = PrimaryError != 0 ? PrimaryStage : CleanupStage;
            }
        }
        internal bool FailedInLedger { get { return Stage.StartsWith("ledger-", StringComparison.Ordinal); } }
        internal uint Lost;
        // 分别记录事件写入丢失与消费者缓冲送达丢失
        internal uint LostEvents, LostBuffers;
        // 未启用事件标识过滤的提供程序数
        internal int UnfilteredProviders;
        internal string LossDetail
        { get { return "events=" + LostEvents + " buffers=" + LostBuffers + (UnfilteredProviders == 0 ? "" : " unfiltered=" + UnfilteredProviders); } }
        // 停止时系统回填的会话缓冲读数及停止耗时 仅用于丢事件诊断 未停止过时为空
        internal string SessionStats = "session=n/a";
        internal int PumpTid;
        internal double CpuMs = double.NaN;
        internal bool Drained { get { Thread current = pump; return current == null || !current.IsAlive; } }
        // 只接管一次消费者资源 登记与完成可交错 但 ProcessTrace 返回前不得释放
        internal void OwnDrainedCleanup(Action cleanupAction)
        {
            if (cleanupAction == null) throw new ArgumentNullException("cleanupAction");
            bool complete;
            lock (memory)
            {
                if (drainedCleanupRegistered) throw new InvalidOperationException(Logger.Text("消费者资源已登记释放归属", "consumer resource cleanup ownership already registered"));
                drainedCleanupRegistered = true;
                complete = consumerReturned;
                if (!complete) drainedCleanup = cleanupAction;
            }
            if (complete) RunDrainedCleanup(cleanupAction);
        }

        private void NotifyConsumerReturned()
        {
            Action release;
            lock (memory)
            {
                consumerReturned = true;
                release = drainedCleanup;
                drainedCleanup = null;
            }
            if (release != null) RunDrainedCleanup(release);
        }

        internal void WhenReservedCaptureClosed(Action action)
        {
            lock (memory)
            {
                if (reservation != null && (!reservedTeardownComplete || !consumerReturned))
                {
                    reservedClosed += action;
                    return;
                }
            }
            action();
        }

        private void RunDrainedCleanup(Action release)
        {
            try { release(); }
            catch (Exception) { RecordCleanupFailure(31, "drained-cleanup"); }
        }
        internal static bool IdentityClosing { get { lock (ledgerGate) return identitySealed; } }
        private static string SessionName(string prefix)
        { return prefix + Process.GetCurrentProcess().Id + "." + Guid.NewGuid().ToString("N"); }
        internal static bool HasResidue
        {
            get
            {
                lock (ledgerGate)
                {
                    string receipt;
                    bool frame = frameChannel.Active != null || !Settings.TryLoadStr(TraceKey, out receipt) || receipt.Length > 0;
                    // 健康的有界遥测不是恢复债务 失败 正在停止或前一进程遗留的记录才是
                    return frame || TelemetryResidueLocked(identityChannel) || TelemetryResidueLocked(presentChannel);
                }
            }
        }
        // 调用方需持有 ledgerGate
        private static bool TelemetryResidueLocked(Channel channel)
        {
            string receipt;
            var active = channel.Active;
            return active != null ? active.Error != 0 || active.stopRequested
                : !Settings.TryLoadStr(channel.Key, out receipt) || receipt.Length > 0;
        }
        [DllImport("kernel32.dll")] private static extern ushort GetActiveProcessorGroupCount();
        [StructLayout(LayoutKind.Sequential)] private struct Filter { internal ulong Ptr; internal uint Size, Type; }
#if PAVISE_SELFTEST
        internal delegate int StartTraceHook(string traceName, IntPtr props, out ulong handle);
        internal static Func<ulong, string, IntPtr, uint, int> ControlForTest;
        internal static Func<ulong, int> CloseForTest;
        // 完整 Start 采样和 Stop 路径使用替身 不创建 ETW 会话 消费者或提供程序
        internal static StartTraceHook StartForTest;
        internal static Func<ulong> OpenForTest;
        // 测试替身中 ids 为 null 表示不使用事件标识过滤
        internal static Func<Guid, ulong, ushort[], int> EnableForTest;
        internal static Func<ulong, Guid, uint, int> FrameProviderForTest;
        // 正值覆盖测试系统版本 用于核对关键字选择
        internal static int BuildForTest;
        internal static Func<ulong, Native.EventRecordCallback, int> ProcessForTest;
        internal static int StopWaitMsForTest = 3000;
        internal static FrameCoordinationTrace CleanupFixtureForTest(Thread runningPump = null, bool identity = false)
        {
            var trace = new FrameCoordinationTrace();
            if (identity) { trace.channel = identityChannel; trace.name = SessionName(IdentitySessionPrefix); }
            trace.properties = Props(trace.name, true); trace.session = 11; trace.consumer = 22; trace.pump = runningPump;
            trace.started = trace.claimed = true;
            WriteReceipt(trace.channel, trace.name);
            lock (ledgerGate) trace.channel.Active = trace;
            return trace;
        }
        internal static bool IdentitySealedForTest { get { return IdentityClosing; } }
        internal static void UnsealIdentityForTest() { lock (ledgerGate) identitySealed = false; }
#endif
        private static int Control(ulong handle, string traceName, IntPtr props, uint command)
        {
#if PAVISE_SELFTEST
            if (ControlForTest != null) return ControlForTest(handle, traceName, props, command);
#endif
            return Native.ControlTrace(handle, traceName, props, command);
        }
        private static int CloseConsumer(ulong handle)
        {
#if PAVISE_SELFTEST
            if (CloseForTest != null) return CloseForTest(handle);
#endif
            return Native.CloseTrace(handle);
        }
        private static int StartSession(out ulong handle, string traceName, IntPtr props)
        {
#if PAVISE_SELFTEST
            if (StartForTest != null) return StartForTest(traceName, props, out handle);
#endif
            return Native.StartTrace(out handle, traceName, props);
        }
        private static ulong OpenConsumer(ref Native.EventTraceLogfile log)
        {
#if PAVISE_SELFTEST
            if (OpenForTest != null) return OpenForTest();
#endif
            return Native.OpenTrace(ref log);
        }
        private int ProcessConsumer(ulong handle)
        {
#if PAVISE_SELFTEST
            if (ProcessForTest != null) return ProcessForTest(handle, callback);
#endif
            return Native.ProcessTrace(new[] { handle }, 1, IntPtr.Zero, IntPtr.Zero);
        }
        // 身份记录使用独立的严格写入器 不推进用户设置的变更代次
        private static bool WriteReceipt(Channel channel, string value)
        {
            if (channel.Identity) return Settings.SaveGpuIdentityReceipt(value);
            if (channel.Present) return Settings.SavePresentProbeReceipt(value);
            return Settings.SaveStr(channel.Key, value);
        }

        private static IntPtr Props(string name, bool start)
        { return Props(name, start, false); }
        private static IntPtr Props(string name, bool start, bool presenterOnly)
        { return Props(name, start, presenterOnly, 8); }
        private static IntPtr Props(string name, bool start, bool presenterOnly, uint minimumBuffers)
        {
            int bytes = Marshal.SizeOf(typeof(Native.EventTraceProperties));
            byte[] bytes2 = System.Text.Encoding.Unicode.GetBytes(name + "\0");
            IntPtr p = Marshal.AllocHGlobal(bytes + bytes2.Length);
            Marshal.Copy(new byte[bytes + bytes2.Length], 0, p, bytes + bytes2.Length);
            var value = new Native.EventTraceProperties { WnodeBufferSize = (uint)(bytes + bytes2.Length),
                WnodeFlags = Native.WnodeFlagTracedGuid, WnodeClientContext = 1, WnodeGuid = Guid.NewGuid(),
                // 完整采集每秒可达上百万事件 缓冲区 2MB 时消费线程稍一卡顿就丢事件 按需加到最多 32MB
                // 只识别画面提交的轻量采集事件很少 最多 2MB 缓冲就够
                BufferSize = presenterOnly ? 64u : 256u, MinimumBuffers = minimumBuffers, MaximumBuffers = presenterOnly ? 32u : 128u, FlushTimer = 1,
                LogFileMode = Native.EventTraceRealTimeMode | (presenterOnly ? 0u : 0x02000000u),
                EnableFlags = start && !presenterOnly ? 0x0812u : 0u,
                LoggerNameOffset = (uint)bytes };
            Marshal.StructureToPtr(value, p, false); Marshal.Copy(bytes2, 0, IntPtr.Add(p, bytes), bytes2.Length); return p;
        }
        internal bool Start(int pid, Native.EventRecordCallback receive)
        { return Start(pid, receive, false); }
        internal bool Start(int pid, Native.EventRecordCallback receive, bool presenterOnly, bool filterPresentIds = false)
        { lock (cleanup) return StartLocked(pid, receive, presenterOnly, filterPresentIds); }
        // 完整采集交给原生回调处理 须在 Start 之前设置 上下文的释放归调用方 要等消费者退出
        // 之后传给 Start 的托管回调只给替换了 ProcessTrace 的测试注入事件用 系统直接调用原生函数
        internal bool UseNativeConsumer(IntPtr receive, IntPtr context)
        {
            lock (cleanup)
            {
                if (started || receive == IntPtr.Zero || context == IntPtr.Zero) return false;
                nativeCallback = receive; nativeContext = context;
                return true;
            }
        }
        // 初始化全程持有清理锁 防止停止操作插入到句柄只打开一半的状态
        private bool StartLocked(int pid, Native.EventRecordCallback receive, bool presenterOnly, bool filterPresentIds)
        {
            if (started || stopRequested) { RecordPrimaryFailure(1247, "already-started"); return false; }
            started = true;
            if (!presenterOnly && GetActiveProcessorGroupCount() != 1) { RecordPrimaryFailure(50, "processor-groups"); return false; }
            int refusal;
            lock (ledgerGate) refusal = ClaimLocked();
            if (refusal != 0) { RecordPrimaryFailure(refusal, Stage); return false; }
            properties = Props(name, true, presenterOnly);
            Error = StartSession(out session, name, properties);
            if (Error != 0) { RecordPrimaryFailure(Error, "start-trace"); session = 0; Stop(); return false; }
            if (!StartFrameProviders(pid, filterPresentIds)) { Stop(); return false; }
            return OpenAndPumpLocked(receive);
        }

        internal bool StartReserved(int pid, Native.EventRecordCallback receive, Reservation owned)
        {
            lock (cleanup)
            {
                if (started || stopRequested) { RecordPrimaryFailure(1247, "already-started"); return false; }
                started = true;
                reservedAttempt = true;
                if (GetActiveProcessorGroupCount() != 1) { RecordPrimaryFailure(50, "processor-groups"); return false; }
                if (owned == null) { RecordPrimaryFailure(1450, "reservation-unavailable"); return false; }
                int status = owned.Verify(0, out initialReservedStats);
                if (status != 0) { RecordPrimaryFailure(status, "reservation-query"); return false; }
                reservation = owned;
                session = initialReservedStats.WnodeHistoricalContext;
                name = owned.Name;
                // 先接上实时消费者 再开启高频调度事件
                if (!OpenAndPumpLocked(receive)) return false;
                status = owned.SetFlags(SchedulerFlags);
                if (status != 0)
                {
                    RecordPrimaryFailure(status, "reservation-enable-flags");
                    Stop();
                    return false;
                }
                if (!StartFrameProviders(pid, false))
                {
                    owned.Invalidate(PrimaryError);
                    Stop();
                    return false;
                }
                reservedActive = true;
                return true;
            }
        }

        private bool StartFrameProviders(int pid, bool filterPresentIds)
        {
            int status = EnableFrameProvider(pid, FrameCoordinationSampler.Dxgi, 42, 43, filterPresentIds);
            if (status != 0) { RecordPrimaryFailure(status, "enable-dxgi"); return false; }
            status = EnableFrameProvider(pid, FrameCoordinationSampler.D3d9, 1, 2, filterPresentIds);
            if (status != 0) { RecordPrimaryFailure(status, "enable-d3d9"); return false; }
            return true;
        }

        private unsafe int EnableFrameProvider(int pid, Guid provider, ushort start, ushort stop, bool filterPresentIds)
        {
#if PAVISE_SELFTEST
            if (FrameProviderForTest != null) return FrameProviderForTest(session, provider, 1);
#endif
            Filter* filters = stackalloc Filter[2];
            filters[0] = new Filter { Ptr = (ulong)&pid, Size = 4, Type = 0x80000004 };
            // 前四字节保存 FilterIn Reserved 和 Count
            ushort* ids = stackalloc ushort[4];
            ids[0] = 1; ids[1] = 2; ids[2] = start; ids[3] = stop;
            filters[1] = new Filter { Ptr = (ulong)ids, Size = 8, Type = EventIdFilterType };
            var parameters = new Native.EnableTraceParameters { Version = 2, EnableFilterDesc = (IntPtr)filters,
                FilterDescCount = filterPresentIds ? 2u : 1u };
            int status = Native.EnableTraceEx2(session, ref provider, 1, 5, RuntimePresentKeyword, 0, 0, ref parameters);
            if (!filterPresentIds || (status != 50 && status != 87)) return status;
            // 系统不支持组合过滤时保留进程约束
            parameters.FilterDescCount = 1;
            status = Native.EnableTraceEx2(session, ref provider, 1, 5, RuntimePresentKeyword, 0, 0, ref parameters);
            if (status == 0) UnfilteredProviders++;
            return status;
        }

        // 同进程通道占用提示 不作为后续启动成功的保证
        internal static bool PresentProbeBusy
        {
            get { lock (ledgerGate) return presentChannel.Active != null; }
        }
        // 只采集系统级画面提交事件 不按 PID 过滤 不启用内核线程标志 共使用三个提交提供程序
        internal bool StartPresentProbe(Native.EventRecordCallback receive)
        {
            lock (cleanup)
            {
                if (started || stopRequested) { RecordPrimaryFailure(1247, "already-started"); return false; }
                channel = presentChannel; name = SessionName(PresentSessionPrefix);
                started = true;
                int refusal;
                lock (ledgerGate) refusal = ClaimLocked();
                if (refusal != 0) { RecordPrimaryFailure(refusal, Stage); return false; }
                properties = Props(name, true, true, PresentProbeBuffers);
                Error = StartSession(out session, name, properties);
                if (Error != 0) { RecordPrimaryFailure(Error, "start-trace"); session = 0; Stop(); return false; }
                Error = EnablePresentProvider(FrameCoordinationSampler.Dxgi, RuntimePresentKeyword, RuntimePresentKeyword, DxgiPresentIds);
                if (Error != 0) Stage = "enable-dxgi";
                else if ((Error = EnablePresentProvider(FrameCoordinationSampler.D3d9, RuntimePresentKeyword, RuntimePresentKeyword, D3d9PresentIds)) != 0) Stage = "enable-d3d9";
                else if ((Error = EnablePresentProvider(DxgKrnl, DxgKrnlFilteredKeyword(SystemBuild()), DxgKrnlPresentKeyword, DxgKrnlPresentIds)) != 0) Stage = "enable-dxgkrnl";
                if (Error != 0) { RecordPrimaryFailure(Error, Stage); Stop(); return false; }
                return OpenAndPumpLocked(receive);
            }
        }
        // EVENT_FILTER_EVENT_ID 的内存布局 FilterIn Reserved 各一字节 Count 两字节 后面是 Count 个两字节标识
        internal static byte[] EventIdFilterBytes(ushort[] ids)
        {
            var bytes = new byte[4 + 2 * ids.Length];
            bytes[0] = 1;
            bytes[2] = (byte)ids.Length; bytes[3] = (byte)(ids.Length >> 8);
            for (int i = 0; i < ids.Length; i++) { bytes[4 + 2 * i] = (byte)ids[i]; bytes[5 + 2 * i] = (byte)(ids[i] >> 8); }
            return bytes;
        }
            // 事件标识过滤不受支持时仅回退一次 并记录未过滤的提供程序数
        private int EnablePresentProvider(Guid provider, ulong filteredKeyword, ulong plainKeyword, ushort[] ids)
        {
            int filtered = EnableProvider(provider, filteredKeyword, ids);
            if (filtered == 0) return 0;
            int plain = EnableProvider(provider, plainKeyword, null);
            if (plain == 0) UnfilteredProviders++;
            return plain;
        }
        private int EnableProvider(Guid provider, ulong keyword, ushort[] ids)
        {
#if PAVISE_SELFTEST
            if (EnableForTest != null) return EnableForTest(provider, keyword, ids);
#endif
            var parameters = new Native.EnableTraceParameters { Version = 2 };
            if (ids == null) return Native.EnableTraceEx2(session, ref provider, 1, 5, keyword, 0, 0, ref parameters);
            byte[] list = EventIdFilterBytes(ids);
            IntPtr listPointer = Marshal.AllocHGlobal(list.Length), filterPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Filter)));
            try
            {
                Marshal.Copy(list, 0, listPointer, list.Length);
                Marshal.StructureToPtr(new Filter { Ptr = (ulong)listPointer.ToInt64(), Size = (uint)list.Length, Type = EventIdFilterType }, filterPointer, false);
                parameters.EnableFilterDesc = filterPointer; parameters.FilterDescCount = 1;
                return Native.EnableTraceEx2(session, ref provider, 1, 5, keyword, 0, 0, ref parameters);
            }
            finally { Marshal.FreeHGlobal(listPointer); Marshal.FreeHGlobal(filterPointer); }
        }
        // 调用方持有清理锁 且会话已启动 提供程序已启用
        private bool OpenAndPumpLocked(Native.EventRecordCallback receive)
        {
            callback = receive; loggerName = Marshal.StringToHGlobalUni(name);
            bool native = nativeCallback != IntPtr.Zero;
            var log = new Native.EventTraceLogfile { LoggerName = loggerName,
                ProcessTraceMode = Native.ProcessTraceModeRealTime | Native.ProcessTraceModeEventRecord | 0x1000,
                EventRecordCallback = native ? nativeCallback : Marshal.GetFunctionPointerForDelegate(callback),
                Context = native ? nativeContext : IntPtr.Zero };
            consumer = OpenConsumer(ref log);
            if (consumer == Native.InvalidProcessTraceHandle) { int error = Marshal.GetLastWin32Error(); RecordPrimaryFailure(error == 0 ? 6 : error, "open-consumer"); Stop(); return false; }
            ulong opened = consumer;
            pump = new Thread(delegate()
            {
                PumpTid = Native.GetCurrentThreadId(); LoopProfile.Tag("FrameTracePump");
                try
                {
                    int rc = ProcessConsumer(opened);
                    if (rc != 0 && rc != 1223) RecordPrimaryFailure(rc, "process-trace");
                }
                finally
                {
                    try
                    {
                        IntPtr thread = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, PumpTid);
                        if (thread != IntPtr.Zero) { try { long b,e,k,u; if (Native.GetThreadTimes(thread,out b,out e,out k,out u)) CpuMs=(k+u)/10000.0; } finally { Native.CloseHandle(thread); } }
                    }
                    finally
                    {
                        // ProcessTrace 已返回 迟到消费者也先释放本方回调资源 回执仍按句柄状态结清
                        NotifyConsumerReturned();
                        if (handlesClosed || reservedTeardownComplete) Settle();
                    }
                }
            });
            pump.IsBackground = true; pump.Priority = ThreadPriority.BelowNormal;
            pump.Name = "Pavise.FrameEvents"; pump.Start();
            return true;
        }
        // 调用方持有 ledgerGate 取得通道归属后返回 0 拒绝阶段写入 Stage
        private int ClaimLocked()
        {
            if (channel.Identity && identitySealed) { Stage = "channel-closed"; return IdentityClosedError; }
            if (channel.Present && presentSealed) { Stage = "channel-closed"; return IdentityClosedError; }
            if (channel.Active != null) { Stage = "channel-busy"; return 170; }
            string previous, written;
            if (!Settings.TryLoadStr(channel.Key, out previous)) { Stage = "ledger-read"; return 5; }
            if (previous.Length != 0)
            {
                // 只有遥测通道能自行恢复 且仅处理没有存活跟踪持有的自身过期记录
                if (!channel.Telemetry) { Stage = "ledger-stale"; return 5; }
                int recovered = RecoverReceiptLocked(channel, previous);
                if (recovered != 0) { Stage = "ledger-recover"; return recovered; }
            }
            if (!WriteReceipt(channel, name) || !Settings.TryLoadStr(channel.Key, out written) || written != name) { Stage = "ledger-write"; return 5; }
            FrameRecoveryBudget.Own(channel.Key, 0, 0, name);
            claimed = true; channel.Active = this;
            return 0;
        }
        // 调用方持有 ledgerGate 并已确认当前进程没有存活跟踪持有该通道
        private static int RecoverReceiptLocked(Channel channel, string receipt)
        {
            int result = 5;
            return FrameRecoveryBudget.Run(channel.Key, delegate { result = RecoverReceiptCore(channel, receipt); return result == 0; }) ? 0 : result;
        }
        private static int RecoverReceiptCore(Channel channel, string receipt)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(receipt, channel.Pattern)) return 5;
            IntPtr p = Props(receipt, false);
            try
            {
                int rc = Control(0, receipt, p, Native.EventTraceControlStop);
                if (rc != 0 && rc != 4201 && rc != 234) return rc;
                return ClearLedger(channel, receipt) ? 0 : 5;
            }
            finally { Marshal.FreeHGlobal(p); }
        }
        internal bool Stop()
        { lock (cleanup) return StopLocked(StopWaitMs()); }
        // 若初始化仍持有清理锁 报告恢复债务 避免阻塞退出
        private bool StopWithin(int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            if (!Monitor.TryEnter(cleanup, Math.Max(0, timeoutMs))) return false;
            try { return StopLocked(Math.Max(0, Math.Min(StopWaitMs(), timeoutMs - (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds)))); }
            finally { Monitor.Exit(cleanup); }
        }
        private static int StopWaitMs()
        {
            int waitMs = 3000;
#if PAVISE_SELFTEST
            waitMs = StopWaitMsForTest;
#endif
            return waitMs;
        }
        private bool StopLocked(int waitMs)
        {
            if (receiptSettled) return Error == 0 && Lost == 0;
            if (reservedAttempt)
            {
                if (reservation != null) return StopReserved(waitMs);
                NotifyConsumerReturned();
                Settle();
                return false;
            }
            bool stopped = false;
            bool released = FrameRecoveryBudget.Run(channel.Key, delegate { stopped = StopCore(waitMs); return receiptSettled; });
            return released && stopped;
        }
        private bool StopReserved(int waitMs)
        {
            stopRequested = true;
            if (!reservedQuiesced)
            {
                reservedQuiesced = true;
                if (PrimaryStage == "process-trace") reservation.Invalidate(PrimaryError);
                if (!reservation.NeedsRebuild) QuiesceReservation();
            }
            if (CloseTraceConsumer() != 0) reservation.Invalidate(CleanupError);
            if (reservation.NeedsRebuild) StopDamagedReservation();
            bool drained = PumpFinished(waitMs);
            if (drained)
            {
                NotifyConsumerReturned();
                CloseTraceConsumer();
                if (!reservation.NeedsRebuild && PrimaryStage == "process-trace")
                {
                    reservation.Invalidate(PrimaryError);
                    StopDamagedReservation();
                }
                if (!reservation.NeedsRebuild) ReadReservedStats();
            }
            else
            {
                RecordCleanupFailure(1460, "drain-consumer");
                reservation.Invalidate(1460);
                StopDamagedReservation();
            }
            reservedTeardownComplete = true;
            bool returned;
            lock (memory) returned = consumerReturned;
            if (returned) Settle();
            return drained && receiptSettled && Error == 0 && Lost == 0;
        }

        private void QuiesceReservation()
        {
            Native.EventTraceProperties value;
            int result = reservation.Verify(reservedActive ? SchedulerFlags : 0, out value);
            string stage = "reservation-query";
            if (result == 0 && reservedActive)
            {
                stage = "disable-dxgi";
                result = DisableFrameProvider(FrameCoordinationSampler.Dxgi);
            }
            if (result == 0 && reservedActive)
            {
                stage = "disable-d3d9";
                result = DisableFrameProvider(FrameCoordinationSampler.D3d9);
            }
            if (result == 0 && reservedActive)
            {
                stage = "reservation-disable-flags";
                result = reservation.SetFlags(0);
            }
            if (result == 0 && reservedActive)
            {
                // 冲刷会话缓冲后排完已入队事件
                stage = "reservation-flush";
                result = reservation.Flush();
            }
            if (result != 0) RecordCleanupFailure(reservation.Invalidate(result), stage);
        }

        private int DisableFrameProvider(Guid provider)
        {
#if PAVISE_SELFTEST
            if (FrameProviderForTest != null) return FrameProviderForTest(session, provider, 0);
#endif
            var parameters = new Native.EnableTraceParameters { Version = 2 };
            return Native.EnableTraceEx2(session, ref provider, 0, 0, 0, 0, 0, ref parameters);
        }

        private void StopDamagedReservation()
        {
            if (!reservation.OwnsSession) return;
            int result = reservation.Stop();
            if (result != 0) RecordCleanupFailure(result, "reservation-stop");
        }

        private void ReadReservedStats()
        {
            Native.EventTraceProperties value;
            int result = reservation.Verify(0, out value);
            if (result != 0)
            {
                RecordCleanupFailure(result, "reservation-stats");
                StopDamagedReservation();
                return;
            }
            // 会话累计计数按采样窗取无符号差值
            LostEvents = unchecked(value.EventsLost - initialReservedStats.EventsLost);
            LostBuffers = unchecked(value.RealTimeBuffersLost - initialReservedStats.RealTimeBuffersLost);
            Lost = (uint)Math.Min(uint.MaxValue, (ulong)LostEvents + LostBuffers);
            SessionStats = "session=bufferKB:" + value.BufferSize + ",min:" + value.MinimumBuffers + ",max:" + value.MaximumBuffers
                + ",allocated:" + value.NumberOfBuffers + ",free:" + value.FreeBuffers
                + ",written:" + unchecked(value.BuffersWritten - initialReservedStats.BuffersWritten)
                + ",logLost:" + unchecked(value.LogBuffersLost - initialReservedStats.LogBuffersLost);
        }

        private int CloseTraceConsumer()
        {
            if (consumer == Native.InvalidProcessTraceHandle) return 0;
            int close = CloseConsumer(consumer);
            // 实时追踪返回关闭待完成仍需排空回调
            if (close == 0 || close == 7007 || close == 6) consumer = Native.InvalidProcessTraceHandle;
            if (close == 0 || close == 7007) return 0;
            RecordCleanupFailure(close, "close-consumer");
            return close;
        }

        private bool StopCore(int waitMs)
        {
            int rc = 0;
            stopRequested = true;
            if (session != 0)
            {
                long stopStarted = Stopwatch.GetTimestamp();
                rc = Control(session, name, properties, Native.EventTraceControlStop);
                long stopMs = (Stopwatch.GetTimestamp() - stopStarted) * 1000 / Stopwatch.Frequency;
                var value = (Native.EventTraceProperties)Marshal.PtrToStructure(properties, typeof(Native.EventTraceProperties));
                LostEvents = value.EventsLost; LostBuffers = value.RealTimeBuffersLost;
                Lost = LostEvents + LostBuffers;
                SessionStats = "session=bufferKB:" + value.BufferSize + ",min:" + value.MinimumBuffers + ",max:" + value.MaximumBuffers
                    + ",allocated:" + value.NumberOfBuffers + ",free:" + value.FreeBuffers + ",written:" + value.BuffersWritten
                    + ",logLost:" + value.LogBuffersLost + ",stopRc:" + rc + ",stopMs:" + stopMs;
                // 停止失败时保留会话句柄 由所属预算控制后续恢复
                if (rc == 0 || rc == 4201 || rc == 234) session = 0;
                if (rc != 0 && rc != 4201) RecordCleanupFailure(rc, "stop-trace");
            }
            CloseTraceConsumer();
            bool closedHandles = session == 0 && consumer == Native.InvalidProcessTraceHandle;
            if (closedHandles) handlesClosed = true;
            bool settled = closedHandles && PumpFinished(waitMs);
            if (Drained) NotifyConsumerReturned();
            bool cleared = settled && Settle();
            return settled && cleared && Error == 0 && Lost == 0;
        }
        private bool PumpFinished(int waitMs)
        {
            Thread current = pump;
            if (current == null || !current.IsAlive) return true;
            // 回调中发出的停止不能等待自身采样线程 该线程返回后自行结清
            if (current == Thread.CurrentThread) return false;
            return current.Join(Math.Max(0, waitMs));
        }
        // 停止方与迟到线程可并发结清 须确认两个句柄关闭且 ProcessTrace 返回 清账失败仍保留债务

        private bool Settle()
        {
            bool cleared = true;
            lock (ledgerGate)
            {
                // 迟到的停止操作不能清除新通道所有者的记录
                if (!receiptSettled)
                {
                    if (claimed) cleared = ClearLedger(channel, name);
                    if (cleared)
                    {
                        receiptSettled = true;
                        // 清账失败时保留原对象 后续恢复继续用它的句柄状态与原预算 不能另起所有者
                        if (channel.Active == this) channel.Active = null;
                        if (claimed) FrameRecoveryBudget.Settled(channel.Key, name);
                    }
                }
            }
            if (!cleared) RecordCleanupFailure(5, "ledger-clear");
            Action closed;
            lock (memory)
            {
                FreeMemory();
                closed = reservedClosed;
                reservedClosed = null;
            }
            if (closed != null) RunDrainedCleanup(closed);
            return cleared;
        }
        private static bool ClearLedger(Channel channel, string expected)
        {
            string current;
            if (!Settings.TryLoadStr(channel.Key, out current)) return false;
            if (current.Length == 0) return true;
            return current == expected && WriteReceipt(channel, "")
                && Settings.TryLoadStr(channel.Key, out current) && current.Length == 0;
        }
        private void FreeMemory()
        {
            if (properties != IntPtr.Zero) { Marshal.FreeHGlobal(properties); properties = IntPtr.Zero; }
            if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
            GC.KeepAlive(callback);
        }
        internal static bool HealLegacyFrameForReservation()
        {
            lock (ledgerGate)
            {
                if (frameChannel.Active != null) return true;
                string previous;
                if (!Settings.TryLoadStr(frameChannel.Key, out previous)) return false;
                if (previous.Length == 0) return true;
                if (!System.Text.RegularExpressions.Regex.IsMatch(previous, frameChannel.Pattern)) return false;
                int start = FrameSessionPrefix.Length;
                int end = previous.IndexOf('.', start);
                int owner;
                if (!int.TryParse(previous.Substring(start, end - start), out owner) || owner <= 0) return false;
                if (owner != Process.GetCurrentProcess().Id)
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(owner))
                            if (!process.HasExited) return false;
                    }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { return false; }
                    catch (System.ComponentModel.Win32Exception) { return false; }
                }
                return RecoverReceiptLocked(frameChannel, previous) == 0;
            }
        }

        internal static bool Heal()
        { bool frame = Heal(frameChannel); bool identity = Heal(identityChannel); return Heal(presentChannel) && identity && frame; }
        private static bool Heal(Channel channel)
        {
            FrameCoordinationTrace pending;
            lock (ledgerGate)
            {
                pending = channel.Active;
                // 健康的有界遥测不属于恢复债务 普通修复不能中断它
                if (channel.Telemetry && pending != null && pending.Error == 0 && !pending.stopRequested) return true;
            }
            if (pending != null)
            {
                // 持有 ledgerGate 时不能获取清理锁
                if (channel.Telemetry)
                {
                    // 健康的停止中身份跟踪自行结清 失败跟踪走有界 STOP 恢复路径
                    if (pending.Error == 0 && !pending.Drained) return true;
                    pending.StopWithin(IdentityHealWaitMs);
                }
                else
                {
                    // 健康采集不被修复打断 停止失败的消费者须先重试关闭 不能反过来等它自行退出
                    if (!pending.Drained && !pending.stopRequested && pending.Error == 0) return false;
                    pending.StopWithin(IdentityHealWaitMs);
                }
            }
            lock (ledgerGate)
            {
                if (channel.Active != null) return false;
                string name; if (!Settings.TryLoadStr(channel.Key, out name)) return false; if (name.Length == 0) return true;
                return RecoverReceiptLocked(channel, name) == 0;
            }
        }
        // 进程退出时也处理已退役遥测记录 停止或清除失败均保留恢复债务
        internal static bool CloseIdentity(int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            bool identity = CloseSealed(identityChannel, timeoutMs);
            int remaining = Math.Max(0, timeoutMs - (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds));
            return CloseSealed(presentChannel, remaining) && identity;
        }
        private static bool CloseSealed(Channel channel, int timeoutMs)
        {
            FrameCoordinationTrace pending;
            lock (ledgerGate)
            {
                if (channel.Identity) identitySealed = true; else presentSealed = true;
                pending = channel.Active;
            }
            if (pending != null) pending.StopWithin(timeoutMs);
            lock (ledgerGate)
            {
                if (channel.Active != null) return false;
                string receipt;
                if (!Settings.TryLoadStr(channel.Key, out receipt)) return false;
                return receipt.Length == 0 || RecoverReceiptLocked(channel, receipt) == 0;
            }
        }
    }
}
