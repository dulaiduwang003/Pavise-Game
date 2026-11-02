using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

// Production catalog/coordinator/runtime/adapter and GameMode preset entry point are compiled unchanged.
// Only surrounding OS/transaction services below are replaced. This executable has no native writes,
// registry handles, product workers or UI. Existing transaction suites test the mocked services separately.
namespace PaviseApp
{
    internal static class PresetProductionProbe
    {
        internal static readonly List<string> Calls = new List<string>();
        internal static readonly Dictionary<string, string> States = new Dictionary<string, string>();
        internal static readonly HashSet<string> MissingTargets = new HashSet<string>();
        internal static readonly HashSet<string> NvResidue = new HashSet<string>();
        internal static readonly object Gate = new object();
        internal static bool Services = true, Ntfs = true, Registry = true, Nvidia = true, Gpu = true,
            CpuIdle = true, CpuIdleDisable = true, Privacy = true, Vbs = true, VbsFailure, AudioFailure, RestoreFailure, Elevated = true,
            EnvironmentQueueBusy, PrivacyFailure, PrivacyBusy;
        internal static int OsBuild = 22631, NativeQueries;
        internal static string FailEnvironment;
        internal static string FailAction;
        internal static string RejectEnvironmentQueue;
        internal static ManualResetEvent AsyncBlock;
        internal static void Record(string value) { lock (Gate) Calls.Add(value); }
        internal static bool Saw(string value) { lock (Gate) return Calls.Contains(value); }
        internal static void Reset()
        {
            lock (Gate) { Calls.Clear(); States.Clear(); MissingTargets.Clear(); NvResidue.Clear(); }
            Services = Ntfs = Registry = Nvidia = Gpu = CpuIdle = CpuIdleDisable = Privacy = Vbs = Elevated = true;
            VbsFailure = AudioFailure = RestoreFailure = EnvironmentQueueBusy = PrivacyFailure = PrivacyBusy = false;
            OsBuild = 22631; NativeQueries = 0; FailEnvironment = FailAction = RejectEnvironmentQueue = null; AsyncBlock = null;
            Settings.Reset(); LaboratoryGpu.HasResidue = false;
            PerformanceActionStore.Records.Clear();
        }
        internal static void Environment(string key, bool on, Action completed)
        {
            Record("environment:" + key + ":" + on);
            ThreadPool.QueueUserWorkItem(delegate {
                if (AsyncBlock != null) AsyncBlock.WaitOne();
                lock (Gate) States[key] = on && key == FailEnvironment ? "failed" : on ? "configured" : "restored";
                if (completed != null) completed();
            });
        }
    }

