using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace PaviseApp
{
    internal static class PerformancePolicy { internal static string Text(string zh, string en) { return zh; } }
    internal static class Settings { internal static int MutationGeneration; }
    internal class GameMode { internal bool RunLaboratoryManualWork(Func<bool> work) { return work(); } }
    internal sealed class ConfigurationPresetBackend : MemoryPresetBackend { internal ConfigurationPresetBackend(GameMode mode) { } }
    internal class MemoryPresetBackend : IConfigurationPresetBackend, IConfigurationPresetSnapshot
    {
        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>();
        internal readonly HashSet<string> Unsupported = new HashSet<string>();
        internal readonly List<string> Writes = new List<string>();
        internal string Identity = "", FailKey, UnreadableKey, ThrowInspectKey, Extra = "";
        internal bool IdentityFails, ReadbackMismatch;
        internal Action<string, string> AfterWrite;
        internal Action<string> OnInspect;
        internal MemoryPresetBackend() { foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) Values[e.Key] = e.DefaultValue; }
        public string Read(ConfigurationPresetEntry e) { return e.Key == UnreadableKey ? null : Values[e.Key]; }
        public ConfigurationPresetAvailability Inspect(ConfigurationPresetEntry e)
        { if (OnInspect != null) OnInspect(e.Key); if (e.Key == ThrowInspectKey) throw new Exception("probe failed"); return Unsupported.Contains(e.Key) ? ConfigurationPresetAvailability.No("unsupported") : ConfigurationPresetAvailability.Yes(); }
        public bool Write(ConfigurationPresetEntry e, string value, Func<bool> cancel, out string detail)
        {
            detail = "fake"; if (cancel != null && cancel()) return false;
            Writes.Add(e.Key + "=" + value); if (e.Key == FailKey) return false;
            if (!ReadbackMismatch) Values[e.Key] = value; Settings.MutationGeneration++;
            if (AfterWrite != null) AfterWrite(e.Key, value); return true;
        }
        public bool SaveIdentity(string identity) { if (IdentityFails) return false; Identity = identity; Settings.MutationGeneration++; return true; }
        public string LoadIdentity() { return Identity; }
        public string ExtraSnapshot() { return Extra; }
    }
    internal static class ConfigurationPresetChecks
    {
        private static int checks;
        private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
        private static ConfigurationPresetEntry Find(string key) { foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) if (e.Key == key) return e; throw new Exception(key); }
        private static int Enabled(ConfigurationPresetKind preset)
        { int count = 0; foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) if (e.Key != "LabCpuIdleStrategyV1" && e.Key != "EnvQuantumProfileV1" && ConfigurationPresetEntry.IsEnabled(e.Value(preset))) count++; return count; }
        internal static void Main()
        {
            try { Run(); Console.WriteLine("PASS " + checks + " configuration preset checks; fake settings/hardware only"); }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        }
        private static void Run()
        {
            Check(ConfigurationPresetCatalog.Entries.Length == 34 && !ConfigurationPresetCatalog.Entries.Any(e => e.Key == "MatchVisualEffectsV1"), "retired desktop visual effects must not return through presets");
            Check(Enabled(ConfigurationPresetKind.Default) == 9, "default 9 on");
            Check(Enabled(ConfigurationPresetKind.Extreme) == 24, "extreme 24 on incl annotated NTFS");
            HashSet<string> keys = new HashSet<string>(); int changed = 0, booleanChanges = 0;
            foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries)
            {
                Check(keys.Add(e.Key), "unique " + e.Key);
                Check(e.Key != "PerformancePreset" && e.Key != "PowerPlanOn" && !e.Key.StartsWith("Game" + "Family") && e.Key != "GmAutoCoresV2", "no runtime strategy or per-game scope " + e.Key);
                if (e.DefaultValue != e.ExtremeValue) { changed++; if (e.Key != "LabCpuIdleStrategyV1") booleanChanges++; }
            }
            Check(changed == 16 && booleanChanges == 15, "exact delta 15 toggles + idle after visual-effects removal");
            Check(Find("EnvQuantumProfileV1").DefaultValue == "38" && Find("EnvQuantumProfileV1").ExtremeValue == "38", "both short variable 3:1");
            Check(Find("LabCpuIdleStrategyV1").ExtremeValue == "threshold", "threshold not disabled");
            Check(Find("EnvPrivacyNtfsMemoryV1").ExtremeValue == "1", "NTFS annotation overrides disabled screenshot");
            foreach (string key in new[] { "GmCacheWarmV2", "GmIntelEnduranceV1", "GmEnglishInputV1", "GameDvrOff", "NvShaderCache", "AutoOpenGlThreadsV1", "LabEppYieldV1", "LabAudioLowBufferV1" })
                Check(Find(key).ExtremeValue == "0", "eight explicit off " + key);

            MemoryPresetBackend backend = new MemoryPresetBackend(); ConfigurationPresetCoordinator coordinator = new ConfigurationPresetCoordinator(backend);
            ConfigurationPresetResult result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => false);
            Check(result.Items.Count == 34 && result.Failed == 0 && result.Skipped == 0 && result.Identity != null, "extreme applies all remaining entries");
            Check(coordinator.CurrentPreset() == ConfigurationPresetKind.Extreme, "current extreme actual fingerprint");
            foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) Check(backend.Values[e.Key] == e.ExtremeValue, "extreme setting " + e.Key);
            backend.Values["GmAwake"] = "0"; Check(!coordinator.CurrentPreset().HasValue, "manual change becomes custom");
            backend.Values["GmAwake"] = "1"; Check(coordinator.CurrentPreset() == ConfigurationPresetKind.Extreme, "restored matching configuration");
            backend.Extra = "submember changed"; Check(!coordinator.CurrentPreset().HasValue, "group member changes become custom"); backend.Extra = "";
            result = coordinator.Apply(ConfigurationPresetKind.Default, () => false);
            Check(result.Failed == 0 && coordinator.CurrentPreset() == ConfigurationPresetKind.Default, "default restores through write path");
            foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) Check(backend.Values[e.Key] == e.DefaultValue, "default setting " + e.Key);
            Check(backend.Writes.Contains("EnvStoragePowerGroupV1=0") && backend.Writes.Contains("LabGpuClockMaxV1=0") && backend.Writes.Contains("GameDvrOff=0"), "OFF invokes restoration even for default-off");

            backend = new MemoryPresetBackend(); coordinator = new ConfigurationPresetCoordinator(backend);
            backend.Unsupported.Add("EnvPrivacyNtfsMemoryV1"); backend.Unsupported.Add("LabGpuClockMaxV1"); backend.Unsupported.Add("AppGpuAutoV1");
            result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => false);
            Check(result.Skipped == 3 && result.Failed == 0, "unsupported skipped");
            Check(!backend.Writes.Contains("EnvPrivacyNtfsMemoryV1=1") && !backend.Writes.Contains("LabGpuClockMaxV1=1"), "unsupported never stored enabled");
            Check(backend.Writes.Contains("AppGpuAutoV1=0") && backend.Values["AppGpuAutoV1"] == "0", "unsupported prior ON withdrawn");
            Check(coordinator.CurrentPreset() == ConfigurationPresetKind.Extreme, "hardware-adapted preset identity");
            backend.Values["EnvPrivacyNtfsMemoryV1"] = "1"; Check(!coordinator.CurrentPreset().HasValue, "skipped item's later edit is custom");
            backend.FailKey = "GmAwake"; result = coordinator.Apply(ConfigurationPresetKind.Default, () => false);
            Check(result.Failed > 0 && !coordinator.CurrentPreset().HasValue, "failure remains custom");

            backend = new MemoryPresetBackend(); coordinator = new ConfigurationPresetCoordinator(backend); backend.ThrowInspectKey = "NvMaxPerf";
            result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => false);
            Check(result.Skipped == 1 && result.Failed == 0 && !backend.Writes.Contains("NvMaxPerf=1"), "probe throws skips and never enables");
            backend = new MemoryPresetBackend(); coordinator = new ConfigurationPresetCoordinator(backend); backend.UnreadableKey = "GmAwake";
            result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => false);
            Check(result.Skipped == 1 && !backend.Writes.Contains("GmAwake=1") && result.Identity == null, "unreadable preference skipped without false identity");
            backend = new MemoryPresetBackend(); coordinator = new ConfigurationPresetCoordinator(backend); backend.IdentityFails = true;
            result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => false);
            Check(result.Failed > 0 && backend.Writes.Count == 0, "identity invalidate failure prevents mutations");
            backend = new MemoryPresetBackend(); coordinator = new ConfigurationPresetCoordinator(backend); backend.ReadbackMismatch = true;
            result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => false);
            Check(result.Failed > 0 && result.Identity == null, "write success without readback not accepted");

            backend = new MemoryPresetBackend(); coordinator = new ConfigurationPresetCoordinator(backend); bool cancel = false;
            backend.OnInspect = key => cancel = true;
            result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => cancel);
            Check(result.Cancelled && backend.Writes.Count == 0 && result.Identity == null, "cancel after capability probe prevents write");
            backend = new MemoryPresetBackend(); coordinator = new ConfigurationPresetCoordinator(backend); cancel = false;
            backend.AfterWrite = (key, value) => cancel = true;
            result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => cancel);
            Check(result.Cancelled && backend.Writes.Count == 1 && result.Identity == null, "cancel after write prevents subsequent items and identity");
            backend = new MemoryPresetBackend(); coordinator = new ConfigurationPresetCoordinator(backend);
            backend.AfterWrite = (key, value) => { if (key == "LabCpuIdleStrategyV1") backend.Values["GmAwake"] = "0"; };
            result = coordinator.Apply(ConfigurationPresetKind.Extreme, () => false);
            Check(result.Failed > 0 && result.Identity == null, "concurrent external edit cannot be adopted as target");

            backend = new MemoryPresetBackend(); bool admitted = false; ManualResetEvent entered = new ManualResetEvent(false), release = new ManualResetEvent(false), done = new ManualResetEvent(false);
            ConfigurationPresetRuntime runtime = new ConfigurationPresetRuntime(backend, () => Settings.MutationGeneration, work => { admitted = true; entered.Set(); release.WaitOne(); return work(); });
            Check(runtime.TryApply(ConfigurationPresetKind.Extreme, () => false, r => done.Set()), "runtime starts");
            Check(entered.WaitOne(3000) && admitted && runtime.Busy, "admitted and busy");
            Check(!runtime.TryApply(ConfigurationPresetKind.Default, () => false, null), "duplicate click rejected");
            runtime.Cancel(); release.Set(); Check(done.WaitOne(3000), "cancel completion delivered");
            Check(runtime.WaitForCompletion(1000) && !runtime.Busy && backend.Writes.Count == 0, "cancel drains before release and no late write");
            done.Dispose(); entered.Dispose(); release.Dispose();
            backend = new MemoryPresetBackend(); done = new ManualResetEvent(false); ConfigurationPresetResult callback = null;
            runtime = new ConfigurationPresetRuntime(backend, () => Settings.MutationGeneration, work => false);
            Check(runtime.TryApply(ConfigurationPresetKind.Extreme, () => false, r => { callback = r; done.Set(); }), "admission refusal queued");
            Check(done.WaitOne(3000) && callback.Failed > 0 && backend.Writes.Count == 0 && !runtime.Busy, "match/shutdown refusal has zero writes"); done.Dispose();
            Check(!runtime.TryApply((ConfigurationPresetKind)99, () => false, null), "invalid preset rejected");
        }
    }
}
