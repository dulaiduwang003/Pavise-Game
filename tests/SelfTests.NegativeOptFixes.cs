// 2026-09-06 联合负优化复审的修复回归 纯函数与注入探针 不改设置 不开窗口
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static int RunNegativeOptimizationFixTests()
        {
            Action[] tests =
            {
                VoiceAppsFollowGeneralBackgroundRules,
                ControllerMappersAreInputChain,
                SaturationForgetsAfterStaleSamples,
                IntelHybridKeepsAutomaticHeteroPolicy,
                DesktopArenaLeavesCoreParkingRelaxed,
                AdaptiveEscalationDefaultsOff,
                CatalogVerdictCacheKeyedByIdentity,
                WhitelistEvaluationReusedForSameSnapshot
            };
            foreach (Action test in tests)
            {
                test();
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS negative-opt fixes audio_enumerated=false settings=untouched windows_shown=false");
            return tests.Length;
        }

        private static void NegCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Negative-opt fix regression: " + message);
        }

        private static void VoiceAppsFollowGeneralBackgroundRules()
        {
            string[] names =
            {
                "KOOK", "YY", "Oopz", "ts3client_win64", "ts3client_win32",
                "TeamSpeak", "mumble", "QQ", "TIM", "Weixin", "WeChat"
            };
            foreach (string name in names)
            {
                string path = @"C:\PaviseVoiceRuleFixture\" + name + ".exe";
                foreach (bool familyExempt in new[] { false, true })
                {
                    NegCheck(!OverlayHostCatalog.ShouldProtectProcess(name, path, familyExempt),
                        name + " has no fixed voice exemption");
                    foreach (bool aggressive in new[] { false, true })
                        NegCheck(FamilyBoundary.BasicBackgroundEligible(9001, 9000, name, path,
                            1, 1, 9002, false, @"C:\Windows\", false, null, aggressive, familyExempt),
                            name + " is eligible as ordinary background");
                }
                NegCheck(!FamilyBoundary.BasicBackgroundEligible(9001, 9000, name, path,
                    1, 1, 9001, false, @"C:\Windows\"), name + " keeps normal foreground protection");
            }
        }

        private static void ControllerMappersAreInputChain()
        {
            string[] mappers = { "DS4Windows", "reWASD", "reWASDEngine", "XOutput", "JoyToKey", "x360ce", "DualSenseX", "BetterJoy" };
            foreach (string name in mappers)
                NegCheck(PeripheralCatalog.IsInputAudioLike(name), name + " must be input chain");
            NegCheck(!PeripheralCatalog.IsInputAudioLike("Weixin"), "Weixin is not input chain by name");
            NegCheck(!PeripheralCatalog.IsInputAudioLike("chrome"), "chrome is not input chain");
        }

        private static void SaturationForgetsAfterStaleSamples()
        {
            var s = new CpuSaturation();
            long sec = TimeSpan.TicksPerSecond;
            s.Update(1.0, 0);
            NegCheck(s.Update(1.0, CpuSaturation.EnterHoldTicks), "enters saturation after hold");
            long t = CpuSaturation.EnterHoldTicks + sec;
            NegCheck(s.Update(double.NaN, t), "first stale sample keeps state");
            NegCheck(s.Update(double.NaN, t + CpuSaturation.StaleTicks - sec), "still saturated before stale window");
            NegCheck(!s.Update(double.NaN, t + CpuSaturation.StaleTicks), "stale window clears saturation");
            // 样本恢复后正常判定 不受之前的失联影响
            NegCheck(!s.Update(0.1, t + CpuSaturation.StaleTicks + sec), "valid sample after stale stays calm");
            var fresh = new CpuSaturation();
            fresh.Update(double.NaN, 0);
            NegCheck(!fresh.Update(double.NaN, CpuSaturation.StaleTicks * 2), "never saturated stays false");
        }

        private static void IntelHybridKeepsAutomaticHeteroPolicy()
        {
            PowerPlanProfile intel = PowerPlanProfile.Resolve(false, true, false, null);
            NegCheck(intel.WriteHetero && intel.HeteroSched == 5, "Intel hybrid writes automatic (5)");
            PowerPlanProfile amd = PowerPlanProfile.Resolve(true, true, false, null);
            NegCheck(amd.WriteHetero && amd.HeteroSched == 0, "AMD hybrid unchanged");
            NegCheck(!PowerPlanProfile.Resolve(false, false, false, null).WriteHetero, "plain CPU writes no hetero policy");
        }

        private static void DesktopArenaLeavesCoreParkingRelaxed()
        {
            // 台式机 核心停泊那一项写智能档值 其它激进项照写
            NegCheck(PowerPlan.ResolveArenaAc(false, false, true, false, 100, 50) == 50, "desktop core parking relaxed");
            NegCheck(PowerPlan.ResolveArenaAc(false, false, false, false, 100, 20) == 100, "desktop other knobs stay arena");
            // 笔记本插电同上 掌机插电按电池表放开纯省电项
            NegCheck(PowerPlan.ResolveArenaAc(true, false, true, false, 100, 50) == 50, "laptop ac relaxed");
            NegCheck(PowerPlan.ResolveArenaAc(true, false, false, true, 100, 20) == 100, "laptop ac keeps dc-only items");
            NegCheck(PowerPlan.ResolveArenaAc(true, true, false, true, 100, 20) == 20, "handheld relaxes dc-table items");
        }

        private static void CatalogVerdictCacheKeyedByIdentity()
        {
            FamilyBoundary.ClearCatalogVerdictsForTest();
            try
            {
                // 无创建时间不进缓存
                NegCheck(!FamilyBoundary.CatalogProtected(9001, 0, "chrome", @"C:\x\chrome.exe"), "chrome not protected");
                NegCheck(FamilyBoundary.CatalogVerdictCountForTest == 0, "no creation, no cache entry");
                // 有创建时间的结果被记住 反作弊名字命中为保护
                NegCheck(FamilyBoundary.CatalogProtected(9002, 77, "SGuard64", @"C:\ace\SGuard64.exe"), "anti-cheat protected");
                NegCheck(FamilyBoundary.CatalogProtected(9003, 78, "chrome", @"C:\x\chrome.exe") == false, "chrome eligible");
                NegCheck(FamilyBoundary.CatalogVerdictCountForTest == 2, "two entries cached");
                // 同 pid 换创建时间或换名字 必须重算 不能拿旧结论
                NegCheck(!FamilyBoundary.CatalogProtected(9002, 99, "chrome", @"C:\x\chrome.exe"), "pid reuse recomputes");
                NegCheck(FamilyBoundary.CatalogProtected(9003, 78, "DS4Windows", @"C:\x\DS4Windows.exe"), "name change recomputes");
                // 清理只留活着的
                FamilyBoundary.PruneCatalogVerdicts(new HashSet<int> { 9003 });
                NegCheck(FamilyBoundary.CatalogVerdictCountForTest == 1, "prune keeps live only");
            }
            finally { FamilyBoundary.ClearCatalogVerdictsForTest(); }
        }

        private static void WhitelistEvaluationReusedForSameSnapshot()
        {
            string folder = Path.Combine(Path.GetTempPath(), "PaviseNegOpt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var mode = new GameMode(folder, new SuppressionCore());
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                MethodInfo eval = typeof(GameMode).GetMethod("EvaluateWhitelist", flags);
                FieldInfo revision = typeof(GameMode).GetField("whiteRevision", flags);
                FieldInfo members = typeof(GameMode).GetField("whiteFamilyMembersVersion", flags);
                var snapshot = new ProcessSnapshot(new[]
                {
                    new ProcEntry { Pid = 100, ParentPid = 4, Session = 1, Creation = 5, Name = "a", Path = @"C:\a\a.exe" },
                    new ProcEntry { Pid = 101, ParentPid = 100, Session = 1, Creation = 6, Name = "b", Path = @"C:\a\b.exe" },
                });
                object first = eval.Invoke(mode, new object[] { snapshot });
                object second = eval.Invoke(mode, new object[] { snapshot });
                NegCheck(ReferenceEquals(first, second), "same snapshot reuses evaluation");
                // 规则版本变了要重算
                revision.SetValue(mode, (int)revision.GetValue(mode) + 1);
                object third = eval.Invoke(mode, new object[] { snapshot });
                NegCheck(!ReferenceEquals(second, third), "rule revision change recomputes");
                // 家族成员表变了要重算
                members.SetValue(mode, (int)members.GetValue(mode) + 1);
                object fourth = eval.Invoke(mode, new object[] { snapshot });
                NegCheck(!ReferenceEquals(third, fourth), "family member change recomputes");
                // 新快照要重算 即使内容一样
                var again = new ProcessSnapshot(snapshot.Entries);
                object fifth = eval.Invoke(mode, new object[] { again });
                NegCheck(!ReferenceEquals(fourth, fifth), "new snapshot recomputes");
                NegCheck(ReferenceEquals(fifth, eval.Invoke(mode, new object[] { again })), "then reuses again");
            }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }

        private static void AdaptiveEscalationDefaultsOff()
        {
            PolicyItem item = PolicyCatalog.ItemOf(PolicyCatalog.KeyAdaptiveEscalate);
            NegCheck(item != null && item.Fallback == "0", "adaptive escalation defaults off");
            NegCheck(item.Kind == PolicyValueKind.Bool && item.LangKey == "gm.adaptive", "adaptive escalation item shape");
        }
    }
}
#endif
