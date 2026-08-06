// @author bdth 2074055628@qq.com
// 文件用途 合并进程事件并限制游戏模式全量扫描频率

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
            bool relevant = batch.Overflowed;
            bool detectionRelevant = false;
            bool transitionRelevant = false;
            lock (sync)
            {
                relevant |= active;
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

            if (immediate || transitionRelevant) kick.Set();
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
                && detection.RendererCreation > 0
                && GameSessionDetector.IsLauncherLikeName(
                    detection.RendererName);
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
                && change.Creation
                    > detection.RendererCreation
                && GameSessionDetector.IsLauncherLikeName(
                    detection.RendererName);
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
            return next != null
                && GameSessionDetector.IsLauncherLikeName(
                    next.RendererName)
                && (previous == null
                    || !GameSessionDetector.IsLauncherLikeName(
                        previous.RendererName));
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

        private bool ShouldRunProcessScan()
        {
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
            int fallback = ProcessScanIntervalMs(ProcessEventsAvailable);
            if (armedAwaitingElection)
                fallback = Math.Min(fallback, PollingSweepIntervalMs);
            long fallbackTicks = fallback * TimeSpan.TicksPerMillisecond;
            if (last <= 0 || elapsed < 0 || elapsed >= fallbackTicks)
            {
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
            int interval = ProcessScanIntervalMs(ProcessEventsAvailable);
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
            if (remaining <= 0) return 1;
            return (int)Math.Min(interval, remaining);
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
