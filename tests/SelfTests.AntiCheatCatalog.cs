// 文件用途 豁免回归 不启动游戏 不写进程 不改真实设置
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunAntiCheatCatalogRegressionTests()
        {
            Action[] tests =
            {
                AcCatalogPreservesExistingNames,
                AcCatalogRecognizesProtectionOnlyFamilies,
                AcCatalogProtectsDedicatedDirectories,
                AcCatalogRejectsBroadDirectoryMatches,
                AcCatalogExemptionsIgnoreSuppressionSwitches,
                AcCatalogNeverAddsProtectionPatternsToTamer,
                AcCatalogCacheTracksPathChanges,
                AcCatalogHelpersCannotBecomeRenderers,
                AcCatalogProtectionLabelsExist
            };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            return tests.Length;
        }

        private static readonly string[] AcProtectedSamples =
        {
            "PnkBstrA", "PNKBSTRB.EXE", "PunkBusterHelper", "BlackCipher.aes",
            "BlackCipher64.aes", "BlackCall64.aes", "BlackXchg.aes",
            "XIGNCODE3", "UncheaterHelper", "ucldr_battlegrounds_gl", "ucldr_AnotherGame_GL"
        };

        private static void AcCatalogPreservesExistingNames()
        {
            foreach (AcGroup group in AntiCheatCatalog.Groups)
                foreach (string name in group.Procs)
                {
                    Eq(true, AntiCheatCatalog.IsKnownProcess(name));
                    Eq(true, AntiCheatCatalog.IsAntiCheatLikeName(name.ToUpperInvariant() + ".EXE"));
                    Eq(false, AcBackgroundEligible(name, @"D:\OutsideGame\component.exe", true, false));
                }
            Eq(true, AntiCheatCatalog.IsKnownProcess("EAAntiCheat.GameService.exe"));
            Eq(true, AntiCheatCatalog.IsKnownProcess("GameMon64.des"));
            Eq(false, AntiCheatCatalog.IsKnownProcess("EAAntiCheat.GameService.dll"));
            Eq(false, AntiCheatCatalog.IsKnownProcess("PnkBstrK.sys"));
            Eq(false, AntiCheatCatalog.IsKnownProcess("xhunter1.sys"));
        }

        private static void AcCatalogRecognizesProtectionOnlyFamilies()
        {
            foreach (AcProtectionGroup group in AntiCheatCatalog.ProtectionOnlyGroups)
                foreach (string name in group.Procs)
                    Eq(true, AntiCheatCatalog.IsAntiCheatLikeName(name.ToUpperInvariant() + ".EXE"));
            foreach (string name in AcProtectedSamples)
            {
                Eq(true, AntiCheatCatalog.IsAntiCheatLikeName(name));
                Eq(true, AntiCheatCatalog.IsAntiCheatProcess(name, null));
            }
            foreach (string name in new[] { "xm", "NGService", "AC", "EA", "Nexon", "worker", "myucldr_tool" })
                Eq(false, AntiCheatCatalog.IsAntiCheatLikeName(name));
            Eq(false, AntiCheatCatalog.IsAntiCheatLikeName(null));
            Eq(false, AntiCheatCatalog.IsAntiCheatLikeName(""));
        }

        private static void AcCatalogProtectsDedicatedDirectories()
        {
            string[] paths =
            {
                @"C:\Program Files\EA\AC\worker.exe",
                @"D:\Game\BlackCipher\NGService.exe",
                @"D:\Game\XIGNCODE\client\xm.exe",
                @"C:\Program Files\Common Files\Wellbia.com\worker.exe",
                @"C:\Program Files\Common Files\Wellbia\worker.exe",
                @"D:\Game\UNCHEATER\worker.exe",
                @"D:\Game\PunkBuster\worker.exe",
                @"D:\Game\BattlEye\worker.exe",
                @"C:\Program Files\Riot Vanguard\worker.exe",
                @"D:\Game\EasyAntiCheat_EOS\worker.exe",
                @"D:\Game\AntiCheatExpert\worker.exe",
                @"D:\Game\Nexon Game Security\worker.exe",
                @"\\server\share\Game\BlackCipher\worker.aes",
                @"\\?\UNC\server\share\BlackCipher\worker.aes",
                @"\\?\D:\Game\BlackCipher\worker.aes",
                "d:/Game/xigncode3/client/worker.exe"
            };
            foreach (string path in paths)
            {
                Eq(true, AntiCheatCatalog.IsAntiCheatProcess("worker", path));
                Eq(false, AcBackgroundEligible("worker", path, true, false));
            }
        }

        private static void AcCatalogRejectsBroadDirectoryMatches()
        {
            string[] paths =
            {
                null, "", @"BlackCipher\worker.exe", @"C:BlackCipher\worker.exe",
                @"\BlackCipher\worker.exe", @"C:\BlackCipher\..\Other\worker.exe",
                @"C:\Other\BlackCipher", @"C:\Other\BlackCipher.exe", @"C:\BlackCipher\",
                @"C:\BlackCipherBackup\worker.exe", @"C:\MyXIGNCODE\worker.exe",
                @"C:\Wellbia.com.backup\worker.exe", @"C:\Program Files\EA\Desktop\worker.exe",
                @"C:\Program Files\Nexon\Game\worker.exe", @"C:\Other\AC\worker.exe",
                @"C:\EA\ACTools\worker.exe", @"C:\EA\Other\AC\worker.exe",
                @"\\BlackCipher\share\worker.exe", @"\\server\BlackCipher\worker.exe",
                @"\\?\UNC\server\BlackCipher\worker.exe", @"\\.\BlackCipher\worker.exe"
            };
            foreach (string path in paths) Eq(false, AntiCheatCatalog.IsAntiCheatPath(path));
            Eq(true, AcBackgroundEligible("worker", @"C:\Other\worker.exe", true, false));
        }

        private static bool AcBackgroundEligible(string name, string path, bool aggressive, bool familyExempt)
        {
            // 同一个用户会话 没有活动游戏根 也没有前台 白名单和家族保护
            return FamilyBoundary.BasicBackgroundEligible(9101, 9999, name, path,
                1, 1, 9102, false, @"C:\Windows\", false, null, aggressive, familyExempt);
        }

        private static void AcCatalogExemptionsIgnoreSuppressionSwitches()
        {
            foreach (bool enabled in new[] { false, true })
            {
                Settings.Save("TameOn", enabled);
                foreach (AcGroup group in AntiCheatCatalog.Groups) Settings.Save("Tame_" + group.Key, enabled);
                foreach (bool aggressive in new[] { false, true })
                    foreach (bool familyExempt in new[] { false, true })
                    {
                        foreach (AcProtectionGroup group in AntiCheatCatalog.ProtectionOnlyGroups)
                            foreach (string name in group.Procs)
                                Eq(false, AcBackgroundEligible(name, @"D:\OutsideGame\component.exe", aggressive, familyExempt));
                        foreach (string name in AcProtectedSamples)
                            Eq(false, AcBackgroundEligible(name, @"D:\OutsideGame\component.exe", aggressive, familyExempt));
                        Eq(false, AcBackgroundEligible("worker", @"C:\Program Files\EA\AC\worker.exe", aggressive, familyExempt));
                        Eq(false, AcBackgroundEligible("EAAntiCheat.GameService", @"D:\OutsideGame\component.exe", aggressive, familyExempt));
                    }
            }
        }

        private static void AcCatalogNeverAddsProtectionPatternsToTamer()
        {
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AcGroup group in AntiCheatCatalog.Groups)
            {
                Eq(false, group.Default);
                Settings.Save("Tame_" + group.Key, true);
                // 仅保护的分组即使键被打开也不该进压制目标
                if (group.Suppressible) foreach (string name in group.Procs) expected.Add(name);
            }
            // 就算键是过期的或者被手改过 仅豁免的规则也不能变成压制目标
            foreach (AcProtectionGroup group in AntiCheatCatalog.ProtectionOnlyGroups)
                Settings.Save("Tame_" + group.Key, true);
            var core = new SuppressionCore(delegate { throw new InvalidOperationException("Unexpected restore"); }, true);
            var tamer = new Tamer(core); // Never start the worker.
            MethodInfo build = typeof(Tamer).GetMethod("BuildActive", BindingFlags.Instance | BindingFlags.NonPublic);
            var actual = (Dictionary<string, string>)build.Invoke(tamer, null);
            Eq(true, expected.SetEquals(actual.Keys));
            // 改判仅保护的分组 构造时会把老配置里开着的键说明一次再清掉 不静默失效
            foreach (AcGroup group in AntiCheatCatalog.Groups)
            {
                if (group.Suppressible) continue;
                foreach (string name in group.Procs) Eq(false, actual.ContainsKey(name));
                Eq(false, Settings.Load("Tame_" + group.Key, false));
                Eq(false, tamer.IsGroupEnabled(group.Key));
            }
            foreach (string sample in AcProtectedSamples) Eq(false, actual.ContainsKey(sample));
            foreach (AcProtectionGroup group in AntiCheatCatalog.ProtectionOnlyGroups)
                foreach (string name in group.Procs) Eq(false, actual.ContainsKey(name));
        }

        private static void AcCatalogCacheTracksPathChanges()
        {
            FamilyBoundary.ClearCatalogVerdictsForTest();
            try
            {
                Eq(false, FamilyBoundary.CatalogProtected(9201, 11, "worker", @"C:\Other\worker.exe"));
                Eq(true, FamilyBoundary.CatalogProtected(9201, 11, "worker", @"C:\EA\AC\worker.exe"));
                Eq(false, FamilyBoundary.CatalogProtected(9201, 12, "worker", @"C:\Other\worker.exe"));
                Eq(true, FamilyBoundary.CatalogProtected(9201, 12, "BlackCipher64.aes", @"C:\Other\component.aes"));
            }
            finally { FamilyBoundary.ClearCatalogVerdictsForTest(); }
        }

        private static void AcCatalogHelpersCannotBecomeRenderers()
        {
            foreach (string name in AcProtectedSamples)
                Eq(true, GameSessionDetector.ElectionVetoed(name, @"D:\OutsideGame\component.exe"));
            Eq(true, GameSessionDetector.ElectionVetoed("xm", @"D:\Game\XIGNCODE\xm.exe"));
            Eq(false, GameSessionDetector.IsLibraryCandidate("worker", @"C:\EA\AC\worker.exe", @"C:\Windows\"));
            Eq(false, GameSessionDetector.ElectionVetoed("xm", @"D:\OrdinaryGame\xm.exe"));
            Eq(false, GameSessionDetector.ElectionVetoed("bf6", @"D:\Battlefield 6\bf6.exe"));
        }

        private static void AcCatalogProtectionLabelsExist()
        {
            foreach (AcProtectionGroup group in AntiCheatCatalog.ProtectionOnlyGroups)
            {
                Eq(true, Lang.Row("ac." + group.Key + ".n") != null);
                Eq(true, Lang.Row("ac." + group.Key + ".d") != null);
                Eq(true, group.PatternsForDisplay().Length > 0);
            }
            Eq(true, Lang.Row("ac.protectiononly") != null);
            Eq(true, Lang.Row("white.auto.details.anticheatdirs") != null);
        }
    }
}
#endif
