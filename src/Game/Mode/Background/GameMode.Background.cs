using System;
using System.Collections.Generic;
using System.IO;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private void StepAdaptiveGuard()
        {
            if (!AdaptiveEscalationWanted)
            {
                bool adaptiveEscalatedSnapshot = adaptiveEscalated;
                ResetAdaptiveGuard();
                if (adaptiveEscalatedSnapshot)
                {
                    Logger.Log(() => Lang.T("log.adaptive.3"));
                }
                return;
            }
            long now = AdaptiveNow();
            int stepResult = adaptiveGuard.Step(adaptivePressure.Update(AdaptiveSample(), now), now);
            if (stepResult > 0)
            {
                adaptiveEscalated = true;
                Logger.Log(() => Lang.T("log.adaptive.1") + adaptiveGuard.Episodes + "/" + 3);
            }
            else if (stepResult < 0)
            {
                adaptiveEscalated = false;
                Logger.Log(() => Lang.T("log.adaptive.2"));
            }
        }
        private void ResetAdaptiveGuard()
        {
            adaptiveGuard.Reset();
            adaptivePressure.Reset();
            adaptiveEscalated = false;
        }
        private long AdaptiveNow()
        {
            return DateTime.UtcNow.Ticks;
        }
        private double AdaptiveSample()
        {
            return adaptivePressure.Sample();
        }
        private bool WideBackgroundScope(PolicySnapshot policy)
        {
            if (policy != null)
            {
                if (!policy.WideBackgroundScope)
                {
                    if (adaptiveEscalated)
                    {
                        return policy.AdaptiveEscalation;
                    }
                    return false;
                }
                return true;
            }
            return false;
        }
        private void ClearBackgroundSqueezes()
        {
            if (core.SqueezedCount(SuppressReason.Background, false) > 0)
            {
                core.ClearSqueezes(SuppressReason.Background);
            }
            bool jobReleased = core.ReleaseAllBackgroundAffinityJobs();
            // 先按 Job 核验每个后台条目 核验不过的转入待恢复 下一行的待恢复判断要看这个结果 计数本身不用
            core.SqueezedCount(SuppressReason.Background);
            coreBackgroundPending = !jobReleased || core.SqueezedCount(SuppressReason.Background, false) > 0 || core.BackgroundAffinityPending;
            if (!coreBackgroundPending)
            {
                core.EnforceBackgroundAffinity = false;
            }
        }
        private bool RunBackgroundPolicy(int epoch, Action action)
        {
            lock (familyPolicyGate)
            {
                if (!FamilyPolicyEpochCurrent(epoch))
                {
                    return false;
                }
                action();
                return true;
            }
        }
        internal static bool RuntimeBackgroundExempt(BackgroundReleaseState state)
        {
            if (state != 0 && state != BackgroundReleaseState.Gone)
            {
                return state == BackgroundReleaseState.OtherReasonActive;
            }
            return true;
        }
        internal bool SetGameDvrSuppression(bool value)
        {
            lock (sync)
            {
                if (stopping || !Settings.Save("GameDvrOff", value))
                {
                    return false;
                }
                killGameDvr = value;
                ClearEnvFuse("gamedvr");
            }
            SyncGameDvr();
            RequestPolicyApply("SetGameDvrSuppression");
            return true;
        }
        private long nextSuppressionRetryTicks;

        // 普通后台恢复失败的条目按各自预算重试 每 8 秒检查一次 不随对局扫描节奏加快
        // 用单调时钟计时 系统时间被往回调时 墙钟会让重试停到时间追回为止
        private void RetryPendingSuppressionRestores()
        {
            long now = AutoCores.NowTicks;
            if (now < nextSuppressionRetryTicks) return;
            nextSuppressionRetryTicks = now + TimeSpan.FromSeconds(8).Ticks;
            core.RetryPending();
        }

        internal static SuppressionLevel BackgroundLevel()
        {
            return SuppressionLevel.Isolated;
        }
        private void Sweep(ProcessSnapshot all, HashSet<int> gamePids)
        {
            lock (whiteEvalSync)
            {
                SweepWithStableWhitelist(all, gamePids);
            }
        }
    }
}