    internal static class ConfigurationPresetProductionChecks
    {
        private static int assertions;
        private static void Check(bool condition, string reason)
        { assertions++; if (!condition) throw new InvalidOperationException(reason); }
        private static ConfigurationPresetEntry Entry(string key)
        { return Array.Find(ConfigurationPresetCatalog.Entries, e => e.Key == key); }
        private static ConfigurationPresetBackend Backend() { return new ConfigurationPresetBackend(new GameMode()); }
        private static bool Write(string key, string value)
        { string detail; return Backend().Write(Entry(key), value, () => false, out detail); }
        private static void CheckPreset(ConfigurationPresetKind preset)
        {
            PresetProductionProbe.Reset();
            Settings.SaveStrDurable("PerformancePreset", "1"); Settings.SaveStrDurable("PowerPlanOn", "0");
            var backend = Backend();
            var result = new ConfigurationPresetCoordinator(backend).Apply(preset, () => false);
            Check(result.Failed == 0 && result.Items.Count == 34, "Production adapter did not process all 34 entries: " + result.Summary);
            foreach (var entry in ConfigurationPresetCatalog.Entries)
                Check(backend.Read(entry) == entry.Value(preset), "Adapter persisted wrong preset value: " + entry.Key);
            Check(Settings.Data["PerformancePreset"] == "1" && Settings.Data["PowerPlanOn"] == "0", "Preset changed strategy or managed power choice");
            Check(PresetProductionProbe.Saw("action:AutoOpenGlThreadsV1:False"), "OpenGL OFF bypassed the action service");
            Check(PresetProductionProbe.Saw("audio:restore"), "Low-buffer audio OFF failed to drain its previous session");
            Check(PresetProductionProbe.Saw("environment:EnvQuantumProfileV1:True"), "Foreground quantum did not use the environment transaction");
            Check(PresetProductionProbe.Saw("privacy:EnvPrivacyBackgroundGlobalV1:" + (preset == ConfigurationPresetKind.Extreme)), "UWP did not use the privacy transaction");
        }
        private static void CheckUnknownAndSupport()
        {
            PresetProductionProbe.Reset();
            PresetProductionProbe.Nvidia = PresetProductionProbe.Gpu = PresetProductionProbe.CpuIdle = PresetProductionProbe.Privacy = false;
            var result = new ConfigurationPresetCoordinator(Backend()).Apply(ConfigurationPresetKind.Extreme, () => false);
            foreach (string key in new[] { "NvMaxPerf", "NvLowLat", "LabCpuIdleStrategyV1", "EnvPrivacyNtfsMemoryV1" })
            {
                Check(result.Items.Single(i => i.Key == key).Status == ConfigurationPresetItemStatus.Skipped, "Unsupported feature was not skipped: " + key);
                Check(!ConfigurationPresetEntry.IsEnabled(Backend().Read(Entry(key))), "Unsupported feature retained ON: " + key);
            }
            foreach (string key in new[] { "LabGpuClockMaxV1", "LabGpuPowerMaxV1" })
                Check(ConfigurationPresetEntry.IsEnabled(Backend().Read(Entry(key)))
                    && result.Items.Single(i => i.Key == key).Status == ConfigurationPresetItemStatus.Applied,
                    "Extreme must save the same GPU session request as the manual switch: " + key);
            PresetProductionProbe.Reset(); Settings.Data["GmAwake"] = "not-a-boolean";
            result = new ConfigurationPresetCoordinator(Backend()).Apply(ConfigurationPresetKind.Default, () => false);
            Check(result.Items.Single(i => i.Key == "GmAwake").Status == ConfigurationPresetItemStatus.Skipped,
                "Unreadable saved preference was overwritten");
            Check(Settings.Data["GmAwake"] == "not-a-boolean", "Unreadable setting did not stay unchanged");
            PresetProductionProbe.Reset(); PresetProductionProbe.CpuIdleDisable = false;
            Check(!Backend().Inspect(Entry(LaboratoryPower.IdleKey)).Available,
                "Threshold idle policy admitted an unreadable Disable setting");
        }
        private static void CheckVbsAndGroup()
        {
            PresetProductionProbe.Reset(); PresetProductionProbe.Vbs = false;
            Check(Write(PerformanceTuning.EnvironmentBundleKey, "1"), "Unsupported VBS should not fail supported bundle members");
            Check(!PresetProductionProbe.Saw("vbs:True") && !Settings.LoadCached("EnvVbsDisabledV1", false), "Unverified VBS received an ON request");
            Check(Settings.LoadCached("EnvCpuMitigationsV1", false), "Skipping VBS skipped an independent supported member");
            PresetProductionProbe.Reset(); PresetProductionProbe.VbsFailure = true;
            Check(!Write(PerformanceTuning.EnvironmentBundleKey, "1"), "Failed VBS was reported configured");
            Check(!Settings.LoadCached("EnvVbsDisabledV1", false) && PresetProductionProbe.Saw("vbs:False"), "Failed VBS left ON intent or did not restore");
            PresetProductionProbe.Reset(); PresetProductionProbe.FailEnvironment = "EnvCpuMitigationsV1";
            Check(!Write(PerformanceTuning.EnvironmentBundleKey, "1"), "Failed environment member was reported configured");
            Check(!Settings.LoadCached("EnvCpuMitigationsV1", false) && PresetProductionProbe.Saw("environment:EnvCpuMitigationsV1:False"), "Failed member retained ON intent");
            PresetProductionProbe.Reset(); PresetProductionProbe.MissingTargets.Add("EnvAudioIdleV1");
            Check(Write(PerformanceTuning.EnvironmentBundleKey, "1"), "No audio target should be skipped, not fail independent settings");
            Check(!Settings.LoadCached("EnvAudioIdleV1", false) && !PresetProductionProbe.Saw("environment:EnvAudioIdleV1:True"), "Missing audio target was requested ON");
        }
        private static void CheckRestoration()
        {
            PresetProductionProbe.Reset(); PresetProductionProbe.NvResidue.Add("pstate"); PresetProductionProbe.NvResidue.Add("forcep2");
            Check(Write("NvMaxPerf", "0"), "NVIDIA maximum performance OFF failed");
            Check(PresetProductionProbe.Saw("nv:restore:pstate") && PresetProductionProbe.Saw("nv:restore:forcep2") && PresetProductionProbe.NvResidue.Count == 0,
                "NVIDIA OFF only saved preference without restoring its driver entries");
            PresetProductionProbe.Reset(); PresetProductionProbe.NvResidue.Add("shadercache"); PresetProductionProbe.NvResidue.Add("shaderon");
            Check(Write("NvShaderCache", "0") && PresetProductionProbe.NvResidue.Count == 0, "Shader-cache OFF left its driver entries");
            PresetProductionProbe.Reset(); PresetProductionProbe.RestoreFailure = true; PresetProductionProbe.NvResidue.Add("pstate");
            Check(!Write("NvMaxPerf", "0") && PresetProductionProbe.NvResidue.Contains("pstate"), "NVIDIA restore failure was reported complete or recovery receipt discarded");
            PresetProductionProbe.Reset(); PresetProductionProbe.AudioFailure = true;
            Check(!Write(LaboratoryAudio.Key, "0") && PresetProductionProbe.Saw("audio:restore"), "Audio session release failure was reported complete");
            PresetProductionProbe.Reset(); PerformanceActionStore.Records.Add(new PerformanceActionStore.Receipt { Id = "AutoOpenGlThreadsV1" });
            PerformanceActionStore.Records.Add(new PerformanceActionStore.Receipt { Id = "UnrelatedOwnedAction" });
            Check(Write("AutoOpenGlThreadsV1", "0") && PerformanceActionStore.Records.Count == 1 && PerformanceActionStore.Records[0].Id == "UnrelatedOwnedAction",
                "Explicit OFF failed its own receipt or touched an unrelated receipt");
            PresetProductionProbe.Reset(); PresetProductionProbe.RestoreFailure = true; PerformanceActionStore.Records.Add(new PerformanceActionStore.Receipt { Id = "AutoOpenGlThreadsV1" });
            Check(!Write("AutoOpenGlThreadsV1", "0") && PerformanceActionStore.Records.Count == 1,
                "Failed action OFF reported success or lost the recovery receipt");
            PresetProductionProbe.Reset(); PresetProductionProbe.FailAction = "AutoGameMitigationsV1";
            Check(!Write("AutoGameMitigationsV1", "1") && !Settings.LoadCached("AutoGameMitigationsV1", false),
                "Unsupported action left enabled intent after application failure");
            PresetProductionProbe.Reset(); Settings.Reject = "GmAwake";
            Check(!Write("GmAwake", "1") && !Settings.Data.ContainsKey("GmAwake"), "Rejected setting write reported success");
        }
        private static void CheckFailedAsyncRequests()
        {
            PresetProductionProbe.Reset(); PresetProductionProbe.FailEnvironment = PerformanceActionCatalog.Quantum;
            Check(!Write(PerformanceActionCatalog.Quantum, "38"), "Failed quantum transaction was reported complete");
            Check(!Settings.LoadCached(PerformanceActionCatalog.Quantum, false) && PresetProductionProbe.Saw("environment:" + PerformanceActionCatalog.Quantum + ":False"),
                "Failed quantum transaction retained ON or skipped restoration");
            PresetProductionProbe.Reset(); PresetProductionProbe.PrivacyFailure = true;
            const string privacy = "EnvPrivacyNtfsMemoryV1";
            Check(!Write(privacy, "1"), "Failed privacy transaction was reported complete");
            Check(!PrivacyControl.PreferenceEnabled(privacy) && PresetProductionProbe.Saw("privacy:" + privacy + ":False"),
                "Failed privacy transaction retained ON or skipped restoration");
            PresetProductionProbe.Reset(); PresetProductionProbe.RejectEnvironmentQueue = "EnvCpuMitigationsV1";
            Check(!Write(PerformanceTuning.EnvironmentBundleKey, "1"), "Failed environment queue was reported complete");
            Check(!Settings.LoadCached("EnvCpuMitigationsV1", false) && PresetProductionProbe.Saw("environment:EnvCpuMitigationsV1:False"),
                "Rejected nonworking environment queue retained its previously saved ON");
            PresetProductionProbe.Reset(); PresetProductionProbe.RejectEnvironmentQueue = "EnvCpuMitigationsV1"; PresetProductionProbe.EnvironmentQueueBusy = true;
            Settings.SaveDurable("EnvCpuMitigationsV1", true);
            Check(!Write(PerformanceTuning.EnvironmentBundleKey, "1") && Settings.LoadCached("EnvCpuMitigationsV1", false)
                && !PresetProductionProbe.Saw("environment:EnvCpuMitigationsV1:False"), "Rejected busy queue cancelled another environment transaction");
            PresetProductionProbe.Reset(); PresetProductionProbe.RejectEnvironmentQueue = PerformanceActionCatalog.Quantum; PresetProductionProbe.EnvironmentQueueBusy = true;
            Settings.SaveDurable(PerformanceActionCatalog.Quantum, true);
            Check(!Write(PerformanceActionCatalog.Quantum, "38") && Settings.LoadCached(PerformanceActionCatalog.Quantum, false)
                && !PresetProductionProbe.Saw("environment:" + PerformanceActionCatalog.Quantum + ":False"), "Rejected busy quantum queue cancelled another transaction");
            PresetProductionProbe.Reset(); PresetProductionProbe.PrivacyBusy = true;
            Settings.SaveDurable(PrivacyControl.PreferencePrefix + privacy, true);
            Check(!Write(privacy, "1") && PrivacyControl.PreferenceEnabled(privacy) && !PresetProductionProbe.Saw("privacy:" + privacy + ":False"),
                "Rejected busy privacy queue cancelled another transaction");
        }
        private static void CheckAsyncAndIdentity()
        {
            PresetProductionProbe.Reset();
            using (var release = new ManualResetEvent(false))
            using (var entered = new ManualResetEvent(false))
            {
                PresetProductionProbe.AsyncBlock = release;
                bool result = false;
                var worker = new Thread(delegate() { entered.Set(); var mode = new GameMode(); result = mode.RunLaboratoryManualWork(() => {
                    string detail; return new ConfigurationPresetBackend(mode).Write(Entry(PerformanceTuning.EnvironmentBundleKey), "1", () => false, out detail);
                }); });
                worker.IsBackground = true; worker.Start(); entered.WaitOne();
                Thread.Sleep(50); Check(worker.IsAlive, "Adapter abandoned in-flight callback");
                release.Set(); Check(worker.Join(2000) && result, "Outer driver/timer gates deadlocked a transaction callback");
            }
            PresetProductionProbe.Reset();
            using (var release = new ManualResetEvent(false))
            using (var completed = new ManualResetEvent(false))
            {
                PresetProductionProbe.AsyncBlock = release;
                var mode = new GameMode();
                var runtime = new ConfigurationPresetRuntime(Backend(), () => Settings.MutationGeneration, mode.RunLaboratoryManualWork);
                ConfigurationPresetResult outcome = null;
                Check(runtime.TryApply(ConfigurationPresetKind.Default, () => false, result => { outcome = result; completed.Set(); }), "Preset runtime rejected initial work");
                DateTime deadline = DateTime.UtcNow.AddSeconds(2);
                while (!PresetProductionProbe.Saw("environment:EnvCpuMitigationsV1:True") && DateTime.UtcNow < deadline) Thread.Sleep(5);
                Check(runtime.Busy && !runtime.TryApply(ConfigurationPresetKind.Extreme, () => false, result => {}), "Preset runtime admitted duplicate work");
                runtime.Cancel(); release.Set();
                Check(completed.WaitOne(2000) && outcome != null && outcome.Cancelled && !runtime.Busy, "Cancellation abandoned or mislabeled its in-flight transaction");
                Check(!PresetProductionProbe.Saw("environment:EnvQuantumProfileV1:True") && Settings.LoadStr(ConfigurationPresetRuntime.IdentityKey, "") == "",
                    "Cancelled preset changed later options or published a completed identity");
            }
            PresetProductionProbe.Reset();
            Check(ConfigurationPresetRuntime.CurrentFromSettings() == null && PresetProductionProbe.NativeQueries == 0, "Current identity probes native capabilities");
            var state = new ConfigurationPresetCoordinator(Backend());
            Check(state.Apply(ConfigurationPresetKind.Default, () => false).Failed == 0, "Default state could not be saved");
            PresetProductionProbe.NativeQueries = 0;
            Check(ConfigurationPresetRuntime.CurrentFromSettings() == ConfigurationPresetKind.Default && PresetProductionProbe.NativeQueries == 0,
                "Current preset signature is stale or performs capability probes");
            Settings.SaveDurable("GmAwake", false);
            Check(ConfigurationPresetRuntime.CurrentFromSettings() == null, "Manual covered change did not invalidate saved preset identity");
        }
        public static int Main()
        {
            try {
                CheckPreset(ConfigurationPresetKind.Default); CheckPreset(ConfigurationPresetKind.Extreme);
                CheckUnknownAndSupport(); CheckVbsAndGroup(); CheckRestoration(); CheckFailedAsyncRequests(); CheckAsyncAndIdentity();
                Console.WriteLine("PASS production preset adapter assertions=" + assertions + " real_adapter=true transaction_services=fake native_writes=false product_workers=false"); return 0;
            } catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }

