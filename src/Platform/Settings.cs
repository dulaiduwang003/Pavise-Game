// @author bdth 2074055628@qq.com
// File purpose Reads and writes the current user's persistent config
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

        // Write generation of the config store, every Save/Remove entry increments it regardless of outcome, read-only caches
        //    such as the EnvActive residue check use it to tell whether the last result is still valid, better to invalidate too often
        //   than to miss one
        private static int mutationGeneration;
        internal static int MutationGeneration
        {
            get { return System.Threading.Volatile.Read(ref mutationGeneration); }
        }
        private static void BumpMutationGeneration()
        {
            System.Threading.Interlocked.Increment(ref mutationGeneration);
        }

        // Called after a successful restore and before deleting persistent data
        // On return from this barrier earlier writes are drained and later callbacks are all rejected
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
                    BumpMutationGeneration();
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

        // Switches the UI reads every 1.2 s go through here, the registry is reopened only after a write has happened
        //   Any Save/Remove advances MutationGeneration and invalidates the whole cache, better to invalidate too often than miss one
        //   The default for a given key must be identical at every call site, the cache does not distinguish defaults
        private static readonly object readCacheSync = new object();
        private static readonly Dictionary<string, bool> readCache =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static int readCacheGeneration = -1;

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
#endif

        // The restore ledger must distinguish a missing value from one that is unreadable or malformed
        // LoadStr keeps the original lenient behavior for other callers
        internal static bool TryLoadStr(string name, out string value)
        {
            value = "";
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
                value = (string)raw;
                return true;
            }
            catch { return false; }
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
                BumpMutationGeneration();
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

        // Transaction receipts must land durably before the cross-hive system changes
        // Ordinary preferences still go through Save and SaveStr, only the restore ledger uses a synchronous Flush so the cost is not spread globally
        internal static bool SaveStrDurable(string name, string val)
        {
            lock (writeSync)
            {
                BumpMutationGeneration();
                if (writesSuspendedForReset) return false;
#if PAVISE_SELFTEST || PAVISE_PERFLAB
                if (TrySaveTransient(name, val ?? "")) return true;
#endif
                try
                {
                    using (var k = Registry.CurrentUser.CreateSubKey(Key))
                    {
                        if (k == null) throw new InvalidOperationException(Lang.T("t.settings.1"));
                        k.SetValue(name, val ?? "", RegistryValueKind.String);
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
