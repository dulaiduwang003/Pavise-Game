#if PAVISE_SELFTEST && PAVISE_EXECUTION_ENGINE_CHECKS
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args)
        { throw new InvalidOperationException("Application runtime is unavailable in execution engine checks"); }
    }
    internal static partial class ExecutionEngineChecks
    {
        private static int assertions;
        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
            assertions++;
        }
        [STAThread]
        private static int Main()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Logger.WritesEnabled = false;
            Lang.Init();
            try
            {
                DisabledPersistentDiscoveryKeepsRecovery();
                AutomaticOpenGlDoesNotFeedLegacyGames();
                PreferenceReadsAreBounded();
                PreferenceChangesAndAdmissionStayLive();
                PreferenceScopesDoNotEscape();
                PreferencesChangedDuringReadAreNotCached();
                TargetDiscoveryIsDeferredAndCoalesced();
                QueuedTargetsAreImmutable();
                MatchDoesNotDependOnLibraryTargets();
                MatchSkipsLibraryEnumeration();
                TargetAdmissionKeepsTheExecutionBoundary();
                Console.WriteLine("PASS execution engine assertions=" + assertions
                    + " settings=transient system_writes=mocked process_sampling=none windows_shown=false");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally
            {
                PolicyResolver.RawReadForTest = null;
                GamePerformanceTargetPolicy.ResetForTest();
                PerformanceActionBackend.ReadForTest = null;
                PerformanceActionBackend.WriteForTest = null;
                PerformanceTuning.BootForTest = null;
            }
        }
        private static void PreferenceReadsAreBounded()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(PolicyCatalog.KeyBoost, true);
            int reads = 0;
            PolicyResolver.RawReadForTest = delegate { reads++; };
            using (PolicyResolver.BeginReadScope())
                for (int i = 0; i < 1000; i++)
                {
                    Check(PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost) == "1", "boost preference changed");
                    Check(PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoostCpu) == "1", "priority alias changed");
                }
            Check(reads == 1, "one execution pass must read the shared preference once");
            using (PolicyResolver.BeginReadScope())
                Check(PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost) == "1", "next pass lost the preference");
            Check(reads == 2, "the next pass must read a fresh preference");
            PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
            PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
            Check(reads == 4, "unscoped readers must remain fresh");
            PolicyResolver.RawReadForTest = null;
            Console.WriteLine("PASS shared preference reads 2000 -> 1 within one isolated execution pass");
        }
        private static void PreferenceChangesAndAdmissionStayLive()
        {
            Settings.UseTransientStoreForCurrentProcess();
            Settings.Save(PolicyCatalog.KeyBoost, true);
            PolicySnapshot policy = PolicyResolver.Global();
            using (PolicyResolver.BeginReadScope())
            {
                Check(policy.BoostGame, "manual boost should be enabled");
                Settings.Save(PolicyCatalog.KeyBoost, false);
                Check(!policy.BoostGame, "a preference write must invalidate the current pass");
                Settings.Save(PolicyCatalog.KeyBoost, true);
                Check(policy.BoostGame, "re-enabling must not wait for another pass");
                Settings.Save(CalibrationRuntime.RequestedKey, true);
                Check(PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost) == "1", "automatic mode must preserve raw intent");
                Check(!policy.BoostGame, "unapproved automatic mode must not reuse an enabled resolved value");
                Settings.Save(CalibrationRuntime.RequestedKey, false);
                Check(policy.BoostGame, "returning to manual must take effect immediately");
                Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)PerformancePreset.Competitive).ToString());
                Check(policy.Preset == PerformancePreset.Competitive, "enum writes must be visible immediately");
                Settings.Remove(PolicyCatalog.KeyBoost);
                Check(PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost) == PolicyCatalog.ItemOf(PolicyCatalog.KeyBoost).Fallback,
                    "deletion must restore the catalog fallback");
            }
            Console.WriteLine("PASS live preference changes and automatic admission");
        }
        private static void PreferenceScopesDoNotEscape()
        {
            int reads = 0;
            PolicyResolver.RawReadForTest = delegate { Interlocked.Increment(ref reads); };
            using (PolicyResolver.BeginReadScope())
            {
                PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
                using (PolicyResolver.BeginReadScope()) PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
                PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
                Check(reads == 2, "nested scopes must restore the outer pass");
                var thread = new Thread(delegate()
                {
                    PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
                    PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
                });
                thread.Start(); Check(thread.Join(3000), "isolated reader did not stop");
                Check(reads == 4, "a pass must not cache reads on other threads");
            }
            try
            {
                using (PolicyResolver.BeginReadScope())
                {
                    PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
                    throw new OperationCanceledException();
                }
            }
            catch (OperationCanceledException) { }
            PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost);
            Check(reads == 6, "exceptional exits must dispose the pass");
            PolicyResolver.RawReadForTest = null;
            Console.WriteLine("PASS nested scopes thread isolation and exceptional exits");
        }
        private static void PreferencesChangedDuringReadAreNotCached()
        {
            Settings.Save(PolicyCatalog.KeyBoost, true);
            int reads = 0;
            PolicyResolver.RawReadForTest = delegate
            {
                if (++reads == 1) Settings.Save(PolicyCatalog.KeyBoost, false);
            };
            using (PolicyResolver.BeginReadScope())
            {
                Check(PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost) == "0", "a concurrent write was lost");
                Check(PolicyResolver.RawGlobalValue(PolicyCatalog.KeyBoost) == "0", "the next read changed the value");
                Check(reads == 2, "a value read across a mutation must not seed the cache");
            }
            PolicyResolver.RawReadForTest = null;
            Console.WriteLine("PASS mutation during preference read");
        }
        private static void WithCoordinator(Action<PerformanceActionCoordinator, string, Dictionary<string, string>> test)
        {
            Settings.UseTransientStoreForCurrentProcess();
            GamePerformanceTargetPolicy.ResetForTest();
            Settings.Save(PerformanceActionCatalog.OpenGl, true);
            string root = Path.Combine(Path.GetTempPath(), "PaviseExecution-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string game = Path.Combine(root, "AsterGame.exe");
            File.Copy(typeof(ExecutionEngineChecks).Assembly.Location, game);
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            GamePerformanceTargetPolicy.MetadataForTest = delegate { return null; };
            PerformanceTuning.BootForTest = delegate { return "b:123"; };
            PerformanceActionBackend.ReadForTest = delegate(string id, string target)
            {
                Check(id == PerformanceActionCatalog.OpenGl, "unexpected backend read");
                string value; return values.TryGetValue(target, out value) ? value : "absent";
            };
            PerformanceActionBackend.WriteForTest = delegate(string id, string target, string value)
            {
                Check(id == PerformanceActionCatalog.OpenGl, "unexpected backend write");
                values[target] = value; return true;
            };
            var coordinator = new PerformanceActionCoordinator();
            try { test(coordinator, game, values); }
            finally
            {
                Check(coordinator.Close(3000), "coordinator recovery did not finish");
                Check(PerformanceTuning.RestoreAll(), "fixture configuration was not restored");
                Check(PerformanceActionStore.All().Count == 0, "fixture recovery records remain");
                GamePerformanceTargetPolicy.ResetForTest();
                PerformanceActionBackend.ReadForTest = null;
                PerformanceActionBackend.WriteForTest = null;
                PerformanceTuning.BootForTest = null;
            }
        }
        private static void TargetDiscoveryIsDeferredAndCoalesced()
        {
            WithCoordinator(delegate(PerformanceActionCoordinator coordinator, string game, Dictionary<string, string> values)
            {
                long now = DateTime.UtcNow.Ticks;
                coordinator.ClockForTest = delegate { return now; };
                int validations = 0;
                GamePerformanceTargetPolicy.TargetValidationForTest = delegate { validations++; };
                string missing = Path.Combine(Path.GetDirectoryName(game), "MissingGame.exe");
                string[] games = { game, missing, "relative.exe" };
                coordinator.Queue(true, true, games, false, true);
                Check(coordinator.DrainForTest(3000), "match request did not finish");
                Check(validations == 0 && values.Count == 0, "match requests must not discover persistent game targets");
                coordinator.Queue(true, false, games, false, true);
                Check(coordinator.DrainForTest(3000), "configuration request did not finish");
                Check(validations == 3, "configuration must validate every candidate");
                Check(values.Count == 1 && values.ContainsKey(game), "missing and relative targets must be rejected");
                for (int i = 0; i < 1000; i++)
                    coordinator.Queue(true, false, new[] { "relative.exe", missing, game }, false, true);
                Check(coordinator.DrainForTest(3000), "duplicate requests did not drain");
                Check(validations == 3, "duplicates must not repeat target discovery");
                now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(true, false, games, false, true);
                Check(coordinator.DrainForTest(3000), "periodic audit did not finish");
                Check(validations == 6, "periodic validation must remain active");
                File.Delete(game);
                now += TimeSpan.FromMinutes(1).Ticks;
                coordinator.Queue(true, false, games, false, true);
                Check(coordinator.DrainForTest(3000), "removed target audit did not finish");
                Check(validations == 9, "deleted targets must be checked again");
                Settings.Save(PerformanceActionCatalog.OpenGl, false);
                coordinator.Queue(true, false, games, false, true);
                Check(coordinator.DrainForTest(3000), "disabled action did not restore");
                Check(values[game] == "absent", "disabled actions must restore even after the executable disappears");
                Check(validations == 9, "disabled actions must not discover targets");
                Console.WriteLine("PASS 1000 duplicate requests target validation=0 match target validation=0 periodic_audit=retained recovery=retained");
            });
        }
        private static void QueuedTargetsAreImmutable()
        {
            WithCoordinator(delegate(PerformanceActionCoordinator coordinator, string game, Dictionary<string, string> values)
            {
                using (var entered = new ManualResetEvent(false))
                using (var release = new ManualResetEvent(false))
                {
                    GamePerformanceTargetPolicy.TargetValidationForTest = delegate
                    {
                        entered.Set();
                        if (!release.WaitOne(3000)) throw new TimeoutException("fixture validation gate timed out");
                    };
                    string[] games = { game };
                    try
                    {
                        coordinator.Queue(true, false, games, false, true);
                        Check(entered.WaitOne(3000), "target validation was not reached");
                        games[0] = Path.Combine(Path.GetDirectoryName(game), "ReplacedGame.exe");
                    }
                    finally { release.Set(); }
                    Check(coordinator.DrainForTest(3000), "immutable target request did not finish");
                    Check(values.ContainsKey(game) && !values.ContainsKey(games[0]), "caller mutation changed queued target identity");
                }
                Console.WriteLine("PASS queued target input is copied before asynchronous processing");
            });
        }
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static object Latest(PerformanceActionCoordinator coordinator)
        { return typeof(PerformanceActionCoordinator).GetField("latest", Hidden).GetValue(coordinator); }
        private static string[] RequestTargets(object request, string field)
        { return (string[])request.GetType().GetField(field, Hidden).GetValue(request); }
        private static void MatchDoesNotDependOnLibraryTargets()
        {
            WithCoordinator(delegate(PerformanceActionCoordinator coordinator, string game, Dictionary<string, string> values)
            {
                long now = DateTime.UtcNow.Ticks; coordinator.ClockForTest = delegate { return now; };
                coordinator.Queue(true, true, new[] { game }, false, true);
                Check(coordinator.DrainForTest(3000), "match snapshot did not finish");
                object original = Latest(coordinator);
                Check(RequestTargets(original, "Games").Length == 0, "match requests retained unused game library snapshots");
                for (int i = 0; i < 1000; i++) coordinator.Queue(true, true, new[] { "changed" + i + ".exe", game }, false, true);
                Check(coordinator.DrainForTest(3000) && object.ReferenceEquals(original, Latest(coordinator)), "unused library changes repeatedly queued match work");
                coordinator.Queue(true, false, new[] { game }, false, true);
                Check(coordinator.DrainForTest(3000) && values.ContainsKey(game), "leaving a match failed to discover the current game library");
            });
            Console.WriteLine("PASS match library signature independent and current library discovered after exit");
        }
        private static void MatchSkipsLibraryEnumeration()
        {
            WithCoordinator(delegate(PerformanceActionCoordinator coordinator, string game, Dictionary<string, string> values)
            {
                var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode)); GC.SuppressFinalize(mode);
                typeof(GameMode).GetField("sync", Hidden).SetValue(mode, new object());
                typeof(GameMode).GetField("profileStore", Hidden).SetValue(mode, new GameProfileStore(Path.GetDirectoryName(game)));
                typeof(GameMode).GetField("performanceActions", Hidden).SetValue(mode, coordinator);
                typeof(GameMode).GetField("enabled", Hidden).SetValue(mode, true); typeof(GameMode).GetField("active", Hidden).SetValue(mode, true);
                MethodInfo sync = typeof(GameMode).GetMethod("SyncPerformanceAutomation", Hidden);
                try { sync.Invoke(mode, new object[] { null }); }
                catch (TargetInvocationException error)
                {
                    if (!(error.InnerException is NullReferenceException)) throw;
                    Check(false, "actual match path enumerated its unused library");
                }
                Check(coordinator.DrainForTest(3000) && RequestTargets(Latest(coordinator), "Games").Length == 0, "actual match path touched the deliberately absent unused library");
                typeof(GameMode).GetField("active", Hidden).SetValue(mode, false);
                typeof(GameMode).GetField("profiles", Hidden).SetValue(mode, new List<GameProfile> { new GameProfile { ExecutablePath = game } });
                sync.Invoke(mode, new object[] { null });
                Check(coordinator.DrainForTest(3000) && values.ContainsKey(game), "actual non match path failed to collect the current library");
            });
            Console.WriteLine("PASS actual GameMode match skips profile enumeration non match library remains live");
        }
        private static void TargetAdmissionKeepsTheExecutionBoundary()
        {
            WithCoordinator(delegate(PerformanceActionCoordinator coordinator, string game, Dictionary<string, string> values)
            {
                int reads = 0; long clock = DateTime.UtcNow.Ticks;
                GamePerformanceTargetPolicy.ClockForTest = delegate { return clock += TimeSpan.FromMinutes(10).Ticks; };
                GamePerformanceTargetPolicy.MetadataForTest = delegate { reads++; return null; };
                coordinator.Queue(true, false, new[] { game }, false, true);
                Check(coordinator.DrainForTest(3000), "target admission did not finish");
                Check(reads == 2 && values.ContainsKey(game), "target admission repeated preparation checks or lost the execution boundary");
            });
            WithCoordinator(delegate(PerformanceActionCoordinator coordinator, string game, Dictionary<string, string> values)
            {
                int reads = 0; long clock = DateTime.UtcNow.Ticks;
                GamePerformanceTargetPolicy.ClockForTest = delegate { return clock += TimeSpan.FromMinutes(10).Ticks; };
                GamePerformanceTargetPolicy.MetadataForTest = delegate { return ++reads == 1 ? null : new GamePerformanceTargetPolicy.Metadata { Company = "OpenAI", Product = "Codex" }; };
                coordinator.Queue(true, false, new[] { game }, false, true);
                Check(coordinator.DrainForTest(3000), "changed target boundary did not finish");
                Check(reads == 2 && values.Count == 0, "a target newly classified as protected work passed the execution boundary");
            });
            Console.WriteLine("PASS preparation policy calls3 -> 2 execution revalidation retained protected target writes0");
        }
    }
}
#endif