    internal static class Settings
    {
        internal static readonly Dictionary<string, string> Data = new Dictionary<string, string>();
        internal static string Reject;
        internal static int MutationGeneration;
        internal static void Reset() { Data.Clear(); Reject = null; MutationGeneration++; }
        internal static bool SaveStrDurable(string key, string value) { if (key == Reject) return false; Data[key] = value; MutationGeneration++; return true; }
        internal static bool SaveDurable(string key, bool value) { return SaveStrDurable(key, value ? "1" : "0"); }
        internal static bool TryLoadStoredBool(string key, out bool value) { string raw; value = false; if (!Data.TryGetValue(key, out raw) || (raw != "1" && raw != "0")) return false; value = raw == "1"; return true; }
        internal static bool TryLoadStr(string key, out string value) { if (!Data.TryGetValue(key, out value)) value = ""; return true; }
        internal static string LoadStr(string key, string fallback) { string raw; return Data.TryGetValue(key, out raw) ? raw : fallback; }
        internal static bool LoadCached(string key, bool fallback) { bool raw; return TryLoadStoredBool(key, out raw) ? raw : fallback; }
    }
    internal static class PerformancePolicy { internal static string Text(string zh, string en) { return en; } internal static bool IsIntent(string key) { return false; } }
    internal enum PolicyValueKind { Bool, Choice, Enum }
    internal sealed class PolicyItem { internal PolicyValueKind Kind; internal string Fallback; internal string[] Choices; }
    internal static class PolicyCatalog
    {
        internal const string KeyPreset = "PerformancePreset", KeyPowerPlan = "PowerPlanOn";
        internal static PolicyItem ItemOf(string key) {
            if (key == "NvLowLat") return new PolicyItem { Kind = PolicyValueKind.Choice, Fallback = "off", Choices = new[] { "off", "on" } };
            if (key == ThreadBoostSelection.Key) return new PolicyItem { Kind = PolicyValueKind.Choice, Fallback = "on", Choices = new[] { "off", "on" } };
            string[] keys = { "GmBoost", "GmAwake", "GmSuppress", "GmGpuDemote", "GmPauseUpdate", "GmPauseDl", "GmPauseMaintenanceV1", "GmDwmBoost", "GmIntelEnduranceV1", "GmEnglishInputV1", "NvMaxPerf", "NvShaderCache", KeyPreset, KeyPowerPlan };
            return Array.IndexOf(keys, key) < 0 ? null : new PolicyItem { Kind = PolicyValueKind.Bool, Fallback = key == "GmBoost" || key == "GmAwake" || key == "GmSuppress" || key == "GmPauseDl" || key == "GmPauseMaintenanceV1" ? "1" : "0" };
        }
    }
    internal static class ThreadBoostSelection { internal const string Key = "GmThreadBoostStrategyV1"; internal static bool TryReadGlobal(out string value) { value = Settings.LoadStr(Key, "on"); return value == "on" || value == "off"; } internal static bool Save(string value) { return Settings.SaveStrDurable(Key, value); } }
    internal static class Native { internal static bool IsElevated() { PresetProductionProbe.NativeQueries++; return PresetProductionProbe.Elevated; } internal static int OsBuild() { PresetProductionProbe.NativeQueries++; return PresetProductionProbe.OsBuild; } internal static int D3DKMTGetProcessSchedulingPriorityClass(IntPtr handle, out int value) { value = 0; return 0; } }
    internal static class NvApi { internal static bool Available { get { return PresetProductionProbe.Nvidia; } } internal static bool SupportsDwordSetting(uint id) { return PresetProductionProbe.Nvidia; } }
    internal static class NvDrsTweaks {
        internal static readonly string[] UltraKeys = { "prerender", "ullenable", "lowlatcpl" };
        internal static uint SettingIdOf(string key) { return 1; }
        internal static void RestoreKind(string key) { PresetProductionProbe.Record("nv:restore:" + key); if (!PresetProductionProbe.RestoreFailure) PresetProductionProbe.NvResidue.Remove(key); }
        internal static void RestoreKinds(string[] keys) { foreach (string key in keys) RestoreKind(key); }
        internal static bool HasSnapshotFor(string key) { return PresetProductionProbe.NvResidue.Contains(key); }
        internal static bool PresetKindsRestored(string[] keys) { foreach (string key in keys) if (HasSnapshotFor(key)) return false; return true; }
    }
    internal static class GpuPrefStage { internal static bool Supported { get { return true; } } internal static bool Restore() { PresetProductionProbe.Record("gpu:restore"); return !PresetProductionProbe.RestoreFailure; } }
    internal sealed class AppGpuPreferences { internal static AppGpuPreferences Shared = new AppGpuPreferences(); internal bool Supported { get { return true; } } internal static bool RestoreAll() { PresetProductionProbe.Record("appgpu:restore"); return !PresetProductionProbe.RestoreFailure; } }
    internal static class GlobalTimerResTweak { internal static bool OwnsState; }
    internal static class TimerTickTweak { internal static bool OwnsState; internal static bool CanReadForPreset() { return true; } }
    internal static class LaboratoryClockRepair { internal static bool HasReceipt; }
    internal static class LaboratoryGpu { internal const string ClockKey = "LabGpuClockMaxV1", PowerKey = "LabGpuPowerMaxV1"; internal static bool HasResidue; internal static bool CanConfigure(string key, out string reason) { reason = "unsupported GPU"; return PresetProductionProbe.Gpu; } internal static void Update(bool power, bool clocks) { PresetProductionProbe.Record("gpulab:restore"); if (!PresetProductionProbe.RestoreFailure) HasResidue = false; } }
    internal static class LaboratoryPower { internal const string EppKey = "LabEppYieldV1", IdleKey = "LabCpuIdleStrategyV1"; internal static Guid Promote = Guid.NewGuid(), Scaling = Guid.NewGuid(), Disable = Guid.NewGuid(); internal static bool Restore() { PresetProductionProbe.Record("power:restore"); return !PresetProductionProbe.RestoreFailure; } }
    internal static class LaboratoryAudio { internal const string Key = "LabAudioLowBufferV1"; internal static bool Restore() { PresetProductionProbe.Record("audio:restore"); return !PresetProductionProbe.AudioFailure; } }
    internal sealed class LaboratoryEppAvailability { internal bool Available = false; }
    internal static class LaboratoryEppEligibility { internal static LaboratoryEppAvailability Current = new LaboratoryEppAvailability(); }
    internal static class PowerPlan { internal sealed class NativeLaboratoryPower { internal Guid ActivePlan() { return Guid.NewGuid(); } internal int PlanStatus(Guid plan) { return 1; } internal uint? Read(Guid plan, Guid setting) { return PresetProductionProbe.CpuIdle && (setting != LaboratoryPower.Disable || PresetProductionProbe.CpuIdleDisable) ? (uint?)1 : null; } } }
    internal static class QuantumProfiles { internal static readonly int[] Values = { 0, 38, 18 }; }
    internal static class VbsTweak { internal static bool CanConfigureForPreset(out string reason) { reason = "VBS unknown"; return PresetProductionProbe.Vbs; } }
    internal static class SystemConfigurationGate { internal static readonly object Sync = new object(); }
    internal static class PerformanceActionCatalog { internal const string Quantum = "EnvQuantumProfileV1"; internal static PerformanceAction Find(string key) { return new PerformanceAction { Title = key }; } }
    internal sealed class PerformanceAction { internal string Title; }
    internal static class PerformanceActionBackend { internal static string[] EnvironmentTargets(string key) { return PresetProductionProbe.MissingTargets.Contains(key) ? new string[0] : new[] { "fake-target" }; } }
    internal static class PerformanceActionStore { internal sealed class Receipt { internal string Id; } internal static readonly List<Receipt> Records = new List<Receipt>(); internal static List<Receipt> All() { return new List<Receipt>(Records); } }
    internal sealed class PresetActionCoordinator { internal bool DrainForNvHandoff(int timeout) { PresetProductionProbe.Record("actions:drain"); return true; } }
    internal static class PerformanceTuning
    {
        internal const string EnvironmentBundleKey = "EnvPerformanceBundleV1", StorageGroupKey = "EnvStoragePowerGroupV1";
        internal static bool EnvironmentWorkClosing;
        internal static List<string> StorageMembers() { return new List<string> { "EnvStorageIdleV1", "EnvStorageRuntimeV1", "EnvStorageD3ColdV1" }; }
        internal static bool SetEnvironment(string key, bool on, Action done) {
            if (on && key == PresetProductionProbe.RejectEnvironmentQueue) {
                if (!PresetProductionProbe.EnvironmentQueueBusy) Settings.SaveDurable(key, on);
                lock (PresetProductionProbe.Gate) PresetProductionProbe.States[key] = PresetProductionProbe.EnvironmentQueueBusy ? "working" : "failed";
                return false;
            }
            if (!Settings.SaveDurable(key, on)) return false; PresetProductionProbe.Environment(key, on, done); return true;
        }
        internal static bool SetQuantumProfile(int index, Action done) { Settings.SaveStrDurable("EnvQuantumSelectedProfileV1", "38"); return SetEnvironment(PerformanceActionCatalog.Quantum, index > 0, done); }
        internal static string State(string key) { lock (PresetProductionProbe.Gate) { string value; return PresetProductionProbe.States.TryGetValue(key, out value) ? value : "off"; } }
        internal static string Label(string value) { return value; }
        internal static bool QueueEnvironmentWork(WaitCallback work) { return ThreadPool.QueueUserWorkItem(work); }
        internal static bool ApplyEnvironmentVbs(bool on) { PresetProductionProbe.Record("vbs:" + on); bool ok = !on || !PresetProductionProbe.VbsFailure; lock (PresetProductionProbe.Gate) PresetProductionProbe.States["EnvVbsDisabledV1"] = ok ? on ? "configured" : "restored" : "failed"; return ok; }
        internal static bool RestoreWhere(Predicate<PerformanceActionStore.Receipt> filter) { PresetProductionProbe.Record("actions:restore"); if (PresetProductionProbe.RestoreFailure) return false; PerformanceActionStore.Records.RemoveAll(filter); return true; }
    }
    internal sealed class PrivacyTarget { }
    internal sealed class PrivacyPlan { internal bool Supported, Managed; internal PrivacyTarget[] Targets; internal string Note, UnavailableReason; }
    internal sealed class PrivacyResult { internal string State, Detail = ""; internal bool HasRecovery, Busy; }
    internal sealed class PrivacyNativeBackend { internal PrivacyPlan Inspect(string key, string target) { return new PrivacyPlan { Supported = PresetProductionProbe.Privacy, Targets = PresetProductionProbe.Privacy ? new[] { new PrivacyTarget() } : new PrivacyTarget[0], Note = "unsupported privacy" }; } }
    internal static class PrivacyControl {
        internal const string PreferencePrefix = "PrivacySetting_";
        internal static bool IsAction(string key) { return key == "EnvPrivacyNtfsMemoryV1" || key == "EnvPrivacyBackgroundGlobalV1"; }
        internal static bool Set(string key, bool on, string target, Action done) {
            PresetProductionProbe.Record("privacy:" + key + ":" + on);
            if (PresetProductionProbe.PrivacyBusy) return false;
            Settings.SaveDurable(PreferencePrefix + key, on);
            lock (PresetProductionProbe.Gate) PresetProductionProbe.States["privacy:" + key] = on && PresetProductionProbe.PrivacyFailure ? "failed" : on ? "configured" : "restored";
            ThreadPool.QueueUserWorkItem(delegate { done(); }); return true;
        }
        internal static PrivacyResult State(string key) { string state; lock (PresetProductionProbe.Gate) PresetProductionProbe.States.TryGetValue("privacy:" + key, out state); return new PrivacyResult { State = state ?? "configured", Busy = PresetProductionProbe.PrivacyBusy }; }
        internal static bool PreferenceEnabled(string key) { return Settings.LoadCached(PreferencePrefix + key, false); }
    }
    internal sealed class PresetTimerMode { internal readonly object Gate = new object(); internal string TickState = "ready", GlobalState = "ready"; }
    internal partial class GameMode
    {
        private bool stopping, active, laboratoryClosed, mmcssOn, gpuPrefStageOn, autoGpuOn;
        private readonly object driverStageGate = new object(), sync = new object();
        private readonly AutoResetEvent kick = new AutoResetEvent(false);
        private readonly PresetTimerMode timerMode = new PresetTimerMode();
        private readonly PresetActionCoordinator performanceActions = new PresetActionCoordinator();
        internal bool NvMaxPerf { set { Settings.SaveDurable("NvMaxPerf", value); if (!value) NvDrsTweaks.RestoreKinds(new[] { "pstate", "forcep2" }); } }
        internal bool NvShaderCacheMax { set { Settings.SaveDurable("NvShaderCache", value); if (!value) NvDrsTweaks.RestoreKinds(new[] { "shadercache", "shaderon" }); } }
        internal bool SetGameLowLatency(bool on) { PresetProductionProbe.Record("low:" + on); if (!on) NvDrsTweaks.RestoreKinds(NvDrsTweaks.UltraKeys); return Settings.SaveStrDurable("NvLowLat", on ? "on" : "off"); }
        internal bool SetGameDvrSuppression(bool on) { PresetProductionProbe.Record("dvr:" + on); return Settings.SaveDurable("GameDvrOff", on); }
        internal bool SetEnvironmentPreference(string key, bool on) { PresetProductionProbe.Record("session:" + key + ":" + on); return Settings.SaveDurable(key, on); }
        private void ApplyEnvironmentTimerOptions() { }
        internal static string AutomationStateLabel(string value) { return value; }
        internal bool SetPerformanceActionSelected(string key, bool on) { PresetProductionProbe.Record("action:" + key + ":" + on); PresetProductionProbe.States[key] = on && key == PresetProductionProbe.FailAction ? "unsupported" : on ? "configured" : "off"; return Settings.SaveDurable(key, on); }
        internal string PerformanceExecutionState(string key) { return PerformanceTuning.State(key); }
        private void SyncGameDvr() { PresetProductionProbe.Record("mmcss"); }
        internal string AutomaticExecutionState(string key) { return "configured"; }
        internal void RefreshAutomaticPreferences() { PresetProductionProbe.Record("refresh"); }
        internal void Poke() { }
    }
}
