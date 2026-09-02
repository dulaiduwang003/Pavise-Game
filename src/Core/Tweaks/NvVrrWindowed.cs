// @author bdth 2074055628@qq.com
// 文件用途 对局中把 NVIDIA 的 G-SYNC 从"仅全屏"补成"全屏加窗口" 退局按快照写回
using System;

namespace PaviseApp
{
    // 全局键 VRR_MODE 0 关 1 仅全屏 2 全屏加窗口 只在用户已经开了 G-SYNC 且只给全屏时才补
    //   用户关着 G-SYNC 的不动 那是显示器层面的选择 不该被一个开关替他改
    //   逐游戏那半边 VRR_APP_OVERRIDE 写允许 由 NvDrsTweaks 随游戏配置一起下发
    internal static class NvVrrWindowed
    {
        internal const string SnapKey = "NvVrrModeSnap";
        private const uint SettingVrrMode = 0x1194F158;
        private const uint VrrDisabled = 0;
        private const uint VrrFullscreenOnly = 1;
        private const uint VrrFullscreenAndWindowed = 2;
        private static readonly object lk = new object();

        public static bool HasResidue { get { return Settings.LoadStr(SnapKey, "").Length > 0; } }

        // 只把"仅全屏"补成"全屏加窗口" 其余取值一律不碰
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

        // 界面用 用户已开 G-SYNC 且只给全屏才有东西可补
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
                // 关着或已经是全屏加窗口 都是不适用 报成功不留账
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
                // 用户中途自己改过就不抢回来 只清账
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
