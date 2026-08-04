// @author bdth 2074055628@qq.com
// 文件用途 禁用 恢复多平面叠加（MPO）排查闪烁与卡顿

using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class MpoTweak
    {
        private const string DwmKey = @"SOFTWARE\Microsoft\Windows\Dwm";
        private const string Val = "OverlayTestMode";
        private const int DisableValue = 5;

        private static readonly ReversibleReg Overlay = new ReversibleReg(
            Registry.LocalMachine, DwmKey, Val, RegistryValueKind.DWord, "PrevMpoOverlay");

        public static bool DisabledByPavise { get { return Settings.Load("MpoOffByPavise", false); } }

        public static bool CurrentlyDisabled()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(DwmKey))
                {
                    if (k == null) return false;
                    object v = k.GetValue(Val);
                    return v is int && (int)v == DisableValue;
                }
            }
            catch { return false; }
        }

        public static bool Disable()
        {
            try
            {
                if (!Overlay.Apply(DisableValue))
                {
                    Logger.Log("MPO 禁用写入或回读失败，未标记为已禁用");
                    return false;
                }
                Settings.Save("MpoOffByPavise", true);
                if (!Settings.Load("MpoOffByPavise", false))
                {
                    Overlay.Restore();
                    Logger.Log("MPO 状态标志无法持久化，已还原注册表修改");
                    return false;
                }
                Logger.Log("多平面叠加（MPO）已禁用，重启或重新登录后生效");
                return true;
            }
            catch { return false; }
        }

        public static bool Restore()
        {
            try
            {
                bool ok = Overlay.HasBackup ? Overlay.Restore() : RemoveValue();
                if (ok && CurrentlyDisabled()) ok = RemoveValue();
                if (ok)
                {
                    Settings.Save("MpoOffByPavise", false);
                    if (Settings.Load("MpoOffByPavise", true)) return false;
                    Logger.Log("多平面叠加（MPO）设置已恢复，重启或重新登录后生效");
                }
                return ok;
            }
            catch { return false; }
        }

        private static bool RemoveValue()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(DwmKey, true))
                {
                    if (k == null) return true;
                    if (k.GetValue(Val) != null) k.DeleteValue(Val, false);
                    return k.GetValue(Val) == null;
                }
            }
            catch { return false; }
        }
    }
}
