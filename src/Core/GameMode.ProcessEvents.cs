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
        internal const int ActiveGameSweepIntervalMs = 500;
        // ETW 事件在场时的对局底噪轮询
        //   2.0 删掉热度采样之后 底噪轮询只用来兜底发现新进程 不再需要对齐采样窗
        //   进程集真变动时由 processSetDirty 驱动扫描 不靠这个底噪兜底
        //   台架模拟 600 秒对局 平均 10 秒冒一个新进程 单次快照按本机实测 2.19ms 计
        //     500ms 固定轮询    1200 次  一个核 0.438%  发现延迟均 241ms 最坏 490ms
        //     1000ms + dirty     621 次  一个核 0.227%  发现延迟均  86ms 最坏 460ms
        //     1000ms 不接 dirty  600 次  一个核 0.219%  发现延迟均 517ms 最坏 990ms
        //   放宽轮询和接上 dirty 必须一起做 只放宽不接的那一档延迟烂一倍
        internal const int EventBackedActiveGameSweepIntervalMs = 1000;
        // 进程集变动驱动扫描的最小间隔 取 500 是为了扫描率永远不高于放宽之前
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

            // 进程集变动本身就是扫描信号 一律唤醒主循环 真扫不扫由 DirtyScanDue 的地板决定
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

        internal static int ProcessScanIntervalMs(bool eventsAvailable, bool gameActive)
        {
            if (gameActive)
                return eventsAvailable
                    ? EventBackedActiveGameSweepIntervalMs : ActiveGameSweepIntervalMs;
            return ProcessScanIntervalMs(eventsAvailable);
        }

        // 进程集变动是否已经够格触发一次扫描
        //   没有事件源时 processSetDirty 只由扫描失败重排置位 那条路自己会置 urgentProcessScan
        //   所以这里只认事件在场的情况 没有事件源时行为一个字节都不变
        internal static bool DirtyScanDue(bool eventsAvailable, bool dirty, long elapsedMs)
        {
            if (!eventsAvailable || !dirty) return false;
            return elapsedMs < 0 || elapsedMs >= DirtyScanFloorMs;
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
