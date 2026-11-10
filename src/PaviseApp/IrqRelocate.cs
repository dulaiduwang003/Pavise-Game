using System;
using System.Collections.Generic;
using System.Text;

namespace PaviseApp
{
    internal static class IrqRelocate
    {
        private static readonly IrqAffinityEngine engine = new IrqAffinityEngine("IrqRelocateOn", "IrqReloc_", Lang.T("irqmove.logprefix"));

        public static bool HasResidue
        {
            get
            {
                return engine.HasResidue;
            }
        }

        internal static IrqRestoreResult RestoreDevices(string selected)
        {
            if (!Native.IsElevated())
            {
                return new IrqRestoreResult();
            }
            IrqRestoreResult irqRestoreResult = IrqRestoreResult.Run(selected, engine.TryTouchedDevices, IrqPriorityTweak.TryTouchedDevices, OwnedByOtherTweaks, (string id) => engine.RestoreOnly(new List<string> { id }), IrqPriorityTweak.RestoreOnly);
            if (selected == null && irqRestoreResult.ScopeReadSucceeded)
            {
                bool affinityRequested = false;
                foreach (IrqDeviceRestoreResult device in irqRestoreResult.Devices)
                {
                    affinityRequested |= device.AffinityRequested;
                }
                if (!affinityRequested)
                {
                    try
                    {
                        irqRestoreResult.AffinityFinalized = engine.Disable(null);
                    }
                    catch
                    {
                        irqRestoreResult.AffinityFinalized = false;
                    }
                }
            }
            return irqRestoreResult;
        }

        public static bool Revert()
        {
            return engine.Disable(null);
        }

        public static void HealFromCrash()
        {
            try
            {
                if (engine.HasResidue && !engine.EnabledByPavise)
                {
                    Revert();
                }
            }
            catch
            {
            }
        }

        private static List<string> OwnedByOtherTweaks()
        {
            List<string> texts = new List<string>();
            try
            {
                if (NetworkAffinityTweak.EnabledByPavise)
                {
                    foreach (string enumerateNicDeviceId in NetworkAffinityTweak.EnumerateNicDeviceIds())
                    {
                        if (!Has(texts, enumerateNicDeviceId))
                        {
                            texts.Add(enumerateNicDeviceId);
                        }
                    }
                }
            }
            catch
            {
            }
            try
            {
                foreach (string touchedDevice in IrqAutoPilot.TouchedDevices())
                {
                    if (!Has(texts, touchedDevice))
                    {
                        texts.Add(touchedDevice);
                    }
                }
            }
            catch
            {
            }
            try
            {
                foreach (string currentTouchedDevice in GpuIrqCheck.TouchedDevices())
                {
                    if (!Has(texts, currentTouchedDevice))
                    {
                        texts.Add(currentTouchedDevice);
                    }
                }
            }
            catch
            {
            }
            return texts;
        }

        private static bool Has(List<string> list, string id)
        {
            if (id == null || id.Length == 0)
            {
                return true;
            }
            foreach (string item in list)
            {
                if (string.Equals(item, id, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        internal static string MaskText(ulong mask)
        {
            if (mask == 0)
            {
                return "?";
            }
            StringBuilder stringBuilder = new StringBuilder();
            int rangeStart = -1;
            for (int i = 0; i <= 64; i++)
            {
                bool selected = i < 64 && (mask & (ulong)(1L << i)) != 0;
                if (selected && rangeStart < 0)
                {
                    rangeStart = i;
                }
                else if (!selected && rangeStart >= 0)
                {
                    if (stringBuilder.Length > 0)
                    {
                        stringBuilder.Append(',');
                    }
                    if (i - 1 == rangeStart)
                    {
                        stringBuilder.Append(rangeStart);
                    }
                    else
                    {
                        stringBuilder.Append(rangeStart).Append('-').Append(i - 1);
                    }
                    rangeStart = -1;
                }
            }
            return stringBuilder.ToString();
        }
    }
}
