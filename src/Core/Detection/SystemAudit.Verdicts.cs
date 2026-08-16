// @author bdth 2074055628@qq.com
// 文件用途 系统体检结论分部 汇总事实生成带依据的处理建议

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        private static void BuildVerdicts(AuditReport report, Facts facts, double worstIrq, int hzCur, int hzBest,
            InterruptAttributionResult culprits)
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

            report.Verdicts.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.20"),
                Value = Lang.T("t.systemauditverdicts.21"),
                Note = Lang.T("t.systemauditverdicts.22"),
                Evidence = EvMeasuredBench,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = Lang.T("cfg.group.bg"),
                Value = facts.SuppressOn ? Lang.T("t.systemauditverdicts.23") : Lang.T("t.systemauditverdicts.24"),
                Note = Lang.T("t.systemauditverdicts.25"),
                Evidence = EvMeasuredBench,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.26"),
                Value = facts.GameMode ? Lang.T("t.systemauditverdicts.23") : Lang.T("t.systemauditverdicts.24"),
                Note = Lang.T("t.systemauditverdicts.27"),
                Evidence = EvMechanism,
                Warn = false
            });

            report.Verdicts.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.28"),
                Value = facts.Nv ? Lang.T("t.systemauditverdicts.29") : Lang.T("t.systemauditverdicts.30"),
                Note = facts.Nv ? Lang.T("t.systemauditverdicts.31")
                    : facts.IntegratedOnly
                        ? Lang.T("t.systemauditverdicts.32")
                        : Lang.T("t.systemauditverdicts.33"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            if (report.MeasureOk)
            {
                int tier = InterruptTier(worstIrq);
                string culprit = culprits != null && culprits.Ok && culprits.TopDpc != null
                    ? culprits.TopDpc : null;
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.34"),
                    Value = tier == 2 ? Lang.T("t.systemauditverdicts.35") : Lang.T("t.systemauditverdicts.36"),
                    Note = tier == 2
                        ? (culprit != null
                            ? Lang.T("t.systemauditverdicts.37") + PercentText(worstIrq) + Lang.T("t.systemauditverdicts.38") + culprit
                                + Lang.T("t.systemauditverdicts.39")
                            : Lang.T("t.systemauditverdicts.37") + PercentText(worstIrq) + Lang.T("t.systemauditverdicts.40"))
                        : Lang.T("t.systemauditverdicts.41") + PercentText(worstIrq) + Lang.T("t.systemauditverdicts.42"),
                    Evidence = EvMeasuredLocal,
                    Warn = tier == 2
                });
            }

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

            if (facts.ClockStale != null && facts.ClockStale.Count > 0)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.50"),
                    Value = Lang.T("t.systemauditverdicts.51"),
                    Note = Lang.T("t.systemauditverdicts.52"),
                    Evidence = EvMechanism,
                    Warn = true,
                    FixKey = "clock"
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

            // X3D 双缓存平台停泊是调度机制的一部分 对局中也不解除 不作为提示项
            bool coreUnparked;
            if (!CpuTopology.AsymCache
                && PowerPlan.TryCurrentUnparked(out coreUnparked) && !coreUnparked)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.legacypurge.3"),
                    Value = Lang.T("t.systemauditverdicts.56"),
                    Note = Lang.T("t.systemauditverdicts.57"),
                    Evidence = EvMechanism,
                    Warn = false
                });
            }
        }
    }
}
