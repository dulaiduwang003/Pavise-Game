// @author bdth 2074055628@qq.com
// 文件用途 采样结果折叠 事件回调与地址归属
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
                // 控制器成功停会话后 实时 ProcessTrace 会排空并自行返回 其后再 CloseTrace
                // 停止失败只能先关消费句柄解除阻塞 这种样本必须标不完整
                if (!stopSucceeded)
                    try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                //   高事件量对局收尾时仍可能需要排空积压 2 秒会把正常收尾误判成卡死
                bool workerDone = true;
                if (worker != null) { try { workerDone = worker.Join(10000); } catch { workerDone = false; } }
                if (stopSucceeded)
                    try { if (traceHandle != 0) CloseTrace(traceHandle); } catch { }
                result.Incomplete = !CaptureComplete(
                    stopSucceeded, workerDone, processTraceSucceeded, consumerExitedEarly);
                started = false;
                // 无论排空成功与否都要交还探针所有权
                //   早先这里直接 return 把 aliveOwned 一路留着
                //   否则一次异常就会让后续每局都被判 观测被占 只能重启进程才恢复
                ReleaseOwnership();
                if (!workerDone)
                {
                    // worker 还在写 dpcHits 这轮数据不能读 但下一轮可以正常重来
                    result.Error = Lang.T("t.interruptattribution.5");
                    return result;
                }
                keepAlive = null;

                // worker 已 Join 原始标记稳定 单线程内解析出逐事件 DPC 时间线
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
                // 归因失败不能掩盖更高优先级的采集不完整/丢失 三种情况都不能提供有效样本
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
                d.CpuMask |= st.CpuMask;
                d.CpuMaskTruncated |= st.CpuMaskTruncated;
                d.BadDuration += st.BadDuration;
                double totalUs = st.TotalTicks * usPerTick;
                double maxUs = st.MaxTicks * usPerTick;
                if (dpc)
                {
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
            if (isr) isrTotal++; else dpcTotal++;

            // 逐事件 DPC 时间线 额外多存一条 极轻 append(不解析模块 模块地址留到 Stop 后再解析)
            //   聚合逻辑上面一行未动 这里只是并行追加 开关关时(默认)整段被首个 bool 短路 零开销
            if (dpc && captureTimeline && !timelineTruncated && dpcMarks != null)
            {
                if (dpcMarks.Count >= DpcTimelineCap) timelineTruncated = true;
                else dpcMarks.Add(new DpcMark
                {
                    // ETW 已判为坏时长时不能再把不可信 start 当成一个可能横跨数秒的
                    // 区间参与长帧对齐 退化成结束时刻的点事件 保留归因但不制造假重叠
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
