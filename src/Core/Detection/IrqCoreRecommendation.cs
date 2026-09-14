using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class IrqCoreOption
    {
        internal ulong Mask;
        internal double AveragePercent, BusyPercent;
        internal int EvidenceSessions;
        internal bool Seen;
    }

    internal static class IrqCoreRecommendation
    {
        private static bool CanRecommend(IrqPinSession view, IrqDevice device, ulong[] physical, bool multiGroup)
        {
            return !multiGroup && view != null && view.Available && view.Record != null
                && view.GameMask != 0 && view.GameMask != view.Record.SystemMask
                && (view.GameMask & ~view.Record.SystemMask) == 0
                && physical != null && physical.Length > 0 && device != null
                && !device.SharedStats && !device.FrameworkStats && !device.ManagedElsewhere
                && !device.MultiMessageRisk && !device.CompletionFollowsIssuer
                && view.Record.CoreLoadWindowTicks > 0 && IrqSessionLedger.ValidCoreLoads(view.Record);
        }

        internal static List<IrqCoreOption> Candidates(IrqPinSession view, IrqDevice device, ulong[] physical, bool multiGroup)
        {
            var options = new List<IrqCoreOption>();
            if (!CanRecommend(view,device,physical,multiGroup)) return options;
            foreach (ulong core in physical)
            {
                if ((core & view.GameMask) != 0 || core == 0 || (core & ~view.Record.SystemMask) != 0) continue;
                ulong known = 0; double peak = 0, busy = 0, coverage = 1;
                foreach (var c in view.Record.CoreLoads)
                    if ((core & (1UL << c.Cpu)) != 0 && c.BusyTicks >= 0 && c.ObservedTicks > 0)
                    {
                        known |= 1UL << c.Cpu; peak = Math.Max(peak,c.AveragePercent);
                        busy = Math.Max(busy,c.BusyTicks / (double)c.ObservedTicks);
                        coverage = Math.Min(coverage,c.ObservedTicks / (double)view.Record.CoreLoadWindowTicks);
                    }
                if (known != core || coverage < .8 || peak > 60 || busy > .2) continue;
                bool seen = (view.SeenMask & core) != 0;
                options.Add(new IrqCoreOption { Mask = core, AveragePercent = peak, BusyPercent = busy * 100, Seen = seen });
            }
            options.Sort(delegate(IrqCoreOption a, IrqCoreOption b) {
                double aScore = a.AveragePercent + a.BusyPercent - (a.Seen ? 1 : 0);
                double bScore = b.AveragePercent + b.BusyPercent - (b.Seen ? 1 : 0);
                int score = aScore.CompareTo(bScore);
                return score != 0 ? score : a.Mask.CompareTo(b.Mask);
            });
            return options;
        }

        internal static string Describe(IrqPinSession view, IrqDevice device, ulong[] physical, bool multiGroup)
        {
            if (multiGroup) return Lang.T("irq.exact.unsupported");
            if (!CanRecommend(view,device,physical,multiGroup)) return Lang.T("irq.candidate.unknown");
            var options = Candidates(view,device,physical,multiGroup);
            string text = options.Count == 0 ? Lang.T("irq.candidate.none") : Lang.T("irq.candidate.title");
            for (int i = 0; i < Math.Min(3,options.Count); i++)
            {
                var option = options[i];
                text += "\n" + Lang.F("irq.candidate.core",IrqRelocate.MaskText(option.Mask),
                    option.AveragePercent.ToString("F0"),option.BusyPercent.ToString("F0"))
                    + (option.Seen ? Lang.T("irq.candidate.seen") : "");
            }
            return text + "\n" + Lang.T("irq.candidate.caution");
        }

        internal static string CoreDetails(IrqPinSession view)
        {
            if (view.Driver == null || view.Record == null || !view.Driver.ValidCores(view.Record.SystemMask))
                return Lang.T("irq.cores.unknown");
            var lines = new List<string>();
            foreach (var c in view.Driver.Cores)
                lines.Add(Lang.F("irq.cores.row",c.Cpu,c.Count,(c.TotalNs / 1000.0).ToString("F0"),
                    (c.MaxNs / 1000.0).ToString("F0"),c.Over500Us,c.Over1Ms,
                    IrqPercentile.ApproxUs(c.Buckets,c.MaxNs / 1000.0,.99).ToString("F0")));
            return string.Join("\n",lines.ToArray());
        }
    }
}
