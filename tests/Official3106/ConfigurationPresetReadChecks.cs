using System;
using System.Threading;

namespace PaviseApp
{
    internal static class ConfigurationPresetReadChecks
    {
        private static int checks;
        private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
        internal static void Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false;
                ConfigurationPresetBackend backend = new ConfigurationPresetBackend(null);
                ConfigurationPresetCoordinator model = new ConfigurationPresetCoordinator(backend);
                foreach (ConfigurationPresetKind preset in new[] { ConfigurationPresetKind.Default, ConfigurationPresetKind.Extreme })
                {
                    foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) Store(e.Key, e.Value(preset));
                    foreach (ConfigurationPresetEntry e in ConfigurationPresetCatalog.Entries) Check(e.Value(preset) == null ? backend.Read(e) != null : backend.Read(e) == e.Value(preset), "production saved-setting mapping " + e.Key);
                    string raw = (preset == ConfigurationPresetKind.Extreme ? "2|extreme|" : "4|default|") + model.Fingerprint();
                    Check(backend.SaveIdentity(raw), "identity saved");
                    Check(model.CurrentPreset() == preset && ConfigurationPresetRuntime.CurrentFromSettings() == preset, "AI/current identity matches saved settings");
                    Settings.Save("GmBoost", false); Check(!ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "manual boolean changes actual identity"); Settings.Save("GmBoost", true);
                    Settings.Save("EnvAudioIdleV1", false); Check(!ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "partial environment member change changes identity"); Settings.Save("EnvAudioIdleV1", true);
                    Settings.SaveStr("LabCpuIdleStrategyV1", "disabled"); Check(!ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "manual idle disabled not mislabeled extreme"); Store("LabCpuIdleStrategyV1", "default");
                    Check(ConfigurationPresetRuntime.CurrentFromSettings() == preset, "restoring exact saved values restores identity");
                    Settings.Save(CpuMitigationPolicy.ExpandedPreferenceKey, preset != ConfigurationPresetKind.Extreme);
                    Check(!ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "CPU scope change invalidates preset identity even while the bundle remains ON");
                    Settings.Save(CpuMitigationPolicy.ExpandedPreferenceKey, preset == ConfigurationPresetKind.Extreme);
                    Check(ConfigurationPresetRuntime.CurrentFromSettings() == preset, "restoring CPU scope restores exact preset identity");
                }
                Settings.SaveStr("GmBoost", "invalid"); Check(backend.Read(ConfigurationPresetCatalog.Entries[0]) == null, "malformed boolean is unknown, not assumed default");
                Check(model.Fingerprint() == null && !ConfigurationPresetRuntime.CurrentFromSettings().HasValue, "unreadable mapping invalidates fingerprint");
                Check(Settings.LoadStr("PerformancePreset", "not-set") == "not-set" && Settings.LoadStr("PowerPlanOn", "not-set") == "not-set", "reads do not create strategy or power settings");
                Check(NvDrsTweaks.PresetKindsRestored(new[] { "pstate" }), "no NVIDIA receipts means restored");
                Settings.SaveStr("NvDrsList", "game.exe"); Settings.SaveStr("NvDrs_game.exe", "pstate=0~1;shadercache=1~2");
                Check(!NvDrsTweaks.PresetKindsRestored(new[] { "pstate", "forcep2" }), "pstate receipt blocks false restore success");
                Check(!NvDrsTweaks.PresetKindsRestored(new[] { "shadercache", "shaderon" }), "shader receipt blocks false restore success");
                Check(NvDrsTweaks.PresetKindsRestored(new[] { "prerender" }), "unrelated kinds do not block restore");
                Settings.SaveStr("NvDrs_game.exe", "corrupt"); Check(!NvDrsTweaks.PresetKindsRestored(new[] { "pstate" }), "corrupt receipts cannot impersonate restored");
                Settings.SaveStr("NvDrsList", ""); Settings.SaveStr("NvDrs_game.exe", "shadercache=1~2");
                Check(!NvDrsTweaks.PresetKindsRestored(new[] { "shadercache" }), "orphaned receipt is still detected");
                Console.WriteLine("PASS " + checks + " production preset read checks; transient settings, no native eligibility or writes");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        }
        private static void Store(string key, string value)
        {
            if (value == null) return;
            if (key == PerformanceTuning.EnvironmentBundleKey)
            { foreach (string member in new[] { "EnvCpuMitigationsV1", "EnvNtfsLastAccessV1", "EnvAudioIdleV1", "EnvVbsDisabledV1" }) Settings.Save(member, value == "1"); }
            else if (key == PerformanceActionCatalog.CpuMitigations)
            { Settings.Save(key, value != "0"); Settings.Save(CpuMitigationPolicy.ExpandedPreferenceKey, value == "1"); }
            else if (key == PerformanceTuning.StorageGroupKey)
            { foreach (string member in PerformanceTuning.StorageMembers()) Settings.Save(member, value == "1"); }
            else if (key == PerformanceActionCatalog.Quantum)
            { Settings.Save(key, value != "0"); Settings.SaveStr("EnvQuantumSelectedProfileV1", value); }
            else if (PrivacyControl.IsAction(key)) Settings.SaveStr(PrivacyControl.PreferencePrefix + key, value);
            else Settings.SaveStr(key, value);
        }
    }
}
