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
                // 没有快照说明 HAGS 是 Aegis 介入前就开着的（新驱动多数默认开）。
                // 这种情况下要关掉它，同样得先存快照再写——直接硬写 1 的话，
                // 万一原来这个值根本不存在，就等于凭空造了一个用户从未有过、
                // 而且我们自己也再删不掉的注册表项。
                bool ok = Sch.HasBackup ? Sch.Restore() : Sch.Apply(1);
                if (ok && CurrentlyOn()) ok = Sch.Apply(1);
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

    }
}
