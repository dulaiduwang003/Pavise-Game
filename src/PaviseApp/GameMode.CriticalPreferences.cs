using System;
using System.Collections.Generic;
using System.Threading;

namespace PaviseApp
{
    internal partial class GameMode
    {
        private GameProfile criticalSessionPreference;
        private PolicySnapshot coreSessionPolicy;
        private long criticalPreferenceSerial;

        private void CaptureSessionCorePreferencesLocked(GameProfile profile, PolicySnapshot policy)
        {
            if (!Monitor.IsEntered(sync)) throw new InvalidOperationException("Session preference gate required");
            coreSessionPolicy = policy;
            criticalPreferenceSerial = gameSessionStarts;
            criticalSessionPreference = profile == null ? null : profile.Clone();
            if (criticalSessionPreference != null)
                criticalSessionPreference.Overrides["GmAutoCoresV2"] = policy.ValueOf("GmAutoCoresV2");
        }

        private GameProfile CriticalPreferenceProfileLocked(string profileId)
        {
            if (!Monitor.IsEntered(sync)) throw new InvalidOperationException("Session preference gate required");
            GameProfile stored = FindProfileLocked(profileId);
            if (stored == null) return null;
            if (active && criticalSessionPreference != null && criticalPreferenceSerial == gameSessionStarts
                && string.Equals(criticalSessionPreference.Id, profileId, StringComparison.OrdinalIgnoreCase))
                return criticalSessionPreference;
            return stored;
        }

        private void DisableSessionCriticalPreferenceLocked(string profileId)
        {
            if (criticalSessionPreference != null && string.Equals(criticalSessionPreference.Id, profileId, StringComparison.OrdinalIgnoreCase))
                criticalSessionPreference.Overrides.Remove(CriticalThreadRule.Key);
        }

        internal static string ValidateCriticalPreference(GameProfile profile)
        {
            string raw = CriticalThreadRule.Raw(profile);
            if (raw == null) return null;
            CriticalThreadRule rule = CriticalThreadRule.ParseForRuntime(raw);
            if (rule == null) return PerformancePolicy.Text("渲染亲和性规则无效，请重新设置", "The render affinity rule is invalid; configure it again");
            CriticalThreadAllowedRange scope = CriticalThreadAllowedRange.For(profile);
            return scope.Error ?? rule.Validate(CpuTopology.AllMask, CpuTopology.PhysicalCoreMasks(), scope.Mask,
                AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask,
                IntPtr.Size == 8 && !CpuTopology.MultiGroup && CpuTopology.TopologySourcesAgree && !CpuTopology.AllMaskReconciled);
        }

        internal string SaveCoreConfiguration(string profileId, string expectedPlan, string expectedRule, string expectedEnabled,
            ManualCorePlan plan, bool enabled, CriticalThreadRule renderRule)
        {
            string error;
            bool withdraw;
            lock (sync)
                error = SaveCoreConfigurationLocked(profileId, expectedPlan, expectedRule, expectedEnabled, plan, enabled, renderRule, out withdraw);
            if (error != null) return error;
            if (withdraw) RequestPolicyApply("DisableRenderFromCoreConfiguration");
            RaiseLibraryChanged();
            return null;
        }

        private string SaveCoreConfigurationLocked(string profileId, string expectedPlan, string expectedRule, string expectedEnabled,
            ManualCorePlan plan, bool enabled, CriticalThreadRule renderRule, out bool withdraw)
        {
            withdraw = false;
            if (stopping || ProfileStoreSaveFailed) return "schedule.manual.savefailed";
            GameProfile current = FindProfileLocked(profileId);
            if (current == null || ManualCorePlan.Raw(current) != expectedPlan || CriticalThreadRule.Raw(current) != expectedRule
                || PolicyResolver.For(current).ValueOf("GmAutoCoresV2") != expectedEnabled)
                return "schedule.manual.changed";
            if (plan != null)
            {
                string validation = plan.ValidateCurrent(AutoCores.LiveAllocated() & ~CoreIsolationClient.ActiveMask);
                if (validation != null) return validation;
            }
            if (enabled && plan == null) return "schedule.manual.required";
            GameProfile next = current.Clone();
            if (plan == null) next.Overrides.Remove("GmManualCorePlanV2");
            else next.Overrides["GmManualCorePlanV2"] = plan.Encode();
            next.Overrides["GmAutoCoresV2"] = enabled ? "1" : "0";
            if (renderRule == null) next.Overrides.Remove(CriticalThreadRule.Key);
            else next.Overrides[CriticalThreadRule.Key] = renderRule.Encode();
            string conflict = ValidateCriticalPreference(next);
            if (conflict != null) return conflict;
            if (ManualCorePlan.Raw(next) == expectedPlan && CriticalThreadRule.Raw(next) == expectedRule
                && (enabled ? "1" : "0") == expectedEnabled) return null;
            List<GameProfile> list = new List<GameProfile>(profiles);
            int index = list.IndexOf(current);
            list[index] = next;
            if (!SaveProfileSnapshotLocked(list)) return "schedule.manual.savefailed";
            profiles[index] = next;
            if (activeDetection != null && activeDetection.Profile != null && activeDetection.Profile.Id == profileId)
                activeDetection.Profile = next.Clone();
            if (stickyDetection != null && stickyDetection.Profile != null && stickyDetection.Profile.Id == profileId)
                stickyDetection.Profile = next.Clone();
            if (renderRule == null)
            {
                DisableSessionCriticalPreferenceLocked(profileId);
                CriticalRuntimeSource source = Volatile.Read(ref criticalSource);
                withdraw = source != null && source.Profile == profileId;
                if (withdraw) RevokeCriticalThreadIntent("DisableRenderFromCoreConfiguration");
            }
            return null;
        }
    }
}
