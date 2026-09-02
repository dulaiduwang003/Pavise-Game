// @author bdth 2074055628@qq.com
// 文件用途 中断采样的执行 提交与有效期校验
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed partial class IrqSessionProbe : IDisposable
    {
        private string Run(bool commit)
        {
            var loadRecord = new IrqSessionRecord();
            bool enabled = platform.Enabled;
            IIrqSessionCapture ia = null;
            IIrqSessionCapture stale = null;
            long began = 0;
            long ended = 0;
            long epoch = 0;
            string game = "";
            string boot = "";
            string topology = "";
            ulong mask = 0;
            ulong available = 0;
            int pid = 0;
            long creation = 0;
            lock (gate)
            {
                if (live == null || completed)
                {
                    if (commit && armed && pendingRecord == null)
                    {
                        armed = false;
                        SetStatusLocked(enabled ? "unavailable" : "disabled", "", 0);
                    }
                    return commit ? CommitPendingLocked() : pendingSummary;
                }
                long now = platform.UtcTicks;
                bool proofFresh = lastProofTicks > 0 && now >= lastProofTicks
                    && now - lastProofTicks <= MaxProofAgeTicks;
                if (disposed || !enabled || !proofFresh)
                {
                    stale = InvalidateLocked();
                    SetStatusLocked(enabled ? "invalidated" : "disabled", "", 0);
                }
                else
                {
                    completed = true;
                    armed = false;
                    sealedPending = !commit;
                    ia = live;
                    live = null;
                    stopInProgress = true;
                    began = startTicks;
                    ended = now;
                    // 在 ETW Stop 还原和退出宽限期之前 先把 CPU 增量冻住
                    if (coreLoads != null) coreLoads.Finish(now, loadRecord);
                    coreLoads = null;
                    epoch = generation;
                    game = gameName;
                    boot = bootStamp;
                    topology = topologyStamp;
                    mask = gameMask;
                    available = systemMask;
                    pid = rendererPid;
                    creation = rendererCreation;
                }
            }
            if (stale != null) { StopAndDiscard(stale); return null; }

            InterruptAttributionResult raw;
            try { raw = ia.Stop(); }
            catch
            {
                lock (gate)
                {
                    stopInProgress = false;
                    if (generation == epoch) SetStatusLocked("incomplete", "", 0);
                }
                return null;
            }
            lock (gate) stopInProgress = false;
            System.Collections.Generic.List<InterruptAttribution.DpcTimelineEntry> timeline = null;
            bool timelineTruncated = false;
            try
            {
                timeline = ia.DpcTimeline;
                // 到达内存上限或 ETW 自身丢事件 零命中都不能作为可靠的负证据
                timelineTruncated = ia.DpcTimelineTruncated
                    || (raw != null && (raw.Lossy || raw.Incomplete));
            }
            catch { }

            lock (gate)
            {
                if (!CaptureStillValidLocked(epoch, mask, available, pid, creation)) return null;
                pendingTimeline = timeline;
                pendingTimelineTruncated = timelineTruncated;
            }
            if (raw == null || raw.Lossy || raw.Incomplete || raw.Drivers.Count == 0)
            {
                lock (gate)
                {
                    if (!CaptureStillValidLocked(epoch, mask, available, pid, creation)) return null;
                    if (raw != null && raw.Lossy)
                        SetStatusLocked("lost", "events=" + raw.EventsLost
                            + ", buffers=" + raw.BuffersLost, 0);
                    else if (raw != null && raw.Incomplete)
                        SetStatusLocked("incomplete", "", 0);
                    else SetStatusLocked("nodata", raw == null ? "" : raw.Error, 0);
                }
                return null;
            }

            var rec = new IrqSessionRecord();
            rec.StartUtcTicks = began;
            rec.DurationSeconds = CaptureDurationSeconds(began, ended);
            rec.GameName = game;
            rec.BootStamp = boot;
            rec.TopologyStamp = topology;
            rec.GameMask = mask;
            rec.SystemMask = available;
            rec.CoreLoadWindowTicks = loadRecord.CoreLoadWindowTicks;
            rec.CoreLoads.AddRange(loadRecord.CoreLoads);
            Func<string, string> driverVersion = null;
            try { driverVersion = platform.DriverVersionReader(); } catch { }
            foreach (DriverInterrupt d in raw.Drivers)
            {
                if (d == null || d.Dpc <= 0) continue;
                var r = new IrqDriverRecord();
                r.Driver = d.Driver ?? "?";
                r.DriverVersion = DriverFileVersion(r.Driver, driverVersion);
                r.Buckets = d.DpcBuckets;
                r.Dpc = d.Dpc;
                r.DpcTotalNs = (long)(d.DpcTotalUs * 1000.0);
                r.DpcMaxNs = (long)(d.DpcMaxUs * 1000.0);
                r.Over500Us = d.DpcOver500Us;
                r.Over1Ms = d.DpcOver1Ms;
                r.CpuMask = d.CpuMask;
                r.MaskTruncated = d.CpuMaskTruncated;
                rec.Drivers.Add(r);
            }
            if (rec.Drivers.Count == 0)
            {
                lock (gate)
                    if (CaptureStillValidLocked(epoch, mask, available, pid, creation))
                        SetStatusLocked("nodata", raw.Error, 0);
                return null;
            }
            string summary = IrqVerdict.SummarizeSession(rec);
            lock (gate)
            {
                // Stop/汇总期间若发生新一局 禁用或失配 旧 epoch 绝不能留下
                if (!CaptureStillValidLocked(epoch, mask, available, pid, creation)) return null;
                pendingRecord = rec;
                pendingSummary = summary;
                return commit ? CommitPendingLocked() : summary;
            }
        }

        // gate 内调用 Append 自己有独立文件锁 这里持有小范围状态锁
        // 保证 Arm/Invalidate 无法在“已判有效”和“落盘”之间插入新一局
        private string CommitPendingLocked()
        {
            if (pendingRecord == null)
            {
                sealedPending = false;
                return pendingSummary;
            }
            bool enabled = platform.Enabled;
            bool saved = false;
            if (enabled)
                try { saved = platform.Append(pendingRecord); } catch { }
            if (!saved)
            {
                sealedPending = false;
                pendingRecord = null;
                pendingTimeline = null;
                pendingTimelineTruncated = false;
                pendingSummary = null;
                SetStatusLocked(enabled ? "savefailed" : "disabled", "", 0);
                return null;
            }
            SetStatusLocked(pendingRecord.GameMask == 0 ? "saved.system" : "saved.placed",
                "", pendingRecord.DurationSeconds);
            sealedPending = false;
            pendingRecord = null;
            return pendingSummary;
        }

        internal static int CaptureDurationSeconds(long began, long ended)
        {
            if (began <= 0 || ended <= began) return 0;
            long seconds = (ended - began) / TimeSpan.TicksPerSecond;
            return seconds >= int.MaxValue ? int.MaxValue : (int)seconds;
        }

        private bool CaptureStillValidLocked(
            long epoch, ulong mask, ulong available,
            int pid, long creation)
        {
            return generation == epoch
                && !gameMaskInvalid
                && (systemObservation
                    ? mask == 0 && CanObserveSystem(available, pid, creation)
                    : CanConfirmMask(mask, available, pid, creation))
                && gameMask == mask
                && systemMask == available
                && rendererPid == pid
                && rendererCreation == creation;
        }
    }
}
