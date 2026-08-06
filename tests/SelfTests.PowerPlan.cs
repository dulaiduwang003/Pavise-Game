// @author bdth 2074055628@qq.com
// 文件用途 竞技电源计划参数表与真机读写的自测

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void TestPowerKnobTableHasNoDuplicates()
        {
            string[] pairs = PowerPlan.SelfTestKnobPairs();
            string[] labels = PowerPlan.SelfTestKnobLabels();
            Eq(pairs.Length, labels.Length);
            if (pairs.Length < 25) throw new Exception("参数表项数异常偏少: " + pairs.Length);

            var seenPair = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in pairs)
                if (!seenPair.Add(p)) throw new Exception("参数表存在重复的子组+设置 GUID: " + p);

            var seenLabel = new HashSet<string>();
            foreach (string l in labels)
                if (!seenLabel.Add(l)) throw new Exception("参数表存在重复的项名: " + l);
        }

        private static void TestPowerArenaDiffersFromCalm()
        {
            Eq(true, PowerPlan.SelfTestArenaDiffersFromCalm("最小处理器状态"));
            Eq(true, PowerPlan.SelfTestArenaDiffersFromCalm("核心停放最小核心数"));
            Eq(true, PowerPlan.SelfTestArenaDiffersFromCalm("允许节流状态"));
            Eq(true, PowerPlan.SelfTestArenaDiffersFromCalm("PCIe 链接电源管理"));
            Eq(false, PowerPlan.SelfTestArenaDiffersFromCalm("最大处理器状态"));
            Eq(false, PowerPlan.SelfTestArenaDiffersFromCalm("处理器最大频率"));
        }

        private static void TestPowerPlanWritesArenaValues()
        {
            Guid probe;
            if (!PowerPlan.SelfTestDuplicate(out probe)) Skip("本机无法复制电源计划");
            try
            {
                PowerPlan.SelfTestWriteName(probe, "Pavise 自测临时计划");

                if (!PowerPlan.SelfTestTune(probe, true, false))
                    throw new Exception("竞技档写入失败");

                ExpectKnob(probe, "最小处理器状态", 100);
                ExpectKnob(probe, "最大处理器状态", 100);
                ExpectKnob(probe, "核心停放最小核心数", 100);
                ExpectKnob(probe, "睿频模式", 2);
                ExpectKnob(probe, "允许节流状态", 0);
                ExpectKnob(probe, "PCIe 链接电源管理", 0);
                ExpectKnob(probe, "USB 选择性暂停", 0);
                ExpectKnob(probe, "关闭硬盘时间", 0);
                ExpectKnob(probe, "能源性能首选项", 0);

                if (!PowerPlan.SelfTestTune(probe, false, false))
                    throw new Exception("常规档写入失败");

                ExpectKnob(probe, "最小处理器状态", 20);
                ExpectKnob(probe, "核心停放最小核心数", 50);
                ExpectKnob(probe, "允许节流状态", 2);
                ExpectKnob(probe, "最大处理器状态", 100);
            }
            finally { PowerPlan.SelfTestDelete(probe); }
        }

        private static void TestPowerPlanNameRoundtrip()
        {
            Guid probe;
            if (!PowerPlan.SelfTestDuplicate(out probe)) Skip("本机无法复制电源计划");
            try
            {
                string title = "Pavise 自测命名 " + probe.ToString().Substring(0, 8);
                if (!PowerPlan.SelfTestWriteName(probe, title)) throw new Exception("写入方案名失败");
                Eq(title, PowerPlan.SelfTestName(probe));
            }
            finally { PowerPlan.SelfTestDelete(probe); }
        }

        private static void TestPowerPlanDeleteLeavesActiveIntact()
        {
            string before = PowerPlan.CurrentPlanLabel();
            Guid probe;
            if (!PowerPlan.SelfTestDuplicate(out probe)) Skip("本机无法复制电源计划");
            PowerPlan.SelfTestWriteName(probe, "Pavise 自测待删计划");
            if (!PowerPlan.SelfTestDelete(probe)) throw new Exception("删除临时计划失败");
            Eq("", PowerPlan.SelfTestName(probe));
            Eq(before, PowerPlan.CurrentPlanLabel());
        }

        private static void TestPowerPlanPurgesDuplicateClones()
        {
            Guid keep, dup1, dup2;
            if (!PowerPlan.SelfTestDuplicate(out keep)) Skip("本机无法复制电源计划");
            bool madeDup1 = PowerPlan.SelfTestDuplicate(out dup1);
            bool madeDup2 = PowerPlan.SelfTestDuplicate(out dup2);
            try
            {
                if (!madeDup1 || !madeDup2) Skip("本机无法复制出多份电源计划");
                PowerPlan.SelfTestWriteName(keep, PowerPlan.PlanTitle);
                PowerPlan.SelfTestWriteName(dup1, PowerPlan.PlanTitle);
                PowerPlan.SelfTestWriteName(dup2, PowerPlan.PlanTitle);

                PowerPlan.SelfTestPurge(keep);

                Eq(PowerPlan.PlanTitle, PowerPlan.SelfTestName(keep));
                Eq("", PowerPlan.SelfTestName(dup1));
                Eq("", PowerPlan.SelfTestName(dup2));
            }
            finally
            {
                PowerPlan.SelfTestDelete(keep);
                if (madeDup1) PowerPlan.SelfTestDelete(dup1);
                if (madeDup2) PowerPlan.SelfTestDelete(dup2);
            }
        }

        private static void TestPowerPlanPurgeSparesForeignSchemes()
        {
            Guid mine, foreign;
            if (!PowerPlan.SelfTestDuplicate(out mine)) Skip("本机无法复制电源计划");
            bool madeForeign = PowerPlan.SelfTestDuplicate(out foreign);
            try
            {
                if (!madeForeign) Skip("本机无法复制出多份电源计划");
                PowerPlan.SelfTestWriteName(mine, PowerPlan.PlanTitle);
                PowerPlan.SelfTestWriteName(foreign, "用户自己的计划");

                PowerPlan.SelfTestPurge(mine);

                Eq("用户自己的计划", PowerPlan.SelfTestName(foreign));
            }
            finally
            {
                PowerPlan.SelfTestDelete(mine);
                if (madeForeign) PowerPlan.SelfTestDelete(foreign);
            }
        }

        private static void TestPowerPlanMigratesLegacyClone()
        {
            const string key = "SelfTestLegacyPlanGuid";
            Guid legacy;
            if (!PowerPlan.SelfTestDuplicate(out legacy)) Skip("本机无法复制电源计划");
            try
            {
                PowerPlan.SelfTestWriteName(legacy, "Pavise 自测旧副本");
                Settings.SaveStr(key, legacy.ToString());

                PowerPlan.SelfTestMigrate(key);

                string nameAfter = PowerPlan.SelfTestName(legacy);
                if (nameAfter.Length != 0)
                    throw new Exception("旧副本未被删除，方案名仍是 " + nameAfter);
                string keyAfter = Settings.LoadStr(key, "");
                if (keyAfter.Length != 0)
                    throw new Exception("旧键未清空，仍是 " + keyAfter);
            }
            finally
            {
                PowerPlan.SelfTestDelete(legacy);
                Settings.SaveStr(key, "");
            }
        }

        private static void TestPowerPlanResolveIsIdempotent()
        {
            PowerPlan.SelfTestResetResolve();
            Settings.SaveStr("ArenaPlanGuid", "");
            int before = PowerPlan.SelfTestSchemeCount();

            Guid first = PowerPlan.SelfTestResolve();
            try
            {
                if (!PowerPlan.SelfTestTargetOwned()) Skip("本机无法创建独立电源计划");

                Eq(PowerPlan.PlanTitle, PowerPlan.SelfTestName(first));
                Eq(before + 1, PowerPlan.SelfTestSchemeCount());
                Eq(first.ToString(), Settings.LoadStr("ArenaPlanGuid", ""));

                PowerPlan.SelfTestResetResolve();
                Guid second = PowerPlan.SelfTestResolve();
                Eq(first, second);
                Eq(before + 1, PowerPlan.SelfTestSchemeCount());

                Settings.SaveStr("ArenaPlanGuid", "");
                PowerPlan.SelfTestResetResolve();
                Guid third = PowerPlan.SelfTestResolve();
                Eq(first, third);
                Eq(before + 1, PowerPlan.SelfTestSchemeCount());
            }
            finally
            {
                PowerPlan.SelfTestDelete(first);
                PowerPlan.SelfTestResetResolve();
                Settings.SaveStr("ArenaPlanGuid", "");
            }
        }

        private static void ExpectKnob(Guid scheme, string label, uint expected)
        {
            if (!PowerPlan.SelfTestSupports(scheme, label)) return;
            uint actual;
            if (!PowerPlan.SelfTestReadKnob(scheme, label, out actual))
                throw new Exception("读不回「" + label + "」");
            if (actual != expected)
                throw new Exception("「" + label + "」期望 " + expected + "，实际 " + actual);
        }
    }
}
