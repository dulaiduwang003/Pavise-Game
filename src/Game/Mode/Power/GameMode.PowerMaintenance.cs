using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode : IPowerMaintenanceHost
    {
        private PowerMaintenance powerMaintenance;

        // 正常由构造函数创建 这里的兜底只给测试里不走构造函数造出来的 GameMode 用
        internal PowerMaintenance PowerMaintenance
        {
            get { return LazyInitializer.EnsureInitialized(ref powerMaintenance, () => new PowerMaintenance(this)); }
        }

        object IPowerMaintenanceHost.Sync { get { return sync; } }
        object IPowerMaintenanceHost.PowerApplyGate { get { return powerApplyGate; } }
        bool IPowerMaintenanceHost.GuardEnabled { get { return enabled; } }
        bool IPowerMaintenanceHost.GameActive { get { return active; } }
        bool IPowerMaintenanceHost.PowerPlanActive { get { return planActive; } }
        bool IPowerMaintenanceHost.PowerSessionEnding { get { return powerSessionEnding; } }
        bool IPowerMaintenanceHost.ShuttingDown { get { return stopping || panicReq; } }
        bool IPowerMaintenanceHost.ProfileStoreReady { get { return !ProfileStoreSaveFailed; } }
        bool IPowerMaintenanceHost.GameGone { get { return gameGoneSinceTicks != 0; } }
        int IPowerMaintenanceHost.PowerSessionGeneration { get { return Volatile.Read(ref powerSessionGen); } }
        int IPowerMaintenanceHost.LastPowerPolicyKey { get { return lastPowerPolicyKey; } }
        long IPowerMaintenanceHost.NextPowerAuditTicks { get { return Interlocked.Read(ref nextPowerAuditTicks); } }
        int IPowerMaintenanceHost.PlanFailStreak { get { return planFailStreak; } }
        bool IPowerMaintenanceHost.PowerAttemptInProgress { get { return powerAttemptInProgress != null; } }
        bool IPowerMaintenanceHost.PowerSessionCurrent(int generation) { return PowerPlanApplySessionCurrent(generation); }
        bool IPowerMaintenanceHost.PowerPlanStopped(int policyKey) { return PowerPlanStopped(policyKey); }
        bool IPowerMaintenanceHost.TryClaimPowerApply() { return Interlocked.CompareExchange(ref powerApplyInFlight, 1, 0) == 0; }
        void IPowerMaintenanceHost.ReleasePowerApply() { Interlocked.Exchange(ref powerApplyInFlight, 0); }
        bool IPowerMaintenanceHost.AutomaticWorkAllowed(string key) { return AutomaticWorkAllowed(key); }
        void IPowerMaintenanceHost.RecordAutomaticWorkFailure(string key, string reason) { RecordAutomaticWorkFailure(key, reason); }
        bool IPowerMaintenanceHost.BeginPowerAttempt(int policyKey, out string reason) { return BeginPowerAttempt(policyKey, out reason); }
        void IPowerMaintenanceHost.CompletePowerAttempt(bool success, string reason) { CompletePowerAttempt(success, reason); }
        bool IPowerMaintenanceHost.RunPowerPlanAction(Func<bool> action, out bool skipped, out string reason)
        { return RunPowerPlanAction(action, out skipped, out reason); }
        void IPowerMaintenanceHost.RollbackPowerPlan(string reason) { RollbackPowerPlan(reason); }
        void IPowerMaintenanceHost.OnPowerPlanApplied(bool planOk, string reason, int generation)
        { OnPowerPlanAppliedWithReason(planOk, reason, generation); }
    }
}
