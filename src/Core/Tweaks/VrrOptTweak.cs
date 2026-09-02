// @author bdth 2074055628@qq.com
// 文件用途 开启关闭并恢复 Windows 可变刷新率优化 让不支持 VRR 的 DX11 独占全屏游戏也走 VRR
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    // 和窗口化游戏优化写同一个注册表值串 只是字段不同 系统默认没有这个字段 等于关
    //   显示器不支持 VRR 时这个字段没有作用 写了也无害 所以不设显示器门 只设系统版本门
    internal static class VrrOptTweak
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
        private const string ValueName = "DirectXUserGlobalSettings";
        private const string Field = "VRROptimizeEnable";
        private const string BackupSlot = "PrevVrrOptimize";
        private const string OnKey = "VrrOptOnByPavise";
        internal const int MinOsBuild = 18362;
        private static readonly object lk = new object();

        public static bool EnabledByPavise { get { return Settings.Load(OnKey, false); } }

        public static bool OsSupported()
        {
            try { return Native.OsBuild() >= MinOsBuild; }
            catch { return false; }
        }

        public static bool HasResidue()
        {
            return EnabledByPavise || Settings.LoadStr(BackupSlot, "").Length > 0;
        }

        public static string Describe()
        {
            bool on = CurrentlyOn();
            if (on && EnabledByPavise) return Lang.T("t.vrropttweak.1");
            if (on) return Lang.T("t.vrropttweak.2");
            if (EnabledByPavise) return Lang.T("t.vrropttweak.3");
            return Lang.T("t.vrropttweak.4");
        }

        public static bool CurrentlyOn()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(GpuKey))
                {
                    string cur = k == null ? null : k.GetValue(ValueName) as string;
                    string field = PrefFieldText.ReadField(cur, Field);
                    return field != null && string.Equals(field, "1", StringComparison.Ordinal);
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
                    if (CurrentlyOn())
                    {
                        Logger.Log(Lang.T("log.vrropttweak.7"));
                        return true;
                    }
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
                        string next = PrefFieldText.MergeField(cur, Field, "1");
                        k.SetValue(ValueName, next, RegistryValueKind.String);
                        if (!CurrentlyOn()) return false;
                        Settings.Save(OnKey, true);
                        if (!Settings.Load(OnKey, false)) { Restore(); return false; }
                        Logger.Log(Lang.T("log.vrropttweak.5"));
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
                    if (orig.Length == 0 && !EnabledByPavise) return true;
                    using (var k = Registry.CurrentUser.CreateSubKey(GpuKey))
                    {
                        if (k == null) return false;
                        string cur = k.GetValue(ValueName) as string;
                        string next = orig.Length == 0 || orig == ReversibleReg.Absent
                            ? PrefFieldText.RemoveField(cur, Field)
                            : PrefFieldText.RestoreField(cur, orig, Field);
                        if (next.Length == 0)
                        {
                            if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                        }
                        else k.SetValue(ValueName, next, RegistryValueKind.String);
                    }
                    Settings.SaveStr(BackupSlot, "");
                    Settings.Save(OnKey, false);
                    Logger.Log(Lang.T("log.vrropttweak.6"));
                    return true;
                }
                catch { return false; }
            }
        }
    }
}
