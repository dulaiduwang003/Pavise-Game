using System;
using System.Collections.Generic;

namespace PaviseApp
{
    // A bounded read-only plan Every accepted session must agree on identity
    // configuration and placement a cool latest match cannot hide an older busy one
    internal sealed class IrqCorePlan
    {
        internal readonly List<IrqCoreOption> Options = new List<IrqCoreOption>();
        internal int Sessions, Excluded;
        internal string Reason = "missing";
        internal double GameDpcMsPerMinute, GameSlowPerMinute, GameDpcShare;
        internal bool Historical;
        internal bool Stable { get { return Sessions >= IrqSessionLedger.MinSessionsForVerdict; } }

        internal string Heading
        {
            get { return Lang.T("irq.plan." + Reason); }
        }
        internal string Summary
        {
            get
            {
                if (Sessions == 0) return Lang.T("irq.plan.next." + Reason);
                return Lang.F("irq.plan.summary",Sessions,GameSlowPerMinute.ToString("F1"),GameDpcMsPerMinute.ToString("F1"))
                    + "\n" + Lang.T(Historical ? "irq.plan.historical" : Options.Count == 0 ? "irq.plan.nocore"
                        : Stable ? "irq.plan.ready" : "irq.plan.more");
            }
        }
        internal string Details
        {
            get
            {
                string text = Heading + "\n" + Summary;
                if (Sessions == 0) return text;
                text += "\n" + Lang.F("irq.plan.share",GameDpcShare.ToString("F1"),Excluded);
                foreach (var option in Options)
                    text += "\n" + Lang.F("irq.plan.option.detail",IrqRelocate.MaskText(option.Mask),
                        option.EvidenceSessions,option.AveragePercent.ToString("F0"),option.BusyPercent.ToString("F0"));
                return text + "\n" + Lang.T("irq.plan.caution");
            }
        }

