#if PAVISE_SELFTEST && PAVISE_DEFENDER_RESOURCE_RETIREMENT_RUNNER
using System;
using System.IO;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application runtime is unavailable in retirement tests"); } }
    internal static class RetiredDefenderResourceChecks
    {
        private static int assertions, reads, writes, serviceQueries;
        private static string current;
        private static void Check(bool ok, string message) { assertions++; if (!ok) throw new InvalidOperationException(message); }
        private static void Fresh()
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            reads = writes = serviceQueries = 0;
            PerformanceActionBackend.DefenderServiceForTest = delegate { serviceQueries++; return 4; };
            PerformanceActionBackend.ReadForTest = delegate(string id, string target) { reads++; return current; };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) { writes++; Check(value == "0", "retired exclusion attempted to add or rewrite an exclusion"); current = value; return true; };
        }
        private static PerformanceActionStore.Receipt Seed(string original, string expected, bool committed, string target)
        {
            var receipt = new PerformanceActionStore.Receipt { Id = PerformanceActionCatalog.DefenderResources,
                Target = target, Original = original, Expected = expected, Committed = committed };
            string slot = PerformanceActionStore.Slot(receipt.Id, receipt.Target);
            Check(Settings.SaveStrDurable(slot, PerformanceActionStore.Pack(receipt)), "seed receipt failed");
            Check(Settings.SaveStrDurable(PerformanceActionStore.ListKey, slot), "seed receipt index failed");
            return receipt;
        }
        private static void RetiredAdmission()
        {
            Fresh(); bool active = false, retired = false;
            foreach (PerformanceAction action in PerformanceActionCatalog.All) active |= action.Key == PerformanceActionCatalog.DefenderResources;
            foreach (PerformanceAction action in PerformanceActionCatalog.Retired) retired |= action.Key == PerformanceActionCatalog.DefenderResources;
            Check(!active && retired && PerformanceActionCatalog.Find(PerformanceActionCatalog.DefenderResources) != null, "retired catalogue identity lost or still active");
            foreach (string value in new[] { "1", "0", "2", "absent", "", null })
            { Check(!PerformanceActionBackend.Apply(PerformanceActionCatalog.DefenderResources, @"C:\Games", value), "retired apply was admitted"); }
            Check(reads == 0 && writes == 0 && serviceQueries == 0 && PerformanceActionStore.All().Count == 0, "retired apply inspected or prepared a new modification");
            foreach (string value in new[] { "1", "2", "absent", "", null })
                Check(!PerformanceActionBackend.Write(PerformanceActionCatalog.DefenderResources, @"C:\Games", value), "non-removal write bypassed retirement");
            Check(writes == 0 && serviceQueries == 0, "retired write reached native service checks or test hook");
            Check(PerformanceActionBackend.LegacyDefenderRestoreScript("False,True").Contains("-DisableRealtimeMonitoring $false"), "older Defender protection recovery changed");
            Check(PerformanceActionBackend.LegacyDefenderRestoreScript("True,True") == "", "older Defender protection restore began disabling protection");
        }
        private static void OwnedAndPreexisting()
        {
            foreach (bool committed in new[] { false, true }) foreach (string target in new[] { @"C:\Games\资源'包", @"C:\Games\资源\:{Process:C:\Games\游戏.exe}" })
            {
                Fresh(); current = "1"; var receipt = Seed("0", "1", committed, target);
                Check(PerformanceActionBackend.Restore(receipt) && current == "0" && writes == 1 && PerformanceActionStore.All().Count == 0, "old owned exclusion not removed and confirmed");
                Fresh(); current = "1"; receipt = Seed("1", "1", committed, target);
                Check(PerformanceActionBackend.Restore(receipt) && current == "1" && writes == 0 && PerformanceActionStore.All().Count == 0, "pre-existing exclusion was deleted");
                Fresh(); current = "0"; receipt = Seed("0", "1", committed, target);
                Check(PerformanceActionBackend.Restore(receipt) && writes == 0 && PerformanceActionStore.All().Count == 0, "already-removed owned exclusion was rebuilt or left pending");
                Fresh(); current = "0"; receipt = Seed("1", "1", committed, target);
                Check(!PerformanceActionBackend.Restore(receipt) && writes == 0 && PerformanceActionStore.All().Count == 1, "external removal of prior exclusion was rewritten or forgotten");
            }
        }
        private static void UnknownAndFailureBoundaries()
        {
            foreach (string value in new[] { null, "", "unknown", "d:1", "2" })
            {
                Fresh(); current = value; var receipt = Seed("0", "1", true, @"C:\Games\Unknown");
                Check(!PerformanceActionBackend.Restore(receipt) && writes == 0 && PerformanceActionStore.All().Count == 1, "unknown membership was treated as absent or owned");
            }
            Fresh(); current = "1"; var saved = Seed("0", "1", true, @"C:\Games\Unreadable");
            PerformanceActionBackend.ReadForTest = delegate { throw new IOException("unreadable preference"); };
            bool failed = false; try { PerformanceActionBackend.Restore(saved); } catch (IOException) { failed = true; }
            Check(failed && writes == 0 && PerformanceActionStore.All().Count == 1, "unreadable preference lost original recovery evidence");
            Fresh(); current = "1"; saved = Seed("0", "1", true, @"C:\Games\Unavailable");
            PerformanceActionBackend.DefenderServiceForTest = delegate { return 1; };
            Check(!PerformanceActionBackend.Restore(saved) && reads == 0 && writes == 0 && PerformanceActionStore.All().Count == 1, "inactive Defender erased recovery record");
            Fresh(); current = "1"; saved = Seed("0", "1", false, @"C:\Games\Partial");
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) { writes++; current = "0"; return false; };
            Check(!PerformanceActionBackend.Restore(saved) && PerformanceActionStore.All().Count == 1, "partial removal failure was declared complete");
            Check(PerformanceActionBackend.Restore(saved) && writes == 1 && PerformanceActionStore.All().Count == 0, "already removed partial write was re-added or retried natively");
            foreach (string original in new[] { "absent", "2", "False", "" })
            { Fresh(); current = "1"; saved = Seed(original, "1", true, @"C:\Games\Malformed"); Check(!PerformanceActionBackend.Restore(saved) && writes == 0 && PerformanceActionStore.All().Count == 1, "invalid original was used to delete exclusion"); }
            Fresh(); current = "1"; saved = Seed("0", "0", true, @"C:\Games\BadExpected");
            Check(!PerformanceActionBackend.Restore(saved) && writes == 0 && PerformanceActionStore.All().Count == 1, "unproven expected membership was treated as ownership");
            Fresh(); current = "1"; saved = Seed("0", "1", false, @"C:\Games\Stale");
            var replacement = new PerformanceActionStore.Receipt { Id = saved.Id, Target = saved.Target, Original = "1", Expected = "1", Committed = true };
            Check(Settings.SaveStrDurable(PerformanceActionStore.Slot(saved.Id, saved.Target), PerformanceActionStore.Pack(replacement)), "stale fixture replacement failed");
            Check(!PerformanceActionBackend.Restore(saved) && writes == 0 && PerformanceActionStore.All().Count == 1, "stale caller removed exclusion owned by a newer record");
            Check(PerformanceActionBackend.Restore(replacement) && current == "1" && writes == 0, "replacement pre-existing exclusion was deleted");
        }
        private static int Main()
        {
            try { Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init(); RetiredAdmission(); OwnedAndPreexisting(); UnknownAndFailureBoundaries();
                Console.WriteLine("PASS retired Defender resources assertions=" + assertions + " settings=transient backend=mocked application_started=false native_writes=false"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null; PerformanceActionBackend.DefenderServiceForTest = null; Settings.UseTransientStoreForCurrentProcess(); }
        }
    }
}
#endif
