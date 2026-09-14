// @author bdth 2074055628@qq.com
// File purpose Kernel interrupt and DPC attribution sampling startup and timeline
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
        public readonly List<IrqDriverCoreRecord> Cores = new List<IrqDriverCoreRecord>();
    }

    internal sealed class InterruptAttributionResult
    {
        public bool Ok;
        public string Error;
        public readonly List<DriverInterrupt> Drivers = new List<DriverInterrupt>();

        public uint EventsLost;
        public uint BuffersLost;
        public bool Incomplete;
        public long Unmapped;
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

        // Start() has four distinct failure paths; the caller only gets one false and would report them as the same cause
        //   Historically all reported as needs administrator rights, but elevation was already checked before entering this function, so that only misleads
        //   Busy gets its own path; the other three put the real cause plus error code here and the caller presents it verbatim
        public string FailDetail { get; private set; }

        // The line shown to the user after Start() fails; probe-in-use and real failure must be told apart
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
            public Dictionary<int, RoutineStat> Cores;
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

        // Per-DPC event timeline, available in release, controlled by a runtime switch, off by default, zero cost when off
        //   Raw material for present long-frame and DPC causal alignment; fully parallel to the aggregation path, aggregation logic unchanged
        //   Writes happen on the consumer thread in OnEvent, only appending an ultra-light struct, no module resolution
        //   Reads only after Stop and worker.Join; single producer single consumer, no lock needed
        //   Was the falsification-bench exit behind #if PAVISE_SELFTEST, now promoted to a regular release capability
        internal struct DpcTimelineEntry
        {
            public long StartQpc; // DPC start time, used for real overlap checks against long-frame intervals
            public long EndQpc;   // DPC end time, same QPC ruler as QueryPerformanceCounter
            public string Module; // Resolved in one pass after Stop, hot path never touches it
            public ushort Cpu;
            public double DpcUs;  // Duration of this DPC in microseconds
        }

        // Hot path only records this lighter raw mark; module address resolution waits until after Stop, avoiding a linear module-table scan per DPC
        private struct DpcMark
        {
            public long StartQpc;
            public long EndQpc;
            public ulong Routine;
            public ushort Cpu;
            public double DpcUs;
        }

        // Hard cap as damage control, following PresentProbe.FrameCap: at the cap set Truncated and stop recording, so a long match cannot eat all memory
        //   DpcMark is about 32 bytes, 2 million entries roughly 64MB held for the session, released at the end
        private const int DpcTimelineCap = 2000000;
        private volatile bool captureTimeline;      // Runtime switch, off by default
        private bool timelineTruncated;
        private List<DpcMark> dpcMarks;
        private List<DpcTimelineEntry> dpcTimeline;

        // Call before Start, before capture, to turn on per-event DPC timeline recording
        internal void EnableDpcTimeline()
        {
            captureTimeline = true;
            timelineTruncated = false;
            dpcMarks = new List<DpcMark>(1 << 18);
        }
        // After Stop(), fetch this match's per-event DPC timeline with module names resolved; null if nothing was captured
        internal List<DpcTimelineEntry> DpcTimeline { get { return dpcTimeline; } }
        // Whether the timeline was truncated by hitting the cap
        internal bool DpcTimelineTruncated { get { return timelineTruncated; } }
        // QPC frequency used by the session, same ruler as the present session
        internal long QpcFrequencyValue { get { return qpcFrequency; } }

        // Call after Stop and worker.Join; resolves raw marks into a timeline by module address
        //   Small per-resolution cache; distinct routine addresses are few, amortized O(entries)
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

        // Subscribe to DPC only, not ISR; event volume roughly halves, and so does the load on the kernel side and consumer thread
        //   Match observation ledger and verdict use DPC evidence only; ISR counts are only useful to one-shot health-check scans
        //   Must be called before Start
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
                        // Code 5 has two sources that must be told apart; the user cannot act on a bare code
                        //   Not elevated is the most common: the switch state lives in the registry, turned on by a previous elevated session
                        //   This non-elevated launch still shows it as enabled, and sampling is denied as soon as it starts
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
                // Device interrupts during a match can burst briefly; if the consumer callback is scheduled late, buffer recycling slows down and events are lost
                //   The drain thread only reads ETW buffers and blocks waiting most of the time; raising its priority reduces loss caused by the sampling itself
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
