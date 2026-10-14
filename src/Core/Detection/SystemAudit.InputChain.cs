// 作者：bdth 2074055628@qq.com
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        internal static string InputSummary(List<InputDevice> devices, bool mouse)
        {
            if (devices == null) return null;
            bool hasNonPs2 = false;
            foreach (InputDevice d in devices)
                if (d.IsMouse == mouse && d.Transport != InputTransport.Virtual
                    && d.Transport != InputTransport.Ps2) hasNonPs2 = true;
            var seen = new List<string>();
            foreach (InputDevice d in devices)
            {
                if (d.IsMouse != mouse) continue;
                if (d.Transport == InputTransport.Virtual) continue;
                if (d.Transport == InputTransport.Ps2 && hasNonPs2) continue;
                string text = InputChainProbe.TransportText(d.Transport);
                if (!seen.Contains(text)) seen.Add(text);
            }
            return seen.Count == 0 ? null : string.Join(Lang.T("t.systemaudit.101"), seen.ToArray());
        }

        private static void BuildInputChain(AuditReport report, Facts facts)
        {
            bool bt = false, btOnly = false;
            try
            {
                bt = InputChainProbe.AnyBluetooth(facts.Inputs);
                btOnly = InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, true)
                    || InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, false);
            }
            catch { }
            string mice = InputSummary(facts.Inputs, true);
            string keys = InputSummary(facts.Inputs, false);
            if (mice != null || keys != null)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("audit.input.devices"),
                    Value = (mice == null ? "" : Lang.T("t.systemaudit.102") + mice) + (mice != null && keys != null ? "   " : "")
                        + (keys == null ? "" : Lang.T("t.systemaudit.103") + keys),
                    Note = btOnly
                        ? Lang.T("t.systemaudit.104")
                        : bt
                            ? Lang.T("t.systemaudit.105")
                            : Lang.T("t.systemaudit.106"),
                    Evidence = bt ? EvMeasuredBench : EvMeasuredLocal,
                    Warn = btOnly
                });
            }

            if (facts.Access != null && facts.Access.Read)
            {
                bool swallow = facts.Access.FilterKeysSwallowing;
                bool needFix = facts.Access.AnyNeedsFix;
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.legacypurge.14"),
                    Value = swallow ? Lang.T("t.systemaudit.107") : needFix ? Lang.T("t.systemaudit.108") : Lang.T("t.systemaudit.1"),
                    Note = swallow
                        ? Lang.T("t.systemaudit.109")
                            + facts.Access.DelayBeforeAcceptanceMs
                            + Lang.T("t.systemaudit.110")
                        : needFix
                            ? Lang.T("t.systemaudit.111")
                            : Lang.T("t.systemaudit.112"),
                    Evidence = EvMechanism,
                    Warn = needFix
                });
            }

            bool dpc = facts.ThreadDpc.HasValue;
            if (facts.QueueTampered || dpc)
            {
                var parts = new List<string>();
                if (facts.QueueTampered) parts.Add(Lang.T("t.systemaudit.119"));
                if (dpc) parts.Add(Lang.T("t.systemaudit.120") + facts.ThreadDpc.Value);
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.121"),
                    Value = string.Join("  ", parts.ToArray()),
                    Note = (facts.QueueTampered
                            ? Lang.T("t.systemaudit.122") + InputMythTweak.SystemDefault + Lang.T("t.systemaudit.123")
                            : "")
                        + (dpc
                            ? Lang.T("t.systemaudit.124")
                            : ""),
                    Evidence = EvMechanism,
                    Warn = true
                });
            }
        }

        private static void BuildPersistent(AuditReport report, Facts facts)
        {
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.125"),
                Value = facts.Hags ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close"),
                Note = Lang.T("t.systemaudit.126"),
                Evidence = EvMechanism,
                Warn = false
            });

            BuildMemoryCompression(report, facts.InstalledMemoryKnown, facts.InstalledMemoryKb,
                facts.MemoryCompressionKnown, facts.CompressionOn, facts.CombiningOn, MemCompressTweak.OwnsState);

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.132"),
                Value = facts.MpoOff ? Lang.T("t.systemaudit.133") : Lang.T("t.systemaudit.134"),
                Note = facts.MpoOff
                    ? Lang.T("t.systemaudit.135")
                    : Lang.T("t.systemaudit.136"),
                Evidence = EvMechanism,
                Warn = facts.MpoOff
            });

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.43"),
                Value = facts.Dvr ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close"),
                Note = facts.Dvr ? Lang.T("t.systemaudit.137")
                    : Lang.T("t.systemaudit.138"),
                Evidence = EvMechanism,
                Warn = facts.Dvr
            });

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.139"),
                Value = facts.Plan,
                Note = Lang.T("t.systemaudit.140"),
                Evidence = EvMechanism,
                Warn = false
            });

