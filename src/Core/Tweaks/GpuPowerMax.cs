using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class GpuPowerMax
    {
        internal const string SnapKey = "GpuPowerSnap";
        private static readonly object lk = new object();
        internal sealed class State
        {
            internal string Vendor, Id;
            internal long Current, Maximum;
        }
        private static string VendorGpuId(GpuVendor vendor)
        {
            GpuAdapter only = null;
            foreach (GpuAdapter a in GpuInventory.Adapters())
            {
                if (a.Vendor != vendor) continue;
                if (only != null) return "";
                only = a;
            }
            return only == null ? "" : GpuInventory.VenDevKey(only.HardwareId) ?? "";
        }
        private static bool Read(string vendor, out State state)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            state = ReadForTest == null ? null : ReadForTest(vendor);
            return state != null;
#else
            state = null;
            GpuVendor kind = vendor == "nv" ? GpuVendor.Nvidia : GpuVendor.Amd;
            string id = VendorGpuId(kind);
            if (id.Length == 0) return false;
            if (vendor == "nv")
            {
                uint current, def, maximum;
                if (!NvApi.Available || !NvApi.TryGetPowerLimit(out current, out def, out maximum)
                    || current == 0 || maximum < current) return false;
                state = new State { Vendor = vendor, Id = id, Current = current, Maximum = maximum };
            }
            else
            {
                int current, maximum;
                if (!AdlxTweaks.PowerLimitGetFirst(out current, out maximum) || maximum < current) return false;
                state = new State { Vendor = vendor, Id = id, Current = current, Maximum = maximum };
            }
            return true;
#endif
        }
        private static bool Write(State state, long value)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (WriteForTest == null) throw new InvalidOperationException("Unmocked GPU power write");
            return WriteForTest(state, value);
#else
            State live;
            if (!Read(state.Vendor, out live) || live.Id != state.Id || value > live.Maximum) return false;
            if (state.Vendor == "nv")
                return value > 0 && value <= uint.MaxValue && NvApi.TrySetPowerLimit((uint)value);
            return value >= int.MinValue && value <= int.MaxValue && AdlxTweaks.PowerLimitSetFirst((int)value);
#endif
        }
        private static bool Save(string text)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            if (DenySaveForTest) return false;
#endif
            return Settings.SaveStrDurable(SnapKey, text) && Settings.LoadStr(SnapKey, "!") == text;
        }
        public static bool Restore()
        {
            lock (lk)
            {
                string snap;
                if (!Settings.TryLoadStr(SnapKey, out snap)) return false;
                if (snap.Length == 0) return true;
                string[] parts = snap.Split(':');
                long previous, applied;
                if (parts.Length != 4 || (parts[0] != "nv" && parts[0] != "amd") || parts[1].Length == 0
                    || !long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out previous)
                    || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out applied)
                    || previous > applied) return false;
                State now;
                if (!Read(parts[0], out now) || now.Id != parts[1]) return false;
                // 驱动或用户后续设置优先。
                if (now.Current != applied) return Save("");
                if (!Write(now, previous)) return false;
                State check;
                if (!Read(parts[0], out check) || check.Id != parts[1] || check.Current != previous) return false;
                if (!Save("")) return false;
                Logger.Log("GPU power limit: original value restored and verified");
                return true;
            }
        }
        public static bool HasResidue()
        { string snap; return !Settings.TryLoadStr(SnapKey, out snap) || !string.IsNullOrEmpty(snap); }
        public static void HealFromCrash()
        { if (HasResidue()) Restore(); }
#if PAVISE_SELFTEST || PAVISE_PERFLAB
        internal static Func<string, State> ReadForTest;
        internal static Func<State, long, bool> WriteForTest;
        internal static bool DenySaveForTest;
#endif
    }
}
