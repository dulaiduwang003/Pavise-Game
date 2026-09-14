// @author bdth 2074055628@qq.com
// File purpose Disable Intel Endurance Gaming during a match so frame rate is no longer capped at the panel refresh rate on battery; write back from snapshot at match end
using System;

namespace PaviseApp
{
    // Per Intel's own docs, on battery Endurance Gaming caps frame rate at a fraction of the panel refresh rate, commonly landing around 30 fps
    //   It does nothing on AC power, so it only matters on machines with a battery; disabling is a battery-life vs frame-rate tradeoff, default off
    //   Control value: 0 off, 1 on, 2 auto; write 0 only when 1 or 2 is read, write the original back at match end
    internal static class IntelEndurance
    {
        internal const string SnapKey = "IntelEnduranceSnap";
        private const int ControlOff = 0;
        private static readonly object lk = new object();
        private static readonly IntelGraphicsApi api = new IntelGraphicsApi();

        public static bool HasResidue { get { return Settings.LoadStr(SnapKey, "").Length > 0; } }

        internal static bool Wanted(int control)
        {
            return control != ControlOff;
        }

        public static bool Supported()
        {
            int control, mode;
            return Native.HasSystemBattery() && api.TryReadEndurance(out control, out mode);
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (HasResidue) return true;
                int control, mode;
                if (!api.TryReadEndurance(out control, out mode)) { Logger.Warn(Lang.T("log.intelend.1")); return false; }
                if (!Wanted(control)) return true;
                string snap = control + "|" + mode;
                Settings.SaveStr(SnapKey, snap);
                if (Settings.LoadStr(SnapKey, "") != snap) return false;
                int check, checkMode;
                if (!api.TryWriteEndurance(ControlOff, mode) || !api.TryReadEndurance(out check, out checkMode)
                    || check != ControlOff)
                {
                    Settings.SaveStr(SnapKey, "");
                    Logger.Log(Lang.T("log.intelend.2"));
                    return false;
                }
                Logger.Log(Lang.T("log.intelend.3"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string raw = Settings.LoadStr(SnapKey, "");
                if (raw.Length == 0) return true;
                string[] parts = raw.Split('|');
                int original, originalMode;
                if (parts.Length != 2 || !int.TryParse(parts[0], out original) || !int.TryParse(parts[1], out originalMode))
                { Settings.SaveStr(SnapKey, ""); return true; }
                int now, nowMode;
                if (api.TryReadEndurance(out now, out nowMode) && now != ControlOff)
                {
                    // User or Intel's tool changed it mid-match; don't take it back, just clear the record
                    Settings.SaveStr(SnapKey, "");
                    return true;
                }
                if (!api.TryWriteEndurance(original, originalMode)) { Logger.Log(Lang.T("log.intelend.4")); return false; }
                Settings.SaveStr(SnapKey, "");
                Logger.Log(Lang.T("log.intelend.5"));
                return true;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue && Restore()) Logger.Log(Lang.T("log.intelend.6"));
        }
    }
}
