// @author bdth 2074055628@qq.com
// 文件用途 进程外帧时钟 用 DxgKrnl ETW 抓目标进程的 Present 得到每帧边界
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class FrameWindow
    {
        public int Frames;
        public double BudgetMs;
        public double MedianMs;
        public double P95Ms;
        public double P99Ms;
        public double OnePercentLowMs;
        public int OverBudget;
        public long FirstQpc;
        public long LastQpc;
        public long StampsDropped;

        public bool Enough { get { return Frames >= 30; } }

        public double OverBudgetShare { get { return Frames > 0 ? (double)OverBudget / Frames : 0; } }
    }

    internal sealed class FrameClock
    {
        private const string SessionName = "PaviseFrameClock";
        private static readonly Guid SessionGuid = new Guid("4f1a9c07-8d3b-4e62-b5a1-6c7d2e9f0a48");
        private static readonly Guid DxgKrnl = new Guid("802ec45a-1e99-4b83-9920-87c98277ba9d");

        private const ushort EventIdPresent = 184;
        private const uint ProcessTraceModeRawTimestamp = 0x00001000;
        private const ulong KeywordAll = ulong.MaxValue;
        private const uint FilterTypeEventId = 0x80000200;

        internal enum EnableMode
        {
            AllKeywordsAndEventId = 0,
            AllKeywords = 1
        }
        internal EnableMode ModeUsed { get; private set; }
        internal static EnableMode ForceMode = (EnableMode)(-1);
        private const int ErrorAlreadyExists = 183;

        private const int RingSize = 16384;

        private readonly object gate = new object();
        private readonly long[] stamps = new long[RingSize];
        private readonly uint[] stampTids = new uint[RingSize];
        private long written;
        private int targetPid;
        private ulong sessionHandle;
        private ulong traceHandle;
        private Thread worker;
        private Native.EventRecordCallback keepAlive;
        private IntPtr pidFilter;
        private volatile bool started;
        private double budgetMs;
        private long qpcFrequency = System.Diagnostics.Stopwatch.Frequency;
        private long totalEvents;
        private long targetEvents;
        private readonly long[] idTally = new long[1024];
        private const int ThreadSlots = 16;
        private readonly uint[] presentTids = new uint[ThreadSlots];
        private readonly long[] presentTidHits = new long[ThreadSlots];

        public bool Started { get { return started; } }
        public long TotalFrames { get { lock (gate) return written; } }
        public long QpcFrequency { get { return qpcFrequency; } }
        public long TotalEvents { get { return Interlocked.Read(ref totalEvents); } }
        public long TargetEvents { get { return Interlocked.Read(ref targetEvents); } }

        public void TopEventIds(int[] ids, long[] counts)
        {
            for (int k = 0; k < ids.Length; k++) { ids[k] = -1; counts[k] = 0; }
            for (int i = 0; i < idTally.Length; i++)
            {
                long c = Volatile.Read(ref idTally[i]);
                if (c <= 0) continue;
                for (int k = 0; k < ids.Length; k++)
                    if (c > counts[k])
                    {
                        for (int j = ids.Length - 1; j > k; j--) { ids[j] = ids[j - 1]; counts[j] = counts[j - 1]; }
                        ids[k] = i; counts[k] = c;
                        break;
                    }
            }
        }

        public bool Start(int pid, double refreshHz)
        {
            lock (gate)
            {
                if (started) return true;
                targetPid = pid;
                budgetMs = refreshHz > 1 ? 1000.0 / refreshHz : 0;
                written = 0;
                for (int i = 0; i < ThreadSlots; i++) { presentTids[i] = 0; presentTidHits[i] = 0; }

                IntPtr props = AllocProps();
                try
                {
                    int rc = Native.StartTrace(out sessionHandle, SessionName, props);
                    if (rc == ErrorAlreadyExists)
                    {
                        StopStale();
                        Marshal.FreeHGlobal(props);
                        props = AllocProps();
                        rc = Native.StartTrace(out sessionHandle, SessionName, props);
                    }
                    if (rc != 0) { Logger.Log(Lang.T("log.frameclock.1") + rc); return false; }
                }
                finally { Marshal.FreeHGlobal(props); }

                if (!EnableProviderForPid(pid))
                {
                    StopStale();
                    return false;
                }

                keepAlive = OnEvent;
                var logfile = new Native.EventTraceLogfile();
                logfile.LoggerName = Marshal.StringToHGlobalUni(SessionName);
                logfile.ProcessTraceMode = Native.ProcessTraceModeRealTime
                    | Native.ProcessTraceModeEventRecord | ProcessTraceModeRawTimestamp;
                logfile.EventRecordCallback = Marshal.GetFunctionPointerForDelegate(keepAlive);
                traceHandle = Native.OpenTrace(ref logfile);
                if (traceHandle == Native.InvalidProcessTraceHandle || traceHandle == 0)
                {
                    Logger.Log(Lang.T("log.frameclock.2") + Marshal.GetLastWin32Error());
                    FreeFilter();
                    StopStale();
                    return false;
                }

                qpcFrequency = logfile.LogfileHeader.PerfFreq;
                if (qpcFrequency <= 0) qpcFrequency = System.Diagnostics.Stopwatch.Frequency;

                worker = new Thread(RunProcessTrace);
                worker.IsBackground = true;
                worker.Priority = ThreadPriority.AboveNormal;
                worker.Start();
                started = true;
                return true;
            }
        }

        private bool EnableProviderForPid(int pid)
        {
            if ((int)ForceMode >= 0) return EnableWith(ForceMode);
            if (EnableWith(EnableMode.AllKeywordsAndEventId)) return true;
            return EnableWith(EnableMode.AllKeywords);
        }

        private bool EnableWith(EnableMode mode)
        {
            Guid provider = DxgKrnl;
            IntPtr desc = IntPtr.Zero;
            var parms = new Native.EnableTraceParameters();
            parms.Version = 2;
            try
            {
                if (mode == EnableMode.AllKeywordsAndEventId)
                {
                    int idBytes = 1 + 1 + 2 + 2;
                    FreeFilter();
                    pidFilter = Marshal.AllocHGlobal(idBytes);
                    Marshal.WriteByte(pidFilter, 0, 1);
                    Marshal.WriteByte(pidFilter, 1, 0);
                    Marshal.WriteInt16(pidFilter, 2, 1);
                    Marshal.WriteInt16(pidFilter, 4, unchecked((short)EventIdPresent));
                    desc = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(EventFilterDescriptor)));
                    var fd = new EventFilterDescriptor
                    {
                        Ptr = (ulong)pidFilter.ToInt64(),
                        Size = (uint)idBytes,
                        Type = FilterTypeEventId
                    };
                    Marshal.StructureToPtr(fd, desc, false);
                    parms.EnableFilterDesc = desc;
                    parms.FilterDescCount = 1;
                }
                int rc = Native.EnableTraceEx2(sessionHandle, ref provider,
                    Native.EventControlCodeEnableProvider, 5, KeywordAll, 0, 0, ref parms);
                if (rc != 0)
                {
                    Logger.Log(Lang.T("log.frameclock.3") + (int)mode + " rc " + rc);
                    return false;
                }
                ModeUsed = mode;
                return true;
            }
            finally { if (desc != IntPtr.Zero) Marshal.FreeHGlobal(desc); }
        }

        internal bool WidenIfSilent()
        {
            if (Interlocked.Read(ref totalEvents) > 0) return false;
            if (ModeUsed == EnableMode.AllKeywords) return false;
            return EnableWith(EnableMode.AllKeywords);
        }

        private void RunProcessTrace()
        {
            try
            {
                ulong[] handles = { traceHandle };
                Native.ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private void OnEvent(ref Native.EventRecord record)
        {
            if (record.EventHeader.ProviderId != DxgKrnl) return;
            Interlocked.Increment(ref totalEvents);
            ushort id = record.EventHeader.EventDescriptor.Id;
            if (id < idTally.Length) Interlocked.Increment(ref idTally[id]);
            if ((int)record.EventHeader.ProcessId == targetPid) Interlocked.Increment(ref targetEvents);
            if (id != EventIdPresent) return;
            if ((int)record.EventHeader.ProcessId != targetPid) return;
            TallyPresentThread(record.EventHeader.ThreadId);
            long w = Interlocked.Increment(ref written) - 1;
            int slot = (int)(w % RingSize);
            Volatile.Write(ref stampTids[slot], record.EventHeader.ThreadId);
            Volatile.Write(ref stamps[slot], record.EventHeader.TimeStamp);
        }

        private void TallyPresentThread(uint tid)
        {
            for (int i = 0; i < ThreadSlots; i++)
            {
                uint cur = Volatile.Read(ref presentTids[i]);
                if (cur == tid) { Volatile.Write(ref presentTidHits[i], Volatile.Read(ref presentTidHits[i]) + 1); return; }
                if (cur == 0)
                {
                    Volatile.Write(ref presentTids[i], tid);
                    Volatile.Write(ref presentTidHits[i], 1);
                    return;
                }
            }
        }

        public int TopPresentThread()
        {
            uint best = 0;
            long bestHits = 0;
            for (int i = 0; i < ThreadSlots; i++)
            {
                uint tid = Volatile.Read(ref presentTids[i]);
                if (tid == 0) continue;
                long h = Volatile.Read(ref presentTidHits[i]);
                if (h > bestHits) { bestHits = h; best = tid; }
            }
            return (int)best;
        }

        public void Stop()
        {
            lock (gate)
            {
                if (!started) return;
                StopStale();
                try { if (traceHandle != 0) Native.CloseTrace(traceHandle); } catch { }
                if (worker != null) { try { worker.Join(2000); } catch { } }
                worker = null;
                traceHandle = 0;
                sessionHandle = 0;
                started = false;
                keepAlive = null;
                FreeFilter();
            }
        }

        private void FreeFilter()
        {
            if (pidFilter == IntPtr.Zero) return;
            try { Marshal.FreeHGlobal(pidFilter); } catch { }
            pidFilter = IntPtr.Zero;
        }

        public uint[] CopyRecentStampTids(int count)
        {
            long total = Volatile.Read(ref written);
            int n = (int)Math.Min(Math.Min(total, RingSize), Math.Max(0, count));
            long first = total - n;
            var outp = new uint[n];
            for (int i = 0; i < n; i++)
                outp[i] = Volatile.Read(ref stampTids[(int)((first + i) % RingSize)]);
            return outp;
        }

        public long[] CopyRecentStamps(int count)
        {
            long total = Interlocked.Read(ref written);
            int n = (int)Math.Min(Math.Min(total, RingSize), Math.Max(0, count));
            var outp = new long[n];
            long first = total - n;
            for (int i = 0; i < n; i++)
                outp[i] = Volatile.Read(ref stamps[(int)((first + i) % RingSize)]);
            return outp;
        }

        public FrameWindow Snapshot(int frames)
        {
            var w = new FrameWindow { BudgetMs = budgetMs };
            long total = Interlocked.Read(ref written);
            w.StampsDropped = total > RingSize ? total - RingSize : 0;
            long[] s = CopyRecentStamps(frames);
            if (s.Length < 2) return w;

            double freq = qpcFrequency > 0 ? qpcFrequency : System.Diagnostics.Stopwatch.Frequency;
            var iv = new double[s.Length - 1];
            int bad = 0;
            for (int i = 1; i < s.Length; i++)
            {
                double ms = (s[i] - s[i - 1]) * 1000.0 / freq;
                if (ms <= 0) { bad++; ms = 0; }
                iv[i - 1] = ms;
            }
            if (bad >= iv.Length) return w;

            var sorted = (double[])iv.Clone();
            Array.Sort(sorted);
            int valid = 0;
            for (int i = 0; i < sorted.Length; i++) if (sorted[i] > 0) valid++;
            int off = sorted.Length - valid;

            w.Frames = valid;
            w.FirstQpc = s[0];
            w.LastQpc = s[s.Length - 1];
            w.MedianMs = Pick(sorted, off, valid, 0.50);
            w.P95Ms = Pick(sorted, off, valid, 0.95);
            w.P99Ms = Pick(sorted, off, valid, 0.99);

            int worst = Math.Max(1, valid / 100);
            double sum = 0;
            for (int i = 0; i < worst; i++) sum += sorted[sorted.Length - 1 - i];
            w.OnePercentLowMs = sum / worst;

            if (budgetMs > 0)
            {
                double limit = budgetMs * 1.5;
                for (int i = off; i < sorted.Length; i++) if (sorted[i] > limit) w.OverBudget++;
            }
            return w;
        }

        private static double Pick(double[] sorted, int off, int valid, double q)
        {
            if (valid <= 0) return 0;
            int idx = off + (int)Math.Floor(q * (valid - 1));
            if (idx < off) idx = off;
            if (idx >= sorted.Length) idx = sorted.Length - 1;
            return sorted[idx];
        }

        private static IntPtr AllocProps()
        {
            int nameBytes = (SessionName.Length + 1) * 2;
            int size = Marshal.SizeOf(typeof(Native.EventTraceProperties)) + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(props, i, 0);
            var p = new Native.EventTraceProperties();
            p.WnodeBufferSize = (uint)size;
            p.WnodeFlags = Native.WnodeFlagTracedGuid;
            p.WnodeGuid = SessionGuid;
            p.WnodeClientContext = 1;
            p.BufferSize = 256;
            p.MinimumBuffers = 16;
            p.MaximumBuffers = 128;
            p.LogFileMode = Native.EventTraceRealTimeMode;
            p.FlushTimer = 1;
            p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(Native.EventTraceProperties));
            Marshal.StructureToPtr(p, props, false);
            return props;
        }

        private static void StopStale()
        {
            int nameBytes = (SessionName.Length + 1) * 2;
            int size = Marshal.SizeOf(typeof(Native.EventTraceProperties)) + nameBytes + 16;
            IntPtr props = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(props, i, 0);
                var p = new Native.EventTraceProperties();
                p.WnodeBufferSize = (uint)size;
                p.WnodeGuid = SessionGuid;
                p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(Native.EventTraceProperties));
                Marshal.StructureToPtr(p, props, false);
                Native.ControlTrace(0, SessionName, props, Native.EventTraceControlStop);
            }
            catch { }
            finally { Marshal.FreeHGlobal(props); }
        }

        public static void HealFromCrash() { StopStale(); }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventFilterDescriptor
        {
            public ulong Ptr;
            public uint Size;
            public uint Type;
        }
    }
}
