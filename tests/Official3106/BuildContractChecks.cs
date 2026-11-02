using System;
using System.Reflection;

namespace PaviseApp
{
    internal static class OfficialBuildContractChecks
    {
        private static int checks;
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks++; }

        internal static int Main()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                Check(App.Version == "3.2.0.2", "Application identity changed");
                Check(App.VersionTag == "v" + App.Version, "Application version tag is inconsistent");
                Check(assembly.GetName().Version.ToString() == App.Version, "Assembly version differs from application identity");
                Check(((AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyFileVersionAttribute))).Version == App.Version,
                    "File version differs from application identity");
                Check(((AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute))).InformationalVersion == App.Version,
                    "Informational version differs from application identity");
                Check(ReleaseNotes.Current != null && object.ReferenceEquals(ReleaseNotes.All[0], ReleaseNotes.Current),
                    "Current release notes must match the application version and appear first");
                Check(ReleaseNotes.All[1].Version == "3.2.0.1" && ReleaseNotes.All[1].Date == "2026-11-01"
                    && ReleaseNotes.All[2].Version == "3.1.0.8" && ReleaseNotes.All[2].Date == "2026-10-27"
                    && ReleaseNotes.All[3].Version == "3.1.0.6" && ReleaseNotes.All[3].Date == "2026-10-25",
                    "The previous published release history was overwritten");
                bool oldRemoval = false, currentRemoval = false;
                Check(ReleaseNotes.Current.Date == "2026-11-02", "Current release date is stale");
                foreach (ReleaseNote note in ReleaseNotes.All) Check(note.Version != "3.1.1.0", "Unpublished development notes must be consolidated");
                FieldInfo noteItems = typeof(ReleaseNote).GetField("items", BindingFlags.Instance | BindingFlags.NonPublic);
                string[][] published3201Items = (string[][])noteItems.GetValue(ReleaseNotes.All[1]);
                Check(published3201Items[0][0].Contains("帧率检测回归"), "Published 3.2.0.1 notes were replaced by later retirement changes");
                string[][] previousItems = (string[][])noteItems.GetValue(ReleaseNotes.All[2]);
                string[][] currentItems = (string[][])noteItems.GetValue(ReleaseNotes.Current);
                for (int i = 0; i < ReleaseNotes.All[2].Count; i++)
                {
                    string item = previousItems[i][0];
                    if (item.Replace(" ", "").Contains("移除RTSS")) oldRemoval = true;
                    Check(!item.Contains("恢复 RTSS"), "3.2.0.1 RTSS restoration leaked into 3.1.0.8 history");
                }
                for (int i = 0; i < ReleaseNotes.Current.Count; i++)
                {
                    string item = currentItems[i][0];
                    if (item.Contains("帧率检测下线") && item.Contains("悬浮 OSD") && item.Contains("游戏运行报告")) currentRemoval = true;
                    for (int language = 0; language < 3; language++)
                        foreach (string[] publishedItem in published3201Items)
                            Check(currentItems[i][language] != publishedItem[language],
                                "Published 3.2.0.1 release item was repeated as a new change");
                }
                Check(currentItems[0][0].Contains("帧率检测下线") && currentItems[0][0].Contains("特殊原因")
                    && currentItems[0][0].Contains("无法继续提供"), "The current release must lead with the requested monitoring retirement notice");
                Check(oldRemoval && currentRemoval, "Historical release notes or current report highlights were lost");
                string currentSummary = string.Join("\n", Array.ConvertAll(currentItems, item => item[0]));
                foreach (string change in new[] { "完整电源方案", "X3D", "工作站", "每秒参数巡检",
                    "CPU 空闲", "EPP", "本局守护统计", "软件内更新", "手动升级" })
                    Check(currentSummary.Contains(change), "An audited current-release change is missing: " + change);
                Check(!currentSummary.Contains("开源仓库地址") && !currentSummary.Contains("许可链接"),
                    "Current release notes contain label/link housekeeping instead of user-facing changes");
                foreach (string name in new[] { "OverviewTelemetryReader", "OverviewMemoryCleanupControl", "ManualMemoryCleaner", "WindowsManualMemoryNative", "HardThreadGuardianLaunchPolicy", "GameConfigurationDialog", "WeGameGameExtension", "WeGameShellService", "WeGameCardPanel", "LaboratorySession", "LaboratoryPower", "LaboratoryGpu", "LaboratoryAudio", "AiTuningPrompt", "AiTuningPromptDialog", "AiTuningHardwareReport", "AiTuningFeatureCatalog", "GpuInventoryDiscovery", "LegacyMonitoringCleanup" })
                    Check(assembly.GetType("PaviseApp." + name) != null, "Required implementation missing: " + name);
                foreach (string name in new[] { "MonitoringAmdTelemetry", "MonitoringGpuRouting", "MonitoringWddmSensors",
                    "MonitoringConsoleSurface", "WeGameShellReadiness", "PaviseNativeMonitor", "NativeMonitoringAvailability",
                    "NativeMonitoringPackage", "NativeMonitoringChannel", "NativeSensorProvider", "NativeSensorDriver",
                    "NativeSensorDriverSystem", "NativeVulkanRegistration", "MonitoringWindowOverlay",
                    "MonitoringPresenterSelection", "MonitoringPresenterScope", "GameFrameSample" })
                    Check(assembly.GetType("PaviseApp." + name) == null, "Retired or independent OSD sampling implementation present: " + name);
                foreach (string name in new[] { "PerformanceMonitoring", "MonitoringPreferences", "MonitoringHotkey", "MonitoringNativeSensors", "RtssComponent", "RtssOverlayClient", "RtssHardwareProvider", "RtssStartup", "RtssStartupPermissions", "RtssProcessElevation", "RtssTrayIcon", "MonitoringDisplayCache", "RtssNativeBenchmark", "GameRunReports", "GameRunReportStore", "GameRunObservation", "GameRunReportAnalysis", "GameRunReportExport", "GameReportTimeline", "MonitoringGpuReading", "GameRunGpuObservation", "GameRunGpuAdapter", "GameRunAdapterTrack" })
                    Check(assembly.GetType("PaviseApp." + name) == null, "Retired monitoring/report implementation remains: " + name);
                Check(Enum.GetNames(typeof(PageId)).Length == 14 && (int)PageId.Count == 13, "Retired page remains or existing page identities moved");
                foreach (string key in new[] { "PerfMonitorEnabledV2", "GameRunReportsEnabledV1", "PerfMonitorDisplayIntervalMsV2", "PerfMonitorTextStyleV2" })
                    Check(AppDataReset.IsUserPreference(key), "Retired preference cannot be reset: " + key);
                Check(RecoveryRecordKeys.Contains("RtssInstallationOwnerV1") && !AppDataReset.IsUserPreference("RtssInstallationOwnerV1"),
                    "An incomplete legacy uninstall must retain its installation ownership receipt");
                Check(typeof(WeGameShellService).GetMethod("ConfirmReadyAndDetach", BindingFlags.Instance | BindingFlags.NonPublic) == null,
                    "Manual WeGame confirmation was restored");
                Check(typeof(WeGameGameExtension).GetMethod("CanAutoDetachRenderer", BindingFlags.Static | BindingFlags.NonPublic) != null,
                    "Observed renderer gate is missing");
                const BindingFlags inputQueueFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (string name in new[] { "Repair", "NeedsRepair", "Describe", "MouseQueueSize", "KeyboardQueueSize", "IsTampered" })
                    Check(typeof(InputMythTweak).GetMethod(name, inputQueueFlags) == null, "Retired input queue correction is present: " + name);
                foreach (string name in new[] { "Restore", "HasResidue" })
                    Check(typeof(InputMythTweak).GetMethod(name, inputQueueFlags) != null, "Legacy input queue recovery is missing: " + name);
                Check(RepairCatalog.Find("inputmyth") == null, "Retired input queue repair is exposed");
                foreach (string key in new[] { "PrevMouseQueue", "PrevKbdQueue" })
                    Check(RecoveryRecordKeys.Contains(key) && !AppDataReset.IsUserPreference(key), "Input queue recovery record is unprotected: " + key);
                foreach (var client in new[] { new System.Drawing.Size(640, 480), new System.Drawing.Size(1220, 760), new System.Drawing.Size(3840, 2160) })
                {
                    var layout = new WorkspaceLayout(client, 1f);
                    Check(layout.Width == 1220 && layout.Height == 760, "Window dimensions must retain the 3.1.0.5 design canvas");
                    Check(layout.RailWidth == 0 && layout.HeaderHeight == 60, "Restoring dimensions must not restore the old UI");
                }
                foreach (string name in new[] { "BuildOverviewHud", "BuildCoreWorkspace", "LayoutSettingsWorkspace",
                    "StartManualMemoryCleanup", "BuildLaboratoryOptions" })
                    Check(typeof(PanelForm).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic) != null, "Workspace method missing: " + name);
                foreach (string key in new[] { LaboratoryPower.EppKey, LaboratoryPower.IdleKey, LaboratoryGpu.ClockKey,
                    LaboratoryGpu.PowerKey, LaboratoryAudio.Key })
                {
                    Check(LaboratorySelection.IsPreference(key), "Laboratory preference omitted: " + key);
                    Check(AppDataReset.IsUserPreference(key), "Reset does not recognize laboratory preference: " + key);
                }
                foreach (string key in new[] { LaboratoryPower.ReceiptKey, LaboratoryGpu.PowerReceipt, LaboratoryGpu.ClockReceipt })
                {
                    Check(RecoveryRecordKeys.Contains(key), "Laboratory recovery receipt is unprotected: " + key);
                    Check(!AppDataReset.IsUserPreference(key), "Reset would discard a laboratory recovery receipt: " + key);
                }
                Check(PrivacyActionCatalog.Find(PrivacyActionCatalog.BackgroundGlobal) != null
                    && !PrivacyActionCatalog.IsRetired(PrivacyActionCatalog.BackgroundGlobal), "Background restriction was not restored");
                Check(PrivacyActionCatalog.IsRetired(PrivacyActionCatalog.Diagnostics), "Unrequested privacy feature was restored");
                Check(LaboratorySelection.CanonicalIdle("threshold") == "threshold", "Threshold strategy missing");
                Check(LaboratorySelection.CanonicalIdle("disabled") == "disabled", "Disabled idle strategy missing");
                Check(LaboratorySelection.CanonicalIdle("unknown") == "default", "Invalid idle strategy is not rejected");
                ulong[] cores = { 3, 12, 48, 192 };
                ulong[] targets = CriticalThreadAutoPolicy.Targets(1, cores, 170, 0, 0, 0, null, 0, 0);
                Check(targets != null && targets.Length == 1, "Sparse SMT selection rejected");
                Check((targets[0] & 170) == targets[0], "Target expanded the user's logical selection");
                Check(CriticalThreadAutoPolicy.Targets(1, cores, 10, 0, 0, 0, null, 0, 0) == null,
                    "At least two worker physical cores are required");
                targets = CriticalThreadAutoPolicy.Targets(1, cores, 170, 16, 0, 0, null, 0, 0);
                Check(targets != null && (targets[0] & 48) == 0, "An external half-core reservation was not respected");
                Console.WriteLine("PASS " + checks + " build-contract assertions; no runtime, hardware or policy operations.");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
