using System;
using System.Collections.Generic;
using System.Globalization;

namespace PaviseApp
{
    internal sealed class IrqFrameEvidence
    {
        internal int Intervals, LongFrames, AlignmentHits;
        internal double Seconds, P99Ms, P999Ms;
        internal bool IdentityReliable, AlignmentComplete;
        internal string AlignmentModule = "";
        internal bool Valid
        {
            get { return Intervals >= 30 && LongFrames >= 0 && LongFrames <= Intervals
                && AlignmentHits >= 0 && AlignmentHits <= LongFrames
                && Finite(Seconds) && Seconds > 0 && Finite(P99Ms) && P99Ms > 0
                && Finite(P999Ms) && P999Ms >= P99Ms; }
        }
        private static bool Finite(double n) { return !double.IsNaN(n) && !double.IsInfinity(n); }
    }

    internal static partial class IrqSessionLedger
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        private static long Int64(string s) { return long.Parse(s, Inv); }
        private static double Real(string s) { return double.Parse(s, Inv); }
        private static bool Flag(string s)
        { if (s != "0" && s != "1") throw new FormatException(); return s == "1"; }

        private static bool ReadExtended(IrqSessionRecord s, string[] p)
        {
            if (p[0] == "Q" && p.Length == 3 && s.DeviceConfigurations.Count < 2048)
            {
                string id = UnB64(p[1]);
                if (id.Length == 0 || s.DeviceConfigurations.ContainsKey(id)) return false;
                s.DeviceConfigurations.Add(id,UnB64(p[2])); return true;
            }
            if (p[0] == "M" && p.Length == 4 && !s.MetadataRead)
            {
                s.MetadataRead = true; s.GameId = UnB64(p[1]);
                s.Configuration = UnB64(p[2]); s.Scene = UnB64(p[3]); return true;
            }
            if (p[0] == "B" && p.Length == 3)
            {
                var c = s.CoreLoads.Find(delegate(IrqCoreLoadRecord n) { return n.Cpu == int.Parse(p[1], Inv); });
                if (c == null || c.BusyTicks != -1) return false;
                c.BusyTicks = Int64(p[2]); return c.BusyTicks >= 0;
            }
            if (p[0] == "K" && p.Length == 11)
            {
                string driver = UnB64(p[1]);
                var d = s.Drivers.Find(delegate(IrqDriverRecord n) { return n.Driver == driver; });
                if (d == null || d.Cores.Count >= 64) return false;
                bool valid;
                var c = new IrqDriverCoreRecord { Cpu = int.Parse(p[2], Inv), Count = Int64(p[3]),
                    TotalNs = Int64(p[4]), MaxNs = Int64(p[5]), Over500Us = Int64(p[6]),
                    Over1Ms = Int64(p[7]), BadDuration = Int64(p[8]), Buckets = ParseBuckets(p[10], out valid) };
                if (!valid || p[9] != "1") return false;
                d.Cores.Add(c); return true;
            }
            if (p[0] == "F" && p.Length == 10 && s.Frames == null)
            {
                s.Frames = new IrqFrameEvidence { Intervals = int.Parse(p[1], Inv),
                    LongFrames = int.Parse(p[2], Inv), Seconds = Real(p[3]), P99Ms = Real(p[4]),
                    P999Ms = Real(p[5]), IdentityReliable = Flag(p[6]), AlignmentComplete = Flag(p[7]),
                    AlignmentModule = UnB64(p[8]), AlignmentHits = int.Parse(p[9], Inv) };
                return s.Frames.Valid;
            }
            return false;
        }

        private static void WriteExtended(IrqSessionRecord s, List<string> lines)
        {
            if (s.GameId.Length > 0 || s.Configuration.Length > 0 || s.Scene.Length > 0)
                lines.Add("M|" + B64(s.GameId) + "|" + B64(s.Configuration) + "|" + B64(s.Scene));
            foreach (var pair in s.DeviceConfigurations)
                lines.Add("Q|" + B64(pair.Key) + "|" + B64(pair.Value));
            foreach (var c in s.CoreLoads)
                if (c.BusyTicks >= 0) lines.Add("B|" + c.Cpu.ToString(Inv) + "|" + c.BusyTicks.ToString(Inv));
            foreach (var d in s.Drivers)
                foreach (var c in d.Cores)
                    lines.Add(string.Join("|", new[] { "K", B64(d.Driver), c.Cpu.ToString(Inv),
                        c.Count.ToString(Inv), c.TotalNs.ToString(Inv), c.MaxNs.ToString(Inv),
                        c.Over500Us.ToString(Inv), c.Over1Ms.ToString(Inv), c.BadDuration.ToString(Inv),
                        "1", BucketsText(c.Buckets) }));
            var f = s.Frames;
            if (f != null) lines.Add(string.Join("|", new[] { "F", f.Intervals.ToString(Inv),
                f.LongFrames.ToString(Inv), f.Seconds.ToString("R", Inv), f.P99Ms.ToString("R", Inv),
                f.P999Ms.ToString("R", Inv), f.IdentityReliable ? "1" : "0", f.AlignmentComplete ? "1" : "0",
                B64(f.AlignmentModule), f.AlignmentHits.ToString(Inv) }));
        }

        private static bool ValidExtended(IrqSessionRecord s)
        {
            if (s.Frames != null && !s.Frames.Valid) return false;
            foreach (var d in s.Drivers)
            {
                if (d.Cores.Count > 64) return false;
                ulong seen = 0;
                foreach (var c in d.Cores)
                {
                    if (c == null || c.Cpu < 0 || c.Cpu >= 64 || (seen & (1UL << c.Cpu)) != 0
                        || (s.SystemMask & (1UL << c.Cpu)) == 0 || c.Count <= 0 || c.TotalNs < 0
                        || c.MaxNs < 0 || c.MaxNs > c.TotalNs || c.BadDuration < 0 || c.BadDuration > c.Count
                        || c.Over1Ms < 0 || c.Over500Us < c.Over1Ms || c.Over500Us > c.Count
                        || c.Buckets == null || c.Buckets.Length != InterruptAttribution.BucketCount) return false;
                    seen |= 1UL << c.Cpu;
                    long total = c.BadDuration;
                    foreach (long n in c.Buckets)
                    { if (n < 0 || long.MaxValue - total < n) return false; total += n; }
                    if (total != c.Count) return false;
                }
            }
            return true;
        }

    }
}
