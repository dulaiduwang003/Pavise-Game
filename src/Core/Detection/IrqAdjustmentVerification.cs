using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class IrqAdjustmentVerification
    {
        internal static string Describe(IrqAdjustment a, IrqDevice device,
            IList<IrqSessionRecord> sessions, string boot, string topology)
        {
            string placement = Placement(a, device, sessions, boot, topology);
            string performance; string detail = Compare(a, sessions, out performance);
            a.Placement = placement; a.Performance = performance;
            return Lang.T("irq.phase." + a.Phase) + " · " + Lang.T("irq.verify." + placement)
                + "\n" + Results(a)
                + "\n" + Lang.T("irq.performance." + performance) + " · " + detail;
        }

        internal static string DescribeHistory(IList<IrqAdjustment> records, IrqDevice device,
            IList<IrqSessionRecord> sessions, string boot, string topology, out IrqAdjustment current)
        {
            current = IrqAdjustmentHistory.Current(records, device == null ? null : device.InstanceId);
            if (current == null) return "";
            var latest = IrqAdjustmentHistory.Latest(records, device.InstanceId);
            string text = Describe(current, device, sessions, boot, topology);
            if (latest != current)
                text = Lang.F("irq.adjust.latest", Lang.T("irq.phase." + latest.Phase)) + "\n"
                    + Results(latest) + "\n" + Lang.T("irq.adjust.current") + "\n" + text;
            return text;
        }

        private static string Results(IrqAdjustment a)
        {
            return Lang.F("irq.adjust.results", Lang.T("irq.step." + a.AffinityResult),
                Lang.T("irq.step." + a.PriorityResult), Lang.T("irq.step." + a.RollbackResult),
                Lang.T("irq.step." + a.RestoreAffinityResult), Lang.T("irq.step." + a.RestorePriorityResult));
        }

        internal static string Placement(IrqAdjustment a, IrqDevice device,
            IList<IrqSessionRecord> sessions, string boot, string topology)
        {
            if (a.Phase == "restored") return "restored";
            if (a.Phase == "restorepartial") return "unknown";
            if (a.Phase != "written" && a.Phase != "partial") return "unknown";
            if (device == null || device.Policy != 4 || device.Mask != a.Target
                || (a.Phase == "written" && a.PriorityRequested && !device.DevicePriorityHigh)) return "config";
            IrqRebootState state = IrqAffinityEngine.RebootStateFromStamps(a.Boot, boot);
            if (state == IrqRebootState.AwaitingReboot) return "reboot";
            if (state != IrqRebootState.Rebooted || a.Topology != topology) return "unknown";
            if (device.SharedStats || device.FrameworkStats || a.DriverVersion.Length == 0
                || device.DriverVersion != a.DeviceVersion) return "pending";
            if (sessions != null)
                for (int i = sessions.Count - 1; i >= 0; i--)
                {
                    var s = sessions[i];
                    if (s == null || s.StartUtcTicks <= a.WrittenUtc || s.DurationSeconds < 60
                        || s.EventsLost != 0 || s.Unmapped != 0 || s.TopologyStamp != topology
                        || !IrqAffinityEngine.SameBoot(s.BootStamp, boot)) continue;
                    string captured;
                    if (!s.DeviceConfigurations.TryGetValue(a.Device,out captured)) continue;
                    // Priority has its own outcome A failed priority write must not
                    // prevent verifying an independently successful affinity change
                    string[] fields = captured.Split(':');
                    if (fields.Length != 4 || fields[0] != "4"
                        || fields[1] != a.Target.ToString("X",System.Globalization.CultureInfo.InvariantCulture)
                        || fields[3] != a.DeviceVersion) continue;
                    foreach (var d in s.Drivers)
                        if (string.Equals(d.Driver, a.Driver, StringComparison.OrdinalIgnoreCase)
                            && d.DriverVersion == a.DriverVersion && d.ValidCores(s.SystemMask))
                            return (d.CpuMask & ~a.Target) == 0 ? "matches" : "mismatch";
                }
            return "pending";
        }

        internal static string Compare(IrqAdjustment a, IList<IrqSessionRecord> sessions, out string outcome)
        {
            outcome = "insufficient";
            var after = new List<IrqComparisonSample>();
            var seen = new HashSet<long>();
            var reasons = new SortedSet<string>();
            if (a.Phase != "written") reasons.Add("write");
            if (a.Baseline.Count == 0) reasons.Add("baseline");
            if (sessions != null)
                foreach (var s in sessions)
                {
                    if (s == null || s.StartUtcTicks <= a.WrittenUtc
                        || a.RestoredUtc > 0 && s.StartUtcTicks >= a.RestoredUtc) continue;
                    if (IrqAffinityEngine.RebootStateFromStamps(a.Boot, s.BootStamp) != IrqRebootState.Rebooted)
                    { reasons.Add("reboot"); continue; }
                    string reason; var sample = IrqAdjustmentLedger.Sample(a, s, out reason);
                    if (sample == null) { reasons.Add(reason); continue; }
                    if (a.Baseline.Count > 0)
                    {
                        double seconds = Median(a.Baseline, delegate(IrqComparisonSample n) { return n.Seconds; });
                        if (sample.Seconds < seconds * .5 || sample.Seconds > seconds * 2)
                        { reasons.Add("duration"); continue; }
                    }
                    if (seen.Add(sample.Start)) after.Add(sample);
                }
            string detail = Lang.F("irq.compare.counts", a.Baseline.Count, after.Count);
            var beforeComplete = a.Baseline.FindAll(Complete);
            var afterComplete = after.FindAll(Complete);
            if (a.Baseline.Count < 3 || after.Count < 3) reasons.Add("sample");
            if (beforeComplete.Count < 3 || afterComplete.Count < 3) reasons.Add("coverage");
            foreach (var sample in a.Baseline) if (!sample.FramesReliable) reasons.Add("stream");
            foreach (var sample in after) if (!sample.FramesReliable) reasons.Add("stream");
            if (a.Baseline.Count > 0 && after.Count > 0)
            {
                detail += "\n" + Metric("irq.compare.slow", a.Baseline, after, delegate(IrqComparisonSample n) { return n.SlowPerMinute; });
                detail += "\n" + Metric("irq.compare.p99", a.Baseline, after, delegate(IrqComparisonSample n) { return n.P99; });
                detail += " · " + Metric("irq.compare.p999", a.Baseline, after, delegate(IrqComparisonSample n) { return n.P999; });
                detail += "\n" + Metric("irq.compare.long", a.Baseline, after, delegate(IrqComparisonSample n) { return n.LongPerMinute; });
                detail += " · " + Metric("irq.compare.load", a.Baseline, after, delegate(IrqComparisonSample n) { return n.TargetLoad; });
                detail += "\n" + Lang.F("irq.compare.coverage",
                    (100 * Median(a.Baseline, delegate(IrqComparisonSample n) { return n.FrameCoverage; })).ToString("F0"),
                    (100 * Median(after, delegate(IrqComparisonSample n) { return n.FrameCoverage; })).ToString("F0"),
                    (100 * Median(a.Baseline, delegate(IrqComparisonSample n) { return n.LoadCoverage; })).ToString("F0"),
                    (100 * Median(after, delegate(IrqComparisonSample n) { return n.LoadCoverage; })).ToString("F0"));
            }
            // Evaluate only complete matched samples excluded sessions are still explained below
            if (a.Phase == "written" && beforeComplete.Count >= 3 && afterComplete.Count >= 3)
            {
                bool improved = Separated(beforeComplete, afterComplete, delegate(IrqComparisonSample n) { return n.SlowPerMinute; })
                    && Separated(beforeComplete, afterComplete, delegate(IrqComparisonSample n) { return n.P99; })
                    && Separated(beforeComplete, afterComplete, delegate(IrqComparisonSample n) { return n.LongPerMinute; });
                improved = improved
                    && Median(afterComplete,delegate(IrqComparisonSample n) { return n.P999; })
                        <= Median(beforeComplete,delegate(IrqComparisonSample n) { return n.P999; })
                    && Median(afterComplete,delegate(IrqComparisonSample n) { return n.TargetLoad; })
                        <= Median(beforeComplete,delegate(IrqComparisonSample n) { return n.TargetLoad; }) + 5;
                outcome = improved ? "improved" : "unchanged";
            }
            foreach (string reason in reasons) detail += "\n" + Lang.T("irq.compare.reason." + reason);
            return detail + "\n" + Lang.T("irq.compare.caution");
        }
        private static bool Complete(IrqComparisonSample n)
        { return n != null && n.Valid && n.FramesReliable && n.FrameCoverage >= .8 && n.LoadCoverage >= .8 && n.P99 > 0 && n.P999 >= n.P99 && n.TargetLoad >= 0; }
        private static bool Separated(List<IrqComparisonSample> before, List<IrqComparisonSample> after, Func<IrqComparisonSample,double> get)
        {
            double min = double.MaxValue, max = 0;
            foreach (var n in before) min = Math.Min(min, get(n));
            foreach (var n in after) max = Math.Max(max, get(n));
            return min > 0 && max < min * .9;
        }
        private static double Median(List<IrqComparisonSample> samples, Func<IrqComparisonSample,double> get)
        {
            var values = new List<double>();
            foreach (var s in samples) { double v = get(s); if (v >= 0 && !double.IsNaN(v) && !double.IsInfinity(v)) values.Add(v); }
            if (values.Count == 0) return -1;
            values.Sort(); int n = values.Count;
            return n % 2 == 0 ? (values[n / 2 - 1] + values[n / 2]) / 2 : values[n / 2];
        }
        private static string Metric(string key, List<IrqComparisonSample> before, List<IrqComparisonSample> after, Func<IrqComparisonSample,double> get)
        {
            double b = Median(before,get), a = Median(after,get);
            return Lang.T(key) + " " + (b < 0 ? "?" : b.ToString("F2")) + " → " + (a < 0 ? "?" : a.ToString("F2"));
        }
    }
}
