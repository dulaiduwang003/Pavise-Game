#if PAVISE_SELFTEST && PAVISE_SELFTEST_RUNNER
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static class CoreSchedulingIntegrationRunner
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--child") { Console.ReadLine(); return 0; }
            if (args.Length != 1 || args[0] != "--integration") return 2;
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Lang.Init();
                SelfTests.RunCoreSchedulingIntegration();
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }

    internal static partial class SelfTests
    {
        internal static void RunCoreSchedulingIntegration()
        {
            if (CpuTopology.MultiGroup) throw new InvalidOperationException("This integration fixture requires a single processor group.");
            string root = Path.Combine(Path.GetTempPath(), "PaviseCoreIntegration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); Logger.LogPath = Path.Combine(root, "integration.log");
            using (var f = new FamilyPolicyFixture(root, "manual-affinity"))
            using (var child = Process.Start(new ProcessStartInfo
            {
                FileName = Assembly.GetExecutingAssembly().Location, Arguments = "--child",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
            }))
            {
                try
                {
                    IntPtr h = child.Handle;
                    ulong original = Native.QueryAffinity(h), target = 0;
                    for (int i = 0; i < 64 && CpuTopology.CountSetBits(target) < 2; i++)
                        if ((original & (1UL << i)) != 0) target |= 1UL << i;
                    if (CpuTopology.CountSetBits(original) < 4) throw new InvalidOperationException("Fixture needs at least four logical CPUs.");
                    long creation, cpu; ulong disk;
                    Eq(true, Native.QueryProcessSample(h, out creation, out cpu, out disk));
                    Eq(true, f.Mode.ProbeManualPlacement(h, child.Id, creation, original, target));
                    Eq(target, Native.QueryAffinity(h));
                    Eq(true, f.Mode.ProbeRestoreManualPlacement(child.Id, false));
                    Eq(target, Native.QueryAffinity(h)); // stopping IRQ observation must not unpin the manual game
                    Eq(true, f.Mode.ProbeRestoreManualPlacement(child.Id, true));
                    Eq(original, Native.QueryAffinity(h));
                    Console.WriteLine("PASS production placement + retained-handle restoration: original=" + original.ToString("X") + " manual=" + target.ToString("X"));

                    Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)target));
                    Eq(false, f.Mode.ProbeManualPlacement(h, child.Id, creation, target, original));
                    Eq(target, Native.QueryAffinity(h)); // never widen a process's pre-existing restriction
                    Eq(true, Native.SetProcessAffinityMask(h, (UIntPtr)original));
                    Eq(original, Native.QueryAffinity(h));
                    Console.WriteLine("PASS pre-existing affinity restriction is preserved on refused placement");
                    Console.WriteLine("ISOLATION fixture_child_only=true system_reservation_written=false normal_app_started=false");
                }
                finally
                {
                    try { child.StandardInput.WriteLine("exit"); } catch { }
                    if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(3000); }
                }
            }
        }
    }
}
#endif