        internal static IrqCorePlan Build(IrqPinSession selected, IList<IrqPinSession> history,
            IrqDevice device, ulong[] physical, bool multiGroup)
        {
            var plan = new IrqCorePlan();
            if (multiGroup) { plan.Reason = "groups"; return plan; }
            if (device == null) return plan;
            if (device.ManagedElsewhere) { plan.Reason = "owned"; return plan; }
            if (device.SharedStats || device.FrameworkStats) { plan.Reason = "shared"; return plan; }
            if (device.CompletionFollowsIssuer) { plan.Reason = "storage"; return plan; }
            if (device.MultiMessageRisk) { plan.Reason = "queues"; return plan; }
            if (!device.ConfigurationKnown || string.IsNullOrEmpty(device.DriverVersion) || string.IsNullOrEmpty(device.InstanceId))
            { plan.Reason = "configuration"; return plan; }
            if (!Valid(selected)) return plan;
            var anchor = selected.Record;
            string configuration;
            if (!anchor.DeviceConfigurations.TryGetValue(device.InstanceId,out configuration)
                || configuration != IrqAdjustmentLedger.DeviceConfiguration(device))
            { plan.Reason = "changed"; return plan; }

            var accepted = new List<IrqPinSession> { selected };
            var seen = new HashSet<long> { anchor.StartUtcTicks };
            var ordered = new List<IrqPinSession>();
            if (history != null)
                foreach (var view in history)
                    if (view != null && view.Record != null && view.StartUtcTicks > 0
                        && view.StartUtcTicks < anchor.StartUtcTicks) ordered.Add(view);
            ordered.Sort(delegate(IrqPinSession a,IrqPinSession b) { return b.StartUtcTicks.CompareTo(a.StartUtcTicks); });
            foreach (var view in ordered)
            {
                if (!seen.Add(view.StartUtcTicks)) continue;
                if (!SameScope(selected,view,device.InstanceId,configuration)) { plan.Excluded++; continue; }
                accepted.Add(view);
                if (accepted.Count >= IrqSessionLedger.VerdictWindow) break;
            }

            double seconds = 0, gameNs = 0, totalNs = 0, gameSlow = 0;
            foreach (var view in accepted)
            {
                var game = view.Driver.OnCores(view.Record.GameMask);
                seconds += view.DurationSeconds; gameNs += game.TotalNs;
                totalNs += view.Driver.DpcTotalNs; gameSlow += game.Over500Us;
                var candidates = IrqCoreRecommendation.Candidates(view,device,physical,false);
                if (plan.Sessions == 0)
                    foreach (var option in candidates)
                        plan.Options.Add(new IrqCoreOption { Mask = option.Mask,AveragePercent = option.AveragePercent,
                            BusyPercent = option.BusyPercent,Seen = option.Seen,EvidenceSessions = 1 });
                else
                    for (int i = plan.Options.Count - 1; i >= 0; i--)
                    {
                        var target = plan.Options[i];
                        var match = candidates.Find(delegate(IrqCoreOption candidate) { return candidate.Mask == target.Mask; });
                        if (match == null) { plan.Options.RemoveAt(i); continue; }
                        target.AveragePercent = Math.Max(target.AveragePercent,match.AveragePercent);
                        target.BusyPercent = Math.Max(target.BusyPercent,match.BusyPercent);
                        target.Seen &= match.Seen; target.EvidenceSessions++;
                    }
                plan.Sessions++;
            }
            plan.Options.Sort(delegate(IrqCoreOption a,IrqCoreOption b) {
                int score = (a.AveragePercent + a.BusyPercent).CompareTo(b.AveragePercent + b.BusyPercent);
                if (score != 0) return score;
                if (a.Seen != b.Seen) return b.Seen.CompareTo(a.Seen);
                return a.Mask.CompareTo(b.Mask);
            });
            plan.GameDpcMsPerMinute = gameNs / 1000000.0 * 60.0 / seconds;
            plan.GameSlowPerMinute = gameSlow * 60.0 / seconds;
            plan.GameDpcShare = totalNs <= 0 ? 0 : 100.0 * gameNs / totalNs;
            plan.Historical = !selected.CurrentBoot;
            plan.Reason = plan.Options.Count == 0 ? "none" : plan.Stable ? "stable" : "provisional";
            return plan;
        }

        private static bool Valid(IrqPinSession view)
        {
            if (view == null || !view.Available || view.Record == null || view.Driver == null) return false;
            var record = view.Record;
            return record.StartUtcTicks > 0 && view.StartUtcTicks == record.StartUtcTicks
                && view.DurationSeconds == record.DurationSeconds && view.GameMask == record.GameMask
                && record.VerdictExclusion(record.BootStamp,record.TopologyStamp) == IrqSessionExclusion.None
                && !string.IsNullOrEmpty(record.GameId) && !string.IsNullOrEmpty(record.Configuration)
                && record.Drivers.Contains(view.Driver) && !string.IsNullOrEmpty(view.Driver.Driver)
                && !string.IsNullOrEmpty(view.Driver.DriverVersion)
                && record.GameMask != 0 && record.GameMask != record.SystemMask
                && (record.GameMask & ~record.SystemMask) == 0 && view.Driver.ValidCores(record.SystemMask);
        }

        private static bool SameScope(IrqPinSession anchor, IrqPinSession other,string device,string configuration)
        {
            if (!Valid(other)) return false;
            var a = anchor.Record; var b = other.Record;
            string captured;
            return a.GameId == b.GameId && a.Configuration == b.Configuration && a.GameMask == b.GameMask
                && a.SystemMask == b.SystemMask && a.TopologyStamp == b.TopologyStamp
                && IrqAffinityEngine.SameBoot(a.BootStamp,b.BootStamp)
                && string.Equals(anchor.Driver.Driver,other.Driver.Driver,StringComparison.OrdinalIgnoreCase)
                && anchor.Driver.DriverVersion == other.Driver.DriverVersion
                && b.DeviceConfigurations.TryGetValue(device,out captured) && captured == configuration;
        }
    }
}
