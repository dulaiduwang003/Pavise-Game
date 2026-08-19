// @author bdth 2074055628@qq.com
// 文件用途 用内核 CSwitch 跟踪游戏进程所有线程排队等 CPU 的区间 供与帧间隔做交集
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal struct RunSpan
    {
        public long StartQpc;
        public long EndQpc;
        public uint Tid;
        public ushort Cpu;
    }

    internal struct PreemptSpan
    {
        public long StartQpc;
        public long EndQpc;
        public uint VictimTid;
        public uint DirectByPid;
        public long DirectEndQpc;
        public ushort DirectCpu;
        public bool Open;
    }

    internal struct WaitSpan
    {
        public long StartQpc;
        public long EndQpc;
        public uint Tid;
    }

    internal struct WakeEdge
    {
        public long Qpc;
        public uint WokenTid;
        public uint WakerTid;
        public uint WakerPid;
        public uint Reason;
        public ushort Cpu;
        public bool FromDpc;
        public bool Unverified;
    }

    internal sealed class CpuContentionResult
    {
        public bool Ok;
        public string Error;
        public long QpcFrequency;
        public long CSwitchTotal;
        public long TargetSwitches;
        public double ReadyWaitMs;
        public RunSpan[] Runs = new RunSpan[0];
        public PreemptSpan[] Preempts = new PreemptSpan[0];
        public int OpenRuns;
        public WaitSpan[] Waits = new WaitSpan[0];
        public long WaitsDropped;
        public WakeEdge[] Wakes = new WakeEdge[0];
        public long WakesDropped;
        public int OpenWaits;
        public long OpenWaitOverflow;
        public readonly Dictionary<uint, double> StarvedOccupancyMs = new Dictionary<uint, double>();
        public long OccupancyOverflow;
        public double OpenOccupancyMs;
        public int StarveCpuCount;
        public long RunsDropped;
        public long PreemptsDropped;
        public int TargetTid;
    }

    internal sealed class CpuContentionProbe
    {
        private const int WnodeFlagTracedGuid = 0x00020000;
        private const uint RealTimeMode = 0x00000100;
        private const uint SystemLoggerMode = 0x02000000;
        private const uint IndependentSessionMode = 0x08000000;
        private const uint ProcessModeRealTime = 0x00000100;
        private const uint ProcessModeEventRecord = 0x10000000;
        private const uint ProcessModeRawTimestamp = 0x00001000;
        private const uint ControlStop = 1;
        private const uint FlagThread = 0x00000002;
        private const uint FlagCSwitch = 0x00000010;
        private const uint FlagDispatcher = 0x00000800;
        private const int ErrorAlreadyExists = 183;
        private const int ErrorInvalidParameter = 87;

        private static readonly Guid SessionGuid = new Guid("2b7e5a91-4c30-4f18-8ad2-1e6b9c0f7a52");
        private static readonly Guid ThreadGuid = new Guid("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
        private const string SessionName = "PaviseCpuContention";

        private const byte StateReady = 1;
        private const byte StateDeferredReady = 7;

        private const int RunRingSize = 65536;
        private const int PreemptRingSize = 65536;
        private const int WaitRingSize = 32768;
        private const int WakeRingSize = 32768;

        private readonly object gate = new object();
        private readonly RunSpan[] runs = new RunSpan[RunRingSize];
        private readonly long[] runSeq = new long[RunRingSize];
        private readonly PreemptSpan[] preempts = new PreemptSpan[PreemptRingSize];
        private readonly long[] preemptSeq = new long[PreemptRingSize];
        private readonly WaitSpan[] waits = new WaitSpan[WaitRingSize];
        private readonly long[] waitRingSeq = new long[WaitRingSize];
        private long waitWritten;
        private readonly WakeEdge[] wakes = new WakeEdge[WakeRingSize];
        private readonly long[] wakeRingSeq = new long[WakeRingSize];
        private long wakeWritten;
        private readonly Dictionary<uint, long> yieldSince = new Dictionary<uint, long>();
        private readonly Dictionary<uint, uint> tidToPid = new Dictionary<uint, uint>();

        private long runWritten;
        private long preemptWritten;
        private long cswitchTotal;
        private long targetSwitches;
        private long qpcFrequency = System.Diagnostics.Stopwatch.Frequency;

        private uint targetTid;
        private uint targetPid;
        private long readyWaitTotal;
        private readonly HashSet<uint> ourTids = new HashSet<uint>();
        private readonly Dictionary<uint, long> readyAt = new Dictionary<uint, long>();
        private readonly Dictionary<uint, long> runSince = new Dictionary<uint, long>();
        private readonly Dictionary<uint, ushort> runCpu = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, uint> takerOf = new Dictionary<uint, uint>();
        private readonly Dictionary<uint, ushort> takerCpuOf = new Dictionary<uint, ushort>();
        private readonly Dictionary<uint, long> takerEndOf = new Dictionary<uint, long>();
        private readonly uint[] directVictim = new uint[MaxCpuSlots];

        private readonly uint[] runningTid = new uint[MaxCpuSlots];
        private readonly long[] runningSince = new long[MaxCpuSlots];
        private readonly long[] ourCpuLast = new long[MaxCpuSlots];
        private long affinityMask;
        private readonly bool[] starveCpu = new bool[MaxCpuSlots];
        private long recentTicks;
        private int starving;
        private long starveStart;
        private long starveEnd;
        private readonly HashSet<uint> starveTid = new HashSet<uint>();

        private const int OccSlots = 512;
        internal const uint IdlePid = 0xFFFFFFFF;
        internal const uint UnknownPid = 0xFFFFFFFE;
        internal const uint SelfPid = 0xFFFFFFFD;
        private readonly uint[] occPid = new uint[OccSlots];
        private readonly long[] occTicks = new long[OccSlots];
        private long occOverflow;
        internal long dbgCloses, dbgIdleCloses;

        private const int MaxCpuSlots = 256;
        private readonly long[] openSeq = new long[MaxCpuSlots];
        private readonly uint[] openTid = new uint[MaxCpuSlots];
        private readonly long[] openSince = new long[MaxCpuSlots];

        private ulong sessionHandle;
        private ulong traceHandle;
        private Thread worker;
        private EventRecordCallback keepAlive;
        private volatile bool started;

        public bool Started { get { return started; } }
        public int TargetTid { get { return (int)targetTid; } }
        public long QpcFrequency { get { return qpcFrequency; } }

        public bool Start(int pid)
        {
            lock (gate)
            {
                if (started) return true;
                if (pid <= 0) return false;
                targetPid = (uint)pid;
                targetTid = 0;
                runWritten = 0; preemptWritten = 0;
                cswitchTotal = 0; targetSwitches = 0; readyWaitTotal = 0;
                tidToPid.Clear(); ourTids.Clear(); readyAt.Clear();
                runSince.Clear(); runCpu.Clear(); takerOf.Clear(); takerCpuOf.Clear();
                takerEndOf.Clear(); yieldSince.Clear(); waitWritten = 0; wakeWritten = 0;
                for (int i = 0; i < MaxCpuSlots; i++)
                {
                    openSeq[i] = 0; openTid[i] = 0; openSince[i] = 0; directVictim[i] = 0;
                    runningTid[i] = 0; runningSince[i] = 0; ourCpuLast[i] = 0;
                    starveCpu[i] = false;
                }
                for (int i = 0; i < OccSlots; i++) { occPid[i] = 0; occTicks[i] = 0; }
                starving = 0; starveStart = 0; starveEnd = 0; occOverflow = 0;
                starveTid.Clear();
                for (int i = 0; i < WaitSlots; i++)
                { waitSeq[i] = 0; waitTid[i] = 0; }
                waitFree = WaitSlots;
                for (int i = 0; i < WaitSlots; i++) waitFreeList[i] = i;
                waitSlotOf.Clear();
                openWaitOverflow = 0;

                IntPtr props = AllocProps();
                try
                {
                    uint rc = StartTrace(out sessionHandle, SessionName, props);
                    if (rc == ErrorAlreadyExists)
                    {
                        StopStale();
                        Marshal.FreeHGlobal(props);
                        props = AllocProps();
                        rc = StartTrace(out sessionHandle, SessionName, props);
                    }
                    if (rc == ErrorInvalidParameter)
                    {
                        Logger.Log(Lang.T("log.cpucontention.1"));
                        return false;
                    }
                    if (rc != 0) { Logger.Log(Lang.T("log.cpucontention.2") + rc); return false; }
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
                    Logger.Log(Lang.T("log.cpucontention.3") + Marshal.GetLastWin32Error());
                    StopStale();
                    return false;
                }

                long freq = logfile.LogfileHeader.PerfFreq;
                if (freq <= 0) freq = System.Diagnostics.Stopwatch.Frequency;
                qpcFrequency = freq;
                recentTicks = freq * 2;

                try
                {
                    using (var proc = System.Diagnostics.Process.GetProcessById(pid))
                        foreach (System.Diagnostics.ProcessThread t in proc.Threads)
                            ourTids.Add((uint)t.Id);
                }
                catch { }

                worker = new Thread(RunProcessTrace);
                worker.IsBackground = true;
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
                ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
            }
            catch { }
        }

        private void OnEvent(ref EventRecord record)
        {
            if (record.EventHeader.ProviderId != ThreadGuid) return;
            byte op = record.EventHeader.EventDescriptor.Opcode;

            if (op == 36)
            {
                if (record.UserDataLength < 16) return;
                long p = record.UserData.ToInt64();
                uint newTid = (uint)Marshal.ReadInt32(new IntPtr(p));
                uint oldTid = (uint)Marshal.ReadInt32(new IntPtr(p + 4));
                cswitchTotal++;
                long now = record.EventHeader.TimeStamp;
                ushort cpu = record.BufferContext.ProcessorIndex;

                if (cpu < MaxCpuSlots)
                {
                    uint dv = directVictim[cpu];
                    if (dv != 0)
                    {
                        long de;
                        if (!takerEndOf.TryGetValue(dv, out de) || de <= 0) takerEndOf[dv] = now;
                        directVictim[cpu] = 0;
                        UpdateOpenWaitDirectEnd(dv, now);
                    }
                    CloseOccupancy(cpu, now);
                    runningTid[cpu] = newTid;
                    runningSince[cpu] = now;
                }

                bool inIsOurs = ourTids.Contains(newTid);
                bool outIsOurs = ourTids.Contains(oldTid);
                if (!inIsOurs && !outIsOurs) return;

                targetSwitches++;

                if (outIsOurs)
                {
                    long rs;
                    if (runSince.TryGetValue(oldTid, out rs) && rs > 0 && now > rs)
                    {
                        ushort rc;
                        if (!runCpu.TryGetValue(oldTid, out rc)) rc = cpu;
                        PushRun(rs, now, rc, oldTid);
                    }
                    runSince[oldTid] = 0;
                    ClearOpen(cpu);

                    byte oldState = Marshal.ReadByte(new IntPtr(p + 14));
                    if (oldState == StateReady || oldState == StateDeferredReady)
                    {
                        readyAt[oldTid] = now;
                        uint takerPid;
                        if (!tidToPid.TryGetValue(newTid, out takerPid)) takerPid = 0;
                        if (takerPid == targetPid) takerPid = 0;
                        takerOf[oldTid] = takerPid;
                        takerCpuOf[oldTid] = cpu;
                        takerEndOf[oldTid] = 0;
                        if (cpu < MaxCpuSlots && takerPid != 0) directVictim[cpu] = oldTid;
                        AllocOpenWait(oldTid, now, takerPid, cpu);
                        if (IsEligible(oldTid) && starveTid.Add(oldTid)) BeginStarve(now);
                    }
                    else
                    {
                        if (starveTid.Remove(oldTid)) EndStarve(now);
                        readyAt[oldTid] = 0; takerOf[oldTid] = 0;
                        takerCpuOf[oldTid] = 0; takerEndOf[oldTid] = 0;
                        yieldSince[oldTid] = now;
                    }
                }

                if (inIsOurs)
                {
                    long since;
                    if (readyAt.TryGetValue(newTid, out since) && since > 0 && now > since)
                    {
                        uint byPid;
                        if (!takerOf.TryGetValue(newTid, out byPid)) byPid = 0;
                        ushort byCpu;
                        if (!takerCpuOf.TryGetValue(newTid, out byCpu)) byCpu = cpu;
                        long directEnd;
                        if (!takerEndOf.TryGetValue(newTid, out directEnd) || directEnd <= 0)
                            directEnd = now;
                        if (directEnd > now) directEnd = now;
                        if (byPid == 0) directEnd = since;
                        readyWaitTotal += now - since;
                        PushPreempt(since, now, newTid, byPid, directEnd, byCpu, false);
                    }
                    if (starveTid.Remove(newTid)) EndStarve(now);
                    FreeOpenWait(newTid);
                    long ws;
                    if (yieldSince.TryGetValue(newTid, out ws) && ws > 0 && now > ws)
                        PushWait(ws, now, newTid);
                    yieldSince[newTid] = 0;
                    readyAt[newTid] = 0;
                    takerOf[newTid] = 0;
                    takerCpuOf[newTid] = 0;
                    takerEndOf[newTid] = 0;
                    runSince[newTid] = now;
                    runCpu[newTid] = cpu;
                    if (cpu < MaxCpuSlots) ourCpuLast[cpu] = now;
                    PublishOpen(cpu, newTid, now);
                }
                return;
            }

            if (op == 50)
            {
                if (record.UserDataLength < 4) return;
                uint rtid = (uint)Marshal.ReadInt32(record.UserData);
                if (!ourTids.Contains(rtid)) return;
                long cur;
                if (readyAt.TryGetValue(rtid, out cur) && cur > 0) return;
                long wsr;
                if (yieldSince.TryGetValue(rtid, out wsr) && wsr > 0
                    && record.EventHeader.TimeStamp > wsr)
                    PushWait(wsr, record.EventHeader.TimeStamp, rtid);
                yieldSince[rtid] = 0;
                readyAt[rtid] = record.EventHeader.TimeStamp;
                PushWake(ref record, rtid);
                takerOf[rtid] = 0;
                takerCpuOf[rtid] = 0;
                takerEndOf[rtid] = 0;
                AllocOpenWait(rtid, readyAt[rtid], 0, 0);
                return;
            }

            if (op == 1 || op == 3)
            {
                if (record.UserDataLength < 8) return;
                long p = record.UserData.ToInt64();
                uint pid = (uint)Marshal.ReadInt32(new IntPtr(p));
                uint tid = (uint)Marshal.ReadInt32(new IntPtr(p + 4));
                if (tid == 0) return;
                tidToPid[tid] = pid;
                if (pid == targetPid) ourTids.Add(tid);
                else if (ourTids.Remove(tid)) ForgetThread(tid, record.EventHeader.TimeStamp);
                return;
            }
            if (op == 2 || op == 4)
            {
                if (record.UserDataLength < 8) return;
                long p = record.UserData.ToInt64();
                uint tid = (uint)Marshal.ReadInt32(new IntPtr(p + 4));
                if (tid == 0) return;
                tidToPid.Remove(tid);
                ourTids.Remove(tid);
                ForgetThread(tid, record.EventHeader.TimeStamp);
            }
        }

        private void ForgetThread(uint tid) { ForgetThread(tid, 0); }

        private void ForgetThread(uint tid, long now)
        {
            readyAt.Remove(tid);
            runSince.Remove(tid);
            runCpu.Remove(tid);
            takerOf.Remove(tid);
            takerCpuOf.Remove(tid);
            takerEndOf.Remove(tid);
            yieldSince.Remove(tid);
            if (starveTid.Remove(tid))
            {
                if (now > 0) EndStarve(now);
                else if (starving > 0) { starving--; if (starving == 0) starveEnd = starveStart; }
            }
            FreeOpenWait(tid);
            for (int i = 0; i < MaxCpuSlots; i++) if (directVictim[i] == tid) directVictim[i] = 0;
            for (int i = 0; i < MaxCpuSlots; i++)
                if (Volatile.Read(ref openTid[i]) == tid) ClearOpen((ushort)i);
        }

        private void BeginStarve(long now)
        {
            if (starving == 0)
            {
                starveStart = now;
                long mask = affinityMask;
                for (int c = 0; c < MaxCpuSlots; c++)
                {
                    if (mask != 0 && c < 64) starveCpu[c] = ((mask >> c) & 1) != 0;
                    else if (mask != 0) starveCpu[c] = false;
                    else { long seen = ourCpuLast[c]; starveCpu[c] = seen > 0 && now - seen <= recentTicks; }
                    if (runningTid[c] != 0 || runningSince[c] > 0) runningSince[c] = now;
                }
            }
            starving++;
        }

        private void EndStarve(long now)
        {
            if (starving <= 0) { starving = 0; return; }
            if (starving == 1)
            {
                SettleAll(now);
                starveEnd = now;
                for (int c = 0; c < MaxCpuSlots; c++)
                    if (runningTid[c] != 0 || runningSince[c] > 0) runningSince[c] = now;
            }
            starving--;
        }

        private bool IsEligible(uint tid)
        {
            HashSet<uint> e = eligible;
            return e == null || e.Count == 0 || e.Contains(tid);
        }

        private volatile HashSet<uint> eligible;

        public void SetEligibleThreads(HashSet<uint> set) { eligible = set; }

        public void SetAffinityMask(long mask) { Volatile.Write(ref affinityMask, mask); }

        private void SettleAll(long now)
        {
            if (starving <= 0) return;
            for (ushort c = 0; c < MaxCpuSlots; c++) CloseOccupancy(c, now);
        }

        private void CloseOccupancy(ushort cpu, long now)
        {
            if (!starveCpu[cpu]) return;
            uint tid = runningTid[cpu];
            if (starving > 0) { dbgCloses++; if (tid == 0) dbgIdleCloses++; }
            long segStart = runningSince[cpu];
            if (segStart <= 0) return;
            long lo = segStart > starveStart ? segStart : starveStart;
            long hi = starving > 0 ? now : starveEnd;
            if (hi > now) hi = now;
            if (hi <= lo) return;
            uint who;
            if (tid == 0) who = IdlePid;
            else if (!tidToPid.TryGetValue(tid, out who)) who = UnknownPid;
            else if (who == targetPid) who = SelfPid;
            AddOccupancy(who, hi - lo);
        }

        private bool TryPeekPid(uint tid, out uint who)
        {
            who = 0;
            try { return tidToPid.TryGetValue(tid, out who); }
            catch { return false; }
        }

        private void AddOccupancy(uint who, long ticks)
        {
            int i = (int)((who * 2654435761u) % OccSlots);
            for (int n = 0; n < 16; n++)
            {
                uint cur = occPid[i];
                if (cur == who) { occTicks[i] = occTicks[i] + ticks; return; }
                if (cur == 0) { occPid[i] = who; occTicks[i] = ticks; return; }
                i++;
                if (i >= OccSlots) i = 0;
            }
            occOverflow++;
        }

        private const int WaitSlots = 256;
        private readonly long[] waitSeq = new long[WaitSlots];
        private readonly uint[] waitTid = new uint[WaitSlots];
        private readonly long[] waitSince = new long[WaitSlots];
        private readonly uint[] waitPid = new uint[WaitSlots];
        private readonly long[] waitDirectEnd = new long[WaitSlots];
        private readonly ushort[] waitCpu = new ushort[WaitSlots];
        private readonly int[] waitFreeList = new int[WaitSlots];
        private readonly Dictionary<uint, int> waitSlotOf = new Dictionary<uint, int>();
        private int waitFree;
        private long openWaitOverflow;

        private void AllocOpenWait(uint tid, long since, uint pid, ushort cpu)
        {
            int slot;
            if (!waitSlotOf.TryGetValue(tid, out slot))
            {
                if (waitFree <= 0) { openWaitOverflow++; return; }
                slot = waitFreeList[--waitFree];
                waitSlotOf[tid] = slot;
            }
            long seq = Volatile.Read(ref waitSeq[slot]);
            if (seq < 0) seq = -seq;
            seq++;
            Volatile.Write(ref waitSeq[slot], -seq);
            waitTid[slot] = tid;
            waitSince[slot] = since;
            waitPid[slot] = pid;
            waitDirectEnd[slot] = pid == 0 ? since : 0;
            waitCpu[slot] = cpu;
            Volatile.Write(ref waitSeq[slot], seq);
        }

        private void UpdateOpenWaitDirectEnd(uint tid, long end)
        {
            int slot;
            if (!waitSlotOf.TryGetValue(tid, out slot)) return;
            if (waitTid[slot] != tid) return;
            long seq = Volatile.Read(ref waitSeq[slot]);
            if (seq < 0) seq = -seq;
            seq++;
            Volatile.Write(ref waitSeq[slot], -seq);
            if (waitDirectEnd[slot] <= 0) waitDirectEnd[slot] = end;
            Volatile.Write(ref waitSeq[slot], seq);
        }

        private void FreeOpenWait(uint tid)
        {
            int slot;
            if (!waitSlotOf.TryGetValue(tid, out slot)) return;
            waitSlotOf.Remove(tid);
            long seq = Volatile.Read(ref waitSeq[slot]);
            if (seq < 0) seq = -seq;
            seq++;
            Volatile.Write(ref waitSeq[slot], -seq);
            waitTid[slot] = 0;
            waitSince[slot] = 0;
            Volatile.Write(ref waitSeq[slot], seq);
            if (waitFree < WaitSlots) waitFreeList[waitFree++] = slot;
        }

        private void PublishOpen(ushort cpu, uint tid, long since)
        {
            if (cpu >= MaxCpuSlots) return;
            long seq = Volatile.Read(ref openSeq[cpu]) + 1;
            Volatile.Write(ref openSeq[cpu], -seq);
            openTid[cpu] = tid;
            openSince[cpu] = since;
            Volatile.Write(ref openSeq[cpu], seq);
        }

        private void ClearOpen(ushort cpu)
        {
            if (cpu >= MaxCpuSlots) return;
            long seq = Volatile.Read(ref openSeq[cpu]);
            if (seq < 0) seq = -seq;
            seq++;
            Volatile.Write(ref openSeq[cpu], -seq);
            openTid[cpu] = 0;
            openSince[cpu] = 0;
            Volatile.Write(ref openSeq[cpu], seq);
        }

        private void PushRun(long start, long end, ushort cpu, uint tid)
        {
            long seq = runWritten;
            int i = (int)(seq % RunRingSize);
            Volatile.Write(ref runSeq[i], -1);
            runs[i].StartQpc = start;
            runs[i].EndQpc = end;
            runs[i].Cpu = cpu;
            runs[i].Tid = tid;
            Volatile.Write(ref runSeq[i], seq);
            Volatile.Write(ref runWritten, seq + 1);
        }

        private void PushWake(ref EventRecord record, uint woken)
        {
            uint reason = 0;
            bool fromDpc = false;
            if (record.UserDataLength >= 8)
            {
                reason = (uint)Marshal.ReadInt32(new IntPtr(record.UserData.ToInt64() + 4));
                fromDpc = ((reason >> 16) & 0x1) != 0;
            }
            ushort wcpu = record.BufferContext.ProcessorIndex;
            uint wtid = 0;
            bool unverified = true;
            if (wcpu < MaxCpuSlots)
            {
                wtid = runningTid[wcpu];
                if (wtid != 0) unverified = false;
            }
            uint wpid;
            if (!tidToPid.TryGetValue(wtid, out wpid)) wpid = 0;
            long seq = wakeWritten;
            int i = (int)(seq % WakeRingSize);
            Volatile.Write(ref wakeRingSeq[i], -1);
            wakes[i].Qpc = record.EventHeader.TimeStamp;
            wakes[i].WokenTid = woken;
            wakes[i].WakerTid = wtid;
            wakes[i].WakerPid = wpid;
            wakes[i].Reason = reason;
            wakes[i].Cpu = wcpu;
            wakes[i].FromDpc = fromDpc;
            wakes[i].Unverified = unverified;
            Volatile.Write(ref wakeRingSeq[i], seq);
            Volatile.Write(ref wakeWritten, seq + 1);
        }

        private void PushWait(long start, long end, uint tid)
        {
            long seq = waitWritten;
            int i = (int)(seq % WaitRingSize);
            Volatile.Write(ref waitRingSeq[i], -1);
            waits[i].StartQpc = start;
            waits[i].EndQpc = end;
            waits[i].Tid = tid;
            Volatile.Write(ref waitRingSeq[i], seq);
            Volatile.Write(ref waitWritten, seq + 1);
        }

        private void PushPreempt(long start, long end, uint victimTid, uint byPid,
            long directEnd, ushort cpu, bool open)
        {
            long seq = preemptWritten;
            int i = (int)(seq % PreemptRingSize);
            Volatile.Write(ref preemptSeq[i], -1);
            preempts[i].StartQpc = start;
            preempts[i].EndQpc = end;
            preempts[i].VictimTid = victimTid;
            preempts[i].DirectByPid = byPid;
            preempts[i].DirectEndQpc = directEnd;
            preempts[i].DirectCpu = cpu;
            preempts[i].Open = open;
            Volatile.Write(ref preemptSeq[i], seq);
            Volatile.Write(ref preemptWritten, seq + 1);
        }

        public CpuContentionResult Snapshot()
        {
            var r = new CpuContentionResult();
            r.QpcFrequency = qpcFrequency;
            r.CSwitchTotal = Volatile.Read(ref cswitchTotal);
            r.TargetSwitches = Volatile.Read(ref targetSwitches);
            r.ReadyWaitMs = qpcFrequency > 0
                ? Volatile.Read(ref readyWaitTotal) * 1000.0 / qpcFrequency : 0;
            r.TargetTid = (int)targetTid;

            long rw = Volatile.Read(ref runWritten);
            int rn = (int)Math.Min(rw, RunRingSize);
            long rfirst = rw > RunRingSize ? rw - RunRingSize : 0;
            var ro = new RunSpan[rn];
            int rk = 0;
            for (int k = 0; k < rn; k++)
            {
                long want = rfirst + k;
                int i = (int)(want % RunRingSize);
                if (Volatile.Read(ref runSeq[i]) != want) continue;
                RunSpan c = runs[i];
                if (Volatile.Read(ref runSeq[i]) != want) continue;
                ro[rk++] = c;
            }
            long nowQpc = System.Diagnostics.Stopwatch.GetTimestamp();
            var open = new List<RunSpan>();
            for (ushort c = 0; c < MaxCpuSlots; c++)
            {
                long a = Volatile.Read(ref openSeq[c]);
                if (a <= 0) continue;
                uint t = openTid[c];
                long since = openSince[c];
                if (Volatile.Read(ref openSeq[c]) != a) continue;
                if (t == 0 || since <= 0 || nowQpc <= since) continue;
                var sp = new RunSpan();
                sp.StartQpc = since; sp.EndQpc = nowQpc; sp.Cpu = c; sp.Tid = t;
                open.Add(sp);
            }
            if (open.Count > 0)
            {
                var merged = new RunSpan[rk + open.Count];
                Array.Copy(ro, merged, rk);
                open.CopyTo(merged, rk);
                r.Runs = merged;
            }
            else r.Runs = Trim(ro, rk);
            r.OpenRuns = open.Count;
            r.RunsDropped = rfirst;

            long pw = Volatile.Read(ref preemptWritten);
            int pn = (int)Math.Min(pw, PreemptRingSize);
            long pfirst = pw > PreemptRingSize ? pw - PreemptRingSize : 0;
            var po = new PreemptSpan[pn];
            int pk = 0;
            for (int k = 0; k < pn; k++)
            {
                long want = pfirst + k;
                int i = (int)(want % PreemptRingSize);
                if (Volatile.Read(ref preemptSeq[i]) != want) continue;
                PreemptSpan c = preempts[i];
                if (Volatile.Read(ref preemptSeq[i]) != want) continue;
                po[pk++] = c;
            }
            var openWaits = new List<PreemptSpan>();
            for (int i = 0; i < WaitSlots; i++)
            {
                long a = Volatile.Read(ref waitSeq[i]);
                if (a <= 0) continue;
                uint t = waitTid[i];
                long since = waitSince[i];
                uint wp = waitPid[i];
                long wde = waitDirectEnd[i];
                ushort wc = waitCpu[i];
                if (Volatile.Read(ref waitSeq[i]) != a) continue;
                if (t == 0 || since <= 0 || nowQpc <= since) continue;
                var sp = new PreemptSpan();
                sp.StartQpc = since; sp.EndQpc = nowQpc; sp.VictimTid = t;
                sp.DirectByPid = wp;
                sp.DirectEndQpc = wp == 0 ? since : (wde > 0 ? wde : nowQpc);
                sp.DirectCpu = wc; sp.Open = true;
                openWaits.Add(sp);
            }
            if (openWaits.Count > 0)
            {
                var merged = new PreemptSpan[pk + openWaits.Count];
                Array.Copy(po, merged, pk);
                openWaits.CopyTo(merged, pk);
                r.Preempts = merged;
            }
            else r.Preempts = TrimP(po, pk);
            r.OpenWaits = openWaits.Count;
            r.OpenWaitOverflow = Volatile.Read(ref openWaitOverflow);

            long kw = Volatile.Read(ref wakeWritten);
            int kn = (int)Math.Min(kw, WakeRingSize);
            long kfirst = kw > WakeRingSize ? kw - WakeRingSize : 0;
            var ko = new WakeEdge[kn];
            int kk = 0;
            for (int k = 0; k < kn; k++)
            {
                long want = kfirst + k;
                int i = (int)(want % WakeRingSize);
                if (Volatile.Read(ref wakeRingSeq[i]) != want) continue;
                WakeEdge c = wakes[i];
                if (Volatile.Read(ref wakeRingSeq[i]) != want) continue;
                ko[kk++] = c;
            }
            if (kk != ko.Length) { var t = new WakeEdge[kk]; Array.Copy(ko, t, kk); ko = t; }
            r.Wakes = ko;
            r.WakesDropped = kfirst;

            long ww = Volatile.Read(ref waitWritten);
            int wn = (int)Math.Min(ww, WaitRingSize);
            long wfirst = ww > WaitRingSize ? ww - WaitRingSize : 0;
            var wo = new WaitSpan[wn];
            int wk = 0;
            for (int k = 0; k < wn; k++)
            {
                long want = wfirst + k;
                int i = (int)(want % WaitRingSize);
                if (Volatile.Read(ref waitRingSeq[i]) != want) continue;
                WaitSpan c = waits[i];
                if (Volatile.Read(ref waitRingSeq[i]) != want) continue;
                wo[wk++] = c;
            }
            if (wk != wo.Length) { var t = new WaitSpan[wk]; Array.Copy(wo, t, wk); wo = t; }
            r.Waits = wo;
            r.WaitsDropped = wfirst;

            double occMsPerTick = qpcFrequency > 0 ? 1000.0 / qpcFrequency : 0;
            if (Volatile.Read(ref starving) > 0)
            {
                long ss = starveStart;
                for (int c = 0; c < MaxCpuSlots; c++)
                {
                    if (!starveCpu[c]) continue;
                    long segStart = runningSince[c];
                    if (segStart <= 0) continue;
                    long lo2 = segStart > ss ? segStart : ss;
                    if (nowQpc <= lo2) continue;
                    uint tid2 = runningTid[c];
                    uint who2;
                    if (tid2 == 0) who2 = IdlePid;
                    else if (!TryPeekPid(tid2, out who2)) who2 = UnknownPid;
                    else if (who2 == targetPid) who2 = SelfPid;
                    double ms2;
                    r.StarvedOccupancyMs.TryGetValue(who2, out ms2);
                    r.StarvedOccupancyMs[who2] = ms2 + (nowQpc - lo2) * occMsPerTick;
                    r.OpenOccupancyMs += (nowQpc - lo2) * occMsPerTick;
                }
            }
            for (int i = 0; i < OccSlots; i++)
            {
                uint who = occPid[i];
                if (who == 0) continue;
                long t = occTicks[i];
                if (t <= 0) continue;
                double ms;
                r.StarvedOccupancyMs.TryGetValue(who, out ms);
                r.StarvedOccupancyMs[who] = ms + t * occMsPerTick;
            }
            r.OccupancyOverflow = Volatile.Read(ref occOverflow);
            int nc = 0;
            for (int i = 0; i < MaxCpuSlots; i++) if (starveCpu[i]) nc++;
            r.StarveCpuCount = nc;
            r.PreemptsDropped = pfirst;
            r.Ok = r.TargetSwitches > 0;
            if (!r.Ok) r.Error = Lang.T("t.cpucontention.1");
            return r;
        }

        private static RunSpan[] Trim(RunSpan[] a, int n)
        {
            if (n == a.Length) return a;
            var t = new RunSpan[n];
            Array.Copy(a, t, n);
            return t;
        }

        private static PreemptSpan[] TrimP(PreemptSpan[] a, int n)
        {
            if (n <= 0) return new PreemptSpan[0];
            if (n == a.Length) return a;
            var t = new PreemptSpan[n];
            Array.Copy(a, t, n);
            return t;
        }

        public CpuContentionResult Stop()
        {
            lock (gate)
            {
                if (!started) { var e = new CpuContentionResult(); e.Error = Lang.T("t.cpucontention.2"); return e; }
                StopStale();
                try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                if (worker != null) { try { worker.Join(2000); } catch { } }
                started = false;
                keepAlive = null;
                worker = null;
                traceHandle = 0;
                sessionHandle = 0;
                return Snapshot();
            }
        }

        public static void HealFromCrash() { StopStale(); }

        private static IntPtr AllocProps()
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
            p.BufferSize = 512;
            p.MinimumBuffers = 64;
            p.MaximumBuffers = 256;
            p.LogFileMode = RealTimeMode | SystemLoggerMode | IndependentSessionMode;
            p.FlushTimer = 1;
            p.EnableFlags = FlagThread | FlagCSwitch | FlagDispatcher;
            p.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EventTraceProperties));
            Marshal.StructureToPtr(p, props, false);
            return props;
        }

        private static void StopStale()
        {
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
                ControlTrace(0, SessionName, props, ControlStop);
            }
            catch { }
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

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
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

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTrace
        {
            public EventTraceHeaderStub Header;
            public uint InstanceId, ParentInstanceId;
            public Guid ParentGuid;
            public IntPtr MofData;
            public uint MofLength, ClientContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct EventTraceHeaderStub
        {
            public ushort Size, FieldTypeFlags;
            public uint Version, ThreadId, ProcessId;
            public long TimeStamp;
            public Guid Guid;
            public uint KernelTime, UserTime;
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

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
        private static extern uint StartTrace(out ulong handle, string name, IntPtr props);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
        private static extern uint ControlTrace(ulong handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "OpenTraceW")]
        private static extern ulong OpenTrace(ref EventTraceLogfile logfile);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern uint CloseTrace(ulong handle);
    }
}
