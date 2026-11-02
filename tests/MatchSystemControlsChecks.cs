#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
#if PAVISE_MATCH_SYSTEM_CONTROLS
    internal static partial class SelfTests
    { public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application runtime is unavailable in match-control checks"); } }
#endif
    internal static class MatchSystemControlsChecks
    {
        private static int checks;
        private static void Check(bool good, string detail) { checks++; if (!good) throw new InvalidOperationException(detail); }
        private sealed class Ledger : IOptionalServiceLedger
        {
            internal string Value = "";
            internal int Writes;
            internal bool DenyRead, DenyWrite, DropWrite;
            internal Action<string> AfterWrite;
            public bool TryRead(out string value) { value = Value; return !DenyRead; }
            public bool TryWrite(string value) { Writes++; if (DenyWrite) return false; if (!DropWrite) Value = value; if (AfterWrite != null) AfterWrite(value); return true; }
        }
        private sealed class Services : IOptionalServiceControl
        {
            internal readonly Dictionary<string, OptionalServiceSnapshot> Values = new Dictionary<string, OptionalServiceSnapshot>(StringComparer.OrdinalIgnoreCase);
            internal readonly List<string> Stops = new List<string>(), Starts = new List<string>(), Queries = new List<string>();
            internal readonly string Boot = Guid.NewGuid().ToString("D");
            internal string DenyQuery, RejectStop;
            internal bool ThrowStop;
            internal int StopState = 1;
            internal Action<string> BeforeStop;
            internal Action<string> AfterStop;
            internal void Add(string name) { Values[name] = new OptionalServiceSnapshot { Exists = true, State = 4, StartType = 3, AcceptsStop = true, Configuration = "same:" + name }; }
            public bool TryGetBootIdentity(out string identity) { identity = Boot; return true; }
            public bool TryQuery(string name, out OptionalServiceSnapshot value)
            {
                Queries.Add(name); value = null;
                if (name == DenyQuery) return false;
                OptionalServiceSnapshot item;
                value = Values.TryGetValue(name, out item) ? new OptionalServiceSnapshot { Exists = item.Exists, State = item.State, StartType = item.StartType, AcceptsStop = item.AcceptsStop, HasActiveDependents = item.HasActiveDependents, Configuration = item.Configuration } : new OptionalServiceSnapshot();
                return true;
            }
            public bool IsPrintingIdle() { return true; }
            public bool TryStop(string name, OptionalServiceSnapshot before, Func<bool> mayContinue)
            {
                Stops.Add(name); if (BeforeStop != null) BeforeStop(name);
                if (mayContinue != null && !mayContinue()) return false;
                if (name == RejectStop) return false;
                Values[name].State = StopState;
                if (AfterStop != null) AfterStop(name);
                if (ThrowStop) throw new IOException("Unknown STOP result");
                return true;
            }
            public OptionalServiceStartResult TryStart(string name, string expected)
            {
                Starts.Add(name);
                OptionalServiceSnapshot current;
                if (!Values.TryGetValue(name, out current) || !current.Exists || current.State != 1 || current.StartType == 4 || current.Configuration != expected) return OptionalServiceStartResult.OwnershipChanged;
                current.State = 4; return OptionalServiceStartResult.Accepted;
            }
        }
        private sealed class Maintenance : IMaintenanceControl
        {
            internal MaintenanceValue Value = new MaintenanceValue();
            internal bool DenyRead, DenyWrite, LoseWrite, DenyDelete, ThrowRuntime;
            internal int Writes, Deletes, RuntimeQueries;
            internal Action BeforeWrite;
            internal MaintenanceRuntimeState Runtime = MaintenanceRuntimeState.NotObservedRunning;
            public bool TryRead(out MaintenanceValue value) { value = Value; return !DenyRead; }
            public bool TryWrite(int value, MaintenanceValue expected) { Writes++; if (BeforeWrite != null) BeforeWrite(); if (DenyWrite || !expected.SameAs(Value)) return false; if (!LoseWrite) Value = new MaintenanceValue { Exists = true, Kind = RegistryValueKind.DWord, Value = value }; return true; }
            public bool TryDelete(MaintenanceValue expected) { Deletes++; if (DenyDelete || !expected.SameAs(Value)) return false; Value = new MaintenanceValue(); return true; }
            public MaintenanceRuntimeSnapshot QueryRuntime() { RuntimeQueries++; if (ThrowRuntime) throw new IOException(); return new MaintenanceRuntimeSnapshot { State = Runtime, RunningTaskCount = Runtime == MaintenanceRuntimeState.Running ? 2 : 0 }; }
        }
        private static void ServicesCheck()
        {
            Check(!Settings.Load(OptionalServicePauseEngine.PcaPreferenceKey, false), "PCA pause default must remain off");
            foreach (bool include in new[] { false, true })
            {
                var native = new Services(); native.Add("PcaSvc"); native.Add("WSearch"); var ledger = new Ledger();
                var engine = new OptionalServicePauseEngine(native, ledger);
                native.BeforeStop = delegate(string name) { Check(ledger.Value.Contains("\nP\t" + name + "\t"), "STOP before durable prepared record"); };
                Check(engine.Activate(null, include), "Base optional-service activation failed");
                Check(engine.AuditStops(null) && engine.Activate(null, include), "Stable paused service was misclassified as resumed");
                Check(!native.Stops.Contains("PcaSvc") && !native.Queries.Contains("PcaSvc"), "Retired PCA flag still queried or paused the service");
                var afterCrash = new OptionalServicePauseEngine(native, ledger);
                Check(afterCrash.Restore(), "PCA recovery was blocked by default preference");
                Check(!native.Starts.Contains("PcaSvc") && native.Starts.Count == 1, "Retired PCA flag changed base restoration ownership");
            }
            foreach (string issue in new[] { "read", "stop", "throw" })
            {
                var native = new Services(); native.Add("WSearch"); native.Add("DiagTrack"); var ledger = new Ledger();
                if (issue == "read") native.DenyQuery = "WSearch";
                if (issue == "stop") native.RejectStop = "WSearch";
                if (issue == "throw") native.ThrowStop = true;
                var engine = new OptionalServicePauseEngine(native, ledger);
                Check(!engine.Activate() && !engine.Active, "Unknown or failed service state was reported applied: " + issue);
                Check(!native.Stops.Contains("DiagTrack"), "Later service mutated after first failure");
                Check(engine.LastFailure != null, "First failure lacked a diagnostic");
                if (issue == "throw") Check(engine.HasResidue && !engine.Restore() && native.Starts.Count == 0, "Unconfirmed STOP ownership was discarded or replayed");
            }
            foreach (string protectedState in new[] { "missing", "disabled", "dependent", "no-stop", "pending" })
            {
                var native = new Services(); native.Add("WSearch"); var item = native.Values["WSearch"];
                if (protectedState == "missing") item.Exists = false;
                if (protectedState == "disabled") item.StartType = 4;
                if (protectedState == "dependent") item.HasActiveDependents = true;
                if (protectedState == "no-stop") item.AcceptsStop = false;
                if (protectedState == "pending") item.State = 3;
                var engine = new OptionalServicePauseEngine(native, new Ledger());
                Check(engine.Activate() && native.Stops.Count == 0 && !engine.HasResidue, "Ineligible service was modified: " + protectedState);
            }
            var failedReadback = new Services(); failedReadback.Add("WSearch"); failedReadback.Add("DiagTrack");
            failedReadback.AfterStop = delegate(string name) { if (name == "WSearch") failedReadback.DenyQuery = name; };
            var readbackEngine = new OptionalServicePauseEngine(failedReadback, new Ledger());
            Check(!readbackEngine.Activate() && readbackEngine.HasResidue && failedReadback.Stops.Count == 1, "Post-STOP readback failure allowed later service mutations or discarded ownership");
            failedReadback.DenyQuery = null;
            Check(readbackEngine.Restore() && failedReadback.Starts.Count == 1, "Accepted STOP could not restore after readback access returned");
            foreach (string audit in new[] { "timeout", "read", "restart", "permanent-disable" })
            {
                var native = new Services(); native.Add("DiagTrack"); var ledger = new Ledger(); long now = DateTime.UtcNow.Ticks;
                if (audit == "timeout") native.StopState = 3;
                var engine = new OptionalServicePauseEngine(native, ledger, delegate { return now; });
                Check(engine.Activate(), "Could not prepare audit test");
                if (audit == "timeout")
                {
                    Check(engine.PendingStopCount == 1 && engine.AuditStops(null), "Pending STOP must have a finite observation window");
                    now += TimeSpan.FromSeconds(31).Ticks;
                }
                if (audit == "read") native.DenyQuery = "DiagTrack";
                if (audit == "restart") native.Values["DiagTrack"].State = 4;
                if (audit == "permanent-disable") { native.Values["DiagTrack"].StartType = 4; native.Values["DiagTrack"].Configuration = "longterm-disabled"; }
                if (audit != "timeout") now += TimeSpan.FromSeconds(30).Ticks;
                Check(!engine.AuditStops(null) && native.Stops.Count == 1, "Audit failure issued a repeated STOP or reported success: " + audit);
                native.DenyQuery = null;
                if (audit == "timeout") native.Values["DiagTrack"].State = 1;
                Check(engine.Restore(), "Audit failure could not restore/relinquish ownership: " + audit);
                if (audit == "restart" || audit == "permanent-disable") Check(native.Starts.Count == 0, "Match exit revived an externally controlled service");
                if (audit == "permanent-disable") Check(native.Values["DiagTrack"].StartType == 4, "Match changed the permanent disable startup type");
            }
            var cancelled = new Services(); cancelled.Add("WSearch"); bool proceed = true;
            cancelled.BeforeStop = delegate { proceed = false; };
            var cancelledEngine = new OptionalServicePauseEngine(cancelled, new Ledger());
            Check(cancelledEngine.Activate(delegate { return proceed; }) && !cancelledEngine.Active && cancelled.Starts.Count == 0, "Cancellation created false failure ownership");

            // A new process has a new monotonic epoch. Only live receipts carry a
            // stop deadline; loaded ownership must recover before a fresh activation.
            var reloadedNative = new Services(); reloadedNative.Add("WSearch"); reloadedNative.StopState = 3;
            var reloadedLedger = new Ledger(); long firstEpoch = TimeSpan.FromHours(24).Ticks;
            var oldEngine = new OptionalServicePauseEngine(reloadedNative, reloadedLedger, delegate { return firstEpoch; });
            Check(oldEngine.Activate() && oldEngine.PendingStopCount == 1, "Could not persist a pending stop for epoch-reload check");
            long freshEpoch = 1;
            var newEngine = new OptionalServicePauseEngine(reloadedNative, reloadedLedger, delegate { return freshEpoch; });
            Check(!newEngine.Activate() && reloadedNative.Stops.Count == 1 && reloadedNative.Starts.Count == 0, "New clock epoch replayed a pending service request");
            reloadedNative.Values["WSearch"].State = 1;
            Check(newEngine.Restore() && reloadedNative.Starts.Count == 1 && !newEngine.HasResidue, "Old pending ownership could not recover in a new epoch");
            Check(newEngine.Activate() && reloadedNative.Stops.Count == 2, "Fresh activation could not start after old epoch recovery");
            freshEpoch += TimeSpan.FromSeconds(29).Ticks;
            Check(newEngine.AuditStops(null), "A loaded receipt's old deadline shortened the fresh observation window");
            freshEpoch += TimeSpan.FromSeconds(2).Ticks;
            Check(!newEngine.AuditStops(null) && reloadedNative.Stops.Count == 2, "Fresh stop failed to expire after its own monotonic thirty-second window");
        }
        private static void ServiceAuditCadenceCheck()
        {
            long now = 1;
            var native = new Services();
            native.Add("Spooler"); native.Add("WSearch"); native.Add("PcaSvc");
            var ledger = new Ledger();
            var engine = new OptionalServicePauseEngine(native, ledger, delegate { return now; });
            Check(engine.Activate(null, true), "Could not prepare settled audit cadence");
            native.Queries.Clear();
            int writes = ledger.Writes;
            for (int i = 1; i < 60; i++)
            {
                now = 1 + TimeSpan.FromMilliseconds(i * 500).Ticks;
                Check(engine.AuditStops(null) && engine.Activate(null, true), "Settled service was not retained between audits");
            }
            Check(native.Queries.Count == 0 && ledger.Writes == writes, "Settled services polled SCM or rewrote receipts before thirty seconds");
            now = 1 + TimeSpan.FromSeconds(30).Ticks;
            Check(engine.AuditStops(null) && native.Queries.Count == 2, "Due audit did not query each settled service exactly once");
            Check(engine.AuditStops(null) && native.Queries.Count == 2 && ledger.Writes == writes, "Repeated scan bypassed settled audit deadline");
            now = 2;
            Check(engine.AuditStops(null) && native.Queries.Count == 4, "Clock rollback postponed settled service verification indefinitely");
            Check(engine.AuditStops(null) && native.Queries.Count == 4 && ledger.Writes == writes, "Clock rollback did not establish a new stable audit deadline");
            now = 1 + TimeSpan.FromSeconds(60).Ticks;
            Check(engine.AuditStops(delegate { return false; }) && native.Queries.Count == 4, "Cancelled audit still queried services");
            Check(!native.Queries.Contains("PcaSvc"), "Retired PCA flag participated in periodic service audits");
            Check(engine.Restore() && native.Starts.Count == 2 && !engine.HasResidue, "Audit delay blocked immediate match-exit restoration");

            now = 1;
            var mixed = new Services(); mixed.Add("WSearch"); mixed.Add("DiagTrack");
            mixed.AfterStop = delegate(string name) { if (name == "WSearch") mixed.Values[name].State = 3; };
            var mixedLedger = new Ledger();
            var mixedEngine = new OptionalServicePauseEngine(mixed, mixedLedger, delegate { return now; });
            Check(mixedEngine.Activate(null, true) && mixedEngine.PendingStopCount == 1, "Could not prepare mixed service confirmation");
            mixed.Queries.Clear();
            writes = mixedLedger.Writes;
            for (int i = 1; i <= 5; i++)
            {
                now = 1 + TimeSpan.FromMilliseconds(i * 500).Ticks;
                Check(mixedEngine.AuditStops(null), "Pending service did not retain fast confirmation checks");
            }
            Check(mixed.Queries.Count == 5 && !mixed.Queries.Contains("DiagTrack") && mixedLedger.Writes == writes,
                "Pending service was queried twice per scan or forced settled peers to poll");
            mixed.Values["WSearch"].State = 1;
            now += TimeSpan.FromMilliseconds(500).Ticks;
            Check(mixedEngine.AuditStops(null) && mixedEngine.PendingStopCount == 0 && mixed.Queries.Count == 6
                && mixedLedger.Writes == writes + 1, "Observed stop was not persisted once and moved to slow audits");
            Check(mixedEngine.AuditStops(null) && mixed.Queries.Count == 6 && mixedLedger.Writes == writes + 1,
                "Newly confirmed stop was immediately queried or persisted again");
            mixed.AfterStop = null;
            Check(mixedEngine.Activate(null, false) && mixed.Starts.Count == 0
                && mixedEngine.MatchesSelection(true, true), "Retired PCA flag changed the settled base-service selection");
            mixed.Values["WSearch"].Configuration = "external-choice";
            int started = mixed.Starts.Count;
            Check(mixedEngine.Restore() && mixed.Starts.Count == started + 1 && mixed.Starts[0] == "DiagTrack", "Restore skipped ownership verification during the audit cooldown");
        }
        private static void ServiceHostCandidateCheck()
        {
            Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest();
            var mode = new GameMode(Path.Combine(Path.GetTempPath(), "Pavise-service-hosts-" + Guid.NewGuid().ToString("N")), new SuppressionCore());
            var query = typeof(GameMode).GetMethod("BackgroundServiceHosts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            int calls = 0;
            var firstMap = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("WSearch", 41) };
            var nextMap = new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>("Spooler", 41) };
            mode.ServiceHostsForTest = delegate { calls++; return calls == 1 ? firstMap : nextMap; };
            Func<ProcessSnapshot, object> hosts = delegate(ProcessSnapshot snapshot) { return query.Invoke(mode, new object[] { snapshot }); };
            foreach (ProcEntry entry in new[] {
                new ProcEntry { Pid = 40, Session = 0, Creation = 1, Name = "svchost" },
                new ProcEntry { Pid = 41, Session = 1, Creation = 1, Name = "SearchIndexer" },
                new ProcEntry { Pid = 4, Session = 0, Creation = 1, Name = "spoolsv" },
                new ProcEntry { Pid = 41, Session = 0, Creation = 0, Name = "SearchIndexer" } })
                Check(hosts(new ProcessSnapshot(new[] { entry })) == null && calls == 0,
                    "Service enumeration ran without a usable system-service candidate");
            Check(hosts(new ProcessSnapshot(null)) == null && hosts(null) == null && calls == 0,
                "Empty process snapshot enumerated service hosts");
            var candidate = new ProcessSnapshot(new[] { new ProcEntry { Pid = 41, Session = 0, Creation = 1, Name = "searchindexer" } });
            Check(ReferenceEquals(hosts(candidate), firstMap) && calls == 1, "Eligible service host did not query the current service map");
            Check(ReferenceEquals(hosts(candidate), firstMap) && calls == 1, "Same process snapshot repeated service enumeration");
            var replacement = new ProcessSnapshot(new[] { new ProcEntry { Pid = 41, Session = 0, Creation = 2, Name = "SPOOLSV" } });
            Check(ReferenceEquals(hosts(replacement), nextMap) && calls == 2, "New process identity reused a stale service map");
            Check(hosts(new ProcessSnapshot(null)) == null && calls == 2, "Vanished service host retained its stale map or forced enumeration");
            Check(ReferenceEquals(hosts(replacement), nextMap) && calls == 3, "Returning service host did not refresh service ownership");
        }
        private static void MaintenanceCheck()
        {
            foreach (bool exists in new[] { false, true })
            {
                var native = new Maintenance { Runtime = MaintenanceRuntimeState.Running };
                if (exists) native.Value = new MaintenanceValue { Exists = true, Kind = RegistryValueKind.DWord, Value = 0 };
                var ledger = new Ledger(); var engine = new MaintenancePauseEngine(native, ledger);
                native.BeforeWrite = delegate { Check(ledger.Value.EndsWith("\u001f=1", StringComparison.Ordinal), "Maintenance write before durable snapshot"); };
                Check(engine.Activate() && native.Value.IsDisabled && engine.HasResidue, "Maintenance configuration apply failed");
                Check(engine.QueryRuntime().State == MaintenanceRuntimeState.Running, "Configuration-only apply falsely stopped running maintenance");
                native.BeforeWrite = null;
                Check(engine.Restore() && !engine.HasResidue && native.Value.Exists == exists, "Original maintenance presence was lost");
                if (exists) Check(native.Value.Kind == RegistryValueKind.DWord && (int)native.Value.Value == 0, "Original maintenance type/value was lost");
            }
            foreach (string failure in new[] { "native-read", "native-write", "readback", "ledger-read", "ledger-write", "ledger-readback" })
            {
                var native = new Maintenance(); var ledger = new Ledger();
                if (failure == "native-read") native.DenyRead = true;
                if (failure == "native-write") native.DenyWrite = true;
                if (failure == "readback") native.LoseWrite = true;
                if (failure == "ledger-read") ledger.DenyRead = true;
                if (failure == "ledger-write") ledger.DenyWrite = true;
                if (failure == "ledger-readback") ledger.DropWrite = true;
                var engine = new MaintenancePauseEngine(native, ledger);
                Check(!engine.Activate(), "Unverified maintenance apply was accepted: " + failure);
                if (failure.StartsWith("ledger", StringComparison.Ordinal)) Check(native.Writes == 0, "Maintenance mutation admitted despite journal failure");
            }
            foreach (string original in new[] { "=0", "0", ReversibleReg.Absent, "=0\u001f=1" })
            {
                var native = new Maintenance { Value = new MaintenanceValue { Exists = true, Kind = RegistryValueKind.DWord, Value = 1 } };
                var ledger = new Ledger { Value = original }; var engine = new MaintenancePauseEngine(native, ledger);
                Check(engine.Restore() && !engine.HasResidue, "Valid legacy maintenance snapshot could not restore");
                Check(native.Value.Exists == (original != ReversibleReg.Absent), "Legacy original presence changed");
            }
            foreach (string malformed in new[] { "bad", "!unreadable-recovery-record!", "=0\u001f=2", "=0\u001f=1\u001f=1", "2147483648", "" + new string('1', 129) })
            {
                var native = new Maintenance(); var ledger = new Ledger { Value = malformed }; var engine = new MaintenancePauseEngine(native, ledger);
                Check(!engine.Restore() && engine.HasResidue && ledger.Value == malformed && native.Writes == 0 && native.Deletes == 0, "Corrupt maintenance evidence was cleared/mutated");
            }
            foreach (string changed in new[] { "absent", "value", "type" })
            {
                var native = new Maintenance(); var ledger = new Ledger(); var engine = new MaintenancePauseEngine(native, ledger);
                Check(engine.Activate(), "Could not start maintenance ownership test");
                native.Value = changed == "absent" ? new MaintenanceValue() : new MaintenanceValue { Exists = true,
                    Kind = changed == "type" ? RegistryValueKind.String : RegistryValueKind.DWord, Value = changed == "type" ? (object)"1" : 2 };
                int writes = native.Writes;
                Check(engine.Restore() && !engine.HasResidue && native.Writes == writes && native.Deletes == 0, "Maintenance restoration overwrote external ownership");
            }
            var blocked = new Maintenance(); var debt = new Ledger(); var recovery = new MaintenancePauseEngine(blocked, debt);
            Check(recovery.Activate(), "Could not create maintenance restore debt"); blocked.DenyDelete = true;
            Check(!recovery.Restore() && recovery.HasResidue && debt.Value.Length != 0, "Failed maintenance restoration discarded debt");
            blocked.DenyDelete = false; Check(recovery.Restore() && !recovery.HasResidue, "Maintenance rollback could not complete after access returned");
            var raceNative = new Maintenance(); var raceLedger = new Ledger(); var race = new MaintenancePauseEngine(raceNative, raceLedger);
            raceLedger.AfterWrite = delegate(string raw) { if (raw.Length != 0) raceNative.Value = new MaintenanceValue { Exists = true, Kind = RegistryValueKind.DWord, Value = 9 }; };
            Check(!race.Activate() && raceNative.Writes == 0 && (int)raceNative.Value.Value == 9 && race.HasResidue, "Maintenance write overwrote an owner change during snapshot persistence");
            raceLedger.AfterWrite = null; Check(race.Restore() && (int)raceNative.Value.Value == 9, "Maintenance race cleanup overwrote an external value");
            var preexisting = new Maintenance { Value = new MaintenanceValue { Exists = true, Kind = RegistryValueKind.DWord, Value = 1 } };
            var preexistingEngine = new MaintenancePauseEngine(preexisting, new Ledger());
            Check(preexistingEngine.Activate() && preexistingEngine.Restore() && preexisting.Writes == 0 && preexisting.Value.IsDisabled, "Preexisting maintenance disable was owned/re-enabled");
            var wrongType = new Maintenance { Value = new MaintenanceValue { Exists = true, Kind = RegistryValueKind.String, Value = "0" } };
            Check(!new MaintenancePauseEngine(wrongType, new Ledger()).Activate() && wrongType.Writes == 0, "Unexpected original type was coerced");
            var unavailable = new Maintenance { ThrowRuntime = true };
            Check(new MaintenancePauseEngine(unavailable, new Ledger()).QueryRuntime().State == MaintenanceRuntimeState.Unknown, "Unreadable runtime became idle");
            Check(MaintenancePause.ConfigurationOnly, "Maintenance advertised global task termination");
            const string start = "<Task xmlns='http://schemas.microsoft.com/windows/2004/02/mit/task'><Settings>";
            Check(MaintenanceControl.IsMaintenanceTaskXml(start + "<MaintenanceSettings><Period>P1D</Period></MaintenanceSettings></Settings></Task>"), "Valid maintenance task not detected");
            Check(!MaintenanceControl.IsMaintenanceTaskXml(start + "<IdleSettings/></Settings></Task>"), "Idle task was misclassified as maintenance");
            Check(!MaintenanceControl.IsMaintenanceTaskXml("<Task><Settings><MaintenanceSettings><Period>P1D</Period></MaintenanceSettings></Settings></Task>"), "Wrong schema was trusted");
            bool refused = false; try { MaintenanceControl.IsMaintenanceTaskXml("<!DOCTYPE Task [<!ENTITY ex SYSTEM 'file:///C:/secret'>]><Task>&ex;</Task>"); } catch { refused = true; }
            Check(refused, "Task XML external entity accepted");
            refused = false; try { new MaintenanceControl().TryWrite(1, new MaintenanceValue()); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Selftest native maintenance writes were reachable");
            refused = false; try { new OptionalServiceControl().TryQuery("DiagTrack", out dummy); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Selftest native service reads were reachable");
            refused = false; try { OptionalServiceControl.TryQueryHandleForPrivacy(IntPtr.Zero, out dummy); } catch (InvalidOperationException) { refused = true; }
            Check(refused, "Selftest privacy exact-handle service reads were reachable");
        }
        private static void EnvironmentLifecycleCheck()
        {
            Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest();
            GameMode.EnvironmentReadyForTest = delegate { return true; };
            var native = new Services(); native.Add("WSearch"); native.RejectStop = "WSearch";
            long now = 1;
            var engine = new OptionalServicePauseEngine(native, new Ledger(), delegate { return now; });
            var mode = new GameMode(Path.Combine(Path.GetTempPath(), "Pavise-match-mode-" + Guid.NewGuid().ToString("N")), new SuppressionCore());
            Check(!mode.StepOptionalServicesForTest(true, false, engine), "Failed STOP was published as an applied environment action");
            Check(ActionFailureStore.IsIsolated(PolicyCatalog.KeyPauseServices, "system"), "First failed STOP was not durably isolated");
            for (int i = 0; i < 5; i++) Check(!mode.StepOptionalServicesForTest(true, false, engine), "Cached service apply bypassed isolation");
            Check(native.Stops.Count == 1, "Failed service STOP retried without user permission");
            mode.StepOptionalServicesForTest(false, false, engine);
            mode.StepOptionalServicesForTest(true, false, engine);
            Check(native.Stops.Count == 1, "Preference toggle bypassed service isolation");
            native.RejectStop = null;
            Check(ActionFailureStore.RequestRetry(ActionFailureStore.RecordKey(PolicyCatalog.KeyPauseServices, "system")), "Service manual retry could not be granted");
            Check(mode.StepOptionalServicesForTest(true, false, engine) && native.Stops.Count == 2 && !ActionFailureStore.IsIsolated(PolicyCatalog.KeyPauseServices, "system"), "One verified service retry did not clear isolation");
            native.Values["WSearch"].State = 4;
            now += TimeSpan.FromSeconds(30).Ticks;
            Check(!mode.StepOptionalServicesForTest(true, true, engine) && ActionFailureStore.IsIsolated(PolicyCatalog.KeyPauseServices, "system"), "External service restart was published as still paused");
            Check(native.Stops.Count == 2 && native.Starts.Count == 0, "Restart audit re-enforced STOP or started external service");
            GameMode.EnvironmentReadyForTest = null;
        }
        private static void IndependentPcaCheck()
        {
            // 可选服务暂停只按明确选择执行（3D 自动化重构规格）；场景不再隐含开启，旧的自动总开关迁移为手动。
            foreach (PerformancePreset preset in new[] { PerformancePreset.Standard, PerformancePreset.Handheld, PerformancePreset.Competitive, PerformancePreset.Extreme })
            foreach (bool explicitServices in new[] { false, true })
                foreach (bool oldPca in new[] { false, true })
                {
                    Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest();
                    Settings.Save(AutomaticPolicy.VersionKey, true); Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)preset).ToString());
                    if (explicitServices) Settings.Save(PolicyCatalog.KeyPauseServices, true);
                    Settings.Save(OptionalServicePauseEngine.PcaPreferenceKey, oldPca);
                    GameMode.EnvironmentReadyForTest = delegate { return true; };
                    var native = new Services(); foreach (string name in OptionalServicePauseEngine.Names) native.Add(name); native.Add("PcaSvc");
                    var engine = new OptionalServicePauseEngine(native, new Ledger());
                    string modePath = Path.Combine(Path.GetTempPath(), "Pavise-pca-retired-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(modePath);
                    try
                    {
                        var mode = new GameMode(modePath, new SuppressionCore());
                        string old; Check(Settings.TryLoadStr(OptionalServicePauseEngine.PcaPreferenceKey, out old) && old == "", "Old PCA preference was not cleared");
                        bool baseEnabled = false;
                        typeof(GameMode).GetField("enabled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).SetValue(mode, true);
                        Func<bool> admission = mode.CaptureOptionalServicesAdmissionForTest();
                        Check(admission() == baseEnabled, "Retired PCA preference changed actual service admission");
                        Check(mode.ProbeEffPauseServices == baseEnabled && mode.ProbeOptionalServicesRequested == baseEnabled, "Retired PCA preference revived the independent service policy");
                        Check(mode.StepConfiguredOptionalServicesForTest(false, engine) == baseEnabled, "Base service admission changed after PCA retirement");
                        Check(native.Stops.Count == (baseEnabled ? 7 : 0) && !native.Stops.Contains("PcaSvc") && !native.Queries.Contains("PcaSvc"), "A mode still queried or paused retired PCA");
                        Check(engine.Restore() && !engine.HasResidue && native.Starts.Count == native.Stops.Count, "Base service restoration changed during PCA retirement");
                        Settings.Save(OptionalServicePauseEngine.PcaPreferenceKey, true);
                        Check(mode.ProbeOptionalServicesRequested == baseEnabled && admission() == baseEnabled, "Reintroduced old PCA preference bypassed retirement");
                    }
                    finally { GameMode.EnvironmentReadyForTest = null; Directory.Delete(modePath, true); }
                }
            foreach (bool includeBase in new[] { false, true })
            {
                var native = new Services(); native.Add("WSearch"); native.Add("PcaSvc");
                var engine = new OptionalServicePauseEngine(native, new Ledger());
                Check(engine.Activate(null, true, includeBase) && native.Stops.Count == (includeBase ? 1 : 0), "Legacy PCA API flag changed base selection");
                Check(!native.Queries.Contains("PcaSvc") && !native.Stops.Contains("PcaSvc"), "Legacy API flag still pauses PCA");
                Check(engine.MatchesSelection(false, includeBase) && engine.MatchesSelection(true, includeBase), "Legacy flag changes active-selection identity");
                Check(engine.Restore(), "Base selection did not restore");
            }
            foreach (string state in new[] { "owned", "disabled", "changed", "unreadable", "prepared" })
            {
                var native = new Services(); native.Add("PcaSvc"); native.Values["PcaSvc"].State = 1;
                string original = "v1\t" + native.Boot + "\n" + (state == "prepared" ? "P" : "O") + "\tPcaSvc\t"
                    + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("same:PcaSvc"));
                var ledger = new Ledger { Value = original };
                if (state == "disabled") native.Values["PcaSvc"].StartType = 4;
                if (state == "changed") native.Values["PcaSvc"].Configuration = "user-managed";
                if (state == "unreadable") native.DenyQuery = "PcaSvc";
                var engine = new OptionalServicePauseEngine(native, ledger);
                bool expected = state != "unreadable" && state != "prepared";
                Check(engine.Restore() == expected && native.Stops.Count == 0, "Retired PCA restoration lost its ownership boundary: " + state);
                Check(native.Starts.Count == (state == "owned" ? 1 : 0), "Retired restoration started an externally controlled or unconfirmed PCA service: " + state);
                if (!expected) Check(ledger.Value == original && engine.HasResidue, "Unresolved PCA receipt was discarded");
                if (state == "unreadable")
                {
                    native.DenyQuery = null;
                    Check(engine.Restore() && native.Starts.Count == 1 && !engine.HasResidue, "Old PCA ownership did not restore after read access recovered");
                }
                if (expected)
                {
                    int queries = native.Queries.Count;
                    Check(engine.Activate(null, true, false) && native.Queries.Count == queries && native.Stops.Count == 0,
                        "Restored old PCA ownership was reacquired through a legacy flag");
                }
            }
        }
        private static OptionalServiceSnapshot dummy;
        private static void ServiceStatusLockOrderCheck()
        {
            Settings.UseTransientStoreForCurrentProcess(); ActionFailureStore.ReloadForTest();
            var mode = new GameMode(Path.Combine(Path.GetTempPath(), "Pavise-service-lock-" + Guid.NewGuid().ToString("N")), new SuppressionCore());
            const System.Reflection.BindingFlags instance = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            object modeGate = typeof(GameMode).GetField("sync", instance).GetValue(mode);
            var sharedEngine = (OptionalServicePauseEngine)typeof(OptionalServicePause).GetField("engine", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
            object serviceGate = typeof(OptionalServicePauseEngine).GetField("gate", instance).GetValue(sharedEngine);
            bool admissionTimedOut = false, probeHeld = false, workerJoined = false, queryJoined = false;
            Exception workerError = null, queryError = null;
            string queryState = null;
            using (var engineHeld = new ManualResetEvent(false))
            using (var releaseAdmission = new ManualResetEvent(false))
            using (var queryStarted = new ManualResetEvent(false))
            {
                // Exercise the real public status query against its actual engine lock.
                // The simulated legacy admission is bounded so a regression reports a
                // failure and drains both owned threads instead of hanging the process.
                var worker = new Thread(delegate()
                {
                    try
                    {
                        lock (serviceGate)
                        {
                            engineHeld.Set();
                            if (!releaseAdmission.WaitOne(3000)) throw new TimeoutException("Lock-order admission was never released");
                            if (!Monitor.TryEnter(modeGate, 1000)) admissionTimedOut = true;
                            else Monitor.Exit(modeGate);
                        }
                    }
                    catch (Exception error) { workerError = error; }
                });
                var query = new Thread(delegate()
                {
                    try { queryStarted.Set(); queryState = mode.AutomaticExecutionState(PolicyCatalog.KeyPauseServices); }
                    catch (Exception error) { queryError = error; }
                });
                worker.IsBackground = query.IsBackground = true;
                try
                {
                    worker.Start();
                    Check(engineHeld.WaitOne(3000), "Service fixture did not acquire the production engine lock");
                    query.Start();
                    Check(queryStarted.WaitOne(3000) && SpinWait.SpinUntil(delegate { return (query.ThreadState & ThreadState.WaitSleepJoin) != 0; }, 3000), "UI status query did not contend on the service lock");
                    probeHeld = Monitor.TryEnter(modeGate, 100);
                    if (probeHeld) Monitor.Exit(modeGate);
                }
                finally
                {
                    releaseAdmission.Set();
                    workerJoined = worker.Join(4000);
                    if ((query.ThreadState & ThreadState.Unstarted) == 0) queryJoined = query.Join(4000);
                }
            }
            Check(probeHeld, "UI status query held GameMode.sync while waiting for the service engine");
            Check(workerJoined && queryJoined && workerError == null && queryError == null && !admissionTimedOut,
                "Service admission and UI status query inverted lock order");
            Check(queryState == "", "Lock-order fixture unexpectedly published an applied service state");

            var activeField = typeof(OptionalServicePauseEngine).GetField("active", instance);
            bool previousActive;
            lock (serviceGate) { previousActive = (bool)activeField.GetValue(sharedEngine); activeField.SetValue(sharedEngine, true); }
            try
            {
                Check(mode.AutomaticExecutionState(PolicyCatalog.KeyPauseServices) == "applied", "Service state snapshot was lost outside the GameMode lock");
                ((HashSet<string>)typeof(GameMode).GetField("envFused", instance).GetValue(mode)).Add("services");
                Check(mode.AutomaticExecutionState(PolicyCatalog.KeyPauseServices) == "stopped", "Service active snapshot bypassed persistent isolation priority");
                ((HashSet<string>)typeof(GameMode).GetField("envRestorePending", instance).GetValue(mode)).Add("services");
                Check(mode.AutomaticExecutionState(PolicyCatalog.KeyPauseServices) == "restore-failed", "Service active snapshot bypassed recovery priority");
            }
            finally { lock (serviceGate) activeField.SetValue(sharedEngine, previousActive); }
        }
        [STAThread]
        internal static int Main(string[] args)
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                ServicesCheck(); ServiceAuditCadenceCheck(); ServiceHostCandidateCheck(); MaintenanceCheck(); EnvironmentLifecycleCheck(); IndependentPcaCheck(); ServiceStatusLockOrderCheck();
                Console.WriteLine("PASS MatchSystemControls assertions=" + checks + " services=mocked maintenance=mocked settings=transient application_started=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
