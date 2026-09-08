// 文件用途 非必要服务回归 下面每一个 SCM 操作和台账访问都是注入的
// 不起应用运行时 不碰在用设置 不碰服务控制器 不跑 UI
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int optionalServiceChecks;

        internal static int RunOptionalServicePauseRegressionTests()
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseOptionalServices-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string previousLog = Logger.LogPath;
            Func<List<string>> previousRestore = LegacyPurge.RestoreHook;
            Func<bool> previousRegistry = LegacyPurge.DeleteRegistryHook;
            bool previousSkip = LegacyPurge.SkipRegistryDelete;
            Action<string>[] tests =
            {
                OptionalServicesAllowlistAndDefaults,
                OptionalServicesEligibility,
                OptionalServicesPrintingAndDependents,
                OptionalServicesOwnedRoundTrip,
                OptionalServicesQueryAndBootFailure,
                OptionalServicesRejectedStopIsNotOwned,
                OptionalServicesJournalBeforeStop,
                OptionalServicesReceiptWriteRollback,
                OptionalServicesPendingReceiptSurvives,
                OptionalServicesAcceptedStopMustBeObserved,
                OptionalServicesObserveStopsDoesNotEnforce,
                OptionalServicesObserveStopsRetriesJournal,
                OptionalServicesPreparedCrashIsUnowned,
                OptionalServicesMalformedJournal,
                OptionalServicesRestoreRespectsCurrentState,
                OptionalServicesStartOwnershipChangeIsFinal,
                OptionalServicesRejectedStartRelinquishesChangedState,
                OptionalServicesStartNeedsConfirmation,
                OptionalServicesRestoreIntentBeforeStart,
                OptionalServicesRestoreFailureKeepsDebt,
                OptionalServicesCleanupFailureDoesNotRestartTwice,
                OptionalServicesSettledOwnershipCannotRevive,
                OptionalServicesRestoreIntentSurvivesCrash,
                OptionalServicesBootChangeDropsOldOwnership,
                OptionalServicesRecoverBeforeNewSession,
                OptionalServicesCancellationBoundaries,
                OptionalServicesSerializedRestore,
                OptionalServicesStalePolicyAdmission,
                OptionalServicesCancelledActivationHasNoActiveBit,
                OptionalServicesLiveOverrideFuse,
                OptionalServicesSessionPolicyAndDebt,
                OptionalServicesResetKeepsFailedRecovery
            };
            optionalServiceChecks = 0;
            try
            {
                foreach (Action<string> test in tests)
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest();
                    Logger.LogPath = Path.Combine(root, "decisions.log");
                    Lang.Cur = 0;
                    LegacyPurge.SkipRegistryDelete = false;
                    LegacyPurge.RestoreHook = delegate { throw new InvalidOperationException("Unmocked service-test restoration"); };
                    LegacyPurge.DeleteRegistryHook = delegate { throw new InvalidOperationException("Unmocked service-test registry deletion"); };
                    test(root);
                    Console.WriteLine("PASS " + test.Method.Name);
                }
                Console.WriteLine("PASS optional-services assertions=" + optionalServiceChecks
                    + " scm=mocked settings=transient application_started=false");
                return tests.Length;
            }
            finally
            {
                LegacyPurge.RestoreHook = previousRestore;
                LegacyPurge.DeleteRegistryHook = previousRegistry;
                LegacyPurge.SkipRegistryDelete = previousSkip;
                Logger.ResetWriteBarrierForTest(); Logger.LogPath = previousLog;
                Settings.UseTransientStoreForCurrentProcess();
                string actual = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
                string temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
                if (actual.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(actual).StartsWith("PaviseOptionalServices-", StringComparison.Ordinal)
                    && Directory.Exists(actual)) Directory.Delete(actual, true);
            }
        }

        private static void OptionalCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Optional service regression: " + message);
            Interlocked.Increment(ref optionalServiceChecks);
        }

        private sealed class OptionalLedgerFake : IOptionalServiceLedger
        {
            internal string Value = "", LastRead;
            internal int Reads, Writes;
            internal bool DenyRead, ThrowRead, ThrowWrite, LoseWrites;
            internal int RejectWriteNumber = -1, RejectWritesFrom = int.MaxValue;
            internal Action<string> AfterWrite;
            internal Func<string, bool> RejectWrite;
            internal readonly List<string> Written = new List<string>();

            public bool TryRead(out string value)
            {
                Reads++;
                value = null;
                if (ThrowRead) throw new IOException("Fake ledger read failed");
                if (DenyRead) return false;
                value = Value; LastRead = value;
                return true;
            }

            public bool TryWrite(string value)
            {
                Writes++; Written.Add(value);
                if (ThrowWrite) throw new IOException("Fake ledger write failed");
                if (Writes == RejectWriteNumber || Writes >= RejectWritesFrom
                    || (RejectWrite != null && RejectWrite(value))) return false;
                if (!LoseWrites) Value = value;
                if (AfterWrite != null) AfterWrite(value);
                return true;
            }
        }

        private sealed class OptionalControlFake : IOptionalServiceControl
        {
            internal readonly Dictionary<string, OptionalServiceSnapshot> Services =
                new Dictionary<string, OptionalServiceSnapshot>(StringComparer.OrdinalIgnoreCase);
            internal readonly HashSet<string> RejectStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> StopAttempts = new List<string>(), Stops = new List<string>();
            internal readonly List<string> Starts = new List<string>(), Events = new List<string>();
            internal string Boot = Guid.NewGuid().ToString("D");
            internal bool DenyBoot, ThrowBoot, DenyQuery, ThrowQuery, PrintingIdle = true, ThrowPrinting;
            internal bool ThrowStop, ExternalStopOnReject, DenyStart, ThrowStart, StartOwnershipChanged;
            internal int StopState = 1, StartState = 4, Queries, PrintQueries, UnexpectedQueries;
            internal Action AfterBoot, AfterPrinting, BeforeNativeStop, AfterNativeStop;
            internal Action<string> AfterQuery, BeforeStart, StopGuard;

            internal OptionalServiceSnapshot Running(string name)
            {
                var snapshot = new OptionalServiceSnapshot { Exists = true, State = 4, StartType = 3,
                    AcceptsStop = true, Configuration = "fixture-config:" + name };
                Services[name] = snapshot;
                return snapshot;
            }

            public bool TryGetBootIdentity(out string identity)
            {
                identity = Boot;
                if (AfterBoot != null) AfterBoot();
                if (ThrowBoot) throw new IOException("Fake boot query failed");
                return !DenyBoot;
            }

            public bool TryQuery(string name, out OptionalServiceSnapshot snapshot)
            {
                Queries++;
                if (!OptionalServicePauseEngine.IsAllowed(name))
                {
                    UnexpectedQueries++;
                    throw new InvalidOperationException("Query escaped the service allowlist: " + name);
                }
                snapshot = null;
                if (ThrowQuery) throw new IOException("Fake service query failed");
                if (DenyQuery) return false;
                OptionalServiceSnapshot current;
                if (!Services.TryGetValue(name, out current)) snapshot = new OptionalServiceSnapshot();
                else snapshot = new OptionalServiceSnapshot { Exists = current.Exists, State = current.State,
                    StartType = current.StartType, AcceptsStop = current.AcceptsStop,
                    HasActiveDependents = current.HasActiveDependents, Configuration = current.Configuration };
                if (AfterQuery != null) AfterQuery(name);
                return true;
            }

            public bool IsPrintingIdle()
            {
                PrintQueries++;
                if (AfterPrinting != null) AfterPrinting();
                if (ThrowPrinting) throw new IOException("Fake print query failed");
                return PrintingIdle;
            }

            public bool TryStop(string name, OptionalServiceSnapshot expected, Func<bool> mayContinue)
            {
                StopAttempts.Add(name);
                if (BeforeNativeStop != null) BeforeNativeStop();
                if (mayContinue != null && !mayContinue()) return false;
                OptionalServiceSnapshot current;
                if (!Services.TryGetValue(name, out current) || !current.Exists || current.State != 4
                    || current.StartType == 4 || !current.AcceptsStop || current.HasActiveDependents
                    || expected == null || current.Configuration != expected.Configuration) return false;
                if (RejectStop.Contains(name))
                {
                    if (ExternalStopOnReject) current.State = 1;
                    return false;
                }
                if (StopGuard != null) StopGuard(name);
                Stops.Add(name); Events.Add("stop:" + name); current.State = StopState;
                if (AfterNativeStop != null) AfterNativeStop();
                if (ThrowStop) throw new IOException("Fake STOP had no success receipt");
                return true;
            }

            public OptionalServiceStartResult TryStart(string name, string expectedConfiguration)
            {
                Starts.Add(name); Events.Add("start:" + name);
                if (BeforeStart != null) BeforeStart(name);
                if (ThrowStart) throw new IOException("Fake START failed");
                if (StartOwnershipChanged) return OptionalServiceStartResult.OwnershipChanged;
                if (DenyStart) return OptionalServiceStartResult.NotIssued;
                OptionalServiceSnapshot current;
                if (!Services.TryGetValue(name, out current) || !current.Exists
                    || current.StartType == 4 || current.State != 1
                    || current.Configuration != expectedConfiguration) return OptionalServiceStartResult.OwnershipChanged;
                current.State = StartState;
                return OptionalServiceStartResult.Accepted;
            }
        }

        private sealed class OptionalServiceFixture
        {
            internal readonly OptionalControlFake Control = new OptionalControlFake();
            internal readonly OptionalLedgerFake Ledger = new OptionalLedgerFake();
            internal readonly OptionalServicePauseEngine Engine;

            internal OptionalServiceFixture(params string[] names)
            {
                foreach (string name in names) Control.Running(name);
                Control.StopGuard = delegate(string name)
                {
                    OptionalCheck(Ledger.LastRead == Ledger.Value
                        && Ledger.Value.IndexOf("\nP\t" + name + "\t", StringComparison.Ordinal) >= 0,
                        "STOP preceded persisted, verified Prepared ownership for " + name);
                };
                Engine = new OptionalServicePauseEngine(Control, Ledger);
            }

            internal void Own()
            {
                OptionalCheck(Engine.Activate() && Engine.Active && Engine.HasResidue,
                    "The valid running fixture could not establish ownership");
            }
        }

        private static void OptionalServicesAllowlistAndDefaults(string root)
        {
            var expected = new HashSet<string>(new[]
                { "PrintNotify", "Spooler", "WSearch", "WMPNetworkSvc", "MapsBroker", "DiagTrack", "RetailDemo" },
                StringComparer.OrdinalIgnoreCase);
            OptionalCheck(OptionalServicePauseEngine.Names.Length == expected.Count, "Unexpected optional-service roster");
            foreach (string name in OptionalServicePauseEngine.Names)
                OptionalCheck(expected.Remove(name) && OptionalServicePauseEngine.IsAllowed(name.ToUpperInvariant()),
                    "Allowlist duplicate, unknown entry, or case-sensitive service name: " + name);
            foreach (string name in new[] { null, "", " ", "SysMain", "Fax", "stisvc", "wuauserv", "WinDefend",
                "MemoryCompression", "Spooler*", " Spooler", "Spooler|WinDefend" })
                OptionalCheck(!OptionalServicePauseEngine.IsAllowed(name), "Unsafe optional service was accepted: " + name);
            OptionalCheck(PolicyCatalog.ItemOf(PolicyCatalog.KeyPauseServices).Fallback == "0"
                && !PolicyResolver.Global().PauseServices, "Service pausing is not opt-in by default");
            foreach (PerformancePreset preset in Enum.GetValues(typeof(PerformancePreset)))
            {
                var profile = new GameProfile { Id = "service-default", Name = "service-default" };
                OptionalCheck(PolicyResolver.SetOverride(profile, PolicyCatalog.KeyPreset, ((int)preset).ToString()),
                    "Could not select the preset fixture");
                OptionalCheck(!PolicyResolver.For(profile).PauseServices, "A preset silently opted into pausing services");
            }
        }

        private static void OptionalServicesEligibility(string root)
        {
            foreach (int state in new[] { 0, 1, 2, 3, 5, 6, 7 })
            {
                var f = new OptionalServiceFixture("WSearch"); f.Control.Services["WSearch"].State = state;
                f.Engine.Activate(); f.Engine.Restore();
                OptionalCheck(f.Control.StopAttempts.Count == 0 && f.Control.Starts.Count == 0 && !f.Engine.HasResidue,
                    "A service originally not Running was changed: " + state);
            }
            foreach (string reason in new[] { "disabled", "missing", "cannot-stop", "dependent", "unknown-config" })
            {
                var f = new OptionalServiceFixture("WSearch"); OptionalServiceSnapshot service = f.Control.Services["WSearch"];
                if (reason == "disabled") service.StartType = 4;
                if (reason == "missing") service.Exists = false;
                if (reason == "cannot-stop") service.AcceptsStop = false;
                if (reason == "dependent") service.HasActiveDependents = true;
                if (reason == "unknown-config") service.Configuration = "";
                f.Engine.Activate(); f.Engine.Restore();
                OptionalCheck(f.Control.StopAttempts.Count == 0 && f.Control.Starts.Count == 0 && !f.Engine.HasResidue,
                    "An ineligible service was changed: " + reason);
            }
        }

        private static void OptionalServicesPrintingAndDependents(string root)
        {
            foreach (bool throwing in new[] { false, true })
            {
                var f = new OptionalServiceFixture("PrintNotify", "Spooler", "WSearch");
                f.Control.PrintingIdle = false; f.Control.ThrowPrinting = throwing;
                f.Own();
                OptionalCheck(f.Control.PrintQueries > 0 && f.Control.Stops.Count == 1 && f.Control.Stops[0] == "WSearch",
                    "Busy or unreadable printing was paused, or blocked unrelated optional work");
                OptionalCheck(f.Engine.Restore() && f.Control.Starts.Count == 1 && f.Control.Starts[0] == "WSearch",
                    "Printing eligibility changed the restoration set");
            }
            var dependent = new OptionalServiceFixture("Spooler", "WSearch");
            dependent.Control.Services["Spooler"].HasActiveDependents = true;
            dependent.Own();
            OptionalCheck(dependent.Control.Stops.Count == 1 && dependent.Control.Stops[0] == "WSearch",
                "A service with an active dependent was stopped");
            dependent.Engine.Restore();
        }

        private static void OptionalServicesOwnedRoundTrip(string root)
        {
            var f = new OptionalServiceFixture(OptionalServicePauseEngine.Names);
            f.Own();
            OptionalCheck(f.Control.Stops.Count == 7 && f.Ledger.Value.IndexOf("\nP\t", StringComparison.Ordinal) < 0,
                "Successful STOP receipts were not promoted to Owned records");
            OptionalCheck(f.Engine.Activate() && f.Control.Stops.Count == 7,
                "Repeated activation stopped an already-owned service again");
            OptionalCheck(f.Engine.Restore() && !f.Engine.Active && !f.Engine.HasResidue && f.Ledger.Value == "",
                "Successful restoration retained active state or ownership");
            OptionalCheck(f.Control.Starts.Count == 7, "Restoration did not cover exactly the owned services");
            for (int i = 0; i < f.Control.Stops.Count; i++)
                OptionalCheck(f.Control.Starts[i] == f.Control.Stops[f.Control.Stops.Count - 1 - i],
                    "Services were not restored in reverse dependency order");
            OptionalCheck(f.Engine.Restore() && f.Control.Starts.Count == 7, "Repeated restore started a service twice");
        }

        private static void OptionalServicesQueryAndBootFailure(string root)
        {
            foreach (string kind in new[] { "query-false", "query-throw", "boot-false", "boot-throw", "boot-empty", "boot-invalid", "boot-zero" })
            {
                var f = new OptionalServiceFixture("WSearch");
                f.Control.DenyQuery = kind == "query-false"; f.Control.ThrowQuery = kind == "query-throw";
                f.Control.DenyBoot = kind == "boot-false"; f.Control.ThrowBoot = kind == "boot-throw";
                if (kind == "boot-empty") f.Control.Boot = "";
                if (kind == "boot-invalid") f.Control.Boot = "not-a-boot-identity";
                if (kind == "boot-zero") f.Control.Boot = Guid.Empty.ToString("D");
                f.Engine.Activate();
                OptionalCheck(f.Control.StopAttempts.Count == 0 && f.Control.Starts.Count == 0 && f.Ledger.Value == "",
                    "Unknown service/boot state admitted a mutation: " + kind);
            }
        }

        private static void OptionalServicesRejectedStopIsNotOwned(string root)
        {
            foreach (bool externalStop in new[] { false, true })
            {
                var f = new OptionalServiceFixture("WSearch");
                f.Control.RejectStop.Add("WSearch"); f.Control.ExternalStopOnReject = externalStop;
                f.Engine.Activate();
                OptionalCheck(f.Engine.Restore() && f.Control.Starts.Count == 0 && !f.Engine.HasResidue,
                    "A rejected STOP claimed another actor's stopped service");
            }
            var uncertain = new OptionalServiceFixture("WSearch"); uncertain.Control.ThrowStop = true;
            OptionalCheck(!uncertain.Engine.Activate() && uncertain.Engine.HasResidue
                && uncertain.Ledger.Value.IndexOf("\nP\tWSearch\t", StringComparison.Ordinal) >= 0,
                "A throwing STOP adapter fabricated a success receipt or discarded uncertainty");
            OptionalCheck(!uncertain.Engine.Restore() && uncertain.Control.Starts.Count == 0,
                "Unconfirmed STOP was restarted without an ownership receipt");
        }

        private static void OptionalServicesJournalBeforeStop(string root)
        {
            foreach (string failure in new[] { "read-false", "read-throw", "write-false", "write-throw", "readback-mismatch", "readback-false" })
            {
                var f = new OptionalServiceFixture("WSearch");
                f.Ledger.DenyRead = failure == "read-false"; f.Ledger.ThrowRead = failure == "read-throw";
                if (failure == "write-false") f.Ledger.RejectWritesFrom = 1;
                f.Ledger.ThrowWrite = failure == "write-throw"; f.Ledger.LoseWrites = failure == "readback-mismatch";
                if (failure == "readback-false") f.Ledger.AfterWrite = delegate { f.Ledger.DenyRead = true; };
                OptionalCheck(!f.Engine.Activate() && !f.Engine.Active && f.Control.StopAttempts.Count == 0,
                    "An unverified Prepared record admitted STOP: " + failure);
                OptionalCheck(f.Control.Starts.Count == 0, "Journal failure started a service without a receipt");
                if (f.Ledger.DenyRead || f.Ledger.ThrowRead)
                    OptionalCheck(f.Engine.HasResidue, "Unreadable recovery data was treated as an empty ledger");
            }
        }

        private static void OptionalServicesReceiptWriteRollback(string root)
        {
            var f = new OptionalServiceFixture("WSearch");
            f.Ledger.RejectWrite = delegate(string raw) { return OptionalHasStage(raw, 'A') || OptionalHasStage(raw, 'O'); };
            OptionalCheck(!f.Engine.Activate() && !f.Engine.Active && f.Control.Stops.Count == 1
                && f.Control.Starts.Count == 1 && f.Control.Services["WSearch"].State == 4,
                "A failed Owned write did not immediately roll back the in-process STOP receipt");
            OptionalCheck(!f.Engine.HasResidue, "Successful receipt rollback left unnecessary recovery debt");
        }

        private static void OptionalServicesPendingReceiptSurvives(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); f.Control.StopState = 3;
            f.Ledger.RejectWrite = delegate(string raw) { return OptionalHasStage(raw, 'A') || OptionalHasStage(raw, 'O'); };
            OptionalCheck(!f.Engine.Activate() && f.Engine.HasResidue && f.Control.Starts.Count == 0,
                "Stop-pending receipt was lost or START was issued before STOP completed");
            OptionalCheck(!f.Engine.Restore() && f.Control.Starts.Count == 0,
                "A pending stop was force-restored instead of retaining its receipt");
            f.Control.Services["WSearch"].State = 1; f.Ledger.RejectWrite = null;
            OptionalCheck(f.Engine.Restore() && f.Control.Starts.Count == 1 && !f.Engine.HasResidue,
                "The live receipt could not finish rollback after STOP completed");
        }

        private static bool OptionalHasStage(string raw, char stage)
        { return raw != null && raw.IndexOf("\n" + stage + "\t", StringComparison.Ordinal) >= 0; }

        private static void OptionalServicesObserveStopsDoesNotEnforce(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); f.Control.StopState = 4; f.Own();
            OptionalCheck(f.Engine.ObserveStops() && OptionalHasStage(f.Ledger.Value, 'A'),
                "Observing the initial Running state discarded an accepted STOP");
            f.Control.Services["WSearch"].State = 3;
            OptionalCheck(f.Engine.ObserveStops() && OptionalHasStage(f.Ledger.Value, 'A'),
                "STOP_PENDING was promoted to a confirmed stop");
            f.Control.Services["WSearch"].State = 1;
            OptionalCheck(f.Engine.ObserveStops() && OptionalHasStage(f.Ledger.Value, 'O'),
                "Observed Stopped state was not persisted as confirmed ownership");
            f.Control.Services["WSearch"].State = 4;
            OptionalCheck(f.Engine.ObserveStops() && f.Control.StopAttempts.Count == 1 && f.Control.Starts.Count == 0,
                "Observation re-enforced STOP after another actor restarted the service");
            OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue && f.Control.Starts.Count == 0,
                "An externally restarted service was not relinquished after its stop was observed");
            int queries = f.Control.Queries;
            OptionalCheck(f.Engine.ObserveStops() && f.Control.Queries == queries,
                "Inactive service observation kept polling");
        }

        private static void OptionalServicesObserveStopsRetriesJournal(string root)
        {
            foreach (string failure in new[] { "read", "write", "readback" })
            {
                var f = new OptionalServiceFixture("WSearch"); f.Control.StopState = 3; f.Own();
                f.Control.Services["WSearch"].State = 1;
                if (failure == "read") f.Ledger.DenyRead = true;
                if (failure == "write") f.Ledger.RejectWrite = delegate(string raw) { return OptionalHasStage(raw, 'O'); };
                if (failure == "readback") f.Ledger.AfterWrite = delegate(string raw)
                    { if (OptionalHasStage(raw, 'O')) f.Ledger.DenyRead = true; };
                OptionalCheck(!f.Engine.ObserveStops() && f.Engine.HasResidue,
                    "Unverified observation persistence was reported successful: " + failure);
                f.Ledger.DenyRead = false; f.Ledger.RejectWrite = null; f.Ledger.AfterWrite = null;
                OptionalCheck(f.Engine.ObserveStops() && OptionalHasStage(f.Ledger.Value, 'O')
                    && f.Control.StopAttempts.Count == 1 && f.Control.Starts.Count == 0,
                    "Observation never retried its failed A-to-O persistence: " + failure);
                f.Control.Services["WSearch"].State = 4;
                var afterCrash = new OptionalServicePauseEngine(f.Control, f.Ledger);
                OptionalCheck(afterCrash.Restore() && f.Control.Starts.Count == 0 && !afterCrash.HasResidue,
                    "Persisted observation did not let a fresh process relinquish an external restart");
            }
        }

        private static void OptionalServicesAcceptedStopMustBeObserved(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); f.Control.StopState = 4;
            OptionalCheck(f.Engine.Activate() && f.Engine.HasResidue && OptionalHasStage(f.Ledger.Value, 'A'),
                "Accepted STOP without an observed stop was not recorded as pending acceptance");
            OptionalCheck(!f.Engine.Restore() && f.Engine.HasResidue && f.Control.Starts.Count == 0,
                "The brief Running state after accepted STOP prematurely cleared ownership");
            f.Control.Services["WSearch"].State = 3;
            OptionalCheck(!f.Engine.Restore() && f.Engine.HasResidue && f.Control.Starts.Count == 0,
                "Accepted STOP_PENDING was treated as completed restoration");
            f.Control.Services["WSearch"].State = 1;
            OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue && f.Control.Starts.Count == 1,
                "Accepted STOP could not restore after finally observing Stopped");
        }

        private static void OptionalServicesPreparedCrashIsUnowned(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); string prepared = null;
            f.Ledger.AfterWrite = delegate(string raw) { if (prepared == null) prepared = raw; };
            f.Own();
            OptionalCheck(prepared != null && prepared.IndexOf("\nP\tWSearch\t", StringComparison.Ordinal) >= 0,
                "Could not capture the real Prepared journal format");
            var afterCrash = new OptionalServicePauseEngine(f.Control, new OptionalLedgerFake { Value = prepared });
            OptionalCheck(!afterCrash.Restore() && afterCrash.HasResidue && f.Control.Starts.Count == 0,
                "A fresh process inferred STOP ownership from a Prepared record");
            int stops = f.Control.Stops.Count;
            OptionalCheck(!afterCrash.Activate() && f.Control.Stops.Count == stops,
                "Unverified recovery debt was overwritten by a new session");
        }

        private static void OptionalServicesMalformedJournal(string root)
        {
            var original = new OptionalServiceFixture("WSearch"); original.Own();
            string owned = original.Ledger.Value, header = owned.Split('\n')[0];
            foreach (string raw in new[] { "garbage", owned.Replace("v1\t", "v9\t"),
                owned.Replace("\tWSearch\t", "\tWinDefend\t"), owned.Replace("\nO\t", "\nX\t"),
                header + "\n" + owned, owned + "\n" + owned.Split('\n')[1],
                header + "\nO\tWSearch\t!not-base64!", header + "\nO\tWSearch\t/w==", new string('x', 262145) })
            {
                var ledger = new OptionalLedgerFake { Value = raw };
                var control = new OptionalControlFake { Boot = original.Control.Boot };
                control.Running("WSearch").State = 1;
                var engine = new OptionalServicePauseEngine(control, ledger);
                OptionalCheck(!engine.Restore() && engine.HasResidue && ledger.Value == raw,
                    "Malformed or unauthorized recovery data was cleared or accepted");
                OptionalCheck(!engine.Activate() && control.StopAttempts.Count == 0 && control.Starts.Count == 0
                    && control.UnexpectedQueries == 0, "Invalid recovery data reached a service mutation or foreign query");
            }
        }

        private static void OptionalServicesRestoreRespectsCurrentState(string root)
        {
            foreach (string state in new[] { "running", "paused", "disabled", "removed", "config-changed",
                "state-2", "state-3", "state-5", "state-6" })
            {
                var f = new OptionalServiceFixture("WSearch"); f.Own();
                OptionalServiceSnapshot current = f.Control.Services["WSearch"];
                if (state == "running") current.State = 4;
                if (state == "paused") current.State = 7;
                if (state == "disabled") current.StartType = 4;
                if (state == "removed") current.Exists = false;
                if (state == "config-changed") current.Configuration += ":new-installer";
                if (state.StartsWith("state-", StringComparison.Ordinal)) current.State = int.Parse(state.Substring(6));
                OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue && f.Control.Starts.Count == 0,
                    "Restoration overrode current user/service state: " + state);
            }
        }

        private static void OptionalServicesStartNeedsConfirmation(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); f.Own(); f.Control.StartState = 2;
            OptionalCheck(!f.Engine.Restore() && f.Engine.HasResidue && f.Control.Starts.Count == 1,
                "A START request was mistaken for confirmed Running state");
            OptionalCheck(!f.Engine.Restore() && f.Control.Starts.Count == 1,
                "START_PENDING caused a duplicate START request");
            f.Control.Services["WSearch"].State = 4;
            OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue && f.Control.Starts.Count == 1,
                "A later confirmed Running state did not clear recovery debt");
        }

        private static void OptionalServicesStartOwnershipChangeIsFinal(string root)
        {
            foreach (bool failClear in new[] { false, true })
            {
                var f = new OptionalServiceFixture("WSearch"); f.Own();
                // 适配器看见外部换了持有者或者改了配置
                // 等它返回的时候 外层查询已经能看到原来那个 Stopped 状态了
                f.Control.StartOwnershipChanged = true;
                if (failClear) f.Ledger.RejectWrite = delegate(string raw) { return raw.Length == 0; };
                OptionalCheck(f.Engine.Restore() == !failClear && f.Control.Starts.Count == 1
                    && f.Control.Services["WSearch"].State == 1,
                    "Adapter ownership-change evidence was ignored");
                f.Control.StartOwnershipChanged = false; f.Ledger.RejectWrite = null;
                OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue && f.Control.Starts.Count == 1
                    && f.Control.Services["WSearch"].State == 1,
                    "OwnershipChanged was downgraded into a retryable START failure");
            }
        }

        private static void OptionalServicesRejectedStartRelinquishesChangedState(string root)
        {
            foreach (string change in new[] { "disabled", "configuration", "paused", "state-2", "state-3", "state-5", "state-6" })
            foreach (bool failClear in new[] { false, true })
            {
                var f = new OptionalServiceFixture("WSearch"); f.Own();
                OptionalServiceSnapshot service = f.Control.Services["WSearch"];
                string configuration = service.Configuration;
                f.Control.DenyStart = true;
                f.Control.BeforeStart = delegate
                {
                    if (change == "disabled") service.StartType = 4;
                    if (change == "configuration") service.Configuration += ":changed-after-query";
                    if (change == "paused") service.State = 7;
                    if (change.StartsWith("state-", StringComparison.Ordinal)) service.State = int.Parse(change.Substring(6));
                };
                if (failClear) f.Ledger.RejectWrite = delegate(string raw) { return raw.Length == 0; };
                OptionalCheck(f.Engine.Restore() == !failClear && f.Control.Starts.Count == 1,
                    "Rejected START did not relinquish an observed user change: " + change);
                service.StartType = 3; service.Configuration = configuration; service.State = 1;
                f.Control.DenyStart = false; f.Control.BeforeStart = null; f.Ledger.RejectWrite = null;
                OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue && f.Control.Starts.Count == 1
                    && service.State == 1, "Returning to the old configuration revived settled START ownership: " + change);
            }
        }

        private static void OptionalServicesRestoreFailureKeepsDebt(string root)
        {
            foreach (string failure in new[] { "start-false", "start-throw", "query-false", "query-throw", "boot-false", "ledger-false" })
            {
                var f = new OptionalServiceFixture("WSearch"); f.Own(); string owned = f.Ledger.Value;
                f.Control.DenyStart = failure == "start-false"; f.Control.ThrowStart = failure == "start-throw";
                f.Control.DenyQuery = failure == "query-false"; f.Control.ThrowQuery = failure == "query-throw";
                f.Control.DenyBoot = failure == "boot-false"; f.Ledger.DenyRead = failure == "ledger-false";
                OptionalCheck(!f.Engine.Restore() && f.Engine.HasResidue
                    && (failure == "start-throw" ? OptionalHasStage(f.Ledger.Value, 'R') : f.Ledger.Value == owned),
                    "Failed restoration discarded the ownership journal: " + failure);
                f.Control.DenyStart = f.Control.ThrowStart = f.Control.DenyQuery = f.Control.ThrowQuery = f.Control.DenyBoot = false;
                f.Ledger.DenyRead = false;
                if (failure == "start-throw")
                {
                    int starts = f.Control.Starts.Count;
                    OptionalCheck(!f.Engine.Restore() && f.Engine.HasResidue && f.Control.Starts.Count == starts,
                        "An uncertain START exception was blindly retried");
                    f.Control.Services["WSearch"].State = 4;
                }
                OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue,
                    "Restoration could not retry after the failure cleared: " + failure);
            }
        }

        private static void OptionalServicesCleanupFailureDoesNotRestartTwice(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); f.Own();
            f.Ledger.RejectWrite = delegate(string raw) { return raw.Length == 0; };
            OptionalCheck(!f.Engine.Restore() && f.Engine.HasResidue && f.Control.Starts.Count == 1
                && f.Control.Services["WSearch"].State == 4, "Failed journal cleanup was reported as complete");
            f.Ledger.RejectWrite = null;
            OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue && f.Control.Starts.Count == 1,
                "Retrying journal cleanup restarted an already-running service");
        }

        private static void OptionalServicesBootChangeDropsOldOwnership(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); f.Own(); f.Control.Boot = Guid.NewGuid().ToString("D");
            var afterReboot = new OptionalServicePauseEngine(f.Control, f.Ledger);
            OptionalCheck(afterReboot.Restore() && !afterReboot.HasResidue && f.Control.Starts.Count == 0,
                "A new boot restarted a service using last boot's ownership");
        }

        private static void OptionalServicesSettledOwnershipCannotRevive(string root)
        {
            foreach (string reason in new[] { "restored", "disabled", "config-changed", "removed", "paused" })
            {
                var f = new OptionalServiceFixture("WSearch"); f.Own();
                OptionalServiceSnapshot current = f.Control.Services["WSearch"];
                string configuration = current.Configuration;
                if (reason == "disabled") current.StartType = 4;
                if (reason == "config-changed") current.Configuration += ":user-edited";
                if (reason == "removed") current.Exists = false;
                if (reason == "paused") current.State = 7;
                f.Ledger.RejectWrite = delegate(string raw) { return raw.Length == 0; };
                OptionalCheck(!f.Engine.Restore() && f.Engine.HasResidue,
                    "Settled ownership fixture did not retain its failed ledger cleanup: " + reason);
                int starts = f.Control.Starts.Count;
                OptionalCheck(starts == (reason == "restored" ? 1 : 0),
                    "Settling current service state issued an unexpected START: " + reason);
                // 用户又把服务停了 或者把旧配置还原回去
                // 而这时候因为上次清理失败 旧的 Owned 记录还躺在盘上
                current.Exists = true; current.State = 1; current.StartType = 3; current.Configuration = configuration;
                f.Ledger.RejectWrite = null;
                OptionalCheck(f.Engine.Restore() && !f.Engine.HasResidue && f.Control.Starts.Count == starts
                    && current.State == 1, "Failed ledger cleanup resurrected settled ownership: " + reason);
            }
        }

        private static void OptionalServicesRestoreIntentBeforeStart(string root)
        {
            foreach (bool failReadback in new[] { false, true })
            {
                var f = new OptionalServiceFixture("WSearch"); f.Own();
                if (failReadback)
                    f.Ledger.AfterWrite = delegate(string raw) { if (OptionalHasStage(raw, 'R')) f.Ledger.DenyRead = true; };
                else f.Ledger.RejectWrite = delegate(string raw) { return OptionalHasStage(raw, 'R'); };
                OptionalCheck(!f.Engine.Restore() && f.Engine.HasResidue && f.Control.Starts.Count == 0,
                    "START preceded persisted and verified restoration intent");
            }
        }

        private static void OptionalServicesRestoreIntentSurvivesCrash(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); f.Own();
            f.Ledger.RejectWrite = delegate(string raw) { return raw.Length == 0; };
            OptionalCheck(!f.Engine.Restore() && f.Control.Starts.Count == 1 && OptionalHasStage(f.Ledger.Value, 'R'),
                "A completed START without cleared ledger lost its durable restoration intent");
            f.Control.Services["WSearch"].State = 1; f.Ledger.RejectWrite = null;
            var afterCrash = new OptionalServicePauseEngine(f.Control, f.Ledger);
            OptionalCheck(!afterCrash.Restore() && afterCrash.HasResidue && f.Control.Starts.Count == 1,
                "A fresh process restarted a service the user had stopped after previous restoration");
            f.Control.Services["WSearch"].State = 2;
            OptionalCheck(!afterCrash.Restore() && f.Control.Starts.Count == 1,
                "Recovered START_PENDING intent issued a second START");
            f.Control.Services["WSearch"].State = 4;
            OptionalCheck(afterCrash.Restore() && !afterCrash.HasResidue && f.Control.Starts.Count == 1,
                "Recovered restore intent did not settle when Running was observed");
        }

        private static void OptionalServicesRecoverBeforeNewSession(string root)
        {
            var f = new OptionalServiceFixture("WSearch"); f.Own(); f.Control.DenyStart = true;
            f.Control.Running("DiagTrack");
            var next = new OptionalServicePauseEngine(f.Control, f.Ledger);
            OptionalCheck(!next.Activate() && f.Control.Stops.Count == 1 && next.HasResidue,
                "New optional services were paused over unresolved recovery debt");
            f.Control.DenyStart = false; f.Control.Events.Clear();
            OptionalCheck(next.Activate() && f.Control.Events.Count >= 3
                && f.Control.Events[0] == "start:WSearch" && f.Control.Stops.Count == 3,
                "New session did not restore the previous session before issuing STOP");
            OptionalCheck(next.Restore(), "Fresh session could not restore after prior debt was cleared");
        }

        private static void OptionalServicesCancellationBoundaries(string root)
        {
            foreach (string stage in new[] { "initial", "boot", "query", "printing", "journal", "inside-stop", "predicate-throw" })
            {
                bool proceed = stage != "initial";
                var f = new OptionalServiceFixture(stage == "printing" ? "Spooler" : "WSearch");
                Action cancel = delegate { proceed = false; };
                if (stage == "boot") f.Control.AfterBoot = cancel;
                if (stage == "query") f.Control.AfterQuery = delegate(string name) { if (name == "WSearch") cancel(); };
                if (stage == "printing") f.Control.AfterPrinting = cancel;
                if (stage == "journal") f.Ledger.AfterWrite = delegate { cancel(); };
                if (stage == "inside-stop") f.Control.BeforeNativeStop = cancel;
                f.Engine.Activate(delegate
                {
                    if (stage == "predicate-throw") throw new InvalidOperationException("Fake cancelled lifecycle");
                    return proceed;
                });
                OptionalCheck(f.Control.Stops.Count == 0 && f.Control.Starts.Count == 0 && !f.Engine.Active
                    && !f.Engine.HasResidue, "A cancelled boundary issued STOP or retained unowned debt: " + stage);
                OptionalCheck(stage == "inside-stop" ? f.Control.StopAttempts.Count == 1 : f.Control.StopAttempts.Count == 0,
                    "Cancellation was not checked at the intended boundary: " + stage);
            }
        }

        private static void OptionalServicesSerializedRestore(string root)
        {
            var f = new OptionalServiceFixture("WSearch");
            using (var stopped = new ManualResetEvent(false))
            using (var release = new ManualResetEvent(false))
            using (var restoreStarted = new ManualResetEvent(false))
            using (var restoreDone = new ManualResetEvent(false))
            {
                Exception activationError = null, restoreError = null;
                bool activated = false, restored = false;
                f.Control.AfterNativeStop = delegate
                {
                    stopped.Set();
                    if (!release.WaitOne(3000)) throw new TimeoutException("Fake STOP receipt was not released");
                };
                var activation = new Thread(delegate()
                { try { activated = f.Engine.Activate(); } catch (Exception ex) { activationError = ex; } });
                var restoration = new Thread(delegate()
                {
                    restoreStarted.Set();
                    try { restored = f.Engine.Restore(); } catch (Exception ex) { restoreError = ex; }
                    finally { restoreDone.Set(); }
                });
                activation.IsBackground = restoration.IsBackground = true;
                try
                {
                    activation.Start();
                    OptionalCheck(stopped.WaitOne(3000), "Concurrent fixture never reached the simulated STOP");
                    restoration.Start();
                    OptionalCheck(restoreStarted.WaitOne(3000) && !restoreDone.WaitOne(20),
                        "Restore passed an in-flight STOP before its receipt was recorded");
                    release.Set();
                    OptionalCheck(activation.Join(3000) && restoration.Join(3000), "Owned fake service workers did not drain");
                    OptionalCheck(activationError == null && restoreError == null && activated && restored
                        && !f.Engine.HasResidue && f.Control.Stops.Count == 1 && f.Control.Starts.Count == 1,
                        "Serialized restoration lost or duplicated an in-flight STOP receipt");
                }
                finally
                {
                    release.Set();
                    if ((activation.ThreadState & ThreadState.Unstarted) == 0) activation.Join(3000);
                    if ((restoration.ThreadState & ThreadState.Unstarted) == 0) restoration.Join(3000);
                }
            }
        }

        private static void OptionalServicesSessionPolicyAndDebt(string root)
        {
            using (var fixture = new FamilyPolicyFixture(root, "optional-services-session"))
            {
                var irq = new ResetFlowIrqPlatform();
                FamilyPolicySetField(fixture.Mode, "irqProbe", new IrqSessionProbe(irq));
                OptionalCheck(!fixture.Mode.ProbeEffPauseServices, "A new GameMode enabled optional services by default");
                OptionalCheck(fixture.Mode.SetProfileOverride("first", PolicyCatalog.KeyPauseServices, "1"),
                    "Could not opt in the per-game fixture");
                fixture.Mode.ProbeSessionPolicyApply(fixture.Current("first"));
                OptionalCheck(fixture.Mode.ProbeEffPauseServices, "Explicit per-game opt-in was ignored");
                OptionalCheck(fixture.Mode.SetProfileOverride("first", PolicyCatalog.KeyPauseServices, "0"),
                    "Could not opt out the current game");
                fixture.Mode.PauseServices = true;
                OptionalCheck(!fixture.Mode.ProbeEffPauseServices, "Per-game opt-out was overwritten by the global setting");
                fixture.Mode.ProbeSessionPolicyApply(null);
                OptionalCheck(fixture.Mode.ProbeEffPauseServices, "Inherited service preference did not read current global intent");
                fixture.Mode.PauseServices = false;
                OptionalCheck(!fixture.Mode.ProbeEffPauseServices, "Live global opt-out did not reach the inherited session");

                var f = new OptionalServiceFixture("WSearch", "DiagTrack");
                f.Control.StopState = 3;
                f.Ledger.RejectWrite = delegate(string raw) { return raw.IndexOf("\nP\tDiagTrack\t", StringComparison.Ordinal) >= 0; };
                bool active = fixture.Mode.StepOptionalServicesForTest(true, false, f.Engine);
                OptionalCheck(!active && f.Engine.HasResidue && f.Control.Stops.Count == 1,
                    "Could not establish partial-activation recovery debt with active=false");
                f.Control.Services["WSearch"].State = 1; f.Ledger.RejectWrite = null;
                active = fixture.Mode.StepOptionalServicesForTest(false, active, f.Engine);
                OptionalCheck(!active && !f.Engine.HasResidue && f.Control.Starts.Count == 1,
                    "Disabling after a failed activation skipped owned restoration because active was false");
                OptionalCheck(irq.ForbiddenCalls == 0, "Service-only env test attempted IRQ capture or native sampling");
            }
        }

        private static void OptionalServicesStalePolicyAdmission(string root)
        {
            using (var fixture = new FamilyPolicyFixture(root, "optional-services-admission"))
            {
                FamilyPolicySetField(fixture.Mode, "enabled", true);
                fixture.Mode.PauseServices = true;
                Func<bool> global = fixture.Mode.CaptureOptionalServicesAdmissionForTest();
                OptionalCheck(global(), "A current global opt-in was not admitted");
                fixture.Mode.PauseServices = false;
                OptionalCheck(!global(), "Global opt-out left an admitted STOP request live");
                fixture.Mode.PauseServices = true;
                OptionalCheck(!global() && fixture.Mode.CaptureOptionalServicesAdmissionForTest()(),
                    "Global off/on revived a stale service request");

                OptionalCheck(fixture.Mode.SetProfileOverride("first", PolicyCatalog.KeyPauseServices, "1"),
                    "Could not save the per-game admission fixture");
                fixture.Mode.ProbeSessionPolicyApply(fixture.Current("first"));
                Func<bool> perGame = fixture.Mode.CaptureOptionalServicesAdmissionForTest();
                OptionalCheck(perGame(), "A current per-game opt-in was not admitted");
                OptionalCheck(fixture.Mode.SetProfileOverride("first", PolicyCatalog.KeyPauseServices, "0"),
                    "Could not revoke the per-game admission fixture");
                OptionalCheck(!perGame(), "Per-game opt-out was hidden by the old session snapshot");
                OptionalCheck(fixture.Mode.SetProfileOverride("first", PolicyCatalog.KeyPauseServices, "1"),
                    "Could not re-enable the per-game admission fixture");
                OptionalCheck(!perGame() && fixture.Mode.CaptureOptionalServicesAdmissionForTest()(),
                    "Per-game off/on revived a stale service request");

                var fake = new OptionalServiceFixture("WSearch");
                fake.Engine.Activate(perGame);
                OptionalCheck(fake.Control.StopAttempts.Count == 0 && !fake.Engine.Active,
                    "An expired policy guard reached the service adapter");
                Func<bool> inherited = fixture.Mode.CaptureOptionalServicesAdmissionForTest();
                OptionalCheck(fixture.Mode.ClearProfileOverride("first", PolicyCatalog.KeyPauseServices),
                    "Could not clear the explicit service override");
                OptionalCheck(!inherited() && fixture.Mode.CaptureOptionalServicesAdmissionForTest()(),
                    "Clearing a service override retained the previous request's admission");
                OptionalCheck(fixture.Mode.SetProfileOverride("first", PolicyCatalog.KeyPauseServices, "1"),
                    "Could not prepare the bulk-clear admission fixture");
                Func<bool> beforeClearAll = fixture.Mode.CaptureOptionalServicesAdmissionForTest();
                OptionalCheck(beforeClearAll(), "Bulk-clear fixture did not start with current admission");
                OptionalCheck(fixture.Mode.ClearProfileOverrides("first") > 0
                    && fixture.Current("first").Overrides.Count == 0,
                    "Bulk reset did not remove the current game's overrides");
                OptionalCheck(!beforeClearAll() && fixture.Mode.CaptureOptionalServicesAdmissionForTest()(),
                    "Clearing all overrides failed to invalidate a previous service request");
            }
        }

        private static void OptionalServicesCancelledActivationHasNoActiveBit(string root)
        {
            using (var fixture = new FamilyPolicyFixture(root, "optional-services-cancelled-active"))
            {
                var irq = new ResetFlowIrqPlatform();
                FamilyPolicySetField(fixture.Mode, "irqProbe", new IrqSessionProbe(irq));
                var f = new OptionalServiceFixture("WSearch"); bool proceed = true;
                f.Control.AfterQuery = delegate(string name) { if (name == "WSearch") proceed = false; };
                Func<bool> hasResidue = delegate { return f.Engine.HasResidue; };
                Func<bool> isApplied = delegate { return f.Engine.Active; };
                Func<bool> activate = delegate { return f.Engine.Activate(delegate { return proceed; }); };
                Func<bool> restore = delegate { return f.Engine.Restore(); };
                bool active = (bool)FamilyPolicyInvoke(fixture.Mode, "StepOptionalServices", true, false,
                    hasResidue, isApplied, activate, restore);
                OptionalCheck(!active && !f.Engine.Active && !f.Engine.HasResidue && f.Control.Stops.Count == 0,
                    "A successful cancellation was published as an applied environment strategy");
                proceed = true; f.Control.AfterQuery = null;
                active = (bool)FamilyPolicyInvoke(fixture.Mode, "StepOptionalServices", true, active,
                    hasResidue, isApplied, activate, restore);
                OptionalCheck(active && f.Engine.Active && f.Control.Stops.Count == 1,
                    "The canceled active bit suppressed a later real activation");
                OptionalCheck(!fixture.Mode.StepOptionalServicesForTest(false, active, f.Engine)
                    && !f.Engine.HasResidue && irq.ForbiddenCalls == 0,
                    "Canceled-activation retry could not restore using the isolated environment path");
            }
        }

        private static void OptionalServicesLiveOverrideFuse(string root)
        {
            using (var fixture = new FamilyPolicyFixture(root, "optional-services-live-fuse"))
            {
                var irq = new ResetFlowIrqPlatform();
                FamilyPolicySetField(fixture.Mode, "irqProbe", new IrqSessionProbe(irq));
                fixture.Mode.ClearProfileOverrides("first");
                GameProfile inherited = fixture.Current("first");
                OptionalCheck(inherited.Overrides.Count == 0,
                    "Fuse fixture must enter the session with no overrides at all");
                fixture.Mode.ProbeSessionPolicyApply(inherited);
                OptionalCheck(fixture.Mode.SetProfileOverride("first", PolicyCatalog.KeyPauseServices, "1")
                    && fixture.Mode.ProbeEffPauseServices, "The late per-game service opt-in was not applied");
                var f = new OptionalServiceFixture("WSearch"); f.Ledger.DenyRead = true;
                bool active = fixture.Mode.StepOptionalServicesForTest(true, false, f.Engine);
                var retry = (Dictionary<string, long>)typeof(GameMode).GetField("envNextAttempt",
                    BindingFlags.Instance | BindingFlags.NonPublic).GetValue(fixture.Mode);
                retry["services"] = 0; // Advance only the deadline, preserving the first failure.
                active = fixture.Mode.StepOptionalServicesForTest(true, active, f.Engine);
                OptionalCheck(!active && Settings.Load("EnvFuse_services", false)
                    && !fixture.Current("first").Overrides.ContainsKey(PolicyCatalog.KeyPauseServices)
                    && !fixture.Mode.ProbeEffPauseServices,
                    "Fuse relied on the old inherited snapshot and left the current service override enabled");
                OptionalCheck(f.Control.StopAttempts.Count == 0 && f.Control.Starts.Count == 0 && irq.ForbiddenCalls == 0,
                    "Service fuse fixture reached a real or unverified mutation");
            }
        }

        private static void OptionalServicesResetKeepsFailedRecovery(string root)
        {
            var reset = new ResetFlowFixture(root, "optional-services-reset");
            var f = new OptionalServiceFixture("WSearch"); f.Own(); f.Control.DenyStart = true;
            reset.SetRestore(delegate { return f.Engine.Restore() ? new List<string>() : new List<string> { "optional service recovery" }; });
            int files; string failure;
            OptionalCheck(!Program.TryResetUserData(reset.DirectoryPath, reset.Stop(true), out files, out failure),
                "Reset succeeded while an owned optional service could not be restored");
            reset.AssertOriginalFiles(); reset.AssertRegistryPresent();
            OptionalCheck(reset.RegistryCalls == 0 && f.Engine.HasResidue,
                "Reset deleted registry/recovery data before restoration was confirmed");
            f.Control.DenyStart = false;
            // 新的重置回调会把同一批文件再校验一遍 不预设这是第一次尝试
            OptionalCheck(Program.TryResetUserData(reset.DirectoryPath, delegate { return true; }, out files, out failure),
                "Reset could not retry after service recovery became available");
            reset.AssertOwnedFilesGone(); reset.AssertForeignFiles();
            OptionalCheck(!f.Engine.HasResidue && reset.RegistryCalls == 1,
                "Confirmed service recovery did not allow the normal reset sequence");
        }
    }
}
#endif
