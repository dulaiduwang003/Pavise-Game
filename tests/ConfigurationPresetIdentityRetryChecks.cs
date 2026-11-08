using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;

namespace PaviseApp
{
    internal static class PerformancePolicy { internal static string Text(string zh, string en) { return en; } }
    internal static class Settings { internal static int MutationGeneration; }
    internal static class PerformanceTuning { internal static long RecoveryCycleSerial; }
    internal static class Logger { internal static int Calls; internal static void Log(string text) { Calls++; } }
    internal sealed class GameMode { internal bool RunLaboratoryManualWork(Func<bool> work) { throw new Exception("Product work forbidden"); } }
    internal sealed class ConfigurationPresetBackend : IdentityBackend { internal ConfigurationPresetBackend(GameMode mode) { } internal static string SavedIdentityPhase() { return ""; } }
    internal class IdentityBackend : IConfigurationPresetBackend
    {
        internal int Loads, Reads;
        internal bool Fail, Mismatch;
        internal string Identity = "";
        internal Action OnLoad;
        public string LoadIdentity() { Interlocked.Increment(ref Loads); if (OnLoad != null) OnLoad(); if (Fail) throw new InvalidOperationException("fixture read failure"); return Identity; }
        public string Read(ConfigurationPresetEntry entry) { Reads++; return Mismatch ? "different" : entry.DefaultValue; }
        public ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry entry) { throw new Exception("No hardware probe allowed"); }
        public bool Write(ConfigurationPresetEntry entry, string value, Func<bool> cancel, out string detail) { throw new Exception("No write allowed"); }
        public bool SaveIdentity(string identity) { throw new Exception("No write allowed"); }
    }
    internal static class ConfigurationPresetIdentityRetryChecks
    {
        private static int checks;
        private static readonly FieldInfo Refreshing = typeof(ConfigurationPresetRuntime).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance);
        private static void Check(bool value, string text) { checks++; if (!value) throw new Exception(text); }
        private static void Await(ConfigurationPresetRuntime runtime)
        {
            var timer = Stopwatch.StartNew();
            while ((int)Refreshing.GetValue(runtime) != 0 && timer.ElapsedMilliseconds < 3000) Thread.Sleep(1);
            Check((int)Refreshing.GetValue(runtime) == 0, "Refresh worker did not release ownership");
        }
        private static void Poll(ConfigurationPresetRuntime runtime) { var ignored = runtime.CurrentPreset; Await(runtime); }
        private static void BackendFailure()
        {
            long session = 1; int generation = 0; var backend = new IdentityBackend { Fail = true };
            var runtime = new ConfigurationPresetRuntime(backend, () => generation, null, () => session);
            for (int i = 0; i < 1003; i++) { generation++; Poll(runtime); }
            Check(backend.Loads == 3, "Unrelated setting mutations reopened failed identity reads");
            session = 2; Poll(runtime); Check(backend.Loads == 4, "True new session did not resume a bounded identity read");
            session = 1; for (int i = 0; i < 10; i++) Poll(runtime);
            Check(backend.Loads == 4, "Old session rolled back identity failure phase");
            session = 2; Poll(runtime); Poll(runtime); Poll(runtime);
            Check(backend.Loads == 6, "Returning to the same session replenished its budget");
        }
        private static void MaterialSnapshotPhase()
        {
            long session = 1; int generation = 0, snapshots = 0; string configuration = "A";
            var backend = new IdentityBackend { Fail = true };
            var runtime = new ConfigurationPresetRuntime(backend, () => generation, null, () => session, null,
                () => { snapshots++; return configuration; });
            for (int i = 0; i < 3; i++) Poll(runtime);
            Check(backend.Loads == 3 && snapshots == 1, "Failure retries reread unchanged phase snapshot");
            for (int i = 0; i < 1000; i++) { generation++; Poll(runtime); }
            Check(backend.Loads == 3, "Unrelated generation changes refilled failed configuration A");
            configuration = "B"; generation++; for (int i = 0; i < 3; i++) Poll(runtime);
            Check(backend.Loads == 6, "Actual configuration B did not open its bounded phase");
            configuration = "A"; generation++; Poll(runtime);
            Check(backend.Loads == 6, "Configuration A-B-A refilled exhausted A");
            session++; Poll(runtime); Check(backend.Loads == 7, "True session did not reopen actual configuration phase");
        }
        private static void PhaseSnapshotFailure()
        {
            long session = 1; int generation = 0, snapshots = 0; bool fail = true;
            var backend = new IdentityBackend();
            var runtime = new ConfigurationPresetRuntime(backend, () => generation, null, () => session, null,
                () => { snapshots++; if (fail) throw new InvalidOperationException("fixture snapshot"); return "A"; });
            for (int i = 0; i < 1000; i++) { generation++; Poll(runtime); }
            Check(snapshots == 3 && backend.Loads == 0, "Phase snapshot exception was retried outside its budget");
            session++; fail = false; Poll(runtime);
            Check(snapshots == 4 && backend.Loads == 1, "New session did not recover failed phase snapshot");
            fail = true; generation++; for (int i = 0; i < 10; i++) Poll(runtime);
            Check(snapshots == 7, "Completed phase snapshot did not end its own failure episode");
            int reads = 0;
            runtime = new ConfigurationPresetRuntime(backend, () => { reads++; throw new InvalidOperationException("fixture generation"); }, null, () => session);
            for (int i = 0; i < 1000; i++) Poll(runtime);
            Check(reads == 3, "Generation exceptions escaped the phase-source budget");
        }
        private static void CapturedConfigurationFailure()
        {
            long session = 1; int generation = 0; string configuration = "A";
            var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
            var backend = new IdentityBackend { Fail = true, OnLoad = () => { entered.Set(); release.WaitOne(); } };
            var runtime = new ConfigurationPresetRuntime(backend, () => generation, null, () => session, null, () => configuration);
            try
            {
                var ignored = runtime.CurrentPreset; Check(entered.WaitOne(3000), "Captured configuration worker did not start");
                configuration = "B"; generation++; release.Set(); Await(runtime); backend.OnLoad = null;
                for (int i = 0; i < 3; i++) Poll(runtime);
                Check(backend.Loads == 4, "Late A failure was charged to configuration B");
                configuration = "A"; generation++;
                for (int i = 0; i < 10; i++) Poll(runtime);
                Check(backend.Loads == 6, "Returning to A lost its captured first failure");
            }
            finally { release.Set(); Await(runtime); entered.Dispose(); release.Dispose(); }
        }
        private sealed class SameValueApplyBackend : IConfigurationPresetBackend
        {
            internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            internal readonly ManualResetEvent Entered = new ManualResetEvent(false), Release = new ManualResetEvent(false);
            internal int Loads;
            internal bool FailIdentity = true, BlockThird = true;
            internal string Identity;
            internal SameValueApplyBackend() { foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries) Values[entry.Key] = entry.ExtremeValue; }
            public string LoadIdentity() { int call = Interlocked.Increment(ref Loads); if (call == 3 && BlockThird) { Entered.Set(); Release.WaitOne(); } if (FailIdentity) throw new InvalidOperationException("old identity observer"); return Identity; }
            public string Read(ConfigurationPresetEntry entry) { return Values[entry.Key]; }
            public ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry entry) { return ConfigurationPresetAvailability.Yes(); }
            public bool Write(ConfigurationPresetEntry entry, string value, Func<bool> cancel, out string detail) { detail = "same-value fixture"; Values[entry.Key] = value; return true; }
            public bool SaveIdentity(string identity) { Identity = identity; return true; }
        }
        private static void SameValueApplySurvivesOldFailure()
        {
            var backend = new SameValueApplyBackend(); var done = new ManualResetEvent(false);
            // This isolated backend deliberately keeps generation fixed. The separate
            // full-source consumer test uses real Settings, whose identity saves bump it.
            var runtime = new ConfigurationPresetRuntime(backend, () => 0, null, () => 1, null, () => "same-material-Extreme");
            try
            {
                Poll(runtime); Poll(runtime); var ignored = runtime.CurrentPreset;
                Check(backend.Entered.WaitOne(3000), "Third old identity read did not block");
                ConfigurationPresetResult result = null;
                Check(runtime.TryApply(ConfigurationPresetKind.Extreme, () => false, value => { result = value; done.Set(); }), "Same-value apply was not admitted");
                Check(done.WaitOne(3000) && result != null && result.Failed == 0 && result.Identity != null, "Real coordinator did not verify same-value apply");
                Check(runtime.CurrentPreset == ConfigurationPresetKind.Extreme, "Verified apply did not publish its identity");
                backend.Release.Set(); Await(runtime);
                for (int i = 0; i < 1000; i++) Check(runtime.CurrentPreset == ConfigurationPresetKind.Extreme, "Late observer failure erased a newer verified apply");
                Check(backend.Loads == 3, "Same-value apply refilled the old identity failure phase");
            }
            finally { backend.Release.Set(); Await(runtime); backend.Entered.Dispose(); backend.Release.Dispose(); done.Dispose(); }
        }
        private static void VerifiedIdentityBelongsToMaterialPhase()
        {
            int generation = 0; string configuration = "A";
            var backend = new SameValueApplyBackend { BlockThird = false }; var coordinator = new ConfigurationPresetCoordinator(backend);
            foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries) backend.Values[entry.Key] = entry.DefaultValue;
            backend.Identity = "4|default|" + coordinator.Fingerprint();
            var runtime = new ConfigurationPresetRuntime(backend, () => generation, null, () => 1, null, () => configuration);
            var done = new ManualResetEvent(false);
            try
            {
                for (int i = 0; i < 3; i++) Poll(runtime);
                Check(!runtime.CurrentPreset.HasValue && backend.Loads == 3, "Configuration A did not exhaust with unknown identity");
                foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries) backend.Values[entry.Key] = entry.ExtremeValue;
                backend.Identity = "2|extreme|" + coordinator.Fingerprint(); backend.FailIdentity = false;
                configuration = "B"; generation++; Poll(runtime);
                Check(runtime.CurrentPreset == ConfigurationPresetKind.Extreme && backend.Loads == 4, "Configuration B did not publish its verified identity");
                foreach (ConfigurationPresetEntry entry in ConfigurationPresetCatalog.Entries) backend.Values[entry.Key] = entry.DefaultValue;
                backend.Identity = "4|default|" + coordinator.Fingerprint(); configuration = "A"; generation++;
                for (int i = 0; i < 1000; i++) Check(!runtime.CurrentPreset.HasValue, "Exhausted A displayed B's verified preset label");
                Check(backend.Loads == 4, "Invalidating a different material label reopened exhausted A");
                ConfigurationPresetResult result = null;
                Check(runtime.TryApply(ConfigurationPresetKind.Default, () => false, value => { result = value; done.Set(); }), "Same-material explicit apply was not admitted");
                Check(done.WaitOne(3000) && result != null && result.Failed == 0 && result.Identity != null, "Default material was not verified by coordinator apply");
                for (int i = 0; i < 1000; i++) Check(runtime.CurrentPreset == ConfigurationPresetKind.Default, "Exhausted A erased its own newer verified apply");
                Check(backend.Loads == 4, "Verified apply silently refunded A's observer budget");
            }
            finally { backend.Release.Set(); Await(runtime); backend.Entered.Dispose(); backend.Release.Dispose(); done.Dispose(); }
        }
        private static void QueueFailures()
        {
            foreach (bool throws in new[] { false, true })
            {
                long session = 1; int generation = 0, queues = 0; var backend = new IdentityBackend();
                var runtime = new ConfigurationPresetRuntime(backend, () => generation, null, () => session,
                    callback => { queues++; if (throws) throw new InvalidOperationException("fixture enqueue failure"); return false; });
                for (int i = 0; i < 1000; i++) { generation++; Poll(runtime); }
                Check(queues == 3 && backend.Loads == 0, "Queue rejection/exception remained unbounded");
                session = 2; Poll(runtime); Check(queues == 4, "New session did not resume failed queue admission");
            }
        }
        private static void HealthyNegativeAndSuccessEpisode()
        {
            long session = 1; int generation = 0; var backend = new IdentityBackend { Fail = true };
            var runtime = new ConfigurationPresetRuntime(backend, () => generation, null, () => session);
            Poll(runtime); Poll(runtime); backend.Fail = false; Poll(runtime);
            int before = backend.Loads;
            for (int i = 0; i < 1000; i++) Poll(runtime);
            Check(backend.Loads == before && !runtime.CurrentPreset.HasValue, "Healthy null identity was not cached");
            for (int i = 0; i < 20; i++) { generation++; Poll(runtime); }
            Check(backend.Loads == before + 20, "Healthy negative observations were capped");
            backend.Fail = true; generation++;
            for (int i = 0; i < 10; i++) Poll(runtime);
            Check(backend.Loads == before + 23, "Complete healthy observation did not end the prior failure episode");
            backend = new IdentityBackend(); var coordinator = new ConfigurationPresetCoordinator(backend);
            backend.Identity = "4|default|" + coordinator.Fingerprint();
            runtime = new ConfigurationPresetRuntime(backend, () => generation, null, () => session);
            Poll(runtime); Check(runtime.CurrentPreset == ConfigurationPresetKind.Default, "Verified identity was not published");
            backend.Mismatch = true; generation++; Poll(runtime);
            Check(!runtime.CurrentPreset.HasValue, "Successful mismatch observation retained a stale preset");
        }
        private static void SingleFlightAndStaleCompletion()
        {
            long session = 1; var entered = new ManualResetEvent(false); var release = new ManualResetEvent(false);
            var backend = new IdentityBackend(); var coordinator = new ConfigurationPresetCoordinator(backend);
            backend.Identity = "4|default|" + coordinator.Fingerprint();
            backend.OnLoad = () => { entered.Set(); release.WaitOne(); };
            var runtime = new ConfigurationPresetRuntime(backend, () => 0, null, () => Interlocked.Read(ref session));
            try
            {
                var ignored = runtime.CurrentPreset; Check(entered.WaitOne(3000), "Real ThreadPool worker did not start");
                var timer = Stopwatch.StartNew();
                for (int i = 0; i < 1000; i++) ignored = runtime.CurrentPreset;
                Check(backend.Loads == 1 && timer.ElapsedMilliseconds < 500, "UI polling blocked or queued concurrent identity reads");
                Interlocked.Exchange(ref session, 2); release.Set(); Await(runtime);
                // Inspect the stored result without starting the next read.
                var current = typeof(ConfigurationPresetRuntime).GetField("current", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(runtime);
                Check(current == null, "Old-session completion published a stale identity");
                backend.OnLoad = null; Poll(runtime); Check(runtime.CurrentPreset == ConfigurationPresetKind.Default, "New session did not publish its own identity");
            }
            finally { release.Set(); Await(runtime); entered.Dispose(); release.Dispose(); }
        }
        private static int Main()
        {
            try { BackendFailure(); MaterialSnapshotPhase(); PhaseSnapshotFailure(); CapturedConfigurationFailure(); SameValueApplySurvivesOldFailure(); VerifiedIdentityBelongsToMaterialPhase(); QueueFailures(); HealthyNegativeAndSuccessEpisode(); SingleFlightAndStaleCompletion();
                Console.WriteLine("PASS ConfigurationPresetIdentityRetryChecks checks=" + checks + " real_ThreadPool=true native_writes=0 product_started=false"); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
