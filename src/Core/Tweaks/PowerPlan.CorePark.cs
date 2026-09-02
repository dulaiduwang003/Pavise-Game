// @author bdth 2074055628@qq.com
// 文件用途 检测当前电源方案核心停泊状态(体检用) 并清理旧版遗留的解停泊快照
using System;

namespace PaviseApp
{
    internal static partial class PowerPlan
    {
        private const string ParkSnapKey = "CoreParkSnap";
        private const string ParkTriesKey = "CoreParkTries";
        private const string ParkAbsent = "-";
        private const int MaxRestoreTries = 3;

        public static bool HasParkResidue()
        {
            return Settings.LoadStr(ParkSnapKey, "").Length > 0;
        }

        public static bool TryCurrentUnparked(out bool unparked)
        {
            unparked = false;
            Guid? cur = Current();
            if (!cur.HasValue) return false;
            Guid scheme = cur.Value, sub = SubProcessor, set = CpMinCores;
            uint ac;
            if (PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref set, out ac) != 0) return false;
            unparked = ac >= 100;
            return true;
        }

        private static void ReadPark(Guid scheme, Guid setting,
            out bool haveAc, out uint ac, out bool haveDc, out uint dc)
        {
            Guid sub = SubProcessor, set = setting, sc = scheme;
            haveAc = PowerReadACValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, out ac) == 0;
            sc = scheme; sub = SubProcessor; set = setting;
            haveDc = PowerReadDCValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, out dc) == 0;
        }

        public static bool RestoreParkState()
        {
            string s = Settings.LoadStr(ParkSnapKey, "");
            if (s.Length == 0) return true;
            string[] parts = s.Split('|');
            if (parts.Length < 5) { ClearParkSnap(); return true; }
            Guid scheme;
            try { scheme = new Guid(parts[0]); }
            catch { ClearParkSnap(); return true; }

            bool ok = WriteBack(scheme, CpMinCores, parts[1], parts[2]);
            ok &= WriteBack(scheme, CpMinCores1, parts[3], parts[4]);
            if (ok)
            {
                Guid? nowActive = Current();
                if (nowActive.HasValue && nowActive.Value == scheme) Set(scheme);
                ClearParkSnap();
                Logger.Log(Lang.T("log.powerplancorepark.4"));
                return true;
            }

            int tries;
            int.TryParse(Settings.LoadStr(ParkTriesKey, "0"), out tries);
            if (++tries >= MaxRestoreTries)
            {
                ClearParkSnap();
                Logger.Warn(Lang.T("log.powerplancorepark.8"));
                return true;
            }
            Settings.SaveStr(ParkTriesKey, tries.ToString());
            Logger.Log(Lang.T("log.powerplancorepark.3"));
            return false;
        }

        private static void ClearParkSnap()
        {
            Settings.SaveStr(ParkSnapKey, "");
            Settings.Remove(ParkTriesKey);
        }

        private static bool WriteBack(Guid scheme, Guid setting, string acStr, string dcStr)
        {
            try
            {
                uint curAc, curDc; bool haveAc, haveDc;
                ReadPark(scheme, setting, out haveAc, out curAc, out haveDc, out curDc);
                Guid sub = SubProcessor, set = setting, sc = scheme;
                uint v;
                if (acStr != ParkAbsent && uint.TryParse(acStr, out v)
                    && haveAc && curAc == 100
                    && PowerWriteACValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, v) != 0) return false;
                sc = scheme; sub = SubProcessor; set = setting;
                if (dcStr != ParkAbsent && uint.TryParse(dcStr, out v)
                    && haveDc && curDc == 100
                    && PowerWriteDCValueIndex(IntPtr.Zero, ref sc, ref sub, ref set, v) != 0) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
