// @author bdth 2074055628@qq.com
// 文件用途 本机项结论 CPU 显示 显卡 电源 内存 网络与电池
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        private static void BuildMachine(AuditReport report, Facts facts, double cpuBusy, int hzCur, int hzBest)
        {
            BuildMachineCpu(report, cpuBusy);
            BuildMachineDisplay(report, hzCur, hzBest);
            BuildMachineGpu(report);
            BuildMachinePower(report);
            BuildMachineRebar(report);
            BuildMachineMemory(report, facts);
            BuildMachineNetwork(report, facts);
            BuildMachineBattery(report);
            BuildMachineTopConsumers(report);
        }

        private static void BuildMachineCpu(AuditReport report, double cpuBusy)
        {
            int logical = Environment.ProcessorCount;
            int physical = 0;
            try { physical = CpuTopology.PhysicalCoreCount; } catch { }
            string arch = CpuTopology.Hybrid ? Lang.T("t.systemaudit.28")
                : CpuTopology.AsymCache ? Lang.T("t.systemaudit.29")
                : CpuTopology.PartitionTag == "symmetric-ccd" ? Lang.T("t.systemaudit.30")
                : CpuTopology.PartitionTag == "pool-iso-core0" ? Lang.T("t.systemaudit.31")
                : Lang.T("t.systemaudit.32");
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.33"),
                Value = physical + Lang.T("t.systemaudit.34") + logical + Lang.T("t.systemaudit.35"),
                Note = arch + (CpuTopology.HasSafeBackgroundPartition()
                    ? Lang.T("t.systemaudit.36") + CpuTopology.StrictBoostMask.ToString("X") : Lang.T("t.systemaudit.37")),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            if (report.MeasureOk)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.38"),
                    Value = PercentText(cpuBusy),
                    Note = Lang.T("t.systemaudit.39") + (report.MeasureWindowMs >= 1000
                        ? (report.MeasureWindowMs / 1000) + Lang.T("t.systemaudit.40") : report.MeasureWindowMs + Lang.T("t.systemaudit.41")) + Lang.T("t.systemaudit.42"),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }
        }

        private static void BuildMachineDisplay(AuditReport report, int hzCur, int hzBest)
        {
            bool hzRead = hzCur > 0 && hzBest > 0;
            bool hzOk = RefreshRateIsBest(hzCur, hzBest);
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.1"),
                Value = hzRead
                    ? hzCur + " Hz" + (hzOk ? Lang.T("t.systemaudit.54") : Lang.T("t.systemaudit.55") + hzBest + " Hz")
                    : Lang.T("t.systemaudithardware.3"),
                Note = !hzRead
                    ? Lang.T("t.systemaudit.56")
                    : (hzOk
                        ? Lang.T("t.systemaudit.57")
                        : Lang.T("t.systemaudit.58") + hzCur + Lang.T("t.systemaudit.59") + hzBest
                            + Lang.T("t.systemaudit.60")
                            + hzBest + Lang.T("t.systemaudit.61")),
                Evidence = EvMeasuredLocal,
                Warn = !hzOk || !hzRead
            });
        }

        private static void BuildMachineGpu(AuditReport report)
        {
            string throttleNow = null;
            try { throttleNow = GpuThrottleProbe.InstantText(); } catch { }
            if (throttleNow != null)
            {
                bool throttled = throttleNow != Lang.T("t.gputhrottleprobe.6");
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.62"),
                    Value = throttleNow,
                    Note = throttled
                        ? Lang.T("t.systemaudit.63")
                        : Lang.T("t.systemaudit.64"),
                    Evidence = EvMeasuredLocal,
                    Warn = throttled
                });
            }
        }

        private static void BuildMachinePower(AuditReport report)
        {
            bool saverOn;
            if (TryEnergySaver(out saverOn))
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.65"),
                    Value = saverOn ? Lang.T("t.systemaudit.66") : Lang.T("t.systemaudit.67"),
                    Note = saverOn
                        ? Lang.T("t.systemaudit.68")
                        : Lang.T("t.systemaudit.69"),
                    Evidence = EvMeasuredLocal,
                    Warn = saverOn
                });
            }

            bool presenceOff = false;
            try { presenceOff = PresenceQos.CurrentlyDisabled(); } catch { }
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.legacypurge.6"),
                Value = presenceOff ? Lang.T("col.ux.closed") : Lang.T("t.systemaudit.70"),
                Note = presenceOff
                    ? Lang.T("t.systemaudit.71")
                    : Lang.T("t.systemaudit.72"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });
        }

        private static void BuildMachineRebar(AuditReport report)
        {
            bool rebarOn = false;
            ulong rebarWindow = 0;
            string rebarGpu = null;
            bool rebarNvidia = false;
            bool rebarRead = false;
            try { rebarRead = RebarProbe.TryDetect(out rebarOn, out rebarWindow, out rebarGpu, out rebarNvidia); } catch { }
            if (rebarRead)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.73"),
                    Value = (rebarOn ? Lang.T("v16.device.hags.on") : Lang.T("gs.noff")) + Lang.T("t.systemaudit.74") + RebarProbe.WindowText(rebarWindow),
                    Note = (string.IsNullOrEmpty(rebarGpu) ? "" : rebarGpu + " ")
                        + (rebarNvidia
                            ? (rebarOn ? Lang.T("t.systemaudit.75") : Lang.T("t.systemaudit.76"))
                            : (rebarOn ? Lang.T("t.systemaudit.77") : Lang.T("t.systemaudit.78"))),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }
        }

        private static void BuildMachineMemory(AuditReport report, Facts facts)
        {
            if (facts.MemOk)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.79"),
                    Value = facts.TotalGb.ToString("F1") + Lang.T("t.systemaudit.80") + PercentText(facts.UsedRatio)
                        + Lang.T("t.systemaudit.55") + facts.AvailGb.ToString("F1") + " GB",
                    Note = facts.UsedRatio >= 0.85
                        ? Lang.T("t.systemaudit.81")
                        : Lang.T("t.systemaudit.82"),
                    Evidence = EvMeasuredLocal,
                    Warn = facts.UsedRatio >= 0.85
                });
            }

            if (facts.PageOk)
            {
                bool pageDisabled = PageFileLooksDisabled(facts.PageFileGb);
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.53"),
                    Value = pageDisabled ? Lang.T("t.systemaudit.83") : facts.PageFileGb.ToString("F1") + " GB",
                    Note = pageDisabled
                        ? Lang.T("t.systemaudit.84")
                        : Lang.T("t.systemaudit.85"),
                    Evidence = EvMeasuredLocal,
                    Warn = pageDisabled
                });
            }
        }

        private static void BuildMachineNetwork(AuditReport report, Facts facts)
        {
            if (facts.Link != null && facts.Link != "none")
            {
                bool wifiOnly = facts.Link == "wifi";
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.86"),
                    Value = facts.Link == "wired" ? Lang.T("t.systemaudit.87") : wifiOnly ? Lang.T("t.systemaudit.88") : Lang.T("t.systemaudit.89"),
                    Note = wifiOnly
                        ? Lang.T("t.systemaudit.90")
                        : facts.Link == "wired" ? Lang.T("t.systemaudit.91")
                        : Lang.T("t.systemaudit.92"),
                    Evidence = EvMeasuredLocal,
                    Warn = wifiOnly
                });
                // 双链路时默认路由可能走无线 有线跃点压低是唯一的修法 修过的也在这里看得到
                if (facts.Link == "both" || LinkMetricTweak.RepairedByPavise)
                {
                    bool metricBad = false;
                    string metricState = "";
                    try { metricBad = LinkMetricTweak.NeedsRepair(); metricState = LinkMetricTweak.Describe(); }
                    catch { }
                    report.Machine.Add(new AuditRow
                    {
                        Name = Lang.T("t.linkmetric.name"),
                        Value = metricBad ? Lang.T("t.linkmetric.bad") : Lang.T("t.linkmetric.good"),
                        Note = metricState,
                        Evidence = EvMechanism,
                        Warn = metricBad,
                        FixKey = "linkmetric"
                    });
                }
            }
        }

        private static void BuildMachineBattery(AuditReport report)
        {
            try
            {
                SystemPowerStatus power;
                if (GetSystemPowerStatus(out power) && power.BatteryFlag != 128
                    && power.BatteryFlag != 255 && power.AcLineStatus != 255)
                {
                    bool onAc = power.AcLineStatus == 1;
                    report.Machine.Add(new AuditRow
                    {
                        Name = Lang.T("t.systemaudit.93"),
                        Value = onAc ? Lang.T("t.systemaudit.94") : Lang.T("t.systemaudit.95"),
                        Note = onAc ? Lang.T("t.systemaudit.96")
                            : Lang.T("t.systemaudit.97"),
                        Evidence = EvMechanism,
                        Warn = !onAc
                    });
                }
            }
            catch { }
        }

        private static void BuildMachineTopConsumers(AuditReport report)
        {
            int topWindow = Math.Max(600, Math.Min(3000, report.MeasureWindowMs / 10));
            var top = TopConsumers(topWindow, 3);
            if (top.Count > 0)
            {
                var parts = new List<string>();
                foreach (LoadEntry e in top) parts.Add(e.Name + " " + PercentText(e.Ratio));
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.98"),
                    Value = string.Join("   ", parts.ToArray()),
                    Note = Lang.T("t.systemaudit.99") + (topWindow / 1000.0).ToString("0.#")
                        + Lang.T("t.systemaudit.100"),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }
        }
    }
}
