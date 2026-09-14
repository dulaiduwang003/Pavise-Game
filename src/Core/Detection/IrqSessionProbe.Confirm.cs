// @author bdth 2074055628@qq.com
// File purpose Interrupt sampling confirmation, invalidation, and the external mutation window
using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal sealed partial class IrqSessionProbe : IDisposable
    {
        internal static bool CanConfirmMask(
            ulong verifiedMask, ulong availableSystemMask,
            int verifiedRendererPid, long verifiedRendererCreation)
        {
            return CanConfirmMaskShape(verifiedMask, availableSystemMask)
                && verifiedRendererPid > 0
                && verifiedRendererCreation > 0;
        }

        internal static bool CanConfirmMaskShape(
            ulong verifiedMask, ulong availableSystemMask)
        {
            return verifiedMask != 0
                && availableSystemMask != 0
                && verifiedMask != availableSystemMask
                && (verifiedMask & ~availableSystemMask) == 0;
        }

        public bool ConfirmGameMask(
            ulong verifiedMask, int verifiedRendererPid,
            long verifiedRendererCreation)
        {
            return ConfirmCapture(verifiedMask, verifiedRendererPid, verifiedRendererCreation, false);
        }

        public bool ConfirmSystemObservation(int verifiedRendererPid, long verifiedRendererCreation)
        {
            // 0 means system interrupts are only observed, with no proof of the game's actual core domain
            return ConfirmCapture(0, verifiedRendererPid, verifiedRendererCreation, true);
        }

        internal static bool CanObserveSystem(ulong availableSystemMask, int pid, long creation)
        {
            return availableSystemMask != 0 && pid > 0 && creation > 0;
        }

        private bool ConfirmCapture(
            ulong verifiedMask, int verifiedRendererPid,
            long verifiedRendererCreation, bool observeSystem)
        {
            bool enabled = platform.Enabled;
            bool accepted = false;
            bool logStarted = false;
            bool logStartFailed = false;
            bool logNoAdmin = false;
            IIrqSessionCapture discard = null;
            IIrqSessionCapture failedStart = null;
            lock (gate)
            {
                if (disposed || !armed || gameMaskInvalid || systemObservation != observeSystem) return false;
                // If async tuning like RenderLane is mid-write on the renderer, capture start must
                // wait until it leaves the write region; this counter and capture start sit under the same gate, so there is no
                // window where the callback just checked, ETW started, and only then the setter landed - that is the window being blocked
                if (!observeSystem && externalMutations > 0) return false;
                bool identityValid = observeSystem
                    ? verifiedMask == 0 && CanObserveSystem(systemMask,
                        verifiedRendererPid, verifiedRendererCreation)
                    : CanConfirmMask(verifiedMask, systemMask,
                        verifiedRendererPid, verifiedRendererCreation);
                if (!enabled || !identityValid)
                {
                    discard = InvalidateLocked();
                    SetStatusLocked(enabled ? "unavailable" : "disabled", "", 0);
                }
                else if (live != null)
                {
                    if (!completed
                        && gameMask == verifiedMask
                        && rendererPid == verifiedRendererPid
                        && rendererCreation == verifiedRendererCreation)
                    {
                        long now = platform.UtcTicks;
                        bool continuous = lastProofTicks > 0
                            && now >= lastProofTicks
                            && now - lastProofTicks <= MaxProofAgeTicks;
                        if (continuous)
                        {
                            lastProofTicks = now;
                            if (coreLoads != null) coreLoads.Poll(now);
                            placementWaitScans = 0;
                            accepted = true;
                        }
                        else
                        {
                            // A gap longer than the proof freshness cannot be proven after the fact
                            // Discard the old ETW but keep this match armed; next round reopens a clean epoch
                            // from the currently verified core placement
                            string resumeGame = gameName;
                            ulong resumeSystem = systemMask;
                            discard = InvalidateLocked();
                            gameName = resumeGame;
                            systemMask = resumeSystem;
                            systemObservation = observeSystem;
                            gameMaskInvalid = false;
                            armed = enabled && resumeSystem != 0;
                            completed = true;
                            SetStatusLocked("waiting", "", 0);
                        }
                    }
                    else
                    {
                        discard = InvalidateLocked();
                        SetStatusLocked("invalidated", "", 0);
                    }
                }
                else if (!platform.IsElevated)
                {
                    if (!warnedNoAdmin) { warnedNoAdmin = true; logNoAdmin = true; }
                    discard = InvalidateLocked();
                    SetStatusLocked("needadmin", "", 0);
                }
                else
                {
                    deviceConfigurations = new Dictionary<string,string>();
                    var devicePlatform = platform as IIrqDeviceSnapshotPlatform;
                    if (devicePlatform != null)
                        try { deviceConfigurations = devicePlatform.DeviceConfigurations(); } catch { }
                    IIrqSessionCapture ia = platform.CreateCapture(!observeSystem);
                    bool started = false;
                    try { started = ia.Start(); } catch { }
                    if (!started)
                    {
                        failedStart = ia;
                        logStartFailed = !ia.Busy;
                        discard = InvalidateLocked();
                        SetStatusLocked("startfailed", ia.Busy
                            ? Lang.T("irq.probe.busy") : ia.FailDetail, 0);
                    }
                    else
                    {
                        live = ia;
                        gameMask = verifiedMask;
                        rendererPid = verifiedRendererPid;
                        rendererCreation = verifiedRendererCreation;
                        startTicks = platform.UtcTicks;
                        captureStartQpc = System.Diagnostics.Stopwatch.GetTimestamp();
                        captureEndQpc = 0;
                        lastProofTicks = startTicks;
                        bootStamp = platform.BootStamp;
                        topologyStamp = platform.TopologyStamp;
                        try { coreLoads = new IrqCoreLoadCapture(platform.OpenCoreLoadSource(), startTicks, systemMask); }
                        catch { coreLoads = null; }
                        completed = false;
                        placementWaitScans = 0;
                        accepted = true;
                        logStarted = true;
                        SetStatusLocked(observeSystem ? "system" : "placed", "", 0);
                    }
                }
            }
            StopAndDiscard(discard);
            StopAndDiscard(failedStart);
            if (logNoAdmin) platform.Log(Logger.WarnTag + Lang.T("log.irqsession.4"));
            if (logStartFailed) platform.Log(Logger.WarnTag + Lang.T("log.irqsession.1"));
            if (logStarted) platform.Log(Lang.T("log.irqsession.2"));
            return accepted;
        }

        public void InvalidateGameMask()
        {
            IIrqSessionCapture discard;
            lock (gate)
            {
                bool hadCapture = armed || live != null || pendingRecord != null;
                discard = InvalidateLocked();
                if (hadCapture) SetStatusLocked(platform.Enabled ? "invalidated" : "disabled", "", 0);
            }
            StopAndDiscard(discard);
        }

        // When this match's tuning state needs rewriting, discard live but keep armed
        // Caller must wait for this method to return, old ETW stopped, before writing
        // After the write, the next Confirm starts capture from the new proof point
        public void RestartCurrentEpoch()
        {
            IIrqSessionCapture discard = null;
            lock (gate)
            {
                if (disposed || !armed || gameMaskInvalid
                    || live == null || completed) return;
                string resumeGame = gameName;
                ulong resumeSystem = systemMask;
                discard = InvalidateLocked();
                gameName = resumeGame;
                systemMask = resumeSystem;
                gameMaskInvalid = false;
                armed = platform.Enabled && resumeSystem != 0;
                completed = true;
                SetStatusLocked("waiting", "", 0);
            }
            StopAndDiscard(discard);
        }

        // For independent workers like RenderLane to mark before and after the real setter
        // If capturing, invalidate the old epoch first and keep this match armed; after the write ends
        // the next round can start from a new proof, so Pavise's own writes are not counted into the match
        public void BeginExternalMutation()
        {
            IIrqSessionCapture discard = null;
            lock (gate)
            {
                if (disposed) return;
                externalMutations++;
                // System observation records real whole-machine DPC, which inherently includes normal background activity
                // and claims no game-core attribution, so writes like new-process suppression should not keep shattering the whole match
                // Strict core-domain evidence must still exclude observation pollution caused by those writes
                if (!systemObservation && (stopInProgress
                    || (armed && !gameMaskInvalid && live != null && !completed)))
                {
                    string resumeGame = gameName;
                    ulong resumeSystem = systemMask;
                    discard = InvalidateLocked();
                    gameName = resumeGame;
                    systemMask = resumeSystem;
                    gameMaskInvalid = false;
                    armed = platform.Enabled && resumeSystem != 0;
                    completed = true;
                    SetStatusLocked("waiting", "", 0);
                }
            }
            StopAndDiscard(discard);
        }

        public void EndExternalMutation()
        {
            lock (gate)
                if (externalMutations > 0) externalMutations--;
        }

        private IIrqSessionCapture InvalidateLocked()
        {
            if (coreLoads != null) coreLoads.Dispose();
            coreLoads = null;
            generation++;
            IIrqSessionCapture discard = live;
            live = null;
            startTicks = 0;
            captureStartQpc = captureEndQpc = 0;
            lastProofTicks = 0;
            gameMask = 0;
            systemMask = 0;
            rendererPid = 0;
            rendererCreation = 0;
            bootStamp = "";
            topologyStamp = "";
            armed = false;
            gameMaskInvalid = true;
            completed = true;
            sealedPending = false;
            pendingRecord = null;
            pendingSummary = null;
            pendingTimeline = null;
            pendingTimelineTruncated = false;
            return discard;
        }

        private static void StopAndDiscard(IIrqSessionCapture ia)
        {
            if (ia == null) return;
            try { ia.Stop(); } catch { }
        }

        public string TakeSummary()
        {
            lock (takeGate)
            {
                Run(true);
                lock (gate)
                {
                    string held = pendingSummary;
                    pendingSummary = null;
                    return held;
                }
            }
        }

        // Seal immediately the first time the game is seen gone, so system DPC during the exit grace period does not leak in
        // Sealing does not consume pending; ReportFinish 8 seconds later can still take the summary and timeline as usual
        public void Seal()
        {
            lock (takeGate) Run(false);
        }

        // Take this match's per-event DPC timeline; taking clears it; null if not captured or not admin, caller falls back gracefully
        //   Same source as TakeSummary; calling after Run() has completed is idempotent, since Run returns without touching pending once completed
        public System.Collections.Generic.List<InterruptAttribution.DpcTimelineEntry> TakeDpcTimeline(out bool truncated)
        { return TakeDpcTimeline(out truncated, true); }

        internal List<InterruptAttribution.DpcTimelineEntry> TakeDpcTimeline(out bool truncated, bool commit)
        {
            lock (takeGate)
            {
                Run(commit);
                lock (gate)
                {
                    var held = pendingTimeline;
                    truncated = pendingTimelineTruncated;
                    pendingTimeline = null;
                    pendingTimelineTruncated = false;
                    return held;
                }
            }
        }
    }
}
