// @author bdth 2074055628@qq.com
// 文件用途 对局中把显卡功耗墙拉到厂商允许的上限 退局按快照还原
using System;
using System.Globalization;

namespace PaviseApp
{
    internal static class GpuPowerMax
    {
        private const string SnapKey = "GpuPowerSnap";
        private static readonly object lk = new object();

        private static string VendorGpuId(GpuVendor vendor)
        {
            GpuAdapter best = null;
            foreach (GpuAdapter a in GpuInventory.Adapters())
                if (a.Vendor == vendor && (best == null || (best.Integrated && !a.Integrated))) best = a;
            if (best == null) return "";
            return GpuInventory.VenDevKey(best.HardwareId) ?? "";
        }

        private static bool VendorAbsent(GpuVendor vendor)
        {
            GpuAdapter[] all = GpuInventory.Adapters();
            if (all == null || all.Length == 0) return false;
            foreach (GpuAdapter a in all) if (a.Vendor == vendor) return false;
            return true;
        }

        public static bool Supported()
        {
            uint nvCur, nvDef, nvMax;
            if (NvApi.Available && NvApi.TryGetPowerLimit(out nvCur, out nvDef, out nvMax))
                return nvMax > nvCur;
            return false;
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (Settings.LoadStr(SnapKey, "").Length > 0) return true;

                uint nvCur, nvDef, nvMax;
                if (NvApi.Available && NvApi.TryGetPowerLimit(out nvCur, out nvDef, out nvMax))
                {
                    if (nvCur >= nvMax) return true;
                    Settings.SaveStr(SnapKey, "nv:" + VendorGpuId(GpuVendor.Nvidia)
                        + ":" + nvCur.ToString(CultureInfo.InvariantCulture)
                        + ":" + nvMax.ToString(CultureInfo.InvariantCulture));
                    if (Settings.LoadStr(SnapKey, "").Length == 0)
                    {
                        Logger.Log(Lang.T("log.gpupowermax.1"));
                        return false;
                    }
                    if (!NvApi.TrySetPowerLimit(nvMax))
                    {
                        Settings.SaveStr(SnapKey, "");
                        Logger.Log(Lang.T("log.gpupowermax.2"));
                        return false;
                    }
                    Logger.Log(Lang.T("log.gpupowermax.3") + (nvMax / 1000) + Lang.T("log.gpupowermax.4")
                        + (nvCur / 1000) + Lang.T("log.gpupowermax.5"));
                    return true;
                }

                Logger.Log(Lang.T("log.gpupowermax.9"));
                return false;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string snap = Settings.LoadStr(SnapKey, "");
                if (snap.Length == 0) return true;
                string[] parts = snap.Split(':');
                if (parts.Length != 4)
                {
                    Settings.SaveStr(SnapKey, "");
                    Logger.Log(Lang.T("log.gpupowermax.13"));
                    return true;
                }
                string vendor = parts[0], id = parts[1], val = parts[2], appliedStr = parts[3];
                GpuVendor snapVendor = vendor == "nv" ? GpuVendor.Nvidia
                    : vendor == "amd" ? GpuVendor.Amd : GpuVendor.Unknown;
                if (snapVendor != GpuVendor.Unknown)
                {
                    string curId = VendorGpuId(snapVendor);
                    bool swapped = VendorAbsent(snapVendor)
                        || (id.Length > 0 && curId.Length > 0
                            && !string.Equals(id, curId, StringComparison.OrdinalIgnoreCase));
                    if (swapped)
                    {
                        Settings.SaveStr(SnapKey, "");
                        Logger.Log(Lang.T("log.gpupowermax.14"));
                        return true;
                    }
                }
                bool ok = false;
                if (snapVendor == GpuVendor.Nvidia)
                {
                    uint prev, applied, curNow, defNow, maxNow;
                    if (uint.TryParse(appliedStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out applied)
                        && NvApi.TryGetPowerLimit(out curNow, out defNow, out maxNow) && curNow != applied)
                    {
                        Settings.SaveStr(SnapKey, "");
                        Logger.Log(Lang.T("log.gpupowermax.15"));
                        return true;
                    }
                    ok = uint.TryParse(val, NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out prev) && NvApi.TrySetPowerLimit(prev);
                }
                else ok = true;
                if (!ok)
                {
                    Logger.Log(Lang.T("log.gpupowermax.10"));
                    return false;
                }
                Settings.SaveStr(SnapKey, "");
                Logger.Log(Lang.T("log.gpupowermax.11"));
                return true;
            }
        }

        public static bool HasResidue() { return Settings.LoadStr(SnapKey, "").Length > 0; }

        public static void HealFromCrash()
        {
            if (HasResidue() && Restore())
                Logger.Log(Lang.T("log.gpupowermax.12"));
        }
    }
}
