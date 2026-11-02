#if PAVISE_SELFTEST && PAVISE_INTERACTIVE_UNINSTALL_TEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static class SelfTests
    { internal static bool TryHandleRuntimeMode(string[] args) { return false; } }

    internal static class InteractiveUninstallRunner
    {
        private static string output;
        private static string productionFixture;
        private static int suites, assertions, failed;

        private static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--finish-uninstall") return UninstallFinalizer.Run(args[1]) ? 0 : 1;
            if (args.Length == 3 && args[0] == "--launch-fixture")
            {
                StartupPayload.ManagedRootForTest = Path.Combine(args[1], "Autostart");
                string failure;
                bool launched = UninstallFinalizer.Launch(Process.GetCurrentProcess().MainModule.FileName, out failure);
                File.WriteAllText(args[2], launched ? UninstallFinalizer.LastLaunchedDirectoryForTest : "ERROR " + failure);
                Thread.Sleep(2000);
                return launched ? 0 : 2;
            }
            if (args.Length == 2 && args[0] == "--exit-fixture")
            {
                using (var me = Process.GetCurrentProcess())
                    File.WriteAllLines(args[1], new[] { me.Id.ToString(), me.StartTime.ToUniversalTime().Ticks.ToString(), me.MainModule.FileName });
                return 0;
            }
            if (args.Length != 2) return 2;
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            productionFixture = Path.GetFullPath(args[1]);
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 60000, Timeout.Infinite))
            {
                Run("failed runtime stop leaves all data and startup untouched", StopFailure);
                Run("invalid data directory rejects restoration before stopping runtime", InvalidRoot);
                Run("failed restoration clears preferences and library but retains original values", RestoreFailure);
                Run("startup failure does not block independently restored data", StartupFailure);
                Run("locked owned file does not block siblings or restored registry cleanup", LockedData);
                Run("progress callback exceptions do not change cleanup outcome", ProgressFailure);
                Run("real helper waits for original PID exit and deletes only verified exe copies", delegate { LaunchFixture(false); });
                Run("real helper skips locked current exe and removes independent managed copy", delegate { LaunchFixture(true); });
                Run("changed managed copy does not block original executable removal", delegate { LaunchFixture(false, true); });
                Run("changed executable content is preserved", ChangedExecutable);
                Run("mismatched parent identity preserves the executable", WrongParent);
                Run("arbitrary managed target is rejected", OutsideManaged);
                Run("malformed manifest does not remove the target", MalformedManifest);
                Run("redirected executable ancestors are rejected", RedirectedPath);
                Run("production helper removes its own temporary copy after verified deletion", ProductionHelperCleanup);
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failed + " assertions=" + assertions
                    + " restore=mocked settings=transient startup=mocked real_helper=fixture_only native_tuning=0");
                return failed == 0 ? 0 : 1;
            }
        }

        private static void Run(string name, Action test)
        { suites++; try { test(); Console.WriteLine("PASS " + name); } catch (Exception error) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + error); } }
        private static void Check(bool value, string why)
        { assertions++; if (!value) throw new InvalidOperationException(why); }
        private static string Root()
        {
            string root = Path.Combine(Path.GetTempPath(), "Pavise-UninstallFixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); return root;
        }
        private static void Prepare()
        {
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            LegacyPurge.RestoreHook = delegate { return new List<string>(); };
            LegacyPurge.DeleteRegistryHook = delegate { return true; };
            AppDataReset.ClearUserPreferencesForTest = null;
        }
        private static void Finish(string root)
        {
            LegacyPurge.RestoreHook = null; LegacyPurge.DeleteRegistryHook = null;
            AppDataReset.ClearUserPreferencesForTest = null;
            try { Directory.Delete(root, true); } catch { }
        }
        private static string Seed(string root, string name)
        { string path = Path.Combine(root, name); File.WriteAllText(path, "fixture"); return path; }
        private static void StopFailure()
        {
            string root = Root(); Prepare();
            try
            {
                string file = Seed(root, "Pavise.games.txt"); int restores = 0, tasks = 0;
                LegacyPurge.RestoreHook = delegate { restores++; return new List<string>(); };
                UninstallReport result = UninstallMode.ExecuteInteractive(root, Path.Combine(root, "Pavise.exe"),
                    delegate { return false; }, null, delegate { tasks++; return true; });
                Check(!result.RuntimeStopped && !result.Cleared && !string.IsNullOrEmpty(result.Failure), "Stop failure must be explicit");
                Check(restores == 0 && tasks == 0 && File.Exists(file), "Stop failure caused side effects");
            }
            finally { Finish(root); }
        }
        private static void InvalidRoot()
        {
            Prepare(); int stops = 0, tasks = 0, restores = 0;
            LegacyPurge.RestoreHook = delegate { restores++; return new List<string>(); };
            UninstallReport result = UninstallMode.ExecuteInteractive(".", "Pavise.exe", delegate { stops++; return true; }, null,
                delegate { tasks++; return true; });
            Check(!result.RuntimeStopped && !result.Cleared && stops == 0 && tasks == 0 && restores == 0, "Invalid path admitted cleanup");
        }
        private static void RestoreFailure()
        {
            string root = Root(); Prepare();
            try
            {
                string library = Seed(root, "Pavise.games.txt"), log = Seed(root, "Pavise.log");
                string recovery = Seed(root, "Pavise.freeze.state"), unrelated = Seed(root, "Other.txt");
                Settings.Save("GmAutoAdd", true); Settings.SaveStr("Performance.ReceiptV1", "original");
                LegacyPurge.RestoreHook = delegate { return new List<string> { "CFG" }; };
                UninstallReport result = UninstallMode.ExecuteInteractive(root, Path.Combine(root, "Pavise.exe"), delegate { return true; }, null, delegate { return true; });
                Check(result.RuntimeStopped && result.Cleared && result.Warnings == 1 && result.Failure == null, "Restoration warning blocked independent cleanup");
                Check(!File.Exists(library) && !File.Exists(log) && File.Exists(recovery) && File.Exists(unrelated), "User data/recovery ownership boundary violated");
                Check(!Settings.Load("GmAutoAdd", false) && Settings.LoadStr("Performance.ReceiptV1", "") == "original", "Original value was forgotten");
                Check(!Settings.SaveStr("late", "write"), "Late writer recreated settings");
            }
            finally { Finish(root); }
        }
        private static void StartupFailure()
        {
            string root = Root(); Prepare();
            try
            {
                string library = Seed(root, "Pavise.games.txt"); int registries = 0;
                LegacyPurge.DeleteRegistryHook = delegate { registries++; return true; };
                UninstallReport result = UninstallMode.ExecuteInteractive(root, Path.Combine(root, "Pavise.exe"), delegate { return true; }, null, delegate { return false; });
                Check(result.RuntimeStopped && result.Cleared && !result.StartupRemoved && result.Warnings == 1, "Startup failure prevented cleanup");
                Check(!File.Exists(library) && registries == 1, "Independent restored data was not cleared");
            }
            finally { Finish(root); }
        }
        private static void LockedData()
        {
            string root = Root(); Prepare();
            try
            {
                string locked = Seed(root, "Pavise.games.txt"), log = Seed(root, "Pavise.log"); int registries = 0;
                LegacyPurge.DeleteRegistryHook = delegate { registries++; return true; };
                using (var stream = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    UninstallReport result = UninstallMode.ExecuteInteractive(root, Path.Combine(root, "Pavise.exe"), delegate { return true; }, null, delegate { return true; });
                    Check(!result.Cleared && result.Warnings == 1 && result.Failure == null, "Locked file wasn't a local warning");
                    Check(File.Exists(locked) && !File.Exists(log) && registries == 1, "Locked file blocked independent items");
                }
            }
            finally { Finish(root); }
        }
        private static void ProgressFailure()
        {
            string root = Root(); Prepare();
            try
            {
                Seed(root, "Pavise.games.txt");
                UninstallReport result = UninstallMode.ExecuteInteractive(root, Path.Combine(root, "Pavise.exe"), delegate { return true; },
                    delegate { throw new IOException("UI gone"); }, delegate { return true; });
                Check(result.Cleared && result.Warnings == 0 && result.Failure == null, "UI callback changed operation result");
            }
            finally { Finish(root); }
        }
        private static Process Spawn(string executable, string arguments)
        {
            var info = new ProcessStartInfo(executable, arguments) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(executable) };
            return Process.Start(info);
        }
        private static string Q(string path) { return "\"" + path + "\""; }
        private static bool WaitFile(string path, int milliseconds)
        {
            var watch = Stopwatch.StartNew();
            while (!File.Exists(path) && watch.ElapsedMilliseconds < milliseconds) Thread.Sleep(25);
            return File.Exists(path);
        }
        private static bool WaitHelperExit(string directory)
        {
            string executable = Path.Combine(directory, "Pavise.Uninstall.exe");
            foreach (Process process in Process.GetProcessesByName("Pavise.Uninstall"))
                using (process)
                {
                    try
                    {
                        if (string.Equals(process.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase)
                            && !process.WaitForExit(10000)) return false;
                    }
                    catch (InvalidOperationException) { }
                }
            return true;
        }
        private static void LaunchFixture(bool locked, bool changeManaged = false)
        {
            string root = Root(), helperDirectory = null; Process parent = null; FileStream lockStream = null;
            try
            {
                string source = Path.Combine(root, "RenamedPavise.exe"); File.Copy(Process.GetCurrentProcess().MainModule.FileName, source);
                string other = Seed(root, "unrelated.exe"); string unrelated = Seed(root, "keep.txt");
                string managedRoot = Path.Combine(root, "Autostart");
                string managed = StartupPayload.Ensure(source, managedRoot);
                string corruptDirectory = Path.Combine(managedRoot, new string('a', 64)); Directory.CreateDirectory(corruptDirectory);
                string corrupt = Seed(corruptDirectory, "PaviseAuto.exe");
                string info = Path.Combine(root, "launch.txt");
                if (locked) lockStream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                parent = Spawn(source, "--launch-fixture " + Q(root) + " " + Q(info));
                Check(WaitFile(info, 10000), "Fixture parent did not launch helper");
                helperDirectory = File.ReadAllText(info); Check(!helperDirectory.StartsWith("ERROR"), helperDirectory);
                Check(File.Exists(source) && !parent.HasExited, "Executable removed before parent exit");
                if (changeManaged) File.WriteAllText(managed, "changed after scheduling");
                string result = Path.Combine(helperDirectory, "result.txt");
                Check(!File.Exists(result), "Helper finished while parent was running");
                Check(parent.WaitForExit(10000) && parent.ExitCode == 0, "Fixture parent failed");
                Check(WaitFile(result, 15000), "Helper never completed");
                Check(WaitHelperExit(helperDirectory), "Fixture helper did not exit after writing its result");
                Check(File.Exists(source) == locked, "Current executable delete/skip result was incorrect");
                Check(File.Exists(managed) == changeManaged && File.Exists(corrupt), "Managed snapshot deleted corrupt/unowned file or left verified copy");
                Check(File.Exists(other) && File.Exists(unrelated) && Directory.Exists(root), "Sibling executable or parent directory was touched");
                Check(File.ReadAllText(result) == (locked || changeManaged ? "skipped" : "removed"), "Helper completion status lied");
            }
            finally
            {
                if (parent != null) { if (!parent.HasExited) parent.Kill(); parent.Dispose(); }
                if (lockStream != null) lockStream.Dispose();
                if (helperDirectory != null) WaitHelperExit(helperDirectory);
                if (helperDirectory != null && Directory.Exists(helperDirectory)) Directory.Delete(helperDirectory, true);
                Directory.Delete(root, true);
            }
        }

        private static void ChangedExecutable()
        { ManualPlan("changed", false, false, false); }
        private static void WrongParent()
        { ManualPlan("same", true, false, false); }
        private static void OutsideManaged()
        { ManualPlan("same", false, true, false); }
        private static void MalformedManifest()
        { ManualPlan("same", false, false, true); }
        private static void ManualPlan(string contents, bool wrongParent, bool outsideManaged, bool malformed)
        {
            string root = Root(); string directory = Path.Combine(Path.GetTempPath(), "Pavise-Uninstall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string binary = Process.GetCurrentProcess().MainModule.FileName;
                string helper = Path.Combine(directory, "Pavise.Uninstall.exe"); File.Copy(binary, helper);
                string target = Path.Combine(root, "Pavise.exe");
                if (contents == "same") File.Copy(binary, target); else File.WriteAllText(target, "other application");
                string identityFile = Path.Combine(root, "identity.txt");
                int pid; long started;
                using (Process dead = Spawn(helper, "--exit-fixture " + Q(identityFile)))
                { Check(dead.WaitForExit(10000) && dead.ExitCode == 0, "Identity fixture failed"); }
                string[] identity = File.ReadAllLines(identityFile); pid = int.Parse(identity[0]); started = long.Parse(identity[1]);
                if (wrongParent) using (Process current = Process.GetCurrentProcess())
                { pid = current.Id; started = current.StartTime.ToUniversalTime().Ticks; }
                string manifest = Path.Combine(directory, "cleanup.dat");
                if (malformed) File.WriteAllText(manifest, "wrong format");
                else using (var writer = new BinaryWriter(File.Open(manifest, FileMode.CreateNew)))
                {
                    writer.Write("Pavise uninstall 1"); writer.Write(pid); writer.Write(started);
                    writer.Write(target); writer.Write(Path.GetFileName(Path.GetDirectoryName(StartupPayload.GetExpectedExecutable(binary, Path.Combine(root, "Autostart")))));
                    writer.Write(Path.Combine(root, "Autostart")); writer.Write(outsideManaged ? 1 : 0);
                    if (outsideManaged) writer.Write(target);
                }
                using (Process finalizer = Spawn(helper, "--finish-uninstall " + Q(manifest)))
                { Check(finalizer.WaitForExit(10000) && finalizer.ExitCode != 0, "Unsafe plan wasn't rejected"); }
                Check(File.Exists(target), "Unsafe plan deleted its target");
                Check(Directory.Exists(root), "Unsafe plan touched its parent directory");
            }
            finally { Directory.Delete(directory, true); Directory.Delete(root, true); }
        }
        private static void RedirectedPath()
        {
            string root = Root(); string external = Path.Combine(root, "external"), link = Path.Combine(root, "linked");
            Directory.CreateDirectory(external);
            try
            {
                string target = Path.Combine(external, "Pavise.exe"); File.Copy(Process.GetCurrentProcess().MainModule.FileName, target);
                using (Process create = Spawn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    "/c mklink /J " + Q(link) + " " + Q(external)))
                { Check(create.WaitForExit(5000) && create.ExitCode == 0, "Fixture junction creation failed"); }
                Prepare(); int stops = 0;
                UninstallReport result = UninstallMode.ExecuteInteractive(link, Path.Combine(link, "Pavise.exe"),
                    delegate { stops++; return true; }, null, delegate { return true; });
                Check(!result.RuntimeStopped && stops == 0 && File.Exists(target), "Redirected data directory admitted cleanup");
                Check(!LegacyPurge.IsSafeInteractiveDataRoot(Path.Combine(link, "nested")), "Redirected ancestor wasn't rejected");
            }
            finally { if (Directory.Exists(link)) Directory.Delete(link, false); Finish(root); }
        }

        private static void ProductionHelperCleanup()
        {
            string root = Root(); string directory = Path.Combine(Path.GetTempPath(), "Pavise-Uninstall-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string source = Path.Combine(root, "Pavise.exe"); File.Copy(productionFixture, source);
                string identityFile = Path.Combine(root, "identity.txt");
                using (Process dead = Spawn(Process.GetCurrentProcess().MainModule.FileName, "--exit-fixture " + Q(identityFile)))
                { Check(dead.WaitForExit(10000) && dead.ExitCode == 0, "Dead parent identity fixture failed"); }
                string[] identity = File.ReadAllLines(identityFile);
                string helper = Path.Combine(directory, "Pavise.Uninstall.exe"); File.Copy(source, helper);
                string manifest = Path.Combine(directory, "cleanup.dat");
                string nativeManaged = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PaviseAuto", "Autostart");
                bool nativeManagedExisted = Directory.Exists(nativeManaged);
                using (var writer = new BinaryWriter(File.Open(manifest, FileMode.CreateNew)))
                {
                    writer.Write("Pavise uninstall 1"); writer.Write(int.Parse(identity[0])); writer.Write(long.Parse(identity[1]));
                    writer.Write(source); writer.Write(Path.GetFileName(Path.GetDirectoryName(StartupPayload.GetExpectedExecutable(source, Path.Combine(root, "Autostart")))));
                    writer.Write(nativeManaged); writer.Write(0);
                }
                using (Process finalizer = Spawn(helper, "--finish-uninstall " + Q(manifest)))
                { Check(finalizer.WaitForExit(10000) && finalizer.ExitCode == 0, "Production deletion failed"); }
                Check(!File.Exists(source), "Production helper left the fixture target");
                var watch = Stopwatch.StartNew();
                while (Directory.Exists(directory) && watch.ElapsedMilliseconds < 15000) Thread.Sleep(100);
                Check(!Directory.Exists(directory), "Production helper did not clean its temporary files: "
                    + (Directory.Exists(directory) ? string.Join(",", Directory.GetFileSystemEntries(directory)) : ""));
                Check(Directory.Exists(root), "Production helper deleted its target parent directory");
                Check(Directory.Exists(nativeManaged) == nativeManagedExisted, "Empty managed plan touched the real managed root");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
                Directory.Delete(root, true);
            }
        }
    }
}
#endif
