// 作者：bdth 2074055628@qq.com
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

        public string FailDetail { get; private set; }

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

        private sealed class Module { public ulong Base; public ulong End; public string Name; }

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

                uint enableFlags = FlagDpc | FlagInterrupt;
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
                        // 错误码 5 需区分未提权与已提权后仍被拒绝。
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
                usPerTick = 1000000.0 / freq;
                sanityMaxTicks = freq;

                worker = new Thread(RunProcessTrace);
                worker.IsBackground = true;
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
