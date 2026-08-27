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
            // 挪核建议必须知道游戏核与完整落核掩码，并且同一驱动要在多局都实际越线。
            // 全局凑够三局不等于这个驱动也稳定复现；一局偶发尖峰不能升级成建议。
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

            // window is newest-first. Once a driver version has appeared, older versions
            // of that same module must not inherit or contribute a recommendation after an update.
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
                    // A valid probe writes one record per driver. Ignore duplicate records in
                    // a damaged ledger so one match cannot impersonate multiple sessions.
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
                    // The ledger has one aggregate latency distribution plus a CPU mask;
                    // it cannot prove which CPU produced a slow DPC. Score a session only
                    // when every observed CPU for this driver was inside the game's mask.
                    bool provenOnGameCores = s.GameMask != 0
                        && s.SystemMask != 0 && s.GameMask != s.SystemMask
                        && (s.GameMask & ~s.SystemMask) == 0 && d.CpuMask != 0
                        && (d.CpuMask & ~s.GameMask) == 0;
                    if (provenOnGameCores)
                    {
                        if (d.Over500Us > 0) v.SessionsOverThreshold++;
                        v.OverlapOver500 += d.Over500Us;
                        if (d.DpcMaxUs > v.OverlapWorstMaxUs)
                            v.OverlapWorstMaxUs = d.DpcMaxUs;
                        v.OverlapCpuMask |= d.CpuMask;
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
