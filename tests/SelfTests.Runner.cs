// The public self-test build runs only isolated regression suites, never Program.Main.
#if PAVISE_SELFTEST && PAVISE_SELFTEST_RUNNER
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace PaviseApp
{
    internal static class SelfTestRunner
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 1 || args.Length > 2 || args[0] != "--selftest")
            {
                Console.Error.WriteLine("Usage: Pavise.selftest.exe --selftest [report.txt]");
                return 2;
            }
            TextWriter console = Console.Out, errorConsole = Console.Error;
            try
            {
                string output = Path.Combine(Path.GetTempPath(), "PaviseSelftestData-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(output);
                string report = args.Length == 2 ? Path.GetFullPath(args[1]) : Path.Combine(output, "results.txt");
                Settings.UseTransientStoreForCurrentProcess();
                Logger.ResetWriteBarrierForTest();
                Lang.Init();
                // A forgotten mock must fail rather than restore or delete real user settings.
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
                    failed = SelfTests.RunIsolatedSuites(output);
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
            // Explicit allowlist: no real-process matrix, screenshot mode, ETW or tuning runtime.
            run("ResetCleanup", delegate { RunResetCleanupRegressionTests(); });
            run("ResetFlow", delegate { RunResetFlowRegressionTests(); });
            run("OptionalServicePause", delegate { RunOptionalServicePauseRegressionTests(); });
            run("ServicePauser", delegate { RunServicePauserRegressionTests(); });
            run("StandbyListCleaner", delegate { RunStandbyListCleanerRegressionTests(); });
            run("MemShield", delegate { RunMemShieldRegressionTests(); });
            run("CacheWarm", delegate { RunCacheWarmRegressionTests(); });
            run("DisplaySolo", delegate { RunDisplaySoloRegressionTests(); });
            run("IrqAutoPilot", delegate { RunIrqAutoPilotRegressionTests(); });
            run("SnapshotReuse", delegate { RunSnapshotReuseRegressionTests(); });
            run("AudioLowLatency", delegate { RunAudioLowLatencyRegressionTests(); });
            run("GpuClockLock", delegate { RunGpuClockLockRegressionTests(); });
            run("Eee", delegate { RunEeeRegressionTests(); });
            run("RogueProcess", delegate { RunRogueProcessRegressionTests(); });
            run("MemCompress", delegate { RunMemCompressRegressionTests(); });
            run("RssSteer", delegate { RunRssSteerRegressionTests(); });
            run("ExtremeMode", delegate { RunExtremeModeRegressionTests(); });
            run("OsBaseline", delegate { RunOsBaselineRegressionTests(); });
            run("AdaptiveGuard", delegate { RunAdaptiveGuardRegressionTests(); });
            run("NicModeration", delegate { RunNicModerationRegressionTests(); });
            run("LogWrites", delegate { RunLogWritesRegressionTests(); });
            run("LogSeverity", delegate { RunLogSeverityRegressionTests(); });
            run("AntiCheatThrottle", delegate { RunAntiCheatThrottleRegressionTests(); });
            run("EnglishInput", delegate { RunEnglishInputRegressionTests(); });
            run("IntelGraphics", delegate { RunIntelGraphicsRegressionTests(); });
            run("AppGpuPreferences", delegate { RunAppGpuPreferencesRegressionTests(); });
            run("GraphicsInputUi", delegate { RunGraphicsInputUiRegressionTests(); });
            run("UiConfigAudit", delegate { RunUiConfigAuditRegressionTests(); });
            run("PowerYieldTarget", delegate { RunPowerYieldTargetRegressionTests(); });
            run("HeavySqueeze", delegate { RunHeavySqueezeRegressionTests(); });
            run("AddGameFolder", delegate { RunAddGameFolderRegressionTests(); });
            run("ReleaseNotes", delegate { RunReleaseNotesRegressionTests(); });
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
            run("IrqCoreLoadWeighted", TestIrqCoreLoadWeighted);
            run("IrqCoreLoadMissingAndFailure", TestIrqCoreLoadMissingAndFailure);
            run("IrqCoreLoadLifecycle", TestIrqCoreLoadLifecycle);
            run("IrqCoreLoadDiscardAndRearm", TestIrqCoreLoadDiscardAndRearm);
            run("IrqCoreLoadLedger", delegate { TestIrqCoreLoadLedger(output); });
            run("IrqPinSessionSources", TestIrqPinSessionSources);
            run("IrqObservationLifecycle", TestIrqObservationLifecycle);
            run("IrqEveryMatchFallback", TestIrqEveryMatchFallback);
            run("IrqShortMatchMeasurements", TestIrqShortMatchMeasurements);
            run("IrqSessionFailureSummary", TestIrqSessionFailureSummary);
            run("IrqObservationVerdictProvenance", TestIrqObservationVerdictProvenance);
            run("IrqShortObservationDisplay", TestIrqShortObservationDisplay);
            run("IrqDisplayVersionAndValidity", TestIrqDisplayVersionAndValidity);
            run("IrqObservationStatusReasons", TestIrqObservationStatusReasons);
            run("IrqSessionExclusionReasons", TestIrqSessionExclusionReasons);
            run("IrqLedgerReadStatus", delegate { TestIrqLedgerReadStatus(output); });
            run("IrqSessionLedgerRoundtrip", delegate { TestIrqSessionLedgerRoundtrip(output); });
            run("IrqVerdictRanking", TestIrqVerdictRanking);
            run("IrqSessionSummaryPicksByImpact", TestIrqSessionSummaryPicksByImpact);
            // IrqVerdictGuards also writes the live test process affinity; keep it outside this mock-only entry.
            Console.WriteLine("TOTAL suites=" + (passed + failed) + " passed=" + passed + " failed=" + failed);
            return failed;
        }
    }
}
#endif
