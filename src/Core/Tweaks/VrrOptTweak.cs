// @author bdth 2074055628@qq.com
// File purpose Enable, disable and restore Windows variable refresh rate optimization so DX11 exclusive fullscreen games without VRR support still get VRR
using System;
using Microsoft.Win32;

namespace PaviseApp
{
    // Writes the same registry value string as optimizations for windowed games, just a different field, absent by default which equals off
    //   the field has no effect when the monitor lacks VRR, writing it is harmless, so gate only on OS version, not on the monitor
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
