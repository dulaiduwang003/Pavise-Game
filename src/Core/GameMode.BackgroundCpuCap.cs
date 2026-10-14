using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly BackgroundCpuCap backgroundCpuCap = new BackgroundCpuCap();
        private readonly object cpuCapPolicyGate = new object();
        internal static bool CpuCapSupported(PerformancePreset mode)
        { return mode == PerformancePreset.Extreme; }
        private void ReleaseUnsupportedCpuCaps(PerformancePreset mode)
        {
            lock (cpuCapPolicyGate)
            {
                if (!CpuCapSupported(mode)) backgroundCpuCap.Clear();
                if (mode != PerformancePreset.Extreme) backgroundFreeze.Clear("mode-changed");
            }
        }
        public string BackgroundCpuCapStatus
        { get { return !CpuCapSupported(Preset) ? Lang.T("cpucap.unsupported")
            : backgroundCpuCap.Error == null ? Lang.F("cpucap.status", backgroundCpuCap.ActiveCount)
            : Lang.F("cpucap.failed", backgroundCpuCap.Error); } }
        private void SyncBackgroundCpuCap(Dictionary<int, long> wanted, HashSet<int> servicePids)
        {
            lock (cpuCapPolicyGate)
            {
                if (!CpuCapSupported(Preset) || !CpuCapSupported(EffPreset)
                    || !EffSuppress || !enabled || stopping || panicReq || ProfileStoreSaveFailed)
                { backgroundCpuCap.Clear(); return; }
#if !PAVISE_SELFTEST && !PAVISE_PERFLAB
                backgroundCpuCap.Sync(wanted, BackgroundCpuCap.AutomaticPercent, servicePids);
#endif
            }
        }
    }
}
