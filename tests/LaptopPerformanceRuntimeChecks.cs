#if PAVISE_SELFTEST && PAVISE_LAPTOP_RUNTIME_CHECKS
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Laptop runtime checks cannot start the application"); }
    }

    // Exercises the production coordinator and environment ledger, with firmware
    // replaced at the existing backend boundary. No game loop or native plan runs.
    internal static class LaptopPerformanceRuntimeChecks
    {
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        private static string output;
        private static int Main(string[] args)
        {
            if (args.Length != 2) return 2;
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 30000, Timeout.Infinite))
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                switch (args[1])
                {
                    case "admission": Admission(); break;
                    case "apply": ApplyAndDisable(); break;
                    case "queued-cancel": QueuedCancellation(); break;
                    case "write-cancel": CancellationAfterWrite(); break;
                    case "external": ExternalSelectionWins(); break;
                    case "no-switch": NoSwitchRestores(); break;
                    case "session-release": SessionReleaseRestores(); break;
                    case "restore-order": RestoreOrdering(); break;
                    case "restore-debt": RestoreFailureKeepsDebt(); break;
                    case "shutdown-drain": ShutdownWaitsForWriter(); break;
                    default: return 2;
                }
                Console.WriteLine("PASS laptop-runtime suite=" + args[1] + " assertions=" + assertions
                    + " firmware=mocked settings=transient ac=mocked power_plan=native-blocked runtime_started=false windows_shown=false");
                return 0;
            }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                Console.Error.WriteLine(error); return 1;
            }
            finally { PowerPlan.LaboratoryRestoreForTest = null; LaptopPerfMode.ResetForTest(); }
        }
        private static void Check(bool value, string detail)
        { Interlocked.Increment(ref assertions); if (!value) throw new InvalidOperationException(detail); }
        private static object Call(GameMode mode, string method, params object[] arguments)
        {
            Type[] types = Array.ConvertAll(arguments, delegate(object value) { return value.GetType(); });
            MethodInfo target = typeof(GameMode).GetMethod(method, Hidden, null, types, null);
            Check(target != null, "Missing runtime method: " + method);
            return target.Invoke(mode, arguments);
        }
        private static void Set(GameMode mode, string field, object value)
        {
            FieldInfo target = typeof(GameMode).GetField(field, Hidden);
            Check(target != null, "Missing runtime field: " + field); target.SetValue(mode, value);
        }
        private static T Get<T>(GameMode mode, string field)
        { return (T)typeof(GameMode).GetField(field, Hidden).GetValue(mode); }
        private static bool Drain(GameMode mode, int ms)
        { return (bool)Call(mode, "DrainLaptopPerformanceWork", ms); }

        private sealed class Fixture : LaptopPerfMode.IBackend, IDisposable
        {
            internal readonly List<string> Trace = new List<string>();
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            internal readonly string DirectoryPath;
            internal readonly GameMode Mode;
            internal int Vendor, Current, Original, Writes, Reads, Generation = 7;
            internal bool Ac = true, FailRestore, HoldApply;
            internal Action AfterApply;
            internal int Target { get { return Vendor == 1 ? 3 : 1; } }
            internal string Receipt { get { return Settings.LoadStr(LaptopPerfMode.SnapKey, ""); } }
            internal Fixture(int vendor = 1)
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; LaptopPerfMode.ResetForTest();
                Vendor = vendor; Original = Current = vendor == 1 ? 2 : 0;
                LaptopPerfMode.BackendForTest = this; LaptopPerfMode.OnAcPowerForTest = delegate { return Ac; };
                // Failing the first existing plan hook prevents every native plan call.
                PowerPlan.LaboratoryRestoreForTest = delegate { Trace.Add("windows-plan"); return false; };
                DirectoryPath = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(DirectoryPath);
                Mode = new GameMode(DirectoryPath, new SuppressionCore());
                Mode.LaptopAcPowerForTest = delegate { return Ac; };
                Check(!Mode.LaptopPerformanceEnabled && !LaptopPerfMode.HasResidue, "New opt-in must be off without debt");
                Check(PowerPlan.SelectPlan(PowerPlan.ManagedChoice), "Cannot save transient managed choice");
                Settings.Save(LaptopPerfMode.PreferenceKey, true); Arm();
            }
            internal void Arm()
            {
                Set(Mode, "enabled", true); Set(Mode, "active", true); Set(Mode, "planActive", true);
                Set(Mode, "powerSessionEnding", false); Set(Mode, "stopping", false);
                Set(Mode, "powerSessionGen", Generation); Set(Mode, "laptopPerfPowerGeneration", Generation);
            }
            internal void Step() { Call(Mode, "RunLaptopPerformanceStep", Generation); }
            public bool Read(int vendor, out int mode)
            { Reads++; mode = Current; return vendor == Vendor; }
            public bool Write(int vendor, int mode)
            {
                Check(vendor == Vendor && (mode == Original || mode == Target), "Unknown vendor mode requested");
                Check(Receipt.Length > 0, "Firmware write preceded the original-mode receipt");
                Trace.Add("oem:" + mode); Writes++;
                if (mode == Target && HoldApply)
                { Entered.Set(); Check(Release.WaitOne(5000), "Mock firmware write was not released"); }
                if (mode == Original && FailRestore) return false;
                Current = mode;
                if (mode == Target && AfterApply != null) AfterApply();
                return true;
            }
            public void Dispose()
            {
                Release.Set(); Set(Mode, "stopping", true);
                Check(Drain(Mode, 5000), "Fixture left a firmware worker running");
                LaptopPerfMode.ResetForTest(); PowerPlan.LaboratoryRestoreForTest = null;
                Settings.UseTransientStoreForCurrentProcess(); Entered.Dispose(); Release.Dispose();
            }
        }

        private static void Admission()
        {
            bool[] normal = { true, true, true, true, false, false, false, true, false, true };
            for (int bit = -1; bit < normal.Length; bit++)
            {
                bool[] values = (bool[])normal.Clone(); if (bit >= 0) values[bit] = !values[bit];
                Check(GameMode.LaptopPerformanceAdmitted(values[0], values[1], values[2], values[3], values[4],
                    values[5], values[6], values[7], values[8], values[9]) == (bit == -1), "Admission gate lost bit " + bit);
            }
            using (var f = new Fixture())
            {
                f.Ac = false; f.Step(); Check(f.Writes == 0 && f.Receipt.Length == 0, "Battery mode applied firmware");
                f.Ac = true; Set(f.Mode, "laptopPerfPowerGeneration", -1); f.Step();
                Check(f.Writes == 0, "Unconfirmed Windows power plan admitted vendor mode");
            }
        }
        private static void ApplyAndDisable()
        {
            foreach (int vendor in new[] { 1, 2 }) using (var f = new Fixture(vendor))
            {
                f.Step(); Check(f.Current == f.Target && f.Writes == 1 && f.Receipt.Length > 0, "Eligible session did not apply owned vendor performance mode");
                f.Step(); Check(f.Writes == 1, "Steady mode rewrote firmware");
                Check(f.Mode.TrySetLaptopPerformanceEnabled(false), "Cannot disable saved opt-in"); f.Step();
                Check(f.Current == f.Original && f.Writes == 2 && f.Receipt.Length == 0, "Disabling did not restore owned vendor state");
                f.Step(); Check(f.Writes == 2 && !f.Mode.LaptopPerformanceEnabled, "Disabled mode reapplied firmware");
            }
        }
        private static void QueuedCancellation()
        {
            using (var f = new Fixture())
            {
                object gate = Get<object>(f.Mode, "powerApplyGate");
                Monitor.Enter(gate);
                try
                {
                    Call(f.Mode, "StepLaptopPerformancePolicy");
                    Check(Get<int>(f.Mode, "laptopPerfWorker") == 1, "Worker was not admitted while gate held");
                    f.Generation++; f.Arm();
                    Check(!Get<ManualResetEvent>(f.Mode, "laptopPerfIdle").WaitOne(30), "Queued writer was marked idle before executing");
                }
                finally { Monitor.Exit(gate); }
                Check(Get<ManualResetEvent>(f.Mode, "laptopPerfIdle").WaitOne(3000)
                    && Get<int>(f.Mode, "laptopPerfWorker") == 0, "Cancelled queued writer did not finish");
                Check(f.Writes == 0 && f.Receipt.Length == 0, "Old power generation wrote vendor mode");
                f.Step(); Check(f.Writes == 1 && f.Current == f.Target, "Current generation could not apply after stale work ended");
            }
        }
        private static void CancellationAfterWrite()
        {
            using (var f = new Fixture())
            {
                f.AfterApply = delegate { Set(f.Mode, "powerSessionGen", f.Generation + 1); };
                f.Step();
                Check(f.Current == f.Original && f.Writes == 2 && f.Receipt.Length == 0, "Generation change after apply was not rolled back");
                Check(f.Mode.LaptopPerformanceState != "applied", "Cancelled mode is shown as applied");
            }
        }
        private static void ExternalSelectionWins()
        {
            using (var f = new Fixture())
            {
                f.Step(); f.Current = 1; f.Step();
                Check(f.Writes == 1 && f.Current == 1 && f.Receipt.Length == 0, "External mode was overwritten or still owned");
                f.Current = f.Original; f.Step(); f.Step();
                Check(f.Writes == 1 && f.Current == f.Original, "Same session fought an external selection");
                Call(f.Mode, "BeginLaptopPerformanceSession"); f.Generation++; f.Arm(); f.Step();
                Check(f.Writes == 2 && f.Current == f.Target, "Next session did not release the external-change veto");
            }
        }
        private static void NoSwitchRestores()
        {
            using (var f = new Fixture())
            {
                f.Step(); Check(PowerPlan.SelectPlan(PowerPlan.NoSwitchChoice), "Cannot save no-switch choice"); f.Step();
                Check(f.Current == f.Original && f.Receipt.Length == 0 && f.Writes == 2, "No-switch did not restore owned firmware mode");
                f.Step(); Check(f.Writes == 2 && f.Mode.LaptopPerformanceEnabled, "No-switch reapplied firmware or erased saved user intent");
            }
        }
        private static void RestoreOrdering()
        {
            using (var f = new Fixture())
            {
                f.Step(); f.Trace.Clear(); Call(f.Mode, "RollbackPowerPlan", "fixture rollback");
                Check(f.Trace.Count == 2 && f.Trace[0] == "oem:" + f.Original && f.Trace[1] == "windows-plan",
                    "Windows plan restore ran before original vendor mode restoration");
                Check(f.Current == f.Original && f.Receipt.Length == 0, "Plan rollback lost vendor restoration");
            }
        }
        private static void SessionReleaseRestores()
        {
            for (int reason = 0; reason < 5; reason++) using (var f = new Fixture())
            {
                f.Step();
                switch (reason)
                {
                    case 0: Set(f.Mode, "enabled", false); break;
                    case 1: Set(f.Mode, "active", false); break;
                    case 2: Set(f.Mode, "gameGoneSinceTicks", DateTime.UtcNow.Ticks); break;
                    case 3: Set(f.Mode, "powerSessionEnding", true); break;
                    case 4: f.Ac = false; break;
                }
                f.Step(); Check(f.Current == f.Original && f.Receipt.Length == 0 && f.Writes == 2,
                    "Lost game/guard/AC admission did not restore: " + reason);
                f.Step(); Check(f.Writes == 2 && f.Mode.LaptopPerformanceEnabled,
                    "Ineligible session reapplied firmware or erased saved user intent: " + reason);
            }
        }
        private static void RestoreFailureKeepsDebt()
        {
            using (var f = new Fixture())
            {
                f.Step(); f.Trace.Clear(); f.FailRestore = true; Call(f.Mode, "RollbackPowerPlan", "fixture debt");
                Check(f.Trace.Count == 1 && f.Trace[0] == "oem:" + f.Original, "Plan release bypassed failed vendor recovery");
                Check(f.Receipt.Length > 0 && f.Current == f.Target && Get<bool>(f.Mode, "planActive"), "Failed recovery lost its receipt or plan ownership");
                Check(f.Mode.LaptopPerformanceState == "restore-failed", "Recovery debt is hidden from the UI state");
            }
        }
        private static void ShutdownWaitsForWriter()
        {
            using (var f = new Fixture())
            {
                f.HoldApply = true; Call(f.Mode, "StepLaptopPerformancePolicy");
                Check(f.Entered.WaitOne(3000), "Worker did not enter mock firmware write");
                Set(f.Mode, "stopping", true); Call(f.Mode, "InvalidatePowerPlanSession");
                Check(!Drain(f.Mode, 40) && f.Receipt.Length > 0, "Shutdown completed while its firmware writer was held");
                f.Release.Set(); Check(Drain(f.Mode, 3000), "Shutdown did not observe writer completion");
                Check(f.Current == f.Original && f.Writes == 2 && f.Receipt.Length == 0, "Admitted write did not restore after shutdown revoked intent");
                int writes = f.Writes; Call(f.Mode, "StepLaptopPerformancePolicy");
                Check(Drain(f.Mode, 1000) && f.Writes == writes, "Closing coordinator admitted a later firmware write");
            }
        }
    }
}
#endif
