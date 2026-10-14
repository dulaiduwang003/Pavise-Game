using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        private static volatile bool idlePolicyActive;
        internal static bool IdlePolicyActive { get { return idlePolicyActive && active && targetOwned; } }
        internal static bool IdlePolicyHasResidue
        {
            get { string raw; return !Settings.TryLoadStr(ExtremeSnapKey, out raw) || !string.IsNullOrEmpty(raw); }
        }

        private static bool ApplyExtremeIdlePolicy(Guid scheme, bool wanted)
        {
            idlePolicyActive = false;
            if (!wanted) return PrepareExtremeKnobs(scheme);
            List<ExtremeSavedValue> savedValues;
            if (!ReadExtremeSnapshot(scheme, out savedValues))
            {
                if (!DropOrphanExtremeSnapshot(scheme) || !ReadExtremeSnapshot(scheme, out savedValues))
                { extremeTunePending = true; return false; }
            }
            Guid[] settings = { IdlePromote, IdleScaling };
            foreach (Guid setting in settings)
            {
                if (savedValues.Exists(delegate(ExtremeSavedValue v) { return v.Setting == setting; })) continue;
                uint? ac = ReadExtremeIndex(scheme, setting, true), dc = ReadExtremeIndex(scheme, setting, false);
                if (!ac.HasValue || !dc.HasValue) { extremeTunePending = true; return false; }
                savedValues.Add(new ExtremeSavedValue { Setting = setting, Ac = ac.Value, Dc = dc.Value });
            }
            // 首次写入前保存 AC、DC 原值，重试不覆盖。
            if (!SaveExtremeSnapshot(scheme, savedValues)) { extremeTunePending = true; return false; }
            bool applied = true;
            foreach (Guid setting in settings)
            {
                uint value = setting == IdlePromote ? 100u : 0u;
                applied &= RestoreExtremeIndex(scheme, setting, true, value);
                applied &= RestoreExtremeIndex(scheme, setting, false, value);
            }
            if (!applied) RestoreExtremeKnobs(scheme);
            extremeTunePending = !applied;
            idlePolicyActive = applied;
            return applied;
        }

        private static bool RestoreOwnedIdlePolicy()
        {
            idlePolicyActive = false;
            string raw;
            if (!Settings.TryLoadStr(ExtremeSnapKey, out raw)) return false;
            if (string.IsNullOrEmpty(raw)) return true;
            Guid owner;
            string[] parts = raw.Split('|');
            if (parts.Length == 3 && parts[0] == "2")
            { if (!Guid.TryParse(parts[1], out owner) || owner == Guid.Empty) return false; }
            else if (parts.Length == 1)
            { if (!Guid.TryParse(Settings.LoadStr(ManagedPlanKey, ""), out owner) || owner == Guid.Empty) return false; }
            else return false;
            if (!RestoreExtremeKnobs(owner)) return false;
            tuneState = -1;
            return true;
        }

#if PAVISE_SELFTEST
        internal static bool ApplyExtremeIdleForTest(Guid scheme, bool wanted)
        { return ApplyExtremeIdlePolicy(scheme, wanted); }
#endif
    }
}
