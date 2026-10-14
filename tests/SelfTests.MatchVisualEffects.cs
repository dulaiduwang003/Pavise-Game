#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunMatchVisualEffectsTests()
        {
            Logger.WritesEnabled = false; Lang.Init();
            VisualEffectsLifecycle(); VisualEffectsRecovery(); VisualEffectsFailures();
            VisualEffectsRestoreRetry(); VisualEffectsInFlightExit(); VisualEffectsModePreference();
        }
        private sealed class VisualEffectsFixture : IDisposable
        {
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            internal readonly Dictionary<string, string> Original = new Dictionary<string, string>();
            internal readonly PerformanceActionCoordinator Coordinator = new PerformanceActionCoordinator();
            internal long Clock = DateTime.UtcNow.Ticks;
            internal int Writes;
            internal string ReadFailure, WriteFailure;
            internal bool PartialWrite;
            internal Action BeforeWrite;
            private readonly Func<string, string, string> oldRead = PerformanceActionBackend.ReadForTest;
            private readonly Func<string, string, string, bool> oldWrite = PerformanceActionBackend.WriteForTest;
            private readonly Func<int> oldDefender = PerformanceActionBackend.DefenderServiceForTest;
            internal VisualEffectsFixture()
            {
                Settings.UseTransientStoreForCurrentProcess();
                int i = 0;
                foreach (MatchVisualEffects.Effect effect in MatchVisualEffects.Effects)
                    Values[effect.Name] = Original[effect.Name] = i++ % 3 == 0 ? "0" : "1";
                Values["FontSmoothing"] = "1"; Values["ClearType"] = "2";
                Values["FontContrast"] = "1400";
                Coordinator.ClockForTest = delegate { return Clock; };
                PerformanceActionBackend.DefenderServiceForTest = delegate { return 0; };
                PerformanceActionBackend.ReadForTest = delegate(string id, string target) {
                    PerformanceEq(PerformanceActionCatalog.VisualEffects, id);
                    PerformanceEq(true, Original.ContainsKey(target));
                    if (target == ReadFailure) throw new IOException("Injected desktop preference read failure");
                    return Values[target];
                };
                PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value) {
                    PerformanceEq(PerformanceActionCatalog.VisualEffects, id);
                    PerformanceEq(true, Original.ContainsKey(target));
                    bool captured = false;
                    foreach (var receipt in PerformanceActionStore.All())
                        if (receipt.Id == id && receipt.Target == target) { captured = true; break; }
                    PerformanceEq(true, captured);
                    Writes++;
                    if (BeforeWrite != null) BeforeWrite();
                    if (target == WriteFailure) {
                        if (PartialWrite) Values[target] = value;
                        return false;
                    }
                    Values[target] = value; return true;
                };
            }
            internal void Queue(bool active)
            {
                Coordinator.Queue(false, false, new string[0], new string[0], null, active);
                PerformanceEq(true, Coordinator.DrainForTest(5000));
            }
            internal void Reduced()
            {
                foreach (string target in Original.Keys) PerformanceEq("0", Values[target]);
                Fonts();
            }
            internal void Restored()
            {
                foreach (string target in Original.Keys) PerformanceEq(Original[target], Values[target]);
                PerformanceEq(0, PerformanceActionStore.All().Count); Fonts();
            }
            internal void Fonts()
            {
                PerformanceEq("1", Values["FontSmoothing"]); PerformanceEq("2", Values["ClearType"]);
                PerformanceEq("1400", Values["FontContrast"]);
            }
            public void Dispose()
            {
                ReadFailure = WriteFailure = null; BeforeWrite = null;
                PerformanceEq(true, Coordinator.Close(5000));
                PerformanceActionBackend.ReadForTest = oldRead; PerformanceActionBackend.WriteForTest = oldWrite;
                PerformanceActionBackend.DefenderServiceForTest = oldDefender;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void VisualEffectsLifecycle()
        {
            using (var f = new VisualEffectsFixture())
            {
                PerformanceEq(false, MatchVisualEffects.Enabled);
                f.Queue(false); PerformanceEq(0, f.Writes); f.Restored();
                Settings.Save(PerformanceActionCatalog.VisualEffects, true);
                f.Queue(true); f.Reduced();
                var receipts = PerformanceActionStore.All();
                PerformanceEq(6, receipts.Count); PerformanceEq(6, f.Writes);
                foreach (var receipt in receipts) {
                    PerformanceEq("1", receipt.Original); PerformanceEq("0", receipt.Expected);
                    PerformanceEq(true, receipt.Committed);
                }
                f.Clock += TimeSpan.FromMinutes(2).Ticks;
                f.Queue(true); PerformanceEq(6, f.Writes);
                f.Queue(false); PerformanceEq(true, f.Coordinator.WaitForSessionRestore(100)); f.Restored();
                f.Values["MenuAnimation"] = f.Original["MenuAnimation"] = "0";
                f.Values["WindowAnimation"] = f.Original["WindowAnimation"] = "1";
                f.Queue(true); f.Reduced();
                Settings.Save(PerformanceActionCatalog.VisualEffects, false);
                f.Queue(false); f.Restored();
            }
        }
        private static void VisualEffectsRecovery()
        {
            using (var f = new VisualEffectsFixture())
            {
                f.Queue(true); f.Reduced();
                PerformanceEq(true, PerformanceTuning.RestoreSession()); f.Restored();
            }
            using (var f = new VisualEffectsFixture())
            {
                f.Queue(true);
                f.Values["ClientAnimation"] = "1";
                int writes = f.Writes;
                f.Queue(false); f.Restored();
                PerformanceEq(writes + 5, f.Writes);
            }
            using (var f = new VisualEffectsFixture())
            {
                f.Queue(true); PerformanceEq(true, f.Coordinator.Close(5000)); f.Restored();
            }
        }
        private static void VisualEffectsFailures()
        {
            using (var f = new VisualEffectsFixture())
            {
                Settings.SuspendWritesForReset();
                f.Queue(true); PerformanceEq(0, f.Writes); f.Restored();
                PerformanceEq("stopped", f.Coordinator.State(PerformanceActionCatalog.VisualEffects));
                Settings.UseTransientStoreForCurrentProcess();
            }
            using (var f = new VisualEffectsFixture())
            {
                f.ReadFailure = "ClientAnimation";
                f.Queue(true);
                PerformanceEq("1", f.Values["ClientAnimation"]);
                PerformanceEq(5, PerformanceActionStore.All().Count);
                PerformanceEq("stopped", f.Coordinator.State(PerformanceActionCatalog.VisualEffects));
                f.ReadFailure = null; f.Queue(false); f.Restored();
            }
            using (var f = new VisualEffectsFixture())
            {
                f.WriteFailure = "ClientAnimation"; f.PartialWrite = true;
                f.Queue(true); PerformanceEq("1", f.Values["ClientAnimation"]); PerformanceEq("restore-failed", f.Coordinator.State(PerformanceActionCatalog.VisualEffects));
                foreach (var receipt in PerformanceActionStore.All())
                    if (receipt.Target == f.WriteFailure) PerformanceEq(false, receipt.Committed);
                f.WriteFailure = null; f.Queue(false); f.Restored();
            }
        }
        private static void VisualEffectsRestoreRetry()
        {
            using (var f = new VisualEffectsFixture())
            {
                f.Queue(true); f.WriteFailure = "ClientAnimation";
                f.Queue(false);
                PerformanceEq(false, f.Coordinator.WaitForSessionRestore(100));
                PerformanceEq(1, PerformanceActionStore.All().Count);
                PerformanceEq("restore-failed", f.Coordinator.State(PerformanceActionCatalog.VisualEffects));
                int writes = f.Writes;
                f.Coordinator.Queue(false, false, new string[0], new[] { @"C:\Fixture\Browser.exe" });
                PerformanceEq(true, f.Coordinator.DrainForTest(5000)); PerformanceEq(writes, f.Writes);
                f.WriteFailure = null; f.Clock += TimeSpan.FromMinutes(1).Ticks;
                f.Queue(false); PerformanceEq(true, f.Coordinator.WaitForSessionRestore(100)); f.Restored();
            }
        }
        private static void VisualEffectsInFlightExit()
        {
            using (var f = new VisualEffectsFixture())
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            {
                f.BeforeWrite = delegate { entered.Set(); PerformanceEq(true, release.WaitOne(5000)); };
                f.Coordinator.Queue(false, false, new string[0], new string[0], null, true);
                PerformanceEq(true, entered.WaitOne(5000));
                f.Coordinator.Queue(false, false, new string[0], new string[0]);
                release.Set();
                PerformanceEq(true, f.Coordinator.Close(5000)); f.Restored();
            }
        }
        private static void VisualEffectsModePreference()
        {
            using (var modeFixture = new AutomaticFsoFixture())
            using (var f = new VisualEffectsFixture())
            {
                GameMode mode = modeFixture.Mode;
                var coordinator = (PerformanceActionCoordinator)typeof(GameMode).GetField("performanceActions", FsoHidden).GetValue(mode);
                Settings.Save(AutomaticPolicy.VersionKey, false);
                PerformanceEq(true, mode.SetVisualEffectsReduction(true));
                PerformanceEq(true, coordinator.DrainForTest(5000)); f.Restored();
                modeFixture.SetField("active", true);
                typeof(GameMode).GetMethod("SyncPerformanceAutomation", FsoHidden).Invoke(mode, new object[] { null });
                PerformanceEq(true, coordinator.DrainForTest(5000)); f.Reduced();
                PerformanceEq(true, mode.SetVisualEffectsReduction(false));
                PerformanceEq(true, coordinator.DrainForTest(5000)); f.Restored();
                PerformanceEq(true, mode.SetVisualEffectsReduction(true));
                PerformanceEq(true, coordinator.DrainForTest(5000)); f.Reduced();
                modeFixture.SetField("enabled", false);
                typeof(GameMode).GetMethod("SyncPerformanceAutomation", FsoHidden).Invoke(mode, new object[] { null });
                PerformanceEq(true, coordinator.DrainForTest(5000)); f.Restored();
                modeFixture.SetField("active", false);
                Settings.SuspendWritesForReset();
                PerformanceEq(false, mode.SetVisualEffectsReduction(false));
                PerformanceEq(true, MatchVisualEffects.Enabled);
                Settings.UseTransientStoreForCurrentProcess();
                PerformanceEq(true, coordinator.Close(5000));
            }
        }
    }
}
#endif
