using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

namespace PaviseApp
{
    internal static class LaboratoryManualActionsChecks
    {
        private static int checks;
        private static void Check(bool ok, string why) { checks++; if (!ok) throw new InvalidOperationException(why); }
        private static void Reset()
        {
            LaboratoryManualActions.ResetForTest();
            Settings.UseTransientStoreForCurrentProcess();
            LaboratoryManualActions.InspectForTest = delegate { return new LaboratoryManualState { CanApply = true, Summary = "ready", Details = "inspection evidence" }; };
        }
        private static void Wait(WaitHandle signal, string why) { Check(signal.WaitOne(4000), why); }
        private static void RetiredActionsRejectApply()
        {
            Reset(); int writes = 0, inspections = 0;
            LaboratoryManualActions.ExecuteForTest = delegate { writes++; return ""; };
            LaboratoryManualActions.InspectForTest = delegate { inspections++; return new LaboratoryManualState { CanApply = true }; };
            foreach (string key in new[] { LaboratoryFileFilter.Key, LaboratoryClockRepair.Key, LaboratoryAudioEffects.Key })
                Check(!LaboratoryManualActions.Run(key, false, null, work => work(), null), "Retired experiment admitted new application");
            using (var done = new ManualResetEvent(false))
            {
                Check(LaboratoryManualActions.Refresh(() => done.Set()), "Recovery-state refresh failed");
                Wait(done, "Recovery-state refresh timed out");
            }
            Check(writes == 0 && inspections == 0 && LaboratoryManualActions.Candidates.Length == 0, "Retired empty state performed inspection or mutation");
            foreach (string key in new[] { LaboratoryFileFilter.Key, LaboratoryClockRepair.Key, LaboratoryAudioEffects.Key })
                Check(!LaboratoryManualActions.State(key).CanApply, "Retired state exposes application");
        }
        private static void AdmissionAndFailure()
        {
            Reset(); Settings.SaveStr(LaboratoryClockRepair.ReceiptKey, "controlled-original-record"); int executions = 0;
            LaboratoryManualActions.ExecuteForTest = delegate { Interlocked.Increment(ref executions); return ""; };
            using (var done = new ManualResetEvent(false))
            {
                Check(!LaboratoryManualActions.Run("unknown", false, null, work => work(), null), "Unknown action admitted");
                Check(LaboratoryManualActions.Run(LaboratoryClockRepair.Key, true, null, work => false, () => done.Set()), "Denied action was not queued");
                Wait(done, "Admission callback did not finish");
                Check(executions == 0, "Denied admission invoked a native action");
                Check(!string.IsNullOrEmpty(LaboratoryManualActions.State(LaboratoryClockRepair.Key).Failure), "Denied admission lost its explanation");
                done.Reset();
                Check(LaboratoryManualActions.Run(LaboratoryClockRepair.Key, true, null, work => true, () => done.Set()), "Broken admission case not queued");
                Wait(done, "Broken admission case did not finish");
                Check(executions == 0 && !string.IsNullOrEmpty(LaboratoryManualActions.State(LaboratoryClockRepair.Key).Failure), "Admission without execution showed success");
                done.Reset();
                LaboratoryManualActions.ExecuteForTest = delegate { executions++; return "readback rejected"; };
                Check(LaboratoryManualActions.Run(LaboratoryClockRepair.Key, true, null, work => work(), () => done.Set()), "Failure case not queued");
                Wait(done, "Failed operation did not complete");
                Check(LaboratoryManualActions.State(LaboratoryClockRepair.Key).Failure == "readback rejected", "Readback failure was hidden by inspection");
                done.Reset();
                LaboratoryManualActions.ExecuteForTest = delegate { throw new InvalidOperationException("native failure"); };
                Check(LaboratoryManualActions.Run(LaboratoryClockRepair.Key, true, null, work => work(), () => done.Set()), "Throwing case not queued");
                Wait(done, "Throwing operation left worker stuck");
                Check(!LaboratoryManualActions.Busy && LaboratoryManualActions.State(LaboratoryClockRepair.Key).Failure == "InvalidOperationException", "Exception left false success or busy state");
                done.Reset();
                LaboratoryManualActions.ExecuteForTest = delegate { return ""; };
                Check(LaboratoryManualActions.Run(LaboratoryClockRepair.Key, true, null, work => work(), () => done.Set()), "Recovery action not queued after failure");
                Wait(done, "Recovery case did not complete");
                var state = LaboratoryManualActions.State(LaboratoryClockRepair.Key);
                Check(state.Failure == "" && state.Summary == "ready", "Successful recheck retained stale failure");
                Check(state.Details == "inspection evidence", "Inspection evidence was lost in the UI cache copy");
                state.Summary = "tampered";
                Check(LaboratoryManualActions.State(LaboratoryClockRepair.Key).Summary == "ready", "UI state exposes mutable internal cache");
            }
        }
        private static void DrainAndDoNotRearm()
        {
            Reset(); int writes = 0, restores = 0;
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var done = new ManualResetEvent(false))
            {
                LaboratoryManualActions.ExecuteForTest = delegate { writes++; entered.Set(); release.WaitOne(); return ""; };
                LaboratoryManualActions.RuntimeRestoreForTest = delegate { restores++; return true; };
                Check(LaboratoryManualActions.Run(LaboratoryFileFilter.Key, true, null, work => work(), () => done.Set()), "Blocking action not queued");
                Wait(entered, "Blocking action did not start");
                Check(LaboratoryManualActions.Busy && LaboratoryManualActions.BusyKey == LaboratoryFileFilter.Key, "In-flight state not visible");
                Check(!LaboratoryManualActions.Run(LaboratoryAudioEffects.Key, false, null, work => work(), null), "Concurrent mutation admitted");
                Check(!LaboratoryManualActions.Refresh(null), "Concurrent inspection admitted");
                Check(!LaboratoryManualActions.CloseAndRestoreRuntime(10), "Close falsely succeeded before mutation drained");
                Check(restores == 0, "Restore raced an active mutation");
                release.Set(); Wait(done, "Released mutation did not drain");
                Check(LaboratoryManualActions.CloseAndRestoreRuntime(100), "Close could not restore after writer drained");
                Check(writes == 1 && restores == 1, "Unexpected writes during shutdown");
                Check(!LaboratoryManualActions.Run(LaboratoryFileFilter.Key, true, null, work => work(), null), "Closed manager rearmed mutation");
                Check(!LaboratoryManualActions.Refresh(null), "Closed manager rearmed native inspection");
            }
        }
        private static void ReceiptLifetime()
        {
            Reset();
            foreach (string key in new[] { LaboratoryFileFilter.ReceiptKey, LaboratoryClockRepair.ReceiptKey, LaboratoryAudioEffects.ReceiptKey })
                Check(RecoveryRecordKeys.Contains(key), "Reset does not protect receipt: " + key);
            Check(LaboratoryManualActions.RecoverStartup(), "Empty startup found recovery debt");
            Check(LaboratoryManualActions.CloseAndRestoreRuntime(100), "Empty shutdown found runtime debt");
            Reset(); Settings.SaveStr(LaboratoryClockRepair.ReceiptKey,
                new ClockRepairReceipt { Phase = "applied", EntryId = "{335a6788-f2af-4b5b-9d81-dedb38a02911}", Boot = "boot2:335a6788f2af4b5b9d81dedb38a02911:1", Attempted = 1,
                    Values = { new System.Collections.Generic.KeyValuePair<uint, ulong>(ClockRepairNative.UsePlatformClock, 1) } }.Encode());
            string raw = Settings.LoadStr(LaboratoryClockRepair.ReceiptKey, "");
            ClockRepairReceipt parsed;
            Check(ClockRepairReceipt.TryParse(raw, out parsed), "Applied receipt fixture is invalid");
            Check(!LaboratoryManualActions.RecoverStartup(), "Retired clock receipt was treated as recovered without restoration");
            Check(!LaboratoryManualActions.CloseAndRestoreRuntime(100), "Retired clock receipt was ignored during shutdown");
            Check(Settings.LoadStr(LaboratoryClockRepair.ReceiptKey, "") == raw, "Ordinary exit deleted persistent repair receipt");
            Check(!LaboratoryManualActions.RestoreAllForReset(), "Restore-all hid unresolved persistent repair");
            int runtime = 0, reset = 0, startup = 0;
            LaboratoryManualActions.RuntimeRestoreForTest = delegate { runtime++; return true; };
            LaboratoryManualActions.ResetRestoreForTest = delegate { reset++; return true; };
            LaboratoryManualActions.StartupRestoreForTest = delegate { startup++; return true; };
            Check(LaboratoryManualActions.RecoverStartup() && startup == 1, "Startup restoration was not routed");
            Check(LaboratoryManualActions.CloseAndRestoreRuntime(100) && runtime == 1 && reset == 0, "Ordinary exit restored persistent clock experiment");
            Check(LaboratoryManualActions.RestoreAllForReset() && reset == 1, "Restore-all omitted persistent experiment");
            Reset(); Settings.SaveStr(LaboratoryAudioEffects.ReceiptKey, "unresolved");
            Check(!LaboratoryManualActions.CloseAndRestoreRuntime(100), "Unresolved runtime receipt was discarded");
            Check(Settings.LoadStr(LaboratoryAudioEffects.ReceiptKey, "") == "unresolved", "Shutdown deleted unresolved receipt");
            Reset(); Settings.SaveStr(LaboratoryAudioEffects.ReceiptKey, "unresolved");
            LaboratoryManualActions.InspectForTest = delegate { throw new InvalidOperationException("query failure"); };
            using (var done = new ManualResetEvent(false))
            {
                Check(LaboratoryManualActions.Refresh(() => done.Set()), "Failing inspection not queued");
                Wait(done, "Failing inspection did not finish");
                Check(LaboratoryManualActions.State(LaboratoryAudioEffects.Key).CanRestore, "Inspection failure removed recovery entry point");
                Check(!LaboratoryManualActions.State(LaboratoryAudioEffects.Key).CanApply, "Inspection failure permitted new mutation");
            }
        }
        private static void GameAdmissionDoesNotBlockUi()
        {
            Reset();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var game = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            typeof(GameMode).GetField("sync", flags).SetValue(game, new object());
            typeof(GameMode).GetField("driverStageGate", flags).SetValue(game, new object());
            typeof(GameMode).GetField("timerMode", flags).SetValue(game, new TimerModeController());
            using (var kick = new AutoResetEvent(false))
            using (var entered = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var read = new ManualResetEvent(false))
            {
                typeof(GameMode).GetField("kick", flags).SetValue(game, kick);
                bool completed = false;
                var work = new Thread(() => { completed = game.RunLaboratoryManualWork(() => { entered.Set(); release.WaitOne(); return true; }); });
                work.IsBackground = true; work.Start(); Wait(entered, "Game admission did not start");
                var ui = new Thread(() => { bool active = game.IsActive; read.Set(); });
                ui.IsBackground = true; ui.Start();
                try { Check(read.WaitOne(500), "Manual native work holds UI IsActive lock"); }
                finally { release.Set(); work.Join(4000); ui.Join(4000); }
                Check(completed, "Outside-match work was rejected");
                typeof(GameMode).GetField("active", flags).SetValue(game, true);
                bool called = false;
                Check(!game.RunLaboratoryManualWork(() => { called = true; return true; }) && !called, "Mutation admitted during a match");
                typeof(GameMode).GetField("active", flags).SetValue(game, false);
                typeof(GameMode).GetField("stopping", flags).SetValue(game, true);
                Check(!game.RunLaboratoryManualWork(() => { called = true; return true; }) && !called, "Mutation admitted during shutdown");
            }
        }
        private static int Main()
        {
            try { Lang.Cur = 1; RetiredActionsRejectApply(); AdmissionAndFailure(); DrainAndDoNotRearm(); ReceiptLifetime(); GameAdmissionDoesNotBlockUi();
                Console.WriteLine("PASS manual laboratory assertions=" + checks + " native_writes=false"); return 0; }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
