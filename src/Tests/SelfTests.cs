// @author bdth 2074055628@qq.com
// 文件用途 运行不依赖测试框架的项目自测

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AegisApp
{
    internal static partial class SelfTests
    {
        private sealed class TestSkippedException : Exception
        {
            public TestSkippedException(string reason) : base(reason) { }
        }

        public static bool TryHandleRuntimeMode(string[] args)
        {
            if (args == null || args.Length == 0) return false;
            if (args[0] == "--freeze-watchdog" && args.Length >= 4)
            {
                int owner; long creation;
                if (int.TryParse(args[1], out owner) && long.TryParse(args[2], out creation))
                    FreezeGuard.WatchOwnerAndRestore(owner, creation, args[3]);
                return true;
            }
            if (args[0] == "--freeze-probe" && args.Length >= 2)
            {
                RunProbe(args[1]);
                return true;
            }
            if (args[0] == "--cpu-burn")
            {
                RunCpuBurn();
                return true;
            }
            if (args[0] == "--freeze-crash-parent" && args.Length >= 3)
            {
                int pid;
                if (!int.TryParse(args[1], out pid)) { Environment.ExitCode = 2; return true; }
                RunCrashParent(pid, args[2]);
                return true;
            }
            if (args[0] == "--selftest")
            {
                string report = args.Length >= 2 ? args[1] : Path.Combine(Path.GetTempPath(), "Aegis.selftest.txt");
                Run(report);
                return true;
            }
            if (args[0] == "--config-screenshot" && args.Length >= 2)
            {
                RunConfigScreenshot(args[1], args.Length >= 3 ? args[2] : "zh");
                return true;
            }
            if (args[0] == "--detector-probe" && args.Length >= 4)
            {
                int pid;
                if (!int.TryParse(args[1], out pid)) { Environment.ExitCode = 2; return true; }
                RunDetectorProbe(pid, args[2], args[3]);
                return true;
            }
            if (args[0] == "--live-repro" && args.Length >= 4)
            {
                RunLiveRepro(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
                return true;
            }
            if (args[0] == "--detect-live" && args.Length >= 2)
            {
                RunDetectLive(args[1], args.Length >= 3 ? args[2] : null);
                return true;
            }
            if (args[0] == "--profile-probe" && args.Length >= 3)
            {
                try
                {
                    var store = new GameProfileStore(args[1]);
                    List<GameProfile> profiles = store.LoadOrMigrate(Path.Combine(args[1], "Aegis.games.txt"));
                    int entries = 0;
                    foreach (GameProfile profile in profiles) entries += profile.Entries.Count;
                    File.WriteAllText(args[2], "PROFILES=" + profiles.Count + "\r\nENTRIES=" + entries
                        + "\r\nFORMAT=V2", Encoding.UTF8);
                    Environment.ExitCode = 0;
                }
                catch (Exception ex)
                {
                    File.WriteAllText(args[2], "ERROR=" + ex.Message, Encoding.UTF8);
                    Environment.ExitCode = 1;
                }
                return true;
            }
            return false;
        }

        private static void RunLiveRepro(string scratchDir, string exePath, string displayName, string output)
        {
            var sb = new System.Text.StringBuilder();
            Process probe = null;
            GameMode mode = null;
            string prevLogPath = Logger.LogPath;
            try
            {
                Directory.CreateDirectory(scratchDir);
                Logger.LogPath = Path.Combine(scratchDir, "repro.log");
                mode = new GameMode(scratchDir, new SuppressionCore(Path.Combine(scratchDir, "repro.state")));
                if (!mode.AddGameExecutable(displayName, exePath))
                {
                    sb.AppendLine("AddGameExecutable 失败：" + exePath);
                    return;
                }
                sb.AppendLine("已注册目标：" + displayName + " -> " + exePath);
                sb.AppendLine("当前生效预设（读共享注册表）：" + mode.ActivePreset);

                probe = Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
                if (probe == null) { sb.AppendLine("目标进程启动失败"); return; }
                Thread.Sleep(500);
                probe.Refresh();
                sb.AppendLine("目标进程已启动 pid=" + probe.Id);

                mode.Start();
                mode.Enabled = true;

                for (int i = 0; i < 8; i++)
                {
                    Thread.Sleep(4300);
                    string state = "pid 已失效";
                    IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, probe.Id);
                    if (h != IntPtr.Zero)
                    {
                        try
                        {
                            uint pri = Native.GetPriorityClass(h);
                            int ctrl = 0, st = 0;
                            bool ecoOk = Native.TryQueryPowerThrottling(h, out ctrl, out st);
                            state = "优先级=0x" + pri.ToString("X") + (ecoOk ? " EcoQoS(ctrl=" + ctrl + ",state=" + st + ")" : " EcoQoS读取失败");
                        }
                        finally { Native.CloseHandle(h); }
                    }
                    sb.AppendLine("[第 " + (i + 1) + " 轮] IsActive=" + mode.IsActive + " ActiveGame=" + mode.ActiveGame + " | 目标进程 " + state);
                }

                mode.Enabled = false;
                Thread.Sleep(1200);
                sb.AppendLine();
                sb.AppendLine("=== repro.log 全文 ===");
                try { sb.AppendLine(File.ReadAllText(Logger.LogPath)); } catch { }
            }
            catch (Exception ex) { sb.AppendLine("异常：" + ex); }
            finally
            {
                try { if (mode != null) { mode.Enabled = false; mode.Stop(); } } catch { }
                try { if (probe != null && !probe.HasExited) probe.Kill(); } catch { }
                Logger.LogPath = prevLogPath;
            }
            string text = sb.ToString();
            try { if (output != null) File.WriteAllText(output, text, Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static void RunDetectLive(string dataDir, string output)
        {
            var sb = new System.Text.StringBuilder();
            try
            {
                var store = new GameProfileStore(dataDir);
                List<GameProfile> profiles = store.LoadOrMigrate(Path.Combine(dataDir, "Aegis.games.txt"));
                Process[] all = Process.GetProcesses();
                int fg = GameSessionDetector.ForegroundPid();
                sb.AppendLine("foreground pid=" + fg);
                GameDetection hit = GameSessionDetector.Detect(all, profiles);
                sb.AppendLine("DETECT RESULT: " + (hit == null ? "NULL (无活动游戏)"
                    : hit.Profile.Name + " | renderer=" + hit.RendererName + " pid=" + hit.RendererPid));
                sb.AppendLine();
                foreach (GameProfile profile in profiles)
                {
                    sb.AppendLine("=== profile: " + profile.Name + " (exe=" + profile.ExecutablePath + ")");
                    foreach (Process p in all)
                    {
                        try
                        {
                            int pid = p.Id;
                            string path = null;
                            IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                            if (h != IntPtr.Zero) { try { path = Native.ImagePath(h); } finally { Native.CloseHandle(h); } }
                            if (path == null || !profile.ContainsPath(path)) continue;
                            bool visible = GameSessionDetector.HasUserFacingWindow(p);
                            bool foreground = pid == fg;
                            int score = GameSessionDetector.Score(profile, p.ProcessName, path, visible, foreground);
                            bool qual = GameSessionDetector.QualifiesRenderer(profile, p.ProcessName, path, visible, foreground);
                            sb.AppendLine("   " + p.ProcessName + " pid=" + pid + " vis=" + visible
                                + " fg=" + foreground + " score=" + score + " renderer=" + qual);
                        }
                        catch { }
                    }
                }
                foreach (Process p in all) { try { p.Dispose(); } catch { } }
            }
            catch (Exception ex) { sb.AppendLine("ERROR: " + ex); }
            string text = sb.ToString();
            try { if (output != null) File.WriteAllText(output, text, Encoding.UTF8); } catch { }
            Environment.ExitCode = 0;
        }

        private static void RunDetectorProbe(int pid, string configuredRoot, string output)
        {
            Process[] all = null;
            try
            {
                using (Process target = Process.GetProcessById(pid))
                {
                    var profile = GameProfileStore.NewProfile("DetectorProbe", configuredRoot);
                    profile.Entries.Add(target.ProcessName);
                    all = Process.GetProcesses();
                    GameDetection hit = GameSessionDetector.Detect(all, new[] { profile });
                    string result = hit == null ? "NONE" : (hit.RendererPid > 0 ? "MATCH" : "SESSION") + "|" + hit.RendererName + "|" + hit.RendererPath;
                    File.WriteAllText(output, result, Encoding.UTF8);
                    Environment.ExitCode = hit == null ? 0 : 3;
                }
            }
            catch (Exception ex) { File.WriteAllText(output, "ERROR|" + ex.Message, Encoding.UTF8); Environment.ExitCode = 4; }
            finally { if (all != null) foreach (Process p in all) p.Dispose(); }
        }

        private static void RunConfigScreenshot(string output, string language)
        {
            string data = Path.Combine(Path.GetTempPath(), "AegisConfigShot_" + Process.GetCurrentProcess().Id);
            try
            {
                Directory.CreateDirectory(data);
                Dpi.Init();
                Lang.Init();
                Lang.Cur = language == "en" ? 1 : (language == "ja" ? 2 : 0);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var dlg = new GameModeConfigDialog(new GameMode(data, new SuppressionCore())))
                {
                    dlg.StartPosition = FormStartPosition.Manual;
                    dlg.Location = new Point(-20000, -20000);
                    dlg.Show();
                    DateTime paintReady = DateTime.UtcNow.AddMilliseconds(450);
                    while (DateTime.UtcNow < paintReady)
                    {
                        Application.DoEvents();
                        Thread.Sleep(15);
                    }
                    using (var bmp = new Bitmap(dlg.ClientSize.Width, dlg.ClientSize.Height))
                    {
                        dlg.DrawToBitmap(bmp, new Rectangle(Point.Empty, dlg.ClientSize));
                        bmp.Save(output, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    dlg.Hide();
                }
            }
            finally { try { Directory.Delete(data, true); } catch { } }
        }

        private static void Run(string reportPath)
        {
            var log = new List<string>();
            int passed = 0, failed = 0, skipped = 0;
            double contentionGain = 0;
            Action<string, Action> test = (name, body) =>
            {
                try { body(); log.Add("PASS  " + name); passed++; }
                catch (TestSkippedException ex) { log.Add("SKIP  " + name + " :: " + ex.Message); skipped++; }
                catch (Exception ex) { log.Add("FAIL  " + name + " :: " + ex.Message); failed++; }
            };

            test("strict mask: ordinary CPU partitions background cores", () =>
                Eq(0x3FUL, CpuPartitionPolicy.StrictMask(0xFF, 0xC0, 0, 0)));
            test("strict mask: hybrid prefers reported P cores", () =>
                Eq(0x0FUL, CpuPartitionPolicy.StrictMask(0xFF, 0xF0, 0x0F, 0)));
            test("strict mask: X3D cache CCD wins", () =>
                Eq(0xF0UL, CpuPartitionPolicy.StrictMask(0xFF, 0x0F, 0, 0xF0)));
            test("strict mask: invalid empty partition falls back all", () =>
                Eq(0x03UL, CpuPartitionPolicy.StrictMask(0x03, 0x03, 0, 0)));
            test("CPU tiering: low-core homogeneous CPUs are never hard-partitioned", () =>
            {
                Eq(0, CpuPartitionPolicy.BackgroundCoreCount(4));
                Eq(0, CpuPartitionPolicy.BackgroundCoreCount(6));
            });
            test("CPU tiering: high-core homogeneous CPUs reserve proportionally", () =>
            {
                Eq(1, CpuPartitionPolicy.BackgroundCoreCount(8));
                Eq(1, CpuPartitionPolicy.BackgroundCoreCount(10));
                Eq(2, CpuPartitionPolicy.BackgroundCoreCount(12));
                Eq(3, CpuPartitionPolicy.BackgroundCoreCount(24));
                Eq(4, CpuPartitionPolicy.BackgroundCoreCount(64));
            });
            test("resource sampler: threshold and PID reuse", TestSampler);
            test("extreme exclusions: anti-cheat names are case-insensitive", () =>
            {
                Eq(true, AntiCheatCatalog.IsKnownProcess("VGC"));
                Eq(true, AntiCheatCatalog.IsKnownProcess("EasyAntiCheat_EOS"));
                Eq(true, AntiCheatCatalog.IsKnownProcess("ace-helper"));
                Eq(false, AntiCheatCatalog.IsKnownProcess("ordinary-app"));
            });
            test("game family: generic multi-folder layouts share one protected root", TestMultiFolderGameRoot);
            test("game catalog: protected root survives save format and legacy entries", TestGameCatalogFormat);
            test("render detector: Office and launchers cannot masquerade as games", TestRenderScoring);
            test("release metadata: product and file versions are present", TestReleaseMetadata);
            test("mode theme: graphite stays fixed while Standard, Competitive and Custom accents differ", () =>
            {
                Color bg = Theme.Bg;
                if (Theme.ModeColor(PerformancePreset.Standard) == Theme.ModeColor(PerformancePreset.Competitive)) throw new Exception("Standard and Competitive accents match");
                if (Theme.ModeColor(PerformancePreset.Competitive) == Theme.ModeColor(PerformancePreset.Custom)) throw new Exception("Competitive and Custom accents match");
                Theme.SetMode(PerformancePreset.Competitive, false);
                Eq(Theme.ModeColor(PerformancePreset.Competitive), Theme.Accent);
                Eq(bg, Theme.Bg);
                Theme.SetMode(PerformancePreset.Custom, true);
                Color start = Theme.Accent;
                Theme.StepTheme();
                if (Theme.Accent == start || Theme.Accent == Theme.ModeColor(PerformancePreset.Custom)) throw new Exception("theme transition did not interpolate");
                while (Theme.StepTheme()) { }
                Eq(Theme.ModeColor(PerformancePreset.Custom), Theme.Accent);
                Theme.SetMode(PerformancePreset.Standard, false);
            });
            test("runtime icon: tray artwork fills the canvas and changes with effective mode", TestModeIcons);
            test("dashboard motion: independent layers advance between frames", TestDashboardMotion);
            test("high-DPI typography: body sizes land on whole device pixels from 100% to 200%", () =>
            {
                float old = Dpi.Scale;
                try
                {
                    foreach (float scale in new[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
                    {
                        Dpi.Scale = scale;
                        foreach (float size in new[] { 6.75f, 7.5f, 8.25f, 9.5f, 10f, 14.5f })
                        {
                            double pixels = Dpi.CrispPoint(size) * scale * 96d / 72d;
                            if (Math.Abs(pixels - Math.Round(pixels)) > 0.001d)
                                throw new Exception(size + "pt is fractional at " + scale);
                        }
                    }
                }
                finally { Dpi.Scale = old; }
            });
            test("background controller: sustained pressure escalates and cools down", TestPressureController);
            test("preset policy: eligible background escalates and Competitive isolates immediately", TestPresetBackgroundPolicy);
            test("background boundary: foreground and user-facing apps stay protected", TestBackgroundBoundary);
            test("game protection: sticky-launcher guard and any-process targeting", TestGameProtectionRedesign);
            test("CPU Sets: game and background partitions never overlap", TestCpuSetPartition);
            test("DPC policy: only sustained outlier cores are avoided", () =>
            {
                Eq(-1, DpcSampler.FindNoisy(new[] { 0.01, 0.02, 0.015, 0.01 }));
                Eq(2, DpcSampler.FindNoisy(new[] { 0.01, 0.02, 0.20, 0.01 }));
                var sampler = new DpcSampler();
                sampler.ObserveCandidate(2);
                Eq(0UL, sampler.NoisyPhysicalMask);
                sampler.ObserveCandidate(2);
                if (sampler.NoisyPhysicalMask == 0) throw new Exception("sustained DPC spike was not activated");
                sampler.ObserveCandidate(-1);
                if (sampler.NoisyPhysicalMask == 0) throw new Exception("DPC avoidance cooled down too early");
                sampler.ObserveCandidate(-1);
                Eq(0UL, sampler.NoisyPhysicalMask);
            });

            string root = Path.Combine(Path.GetTempPath(), "AegisSelfTest_" + Process.GetCurrentProcess().Id + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                test("freeze guard: expected-name mismatch is rejected", () => TestNameMismatch(root));
                test("game catalog: legacy entry upgrades to a persisted install root", () => TestGameCatalogUpgrade(root));
                test("game profiles: migration removes learning state and deduplicates", () => TestProfileStore(root));
                test("game library: EXE/LNK resolve without executing the target", () => TestExecutableResolver(root));
                test("LoL Cross cleaner: running-client guard and scoped deletion", () => TestLolCrossCleaner(root));
                test("render detector: user-selected headless exe activates; legacy headless does not", () => TestHeadlessEntry(root));
                test("render detector: bootstrap launcher's real process outside profile root is still protected", () => TestFallbackEntryOutsideRoot(root));
                test("session reports: legacy frame telemetry is archived", () => TestReportMigration(root));
                test("renderer boost: HIGH priority and IO3 are verified by readback", () => TestBoostReadback(root));
                test("freeze journal: corrupt data is retained and blocks overwrite", () => TestCorruptJournal(root));
                test("freeze journal: PID reuse identity is never resumed", () => TestPidReuseJournal(root));
                test("freeze guard: real child suspend and exact restore", () => TestNormalFreeze(root));
                test("freeze watchdog: owner exit restores real child", () => TestCrashRecovery(root));
                test("freeze watchdog: reused owner PID restores without waiting", () => TestReusedOwnerRecovery(root));
                test("strict placement: hard-affinity fallback restores exactly", () => TestAffinityRestore(root));
                test("CPU Sets: pre-existing process policy restores exactly", () => TestExistingCpuSetRestore(root));
                test("staged suppression: queryable state and CPU Sets restore", () => TestStagedSuppression(root));
                test("competitive suppression: target resets are re-applied", () => TestSuppressionReapply(root));
                test("staged suppression: crash journal restores a live process", () => TestSuppressionCrashRecovery(root));
                test("synthetic contention: freezing a same-core contender restores CPU time", () =>
                {
                    contentionGain = TestContentionGain(root);
                    if (contentionGain < 1.20d) throw new Exception("measured gain only " + contentionGain.ToString("0.00") + "x");
                });
            }
            finally { try { Directory.Delete(root, true); } catch { } }

            log.Insert(0, "Aegis " + App.Version + " self-test @ " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.Add("");
            if (contentionGain > 0) log.Add("SYNTHETIC same-core contention -> frozen throughput ratio: " + contentionGain.ToString("0.00") + "x");
            log.Add("TOTAL " + (passed + failed + skipped) + "  PASS " + passed
                + "  FAIL " + failed + "  SKIP " + skipped);
            try
            {
                string dir = Path.GetDirectoryName(reportPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(reportPath, log.ToArray(), Encoding.UTF8);
            }
            catch { }
            Environment.ExitCode = failed == 0 ? 0 : 1;
        }

        private static void TestSampler()
        {
            var s = new InterferenceSampler(0.35, 32d * 1024d * 1024d);
            long t0 = TimeSpan.TicksPerSecond * 100;
            Eq(InterferenceKind.None, s.Observe(7, "worker", 11, 100, 1000, t0).Kind);
            Eq(InterferenceKind.None, s.Observe(7, "worker", 11,
                100 + (long)(TimeSpan.TicksPerSecond * 4 * 0.2), 1000, t0 + TimeSpan.TicksPerSecond * 4).Kind);
            long t1 = t0 + TimeSpan.TicksPerSecond * 8;
            long cpu = 100 + (long)(TimeSpan.TicksPerSecond * 4 * 0.7);
            ulong io = 1000 + (ulong)(40d * 1024d * 1024d * 4d);
            Eq(InterferenceKind.Cpu | InterferenceKind.Io, s.Observe(7, "worker", 11, cpu, io, t1).Kind);
            Eq(InterferenceKind.None, s.Observe(7, "worker", 12, cpu + TimeSpan.TicksPerSecond * 9, io, t1 + TimeSpan.TicksPerSecond * 4).Kind);
        }

        private static void TestReleaseMetadata()
        {
            Version assemblyVersion = typeof(App).Assembly.GetName().Version;
            Eq("1.0.0.0", assemblyVersion == null ? "" : assemblyVersion.ToString());
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(Application.ExecutablePath);
            Eq("1.0.0.0", info.FileVersion);
            Eq("Aegis", info.ProductName);
            Eq("bdth", info.CompanyName);
        }

        private static void TestRenderScoring()
        {
            var profile = GameProfileStore.NewProfile("Example", Path.Combine(Path.GetTempPath(), "ExampleGame"));
            profile.Entries.Add("ExampleGame");
            string game = Path.Combine(profile.Root, "Binaries", "Win64", "ExampleGame-Win64-Shipping.exe");
            string launcher = Path.Combine(profile.Root, "Launcher", "ExampleLauncher.exe");
            Eq(true, profile.ContainsPath(game));
            Eq(false, profile.ContainsPath(Path.Combine(profile.Root + "-backup", "game.exe")));
            if (GameSessionDetector.Score(profile, "ExampleGame", game, true, true) < 65) throw new Exception("game candidate score too low");
            if (GameSessionDetector.Score(profile, "ExampleLauncher", launcher, true, true) >= 65) throw new Exception("launcher activated profile");
            Eq(true, GameSessionDetector.QualifiesRenderer(profile, "ExampleGame", game, true, true));
            Eq(false, GameSessionDetector.QualifiesRenderer(profile, "ExampleLauncher", launcher, true, true));
            Eq(-1000, GameSessionDetector.Score(profile, "POWERPNT", @"C:\Program Files\Microsoft Office\POWERPNT.EXE", true, true));
            Eq(-1000, GameSessionDetector.Score(profile, "ACE-Helper", Path.Combine(profile.Root, "ACE-Helper.exe"), true, true));
            Eq(-1000, GameSessionDetector.Score(profile, "LeagueClientUxRender", Path.Combine(profile.Root, "LeagueClient", "LeagueClientUxRender.exe"), true, true));
        }

        private static void TestPressureController()
        {
            var c = new BackgroundPressureController();
            long second = TimeSpan.TicksPerSecond;
            long cpu = 0;
            Eq(SuppressionLevel.None, c.Observe(8, "worker", 1, cpu, 0, 100 * second, PerformancePreset.Standard));
            cpu += (long)(second * 4 * 0.10);
            Eq(SuppressionLevel.Eco, c.Observe(8, "worker", 1, cpu, 0, 104 * second, PerformancePreset.Standard));
            cpu += (long)(second * 4 * 0.10);
            Eq(SuppressionLevel.Restrained, c.Observe(8, "worker", 1, cpu, 0, 108 * second, PerformancePreset.Standard));
            cpu += (long)(second * 4 * 0.10);
            Eq(SuppressionLevel.Isolated, c.Observe(8, "worker", 1, cpu, 0, 112 * second, PerformancePreset.Standard));
            Eq(SuppressionLevel.Isolated, c.Observe(8, "worker", 1, cpu, 0, 116 * second, PerformancePreset.Standard));
            Eq(SuppressionLevel.Restrained, c.Observe(8, "worker", 1, cpu, 0, 120 * second, PerformancePreset.Standard));
            Eq(SuppressionLevel.None, c.Observe(8, "worker", 2, 99 * second, 0, 124 * second, PerformancePreset.Standard));
        }

        private static void TestPresetBackgroundPolicy()
        {
            Eq(SuppressionLevel.Eco, GameMode.ResolveBackgroundLevel(PerformancePreset.Standard, false, SuppressionLevel.None, true));
            Eq(SuppressionLevel.Restrained, GameMode.ResolveBackgroundLevel(PerformancePreset.Standard, false, SuppressionLevel.Restrained, true));
            Eq(SuppressionLevel.Isolated, GameMode.ResolveBackgroundLevel(PerformancePreset.Competitive, false, SuppressionLevel.None, true));
            Eq(SuppressionLevel.Isolated, GameMode.ResolveBackgroundLevel(PerformancePreset.Competitive, false, SuppressionLevel.None, false));
            Eq(SuppressionLevel.Eco, GameMode.ResolveBackgroundLevel(PerformancePreset.Custom, false, SuppressionLevel.Isolated, true));
            Eq(SuppressionLevel.Isolated, GameMode.ResolveBackgroundLevel(PerformancePreset.Custom, true, SuppressionLevel.None, true));
            Eq(SuppressionLevel.Isolated, GameMode.ResolveBackgroundLevel(PerformancePreset.Custom, true, SuppressionLevel.None, false));

            Eq(true, GameMode.IsAggressive(PerformancePreset.Competitive, false));
            Eq(true, GameMode.IsAggressive(PerformancePreset.Competitive, true));
            Eq(false, GameMode.IsAggressive(PerformancePreset.Standard, true));
            Eq(false, GameMode.IsAggressive(PerformancePreset.Custom, false));
            Eq(true, GameMode.IsAggressive(PerformancePreset.Custom, true));
        }

        private static void TestModeIcons()
        {
            long standard, competitive, custom;
            using (Bitmap s = IconArt.Render(32, PerformancePreset.Standard, true)) standard = IconFingerprint(s, true);
            using (Bitmap c = IconArt.Render(32, PerformancePreset.Competitive, true)) competitive = IconFingerprint(c, false);
            using (Bitmap x = IconArt.Render(32, PerformancePreset.Custom, true)) custom = IconFingerprint(x, false);
            if (standard == competitive || competitive == custom || standard == custom) throw new Exception("mode icons are not visually distinct");
        }

        private static void TestDashboardMotion()
        {
            Theme.SetMode(PerformancePreset.Competitive, false);
            try
            {
                using (var core = new AegisCore())
                using (var first = new Bitmap(360, 342))
                using (var second = new Bitmap(360, 342))
                {
                    core.SetBounds(0, 0, 360, 342);
                    core.SetState(PerformancePreset.Competitive, true, true);
                    core.CreateControl();
                    core.SetAnimationEnabled(false);
                    core.DrawToBitmap(first, new Rectangle(0, 0, first.Width, first.Height));
                    Thread.Sleep(175);
                    core.DrawToBitmap(second, new Rectangle(0, 0, second.Width, second.Height));

                    int changed = 0;
                    for (int y = 0; y < first.Height; y += 2)
                        for (int x = 0; x < first.Width; x += 2)
                            if (first.GetPixel(x, y).ToArgb() != second.GetPixel(x, y).ToArgb()) changed++;
                    if (changed < 180) throw new Exception("only " + changed + " sampled pixels changed");
                }
            }
            finally { Theme.SetMode(PerformancePreset.Standard, false); }
        }

        private static long IconFingerprint(Bitmap bitmap, bool verifyBounds)
        {
            int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1;
            long hash = 17;
            for (int y = 0; y < bitmap.Height; y++)
                for (int x = 0; x < bitmap.Width; x++)
                {
                    Color p = bitmap.GetPixel(x, y);
                    hash = unchecked(hash * 31 + p.ToArgb());
                    if (p.A > 12) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
                }
            if (verifyBounds && (maxX - minX + 1 < bitmap.Width - 1 || maxY - minY + 1 < bitmap.Height - 1))
                throw new Exception("tray artwork still has excessive transparent padding");
            return hash;
        }

        private static void TestCpuSetPartition()
        {
            if (!CpuTopology.HasSafeBackgroundPartition()) Skip("no safe background CPU Set partition");
            uint[] background = CpuTopology.BackgroundCpuSetIds();
            uint[] game = CpuTopology.AdaptiveGameCpuSetIds(true);
            if (background == null || game == null) throw new Exception("partition was reported safe without CPU Set IDs");
            var occupied = new HashSet<uint>(background);
            foreach (uint id in game) if (occupied.Contains(id)) throw new Exception("game/background CPU Set overlap: " + id);
            if (CpuTopology.Hybrid)
            {
                uint[] expectedPerformance = CpuTopology.CpuSetIdsFor(CpuTopology.PerfMask);
                if (expectedPerformance != null)
                    Eq(expectedPerformance.Length, game.Length);
            }
        }

        private static void TestBackgroundBoundary()
        {
            const string win = @"C:\Windows\";
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 10, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 20, true, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"C:\Windows\worker.exe", 1, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 0, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "discord", @"D:\Apps\discord.exe", 1, 1, 20, true, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "EasyAntiCheat_EOS", @"D:\Games\eac.exe", 1, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "SGuard64", @"D:\WeGame\SGuard64.exe", 1, 1, 20, false, win));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win, true, null));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "railhelper", @"D:\LoL\TCLS\rail.exe", 1, 1, 20, false, win, false, @"D:\LoL\"));

            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "worker", @"D:\Apps\worker.exe", 1, 1, 10, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "discord", @"D:\Apps\discord.exe", 1, 1, 20, true, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "dwm", @"C:\Windows\System32\dwm.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "lsass", @"C:\Windows\System32\lsass.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "audiodg", @"C:\Windows\System32\audiodg.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "explorer", @"C:\Windows\explorer.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "SearchIndexer", @"C:\Windows\System32\SearchIndexer.exe", 1, 1, 20, false, win, false, null, true));
            Eq(true, GameMode.BasicBackgroundEligible(10, 99, "svchost", @"D:\Malware\svchost.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "SGuard64", @"D:\WeGame\SGuard64.exe", 1, 1, 20, false, win, false, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "wegame", @"C:\WeGame\wegame.exe", 1, 1, 20, false, win, true, null, true));
            Eq(false, GameMode.BasicBackgroundEligible(10, 99, "railhelper", @"D:\LoL\TCLS\rail.exe", 1, 1, 20, false, win, false, @"D:\LoL\", true));

            var parents = new Dictionary<int, int> { { 2, 1 }, { 10, 1 }, { 11, 10 }, { 12, 11 }, { 20, 1 } };
            var names = new Dictionary<int, string>
            {
                { 1, "explorer" }, { 2, "worker" }, { 10, "chrome" },
                { 11, "chrome" }, { 12, "gpu-helper" }, { 20, "unrelated" }
            };
            HashSet<int> family = GameMode.ExpandUserFacingFamily(parents, names, new HashSet<int> { 1, 10 });
            Eq(true, family.Contains(10));
            Eq(true, family.Contains(11));
            Eq(true, family.Contains(12));
            Eq(false, family.Contains(2));
            Eq(false, family.Contains(20));
        }

        private static void TestGameProtectionRedesign()
        {
            Eq(true, GameSessionDetector.IsLauncherLikeName("LeagueClient"));
            Eq(true, GameSessionDetector.IsLauncherLikeName("RiotClientServices"));
            Eq(true, GameSessionDetector.IsLauncherLikeName("EpicGamesLauncher"));
            Eq(false, GameSessionDetector.IsLauncherLikeName("League of Legends"));
            Eq(false, GameSessionDetector.IsLauncherLikeName(null));
            Eq(false, GameSessionDetector.IsLauncherLikeName(""));
            Eq(true, GameSessionDetector.IsLauncherLikeName("wegame"));
            Eq(true, GameSessionDetector.IsLauncherLikeName("Steam"));
            Eq(true, GameSessionDetector.IsLauncherLikeName("Battle.net"));

            Eq(true, GameSessionDetector.IsAntiCheatLikeName("SGuard64"));
            Eq(true, GameSessionDetector.IsAntiCheatLikeName("BattlEye"));
            Eq(true, GameSessionDetector.IsAntiCheatLikeName("GameAntiCheat"));
            Eq(false, GameSessionDetector.IsAntiCheatLikeName("League of Legends"));
            Eq(false, GameSessionDetector.IsAntiCheatLikeName(null));

            var selected = new GameProfile { Name = "MyTool", ExecutablePath = @"D:\Apps\chrome.exe" };
            Eq(true, GameSessionDetector.QualifiesRenderer(selected, "chrome", @"D:\Apps\chrome.exe", true, false));
            Eq(false, GameSessionDetector.QualifiesRenderer(selected, "chrome", @"D:\Other\chrome.exe", true, false));
            var acSel = new GameProfile { Name = "AC", ExecutablePath = @"D:\Games\SGuard64.exe" };
            Eq(false, GameSessionDetector.QualifiesRenderer(acSel, "SGuard64", @"D:\Games\SGuard64.exe", true, true));
            var acVariant = new GameProfile { Name = "BE", ExecutablePath = @"D:\Games\BattlEye.exe" };
            Eq(false, GameSessionDetector.QualifiesRenderer(acVariant, "BattlEye", @"D:\Games\BattlEye.exe", true, true));
        }

        private static void TestProfileStore(string root)
        {
            string dir = Path.Combine(root, "profiles");
            Directory.CreateDirectory(dir);
            string legacy = Path.Combine(dir, "Aegis.games.txt");
            string gameRoot = Path.Combine(dir, "GenericGame");
            File.WriteAllLines(legacy, new[] { GameMode.EncodeGameLine("GenericGame", gameRoot), GameMode.EncodeGameLine("GenericHelper", gameRoot) }, Encoding.UTF8);
            var store = new GameProfileStore(dir);
            List<GameProfile> first = store.LoadOrMigrate(legacy);
            Eq(1, first.Count);
            Eq(2, first[0].Entries.Count);
            first[0].ExecutablePath = Path.Combine(gameRoot, "GenericGame.exe");
            var duplicate = first[0].Clone();
            duplicate.Entries.Add("DuplicateHelper");
            store.Save(new[] { first[0], duplicate });
            List<GameProfile> second = store.LoadOrMigrate(legacy);
            Eq(1, second.Count);
            Eq(3, second[0].Entries.Count);
        }

        private static void TestMultiFolderGameRoot()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "AegisFamily_" + Guid.NewGuid().ToString("N"));
            string install = Path.Combine(sandbox, "英雄联盟");
            try
            {
                Directory.CreateDirectory(Path.Combine(install, "Game"));
                Directory.CreateDirectory(Path.Combine(install, "LeagueClient"));
                Directory.CreateDirectory(Path.Combine(install, "Riot Client"));
                Directory.CreateDirectory(Path.Combine(install, "WeGameLauncher"));

                string selected = Path.Combine(install, "LeagueClient", "LeagueClient.exe");
                string root = GameScan.InferGameRoot(selected);
                Eq(Path.GetFullPath(install), root);

                var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { root.TrimEnd('\\') + "\\" };
                Eq(true, GameMode.IsGameFamily(Path.Combine(install, "Riot Client", "RiotClientServices.exe"), roots));
                Eq(true, GameMode.IsGameFamily(Path.Combine(install, "Game", "League of Legends.exe"), roots));
                Eq(true, GameMode.IsGameFamily(Path.Combine(install, "WeGameLauncher", "launcher.exe"), roots));
                Eq(false, GameMode.IsGameFamily(Path.Combine(install, "ACE", "ACE-Helper.exe"), roots, "ACE-Helper"));
                Eq(false, GameMode.IsGameFamily(Path.Combine(sandbox, "WeGame", "wegame.exe"), roots));
                Eq(false, GameMode.IsGameFamily(Path.Combine(install + "-backup", "Game", "game.exe"), roots));

                string other = Path.Combine(sandbox, "GenericUnrealGame");
                string shipping = Path.Combine(other, "Binaries", "Win64", "Generic-Win64-Shipping.exe");
                Eq(Path.GetFullPath(other), GameScan.InferGameRoot(shipping));
            }
            finally { try { Directory.Delete(sandbox, true); } catch { } }
        }

        private static void TestGameCatalogFormat()
        {
            string root = Path.Combine(Path.GetTempPath(), "AegisGames", "英雄联盟");
            string line = GameMode.EncodeGameLine("LeagueClient.exe", root);
            string name, parsed;
            Eq(true, GameMode.TryParseGameLine(line, out name, out parsed));
            Eq("LeagueClient", name);
            Eq(Path.GetFullPath(root), parsed);

            Eq(true, GameMode.TryParseGameLine("League of Legends", out name, out parsed));
            Eq("League of Legends", name);
            Eq<string>(null, parsed);
        }

        private static void TestNameMismatch(string root)
        {
            string beat = Path.Combine(root, "mismatch.beat");
            using (Process probe = StartProbe(beat))
            {
                try
                {
                    WaitAdvance(beat, -1, 4000);
                    var g = new FreezeGuard(Path.Combine(root, "mismatch.state"), false);
                    if (g.Freeze(probe.Id, "definitely-not-this-process", "test")) throw new Exception("mismatched identity was frozen");
                    int before = ReadCounter(beat);
                    WaitAdvance(beat, before, 2000);
                }
                finally { StopOwned(probe); }
            }
        }

        private static void TestGameCatalogUpgrade(string root)
        {
            string data = Path.Combine(root, "catalog-upgrade");
            Directory.CreateDirectory(data);
            string games = Path.Combine(data, "Aegis.games.txt");
            File.WriteAllText(games, "LeagueClient\r\n", Encoding.UTF8);

            var mode = new GameMode(data, new SuppressionCore());
            string install = Path.Combine(data, "英雄联盟");
            Directory.CreateDirectory(Path.Combine(install, "Game"));
            Directory.CreateDirectory(Path.Combine(install, "LeagueClient"));
            string executable = Path.Combine(install, "LeagueClient", "LeagueClient.exe");
            File.Copy(Application.ExecutablePath, executable, true);
            Eq(true, mode.AddGameExecutable("LeagueClient", executable));

            string[] lines = File.ReadAllLines(games, Encoding.UTF8);
            Eq(1, lines.Length);
            string name, parsedRoot;
            Eq(true, GameMode.TryParseGameLine(lines[0], out name, out parsedRoot));
            Eq("LeagueClient", name);
            Eq(Path.GetFullPath(install), parsedRoot);
            Eq(false, mode.AddGameExecutable("LeagueClient", executable));
        }

        private static void TestExecutableResolver(string root)
        {
            string dir = Path.Combine(root, "resolver");
            Directory.CreateDirectory(dir);
            string executable = Path.Combine(dir, "SampleGame.exe");
            File.Copy(Application.ExecutablePath, executable, true);
            string resolved, error;
            Eq(true, GameExecutableResolver.TryResolve(executable, out resolved, out error));
            Eq(Path.GetFullPath(executable), resolved);

            string shortcut = Path.Combine(dir, "Sample Game.lnk");
            Eq(true, GameExecutableResolver.CreateShortcutForTest(shortcut, executable));
            Eq(true, GameExecutableResolver.TryResolve(shortcut, out resolved, out error));
            Eq(Path.GetFullPath(executable), resolved);

            string invalid = Path.Combine(dir, "not-a-game.txt");
            File.WriteAllText(invalid, "x");
            Eq(false, GameExecutableResolver.TryResolve(invalid, out resolved, out error));
        }

        private static void TestHeadlessEntry(string root)
        {
            string dir = Path.Combine(root, "headless-entry");
            Directory.CreateDirectory(dir);
            string executable = Path.Combine(dir, "HeadlessProbe.exe");
            string beat = Path.Combine(dir, "headless.beat");
            File.Copy(Application.ExecutablePath, executable, true);
            Process probe = null;
            Process[] all = null;
            try
            {
                ProcessStartInfo start = Hidden("--freeze-probe " + Quote(beat));
                start.FileName = executable;
                probe = Process.Start(start);
                if (probe == null) throw new Exception("headless entry did not start");
                WaitAdvance(beat, -1, 4000);

                all = Process.GetProcesses();
                var selectedProfile = GameProfileStore.NewProfile("Headless", dir, executable);
                selectedProfile.Entries.Clear();
                selectedProfile.Entries.Add("HeadlessProbe");
                if (GameSessionDetector.Detect(all, new[] { selectedProfile }) == null)
                    throw new Exception("user-selected headless exe did NOT activate (regression: 客户端/大厅将无法被识别)");

                var legacyProfile = GameProfileStore.NewProfile("HeadlessLegacy", dir);
                legacyProfile.Entries.Clear();
                legacyProfile.Entries.Add("HeadlessProbe");
                legacyProfile.ExecutablePath = null;
                if (GameSessionDetector.Detect(all, new[] { legacyProfile }) != null)
                    throw new Exception("legacy headless entry wrongly activated game policy");
            }
            finally
            {
                if (all != null) foreach (Process process in all) process.Dispose();
                if (probe != null) { StopOwned(probe); probe.Dispose(); }
            }
        }

        private static void TestFallbackEntryOutsideRoot(string root)
        {
            string dir = Path.Combine(root, "fallback-entry");
            string gameRoot = Path.Combine(dir, "game");
            string elsewhere = Path.Combine(dir, "elsewhere");
            Directory.CreateDirectory(gameRoot);
            Directory.CreateDirectory(elsewhere);
            string stubExe = Path.Combine(gameRoot, "aegisfbtest.exe");
            string realExe = Path.Combine(elsewhere, "aegisfbtest64.exe");
            string benchExe = Path.Combine(elsewhere, "aegisfbtestbench.exe");
            string updaterExe = Path.Combine(elsewhere, "aegisfbtest_updater.exe");
            File.Copy(Application.ExecutablePath, stubExe, true);
            File.Copy(Application.ExecutablePath, realExe, true);
            File.Copy(Application.ExecutablePath, benchExe, true);
            File.Copy(Application.ExecutablePath, updaterExe, true);
            string beatReal = Path.Combine(dir, "real.beat");
            string beatBench = Path.Combine(dir, "bench.beat");
            string beatUpdater = Path.Combine(dir, "updater.beat");
            Process real = null, bench = null, updater = null;
            Process[] all = null;
            try
            {
                ProcessStartInfo startReal = Hidden("--freeze-probe " + Quote(beatReal));
                startReal.FileName = realExe;
                real = Process.Start(startReal);
                if (real == null) throw new Exception("fallback probe did not start");
                WaitAdvance(beatReal, -1, 4000);

                ProcessStartInfo startBench = Hidden("--freeze-probe " + Quote(beatBench));
                startBench.FileName = benchExe;
                bench = Process.Start(startBench);
                if (bench == null) throw new Exception("bench probe did not start");
                WaitAdvance(beatBench, -1, 4000);

                ProcessStartInfo startUpdater = Hidden("--freeze-probe " + Quote(beatUpdater));
                startUpdater.FileName = updaterExe;
                updater = Process.Start(startUpdater);
                if (updater == null) throw new Exception("updater probe did not start");
                WaitAdvance(beatUpdater, -1, 4000);

                all = Process.GetProcesses();
                var profile = GameProfileStore.NewProfile("FallbackTest", gameRoot, stubExe);
                profile.Entries.Clear();
                profile.Entries.Add("aegisfbtest");

                GameDetection hit = GameSessionDetector.Detect(all, new[] { profile });
                if (hit == null) throw new Exception("fallback entry outside profile root was not detected at all");
                if (!string.Equals(hit.RendererName, "aegisfbtest64", StringComparison.OrdinalIgnoreCase))
                    throw new Exception("renderer should resolve to the real out-of-root process, got " + hit.RendererName);
                if (!hit.FamilyPids.Contains(real.Id))
                    throw new Exception("real out-of-root process must be in game family (protected from suppression)");
                if (hit.FamilyPids.Contains(bench.Id))
                    throw new Exception("letter-continuation name (bench) must NOT be treated as the same app — over-broad match");
                if (hit.FamilyPids.Contains(updater.Id))
                    throw new Exception("underscore-plus-word name (_updater) must NOT be treated as the same app — an unrelated third-party process could collide on prefix alone");
                if (!hit.RendererUserSelected)
                    throw new Exception("fallback-matched renderer must be marked RendererUserSelected (sticky anchor depends on this truth, not a path string guess)");
            }
            finally
            {
                if (all != null) foreach (Process process in all) process.Dispose();
                if (real != null) { StopOwned(real); real.Dispose(); }
                if (bench != null) { StopOwned(bench); bench.Dispose(); }
                if (updater != null) { StopOwned(updater); updater.Dispose(); }
            }
        }

    }
}
