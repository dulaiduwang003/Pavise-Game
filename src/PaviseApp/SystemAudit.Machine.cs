using System;
using System.Collections.Generic;

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
            int processorCount = Environment.ProcessorCount;
            int machineCpuValue = 0;
            try
            {
                machineCpuValue = CpuTopology.PhysicalCoreCount;
            }
            catch
            {
            }
            string tSystemauditText = (CpuTopology.Hybrid ? Lang.T("t.systemaudit.28") : (CpuTopology.AsymCache ? Lang.T("t.systemaudit.29") : ((CpuTopology.PartitionTag == "symmetric-ccd") ? Lang.T("t.systemaudit.30") : ((CpuTopology.PartitionTag == "pool-iso-core0") ? Lang.T("t.systemaudit.31") : Lang.T("t.systemaudit.32")))));
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.33"),
                Value = machineCpuValue + Lang.T("t.systemaudit.34") + processorCount + Lang.T("t.systemaudit.35"),
                Note = tSystemauditText + (CpuTopology.HasSafeBackgroundPartition() ? (Lang.T("t.systemaudit.36") + CpuTopology.StrictBoostMask.ToString("X")) : Lang.T("t.systemaudit.37")),
                Evidence = "本机实测",
                Warn = false
            });
            if (report.MeasureOk)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.38"),
                    Value = PercentText(cpuBusy),
                    Note = Lang.T("t.systemaudit.39") + ((report.MeasureWindowMs >= 1000) ? (report.MeasureWindowMs / 1000 + Lang.T("t.systemaudit.40")) : (report.MeasureWindowMs + Lang.T("t.systemaudit.41"))) + Lang.T("t.systemaudit.42"),
                    Evidence = "本机实测",
                    Warn = false
                });
            }
        }

        private static void BuildMachineDisplay(AuditReport report, int hzCur, int hzBest)
        {
            bool refreshRatesKnown = hzCur > 0 && hzBest > 0;
            bool refreshRateIsBestSucceeded = RefreshRateIsBest(hzCur, hzBest);
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.1"),
                Value = (refreshRatesKnown ? (hzCur + " Hz" + (refreshRateIsBestSucceeded ? Lang.T("t.systemaudit.54") : (Lang.T("t.systemaudit.55") + hzBest + " Hz"))) : Lang.T("t.systemaudithardware.3")),
                Note = ((!refreshRatesKnown) ? Lang.T("t.systemaudit.56") : (refreshRateIsBestSucceeded ? Lang.T("t.systemaudit.57") : (Lang.T("t.systemaudit.58") + hzCur + Lang.T("t.systemaudit.59") + hzBest + Lang.T("t.systemaudit.60") + hzBest + Lang.T("t.systemaudit.61")))),
                Evidence = "本机实测",
                Warn = (!refreshRateIsBestSucceeded || !refreshRatesKnown)
            });
        }

        private static void BuildMachineGpu(AuditReport report)
        {
            string instantTextResult = null;
            try
            {
                instantTextResult = GpuThrottleProbe.InstantText();
            }
            catch
            {
            }
            if (instantTextResult != null)
            {
                bool throttlingReported = instantTextResult != Lang.T("t.gputhrottleprobe.6");
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.62"),
                    Value = instantTextResult,
                    Note = (throttlingReported ? Lang.T("t.systemaudit.63") : Lang.T("t.systemaudit.64")),
                    Evidence = "本机实测",
                    Warn = throttlingReported
                });
            }
        }

        private static void BuildMachinePower(AuditReport report)
        {
            bool on;
            if (TryEnergySaver(out on))
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.65"),
                    Value = (on ? Lang.T("t.systemaudit.66") : Lang.T("t.systemaudit.67")),
                    Note = (on ? Lang.T("t.systemaudit.68") : Lang.T("t.systemaudit.69")),
                    Evidence = "本机实测",
                    Warn = on
                });
            }
            bool presenceQosDisabled = false;
            try
            {
                presenceQosDisabled = PresenceQos.CurrentlyDisabled();
            }
            catch
            {
            }
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.legacypurge.6"),
                Value = (presenceQosDisabled ? Lang.T("col.ux.closed") : Lang.T("t.systemaudit.70")),
                Note = (presenceQosDisabled ? Lang.T("t.systemaudit.71") : Lang.T("t.systemaudit.72")),
                Evidence = "本机实测",
                Warn = false
            });
        }

        private static void BuildMachineRebar(AuditReport report)
        {
            bool enabled = false;
            ulong windowBytes = 0uL;
            string gpuName = null;
            bool nvidia = false;
            bool detected = false;
            try
            {
                detected = RebarProbe.TryDetect(out enabled, out windowBytes, out gpuName, out nvidia);
            }
            catch
            {
            }
            if (detected)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.73"),
                    Value = (enabled ? Lang.T("v16.device.hags.on") : Lang.T("gs.noff")) + Lang.T("t.systemaudit.74") + RebarProbe.WindowText(windowBytes),
                    Note = (string.IsNullOrEmpty(gpuName) ? "" : (gpuName + " ")) + ((!nvidia) ? (enabled ? Lang.T("t.systemaudit.77") : Lang.T("t.systemaudit.78")) : (enabled ? Lang.T("t.systemaudit.75") : Lang.T("t.systemaudit.76"))),
                    Evidence = "本机实测",
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
                    Value = facts.TotalGb.ToString("F1") + Lang.T("t.systemaudit.80") + PercentText(facts.UsedRatio) + Lang.T("t.systemaudit.55") + facts.AvailGb.ToString("F1") + " GB",
                    Note = ((facts.UsedRatio >= 0.85) ? Lang.T("t.systemaudit.81") : Lang.T("t.systemaudit.82")),
                    Evidence = "本机实测",
                    Warn = (facts.UsedRatio >= 0.85)
                });
            }
            if (facts.PageOk)
            {
                bool pageFileLooksDisabledSucceeded = PageFileLooksDisabled(facts.PageFileGb);
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.53"),
                    Value = (pageFileLooksDisabledSucceeded ? Lang.T("t.systemaudit.83") : (facts.PageFileGb.ToString("F1") + " GB")),
                    Note = (pageFileLooksDisabledSucceeded ? Lang.T("t.systemaudit.84") : Lang.T("t.systemaudit.85")),
                    Evidence = "本机实测",
                    Warn = pageFileLooksDisabledSucceeded
                });
            }
        }

        private static void BuildMachineNetwork(AuditReport report, Facts facts)
        {
            if (facts.Link == null || facts.Link == "none")
            {
                return;
            }
            bool wireless = facts.Link == "wifi";
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.86"),
                Value = ((facts.Link == "wired") ? Lang.T("t.systemaudit.87") : (wireless ? Lang.T("t.systemaudit.88") : Lang.T("t.systemaudit.89"))),
                Note = (wireless ? Lang.T("t.systemaudit.90") : ((facts.Link == "wired") ? Lang.T("t.systemaudit.91") : Lang.T("t.systemaudit.92"))),
                Evidence = "本机实测",
                Warn = wireless
            });
            if (facts.Link == "both" || LinkMetricTweak.RepairedByPavise)
            {
                bool metricNeedsRepair = false;
                string note = "";
                try
                {
                    metricNeedsRepair = LinkMetricTweak.NeedsRepair();
                    note = LinkMetricTweak.Describe();
                }
                catch
                {
                }
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.linkmetric.name"),
                    Value = (metricNeedsRepair ? Lang.T("t.linkmetric.bad") : Lang.T("t.linkmetric.good")),
                    Note = note,
                    Evidence = "机制明确",
                    Warn = metricNeedsRepair,
                    FixKey = "linkmetric"
                });
            }
        }

        private static void BuildMachineBattery(AuditReport report)
        {
            try
            {
                SystemPowerStatus status;
                if (GetSystemPowerStatus(out status) && status.BatteryFlag != 128 && status.BatteryFlag != byte.MaxValue && status.AcLineStatus != byte.MaxValue)
                {
                    bool onAcPower = status.AcLineStatus == 1;
                    report.Machine.Add(new AuditRow
                    {
                        Name = Lang.T("t.systemaudit.93"),
                        Value = (onAcPower ? Lang.T("t.systemaudit.94") : Lang.T("t.systemaudit.95")),
                        Note = (onAcPower ? Lang.T("t.systemaudit.96") : Lang.T("t.systemaudit.97")),
                        Evidence = "机制明确",
                        Warn = !onAcPower
                    });
                }
            }
            catch
            {
            }
        }

        private static void BuildMachineTopConsumers(AuditReport report)
        {
            int minLimit = Math.Max(600, Math.Min(3000, report.MeasureWindowMs / 10));
            List<LoadEntry> list = TopConsumers(minLimit, 3);
            if (list.Count <= 0)
            {
                return;
            }
            List<string> list2 = new List<string>();
            foreach (LoadEntry item in list)
            {
                list2.Add(item.Name + " " + PercentText(item.Ratio));
            }
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.98"),
                Value = string.Join("   ", list2.ToArray()),
                Note = Lang.T("t.systemaudit.99") + ((double)minLimit / 1000.0).ToString("0.#") + Lang.T("t.systemaudit.100"),
                Evidence = "本机实测",
                Warn = false
            });
        }

    }
}
