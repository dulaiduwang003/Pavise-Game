using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    // NVIDIA 档案的重试账本只通过这个接口读游戏模式的状态和请它做事 不直接碰 GameMode 的字段
    internal interface INvDriverRetryHost
    {
        // 就是游戏模式自己的 sync 锁 开局写入和预置的准入与结算都在这把锁里
        object SyncGate { get; }
        // 就是游戏模式的 NVIDIA 交付锁 旧档案恢复的准入与结算都在这把锁里
        object GameNvGate { get; }
        long GameSessionStarts { get; }
        string DataDir { get; }
        // 下面五项只在 GameNvGate 里调用 交付工作线程的占用和等待信号归游戏模式管
        bool GameNvWorkerBusy { get; }
        void EnterGameNvWorker();
        void LeaveGameNvWorker();
        bool GameNvRootClear { get; }
        void MarkGameNvRootPending();
        // 下面三项只在 SyncGate 里调用
        long NvTweakGeneration { get; }
        bool NvTweakTargetCurrentLocked(int pid, long creation, long generation);
        void MarkTweakAppliedLocked(int pid);
        // 预置目标路径也会被退局和环境恢复清掉 所以留在游戏模式 本组件只在 SyncGate 里读写它
        string PreStagedNvPath { get; set; }
    }

    // 一次开局写入的目标身份和结果 工作线程填写结果 结算只做一次
    internal sealed class NvSessionAttempt
    {
        internal string Key, Path;
        internal int Pid;
        internal int Completed;
        internal long Birth, Generation, Session;
        internal bool Failed = true, Processed, Satisfied;
    }

    // 开局写入 对局前预置和旧档案恢复三种 NVIDIA 工作各自的准入与有限重试 实例归 GameMode 所有
    internal sealed class NvDriverRetryCoordinator
    {
        private sealed class Cooldown
        {
            internal int PlanKey;
            internal long NotBefore;
            internal int Failures;
        }

        private readonly INvDriverRetryHost host;
        private readonly Dictionary<string, Cooldown> preStageCooldowns =
            new Dictionary<string, Cooldown>(StringComparer.OrdinalIgnoreCase);
        private bool preStageInFlight;
        private int preStagedPlanKey = -1;
        private readonly Dictionary<string, Cooldown> sessionCooldowns =
            new Dictionary<string, Cooldown>(StringComparer.OrdinalIgnoreCase);
        private int sessionInFlight;
        private readonly Dictionary<string, Cooldown> legacyCooldowns =
            new Dictionary<string, Cooldown>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> legacyOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long legacySession = -1;
        private bool legacyOwnerRunning, legacyOwnerExited;

        internal NvDriverRetryCoordinator(INvDriverRetryHost host)
        {
            if (host == null) throw new ArgumentNullException("host");
            this.host = host;
        }

        private static long Clock() { return AutoCores.NowTicks; }

        private bool LegacyOwnersExited()
        {
            if (legacyOwners.Count == 0) return false;
            bool allClosed = true;
            int session;
            using (var current = System.Diagnostics.Process.GetCurrentProcess()) session = current.SessionId;
            foreach (string owner in legacyOwners)
            {
                GameNvTargetPhase phase = GameNvTargetProcesses.Phase(owner, session);
                if (phase == GameNvTargetPhase.Running) legacyOwnerRunning = true;
                allClosed &= phase == GameNvTargetPhase.Closed;
            }
            return legacyOwnerRunning && allClosed;
        }

        // 下面两个只在旧档案恢复准入之后调用 占用标记保证同一时刻只有一个调用方
        internal void AddLegacyOwner(string executable) { legacyOwners.Add(executable); }
        internal void ObserveLegacyOwners() { LegacyOwnersExited(); }

        internal bool BeginLegacyRecovery(out string key)
        {
            lock (host.GameNvGate)
            {
                key = null;
                if (host.GameNvWorkerBusy) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
                long session = host.GameSessionStarts;
                if (session != legacySession) { legacySession = session; legacyOwnerExited = legacyOwnerRunning = false; }
                if (!legacyOwnerExited) legacyOwnerExited = LegacyOwnersExited();
                key = session + "|" + (host.DataDir ?? "").ToLowerInvariant() + (legacyOwnerExited ? "|exited" : "|pending");
                Cooldown row;
                if (legacyCooldowns.TryGetValue(key, out row) && (row.Failures >= 3 || Clock() < row.NotBefore))
                { ReportLegacyRecovery(row); return false; }
                host.EnterGameNvWorker();
                return true;
            }
        }

        internal void CompleteLegacyRecovery(string key, bool restored)
        {
            lock (host.GameNvGate)
            {
                if (restored) { legacyCooldowns.Remove(key); legacyOwners.Clear(); legacyOwnerRunning = legacyOwnerExited = false; }
                else
                {
                    Cooldown row;
                    if (!legacyCooldowns.TryGetValue(key, out row)) legacyCooldowns[key] = row = new Cooldown();
                    row.Failures = Math.Min(3, row.Failures + 1); row.NotBefore = Clock() + TimeSpan.FromSeconds(30).Ticks;
                    host.MarkGameNvRootPending();
                    ReportLegacyRecovery(row);
                    if (row.Failures == 3) Logger.Log("旧 NVIDIA 配置恢复连续失败三次后暂停，保留日志和所有权", "Legacy NVIDIA recovery paused after 3 failures; journals and ownership retained");
                }
                host.LeaveGameNvWorker();
            }
        }

        private void ReportLegacyRecovery(Cooldown row)
        {
            if (row.Failures < 3) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(Math.Max(0, row.NotBefore - Clock()))); return; }
            long session = legacySession;
            RecoveryAttemptTrace.Exhausted(delegate
            {
                lock (host.GameNvGate)
                {
                    return host.GameNvRootClear || host.GameSessionStarts != session
                        || (!legacyOwnerExited && LegacyOwnersExited());
                }
            });
        }

        internal NvSessionAttempt BeginSessionAttempt(string path, int pid, long created, int planKey)
        {
            lock (host.SyncGate)
            {
                string key = host.GameSessionStarts + "|" + pid + "|" + created + "|" + (path ?? "").ToLowerInvariant() + "|" + planKey;
                Cooldown row;
                if (sessionCooldowns.TryGetValue(key, out row) && (row.Failures >= 3 || Clock() < row.NotBefore)) return null;
                if (Interlocked.CompareExchange(ref sessionInFlight, 1, 0) != 0) return null;
                return new NvSessionAttempt { Key = key, Path = path, Pid = pid, Birth = created, Generation = host.NvTweakGeneration, Session = host.GameSessionStarts };
            }
        }

        internal void CompleteSessionAttempt(NvSessionAttempt attempt)
        {
            if (Interlocked.Exchange(ref attempt.Completed, 1) != 0) return;
            lock (host.SyncGate)
            {
                if (attempt.Failed)
                {
                    Cooldown row;
                    if (!sessionCooldowns.TryGetValue(attempt.Key, out row)) sessionCooldowns[attempt.Key] = row = new Cooldown();
                    row.Failures = Math.Min(3, row.Failures + 1);
                    row.NotBefore = Clock() + TimeSpan.FromSeconds(30).Ticks;
                    if (row.Failures == 3) Logger.Log(() => Logger.Format("NVIDIA 游戏配置应用连续失败三次后暂停： {0}；保留恢复回执", "NVIDIA game apply paused after 3 failures: {0}; recovery receipt retained", attempt.Path));
                }
                else if (attempt.Satisfied) sessionCooldowns.Remove(attempt.Key);
                if (attempt.Processed && !attempt.Failed && host.GameSessionStarts == attempt.Session
                    && host.NvTweakTargetCurrentLocked(attempt.Pid, attempt.Birth, attempt.Generation)) host.MarkTweakAppliedLocked(attempt.Pid);
                Interlocked.Exchange(ref sessionInFlight, 0);
            }
        }

        internal string PreStageBudgetKey(string path, int planKey)
        {
            return host.GameSessionStarts + "|" + path.ToLowerInvariant() + "|" + planKey;
        }

        // 只在 SyncGate 里调用 预置目标和计划都还是这一次的才算数
        internal bool PreStageCurrentLocked(string path, int planKey)
        {
            return string.Equals(host.PreStagedNvPath, path, StringComparison.OrdinalIgnoreCase)
                && preStagedPlanKey == planKey;
        }

        internal bool TryBeginPreStage(string path, int planKey, out string previous)
        {
            lock (host.SyncGate)
            {
                previous = host.PreStagedNvPath;
                if (preStageInFlight) return false;
                if (PreStageCurrentLocked(path, planKey)) return false;
                long now = Clock();
                Cooldown cooldown;
                if (preStageCooldowns.TryGetValue(PreStageBudgetKey(path, planKey), out cooldown)
                    && (cooldown.Failures >= 3 || now < cooldown.NotBefore)) return false;
                preStageInFlight = true;
                host.PreStagedNvPath = path;
                preStagedPlanKey = planKey;
                return true;
            }
        }

        internal void CompletePreStage(string path, int planKey, string budgetKey, bool completed, bool retry, bool verified)
        {
            lock (host.SyncGate)
            {
                if (retry)
                {
                    Cooldown cooldown;
                    if (!preStageCooldowns.TryGetValue(budgetKey, out cooldown))
                        preStageCooldowns[budgetKey] = cooldown = new Cooldown { PlanKey = planKey };
                    cooldown.Failures = Math.Min(3, cooldown.Failures + 1);
                    // 驱动操作可能超过重试延迟 操作完成后才开始计时
                    cooldown.NotBefore = Clock() + TimeSpan.FromSeconds(30).Ticks;
                    if (cooldown.Failures == 3) Logger.Log(() => Logger.Format("NVIDIA 预处理连续失败三次后暂停： {0}；保留恢复回执", "NVIDIA pre-stage paused after 3 failures: {0}; recovery receipt retained", path));
                }
                else if (verified) preStageCooldowns.Remove(budgetKey);
                if ((!completed || retry) && PreStageCurrentLocked(path, planKey))
                    host.PreStagedNvPath = null;
                preStageInFlight = false;
            }
        }
    }
}
