// @author bdth 2074055628@qq.com
// 文件用途 进程外帧时钟 用 DxgKrnl ETW 抓目标进程的 Present 得到帧率与帧时间
// 全程不打开游戏进程句柄 不读游戏内存 不注入 只订阅 ETW 并按事件头的 ProcessId 比对
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed class FrameWindow
    {
        public int Frames;
        public double Seconds;
        public double Fps;
        public double BudgetMs;
        public double AvgMs;
        public double MedianMs;
        public double P95Ms;
        public double P99Ms;
        public double OnePercentLowMs;
        public double OnePercentLowFps;
        public int OverBudget;
        public long FirstQpc;
        public long LastQpc;
        public long StampsDropped;

        public int PresentThreads;
        public double TopThreadShare;
        public double TopThreadFps;
        public bool MultiPresenter;

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
        private const uint FilterTypeEventId = 0x80000200;
        private const int ErrorAlreadyExists = 183;

        internal static readonly ulong[] KeywordLadder =
        {
            0x0000000008000000UL,
            0x0000000000000001UL,
            0x4000000008000001UL,
            ulong.MaxValue
        };

        internal static ulong ForceKeyword = 0;
        internal static bool NoIdFilter = false;
        internal ulong KeywordUsed { get; private set; }
        private int rungIndex;

        private const int RingSize = 1024;
        private const double TopThreadDominant = 0.90;

        private readonly object gate = new object();
        private readonly long[] stamps = new long[RingSize];
        private readonly uint[] stampTids = new uint[RingSize];
        private long written;
        private int targetPid;
        private ulong sessionHandle;
        private ulong traceHandle;
        private Thread worker;
        private Native.EventRecordCallback keepAlive;
        private IntPtr idFilter;
        private volatile bool started;
        private double budgetMs;
        private long qpcFrequency = System.Diagnostics.Stopwatch.Frequency;
        private long totalEvents;
        private long targetEvents;

        public bool Started { get { return started; } }
        public long TotalFrames { get { return Interlocked.Read(ref written); } }
        public long QpcFrequency { get { return qpcFrequency; } }
        public long TotalEvents { get { return Interlocked.Read(ref totalEvents); } }
        public long TargetEvents { get { return Interlocked.Read(ref targetEvents); } }

        public bool Start(int pid, double refreshHz)
        {
            lock (gate)
            {
                if (started) return true;
                targetPid = pid;
                budgetMs = refreshHz > 1 ? 1000.0 / refreshHz : 0;
                Interlocked.Exchange(ref written, 0);
                Interlocked.Exchange(ref totalEvents, 0);
                Interlocked.Exchange(ref targetEvents, 0);

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

                rungIndex = 0;
                if (!EnableWith(ForceKeyword != 0 ? ForceKeyword : KeywordLadder[0]))
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

        private bool EnableWith(ulong mask)
        {
            Guid provider = DxgKrnl;
            IntPtr desc = IntPtr.Zero;
            var parms = new Native.EnableTraceParameters();
            parms.Version = 2;
            try
            {
                if (!NoIdFilter)
                {
                    int idBytes = 1 + 1 + 2 + 2;
                    FreeFilter();
                    idFilter = Marshal.AllocHGlobal(idBytes);
                    Marshal.WriteByte(idFilter, 0, 1);
                    Marshal.WriteByte(idFilter, 1, 0);
                    Marshal.WriteInt16(idFilter, 2, 1);
                    Marshal.WriteInt16(idFilter, 4, unchecked((short)EventIdPresent));
                    desc = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(EventFilterDescriptor)));
                    var fd = new EventFilterDescriptor
                    {
                        Ptr = (ulong)idFilter.ToInt64(),
                        Size = (uint)idBytes,
                        Type = FilterTypeEventId
                    };
                    Marshal.StructureToPtr(fd, desc, false);
                    parms.EnableFilterDesc = desc;
                    parms.FilterDescCount = 1;
                }

                int rc = Native.EnableTraceEx2(sessionHandle, ref provider,
                    Native.EventControlCodeEnableProvider, 5, mask, 0, 0, ref parms);
                if (rc != 0)
                {
                    Logger.Log(Lang.T("log.frameclock.3") + "0x" + mask.ToString("X16") + " rc " + rc);
                    return false;
                }
                KeywordUsed = mask;
                return true;
            }
            finally { if (desc != IntPtr.Zero) Marshal.FreeHGlobal(desc); }
        }

        internal bool WidenIfSilent()
        {
            if (!started) return false;
            if (Interlocked.Read(ref totalEvents) > 0) return false;
            if (ForceKeyword != 0) return false;
            lock (gate)
            {
                if (rungIndex >= KeywordLadder.Length - 1) return false;
                bool ok = EnableWith(KeywordLadder[rungIndex + 1]);
                if (ok)
                {
                    rungIndex++;
                    Logger.Log(Lang.T("log.frameclock.4") + "0x" + KeywordUsed.ToString("X16"));
                }
                return ok;
            }
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
            if (record.EventHeader.EventDescriptor.Id != EventIdPresent) return;
            if ((int)record.EventHeader.ProcessId != targetPid) return;
            Interlocked.Increment(ref targetEvents);
            long w = Interlocked.Increment(ref written) - 1;
            int slot = (int)(w % RingSize);
            Volatile.Write(ref stampTids[slot], record.EventHeader.ThreadId);
            Volatile.Write(ref stamps[slot], record.EventHeader.TimeStamp);
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
            if (idFilter == IntPtr.Zero) return;
            try { Marshal.FreeHGlobal(idFilter); } catch { }
            idFilter = IntPtr.Zero;
        }

        public int CopyRecent(int count, long[] outStamps, uint[] outTids)
        {
            long total = Interlocked.Read(ref written);
            int cap = Math.Min(outStamps.Length, outTids.Length);
            int n = (int)Math.Min(Math.Min(total, RingSize), Math.Max(0, Math.Min(count, cap)));
            long first = total - n;
            for (int i = 0; i < n; i++)
            {
                int slot = (int)((first + i) % RingSize);
                outTids[i] = Volatile.Read(ref stampTids[slot]);
                outStamps[i] = Volatile.Read(ref stamps[slot]);
            }
            return n;
        }

        public FrameWindow Snapshot(int frames)
        {
            var w = new FrameWindow { BudgetMs = budgetMs };
            long total = Interlocked.Read(ref written);
            w.StampsDropped = total > RingSize ? total - RingSize : 0;
            int want = Math.Min(frames, RingSize);
            var s = new long[want];
            var t = new uint[want];
            int n = CopyRecent(want, s, t);
            if (n < 2) return w;
            Fill(w, s, t, n, qpcFrequency);
            return w;
        }

        public static FrameWindow StatsOf(long[] s, uint[] tids, int n, double budget, long freqHint)
        {
            var w = new FrameWindow { BudgetMs = budget };
            if (s == null || n < 2) return w;
            Fill(w, s, tids, n, freqHint > 0 ? freqHint : System.Diagnostics.Stopwatch.Frequency);
            return w;
        }

        private static void Fill(FrameWindow w, long[] s, uint[] tids, int n, long qpcFrequency)
        {
            double freq = qpcFrequency > 0 ? qpcFrequency : System.Diagnostics.Stopwatch.Frequency;

            w.Frames = n;
            w.FirstQpc = s[0];
            w.LastQpc = s[n - 1];
            w.Seconds = (s[n - 1] - s[0]) / freq;
            w.Fps = w.Seconds > 0 ? (n - 1) / w.Seconds : 0;

            var iv = new double[n - 1];
            int valid = 0;
            for (int i = 1; i < n; i++)
            {
                double ms = (s[i] - s[i - 1]) * 1000.0 / freq;
                if (ms <= 0) ms = 0; else valid++;
                iv[i - 1] = ms;
            }
            if (valid < 1) return;

            var sorted = (double[])iv.Clone();
            Array.Sort(sorted);
            int off = sorted.Length - valid;

            double sumAll = 0;
            for (int i = off; i < sorted.Length; i++) sumAll += sorted[i];
            w.AvgMs = sumAll / valid;

            w.MedianMs = Pick(sorted, off, valid, 0.50);
            w.P95Ms = Pick(sorted, off, valid, 0.95);
            w.P99Ms = Pick(sorted, off, valid, 0.99);

            int worst = Math.Max(1, valid / 100);
            double sum = 0;
            for (int i = 0; i < worst; i++) sum += sorted[sorted.Length - 1 - i];
            w.OnePercentLowMs = sum / worst;
            w.OnePercentLowFps = w.OnePercentLowMs > 0 ? 1000.0 / w.OnePercentLowMs : 0;

            if (w.BudgetMs > 0)
            {
                double limit = Math.Max(w.BudgetMs, w.MedianMs) * 1.5;
                for (int i = off; i < sorted.Length; i++) if (sorted[i] > limit) w.OverBudget++;
            }

            FillThreadMix(w, s, tids, n, freq);
        }

        private static void FillThreadMix(FrameWindow w, long[] s, uint[] tids, int n, double freq)
        {
            if (tids == null) return;
            const int Slots = 16;
            var ids = new uint[Slots];
            var hits = new int[Slots];
            int used = 0;
            for (int i = 0; i < n; i++)
            {
                uint tid = tids[i];
                if (tid == 0) continue;
                int at = -1;
                for (int k = 0; k < used; k++) if (ids[k] == tid) { at = k; break; }
                if (at < 0)
                {
                    if (used >= Slots) continue;
                    at = used++;
                    ids[at] = tid;
                }
                hits[at]++;
            }
            if (used == 0) return;

            int best = 0, all = 0;
            for (int k = 0; k < used; k++) { all += hits[k]; if (hits[k] > hits[best]) best = k; }
            if (all <= 0) return;

            w.PresentThreads = used;
            w.TopThreadShare = (double)hits[best] / all;
            w.MultiPresenter = w.TopThreadShare < TopThreadDominant;

            uint top = ids[best];
            long first = -1, last = -1;
            int count = 0;
            for (int i = 0; i < n; i++)
            {
                if (tids[i] != top) continue;
                if (first < 0) first = s[i];
                last = s[i];
                count++;
            }
            if (count >= 2 && last > first)
                w.TopThreadFps = (count - 1) / ((last - first) / freq);
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
