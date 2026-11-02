#if PAVISE_SELFTEST && PAVISE_SPI_BOOTSTRAP_CHECKS
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application entrypoint forbidden in SPI bootstrap checks"); } }
    internal static class SpiBootstrapChecks
    {
        private const string Sid = "S-1-5-21-111-222-333-1001";
        private static int assertions;
        private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception("FAIL: " + message); }
        private static void Refuses(Action call, string message)
        { bool refused = false; try { call(); } catch (InvalidDataException) { refused = true; } catch (InvalidOperationException) { refused = true; } catch (UnauthorizedAccessException) { refused = true; } catch (IOException) { refused = true; } Check(refused, message); }
        private sealed class Held : IDisposable
        { private object sync; internal Held(object value) { sync = value; Monitor.Enter(value); } public void Dispose() { if (sync == null) return; object value = sync; sync = null; Monitor.Exit(value); } }
        private sealed class Fixture : IDisposable
        {
            internal readonly string Root, OwnerDirectory;
            internal readonly Mutex OriginalMain;
            internal readonly Program.MainInstanceBootstrapLease Main;
            internal readonly SpiOwnerFileStore Store;
            internal readonly SpiOwnership Owner;
            internal Action BeforeGate;
            internal Fixture(string parent)
            {
                Settings.UseTransientStoreForCurrentProcess(); Root = Path.Combine(parent, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root);
                OwnerDirectory = Path.Combine(Root, "canonical-owner"); Store = new SpiOwnerFileStore(Path.Combine(OwnerDirectory, "owner.tsv"));
                var sync = new object(); Owner = new SpiOwnership(Store, Sid, delegate { var held = new Held(sync); try { var before = BeforeGate; if (before != null) before(); return held; } catch { held.Dispose(); throw; } });
                OriginalMain = new Mutex(true); Main = Program.AcquireMainInstanceBootstrapForTest(OriginalMain);
            }
            internal SpiBootstrapResult Initialize()
            { return SharedSpiBootstrap.InitializeForTest(Main, Sid, Store, Owner); }
            public void Dispose() { BeforeGate = null; Store.BeforePublishForTest = null; Main.Dispose(); try { OriginalMain.ReleaseMutex(); } finally { OriginalMain.Dispose(); } }
        }
        private static void FreshAndExisting(string parent)
        {
            using (var f = new Fixture(parent))
            {
                var result = f.Initialize(); var record = f.Owner.Read(); f.Store.ValidateInitialization(Sid, record);
                Check(result.Initialized && !result.Existing && record.Generation == 0 && record.Phase == SpiOwnerPhase.Closed, "Real original-main lease and explicit empty evidence mint closed zero");
                Check(File.Exists(Path.Combine(f.OwnerDirectory, "initialization.tsv")), "One-time initialization sentinel precedes the owner record");
                Refuses(delegate { f.Initialize(); }, "An original main lease cannot bootstrap twice");
            }
            using (var f = new Fixture(parent))
            {
                f.Initialize(); byte[] owner = f.Store.Read(); f.Main.Dispose();
                using (var next = Program.AcquireMainInstanceBootstrapForTest(f.OriginalMain))
                { var result = SharedSpiBootstrap.InitializeForTest(next, Sid, f.Store, f.Owner); Check(result.Existing && !result.Initialized && SpiOwnerRecord.Equal(owner, f.Store.Read()), "Existing valid protocol is retained without free-slot replacement"); }
            }
            // Stores initialized before the retired data root still carry its two version-2 lines.
            using (var f = new Fixture(parent))
            {
                byte[] v2 = new UTF8Encoding(false, true).GetBytes("pavise-spi-initialization-2\nuser_sid\t" + Sid + "\nnonce\t" + Guid.NewGuid().ToString("N")
                    + "\nknown_root64\t" + Convert.ToBase64String(Encoding.UTF8.GetBytes(@"C:\Retired\Root")) + "\nroot_ledger\troot-absent\nlegacy_closure\tnone\nmain\toriginal-single-instance-lease-v1\n");
                Directory.CreateDirectory(f.OwnerDirectory); File.WriteAllBytes(Path.Combine(f.OwnerDirectory, "initialization.tsv"), v2);
                byte[] owner = new SpiOwnerRecord(Sid, 0, SpiOwnerPhase.Closed, null, SpiOwnerRecord.Digest(v2)).Bytes(); File.WriteAllBytes(f.Store.PathName, owner);
                SpiBootstrapResult result = null;
                try { result = f.Initialize(); } catch (InvalidDataException) { }
                Check(result != null && result.Existing && !result.Initialized && SpiOwnerRecord.Equal(owner, f.Store.Read()), "Version 2 initialization is still a valid existing owner");
            }
            using (var f = new Fixture(parent))
            {
                byte[] torn = new UTF8Encoding(false, true).GetBytes("pavise-spi-initialization-2\nuser_sid\t" + Sid + "\nnonce\t" + Guid.NewGuid().ToString("N")
                    + "\nroot_ledger\troot-absent\nlegacy_closure\tnone\nmain\toriginal-single-instance-lease-v1\n");
                Directory.CreateDirectory(f.OwnerDirectory); File.WriteAllBytes(Path.Combine(f.OwnerDirectory, "initialization.tsv"), torn);
                var record = new SpiOwnerRecord(Sid, 0, SpiOwnerPhase.Closed, null, SpiOwnerRecord.Digest(torn));
                Refuses(delegate { f.Store.ValidateInitialization(Sid, record); }, "Version 2 initialization missing a line is not reinterpreted");
            }
        }
        private static void UnknownAndDebt(string parent)
        {
            using (var f = new Fixture(parent))
            {
                Directory.CreateDirectory(f.OwnerDirectory); File.WriteAllText(Path.Combine(f.OwnerDirectory, "foreign-canary"), "preserve");
                Refuses(delegate { f.Initialize(); }, "Existing owner directory with missing record is unknown"); Check(!File.Exists(f.Store.PathName) && File.ReadAllText(Path.Combine(f.OwnerDirectory, "foreign-canary")) == "preserve", "Missing owner is not repaired or initialized over existing data");
            }
            using (var f = new Fixture(parent))
            { Directory.CreateDirectory(f.OwnerDirectory); File.WriteAllText(f.Store.PathName, "torn"); Refuses(delegate { f.Initialize(); }, "Corrupt existing owner cannot become closed zero"); Check(File.ReadAllText(f.Store.PathName) == "torn", "Corrupt evidence is preserved"); }
            using (var f = new Fixture(parent))
            {
                string current = "1"; PerformanceActionStore.Change(PerformanceActionCatalog.VisualEffects, "WindowAnimation", "0", delegate { return current; }, delegate(string next) { current = next; return true; }, delegate(string value) { return value == "0"; });
                Refuses(delegate { f.Initialize(); }, "Legacy restore-only upgrade cannot execute an unmocked getter in isolated checks");
                Check(!File.Exists(f.Store.PathName) && PerformanceActionStore.All().Count == 1, "Unconfirmed legacy recovery cannot mint a free slot");
            }
        }
        private static void PublicationAndCapabilities(string parent)
        {
            using (var f = new Fixture(parent))
            {
                f.Store.BeforePublishForTest = delegate(string path) { if (path == f.Store.PathName) throw new IOException("injected first owner publication fault"); };
                Refuses(delegate { f.Initialize(); }, "Failed closed-zero publication cannot leave a partial record");
                Check(File.Exists(Path.Combine(f.OwnerDirectory, "initialization.tsv")) && !File.Exists(f.Store.PathName), "One-time sentinel remains a witness of interrupted initialization");
                Check(Directory.GetFiles(f.OwnerDirectory, "*.tmp").Length == 0, "Initialization removes only its own temporary files");
                f.Main.Dispose(); f.Store.BeforePublishForTest = null;
                using (var next = Program.AcquireMainInstanceBootstrapForTest(f.OriginalMain)) Refuses(delegate { SharedSpiBootstrap.InitializeForTest(next, Sid, f.Store, f.Owner); }, "Later startup cannot reinterpret interrupted initialization as never initialized");
            }
            using (var f = new Fixture(parent))
            { f.Store.BeforePublishForTest = delegate { f.Main.Dispose(); }; Refuses(delegate { f.Initialize(); }, "Invalidated original Main fact cannot commit closed zero"); Check(!File.Exists(f.Store.PathName), "Revoked original Main fact has no ownership publication"); }
            using (var f = new Fixture(parent))
            {
                f.BeforeGate = delegate { f.BeforeGate = null; string value = "1"; PerformanceActionStore.Change(PerformanceActionCatalog.VisualEffects, "WindowAnimation", "0", delegate { return value; }, delegate(string next) { value = next; return true; }, delegate(string current) { return current == "0"; }); };
                Refuses(delegate { f.Initialize(); }, "Manual receipt emptiness is rechecked inside the short commit gate"); Check(!Directory.Exists(f.OwnerDirectory), "Newly appeared legacy debt cannot receive closed zero");
            }
            using (var f = new Fixture(parent))
            {
                Refuses(delegate { new SharedSpiBootstrap.BootstrapProof(new object(), f.Main, Sid, f.Store.PathName, new byte[] { 1 }); }, "An arbitrary caller cannot mint startup bootstrap proof");
                Refuses(delegate { SharedSpiBootstrap.Initialize(f.Main); }, "Isolated startup cannot perform real user/canonical inventory");
                Check(!Directory.Exists(f.OwnerDirectory), "Unmocked bootstrap rejection precedes canonical writes");
            }
        }
        private static int Main(string[] args)
        { try { if (args.Length != 1) return 2; Directory.CreateDirectory(args[0]); FreshAndExisting(args[0]); UnknownAndDebt(args[0]); PublicationAndCapabilities(args[0]); Console.WriteLine("PASS SPI bootstrap assertions=" + assertions + "; original own unnamed Main-mutex capability; transient settings/own files; no application/global mutex/SPI"); return 0; } catch (Exception error) { Console.WriteLine(error); return 1; } }
    }
}
#endif
