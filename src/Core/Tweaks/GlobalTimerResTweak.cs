// @author bdth 2074055628@qq.com
// 文件用途 全局计时器分辨率实验开关 注册表把按进程隔离改回全局语义 重启生效
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GlobalTimerResTweak
    {
        private const string KernelKey =
            @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

        private static readonly ReversibleReg Global = new ReversibleReg(
            Registry.LocalMachine, KernelKey, "GlobalTimerResolutionRequests",
            RegistryValueKind.DWord, "PrevGlobalTimerRes");

        private const string OnKey = "GlobalTimerResByPavise";
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }
        public static bool OwnsState { get { return EnabledByPavise || Global.HasBackup; } }

        public static bool Enable()
        {
            lock (lk)
            {
                if (!Native.IsElevated()) return false;
                if (!Global.Apply(1)) { Logger.Log(Lang.T("log.gtimer.1")); return false; }
                Settings.Save(OnKey, true);
                Logger.Log(Lang.T("log.gtimer.2"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                if (!Global.Restore()) { Logger.Log(Lang.T("log.gtimer.3")); return false; }
                Settings.Save(OnKey, false);
                Logger.Log(Lang.T("log.gtimer.4"));
                return true;
            }
        }
    }
}
