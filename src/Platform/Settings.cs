// @author bdth 2074055628@qq.com
// 文件用途 读写当前用户的持久配置

using System;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class Settings
    {
        private const string Key = @"Software\Aegis";

        public static bool Load(string name, bool def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name);
                    return v == null ? def : Convert.ToInt32(v) != 0;
                }
            }
            catch { return def; }
        }

        public static bool Save(string name, bool val)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (k == null) throw new InvalidOperationException("注册表配置键无法创建");
                    k.SetValue(name, val ? 1 : 0);
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("设置写入失败 [" + name + "]", ex);
                return false;
            }
        }

        public static string LoadStr(string name, string def)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    if (k == null) return def;
                    object v = k.GetValue(name);
                    return v == null ? def : v.ToString();
                }
            }
            catch { return def; }
        }

        public static bool SaveStr(string name, string val)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (k == null) throw new InvalidOperationException("注册表配置键无法创建");
                    k.SetValue(name, val ?? "");
                }
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogFailure("设置写入失败 [" + name + "]", ex);
                return false;
            }
        }
    }


}
