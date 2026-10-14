// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class IrqRelocate
    {
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("IrqRelocateOn", "IrqReloc_", Lang.T("irqmove.logprefix"));

        internal static IrqRestoreResult RestoreDevices(string selected)
        {
            if (!Native.IsElevated()) return new IrqRestoreResult();
            var result = IrqRestoreResult.Run(selected,engine.TryTouchedDevices,IrqPriorityTweak.TryTouchedDevices,
                OwnedByOtherTweaks,delegate(string id) { return engine.RestoreOnly(new List<string> { id }); },
                IrqPriorityTweak.RestoreOnly);
            if (selected == null && result.ScopeReadSucceeded)
            {
                bool affinityPresent = false;
                foreach (var device in result.Devices) affinityPresent |= device.AffinityRequested;
                if (!affinityPresent)
                    try { result.AffinityFinalized = engine.Disable(null); }
                    catch { result.AffinityFinalized = false; }
            }
            return result;
        }

        public static bool HasResidue { get { return engine.HasResidue; } }

        public static bool Revert() { return engine.Disable(null); }

        public static void HealFromCrash()
        {
            try { if (engine.HasResidue && !engine.EnabledByPavise) Revert(); } catch { }
        }

        private static List<string> OwnedByOtherTweaks()
        {
            var owned = new List<string>();
            try
            {
                if (NetworkAffinityTweak.EnabledByPavise)
                    foreach (string id in NetworkAffinityTweak.EnumerateNicDeviceIds())
                        if (!Has(owned, id)) owned.Add(id);
            }
            catch { }
            try
            {
                foreach (string id in IrqAutoPilot.TouchedDevices())
                    if (!Has(owned, id)) owned.Add(id);
            }
            catch { }
            try
            {
                foreach (string id in GpuIrqCheck.TouchedDevices())
                    if (!Has(owned, id)) owned.Add(id);
            }
            catch { }
            return owned;
        }

        private static bool Has(List<string> list, string id)
        {
            if (id == null || id.Length == 0) return true;
            foreach (string had in list)
                if (string.Equals(had, id, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static string MaskText(ulong mask)
        {
            if (mask == 0) return "?";
            var sb = new System.Text.StringBuilder();
            int run = -1;
            for (int i = 0; i <= 64; i++)
            {
                bool on = i < 64 && (mask & (1UL << i)) != 0;
                if (on && run < 0) run = i;
                else if (!on && run >= 0)
                {
                    if (sb.Length > 0) sb.Append(',');
                    if (i - 1 == run) sb.Append(run);
                    else sb.Append(run).Append('-').Append(i - 1);
                    run = -1;
                }
            }
            return sb.ToString();
        }
    }
}
