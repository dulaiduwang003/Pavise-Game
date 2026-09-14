// @author bdth 2074055628@qq.com
// File purpose Interrupt sampling execution, commit, and validity checks
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
                    captureEndQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                    // Freeze the CPU deltas before the ETW Stop restore and the exit grace period
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
                // Hitting the memory cap or ETW itself dropping events means zero hits cannot serve as reliable negative evidence
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
            rec.EventsLost = raw.EventsLost + (long)raw.BuffersLost;
            rec.Unmapped = raw.Unmapped;
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
                r.Cores.AddRange(d.Cores);
                if (r.Cores.Count > 0 && !r.MaskTruncated)
                {
                    // Use one integer conversion path for totals and per-core validation
                    r.DpcTotalNs = 0; r.DpcMaxNs = 0;
                    foreach (var core in r.Cores)
                    { r.DpcTotalNs += core.TotalNs; r.DpcMaxNs = Math.Max(r.DpcMaxNs, core.MaxNs); }
                }
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
                // If a new match, disable, or mismatch happens during Stop/summary, the old epoch must never survive
                if (!CaptureStillValidLocked(epoch, mask, available, pid, creation)) return null;
                rec.GameId = stableGameId; rec.Configuration = configuration;
                var endDevicePlatform = platform as IIrqDeviceSnapshotPlatform;
                if (endDevicePlatform != null)
                    try
                    {
                        var endDevices = endDevicePlatform.DeviceConfigurations();
                        foreach (var pair in deviceConfigurations)
                        {
                            string value;
                            if (endDevices.TryGetValue(pair.Key,out value) && value == pair.Value)
                                rec.DeviceConfigurations[pair.Key] = pair.Value;
                        }
                    }
                    catch { }
                rec.Frames = frameEvidence;
                pendingRecord = rec;
                pendingSummary = summary;
                return commit ? CommitPendingLocked() : summary;
            }
        }

        // Called inside gate; Append has its own file lock, this holds a narrow state lock
        // so Arm and Invalidate cannot slip in between the validity verdict and the disk write, and no new match can be wedged in
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
