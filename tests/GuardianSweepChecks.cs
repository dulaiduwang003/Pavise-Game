#if PAVISE_GUARDIAN_SWEEP_CHECKS
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    // The settled-guard sweep on a real local directory provisioned like the original store: guardian
    // leftovers one reader keeps open without delete sharing (as an anti-cheat scanner does), sessions
    // still owned by a ledger or lock, a directory holding an unknown entry, a directory that is not a
    // session, a junction named like a session and an unreleased controller. Nothing is spawned and no
    // file outside the temporary root is touched.
    internal static class GuardianSweepChecks
    {
        private const BindingFlags Hidden = BindingFlags.Static | BindingFlags.NonPublic;
        private static readonly string[] Leftovers = { "Pavise.HardThreadGuardian.exe.config", "Pavise.HardThreadGuardian.exe", "scope.xml" };
        private static int checks;
        private static void Check(bool value, string reason) { checks++; if (!value) throw new InvalidOperationException("FAIL " + reason); }
        private static string NewSession(string guards, params string[] names)
        {
            string directory = Path.Combine(guards, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            foreach (string name in names) File.WriteAllBytes(Path.Combine(directory, name), new byte[] { 1, 2, 3 });
            return directory;
        }
        private static int Count(string directory) { return Directory.Exists(directory) ? Directory.GetFileSystemEntries(directory).Length : -1; }

        [STAThread] private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--negative-control") { Console.WriteLine("FAIL: intentional guardian sweep negative control"); return 3; }
            string data = Path.Combine(Path.GetTempPath(), "Pavise-GuardianSweep-" + Guid.NewGuid().ToString("N"));
            string outside = data + "-outside";
            try {
                Type guardian = typeof(CriticalThreadGuardian);
                string root = Path.Combine(data, CriticalThreadGuardian.StoreDirectory), guards = Path.Combine(root, "guards");
                string sid = (string)guardian.GetMethod("UserSid", Hidden).Invoke(null, null);
                Directory.CreateDirectory(data);
                guardian.GetMethod("ProvisionRoot", Hidden).Invoke(null, new object[] { root, sid });
                Directory.CreateDirectory(guards);

                string held = NewSession(guards, Leftovers), emptied = NewSession(guards);
                string owned = NewSession(guards, Leftovers), locked = NewSession(guards, Leftovers);
                File.WriteAllBytes(Path.Combine(root, "hard-thread-" + Path.GetFileName(owned) + ".xml"), new byte[] { 1 });
                File.WriteAllBytes(Path.Combine(root, "hard-thread-" + Path.GetFileName(locked) + ".lock"), new byte[0]);
                string unknown = NewSession(guards, "Pavise.HardThreadGuardian.exe", "notes.txt");
                string named = Path.Combine(guards, "not-a-session");
                Directory.CreateDirectory(named); File.WriteAllBytes(Path.Combine(named, "scope.xml"), new byte[] { 1 });

                // An unreleased controller owns the root: nothing is touched.
                Type parent = guardian.GetNestedType("ParentGuardian", BindingFlags.NonPublic);
                object controller = Activator.CreateInstance(parent, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { root }, null);
                guardian.GetMethod("RegisterController", Hidden).Invoke(null, new[] { controller });
                Check(!CriticalThreadGuardian.SweepSettledGuards(data) && CriticalThreadGuardian.SettledLeftoversPending, "a sweep ran while an original controller owned the root");
                Check(Directory.Exists(emptied) && Count(held) == 3, "a sweep touched leftovers under an unreleased controller");
                guardian.GetMethod("RemoveController", Hidden).Invoke(null, new[] { controller });

                using (new FileStream(Path.Combine(held, "Pavise.HardThreadGuardian.exe"), FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    Check(!CriticalThreadGuardian.SweepSettledGuards(data) && CriticalThreadGuardian.SettledLeftoversPending, "a sweep blocked by a foreign reader reported clean");
                    Check(File.Exists(Path.Combine(held, "Pavise.HardThreadGuardian.exe")), "an image open without delete sharing disappeared");
                    Check(!Directory.Exists(emptied), "an emptied settled session directory remained");
                }
                Check(Count(owned) == 3 && Count(locked) == 3, "a session still owned by its ledger or lock was swept");
                Check(Count(unknown) == 2 && File.Exists(Path.Combine(named, "scope.xml")), "an unknown entry or a non-session directory was swept");

                File.Delete(Path.Combine(unknown, "notes.txt"));
                Check(CriticalThreadGuardian.SweepSettledGuards(data) && !CriticalThreadGuardian.SettledLeftoversPending, "released leftovers were not swept");
                Check(!Directory.Exists(held) && !Directory.Exists(unknown), "settled leftovers remained after their reader closed");
                Check(Count(owned) == 3 && Count(locked) == 3 && Directory.Exists(named), "a clean sweep removed something it does not own");

                // A junction named like a session is refused and its target stays intact.
                Directory.CreateDirectory(outside);
                File.WriteAllBytes(Path.Combine(outside, "scope.xml"), new byte[] { 1 });
                string junction = Path.Combine(guards, Guid.NewGuid().ToString("N"));
                var link = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + junction + "\" \"" + outside + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (Process process = Process.Start(link)) { process.StandardOutput.ReadToEnd(); process.WaitForExit(); }
                Check(Directory.Exists(junction) && (File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0, "the junction fixture was not created");
                Check(!CriticalThreadGuardian.SweepSettledGuards(data) && File.Exists(Path.Combine(outside, "scope.xml")), "a junction session was followed or reported clean");
                Directory.Delete(junction);
                Check(CriticalThreadGuardian.SweepSettledGuards(data), "the sweep did not recover after the junction was removed");

                Console.WriteLine("PASS guardian sweep checks=" + checks + " native=true spawned=false");
                return 0;
            } catch (Exception error) {
                Console.WriteLine(error is TargetInvocationException && error.InnerException != null ? error.InnerException.ToString() : error.ToString());
                return 1;
            } finally {
                try { Directory.Delete(data, true); } catch { }
                try { Directory.Delete(outside, true); } catch { }
            }
        }
    }
}
#endif
