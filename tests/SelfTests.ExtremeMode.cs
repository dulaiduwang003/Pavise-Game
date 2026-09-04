// Settings-backed state checks in the isolated selftest store; no env tweaks executed,
// no registry outside the store, no reboot, no windows.
// 极限档回归 重启门 取值解析 覆盖清单 退出集 不碰任何真实环境项
#if PAVISE_SELFTEST
using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static int extremeChecks;

        internal static int RunExtremeModeRegressionTests()
        {
            Action[] tests =
            {
                ExtremeRebootGateNeedsARealBoot,
                ExtremePresetValueResolvesByVisibility,
                ExtremeForcedValuesRespectListAndOptOut,
                ExtremeSnapshotOverlayLeavesUserConfigUntouched,
                ExtremeEnvLedgerDrivesTheReadout,
                ExtremePowerKnobsAreTierExclusive,
                ExtremeGatesFollowHardwareEvidence,
                ExtremeForcesEnvOnlyWhileUnlockedAndNotOptedOut,
                ExtremeForcesLiveSessionPreferences,
                ExtremeCrashFuseTripsOnRepeatedAbnormalRestarts,
                ExtremeManageListLabelsEverySessionKey
            };
            extremeChecks = 0;
            foreach (Action test in tests)
            {
                ExtremeResetState();
                try { test(); }
                finally { ExtremeResetState(); }
                Console.WriteLine("PASS " + test.Method.Name);
            }
            Console.WriteLine("PASS extreme-mode assertions=" + extremeChecks
                + " envtweaks=untouched reboot=none windows_shown=false");
            return tests.Length;
        }

        private static void ExtremeResetState()
        {
            ExtremeMode.PurgeAll();
            Settings.SaveStr("PerformancePreset", "0");
        }

        private static void ExtCheck(bool good, string message)
        {
            if (!good) throw new InvalidOperationException("Extreme mode regression: " + message);
            extremeChecks++;
        }

        // 解锁后把解锁时刻放到当前开机点之前 等效于经历过一次重启
        private static void ExtremeUnlockPastGate()
        {
            Settings.Save("ExtremeUnlocked", true);
            Settings.SaveStr("ExtremeUnlockTicks", "1");
        }

        private static void ExtremeRebootGateNeedsARealBoot()
        {
            long boot = ExtremeMode.BootTicksUtc();
            ExtCheck(ExtremeMode.RebootGatePassed(boot, boot - 1),
                "a boot after the unlock moment passes the gate");
            ExtCheck(!ExtremeMode.RebootGatePassed(boot, boot + TimeSpan.TicksPerMinute),
                "an unlock after the current boot must wait for a restart");
            ExtCheck(!ExtremeMode.RebootGatePassed(boot, 0),
                "no recorded unlock moment never passes");
            ExtCheck(!ExtremeMode.Visible, "locked state is invisible");
            Settings.Save("ExtremeUnlocked", true);
            Settings.SaveStr("ExtremeUnlockTicks",
                DateTime.UtcNow.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
            ExtCheck(!ExtremeMode.Visible && ExtremeMode.PendingReboot,
                "freshly unlocked shows pending until the machine restarts");
            ExtremeUnlockPastGate();
            ExtCheck(ExtremeMode.Visible, "after a boot newer than the unlock the tier appears");
        }

        private static void ExtremePresetValueResolvesByVisibility()
        {
            ExtCheck(!PresetValue.IsValid(3), "the 1.x extreme gravestone value 3 stays rejected");
            ExtCheck(PresetValue.From(5) == PerformancePreset.Competitive,
                "a stored 5 resolves to Competitive while locked, data preserved");
            ExtCheck(PresetValue.VisibleChoices().Length == 4,
                "locked machines list four tiers");
            ExtremeUnlockPastGate();
            ExtCheck(PresetValue.From(5) == PerformancePreset.Extreme,
                "the same stored 5 resolves to Extreme once visible");
            ExtCheck(PresetValue.VisibleChoices().Length == 5
                && PresetValue.VisibleChoices()[2] == "5",
                "unlocked machines list five tiers with extreme in the middle");
        }

        private static void ExtremeForcedValuesRespectListAndOptOut()
        {
            ExtremeUnlockPastGate();
            ExtCheck(ExtremeMode.ForcedPolicyValue(PolicyCatalog.KeyAudioLowLat) == "1",
                "a listed session key is forced on");
            ExtCheck(ExtremeMode.ForcedPolicyValue(PolicyCatalog.KeyNvLowLat) == "on",
                "the NVIDIA low-latency choice is forced to on, never ultra");
            ExtCheck(ExtremeMode.ForcedPolicyValue(PolicyCatalog.KeyDisableCpuIdle) == null,
                "CPU idle disable is excluded by decree and never forced");
            ExtCheck(ExtremeMode.ForcedPolicyValue(PolicyCatalog.KeyStandbyCleaner) == null,
                "the standby cleaner is excluded by the project's own bench evidence");
            // 极限专属键没有全局开关也没有逐游戏行 目录里不该找得到它们
            ExtCheck(PolicyCatalog.ItemOf(PolicyCatalog.KeyAudioLowLat) == null
                && PolicyCatalog.ItemOf(PolicyCatalog.KeyWsTrim) == null,
                "extreme-only keys must stay out of the per-game catalog");
            ExtCheck(ExtremeMode.ForceItem(PolicyCatalog.KeyWsTrim),
                "an extreme-only key is driven by the tier alone");
            ExtremeMode.SetOptedOut(PolicyCatalog.KeyAudioLowLat, true);
            ExtCheck(ExtremeMode.ForcedPolicyValue(PolicyCatalog.KeyAudioLowLat) == null,
                "an opted-out item is no longer forced");
            ExtremeMode.SetOptedOut(PolicyCatalog.KeyAudioLowLat, false);
            ExtCheck(ExtremeMode.ForcedPolicyValue(PolicyCatalog.KeyAudioLowLat) == "1",
                "following again restores the force");
            ExtCheck(ExtremeMode.ForceGlobal("dwmboost"), "global tokens are forced too");
            ExtremeMode.SetOptedOut("g:dwmboost", true);
            ExtCheck(!ExtremeMode.ForceGlobal("dwmboost"), "global tokens honor the opt-out set");
        }

        // 极限组的电源旋钮不许和其它档位共用 否则电竞档会跟着被改
        //   只读表结构 不解析也不写入任何电源方案
        // 强制门只认硬件证据 纯判定函数 不读任何真实状态
        private static void ExtremeGatesFollowHardwareEvidence()
        {
            var kpti = new SpecMitigationTweak.State { QueryOk = true, KvaShadowEnabled = true, KvaShadowRequired = true };
            ExtCheck(SpecMitigationTweak.WorthDisabling(kpti), "KPTI in use is a real per-syscall cost");
            var legacyIbrs = new SpecMitigationTweak.State { QueryOk = true, BpbEnabled = true };
            ExtCheck(SpecMitigationTweak.WorthDisabling(legacyIbrs),
                "legacy IBRS without retpoline or eIBRS is worth removing");
            var retpoline = new SpecMitigationTweak.State
                { QueryOk = true, BpbEnabled = true, RetpolineEnabled = true, MbClearEnabled = true };
            ExtCheck(!SpecMitigationTweak.WorthDisabling(retpoline),
                "retpoline is near free; only the security cost would remain");
            var eibrs = new SpecMitigationTweak.State
                { QueryOk = true, BpbEnabled = true, EnhancedIbrs = true, SsbdSystemWide = true };
            ExtCheck(!SpecMitigationTweak.WorthDisabling(eibrs), "hardware eIBRS must not be forced off");
            ExtCheck(!SpecMitigationTweak.WorthDisabling(new SpecMitigationTweak.State()),
                "an unreadable state never qualifies");
            ExtCheck(VbsTweak.VirtualizationInUse(true, false, 0) && VbsTweak.VirtualizationInUse(false, true, 0)
                && VbsTweak.VirtualizationInUse(false, false, 1),
                "any hypervisor consumer blocks the forced VBS switch-off");
            ExtCheck(!VbsTweak.VirtualizationInUse(false, false, 0), "no consumer lets the forced path proceed");
            ulong gib = 1UL << 30;
            ExtCheck(!WsTrim.ShouldTrim(32 * gib, 20 * gib), "plenty of free memory makes trimming pure cost");
            ExtCheck(WsTrim.ShouldTrim(32 * gib, 3 * gib), "below the 4 GB floor the trim earns its keep");
            ExtCheck(WsTrim.ShouldTrim(64 * gib, 12 * gib),
                "below a quarter of total memory counts as pressure even above the floor");
            ExtCheck(!WsTrim.ShouldTrim(0, 0), "an unreadable memory status never trims");
            ExtCheck(CacheWarm.NvmeBlocks(true, true) && CacheWarm.NvmeBlocks(true, null),
                "the forced warm-up path skips NVMe and unknown buses alike");
            ExtCheck(!CacheWarm.NvmeBlocks(true, false) && !CacheWarm.NvmeBlocks(false, true),
                "SATA passes the forced path and a user's own switch ignores the bus entirely");
        }

        // 环境页只读区按账本渲染 切档不动它 停用销账后那一行就该消失
        //   这里只验账本这一个事实来源 不触发任何真实环境写入
        private static void ExtremePowerKnobsAreTierExclusive()
        {
            ExtCheck(PowerPlan.ExtremeKnobCountForTest > 0,
                "the extreme tier must actually add power knobs of its own");
            Guid[] guids = PowerPlan.ExtremeKnobGuidsForTest();
            foreach (Guid g in guids)
                ExtCheck(PowerPlan.ExtremeOnlyGuidForTest(g),
                    "every extreme knob must be absent from the shared columns");
            var seen = new System.Collections.Generic.HashSet<Guid>();
            foreach (Guid g in guids)
                ExtCheck(g != Guid.Empty && seen.Add(g),
                    "extreme knobs must be distinct and non-empty");
        }

        private static void ExtremeForcesEnvOnlyWhileUnlockedAndNotOptedOut()
        {
            int failed;
            ExtCheck(!ExtremeMode.ForcesEnv("gtimer"), "a locked state forces no environment item");
            ExtCheck(ExtremeMode.ReconcileEnvItems(out failed) == 0 && failed == 0,
                "the startup reconcile is a no-op while locked");
            ExtremeUnlockPastGate();
            ExtCheck(ExtremeMode.ForcesEnv("gmguard") && ExtremeMode.ForcesEnv("devpower"),
                "unlocking forces the items every machine is eligible for");
            ExtCheck(!ExtremeMode.ForcesEnv("gtimer") && !ExtremeMode.ForcesEnv("timertick"),
                "retired timer items are never forced even while unlocked");
            ExtremeMode.SetOptedOut("gmguard", true);
            ExtCheck(!ExtremeMode.ForcesEnv("gmguard") && ExtremeMode.ForcesEnv("devpower"),
                "an item stopped in the manage list is no longer forced");
        }

        // 英文输入与 Intel 低延迟走实时偏好 不经快照层 这里守住它们与快照层同一口径
        private static void ExtremeForcesLiveSessionPreferences()
        {
            ExtremeUnlockPastGate();
            var profile = new GameProfile { Id = "extreme-live", Name = "Mock game" };
            profile.Overrides[PolicyCatalog.KeyPreset] = "5";
            var mode = (GameMode)FormatterServices.GetUninitializedObject(typeof(GameMode));
            FamilyPolicySetField(mode, "sync", new object());
            FamilyPolicySetField(mode, "profiles", new List<GameProfile> { profile });
            FamilyPolicySetField(mode, "sessionPolicy", PolicyResolver.For(profile));
            ExtCheck((bool)FamilyPolicyInvoke(mode, "LiveBoolPreferenceLocked", PolicyCatalog.KeyEnglishInput, false),
                "an extreme session forces english input on with the global switch off");
            ExtCheck((bool)FamilyPolicyInvoke(mode, "LiveBoolPreferenceLocked", PolicyCatalog.KeyIntelLowLatency, false),
                "an extreme session forces intel low latency on with the global switch off");
            ExtCheck(!(bool)FamilyPolicyInvoke(mode, "LiveBoolPreferenceLocked", PolicyCatalog.KeyStandbyCleaner, false),
                "keys outside the extreme list keep the global value");
            ExtremeMode.SetOptedOut(PolicyCatalog.KeyIntelLowLatency, true);
            ExtCheck(!(bool)FamilyPolicyInvoke(mode, "LiveBoolPreferenceLocked", PolicyCatalog.KeyIntelLowLatency, false),
                "a stopped item is not forced in the live session either");
            profile.Overrides[PolicyCatalog.KeyPreset] = "1";
            FamilyPolicySetField(mode, "sessionPolicy", PolicyResolver.For(profile));
            ExtCheck(!(bool)FamilyPolicyInvoke(mode, "LiveBoolPreferenceLocked", PolicyCatalog.KeyEnglishInput, false),
                "an esports session leaves the global value alone");
            FamilyPolicySetField(mode, "sessionPolicy", PolicyResolver.Global());
            ExtCheck(!(bool)FamilyPolicyInvoke(mode, "LiveBoolPreferenceLocked", PolicyCatalog.KeyEnglishInput, false),
                "a global session on a non-extreme tier keeps the global value");
        }

        // 极限会话键的完整性 极限专属的 WsTrim AudioLowLat 不在策略目录里 ItemOf 返回 null
        //   守住 每个会话键要么在目录里能取到名字 要么是已知的极限专属键 不留没名字的孤儿
        private static void ExtremeManageListLabelsEverySessionKey()
        {
            foreach (string key in ExtremeMode.SessionPolicyKeys)
            {
                PolicyItem item = PolicyCatalog.ItemOf(key);
                if (item != null)
                {
                    ExtCheck(Lang.T(item.LangKey).Length > 0, "a catalog session key must resolve to a label: " + key);
                    continue;
                }
                bool known = key == PolicyCatalog.KeyWsTrim || key == PolicyCatalog.KeyAudioLowLat;
                ExtCheck(known, "a session key with no catalog item needs a manage-list fallback label: " + key);
            }
            ExtCheck(Lang.T("gm.wstrim").Length > 0 && Lang.T("gm.audiolat").Length > 0,
                "the extreme-only session keys keep their fallback labels");
        }

        private static void ExtremeCrashFuseTripsOnRepeatedAbnormalRestarts()
        {
            ExtCheck(!ExtremeCrashFuse.ShouldTrip(0) && !ExtremeCrashFuse.ShouldTrip(1),
                "one abnormal restart may be a pulled plug; the fuse waits");
            ExtCheck(ExtremeCrashFuse.ShouldTrip(2) && ExtremeCrashFuse.ShouldTrip(9),
                "two abnormal restarts since the unlock trip the fuse");
            string q = ExtremeCrashFuse.BuildQuery(new DateTime(2026, 9, 2, 8, 30, 0, DateTimeKind.Utc));
            ExtCheck(q.Contains("EventID=41") && q.Contains("EventID=1001") && q.Contains("EventID=6008")
                && q.Contains("2026-09-02T08:30:00.000Z"),
                "the query names the three abnormal-shutdown events and the unlock time");
            ExtCheck(ExtremeCrashFuse.CountCrashesSince(0) == 0, "no unlock time means nothing to count");
            int crashes;
            ExtCheck(!ExtremeCrashFuse.CheckAtStartup(out crashes) && crashes == 0,
                "a locked state never trips the fuse");
            ExtremeItem timer = null;
            foreach (ExtremeItem item in ExtremeMode.EnvItems()) if (item.Token == "timertick") timer = item;
            ExtCheck(timer != null && !timer.Eligible(), "the timer tick item stays listed for rollback but is never auto-written");
        }

        private static void ExtremeEnvLedgerDrivesTheReadout()
        {
            ExtremeUnlockPastGate();
            ExtCheck(!ExtremeMode.LedgerContains("memcompress"),
                "a fresh state claims no environment item");
            ExtremeItem[] items = ExtremeMode.EnvItems();
            ExtCheck(items.Length > 0, "the environment list must not be empty");
            bool everyItemHasCopy = true;
            foreach (ExtremeItem item in items)
                if (string.IsNullOrEmpty(item.Token) || string.IsNullOrEmpty(item.LangKey))
                    everyItemHasCopy = false;
            ExtCheck(everyItemHasCopy, "every environment row needs a token and a label to render");
            // 切档只改档位 不碰账本 这正是只读区在切档后依然列出它们的原因
            Settings.SaveStr("PerformancePreset", "5");
            bool beforeSwitch = ExtremeMode.LedgerContains("hags");
            Settings.SaveStr("PerformancePreset", "1");
            ExtCheck(ExtremeMode.LedgerContains("hags") == beforeSwitch,
                "switching tiers never changes what the ledger claims");
        }

        private static void ExtremeSnapshotOverlayLeavesUserConfigUntouched()
        {
            ExtremeUnlockPastGate();
            // 清单里仍在目录内的键 覆盖期间不改写用户自己的值 切走即恢复
            Settings.Save(PolicyCatalog.KeyCacheWarm, false);
            Settings.SaveStr("PerformancePreset", "5");
            PolicySnapshot snap = PolicyResolver.Global();
            ExtCheck(snap.Preset == PerformancePreset.Extreme, "the global snapshot carries the tier");
            ExtCheck(snap.ValueOf(PolicyCatalog.KeyCacheWarm) == "1",
                "the overlay forces the value while the tier is active");
            ExtCheck(Settings.Load(PolicyCatalog.KeyCacheWarm, true) == false,
                "the user's own configuration is never rewritten");
            Settings.SaveStr("PerformancePreset", "1");
            PolicySnapshot esports = PolicyResolver.Global();
            ExtCheck(esports.ValueOf(PolicyCatalog.KeyCacheWarm) == "0",
                "leaving the tier restores the user's own value at once");
            // 极限专属键没有全局值可回 调用方按 档位 且 ForceItem 求值 缺一即关
            ExtCheck(esports.Preset != PerformancePreset.Extreme,
                "the tier really changed for the second snapshot");
            ExtCheck(PolicyResolver.GlobalValue(PolicyCatalog.KeyAudioLowLat) == null,
                "an extreme-only key has no global value to fall back on");
        }
    }
}
#endif
