// @author bdth 2074055628@qq.com
// 文件用途 按多局真实观测判断某个驱动的中断值不值得挪核
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
        public double Collisions;
        public bool StructuralConflict;
        public bool Worth;
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

        internal static void Score(IrqDriverVerdict v, ulong gameMask, double budgetUs, double seconds)
        {
            v.Worth = false;
            v.StructuralConflict = false;
            v.Collisions = 0;
            if (v.CpuMask == 0 || seconds <= 0) return;
            bool maskTrusted = !v.MaskTruncated;
            if (maskTrusted && gameMask != 0 && (v.CpuMask & gameMask) == 0) return;
            if (v.WorstMaxUs < MinMaxUs) return;

            if (maskTrusted && gameMask != 0
                && IsSingleCore(v.CpuMask) && (v.CpuMask & gameMask) != 0)
            {
                v.StructuralConflict = true;
                double per500PerMin = v.TotalOver500 / (seconds / 60.0);
                v.Collisions = (v.TotalOver500 / seconds) * (v.WorstMaxUs / budgetUs);
                v.Worth = per500PerMin >= MinStructuralOver500PerMin;
                return;
            }

            double perSec = v.TotalOver500 / seconds;
            v.Collisions = perSec * (v.WorstMaxUs / budgetUs);
            v.Worth = v.Collisions >= WorthCollisionsPerSec;
        }

        public static List<IrqDriverVerdict> Evaluate(List<IrqSessionRecord> all,
            ulong gameMask, int hz, out int usedSessions)
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

            foreach (IrqSessionRecord s in window)
                foreach (IrqDriverRecord d in s.Drivers)
                {
                    if (d == null || d.Dpc <= 0) continue;
                    IrqDriverVerdict v;
                    string key = d.Identity;
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
                    if (d.Over500Us > 0) v.SessionsOverThreshold++;
                    if (d.DpcMaxUs > v.WorstMaxUs) v.WorstMaxUs = d.DpcMaxUs;
                    v.TotalOver500 += d.Over500Us;
                    v.CpuMask |= d.CpuMask;
                    v.MaskTruncated |= d.MaskTruncated;
                    maxima[key].Add(d.DpcMaxUs);
                    dpcTotals[key] += d.Dpc;
                    double p99 = d.ApproxPercentileUs(0.99);
                    if (p99 > v.P99Us) v.P99Us = p99;
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
                Score(v, gameMask, budget, seconds);
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
