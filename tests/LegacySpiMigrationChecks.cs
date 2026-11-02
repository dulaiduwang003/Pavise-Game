#if PAVISE_SELFTEST && PAVISE_LEGACY_SPI_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application entrypoint forbidden in legacy SPI checks"); } }
    internal static class LegacySpiMigrationChecks
    {
        private const string Sid = "S-1-5-21-111-222-333-1001";
        private static int assertions;
        private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception("FAIL: " + message); }
        private static void Refuses(Action call, string message)
        { bool refused = false; try { call(); } catch (InvalidDataException) { refused = true; } catch (InvalidOperationException) { refused = true; } catch (IOException) { refused = true; } catch (UnauthorizedAccessException) { refused = true; } Check(refused, message); }
        private sealed class Held : IDisposable
        { private object sync; internal Held(object value) { sync = value; Monitor.Enter(value); } public void Dispose() { if (sync == null) return; object value = sync; sync = null; Monitor.Exit(value); } }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root, OwnerDirectory;
            internal readonly Mutex OriginalMain;
            internal Program.MainInstanceBootstrapLease Main;
            internal readonly SpiOwnerFileStore Store;
            internal readonly SpiOwnership Owner;
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            internal int Reads, Writes;
            internal string ThrowAfterWrite, RefuseWrite;
            internal bool SawLegacyTerminal;
            internal Fixture(string parent)
            {
                Settings.UseTransientStoreForCurrentProcess(); SharedSpiManual.ResetForTest(); Root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
                OwnerDirectory = Path.Combine(Root, "canonical-owner"); Store = new SpiOwnerFileStore(Path.Combine(OwnerDirectory, "owner.tsv"));
                var sync = new object(); Owner = new SpiOwnership(Store, Sid, delegate { return new Held(sync); }); OriginalMain = new Mutex(true); Main = Program.AcquireMainInstanceBootstrapForTest(OriginalMain);
                foreach (var effect in MatchVisualEffects.Effects) Values[effect.Name] = "1";
                PerformanceActionBackend.ReadForTest = delegate(string id, string target) { Check(id == PerformanceActionCatalog.VisualEffects, "Legacy getter domain is exact"); Reads++; return Values[target]; };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) { Check(id == PerformanceActionCatalog.VisualEffects && value == "1", "Legacy setters only restore known original values"); Writes++; if (RefuseWrite == target) return false; Values[target] = value; if (ThrowAfterWrite == target) { ThrowAfterWrite = null; throw new IOException("fake native writer changed value then threw"); } return true; };
                Settings.BeforeStringWriteForTest = delegate { foreach (var row in PerformanceActionStore.All()) if (row.Id == PerformanceActionCatalog.VisualEffects && row.VisualRestored) { SawLegacyTerminal = true; Check(string.IsNullOrEmpty(row.VisualOwner), "Legacy settlement never assigns a new manual token"); } };
            }
            internal void AddLegacy(string target, bool committed = true, bool written = true)
            {
                bool changed = PerformanceActionStore.Change(PerformanceActionCatalog.VisualEffects, target, "0", delegate { return Values[target]; }, delegate(string value) { if (written) Values[target] = value; return committed; }, delegate(string value) { return value == "0"; });
                Check(committed ? changed : !changed, "Legacy fixture persists its actual committed or write-ahead record");
            }
            internal SpiBootstrapResult Run()
            { return SharedSpiBootstrap.InitializeForTest(Main, Sid, Store, Owner); }
            internal void NewMain() { Main.Dispose(); Main = Program.AcquireMainInstanceBootstrapForTest(OriginalMain); }
            public void Dispose() { Settings.BeforeStringWriteForTest = null; Store.BeforePublishForTest = null; SharedSpiManual.ResetForTest(); PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null; Main.Dispose(); try { OriginalMain.ReleaseMutex(); } finally { OriginalMain.Dispose(); } }
        }
        private static void PositiveAndWal(string parent)
        {
            using (var f = new Fixture(parent))
            {
                f.AddLegacy("WindowAnimation"); f.AddLegacy("MenuAnimation"); var result = f.Run(); var record = f.Owner.Read();
                Check(result.Initialized && record.Generation == 0 && record.Phase == SpiOwnerPhase.Closed && record.Owner == null, "Legacy recovery produces closed zero only after original settlement");
                Check(f.Values["WindowAnimation"] == "1" && f.Values["MenuAnimation"] == "1" && f.Writes == 2 && f.SawLegacyTerminal && PerformanceActionStore.All().Count == 0, "Originals and terminal records precede initialization and metadata clear");
                Check(File.Exists(Path.Combine(f.OwnerDirectory, "legacy-migration.tsv")) && File.Exists(Path.Combine(f.OwnerDirectory, "legacy-close.tsv")), "Migration stores only immutable identity/hash closure attachments");
            }
            foreach (bool written in new[] { false, true }) using (var f = new Fixture(parent))
            { f.AddLegacy("WindowAnimation", false, written); var result = f.Run(); Check(result.Initialized && f.Values["WindowAnimation"] == "1" && f.Writes == (written ? 1 : 0), "Legacy write-ahead settles both native-unissued and maybe-written values without applying a candidate"); }
        }
        private static void RetryAndConflicts(string parent)
        {
            using (var f = new Fixture(parent))
            {
                f.AddLegacy("WindowAnimation"); f.ThrowAfterWrite = "WindowAnimation";
                Refuses(delegate { f.Run(); }, "A throwing native restore cannot mint closed zero");
                Check(!File.Exists(f.Store.PathName) && f.Store.ReadLegacy("legacy-migration.tsv") != null && PerformanceActionStore.All().Count == 1, "Exact old receipt and migration identity survive a partial native outcome");
                f.NewMain(); Check(f.Run().Initialized && f.Writes == 1 && f.Values["WindowAnimation"] == "1", "A new verified Main resumes the exact transaction and avoids a duplicate setter");
            }
            using (var f = new Fixture(parent))
            {
                f.AddLegacy("WindowAnimation"); f.Values["WindowAnimation"] = "unknown";
                Refuses(delegate { f.Run(); }, "Unknown current value cannot be guessed as Original or Expected");
                Check(f.Writes == 0 && !File.Exists(f.Store.PathName) && PerformanceActionStore.All().Count == 1, "Conflict preserves original debt and never initializes a free owner");
            }
            using (var f = new Fixture(parent))
            {
                f.AddLegacy("WindowAnimation"); string slot = PerformanceActionStore.Slot(PerformanceActionCatalog.VisualEffects, "WindowAnimation");
                Settings.BeforeStringWriteForTest = delegate(string key) { if (key == slot) throw new IOException("injected terminal save failure"); };
                Refuses(delegate { f.Run(); }, "Original readback without durable terminal remains migration debt");
                Check(f.Values["WindowAnimation"] == "1" && !File.Exists(f.Store.PathName), "Failed terminal commit cannot initialize ownership");
                Settings.BeforeStringWriteForTest = null; f.NewMain(); Check(f.Run().Initialized && f.Writes == 1, "Terminal commit retry uses original readback without another setter");
            }
            using (var f = new Fixture(parent))
            {
                f.AddLegacy("WindowAnimation"); string slot = PerformanceActionStore.Slot(PerformanceActionCatalog.VisualEffects, "WindowAnimation"); Settings.BeforeStringWriteForTest = delegate(string key) { if (key == slot) f.Values["WindowAnimation"] = "0"; };
                Refuses(delegate { f.Run(); }, "Legacy initialization rechecks actual originals after terminal commit");
                Check(!File.Exists(f.Store.PathName) && f.Values["WindowAnimation"] == "0" && f.Writes == 1, "Later external value is preserved without guessing another restore or closed zero");
            }
        }
        private static void MetadataAndTamper(string parent)
        {
            using (var f = new Fixture(parent))
            {
                f.AddLegacy("WindowAnimation"); f.AddLegacy("MenuAnimation"); string blocked = PerformanceActionStore.Slot(PerformanceActionCatalog.VisualEffects, "MenuAnimation"); int saves = 0;
                Settings.BeforeStringWriteForTest = delegate(string key) { if (key == blocked && ++saves > 1) throw new IOException("injected legacy terminal clear fault"); };
                Refuses(delegate { f.Run(); }, "Metadata clear fault remains visible after closed zero");
                Check(f.Owner.Read().Phase == SpiOwnerPhase.Closed && PerformanceActionStore.All().Count == 1, "Closed owner retains the remaining legacy terminal record");
                int reads = f.Reads, writes = f.Writes; Settings.BeforeStringWriteForTest = null; f.NewMain();
                Check(f.Run().Existing && PerformanceActionStore.All().Count == 0 && f.Reads == reads && f.Writes == writes, "Restart clears only bound remaining terminal metadata without native getters/setters");
            }
            using (var f = new Fixture(parent))
            {
                f.AddLegacy("WindowAnimation"); f.ThrowAfterWrite = "WindowAnimation"; Refuses(delegate { f.Run(); }, "Prepare exact interrupted migration for tamper check");
                string manifest = Path.Combine(f.OwnerDirectory, "legacy-migration.tsv"); string text = File.ReadAllText(manifest); string[] lines = text.Split('\n'); string[] member = lines[3].Split('\t'); member[3] = new string('c', 64); lines[3] = string.Join("\t", member); File.WriteAllText(manifest, string.Join("\n", lines), new System.Text.UTF8Encoding(false));
                int reads = f.Reads, writes = f.Writes; f.NewMain(); Refuses(delegate { f.Run(); }, "Altered terminal digest cannot authorize resuming legacy recovery");
                Check(f.Reads == reads && f.Writes == writes && !File.Exists(f.Store.PathName), "Malformed migration identity rejects before any SPI primitive");
            }
        }
        private static int Main(string[] args)
        { try { if (args.Length != 1) return 2; Directory.CreateDirectory(args[0]); PositiveAndWal(args[0]); RetryAndConflicts(args[0]); MetadataAndTamper(args[0]); Console.WriteLine("PASS legacy SPI restore-only assertions=" + assertions + "; original own unnamed Main mutex; fake SPI; transient settings; own files; no new token/candidate/native"); return 0; } catch (Exception error) { Console.WriteLine(error); return 1; } }
    }
}
#endif
