// @author bdth 2074055628@qq.com
// 文件用途 按游戏程序图形偏好的字段读取 写入路径均已退役 不再有还原逻辑
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class GameExeTweaks
    {
        private const string GpuKey = @"SOFTWARE\Microsoft\DirectX\UserGpuPreferences";

        internal static string ReadField(string current, string field)
        {
            return PrefFieldText.ReadField(current, field);
        }

        public static bool PrefersIntegrated(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(GpuKey))
                {
                    if (k == null) return false;
                    return string.Equals(
                        ReadField(k.GetValue(exePath) as string, "GpuPreference"), "1",
                        StringComparison.Ordinal);
                }
            }
            catch { return false; }
        }

    }
}
