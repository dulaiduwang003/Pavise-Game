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
        // 只恢复与本方各通道前缀匹配的记录，包括已退役的遥测通道。
        internal const string FrameSessionPrefix = "Pavise.FrameCoordination.", IdentitySessionPrefix = "Pavise.GpuIdentity.";
        internal const string PresentSessionPrefix = "Pavise.PresentProbe.";
        internal const string PresentProbeReceiptKey = "PresentProbeTraceV1";
        internal const string PresentProbeSessionPattern = @"\APavise\.PresentProbe\.[0-9]+\.[0-9a-f]{32}\z";
        internal static readonly Guid DxgKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");
        // 微软 DxgKrnl 的 Present 关键字以本机清单为准；DXGI、D3D9 使用 Analytic 与 Events。
        internal const ulong DxgKrnlPresentKeyword = 0x8000000UL, RuntimePresentKeyword = 0x8000000000000002UL;
        // 按事件标识过滤，避免同关键字下的无关事件占满缓冲；与 PresentActivityCapture.Classify 保持一致。
        internal static readonly ushort[] DxgiPresentIds = { 42, 55 }, D3d9PresentIds = { 1 }, DxgKrnlPresentIds = { 171, 184, 215 };
        // Windows 10 的提交事件位于 Base 关键字下；仅带事件标识过滤时启用 Base。
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
        // EVENT_FILTER_TYPE_EVENT_ID，Windows 8.1 起可用。
        internal const uint EventIdFilterType = 0x80000200;
        // 提前分配缓冲池，避免短探测窗口内扩容。
        internal const uint PresentProbeBuffers = 32;
        // ERROR_SHUTDOWN_IN_PROGRESS 表示进程退出时已关闭 GPU 身份通道。
        internal const int IdentityClosedError = 1115;
        private const int IdentityHealWaitMs = 3500;
        private static readonly object ledgerGate = new object();
        private sealed class Channel
        {
            internal string Key, Pattern;
            internal bool Identity, Present;
            internal FrameCoordinationTrace Active;
            // 有界遥测通道自行处理过期记录，健康运行时不视为待恢复债务。
            internal bool Telemetry { get { return Identity || Present; } }
        }
        private static readonly Channel frameChannel = new Channel { Key = TraceKey, Pattern = @"\APavise\.FrameCoordination\.[0-9]+\.[0-9a-f]{32}\z" },
            identityChannel = new Channel { Key = IdentityTraceKey, Pattern = Settings.GpuIdentitySessionPattern, Identity = true },
            presentChannel = new Channel { Key = PresentProbeReceiptKey, Pattern = PresentProbeSessionPattern, Present = true };
        // 仅在进程退出的 CloseIdentity 中设置，由 ledgerGate 保护。
        private static bool identitySealed, presentSealed;
        private Channel channel = frameChannel;
        private readonly object memory = new object();
        private readonly object cleanup = new object();
        private readonly object failureGate = new object();
        private IntPtr properties, loggerName;
        private ulong session, consumer = Native.InvalidProcessTraceHandle;
        private Native.EventRecordCallback callback;
        private Thread pump;
        private bool started, claimed;
        // stopRequested 保留恢复债务；两个原生句柄均关闭后设置 handlesClosed，由最后完成的线程结清通道。

        private volatile bool stopRequested, handlesClosed;
        private volatile bool receiptSettled;
        private string name = SessionName(FrameSessionPrefix);
        internal int Error;
        // 错误码须结合阶段解读：系统调用与回执账本可能返回相同错误码。
        internal string Stage = "";
        // 启动或消费失败与清理失败各保留首次证据，后来的恢复不能把失败采样改成成功。
        internal int PrimaryError, CleanupError;
        internal string PrimaryStage = "", CleanupStage = "";
        internal string FailureDetail
        {
            get
            {
                lock (failureGate)
                    return "primary=" + PrimaryError + "@" + PrimaryStage + " cleanup=" + CleanupError + "@" + CleanupStage
                        + " recovery=" + (receiptSettled || !claimed ? "settled" : "pending");
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
        // 分别记录事件写入丢失与消费者缓冲送达丢失。
        internal uint LostEvents, LostBuffers;
        // 未启用事件标识过滤的提供程序数。
        internal int UnfilteredProviders;
        internal string LossDetail
        { get { return "events=" + LostEvents + " buffers=" + LostBuffers + (UnfilteredProviders == 0 ? "" : " unfiltered=" + UnfilteredProviders); } }
        internal int PumpTid;
        internal double CpuMs = double.NaN;
        internal bool Drained { get { Thread current = pump; return current == null || !current.IsAlive; } }
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
                    // 健康的有界遥测不是恢复债务；失败、正在停止或前一进程遗留的记录才是。
                    return frame || TelemetryResidueLocked(identityChannel) || TelemetryResidueLocked(presentChannel);
                }
            }
        }
        // 调用方需持有 ledgerGate。
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
        // 完整 Start、采样和 Stop 路径使用替身，不创建 ETW 会话、消费者或提供程序。
        internal static StartTraceHook StartForTest;
        internal static Func<ulong> OpenForTest;
        // 测试替身中，ids 为 null 表示不使用事件标识过滤。
        internal static Func<Guid, ulong, ushort[], int> EnableForTest;
        // 正值覆盖测试系统版本，用于核对关键字选择。
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
        // 身份记录使用独立的严格写入器，不推进用户设置的变更代次。
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
                BufferSize = 64, MinimumBuffers = minimumBuffers, MaximumBuffers = 32, FlushTimer = 1,
                LogFileMode = Native.EventTraceRealTimeMode | (presenterOnly ? 0u : 0x02000000u),
                EnableFlags = start && !presenterOnly ? 0x0812u : 0u,
                LoggerNameOffset = (uint)bytes };
            Marshal.StructureToPtr(value, p, false); Marshal.Copy(bytes2, 0, IntPtr.Add(p, bytes), bytes2.Length); return p;
        }
        internal bool Start(int pid, Native.EventRecordCallback receive)
        { return Start(pid, receive, false); }
        internal bool Start(int pid, Native.EventRecordCallback receive, bool presenterOnly)
        { lock (cleanup) return StartLocked(pid, receive, presenterOnly); }
        // 初始化全程持有清理锁，防止停止操作插入到句柄只打开一半的状态。
        private bool StartLocked(int pid, Native.EventRecordCallback receive, bool presenterOnly)
        {
            if (started) { RecordPrimaryFailure(1247, "already-started"); return false; }
            started = true;
            if (!presenterOnly && GetActiveProcessorGroupCount() != 1) { RecordPrimaryFailure(50, "processor-groups"); return false; }
            int refusal;
            lock (ledgerGate) refusal = ClaimLocked();
            if (refusal != 0) { RecordPrimaryFailure(refusal, Stage); return false; }
            properties = Props(name, true, presenterOnly);
            Error = StartSession(out session, name, properties);
            if (Error != 0) { RecordPrimaryFailure(Error, "start-trace"); session = 0; Stop(); return false; }
            IntPtr pidPointer = Marshal.AllocHGlobal(4), filterPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Filter)));
            try
            {
                Marshal.WriteInt32(pidPointer, pid);
                Marshal.StructureToPtr(new Filter { Ptr = (ulong)pidPointer.ToInt64(), Size = 4, Type = 0x80000004 }, filterPointer, false);
                var parameters = new Native.EnableTraceParameters { Version = 2, EnableFilterDesc = filterPointer, FilterDescCount = 1 };
                Guid dxgi = FrameCoordinationSampler.Dxgi, d3d9 = FrameCoordinationSampler.D3d9;
                Error = Native.EnableTraceEx2(session, ref dxgi, 1, 5, 0x8000000000000002UL, 0, 0, ref parameters);
                if (Error != 0) Stage = "enable-dxgi";
                else if ((Error = Native.EnableTraceEx2(session, ref d3d9, 1, 5, 0x8000000000000002UL, 0, 0, ref parameters)) != 0) Stage = "enable-d3d9";
            }
            finally { Marshal.FreeHGlobal(pidPointer); Marshal.FreeHGlobal(filterPointer); }
            if (Error != 0) { RecordPrimaryFailure(Error, Stage); Stop(); return false; }
            return OpenAndPumpLocked(receive);
        }
        // 同进程通道占用提示，不作为后续启动成功的保证。
        internal static bool PresentProbeBusy
        {
            get { lock (ledgerGate) return presentChannel.Active != null; }
        }
        // 只采集系统级画面提交事件，不按 PID 过滤，不启用内核线程标志，共使用三个提交提供程序。
        internal bool StartPresentProbe(Native.EventRecordCallback receive)
        {
            lock (cleanup)
            {
                if (started) { RecordPrimaryFailure(1247, "already-started"); return false; }
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
        // EVENT_FILTER_EVENT_ID 的内存布局：FilterIn、Reserved 各一字节，Count 两字节，后面是 Count 个两字节标识。
        internal static byte[] EventIdFilterBytes(ushort[] ids)
        {
            var bytes = new byte[4 + 2 * ids.Length];
            bytes[0] = 1;
            bytes[2] = (byte)ids.Length; bytes[3] = (byte)(ids.Length >> 8);
            for (int i = 0; i < ids.Length; i++) { bytes[4 + 2 * i] = (byte)ids[i]; bytes[5 + 2 * i] = (byte)(ids[i] >> 8); }
            return bytes;
        }
            // 事件标识过滤不受支持时仅回退一次，并记录未过滤的提供程序数。
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
        // 调用方持有清理锁，且会话已启动、提供程序已启用。
        private bool OpenAndPumpLocked(Native.EventRecordCallback receive)
        {
            callback = receive; loggerName = Marshal.StringToHGlobalUni(name);
            var log = new Native.EventTraceLogfile { LoggerName = loggerName,
                ProcessTraceMode = Native.ProcessTraceModeRealTime | Native.ProcessTraceModeEventRecord | 0x1000,
                EventRecordCallback = Marshal.GetFunctionPointerForDelegate(callback) };
            consumer = OpenConsumer(ref log);
            if (consumer == Native.InvalidProcessTraceHandle) { int error = Marshal.GetLastWin32Error(); RecordPrimaryFailure(error == 0 ? 6 : error, "open-consumer"); Stop(); return false; }
            ulong opened = consumer;
            pump = new Thread(delegate() {
                PumpTid = Native.GetCurrentThreadId(); LoopProfile.Tag("FrameTracePump");
                try { int rc = ProcessConsumer(opened); if (rc != 0 && rc != 1223) RecordPrimaryFailure(rc, "process-trace"); }
                finally {
                    IntPtr thread = Native.OpenThread(Native.THREAD_QUERY_LIMITED_INFORMATION, false, PumpTid);
                    if (thread != IntPtr.Zero) { try { long b,e,k,u; if (Native.GetThreadTimes(thread,out b,out e,out k,out u)) CpuMs=(k+u)/10000.0; } finally { Native.CloseHandle(thread); } }
                    // ProcessTrace 已返回，不再有回调；迟到的采样线程自行结清，不再取得清理锁或等待自身。
                    if (handlesClosed) Settle();
                }
            });
            pump.IsBackground = true; pump.Priority = ThreadPriority.BelowNormal;
            pump.Name = "Pavise.FrameEvents"; pump.Start();
            return true;
        }
        // 调用方持有 ledgerGate；取得通道归属后返回 0，拒绝阶段写入 Stage。
        private int ClaimLocked()
        {
            if (channel.Identity && identitySealed) { Stage = "channel-closed"; return IdentityClosedError; }
            if (channel.Present && presentSealed) { Stage = "channel-closed"; return IdentityClosedError; }
            if (channel.Active != null) { Stage = "channel-busy"; return 170; }
            string previous, written;
            if (!Settings.TryLoadStr(channel.Key, out previous)) { Stage = "ledger-read"; return 5; }
            if (previous.Length != 0)
            {
                // 只有遥测通道能自行恢复，且仅处理没有存活跟踪持有的自身过期记录。
                if (!channel.Telemetry) { Stage = "ledger-stale"; return 5; }
                int recovered = RecoverReceiptLocked(channel, previous);
                if (recovered != 0) { Stage = "ledger-recover"; return recovered; }
            }
            if (!WriteReceipt(channel, name) || !Settings.TryLoadStr(channel.Key, out written) || written != name) { Stage = "ledger-write"; return 5; }
            FrameRecoveryBudget.Own(channel.Key, 0, 0, name);
            claimed = true; channel.Active = this;
            return 0;
        }
        // 调用方持有 ledgerGate，并已确认当前进程没有存活跟踪持有该通道。
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
        // 若初始化仍持有清理锁，报告恢复债务，避免阻塞退出。
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
            bool stopped = false;
            bool released = FrameRecoveryBudget.Run(channel.Key, delegate { stopped = StopCore(waitMs); return receiptSettled; });
            return released && stopped;
        }
        private bool StopCore(int waitMs)
        {
            int rc = 0;
            stopRequested = true;
            if (session != 0)
            {
                rc = Control(session, name, properties, Native.EventTraceControlStop);
                var value = (Native.EventTraceProperties)Marshal.PtrToStructure(properties, typeof(Native.EventTraceProperties));
                LostEvents = value.EventsLost; LostBuffers = value.RealTimeBuffersLost;
                Lost = LostEvents + LostBuffers;
                // 停止失败时保留会话句柄，由所属预算控制后续恢复。
                if (rc == 0 || rc == 4201 || rc == 234) session = 0;
                if (rc != 0 && rc != 4201) RecordCleanupFailure(rc, "stop-trace");
            }
            if (consumer != Native.InvalidProcessTraceHandle)
            {
                int close = CloseConsumer(consumer);
                // 实时追踪返回关闭待完成仍需排空回调。
                if (close == 0 || close == 7007 || close == 6) consumer = Native.InvalidProcessTraceHandle;
                if (close != 0 && close != 7007) RecordCleanupFailure(close, "close-consumer");
            }
            bool closedHandles = session == 0 && consumer == Native.InvalidProcessTraceHandle;
            if (closedHandles) handlesClosed = true;
            bool settled = closedHandles && PumpFinished(waitMs);
            bool cleared = settled && Settle();
            return settled && cleared && Error == 0 && Lost == 0;
        }
        private bool PumpFinished(int waitMs)
        {
            Thread current = pump;
            if (current == null || !current.IsAlive) return true;
            // 回调中发出的停止不能等待自身采样线程；该线程返回后自行结清。
            if (current == Thread.CurrentThread) return false;
            return current.Join(Math.Max(0, waitMs));
        }
        // 停止方与迟到线程可并发结清；须确认两个句柄关闭且 ProcessTrace 返回，清账失败仍保留债务。

        private bool Settle()
        {
            bool cleared = true;
            lock (ledgerGate)
            {
                // 迟到的停止操作不能清除新通道所有者的记录。
                if (!receiptSettled)
                {
                    if (claimed) cleared = ClearLedger(channel, name);
                    if (cleared)
                    {
                        receiptSettled = true;
                        // 清账失败时保留原对象，后续恢复继续用它的句柄状态与原预算，不能另起所有者。
                        if (channel.Active == this) channel.Active = null;
                        if (claimed) FrameRecoveryBudget.Settled(channel.Key, name);
                    }
                }
            }
            if (!cleared) RecordCleanupFailure(5, "ledger-clear");
            lock (memory) FreeMemory();
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
        internal static bool Heal()
        { bool frame = Heal(frameChannel); bool identity = Heal(identityChannel); return Heal(presentChannel) && identity && frame; }
        private static bool Heal(Channel channel)
        {
            FrameCoordinationTrace pending;
            lock (ledgerGate)
            {
                pending = channel.Active;
                // 健康的有界遥测不属于恢复债务，普通修复不能中断它。
                if (channel.Telemetry && pending != null && pending.Error == 0 && !pending.stopRequested) return true;
            }
            if (pending != null)
            {
                // 持有 ledgerGate 时不能获取清理锁。
                if (channel.Telemetry)
                {
                    // 健康的停止中身份跟踪自行结清；失败跟踪走有界 STOP 恢复路径。
                    if (pending.Error == 0 && !pending.Drained) return true;
                    pending.StopWithin(IdentityHealWaitMs);
                }
                else
                {
                    if (!pending.Drained) return false;
                    pending.Stop();
                }
            }
            lock (ledgerGate)
            {
                if (channel.Active != null) return false;
                string name; if (!Settings.TryLoadStr(channel.Key, out name)) return false; if (name.Length == 0) return true;
                return RecoverReceiptLocked(channel, name) == 0;
            }
        }
        // 进程退出时也处理已退役遥测记录；停止或清除失败均保留恢复债务。
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
