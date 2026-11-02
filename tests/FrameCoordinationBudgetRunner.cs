#if PAVISE_SELFTEST && PAVISE_FRAME_BUDGET
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static class FrameCoordinationBudgetRunner
    {
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--identity-child") return 0;
            if (args.Length != 1) return 2;
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 45000, Timeout.Infinite))
            {
                try
                {
                    string output = Path.GetFullPath(args[0]);
                    Directory.CreateDirectory(output);
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest(); Logger.WritesEnabled = false;
                    Logger.LogPath = Path.Combine(output, "isolated.log"); Lang.Init();
                    SelfTests.RunFrameCoordinationTests();
                    CheckExitedProcessIdentity();
                    Console.WriteLine("RESULT assertions=" + SelfTests.FrameBudgetAssertions
                        + " settings=transient logs=temp application_started=false real_etw_started=false system_writes=false");
                    Console.WriteLine("SCOPE deterministic mock regressions; not a real Last Epoch capture or performance measurement");
                    return 0;
                }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
                finally { Logger.WritesEnabled = false; Settings.UseTransientStoreForCurrentProcess(); }
            }
        }
        private static void CheckExitedProcessIdentity()
        {
            string runner = typeof(FrameCoordinationBudgetRunner).Assembly.Location;
            var info = new ProcessStartInfo(runner, "--identity-child") {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            using (Process child = Process.Start(info))
            {
                try
                {
                    IntPtr handle = child.Handle;
                    long birth = child.StartTime.ToFileTimeUtc();
                    if (!child.WaitForExit(5000)) throw new InvalidOperationException("Identity fixture did not exit");
                    if (Native.QueryProcessIdentityState(handle, birth) != 1)
                        throw new InvalidOperationException("Exited process with a held handle was mistaken for a live identity");
                    Console.WriteLine("PASS owned exited-process identity: held handle rejected; native read only");
                }
                finally { if (!child.HasExited) child.Kill(); }
            }
        }
    }
    internal static partial class SelfTests
    {
        internal static int FrameBudgetAssertions;
        private static void Eq<T>(T expected, T actual)
        {
            FrameBudgetAssertions++;
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Frame budget checks cannot start the application runtime"); }
    }
}
#endif
