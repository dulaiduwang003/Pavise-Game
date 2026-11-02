#if PAVISE_SELFTEST && PAVISE_RECEIPT_DISCOVERY_RUNNER
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Normal runtime unavailable in discovery runner"); } }
    internal static class PerformanceReceiptDiscoveryRunner
    {
        private static int assertions;
        private static void Check(bool value, string message) { assertions++; if (!value) throw new InvalidOperationException(message); }
        private static PerformanceActionStore.Receipt Receipt(string target)
        { return new PerformanceActionStore.Receipt { Id = PerformanceActionCatalog.Mitigations, Target = target, Original = "original", Expected = "ours", Committed = true }; }
        private static string Seed(PerformanceActionStore.Receipt r)
        { string key = PerformanceActionStore.Slot(r.Id, r.Target); Check(Settings.SaveStrDurable(key, PerformanceActionStore.Pack(r)), "Seed failed"); return key; }
        private static void Fresh() { Settings.UseTransientStoreForCurrentProcess(); Settings.BeforeStoredNamesReadForTest = null; Settings.BeforeStrictStringReadForTest = null; }
        private static void Rejects(Action work, string message)
        { bool rejected = false; try { work(); } catch (InvalidOperationException) { rejected = true; } catch (FormatException) { rejected = true; } Check(rejected, message); }
        private static void NamesAreScopedAndReadOnly()
        {
            Fresh(); Seed(Receipt("A")); Settings.SaveStr("UnrelatedValue", "private");
            int generation = Settings.MutationGeneration; string[] names;
            Check(Settings.TryLoadStoredNames("performancereceipt_", out names) && names.Length == 1, "Owned names not scoped/case insensitive");
            Check(Settings.MutationGeneration == generation && Settings.LoadStr("UnrelatedValue", "") == "private", "Read-only enumeration mutated settings");
            Check(!Settings.TryLoadStoredNames("", out names) && names.Length == 0, "Empty prefix exposed all names");
        }
        private static void OrphansAreDiscoveredAndRestorable()
        {
            Fresh(); var a = Receipt("indexed-game"); var b = Receipt("League of Legends.exe"); var c = Receipt("ChatGPT.exe");
            string ka = Seed(a), kb = Seed(b), kc = Seed(c);
            Settings.SaveStrDurable(PerformanceActionStore.ListKey, ka + ";" + ka.ToLowerInvariant());
            int generation = Settings.MutationGeneration;
            var all = PerformanceActionStore.All(); Check(all.Count == 3, "Duplicate index/orphans did not merge to three owned receipts");
            Check(Settings.MutationGeneration == generation && Settings.LoadStr(PerformanceActionStore.ListKey, "") == ka + ";" + ka.ToLowerInvariant(), "All changed the ledger/index");
            int writes = 0;
            foreach (var r in all)
            {
                string current = r.Expected;
                Check(PerformanceActionStore.Restore(r, delegate { return current; }, delegate(string value) { writes++; current = value; return true; }, delegate { return true; }), "Mock orphan restoration failed");
            }
            Check(writes == 3 && PerformanceActionStore.All().Count == 0, "Recovered orphan records were not cleared");
            Check(Settings.LoadStr(kb, "") == "" && Settings.LoadStr(kc, "") == "", "Recovered orphan durable values remained");
        }
        private static void EmptyRecordsAndUnknownIndex()
        {
            Fresh(); string key = PerformanceActionStore.Slot(PerformanceActionCatalog.Mitigations, "empty");
            Settings.SaveStrDurable(key, ""); Settings.SaveStrDurable(PerformanceActionStore.ListKey, key);
            Check(PerformanceActionStore.All().Count == 0, "Cleared empty record was treated as active");
            Settings.SaveStrDurable(PerformanceActionStore.ListKey, "UnrelatedValue"); Settings.SaveStr("UnrelatedValue", "");
            Rejects(delegate { PerformanceActionStore.All(); }, "Unknown indexed namespace was silently accepted");
            Settings.SaveStrDurable(PerformanceActionStore.ListKey, ""); Settings.SaveStrDurable("PerformanceReceipt_invalid", "");
            Rejects(delegate { PerformanceActionStore.All(); }, "Invalid owned namespace key was silently accepted");
        }
        private static void MalformedRecordsRemain()
        {
            Fresh(); string key = PerformanceActionStore.Slot(PerformanceActionCatalog.Mitigations, "broken");
            Settings.SaveStrDurable(key, "not-a-receipt");
            Rejects(delegate { PerformanceActionStore.All(); }, "Malformed orphan was dropped");
            Check(Settings.LoadStr(key, "") == "not-a-receipt", "Malformed orphan was deleted");
            Fresh(); var a = Receipt("A"); key = PerformanceActionStore.Slot(a.Id, a.Target);
            string wrong = PerformanceActionStore.Pack(Receipt("B")); Settings.SaveStrDurable(key, wrong);
            Rejects(delegate { PerformanceActionStore.All(); }, "Slot/payload mismatch was accepted"); Check(Settings.LoadStr(key, "") == wrong, "Mismatched record was deleted");
            Fresh(); var empty = Receipt(""); key = Seed(empty);
            Rejects(delegate { PerformanceActionStore.All(); }, "Empty target was accepted"); Check(Settings.LoadStr(key, "") != "", "Invalid target lost its record");
        }
        private static void FailedReadsAreNotEmpty()
        {
            Fresh(); string key = Seed(Receipt("orphan")); string packed = Settings.LoadStr(key, "");
            Settings.BeforeStoredNamesReadForTest = delegate { throw new UnauthorizedAccessException("Mock enumeration denial"); };
            Rejects(delegate { PerformanceActionStore.All(); }, "Enumeration failure returned empty ledger");
            Settings.BeforeStoredNamesReadForTest = null;
            Settings.BeforeStrictStringReadForTest = delegate(string name) { if (name == key) throw new UnauthorizedAccessException("Mock record denial"); };
            Rejects(delegate { PerformanceActionStore.All(); }, "Record read failure returned empty ledger"); Settings.BeforeStrictStringReadForTest = null;
            Check(Settings.LoadStr(key, "") == packed, "Read failure altered recovery record");
            Settings.Save(PerformanceActionStore.ListKey, true);
            Rejects(delegate { PerformanceActionStore.All(); }, "Invalid index value kind returned empty ledger");
        }
        private static int Main()
        {
            try
            {
                Logger.WritesEnabled = false;
                Action[] cases = { NamesAreScopedAndReadOnly, OrphansAreDiscoveredAndRestorable, EmptyRecordsAndUnknownIndex, MalformedRecordsRemain, FailedReadsAreNotEmpty };
                foreach (Action test in cases) { test(); Console.WriteLine("PASS " + test.Method.Name); }
                Console.WriteLine("PASS receipt_discovery_cases=" + cases.Length + " assertions=" + assertions + " real_registry=0 system_writes=0 normal_runtime=0"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { Settings.BeforeStoredNamesReadForTest = null; Settings.BeforeStrictStringReadForTest = null; Settings.UseTransientStoreForCurrentProcess(); }
        }
    }
}
#endif
