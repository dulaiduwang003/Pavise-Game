using System;
using System.IO;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {

        private static void BuildPersistent(AuditReport report, Facts facts)
        {
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.125"),
                Value = ((!facts.HagsKnown) ? Lang.T("env.graphics.unknown") : (facts.Hags ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close"))),
                Note = Lang.T("t.systemaudit.126"),
                Evidence = "机制明确",
                Warn = false
            });
            BuildMemoryCompression(report, facts.InstalledMemoryKnown, facts.InstalledMemoryKb, facts.MemoryCompressionKnown, facts.CompressionOn, facts.CombiningOn, MemCompressTweak.OwnsState);
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.132"),
                Value = (facts.MpoOff ? Lang.T("t.systemaudit.133") : Lang.T("t.systemaudit.134")),
                Note = (facts.MpoOff ? Lang.T("t.systemaudit.135") : Lang.T("t.systemaudit.136")),
                Evidence = "机制明确",
                Warn = facts.MpoOff
            });
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.43"),
                Value = (facts.Dvr ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close")),
                Note = (facts.Dvr ? Lang.T("t.systemaudit.137") : Lang.T("t.systemaudit.138")),
                Evidence = "机制明确",
                Warn = facts.Dvr
            });
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.139"),
                Value = facts.Plan,
                Note = Lang.T("t.systemaudit.140"),
                Evidence = "机制明确",
                Warn = false
            });
            QuantumAudit.Finding finding = QuantumAudit.Inspect();
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.144"),
                Value = finding.Value,
                Note = finding.Note,
                Evidence = "机制明确",
                Warn = finding.Warn,
                FixKey = (finding.OfferSetting ? "quantum" : null)
            });
            bool networkNeedsRepair = false;
            string message = "";
            try
            {
                networkNeedsRepair = NetTweak.NeedsRepair();
                message = NetTweak.Describe();
            }
            catch
            {
            }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.148"),
                Value = (networkNeedsRepair ? Lang.T("netthrottle.invalid") : Lang.T("t.systemaudit.134")),
                Note = (networkNeedsRepair ? Lang.T("t.systemaudit.149") : Lang.T("t.systemaudit.150")) + " " + message,
                Evidence = "机制明确",
                Warn = networkNeedsRepair,
                FixKey = "net"
            });
            bool fthNeedsRepair = false;
            string message2 = "";
            try
            {
                fthNeedsRepair = FthTweak.NeedsRepair();
                message2 = FthTweak.Describe();
            }
            catch
            {
            }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.151"),
                Value = (fthNeedsRepair ? Lang.T("t.systemaudit.152") : Lang.T("t.systemaudit.134")),
                Note = message2 + " " + Lang.T(fthNeedsRepair ? "t.systemaudit.153" : "t.systemaudit.154"),
                Evidence = "机制明确",
                Warn = fthNeedsRepair,
                FixKey = "fth"
            });
        }

        internal static void BuildMemoryCompression(AuditReport report, bool memoryKnown, ulong installedKb, bool stateKnown, bool compression, bool combining, bool owned)
        {
            report.MemoryCompressionCanEnable = stateKnown && !compression;
            string value = ((!stateKnown) ? Lang.T("t.systemaudithardware.3") : Lang.T(compression ? "audit.mem.healthy" : "audit.mem.todo"));
            string auditMemUnknownText = ((!stateKnown) ? Lang.T("audit.mem.unknown") : Lang.T(compression ? "audit.mem.keep" : "audit.mem.policy"));
            if (owned)
            {
                auditMemUnknownText = auditMemUnknownText + " " + Lang.T("audit.mem.owned");
            }
            if (memoryKnown)
            {
                auditMemUnknownText = Lang.F("audit.mem.capacity", ((double)installedKb / 1048576.0).ToString("0.#")) + " " + auditMemUnknownText;
            }
            if (stateKnown)
            {
                auditMemUnknownText = auditMemUnknownText + " " + Lang.F("audit.mem.states", Lang.T(compression ? "audit.mem.on" : "notes.close"), Lang.T(combining ? "audit.mem.on" : "notes.close"));
            }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("audit.mem.title"),
                Value = value,
                Note = auditMemUnknownText,
                Warn = report.MemoryCompressionCanEnable,
                FixKey = "memcompress",
                Evidence = (stateKnown ? "本机实测" : "未验证")
            });
        }

        internal static void BuildGpuInterrupt(AuditReport report)
        {
            GpuIrqResult gpuIrq = report.GpuIrq;
            if (gpuIrq == null)
            {
                return;
            }
            report.Machine.RemoveAll((AuditRow row) => row.FixKey == "gpuirq" || row.FixKey == "gpuirqdriver");
            bool warn = false;
            string value;
            string auditIrqProposalText;
            switch (gpuIrq.Outcome)
            {
            case GpuIrqOutcome.NeedsPlacement:
                value = Lang.T("audit.irq.overlap");
                auditIrqProposalText = Lang.F("audit.irq.proposal", IrqRelocate.MaskText(gpuIrq.Target));
                warn = true;
                break;
            case GpuIrqOutcome.NeedsVerification:
                value = Lang.T("irqcheck.state.verify");
                auditIrqProposalText = Lang.T("irqcheck.hint.verify");
                warn = true;
                break;
            case GpuIrqOutcome.AwaitingReboot:
                value = Lang.T("irqcheck.state.pending");
                auditIrqProposalText = Lang.T("irqcheck.hint.pending");
                warn = true;
                break;
            case GpuIrqOutcome.Clear:
                value = Lang.T("irqcheck.state.clear");
                auditIrqProposalText = Lang.T("irqcheck.hint.clear");
                break;
            case GpuIrqOutcome.NoRegion:
                value = Lang.T("irqcheck.state.noregion");
                auditIrqProposalText = Lang.T("irqcheck.out.noregion");
                break;
            case GpuIrqOutcome.NoTarget:
                value = Lang.T("irqcheck.reason.notarget");
                auditIrqProposalText = Lang.T("irqcheck.reason.notarget");
                break;
            case GpuIrqOutcome.Skipped:
                value = Lang.T("audit.irq.skipped");
                auditIrqProposalText = gpuIrq.Detail;
                break;
            case GpuIrqOutcome.NoEvidence:
                value = Lang.T("audit.irq.insufficient");
                auditIrqProposalText = Lang.F("audit.irq.check.hint", (report.MeasureWindowMs > 0) ? ((int)Math.Ceiling((double)report.MeasureWindowMs / 1000.0)) : 120);
                break;
            case GpuIrqOutcome.Cancelled:
                value = Lang.T("audit.irq.skipped");
                auditIrqProposalText = Lang.T("irqcheck.out.cancelled");
                break;
            default:
                value = Lang.T("audit.irq.unavailable");
                auditIrqProposalText = gpuIrq.Detail;
                break;
            }
            if (gpuIrq.Gpu != null)
            {
                auditIrqProposalText = auditIrqProposalText + " " + Lang.F("audit.irq.sample", gpuIrq.Gpu.Name, IrqRelocate.MaskText(gpuIrq.Gpu.Seen), gpuIrq.Gpu.Events, gpuIrq.Gpu.P99Us.ToString("F0"));
            }
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("irqcheck.gpu.title"),
                Value = value,
                Note = auditIrqProposalText,
                Warn = warn,
                FixKey = "gpuirq",
                Evidence = ((gpuIrq.Gpu != null && gpuIrq.Gpu.Events >= 300 && gpuIrq.Gpu.Seen != 0) ? "本机实测" : "未验证")
            });
            foreach (SlowDriver item in gpuIrq.Slow)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("irqcheck.slow.title") + " · " + item.Driver,
                    Value = Lang.F("irqcheck.slow.row", item.MaxUs.ToString("F0"), item.P99Us.ToString("F0")),
                    Note = item.Device + " " + Lang.T("irqcheck.slow.hint"),
                    Warn = true,
                    Evidence = "本机实测",
                    FixKey = "gpuirqdriver"
                });
            }
        }

        private static void BuildBypassIo(AuditReport report, Facts facts)
        {
            if (facts.BypassIo == null)
            {
                return;
            }
            foreach (BypassIoVerdict item in facts.BypassIo)
            {
                string message = "";
                try
                {
                    message = Path.GetPathRoot(item.Path) ?? "";
                }
                catch
                {
                }
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.bypassio.1") + " " + message.TrimEnd('\\'),
                    Value = (item.Enabled ? Lang.T("t.bypassio.2") : Lang.F("t.bypassio.3", (item.Blocker.Length > 0) ? item.Blocker : "?")),
                    Note = (item.Enabled ? Lang.T("t.bypassio.4") : (Lang.T("t.bypassio.5") + ((item.Reason.Length > 0) ? (" " + item.Reason) : ""))),
                    Evidence = "机制明确",
                    Warn = !item.Enabled
                });
            }
        }

        private static void BuildCapability(AuditReport report, Facts facts)
        {
            if (facts.Gpus.Length > 0)
            {
                report.Capability.Add(new AuditRow
                {
                    Name = Lang.T("cfg.group.gpu"),
                    Value = GpuInventory.Describe(),
                    Note = (facts.IntegratedOnly ? Lang.T("t.systemaudit.3") : Lang.T("t.systemaudit.4")),
                    Evidence = "本机实测",
                    Warn = false
                });
            }
            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.5"),
                Value = (facts.Nv ? Lang.T("t.systemaudit.6") : Lang.T("t.systemaudit.7")),
                Note = (facts.Nv ? Lang.T("t.systemaudit.8") : (facts.NvHardware ? Lang.T("t.systemaudit.9") : Lang.T("t.systemaudit.10"))),
                Evidence = "本机实测",
                Warn = false
            });
            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.16"),
                Value = (facts.Partition ? Lang.T("t.systemaudit.6") : Lang.T("t.systemaudit.7")),
                Note = (facts.Partition ? Lang.T("t.systemaudit.17") : Lang.T("t.systemaudit.18")),
                Evidence = "本机实测",
                Warn = false
            });
            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.19"),
                Value = ((!facts.Eco) ? Lang.T("t.systemaudit.20") : (facts.EcoFull ? Lang.T("t.systemaudit.21") : Lang.T("t.systemaudit.22"))),
                Note = ((!facts.Eco) ? Lang.T("t.systemaudit.23") : (facts.EcoFull ? Lang.T("t.systemaudit.24") : Lang.T("t.systemaudit.25"))),
                Evidence = "本机实测",
                Warn = !facts.Eco
            });
            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.26"),
                Value = WindowsText(),
                Note = Lang.T("t.systemaudit.27"),
                Evidence = "本机实测",
                Warn = false
            });
        }

        private static void BuildVerdicts(AuditReport report, Facts facts, int hzCur, int hzBest)
        {
            if (hzCur > 0 && hzBest > 0 && !RefreshRateIsBest(hzCur, hzBest))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.1"),
                    Value = Lang.T("t.systemauditverdicts.2"),
                    Note = Lang.T("t.systemauditverdicts.3") + hzCur + Lang.T("t.systemauditverdicts.4") + hzBest + Lang.T("t.systemauditverdicts.5") + hzBest + Lang.T("t.systemauditverdicts.6"),
                    Evidence = "机制明确",
                    Warn = true
                });
            }
            if (facts.Access != null && facts.Access.FilterKeysSwallowing)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.7"),
                    Value = Lang.T("t.systemauditverdicts.2"),
                    Note = Lang.T("t.systemauditverdicts.8") + facts.Access.DelayBeforeAcceptanceMs + Lang.T("t.systemauditverdicts.9"),
                    Evidence = "机制明确",
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
                    Evidence = "机制明确",
                    Warn = false
                });
            }
            bool requiresBluetooth = false;
            try
            {
                requiresBluetooth = InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, true) || InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, false);
            }
            catch
            {
            }
            if (requiresBluetooth)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.13"),
                    Value = Lang.T("t.systemauditverdicts.14"),
                    Note = Lang.T("t.systemauditverdicts.15"),
                    Evidence = "台架实测",
                    Warn = true
                });
            }
            if (!facts.SuppressOn)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("cfg.group.bg"),
                    Value = Lang.T("t.systemauditverdicts.24"),
                    Note = Lang.T("t.systemauditverdicts.25"),
                    Evidence = "台架实测",
                    Warn = false
                });
            }
            if (facts.Dvr)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.43"),
                    Value = Lang.T("t.systemauditverdicts.11"),
                    Note = Lang.T("t.systemauditverdicts.44"),
                    Evidence = "机制明确",
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
                    Evidence = "机制明确",
                    Warn = true
                });
            }
            if (facts.PageOk && PageFileLooksDisabled(facts.PageFileGb))
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.53"),
                    Value = Lang.T("t.systemauditverdicts.54"),
                    Note = Lang.T("t.systemauditverdicts.55"),
                    Evidence = "机制明确",
                    Warn = true
                });
            }
            bool unparked;
            if (!CpuTopology.AsymCache && PowerPlan.TryCurrentUnparked(out unparked) && !unparked)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.legacypurge.3"),
                    Value = Lang.T("t.systemauditverdicts.56"),
                    Note = Lang.T("t.systemauditverdicts.57"),
                    Evidence = "机制明确",
                    Warn = false
                });
            }
            if (report.Verdicts.Count == 0)
            {
                report.Verdicts.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.60"),
                    Value = Lang.T("t.systemauditverdicts.62"),
                    Note = Lang.T("t.systemauditverdicts.61"),
                    Evidence = "本机实测",
                    Warn = false
                });
            }
        }

    }
}
