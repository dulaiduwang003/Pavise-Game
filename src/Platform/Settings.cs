// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class Settings
    {
        internal const string Key = @"Software\PaviseAuto";
        private static readonly object writeSync = new object();
        private static bool writesSuspendedForReset;

        private static int mutationGeneration;
        internal static int MutationGeneration
        {
            get { return System.Threading.Volatile.Read(ref mutationGeneration); }
        }
        private static void BumpMutationGeneration()
        {
            System.Threading.Interlocked.Increment(ref mutationGeneration);
        }

        // 删除数据前排空写入，并拒绝后续回调。
        internal static void SuspendWritesForReset()
        {
            lock (writeSync) writesSuspendedForReset = true;
        }

#if PAVISE_SELFTEST
        internal static Action<string> BeforePreferenceDeleteForTest;
        internal static Action AfterTransientResetForTest;
#endif

        // 只清除已知偏好，保留恢复收据及未知数据。
        internal static bool ClearUserPreferencesForReset(Predicate<string> selected,
            out int removed, out string failure)
        {
            removed = 0;
            failure = null;
            if (selected == null) { failure = "Missing preference selection"; return false; }
            lock (writeSync)
            {
                writesSuspendedForReset = true;
                BumpMutationGeneration();
                var failures = new List<string>();
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                lock (transientSync)
                {
                    if (transientValues != null)
                    {
                        foreach (string name in new List<string>(transientValues.Keys))
                        {
                            if (!selected(name)) continue;
                            try
                            {
#if PAVISE_SELFTEST
                                if (BeforePreferenceDeleteForTest != null) BeforePreferenceDeleteForTest(name);
#endif
                                if (transientValues.Remove(name)) removed++;
                            }
                            catch (Exception error) { failures.Add(name + " (" + error.GetType().Name + ")"); }
                        }
                        failure = failures.Count == 0 ? null : string.Join("; ", failures.ToArray());
                        return failures.Count == 0;
                    }
                }
                failure = "Transient settings store was not initialized";
                return false;
#else
                try
                {
                    using (var key = Registry.CurrentUser.OpenSubKey(Key, true))
                    {
                        if (key == null) return true;
                        foreach (string name in key.GetValueNames())
                        {
                            if (!selected(name)) continue;
                            try
                            {
                                key.DeleteValue(name, false);
                                bool remains = false;
                                foreach (string current in key.GetValueNames())
                                    if (string.Equals(current, name, StringComparison.OrdinalIgnoreCase)) { remains = true; break; }
                                if (remains) failures.Add(name + " (still present)");
                                else removed++;
                            }
                            catch (Exception error) { failures.Add(name + " (" + error.GetType().Name + ")"); }
                        }
                        key.Flush();
                    }
                }
                catch (Exception error) { failures.Add(error.GetType().Name); }
                failure = failures.Count == 0 ? null : string.Join("; ", failures.ToArray());
                return failures.Count == 0;
#endif
            }
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
                    BumpMutationGeneration();
                    transientValues = new Dictionary<string, object>(
                        StringComparer.OrdinalIgnoreCase);
                    writesSuspendedForReset = false;
                }
            }
#if PAVISE_SELFTEST
            if (AfterTransientResetForTest != null) AfterTransientResetForTest();
