// File purpose Legacy update, prefetch and delivery optimization service regression
// Service operations, ledger access and bandwidth changes are all injected
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int sessionServiceChecks;

        internal static int RunServicePauserRegressionTests()
        {
            string output = Path.Combine(Path.GetTempPath(), "PaviseServicePauser-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(output);
            string previousLog = Logger.LogPath;
            int previousLanguage = Lang.Cur;
            Action[] tests =
            {
                SessionServicesNormalRoundTrip,
                SessionServicesOnlyRunningAndPartialSuccess,
                SessionServicesPreparedJournalMustVerify,
                SessionServicesOwnedWriteFailureKeepsRamDebt,
                SessionServicesStopRejectionNeverOwnsExternalStop,
                SessionServicesAcceptedStopWaitsForObservation,
                SessionServicesObservationWriteRetries,
                SessionServicesPreparedCrashIsUnowned,
                SessionServicesOwnedRecoveryAcrossReload,
                SessionServicesRestoreIntentMustVerify,
                SessionServicesDispatchedStartCannotRepeat,
                SessionServicesSettledCleanupCannotRepeat,
                SessionServicesRestoreRespectsExternalState,
                SessionServicesMalformedLedgerFailsClosed,
                SessionServicesUnreadableLedgerNeverCompletes,
                SessionServicesReentrantCallsDoNotDuplicate,
                SessionServicesRecoverBeforeAnotherPause,
                SessionServicesDoNormalAndPartialSuccess,
                SessionServicesDoFailedRecoveryRemainsVisible,
                SessionServicesWrapperOutcomesAndLogs
            };
            sessionServiceChecks = 0;
            try
            {
                foreach (Action test in tests)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    DoTweak.ResetForTest();
                    SessionServiceWrapper(typeof(UpdatePause)).ResetForTest();
                    SessionServiceWrapper(typeof(SvcPause)).ResetForTest();
                    Logger.ResetWriteBarrierForTest();
                    Logger.LogPath = Path.Combine(output, "decisions.log");
                    Logger.Clear();
                    Lang.Cur = 0;
                    test();
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                Console.WriteLine("PASS session-services assertions=" + sessionServiceChecks
                    + " scm=mocked registry=mocked settings=transient application_started=false");
                return tests.Length;
            }
            finally
            {
                DoTweak.ResetForTest();
                SessionServiceWrapper(typeof(UpdatePause)).ResetForTest();
                SessionServiceWrapper(typeof(SvcPause)).ResetForTest();
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest();
                Logger.LogPath = previousLog; Lang.Cur = previousLanguage;
                string full = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (full.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(full).StartsWith("PaviseServicePauser-", StringComparison.Ordinal)
                    && Directory.Exists(full)) Directory.Delete(full, true);
            }
        }

        private static void SessionServiceCheck(bool value, string message)
        {
            if (!value) throw new InvalidOperationException("Session service regression: " + message);
            sessionServiceChecks++;
        }

        private static ServicePauser SessionServiceWrapper(Type type)
        {
            return (ServicePauser)type.GetField("pauser", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        }

        private sealed class SessionServiceFake
        {
            internal readonly string[] Names;
            internal readonly Dictionary<string, int> States = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> DeniedStops = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Stops = new List<string>(), Starts = new List<string>(), Writes = new List<string>();
            internal readonly List<string> Operations = new List<string>();
            internal ServicePauser Engine;
            internal string Ledger = "", LastRead;
            internal int Queries, Reads;
            internal int StopState = 1, StartState = 4;
            internal bool DenyRead, DenyWrite, LoseWrites, DenyStart, RejectStopChangesState;
            internal bool ThrowRead, ThrowWrite, ThrowStop, ThrowStart;
            internal Func<string, bool> RejectWrite;
            internal Action<string> AfterWrite, BeforeStop, AfterStop, BeforeStart, AfterStart, AfterQuery;
            internal List<string> LastStopped, LastConfirmed, Remaining;
            internal bool LedgerLost;

            internal SessionServiceFake(params string[] names)
            {
                Names = names;
                foreach (string name in names) States[name] = 4;
                Reload();
            }

            internal void Reload()
            {
                Bind(new ServicePauser(Names, "ServicePauserIsolated"));
            }

            internal void Bind(ServicePauser engine)
            {
                Engine = engine;
                engine.QueryForTest = Query;
                engine.StopForTest = Stop;
                engine.StartForTest = Start;
                engine.ReadForTest = Read;
                engine.WriteForTest = Write;
            }

            internal bool Activate()
            {
                return Engine.Activate(out LastStopped, out LastConfirmed, out LedgerLost);
            }

            internal bool Restore() { return Engine.Restore(out Remaining); }

            internal bool HasPhase(string name, string phase)
            {
                return Ledger.IndexOf("\n" + phase + "\t" + name + "\t", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            private bool Read(out string value)
            {
                Reads++;
                value = null;
                if (ThrowRead) throw new IOException("mock service ledger read");
                if (DenyRead) return false;
                value = Ledger; LastRead = value;
                return true;
            }

            private bool Write(string value)
            {
                Writes.Add(value);
                if (ThrowWrite) throw new IOException("mock service ledger write");
                if (DenyWrite || (RejectWrite != null && RejectWrite(value))) return false;
                if (!LoseWrites) Ledger = value;
                if (AfterWrite != null) AfterWrite(value);
                return true;
            }

            private int Query(string name)
            {
                Queries++;
                int state;
                if (!States.TryGetValue(name, out state)) state = 0;
                if (AfterQuery != null) AfterQuery(name);
                return state;
            }

            private bool Stop(string name, out bool confirmed)
            {
                confirmed = false;
                SessionServiceCheck(LastRead == Ledger && HasPhase(name, "P"),
                    "STOP was not preceded by a verified prepared journal");
                if (BeforeStop != null) BeforeStop(name);
                Stops.Add(name); Operations.Add("stop:" + name);
                if (ThrowStop) throw new IOException("mock unconfirmed STOP");
                if (DeniedStops.Contains(name) || States[name] == 1)
                {
                    if (RejectStopChangesState) States[name] = 1;
                    return false;
                }
                States[name] = StopState;
                confirmed = StopState == 1;
                if (AfterStop != null) AfterStop(name);
                return true;
            }

            private bool Start(string name)
            {
                SessionServiceCheck(LastRead == Ledger && HasPhase(name, "R"),
                    "START was not preceded by a verified restore journal");
                if (BeforeStart != null) BeforeStart(name);
                Starts.Add(name); Operations.Add("start:" + name);
                if (ThrowStart) throw new IOException("mock uncertain START");
                if (DenyStart) return false;
                States[name] = StartState;
                if (AfterStart != null) AfterStart(name);
                return StartState == 4;
            }
        }

        private static void SessionServicesNormalRoundTrip()
        {
            var f = new SessionServiceFake("wuauserv", "UsoSvc");
            SessionServiceCheck(f.Activate(), "normal activation failed");
            SessionServiceCheck(f.Stops.Count == 2 && f.LastStopped.Count == 2 && f.LastConfirmed.Count == 2,
                "normal activation did not report both accepted and observed stops");
            SessionServiceCheck(f.HasPhase("wuauserv", "O") && f.HasPhase("UsoSvc", "O") && f.Engine.HasResidue,
                "accepted stops were not journaled");
            int writes = f.Writes.Count;
            SessionServiceCheck(f.Activate() && f.Stops.Count == 2 && f.Writes.Count == writes,
                "duplicate activation stopped or re-journaled stable services");
            SessionServiceCheck(f.Restore() && f.Starts.Count == 2 && f.Remaining.Count == 0,
                "normal restoration did not restore both services");
            SessionServiceCheck(f.Ledger == "" && !f.Engine.HasResidue && f.States["wuauserv"] == 4 && f.States["UsoSvc"] == 4,
                "normal restoration left journal or stopped service");
            int queries = f.Queries;
            SessionServiceCheck(f.Restore() && f.Starts.Count == 2 && f.Queries == queries,
                "duplicate restoration touched services");
        }

        private static void SessionServicesOnlyRunningAndPartialSuccess()
        {
            {
                var f = new SessionServiceFake("SysMain", "WSearch");
                f.Bind(new ServicePauser(f.Names, "ServicePauserIsolated"));
                f.States["SysMain"] = 1;
                SessionServiceCheck(f.Activate() && f.Stops.Count == 1 && f.Stops[0] == "WSearch",
                    "already-stopped service was touched");
                SessionServiceCheck(f.Restore() && f.Starts.Count == 1 && f.Starts[0] == "WSearch" && f.States["SysMain"] == 1,
                    "already-stopped service was incorrectly restarted");
            }
            foreach (int state in new[] { 0, 1, 2, 3, 5, 6, 7 })
            {
                var f = new SessionServiceFake("wuauserv"); f.States["wuauserv"] = state;
                SessionServiceCheck(f.Activate() && f.Stops.Count == 0 && f.Ledger == "",
                    "non-running or unknown initial state was claimed: " + state);
                SessionServiceCheck(f.Restore() && f.Starts.Count == 0, "skipped service was restarted: " + state);
            }
            var partial = new SessionServiceFake("wuauserv", "UsoSvc");
            partial.DeniedStops.Add("UsoSvc");
            SessionServiceCheck(partial.Activate() && partial.LastStopped.Count == 1 && !partial.HasPhase("UsoSvc", "O"),
                "partial success claimed a rejected STOP");
            SessionServiceCheck(partial.Restore() && partial.Starts.Count == 1 && partial.Starts[0] == "wuauserv",
                "partial success did not restore exactly the accepted stop");

            var recovery = new SessionServiceFake("wuauserv", "UsoSvc");
            SessionServiceCheck(recovery.Activate(), "partial recovery fixture failed");
            recovery.BeforeStart = delegate(string name) { recovery.DenyStart = name == "wuauserv"; };
            SessionServiceCheck(!recovery.Restore() && recovery.Starts.Count == 2 && recovery.States["UsoSvc"] == 4
                && recovery.Remaining.Count == 1 && recovery.Remaining[0] == "wuauserv",
                "one failed service prevented recovery of the other service or erased its debt");
            recovery.States["wuauserv"] = 4;
            SessionServiceCheck(recovery.Restore() && recovery.Starts.Count == 2,
                "partial recovery repeated a completed service START");
        }

        private static void SessionServicesPreparedJournalMustVerify()
        {
            for (int failure = 0; failure < 4; failure++)
            {
                var f = new SessionServiceFake("wuauserv");
                if (failure == 0) f.DenyWrite = true;
                if (failure == 1) f.LoseWrites = true;
                if (failure == 2) f.ThrowWrite = true;
                if (failure == 3) f.AfterWrite = delegate { f.DenyRead = true; };
                SessionServiceCheck(!f.Activate() && f.LedgerLost && f.Stops.Count == 0,
                    "STOP escaped failed prepare persistence/readback: " + failure);
                SessionServiceCheck(f.Starts.Count == 0, "failed prepare caused an unnecessary START");
                f.DenyWrite = f.LoseWrites = f.ThrowWrite = f.DenyRead = false; f.AfterWrite = null;
                SessionServiceCheck(f.Restore() && f.Starts.Count == 0 && !f.Engine.HasResidue,
                    "undispatched preparation could not settle without touching the service");
            }
            foreach (int state in new[] { 0, 1, 2, 3, 5, 6, 7 })
            {
                var f = new SessionServiceFake("wuauserv");
                f.AfterWrite = delegate(string text)
                {
                    if (text.IndexOf("\nP\t", StringComparison.Ordinal) >= 0) f.States["wuauserv"] = state;
                };
                SessionServiceCheck(f.Activate() && f.Stops.Count == 0 && f.States["wuauserv"] == state,
                    "service changed during slow prepare persistence but STOP was still issued: " + state);
                SessionServiceCheck(f.Restore() && f.Starts.Count == 0 && !f.Engine.HasResidue,
                    "undispatched stale preparation became service ownership");
            }
        }

        private static void SessionServicesOwnedWriteFailureKeepsRamDebt()
        {
            var f = new SessionServiceFake("wuauserv");
            f.RejectWrite = delegate(string text) { return text.IndexOf("\nO\t", StringComparison.Ordinal) >= 0
                || text.IndexOf("\nR\t", StringComparison.Ordinal) >= 0; };
            SessionServiceCheck(!f.Activate() && f.LedgerLost && f.Stops.Count == 1 && f.Starts.Count == 0,
                "owned-record failure did not keep rollback behind the restore-journal gate");
            SessionServiceCheck(f.Engine.HasResidue && f.HasPhase("wuauserv", "P") && !f.Restore(),
                "failed rollback lost its in-memory ownership");
            f.RejectWrite = null;
            SessionServiceCheck(f.Restore() && f.Starts.Count == 1 && !f.Engine.HasResidue,
                "verified same-process STOP receipt did not recover after persistence recovered");

            var rollback = new SessionServiceFake("wuauserv");
            rollback.RejectWrite = delegate(string text) { return text.IndexOf("\nO\t", StringComparison.Ordinal) >= 0; };
            SessionServiceCheck(!rollback.Activate() && rollback.LedgerLost && rollback.Starts.Count == 1
                && !rollback.Engine.HasResidue, "owned-save failure did not perform a fully journaled rollback");

            var lostRead = new SessionServiceFake("wuauserv");
            lostRead.AfterStop = delegate { lostRead.DenyRead = true; };
            SessionServiceCheck(!lostRead.Activate() && lostRead.Engine.HasResidue && !lostRead.Restore()
                && lostRead.Starts.Count == 0, "unreadable ledger after STOP was reported clean");
            lostRead.DenyRead = false; lostRead.AfterStop = null;
            SessionServiceCheck(lostRead.Restore() && lostRead.Starts.Count == 1,
                "acknowledged STOP was forgotten after ledger read access recovered");
        }

        private static void SessionServicesStopRejectionNeverOwnsExternalStop()
        {
            var f = new SessionServiceFake("wuauserv");
            f.DeniedStops.Add("wuauserv"); f.RejectStopChangesState = true;
            SessionServiceCheck(f.Activate() && f.LastStopped.Count == 0 && f.LastConfirmed.Count == 0,
                "failed STOP was converted to accepted STOP based on the current state");
            SessionServiceCheck(f.HasPhase("wuauserv", "P") && !f.Restore() && f.Starts.Count == 0,
                "external stop after a failed request was restarted");
            f.Reload();
            SessionServiceCheck(!f.Restore() && f.Starts.Count == 0, "prepared external stop acquired ownership after reload");

            var uncertain = new SessionServiceFake("wuauserv");
            uncertain.BeforeStop = delegate { uncertain.States["wuauserv"] = 1; };
            uncertain.ThrowStop = true;
            SessionServiceCheck(uncertain.Activate() && uncertain.LastStopped.Count == 0 && !uncertain.Restore()
                && uncertain.Starts.Count == 0, "exceptional STOP inferred ownership from a stopped state");
        }

        private static void SessionServicesAcceptedStopWaitsForObservation()
        {
            var f = new SessionServiceFake("wuauserv"); f.StopState = 4;
            SessionServiceCheck(f.Activate() && f.LastStopped.Count == 1 && f.LastConfirmed.Count == 0,
                "accepted but pending STOP was not reported distinctly");
            SessionServiceCheck(!f.Restore() && f.Engine.HasResidue && f.Starts.Count == 0,
                "still-running accepted STOP was dropped before it completed");
            f.States["wuauserv"] = 3;
            SessionServiceCheck(!f.Restore() && f.Starts.Count == 0, "STOP_PENDING was prematurely restored");
            f.States["wuauserv"] = 1;
            SessionServiceCheck(f.Restore() && f.Starts.Count == 1, "eventually stopped service was not restored");

            var observed = new SessionServiceFake("wuauserv"); observed.StopState = 3;
            SessionServiceCheck(observed.Activate(), "pending fixture failed");
            observed.States["wuauserv"] = 1;
            SessionServiceCheck(observed.Activate() && observed.Ledger.IndexOf("\t1", StringComparison.Ordinal) >= 0,
                "later stopped observation was not persisted");
            observed.States["wuauserv"] = 4;
            SessionServiceCheck(observed.Restore() && observed.Starts.Count == 0 && !observed.Engine.HasResidue,
                "externally restarted observed stop was taken over");

            // STOP accepted but the service stays Running, the first check keeps the debt
            //   the reading may run ahead of the STOP_PENDING transition
            //   the second independent check finds the expected end state reached and settles, the pause feature no longer hangs on a service restarted by a trigger
            var lost = new SessionServiceFake("wuauserv"); lost.StopState = 4;
            SessionServiceCheck(lost.Activate() && lost.LastStopped.Count == 1, "lost STOP fixture failed");
            SessionServiceCheck(!lost.Restore() && lost.Engine.HasResidue && lost.Starts.Count == 0,
                "first still-running check settled an accepted STOP too early");
            SessionServiceCheck(lost.Restore() && lost.Starts.Count == 0 && lost.Ledger == ""
                && !lost.Engine.HasResidue,
                "second consecutive running check did not settle the lost STOP");
        }

        private static void SessionServicesObservationWriteRetries()
        {
            var f = new SessionServiceFake("wuauserv"); f.StopState = 3;
            SessionServiceCheck(f.Activate(), "pending observation fixture failed");
            f.States["wuauserv"] = 1;
            f.RejectWrite = delegate(string text) { return text.IndexOf("\nO\twuauserv\t1", StringComparison.Ordinal) >= 0; };
            SessionServiceCheck(!f.Activate() && f.Ledger.IndexOf("\t0", StringComparison.Ordinal) >= 0,
                "failed observation persistence was not reported");
            int writes = f.Writes.Count; f.RejectWrite = null;
            SessionServiceCheck(f.Activate() && f.Writes.Count > writes && f.Ledger.IndexOf("\t1", StringComparison.Ordinal) >= 0,
                "dirty stopped observation was not retried without another state change");
            f.States["wuauserv"] = 4; f.Reload();
            SessionServiceCheck(f.Restore() && f.Starts.Count == 0, "persisted stop observation did not survive reload");
        }

        private static void SessionServicesPreparedCrashIsUnowned()
        {
            // Old-format ledger keeps the legacy restore-means-restart contract, stopped services get started and settled
            {
                var legacy = new SessionServiceFake("wuauserv"); legacy.Ledger = "wuauserv"; legacy.States["wuauserv"] = 1;
                SessionServiceCheck(legacy.Restore() && legacy.Starts.Count == 1 && legacy.States["wuauserv"] == 4
                    && legacy.Ledger == "" && !legacy.Engine.HasResidue,
                    "legacy stopped service was not restarted under the originating contract");
            }
            {
                var legacy = new SessionServiceFake("wuauserv"); legacy.Ledger = "wuauserv"; legacy.States["wuauserv"] = 4;
                SessionServiceCheck(legacy.Restore() && legacy.Starts.Count == 0 && legacy.Ledger == "",
                    "already-running legacy state could not be safely settled");
            }
            // New-format P record is written before STOP is issued, cannot prove this app stopped it, stays fail-closed
            {
                var f = new SessionServiceFake("wuauserv"); f.Ledger = "2\nP\twuauserv\t0"; f.States["wuauserv"] = 1;
                SessionServiceCheck(!f.Restore() && f.Starts.Count == 0 && f.Engine.HasResidue,
                    "unowned prepared receipt restarted a user-stopped service");
                SessionServiceCheck(!f.Activate() && f.Stops.Count == 0, "pending prepared receipt allowed a new pause");
                f.States["wuauserv"] = 4;
                SessionServiceCheck(f.Restore() && f.Starts.Count == 0 && f.Ledger == "",
                    "already-running prepared state could not be safely settled");
            }
            var capture = new SessionServiceFake("wuauserv");
            capture.BeforeStop = delegate { throw new IOException("crash before native STOP"); };
            SessionServiceCheck(capture.Activate() && capture.Stops.Count == 0,
                "pre-STOP crash fixture accidentally dispatched STOP");
            // Keep the real prepared bytes read back before the callback
            string prepared = null;
            foreach (string write in capture.Writes)
                if (write.StartsWith("2\nP\t", StringComparison.Ordinal)) { prepared = write; break; }
            SessionServiceCheck(prepared != null, "prepared crash fixture did not capture a real journal");
            capture.Ledger = prepared; capture.States["wuauserv"] = 1; capture.BeforeStop = null; capture.Reload();
            SessionServiceCheck(!capture.Restore() && capture.Starts.Count == 0,
                "replayed verified preparation became owned without STOP acknowledgement");

            var oldDo = new SessionServiceFake("DoSvc"); oldDo.Ledger = "1"; oldDo.States["DoSvc"] = 1;
            SessionServiceCheck(oldDo.Restore() && oldDo.Starts.Count == 1 && oldDo.Ledger == "",
                "old DoSvc marker did not honor the originating restart contract");
        }

        private static void SessionServicesOwnedRecoveryAcrossReload()
        {
            var f = new SessionServiceFake("wuauserv", "UsoSvc");
            SessionServiceCheck(f.Activate(), "owned reload fixture failed");
            f.Reload();
            SessionServiceCheck(f.Restore() && f.Starts.Count == 2 && f.Ledger == "",
                "confirmed ownership could not restore after process reload");

            var pending = new SessionServiceFake("wuauserv"); pending.StopState = 4;
            SessionServiceCheck(pending.Activate(), "accepted reload fixture failed");
            pending.Reload();
            SessionServiceCheck(!pending.Restore() && pending.Engine.HasResidue, "accepted STOP was forgotten during reload");
            pending.States["wuauserv"] = 1;
            SessionServiceCheck(pending.Restore() && pending.Starts.Count == 1, "accepted STOP did not recover once stopped");
        }

        private static void SessionServicesRestoreIntentMustVerify()
        {
            var f = new SessionServiceFake("wuauserv");
            SessionServiceCheck(f.Activate(), "restore intent fixture failed");
            f.RejectWrite = delegate(string text) { return text.IndexOf("\nR\t", StringComparison.Ordinal) >= 0; };
            SessionServiceCheck(!f.Restore() && f.Starts.Count == 0 && f.Engine.HasResidue,
                "START escaped a denied restore-intent write");
            f.RejectWrite = null;
            SessionServiceCheck(f.Restore() && f.Starts.Count == 1, "known undispatched restore was not retryable");

            var lost = new SessionServiceFake("wuauserv");
            SessionServiceCheck(lost.Activate(), "restore readback fixture failed");
            lost.LoseWrites = true;
            SessionServiceCheck(!lost.Restore() && lost.Starts.Count == 0, "START escaped failed restore-intent readback");
            lost.LoseWrites = false;
            SessionServiceCheck(lost.Restore() && lost.Starts.Count == 1, "same-process no-dispatch proof was lost");

            var reload = new SessionServiceFake("wuauserv");
            SessionServiceCheck(reload.Activate(), "R reload fixture failed");
            reload.AfterWrite = delegate(string text) { if (text.IndexOf("\nR\t", StringComparison.Ordinal) >= 0) reload.DenyRead = true; };
            SessionServiceCheck(!reload.Restore() && reload.Starts.Count == 0 && reload.HasPhase("wuauserv", "R"),
                "R preparation fixture did not remain unissued");
            reload.AfterWrite = null; reload.DenyRead = false; reload.Reload();
            SessionServiceCheck(!reload.Restore() && reload.Starts.Count == 0,
                "new process guessed that a restoring receipt had not dispatched START");
        }

        private static void SessionServicesDispatchedStartCannotRepeat()
        {
            for (int failure = 0; failure < 3; failure++)
            {
                var f = new SessionServiceFake("wuauserv");
                SessionServiceCheck(f.Activate(), "ambiguous START fixture failed");
                if (failure == 0) f.DenyStart = true;
                if (failure == 1) f.ThrowStart = true;
                if (failure == 2) f.StartState = 2;
                SessionServiceCheck(!f.Restore() && f.Starts.Count == 1 && f.HasPhase("wuauserv", "R"),
                    "unconfirmed START did not preserve restore intent");
                f.States["wuauserv"] = 1; f.DenyStart = f.ThrowStart = false; f.StartState = 4;
                SessionServiceCheck(!f.Restore() && f.Starts.Count == 1, "unconfirmed dispatched START was repeated in-process");
                f.Reload();
                SessionServiceCheck(!f.Restore() && f.Starts.Count == 1, "unconfirmed dispatched START was repeated after reload");
                f.States["wuauserv"] = 4;
                SessionServiceCheck(f.Restore() && f.Starts.Count == 1, "later Running confirmation could not settle R");
            }
            var late = new SessionServiceFake("wuauserv"); SessionServiceCheck(late.Activate(), "late START exception fixture failed");
            late.AfterStart = delegate { throw new IOException("START completed then adapter failed"); };
            SessionServiceCheck(late.Restore() && late.Starts.Count == 1 && !late.Engine.HasResidue,
                "verified Running after an exception was reported as a remaining native debt");
        }

        private static void SessionServicesSettledCleanupCannotRepeat()
        {
            var f = new SessionServiceFake("wuauserv"); SessionServiceCheck(f.Activate(), "settled fixture failed");
            f.RejectWrite = delegate(string text) { return text.Length == 0; };
            SessionServiceCheck(!f.Restore() && f.Starts.Count == 1 && f.HasPhase("wuauserv", "S"),
                "verified restore did not retain a settled tombstone before failed clear");
            f.States["wuauserv"] = 1;
            SessionServiceCheck(!f.Restore() && f.Starts.Count == 1, "cleanup failure repeated START in-process");
            f.RejectWrite = null; f.Reload();
            int queries = f.Queries;
            SessionServiceCheck(f.Restore() && f.Starts.Count == 1 && f.Queries == queries,
                "settled reload touched a user-stopped service");

            var ram = new SessionServiceFake("wuauserv"); SessionServiceCheck(ram.Activate(), "RAM settled fixture failed");
            ram.States["wuauserv"] = 4;
            ram.RejectWrite = delegate(string text) { return text.IndexOf("\nS\t", StringComparison.Ordinal) >= 0; };
            SessionServiceCheck(!ram.Restore() && ram.Starts.Count == 0, "lost-ownership settlement did not report failed persistence");
            ram.States["wuauserv"] = 1; ram.RejectWrite = null;
            SessionServiceCheck(ram.Restore() && ram.Starts.Count == 0, "RAM settled proof revived after an external stop");
        }

        private static void SessionServicesRestoreRespectsExternalState()
        {
            foreach (int state in new[] { 2, 3, 4, 5, 6, 7 })
            {
                var f = new SessionServiceFake("wuauserv"); SessionServiceCheck(f.Activate(), "external-state fixture failed");
                f.States["wuauserv"] = state;
                SessionServiceCheck(f.Restore() && f.Starts.Count == 0,
                    "external post-stop transition was overwritten: " + state);
                f.States["wuauserv"] = 1;
                SessionServiceCheck(f.Restore() && f.Starts.Count == 0, "settled external transition was reclaimed");
            }
            var unknown = new SessionServiceFake("wuauserv"); SessionServiceCheck(unknown.Activate(), "unknown-state fixture failed");
            unknown.States["wuauserv"] = 0;
            SessionServiceCheck(!unknown.Restore() && unknown.Starts.Count == 0 && unknown.Engine.HasResidue,
                "unknown current state allowed START or erased debt");
            unknown.States["wuauserv"] = 1;
            SessionServiceCheck(unknown.Restore() && unknown.Starts.Count == 1, "query recovery lost confirmed ownership");

            var changed = new SessionServiceFake("wuauserv"); SessionServiceCheck(changed.Activate(), "fresh-query fixture failed");
            changed.AfterWrite = delegate(string text) { if (text.IndexOf("\nR\t", StringComparison.Ordinal) >= 0) changed.States["wuauserv"] = 7; };
            SessionServiceCheck(changed.Restore() && changed.Starts.Count == 0,
                "external state change during restore-journal persistence was not respected");
        }

        private static void SessionServicesMalformedLedgerFailsClosed()
        {
            foreach (string raw in new[] { "foreign", "wuauserv|wuauserv", "wuauserv|UsoSvc", "2\nP\twuauserv\t1",
                "2\nO\tforeign\t1", "2\nZ\twuauserv\t1", "2\nO\twuauserv\t2",
                "2\nO\twuauserv\t1\nO\twuauserv\t1", "2\n", new string('x', 4097) })
            {
                var f = new SessionServiceFake("wuauserv"); f.Ledger = raw;
                SessionServiceCheck(!f.Activate() && !f.Restore() && f.Engine.HasResidue,
                    "malformed or foreign ledger was treated as an empty/usable journal");
                SessionServiceCheck(f.Queries == 0 && f.Stops.Count == 0 && f.Starts.Count == 0 && f.Ledger == raw,
                    "invalid ledger caused a native action or destructive clear");
            }
            var unsupported = new ServicePauser(new[] { "wuauserv" }, "ServicePauserNoNative");
            List<string> stopped, confirmed, remaining; bool lost;
            SessionServiceCheck(unsupported.Activate(out stopped, out confirmed, out lost) && stopped.Count == 0,
                "uninjected self-test controller should perform no native action");
            SessionServiceCheck(Settings.SaveStr("ServicePauserNoNative", "wuauserv"), "mock default journal failed");
            SessionServiceCheck(!unsupported.Restore(out remaining) && unsupported.HasResidue,
                "uninjected service controller falsely confirmed native restoration");
        }

        private static void SessionServicesUnreadableLedgerNeverCompletes()
        {
            foreach (bool throws in new[] { false, true })
            {
                var f = new SessionServiceFake("wuauserv"); f.DenyRead = !throws; f.ThrowRead = throws;
                SessionServiceCheck(!f.Activate() && !f.Restore() && f.Engine.HasResidue && f.Engine.HadLedger,
                    "unreadable empty-looking ledger was treated as successfully restored");
                SessionServiceCheck(f.Queries == 0 && f.Stops.Count == 0 && f.Starts.Count == 0 && f.Writes.Count == 0,
                    "unreadable ledger allowed a service operation");
            }
            var pending = new SessionServiceFake("wuauserv"); SessionServiceCheck(pending.Activate(), "strict-read fixture failed");
            pending.DenyRead = true;
            SessionServiceCheck(!pending.Restore() && pending.Engine.HasResidue && pending.Starts.Count == 0,
                "RAM debt disappeared when the persisted journal became unreadable");
            pending.DenyRead = false;
            SessionServiceCheck(pending.Restore() && pending.Starts.Count == 1, "restored journal access did not permit safe recovery");
        }

        private static void SessionServicesReentrantCallsDoNotDuplicate()
        {
            var f = new SessionServiceFake("wuauserv");
            f.BeforeStop = delegate
            {
                List<string> stopped, confirmed, remain; bool lost;
                SessionServiceCheck(!f.Engine.Activate(out stopped, out confirmed, out lost)
                    && !f.Engine.Restore(out remain), "reentrant STOP callback entered service mutation");
            };
            f.BeforeStart = delegate
            {
                List<string> stopped, confirmed, remain; bool lost;
                SessionServiceCheck(!f.Engine.Restore(out remain)
                    && !f.Engine.Activate(out stopped, out confirmed, out lost), "reentrant START callback entered service mutation");
            };
            SessionServiceCheck(f.Activate() && f.Restore() && f.Stops.Count == 1 && f.Starts.Count == 1,
                "reentrant callbacks duplicated a service operation");
            var slowRead = new SessionServiceFake("wuauserv");
            bool once = true;
            slowRead.AfterQuery = delegate
            {
                if (!once) return; once = false;
                List<string> remain;
                SessionServiceCheck(!slowRead.Engine.Restore(out remain), "query callback bypassed mutation serialization");
            };
            SessionServiceCheck(slowRead.Activate() && slowRead.Restore(), "guarded query callback broke normal service flow");
        }

        private static void SessionServicesRecoverBeforeAnotherPause()
        {
            var f = new SessionServiceFake("wuauserv"); SessionServiceCheck(f.Activate(), "recovery-before-pause fixture failed");
            f.Reload(); f.DenyStart = true;
            SessionServiceCheck(!f.Activate() && f.Stops.Count == 1 && f.Starts.Count == 1,
                "new session paused services while earlier recovery was unresolved");
            f.DenyStart = false; f.States["wuauserv"] = 4;
            SessionServiceCheck(f.Activate() && f.Stops.Count == 2 && f.Starts.Count == 1,
                "settled prior restoration prevented the next legitimate pause");
            SessionServiceCheck(f.Restore() && f.Starts.Count == 2, "new session did not retain its own receipt");
        }

        private static void SessionServicesDoNormalAndPartialSuccess()
        {
            var f = new SessionServiceFake("DoSvc"); f.Bind(DoTweak.PauserForTest);
            bool backup = false; int apply = 0, restore = 0;
            DoTweak.DomainJoinedForTest = delegate { return false; };
            DoTweak.BandwidthHasBackupForTest = delegate { return backup; };
            DoTweak.BandwidthApplyForTest = delegate { apply++; backup = true; return true; };
            DoTweak.BandwidthRestoreForTest = delegate { restore++; backup = false; return true; };
            SessionServiceCheck(DoTweak.Activate() && DoTweak.HasResidue && f.Stops.Count == 1 && apply == 1,
                "DO no longer applied its bandwidth and service portions");
            SessionServiceCheck(DoTweak.Activate() && f.Stops.Count == 1 && apply == 1, "duplicate DO activation repeated work");
            SessionServiceCheck(DoTweak.Restore() && f.Starts.Count == 1 && restore == 1 && !DoTweak.HasResidue,
                "DO normal restoration failed");
            SessionServiceCheck(DoTweak.Restore() && f.Starts.Count == 1 && restore == 1, "duplicate DO restoration repeated work");

            DoTweak.ResetForTest(); f = new SessionServiceFake("DoSvc"); f.States["DoSvc"] = 1; f.Bind(DoTweak.PauserForTest);
            backup = false; apply = 0;
            DoTweak.DomainJoinedForTest = delegate { return false; };
            DoTweak.BandwidthHasBackupForTest = delegate { return backup; };
            DoTweak.BandwidthApplyForTest = delegate { apply++; backup = true; return true; };
            DoTweak.BandwidthRestoreForTest = delegate { backup = false; return true; };
            SessionServiceCheck(DoTweak.Activate() && f.Stops.Count == 0 && apply == 1,
                "already-stopped DoSvc prevented the independent bandwidth portion");
            SessionServiceCheck(DoTweak.Restore() && f.Starts.Count == 0 && f.States["DoSvc"] == 1,
                "DO restarted a service that was stopped before activation");

            DoTweak.ResetForTest(); f = new SessionServiceFake("DoSvc"); f.Bind(DoTweak.PauserForTest);
            DoTweak.DomainJoinedForTest = delegate { return true; };
            DoTweak.BandwidthHasBackupForTest = delegate { return false; };
            DoTweak.BandwidthApplyForTest = delegate { throw new InvalidOperationException("domain policy must not be touched"); };
            SessionServiceCheck(DoTweak.Activate() && f.Stops.Count == 1 && DoTweak.Restore() && f.Starts.Count == 1,
                "domain skip changed the independent DoSvc behavior");

            DoTweak.ResetForTest(); f = new SessionServiceFake("DoSvc"); f.Bind(DoTweak.PauserForTest);
            backup = false;
            DoTweak.DomainJoinedForTest = delegate { return false; };
            DoTweak.BandwidthHasBackupForTest = delegate { return backup; };
            DoTweak.BandwidthApplyForTest = delegate { backup = true; return true; };
            DoTweak.BandwidthRestoreForTest = delegate { backup = false; return true; };
            f.RejectWrite = delegate(string text) { return text.IndexOf("\nO\t", StringComparison.Ordinal) >= 0; };
            Logger.Clear();
            SessionServiceCheck(DoTweak.Activate() && f.Starts.Count == 1 && f.States["DoSvc"] == 4 && backup,
                "DO service rollback discarded its independent successful bandwidth portion");
            SessionServiceCheck(Logger.Tail(20).IndexOf(Lang.T("log.dotweak.5"), StringComparison.Ordinal) < 0,
                "DO logged that its already-rolled-back service was still stopped");
            SessionServiceCheck(DoTweak.Restore(), "DO partial rollback could not restore remaining bandwidth debt");
        }

        private static void SessionServicesDoFailedRecoveryRemainsVisible()
        {
            var f = new SessionServiceFake("DoSvc"); f.Bind(DoTweak.PauserForTest);
            DoTweak.DomainJoinedForTest = delegate { return true; };
            DoTweak.BandwidthHasBackupForTest = delegate { return false; };
            f.RejectWrite = delegate(string text) { return text.IndexOf("\nO\t", StringComparison.Ordinal) >= 0
                || text.IndexOf("\nR\t", StringComparison.Ordinal) >= 0; };
            SessionServiceCheck(!DoTweak.Activate() && f.Stops.Count == 1 && f.Starts.Count == 0 && DoTweak.HasResidue,
                "DO failed ownership/rollback persistence lost its service debt");
            SessionServiceCheck(!DoTweak.Restore() && DoTweak.HasResidue, "DO reported failed service restoration as success");
            f.RejectWrite = null;
            SessionServiceCheck(DoTweak.Restore() && f.Starts.Count == 1 && !DoTweak.HasResidue,
                "DO did not recover its own accepted stop after ledger access recovered");

            DoTweak.ResetForTest(); f = new SessionServiceFake("DoSvc"); f.Bind(DoTweak.PauserForTest);
            DoTweak.BandwidthHasBackupForTest = delegate { return false; };
            f.Ledger = "1"; f.States["DoSvc"] = 1;
            DoTweak.HealFromCrash();
            // The legacy 1 marker follows the legacy contract, crash self-heal restarts DoSvc directly and settles the ledger
            SessionServiceCheck(f.Starts.Count == 1 && !DoTweak.HasResidue && DoTweak.Restore(),
                "DO crash recovery did not honor the legacy restart contract");
            DoTweak.ResetForTest(); f = new SessionServiceFake("DoSvc"); f.Bind(DoTweak.PauserForTest);
            DoTweak.BandwidthHasBackupForTest = delegate { return false; };
            f.Ledger = "1"; f.States["DoSvc"] = 4; DoTweak.HealFromCrash();
            SessionServiceCheck(!DoTweak.HasResidue && f.Starts.Count == 0, "already-running old DoSvc marker could not settle");
            f.DenyRead = true;
            SessionServiceCheck(!DoTweak.Restore() && DoTweak.HasResidue,
                "DO treated a failed service journal read as empty");

            DoTweak.ResetForTest(); f = new SessionServiceFake("DoSvc"); f.Bind(DoTweak.PauserForTest);
            bool backup = true;
            DoTweak.BandwidthHasBackupForTest = delegate { return backup; };
            DoTweak.BandwidthRestoreForTest = delegate { return false; };
            SessionServiceCheck(!DoTweak.Restore() && DoTweak.HasResidue,
                "DO ignored failed bandwidth recovery while the service journal was empty");
            backup = false;
            SessionServiceCheck(DoTweak.Restore(), "cleared bandwidth debt did not settle DO");

            DoTweak.ResetForTest(); f = new SessionServiceFake("DoSvc"); f.Bind(DoTweak.PauserForTest);
            backup = true;
            DoTweak.BandwidthHasBackupForTest = delegate { return backup; };
            DoTweak.BandwidthRestoreForTest = delegate { backup = false; return true; };
            // New-format P record stays fail-closed, it carries the service-side-unresolved partial recovery scenario
            f.Ledger = "2\nP\tDoSvc\t0"; f.States["DoSvc"] = 1;
            Logger.Clear();
            SessionServiceCheck(!DoTweak.Restore() && DoTweak.HasResidue && !backup && f.Starts.Count == 0,
                "DO treated partial bandwidth recovery as complete service restoration");
            SessionServiceCheck(Logger.Tail(20).IndexOf(Lang.T("log.dotweak.8"), StringComparison.Ordinal) < 0,
                "DO logged complete restoration while its service journal remained unresolved");
        }

        private static void SessionServicesWrapperOutcomesAndLogs()
        {
            foreach (bool update in new[] { true, false })
            {
                Type type = update ? typeof(UpdatePause) : typeof(SvcPause);
                var f = update ? new SessionServiceFake("wuauserv", "UsoSvc") : new SessionServiceFake("SysMain", "WSearch");
                f.Bind(SessionServiceWrapper(type));
                Func<bool> restore = update ? new Func<bool>(UpdatePause.Restore) : new Func<bool>(SvcPause.Restore);
                Func<bool> activate = update ? new Func<bool>(UpdatePause.Activate) : new Func<bool>(SvcPause.Activate);
                string success = Lang.T(update ? "log.updatepause.3" : "log.svcpause.4");
                string failure = Lang.T(update ? "log.updatepause.4" : "log.svcpause.5");
                f.DenyRead = true; Logger.Clear();
                SessionServiceCheck(!restore(), "service wrapper converted strict read failure to success");
                string log = Logger.Tail(20);
                SessionServiceCheck(log.IndexOf(success, StringComparison.Ordinal) < 0 && log.IndexOf(failure, StringComparison.Ordinal) >= 0
                    && log.IndexOf(Lang.T("t.versionmigrations.2"), StringComparison.Ordinal) >= 0,
                    "wrapper did not report an unreadable/unknown journal: " + type.Name + " " + log);
                f.DenyRead = false; Logger.Clear();
                SessionServiceCheck(activate() && restore(), "wrapper normal pause/restoration regressed");
                SessionServiceCheck(Logger.Tail(20).IndexOf(success, StringComparison.Ordinal) >= 0,
                    "wrapper did not log verified successful restoration");
                f.Engine.ResetForTest();
            }
        }
    }
}
#endif
