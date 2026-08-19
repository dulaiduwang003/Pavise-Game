// @author bdth 2074055628@qq.com
// 文件用途 把帧卡顿的判决变成一个能按下去的动作 这是归因唯一的兑现出口
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal enum RemedyKind
    {
        None = 0,
        Observe = 1,
        IrqAffinity = 2,
        SuppressProcess = 3
    }

    internal sealed class RemedyPlan
    {
        public RemedyKind Kind;
        public string Title = "";
        public string Why = "";
        public string Blocked = "";
        public string Target = "";
        public readonly List<string> DeviceIds = new List<string>();
        public uint Pid;
        public ulong Mask;
        public bool CanApply { get { return Kind == RemedyKind.IrqAffinity && DeviceIds.Count > 0; } }
    }

    internal static class FrameRemedy
    {
        private static readonly IrqAffinityEngine engine =
            new IrqAffinityEngine("FrameRemedyIrqOn", "FrameRemedyIrq_", Lang.T("remedy.logprefix"));

        private const double MinDpcMaxUsToAct = 200.0;

        public static bool Applied { get { return engine.EnabledByPavise; } }
        public static bool HasResidue { get { return engine.HasResidue; } }

        public static RemedyPlan Plan(FrameFaultVerdict v, DriverInterrupt worst)
        {
            var p = new RemedyPlan();
            if (v == null) { p.Title = Lang.T("remedy.none"); return p; }

            if (v.Conclusion == FaultConclusion.NoSlowFrames)
            { p.Title = Lang.T("remedy.noslow"); return p; }

            if (v.Conclusion == FaultConclusion.Inconclusive || v.Conclusion == FaultConclusion.Elsewhere)
            {
                p.Kind = RemedyKind.Observe;
                p.Title = Lang.T("remedy.unproven.title");
                p.Why = Lang.T("remedy.unproven.why");
                return p;
            }

            if (v.Conclusion == FaultConclusion.Interrupts || v.Conclusion == FaultConclusion.Mixed)
            {
                RemedyPlan irq = PlanIrq(v, worst);
                if (irq != null) return irq;
            }

            if (v.Conclusion == FaultConclusion.CpuPreemption || v.Conclusion == FaultConclusion.Mixed)
            {
                p.Kind = RemedyKind.SuppressProcess;
                p.Pid = v.TopOffenderPid;
                p.Title = Lang.T("remedy.cpu.title");
                p.Why = Lang.F("remedy.cpu.why", v.PathKnown
                    ? Lang.F("remedy.cpu.path", (v.PathSpineExplained * 100).ToString("F0"))
                    : Lang.F("remedy.cpu.window", (v.PreemptExplained * 100).ToString("F0")));
                return p;
            }

            if (v.Conclusion == FaultConclusion.DiskStall)
            {
                p.Kind = RemedyKind.Observe;
                p.Title = Lang.T("remedy.disk.title");
                double free;
                bool got = false;
                try { got = StandbySweep.FreeRatio(out free); } catch { free = 0; }
                p.Why = got
                    ? Lang.F("remedy.disk.why2", (free * 100).ToString("F0"),
                        v.DiskExplained > 0 ? (v.DiskExplained * 100).ToString("F0") : "0")
                    : Lang.T("remedy.disk.why");
                p.Blocked = Lang.T("remedy.disk.noproof");
                return p;
            }

            p.Kind = RemedyKind.Observe;
            p.Title = Lang.T("remedy.noaction");
            return p;
        }

        private static RemedyPlan PlanIrq(FrameFaultVerdict v, DriverInterrupt worst)
        {
            if (worst == null || string.IsNullOrEmpty(worst.Driver)) return null;

            var p = new RemedyPlan();
            p.Target = worst.Driver;
            p.Why = Lang.F("remedy.irq.why", (v.SlowIntrShare * 100).ToString("F1"),
                (v.IntrExplained * 100).ToString("F1"), worst.DpcMaxUs.ToString("F0"));

            if (worst.DpcMaxUs < MinDpcMaxUsToAct)
            {
                p.Kind = RemedyKind.Observe;
                p.Title = Lang.F("remedy.irq.short.title", p.Target);
                p.Blocked = Lang.F("remedy.irq.short.blocked", worst.DpcMaxUs.ToString("F0"),
                    MinDpcMaxUsToAct.ToString("F0"));
                return p;
            }

            DriverDeviceMatch m = DriverDeviceResolver.Resolve(worst.Driver);
            if (!m.Actionable)
            {
                p.Kind = RemedyKind.Observe;
                p.Title = Lang.F("remedy.irq.nodev.title", p.Target);
                p.Blocked = Lang.F("remedy.irq.nodev.blocked", m.Why);
                return p;
            }

            List<string> owned = OwnedByOtherTweaks();
            if (owned.Count > 0)
            {
                var left = new List<string>();
                foreach (string id in m.DeviceIds)
                {
                    bool taken = false;
                    foreach (string g in owned)
                        if (string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) { taken = true; break; }
                    if (!taken) left.Add(id);
                }
                if (left.Count == 0)
                {
                    p.Kind = RemedyKind.Observe;
                    p.Title = Lang.F("remedy.irq.taken.title", p.Target);
                    p.Blocked = Lang.T("remedy.irq.taken.blocked");
                    return p;
                }
                m.DeviceIds.Clear();
                foreach (string id in left) m.DeviceIds.Add(id);
            }

            p.Kind = RemedyKind.IrqAffinity;
            p.Mask = CpuTopology.InterruptMask;
            p.DeviceIds.AddRange(m.DeviceIds);
            p.Title = Lang.F("remedy.irq.title", p.Target);
            return p;
        }

        private static List<string> OwnedByOtherTweaks()
        {
            var owned = new List<string>();
            try
            {
                if (InterruptAffinityTweak.EnabledByPavise)
                    foreach (string id in InterruptAffinityTweak.EnumerateGpuDeviceIds())
                        if (!owned.Contains(id)) owned.Add(id);
            }
            catch { }
            try
            {
                if (NetworkAffinityTweak.EnabledByPavise)
                    foreach (string id in NetworkAffinityTweak.EnumerateNicDeviceIds())
                        if (!owned.Contains(id)) owned.Add(id);
            }
            catch { }
            return owned;
        }

        public static bool Apply(RemedyPlan p)
        {
            if (p == null || !p.CanApply) return false;
            if (!Native.IsElevated()) { Logger.Log(Lang.T("remedy.needadmin")); return false; }
            return engine.Enable(p.DeviceIds, p.Mask);
        }

        public static bool Revert() { return engine.Disable(null); }

        public static void HealFromCrash()
        {
            try { if (engine.HasResidue && !engine.EnabledByPavise) Revert(); } catch { }
        }
    }
}
