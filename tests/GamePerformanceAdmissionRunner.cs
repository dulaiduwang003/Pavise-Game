#if PAVISE_SELFTEST && PAVISE_GAME_PERFORMANCE_ADMISSION
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

namespace PaviseApp
{
    internal static class GamePerformanceAdmissionRunner
    {
        private static int assertions, suites, failed;
        private static string output;
        private const string PackagedWork = @"C:\Program Files\WindowsApps\OpenAI.Codex_1.0.0.0_x64__testpublisher\app\ChatGPT.exe";
        private const string PackagedGame = @"C:\Program Files\WindowsApps\Example.Game_1.0.0.0_x64__publisher\Game.exe";
        private const string JavaRuntime = @"C:\Program Files\Java\jdk-21\bin\javaw.exe";
        private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int Main(string[] args)
        {
            if (args.Length != 1) return 2;
            output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
            Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
            using (var watchdog = new Timer(delegate { Environment.Exit(124); }, null, 30000, Timeout.Infinite))
            {
                Run("known work packages and renamed metadata reject library candidates", WorkIdentity);
                Run("packaged Electron and unknown games remain candidates", UnknownGames);
                Run("Java hosts remain library candidates without becoming persistent game targets", JavaCompatibility);
                Run("known work identity still rejects Java-named library candidates", JavaWorkIdentity);
                Run("known auxiliary roles are excluded without widening game directory scope", Helpers);
                Run("metadata cache bounds repeated reads and retries unreadable products", MetadataCache);
                Run("dirty library profiles cannot populate performance or FSO targets", LibraryAdmission);
                Run("real coordinator excludes work and Java hosts before CFG or driver discovery", CoordinatorAdmission);
                Run("execution rechecks admitted targets before game-only work", ProcessAdmission);
                Run("historical work and Java receipts restore despite the new deny boundary", HistoricalRestore);
                Console.WriteLine("RESULT suites=" + suites + " failed=" + failed + " assertions=" + assertions
                    + " settings=transient backend=mocked runtime_started=false native_writes=0");
                return failed == 0 ? 0 : 1;
            }
        }
        private static void Run(string name, Action test)
        {
            suites++;
            try { using (var fixture = new Hooks()) test(); Console.WriteLine("PASS " + name); }
            catch (Exception error)
            {
                while (error is TargetInvocationException && error.InnerException != null) error = error.InnerException;
                failed++; Console.Error.WriteLine("FAIL " + name + ": " + error);
            }
        }
        private static void Check(bool good, string message) { assertions++; if (!good) throw new InvalidOperationException(message); }
        private static GamePerformanceTargetPolicy.Metadata Work()
        { return new GamePerformanceTargetPolicy.Metadata { Company = "OpenAI OpCo, LLC", Product = "Codex" }; }
        private static void WorkIdentity()
        {
            Check(!GameSessionDetector.IsLibraryCandidate("ChatGPT", PackagedWork, @"C:\Windows\"), "Known OpenAI package could be auto-added");
            Check(!GamePerformanceTargetPolicy.AllowsGameTarget(PackagedWork.Replace("ChatGPT.exe", "Game.exe")), "Renamed executable escaped its work package identity");
            foreach (string product in new[] { "Codex", "ChatGPT" })
            {
                GamePerformanceTargetPolicy.ResetForTest();
                GamePerformanceTargetPolicy.MetadataForTest = delegate { return new GamePerformanceTargetPolicy.Metadata { Company = "OpenAI OpCo, LLC", Product = product }; };
                Check(!GameSessionDetector.IsLibraryCandidate("renamed-game", @"C:\Work\renamed-game.exe", @"C:\Windows\"), "Renamed known work product entered the automatic library");
                Check(!GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Work\renamed-game.exe"), "Known work metadata reached a game-only tuning target");
            }
            GamePerformanceTargetPolicy.ResetForTest();
            GamePerformanceTargetPolicy.MetadataForTest = delegate { return new GamePerformanceTargetPolicy.Metadata { Company = "Microsoft Corporation", Product = "Visual Studio Code" }; };
            Check(!GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Work\renamed-editor.exe"), "Known code editor entered game tuning");
        }
        private static void UnknownGames()
        {
            GamePerformanceTargetPolicy.MetadataForTest = delegate { return null; };
            foreach (string path in new[] { PackagedGame, @"C:\Games\ElectronGame\chrome.exe", @"C:\Games\left4dead2.exe", @"C:\Games\DeltaForceClient-Win64-Shipping.exe" })
                Check(GamePerformanceTargetPolicy.AllowsGameTarget(path), "Unknown game, browser-technology game or store game was blanket-blocked: " + path);
            Check(GamePerformanceTargetPolicy.KnownWorkMetadata(new GamePerformanceTargetPolicy.Metadata { Company = "Google LLC", Product = "ExampleGame", Original = "chrome.exe" }) == false,
                "Generic browser original filename was mistaken for a work product");
            GamePerformanceTargetPolicy.ResetForTest(); GamePerformanceTargetPolicy.MetadataForTest = delegate { throw new IOException("Unreadable metadata"); };
            Check(GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Games\Unknown.exe"), "Unavailable metadata blocked an otherwise unknown game");
            Check(!GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Windows\System32\dwm.exe"), "System image became a game-only target");
            Check(!GamePerformanceTargetPolicy.AllowsGameTarget("relative.exe"), "Relative target was admitted");
        }
        private static void Helpers()
        {
            foreach (string name in new[] { "UnityCrashHandler64", "crashpad_handler", "steamwebhelper", "GHelper", "obs64", "msedgewebview2", "powershell" })
                Check(!GameSessionDetector.IsLibraryCandidate(name, @"C:\Games\Title\" + name + ".exe", @"C:\Windows\"), "Known helper/work role entered the library: " + name);
            Check(GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Games\Title\Renderer.exe"), "Helper exclusion spilled to the entire game install directory");
        }
        private static void JavaCompatibility()
        {
            GamePerformanceTargetPolicy.MetadataForTest = delegate
            { return new GamePerformanceTargetPolicy.Metadata { Company = "Oracle Corporation", Product = "Java(TM) Platform SE" }; };
            foreach (string name in new[] { "java", "javaw", "JAVA", "JAVAW", "java8", "java-21", "javaw_17.0.1" })
            {
                string path = @"C:\Program Files\Java\bin\" + name + ".exe";
                Check(GameSessionDetector.IsLibraryCandidate(name, path, @"C:\Windows\"), "Java host was hidden from the running-program picker: " + name);
                Check(WhitelistRule.IsUnsafeFamilyAnchor(path), "Java host became a whitelist family anchor: " + name);
                WhitelistRule rule;
                Check(!WhitelistRule.TryCreate(WhitelistRuleKind.ApplicationFamily, path, out rule), "Java host could whitelist an entire process family: " + name);
                Check(WhitelistRule.TryCreate(WhitelistRuleKind.ExactPath, path, out rule), "Java host lost its exact-path whitelist option: " + name);
                Check(!GamePerformanceTargetPolicy.AllowsGameTarget(path), "Generic Java runtime became a persistent EXE game target: " + name);
            }
            foreach (string name in new[] { "java-game", "javaw-renderer", "javascript" })
            {
                string path = @"C:\Games\Title\" + name + ".exe";
                Check(!WhitelistRule.IsUnsafeFamilyAnchor(path), "Java runtime prefix widened to an unrelated executable: " + name);
                Check(GamePerformanceTargetPolicy.AllowsGameTarget(path), "Java host exclusion blocked an unrelated game: " + name);
            }
            Check(!GameSessionDetector.IsLibraryCandidate("javaw", @"C:\Windows\System32\javaw.exe", @"C:\Windows\"), "Java exception bypassed the Windows image boundary");
        }
        private static void JavaWorkIdentity()
        {
            GamePerformanceTargetPolicy.MetadataForTest = delegate { return Work(); };
            foreach (string name in new[] { "java", "javaw", "java-21", "javaw_17" })
            {
                string path = @"C:\Work\" + name + ".exe";
                Check(GamePerformanceTargetPolicy.KnownNonGame(name, path), "Java exception bypassed verified work metadata: " + name);
                Check(!GameSessionDetector.IsLibraryCandidate(name, path, @"C:\Windows\"), "Known work product renamed to Java entered the library: " + name);
                Check(!GamePerformanceTargetPolicy.AllowsGameTarget(path), "Known work product renamed to Java entered persistent game tuning: " + name);
            }
            string packagedJava = PackagedWork.Replace("ChatGPT.exe", "javaw.exe");
            Check(!GameSessionDetector.IsLibraryCandidate("javaw", packagedJava, @"C:\Windows\"), "Java exception bypassed verified work package identity");
        }
        private static void MetadataCache()
        {
            long now = DateTime.UtcNow.Ticks; int reads = 0;
            GamePerformanceTargetPolicy.ClockForTest = delegate { return now; };
            GamePerformanceTargetPolicy.MetadataForTest = delegate { reads++; return Work(); };
            for (int i = 0; i < 25; i++) Check(!GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Work\renamed.exe"), "Cached work product was admitted");
            Check(reads == 1, "Repeated scans read version metadata from disk every time");
            now += TimeSpan.FromMinutes(6).Ticks;
            Check(!GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Work\renamed.exe") && reads == 2, "Expired metadata was not revalidated");
            GamePerformanceTargetPolicy.ResetForTest(); reads = 0;
            GamePerformanceTargetPolicy.ClockForTest = delegate { return now; };
            GamePerformanceTargetPolicy.MetadataForTest = delegate { reads++; throw new IOException("Unavailable"); };
            Check(GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Game\Unknown.exe"), "Unreadable metadata must preserve unknown games");
            Check(GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Game\Unknown.exe") && reads == 1, "Unavailable metadata hot-looped read attempts");
            now += TimeSpan.FromSeconds(31).Ticks;
            Check(GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Game\Unknown.exe") && reads == 2, "Unavailable metadata never retried");
            for (int i = 0; i < 280; i++) GamePerformanceTargetPolicy.AllowsGameTarget(@"C:\Game\Unknown" + i + ".exe");
            object cache = typeof(GamePerformanceTargetPolicy).GetField("cache", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            Check((int)cache.GetType().GetProperty("Count").GetValue(cache, null) <= 256, "Metadata cache grew without a bound");
        }
        private static void LibraryAdmission()
        {
            string game = Path.Combine(output, "game.exe"), work = Path.Combine(output, "renamed-work.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, game, true); File.Copy(game, work, true);
            var javaPaths = new[] { Path.Combine(output, "javaw.exe"), Path.Combine(output, "java-21.exe") };
            foreach (string path in javaPaths) File.Copy(game, path, true);
            GamePerformanceTargetPolicy.MetadataForTest = delegate(string path) { return path == work ? Work() : null; };
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode)); GC.SuppressFinalize(mode);
            typeof(GameMode).GetField("windowsPrefix", Hidden).SetValue(mode, @"C:\Windows\");
            // 主循环只收集候选路径  执行器在实际配置前统一核验
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MethodInfo add = typeof(GameMode).GetMethod("AddGamePerformancePath", Hidden);
            add.Invoke(mode, new object[] { paths, work });
            foreach (string path in javaPaths) add.Invoke(mode, new object[] { paths, path });
            add.Invoke(mode, new object[] { paths, game });
            Check(paths.Count == 4 && paths.Contains(game), "Candidate collection dropped a library entry before deferred validation");
            using (var backend = new Backend())
            {
                Settings.Save(PerformanceActionCatalog.Mitigations, true);
                Settings.Save(PerformanceActionCatalog.OpenGl, true);
                var coordinator = new PerformanceActionCoordinator();
                try
                {
                    coordinator.Queue(true, false, new List<string>(paths).ToArray(), null, false, true);
                    Check(coordinator.DrainForTest(3000), "Deferred library admission did not finish");
                    Check(backend.ReadsFor(work) == 0 && backend.WritesFor(work) == 0,
                        "Dirty work profile reached game-only configuration after deferred validation");
                    foreach (string path in javaPaths)
                        Check(backend.ReadsFor(path) == 0 && backend.WritesFor(path) == 0,
                            "Dirty Java profile reached game-only configuration after deferred validation");
                    Check(backend.WritesFor(game) == 2, "Deferred validation blocked selected configuration for an ordinary game");
                }
                finally { Check(coordinator.Close(3000), "Library admission coordinator did not close"); }
            }
            paths.Clear(); MethodInfo addFso = typeof(GameMode).GetMethod("AddAutomaticFsoPath", Hidden);
            addFso.Invoke(mode, new object[] { paths, work });
            foreach (string path in javaPaths) addFso.Invoke(mode, new object[] { paths, path });
            Check(paths.Count == 0, "Dirty work or Java profile bypassed the persistent target boundary through FSO");
            addFso.Invoke(mode, new object[] { paths, game });
            Check(paths.Count == 1 && paths.Contains(game), "Persistent Java target exclusion blocked ordinary game FSO configuration");
        }
        private static void CoordinatorAdmission()
        {
            using (var backend = new Backend())
            {
                var coordinator = new PerformanceActionCoordinator();
                var targets = new[] { PackagedWork, JavaRuntime, @"C:\Java\bin\java-21.exe", @"C:\Games\left4dead2.exe" };
                // CFG 减负与 OpenGL 线程只按用户显式选择执行；未选择时守护开启也不写入。
                coordinator.Queue(true, false, targets, new string[0]);
                Check(coordinator.DrainForTest(3000), "Fake coordinator did not drain");
                Check(backend.WritesFor(@"C:\Games\left4dead2.exe") == 0, "Unselected CFG/OpenGL actions were written for an ordinary game");
                Settings.Save(PerformanceActionCatalog.Mitigations, true); Settings.Save(PerformanceActionCatalog.OpenGl, true);
                coordinator.Queue(true, false, targets, new string[0]);
                Check(coordinator.DrainForTest(3000), "Fake coordinator did not drain");
                Check(backend.ReadsFor(PackagedWork) == 0 && backend.WritesFor(PackagedWork) == 0, "Work app was queried or written before coordinator denial");
                Check(backend.ReadsFor(JavaRuntime) == 0 && backend.WritesFor(JavaRuntime) == 0, "Java runtime was queried or written before coordinator denial");
                Check(backend.ReadsFor(@"C:\Java\bin\java-21.exe") == 0 && backend.WritesFor(@"C:\Java\bin\java-21.exe") == 0, "Versioned Java runtime bypassed coordinator denial");
                Check(backend.WritesFor(@"C:\Games\left4dead2.exe") == 2, "Filtering disabled CFG/OpenGL for an ordinary game");
                Check(coordinator.Close(1000), "Fake coordinator did not close");
            }
        }
        private static void ProcessAdmission()
        {
            using (var backend = new Backend())
            {
                var coordinator = new PerformanceActionCoordinator();
                // 选择动作，否则 Process 直接返回，下面的拒绝断言将不再有意义。
                Settings.Save(PerformanceActionCatalog.Mitigations, true); Settings.Save(PerformanceActionCatalog.OpenGl, true);
                const string OrdinaryGame = @"C:\Games\left4dead2.exe";
                Type requestType = typeof(PerformanceActionCoordinator).GetNestedType("Request", BindingFlags.NonPublic);
                object request = Activator.CreateInstance(requestType, true);
                foreach (var field in new Dictionary<string, object> { { "Guard", true }, { "Match", false }, { "Games", new[] { PackagedWork, JavaRuntime, OrdinaryGame } }, { "Background", new string[0] } })
                    requestType.GetField(field.Key, Hidden).SetValue(request, field.Value);
                typeof(PerformanceActionCoordinator).GetField("latest", Hidden).SetValue(coordinator, request);
                typeof(PerformanceActionCoordinator).GetMethod("Process", Hidden).Invoke(coordinator, new[] { request });
                Check(backend.ReadsFor(PackagedWork) == 0 && backend.WritesFor(PackagedWork) == 0, "Execution trusted a stale already-admitted work-app target");
                Check(backend.ReadsFor(JavaRuntime) == 0 && backend.WritesFor(JavaRuntime) == 0, "Execution trusted a stale already-admitted Java target");
                Check(backend.WritesFor(OrdinaryGame) == 2, "Execution did not run the selected game-only work for an admitted ordinary game");
            }
        }
        private static void HistoricalRestore()
        {
            using (var backend = new Backend())
            {
                Check(PerformanceActionBackend.Apply(PerformanceActionCatalog.Mitigations, PackagedWork, "off64"), "Could not seed the fake historical receipt");
                Check(PerformanceActionBackend.Apply(PerformanceActionCatalog.Mitigations, JavaRuntime, "off64"), "Could not seed the fake historical Java mitigation receipt");
                Check(PerformanceActionBackend.Apply(PerformanceActionCatalog.OpenGl, JavaRuntime, "1"), "Could not seed the fake historical Java OpenGL receipt");
                Check(!GamePerformanceTargetPolicy.AllowsGameTarget(PackagedWork), "Historical test app was not actually denied");
                Check(!GamePerformanceTargetPolicy.AllowsGameTarget(JavaRuntime), "Historical Java runtime was not actually denied");
                Check(PerformanceTuning.RestoreAllFailures().Count == 0, "New admission filter blocked restoration of historical work or Java receipts");
                Check(PerformanceActionStore.All().Count == 0 && backend.WritesFor(PackagedWork) == 2 && backend.WritesFor(JavaRuntime) == 4,
                    "Historical owned work or Java settings were not restored and cleared");
            }
        }
        private sealed class Hooks : IDisposable
        {
            internal Hooks() { Settings.UseTransientStoreForCurrentProcess(); PerformanceTuning.ResetForTest(); GamePerformanceTargetPolicy.ResetForTest(); }
            public void Dispose() { GamePerformanceTargetPolicy.ResetForTest(); Settings.UseTransientStoreForCurrentProcess(); }
        }
        private sealed class Backend : IDisposable
        {
            private readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), writes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            internal Backend()
            {
                PerformanceActionBackend.DefenderServiceForTest = delegate { return 0; };
                PerformanceActionBackend.ShortNameVolumeForTest = delegate { return null; };
                PerformanceActionBackend.ReadForTest = delegate(string id, string path)
                {
                    Increment(reads, path); string value;
                    return values.TryGetValue(id + "\n" + path, out value) ? value : id == PerformanceActionCatalog.Mitigations
                        ? "NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False;NOTSET:False" : "absent";
                };
                PerformanceActionBackend.WriteForTest = delegate(string id, string path, string value)
                {
                    Increment(writes, path);
                    if (value == "off64") value = "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;NOTSET:False";
                    if (value == "off32") value = "OFF:False;OFF:False;OFF:False;OFF:False;OFF:False;OFF:False";
                    values[id + "\n" + path] = value; return true;
                };
            }
            private static void Increment(Dictionary<string, int> counts, string path) { int n; counts.TryGetValue(path, out n); counts[path] = n + 1; }
            internal int ReadsFor(string path) { int n; return reads.TryGetValue(path, out n) ? n : 0; }
            internal int WritesFor(string path) { int n; return writes.TryGetValue(path, out n) ? n : 0; }
            public void Dispose() { PerformanceActionBackend.ReadForTest = null; PerformanceActionBackend.WriteForTest = null; PerformanceActionBackend.DefenderServiceForTest = null; PerformanceActionBackend.ShortNameVolumeForTest = null; }
        }
    }
    internal static partial class SelfTests
    {
        public static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Admission tests cannot start optimization runtime"); }
    }
}
#endif
