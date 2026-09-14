// @author bdth 2074055628@qq.com
// File purpose Judge from multiple real match observations whether a driver's interrupts are worth an IRQ core move
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class IrqDriverVerdict
    {
        public string Driver = "";
        public string DriverVersion = "";
        public double P99Us;
        public int SessionsSeen;
        public int SessionsOverThreshold;
        public double WorstMaxUs;
        public double MedianMaxUs;
        public long TotalOver500;
        public double DpcPerMinute;
        public ulong CpuMask;
        public bool MaskTruncated;
        public long OverlapOver500;
        public double OverlapWorstMaxUs;
        public ulong OverlapCpuMask;
        public double ScoredSeconds;
        public double Collisions;
        public bool StructuralConflict;
        public bool Worth;
        public bool VersionVerified;
    }

    internal static class IrqVerdict
    {
        internal const double MinMaxUs = IrqRelocate.MinMaxUsToOffer;
        internal const double WorthCollisionsPerSec = 0.1;
        internal const double MinStructuralOver500PerMin = 6.0;

        internal static double FrameBudgetUs(int hz)
        {
            if (hz < 20 || hz > 1000) hz = 60;
            return 1000000.0 / hz;
        }

        internal static bool IsSingleCore(ulong mask)
        {
            return mask != 0 && (mask & (mask - 1)) == 0;
        }

        internal static void Score(IrqDriverVerdict v, double budgetUs)
        {
            v.Worth = false;
            v.StructuralConflict = false;
            v.Collisions = 0;
            if (v.OverlapCpuMask == 0 || v.ScoredSeconds <= 0) return;
            // An IRQ core move suggestion needs the game cores and the full placement mask, and the same driver must actually cross the line in multiple matches
            // Three matches overall does not mean this driver reproduces stably; a one-match spike must not be escalated into a suggestion
            if (v.MaskTruncated) return;
            if (v.SessionsSeen < IrqSessionLedger.MinSessionsForVerdict
                || v.SessionsOverThreshold < IrqSessionLedger.MinSessionsForVerdict) return;
            if (v.OverlapWorstMaxUs < MinMaxUs) return;

            if (IsSingleCore(v.OverlapCpuMask))
            {
                v.StructuralConflict = true;
                double per500PerMin = v.OverlapOver500 / (v.ScoredSeconds / 60.0);
                v.Collisions = (v.OverlapOver500 / v.ScoredSeconds)
                    * (v.OverlapWorstMaxUs / budgetUs);
                v.Worth = per500PerMin >= MinStructuralOver500PerMin;
                return;
            }

            double perSec = v.OverlapOver500 / v.ScoredSeconds;
            v.Collisions = perSec * (v.OverlapWorstMaxUs / budgetUs);
            v.Worth = v.Collisions >= WorthCollisionsPerSec;
        }

        public static List<IrqDriverVerdict> Evaluate(List<IrqSessionRecord> all,
            int hz, out int usedSessions)
        {
            usedSessions = 0;
            var result = new List<IrqDriverVerdict>();
            if (all == null || all.Count == 0) return result;

            var window = new List<IrqSessionRecord>();
            for (int i = all.Count - 1; i >= 0 && window.Count < IrqSessionLedger.VerdictWindow; i--)
                if (all[i] != null && all[i].UsableForVerdict) window.Add(all[i]);
            usedSessions = window.Count;
            if (window.Count == 0) return result;

            double seconds = 0;
            foreach (IrqSessionRecord s in window) seconds += s.DurationSeconds;
            if (seconds <= 0) return result;

            var byDriver = new Dictionary<string, IrqDriverVerdict>(StringComparer.OrdinalIgnoreCase);
            var maxima = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            var dpcTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var newestVersion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // The window is sorted newest to oldest; once a driver version has appeared, an older version of the same module
            // that comes after the newer one can no longer inherit or contribute to the recommendation
            foreach (IrqSessionRecord s in window)
            {
                var sessionDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IrqDriverRecord d in s.Drivers)
                {
                    if (d == null || d.Dpc <= 0) continue;
                    string driver = d.Driver ?? "";
                    string version = d.DriverVersion ?? "";
                    string currentVersion;
                    if (!newestVersion.TryGetValue(driver, out currentVersion))
                        newestVersion[driver] = version;
                    else if (!string.Equals(currentVersion, version, StringComparison.OrdinalIgnoreCase))
                        continue;
                    IrqDriverVerdict v;
                    string key = d.Identity;
                    // A valid probe writes one record per driver; duplicates are ignored when the ledger is corrupt
                    // so one match cannot pose as several
                    if (!sessionDrivers.Add(key)) continue;
                    if (!byDriver.TryGetValue(key, out v))
                    {
                        v = new IrqDriverVerdict();
                        v.Driver = d.Driver;
                        v.DriverVersion = d.DriverVersion;
                        byDriver[key] = v;
                        maxima[key] = new List<double>();
                        dpcTotals[key] = 0;
                    }
                    v.SessionsSeen++;
                    bool validGame = s.GameMask != 0 && s.GameMask != s.SystemMask
                        && (s.GameMask & ~s.SystemMask) == 0;
                    bool detailed = d.ValidCores(s.SystemMask);
                    bool legacy = d.Cores.Count == 0 && !d.MaskTruncated
                        && d.CpuMask != 0 && (d.CpuMask & ~s.GameMask) == 0;
                    if (validGame && (detailed || legacy))
                    {
                        IrqDriverCoreRecord relevant = detailed ? d.OnCores(s.GameMask)
                            : new IrqDriverCoreRecord { Over500Us = d.Over500Us, MaxNs = d.DpcMaxNs };
                        if (relevant.Over500Us > 0) v.SessionsOverThreshold++;
                        v.OverlapOver500 += relevant.Over500Us;
                        v.OverlapWorstMaxUs = Math.Max(v.OverlapWorstMaxUs, relevant.MaxNs / 1000.0);
                        v.OverlapCpuMask |= d.CpuMask & s.GameMask;
                        v.ScoredSeconds += s.DurationSeconds;
                    }
                    if (d.DpcMaxUs > v.WorstMaxUs) v.WorstMaxUs = d.DpcMaxUs;
                    v.TotalOver500 += d.Over500Us;
                    v.CpuMask |= d.CpuMask;
                    v.MaskTruncated |= d.MaskTruncated;
                    maxima[key].Add(d.DpcMaxUs);
                    dpcTotals[key] += d.Dpc;
                    double p99 = d.ApproxPercentileUs(0.99);
                    if (p99 > v.P99Us) v.P99Us = p99;
                }
            }

            double budget = FrameBudgetUs(hz);
            double minutes = seconds / 60.0;
            foreach (KeyValuePair<string, IrqDriverVerdict> kv in byDriver)
            {
                IrqDriverVerdict v = kv.Value;
                List<double> m = maxima[kv.Key];
                m.Sort();
                v.MedianMaxUs = m.Count == 0 ? 0 : m[m.Count / 2];
                v.DpcPerMinute = minutes > 0 ? dpcTotals[kv.Key] / minutes : 0;
                Score(v, budget);
                result.Add(v);
            }

            result.Sort(delegate (IrqDriverVerdict a, IrqDriverVerdict b)
            {
                if (a.Worth != b.Worth) return b.Worth.CompareTo(a.Worth);
                if (a.StructuralConflict != b.StructuralConflict)
                    return b.StructuralConflict.CompareTo(a.StructuralConflict);
                int c = b.Collisions.CompareTo(a.Collisions);
                if (c != 0) return c;
                return b.WorstMaxUs.CompareTo(a.WorstMaxUs);
            });
            return result;
        }

        // Display and verdict are separated: complete short matches may show the raw distribution, but every suggestion field takes only the strict
        // Evaluate result, never counting short matches into SessionsSeen, ScoredSeconds, or the collision score
        public static List<IrqDriverVerdict> EvaluateForDisplay(List<IrqSessionRecord> all,
            int hz, out int usedSessions, out int displaySessions)
        {
            List<IrqDriverVerdict> strict = Evaluate(all, hz, out usedSessions);
            return AggregateForDisplay(all, strict, null, null, out displaySessions);
        }

        internal static List<IrqDriverVerdict> AggregateForDisplay(List<IrqSessionRecord> all,
            List<IrqDriverVerdict> strict, string currentBoot, string currentTopology,
            out int displaySessions)
        {
            displaySessions = 0;
            var result = new List<IrqDriverVerdict>();
            if (all == null || all.Count == 0) return result;
            var byDriver = new Dictionary<string, IrqDriverVerdict>(StringComparer.OrdinalIgnoreCase);
            var maxima = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            var dpcTotals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var seconds = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var newestVersion = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // The ledger keeps at most 12 matches; show complete records within this bounded history without letting short matches crowd out
            // evidence still in the strict five-match window; old boot/topology, dropped-event, and zero-second records stay out
            int first = Math.Max(0, all.Count - IrqSessionLedger.KeepSessions);
            for (int i = all.Count - 1; i >= first; i--)
            {
                IrqSessionRecord s = all[i];
                if (s == null || s.DisplayExclusion(currentBoot, currentTopology) != IrqSessionExclusion.None)
                    continue;
                displaySessions++;
                var sessionDrivers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (IrqDriverRecord d in s.Drivers)
                {
                    if (d == null || d.Dpc <= 0 || string.IsNullOrEmpty(d.Driver)) continue;
                    string version = d.DriverVersion ?? "";
                    string currentVersion;
                    if (!newestVersion.TryGetValue(d.Driver, out currentVersion))
                        newestVersion[d.Driver] = version;
                    else if (!string.Equals(currentVersion, version, StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Each match accepts one entry per driver; old and new versions of the same driver must not be pooled
                    if (!sessionDrivers.Add(d.Driver)) continue;
                    IrqDriverVerdict v;
                    if (!byDriver.TryGetValue(d.Driver, out v))
                    {
                        v = new IrqDriverVerdict { Driver = d.Driver, DriverVersion = version };
                        byDriver[d.Driver] = v;
                        maxima[d.Driver] = new List<double>();
                        dpcTotals[d.Driver] = 0;
                        seconds[d.Driver] = 0;
                    }
                    v.WorstMaxUs = Math.Max(v.WorstMaxUs, d.DpcMaxUs);
                    v.TotalOver500 += d.Over500Us;
                    v.CpuMask |= d.CpuMask;
                    v.MaskTruncated |= d.MaskTruncated;
                    v.P99Us = Math.Max(v.P99Us, d.ApproxPercentileUs(0.99));
                    maxima[d.Driver].Add(d.DpcMaxUs);
                    dpcTotals[d.Driver] += d.Dpc;
                    seconds[d.Driver] += s.DurationSeconds;
                }
            }
            foreach (KeyValuePair<string, IrqDriverVerdict> kv in byDriver)
            {
                IrqDriverVerdict v = kv.Value;
                List<double> m = maxima[kv.Key];
                m.Sort();
                v.MedianMaxUs = m.Count == 0 ? 0 : m[m.Count / 2];
                // Numerator and denominator come only from that driver's same-version records with a positive measured duration
                v.DpcPerMinute = dpcTotals[kv.Key] / (seconds[kv.Key] / 60.0);
                if (strict != null)
                    foreach (IrqDriverVerdict scored in strict)
                    {
                        if (scored == null || !string.Equals(scored.Driver, v.Driver, StringComparison.OrdinalIgnoreCase)
                            || !string.Equals(scored.DriverVersion ?? "", v.DriverVersion, StringComparison.OrdinalIgnoreCase))
                            continue;
                        v.SessionsSeen = scored.SessionsSeen;
                        v.SessionsOverThreshold = scored.SessionsOverThreshold;
                        v.OverlapOver500 = scored.OverlapOver500;
                        v.OverlapWorstMaxUs = scored.OverlapWorstMaxUs;
                        v.OverlapCpuMask = scored.OverlapCpuMask;
                        v.ScoredSeconds = scored.ScoredSeconds;
                        v.Collisions = scored.Collisions;
                        v.StructuralConflict = scored.StructuralConflict;
                        v.Worth = scored.Worth;
                        v.MaskTruncated |= scored.MaskTruncated;
                        break;
                    }
                result.Add(v);
            }
            result.Sort(delegate (IrqDriverVerdict a, IrqDriverVerdict b)
            {
                if (a.Worth != b.Worth) return b.Worth.CompareTo(a.Worth);
                return b.WorstMaxUs.CompareTo(a.WorstMaxUs);
            });
            return result;
        }

        public static string SummarizeSession(IrqSessionRecord rec)
        {
            if (rec == null || rec.Drivers.Count == 0) return null;
            double seconds = rec.DurationSeconds > 0 ? rec.DurationSeconds : 1;
            IrqDriverRecord worst = null;
            double worstScore = -1;
            foreach (IrqDriverRecord d in rec.Drivers)
            {
                if (d == null || d.Dpc <= 0) continue;
                double score = (d.Over500Us / seconds) * d.DpcMaxUs;
                if (score > worstScore || (score == worstScore
                    && (worst == null || d.DpcMaxUs > worst.DpcMaxUs)))
                { worstScore = score; worst = d; }
            }
            if (worst == null) return null;
            return Lang.F("log.irqsession.3", rec.Drivers.Count,
                worst.Driver, worst.DpcMaxUs.ToString("F0"),
                worst.ApproxPercentileUs(0.99).ToString("F0"),
                worst.Over500Us,
                IrqRelocate.MaskText(worst.CpuMask));
        }
    }
}
