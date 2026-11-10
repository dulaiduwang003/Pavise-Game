using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class InterruptAttribution
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

#pragma warning disable 0649
        private struct WnodeHeader
        {
            public uint BufferSize;

            public uint ProviderId;

            public ulong HistoricalContext;

            public long TimeStamp;

            public Guid Guid;

            public uint ClientContext;

            public uint Flags;
        }

        private struct EventTraceProperties
        {
            public WnodeHeader Wnode;

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

        private struct EventTraceHeader
        {
            public ushort Size;

            public ushort FieldTypeFlags;

            public byte Type;

            public byte Level;

            public ushort Version;

            public uint ThreadId;

            public uint ProcessId;

            public long TimeStamp;

            public Guid Guid;

            public uint KernelTime;

            public uint UserTime;
        }

#pragma warning disable 0649
        private struct EventDescriptor
        {
            public ushort Id;

            public byte Version;

            public byte Channel;

            public byte Level;

            public byte Opcode;

            public ushort Task;

            public ulong Keyword;
        }

        private struct EventHeader
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

        private struct EtwBufferContext
        {
            public ushort ProcessorIndex;

            public ushort LoggerId;
        }

        private struct EventRecord
        {
            public EventHeader EventHeader;

            public EtwBufferContext BufferContext;

            public ushort ExtendedDataCount;

            public ushort UserDataLength;

            public IntPtr ExtendedData;

            public IntPtr UserData;

            public IntPtr UserContext;
        }
#pragma warning restore 0649

        private delegate void EventRecordCallback(ref EventRecord record);

        private struct EventTrace
        {
            public EventTraceHeader Header;

            public uint InstanceId;

            public uint ParentInstanceId;

            public Guid ParentGuid;

            public IntPtr MofData;

            public uint MofLength;

            public uint ClientContext;
        }

        private struct SystemTime
        {
            public ushort Year;

            public ushort Month;

            public ushort DayOfWeek;

            public ushort Day;

            public ushort Hour;

            public ushort Minute;

            public ushort Second;

            public ushort Milliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct TimeZoneInformation
        {
            public int Bias;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string StandardName;

            public SystemTime StandardDate;

            public int StandardBias;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DaylightName;

            public SystemTime DaylightDate;

            public int DaylightBias;
        }

        private struct TraceLogfileHeader
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

            public TimeZoneInformation TimeZone;

            public long BootTime;

            public long PerfFreq;

            public long StartTime;

            public uint ReservedFlags;

            public uint BuffersLost;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct EventTraceLogfile
        {
            public IntPtr LogFileName;

            public IntPtr LoggerName;

            public long CurrentTime;

            public uint BuffersRead;

            public uint ProcessTraceMode;

            public EventTrace CurrentEvent;

            public TraceLogfileHeader LogfileHeader;

            public IntPtr BufferCallback;

            public uint BufferSize;

            public uint Filled;

            public uint EventsLost;

            public IntPtr EventRecordCallbackPtr;

            public uint IsKernelTrace;

            public IntPtr Context;
        }

        internal const int BucketCount = 13;

        private static readonly Guid SessionGuid = new Guid("7d1f8c2a-64b3-4c5e-9a1d-2e8f0b6c4d3a");

        private static readonly Guid PerfInfoGuid = new Guid("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");

        private EventWaitHandle aliveOwned;

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

        private EventRecordCallback keepAlive;

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
            try
            {
                EventWaitHandle result;
                if (EventWaitHandle.TryOpenExisting("Global\\Pavise_InterruptProbeAlive", out result))
                {
                    using (result)
                    {
                    }
                    return false;
                }
            }
            catch
            {
            }
            try
            {
                bool createdNew;
                EventWaitHandle eventWaitHandle = new EventWaitHandle(false, EventResetMode.ManualReset, "Global\\Pavise_InterruptProbeAlive", out createdNew);
                if (!createdNew)
                {
                    eventWaitHandle.Close();
                    return false;
                }
                aliveOwned = eventWaitHandle;
                return true;
            }
            catch
            {
                return true;
            }
        }

        private void ReleaseOwnership()
        {
            EventWaitHandle eventWaitHandle = aliveOwned;
            aliveOwned = null;
            if (eventWaitHandle != null)
            {
                try
                {
                    eventWaitHandle.Close();
                }
                catch
                {
                }
            }
        }

        public bool Start()
        {
            lock (gate)
            {
                if (started)
                {
                    return true;
                }
                Busy = false;
                FailDetail = null;
                if (!TakeOwnership())
                {
                    Busy = true;
                    Logger.Log(Lang.T("log.interruptattribution.9", 0));
                    return false;
                }
                Native.TryEnableDebugPrivilege();
                string error;
                if (!LoadModules(out error))
                {
                    FailDetail = error;
                    Logger.Warn("中断 " + error);
                    ReleaseOwnership();
                    return false;
                }
                uint enableFlags = 96u;
                IntPtr allocPropsHandle = AllocProps(enableFlags);
                try
                {
                    ulong handle;
                    uint startTraceResult = StartTrace(out handle, "PaviseInterruptProbe", allocPropsHandle);
                    if (startTraceResult == 183)
                    {
                        StopStale();
                        Marshal.FreeHGlobal(allocPropsHandle);
                        allocPropsHandle = AllocProps(enableFlags);
                        startTraceResult = StartTrace(out handle, "PaviseInterruptProbe", allocPropsHandle);
                    }
                    switch (startTraceResult)
                    {
                    case 87u:
                        Logger.Log(Lang.T("log.interruptattribution.1", 0));
                        FailDetail = Lang.T("irq.fail.nokernel");
                        ReleaseOwnership();
                        return false;
                    default:
                    {
                        Logger.Warn(Lang.T("log.interruptattribution.2", 0) + startTraceResult);
                        bool elevated = false;
                        try
                        {
                            elevated = Native.IsElevated();
                        }
                        catch
                        {
                        }
                        FailDetail = ((startTraceResult != 5) ? (Lang.T("irq.fail.start") + startTraceResult) : (elevated ? Lang.T("irq.fail.denied") : Lang.T("irq.state.needadmin")));
                        ReleaseOwnership();
                        return false;
                    }
                    case 0u:
                        break;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(allocPropsHandle);
                }
                keepAlive = OnEvent;
                EventTraceLogfile logfile = default(EventTraceLogfile);
                logfile.LoggerName = Marshal.StringToHGlobalUni("PaviseInterruptProbe");
                logfile.ProcessTraceMode = 268439808u;
                logfile.EventRecordCallbackPtr = Marshal.GetFunctionPointerForDelegate(keepAlive);
                traceHandle = OpenTrace(ref logfile);
                if (traceHandle == ulong.MaxValue || traceHandle == 0)
                {
                    int lastWin32Error = Marshal.GetLastWin32Error();
                    Logger.Warn(Lang.T("log.interruptattribution.3", 0) + lastWin32Error);
                    FailDetail = Lang.T("irq.fail.open") + lastWin32Error;
                    StopStale();
                    ReleaseOwnership();
                    return false;
                }
                long perfFreq = logfile.LogfileHeader.PerfFreq;
                if (perfFreq <= 0)
                {
                    perfFreq = Stopwatch.Frequency;
                }
                usPerTick = 1000000.0 / (double)perfFreq;
                sanityMaxTicks = perfFreq;
                worker = new Thread(RunProcessTrace);
                worker.IsBackground = true;
                try
                {
                    worker.Priority = ThreadPriority.Highest;
                }
                catch
                {
                }
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
                if (!stopRequested)
                {
                    consumerExitedEarly = true;
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
            InterruptAttributionResult interruptAttributionResult = new InterruptAttributionResult();
            lock (gate)
            {
                if (!started)
                {
                    interruptAttributionResult.Error = Lang.T("t.interruptattribution.4");
                    return interruptAttributionResult;
                }
                stopRequested = true;
                uint eventsLost;
                uint buffersLost;
                uint error;
                bool stopStaleSucceeded = StopStale(out eventsLost, out buffersLost, out error);
                interruptAttributionResult.EventsLost = eventsLost;
                interruptAttributionResult.BuffersLost = buffersLost;
                if (!stopStaleSucceeded)
                {
                    try
                    {
                        if (traceHandle != 0)
                        {
                            CloseTrace(traceHandle);
                        }
                    }
                    catch
                    {
                    }
                }
                bool workerStopped = true;
                if (worker != null)
                {
                    try
                    {
                        workerStopped = worker.Join(10000);
                    }
                    catch
                    {
                        workerStopped = false;
                    }
                }
                if (stopStaleSucceeded)
                {
                    try
                    {
                        if (traceHandle != 0)
                        {
                            CloseTrace(traceHandle);
                        }
                    }
                    catch
                    {
                    }
                }
                interruptAttributionResult.Incomplete = !CaptureComplete(stopStaleSucceeded, workerStopped, processTraceSucceeded, consumerExitedEarly);
                started = false;
                ReleaseOwnership();
                if (!workerStopped)
                {
                    interruptAttributionResult.Error = Lang.T("t.interruptattribution.5");
                    return interruptAttributionResult;
                }
                keepAlive = null;
                Dictionary<string, DriverInterrupt> driverInterruptsByName = new Dictionary<string, DriverInterrupt>();
                Dictionary<string, long[]> valuesByName = new Dictionary<string, long[]>();
                Fold(driverInterruptsByName, valuesByName, dpcHits, true);
                Fold(driverInterruptsByName, valuesByName, isrHits, false);
                foreach (KeyValuePair<string, DriverInterrupt> driverInterruptEntry in driverInterruptsByName)
                {
                    DriverInterrupt driverInterrupt = driverInterruptEntry.Value;
                    long[] dpcBuckets;
                    if (valuesByName.TryGetValue(driverInterruptEntry.Key, out dpcBuckets))
                    {
                        driverInterrupt.DpcOver500Us = SumFrom(dpcBuckets, 9);
                        driverInterrupt.DpcOver1Ms = SumFrom(dpcBuckets, 10);
                        driverInterrupt.DpcBuckets = (long[])dpcBuckets.Clone();
                    }
                    interruptAttributionResult.Drivers.Add(driverInterrupt);
                }
                interruptAttributionResult.Drivers.Sort(delegate(DriverInterrupt a, DriverInterrupt b)
                {
                    long interruptTicks = a.Dpc + a.Isr;
                    return (b.Dpc + b.Isr).CompareTo(interruptTicks);
                });
                long mappedDpcTicks = 0L;
                foreach (DriverInterrupt driver in interruptAttributionResult.Drivers)
                {
                    mappedDpcTicks += driver.Dpc;
                }
                interruptAttributionResult.Unmapped = Math.Max(0L, dpcTotal - mappedDpcTicks);
                bool attributionMissing = (dpcTotal > 0 || isrTotal > 0) && interruptAttributionResult.Drivers.Count == 0;
                string errorValue = (attributionMissing ? ("已采集中断事件，但无法映射到驱动模块，本局中断归因不可用 DPC=" + dpcTotal + " ISR=" + isrTotal + " 模块=" + modules.Count) : null);
                if (attributionMissing)
                {
                    Logger.Warn("中断 " + errorValue);
                }
                interruptAttributionResult.Ok = HasMappedEvents(dpcTotal, isrTotal, interruptAttributionResult.Drivers.Count) && !interruptAttributionResult.Incomplete && !interruptAttributionResult.Lossy;
                if (interruptAttributionResult.Lossy)
                {
                    Logger.Log(string.Format(Lang.T("log.interruptattribution.lossy", 0), interruptAttributionResult.EventsLost, interruptAttributionResult.BuffersLost));
                }
                if (interruptAttributionResult.Incomplete)
                {
                    interruptAttributionResult.Error = "ETW 消费或停止未完整 win32=" + error;
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
                DriverInterrupt currentByMod;
                if (!byMod.TryGetValue(driverValue, out currentByMod))
                {
                    DriverInterrupt driverInterrupt = new DriverInterrupt();
                    driverInterrupt.Driver = driverValue;
                    currentByMod = (byMod[driverValue] = driverInterrupt);
                }
                RoutineStat routineStat = hit.Value;
                if (dpc)
                {
                    currentByMod.CpuMask |= routineStat.CpuMask;
                    currentByMod.CpuMaskTruncated |= routineStat.CpuMaskTruncated;
                }
                currentByMod.BadDuration += routineStat.BadDuration;
                double dpcTotalUsValue = (double)routineStat.TotalTicks * usPerTick;
                double dpcMaxUsValue = (double)routineStat.MaxTicks * usPerTick;
                if (dpc)
                {
                    currentByMod.Dpc += routineStat.Count;
                    currentByMod.DpcTotalUs += dpcTotalUsValue;
                    if (dpcMaxUsValue > currentByMod.DpcMaxUs)
                    {
                        currentByMod.DpcMaxUs = dpcMaxUsValue;
                    }
                    long[] dpcBucket;
                    if (!dpcBuckets.TryGetValue(driverValue, out dpcBucket))
                    {
                        dpcBucket = (dpcBuckets[driverValue] = new long[13]);
                    }
                    for (int i = 0; i < 13; i++)
                    {
                        dpcBucket[i] += routineStat.Buckets[i];
                    }
                }
                else
                {
                    currentByMod.Isr += routineStat.Count;
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

        private void OnEvent(ref EventRecord record)
        {
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
            int size = (((flags & 0x40u) != 0) ? 8 : (((flags & 0x20u) != 0) ? 4 : IntPtr.Size));
            if (record.UserDataLength < 8 + size)
            {
                return;
            }
            long toInt64Result = record.UserData.ToInt64();
            long startTicks = Marshal.ReadInt64(new IntPtr(toInt64Result));
            ulong key = (ulong)((size == 8) ? Marshal.ReadInt64(new IntPtr(toInt64Result + 8)) : ((uint)Marshal.ReadInt32(new IntPtr(toInt64Result + 8))));
            long timeStamp = record.EventHeader.TimeStamp;
            long totalTicksValue = timeStamp - startTicks;
            bool validDuration = totalTicksValue >= 0 && totalTicksValue <= sanityMaxTicks;
            Dictionary<ulong, RoutineStat> routineStatsByKey = (isIsr ? isrHits : dpcHits);
            RoutineStat routineStat;
            if (!routineStatsByKey.TryGetValue(key, out routineStat))
            {
                routineStat = (routineStatsByKey[key] = new RoutineStat());
            }
            routineStat.Count++;
            ushort processorIndex = record.BufferContext.ProcessorIndex;
            if (processorIndex < 64)
            {
                routineStat.CpuMask |= (ulong)(1L << (int)processorIndex);
            }
            else
            {
                routineStat.CpuMaskTruncated = true;
            }
            if (!validDuration)
            {
                routineStat.BadDuration++;
            }
            else
            {
                routineStat.TotalTicks += totalTicksValue;
                if (totalTicksValue > routineStat.MaxTicks)
                {
                    routineStat.MaxTicks = totalTicksValue;
                }
                routineStat.Buckets[BucketOf(totalTicksValue)]++;
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

        private static bool StopStale(out uint eventsLost, out uint buffersLost, out uint error)
        {
            eventsLost = 0u;
            buffersLost = 0u;
            error = 0u;
            int scaledValue = ("PaviseInterruptProbe".Length + 1) * 2;
            int propertiesSize = Marshal.SizeOf(typeof(EventTraceProperties)) + scaledValue + 16;
            IntPtr nativeBuffer = Marshal.AllocHGlobal(propertiesSize);
            try
            {
                for (int i = 0; i < propertiesSize; i++)
                {
                    Marshal.WriteByte(nativeBuffer, i, 0);
                }
                EventTraceProperties structure = default(EventTraceProperties);
                structure.Wnode.BufferSize = (uint)propertiesSize;
                structure.Wnode.Guid = SessionGuid;
                structure.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
                Marshal.StructureToPtr(structure, nativeBuffer, false);
                uint controlTraceResult = ControlTrace(0uL, "PaviseInterruptProbe", nativeBuffer, 1u);
                if (controlTraceResult != 0)
                {
                    error = controlTraceResult;
                    return false;
                }
                EventTraceProperties eventTraceProperties = (EventTraceProperties)Marshal.PtrToStructure(nativeBuffer, typeof(EventTraceProperties));
                eventsLost = eventTraceProperties.EventsLost;
                buffersLost = eventTraceProperties.RealTimeBuffersLost + eventTraceProperties.LogBuffersLost;
                return true;
            }
            catch
            {
                error = uint.MaxValue;
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(nativeBuffer);
            }
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
            IntPtr nativeBuffer = IntPtr.Zero;
            try
            {
                int ret = 0;
                NtQuerySystemInformation(11, IntPtr.Zero, 0, out ret);
                ret = checked(Math.Max(ret, 1048576) + 65536);
                nativeBuffer = Marshal.AllocHGlobal(ret);
                int ret2;
                int ntQuerySystemInformationResult = NtQuerySystemInformation(11, nativeBuffer, ret, out ret2);
                if (ntQuerySystemInformationResult != 0)
                {
                    uint currentNtQuerySystemInformationResult = (uint)ntQuerySystemInformationResult;
                    error = "驱动模块枚举失败，无法启动中断归因 NTSTATUS=0x" + currentNtQuerySystemInformationResult.ToString("X8");
                    return false;
                }
                int moduleCount = Marshal.ReadInt32(nativeBuffer);
                long toInt64Offset = nativeBuffer.ToInt64() + IntPtr.Size;
                int moduleRecordSize = 296;
                if (moduleCount < 0 || moduleCount > (ret - IntPtr.Size) / moduleRecordSize)
                {
                    error = "驱动模块枚举返回的长度无效，无法启动中断归因";
                    return false;
                }
                for (int i = 0; i < moduleCount; i++)
                {
                    long moduleAddress = toInt64Offset + (long)i * (long)moduleRecordSize;
                    ulong moduleBase = (ulong)Marshal.ReadInt64(new IntPtr(moduleAddress + 16));
                    uint moduleSize = (uint)Marshal.ReadInt32(new IntPtr(moduleAddress + 24));
                    if (moduleBase != 0 && moduleSize != 0)
                    {
                        string ptrToStringAnsiResult = Marshal.PtrToStringAnsi(new IntPtr(moduleAddress + 40));
                        if (!string.IsNullOrEmpty(ptrToStringAnsiResult))
                        {
                            int matchIndex = ptrToStringAnsiResult.LastIndexOf('\\');
                            string name = ((matchIndex >= 0) ? ptrToStringAnsiResult.Substring(matchIndex + 1) : ptrToStringAnsiResult);
                            loaded.Add(new Module
                            {
                                Base = moduleBase,
                                End = moduleBase + moduleSize,
                                Name = name
                            });
                        }
                    }
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
            finally
            {
                if (nativeBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(nativeBuffer);
                }
            }
        }

        private static IntPtr AllocProps(uint enableFlags)
        {
            int scaledValue = ("PaviseInterruptProbe".Length + 1) * 2;
            int propertiesSize = Marshal.SizeOf(typeof(EventTraceProperties)) + scaledValue + 16;
            IntPtr nativeBuffer = Marshal.AllocHGlobal(propertiesSize);
            for (int i = 0; i < propertiesSize; i++)
            {
                Marshal.WriteByte(nativeBuffer, i, 0);
            }
            EventTraceProperties structure = default(EventTraceProperties);
            structure.Wnode.BufferSize = (uint)propertiesSize;
            structure.Wnode.Flags = 131072u;
            structure.Wnode.Guid = SessionGuid;
            structure.Wnode.ClientContext = 1u;
            structure.BufferSize = 128u;
            structure.MinimumBuffers = 64u;
            structure.MaximumBuffers = 256u;
            structure.LogFileMode = 167772416u;
            structure.FlushTimer = 1u;
            structure.EnableFlags = enableFlags;
            structure.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
            Marshal.StructureToPtr(structure, nativeBuffer, false);
            return nativeBuffer;
        }

        public static void CleanupStaleSession()
        {
            try
            {
                StopStale();
            }
            catch
            {
            }
        }

        public static void HealFromCrash()
        {
            StopStale();
        }

        private static void StopStale()
        {
            uint eventsLost;
            uint buffersLost;
            uint error;
            StopStale(out eventsLost, out buffersLost, out error);
        }
    }
}
