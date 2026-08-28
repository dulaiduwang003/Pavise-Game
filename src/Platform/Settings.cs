// @author bdth 2074055628@qq.com
// 文件用途 读写当前用户的持久配置
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class Settings
    {
        private const string Key = @"Software\Pavise";
        private static readonly object writeSync = new object();
        private static bool writesSuspendedForReset;

        // Call after restoration succeeds and before deleting persistent data.
        // Returning from this barrier drains earlier writes and rejects later callbacks.
        internal static void SuspendWritesForReset()
        {
            lock (writeSync) writesSuspendedForReset = true;
        }

#if PAVISE_SELFTEST || PAVISE_PERFLAB
        private static readonly object transientSync = new object();
        private static Dictionary<string, object> transientValues;

        internal static void UseTransientStoreForCurrentProcess()
        {
            lock (writeSync)
            {
                lock (transientSync)
                {
                    transientValues = new Dictionary<string, object>(
                        StringComparer.OrdinalIgnoreCase);
                    writesSuspendedForReset = false;
                }
            }
        }

        private static bool TryLoadTransient(string name, out object value)
        {
            lock (transientSync)
            {
                if (transientValues == null)
                {
                    value = null;
                    return false;
                }
                transientValues.TryGetValue(name, out value);
                return true;
            }
        }

        private static bool TrySaveTransient(string name, object value)
        {
            lock (transientSync)
            {
                if (transientValues == null) return false;
                transientValues[name] = value;
                return true;
            }
        }
#endif

        public static bool Load(string name, bool def)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            object transient;
            if (TryLoadTransient(name, out transient))
                return transient == null ? def : Convert.ToInt32(transient) != 0;
#endif
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
            lock (writeSync)
            {
                if (writesSuspendedForReset) return false;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (TrySaveTransient(name, val ? 1 : 0)) return true;
#endif
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    {
                        if (k == null) throw new InvalidOperationException(Lang.T("t.settings.1"));
                        k.SetValue(name, val ? 1 : 0);
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogFailure(Lang.T("log.settings.2") + name, ex);
                    return false;
                }
            }
        }

        public static void Remove(string name)
        {
            lock (writeSync)
            {
                if (writesSuspendedForReset) return;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                lock (transientSync)
                    if (transientValues != null) { transientValues.Remove(name); return; }
#endif
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Key, true))
                        if (k != null && k.GetValue(name) != null) k.DeleteValue(name, false);
                }
                catch { }
            }
        }

        public static string LoadStr(string name, string def)
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
            object transient;
            if (TryLoadTransient(name, out transient))
                return transient == null ? def : transient.ToString();
#endif
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
            lock (writeSync)
            {
                if (writesSuspendedForReset) return false;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (TrySaveTransient(name, val ?? "")) return true;
#endif
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    {
                        if (k == null) throw new InvalidOperationException(Lang.T("t.settings.1"));
                        k.SetValue(name, val ?? "");
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.LogFailure(Lang.T("log.settings.2") + name, ex);
                    return false;
                }
            }
        }
    }

}
