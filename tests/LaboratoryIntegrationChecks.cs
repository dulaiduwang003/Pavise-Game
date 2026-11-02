#if PAVISE_SELFTEST && PAVISE_LAB_INTEGRATION
using System;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Runtime is disabled in lab integration checks"); } }
    internal static class LaboratoryIntegrationChecks
    {
        private static int checks;
        private static void Check(bool value, string detail) { checks++; if (!value) throw new Exception(detail); }
        private static LaboratorySelection On() { return new LaboratorySelection { Power = true }; }
        private static void DefaultsAndReset()
        {
            Settings.UseTransientStoreForCurrentProcess();
            var off = LaboratorySelection.Read(true, 1, 2);
            Check(!off.Any && off.Idle == "default", "Fresh settings enabled a laboratory action");
            Settings.Save("GmGpuPowerMax", true); Settings.Save("GmAudioLowLatV1", true);
            Check(!LaboratorySelection.Read(true, 1, 2).Any, "Retired preferences silently reactivated experiments");
            Settings.Save(LaboratoryGpu.PowerKey, true); Settings.SaveStr(LaboratoryPower.IdleKey, "disabled");
            Check(!LaboratorySelection.Read(false, 1, 2).Any, "Out-of-session settings admitted experiments");
            var active = LaboratorySelection.Read(true, 7, 9);
            Check(active.Power && active.Idle == "disabled" && active.RendererPid == 7 && active.RendererCreation == 9, "Selection lost explicit intent or process identity");
            Check(LaboratorySelection.CanonicalIdle("extreme") == "default", "Unknown strategy enabled an aggressive mode");
            foreach (string key in new[] { LaboratoryPower.EppKey, LaboratoryPower.IdleKey, LaboratoryGpu.PowerKey, LaboratoryGpu.ClockKey, LaboratoryAudio.Key })
                Check(AppDataReset.IsUserPreference(key) && !RecoveryRecordKeys.Contains(key), "Preference cannot be reset independently: " + key);
            foreach (string key in new[] { LaboratoryPower.ReceiptKey, LaboratoryGpu.PowerReceipt, LaboratoryGpu.ClockReceipt })
                Check(RecoveryRecordKeys.Contains(key) && !AppDataReset.IsUserPreference(key), "Recovery debt may be deleted as a preference: " + key);
        }
        private static void DefaultOff()
        {
            int applies = 0, restores = 0;
            var session = new LaboratorySession(new object());
            session.ApplyForTest = value => Interlocked.Increment(ref applies);
            session.RestoreForTest = delegate { Interlocked.Increment(ref restores); return true; };
            session.Queue(new LaboratorySelection());
            Check(session.IsRestored && session.WaitForRestore(30) && session.Close(100), "Default-off lifecycle did not remain clean");
            Check(applies == 0 && restores == 0, "Default-off lifecycle touched an action backend");
        }
        private static void DrainInFlight()
        {
            var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
            int applies = 0, restores = 0;
            var session = new LaboratorySession(new object());
            session.ApplyForTest = delegate { Interlocked.Increment(ref applies); entered.Set(); release.WaitOne(); };
            session.RestoreForTest = delegate { Interlocked.Increment(ref restores); return true; };
            session.Queue(On()); Check(entered.WaitOne(3000), "Worker did not start");
            Check(!session.Close(25), "Close declared in-flight writer drained");
            session.Queue(On()); // Closing cannot accept a new game-loop publication.
            release.Set(); Check(session.Close(3000), "Writer did not finish and restore");
            Check(applies == 1 && restores == 1 && session.IsRestored, "A closing worker reapplied settings after recovery");
        }
        private static void RecoveryFailure()
        {
            var entered = new ManualResetEvent(false); int restores = 0;
            var session = new LaboratorySession(new object());
            session.ApplyForTest = delegate { entered.Set(); };
            session.RestoreForTest = delegate { Interlocked.Increment(ref restores); return false; };
            session.Queue(On()); Check(entered.WaitOne(3000), "Apply was not reached");
            Check(!session.WaitForRestore(100), "Failed restoration was reported as clean");
            Check(!session.Close(3000) && restores >= 1 && !session.IsRestored, "Closing discarded unresolved recovery debt");
        }
        private static void RestoreOnly()
        {
            int applies = 0, restores = 0, allowRestore = 0;
            var session = new LaboratorySession(new object(), true);
            session.ApplyForTest = delegate { Interlocked.Increment(ref applies); };
            session.RestoreForTest = delegate { Interlocked.Increment(ref restores); return Volatile.Read(ref allowRestore) == 1; };
            Check(!session.IsRestored, "Startup recovery debt was hidden by default-off settings");
            Check(!session.WaitForRestore(100), "First failed startup recovery was treated as settled");
            Volatile.Write(ref allowRestore, 1);
            Check(session.Close(3000) && session.IsRestored && applies == 0 && restores >= 2,
                "Restore-only worker could not retry debt without enabling experiments");
        }
        private static void ReenableDuringRestore()
        {
            var firstApply = new ManualResetEvent(false); var restoring = new ManualResetEvent(false);
            var releaseRestore = new ManualResetEvent(false); var secondApply = new ManualResetEvent(false);
            var releaseApply = new ManualResetEvent(false); int applies = 0, restores = 0, newIntent = 0;
            var session = new LaboratorySession(new object());
            session.ApplyForTest = delegate {
                Interlocked.Increment(ref applies);
                if (Volatile.Read(ref newIntent) == 0) firstApply.Set();
                else { secondApply.Set(); releaseApply.WaitOne(); }
            };
            session.RestoreForTest = delegate {
                if (Interlocked.Increment(ref restores) == 1) { restoring.Set(); releaseRestore.WaitOne(); }
                return true;
            };
            session.Queue(On()); Check(firstApply.WaitOne(3000), "First apply was not reached");
            session.Queue(new LaboratorySelection()); Check(restoring.WaitOne(3000), "Restore was not reached");
            session.Queue(On()); Volatile.Write(ref newIntent, 1); releaseRestore.Set(); Check(secondApply.WaitOne(3000), "Re-enabled intent was lost during restore");
            Check(!session.IsRestored && !session.WaitForRestore(50), "Previous restore incorrectly settled a new in-flight apply");
            releaseApply.Set(); Check(session.Close(3000) && restores >= 2, "New apply was not restored before closing");
        }
        private static int Main()
        {
            try { Logger.WritesEnabled = false; DefaultsAndReset(); DefaultOff(); DrainInFlight(); RecoveryFailure(); RestoreOnly(); ReenableDuringRestore();
                Console.WriteLine("PASS laboratory integration assertions=" + checks + " settings=transient actions=mocked application_started=false"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
