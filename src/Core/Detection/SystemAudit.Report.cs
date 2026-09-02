// @author bdth 2074055628@qq.com
// 文件用途 体检报告装配 BypassIO 与能力项结论
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        // 体检只读不写 每个 Build 段各管一组结论 互相不依赖
        //   采样失败只会让 MeasureOk 为 false 其余结论照常出 不整个报告作废
        public static AuditReport Collect(int measureWindowMs)
        {
            var report = new AuditReport();
            report.MeasureWindowMs = measureWindowMs;

            double cpuBusy = 0;
            double[] rates = null;
            try { rates = DpcSampler.MeasureLoad(measureWindowMs, out cpuBusy); } catch { }
            report.MeasureOk = rates != null;

            int hzCur, hzBest;
            DisplayGuard.QueryRefreshRates(out hzCur, out hzBest);

            Facts facts = Gather();
            BuildCapability(report, facts);
            BuildMachine(report, facts, cpuBusy, hzCur, hzBest);
            BuildBypassIo(report, facts);
            BuildHardwareHealth(report, facts);
            BuildInputChain(report, facts);
            BuildPersistent(report, facts);
            BuildVerdicts(report, facts, hzCur, hzBest);
            return report;
        }

        private static List<BypassIoVerdict> GatherBypassIo()
        {
            var verdicts = new List<BypassIoVerdict>();
            if (Native.OsBuild() > 0 && Native.OsBuild() < 22000) return verdicts;
            var paths = new List<string>();
            try
            {
                Func<List<string>> provider = LibraryPaths;
                List<string> lib = provider != null ? provider() : null;
                if (lib != null) paths.AddRange(lib);
            }
            catch { }
            try
            {
                using (var me = System.Diagnostics.Process.GetCurrentProcess())
                    paths.Add(me.MainModule.FileName);
            }
            catch { }
            var volumes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                string root;
                try { root = System.IO.Path.GetPathRoot(path); } catch { continue; }
                if (string.IsNullOrEmpty(root) || !volumes.Add(root)) continue;
                try { if (!System.IO.File.Exists(path)) continue; } catch { continue; }
                BypassIoVerdict v = BypassIoProbe.Query(path);
                if (v.Supported) verdicts.Add(v);
                if (verdicts.Count >= 4) break;
            }
            return verdicts;
        }

        private static void BuildBypassIo(AuditReport report, Facts facts)
        {
            if (facts.BypassIo == null) return;
            foreach (BypassIoVerdict v in facts.BypassIo)
            {
                string root = "";
                try { root = System.IO.Path.GetPathRoot(v.Path) ?? ""; } catch { }
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.bypassio.1") + " " + root.TrimEnd('\\'),
                    Value = v.Enabled
                        ? Lang.T("t.bypassio.2")
                        : Lang.F("t.bypassio.3", v.Blocker.Length > 0 ? v.Blocker : "?"),
                    Note = v.Enabled
                        ? Lang.T("t.bypassio.4")
                        : Lang.T("t.bypassio.5")
                            + (v.Reason.Length > 0 ? " " + v.Reason : ""),
                    Evidence = EvMechanism,
                    Warn = !v.Enabled
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
                    Note = facts.IntegratedOnly
                        ? Lang.T("t.systemaudit.3")
                        : Lang.T("t.systemaudit.4"),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.5"),
                Value = facts.Nv ? Lang.T("t.systemaudit.6") : Lang.T("t.systemaudit.7"),
                Note = facts.Nv
                    ? Lang.T("t.systemaudit.8")
                    : facts.NvHardware
                        ? Lang.T("t.systemaudit.9")
                        : Lang.T("t.systemaudit.10"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.16"),
                Value = facts.Partition ? Lang.T("t.systemaudit.6") : Lang.T("t.systemaudit.7"),
                Note = facts.Partition ? Lang.T("t.systemaudit.17")
                    : Lang.T("t.systemaudit.18"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.19"),
                Value = !facts.Eco ? Lang.T("t.systemaudit.20") : (facts.EcoFull ? Lang.T("t.systemaudit.21") : Lang.T("t.systemaudit.22")),
                Note = !facts.Eco
                    ? Lang.T("t.systemaudit.23")
                    : (facts.EcoFull
                        ? Lang.T("t.systemaudit.24")
                        : Lang.T("t.systemaudit.25")),
                Evidence = EvMeasuredLocal,
                Warn = !facts.Eco
            });

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.26"),
                Value = WindowsText(),
                Note = Lang.T("t.systemaudit.27"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });
        }
    }
}
