using System.Globalization;

namespace PaviseApp
{
    internal static class GpuPowerMax
    {
        internal sealed class State
        {
            internal string Vendor;

            internal string Id;

            internal long Current;

            internal long Maximum;
        }

        internal const string SnapKey = "GpuPowerSnap";

        private static readonly object lk = new object();

        private static string VendorGpuId(GpuVendor vendor)
        {
            GpuAdapter gpuAdapter = null;
            foreach (GpuAdapter currentAdaptersResult in GpuInventory.Adapters())
            {
                if (currentAdaptersResult.Vendor == vendor)
                {
                    if (gpuAdapter != null)
                    {
                        return "";
                    }
                    gpuAdapter = currentAdaptersResult;
                }
            }
            object venDevKeyResult;
            if (gpuAdapter != null)
            {
                venDevKeyResult = GpuInventory.VenDevKey(gpuAdapter.HardwareId);
                if (venDevKeyResult == null)
                {
                    return "";
                }
            }
            else
            {
                venDevKeyResult = "";
            }
            return (string)venDevKeyResult;
        }

        private static bool Read(string vendor, out State state)
        {
            state = null;
            GpuVendor vendor2 = ((vendor == "nv") ? GpuVendor.Nvidia : GpuVendor.Amd);
            string vendorGpuIdResult = VendorGpuId(vendor2);
            if (vendorGpuIdResult.Length == 0)
            {
                return false;
            }
            if (vendor == "nv")
            {
                uint current;
                uint def;
                uint max;
                if (!NvApi.Available || !NvApi.TryGetPowerLimit(out current, out def, out max) || current == 0 || max < current)
                {
                    return false;
                }
                state = new State
                {
                    Vendor = vendor,
                    Id = vendorGpuIdResult,
                    Current = current,
                    Maximum = max
                };
            }
            else
            {
                int current2;
                int max2;
                if (!AdlxTweaks.PowerLimitGetFirst(out current2, out max2) || max2 < current2)
                {
                    return false;
                }
                state = new State
                {
                    Vendor = vendor,
                    Id = vendorGpuIdResult,
                    Current = current2,
                    Maximum = max2
                };
            }
            return true;
        }

        private static bool Write(State state, long value)
        {
            State currentState;
            if (!Read(state.Vendor, out currentState) || currentState.Id != state.Id || value > currentState.Maximum)
            {
                return false;
            }
            if (state.Vendor == "nv")
            {
                if (value > 0 && value <= uint.MaxValue)
                {
                    return NvApi.TrySetPowerLimit((uint)value);
                }
                return false;
            }
            if (value >= int.MinValue && value <= int.MaxValue)
            {
                return AdlxTweaks.PowerLimitSetFirst((int)value);
            }
            return false;
        }

        private static bool Save(string text)
        {
            if (Settings.SaveStrDurable("GpuPowerSnap", text))
            {
                return Settings.LoadStr("GpuPowerSnap", "!") == text;
            }
            return false;
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string text;
                if (!Settings.TryLoadStr("GpuPowerSnap", out text))
                {
                    return false;
                }
                if (text.Length == 0)
                {
                    return true;
                }
                string[] valueParts = text.Split(':');
                long result;
                long result2;
                if (valueParts.Length != 4 || (valueParts[0] != "nv" && valueParts[0] != "amd") || valueParts[1].Length == 0 || !long.TryParse(valueParts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || !long.TryParse(valueParts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out result2) || result > result2)
                {
                    return false;
                }
                State state;
                if (!Read(valueParts[0], out state) || state.Id != valueParts[1])
                {
                    return false;
                }
                if (state.Current != result2)
                {
                    return Save("");
                }
                if (!Write(state, result))
                {
                    return false;
                }
                State currentState;
                if (!Read(valueParts[0], out currentState) || currentState.Id != valueParts[1] || currentState.Current != result)
                {
                    return false;
                }
                if (!Save(""))
                {
                    return false;
                }
                Logger.Log("GPU 功耗上限：原值已恢复并核验");
                return true;
            }
        }

        public static bool HasResidue()
        {
            string value;
            if (Settings.TryLoadStr("GpuPowerSnap", out value))
            {
                return !string.IsNullOrEmpty(value);
            }
            return true;
        }

        public static void HealFromCrash()
        {
            if (HasResidue())
            {
                Restore();
            }
        }
    }
}
