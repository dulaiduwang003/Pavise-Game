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
            // Runner 持有 operationGate 时也会调用，只读原子量/冻结副本，不取得 sync。
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
                if (mode != PerformancePreset.Competitive && mode != PerformancePreset.Extreme
                    && mode != PerformancePreset.Handheld) return false;
                if (mode == PerformancePreset.Extreme
                    && ExtremeMode.ForcedPolicyValue(PolicyCatalog.KeyPowerYield) == "1") return true;
                return hasOverride ? overrideOn : PowerBudgetYieldRunner.EnabledSetting;
            };
        }
    }
}