bool quantumTampered = false;
            string quantumState = "";
            try { quantumTampered = QuantumTweak.NeedsRepair(); quantumState = QuantumTweak.Describe(); }
            catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.144"),
                Value = quantumTampered ? Lang.T("t.systemaudit.145") : Lang.T("t.systemaudit.134"),
                Note = (quantumTampered
                    ? Lang.T("t.systemaudit.146")
                    : Lang.T("t.systemaudit.147")) + " " + quantumState,
                Evidence = EvMechanism,
                Warn = quantumTampered,
                FixKey = "quantum"
            });

            bool netTampered = false;
            string netState = "";
            try { netTampered = NetTweak.NeedsRepair(); netState = NetTweak.Describe(); }
            catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.148"),
                Value = netTampered ? Lang.T("t.systemaudit.145") : Lang.T("t.systemaudit.134"),
                Note = (netTampered
                    ? Lang.T("t.systemaudit.149")
                    : Lang.T("t.systemaudit.150")) + " " + netState,
                Evidence = EvMechanism,
                Warn = netTampered,
                FixKey = "net"
            });

            bool fthShimmed = false;
            string fthState = "";
            try { fthShimmed = FthTweak.NeedsRepair(); fthState = FthTweak.Describe(); }
            catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.151"),
                Value = fthShimmed ? Lang.T("t.systemaudit.152") : Lang.T("t.systemaudit.134"),
                Note = fthState + " " + Lang.T(fthShimmed ? "t.systemaudit.153" : "t.systemaudit.154"),
                Evidence = EvMechanism,
                Warn = fthShimmed,
                FixKey = "fth"
            });
        }

        internal static void BuildMemoryCompression(AuditReport report, bool memoryKnown, ulong installedKb,
            bool stateKnown, bool compression, bool combining, bool owned)
        {
            bool eligible = memoryKnown && MemCompressTweak.RamEligible(installedKb);
            bool off = stateKnown && !compression && !combining;
            report.MemoryCompressionCanDisable = eligible && stateKnown && !off;
            string value = !memoryKnown || !stateKnown ? Lang.T("t.systemaudithardware.3")
                : off ? Lang.T("audit.mem.off") : eligible ? Lang.T("audit.mem.todo") : Lang.T("audit.mem.small");
            string note = !memoryKnown || !stateKnown ? Lang.T("audit.mem.unknown")
                : off ? Lang.T(owned ? "audit.mem.owned" : "audit.mem.external")
                : eligible ? Lang.T("audit.mem.policy") : Lang.T("audit.mem.keep");
            if (memoryKnown) note = Lang.F("audit.mem.capacity", (installedKb / 1048576.0).ToString("0.#")) + " " + note;
            if (stateKnown) note += " " + Lang.F("audit.mem.states", Lang.T(compression ? "audit.mem.on" : "notes.close"),
                Lang.T(combining ? "audit.mem.on" : "notes.close"));
            report.Persistent.Add(new AuditRow { Name = Lang.T("audit.mem.title"), Value = value, Note = note,
                Warn = report.MemoryCompressionCanDisable, FixKey = "memcompress",
                Evidence = memoryKnown && stateKnown ? EvMeasuredLocal : EvUnverified });
        }

        internal static void BuildGpuInterrupt(AuditReport report)
        {
            GpuIrqResult r = report.GpuIrq;
            if (r == null) return;
            report.Machine.RemoveAll(row => row.FixKey == "gpuirq" || row.FixKey == "gpuirqdriver");
            string value, note;
            bool warn = false;
            switch (r.Outcome)
            {
                case GpuIrqOutcome.NeedsPlacement:
                    value = Lang.T("audit.irq.overlap"); note = Lang.F("audit.irq.proposal", IrqRelocate.MaskText(r.Target)); warn = true; break;
                case GpuIrqOutcome.NeedsVerification:
                    value = Lang.T("irqcheck.state.verify"); note = Lang.T("irqcheck.hint.verify"); warn = true; break;
                case GpuIrqOutcome.AwaitingReboot:
                    value = Lang.T("irqcheck.state.pending"); note = Lang.T("irqcheck.hint.pending"); warn = true; break;
                case GpuIrqOutcome.Clear:
                    value = Lang.T("irqcheck.state.clear"); note = Lang.T("irqcheck.hint.clear"); break;
                case GpuIrqOutcome.NoRegion:
                    value = Lang.T("irqcheck.state.noregion"); note = Lang.T("irqcheck.out.noregion"); break;
                case GpuIrqOutcome.NoTarget:
                    value = Lang.T("irqcheck.reason.notarget"); note = Lang.T("irqcheck.reason.notarget"); break;
                case GpuIrqOutcome.Skipped:
                    value = Lang.T("audit.irq.skipped"); note = r.Detail; break;
                case GpuIrqOutcome.NoEvidence:
                    value = Lang.T("audit.irq.insufficient");
                    note = Lang.F("audit.irq.check.hint", report.MeasureWindowMs > 0
                        ? (int)Math.Ceiling(report.MeasureWindowMs / 1000.0) : GpuIrqCheck.CheckSeconds); break;
                case GpuIrqOutcome.Cancelled:
                    value = Lang.T("audit.irq.skipped"); note = Lang.T("irqcheck.out.cancelled"); break;
                default:
                    value = Lang.T("audit.irq.unavailable"); note = r.Detail; break;
            }
            if (r.Gpu != null)
                note += " " + Lang.F("audit.irq.sample", r.Gpu.Name, IrqRelocate.MaskText(r.Gpu.Seen),
                    r.Gpu.Events, r.Gpu.P99Us.ToString("F0"));
            report.Machine.Add(new AuditRow { Name = Lang.T("irqcheck.gpu.title"), Value = value,
                Note = note, Warn = warn, FixKey = "gpuirq",
                Evidence = r.Gpu != null && r.Gpu.Events >= GpuIrqCheck.MinEvents && r.Gpu.Seen != 0 ? EvMeasuredLocal : EvUnverified });
            foreach (SlowDriver slow in r.Slow)
                report.Machine.Add(new AuditRow { Name = Lang.T("irqcheck.slow.title") + " · " + slow.Driver,
                    Value = Lang.F("irqcheck.slow.row", slow.MaxUs.ToString("F0"), slow.P99Us.ToString("F0")),
                    Note = slow.Device + " " + Lang.T("irqcheck.slow.hint"), Warn = true,
                    Evidence = EvMeasuredLocal, FixKey = "gpuirqdriver" });
        }
    }
}
