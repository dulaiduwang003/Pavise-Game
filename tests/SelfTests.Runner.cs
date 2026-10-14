#if PAVISE_SELFTEST && PAVISE_SELFTEST_RUNNER
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace PaviseApp
{
    internal static class SelfTestRunner
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
        [STAThread]
        private static int Main(string[] args)
        {
            SelfTests.RunRtssDialogFixtureIfRequested();
            if (CpuCapGuard.TryHandleArgs(args)) return Environment.ExitCode;
            if (FreezeGuard.TryHandleArgs(args)) return Environment.ExitCode;
            if (SelfTests.TryFreezeHelper(args)) return Environment.ExitCode;
            if (SelfTests.TryCpuCapHelper(args)) return Environment.ExitCode;
            if (args.Length == 1 && (args[0] == "--sampling-audit" || args[0] == "--sampling-test"))
                return SelfTests.RunSamplingAudit(args);
            int result = 1;
            var worker = new System.Threading.Thread(delegate() { result = RunSuites(args); });
            worker.SetApartmentState(System.Threading.ApartmentState.STA);
            worker.Start(); worker.Join();
            return result;
        }
        private static int RunSuites(string[] args)
        {
            bool modeLockOnly = args.Length > 0 && args[0] == "--mode-lock-test";
            bool manualCoresOnly = args.Length > 0 && args[0] == "--manual-cores";
            bool fullscreenOnly = args.Length > 0 && args[0] == "--fullscreen-optimizations";
            if (args.Length < 1 || args.Length > 2 || (args[0] != "--selftest" && !modeLockOnly && !manualCoresOnly && !fullscreenOnly))
            {
                Console.Error.WriteLine("Usage: Pavise.selftest.exe --selftest|--mode-lock-test|--manual-cores|--fullscreen-optimizations [report.txt]");
                return 2;
            }
            TextWriter console = Console.Out, errorConsole = Console.Error;
            IntPtr desktop = IntPtr.Zero;
            try
            {
                // UI 测试不进入用户桌面。
                desktop = CreateDesktop("PaviseSelfTest-" + Guid.NewGuid().ToString("N"), IntPtr.Zero, IntPtr.Zero, 0, 0x01ff, IntPtr.Zero);
                if (desktop == IntPtr.Zero || !SetThreadDesktop(desktop))
                    throw new InvalidOperationException("Private test desktop unavailable");
                string output = Path.Combine(Path.GetTempPath(), "PaviseSelftestData-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(output);
                string report = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.Combine(output, "results.txt");
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest();
                Lang.Init();
                LegacyPurge.RestoreHook = delegate { throw new InvalidOperationException("Unmocked system restoration in self-test"); };
                LegacyPurge.DeleteRegistryHook = delegate { throw new InvalidOperationException("Unmocked registry deletion in self-test"); };
                int failed;
                using (var writer = new StreamWriter(report, false, new UTF8Encoding(false)))
                using (GameInstallScope.UseSnapshotForTest(new GameInstallRecord[0], new string[0]))
                {
                    writer.AutoFlush = true;
                    Console.SetOut(writer); Console.SetError(writer);
                    Console.WriteLine("ISOLATION settings=transient system_writes=mocked application_started=false");
                    Console.WriteLine("OUTPUT " + output);
                    if (modeLockOnly)
                    {
                        SelfTests.RunModeSwitchLockRegressionTests(output);
                        failed = 0;
                    }
                    else if (manualCoresOnly)
                    {
                        SelfTests.RunManualCoreTests(output);
                        SelfTests.RunCoreIsolationTests();
                        SelfTests.RunCoreSchedulingTests(output);
                        Console.WriteLine("PASS manual cores, isolation recovery and automatic scheduling regression");
                        failed = 0;
                    }
                    else if (fullscreenOnly)
                    {
                        SelfTests.RunAutomaticFullscreenOptimizationTests();
                        Console.WriteLine("PASS per-game fullscreen preference, legacy recovery and persistence regression");
                        failed = 0;
                    }
                    else failed = SelfTests.RunIsolatedSuites(output);
                }
                Console.SetOut(console); Console.SetError(errorConsole);
                Console.WriteLine("Self-test " + (failed == 0 ? "passed" : "FAILED") + "; report: " + report);
                return failed == 0 ? 0 : 1;
            }
            catch (Exception error)
            {
                errorConsole.WriteLine("FAIL self-test runner: " + error);
                return 1;
            }
            finally
            {
                Console.SetOut(console); Console.SetError(errorConsole);
                if (desktop != IntPtr.Zero) CloseDesktop(desktop);
            }
        }
    }

    internal static partial class SelfTests
    {
        private static void Eq<T>(T expected, T actual)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
                throw new InvalidOperationException("Expected " + expected + ", actual " + actual);
        }

        public static bool TryHandleRuntimeMode(string[] args)
        {
            throw new InvalidOperationException("The isolated self-test build cannot start the application runtime");
        }

        private static void IsolateDiscoveryCatalogs()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            typeof(GamePlatformCatalog).GetField("rootsResolved", flags).SetValue(null, true);
            typeof(GamePlatformCatalog).GetField("lastResolveTicks", flags).SetValue(null, DateTime.UtcNow.Ticks);
            foreach (object platform in (IEnumerable)typeof(GamePlatformCatalog).GetField("Platforms", flags).GetValue(null))
                platform.GetType().GetField("Roots", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .SetValue(platform, new List<string>());
            typeof(PeripheralVendorProbe).GetField("tokens", flags).SetValue(null, new string[0]);
            typeof(PeripheralVendorProbe).GetField("stamp", flags).SetValue(null, Environment.TickCount);
            typeof(PeripheralVendorProbe).GetField("scanned", flags).SetValue(null, true);
        }

        internal static int RunIsolatedSuites(string output)
        {
            int passed = 0, failed = 0;
            Action<string, Action> run = delegate(string name, Action test)
            {
                try
                {
                    Settings.UseTransientStoreForCurrentProcess();
                    Logger.ResetWriteBarrierForTest();
                    Logger.LogPath = Path.Combine(output, "decisions.log");
                    Lang.Cur = 0;
                    IsolateDiscoveryCatalogs();
                    test();
                    passed++;
                    Console.WriteLine("PASS suite " + name);
                }
                catch (Exception error)
                {
                    while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                    failed++;
                    Console.WriteLine("FAIL suite " + name + ": " + error);
                }
            };
            run("ResetCleanup", delegate { RunResetCleanupRegressionTests(); });
            run("CacheWarm", delegate { RunCacheWarmRegressionTests(); });
            run("ResetFlow", delegate { RunResetFlowRegressionTests(); });
            run("OptionalServicePause", delegate { RunOptionalServicePauseRegressionTests(); });
            run("ServicePauser", delegate { RunServicePauserRegressionTests(); });
            run("StandbyListCleaner", delegate { RunStandbyListCleanerRegressionTests(); });
            run("MemShield", delegate { RunMemShieldRegressionTests(); });
            run("OptimizationSafety", delegate { RunOptimizationSafetyTests(); });
            run("DisplaySolo", delegate { RunDisplaySoloRegressionTests(); });
            run("IrqAutoPilot", delegate { RunIrqAutoPilotRegressionTests(); });
            run("SnapshotReuse", delegate { RunSnapshotReuseRegressionTests(); });
            run("SamplingLifecycle", RunSamplingLifecycleTests);
            run("PerformanceMonitoring", RunMonitoringTests);
            run("RtssComponent", RunRtssComponentTests);
            run("RtssTransport", RunRtssTransportTests);
            run("RtssTrayIcon", RunRtssTrayIconTests);
            run("GpuClockLock", delegate { RunGpuClockLockRegressionTests(); });
            run("MemCompress", delegate { RunMemCompressRegressionTests(); });
            run("RssSteer", delegate { RunRssSteerRegressionTests(); });
            run("PowerPlanPolicy", delegate { RunPowerPlanPolicyRegressionTests(); });
            run("PowerPriority", RunPowerPriorityTests);
            run("OsBaseline", delegate { RunOsBaselineRegressionTests(); });
            run("AdaptiveGuard", delegate { RunAdaptiveGuardRegressionTests(); });
            run("LogWrites", delegate { RunLogWritesRegressionTests(); });
            run("LogSeverity", delegate { RunLogSeverityRegressionTests(); });
            run("UiWorkflow", delegate { RunUiWorkflowRegressionTests(); });
            run("AutomaticPolicy", RunAutomaticPolicyTests);
            run("PerformancePromotion", RunPerformancePromotionTests);
            run("FrameCoordination", RunFrameCoordinationTests);
            run("AutomaticFullscreenOptimizations", RunAutomaticFullscreenOptimizationTests);
            run("PerformanceActions", RunPerformanceActionTests);
            run("GuardScheduling", RunGuardSchedulingRegressionTests);
            run("LaptopPerformance", RunLaptopPerformanceTests);
            run("AutomaticLayout", RunAutomaticLayoutTests);
            run("AutomaticNvidia", RunAutomaticNvidiaTests);
            run("BoundedRetries", RunBoundedRetryTests);
            run("EnvironmentGraphics", RunEnvironmentGraphicsTests);
            run("NvSmoothMotion", RunNvSmoothMotionTests);
            run("AutomaticFreshStart", RunAutomaticFreshStartTests);
            run("PresetValue", delegate { RunPresetValueRegressionTests(); });
            run("DieMasks", delegate { RunDieMaskRegressionTests(); });
            run("AntiCheatThrottle", delegate { RunAntiCheatThrottleRegressionTests(); });
            run("AntiCheatCatalog", delegate { RunAntiCheatCatalogRegressionTests(); });
            run("KernelAcAttribution", delegate { RunKernelAcAttributionRegressionTests(); });
            run("AntiCheatMode", delegate { RunAntiCheatModeRegressionTests(); });
            run("EnglishInput", delegate { RunEnglishInputRegressionTests(); });
            run("IntelGraphics", delegate { RunIntelGraphicsRegressionTests(); });
            run("AppGpuPreferences", delegate { RunAppGpuPreferencesRegressionTests(); });
            run("GraphicsInputUi", delegate { RunGraphicsInputUiRegressionTests(); });
            run("TopologySources", TestTopologySourcesAgree);
            run("Donate", RunDonateTests);
            run("WebsiteUpdate", RunWebsiteUpdateTests);
            run("AllMaskReconcile", TestAllMaskReconcile);
            run("CoreCardHeadTag", TestCoreCardHeadTag);
            run("CoreScheduling", delegate { RunCoreSchedulingTests(output); });
            run("CoreIntelligence", delegate { RunCoreIntelligenceTests(output); });
            run("CoreIsolation", delegate { RunCoreIsolationTests(); });
            run("SuppressionAffinity", delegate { RunSuppressionAffinityTests(); });
            run("RenderLaneEligibility", delegate { RunRenderLaneEligibilityTests(); });
            run("HandheldBlock", delegate { RunHandheldBlockTests(); });
            run("GpuIrqCheck", delegate { RunGpuIrqCheckTests(); });
            run("AddGameFolder", delegate { RunAddGameFolderRegressionTests(); });
            run("GameScan", delegate { RunGameScanRegressionTests(); });
            run("GameScanPlatforms", delegate { RunGameScanPlatformRegressionTests(); });
            run("GameScanShortcuts", delegate { RunGameScanShortcutRegressionTests(); });
            run("GameScanInstalled", delegate { RunGameScanInstalledRegressionTests(); });
            run("GameSearch", delegate { RunGameSearchRegressionTests(); });
            run("ReleaseNotes", delegate { RunReleaseNotesRegressionTests(); });
            run("GameExtensionScope", delegate { RunGameExtensionScopeRegressionTests(); });
            run("NegativeOptFixes", delegate { RunNegativeOptimizationFixTests(); });
            run("UiAsyncState", delegate { RunUiAsyncStateRegressionTests(); });

            run("RendererHandoff", delegate { Eq(0, RunRendererHandoffRegressionTests()); });
            run("RendererRelease", delegate { RunRendererReleaseRegressionTests(); });
            run("RendererCoordinator", delegate { RunRendererCoordinatorRegressionTests(); });
            run("FamilySuppression", delegate { RunFamilySuppressionRegressionTests(); });
            run("GenericRenderer", delegate { RunGenericRendererRegressionTests(); });
            run("GameInstallScope", delegate { RunGameInstallScopeRegressionTests(); });
            run("GameFamilyHistory", delegate { TestGameFamilyHistory(); });
            run("GameFamilyIntegration", delegate { RunGameFamilyIntegrationRegressionTests(); });
            run("RendererCandidates", TestRendererCandidateDetection);
            run("RendererReplacement", delegate { TestConfirmedRendererReplacement(output); });
            run("RendererLearningGuards", delegate { TestConfirmedRendererLearningGuards(output); });
            run("RendererSaveFailure", delegate { TestConfirmedRendererSaveFailure(output); });
            run("IrqAffinityTransactions", TestIrqAffinityTransactions);
            run("IrqRestoreResults", TestIrqRestoreResults);
            run("ManualCores", delegate { RunManualCoreTests(output); });
            run("WhitelistTightening", RunWhitelistTighteningTests);
            run("BackgroundScope", RunBackgroundScopeTests);
            run("WeGameRuntime", RunWeGameRuntimeTests);
            run("BackgroundFreeze", RunBackgroundFreezeTests);
            run("BackgroundCpuCap", delegate { RunBackgroundCpuCapTests(); });
            Console.WriteLine("TOTAL suites=" + (passed + failed) + " passed=" + passed + " failed=" + failed);
            return failed;
        }
    }
}
#endif
