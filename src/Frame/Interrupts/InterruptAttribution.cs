using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class InterruptAttribution : IDisposable
    {
        private sealed class RoutineStat
        {
            public long Count;

            public long TotalTicks;

            public long MaxTicks;

            public long BadDuration;

            public ulong CpuMask;

            public bool CpuMaskTruncated;

            public readonly long[] Buckets = new long[13];
        }

        private sealed class Module
        {
            public ulong Base;

            public ulong End;

            public string Name;
        }

        private const int WnodeFlagTracedGuid = 131072;

        private const uint ProcessModeRealTime = 256u;

        private const uint ProcessModeRawTimestamp = 4096u;

        private const uint ProcessModeEventRecord = 268435456u;

        private const uint ControlStop = 1u;

        private const uint FlagDpc = 32u;

        private const uint FlagInterrupt = 64u;

        private const int ErrorAccessDenied = 5;

        private const int ErrorAlreadyExists = 183;

        private const int ErrorInvalidParameter = 87;

        private const string SessionName = "PaviseInterruptProbe";

        private const string AliveEventName = "Global\\Pavise_InterruptProbeAlive";

        internal const int BucketCount = 13;

        private const int BucketOver500 = 9;

        private const int BucketOver1Ms = 10;

        private const ushort HeaderFlag32Bit = 32;

        private const ushort HeaderFlag64Bit = 64;

        private static readonly Guid SessionGuid = new Guid("7d1f8c2a-64b3-4c5e-9a1d-2e8f0b6c4d3a");

        private static readonly Guid PerfInfoGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");

        private static readonly object ownershipGate = new object();
        private static InterruptAttribution retainedOwner;
        private EventWaitHandle aliveOwned;
        private IntPtr loggerName, sessionProperties;
        private ulong sessionHandle;
        private bool startAttempted, sessionPending, sessionStopAccepted, consumerCloseAccepted, workerStarted, consumerReturned;
        private volatile bool decodeRevoked;
        private bool incomplete, forceConsumerClose, stopProvedComplete;
        private uint capturedEventsLost, capturedBuffersLost, cleanupError;
        private int stopFailures, closeFailures, ownershipFailures;
        private long stopNext, closeNext, ownershipNext;
        private Timer cleanupTimer;
        private InterruptAttributionResult stopResult;
        private const int CleanupAttempts = 3, StopWaitMs = 10000;
        private const uint ClosePending = 7007, InvalidHandle = 6, SessionNotFound = 4201;

        private static long NowMilliseconds()
        { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }

        internal static readonly double[] BucketUpperUs = new double[13]
        {
            1.0,
            2.0,
            5.0,
            10.0,
            25.0,
            50.0,
            100.0,
            250.0,
            500.0,
            1000.0,
            2000.0,
            5000.0,
            double.MaxValue
        };

        private readonly object gate = new object();

        private readonly Dictionary<ulong, RoutineStat> dpcHits = new Dictionary<ulong, RoutineStat>();

        private readonly Dictionary<ulong, RoutineStat> isrHits = new Dictionary<ulong, RoutineStat>();

        private readonly List<Module> modules = new List<Module>();

        private double usPerTick;

        private long sanityMaxTicks;

        private long dpcTotal;

        private long isrTotal;

        private ulong traceHandle;

        private Thread worker;

        private Native.EventRecordCallback keepAlive;

        private volatile bool started;

        private volatile bool stopRequested;

        private volatile bool consumerExitedEarly;

        private volatile bool processTraceSucceeded;

        public bool Busy { get; private set; }

        public string FailDetail { get; private set; }

        public static string StartFailureText(InterruptAttribution ia)
        {
            if (ia == null)
            {
                return Lang.T("irqmove.nosession");
            }
            if (ia.Busy)
            {
                return Lang.T("irq.probe.busy");
            }
            if (!string.IsNullOrEmpty(ia.FailDetail))
            {
                return ia.FailDetail;
            }
            return Lang.T("irqmove.nosession");
        }

        private bool TakeOwnership()
        {
            lock (ownershipGate)
            {
                if (retainedOwner != null) return object.ReferenceEquals(retainedOwner, this);
                try
                {
                    EventWaitHandle existing;
                    if (EventWaitHandle.TryOpenExisting(AliveEventName, out existing))
                    { existing.Close(); return false; }
                    bool created;
                    var owned = new EventWaitHandle(false, EventResetMode.ManualReset, AliveEventName, out created);
                    if (!created) { owned.Close(); return false; }
                    aliveOwned = owned;
                    retainedOwner = this;
                    return true;
                }
                catch { return false; }
            }
        }

        private bool ReleaseOwnership()
        {
            lock (ownershipGate)
            {
                try { if (aliveOwned != null) aliveOwned.Close(); }
                catch { return false; }
                aliveOwned = null;
                if (object.ReferenceEquals(retainedOwner, this)) retainedOwner = null;
                return true;
            }
        }
        public bool Start()
        {
            lock (gate)
            {
                if (started && !stopRequested) return true;
                if (startAttempted || stopRequested) return false;
                startAttempted = true;
                Busy = false; FailDetail = null;
                if (!TakeOwnership())
                {
                    Busy = true; FailDetail = Lang.T("irq.probe.busy");
                    Logger.Log(() => Lang.T("log.interruptattribution.9"));
                    return false;
                }
                try
                {
                    Native.TryEnableDebugPrivilege();
                    string error;
                    if (!LoadModules(out error))
                    {
                        FailDetail = error; Logger.Warn(Logger.Format("中断 {0}", "IRQ {0}", error));
                        BeginCleanupLocked(); return false;
                    }
                    sessionProperties = AllocProps(FlagDpc | FlagInterrupt);
                    ulong opened;
                    uint result = StartTrace(out opened, SessionName, sessionProperties);
                    if (result == ErrorAlreadyExists)
                    {
                        // 旧会话必须先由本所有者按同一份预算结清 失败时不能重建对象再试
                        sessionPending = true;
                        TryStopSessionLocked(NowMilliseconds());
                        if (!sessionStopAccepted) { FailDetail = Lang.Text("旧中断采集会话停止未确认", "Stopping the previous interrupt capture session was not confirmed"); BeginCleanupLocked(); return false; }
                        IntPtr previousProperties = sessionProperties;
                        sessionProperties = IntPtr.Zero;
                        sessionPending = sessionStopAccepted = false;
                        Marshal.FreeHGlobal(previousProperties);
                        sessionProperties = AllocProps(FlagDpc | FlagInterrupt);
                        result = StartTrace(out opened, SessionName, sessionProperties);
                    }
                    if (result != 0)
                    {
                        if (result == ErrorAlreadyExists) sessionPending = true;
                        if (result == ErrorInvalidParameter)
                        {
                            Logger.Log(() => Lang.T("log.interruptattribution.1"));
                            FailDetail = Lang.T("irq.fail.nokernel");
                        }
                        else
                        {
                            Logger.Warn(Lang.T("log.interruptattribution.2") + result);
                            bool elevated = false;
                            try { elevated = Native.IsElevated(); } catch { }
                            FailDetail = result != ErrorAccessDenied ? Lang.T("irq.fail.start") + result
                                : elevated ? Lang.T("irq.fail.denied") : Lang.T("irq.state.needadmin");
                        }
                        BeginCleanupLocked();
                        return false;
                    }
                    sessionHandle = opened; sessionPending = true; sessionStopAccepted = false;
                    stopProvedComplete = incomplete = false; capturedEventsLost = capturedBuffersLost = cleanupError = 0;
                    keepAlive = OnEvent;
                    loggerName = Marshal.StringToHGlobalUni(SessionName);
                    Native.EventTraceLogfile logfile = default(Native.EventTraceLogfile);
                    logfile.LoggerName = loggerName;
                    logfile.ProcessTraceMode = ProcessModeRealTime | ProcessModeRawTimestamp | ProcessModeEventRecord;
                    logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(keepAlive);
                    traceHandle = OpenTrace(ref logfile);
                    if (traceHandle == ulong.MaxValue || traceHandle == 0)
                    {
                        int openError = Marshal.GetLastWin32Error();
                        Logger.Warn(Lang.T("log.interruptattribution.3") + openError);
                        FailDetail = Lang.T("irq.fail.open") + openError;
                        traceHandle = 0; BeginCleanupLocked(); return false;
                    }
                    long frequency = logfile.LogfileHeader.PerfFreq;
                    if (frequency <= 0) frequency = Stopwatch.Frequency;
                    usPerTick = 1000000.0 / frequency; sanityMaxTicks = frequency;
                    worker = new Thread(RunProcessTrace) { IsBackground = true };
                    try { worker.Priority = ThreadPriority.Highest; } catch { }
                    consumerExitedEarly = processTraceSucceeded = false;
                    worker.Start(); workerStarted = true; started = true;
                    return true;
                }
                catch (Exception error)
                {
                    FailDetail = Lang.Text("中断采集启动异常：", "Interrupt capture failed to start: ") + error.GetType().Name;
                    BeginCleanupLocked();
                    return false;
                }
            }
        }

        private void RunProcessTrace()
        {
            try
            {
                ulong[] handles = new ulong[1] { traceHandle };
                uint processTraceResult = ProcessTrace(handles, 1u, IntPtr.Zero, IntPtr.Zero);
                processTraceSucceeded = processTraceResult == 0;
                if (processTraceResult != 0)
                {
                    consumerExitedEarly = true;
                }
            }
            catch
            {
                consumerExitedEarly = true;
            }
            finally
            {
                lock (gate)
                {
                    if (!stopRequested) { consumerExitedEarly = true; incomplete = true; stopRequested = decodeRevoked = true; }
                    consumerReturned = true;
                    CleanupLocked();
                }
            }
        }

        internal static bool CaptureComplete(bool stopSucceeded, bool workerDone, bool processSucceeded, bool exitedEarly)
        {
            if (stopSucceeded && workerDone && processSucceeded)
            {
                return !exitedEarly;
            }
            return false;
        }

        internal static bool HasMappedEvents(long rawDpc, long rawIsr, int mappedDrivers)
        {
            if (rawDpc >= 0 && rawIsr >= 0 && (rawDpc > 0 || rawIsr > 0))
            {
                return mappedDrivers > 0;
            }
            return false;
        }

        public InterruptAttributionResult Stop()
        {
            // 先撤销新的采集资格 正常 STOP 仍排空原窗口 失败或超时才撤销解码并弃用整窗
            stopRequested = true;
            Thread draining;
            lock (gate)
            {
                if (!startAttempted)
                    return new InterruptAttributionResult { Incomplete = true, Error = Lang.T("t.interruptattribution.4") };
                if (stopResult != null) return stopResult;
                CleanupLocked(); draining = worker;
            }
            bool workerStopped = !workerStarted || consumerReturned;
            if (!workerStopped && draining != null && draining != Thread.CurrentThread)
            { try { workerStopped = draining.Join(StopWaitMs); } catch { workerStopped = false; } }
            lock (gate)
            {
                if (stopResult != null) return stopResult;
                workerStopped = !workerStarted || consumerReturned;
                if (!workerStopped) { incomplete = true; decodeRevoked = forceConsumerClose = true; }
                CleanupLocked();
                var interruptAttributionResult = new InterruptAttributionResult {
                    EventsLost = capturedEventsLost, BuffersLost = capturedBuffersLost,
                    Incomplete = incomplete || !CaptureComplete(stopProvedComplete, workerStopped, processTraceSucceeded, consumerExitedEarly) };
                stopResult = interruptAttributionResult;
                if (!workerStopped)
                {
                    interruptAttributionResult.Error = Lang.T("t.interruptattribution.5");
                    return interruptAttributionResult;
                }
                Dictionary<string, DriverInterrupt> dictionary = new Dictionary<string, DriverInterrupt>();
                Dictionary<string, long[]> dictionary2 = new Dictionary<string, long[]>();
                Fold(dictionary, dictionary2, dpcHits, true);
                Fold(dictionary, dictionary2, isrHits, false);
                foreach (KeyValuePair<string, DriverInterrupt> item in dictionary)
                {
                    DriverInterrupt value = item.Value;
                    long[] value2;
                    if (dictionary2.TryGetValue(item.Key, out value2))
                    {
                        value.DpcOver500Us = SumFrom(value2, BucketOver500);
                        value.DpcOver1Ms = SumFrom(value2, BucketOver1Ms);
                        value.DpcBuckets = (long[])value2.Clone();
                    }
                    interruptAttributionResult.Drivers.Add(value);
                }
                interruptAttributionResult.Drivers.Sort(delegate(DriverInterrupt a, DriverInterrupt b)
                {
                    long value3 = a.Dpc + a.Isr;
                    return (b.Dpc + b.Isr).CompareTo(value3);
                });
                long numericValue = 0L;
                foreach (DriverInterrupt driver in interruptAttributionResult.Drivers)
                {
                    numericValue += driver.Dpc;
                }
                interruptAttributionResult.Unmapped = Math.Max(0L, dpcTotal - numericValue);
                bool attributionMissing = (dpcTotal > 0 || isrTotal > 0) && interruptAttributionResult.Drivers.Count == 0;
                string errorValue = (attributionMissing ? (Logger.Text("已采集中断事件，但无法映射到驱动模块，本局中断归因不可用 DPC=", "Interrupt events were captured but could not be mapped to driver modules; interrupt attribution is unavailable for this capture DPC=") + dpcTotal + " ISR=" + isrTotal + Logger.Text(" 模块=", " modules=") + modules.Count) : null);
                if (attributionMissing)
                {
                    Logger.Warn(Logger.Format("中断 {0}", "IRQ {0}", errorValue));
                }
                interruptAttributionResult.Ok = HasMappedEvents(dpcTotal, isrTotal, interruptAttributionResult.Drivers.Count) && !interruptAttributionResult.Incomplete && !interruptAttributionResult.Lossy;
                if (interruptAttributionResult.Lossy)
                {
                    Logger.Log(() => string.Format(Lang.T("log.interruptattribution.lossy"), interruptAttributionResult.EventsLost, interruptAttributionResult.BuffersLost));
                }
                if (interruptAttributionResult.Incomplete)
                {
                    interruptAttributionResult.Error = Lang.Text("ETW 消费或停止未完整 win32=", "ETW consumption or stop incomplete win32=") + cleanupError;
                }
                else if (interruptAttributionResult.Lossy)
                {
                    interruptAttributionResult.Error = Lang.F("t.interruptattribution.7", interruptAttributionResult.EventsLost, interruptAttributionResult.BuffersLost);
                }
                else if (attributionMissing)
                {
                    interruptAttributionResult.Error = errorValue;
                }
                else if (!interruptAttributionResult.Ok)
                {
                    interruptAttributionResult.Error = Lang.T("t.interruptattribution.6");
                }
                return interruptAttributionResult;
            }
        }

        private void Fold(Dictionary<string, DriverInterrupt> byMod, Dictionary<string, long[]> dpcBuckets, Dictionary<ulong, RoutineStat> hits, bool dpc)
        {
            foreach (KeyValuePair<ulong, RoutineStat> hit in hits)
            {
                string driverValue = Resolve(hit.Key);
                if (driverValue == null)
                {
                    continue;
                }
                DriverInterrupt value;
                if (!byMod.TryGetValue(driverValue, out value))
                {
                    DriverInterrupt driverInterrupt = new DriverInterrupt();
                    driverInterrupt.Driver = driverValue;
                    value = (byMod[driverValue] = driverInterrupt);
                }
                RoutineStat value2 = hit.Value;
                if (dpc)
                {
                    value.CpuMask |= value2.CpuMask;
                    value.CpuMaskTruncated |= value2.CpuMaskTruncated;
                }
                value.BadDuration += value2.BadDuration;
                double dpcTotalUsValue = (double)value2.TotalTicks * usPerTick;
                double dpcMaxUsValue = (double)value2.MaxTicks * usPerTick;
                if (dpc)
                {
                    value.Dpc += value2.Count;
                    value.DpcTotalUs += dpcTotalUsValue;
                    if (dpcMaxUsValue > value.DpcMaxUs)
                    {
                        value.DpcMaxUs = dpcMaxUsValue;
                    }
                    long[] value3;
                    if (!dpcBuckets.TryGetValue(driverValue, out value3))
                    {
                        value3 = (dpcBuckets[driverValue] = new long[13]);
                    }
                    for (int i = 0; i < 13; i++)
                    {
                        value3[i] += value2.Buckets[i];
                    }
                }
                else
                {
                    value.Isr += value2.Count;
                }
            }
        }

        internal static long SumFrom(long[] b, int startIndex)
        {
            long sumFromResult = 0L;
            if (b == null)
            {
                return 0L;
            }
            for (int i = startIndex; i < 13; i++)
            {
                sumFromResult += b[i];
            }
            return sumFromResult;
        }

        private void OnEvent(ref Native.EventRecord record)
        {
            if (decodeRevoked) return;
            if (record.EventHeader.ProviderId != PerfInfoGuid)
            {
                return;
            }
            byte opcode = record.EventHeader.EventDescriptor.Opcode;
            bool isIsr = opcode == 67;
            bool isDpc = opcode == 66 || opcode == 68 || opcode == 69;
            if ((!isIsr && !isDpc) || record.UserData == IntPtr.Zero)
            {
                return;
            }
            ushort flags = record.EventHeader.Flags;
            int size = (((flags & HeaderFlag64Bit) != 0) ? 8 : (((flags & HeaderFlag32Bit) != 0) ? 4 : IntPtr.Size));
            if (record.UserDataLength < 8 + size)
            {
                return;
            }
            long toInt64Result = record.UserData.ToInt64();
            long int64 = Marshal.ReadInt64(new IntPtr(toInt64Result));
            ulong key = (ulong)((size == 8) ? Marshal.ReadInt64(new IntPtr(toInt64Result + 8)) : ((uint)Marshal.ReadInt32(new IntPtr(toInt64Result + 8))));
            long timeStamp = record.EventHeader.TimeStamp;
            long totalTicksValue = timeStamp - int64;
            bool validDuration = totalTicksValue >= 0 && totalTicksValue <= sanityMaxTicks;
            Dictionary<ulong, RoutineStat> dictionary = (isIsr ? isrHits : dpcHits);
            RoutineStat value;
            if (!dictionary.TryGetValue(key, out value))
            {
                value = (dictionary[key] = new RoutineStat());
            }
            value.Count++;
            ushort processorIndex = (ushort)record.BufferContext;
            if (processorIndex < 64)
            {
                value.CpuMask |= (ulong)(1L << (int)processorIndex);
            }
            else
            {
                value.CpuMaskTruncated = true;
            }
            if (!validDuration)
            {
                value.BadDuration++;
            }
            else
            {
                value.TotalTicks += totalTicksValue;
                if (totalTicksValue > value.MaxTicks)
                {
                    value.MaxTicks = totalTicksValue;
                }
                value.Buckets[BucketOf(totalTicksValue)]++;
            }
            if (isIsr)
            {
                isrTotal++;
            }
            else
            {
                dpcTotal++;
            }
        }

        private int BucketOf(long ticks)
        {
            double scaledTicks = (double)ticks * usPerTick;
            for (int i = 0; i < 12; i++)
            {
                if (scaledTicks < BucketUpperUs[i])
                {
                    return i;
                }
            }
            return 12;
        }

        private string Resolve(ulong addr)
        {
            for (int i = 0; i < modules.Count; i++)
            {
                if (addr >= modules[i].Base && addr < modules[i].End)
                {
                    return modules[i].Name;
                }
            }
            return null;
        }

        private void BeginCleanupLocked()
        {
            stopRequested = decodeRevoked = forceConsumerClose = incomplete = true;
            CleanupLocked();
        }

        private void TryStopSessionLocked(long now)
        {
            if (!sessionPending || sessionStopAccepted || stopFailures >= CleanupAttempts || now < stopNext) return;
            uint result;
            try { result = ControlTrace(sessionHandle, SessionName, sessionProperties, ControlStop); }
            catch { result = uint.MaxValue; }
            if (result == 0 || result == SessionNotFound)
            {
                sessionStopAccepted = true;
                if (result == 0)
                {
                    stopProvedComplete = true;
                    var properties = (Native.EventTraceProperties)Marshal.PtrToStructure(sessionProperties, typeof(Native.EventTraceProperties));
                    capturedEventsLost = properties.EventsLost;
                    capturedBuffersLost = properties.RealTimeBuffersLost + properties.LogBuffersLost;
                }
                else { incomplete = true; cleanupError = result; }
            }
            else
            {
                stopFailures++; stopNext = now + (1000L << (stopFailures - 1));
                incomplete = forceConsumerClose = decodeRevoked = true; cleanupError = result;
                Logger.Warn(Logger.Format("中断采集停止失败：返回码={0} 尝试={1}/{2}；保留会话归属", "Interrupt capture stop failed: rc={0} attempt={1}/{2}; session ownership retained", result, stopFailures, CleanupAttempts));
            }
        }

        private void CleanupLocked()
        {
            if (!stopRequested) return;
            long now = NowMilliseconds();
            TryStopSessionLocked(now);
            if (traceHandle != 0 && traceHandle != ulong.MaxValue && !consumerCloseAccepted && (forceConsumerClose || consumerReturned || !workerStarted)
                && closeFailures < CleanupAttempts && now >= closeNext)
            {
                uint result;
                try { result = CloseTrace(traceHandle); } catch { result = uint.MaxValue; }
                if (result == 0 || result == ClosePending || result == InvalidHandle && (consumerReturned || !workerStarted))
                    consumerCloseAccepted = true;
                else
                {
                    closeFailures++; closeNext = now + (1000L << (closeFailures - 1));
                    incomplete = decodeRevoked = forceConsumerClose = true; cleanupError = result;
                    Logger.Warn(Logger.Format("中断采集消费者关闭失败：返回码={0} 尝试={1}/{2}；保留回调和原句柄", "Interrupt capture consumer close failed: rc={0} attempt={1}/{2}; callback and original handle retained", result, closeFailures, CleanupAttempts));
                }
            }
            bool drained = !workerStarted || consumerReturned;
            bool sessionClosed = !sessionPending || sessionStopAccepted;
            bool consumerClosed = traceHandle == 0 || traceHandle == ulong.MaxValue || consumerCloseAccepted;
            if (drained && sessionClosed && consumerClosed)
            {
                // 句柄关闭受理与 ProcessTrace 返回均已确认 才可归还回调资源及全局占用
                traceHandle = sessionHandle = 0; sessionPending = false; started = false;
                worker = null; keepAlive = null;
                if (loggerName != IntPtr.Zero) { Marshal.FreeHGlobal(loggerName); loggerName = IntPtr.Zero; }
                if (sessionProperties != IntPtr.Zero) { Marshal.FreeHGlobal(sessionProperties); sessionProperties = IntPtr.Zero; }
                if (now >= ownershipNext && ownershipFailures < CleanupAttempts && !ReleaseOwnership())
                {
                    ownershipFailures++; ownershipNext = now + (1000L << (ownershipFailures - 1)); incomplete = true;
                    Logger.Warn(Logger.Format("中断采集占用句柄关闭未确认：尝试={0}/{1}", "Interrupt capture ownership handle close unconfirmed: attempt={0}/{1}", ownershipFailures, CleanupAttempts));
                }
            }
            long due = long.MaxValue;
            if (sessionPending && !sessionStopAccepted && stopFailures < CleanupAttempts) due = stopNext;
            if (!consumerClosed && (forceConsumerClose || drained) && closeFailures < CleanupAttempts) due = Math.Min(due, closeNext);
            if (drained && sessionClosed && consumerClosed && aliveOwned != null && ownershipFailures < CleanupAttempts) due = Math.Min(due, ownershipNext);
            if (due == long.MaxValue)
            {
                if (cleanupTimer != null) cleanupTimer.Dispose(); cleanupTimer = null;
            }
            else
            {
                if (cleanupTimer == null) cleanupTimer = new Timer(delegate { lock (gate) CleanupLocked(); }, null, -1, -1);
                cleanupTimer.Change((int)Math.Min(int.MaxValue, Math.Max(1L, due - now)), -1);
            }
        }

        public void Dispose() { Stop(); }

        // ETW 调用经这几层包装进入 Native 生命周期台架只替换包装体
        private static uint StartTrace(out ulong handle, string name, IntPtr props) { return (uint)Native.StartTrace(out handle, name, props); }

        private static uint ControlTrace(ulong handle, string name, IntPtr props, uint code) { return (uint)Native.ControlTrace(handle, name, props, code); }

        private static ulong OpenTrace(ref Native.EventTraceLogfile logfile) { return Native.OpenTrace(ref logfile); }

        private static uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end) { return (uint)Native.ProcessTrace(handles, count, start, end); }

        private static uint CloseTrace(ulong handle) { return (uint)Native.CloseTrace(handle); }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);

        private bool LoadModules(out string error)
        {
            modules.Clear();
            List<Module> loaded;
            if (!TryReadLoadedModules(out loaded, out error))
            {
                return false;
            }
            modules.AddRange(loaded);
            return true;
        }

        private static bool TryReadLoadedModules(out List<Module> loaded, out string error)
        {
            loaded = new List<Module>();
            error = null;
            IntPtr intPtr = IntPtr.Zero;
            try
            {
                int ret = 0;
                NtQuerySystemInformation(11, IntPtr.Zero, 0, out ret);
                ret = checked(Math.Max(ret, 1048576) + 65536);
                intPtr = Marshal.AllocHGlobal(ret);
                int ret2;
                int ntQuerySystemInformationResult = NtQuerySystemInformation(11, intPtr, ret, out ret2);
                if (ntQuerySystemInformationResult != 0)
                {
                    uint numericValue = (uint)ntQuerySystemInformationResult;
                    error = Logger.Text("驱动模块枚举失败，无法启动中断归因 NTSTATUS=0x", "Driver module enumeration failed; interrupt attribution cannot start NTSTATUS=0x") + numericValue.ToString("X8");
                    return false;
                }
                int int32 = Marshal.ReadInt32(intPtr);
                long toInt64Offset = intPtr.ToInt64() + IntPtr.Size;
                int readLoadedModulesValue = 296;
                if (int32 < 0 || int32 > (ret - IntPtr.Size) / readLoadedModulesValue)
                {
                    error = Logger.Text("驱动模块枚举返回的长度无效，无法启动中断归因", "Driver module enumeration returned an invalid length; interrupt attribution cannot start");
                    return false;
                }
                for (int i = 0; i < int32; i++)
                {
                    long moduleAddress = toInt64Offset + (long)i * (long)readLoadedModulesValue;
                    ulong int64 = (ulong)Marshal.ReadInt64(new IntPtr(moduleAddress + 16));
                    uint int322 = (uint)Marshal.ReadInt32(new IntPtr(moduleAddress + 24));
                    if (int64 != 0 && int322 != 0)
                    {
                        string ptrToStringAnsiResult = Marshal.PtrToStringAnsi(new IntPtr(moduleAddress + 40));
                        if (!string.IsNullOrEmpty(ptrToStringAnsiResult))
                        {
                            int matchIndex = ptrToStringAnsiResult.LastIndexOf('\\');
                            string name = ((matchIndex >= 0) ? ptrToStringAnsiResult.Substring(matchIndex + 1) : ptrToStringAnsiResult);
                            loaded.Add(new Module
                            {
                                Base = int64,
                                End = int64 + int322,
                                Name = name
                            });
                        }
                    }
                }
                if (loaded.Count == 0)
                {
                    error = Logger.Text("驱动模块枚举未返回可用地址，无法启动中断归因", "Driver module enumeration returned no usable address; interrupt attribution cannot start");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                loaded.Clear();
                error = Logger.Text("驱动模块枚举异常，无法启动中断归因 ", "Driver module enumeration threw an exception; interrupt attribution cannot start ") + ex.GetType().Name;
                return false;
            }
            finally
            {
                if (intPtr != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(intPtr);
                }
            }
        }

        private static IntPtr AllocProps(uint enableFlags)
        {
            int scaledValue = ("PaviseInterruptProbe".Length + 1) * 2;
            int allocPropsValue = Marshal.SizeOf(typeof(Native.EventTraceProperties)) + scaledValue + 16;
            IntPtr intPtr = Marshal.AllocHGlobal(allocPropsValue);
            for (int i = 0; i < allocPropsValue; i++)
            {
                Marshal.WriteByte(intPtr, i, 0);
            }
            Native.EventTraceProperties structure = default(Native.EventTraceProperties);
            structure.WnodeBufferSize = (uint)allocPropsValue;
            structure.WnodeFlags = 131072u;
            structure.WnodeGuid = SessionGuid;
            structure.WnodeClientContext = 1u;
            structure.BufferSize = 128u;
            structure.MinimumBuffers = 64u;
            structure.MaximumBuffers = 256u;
            structure.LogFileMode = 167772416u;
            structure.FlushTimer = 1u;
            structure.EnableFlags = enableFlags;
            structure.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(Native.EventTraceProperties));
            Marshal.StructureToPtr(structure, intPtr, false);
            return intPtr;
        }

        public static void CleanupStaleSession() { StopStale(); }
        public static void HealFromCrash() { StopStale(); }

        private static void StopStale()
        {
            InterruptAttribution original;
            lock (ownershipGate) original = retainedOwner;
            // 活跃诊断不属于陈旧会话 已有清理继续使用原对象和预算
            if (original != null)
            {
                if (original.stopRequested) { lock (original.gate) original.CleanupLocked(); }
                return;
            }
            var recovery = new InterruptAttribution();
            lock (recovery.gate)
            {
                if (!recovery.TakeOwnership()) return;
                recovery.startAttempted = true;
                recovery.stopRequested = recovery.decodeRevoked = recovery.forceConsumerClose = recovery.incomplete = true;
                try { recovery.sessionProperties = AllocProps(0); recovery.sessionPending = true; recovery.CleanupLocked(); }
                catch { recovery.BeginCleanupLocked(); }
            }
        }
    }
}