// @author bdth 2074055628@qq.com
// 文件用途 开启 关闭并恢复硬件加速 GPU 调度

using System;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class HagsTweak
    {
        private const string GfxKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
        private const string Val = "HwSchMode";

        private static readonly ReversibleReg Sch = new ReversibleReg(
            Registry.LocalMachine, GfxKey, Val, RegistryValueKind.DWord, "PrevHwSch");

        public static bool EnabledByAegis { get { return Settings.Load("HagsOnByAegis", false); } }

        public static bool CurrentlyOn()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(GfxKey))
                {
                    if (k == null) return false;
                    object v = k.GetValue(Val);
                    return v is int && (int)v == 2;
                }
            }
            catch { return false; }
        }

        public static bool Enable()
        {
            try
            {
                if (!Sch.Apply(2))
                {
                    Logger.Log("GPU 硬件调度（HAGS）写入或回读失败，未标记为已开启");
                    return false;
                }
                Settings.Save("HagsOnByAegis", true);
                if (!Settings.Load("HagsOnByAegis", false))
                {
                    Sch.Restore();
                    Logger.Log("HAGS 状态标志无法持久化，已还原注册表修改");
                    return false;
                }
                Logger.Log("GPU 硬件调度（HAGS）已开启，重启后生效");
                return true;
            }
            catch { return false; }
        }

        public static bool Disable()
        {
            try
            {
                bool ok;
                if (Sch.HasBackup) ok = Sch.Restore();
                else ok = WriteMode(1);
                if (ok)
                {
                    Settings.Save("HagsOnByAegis", false);
                    if (Settings.Load("HagsOnByAegis", true)) return false;
                    Logger.Log("GPU 硬件调度（HAGS）已关闭，重启后生效");
                }
                return ok;
            }
            catch { return false; }
        }

        private static bool WriteMode(int v)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(GfxKey, true))
            {
                if (k == null) return false;
                k.SetValue(Val, v, RegistryValueKind.DWord);
                object actual = k.GetValue(Val);
                return actual is int && (int)actual == v;
            }
        }
    }
}
