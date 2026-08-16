// @author bdth 2074055628@qq.com
// 文件用途 只读查询多平面叠加 MPO 状态 并还原 v1.7.0.5 移除的禁用开关留下的残留

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

        public static bool Restore()
        {
            try
            {
                bool touched = false;
                bool ok = true;
                if (Overlay.HasBackup) { ok = Overlay.Restore(); touched = true; }
                else if (DisabledByPavise && CurrentlyDisabled()) { ok = RemoveValue(); touched = true; }
                if (!ok) return false;
                Settings.Save("MpoOffByPavise", false);
                if (Settings.Load("MpoOffByPavise", true)) return false;
                if (touched) Logger.Log(Lang.T("log.mpotweak.1"));
                return true;
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
