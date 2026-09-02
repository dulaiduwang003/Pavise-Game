// @author bdth 2074055628@qq.com
// 文件用途 开启 AMD Smart Access Memory 重启生效 关闭开关按记录关回
using System;

namespace PaviseApp
{
    // SAM 就是 AMD 侧的 Resizable BAR NVIDIA 那边逐游戏写驱动配置 AMD 这边是全局开关且要重启
    //   ADLX 报支持才提供 用户本来就开着的不记账 关闭开关时只关 Pavise 自己开的
    internal static class AmdSamTweak
    {
        private const string OnKey = "AmdSamByPavise";
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool HasResidue() { return EnabledByPavise; }

        public static bool Supported()
        {
            bool supported, enabled;
            return AdlxTweaks.SamGet(out supported, out enabled) && supported;
        }

        public static bool CurrentlyOn()
        {
            bool supported, enabled;
            return AdlxTweaks.SamGet(out supported, out enabled) && supported && enabled;
        }

        public static string Describe()
        {
            bool supported, enabled;
            if (!AdlxTweaks.SamGet(out supported, out enabled) || !supported) return Lang.T("t.amdsam.unsupported");
            if (enabled && EnabledByPavise) return Lang.T("t.amdsam.byus");
            if (enabled) return Lang.T("t.amdsam.external");
            if (EnabledByPavise) return Lang.T("t.amdsam.reverted");
            return Lang.T("t.amdsam.off");
        }

        public static bool Enable()
        {
            lock (lk)
            {
                bool supported, enabled;
                if (!AdlxTweaks.SamGet(out supported, out enabled) || !supported)
                {
                    Logger.Log(Lang.T("log.amdsam.1"));
                    return false;
                }
                if (enabled) { Logger.Log(Lang.T("log.amdsam.2")); return true; }
                if (!AdlxTweaks.SamSet(true)) { Logger.Log(Lang.T("log.amdsam.3")); return false; }
                Settings.Save(OnKey, true);
                if (!Settings.Load(OnKey, false)) { AdlxTweaks.SamSet(false); return false; }
                Logger.Log(Lang.T("log.amdsam.4"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!EnabledByPavise) return true;
                bool supported, enabled;
                if (AdlxTweaks.SamGet(out supported, out enabled) && supported && enabled
                    && !AdlxTweaks.SamSet(false))
                {
                    Logger.Log(Lang.T("log.amdsam.5"));
                    return false;
                }
                Settings.Save(OnKey, false);
                Logger.Log(Lang.T("log.amdsam.6"));
                return true;
            }
        }
    }
}
