// @author bdth 2074055628@qq.com
// File purpose Coalesce process events and cap the game mode full-scan rate
using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private int processSetDirty;
        private int urgentProcessScan;
        private volatile bool armedAwaitingElection;
        private int transitionScanPending;
        private int transitionProbeRendererPid;
        private long transitionProbeRendererCreation;
        private int gameDetectionDirty = 1;
        private int processEventsAvailable;
        private long lastProcessScanTicks;
        private long processScanRetryAfterTicks;
        private long lastFullGameDetectionTicks;
        private long processScanCount;

        private const int PollingSweepIntervalMs = 4000;
        private const int EventBackedSweepIntervalMs = 20000;
        internal const int ActiveGameSweepIntervalMs = 500;
        // Baseline polling during a match when ETW events are present
        //   since 2.0 dropped heat sampling, baseline polling only serves as a fallback for discovering new processes, no need to align with a sampling window anymore
        //   real process-set changes drive the scan via processSetDirty, not this baseline fallback
        //   bench simulated a 600 s match with a new process every 10 s on average, one snapshot costed at the locally measured 2.19ms
        //     500ms fixed polling: 1200 scans, 0.438% of one core, discovery latency avg 241ms worst 490ms
        //     1000ms + dirty: 621 scans, 0.227% of one core, discovery latency avg 86ms worst 460ms
        //     1000ms without dirty: 600 scans, 0.219% of one core, discovery latency avg 517ms worst 990ms
        //   relaxing the poll and wiring up dirty must go together, relaxing alone doubles the latency
        internal const int EventBackedActiveGameSweepIntervalMs = 1000;
        // Minimum interval for process-set-change-driven scans, 500 so the scan rate never exceeds what it was before relaxing
        internal const int DirtyScanFloorMs = 500;
        private const int FullGameDetectionIntervalMs = 20000;
        internal const int GameTransitionScanIntervalMs = 5000;
        internal const int FailedProcessScanRetryMs = 1000;

        public bool ProcessEventsAvailable
        {
            get
            {
                return Interlocked.CompareExchange(
                    ref processEventsAvailable, 0, 0) != 0;
            }
            set
            {
                Interlocked.Exchange(
                    ref processEventsAvailable, value ? 1 : 0);
                RequestFullGameDetection();
                try { kick.Set(); } catch { }
            }
        }

        internal long ProcessScanCount
        {
            get { return Interlocked.Read(ref processScanCount); }
        }

        public void KickGameDetectionNow()
        {
            RequestFullGameDetection();
            try { kick.Set(); } catch { }
        }

        public bool NeedsGameProcessIdentity(string name, int session)
        {
            if (session != selfSession || string.IsNullOrEmpty(name))
                return false;
            lock (sync)
                foreach (GameProfile profile in profiles)
                    if (GameSessionDetector.IsProfileEntryName(profile, name))
                        return true;
            return false;
        }

        private void CountProcessScan()
        {
            Interlocked.Increment(ref processScanCount);
        }

        public void NotifyProcessChanges(ProcessChangeBatch batch)
        {
            if (batch == null || stopping) return;
            ObserveWhitelistProcessChanges(batch);
            ObserveGameFamilyChanges(batch);
            bool relevant = batch.Overflowed;
            bool detectionRelevant = false;
            bool transitionRelevant = false;
            lock (sync)
            {
                relevant |= active || armedAwaitingElection;
                foreach (ProcessChange change in batch.Changes)
                {
                    if (change == null) continue;
                    if (change.Kind == ProcessChangeKind.Stopped)
                    {
                        if (activeDetection != null
                            && activeDetection.RendererPid == change.Pid)
                            detectionRelevant = true;
                        continue;
                    }

                    if (IsActiveFamilyChildStart(
                            activeDetection, change, selfSession))
                    {
                        relevant = true;

                        long rendererCreation =
                            activeDetection.RendererCreation;
                        if (!IsSameTransitionEpoch(
                                transitionProbeRendererPid,
                                transitionProbeRendererCreation,
                                activeDetection.RendererPid,
                                rendererCreation))
                        {
                            transitionProbeRendererPid =
                                activeDetection.RendererPid;
                            transitionProbeRendererCreation =
                                rendererCreation;
                            transitionRelevant = true;
                        }
                    }
                    if (string.IsNullOrEmpty(change.Name)) continue;
                    foreach (GameProfile profile in profiles)
                    {
                        bool profileHit =
                            GameSessionDetector.IsProfileEntryProcess(
                                profile, change.Name, change.Path);
                        if (profileHit)
                        {
                            relevant = true;
                            detectionRelevant = true;
                            break;
                        }
                    }
                }
            }

            if (batch.Overflowed)
                Interlocked.Exchange(ref gameDetectionDirty, 1);
            if (transitionRelevant)
            {
                Interlocked.Exchange(ref gameDetectionDirty, 1);
                Interlocked.Exchange(ref transitionScanPending, 1);
            }
            bool immediate = ProcessEventNeedsImmediateScan(
                detectionRelevant);
            if (immediate)
            {
                Interlocked.Exchange(ref gameDetectionDirty, 1);
                Interlocked.Exchange(ref urgentProcessScan, 1);
            }
            if (!relevant) return;
            Interlocked.Exchange(ref processSetDirty, 1);

            // A process-set change is itself a scan signal, always wake the main loop, whether it actually scans is decided by the DirtyScanDue floor
            kick.Set();
        }

        public bool NeedsLauncherChildParentIdentity(
            int eventParentPid, string eventName, int session)
        {
            if (session != selfSession || eventParentPid <= 0)
                return false;
            lock (sync)
                return ShouldCaptureLauncherParentIdentity(
                    activeDetection, eventParentPid);
        }

        internal static bool ShouldCaptureLauncherParentIdentity(
            GameDetection detection, int eventParentPid)
        {
            return detection != null && eventParentPid > 0
                && detection.RendererPid == eventParentPid
                && detection.RendererCreation > 0;
        }

        internal static bool IsActiveFamilyChildStart(
            GameDetection detection, ProcessChange change, int ownerSession)
        {
            return detection != null && change != null
                && change.Kind == ProcessChangeKind.Started
                && ownerSession >= 0
                && change.Session == ownerSession
                && change.Creation > 0
                && change.ParentPid > 0
                && change.ParentPid == detection.RendererPid
                && detection.RendererCreation > 0
                && change.ParentCreation
                    == detection.RendererCreation
                && change.Creation > detection.RendererCreation;
        }

        internal static bool IsSameTransitionEpoch(
            int storedPid, long storedCreation,
            int currentPid, long currentCreation)
        {
            if (storedPid <= 0 || storedPid != currentPid) return false;

            return storedCreation <= 0 || currentCreation <= 0
                || storedCreation == currentCreation;
        }

        internal static bool ShouldRearmLauncherTransition(
            GameDetection previous, GameDetection next)
        {
            return next != null && (previous == null
                || !RendererHandoffTracker.SameIdentity(previous, next));
        }

        internal static bool ProcessEventNeedsImmediateScan(
            bool gameSessionBoundary)
        {
            return gameSessionBoundary;
        }

        internal static int ProcessScanIntervalMs(bool eventsAvailable)
        {
            return eventsAvailable
                ? EventBackedSweepIntervalMs : PollingSweepIntervalMs;
        }

        internal static int ProcessScanIntervalMs(bool eventsAvailable, bool gameActive)
        {
            if (gameActive)
                return eventsAvailable
                    ? EventBackedActiveGameSweepIntervalMs : ActiveGameSweepIntervalMs;
            return ProcessScanIntervalMs(eventsAvailable);
        }

        // Whether the process-set change qualifies to trigger a scan yet
        //   without an event source processSetDirty is only set by the scan-failure reschedule, and that path sets urgentProcessScan itself
        //   so only the events-present case counts here, behavior without an event source is unchanged
        internal static bool DirtyScanDue(bool eventsAvailable, bool dirty, long elapsedMs)
        {
            if (!eventsAvailable || !dirty) return false;
            return elapsedMs < 0 || elapsedMs >= DirtyScanFloorMs;
        }

        // fallbackOnly means this scan round was triggered purely by the fallback interval, events present and no process-set change signal
        //   snapshots for such rounds may be reused within ReuseMaxAgeMs, see ProcessSnapshotSource.Capture
        private bool ShouldRunProcessScan(out bool fallbackOnly)
        {
            fallbackOnly = false;
            long now = DateTime.UtcNow.Ticks;
            long retryAfter = Interlocked.Read(
                ref processScanRetryAfterTicks);
            if (retryAfter > now) return false;
            if (retryAfter > 0)
                Interlocked.CompareExchange(
                    ref processScanRetryAfterTicks, 0, retryAfter);
            long last = Interlocked.Read(ref lastProcessScanTicks);
            long elapsed = now - last;
            bool urgent = Interlocked.Exchange(ref urgentProcessScan, 0) != 0;
            bool dirty = Interlocked.Exchange(ref processSetDirty, 0) != 0;
            if (urgent)
            {
                Interlocked.Exchange(ref transitionScanPending, 0);
                Interlocked.Exchange(ref lastProcessScanTicks, now);
                return true;
            }
            if (Interlocked.CompareExchange(
                    ref transitionScanPending, 0, 0) != 0
                && (last <= 0 || elapsed < 0
                    || elapsed >= GameTransitionScanIntervalMs
                        * TimeSpan.TicksPerMillisecond))
            {
                Interlocked.Exchange(ref transitionScanPending, 0);
                Interlocked.Exchange(ref lastProcessScanTicks, now);
                return true;
            }
            if (DirtyScanDue(ProcessEventsAvailable, dirty, ElapsedMsOrMax(last, elapsed)))
            {
                Interlocked.Exchange(ref transitionScanPending, 0);
                Interlocked.Exchange(ref lastProcessScanTicks, now);
                return true;
            }
            int fallback = ProcessScanIntervalMs(ProcessEventsAvailable, IsActive);
            if (armedAwaitingElection)
                fallback = Math.Min(fallback, PollingSweepIntervalMs);
            long fallbackTicks = fallback * TimeSpan.TicksPerMillisecond;
            if (last <= 0 || elapsed < 0 || elapsed >= fallbackTicks)
            {
                // While awaiting election the tightened fallback interval is used, election needs fresh data then, so it does not count as pure fallback
                fallbackOnly = !armedAwaitingElection;
                Interlocked.Exchange(ref transitionScanPending, 0);
                Interlocked.Exchange(ref lastProcessScanTicks, now);
                return true;
            }
            if (dirty) Interlocked.Exchange(ref processSetDirty, 1);
            return false;
        }

        private int ProcessScanWaitMs()
        {
            long now = DateTime.UtcNow.Ticks;
            long retryAfter = Interlocked.Read(
                ref processScanRetryAfterTicks);
            if (retryAfter > now)
                return TicksToWaitMilliseconds(retryAfter - now);
            if (Interlocked.CompareExchange(
                    ref urgentProcessScan, 0, 0) != 0)
                return 1;
            int interval = ProcessScanIntervalMs(ProcessEventsAvailable, IsActive);
            if (armedAwaitingElection)
                interval = Math.Min(interval, PollingSweepIntervalMs);
            long last = Interlocked.Read(ref lastProcessScanTicks);
            if (last <= 0 || now < last) return 1;
            long elapsedMs = (now - last) / TimeSpan.TicksPerMillisecond;
            long remaining = interval - elapsedMs;
            if (Interlocked.CompareExchange(
                    ref transitionScanPending, 0, 0) != 0)
                remaining = Math.Min(
                    remaining,
                    GameTransitionScanIntervalMs - elapsedMs);
            if (ProcessEventsAvailable
                && Interlocked.CompareExchange(ref processSetDirty, 0, 0) != 0)
                remaining = Math.Min(remaining, DirtyScanFloorMs - elapsedMs);
            if (remaining <= 0) return 1;
            return (int)Math.Min(interval, remaining);
        }

        private static long ElapsedMsOrMax(long last, long elapsedTicks)
        {
            if (last <= 0 || elapsedTicks < 0) return long.MaxValue;
            return elapsedTicks / TimeSpan.TicksPerMillisecond;
        }

        private static int TicksToWaitMilliseconds(long ticks)
        {
            if (ticks <= 0) return 1;
            long milliseconds =
                (ticks + TimeSpan.TicksPerMillisecond - 1)
                / TimeSpan.TicksPerMillisecond;
            return (int)Math.Min(int.MaxValue, Math.Max(1L, milliseconds));
        }

        private void RequeueProcessScanAfterFailure()
        {
            Interlocked.Exchange(ref urgentProcessScan, 1);
            Interlocked.Exchange(ref processSetDirty, 1);
            Interlocked.Exchange(
                ref processScanRetryAfterTicks,
                DateTime.UtcNow.AddMilliseconds(
                    FailedProcessScanRetryMs).Ticks);
        }

        private bool ShouldRunFullGameDetection()
        {
            long now = DateTime.UtcNow.Ticks;
            long last = Interlocked.Read(ref lastFullGameDetectionTicks);
            int interval = ProcessEventsAvailable
                ? FullGameDetectionIntervalMs
                : PollingSweepIntervalMs;
            if (armedAwaitingElection)
                interval = PollingSweepIntervalMs;
            bool due = last <= 0 || now < last
                || now - last >= interval * TimeSpan.TicksPerMillisecond;
            if (Interlocked.Exchange(ref gameDetectionDirty, 0) != 0 || due)
            {
                Interlocked.Exchange(ref lastFullGameDetectionTicks, now);
                return true;
            }
            return false;
        }

        private void RequestFullGameDetection()
        {
            Interlocked.Exchange(ref gameDetectionDirty, 1);
            Interlocked.Exchange(ref urgentProcessScan, 1);
            Interlocked.Exchange(ref processSetDirty, 1);
        }

        private void RequestPolicyApply()
        {

            Interlocked.Exchange(ref gameDetectionDirty, 1);
            Interlocked.Exchange(ref urgentProcessScan, 1);
            Interlocked.Exchange(ref processSetDirty, 1);
            try { kick.Set(); } catch { }
        }
    }
}
