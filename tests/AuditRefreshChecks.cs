#if PAVISE_AUDIT_REFRESH_CHECKS
using System;
using System.Diagnostics;

namespace PaviseApp
{
    internal static partial class SelfTests
    { internal static bool TryHandleRuntimeMode(string[] args) { throw new InvalidOperationException("Audit refresh checks never start the application"); } }

    // 体检只读电源计划；读一律给"未知"，任何写入都拒绝，检查程序不会碰真实电源平台。
    internal sealed class ReadOnlyPowerPlans : IManagedPowerPlans
    {
        public Guid? Current() { return null; }
        public bool Exists(Guid scheme) { return false; }
        public bool IsOwned(Guid scheme) { return false; }
        public bool Duplicate(Guid source, out Guid created) { throw new InvalidOperationException("Audit refresh must not change power plans"); }
        public bool Activate(Guid scheme) { throw new InvalidOperationException("Audit refresh must not change power plans"); }
        public bool Delete(Guid scheme) { throw new InvalidOperationException("Audit refresh must not change power plans"); }
        public bool Rename(Guid scheme, string title, string note) { throw new InvalidOperationException("Audit refresh must not change power plans"); }
        public string Name(Guid scheme) { return ""; }
        public Guid[] ProcessorSettings(Guid scheme, Guid processor) { return new Guid[0]; }
        public uint? Read(Guid scheme, Guid sub, Guid setting, bool ac) { return null; }
        public bool Write(Guid scheme, Guid sub, Guid setting, bool ac, uint value) { throw new InvalidOperationException("Audit refresh must not change power plans"); }
    }

    // 在本机真实执行修复后的刷新：只读系统状态，沿用上一轮测量，不进入采样窗口。
    internal static class AuditRefreshChecks
    {
        private static int checks;

        private static void Check(bool good, string message)
        {
            checks++;
            if (!good) throw new InvalidOperationException("FAIL " + message);
        }

        private static AuditRow GpuIrqRow(AuditReport report)
        {
            foreach (AuditRow row in report.Machine) if (row.FixKey == "gpuirq") return row;
            return null;
        }

        private static int Main()
        {
            try
            {
                Settings.UseTransientStoreForCurrentProcess(); Logger.WritesEnabled = false; Lang.Init();
                PowerPlan.ManagedPlansForTest = new ReadOnlyPowerPlans();
                Check(SystemAudit.Refresh(null, null) == null, "a refresh without a previous checkup produced a report");
                var measured = new GpuIrqResult { Outcome = GpuIrqOutcome.NeedsPlacement };
                var previous = new AuditReport { MeasureOk = true, MeasureWindowMs = GpuIrqCheck.CheckSeconds * 1000, CpuBusy = 42.5, GpuIrq = measured };
                Stopwatch clock = Stopwatch.StartNew();
                AuditReport refreshed = SystemAudit.Refresh(previous, null);
                clock.Stop();
                Check(refreshed != null && !ReferenceEquals(refreshed, previous), "the refresh did not build a new report");
                Check(clock.Elapsed.TotalSeconds < GpuIrqCheck.CheckSeconds / 2.0,
                    "the refresh took " + clock.Elapsed.TotalSeconds.ToString("F1") + " s, as if it sampled again");
                Check(refreshed.MeasureOk && refreshed.MeasureWindowMs == previous.MeasureWindowMs && refreshed.CpuBusy == 42.5,
                    "the refresh lost the previous measurement");
                Check(ReferenceEquals(refreshed.GpuIrq, measured), "the refresh replaced the measured interrupt result");
                AuditRow irq = GpuIrqRow(refreshed);
                Check(irq != null && irq.Value == Lang.T("audit.irq.overlap") && irq.Warn, "the interrupt row was not rebuilt from the measurement");
                Check(refreshed.Capability.Count > 0 && refreshed.Machine.Count > 0 && refreshed.Verdicts.Count > 0,
                    "the refresh did not rebuild the static checks");
                AuditReport again = SystemAudit.Refresh(refreshed, new GpuIrqResult { Outcome = GpuIrqOutcome.Verified });
                AuditRow fixedRow = again == null ? null : GpuIrqRow(again);
                Check(again != null && again.GpuIrq.Outcome == GpuIrqOutcome.Clear && again.CpuBusy == 42.5
                    && fixedRow != null && fixedRow.Value == Lang.T("irqcheck.state.clear") && !fixedRow.Warn,
                    "a verified interrupt fix was not folded into the refreshed report");
                Console.WriteLine("PASS audit refresh checks=" + checks + "; refresh took " + clock.Elapsed.TotalSeconds.ToString("F1")
                    + " s versus a " + GpuIrqCheck.CheckSeconds + " s measured checkup; read-only, no sampling");
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
#endif
