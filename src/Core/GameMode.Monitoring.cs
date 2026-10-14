using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private readonly MonitoringGamePreparation monitoringPreparation = new MonitoringGamePreparation();
        internal bool IsMonitoringPreparing
        {
            get
            {
                lock (sync)
                {
                    if (active || !enabled || stopping || panicReq || ProfileStoreSaveFailed || !MonitoringPreferences.Enabled)
                    { monitoringPreparation.Cancel(); return false; }
                    return monitoringPreparation.IsPending(FindProfileLocked);
                }
            }
        }
        private bool ObserveMonitoringLaunchLocked(GameProfile profile, ProcEntry entry)
        {
            return !active && enabled && !stopping && !panicReq && !ProfileStoreSaveFailed
                && MonitoringPreferences.Enabled && monitoringPreparation.Observe(profile, entry, selfSession);
        }
        private void ObserveMonitoringLaunches(ProcessSnapshot snapshot, List<GameProfile> copy)
        {
            if (snapshot == null || !MonitoringPreferences.Enabled) return;
            bool wake = false;
            lock (sync)
            {
                if (active) { monitoringPreparation.Cancel(); return; }
                if (!enabled || stopping || panicReq || ProfileStoreSaveFailed) return;
                var byPath = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
                foreach (GameProfile profile in copy)
                {
                    string primary = WhitelistRule.NormalizeImagePath(profile.ExecutablePath);
                    string learned = WhitelistRule.NormalizeImagePath(profile.LearnedExecutablePath);
                    if (primary.Length > 0) byPath[primary] = profile;
                    if (learned.Length > 0) byPath[learned] = profile;
                }
                foreach (ProcEntry entry in snapshot.Entries)
                {
                    if (entry.Session != selfSession || string.IsNullOrEmpty(entry.Path)) continue;
                    GameProfile profile;
                    if (byPath.TryGetValue(WhitelistRule.NormalizeImagePath(entry.Path), out profile))
                        wake |= ObserveMonitoringLaunchLocked(profile, entry);
                }
            }
            // 持有游戏锁时不能获取监测锁。
            if (wake) PerformanceMonitoring.WakeForGameStart();
        }
    }
}
