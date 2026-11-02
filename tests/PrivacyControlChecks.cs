#if PAVISE_SELFTEST && PAVISE_PRIVACY_RUNNER
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Application runtime unavailable in privacy checks"); }
    }
    internal static class PrivacyControlChecks
    {
        private static int assertions;
        private sealed class FakeBackend : IPrivacyBackend
        {
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            internal readonly List<string> Writes = new List<string>();
            internal PrivacyPlan Plan;
            internal string FailKey, UnknownKey, RestoreFailKey, UnsupportedKey;
            internal bool PartialFailure;
            internal bool ServiceStartupGuard;
            internal bool RestartOnDisableStartup;
            internal string UnsupportedReadKey, MakeReadUnsupportedAfterWrite;
            internal bool MakeServiceReadUnsupportedAfterStartup;
            internal Action BeforeWrite;
            internal int Inspections;
            internal FakeBackend(int count)
            {
                Plan = new PrivacyPlan { Supported = true, Targets = new PrivacyTarget[count] };
                for (int i = 0; i < count; i++) { string key = "fixture:" + i; Values[key] = i % 2 == 0 ? "absent" : "REG_DWORD:1"; Plan.Targets[i] = new PrivacyTarget { Key = key, Wanted = "REG_DWORD:0" }; }
            }
            public PrivacyPlan Inspect(string action, string selectedTarget) { Inspections++; return Plan; }
            public string Read(string action, string target) { if (UnsupportedReadKey == target) throw new NotSupportedException("Fixture state is transitioning"); if (UnknownKey == target) return null; string value; return Values.TryGetValue(target, out value) ? value : null; }
            public bool Write(string action, string target, string value)
            {
                if (BeforeWrite != null) BeforeWrite();
                Writes.Add(target + "=" + value);
                string canonical = PrivacyControl.CanonicalTarget(target), prefix = target.Substring(0, target.Length - canonical.Length);
                if (UnsupportedKey == target && value == "REG_DWORD:0") throw new NotSupportedException("Fixture target no longer supports mutation");
                bool serviceTarget = canonical == "svc|DiagTrack" || canonical == "svc|PcaSvc";
                bool startupTarget = canonical == "start|DiagTrack" || canonical == "start|PcaSvc";
                if (ServiceStartupGuard && serviceTarget && value.StartsWith("v:4:", StringComparison.Ordinal)
                    && Values[prefix + "start|" + canonical.Substring(4)].StartsWith("c:4:", StringComparison.Ordinal)) return false;
                if (RestoreFailKey == target && value != "REG_DWORD:0") return false;
                if (FailKey == target && value == "REG_DWORD:0") { if (PartialFailure) Values[target] = value; return false; }
                Values[target] = value;
                if (MakeReadUnsupportedAfterWrite == target && value == "REG_DWORD:0") UnsupportedReadKey = target;
                if (MakeServiceReadUnsupportedAfterStartup && startupTarget && value.StartsWith("c:4:", StringComparison.Ordinal)) UnsupportedReadKey = prefix + "svc|" + canonical.Substring(6);
                if (RestartOnDisableStartup && startupTarget && value.StartsWith("c:4:", StringComparison.Ordinal)) Values[prefix + "svc|" + canonical.Substring(6)] = "v:4:identity";
                return true;
            }
        }
        private static void Check(bool ok, string description) { assertions++; if (!ok) throw new InvalidOperationException(description); }
        private static FakeBackend Fresh(int count)
        {
            PrivacyControl.ResetForTest(); Settings.BeforeStringWriteForTest = null;
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
            EnvironmentEnhancementEligibility.SampleForTest = delegate { return new EnvironmentEnhancementEligibility.MemorySnapshot { Installed = 32 * EnvironmentEnhancementEligibility.GiB, Total = 31 * EnvironmentEnhancementEligibility.GiB, Available = 8 * EnvironmentEnhancementEligibility.GiB }; };
            var fake = new FakeBackend(count); PrivacyControl.Bind(fake);
            PrivacyControl.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("10203040-5060-7080-90a0-b0c0d0e0f001"), 1); };
            PrivacyControl.QueueForTest = delegate(WaitCallback work) { work(null); return true; };
            return fake;
        }
        private static bool Set(bool enabled) { return PrivacyControl.Set(PrivacyActionCatalog.NtfsMemory, enabled, "", null); }
        private static void SeedOriginals(string action, FakeBackend fake)
        {
            foreach (PrivacyTarget target in fake.Plan.Targets)
            {
                var receipt = new PerformanceActionStore.Receipt { Id = action, Target = target.Key,
                    Original = fake.Values[target.Key], Expected = target.Wanted, Committed = true };
                Check(Settings.SaveStrDurable(PerformanceActionStore.Slot(action, target.Key), PerformanceActionStore.Pack(receipt)),
                    "Could not seed a historical original: " + action);
                fake.Values[target.Key] = target.Wanted;
            }
        }
        private static void HealthyAndTypedRestoration()
        {
            for (int size = 1; size <= 8; size++)
            {
                FakeBackend fake = Fresh(size);
                Check(Set(true), "healthy work rejected");
                Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "pending-reboot", "healthy state inaccurate");
                Check(PerformanceActionStore.All().Count == size, "missing original snapshot");
                foreach (PrivacyTarget target in fake.Plan.Targets) Check(fake.Values[target.Key] == target.Wanted, "target not configured");
                Check(Set(false), "restore work rejected");
                Check(PerformanceActionStore.All().Count == 0, "restored snapshots not completed");
                for (int i = 0; i < size; i++) Check(fake.Values["fixture:" + i] == (i % 2 == 0 ? "absent" : "REG_DWORD:1"), "registry type or absence not restored");
                Check(!PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).Configured, "restored preference enabled");
            }
        }
        private static void GroupFirstFaultAndOneRetry()
        {
            for (int size = 1; size <= 6; size++) for (int failed = 0; failed < size; failed++)
            {
                FakeBackend fake = Fresh(size); fake.FailKey = "fixture:" + failed; fake.PartialFailure = true;
                Check(Set(true), "fault fixture rejected");
                Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "stopped", "first fault did not stop");
                Check(ActionFailureStore.IsIsolated(PrivacyActionCatalog.NtfsMemory, fake.FailKey), "target isolation missing");
                Check(PerformanceActionStore.All().Count == 0, "verified group rollback left receipt");
                for (int i = 0; i < size; i++) Check(fake.Values["fixture:" + i] == (i % 2 == 0 ? "absent" : "REG_DWORD:1"), "partial group did not roll back");
                for (int i = failed + 1; i < size; i++) Check(!fake.Writes.Contains("fixture:" + i + "=REG_DWORD:0"), "later target executed after first fault");
                int before = fake.Writes.Count;
                Check(Set(true) && fake.Writes.Count == before, "isolated target was automatically retried");
                string key = ActionFailureStore.RecordKey(PrivacyActionCatalog.NtfsMemory, fake.FailKey);
                Check(ActionFailureStore.RequestRetry(key), "manual retry refused");
                Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).RetryAllowed, "manual retry remained inaccessible in environment state");
                Check(Set(true) && ActionFailureStore.IsBlocked(PrivacyActionCatalog.NtfsMemory, fake.FailKey), "failed retry did not consume permission");
                Check(!ActionFailureStore.IsRetryPending(PrivacyActionCatalog.NtfsMemory, fake.FailKey), "retry ticket remained reusable");
                fake.FailKey = null;
                Check(ActionFailureStore.RequestRetry(key) && Set(true), "repaired target could not retry");
                Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "pending-reboot", "repaired group did not complete");
                Check(ActionFailureStore.Records().Count == 0, "successful retry kept fault");
            }
        }
        private static void GuardRails()
        {
            FakeBackend fake = Fresh(2); fake.Plan.Managed = true;
            Check(Set(true) && fake.Writes.Count == 0 && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "managed", "organization policy overwritten");
            fake = Fresh(2); fake.Plan.Supported = false;
            Check(Set(true) && fake.Writes.Count == 0 && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "unsupported", "unsupported plan applied");
            fake = Fresh(3); fake.UnknownKey = "fixture:2";
            Check(Set(true) && fake.Writes.Count == 0 && ActionFailureStore.Records().Count == 0, "unknown original caused mutation or false isolation");
            fake = Fresh(2); fake.Plan.Targets[1].Key = fake.Plan.Targets[0].Key;
            Check(Set(true) && fake.Writes.Count == 0, "duplicate targets allowed");
            fake = Fresh(2); fake.Plan.Targets[1].Wanted = null;
            Check(Set(true) && fake.Writes.Count == 0, "missing desired evidence allowed");
            fake = Fresh(1); fake.Plan.Limited = true;
            Check(Set(true) && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "pending-reboot" && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).Limited, "limited edition claimed full shutdown");
            fake = Fresh(1); fake.Plan.Targets[0].RestartRequired = true;
            Check(Set(true) && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "pending-reboot", "reboot setting claimed runtime effect");
            fake = Fresh(3); fake.UnsupportedKey = "fixture:1";
            Check(Set(true) && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "unsupported" && ActionFailureStore.Records().Count == 0, "normal unsupported target was falsely isolated");
            Check(fake.Values["fixture:0"] == "absent" && PerformanceActionStore.All().Count == 0 && !fake.Writes.Contains("fixture:2=REG_DWORD:0"), "unsupported target left partial changes or ran later targets");
            Check(!PrivacyControl.Set("unknown", true, "", null), "unknown action accepted");
        }
        private static void UnavailableReasonAndRecovery()
        {
            string action = PrivacyActionCatalog.NtfsMemory;
            foreach (string reason in new[] { "system", "edition", "component", "external", "interface", "unimplemented", "unknown", null }) {
                FakeBackend fake = Fresh(1);
                fake.Plan.Supported = false; fake.Plan.UnavailableReason = reason;
                Check(PrivacyControl.Refresh(action, "", null), "Unavailable inspection was rejected");
                PrivacyResult result = PrivacyControl.State(action);
                Check(result.State == "unsupported" && result.UnavailableReason == reason && fake.Writes.Count == 0,
                    "Inspection must preserve the specific unavailable reason without writes");
                result.UnavailableReason = "mutated-copy";
                Check(PrivacyControl.State(action).UnavailableReason == reason, "Returned status must not modify cached reason");
                Check(Set(true) && PrivacyControl.State(action).UnavailableReason == reason && fake.Writes.Count == 0,
                    "Rejected application must preserve the reason without mutation");
            }
            FakeBackend owned = Fresh(1);
            Check(Set(true), "Owned setting fixture failed");
            owned.Plan.Supported = false; owned.Plan.UnavailableReason = "component";
            Check(PrivacyControl.Refresh(action, "", null), "Unavailable owned setting inspection failed");
            PrivacyResult pending = PrivacyControl.State(action);
            Check(pending.Configured && pending.HasRecovery && pending.State == "unsupported" && pending.UnavailableReason == "component",
                "Unavailable owned setting must retain its preference and recovery evidence");
            Check(Set(false) && owned.Values["fixture:0"] == "absent" && PerformanceActionStore.All().Count == 0,
                "Unavailable capability must not block restoring the owned original");
            PrivacyResult restored = PrivacyControl.State(action);
            Check(restored.State == "restore-pending-reboot" && !restored.Configured && !restored.HasRecovery && string.IsNullOrEmpty(restored.UnavailableReason),
                "Successful restoration must clear the obsolete unavailable reason");
        }
        private static void ReceiptAndOwnership()
        {
            FakeBackend fake = Fresh(2);
            Settings.BeforeStringWriteForTest = delegate(string name) { if (name.StartsWith("PerformanceReceipt_")) throw new IOException("receipt persistence failure"); };
            Check(Set(true) && fake.Writes.Count == 0, "native write preceded durable receipt");
            Check(ActionFailureStore.IsIsolated(PrivacyActionCatalog.NtfsMemory, "fixture:0"), "receipt failure not isolated");
            Settings.BeforeStringWriteForTest = null;
            fake = Fresh(2); Check(Set(true), "ownership seed failed");
            fake.Values["fixture:0"] = "REG_SZ:external"; int before = fake.Writes.Count;
            Check(Set(false) && fake.Values["fixture:0"] == "REG_SZ:external", "external change overwritten during restore");
            Check(fake.Writes.Count == before + 1, "external target had native restore write");
            fake = Fresh(1); Check(Set(true), "restore failure seed"); fake.RestoreFailKey = "fixture:0";
            Check(Set(false) && PerformanceActionStore.All().Count == 1 && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "restore-failed", "failed restore lost receipt");
            Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).HasRecovery, "failed restore hid its recovery path");
            fake.RestoreFailKey = null; Check(Set(false) && PerformanceActionStore.All().Count == 0, "pending restore could not retry");
            fake = Fresh(2); fake.FailKey = "fixture:1"; fake.PartialFailure = true; fake.RestoreFailKey = "fixture:0";
            Check(Set(true) && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "restore-failed" && PerformanceActionStore.All().Count == 1, "rollback debt disappeared");
            string key = ActionFailureStore.RecordKey(PrivacyActionCatalog.NtfsMemory, "fixture:1");
            Check(ActionFailureStore.RequestRetry(key), "manual retry debt seed");
            fake.FailKey = null; fake.RestoreFailKey = null;
            Check(Set(true), "recoverable debt retry refused");
            Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "pending-reboot", "recoverable debt did not converge");
        }
        private static void QueueLifecycleAndReload()
        {
            FakeBackend fake = Fresh(1); WaitCallback pending = null;
            PrivacyControl.QueueForTest = delegate(WaitCallback work) { pending = work; return true; };
            Check(Set(true), "async queue refused");
            Check(!PrivacyControl.Set(PrivacyActionCatalog.Ceip, true, "", null) && !PrivacyControl.Refresh(PrivacyActionCatalog.NtfsMemory, ""), "concurrent privacy work admitted");
            Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).Busy, "busy state missing");
            Check(!PrivacyControl.CloseWork(0), "active worker reported drained"); pending(null);
            Check(fake.Writes.Count == 0 && PrivacyControl.CloseWork(0), "shutdown failed to cancel queued work");
            Check(!Set(true), "work admitted after closing");
            fake = Fresh(1); PrivacyControl.QueueForTest = delegate(WaitCallback work) { return false; };
            Check(!Set(true) && !PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).Busy && fake.Writes.Count == 0, "failed queue kept work or mutated");
            fake = Fresh(2); Check(Set(true), "reload seed"); int writes = fake.Writes.Count;
            PrivacyControl.ResetForTest(); PrivacyControl.Bind(fake); ActionFailureStore.ReloadForTest();
            PrivacyControl.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("10203040-5060-7080-90a0-b0c0d0e0f001"), 1); };
            Check(fake.Writes.Count == writes && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).Configured, "reload auto-applied or lost preference");
            Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "unverified", "reload claimed unverified actual configuration");
            PrivacyControl.QueueForTest = delegate(WaitCallback work) { work(null); return true; };
            Check(PrivacyControl.Refresh(PrivacyActionCatalog.NtfsMemory, "") && fake.Writes.Count == writes && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "pending-reboot", "read-only refresh wrote configuration");
            Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory, "other-app").State == "unverified", "stale target state shown for another app");
            string failure; Check(PrivacyControl.RestoreAll(out failure) && PerformanceActionStore.All().Count == 0, "uninstall recovery failed");
            fake = Fresh(1); PrivacyControl.ResetForTest();
            Check(PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "unverified", "cache-only state touched native backend");
            bool threw = false; try { PrivacyControl.ReadTarget(PrivacyActionCatalog.NtfsMemory, "fixture:0"); } catch (InvalidOperationException) { threw = true; }
            Check(threw, "selftest fallback allowed native reads");
        }
        private static void RestartTrackingAndSharedGate()
        {
            FakeBackend fake;
            foreach (string action in new[] { PrivacyActionCatalog.NtfsMemory })
            foreach (string wanted in new[] { "d:1", "d:2" })
            {
                string original = wanted == "d:1" ? "d:2" : "d:1";
                fake = Fresh(1);
                fake.Values["fixture:0"] = original; fake.Plan.Targets[0].Wanted = wanted; fake.Plan.Targets[0].RestartRequired = true;
                Check(PrivacyControl.Set(action, true, "", null) && PrivacyControl.State(action).State == "pending-reboot", "Setting change did not require restart");
                string phase;
                Check(Settings.TryLoadStr(PrivacyControl.PreferencePrefix + action + "Phase", out phase) && phase == "apply",
                    "Apply intent must not depend on the desired registry value: " + wanted);
                Check(PrivacyControl.Refresh(action, "") && PrivacyControl.State(action).State == "pending-reboot", "refresh falsely cleared restart requirement");
                PrivacyControl.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("10203040-5060-7080-90a0-b0c0d0e0f001"), 2); };
                Check(PrivacyControl.Refresh(action, "") && PrivacyControl.State(action).State == "configured", "new boot did not clear pending state");
                Check(PrivacyControl.Set(action, false, "", null) && fake.Values["fixture:0"] == original && PrivacyControl.State(action).State == "restore-pending-reboot", "Setting restoration lost restart tracking");
                Check(Settings.TryLoadStr(PrivacyControl.PreferencePrefix + action + "Phase", out phase) && phase == "restore",
                    "Restore intent must not be mistaken for applying when the original was d:2");
                Check(PrivacyControl.Refresh(action, "") && PrivacyControl.State(action).State == "restore-pending-reboot", "restore refresh falsely cleared reboot state");
                PrivacyControl.BootForTest = delegate { return BootSessionIdentity.Create(new Guid("10203040-5060-7080-90a0-b0c0d0e0f001"), 3); };
                Check(PrivacyControl.Refresh(action, "") && PrivacyControl.State(action).State == "restored", "new boot did not complete restore state");
                fake.Values["fixture:0"] = wanted; // 外部改动不能被旧恢复标记掩盖。
                Check(PrivacyControl.Refresh(action, "") && PrivacyControl.State(action).State == "configured"
                    && !PrivacyControl.State(action).Configured && PerformanceActionStore.All().Count == 0,
                    "old restore boot marker hid the current external setting");
                fake = Fresh(1); fake.Values["fixture:0"] = original; fake.Plan.Targets[0].Wanted = wanted; fake.Plan.Targets[0].RestartRequired = true;
                PrivacyControl.BootForTest = delegate { return null; };
                Check(PrivacyControl.Set(action, true, "", null) && fake.Writes.Count == 0, "unknown boot identity allowed untracked setting mutation");
            }

            fake = Fresh(2); bool mayModify = true;
            PrivacyControl.AdmissionAllowed = delegate { return mayModify; };
            fake.BeforeWrite = delegate { mayModify = false; };
            Check(Set(true) && fake.Values["fixture:0"] == "absent" && fake.Values["fixture:1"] == "REG_DWORD:1", "match-start cancellation left partial changes");
            Check(ActionFailureStore.Records().Count == 0 && PerformanceActionStore.All().Count == 0, "normal cancellation counted as fault or left snapshots");
            Check(!Set(true), "active match admission accepted");
            fake = Fresh(1);
            var started = new ManualResetEvent(false); var completed = new ManualResetEvent(false);
            PrivacyControl.QueueForTest = delegate(WaitCallback work) { return ThreadPool.QueueUserWorkItem(delegate(object ignored) { started.Set(); work(null); }); };
            lock (SystemConfigurationGate.Sync)
            {
                Check(PrivacyControl.Set(PrivacyActionCatalog.NtfsMemory, true, "", delegate { completed.Set(); }), "shared-gate asynchronous work refused");
                Check(started.WaitOne(5000), "worker did not start");
                Check(fake.Writes.Count == 0 && PrivacyControl.Busy, "privacy writes escaped shared system gate");
            }
            Check(completed.WaitOne(5000) && fake.Writes.Count == 1 && !PrivacyControl.Busy, "privacy transaction failed to proceed after gate release");
            started.Dispose(); completed.Dispose();
        }
        private static void ServiceRestoreDependencyWithoutIndex()
        {
            ServiceRestoreDependencyWithoutIndex(PrivacyActionCatalog.Service, "DiagTrack");
            ServiceRestoreDependencyWithoutIndex(PrivacyActionCatalog.Compatibility, "PcaSvc");
        }
        private static void ServiceRestoreDependencyWithoutIndex(string action, string service)
        {
            string runtimeKey = "svc|" + service, startKey = "start|" + service;
            FakeBackend fake = Fresh(2); fake.Values.Clear(); fake.ServiceStartupGuard = true;
            fake.Values[runtimeKey] = "v:4:identity"; fake.Values[startKey] = "c:2:identity";
            fake.Plan.Targets[0] = new PrivacyTarget { Key = runtimeKey, Wanted = "v:1:identity" };
            fake.Plan.Targets[1] = new PrivacyTarget { Key = startKey, Wanted = "c:4:identity" };
            SeedOriginals(action, fake);
            fake.Plan.Supported = false;
            int inspections = fake.Inspections;
            Check(Settings.SaveStrDurable(PerformanceActionStore.ListKey, ""), "erase receipt index fixture failed");
            PerformanceActionStore.Receipt runtime = null;
            foreach (var receipt in PerformanceActionStore.All()) if (receipt.Target == runtimeKey) runtime = receipt;
            int before = fake.Writes.Count;
            Check(runtime != null && PrivacyControl.RestoreReceipt(runtime), "runtime-only restore did not recover startup dependency");
            Check(fake.Writes[before] == startKey + "=c:2:identity" && fake.Writes[before + 1] == runtimeKey + "=v:4:identity", "lost receipt index reversed service dependency");
            Check(PerformanceActionStore.All().Count == 0 && fake.Values[runtimeKey] == "v:4:identity", "service restore left pending originals");
            Check(fake.Inspections == inspections, "Receipt restoration must not inspect current platform support: " + service);

            fake.Plan.Supported = true;
            SeedOriginals(action, fake);
            foreach (var receipt in PerformanceActionStore.All()) if (receipt.Target == runtimeKey) runtime = receipt;
            var replacement = new PerformanceActionStore.Receipt { Id = runtime.Id, Target = runtime.Target,
                Original = runtime.Original, Expected = "v:1:replacement-identity", Committed = true };
            Check(Settings.SaveStrDurable(PerformanceActionStore.Slot(runtime.Id, runtime.Target), PerformanceActionStore.Pack(replacement)), "replace runtime receipt fixture failed");
            before = fake.Writes.Count;
            Check(!PrivacyControl.RestoreReceipt(runtime) && fake.Writes.Count == before, "stale runtime caller changed its startup sibling");
            Check(Settings.SaveStrDurable(PerformanceActionStore.Slot(runtime.Id, runtime.Target), PerformanceActionStore.Pack(runtime)), "restore current runtime fixture failed");
            PerformanceActionStore.Receipt startup = null;
            foreach (var receipt in PerformanceActionStore.All()) if (receipt.Target == startKey) startup = receipt;
            fake.Values[startKey] = "c:3:external-identity"; fake.Values[runtimeKey] = "v:1:external-identity";
            Check(PrivacyControl.RestoreReceipt(runtime) && fake.Values[startKey] == "c:3:external-identity", "external startup replacement was overwritten");
            Check(startup != null && PerformanceActionStore.All().Count == 0, "externally released service ownership not completed");

            fake.Values[startKey] = "c:2:identity"; fake.Values[runtimeKey] = "v:4:identity";
            SeedOriginals(action, fake);
            foreach (var receipt in PerformanceActionStore.All()) if (receipt.Target == runtimeKey) runtime = receipt;
            Check(Settings.SaveStrDurable(PerformanceActionStore.Slot(action, startKey), ""), "missing startup receipt fixture failed");
            Check(!PrivacyControl.RestoreReceipt(runtime) && PerformanceActionStore.All().Count == 1, "disabled startup allowed falsely completed runtime restoration");
            fake.Values[startKey] = "c:2:identity";
            Check(PrivacyControl.RestoreReceipt(runtime), "runtime pending record failed to recover after startup repaired");

            fake = Fresh(2); fake.Values.Clear(); fake.ServiceStartupGuard = true;
            fake.Values[runtimeKey] = "v:4:identity"; fake.Values[startKey] = "c:2:identity";
            fake.Plan.Targets[0] = new PrivacyTarget { Key = runtimeKey, Wanted = "v:1:identity" };
            fake.Plan.Targets[1] = new PrivacyTarget { Key = startKey, Wanted = "c:4:identity" };
            SeedOriginals(action, fake); fake.UnsupportedReadKey = runtimeKey;
            Check(PrivacyControl.Set(action, false, "", null) && PrivacyControl.State(action).State == "restore-failed", "unreadable retired service restoration falsely completed");
            Check(PrivacyControl.State(action).HasRecovery && PerformanceActionStore.All().Count == 1, "unreadable retired service lost restoration debt");
            fake.UnsupportedReadKey = null;
            fake.Plan.Supported = false; inspections = fake.Inspections;
            Check(PrivacyControl.Set(action, false, "", null) && PerformanceActionStore.All().Count == 0, "post-write service recovery debt could not restore later");
            Check(fake.Inspections == inspections && PrivacyControl.State(action).State == "restored",
                "Switching off must restore receipts even when new application is unsupported: " + service);

            fake = Fresh(1); fake.MakeReadUnsupportedAfterWrite = "fixture:0";
            Check(Set(true) && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).State == "restore-failed", "per-target post-write unreadable state treated as unsupported before execution");
            Check(ActionFailureStore.IsIsolated(PrivacyActionCatalog.NtfsMemory, "fixture:0") && PerformanceActionStore.All().Count == 1, "per-target post-write unknown did not preserve recovery and isolation");
            fake.UnsupportedReadKey = null;
            Check(Set(false) && PerformanceActionStore.All().Count == 0, "per-target unknown restoration debt could not recover");
        }
        private static void ScopedTargets()
        {
            ScopedTargets(PrivacyActionCatalog.Service, "DiagTrack");
            ScopedTargets(PrivacyActionCatalog.Compatibility, "PcaSvc");
        }
        private static void ScopedTargets(string action, string service)
        {
            string runtimeKey = "svc|" + service, startKey = "start|" + service;
            string context = "ctx|102030405060708090a0b0c0d0e0f001|system|";
            string other = "ctx|102030405060708090a0b0c0d0e0f002|system|";
            Check(PrivacyControl.CanonicalTarget(context + runtimeKey) == runtimeKey, "scoped target canonicalization lost base target");
            bool rejected = false;
            try { PrivacyControl.CanonicalTarget("ctx|bad|system|" + runtimeKey); } catch (FormatException) { rejected = true; }
            Check(rejected, "malformed machine context accepted");
            FakeBackend fake = Fresh(2); fake.Values.Clear(); fake.ServiceStartupGuard = true;
            fake.Values[context + runtimeKey] = "v:4:identity"; fake.Values[context + startKey] = "c:2:identity";
            fake.Values[other + startKey] = "c:3:foreign";
            fake.Plan.Targets[0] = new PrivacyTarget { Key = context + runtimeKey, Wanted = "v:1:identity" };
            fake.Plan.Targets[1] = new PrivacyTarget { Key = context + startKey, Wanted = "c:4:identity" };
            Check(PerformanceActionStore.Change(action, other + startKey, "c:4:foreign",
                delegate { return fake.Values[other + startKey]; }, delegate(string value) { fake.Values[other + startKey] = value; return true; }, delegate(string value) { return value == "c:4:foreign"; }), "foreign sibling seed failed");
            // Seed the current context directly: a real backend refuses the foreign context,
            // and a new apply correctly stops rather than consuming that portable record.
            SeedOriginals(action, fake);
            Check(Settings.SaveStrDurable(PerformanceActionStore.ListKey, ""), "scoped index loss fixture failed");
            PerformanceActionStore.Receipt runtime = null;
            foreach (var receipt in PerformanceActionStore.All()) if (receipt.Target == context + runtimeKey) runtime = receipt;
            int before = fake.Writes.Count;
            Check(PrivacyControl.RestoreReceipt(runtime), "scoped runtime failed to restore matching startup sibling");
            Check(fake.Writes[before] == context + startKey + "=c:2:identity" && fake.Writes[before + 1] == context + runtimeKey + "=v:4:identity", "scoped service dependency changed context or order");
            Check(fake.Values[other + startKey] == "c:4:foreign" && PerformanceActionStore.All().Count == 1, "scoped service dependency touched another machine's sibling");
        }
        private static void RetiredContracts()
        {
            Check(PrivacyActionCatalog.All.Length == 2 && PrivacyActionCatalog.All[0].Key == PrivacyActionCatalog.NtfsMemory && PrivacyActionCatalog.All[1].Key == PrivacyActionCatalog.BackgroundGlobal,
                "Removed privacy controls remain in the active catalog");
            foreach (PrivacyAction action in PrivacyActionCatalog.Retired)
            {
                FakeBackend fake = Fresh(1);
                Check(Settings.SaveStrDurable(PrivacyControl.PreferencePrefix + action.Key, "1"), "Old preference fixture failed");
                Check(!PrivacyControl.Set(action.Key, true, "", null) && fake.Inspections == 0 && fake.Writes.Count == 0,
                    "Retired setting reached new application: " + action.Key);
                Check(!PrivacyControl.PreferenceEnabled(action.Key) && !PrivacyControl.State(action.Key).Configured,
                    "Old preference re-enabled a retired setting: " + action.Key);
                Check(!PrivacyControl.WriteTarget(action.Key, "fixture:0", "REG_DWORD:0") && fake.Writes.Count == 0,
                    "Direct core mutation bypassed retirement: " + action.Key);
                Check(PrivacyControl.Refresh(action.Key, "") && fake.Inspections == 0
                    && PrivacyControl.State(action.Key).State == "unsupported", "Retired refresh queried a removed component: " + action.Key);
                SeedOriginals(action.Key, fake); fake.Plan.Supported = false;
                Check(PrivacyControl.Set(action.Key, false, "", null) && fake.Values["fixture:0"] == "absent"
                    && PerformanceActionStore.All().Count == 0 && fake.Inspections == 0,
                    "Historical original could not restore without inspecting retired capability: " + action.Key);
            }
            FakeBackend pending = Fresh(1); SeedOriginals(PrivacyActionCatalog.Diagnostics, pending); pending.RestoreFailKey = "fixture:0";
            Check(PrivacyControl.Set(PrivacyActionCatalog.Diagnostics, false, "", null)
                && PrivacyControl.State(PrivacyActionCatalog.Diagnostics).State == "restore-failed"
                && PerformanceActionStore.All().Count == 1, "Retired restore failure discarded ownership");
            pending.RestoreFailKey = null;
            string failure;
            Check(PrivacyControl.RestoreAll(out failure) && failure == null && PerformanceActionStore.All().Count == 0,
                "Retired recovery debt did not clear after the backend recovered");
        }
        private static void MemoryAdmission()
        {
            ulong gib = EnvironmentEnhancementEligibility.GiB;
            ulong[][] cases = {
                new ulong[] { 32*gib,31*gib,8*gib,1 }, new ulong[] { 32*gib,32*gib,8*gib,1 },
                new ulong[] { 32*gib-1,31*gib,8*gib,0 }, new ulong[] { 32*gib,31*gib,8*gib-1,0 },
                new ulong[] { 64*gib,64*gib,8*gib,0 }, new ulong[] { 64*gib,64*gib,16*gib,1 },
                new ulong[] { 64*gib,64*gib,16*gib-1,0 }, new ulong[] { 0,31*gib,8*gib,1 },
                new ulong[] { 0,31*gib-1,8*gib,0 }, new ulong[] { 32*gib,0,0,0 },
                new ulong[] { 32*gib,31*gib,32*gib,0 }, new ulong[] { 32*gib,33*gib,9*gib,0 }
            };
            foreach (ulong[] sample in cases)
                Check(EnvironmentEnhancementEligibility.Fits(sample[0], sample[1], sample[2]) == (sample[3] == 1),
                    "Memory admission boundary was misclassified");
            foreach (bool throws in new[] { false, true })
            {
                FakeBackend fake = Fresh(1);
                EnvironmentEnhancementEligibility.SampleForTest = throws
                    ? (Func<EnvironmentEnhancementEligibility.MemorySnapshot>)delegate { throw new IOException("sample unavailable"); } : null;
                Check(Set(true) && fake.Writes.Count == 0 && PerformanceActionStore.All().Count == 0
                    && PrivacyControl.State(PrivacyActionCatalog.NtfsMemory).UnavailableReason == "memory",
                    "Unmocked or failed memory sample admitted NTFS mutation");
            }
            FakeBackend owned = Fresh(1); Check(Set(true), "Memory restoration seed failed");
            EnvironmentEnhancementEligibility.SampleForTest = null;
            Check(Set(false) && owned.Values["fixture:0"] == "absent" && PerformanceActionStore.All().Count == 0,
                "Memory admission prevented restoring an owned NTFS original");
            FakeBackend changing = Fresh(2);
            changing.BeforeWrite = delegate { EnvironmentEnhancementEligibility.SampleForTest = null; };
            Check(Set(true) && changing.Values["fixture:0"] == "absent" && changing.Values["fixture:1"] == "REG_DWORD:1"
                && PerformanceActionStore.All().Count == 0, "Memory decline between writes left a partial NTFS change");
        }
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                HealthyAndTypedRestoration(); GroupFirstFaultAndOneRetry(); GuardRails(); UnavailableReasonAndRecovery(); ReceiptAndOwnership(); QueueLifecycleAndReload(); RestartTrackingAndSharedGate(); ServiceRestoreDependencyWithoutIndex(); ScopedTargets(); RetiredContracts(); MemoryAdmission();
                Console.WriteLine("PASS privacy control assertions=" + assertions + " settings=transient backend=mocked application_started=false native_writes=false"); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { Settings.BeforeStringWriteForTest = null; EnvironmentEnhancementEligibility.SampleForTest = null; PrivacyControl.ResetForTest(); Settings.UseTransientStoreForCurrentProcess(); }
        }
    }
}
#endif
