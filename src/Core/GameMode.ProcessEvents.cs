// @author bdth 2074055628@qq.com
// 文件用途 合并进程事件并限制游戏模式全量扫描频率

using System;
using System.Threading;

namespace AegisApp
{
    internal partial class GameMode
    {
        private int processSetDirty;
        private int urgentProcessScan;
        private int transitionScanPending;
        private int transitionProbeRendererPid;
        private long transitionProbeRendererCreation;
        private int gameDetectionDirty = 1;
        private int processEventsAvailable;
        private long lastProcessScanTicks;
        private long processScanRetryAfterTicks;
        private long lastFullGameDetectionTicks;
        private long processScanCount;
        // 没有事件源时保留 4 秒兼容轮询；事件源可用时，普通进程突发只标脏，
        // 由 20 秒全量校准统一处理。只有游戏会话边界或显式策略变更能够越过预算。
        // 否则安装器/浏览器/遥测进程持续启停时，会把事件模式重新放大成每 4 秒
        // 一次 Process.GetProcesses + 白名单全量快照。
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
                    // 仅在用户入口/launcher 仍是当前 renderer 的过渡期跟踪
                    // 它的直接子进程；普通游戏 helper 子进程不能绕过扫描预算。
                    if (IsActiveFamilyChildStart(
                            activeDetection, change, selfSession))
                    {
                        relevant = true;
                        // 每个 launcher renderer 身份只给一次 5 秒过渡探测。
                        // 若这次仍未迁移到真正 renderer，后续 helper 突发回到
                        // 20 秒校准，不能永久把全量扫描锁在 5 秒。
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
            // 溢出表示事件不完整，只要求下一轮全量校准，不能把持续溢出
            // 当成游戏边界而每 750 ms 绕过扫描预算。若保留下来的事件中
            // 确实命中入口或渲染进程退出，detectionRelevant 仍会立即处理。
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
            // 普通进程突发已经受事件模式的校准期限覆盖。若每个合并批次仍唤醒
            // worker，即使没有扫描到期，也会让 Aegis 与游戏争抢调度。
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
            // creation 暂不可读时按同一 epoch 处理，优先守住扫描预算；
            // 一旦两侧都有身份，PID 复用会重新武装一次探测。
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

        // 事件模式下脏位不会缩短 20 秒预算。游戏会话变化走 urgent 分支，
        // 显式设置变更走 RequestPolicyApply，两者仍然立即生效。
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
            // 显式策略变更本来就要拿一次完整进程快照；同时刷新游戏检测，
            // 避免一次临近截止时间的策略扫描把 20 秒检测期限顺延到近 40 秒。
            Interlocked.Exchange(ref gameDetectionDirty, 1);
            Interlocked.Exchange(ref urgentProcessScan, 1);
            Interlocked.Exchange(ref processSetDirty, 1);
            try { kick.Set(); } catch { }
        }
    }
}
