#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunWhitelistTighteningTests()
        {
            RunRestoredProtectionCatalogTests();
            foreach (string header in new[] { WhitelistRule.LegacyHeader, WhitelistRule.PreviousHeader, WhitelistRule.Header, "" })
                TestWhitelistMigration(header);
            Console.WriteLine("WHITELIST migration: raw/V2/V3/V4, exact user rules, repeated reload, checksum and failed save PASS");
        }

        internal static void RunRestoredProtectionCatalogTests()
        {
            Eq(33, SystemProcessCatalog.PresetWhitelist.Length);
            Eq(true, Array.IndexOf(SystemProcessCatalog.PresetWhitelist, "ghelper") >= 0);
            foreach (string name in new[] { "chrome", "Weixin", "PaviseFixtureJob", "PatchRunner", "ShieldClient" })
            {
                Eq(false, PeripheralCatalog.IsInputAudioLike(name));
                Eq(false, HardwareControlCatalog.IsHardwareControlProcess(name));
                Eq(false, FamilyBoundary.CatalogProtected(998877, 0, name, @"C:\TestMissing\" + name + ".exe"));
            }
            foreach (string name in new[] { "DS4Windows", "reWASDEngine", "XOutput", "JoyToKey", "x360ce",
                "DualSenseX", "BetterJoy", "AutoHotkey64", "HidService", "HIDService", "KeyboardService",
                "TextInputHost", "QQPinyin", "iflyime", "RtkAudUService64", "NahimicSvc64",
                "osk", "TabTip", "MouseWithoutBorders", "lghub_agent", "LogiOptionsPlus_Agent", "Corsair.Service", "SteelSeriesEngine3" })
                Eq(true, PeripheralCatalog.IsInputAudioLike(name));
            foreach (string name in new[] { "Prime95", "TimerTool", "LogicalBackup",
                "AudioEditor", "KeyboardNotes", "AudioUpdater", "KeyboardInstaller", "RazerUpdater",
                "RazerGameManagerService", "SteelSeriesSonar", "HidHideClient", "Creative.Service", "LogiOverlay" })
                Eq(true, PeripheralCatalog.IsInputAudioLike(name));
            foreach (string name in new[] { "nvcontainer", "NVDisplay.Container", "AsusOptimization",
                "GHelper", "FanControl", "HWiNFO64", "ArmouryCrate.UserSessionHelper", "ArmouryCrateSE",
                "LegionSpace.Service", "MSIAfterburner", "ThrottleStop", "AYASpaceII" })
                Eq(true, HardwareControlCatalog.IsHardwareControlProcess(name));
            foreach (string name in new[] { "FanControlUpdater", "hwmonitor", "ArmouryTelemetry", "NVContainerWebHelper",
                "AsusSystemAnalysis", "AsusSystemDiagnosis", "NVIDIA Web Helper", "igfxTray" })
                Eq(true, HardwareControlCatalog.IsHardwareControlProcess(name));
            RunAcceleratorProtectionTests();
            foreach (string desc in new[] { "ASUS Computer Inc.", "Logitech Update Service", "Shield Service" })
                Eq(false, PeripheralCatalog.DescriptionLooksInputAudio(desc));
            foreach (string desc in new[] { "Realtek Audio Service", "Keyboard Driver", "Input Method",
                "HID Driver Service", "音频驱动服务", "手柄映射", "鼠标驱动", "Runtime Controller",
                "Lighting Controller Service", "音效编辑器", "灯效控制服务", "Audio Editor",
                "Audio Processing", "Realtek HD Audio Manager", "Gamepad Controller", "按键映射工具" })
                Eq(true, PeripheralCatalog.DescriptionLooksInputAudio(desc));
            foreach (string name in new[] { "searchhost", "securityhealthsystray" })
            {
                string path = @"C:\Windows\System32\" + name + ".exe";
                Eq(true, FamilyBoundary.BasicBackgroundEligible(700, 900, name, path, 1, 1, 800, false, @"C:\Windows\", false, null, true));
                Eq(false, FamilyBoundary.BasicBackgroundEligible(700, 900, name, path, 1, 1, 700, false, @"C:\Windows\", false, null, true));
            }
            TestGlobalInputHookProtection();
            TestRestoredPeripheralEligibility();
            TestVendorProtectionGeneration();
            Console.WriteLine("WHITELIST protection: 2.2.2.3 name/metadata/vendor matching restored; current input hooks and other boundaries retained PASS");
        }

        private static void TestGlobalInputHookProtection()
        {
            foreach (string name in new[] { "Listary", "ListaryHookHost32", "ListaryHookHost64", "listaryhookhost64.exe" })
            {
                string normalized = WhitelistRule.NormalizeName(name);
                string path = @"C:\PaviseMissingInputFixture\" + normalized + ".exe";
                Eq(true, PeripheralCatalog.IsInputAudioLike(name));
                Eq(true, FamilyBoundary.CatalogProtected(998876, 0, name, path));
                foreach (PerformancePreset mode in new[] { PerformancePreset.Competitive, PerformancePreset.Extreme })
                    foreach (bool familyExempt in new[] { true, false })
                        Eq(false, FamilyBoundary.BasicBackgroundEligible(700, 900, name, path,
                            1, 1, 800, false, @"C:\Windows\", false, null,
                            GameMode.IsAggressive(mode, false), familyExempt));
            }
            foreach (string name in new[] { "ListaryUpdater", "ListaryHookHostUpdater", "NotListary", "ListaryHookHostSomething" })
                Eq(true, PeripheralCatalog.IsInputAudioLike(name));
            foreach (string name in new[] { "HookHost32", "RandomHookHost64" })
            {
                string path = @"C:\PaviseMissingInputFixture\" + name + ".exe";
                Eq(false, PeripheralCatalog.IsInputAudioLike(name));
                Eq(false, FamilyBoundary.CatalogProtected(998876, 0, name, path));
                foreach (PerformancePreset mode in new[] { PerformancePreset.Competitive, PerformancePreset.Extreme })
                    Eq(true, FamilyBoundary.BasicBackgroundEligible(700, 900, name, path,
                        1, 1, 800, false, @"C:\Windows\", false, null,
                        GameMode.IsAggressive(mode, false)));
            }
            Console.WriteLine("WHITELIST input hooks: existing Listary protection retained under restored broad matching PASS");
        }

        private static void TestRestoredPeripheralEligibility()
        {
            foreach (string name in new[] { "RazerGameManagerService", "SteelSeriesSonar", "HidHideClient",
                "Creative.Service", "LogiOverlay", "ArmouryTelemetry", "FanControlUpdater" })
            {
                string path = @"C:\PaviseMissingProtectionFixture\" + name + ".exe";
                Eq(true, FamilyBoundary.CatalogProtected(998875, 0, name, path));
                foreach (PerformancePreset mode in new[] { PerformancePreset.Standard, PerformancePreset.Competitive,
                    PerformancePreset.Extreme, PerformancePreset.Handheld })
                    foreach (bool familyExempt in new[] { true, false })
                        Eq(false, FamilyBoundary.BasicBackgroundEligible(700, 900, name, path,
                            1, 1, 800, false, @"C:\Windows\", false, null,
                            GameMode.IsAggressive(mode, false), familyExempt));
            }
        }

        internal static void RunAcceleratorProtectionTests()
        {
            foreach (string name in new[] { "ExitLagService", "leigod_launcher", "uu_ball", "WTFast64", "mudfish",
                "GameBooster", "DownloadAccelerator", "ExitLagUpdater", "NotExitLag", "UU.EXE", "BBService.exe",
                "VendorExitLagIpcHost", "LeiGodHelper", "XunYouUpdate", "qiYouRouteWorker", "BiubiuDiagnostics",
                "DolphinQNetWorker", "NoPingWorker", "MudfishIpcClient", "某某加速器服务" })
            {
                Eq(true, NetAcceleratorCatalog.IsAcceleratorLikeName(name));
                string normalized = WhitelistRule.NormalizeName(name);
                string path = @"C:\PaviseMissingAcceleratorFixture\" + normalized + ".exe";
                foreach (PerformancePreset mode in new[] { PerformancePreset.Standard, PerformancePreset.Competitive,
                    PerformancePreset.Extreme, PerformancePreset.Handheld })
                    foreach (bool familyExempt in new[] { true, false })
                        Eq(false, FamilyBoundary.BasicBackgroundEligible(700, 900, normalized, path,
                            1, 1, 800, false, @"C:\Windows\", false, null,
                            GameMode.IsAggressive(mode, false), familyExempt));
            }
            foreach (string name in new[] { "chrome", "firefox", "msedge", "Discord", "steam", "NetworkService",
                "NetworkHelper", "tcp-helper", "VpnFixtureTunnel", "PlainFixtureUpdater", "" })
                Eq(false, NetAcceleratorCatalog.IsAcceleratorLikeName(name));
            Eq(false, NetAcceleratorCatalog.IsAcceleratorLikeName(null));

            var snapshot = new ProcessSnapshot(new[] {
                new ProcEntry { Pid=100, ParentPid=90, Creation=10, Session=7, Name="ExitLagIpcHost", Path=@"C:\AccelFixture\ExitLagIpcHost.exe" },
                new ProcEntry { Pid=101, ParentPid=100, Creation=11, Session=7, Name="session-worker", Path=@"C:\AccelWorkerFixture\session-worker.exe" },
                new ProcEntry { Pid=102, ParentPid=90, Creation=12, Session=7, Name="browser", Path=@"C:\AccelFixture\web\browser.exe" },
                new ProcEntry { Pid=103, ParentPid=90, Creation=13, Session=7, Name="browser", Path=@"C:\UnrelatedFixture\browser.exe" },
                new ProcEntry { Pid=104, ParentPid=100, Creation=5, Session=7, Name="reused", Path=@"C:\AccelWorkerFixture\reused.exe" },
                new ProcEntry { Pid=105, ParentPid=100, Creation=14, Session=8, Name="foreign", Path=@"C:\AccelWorkerFixture\foreign.exe" },
                new ProcEntry { Pid=106, ParentPid=90, Creation=15, Session=7, Name="NetworkHelper", Path=@"C:\UnrelatedFixture\NetworkHelper.exe" }
            });
            var protectedFamily = WeGameRuntimeProtection.Collect(snapshot, 999, 7);
            foreach (int pid in new[] { 100, 101, 102 }) Eq(true, protectedFamily.Contains(pid));
            foreach (int pid in new[] { 90, 103, 104, 105, 106 }) Eq(false, protectedFamily.Contains(pid));
            Console.WriteLine("ACCELERATOR protection: broad 2.2.2.3 name rules, all modes, verified helpers; unrelated network roles excluded PASS");
        }

        private static void TestVendorProtectionGeneration()
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            Type probe = typeof(PeripheralVendorProbe);
            FieldInfo tokens = probe.GetField("tokens", flags), stamp = probe.GetField("stamp", flags),
                scanned = probe.GetField("scanned", flags), generation = probe.GetField("generation", flags);
            object oldTokens = tokens.GetValue(null), oldStamp = stamp.GetValue(null),
                oldScanned = scanned.GetValue(null), oldGeneration = generation.GetValue(null);
            string path = @"C:\PaviseMissingProtectionFixture\FixtureUtility.exe";
            var metadata = (Dictionary<string, string>)typeof(PeripheralCatalog).GetField("infoCache", flags).GetValue(null);
            try
            {
                stamp.SetValue(null, Environment.TickCount); scanned.SetValue(null, true);
                tokens.SetValue(null, new string[0]); generation.SetValue(null, (int)oldGeneration + 1);
                Eq(false, FamilyBoundary.CatalogProtected(998874, 12345, "FixtureVendorUtility", path));
                tokens.SetValue(null, new[] { "fixturevendor" }); generation.SetValue(null, (int)oldGeneration + 2);
                Eq(true, FamilyBoundary.CatalogProtected(998874, 12345, "FixtureVendorUtility", path));
                metadata[path] = "fixture program\nfixture product\nfixturevendor ltd";
                Eq(true, PeripheralCatalog.IsInputChainProcess("FixtureUtility", path));
                tokens.SetValue(null, new string[0]); generation.SetValue(null, (int)oldGeneration + 3);
                Eq(false, FamilyBoundary.CatalogProtected(998874, 12345, "FixtureVendorUtility", path));
                Eq(false, PeripheralCatalog.IsInputChainProcess("FixtureUtility", path));
            }
            finally
            {
                metadata.Remove(path);
                tokens.SetValue(null, oldTokens); stamp.SetValue(null, oldStamp);
                scanned.SetValue(null, oldScanned); generation.SetValue(null, oldGeneration);
                FamilyBoundary.PruneCatalogVerdicts(new HashSet<int>());
            }
        }

        private static GameMode WhitelistMode(string root)
        {
            return new GameMode(root, new SuppressionCore(delegate(int pid, long creation, string name)
                { throw new InvalidOperationException("Whitelist test must not restore a desktop process"); }, true));
        }

        private static void TestWhitelistMigration(string header)
        {
            string root = Path.Combine(Path.GetTempPath(), "PaviseWhite-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "Pavise.whitelist.txt");
            var rules = new List<WhitelistRule>();
            foreach (string name in new[] { "searchhost", "securityhealthsystray", "MyVoiceTool" })
            {
                WhitelistRule rule; Eq(true, WhitelistRule.TryCreate(WhitelistRuleKind.LegacyName, name, out rule)); rules.Add(rule);
            }
            WhitelistRule exact;
            Eq(true, WhitelistRule.TryCreate(WhitelistRuleKind.ExactPath, @"C:\Windows\System32\SearchHost.exe", out exact));
            if (header.Length > 0) rules.Add(exact);
            var lines = new List<string>();
            if (header.Length > 0) lines.Add(header);
            foreach (var rule in rules) lines.Add(header.Length > 0 ? rule.Serialize() : rule.Value);
            if (header == WhitelistRule.Header || header == WhitelistRule.PreviousHeader)
                lines.Add(GameMode.BuildWhitelistFooter(rules));
            File.WriteAllLines(file, lines.ToArray(), new UTF8Encoding(false));
            string original = File.ReadAllText(file);
            var mode = WhitelistMode(root);
            bool preserveName = header == WhitelistRule.Header;
            var loaded = (List<WhitelistRule>)typeof(GameMode).GetField("whiteRules", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
            Eq(preserveName, loaded.Exists(r => r.Kind == WhitelistRuleKind.LegacyName && r.Value == "searchhost"));
            Eq(true, loaded.Exists(r => r.Value == "MyVoiceTool"));
            Eq(header.Length > 0, loaded.Exists(r => r.Key == exact.Key));
            WhitelistRule custom;
            Eq(true, WhitelistRule.TryCreate(WhitelistRuleKind.ExactPath, @"C:\TestMissing\AsusSystemAnalysis.exe", out custom));
            typeof(GameMode).GetMethod("AddWhiteRuleNoSave", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, new object[]{custom});
            int session = (int)typeof(GameMode).GetField("selfSession", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(mode);
            var snapshot = new ProcessSnapshot(new[]{ new ProcEntry { Pid=556677, Session=session, Creation=100,
                Name="AsusSystemAnalysis", Path=custom.Value } });
            object evaluated = typeof(GameMode).GetMethod("EvaluateWhitelist", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(mode, new object[]{snapshot});
            Eq(true, ((HashSet<int>)evaluated.GetType().GetField("Protected").GetValue(evaluated)).Contains(556677));
            Eq(true, File.ReadAllText(file).Contains(WhitelistRule.Header));
            string saved = File.ReadAllText(file);
            mode = WhitelistMode(root);
            Eq(saved, File.ReadAllText(file));
            var newRules = new List<WhitelistRule>(loaded);
            if (!preserveName) newRules.Add(rules[0]);
            var current = new List<string> { WhitelistRule.Header };
            foreach (var r in newRules) current.Add(r.Serialize());
            current.Add(GameMode.BuildWhitelistFooter(newRules));
            File.WriteAllLines(file, current.ToArray(), new UTF8Encoding(false));
            Eq(true, WhitelistMode(root).GetWhitelist().Contains("searchhost"));
            if (header == WhitelistRule.PreviousHeader)
            {
                File.WriteAllText(file, original.Replace(GameMode.BuildWhitelistFooter(rules), GameMode.BuildWhitelistFooter(rules) + "0"));
                string corrupt = File.ReadAllText(file);
                WhitelistMode(root); Eq(corrupt, File.ReadAllText(file));
                File.WriteAllText(file, original);
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    mode = WhitelistMode(root);
                    Eq(false, mode.GetWhitelist().Contains("searchhost"));
                    Eq(original, File.ReadAllText(file));
                }
                WhitelistMode(root); Eq(true, File.ReadAllText(file).Contains(WhitelistRule.Header));
            }
        }
    }
}
#endif
