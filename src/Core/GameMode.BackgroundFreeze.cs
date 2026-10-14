using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly BackgroundFreeze backgroundFreeze = new BackgroundFreeze();
        internal string FreezeDiagnosticLine { get { return backgroundFreeze.DiagnosticLine; } }
        public string BackgroundFreezeStatus
        {
            get
            {
                if (Preset != PerformancePreset.Extreme) return Lang.T("freeze.othermode");
                if (!FreezeNative.Supported) return Lang.T("freeze.unsupported");
                if (backgroundFreeze.Error != null) return Lang.F("freeze.failed", backgroundFreeze.Error);
                return Lang.F("freeze.status", backgroundFreeze.ActiveCount);
            }
        }
        private void SyncBackgroundFreeze(Dictionary<int, FreezeTarget> wanted, int gamePid,
            long gameCreation, Dictionary<int, long> cpuCapTargets)
        {
            lock (cpuCapPolicyGate)
            {
                if (Preset != PerformancePreset.Extreme || EffPreset != PerformancePreset.Extreme
                    || !EffSuppress || !enabled || stopping || panicReq || ProfileStoreSaveFailed)
                { backgroundFreeze.Clear("policy-gate"); return; }
#if !PAVISE_SELFTEST && !PAVISE_PERFLAB
                foreach (int pid in backgroundFreeze.Sync(wanted, gamePid, gameCreation, AutomationSession))
                {
                    if (backgroundCpuCap.Release(pid)) cpuCapTargets.Remove(pid);
                    else if (!backgroundFreeze.Release(pid, wanted[pid].Creation, "job-release-pending"))
                        cpuCapTargets.Clear();
                }
                // 无法确认解冻时不叠加 Job 限额。
                if (backgroundFreeze.Error != null && backgroundFreeze.HasPending) cpuCapTargets.Clear();
#endif
            }
        }
    }
}
