using System;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private int powerYieldGeneration;

        private Func<bool> CapturePowerYieldAdmission(int rendererPid, long rendererCreation)
        {
            PolicySnapshot snapshot;
            int captured;
            bool hasOverride = false, overrideOn = false, profileExists = true;
            lock (sync)
            {
                snapshot = sessionPolicy;
                captured = Volatile.Read(ref powerYieldGeneration);
                if (snapshot != null && !string.IsNullOrEmpty(snapshot.ProfileId))
                {
                    GameProfile profile = FindProfileLocked(snapshot.ProfileId);
                    profileExists = profile != null;
                    string value;
                    if (profile != null && profile.Overrides.TryGetValue(PolicyCatalog.KeyPowerYield, out value))
                    { hasOverride = true; overrideOn = value == "1"; }
                }
            }
            // Also called while Runner holds operationGate, reads only atomics/frozen copies, never takes sync
            return delegate
            {
                if (!profileExists || captured != Volatile.Read(ref powerYieldGeneration)
                    || !object.ReferenceEquals(snapshot, sessionPolicy)
                    || !active || !enabled || stopping || panicReq || ProfileStoreSaveFailed
                    || Volatile.Read(ref stickyGraceOnly) || Volatile.Read(ref gameGoneSinceTicks) != 0) return false;
                GameDetection detection = Volatile.Read(ref activeDetection);
                if (detection == null || detection.RendererPid != rendererPid
                    || detection.RendererCreation != rendererCreation) return false;
                PerformancePreset mode = snapshot != null ? snapshot.Preset : preset;
                if (mode != PerformancePreset.Competitive
                    && mode != PerformancePreset.Handheld) return false;
                return hasOverride ? overrideOn : PowerBudgetYieldRunner.EnabledSetting;
            };
        }
    }
}
