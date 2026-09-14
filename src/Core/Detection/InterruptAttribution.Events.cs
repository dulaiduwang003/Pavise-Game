// @author bdth 2074055628@qq.com
// File purpose Sample result folding, event callbacks and address attribution
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
    internal sealed partial class InterruptAttribution
    {
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
                // After the controller stops the session successfully, the real-time ProcessTrace drains and returns on its own, then CloseTrace
                // If stopping fails, closing the consumer handle first is the only way to unblock; such a sample must be marked incomplete
                if (!stopSucceeded)
                    try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                //   A high-event match may still need to drain a backlog at wind-down; 2 s would misjudge a normal wind-down as a hang
                bool workerDone = true;
                if (worker != null) { try { workerDone = worker.Join(10000); } catch { workerDone = false; } }
                if (stopSucceeded)
                    try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                result.Incomplete = !CaptureComplete(
                    stopSucceeded, workerDone, processTraceSucceeded, consumerExitedEarly);
                started = false;
                // Hand probe ownership back whether or not the drain succeeded
                //   This used to return directly and leave aliveOwned set all the way,
                //   so one exception made every later match report observation busy, recoverable only by restarting the process
                ReleaseOwnership();
                if (!workerDone)
                {
                    // The worker is still writing dpcHits; this pass's data cannot be read, but the next pass can start over normally
                    result.Error = Lang.T("t.interruptattribution.5");
                    return result;
                }
                keepAlive = null;

                // The worker has been Joined and the raw marks are stable; parse the per-event DPC timeline on a single thread
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
                long mappedDpc = 0;
                foreach (var driver in result.Drivers) mappedDpc += driver.Dpc;
                result.Unmapped = Math.Max(0,dpcTotal - mappedDpc);
                bool unmapped = (dpcTotal > 0 || isrTotal > 0) && result.Drivers.Count == 0;
                string mappingError = unmapped
                    ? "已采集中断事件，但无法映射到驱动模块，本局中断归因不可用"
                        + " DPC=" + dpcTotal + " ISR=" + isrTotal + " 模块=" + modules.Count
                    : null;
                if (unmapped) Logger.Warn("IRQ " + mappingError);
                result.Ok = HasMappedEvents(dpcTotal, isrTotal, result.Drivers.Count)
                    && !result.Incomplete && !result.Lossy;
                if (result.Lossy)
                {
                    Logger.Log(Lang.F("log.interruptattribution.lossy", result.EventsLost, result.BuffersLost));
                }
                // Attribution failure must not mask the higher-priority incomplete capture and loss; none of the three yields a valid sample
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
                if (dpc) { d.CpuMask |= st.CpuMask; d.CpuMaskTruncated |= st.CpuMaskTruncated; }
                d.BadDuration += st.BadDuration;
                double totalUs = st.TotalTicks * usPerTick;
                double maxUs = st.MaxTicks * usPerTick;
                if (dpc)
                {
                    if (st.Cores != null)
                        foreach (var pair in st.Cores)
                        {
                            IrqDriverCoreRecord core = d.Cores.Find(delegate(IrqDriverCoreRecord c) { return c.Cpu == pair.Key; });
                            if (core == null)
                            {
                                core = new IrqDriverCoreRecord { Cpu = pair.Key, Buckets = new long[BucketCount] };
                                d.Cores.Add(core);
                            }
                            RoutineStat value = pair.Value;
                            core.Count += value.Count; core.BadDuration += value.BadDuration;
                            core.TotalNs += (long)(value.TotalTicks * usPerTick * 1000);
                            core.MaxNs = Math.Max(core.MaxNs, (long)(value.MaxTicks * usPerTick * 1000));
                            core.Over500Us += SumFrom(value.Buckets, BucketOver500);
                            core.Over1Ms += SumFrom(value.Buckets, BucketOver1Ms);
                            for (int i = 0; i < BucketCount; i++) core.Buckets[i] += value.Buckets[i];
                        }
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
            if (dpc && cpu < 64)
            {
                if (st.Cores == null) st.Cores = new Dictionary<int, RoutineStat>();
                RoutineStat core;
                if (!st.Cores.TryGetValue(cpu, out core)) { core = new RoutineStat(); st.Cores[cpu] = core; }
                core.Count++;
                if (!timed) core.BadDuration++;
                else
                {
                    core.TotalTicks += ticks; core.MaxTicks = Math.Max(core.MaxTicks, ticks);
                    core.Buckets[BucketOf(ticks)]++;
                }
            }
            if (isr) isrTotal++; else dpcTotal++;

            // Per-event DPC timeline, one extra record; ultra-light append, no module parsing; module addresses are resolved after Stop
            //   The aggregation line above is untouched; this only appends in parallel; with the switch off (the default) the first bool short-circuits the whole block, zero overhead
            if (dpc && captureTimeline && !timelineTruncated && dpcMarks != null)
            {
                if (dpcMarks.Count >= DpcTimelineCap) timelineTruncated = true;
                else dpcMarks.Add(new DpcMark
                {
                    // Once ETW is judged to have bad durations, an untrusted start must no longer be treated as an interval possibly spanning seconds
                    // for long-frame alignment; degrade to a point event at the end time, keeping attribution without fabricating overlap
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
    }
}
