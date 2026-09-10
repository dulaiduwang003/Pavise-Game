// @author bdth 2074055628@qq.com
// 文件用途 对局中关闭 Intel Endurance Gaming 电池下不再按面板刷新率封顶帧率 退局按快照写回
using System;

namespace PaviseApp
{
    // Intel 自己的说明 电池供电时 Endurance Gaming 把帧率封在面板刷新率的一个分数上 常见落到 30 帧上下
    //   接电源时它不起作用 所以只在有电池的机器上有意义 要不要关是续航和帧率的取舍 默认关
    //   控制值 0 关 1 开 2 自动 只在读到 1 或 2 时写 0 退局写回原值
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
                    // 用户或 Intel 的工具中途改过 不抢回来 只清账
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
