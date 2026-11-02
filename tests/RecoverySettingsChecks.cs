#if PAVISE_SELFTEST && PAVISE_RECOVERY_SETTINGS_RUNNER
using System;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Runtime unavailable in recovery checks"); }
    }
    internal static class RecoverySettingsChecks
    {
        private static int checks;
        private static void Check(bool value, string message)
        { checks++; if (!value) throw new InvalidOperationException(message); }
        private static void ReadsAndFailures()
        {
            Settings.UseTransientStoreForCurrentProcess();
            int reads = 0;
            Settings.BeforeStrictStringReadForTest = delegate { reads++; };
            for (int i = 0; i < 1000; i++) Check(Settings.LoadStr("PrevTimerTick", "missing") == "missing", "missing record lost fallback");
            Check(reads == 1000, "repeated backing reads for an empty record");
            Check(Settings.SaveStrDurable("PrevTimerTick", "original"), "record save failed");
            string value;
            Check(Settings.TryLoadStr("PrevTimerTick", out value) && value == "original", "original value lost");
            Check(Settings.SaveStr("PrevTimerTick", "") && Settings.LoadStr("PrevTimerTick", "missing") == "", "explicit clear became missing");
            Settings.BeforeStrictStringReadForTest = delegate { throw new IOException("denied"); };
            Check(!Settings.TryLoadStr("PrevTimerTick", out value), "read failure accepted");
            Check(Settings.LoadStr("PrevTimerTick", "missing") == "!unreadable-recovery-record!", "read failure treated as no recovery");
            Check(!Settings.SaveStr("PrevTimerTick", "changed"), "unreadable original overwritten");
            Settings.BeforeStrictStringReadForTest = null;
            Settings.Save("PrevTimerTick", true);
            Check(!Settings.TryLoadStr("PrevTimerTick", out value), "wrong registry type accepted");
            Check(Settings.LoadStr("PrevTimerTick", "missing") == "!unreadable-recovery-record!", "wrong type treated as missing");
            Check(!Settings.SaveStr("PrevTimerTick", "changed"), "wrong registry type overwritten");
            Settings.UseTransientStoreForCurrentProcess();
            Settings.SaveStr("PrevTimerTick", "original");
            Settings.BeforeStringWriteForTest = delegate { throw new IOException("write denied"); };
            Check(!Settings.SaveStrDurable("PrevTimerTick", "changed"), "failed write reported success");
            Check(Settings.LoadStr("PrevTimerTick", "") == "original", "failed write lost original");
            Settings.BeforeStringWriteForTest = null;
            Settings.Remove("PrevTimerTick");
            Check(Settings.LoadStr("PrevTimerTick", "missing") == "", "record removal not confirmed");
            string[] names;
            Settings.SaveStr("PerformanceReceipt_fixture", "pending");
            Check(Settings.TryLoadStoredNames("PerformanceReceipt_", out names) && names.Length == 1, "orphan recovery discovery failed");
        }
        private static void CachedEnvironment()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(PolicyCatalog.KeyTimerTick, false);
            Settings.Save(PolicyCatalog.KeyGlobalTimer, false);
            int reads = 0;
            Settings.BeforeStrictStringReadForTest = delegate { reads++; };
            Check(!AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyTimerTick), "timer preference changed");
            Check(!AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer), "resolution preference changed");
            int coldReads = reads;
            for (int i = 0; i < 1000; i++)
            {
                Check(!AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyTimerTick), "cached timer changed");
                Check(!AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer), "cached resolution changed");
            }
            Check(reads == coldReads, "cache hit still read recovery state");
            Settings.Save(PolicyCatalog.KeyGlobalTimer, true);
            Check(AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer), "changed preference stayed cached");
            Settings.UseTransientStoreForCurrentProcess();
            Settings.SaveStr("PrevGlobalTimerRes", "original");
            Check(AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer), "existing recovery ownership ignored");
            Settings.SaveStr("PrevGlobalTimerRes", "");
            Check(!AutomaticPolicy.EnvironmentPreference(PolicyCatalog.KeyGlobalTimer), "released ownership stayed cached");
            Settings.BeforeStrictStringReadForTest = null;
        }
        private static void RestoreAndPreferenceReset()
        {
            Settings.UseTransientStoreForCurrentProcess();
            string native = "original";
            Check(PerformanceActionStore.Change("fixture", "target", "applied", delegate { return native; },
                delegate(string value) { native = value; return true; }, delegate(string value) { return value == "applied"; }), "apply failed");
            var receipt = PerformanceActionStore.All();
            Check(receipt.Count == 1 && receipt[0].Original == "original" && native == "applied", "original receipt not retained");
            Check(PerformanceActionStore.Restore(receipt[0], delegate { return native; }, delegate(string value) { native = value; return true; },
                delegate(string value) { return value == "applied"; }), "restore failed");
            Check(native == "original" && PerformanceActionStore.All().Count == 0, "restoration left pending state");
            Settings.SaveStr("PrevTimerTick", "original");
            Settings.Save(PolicyCatalog.KeyGlobalTimer, true);
            int removed; string failure;
            Check(Settings.ClearUserPreferencesForReset(AppDataReset.IsUserPreference, out removed, out failure), "preference reset failed");
            Check(Settings.LoadStr("PrevTimerTick", "") == "original", "preference reset lost recovery record");
        }
        // A LoadCached inside a write window (after the writer's first generation bump, before the value changes)
        // caches the old value; once the writer returns, LoadCached must return the new value. Deterministic: the
        // in-window read runs from the writer's own hook, so no timing is involved.
        private static void WriteCacheCoherence()
        {
            const string key = "SyntheticWriteCoherenceV1";
            bool seen = false;
            Action<string> readInWindow = delegate(string name) { if (name == key) seen = Settings.LoadCached(key, false); };
            try
            {
                foreach (string writer in new[] { "Save", "SaveDurable", "SaveStr", "SaveStrDurable" })
                    foreach (bool value in new[] { true, false })
                    {
                        Settings.UseTransientStoreForCurrentProcess();
                        bool text = writer.StartsWith("SaveStr", StringComparison.Ordinal);
                        Check((text ? Settings.SaveStr(key, value ? "0" : "1") : Settings.Save(key, !value)) && Settings.LoadCached(key, false) == !value, "seed " + writer);
                        Settings.BeforeValueWriteForTest = readInWindow; Settings.BeforeStringWriteForTest = readInWindow; seen = value;
                        bool saved = writer == "Save" ? Settings.Save(key, value) : writer == "SaveDurable" ? Settings.SaveDurable(key, value)
                            : writer == "SaveStr" ? Settings.SaveStr(key, value ? "1" : "0") : Settings.SaveStrDurable(key, value ? "1" : "0");
                        Settings.BeforeValueWriteForTest = null; Settings.BeforeStringWriteForTest = null;
                        Check(saved && seen == !value, "the in-window read did not observe the old value: " + writer);
                        Check(Settings.LoadCached(key, false) == value, "a completed " + writer + "(" + value + ") was followed by the cached old value");
                    }
                Settings.UseTransientStoreForCurrentProcess();
                Check(Settings.Save(key, true) && Settings.LoadCached(key, false), "seed Remove");
                Settings.BeforeValueWriteForTest = readInWindow; seen = false;
                Settings.Remove(key);
                Settings.BeforeValueWriteForTest = null;
                Check(seen && !Settings.LoadCached(key, false), "a completed Remove was followed by the cached removed value");
                // Batch reset path (selection is a test predicate, independent of the preference catalog).
                Settings.UseTransientStoreForCurrentProcess();
                Check(Settings.Save(key, true) && Settings.LoadCached(key, false), "seed preference reset");
                Settings.BeforePreferenceDeleteForTest = readInWindow; seen = false;
                int removed; string failure;
                Check(Settings.ClearUserPreferencesForReset(delegate(string name) { return name == key; }, out removed, out failure) && removed == 1, "preference reset failed");
                Settings.BeforePreferenceDeleteForTest = null;
                Check(seen && !Settings.LoadCached(key, false), "a completed preference reset was followed by the cached deleted value");
                // A refused write changes nothing; the cache must keep returning the stored value.
                Settings.UseTransientStoreForCurrentProcess();
                Check(Settings.Save(key, true) && Settings.LoadCached(key, false), "seed refused write");
                Settings.BeforeStringWriteForTest = delegate(string name) { if (name == key) { Settings.LoadCached(key, false); throw new IOException("write denied"); } };
                Check(!Settings.SaveStr(key, "0") && Settings.LoadCached(key, false), "a refused write changed the cached value");
            }
            finally { Settings.BeforeValueWriteForTest = null; Settings.BeforeStringWriteForTest = null; Settings.BeforePreferenceDeleteForTest = null; }
        }
        private static int Main()
        {
            Logger.WritesEnabled = false;
            try
            {
                ReadsAndFailures(); CachedEnvironment(); RestoreAndPreferenceReset(); WriteCacheCoherence();
                Console.WriteLine("PASS recovery settings checks=" + checks + " settings=transient native=mocked runtime_started=false"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { Settings.BeforeStringWriteForTest = null; Settings.BeforeStrictStringReadForTest = null; Settings.UseTransientStoreForCurrentProcess(); }
        }
    }
}
#endif
