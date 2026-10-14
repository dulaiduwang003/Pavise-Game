using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private long automaticLayoutRefresh;
        private ulong automaticLayoutMask;
        public bool AutomaticCoreLayoutEnabled
        { get { return Settings.LoadCached(AutomaticCoreLayout.PreferenceKey, false); } }

        public bool TrySetAutomaticCoreLayoutEnabled(bool value)
        {
            lock (sync)
            {
                if (stopping || !Settings.Save(AutomaticCoreLayout.PreferenceKey, value)) return false;
                automaticLayoutRefresh = 0;
            }
            RequestPolicyApply(); return true;
        }
        private bool AutomaticLayoutAdmitted()
        {
            return AutomaticCoreLayoutEnabled && enabled && active && !stopping && !panicReq && !ProfileStoreSaveFailed
                && coreManual == null;
        }
        private void SyncAutomaticBackgroundLayout(HashSet<int> protectedPids, int foreground)
        {
            bool wanted = AutomaticLayoutAdmitted();
            long now = DateTime.UtcNow.Ticks;
            if (wanted && now >= automaticLayoutRefresh)
            {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                automaticLayoutMask = 0;
#else
                automaticLayoutMask = AutomaticCoreLayout.Current();
#endif
                automaticLayoutRefresh = now + 10 * TimeSpan.TicksPerSecond;
            }
            ulong mask = wanted ? automaticLayoutMask : 0;
            uint[] ids = mask == 0 ? null : CpuTopology.CpuSetIdsFor(mask);
            GameDetection detection = activeDetection;
            core.SyncAutomaticLayout(mask, ids, protectedPids, foreground,
                detection == null ? 0 : detection.RendererPid, AutomaticLayoutAdmitted);
        }
    }
}
