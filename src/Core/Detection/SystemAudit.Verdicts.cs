// @author bdth 2074055628@qq.com
// 文件用途 系统体检结论分部 汇总事实生成带依据的处理建议
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // 结论区把前面各段采到的事实翻成给用户的建议
    //   这里不做任何测量也不写系统 只读 Facts 和已生成的行
    internal static partial class SystemAudit
    {
        private static void BuildVerdicts(AuditReport report, Facts facts, int hzCur, int hzBest)
        {
            if (hzCur > 0 && hzBest > 0 && !RefreshRateIsBest(hzCur, hzBest))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.1"),
                    Value = Lang.T("t.systemauditverdicts.2"),
                    Note = Lang.T("t.systemauditverdicts.3") + hzCur + Lang.T("t.systemauditverdicts.4") + hzBest + Lang.T("t.systemauditverdicts.5")
                        + hzBest + Lang.T("t.systemauditverdicts.6"),
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            if (facts.Access != null && facts.Access.FilterKeysSwallowing)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.7"),
                    Value = Lang.T("t.systemauditverdicts.2"),
                    Note = Lang.T("t.systemauditverdicts.8") + facts.Access.DelayBeforeAcceptanceMs
                        + Lang.T("t.systemauditverdicts.9"),
                    Evidence = EvMechanism,
                    Warn = true
                });
            }
            else if (facts.Access != null && facts.Access.AnyNeedsFix)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.10"),
                    Value = Lang.T("t.systemauditverdicts.11"),
                    Note = Lang.T("t.systemauditverdicts.12"),
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            bool btInput = false;
            try
            {
                btInput = InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, true)
                    || InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, false);
            }
            catch { }
            if (btInput)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.13"),
                    Value = Lang.T("t.systemauditverdicts.14"),
                    Note = Lang.T("t.systemauditverdicts.15"),
                    Evidence = EvMeasuredBench,
                    Warn = true
                });
            }

            if (facts.HidPowerSave)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.legacypurge.15"),
                    Value = Lang.T("t.systemauditverdicts.11"),
                    Note = Lang.T("t.systemauditverdicts.16"),
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            if (facts.QueueTampered)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.17"),
                    Value = Lang.T("t.systemauditverdicts.18"),
                    Note = Lang.T("t.systemauditverdicts.19"),
                    Evidence = EvMechanism,
                    Warn = true,
                    FixKey = "inputq"
                });
            }

            // 结论区只收"有事要说"的行 状态陈述归机器/持久区 通用科普不属于体检
            //   后台压制建议只在关闭时给 开着时"已开启无需处理"是占位废话
            if (!facts.SuppressOn)
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("cfg.group.bg"),
                    Value = Lang.T("t.systemauditverdicts.24"),
                    Note = Lang.T("t.systemauditverdicts.25"),
                    Evidence = EvMeasuredBench,
                    Warn = false
                });

            if (facts.Dvr)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.43"),
                    Value = Lang.T("t.systemauditverdicts.11"),
                    Note = Lang.T("t.systemauditverdicts.44"),
                    Evidence = EvMechanism,
                    Warn = false
                });
            }

            if (facts.MemOk && facts.UsedRatio >= 0.85)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.45"),
                    Value = Lang.T("t.systemauditverdicts.35"),
                    Note = Lang.T("t.systemauditverdicts.46") + facts.AvailGb.ToString("F1") + Lang.T("t.systemauditverdicts.47"),
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            if (facts.Vbs.WmiOk && facts.Vbs.VbsRunning)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = "VBS",
                    Value = Lang.T("t.systemauditverdicts.48"),
                    Note = Lang.T("t.systemauditverdicts.49"),
                    Evidence = EvMechanism,
                    Warn = false
                });
            }


            if (facts.PageOk && PageFileLooksDisabled(facts.PageFileGb))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.53"),
                    Value = Lang.T("t.systemauditverdicts.54"),
                    Note = Lang.T("t.systemauditverdicts.55"),
                    Evidence = EvMechanism,
                    Warn = true
                });
            }

            bool coreUnparked;
            if (!CpuTopology.AsymCache
                && PowerPlan.TryCurrentUnparked(out coreUnparked) && !coreUnparked)
            {
                bool partitionActive = GameMode.ShouldUseCorePartition(facts.PartitionOn, facts.Partition);
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.legacypurge.3"),
                    Value = Lang.T(partitionActive
                        ? "t.systemauditverdicts.58" : "t.systemauditverdicts.56"),
                    Note = Lang.T("t.systemauditverdicts.57")
                        + (partitionActive ? Lang.T("t.systemauditverdicts.59") : ""),
                    Evidence = EvMechanism,
                    Warn = partitionActive
                });
            }

            // 全绿时明说 空区块会被当成没检查
            if (report.Verdicts.Count == 0)
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.60"),
                    Value = Lang.T("t.systemauditverdicts.62"),
                    Note = Lang.T("t.systemauditverdicts.61"),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
        }
    }
}
