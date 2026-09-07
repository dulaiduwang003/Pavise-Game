// 2026-09-06 联合负优化复审的修复回归 纯函数与注入探针 不枚举真实音频会话 不改设置 不开窗口
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
                VoiceAppsAreProtectedByName,
                ControllerMappersAreInputChain,
                VoiceRosterHonoursTtlAndDispatcher,
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

        private static void VoiceAppsAreProtectedByName()
        {
            string[][] cases =
            {
                new[] { "KOOK", @"C:\Users\u\AppData\Local\Programs\KOOK\KOOK.exe" },
                new[] { "YY", @"C:\Program Files (x86)\YY\YY.exe" },
                new[] { "ts3client_win64", @"C:\Program Files\TeamSpeak 3 Client\ts3client_win64.exe" },
                new[] { "mumble", @"C:\Program Files\Mumble\client\mumble.exe" },
                new[] { "Oopz", @"C:\Users\u\AppData\Local\Oopz\Oopz.exe" },
            };
            foreach (string[] c in cases)
                NegCheck(OverlayHostCatalog.ShouldProtectProcess(c[0], c[1], false), c[0] + " must be protected");
            // 名字命中但路径叶子不一致 不能放行 名单只认具体 EXE
            NegCheck(!OverlayHostCatalog.ShouldProtectProcess("KOOK", @"D:\kook\KOOKUpdater.exe", false), "leaf mismatch");
            // QQ TIM 微信是重后台 不进静态名单 通话时靠采集会话放行
            NegCheck(!OverlayHostCatalog.ShouldProtectProcess("QQ", @"D:\qq\QQ.exe", false), "QQ stays eligible");
            NegCheck(!OverlayHostCatalog.ShouldProtectProcess("TIM", @"D:\tim\TIM.exe", false), "TIM stays eligible");
            NegCheck(!OverlayHostCatalog.ShouldProtectProcess("Weixin", @"C:\Program Files\Tencent\Weixin\Weixin.exe", false), "Weixin stays eligible");
            NegCheck(!OverlayHostCatalog.ShouldProtectProcess("WeChat", @"D:\WeChat\WeChat.exe", false), "WeChat stays eligible");
            NegCheck(!OverlayHostCatalog.ShouldProtectProcess("chrome", @"C:\chrome\chrome.exe", false), "chrome stays eligible");
        }

        private static void ControllerMappersAreInputChain()
        {
            string[] mappers = { "DS4Windows", "reWASD", "reWASDEngine", "XOutput", "JoyToKey", "x360ce", "DualSenseX", "BetterJoy" };
            foreach (string name in mappers)
                NegCheck(PeripheralCatalog.IsInputAudioLike(name), name + " must be input chain");
            NegCheck(!PeripheralCatalog.IsInputAudioLike("Weixin"), "Weixin is not input chain by name");
            NegCheck(!PeripheralCatalog.IsInputAudioLike("chrome"), "chrome is not input chain");
        }

        private static void VoiceRosterHonoursTtlAndDispatcher()
        {
            VoiceSessionRoster.ResetForTest();
            try
            {
                long sec = TimeSpan.TicksPerSecond;
                long clock = sec * 1000;
                VoiceSessionRoster.ClockForTest = delegate { return clock; };
                Dictionary<int, long> probe = new Dictionary<int, long> { { 1234, 7 } };
                int probes = 0;
                VoiceSessionRoster.ProbeForTest = delegate { probes++; return probe == null ? null : new Dictionary<int, long>(probe); };
                VoiceSessionRoster.DispatchForTest = delegate(Action work) { work(); };

                NegCheck(VoiceSessionRoster.Snapshot(clock).ContainsKey(1234), "first synchronous refresh visible");
                // 身份校验 创建时间对得上或未知才算 复用了 pid 的新进程不算
                NegCheck(VoiceSessionRoster.IsCapturing(1234, 7), "matching creation counts");
                NegCheck(VoiceSessionRoster.IsCapturing(1234, 0), "unknown caller creation counts");
                NegCheck(!VoiceSessionRoster.IsCapturing(1234, 8), "pid reuse with other creation does not count");
                NegCheck(!VoiceSessionRoster.IsCapturing(4, 0), "system pids never count");
                probe = new Dictionary<int, long> { { 5678, 9 } };
                clock += sec;
                NegCheck(VoiceSessionRoster.Snapshot(clock).ContainsKey(1234), "inside ttl keeps old set");
                NegCheck(probes == 1, "no probe inside ttl");
                clock += VoiceSessionRoster.TtlTicks;
                var after = VoiceSessionRoster.Snapshot(clock);
                NegCheck(after.ContainsKey(5678) && !after.ContainsKey(1234), "expired ttl refreshes");
                NegCheck(probes == 2, "exactly one more probe");

                // 整体失败保留旧快照 连续三次后退避一分钟 旧快照过了寿命就按空集
                probe = null;
                clock += VoiceSessionRoster.TtlTicks;
                NegCheck(VoiceSessionRoster.Snapshot(clock).ContainsKey(5678), "total failure keeps old set");
                clock += VoiceSessionRoster.TtlTicks; VoiceSessionRoster.Snapshot(clock);
                clock += VoiceSessionRoster.TtlTicks; VoiceSessionRoster.Snapshot(clock);
                NegCheck(probes == 5 && VoiceSessionRoster.FailuresForTest == 3, "three failures counted, got " + probes + "/" + VoiceSessionRoster.FailuresForTest);
                clock += VoiceSessionRoster.TtlTicks; VoiceSessionRoster.Snapshot(clock);
                NegCheck(probes == 5, "backoff suppresses probing inside a minute");
                clock += VoiceSessionRoster.MaxAgeTicks;
                NegCheck(VoiceSessionRoster.Snapshot(clock).Count == 0, "stale snapshot expires to empty");
                NegCheck(probes == 6, "backoff elapsed, probing resumes");
                probe = new Dictionary<int, long> { { 42, 1 } };
                clock += VoiceSessionRoster.BackoffTicks;
                NegCheck(VoiceSessionRoster.Snapshot(clock).ContainsKey(42) && VoiceSessionRoster.FailuresForTest == 0, "recovery clears failures");

                // 探针抛异常同样算整体失败 不把异常漏到扫描线程
                VoiceSessionRoster.ResetForTest();
                VoiceSessionRoster.ClockForTest = delegate { return clock; };
                VoiceSessionRoster.ProbeForTest = delegate { throw new InvalidOperationException("no audio"); };
                VoiceSessionRoster.DispatchForTest = delegate(Action work) { work(); };
                NegCheck(VoiceSessionRoster.Snapshot(clock).Count == 0 && VoiceSessionRoster.FailuresForTest == 1, "exception counted as failure");

                // 探测卡死 十秒后换代重发 迟到的结果被丢弃 卡死三次永久停用
                VoiceSessionRoster.ResetForTest();
                VoiceSessionRoster.ClockForTest = delegate { return clock; };
                var parked = new List<Action>();
                int hung = 0;
                VoiceSessionRoster.ProbeForTest = delegate { hung++; return new Dictionary<int, long> { { 777, 1 } }; };
                VoiceSessionRoster.DispatchForTest = delegate(Action work) { parked.Add(work); };
                NegCheck(VoiceSessionRoster.Snapshot(clock).Count == 0 && parked.Count == 1, "first probe dispatched and parked");
                clock += VoiceSessionRoster.TtlTicks;
                NegCheck(VoiceSessionRoster.Snapshot(clock).Count == 0 && parked.Count == 1, "in-flight probe blocks a second dispatch");
                clock += VoiceSessionRoster.StallTicks;
                NegCheck(VoiceSessionRoster.Snapshot(clock).Count == 0 && parked.Count == 2 && VoiceSessionRoster.StallsForTest == 1, "stall takes over and redispatches");
                parked[0](); // 第一代迟到的结果
                NegCheck(VoiceSessionRoster.Snapshot(clock).Count == 0, "late result from stalled generation is discarded");
                parked[1](); // 当前代的结果
                NegCheck(VoiceSessionRoster.Snapshot(clock).ContainsKey(777) && VoiceSessionRoster.StallsForTest == 1, "current generation publishes, stall count is cumulative");
                // 卡死计数不清零 再卡两次就永久停用 停用后迟到的结果也不发布
                clock += VoiceSessionRoster.TtlTicks; VoiceSessionRoster.Snapshot(clock);
                clock += VoiceSessionRoster.StallTicks; VoiceSessionRoster.Snapshot(clock);
                clock += VoiceSessionRoster.StallTicks; VoiceSessionRoster.Snapshot(clock);
                NegCheck(VoiceSessionRoster.UnavailableForTest, "three cumulative stalls give up for good");
                int parkedBefore = parked.Count;
                foreach (Action late in parked.GetRange(2, parked.Count - 2)) late();
                NegCheck(VoiceSessionRoster.Snapshot(clock).Count == 0 && parked.Count == parkedBefore, "late results after give-up are dropped");

                // 部分失败合并旧表 旧条目不续期 到寿命自己掉 新条目照常
                VoiceSessionRoster.ResetForTest();
                VoiceSessionRoster.ClockForTest = delegate { return clock; };
                bool partial = false;
                Dictionary<int, long> feed = new Dictionary<int, long> { { 11, 1 }, { 22, 2 } };
                VoiceSessionRoster.ProbeForTest = delegate { return partial ? null : new Dictionary<int, long>(feed); };
                VoiceSessionRoster.DispatchForTest = delegate(Action work) { work(); };
                NegCheck(VoiceSessionRoster.IsCapturing(11, 1) && VoiceSessionRoster.IsCapturing(22, 2), "both confirmed");
                long firstConfirm = clock;
                // 之后每次都是整体失败 旧表保留 但条目确认时间不变 过了一分钟按条目失效
                partial = true;
                clock = firstConfirm + VoiceSessionRoster.TtlTicks; VoiceSessionRoster.Snapshot(clock);
                NegCheck(VoiceSessionRoster.IsCapturing(11, 1), "old entry survives inside its lifetime");
                clock = firstConfirm + VoiceSessionRoster.MaxAgeTicks + TimeSpan.TicksPerSecond; VoiceSessionRoster.Snapshot(clock);
                NegCheck(!VoiceSessionRoster.IsCapturing(11, 1), "old entry expires after a minute without reconfirmation");
            }
            finally { VoiceSessionRoster.ResetForTest(); }
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
