// 作者：bdth 2074055628@qq.com
using System;

namespace PaviseApp
{
    internal static class NvVrrWindowed
    {
        internal const string SnapKey = "NvVrrModeSnap";
        private const uint SettingVrrMode = 0x1194F158;
        private const uint VrrDisabled = 0;
        private const uint VrrFullscreenOnly = 1;
        private const uint VrrFullscreenAndWindowed = 2;
        private static readonly object lk = new object();
#if PAVISE_SELFTEST
        internal static Func<uint?> ReadForTest;
        internal static Func<uint, bool> WriteForTest;
#endif
        private static bool DriverAvailable
        {
            get
            {
#if PAVISE_SELFTEST
                return ReadForTest != null;
#else
                return NvApi.Available;
#endif
            }
        }

        public static bool HasResidue { get { return Settings.LoadStr(SnapKey, "").Length > 0; } }

        // 仅把已启用的全屏 G-SYNC 扩为窗口模式。
        internal static bool ShouldExpand(uint mode)
        {
            return mode == VrrFullscreenOnly;
        }

        private static bool ReadMode(out uint mode)
        {
            mode = VrrDisabled;
#if PAVISE_SELFTEST
            if (ReadForTest == null) return false;
            uint? testMode = ReadForTest();
            if (!testMode.HasValue) return false;
            mode = testMode.Value; return true;
#else
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
#endif
        }

        private static bool WriteMode(uint mode)
        {
#if PAVISE_SELFTEST
            if (WriteForTest == null) throw new InvalidOperationException("G-SYNC writes must be mocked");
            return WriteForTest(mode);
#else
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
#endif
        }

        public static bool Applicable()
        {
            uint mode;
            return DriverAvailable && ReadMode(out mode) && ShouldExpand(mode);
        }

        public static bool Activate()
        {
            lock (lk)
            {
                if (HasResidue)
                {
                    uint current;
                    if (!ReadMode(out current)) return false;
                    if (current == VrrFullscreenAndWindowed) return true;
                    if (!Restore()) return false;
                }
                uint mode;
                if (!DriverAvailable || !ReadMode(out mode)) { Logger.Warn(Lang.T("log.nvvrr.1")); return false; }
                if (!ShouldExpand(mode)) return true;
                Settings.SaveStr(SnapKey, mode.ToString());
                if (Settings.LoadStr(SnapKey, "") != mode.ToString()) return false;
                if (!WriteMode(VrrFullscreenAndWindowed))
                {
                    Logger.Log(Lang.T("log.nvvrr.2"));
                    return false;
                }
                uint applied;
                bool verified = ReadMode(out applied) && applied == VrrFullscreenAndWindowed;
                if (verified) Logger.Log(Lang.T("log.nvvrr.3"));
                return verified;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string raw = Settings.LoadStr(SnapKey, "");
                if (raw.Length == 0) return true;
                uint original;
                if (!uint.TryParse(raw, out original) || original != VrrFullscreenOnly) return false;
                uint now;
                if (!ReadMode(out now)) return false;
                if (now != VrrFullscreenAndWindowed)
                {
                    Logger.Log(Lang.T("log.nvvrr.5"));
                    return Settings.SaveStr(SnapKey, "");
                }
                if (!WriteMode(original)) { Logger.Log(Lang.T("log.nvvrr.4")); return false; }
                if (!ReadMode(out now) || now != original || !Settings.SaveStr(SnapKey, "")) return false;
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