#endif
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

        // 缓存不区分默认值，同一键的默认值必须一致。
        private static readonly object readCacheSync = new object();
        private static readonly Dictionary<string, bool> readCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static int readCacheGeneration = -1;

        internal static bool TryLoadCached(string name, out bool value)
        {
            int gen = MutationGeneration;
            lock (readCacheSync)
            {
                if (readCacheGeneration != gen) { readCache.Clear(); readCacheGeneration = gen; }
                return readCache.TryGetValue(name, out value);
            }
        }

        public static bool LoadCached(string name, bool def)
        {
            int gen = MutationGeneration;
            lock (readCacheSync)
            {
                if (readCacheGeneration != gen) { readCache.Clear(); readCacheGeneration = gen; }
                bool hit;
                if (readCache.TryGetValue(name, out hit)) return hit;
            }
            bool value = Load(name, def);
            lock (readCacheSync)
                if (readCacheGeneration == gen) readCache[name] = value;
            return value;
        }

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
                BumpMutationGeneration();
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
            if (RecoveryRecordKeys.Contains(name)) { SaveString(name, "", true); return; }
            lock (writeSync)
            {
                BumpMutationGeneration();
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

#if PAVISE_SELFTEST
        internal static Action<string> BeforeStrictStringReadForTest;
        internal static Action<string> BeforeStoredNamesReadForTest;
        internal static Action<string> BeforeStringWriteForTest;
#endif

        private static bool TryLoadRawNames(string prefix, out string[] names)
        {
            names = new string[0];
            if (prefix == null) return false;
            try
            {
#if PAVISE_SELFTEST
                if (BeforeStoredNamesReadForTest != null) BeforeStoredNamesReadForTest(prefix);
#endif
                var matching = new List<string>();
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                lock (transientSync)
                {
                    if (transientValues != null)
                    {
                        foreach (string name in transientValues.Keys)
                            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) matching.Add(name);
                        matching.Sort(StringComparer.OrdinalIgnoreCase);
                        names = matching.ToArray();
                        return true;
                    }
                }
#endif
                using (var key = Registry.CurrentUser.OpenSubKey(Key))
                    if (key != null)
                        foreach (string name in key.GetValueNames())
                            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) matching.Add(name);
                matching.Sort(StringComparer.OrdinalIgnoreCase);
                names = matching.ToArray();
                return true;
            }
            catch { return false; }
        }

        internal static bool TryLoadStoredNames(string prefix, out string[] names)
        {
            names = new string[0];
            if (string.IsNullOrEmpty(prefix)) return false;
            return TryLoadRawNames(prefix, out names);
        }

        // 恢复收据需区分缺失、不可读和格式错误。
        private static bool TryLoadRawStr(string name, out string value)
        { bool present; return TryLoadRawStr(name, out value, out present); }
        private static bool TryLoadRawStr(string name, out string value, out bool present)
        {
            value = ""; present = false;
            try
            {
#if PAVISE_SELFTEST
                if (BeforeStrictStringReadForTest != null) BeforeStrictStringReadForTest(name);
#endif
                object raw;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (!TryLoadTransient(name, out raw))
#endif
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Key))
                        raw = k == null ? null : k.GetValue(name);
                }
                if (raw == null) return true;
                if (!(raw is string)) return false;
                value = (string)raw; present = true;
                return true;
            }
            catch { return false; }
        }

        internal static bool TryLoadStr(string name, out string value)
        {
            return TryLoadRawStr(name, out value);
        }

        public static string LoadStr(string name, string def)
        {
            if (RecoveryRecordKeys.Contains(name))
            {
                string recovered; bool present;
                if (!TryLoadRawStr(name, out recovered, out present))
                    return "!unreadable-recovery-record!";
                return present ? recovered : def;
            }
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
        { return SaveString(name, val, false); }

        internal static bool SaveStrDurable(string name, string val)
        { return SaveString(name, val, true); }

        private static bool SaveString(string name, string val, bool durable)
        {
            lock (writeSync)
            {
                BumpMutationGeneration();
                if (writesSuspendedForReset) return false;
                string value = val ?? "";
                try
                {
#if PAVISE_SELFTEST
                    if (BeforeStringWriteForTest != null) BeforeStringWriteForTest(name);
#endif
                    string original;
                    if (RecoveryRecordKeys.Contains(name) && !TryLoadRawStr(name, out original)) return false;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                    if (TrySaveTransient(name, value)) return true;
#endif
                    using (var key = Registry.CurrentUser.CreateSubKey(Key))
                    {
                        if (key == null) throw new InvalidOperationException(Lang.T("t.settings.1"));
                        key.SetValue(name, value, RegistryValueKind.String);
                        if (durable || RecoveryRecordKeys.Contains(name)) key.Flush();
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
        internal static bool SaveDurable(string name, bool val)
        {
            lock (writeSync)
            {
                BumpMutationGeneration();
                if (writesSuspendedForReset) return false;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (TrySaveTransient(name, val ? 1 : 0)) return true;
#endif
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    {
                        if (k == null) throw new InvalidOperationException(Lang.T("t.settings.1"));
                        k.SetValue(name, val ? 1 : 0, RegistryValueKind.DWord);
                        k.Flush();
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
