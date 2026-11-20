using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private DisplayAwakeLogThrottle displayAwakeLog;

        // 正常由构造函数创建 这里的兜底只给测试里不走构造函数造出来的 GameMode 用
        internal DisplayAwakeLogThrottle DisplayAwakeLog
        {
            get { return LazyInitializer.EnsureInitialized(ref displayAwakeLog, () => new DisplayAwakeLogThrottle()); }
        }
        internal static int PowerPolicyKey(bool processorTuning, bool usePlan)
        {
            return (processorTuning ? 1 : 0) | (usePlan ? 2 : 0);
        }
        private void RollbackPowerPlan(string reason)
        {
            LaptopPerformance.InvalidatePowerGeneration();
            bool runAutomaticRestoreSucceeded = RunAutomaticRestore("powerplan", () => LaptopPerformance.RestoreForPowerPlan() && RestorePowerPlan());
            lock (sync)
            {
                if (runAutomaticRestoreSucceeded)
                {
                    envRestorePending.Remove("powerplan");
                    envNextAttempt.Remove("powerplan");
                    envFailures.Remove("powerplan");
                }
                else
                {
                    envRestorePending.Add("powerplan");
                }
            }
            if (runAutomaticRestoreSucceeded)
            {
                ActionFailureStore.CancelApply("PowerPlanOn", "system");
            }
            else
            {
                ActionFailureStore.Trip("PowerPlanOn", "system", reason + PerformancePolicy.Text("；电源计划回退失败", "; power-plan rollback failed"));
            }
            planActive = !runAutomaticRestoreSucceeded;
            if (runAutomaticRestoreSucceeded)
            {
                lastPowerPolicyKey = -1;
            }
            RecordPowerPlanStatus();
        }
        private bool PowerPlanStopped(int key)
        {
            AutomaticFailurePhase phase = PowerAttemptPhase(key);
            bool isBlocked = ActionFailureStore.IsBlocked("PowerPlanOn", "system");
            lock (sync)
            {
                isBlocked |= phase.Stopped || phase.Attempts >= 3 || DateTime.UtcNow.Ticks < phase.Next;
            }
            if (isBlocked) RecordPowerPlanStatus();
            return isBlocked;
        }
        private void InvalidatePowerPlanSession()
        {
            LaptopPerformance.InvalidatePowerGeneration();
            Interlocked.Increment(ref powerSessionGen);
            PowerMaintenance.DropNotification();
            Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
        }
        private bool PowerPlanApplySessionCurrent(int genShot)
        {
            lock (sync)
            {
                return enabled && active && planActive && !stopping && !panicReq && !powerSessionEnding && !ProfileStoreSaveFailed
                    && gameGoneSinceTicks == 0 && !PowerPlan.NoSwitchSelected
                    && Volatile.Read(ref powerSessionGen) == genShot;
            }
        }
        private bool RunPowerPlanApply(int genShot, Func<bool> apply)
        {
            try
            {
                lock (powerApplyGate)
                {
                    if (!PowerPlanApplySessionCurrent(genShot) || PowerPlanStopped(lastPowerPolicyKey))
                    {
                        return false;
                    }
                    if (apply == null)
                    {
                        return false;
                    }
                    if (LaptopPerformance.PowerGeneration != genShot && !LaptopPerformance.RestoreForPowerPlan())
                    {
                        lastPowerPolicyKey = -1;
                        Interlocked.Exchange(ref nextPowerAuditTicks, 0L);
                        return false;
                    }
                    string reason;
                    if (!BeginPowerAttempt(lastPowerPolicyKey, out reason))
                    {
                        RecordPowerPlanStatus();
                        return false;
                    }
                    bool skipped;
                    bool runPowerPlanActionSucceeded = RunPowerPlanAction(delegate
                    {
                        if (!PowerPlanApplySessionCurrent(genShot))
                        {
                            throw new OperationCanceledException();
                        }
                        return apply();
                    }, out skipped, out reason);
                    if (skipped)
                    {
                        RollbackPowerPlan(PerformancePolicy.Text("电源计划应用已跳过", "Power-plan apply was skipped"));
                        CompletePowerAttempt(false, "stage=apply-skipped");
                        return false;
                    }
                    if (!PowerPlanApplySessionCurrent(genShot))
                    {
                        if (!runPowerPlanActionSucceeded)
                        {
                            ActionFailureStore.Trip("PowerPlanOn", "system", reason);
                        }
                        RollbackPowerPlan(runPowerPlanActionSucceeded ? "stage=session-cancelled" : reason + " phase=session-cancelled");
                        CompletePowerAttempt(false, "stage=session-cancelled");
                        return false;
                    }
                    OnPowerPlanAppliedWithReason(runPowerPlanActionSucceeded, reason, genShot);
                    return runPowerPlanActionSucceeded && !ActionFailureStore.IsBlocked("PowerPlanOn", "system");
                }
            }
            catch
            {
                return false;
            }
            finally
            {
                lock (powerApplyGate)
                {
                    if (powerAttemptInProgress != null)
                    {
                        RollbackPowerPlan("stage=attempt-interrupted");
                        CompletePowerAttempt(false, "stage=attempt-interrupted");
                    }
                }
                Interlocked.Exchange(ref powerApplyInFlight, 0);
                PowerMaintenance.QueueAuditIfPending();
            }
        }
        private void OnPowerPlanAppliedWithReason(bool planOk, string reason, int generation)
        {
            if (planOk)
            {
                if (!ActionFailureStore.CompleteApply("PowerPlanOn", "system"))
                {
                    RollbackPowerPlan(PerformancePolicy.Text("电源计划完成记录失败", "Power-plan completion could not be persisted"));
                    CompletePowerAttempt(false, "stage=completion-persistence");
                    return;
                }
                planFailStreak = 0;
                if (LoadCounter("PowerPlanFailStreak") != 0)
                {
                    SaveCounter("PowerPlanFailStreak", 0);
                }
                Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
                LaptopPerformance.ArmPowerGeneration(generation);
                RecordAutomation("PowerPlanOn", "applied");
                CompletePowerAttempt(true, "stage=write-and-readback" + PowerPlan.PolicyLockedApplyNote);
            }
            else
            {
                planFailStreak = 1;
                ActionFailureStore.Trip("PowerPlanOn", "system", reason);
                Interlocked.Exchange(ref nextPowerAuditTicks, long.MaxValue);
                RollbackPowerPlan(reason);
                CompletePowerAttempt(false, reason, PowerPlan.LastApplyFailureIsPermanent);
            }
        }
        private bool StepDisplayAwake(bool want)
        {
            awakeActive = EnvStep("awake", want, awakeActive, DisplayAwake.ActivateForSession, DisplayAwake.RestoreForSession);
            return awakeActive;
        }
        internal static bool ResolvePowerPlanEnabled(PerformancePreset mode, bool manuallyEnabled)
        {
            if (!PowerPlan.NoSwitchSelected)
            {
                return manuallyEnabled;
            }
            return false;
        }
    }
}
