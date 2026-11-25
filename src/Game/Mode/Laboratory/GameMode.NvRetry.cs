using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode : INvDriverRetryHost
    {
        private NvDriverRetryCoordinator nvDriverRetry;

        // 正常由构造函数创建 这里的兜底只给测试里不走构造函数造出来的 GameMode 用
        internal NvDriverRetryCoordinator NvDriverRetry
        {
            get { return LazyInitializer.EnsureInitialized(ref nvDriverRetry, () => new NvDriverRetryCoordinator(this)); }
        }

        object INvDriverRetryHost.SyncGate { get { return sync; } }
        object INvDriverRetryHost.GameNvGate { get { return gameNvGate; } }
        long INvDriverRetryHost.GameSessionStarts { get { return Interlocked.Read(ref gameSessionStarts); } }
        string INvDriverRetryHost.DataDir { get { return dataDir; } }
        bool INvDriverRetryHost.GameNvWorkerBusy { get { return gameNvWorker; } }
        void INvDriverRetryHost.EnterGameNvWorker() { gameNvWorker = true; gameNvIdle.Reset(); PublishLegacyNvPending(); }
        void INvDriverRetryHost.LeaveGameNvWorker() { gameNvWorker = false; PublishLegacyNvPending(); gameNvIdle.Set(); }
        bool INvDriverRetryHost.GameNvRootClear { get { return gameNvRootClear; } }
        void INvDriverRetryHost.MarkGameNvRootPending() { gameNvRootClear = false; }
        long INvDriverRetryHost.NvTweakGeneration { get { return nvTweakGeneration; } }
        bool INvDriverRetryHost.NvTweakTargetCurrentLocked(int pid, long creation, long generation)
        { return NvTweakTargetCurrentLocked(pid, creation, generation); }
        void INvDriverRetryHost.MarkTweakAppliedLocked(int pid) { tweakApplied.Add(pid); }
        string INvDriverRetryHost.PreStagedNvPath
        {
            get { return preStagedNvPath; }
            set { preStagedNvPath = value; }
        }

        private static bool QueueLegacyNvWorker(WaitCallback callback) { return ThreadPool.QueueUserWorkItem(callback); }

        private static bool QueueNvSessionWorker(WaitCallback callback) { return ThreadPool.QueueUserWorkItem(callback); }

        private void QueueNvSessionPlan(string imagePath, NvGamePlan plan, NvSessionAttempt attempt)
        {
            try
            {
                GameNvWorkerQueue.TryQueue(QueueNvSessionWorker,
                    delegate { ApplySessionNvPlan(imagePath, plan, attempt); },
                    delegate { NvDriverRetry.CompleteSessionAttempt(attempt); });
            }
            catch (Exception error) { Logger.RepeatedError("nv-session-queue", Logger.Format("NVIDIA 游戏配置入队失败： {0}", "NVIDIA game profile queue failed: {0}", error.Message), -1L); }
        }

        private void SyncNvAutomaticPhase(string path, NvGamePlan plan)
        {
            if (string.IsNullOrEmpty(path) || plan == null) return;
            long session = Interlocked.Read(ref gameSessionStarts);
            NvDrsTweaks.SyncAutomaticPhase(path, session, plan.AutomaticKey);
            string cycle = session + "\n" + plan.AutomaticKey;
            foreach (string action in new[] { "NvDriverProfile", "NvMaxPerf", "NvShaderCache", "NvRebar", PolicyCatalog.KeyGameLowLatency })
                ActionFailureStore.BeginAutomaticCycle(action, Path.GetFileName(path), cycle);
        }

        private void QueuePreStagedNvPlan(GameProfile armed, string path, NvGamePlan plan)
        {
            if (plan.Empty) return;
            int planKey = plan.AutomaticKey;
            SyncNvAutomaticPhase(path, plan);
            long session = Interlocked.Read(ref gameSessionStarts);
            string budgetKey = NvDriverRetry.PreStageBudgetKey(path, planKey);
            string previous;
            if (!NvDriverRetry.TryBeginPreStage(path, planKey, out previous)) return;
            bool completed = false;
            bool verified = false;
            bool retry = true; // 入队被拒绝或抛异常时 也必须设置有限重试的截止时间
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
                                    || !NvDriverRetry.PreStageCurrentLocked(path, planKey)) return;
                                GameProfile current = FindProfileLocked(armed.Id);
                                if (current == null || !string.Equals(current.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)
                                    || AutomaticPreStagePlan(PolicyResolver.For(current), Native.OnAcPower()).AutomaticKey != plan.AutomaticKey) return;
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
                        Logger.RepeatedError("nv-prestage", Logger.Format("NVIDIA 预处理失败： {0}", "NVIDIA pre-stage failed: {0}", error.Message), -1L);
                    }
                }, delegate { NvDriverRetry.CompletePreStage(path, planKey, budgetKey, completed, retry, verified); });
            }
            catch (Exception error)
            {
                // 入队失败时 由 GameNvWorkerQueue 释放准入并记录冷却
                Logger.RepeatedError("nv-prestage-queue", Logger.Format("NVIDIA 预处理入队失败： {0}", "NVIDIA pre-stage queue failed: {0}", error.Message), -1L);
            }
        }
    }
}
