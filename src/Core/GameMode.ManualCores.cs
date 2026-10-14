using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal partial class GameMode
    {
        // 按打开时的草稿比较，避免覆盖并发编辑。
        internal string SaveManualCorePlan(string profileId, string expected, ManualCorePlan plan)
        {
            if (plan != null)
            {
                string error = plan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask);
                if (error != null) return error;
            }
            lock (sync)
            {
                if (stopping || ProfileStoreSaveFailed) return "schedule.manual.savefailed";
                GameProfile profile = FindProfileLocked(profileId);
                if (profile == null || ManualCorePlan.Raw(profile) != expected) return "schedule.manual.changed";
                GameProfile replacement = profile.Clone();
                if (plan == null) replacement.Overrides.Remove(ManualCorePlan.Key);
                else
                {
                    replacement.Overrides[ManualCorePlan.Key] = plan.Encode();
                    if (ManualCorePlan.Parse(expected) == null) replacement.Overrides[PolicyCatalog.KeyAutoCores] = "0";
                }
                var next = new List<GameProfile>(profiles);
                int index = next.IndexOf(profile);
                next[index] = replacement;
                if (!SaveProfileSnapshotLocked(next)) return "schedule.manual.savefailed";
                profiles[index] = replacement;
                if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
                    activeDetection.Profile = replacement.Clone();
                if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
                    stickyDetection.Profile = replacement.Clone();
            }
            RequestPolicyApply();
            RaiseLibraryChanged();
            return null;
        }
    }
}
