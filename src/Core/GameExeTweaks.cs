// @author bdth 2074055628@qq.com
// 文件用途 管理按游戏程序保存的图形兼容设置

using System;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class GameExeTweaks
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";
        private const string FsoKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
        private const string BakKey = @"Software\Aegis\ExeTweakBak";
        private const string FsoFlag = "DISABLEDXMAXIMIZEDWINDOWEDMODE";
        private static readonly object lk = new object();

        public static void ApplyForGame(string exePath, bool gpuHighPerf, bool disableFso)
        {
            if (string.IsNullOrEmpty(exePath)) return;
            lock (lk)
            {
                if (gpuHighPerf) SetGpuPref(exePath);
                if (disableFso) SetFso(exePath);
            }
        }

        public static void RestoreKind(string kind)
        {
            lock (lk)
            {
                try
                {
                    using (var bak = Registry.CurrentUser.OpenSubKey(BakKey, true))
                    {
                        if (bak == null) return;
                        int n = 0;
                        foreach (string name in bak.GetValueNames())
                        {
                            int bar = name.IndexOf('|');
                            if (bar <= 0) { try { bak.DeleteValue(name, false); } catch { } continue; }
                            if (!string.Equals(name.Substring(0, bar), kind, StringComparison.OrdinalIgnoreCase)) continue;
                            string exePath = name.Substring(bar + 1);
                            string target = string.Equals(kind, "gpu", StringComparison.OrdinalIgnoreCase) ? GpuKey : FsoKey;
                            string orig = bak.GetValue(name) as string ?? ReversibleReg.Absent;
                            if (RestoreValue(target, exePath, orig))
                            {
                                n++;
                                try { bak.DeleteValue(name, false); } catch { }
                            }
                        }
                        if (n > 0) Logger.Log("已还原 " + n + " 项逐游戏" + (kind == "gpu" ? " GPU 偏好" : "全屏优化") + "设置");
                    }
                }
                catch { }
            }
        }

        private static bool RestoreValue(string key, string exePath, string orig)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(key, true))
                {
                    if (k == null) return true;
                    if (orig == ReversibleReg.Absent)
                    {
                        if (k.GetValue(exePath) != null) k.DeleteValue(exePath, false);
                    }
                    else k.SetValue(exePath, orig, RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }

        private static void SetGpuPref(string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(GpuKey))
                {
                    if (k == null) return;
                    object curObj = k.GetValue(exePath);
                    string cur = curObj as string;
                    if (curObj != null && cur == null) return;
                    if (cur != null && cur.IndexOf("GpuPreference=2;", StringComparison.OrdinalIgnoreCase) >= 0) return;
                    if (!Backup("gpu", exePath, cur)) return;
                    k.SetValue(exePath, "GpuPreference=2;", RegistryValueKind.String);
                    Logger.Log("GPU 偏好 → 高性能：" + exePath + "（下次启动该游戏生效）");
                }
            }
            catch { }
        }

        private static void SetFso(string exePath)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(FsoKey))
                {
                    if (k == null) return;
                    object curObj = k.GetValue(exePath);
                    string cur = curObj as string;
                    if (curObj != null && cur == null) return;
                    if (cur != null && cur.IndexOf(FsoFlag, StringComparison.OrdinalIgnoreCase) >= 0) return;
                    if (!Backup("fso", exePath, cur)) return;
                    string val = string.IsNullOrEmpty(cur) ? "~ " + FsoFlag : cur.TrimEnd() + " " + FsoFlag;
                    k.SetValue(exePath, val, RegistryValueKind.String);
                    Logger.Log("关闭全屏优化：" + exePath + "（下次启动该游戏生效）");
                }
            }
            catch { }
        }

        private static bool Backup(string kind, string exePath, string original)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(BakKey))
                {
                    if (k == null) return false;
                    string name = kind + "|" + exePath;
                    if (k.GetValue(name) != null) return true;
                    k.SetValue(name, original ?? ReversibleReg.Absent, RegistryValueKind.String);
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
