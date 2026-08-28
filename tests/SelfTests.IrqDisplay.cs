// 文件用途 验证短局原始展示与严格建议分离；不创建窗口、不启动 ETW、不写系统配置。
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static IrqSessionRecord IrqDisplayRecord(int seconds)
        {
            IrqSessionRecord rec = IrqStatusRecord();
            rec.DurationSeconds = seconds;
            rec.GameMask = 1;
            rec.Drivers[0].Dpc = Math.Max(1, seconds) * 20;
            rec.Drivers[0].DpcMaxNs = 900000;
            rec.Drivers[0].Over500Us = Math.Max(1, seconds) * 10;
            return rec;
        }

        private static void AssertIrqScoreEqual(IrqDriverVerdict expected, IrqDriverVerdict actual)
        {
            Eq(expected.SessionsSeen, actual.SessionsSeen);
            Eq(expected.SessionsOverThreshold, actual.SessionsOverThreshold);
            Eq(expected.ScoredSeconds, actual.ScoredSeconds);
            Eq(expected.OverlapOver500, actual.OverlapOver500);
            Eq(expected.OverlapWorstMaxUs, actual.OverlapWorstMaxUs);
            Eq(expected.OverlapCpuMask, actual.OverlapCpuMask);
            Eq(expected.Collisions, actual.Collisions);
            Eq(expected.StructuralConflict, actual.StructuralConflict);
            Eq(expected.Worth, actual.Worth);
        }

        private static void TestIrqShortObservationDisplay()
        {
            foreach (int duration in new[] { 1, 30, 59 })
            {
                var all = new List<IrqSessionRecord> { IrqDisplayRecord(duration) };
                int used, displayed;
                List<IrqDriverVerdict> strict = IrqVerdict.Evaluate(all, 60, out used);
                Eq(0, used);
                Eq(0, strict.Count);
                List<IrqDriverVerdict> raw = IrqVerdict.AggregateForDisplay(all, strict,
                    "1000000", "status-test-topology", out displayed);
                Eq(1, displayed);
                Eq(1, raw.Count);
                Eq(1200.0, raw[0].DpcPerMinute);
                Eq(900.0, raw[0].WorstMaxUs);
                AssertIrqScoreEqual(new IrqDriverVerdict(), raw[0]);
            }

            var shortOnly = new List<IrqSessionRecord>
            { IrqDisplayRecord(1), IrqDisplayRecord(30), IrqDisplayRecord(59) };
            int eligible, shown;
            List<IrqDriverVerdict> noAdvice = IrqVerdict.Evaluate(shortOnly, 60, out eligible);
            Eq(0, eligible);
            List<IrqDriverVerdict> shortRaw = IrqVerdict.AggregateForDisplay(shortOnly, noAdvice,
                "1000000", "status-test-topology", out shown);
            Eq(3, shown);
            Eq(1200.0, shortRaw[0].DpcPerMinute);
            AssertIrqScoreEqual(new IrqDriverVerdict(), shortRaw[0]);

            // 使用既有 Session 工厂的本次 boot 标记，以覆盖真正的 Evaluate 入口；
            // 仅查询时间基准，不采集硬件、设备或 ETW 数据。
            var eligibleMatches = new List<IrqSessionRecord>();
            for (int i = 0; i < 3; i++)
            {
                IrqSessionRecord rec = IrqDisplayRecord(120);
                rec.BootStamp = IrqAffinityEngine.BootStamp();
                rec.TopologyStamp = "";
                eligibleMatches.Add(rec);
            }
            List<IrqDriverVerdict> baseline = IrqVerdict.Evaluate(eligibleMatches, 60, out eligible);
            Eq(3, eligible);
            Eq(true, baseline[0].Worth);
            Eq(360.0, baseline[0].ScoredSeconds);
            foreach (int duration in new[] { 1, 20, 30, 59 })
            {
                IrqSessionRecord rec = IrqDisplayRecord(duration);
                rec.BootStamp = eligibleMatches[0].BootStamp;
                rec.TopologyStamp = "";
                rec.Drivers[0].DpcMaxNs = 5000000;
                eligibleMatches.Add(rec);
            }
            List<IrqDriverVerdict> stillStrict = IrqVerdict.Evaluate(eligibleMatches, 60, out eligible);
            Eq(3, eligible);
            AssertIrqScoreEqual(baseline[0], stillStrict[0]);
            List<IrqDriverVerdict> display = IrqVerdict.EvaluateForDisplay(eligibleMatches,
                60, out eligible, out shown);
            Eq(7, shown);
            Eq(3, eligible);
            Eq(5000.0, display[0].WorstMaxUs);
            AssertIrqScoreEqual(baseline[0], display[0]);

            var notEnough = new List<IrqSessionRecord>
            { eligibleMatches[0], eligibleMatches[3], eligibleMatches[4] };
            display = IrqVerdict.EvaluateForDisplay(notEnough, 60, out eligible, out shown);
            Eq(3, shown);
            Eq(1, eligible);
            Eq(1, display[0].SessionsSeen);
            Eq(120.0, display[0].ScoredSeconds);
            Eq(false, display[0].Worth);
        }

        private static void TestIrqDisplayVersionAndValidity()
        {
            const string boot = "1000000", topology = "status-test-topology";
            var strict = new List<IrqDriverVerdict>
            {
                new IrqDriverVerdict
                {
                    Driver = "status-test.sys", DriverVersion = "1.0", SessionsSeen = 3,
                    SessionsOverThreshold = 3, ScoredSeconds = 360, OverlapCpuMask = 1,
                    OverlapOver500 = 3600, OverlapWorstMaxUs = 900,
                    Worth = true, StructuralConflict = true, Collisions = 0.54
                }
            };
            var all = new List<IrqSessionRecord> { IrqDisplayRecord(120), IrqDisplayRecord(120) };
            IrqSessionRecord updated = IrqDisplayRecord(10);
            updated.Drivers[0].Driver = "STATUS-TEST.SYS";
            updated.Drivers[0].DriverVersion = "2.0";
            updated.Drivers[0].Dpc = 20;
            updated.Drivers[0].DpcMaxNs = 100000;
            updated.Drivers[0].Over500Us = 0;
            all.Add(updated);
            int displayed;
            List<IrqDriverVerdict> raw = IrqVerdict.AggregateForDisplay(all, strict,
                boot, topology, out displayed);
            Eq(3, displayed);
            Eq(1, raw.Count);
            Eq("2.0", raw[0].DriverVersion);
            Eq(100.0, raw[0].WorstMaxUs);
            Eq(120.0, raw[0].DpcPerMinute);
            Eq(0L, raw[0].TotalOver500);
            AssertIrqScoreEqual(new IrqDriverVerdict(), raw[0]);
            // 展示聚合不能反向修改由严格入口给出的结论对象。
            Eq(true, strict[0].Worth);
            Eq(3, strict[0].SessionsSeen);

            var guarded = new List<IrqSessionRecord> { IrqDisplayRecord(30) };
            for (int bad = 0; bad < 7; bad++)
            {
                IrqSessionRecord rec = IrqDisplayRecord(30);
                rec.Drivers[0].DpcMaxNs = 9000000;
                rec.Drivers[0].DriverVersion = "untrusted-version";
                if (bad == 0) rec.EventsLost = 1;
                if (bad == 1) rec.BootStamp = "1000010";
                if (bad == 2) rec.TopologyStamp = "different";
                if (bad == 3) rec.DurationSeconds = 0;
                if (bad == 4) rec.DurationSeconds = -1;
                if (bad == 5) rec.SystemMask = 0;
                if (bad == 6) rec.Drivers.Clear();
                guarded.Add(rec);
            }
            raw = IrqVerdict.AggregateForDisplay(guarded, null, boot, topology, out displayed);
            Eq(1, displayed);
            Eq("1.0", raw[0].DriverVersion);
            Eq(900.0, raw[0].WorstMaxUs);
            Eq(1200.0, raw[0].DpcPerMinute);
            AssertIrqScoreEqual(new IrqDriverVerdict(), raw[0]);

            IrqSessionRecord zero = IrqDisplayRecord(0);
            Eq(IrqSessionExclusion.NoDuration, zero.DisplayExclusion(boot, topology));
            Eq(IrqSessionExclusion.TooShort, zero.VerdictExclusion(boot, topology));
            raw = IrqVerdict.AggregateForDisplay(new List<IrqSessionRecord> { zero }, null,
                boot, topology, out displayed);
            Eq(0, displayed);
            Eq(0, raw.Count);

            IrqSessionRecord duplicate = IrqDisplayRecord(10);
            duplicate.Drivers.Add(duplicate.Drivers[0]);
            raw = IrqVerdict.AggregateForDisplay(new List<IrqSessionRecord> { duplicate }, null,
                boot, topology, out displayed);
            Eq(1, displayed);
            Eq(1200.0, raw[0].DpcPerMinute);
            Eq(100L, raw[0].TotalOver500);
        }
    }
}
