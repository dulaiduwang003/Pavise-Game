#if PAVISE_SELFTEST
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        // 掩码与混合架构判定来自 GLPIE 方案戳记来自 CPU Set 两套枚举各数一遍物理核
        //   对不上时保存的选核方案会因戳记不符被判无效 静默回落成不限核 用户毫无察觉
        //   这条跑的是本机真实拓扑 不是构造数据 失败就说明这台机器上真的对不上
        // 混合架构的能效核是单线程卡 选中标以前被卡片宽窄挡掉
        //   13900K 上 24 张卡有 16 张是能效核 选中后只剩填色 没有任何文字
        //   独占标同样两个字 一直画在单线程卡上 能画独占就能画选中
        private static void TestCoreCardHeadTag()
        {
            // 选中要出标 与卡片有几个线程无关 放不放得下另由实测宽度决定
            Eq(CoreMatrix.HeadTag.Primary, CoreMatrix.HeadTagFor(false, true, false, false));
            Eq(CoreMatrix.HeadTag.Primary, CoreMatrix.HeadTagFor(false, true, false, true));
            // 独占压过选中 两种卡宽都一样
            foreach (bool multi in new[] { false, true })
                Eq(CoreMatrix.HeadTag.Exclusive, CoreMatrix.HeadTagFor(true, true, true, multi));
            // 没选中时大缓存标压过 SMT
            Eq(CoreMatrix.HeadTag.Cache, CoreMatrix.HeadTagFor(false, false, true, true));
            // SMT 只给多线程卡 单线程卡谈超线程没有意义
            Eq(CoreMatrix.HeadTag.Smt, CoreMatrix.HeadTagFor(false, false, false, true));
            Eq(CoreMatrix.HeadTag.None, CoreMatrix.HeadTagFor(false, false, false, false));

            // 放得下才画 编号加间隙加标签刚好等于头宽算放得下 多一像素就不画
            Eq(true, CoreMatrix.HeadTagFits(46, 20, 23, 3));
            Eq(false, CoreMatrix.HeadTagFits(46, 20, 24, 3));
            // 宽卡有富余
            Eq(true, CoreMatrix.HeadTagFits(92, 20, 23, 3));
            // 没有标签就没有可画的 不能因为量得下就返回真
            Eq(false, CoreMatrix.HeadTagFits(92, 20, 0, 3));
        }

        // ProcessorCount 与枚举对不上时 AllMask 必须往窄里收
        //   宽了会让掩码带上机器上不存在的核 写进去必然失败还会污染方案戳记
        private static void TestAllMaskReconcile()
        {
            bool bad;
            // 一致时原样返回 不置位
            Eq(0xFFFUL, CpuTopology.ReconcileAllMask(0xFFF, 0xFFF, out bad));
            Eq(false, bad);
            // 枚举读不出来时只能信 ProcessorCount 不置位
            Eq(0xFFFUL, CpuTopology.ReconcileAllMask(0xFFF, 0, out bad));
            Eq(false, bad);
            // ProcessorCount 多报 收到枚举那一份
            Eq(0xFFFFUL, CpuTopology.ReconcileAllMask(0xFFFFFF, 0xFFFF, out bad));
            Eq(true, bad);
            // 枚举多报 收到 ProcessorCount 那一份 两个方向都往窄里收
            Eq(0xFFFFUL, CpuTopology.ReconcileAllMask(0xFFFF, 0xFFFFFF, out bad));
            Eq(true, bad);
            // 完全不相交时无法收窄 退回 ProcessorCount 不能返回空掩码
            Eq(0x0FUL, CpuTopology.ReconcileAllMask(0x0F, 0xF0, out bad));
            Eq(true, bad);
            Console.WriteLine("  本机 AllMask 对账 " + (CpuTopology.AllMaskReconciled ? "不一致 已收窄" : "一致"));
        }

        private static void TestTopologySourcesAgree()
        {
            Console.WriteLine("  本机拓扑 逻辑核 " + CpuTopology.CountSetBits(CpuTopology.AllMask)
                + " 物理核 GLPIE/CpuSet " + CpuTopology.TopologySourceCounts
                + " 混合架构 " + (CpuTopology.Hybrid ? "是" : "否"));
            Eq(true, CpuTopology.TopologySourcesAgree);
        }

        // 优选核只在最高能效档内部比评级 混合架构没有 TBM 优选核时不能把整组 P 核当优选
        private static void FavoredCoresOnlyWithinTopEfficiencyClass()
        {
            // 8P+8E 单一评级 没有优选核
            Eq(0UL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }));
            // 8P+8E P 核里 4 5 评级更高 优选核就是 4 5
            Eq(0x30UL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 2, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0 }));
            // 非混合架构 评级不同直接取最高
            Eq(0x0CUL, CpuTopology.FavoredMaskOf(
                new[] { 0, 1, 2, 3 }, new byte[] { 0, 0, 0, 0 }, new byte[] { 1, 1, 3, 3 }));
            Eq(0UL, CpuTopology.FavoredMaskOf(new int[0], new byte[0], new byte[0]));
            Eq(0UL, CpuTopology.FavoredMaskOf(new[] { 0, 1 }, new byte[] { 0 }, new byte[] { 1, 2 }));
            // 各核明细 相邻同值合并成段 乱序输入按核号排
            Eq("0-7:1/1 8-11:1/2 12-15:0/0", CpuTopology.DescribeCoreClasses(
                new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0 },
                new byte[] { 1, 1, 1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 0, 0, 0, 0 }));
            Eq("0:0/1 2-3:0/1", CpuTopology.DescribeCoreClasses(
                new[] { 3, 0, 2 }, new byte[] { 0, 0, 0 }, new byte[] { 1, 1, 1 }));
            Eq("", CpuTopology.DescribeCoreClasses(new int[0], new byte[0], new byte[0]));
            Console.WriteLine("PASS FavoredCores: ranked only within the top efficiency class");
        }

        internal static void RunCoreSchedulingTests(string output)
        {
            FavoredCoresOnlyWithinTopEfficiencyClass();
            CorePlanValidation(); CorePlanSerialization(); CorePlanMergedPageDraft(output);
            CorePlanProfileReturn(output); CorePlanUnsupportedIsolation(output); CorePlanUiAndPersistence(output);
        }

        private static CoreSchedulingPlan SampleCorePlan()
        {
            return new CoreSchedulingPlan { GameMask = 12, IsolationMask = 12,
                Topology = CoreScheduling.Stamp(255, new ulong[] { 3, 12, 48, 192 }) };
        }

        private static string ValidateSample(CoreSchedulingPlan plan)
        {
            return CoreScheduling.Validate(plan, 255, new ulong[] { 3, 12, 48, 192 }, false, false);
        }

        private static void CorePlanValidation()
        {
            var p = SampleCorePlan(); Eq(null, ValidateSample(p));
            p.GameMask = 68; Eq(null, ValidateSample(p)); // any pair of logical CPUs remains manual
            Eq(12UL, p.IsolationMask); Eq(68UL, p.GameMask);
            p = SampleCorePlan(); p.IsolationMask = 64; Eq("schedule.error.whole", ValidateSample(p));
            p.IsolationMask = 1024; Eq("schedule.error.whole", ValidateSample(p));
            // CPU 0 所在核可以独占 由用户决定 底线只剩独占之外留两颗完整物理核
            p = SampleCorePlan(); p.IsolationMask = 3; Eq(null, ValidateSample(p));
            p = SampleCorePlan(); p.IsolationMask = 252; Eq("schedule.error.spare", ValidateSample(p));
            p = SampleCorePlan(); p.IsolationOn = true; Eq("schedule.error.isolationunsupported", ValidateSample(p));
            Eq(null, CoreScheduling.Validate(p, 255, new ulong[] { 3, 12, 48, 192 }, false, true));
            p.IsolationMask = 0; Eq("schedule.error.isolationempty", ValidateSample(p));
            p = SampleCorePlan(); p.GameMask = 1; Eq("schedule.error.game", ValidateSample(p));
            p.GameMask = 1024; Eq("schedule.error.game", ValidateSample(p));
            p = SampleCorePlan(); p.Topology = "other"; Eq("schedule.error.topology", ValidateSample(p));
            p = SampleCorePlan(); Eq("schedule.error.groups", CoreScheduling.Validate(p, 255, new ulong[] { 3, 12, 48, 192 }, true, false));
            Console.WriteLine("PASS CorePlanValidation: whole cores, CPU0, spare cores, topology, unsupported isolation");
        }

        private static void CorePlanSerialization()
        {
            var original = SampleCorePlan(); CoreSchedulingPlan parsed;
            Eq(true, CoreSchedulingPlan.TryParse(original.Encode(), out parsed)); Eq(original.Encode(), parsed.Encode());
            foreach (string bad in new[] { "", "3|x|C|0|C", "2|x|C|0|C|1|C0", "2|x|C|true|C",
                "1|x|C|true|C|1|C0", "1|x| C|0|C|1|C0", "1|x|C|0|C|1|10000000000000000",
                "1||C|0|C|1|C0", "1|x|C|0|C|1|C0|extra", "2|x|10000000000000000|0|C" })
            {
                Eq(false, CoreSchedulingPlan.TryParse(bad, out parsed)); Eq(true, parsed.ReadFailed);
            }
            original.GameMask = 1UL << 63;
            Eq(true, CoreSchedulingPlan.TryParse(original.Encode(), out parsed)); Eq(1UL << 63, parsed.GameMask);
            // Previously enabled, empty, conflicting, partial or obsolete heavy ranges do not block migration.
            original = SampleCorePlan();
            foreach (string heavy in new[] { "0", "C", "C0", "40", "FFFFFFFFFFFFFFFF" })
            {
                string old = "1|" + original.Topology + "|C|1|C|1|" + heavy;
                Eq(true, CoreSchedulingPlan.TryParse(old, out parsed));
                Eq(12UL, parsed.GameMask); Eq(true, parsed.IsolationOn); Eq(12UL, parsed.IsolationMask);
                Eq("2|" + original.Topology + "|C|1|C", parsed.Encode());
                parsed.IsolationOn = false; Eq(null, ValidateSample(parsed));
            }
            Console.WriteLine("PASS CorePlanSerialization: V1 migration retires heavy fields; V2 strict parsing and bit 63");
        }

        private static void CorePlanUiAndPersistence(string output)
        {
            using (var f = new UiConfigCoreFixture(output, "core-scheduling"))
            {
                CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                var legacySession = PolicyResolver.Global();
                var plan = SampleCorePlan();
                string legacy = "1|" + plan.Topology + "|C|0|C|1|C"; // old heavy/game conflict
                Settings.SaveStr(CoreScheduling.Key, legacy);
                Eq(false, legacySession.ManualPlacement); Eq(true, legacySession.StrictCores);
                Eq("0", legacySession.ValueOf(PolicyCatalog.KeyHeavySqueeze));
                f.Editor.Reload();
                Eq(false, f.Editor.SaveButton.Enabled);
                // 分配与独占合成一页 只剩一张选核图 独占范围由它推出
                Eq(true, f.Editor.Matrix.Visible);
                Eq(true, f.Editor.Contains(f.Editor.Matrix));
                Eq(12UL, f.Editor.Draft.GameMask);
                f.Editor.Matrix.ToggleCpu(4); Eq(28UL, f.Editor.Draft.GameMask);
                // 28 是 bit2,3,4 覆盖 12 和 48 两颗核 对齐整核得 60 不含核 3
                Eq(60UL, f.Editor.Draft.IsolationMask); Eq(true, f.Editor.SaveButton.Enabled);
                Eq(CoreScheduling.IsolationSupported, f.Editor.IsolationToggle.Enabled);
                Eq(28UL, f.Editor.Matrix.Selected);
                Eq(CoreMatrix.GameColor, f.Editor.Matrix.SelectionColor);
                Eq(false, f.Editor.Matrix.SelectWholeCore);
                // CPU 0 所在核也能独占 选它就是它
                f.Editor.SelectMask(3);
                Eq(3UL, f.Editor.Draft.IsolationMask);

                f.Editor.SelectMask(240); f.Editor.PhysicalOnlyButton.PerformClick();
                Eq(80UL, f.Editor.Draft.GameMask); Eq(240UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.PhysicalOnlyButton.Enabled);
                f.Editor.SelectMask(160); f.Editor.PhysicalOnlyButton.PerformClick();
                Eq(160UL, f.Editor.Draft.GameMask); // preserve selected SMT sibling, no new CCD
                f.Editor.SelectMask(28);
                string before = Settings.LoadStr(CoreScheduling.Key, "");
                Settings.SuspendWritesForReset();
                try { f.Editor.SaveDraft(); Eq(before, Settings.LoadStr(CoreScheduling.Key, "")); }
                finally { Settings.UseTransientStoreForCurrentProcess(); Settings.SaveStr(CoreScheduling.Key, before); }
                Eq(true, f.Editor.SaveButton.Enabled);
                f.Editor.SaveDraft(); Eq(28UL, CoreScheduling.LoadGlobal().GameMask);
                Eq(60UL, CoreScheduling.LoadGlobal().IsolationMask); Eq(false, f.Editor.SaveButton.Enabled);
                Eq(true, Settings.LoadStr(CoreScheduling.Key, "").StartsWith("2|"));

                var entered = CoreScheduling.LoadGlobal(); entered.IsolationOn = true;
                Settings.SaveStr(CoreScheduling.Key, entered.Encode());
                var frozen = PolicyResolver.Global();
                var changed = CoreScheduling.LoadGlobal(); changed.GameMask = 3; changed.IsolationOn = false; changed.IsolationMask = 48;
                Settings.SaveStr(CoreScheduling.Key, changed.Encode());
                Eq(28UL, frozen.CoreMask); Eq(3UL, PolicyResolver.Global().CoreMask);
                Eq(true, frozen.CorePlan.IsolationOn); Eq(60UL, frozen.CorePlan.IsolationMask);
                Eq(false, PolicyResolver.Global().CorePlan.IsolationOn); Eq(48UL, PolicyResolver.Global().CorePlan.IsolationMask);
                f.Editor.SelectMask(48); Eq(false, f.Editor.SaveButton.Enabled);
                Eq(48UL, f.Editor.Draft.GameMask); f.Editor.Reload();

                var profileEditor = f.ProfileEditor(); profileEditor.FollowToggle.Checked = false;
                // 独占只有全局一份 逐游戏页不给开关 选核也不改独占范围
                Eq(false, profileEditor.IsolationToggle.Enabled);
                Eq(false, profileEditor.IsolationToggle.Visible);
                profileEditor.SelectMask(12);
                Eq(48UL, profileEditor.Draft.IsolationMask);
                Eq(true, profileEditor.SaveButton.Enabled);
                string token = CoreScheduling.ProfileToken(f.Family.Current(f.Family.First.Id));
                var custom = CoreScheduling.LoadGlobal(); custom.GameMask = 48;
                Eq(null, f.Family.Mode.SaveCoreScheduling(custom, CoreScheduling.GlobalToken(), f.Family.First.Id, false, token));
                var disk = new GameProfileStore(f.Family.DirectoryPath).LoadProfiles();
                Eq(CoreScheduling.ProfileToken(f.Family.Current(f.Family.First.Id)),
                    CoreScheduling.ProfileToken(FamilyPolicyFind(disk, f.Family.First.Id)));
                profileEditor.RefreshView(); Eq(false, profileEditor.SaveButton.Enabled);
                profileEditor.Reload(); profileEditor.FollowToggle.Checked = true;
                Eq(3UL, profileEditor.Matrix.Selected); Eq(false, profileEditor.PhysicalOnlyButton.Enabled);
                profileEditor.FollowToggle.Checked = false;
                Eq(48UL, profileEditor.Draft.GameMask); Eq(48UL, profileEditor.Matrix.Selected);
                profileEditor.SelectMask(240); profileEditor.PhysicalOnlyButton.PerformClick(); Eq(80UL, profileEditor.Draft.GameMask);

                // Legacy heavy overrides must neither block global edits nor survive a profile save.
                var second = FamilyPolicyFind((System.Collections.Generic.List<GameProfile>)
                    FamilyPolicyGetField(f.Family.Mode, "profiles"), f.Family.First.Id);
                second.Overrides[PolicyCatalog.KeyHeavySqueeze] = "1";
                second.Overrides[CoreScheduling.HeavyMaskKey] = "C";
                second.Overrides[CoreScheduling.Key] = legacy;
                changed = CoreScheduling.LoadGlobal(); changed.GameMask = 12;
                Eq(null, f.Family.Mode.SaveCoreScheduling(changed, CoreScheduling.GlobalToken(), null, false, null));
                Eq(12UL, CoreScheduling.LoadGlobal().GameMask);
                Eq(12UL, PolicyResolver.For(second).CoreMask);
                Eq("0", PolicyResolver.For(second).ValueOf(PolicyCatalog.KeyHeavySqueeze));
                Eq(null, f.Family.Mode.SaveCoreScheduling(changed, CoreScheduling.GlobalToken(), second.Id, false, CoreScheduling.ProfileToken(second)));
                second = f.Family.Current(second.Id);
                Eq(false, second.Overrides.ContainsKey(PolicyCatalog.KeyHeavySqueeze));
                Eq(false, second.Overrides.ContainsKey(CoreScheduling.HeavyMaskKey));
                Eq(true, second.Overrides[CoreScheduling.Key].StartsWith("2|"));
                Eq(null, f.Family.Mode.SaveCoreScheduling(changed, CoreScheduling.GlobalToken(), second.Id, true, CoreScheduling.ProfileToken(second)));

                plan.IsolationOn = true; Settings.SaveStr(CoreScheduling.Key, plan.Encode());
                f.Editor.Reload(); Eq(true, f.Editor.IsolationToggle.Enabled);
                f.Editor.IsolationToggle.Checked = false; Eq(12UL, f.Editor.Draft.IsolationMask);
                Eq(true, f.Editor.SaveButton.Enabled);
                f.Editor.SaveDraft(); Eq(false, CoreScheduling.LoadGlobal().IsolationOn);
                if (CoreScheduling.IsolationSupported)
                {
                    // 全选时独占之外一颗不剩 勾不上也存不了空范围
                    f.Editor.SelectMask(255);
                    f.Editor.IsolationToggle.Checked = true;
                    Eq(false, f.Editor.Draft.IsolationOn); Eq(0UL, f.Editor.Draft.IsolationMask);
                    // 换成能推出范围的选核 独占才立得住
                    f.Editor.SelectMask(12);
                    f.Editor.IsolationToggle.Checked = true;
                    Eq(true, f.Editor.Draft.IsolationOn); Eq(12UL, f.Editor.Draft.IsolationMask);
                    Eq(true, f.Editor.SaveButton.Enabled);
                }
                Settings.SaveStr(CoreScheduling.Key, "broken record");
                Eq(0UL, PolicyResolver.Global().CoreMask);
                Eq("0", PolicyResolver.Global().ValueOf(PolicyCatalog.KeyHeavySqueeze));
                Eq("broken record", Settings.LoadStr(CoreScheduling.Key, ""));

                CoreEditorScreenshots(f, output);
            }
            Console.WriteLine("PASS CorePlanUiAndPersistence: separate game/isolation pages, No HT, migration, frozen session, failed writes, CAS");
        }

        // 默认选核是全选 独占会占满所有核 一颗都不留给系统 开关会灰掉
        //   这是用户实际撞到的状态 必须能一键让出并且提示说得清
        private static void CorePlanExclusiveFromFullSelection()
        {
            ulong[] cores = new ulong[] { 3, 12, 48, 192 };
            // 全选时推不出独占范围
            Eq(0UL, CoreScheduling.ExclusiveMaskFor(255, cores));
            // 让出编号最大的两颗整核之后成立
            ulong trimmed = CoreScheduling.TrimForExclusive(255, cores);
            Eq(15UL, trimmed);
            Eq(15UL, CoreScheduling.ExclusiveMaskFor(trimmed, cores));
            Eq(2, CoreScheduling.SpareCoresOutside(15, cores));

            // CPU 0 所在核可以独占 是否选它由用户决定
            Eq(3UL, CoreScheduling.ExclusiveMaskFor(3, cores));
            Eq(3UL, CoreScheduling.TrimForExclusive(3, cores));
            // 已经成立的选核不该被动 让出只在需要时发生
            Eq(12UL, CoreScheduling.TrimForExclusive(12, cores));
            Eq(0UL, CoreScheduling.TrimForExclusive(0, cores));

            // 本机同构的混合拓扑 24 颗物理核 全选后让出一颗即可
            var many = new System.Collections.Generic.List<ulong>();
            ulong all = 0;
            for (int i = 0; i < 8; i++) { ulong c = 3UL << (i * 2); many.Add(c); all |= c; }
            for (int i = 0; i < 16; i++) { ulong c = 1UL << (16 + i); many.Add(c); all |= c; }
            ulong[] hybrid = many.ToArray();
            Eq(0UL, CoreScheduling.ExclusiveMaskFor(all, hybrid));
            ulong hybridTrimmed = CoreScheduling.TrimForExclusive(all, hybrid);
            Eq(true, hybridTrimmed != 0);
            Eq(true, CoreScheduling.ExclusiveMaskFor(hybridTrimmed, hybrid) != 0);
            Eq(2, CoreScheduling.SpareCoresOutside(
                CoreScheduling.ExclusiveMaskFor(hybridTrimmed, hybrid), hybrid));
            // 让出的是末尾的核 CPU 0 所在核仍在独占里 用户没主动取消就不动它
            Eq(3UL, CoreScheduling.ExclusiveMaskFor(hybridTrimmed, hybrid) & 3UL);
            Console.WriteLine("PASS CorePlanExclusiveFromFullSelection: default full selection can be trimmed into a usable exclusive range");
        }

        private static void CorePlanMergedPageDraft(string output)
        {
            using (var f = new UiConfigCoreFixture(output, "core-scheduling-merged-page"))
            {
                CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                Settings.SaveStr(CoreScheduling.Key, SampleCorePlan().Encode());
                f.Editor.Reload();

                // 选核一变 独占范围立刻跟着推出来 不需要再选第二遍
                f.Editor.SelectMask(28);
                Eq(28UL, f.Editor.Draft.GameMask); Eq(60UL, f.Editor.Draft.IsolationMask);
                Eq(28UL, f.Editor.Matrix.SchedulingGameMask);
                f.Editor.SaveButton.PerformClick();
                Eq(28UL, CoreScheduling.LoadGlobal().GameMask); Eq(60UL, CoreScheduling.LoadGlobal().IsolationMask);
                Eq(false, f.Editor.SaveButton.Enabled);

                f.Editor.SelectMask(48); f.Editor.SaveButton.PerformClick();
                Eq(48UL, CoreScheduling.LoadGlobal().GameMask); Eq(48UL, CoreScheduling.LoadGlobal().IsolationMask);

                // 显式重载才丢弃草稿
                f.Editor.SelectMask(28);
                ((PillButton)typeof(CoreSchedulingPanel).GetField("reloadButton",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(f.Editor)).PerformClick();
                Eq(48UL, f.Editor.Draft.GameMask); Eq(48UL, f.Editor.Draft.IsolationMask);
                Eq(48UL, f.Editor.Matrix.Selected);

                // 选核不合法就存不了 独占范围同时退回空
                string stored = Settings.LoadStr(CoreScheduling.Key, "");
                f.Editor.SelectMask(1);
                Eq(3UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.SaveButton.Enabled);
                f.Editor.SaveButton.PerformClick(); Eq(stored, Settings.LoadStr(CoreScheduling.Key, ""));

                // 外部改动造成版本冲突时保留草稿 直到用户显式重载
                f.Editor.SelectMask(28);
                var external = CoreScheduling.LoadGlobal(); external.GameMask = 192; external.IsolationMask = 192;
                Settings.SaveStr(CoreScheduling.Key, external.Encode());
                f.Editor.RefreshView();
                Eq(28UL, f.Editor.Draft.GameMask); Eq(60UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.SaveButton.Enabled);
                f.Editor.SaveButton.PerformClick();
                Eq(external.Encode(), Settings.LoadStr(CoreScheduling.Key, ""));
                ((PillButton)typeof(CoreSchedulingPanel).GetField("reloadButton",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(f.Editor)).PerformClick();
                Eq(192UL, f.Editor.Draft.GameMask); Eq(192UL, f.Editor.Draft.IsolationMask);
                Eq(false, f.Editor.SaveButton.Enabled);
            }
            Console.WriteLine("PASS CorePlanMergedPageDraft: exclusive range follows the selection, shared validation, conflict preserves draft");
        }

        private static void CorePlanProfileReturn(string output)
        {
            bool? previousSupport = CoreScheduling.IsolationSupportedForTest;
            CoreScheduling.IsolationSupportedForTest = true;
            try
            {
                using (var f = new UiConfigCoreFixture(output, "core-scheduling-profile-return"))
                {
                    CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                    Settings.SaveStr(CoreScheduling.Key, SampleCorePlan().Encode());
                    var editor = f.ProfileEditor();
                    editor.FollowToggle.Checked = false; editor.SelectMask(28); editor.SaveDraft();
                    Eq(false, editor.SaveButton.Enabled);

                    // Refresh inherited isolation and its baseline without turning an unchanged local plan dirty.
                    var clean = editor.CaptureEditorState();
                    var global = CoreScheduling.LoadGlobal();
                    global.GameMask = 192; global.IsolationOn = true; global.IsolationMask = 48;
                    Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(clean);
                    Eq(28UL, editor.Draft.GameMask); Eq(false, editor.FollowToggle.Checked);
                    Eq(true, editor.Draft.IsolationOn); Eq(48UL, editor.Draft.IsolationMask);
                    Eq(48UL, editor.Matrix.SchedulingIsolationMask); Eq(false, editor.SaveButton.Enabled);
                    Eq(Lang.F("schedule.exclusive.global", Lang.T("schedule.on"), CpuTopology.DescribeMask(48)),
                        CoreEditorField<SettingCard>(editor, "exclusiveCard").Desc);
                    Eq(true, CoreEditorField<Label>(editor, "status").Text.StartsWith(Lang.T("schedule.stored.active")));

                    // A follow-global edit is also preserved while the inherited range changes.
                    editor.FollowToggle.Checked = true;
                    var following = editor.CaptureEditorState();
                    global.IsolationMask = 12; Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(following);
                    Eq(true, editor.FollowToggle.Checked); Eq(28UL, editor.Draft.GameMask);
                    Eq(192UL, editor.Matrix.Selected); Eq(12UL, editor.Matrix.SchedulingIsolationMask);
                    Eq(true, editor.SaveButton.Enabled);
                    editor.FollowToggle.Checked = false;

                    // A broken global game selection does not invalidate a valid per-game selection.
                    var beforeGlobalGameEdit = editor.CaptureEditorState();
                    global.GameMask = 1; global.IsolationMask = 48;
                    Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(beforeGlobalGameEdit);
                    Eq(28UL, editor.Draft.GameMask); Eq(48UL, editor.Draft.IsolationMask);
                    editor.SelectMask(48); Eq(true, editor.SaveButton.Enabled);
                    editor.SaveDraft(); Eq(48UL, PolicyResolver.For(f.Family.Current(f.Family.First.Id)).CoreMask);
                    Eq(1UL, CoreScheduling.LoadGlobal().GameMask);
                    global.GameMask = 192; global.IsolationMask = 12;
                    Settings.SaveStr(CoreScheduling.Key, global.Encode()); editor.Reload();

                    // A genuine profile edit must still conflict even when global isolation also changed.
                    editor.SelectMask(28);
                    var dirty = editor.CaptureEditorState();
                    var external = CoreScheduling.LoadGlobal(); external.GameMask = 192;
                    Eq(null, f.Family.Mode.SaveCoreScheduling(external, CoreScheduling.GlobalToken(), f.Family.First.Id,
                        false, CoreScheduling.ProfileToken(f.Family.Current(f.Family.First.Id))));
                    global.IsolationMask = 48; Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(dirty);
                    Eq(28UL, editor.Draft.GameMask); Eq(12UL, editor.Draft.IsolationMask);
                    Eq(false, editor.FollowToggle.Checked); Eq(false, editor.SaveButton.Enabled);
                    Eq(true, CoreEditorField<Label>(editor, "status").Text.StartsWith(Lang.T("schedule.error.changed")));
                    editor.SaveDraft(); Eq(192UL, PolicyResolver.For(f.Family.Current(f.Family.First.Id)).CoreMask);

                    // Following global cannot bypass an unreadable global record on return.
                    editor.Reload(); editor.FollowToggle.Checked = true;
                    var beforeCorruption = editor.CaptureEditorState();
                    Settings.SaveStr(CoreScheduling.Key, "broken record");
                    editor.RestoreEditorState(beforeCorruption);
                    Eq(true, editor.FollowToggle.Checked); Eq(192UL, editor.Draft.GameMask);
                    Eq(48UL, editor.Draft.IsolationMask); Eq(false, editor.SaveButton.Enabled);
                    Eq(true, CoreEditorField<Label>(editor, "status").Text.StartsWith(Lang.T("schedule.error.changed")));
                    editor.SaveDraft(); Eq("broken record", Settings.LoadStr(CoreScheduling.Key, ""));

                    // A different physical layout must not be rebased onto this profile's current topology.
                    global.Topology = "old-topology"; Settings.SaveStr(CoreScheduling.Key, global.Encode());
                    editor.RestoreEditorState(beforeCorruption);
                    Eq(192UL, editor.Draft.GameMask); Eq(48UL, editor.Draft.IsolationMask);
                    Eq(false, editor.SaveButton.Enabled);
                    Eq(true, CoreEditorField<Label>(editor, "status").Text.StartsWith(Lang.T("schedule.error.changed")));
                }
            }
            finally { CoreScheduling.IsolationSupportedForTest = previousSupport; }
            Console.WriteLine("PASS CorePlanProfileReturn: refreshed inherited isolation, clean baseline, preserved follow/game draft, real conflicts retained");
        }

        private static void CorePlanUnsupportedIsolation(string output)
        {
            bool? previousSupport = CoreScheduling.IsolationSupportedForTest;
            CoreScheduling.IsolationSupportedForTest = false;
            try
            {
                using (var f = new UiConfigCoreFixture(output, "core-scheduling-unsupported"))
                {
                    CpuTopology.InjectTopologyForTest(255, new ulong[] { 3, 12, 48, 192 }, new ulong[] { 15, 240 }, 0, 0, 0, 0, false, false);
                    Settings.SaveStr(CoreScheduling.Key, SampleCorePlan().Encode());
                    f.Editor.Reload();
                    var card = CoreEditorField<SettingCard>(f.Editor, "exclusiveCard");
                    Eq(Lang.T("schedule.error.isolationunsupported"), card.Desc);
                    Eq(false, f.Editor.IsolationToggle.Enabled);

                    // Existing enabled settings can still be turned off on an unsupported system.
                    var global = CoreScheduling.LoadGlobal(); global.IsolationOn = true;
                    Settings.SaveStr(CoreScheduling.Key, global.Encode()); f.Editor.Reload();
                    Eq(Lang.T("schedule.error.isolationunsupported"), card.Desc);
                    Eq(true, f.Editor.IsolationToggle.Enabled); Eq(false, f.Editor.SaveButton.Enabled);
                    f.Editor.IsolationToggle.Checked = false;
                    Eq(true, f.Editor.SaveButton.Enabled); f.Editor.SaveDraft();
                    Eq(false, CoreScheduling.LoadGlobal().IsolationOn); Eq(12UL, CoreScheduling.LoadGlobal().IsolationMask);
                    Eq(Lang.T("schedule.error.isolationunsupported"), card.Desc);

                    CoreScheduling.IsolationSupportedForTest = true; f.Editor.RefreshView();
                    Eq(Lang.T("schedule.exclusive.off"), card.Desc);
                    f.Editor.IsolationToggle.Checked = true;
                    Eq(Lang.F("schedule.exclusive.on", CpuTopology.DescribeMask(12)), card.Desc);
                }
            }
            finally { CoreScheduling.IsolationSupportedForTest = previousSupport; }
            Console.WriteLine("PASS CorePlanUnsupportedIsolation: explicit support guidance, existing enabled setting can be disabled");
        }

        private static T CoreEditorField<T>(CoreSchedulingPanel editor, string name)
        {
            return (T)typeof(CoreSchedulingPanel).GetField(name,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(editor);
        }

        private static void CoreEditorScreenshots(UiConfigCoreFixture f, string output)
        {
            ulong[] cores = new ulong[16];
            for (int i = 0; i < cores.Length; i++) cores[i] = 3UL << (i * 2);
            CpuTopology.InjectTopologyForTest(uint.MaxValue, cores, new ulong[] { 65535, 0xFFFF0000 },
                0, 0, 0, 0, false, false);
            Settings.SaveStr(CoreScheduling.Key, new CoreSchedulingPlan { GameMask = 65532,
                IsolationOn = true, IsolationMask = 65532, Topology = CoreScheduling.CurrentStamp }.Encode());
            int language = Lang.Cur; bool light = Theme.LightMode; float scale = Dpi.Scale;
            try
            {
                foreach (float screenshotScale in new[] { 1f, 1.5f })
                foreach (int screenshotLanguage in new[] { 0, 1, 2 })
                foreach (bool screenshotLight in new[] { false, true })
                {
                    Dpi.Scale = screenshotScale; Theme.DropFontCache(); Lang.Cur = screenshotLanguage; Theme.SetLight(screenshotLight);
                    // Recreate after topology, language and scale changes so all labels and bounds match.
                    using (var editor = new CoreSchedulingPanel(900, null, delegate { return null; }, delegate { return false; }))
                    {
                        f.Panel.Controls.Add(editor);
                        editor.Matrix.ShowSelectionImmediately(editor.Draft.GameMask);
                        string suffix = "-" + screenshotLanguage + "-" + (screenshotLight ? "light" : "dark")
                            + "-" + (screenshotScale == 1f ? "100" : "150");
                        using (var bitmap = new Bitmap(editor.Width, editor.Height))
                        {
                            editor.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(Path.Combine(output, "core-scheduling-game" + suffix + ".png"));
                        }
                        // 合成一页之后的纵向次序 选核图在上 独占开关在下 保存在最后
                        Eq(true, CoreEditorBounds(editor, editor.Matrix).Bottom
                            < CoreEditorBounds(editor, editor.IsolationToggle).Top);
                        Eq(true, CoreEditorBounds(editor, editor.IsolationToggle).Bottom
                            < CoreEditorBounds(editor, editor.SaveButton).Top);
                    }
                }
                Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light);
                var perGame = f.ProfileEditor(); perGame.Reload();
                using (var bitmap = new Bitmap(perGame.Width, perGame.Height))
                {
                    perGame.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    bitmap.Save(Path.Combine(output, "core-scheduling-editor-profile.png"));
                }
            }
            finally { Dpi.Scale = scale; Theme.DropFontCache(); Lang.Cur = language; Theme.SetLight(light); }
            Console.WriteLine("CORE_EDITOR_IMAGE " + Path.Combine(output, "core-scheduling-game-0-dark-100.png"));
            Console.WriteLine("CORE_ISOLATION_IMAGE " + Path.Combine(output, "core-scheduling-isolation-0-dark-100.png"));
        }

        private static Rectangle CoreEditorBounds(Control root, Control child)
        {
            Point p = child.Location;
            for (Control parent = child.Parent; parent != root; parent = parent.Parent) p.Offset(parent.Location);
            return new Rectangle(p, child.Size);
        }
    }
}
#endif
