using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private sealed class NvPreStageCooldown
        {
            internal int PlanKey;
            internal long NotBefore;
            internal int Failures;
        }

        private readonly Dictionary<string, NvPreStageCooldown> nvPreStageCooldowns =
            new Dictionary<string, NvPreStageCooldown>(StringComparer.OrdinalIgnoreCase);
        private bool nvPreStageInFlight;
        private readonly Dictionary<string, NvPreStageCooldown> nvSessionCooldowns =
            new Dictionary<string, NvPreStageCooldown>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, NvPreStageCooldown> legacyNvCooldowns =
            new Dictionary<string, NvPreStageCooldown>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> legacyNvOwners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private long legacyNvSession = -1;
        private bool legacyNvOwnerRunning, legacyNvOwnerExited;

        private bool LegacyNvOwnersExited()
        {
            if (legacyNvOwners.Count == 0) return false;
            bool allClosed = true;
            int session;
            using (var current = System.Diagnostics.Process.GetCurrentProcess()) session = current.SessionId;
            foreach (string owner in legacyNvOwners)
            {
                GameNvTargetPhase phase = GameNvTargetProcesses.Phase(owner, session);
                if (phase == GameNvTargetPhase.Running) legacyNvOwnerRunning = true;
                allClosed &= phase == GameNvTargetPhase.Closed;
            }
            return legacyNvOwnerRunning && allClosed;
        }

        private bool BeginLegacyNvRecovery(out string key)
        {
            lock (gameNvGate)
            {
                key = null;
                if (gameNvWorker) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromSeconds(1)); return false; }
                long session = Interlocked.Read(ref gameSessionStarts);
                if (session != legacyNvSession) { legacyNvSession = session; legacyNvOwnerExited = legacyNvOwnerRunning = false; }
                if (!legacyNvOwnerExited) legacyNvOwnerExited = LegacyNvOwnersExited();
                key = session + "|" + (dataDir ?? "").ToLowerInvariant() + (legacyNvOwnerExited ? "|exited" : "|pending");
                NvPreStageCooldown row;
                if (legacyNvCooldowns.TryGetValue(key, out row) && (row.Failures >= 3 || NvPreStageClock() < row.NotBefore))
                { ReportLegacyNvRecovery(row); return false; }
                gameNvWorker = true; gameNvIdle.Reset(); PublishLegacyNvPending();
                return true;
            }
        }

        private void CompleteLegacyNvRecovery(string key, bool restored)
        {
            lock (gameNvGate)
            {
                if (restored) { legacyNvCooldowns.Remove(key); legacyNvOwners.Clear(); legacyNvOwnerRunning = legacyNvOwnerExited = false; }
                else
                {
                    NvPreStageCooldown row;
                    if (!legacyNvCooldowns.TryGetValue(key, out row)) legacyNvCooldowns[key] = row = new NvPreStageCooldown();
                    row.Failures = Math.Min(3, row.Failures + 1); row.NotBefore = NvPreStageClock() + TimeSpan.FromSeconds(30).Ticks;
                    gameNvRootClear = false;
                    ReportLegacyNvRecovery(row);
                    if (row.Failures == 3) Logger.Log("旧 NVIDIA 配置恢复连续失败三次后暂停，保留日志和所有权");
                }
                gameNvWorker = false; PublishLegacyNvPending(); gameNvIdle.Set();
            }
        }

        private void ReportLegacyNvRecovery(NvPreStageCooldown row)
        {
            if (row.Failures < 3) { RecoveryAttemptTrace.DeferredFor(TimeSpan.FromTicks(Math.Max(0, row.NotBefore - NvPreStageClock()))); return; }
            long session = legacyNvSession;
            RecoveryAttemptTrace.Exhausted(delegate
            {
                lock (gameNvGate)
                {
                    return gameNvRootClear || Interlocked.Read(ref gameSessionStarts) != session
                        || (!legacyNvOwnerExited && LegacyNvOwnersExited());
                }
            });
        }

        private static bool QueueLegacyNvWorker(WaitCallback callback) { return ThreadPool.QueueUserWorkItem(callback); }

        private sealed class NvSessionAttempt
        {
            internal string Key, Path;
            internal int Pid;
            internal int Completed;
            internal long Birth, Generation, Session;
            internal bool Failed = true, Processed, Satisfied;
        }

        private NvSessionAttempt BeginNvSessionAttempt(string path, int pid, long created, int planKey)
        {
            lock (sync)
            {
                string key = gameSessionStarts + "|" + pid + "|" + created + "|" + (path ?? "").ToLowerInvariant() + "|" + planKey;
                NvPreStageCooldown row;
                if (nvSessionCooldowns.TryGetValue(key, out row) && (row.Failures >= 3 || NvPreStageClock() < row.NotBefore)) return null;
                if (Interlocked.CompareExchange(ref nvTweakInFlight, 1, 0) != 0) return null;
                return new NvSessionAttempt { Key = key, Path = path, Pid = pid, Birth = created, Generation = nvTweakGeneration, Session = gameSessionStarts };
            }
        }

        private void CompleteNvSessionAttempt(NvSessionAttempt attempt)
        {
            if (Interlocked.Exchange(ref attempt.Completed, 1) != 0) return;
            lock (sync)
            {
                if (attempt.Failed)
                {
                    NvPreStageCooldown row;
                    if (!nvSessionCooldowns.TryGetValue(attempt.Key, out row)) nvSessionCooldowns[attempt.Key] = row = new NvPreStageCooldown();
                    row.Failures = Math.Min(3, row.Failures + 1);
                    row.NotBefore = NvPreStageClock() + TimeSpan.FromSeconds(30).Ticks;
                    if (row.Failures == 3) Logger.Log("NVIDIA 游戏配置应用连续失败三次后暂停： " + attempt.Path + "；保留恢复回执");
                }
                else if (attempt.Satisfied) nvSessionCooldowns.Remove(attempt.Key);
                if (attempt.Processed && !attempt.Failed && gameSessionStarts == attempt.Session
                    && NvTweakTargetCurrentLocked(attempt.Pid, attempt.Birth, attempt.Generation)) tweakApplied.Add(attempt.Pid);
                Interlocked.Exchange(ref nvTweakInFlight, 0);
            }
        }

        private static bool QueueNvSessionWorker(WaitCallback callback) { return ThreadPool.QueueUserWorkItem(callback); }

        private void QueueNvSessionPlan(string imagePath, NvGamePlan plan, NvSessionAttempt attempt)
        {
            try
            {
                GameNvWorkerQueue.TryQueue(QueueNvSessionWorker,
                    delegate { ApplySessionNvPlan(imagePath, plan, attempt); },
                    delegate { CompleteNvSessionAttempt(attempt); });
            }
            catch (Exception error) { Logger.RepeatedError("nv-session-queue", "NVIDIA 游戏配置入队失败： " + error.Message, -1L); }
        }

        private static long NvPreStageClock() { return AutoCores.NowTicks; }

        private void SyncNvAutomaticPhase(string path, NvGamePlan plan)
        {
            if (string.IsNullOrEmpty(path) || plan == null) return;
            long session = Interlocked.Read(ref gameSessionStarts);
            NvDrsTweaks.SyncAutomaticPhase(path, session, plan.AutomaticKey);
            string cycle = session + "\n" + plan.AutomaticKey;
            foreach (string action in new[] { "NvDriverProfile", "NvMaxPerf", "NvLowLat", "NvShaderCache", "NvRebar", "NvVrrWindowed" })
                ActionFailureStore.BeginAutomaticCycle(action, Path.GetFileName(path), cycle);
        }

        private string NvPreStageBudgetKey(string path, int planKey)
        {
            return Interlocked.Read(ref gameSessionStarts) + "|" + path.ToLowerInvariant() + "|" + planKey;
        }

        private bool TryBeginNvPreStage(string path, int planKey, out string previous)
        {
            lock (sync)
            {
                previous = preStagedNvPath;
                if (nvPreStageInFlight) return false;
                if (string.Equals(preStagedNvPath, path, StringComparison.OrdinalIgnoreCase)
                    && preStagedAutomaticNvKey == planKey) return false;
                long now = NvPreStageClock();
                NvPreStageCooldown cooldown;
                if (nvPreStageCooldowns.TryGetValue(NvPreStageBudgetKey(path, planKey), out cooldown)
                    && (cooldown.Failures >= 3 || now < cooldown.NotBefore)) return false;
                nvPreStageInFlight = true;
                preStagedNvPath = path;
                preStagedAutomaticNvKey = planKey;
                return true;
            }
        }

        private void CompleteNvPreStage(string path, int planKey, string budgetKey, bool completed, bool retry, bool verified)
        {
            lock (sync)
            {
                if (retry)
                {
                    NvPreStageCooldown cooldown;
                    if (!nvPreStageCooldowns.TryGetValue(budgetKey, out cooldown))
                        nvPreStageCooldowns[budgetKey] = cooldown = new NvPreStageCooldown { PlanKey = planKey };
                    cooldown.Failures = Math.Min(3, cooldown.Failures + 1);
                    // 驱动操作可能超过重试延迟，操作完成后才开始计时。
                    cooldown.NotBefore = NvPreStageClock() + TimeSpan.FromSeconds(30).Ticks;
                    if (cooldown.Failures == 3) Logger.Log("NVIDIA 预处理连续失败三次后暂停： " + path + "；保留恢复回执");
                }
                else if (verified) nvPreStageCooldowns.Remove(budgetKey);
                if ((!completed || retry) && string.Equals(preStagedNvPath, path, StringComparison.OrdinalIgnoreCase)
                    && preStagedAutomaticNvKey == planKey)
                    preStagedNvPath = null;
                nvPreStageInFlight = false;
            }
        }

        private void QueuePreStagedNvPlan(GameProfile armed, string path, NvGamePlan plan)
        {
            if (plan.Empty) return;
            int planKey = plan.AutomaticKey;
            SyncNvAutomaticPhase(path, plan);
            long session = Interlocked.Read(ref gameSessionStarts);
            string budgetKey = NvPreStageBudgetKey(path, planKey);
            string previous;
            if (!TryBeginNvPreStage(path, planKey, out previous)) return;
            bool completed = false;
            bool verified = false;
            bool retry = true; // 入队被拒绝或抛异常时，也必须设置有限重试的截止时间。
            try
            {
                GameNvWorkerQueue.TryQueue(callback => ThreadPool.QueueUserWorkItem(callback), delegate
                {
                    retry = false;
                    try
                    {
                        RunPreStagedMutation(delegate
                        {
                            lock (sync)
                            {
                                if (!enabled || panicReq || ProfileStoreSaveFailed || Interlocked.Read(ref gameSessionStarts) != session
                                    || !string.Equals(armedGameName, armed.Name, StringComparison.OrdinalIgnoreCase)
                                    || !string.Equals(preStagedNvPath, path, StringComparison.OrdinalIgnoreCase)
                                    || preStagedAutomaticNvKey != planKey) return;
                                GameProfile current = FindProfileLocked(armed.Id);
                                if (current == null || !string.Equals(current.PreferredExecutablePath, path, StringComparison.OrdinalIgnoreCase)
                                    || AutomaticPreStagePlan(PolicyResolver.For(current), Native.OnAcPower(), nvVrrWindowedOn).AutomaticKey != plan.AutomaticKey) return;
                            }
                            if (!string.IsNullOrEmpty(previous) && !string.Equals(previous, path, StringComparison.OrdinalIgnoreCase))
                                NvDrsTweaks.RestoreAllGames();
                            List<string> failed = ApplyNvGamePlan(path, plan, out retry, -1L, true);
                            HandleNvTweakOutcome(failed, plan, true);
                            retry |= failed != null && failed.Count > 0;
                            verified = !retry && failed != null && failed.Count == 0 && NvDrsTweaks.PlanSatisfied(path, plan);
                            completed = true;
                        });
                    }
                    catch (Exception error)
                    {
                        retry = true;
                        Logger.RepeatedError("nv-prestage", "NVIDIA 预处理失败： " + error.Message, -1L);
                    }
                }, delegate { CompleteNvPreStage(path, planKey, budgetKey, completed, retry, verified); });
            }
            catch (Exception error)
            {
                // 入队失败时，由 GameNvWorkerQueue 释放准入并记录冷却。
                Logger.RepeatedError("nv-prestage-queue", "NVIDIA 预处理入队失败： " + error.Message, -1L);
            }
        }
    }
}
