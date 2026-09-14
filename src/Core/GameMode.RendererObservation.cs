// File purpose The badge records observed activity, not selection state, nor the mere existence of a fullscreen window
// Sampling is bounded, off the UI and control loops, and shares one gate with the handoff GPU work
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private RendererObservationStore rendererObservations;
        private int rendererValidationBusy;
        private int rendererActivityBusy;
        private int rendererGpuSamplingBusy;
        private long rendererActivityNextMs;
        private GameDetection rendererActivityTarget;
        private int rendererActivityAttempts;
        private int rendererActivityEpoch;
        private long rendererActivityWaitLogMs;
        private long rendererActivityBudgetNextMs;
        private readonly object rendererObservationTraceGate = new object();
        private readonly Queue<string> rendererObservationTraceLines = new Queue<string>();
        private int rendererObservationTraceBusy;

#if PAVISE_SELFTEST
        internal Func<GameDetection, Func<bool>, IDictionary<int, double>> RendererTestActivityGpu;
        internal Func<long> RendererTestObservationNow;
#endif

        private void InitializeRendererObservations()
        {
            rendererObservations = new RendererObservationStore(dataDir);
        }

        public bool HasRendererObservation(GameProfile profile)
        {
            RendererObservationStore store = rendererObservations;
            if (store == null || profile == null) return false;
            if (!stopping && store.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
                QueueRendererObservationValidation();
            return store.Has(profile.Id, profile.ExecutablePath);
        }

        private void QueueRendererObservationValidation()
        {
            if (stopping || Interlocked.CompareExchange(ref rendererValidationBusy, 1, 0) != 0) return;
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    bool changed = false;
                    try
                    {
                        if (stopping) return;
                        foreach (GameProfile profile in GetProfiles())
                        {
                            if (stopping) break;
                            if (rendererObservations.NeedsValidation(profile.Id, profile.ExecutablePath, DateTime.UtcNow.Ticks))
                                changed |= rendererObservations.Validate(profile.Id, profile.ExecutablePath);
                        }
                    }
                    finally
                    {
                        try
                        {
                            if (changed && !stopping) { rendererObservations.Persist(); RaiseLibraryChanged(); }
                        }
                        finally { Interlocked.Exchange(ref rendererValidationBusy, 0); }
                    }
                });
                if (!queued) Interlocked.Exchange(ref rendererValidationBusy, 0);
            }
            catch { Interlocked.Exchange(ref rendererValidationBusy, 0); }
        }

        private void ForgetRendererObservation(string profileId)
        {
            if (stopping || rendererObservations == null) return;
            rendererObservations.Forget(profileId);
            // This is optional history only, not a synchronous transaction for the UI or profile store
            try
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    if (!stopping) rendererObservations.Persist();
                });
            }
            catch { }
        }

        private void MaybeObserveRendererActivity(GameDetection selected)
        {
            if (rendererObservations == null || selected == null || selected.Profile == null
                || !selected.RendererCandidateSelected || selected.RendererSafetyOnly
                || selected.RequiresGpuConfirm || !enabled || stopping || panicReq || ProfileStoreSaveFailed) return;
            int foreground = RendererForegroundPid();
            // Background only consults the in-memory cache, keeping the original overhead bound of queuing EXE verification only in the foreground
            if (foreground == selected.RendererPid ? HasRendererObservation(selected.Profile)
                : rendererObservations.Has(selected.Profile.Id, selected.Profile.ExecutablePath)) return;
            long now = RendererNowMs();
#if PAVISE_SELFTEST
            if (RendererTestObservationNow != null) now = RendererTestObservationNow();
#endif
            int epoch = Volatile.Read(ref rendererHandoffEpoch);
            if (!RendererHandoffTracker.SameIdentity(rendererActivityTarget, selected)
                || rendererActivityTarget.Profile == null
                || !string.Equals(rendererActivityTarget.Profile.Id, selected.Profile.Id, StringComparison.OrdinalIgnoreCase)
                || rendererActivityEpoch != epoch)
            {
                rendererActivityTarget = RendererHandoffTracker.Copy(selected);
                rendererActivityAttempts = 0;
                // Wait time belongs to one target observation, must not carry over from an old game or session to a new target
                rendererActivityNextMs = 0;
                rendererActivityEpoch = epoch;
            }
            if (foreground != selected.RendererPid)
            {
                TraceRendererObservationWait(selected, now, "ForegroundWaiting", "foreground=" + foreground);
                return;
            }
            // A new target does not inherit the old target's long backoff, but all targets still share the once-per-10-seconds overhead cap
            if (now < rendererActivityNextMs || now < rendererActivityBudgetNextMs) return;
            if (Handoff.HasProbe)
            {
                TraceRendererObservationWait(selected, now, "SamplingBusy", "owner=handoff");
                return;
            }
            if (Interlocked.CompareExchange(ref rendererActivityBusy, 1, 0) != 0) return;
            if (Interlocked.CompareExchange(ref rendererGpuSamplingBusy, 1, 0) != 0)
            {
                Interlocked.Exchange(ref rendererActivityBusy, 0);
                TraceRendererObservationWait(selected, now, "SamplingBusy", "owner=gpu");
                return;
            }
            rendererActivityAttempts = Math.Min(rendererActivityAttempts + 1, 5);
            rendererActivityBudgetNextMs = now + 10000L;
            rendererActivityNextMs = now + (rendererActivityAttempts <= 2 ? 10000L
                : rendererActivityAttempts <= 3 ? 30000L : rendererActivityAttempts <= 4 ? 60000L : 120000L);
            GameDetection target = RendererHandoffTracker.Copy(selected);
            TraceRendererObservation(target, "Started", "attempt=" + rendererActivityAttempts
                + " retryMs=" + (rendererActivityNextMs - now) + " exe=" + target.RendererPath);
            try
            {
                bool queued = ThreadPool.QueueUserWorkItem(delegate
                {
                    string outcome = "Canceled", detail = null;
                    try
                    {
                        Func<bool> canceled = delegate { return !RendererEpochCurrent(epoch); };
                        if (canceled()) return;
                        if (!VerifyRendererCandidate(target)) { outcome = "IdentityUnavailable"; detail = "phase=before"; return; }
                        RendererFileStamp stamp = RendererFileStamp.Read(target.RendererPath);
                        if (stamp == null) { outcome = "FileUnavailable"; return; }
                        IDictionary<int, double> values;
                        GpuSampleDiagnostics diagnostics = null;
#if PAVISE_SELFTEST
                        if (RendererTestActivityGpu != null) values = RendererTestActivityGpu(target, canceled);
                        else
#endif
                            values = GpuEvidence.Sample3D(GpuEvidence.BurstRounds, GpuEvidence.BurstIntervalMs, canceled,
                                target.RendererPid, out diagnostics);
                        detail = diagnostics == null ? null : diagnostics.ToString();
                        if (canceled()) return;
                        if (values == null) { outcome = "GpuUnavailable"; return; }
                        detail = "pidCount=" + values.Count + " " + detail;
                        double utilization;
                        // No sorting by name, the badge describes the 3D workload actually measured on this process
                        // not a claim that it is the only or the main game renderer process
                        if (!values.TryGetValue(target.RendererPid, out utilization)) { outcome = "PidMissing"; return; }
                        // Round-trip format on purpose, the threshold test sits right at 10.0, values hugging the threshold must show their difference from it
                        //   F1 would print gpu3d=10.0% minimum=10.0% yet rule BelowThreshold, a self-contradicting log
                        //   the cost is seventeen decimal places far from the threshold, see the NearThreshold case in SelfTests.FamilySuppression
                        detail = "gpu3d=" + utilization.ToString("R", CultureInfo.InvariantCulture) + "% " + detail;
                        int currentForeground = RendererForegroundPid();
                        if (currentForeground != target.RendererPid)
                        { outcome = "ForegroundChanged"; detail += " foreground=" + currentForeground; return; }
                        if (!VerifyRendererCandidate(target)) { outcome = "IdentityUnavailable"; detail += " phase=after"; return; }
                        if (double.IsNaN(utilization) || double.IsInfinity(utilization)) { outcome = "InvalidEvidence"; return; }
                        if (utilization < GpuEvidence.MinElectUtilization)
                        {
                            outcome = "BelowThreshold";
                            detail += " minimum=" + GpuEvidence.MinElectUtilization.ToString(CultureInfo.InvariantCulture) + "%";
                            return;
                        }
                        RendererObservationWriteResult result;
                        lock (sync)
                        {
                            GameProfile live = FindProfileLocked(target.Profile.Id);
                            if (!RendererEpochCurrent(epoch)) return;
                            if (live == null || !string.Equals(live.ExecutablePath, target.RendererPath, StringComparison.OrdinalIgnoreCase))
                            { outcome = "ProfileChanged"; return; }
                            if (activeDetection == null || !RendererHandoffTracker.SameIdentity(activeDetection, target))
                            { outcome = "ActiveChanged"; return; }
                            if (!VerifyRendererCandidate(target)) { outcome = "IdentityUnavailable"; detail += " phase=commit"; return; }
                            result = rendererObservations.RecordActivityWithResult(live.Id, target.RendererPath,
                                RendererObservationEvidence.Gpu3D, utilization, stamp);
                        }
                        outcome = result.ToString();
                        if (result == RendererObservationWriteResult.Recorded) RaiseLibraryChanged();
                    }
                    catch (Exception error) { outcome = "Error"; detail = error.GetType().Name; }
                    finally
                    {
                        TraceRendererObservation(target, outcome, detail);
                        Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                        Interlocked.Exchange(ref rendererActivityBusy, 0);
                    }
                });
                if (!queued)
                {
                    TraceRendererObservation(target, "QueueFailed", null);
                    Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                    Interlocked.Exchange(ref rendererActivityBusy, 0);
                }
            }
            catch (Exception error)
            {
                TraceRendererObservation(target, "QueueFailed", error.GetType().Name);
                Interlocked.Exchange(ref rendererGpuSamplingBusy, 0);
                Interlocked.Exchange(ref rendererActivityBusy, 0);
            }
        }

        private void TraceRendererObservationWait(GameDetection target, long now, string reason, string detail)
        {
            // The main loop may pass through several times a second, the waiting state is logged at most once per 30 seconds
            if (now < rendererActivityWaitLogMs) return;
            rendererActivityWaitLogMs = now + 30000L;
            TraceRendererObservation(target, reason, detail);
        }

        private void TraceRendererObservation(GameDetection target, string reason, string detail)
        {
            try
            {
                string line = SeverityTagFor(reason)
                    + Lang.F("lib.render.observation.trace", target.RendererName, target.RendererPid,
                    Lang.T("lib.render.observation." + reason)) + " [reason=" + reason + "]"
                    + " profile=" + target.Profile.Id + (string.IsNullOrEmpty(detail) ? "" : " " + detail);
                // When disk or the global log lock slows down, neither block the detection loop nor hold the GPU sampling gate waiting on the log
                // At most one log task per GameMode, eight pending messages, no resident thread
                lock (rendererObservationTraceGate)
                {
                    if (rendererObservationTraceLines.Count >= 8) rendererObservationTraceLines.Dequeue();
                    rendererObservationTraceLines.Enqueue(line);
                    if (rendererObservationTraceBusy != 0) return;
                    rendererObservationTraceBusy = 1;
                    try
                    {
                        if (ThreadPool.QueueUserWorkItem(delegate { DrainRendererObservationTrace(); })) return;
                    }
                    catch { }
                    rendererObservationTraceBusy = 0;
                    rendererObservationTraceLines.Clear();
                }
            }
            catch { } // log unavailable must not affect sampling gate release or the game
        }

        // This log line always carries field names like lastFailure= and code=, substring matching in the word table would classify lastFailure=none as an error
        //   so every renderer observation got logged as an error regardless of outcome, hence the level is declared per result here
        //   FAIL only for real faults, no evidence counts as an environment limit and goes WARN, everything else is normal timing or a valid conclusion
        internal static string SeverityTagFor(string reason)
        {
            switch (reason)
            {
                case "Error":
                case "QueueFailed":
                case "SaveFailed":
                    return Logger.FailTag;
                case "GpuUnavailable":
                case "FileUnavailable":
                case "InvalidEvidence":
                case "FileChanged":
                    return Logger.WarnTag;
                default:
                    // Started Recorded Canceled Closed PidMissing BelowThreshold
                    // IdentityUnavailable ActiveChanged ProfileChanged ForegroundChanged
                    // ForegroundWaiting and SamplingBusy are normal flow or valid conclusions
                    return Logger.InfoTag;
            }
        }

        private void DrainRendererObservationTrace()
        {
            while (true)
            {
                string line;
                lock (rendererObservationTraceGate)
                {
                    if (rendererObservationTraceLines.Count == 0)
                    {
                        rendererObservationTraceBusy = 0;
                        return;
                    }
                    line = rendererObservationTraceLines.Dequeue();
                }
                Logger.Log(line);
            }
        }

    }
}
