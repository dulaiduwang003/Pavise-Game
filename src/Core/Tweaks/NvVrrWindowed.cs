// @author bdth 2074055628@qq.com
// File purpose Expand NVIDIA G-SYNC from fullscreen-only to fullscreen plus windowed during a match; write back from snapshot at match end
using System;

namespace PaviseApp
{
    // Global key VRR_MODE: 0 off, 1 fullscreen only, 2 fullscreen plus windowed; only expand when the user already has G-SYNC on and fullscreen-only
    //   Leave users with G-SYNC off alone; that's a monitor-level choice a switch shouldn't change for them
    //   The per-game half, VRR_APP_OVERRIDE, is written as Allow by NvDrsTweaks along with the game profile
    internal static class NvVrrWindowed
    {
        internal const string SnapKey = "NvVrrModeSnap";
        private const uint SettingVrrMode = 0x1194F158;
        private const uint VrrDisabled = 0;
        private const uint VrrFullscreenOnly = 1;
        private const uint VrrFullscreenAndWindowed = 2;
        private static readonly object lk = new object();

        public static bool HasResidue { get { return Settings.LoadStr(SnapKey, "").Length > 0; } }

        // Only expand fullscreen-only to fullscreen plus windowed; every other value is left untouched
        internal static bool ShouldExpand(uint mode)
        {
            return mode == VrrFullscreenOnly;
        }

        private static bool ReadMode(out uint mode)
        {
            mode = VrrDisabled;
            IntPtr session;
            if (!NvApi.TryOpenSession(out session)) return false;
            try
            {
                IntPtr profile;
                if (!NvApi.TryGetBaseProfile(session, out profile)) return false;
                uint value;
                int found = NvApi.TryGetDword(session, profile, SettingVrrMode, out value);
                if (found < 0) return false;
                mode = found == 1 ? value : VrrDisabled;
                return true;
            }
            finally { NvApi.CloseSession(session); }
        }

        private static bool WriteMode(uint mode)
        {
            IntPtr session;
            if (!NvApi.TryOpenSession(out session)) return false;
            try
            {
                IntPtr profile;
                if (!NvApi.TryGetBaseProfile(session, out profile)) return false;
                if (!NvApi.SetDword(session, profile, SettingVrrMode, mode)) return false;
                return NvApi.SaveSession(session);
            }
            finally { NvApi.CloseSession(session); }
        }

        // For the UI: only when the user has G-SYNC on and fullscreen-only is there anything to expand
        public static bool Applicable()
        {
            uint mode;
            return NvApi.Available && ReadMode(out mode) && ShouldExpand(mode);
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (HasResidue) return true;
                uint mode;
                if (!NvApi.Available || !ReadMode(out mode)) { Logger.Warn(Lang.T("log.nvvrr.1")); return false; }
                // Off or already fullscreen plus windowed means not applicable; report success without a record
                if (!ShouldExpand(mode)) return true;
                Settings.SaveStr(SnapKey, mode.ToString());
                if (Settings.LoadStr(SnapKey, "") != mode.ToString()) return false;
                if (!WriteMode(VrrFullscreenAndWindowed))
                {
                    Settings.SaveStr(SnapKey, "");
                    Logger.Log(Lang.T("log.nvvrr.2"));
                    return false;
                }
                Logger.Log(Lang.T("log.nvvrr.3"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string raw = Settings.LoadStr(SnapKey, "");
                if (raw.Length == 0) return true;
                uint original;
                if (!uint.TryParse(raw, out original)) { Settings.SaveStr(SnapKey, ""); return true; }
                uint now;
                // User changed it mid-match; don't take it back, just clear the record
                if (ReadMode(out now) && now != VrrFullscreenAndWindowed)
                {
                    Settings.SaveStr(SnapKey, "");
                    Logger.Log(Lang.T("log.nvvrr.5"));
                    return true;
                }
                if (!WriteMode(original)) { Logger.Log(Lang.T("log.nvvrr.4")); return false; }
                Settings.SaveStr(SnapKey, "");
                Logger.Log(Lang.T("log.nvvrr.6"));
                return true;
            }
        }

        public static void HealFromCrash()
        {
            if (HasResidue && Restore()) Logger.Log(Lang.T("log.nvvrr.7"));
        }
    }
}
