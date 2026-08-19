// @author bdth 2074055628@qq.com
// 文件用途 开启关闭并恢复窗口化游戏优化 DirectX 呈现路径升级
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class WindowedOptTweak
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
        private const string ValueName = "DirectXUserGlobalSettings";
        private const string Field = "SwapEffectUpgradeEnable";
        private const string BackupSlot = "PrevSwapEffectUpgrade";
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load("WindowedOptOnByPavise", false); } }

        public static bool HasResidue()
        {
            return EnabledByPavise || Settings.LoadStr(BackupSlot, "").Length > 0;
        }

        public static string Describe()
        {
            bool on = CurrentlyOn();
            if (on && EnabledByPavise) return Lang.T("t.windowedopttweak.1");
            if (on) return Lang.T("t.windowedopttweak.2");
            if (EnabledByPavise) return Lang.T("t.windowedopttweak.3");
            return Lang.T("t.windowedopttweak.4");
        }

        public static bool CurrentlyOn()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(GpuKey))
                {
                    if (k == null) return false;
                    string cur = k.GetValue(ValueName) as string;
                    return string.Equals(GameExeTweaks.ReadField(cur, Field), "1", StringComparison.Ordinal);
                }
            }
            catch { return false; }
        }

        public static bool Enable()
        {
            lock (lk)
            {
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(GpuKey))
                    {
                        if (k == null) return false;
                        object curObj = k.GetValue(ValueName);
                        string cur = curObj as string;
                        if (curObj != null && cur == null) return false;
                        if (Settings.LoadStr(BackupSlot, "").Length == 0)
                        {
                            string snapshot = cur == null ? ReversibleReg.Absent : cur;
                            Settings.SaveStr(BackupSlot, snapshot);
                            if (Settings.LoadStr(BackupSlot, "") != snapshot) return false;
                        }
                        string next = GameExeTweaks.MergeField(cur, Field, "1");
                        k.SetValue(ValueName, next, RegistryValueKind.String);
                        if (!CurrentlyOn()) return false;
                        Settings.Save("WindowedOptOnByPavise", true);
                        if (!Settings.Load("WindowedOptOnByPavise", false)) { Restore(); return false; }
                        Logger.Log(Lang.T("log.windowedopttweak.5"));
                        return true;
                    }
                }
                catch { return false; }
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                try
                {
                    string orig = Settings.LoadStr(BackupSlot, "");
                    using (var k = Registry.CurrentUser.CreateSubKey(GpuKey))
                    {
                        if (k == null) return false;
                        string cur = k.GetValue(ValueName) as string;
                        string next = orig.Length == 0 || orig == ReversibleReg.Absent
                            ? GameExeTweaks.RemoveField(cur, Field)
                            : GameExeTweaks.RestoreField(cur, orig, Field);
                        if (next.Length == 0)
                        {
                            if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                        }
                        else k.SetValue(ValueName, next, RegistryValueKind.String);
                    }
                    Settings.SaveStr(BackupSlot, "");
                    Settings.Save("WindowedOptOnByPavise", false);
                    Logger.Log(Lang.T("log.windowedopttweak.6"));
                    return true;
                }
                catch { return false; }
            }
        }
    }
}
