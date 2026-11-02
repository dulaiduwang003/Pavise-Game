#if PAVISE_SELFTEST && PAVISE_MANUAL_SPI_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application entrypoint forbidden in manual SPI checks"); } }
    internal static class ManualSpiOwnershipChecks
    {
        private static int assertions;
        private const string Sid = "S-1-5-21-111-222-333-1001";
        private static readonly string Digest = new string('b', 64);
        private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception("FAIL: " + message); }
        private static void Refuses(Action call, string message)
        { bool refused = false; try { call(); } catch (InvalidDataException) { refused = true; } catch (InvalidOperationException) { refused = true; } catch (IOException) { refused = true; } catch (UnauthorizedAccessException) { refused = true; } Check(refused, message); }
        private sealed class Held : IDisposable
        { private object value; internal Held(object sync) { value = sync; Monitor.Enter(sync); } public void Dispose() { if (value == null) return; object release = value; value = null; Monitor.Exit(release); } }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root;
            internal readonly SpiOwnerFileStore Store;
            internal readonly SpiOwnership Owner;
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            internal int Reads, Writes;
            internal Fixture(string parent)
            {
                Settings.UseTransientStoreForCurrentProcess(); SharedSpiManual.ResetForTest();
                Root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); Store = new SpiOwnerFileStore(Path.Combine(Root, "owner.tsv"));
                var sync = new object(); Owner = new SpiOwnership(Store, Sid, delegate { return new Held(sync); });
                var initial = new SpiOwnerRecord(Sid, 0, SpiOwnerPhase.Closed, null, Digest); Owner.InitializeForTest(SpiOwnerEvidence.ForTest(initial, Digest), initial);
                foreach (var effect in MatchVisualEffects.Effects) Values[effect.Name] = "1";
                SharedSpiManual.OwnerForTest = delegate { return Owner; };
                PerformanceActionBackend.ReadForTest = delegate(string id, string target) { Check(id == PerformanceActionCatalog.VisualEffects, "Fake getter is the exact visual SPI domain"); Reads++; return Values[target]; };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) { Check(id == PerformanceActionCatalog.VisualEffects, "Fake setter is the exact visual SPI domain"); Writes++; Values[target] = value; return true; };
            }
            internal PerformanceActionStore.Receipt Receipt(string target)
            { foreach (var receipt in PerformanceActionStore.All()) if (receipt.Id == PerformanceActionCatalog.VisualEffects && receipt.Target == target) return PerformanceActionStore.Unpack(PerformanceActionStore.Pack(receipt)); throw new Exception("Fixture receipt missing: " + target); }
            internal bool Apply(string target) { return PerformanceActionBackend.Apply(PerformanceActionCatalog.VisualEffects, target, "0"); }
            internal bool Restore(string target) { return PerformanceActionBackend.Restore(Receipt(target)); }
            public void Dispose() { Settings.BeforeStringWriteForTest = null; Store.BeforePublishForTest = null; SharedSpiManual.ResetForTest(); PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null; }
        }
        private static void LifecycleAndAba(string parent)
        {
            using (var f = new Fixture(parent))
            {
                Check(f.Apply("WindowAnimation") && f.Apply("MenuAnimation"), "Manual backend uses the shared owner pipeline");
                var old = f.Receipt("WindowAnimation"); string firstEpoch = old.VisualOwner;
                Check(firstEpoch.Length != 0 && PerformanceActionStore.Pack(old).StartsWith("visual2|", StringComparison.Ordinal), "Visual receipt persists its owner epoch");
                Check(f.Restore("WindowAnimation"), "First original restoration succeeds"); bool retainedTerminal = false;
                foreach (var row in PerformanceActionStore.All()) if (row.Id == PerformanceActionCatalog.VisualEffects && row.Target == "WindowAnimation") retainedTerminal = row.VisualRestored;
                Check(retainedTerminal && f.Owner.Read().Phase == SpiOwnerPhase.RestoreOnly, "First restored receipt retains originals while another field is active");
                int reads = f.Reads, writes = f.Writes;
                Refuses(delegate { f.Apply("CursorShadow"); }, "Pending old owner cannot reenter apply"); Check(f.Reads == reads && f.Writes == writes, "Debt rejects both native getter and setter");
                Check(f.Restore("MenuAnimation") && f.Owner.Read().Phase == SpiOwnerPhase.Closed && PerformanceActionStore.All().Count == 0, "All originals and durable terminals precede owner close and metadata clear");
                Check(f.Values["WindowAnimation"] == "1" && f.Values["MenuAnimation"] == "1", "Original visual values were confirmed");
                Check(f.Apply("WindowAnimation") && f.Receipt("WindowAnimation").VisualOwner != firstEpoch, "An identical later request gets a new epoch");
                reads = f.Reads; writes = f.Writes;
                Refuses(delegate { PerformanceActionBackend.Restore(old); }, "Old same-value receipt cannot restore the new owner");
                Check(f.Values["WindowAnimation"] == "0" && f.Reads == reads && f.Writes == writes && !f.Receipt("WindowAnimation").VisualRestored, "Stale restore leaves the new value and receipt untouched");
                Check(!PerformanceActionStore.Restore(f.Receipt("WindowAnimation"), delegate { throw new Exception("Unexpected old API read"); }, delegate { throw new Exception("Unexpected old API write"); }, delegate { return true; }), "Generic legacy restore cannot bypass an owned visual receipt");
            }
        }
        private static void NoWriteStillHasSnapshot(string parent)
        {
            using (var f = new Fixture(parent))
            { f.Values["WindowAnimation"] = "0"; Check(f.Apply("WindowAnimation") && f.Writes == 0 && f.Receipt("WindowAnimation").Original == "0", "Same-value requests keep an explicit owned original without a setter"); Check(f.Restore("WindowAnimation") && f.Writes == 0 && f.Owner.Read().Phase == SpiOwnerPhase.Closed, "No-write ownership closes through original readback and terminal evidence"); }
        }
        private static void JournalAndCloseFailures(string parent)
        {
            using (var f = new Fixture(parent))
            {
                f.Apply("WindowAnimation"); string slot = PerformanceActionStore.Slot(PerformanceActionCatalog.VisualEffects, "WindowAnimation");
                Settings.BeforeStringWriteForTest = delegate(string key) { if (key == slot) throw new IOException("injected terminal journal failure"); };
                Check(!f.Restore("WindowAnimation") && f.Values["WindowAnimation"] == "1" && !f.Receipt("WindowAnimation").VisualRestored && f.Owner.Read().Phase == SpiOwnerPhase.RestoreOnly, "Restored native value without durable terminal remains held debt");
                Settings.BeforeStringWriteForTest = null; Check(f.Restore("WindowAnimation") && PerformanceActionStore.All().Count == 0, "Retry uses retained original after terminal save failure");
            }
            using (var f = new Fixture(parent))
            {
                f.Apply("WindowAnimation"); f.Store.BeforePublishForTest = delegate(string path) { if (path == f.Store.PathName && f.Owner.Read().Phase == SpiOwnerPhase.RestoreOnly) throw new IOException("injected owner close failure"); };
                Refuses(delegate { f.Restore("WindowAnimation"); }, "Owner publication failure cannot consume terminal receipts");
                Check(f.Receipt("WindowAnimation").VisualRestored && f.Owner.Read().Phase == SpiOwnerPhase.RestoreOnly, "Terminal original remains inspectable across failed owner close");
                f.Store.BeforePublishForTest = null; Check(f.Restore("WindowAnimation") && f.Owner.Read().Phase == SpiOwnerPhase.Closed, "Same epoch can retry close without recreating ownership");
            }
            using (var f = new Fixture(parent))
            {
                f.Apply("WindowAnimation"); string slot = PerformanceActionStore.Slot(PerformanceActionCatalog.VisualEffects, "WindowAnimation");
                Settings.BeforeStringWriteForTest = delegate(string key) { if (key == slot) f.Values["WindowAnimation"] = "0"; };
                Refuses(delegate { f.Restore("WindowAnimation"); }, "Owner closure rechecks original readback after durable terminal publication");
                Check(f.Owner.Read().Phase == SpiOwnerPhase.RestoreOnly && f.Receipt("WindowAnimation").VisualRestored && f.Values["WindowAnimation"] == "0", "Later external value is preserved and closure remains pending");
            }
        }
        private static void PartialMetadataClear(string parent)
        {
            using (var f = new Fixture(parent))
            {
                f.Apply("WindowAnimation"); f.Apply("MenuAnimation"); f.Restore("WindowAnimation");
                string blocked = PerformanceActionStore.Slot(PerformanceActionCatalog.VisualEffects, "MenuAnimation");
                Settings.BeforeStringWriteForTest = delegate(string key) { if (key == blocked) throw new IOException("injected partial terminal clear failure"); };
                // Failure is installed only after this row has reached terminal, so the
                // fake hook counts its first terminal Save and blocks its later Clear.
                int calls = 0; Settings.BeforeStringWriteForTest = delegate(string key) { if (key == blocked && ++calls > 1) throw new IOException("injected partial terminal clear failure"); };
                Check(!f.Restore("MenuAnimation") && f.Owner.Read().Phase == SpiOwnerPhase.Closed && PerformanceActionStore.All().Count == 1, "Partial metadata deletion preserves closed epoch and remaining exact terminal");
                int reads = f.Reads, writes = f.Writes;
                Refuses(delegate { f.Owner.ClaimManualFromClosed(f.Owner.Read()); }, "Closed plus uncleared terminal metadata cannot authorize a later claim");
                Check(f.Reads == reads && f.Writes == writes, "Closed metadata retry has no native getter or setter");
                Settings.BeforeStringWriteForTest = null;
                string manifest = Directory.GetFiles(f.Root, "close-*.tsv")[0]; byte[] originalManifest = File.ReadAllBytes(manifest);
                var lines = new List<string>(File.ReadAllLines(manifest)); string clearedSlot = PerformanceActionStore.Slot(PerformanceActionCatalog.VisualEffects, "WindowAnimation"); lines.RemoveAll(delegate(string line) { return line.StartsWith(clearedSlot + "\t", StringComparison.Ordinal); }); File.WriteAllText(manifest, string.Join("\n", lines.ToArray()) + "\n", new System.Text.UTF8Encoding(false));
                Refuses(delegate { f.Restore("MenuAnimation"); }, "Even a removed already-cleared manifest member changes the bound closure proof");
                Check(PerformanceActionStore.All().Count == 1 && f.Reads == reads && f.Writes == writes, "Altered closed evidence cannot clear remaining receipt metadata or call SPI");
                File.WriteAllBytes(manifest, originalManifest); Check(f.Restore("MenuAnimation") && PerformanceActionStore.All().Count == 0 && f.Reads == reads && f.Writes == writes, "Closure manifest permits exact remaining-subset cleanup after a crash window");
                Check(f.Apply("WindowAnimation"), "A new owner is admitted after all terminal metadata clears");
            }
        }
        private static void LegacyAndFences(string parent)
        {
            using (var f = new Fixture(parent))
            {
                string value = "1"; Check(PerformanceActionStore.Change(PerformanceActionCatalog.VisualEffects, "WindowAnimation", "0", delegate { return value; }, delegate(string next) { value = next; return true; }, delegate(string current) { return current == "0"; }), "Legacy fixture has an actual five-field receipt");
                var legacy = f.Receipt("WindowAnimation");
                Refuses(delegate { f.Apply("MenuAnimation"); }, "Legacy debt cannot be silently assigned a new token");
                Check(!PerformanceActionBackend.Restore(legacy) && f.Reads == 0 && f.Writes == 0 && string.IsNullOrEmpty(f.Receipt("WindowAnimation").VisualOwner), "Normal restoration cannot guess legacy ownership");
                Check(Settings.Save(PerformanceActionCatalog.VisualEffects, true) && !MatchVisualEffects.Enabled, "Retired visual-effect preference cannot reactivate native writes");
            }
            using (var f = new Fixture(parent))
            {
                Refuses(delegate { PerformanceActionBackend.Read(PerformanceActionCatalog.VisualEffects, "WindowAnimation"); }, "Direct backend getter requires a held owner capability");
                Refuses(delegate { MatchVisualEffects.Read("WindowAnimation"); }, "Raw SPI getter refuses before native access");
                SharedSpiManual.ResetForTest(); Refuses(delegate { f.Apply("WindowAnimation"); }, "Isolated checks cannot silently open canonical production ownership storage"); Check(f.Reads == 0 && f.Writes == 0, "Missing capability and factory make no native calls");
            }
        }
        private static int Main(string[] args)
        { try { if (args.Length != 1) return 2; Directory.CreateDirectory(args[0]); LifecycleAndAba(args[0]); NoWriteStillHasSnapshot(args[0]); JournalAndCloseFailures(args[0]); PartialMetadataClear(args[0]); LegacyAndFences(args[0]); Console.WriteLine("PASS manual SPI ownership assertions=" + assertions + "; actual backend/store/proof factories; fake getter/setter; transient settings; own files; native=false"); return 0; } catch (Exception error) { Console.WriteLine(error); return 1; } }
    }
}
#endif
