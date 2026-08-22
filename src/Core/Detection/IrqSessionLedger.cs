// @author bdth 2074055628@qq.com
// 文件用途 保存最近若干局的中断观测 供中断页按真实数据给出建议
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PaviseApp
{
    internal sealed class IrqDriverRecord
    {
        public string Driver = "";
        public string DriverVersion = "";
        public long Dpc;
        public long DpcTotalNs;
        public long DpcMaxNs;
        public long Over500Us;
        public long Over1Ms;
        public ulong CpuMask;
        public bool MaskTruncated;
        public long[] Buckets;

        public double DpcTotalUs { get { return DpcTotalNs / 1000.0; } }
        public double DpcMaxUs { get { return DpcMaxNs / 1000.0; } }

        public string Identity
        {
            get { return DriverVersion.Length == 0 ? Driver : Driver + "@" + DriverVersion; }
        }

        public double ApproxPercentileUs(double q)
        {
            return IrqPercentile.ApproxUs(Buckets, DpcMaxUs, q);
        }
    }

    internal sealed class IrqSessionRecord
    {
        public long StartUtcTicks;
        public int DurationSeconds;
        public string GameName = "";
        public string BootStamp = "";
        public string TopologyStamp = "";
        public long EventsLost;
        public long Unmapped;
        public readonly List<IrqDriverRecord> Drivers = new List<IrqDriverRecord>();

        public bool UsableForVerdict
        {
            get
            {
                if (DurationSeconds < MinUsableSeconds) return false;
                if (EventsLost != 0) return false;
                if (Drivers.Count == 0) return false;
                if (TopologyStamp.Length != 0
                    && !string.Equals(TopologyStamp, CpuTopology.TopologyStamp(), StringComparison.Ordinal))
                    return false;
                return true;
            }
        }

        internal const int MinUsableSeconds = 60;
    }

    internal static class IrqSessionLedger
    {
        internal const string FileName = "Pavise.irq-sessions.dat";
        private const string Header = "PAVISE_IRQ_SESSIONS_V2";
        internal const int KeepSessions = 12;
        internal const int VerdictWindow = 5;
        internal const int MinSessionsForVerdict = 3;

        private static readonly object lk = new object();
        private static string dir;
        private static bool readOnlyFormat;

        public static void Bind(string dataDir) { lock (lk) dir = dataDir; }

        private static string Path_()
        {
            string d = dir;
            return string.IsNullOrEmpty(d) ? null : System.IO.Path.Combine(d, FileName);
        }

        private static string B64(string s)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(s ?? ""));
        }

        private static string UnB64(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s ?? "")); }
            catch { return ""; }
        }

        private static string BucketsText(long[] b)
        {
            if (b == null || b.Length == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(b[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static long[] ParseBuckets(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            string[] parts = s.Split(',');
            var b = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out b[i]))
                    return null;
            return b;
        }

        public static List<IrqSessionRecord> Load()
        {
            var list = new List<IrqSessionRecord>();
            string path = Path_();
            if (path == null || !File.Exists(path)) return list;
            string[] lines;
            try { lines = File.ReadAllLines(path, Encoding.UTF8); }
            catch { return list; }
            if (lines.Length == 0) return list;
            if (!string.Equals(lines[0].Trim(), Header, StringComparison.Ordinal))
            {
                lock (lk) readOnlyFormat = true;
                return list;
            }
            lock (lk) readOnlyFormat = false;

            IrqSessionRecord cur = null;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] p = lines[i].Split('|');
                if (p.Length < 2) continue;
                try
                {
                    if (p[0] == "S" && p.Length >= 8)
                    {
                        cur = new IrqSessionRecord();
                        cur.StartUtcTicks = long.Parse(p[1], CultureInfo.InvariantCulture);
                        cur.DurationSeconds = int.Parse(p[2], CultureInfo.InvariantCulture);
                        cur.GameName = UnB64(p[3]);
                        cur.BootStamp = p[4];
                        cur.TopologyStamp = p[5];
                        cur.EventsLost = long.Parse(p[6], CultureInfo.InvariantCulture);
                        cur.Unmapped = long.Parse(p[7], CultureInfo.InvariantCulture);
                        list.Add(cur);
                    }
                    else if (p[0] == "D" && p.Length >= 11 && cur != null)
                    {
                        var d = new IrqDriverRecord();
                        d.Driver = UnB64(p[1]);
                        d.DriverVersion = UnB64(p[2]);
                        d.Dpc = long.Parse(p[3], CultureInfo.InvariantCulture);
                        d.DpcTotalNs = long.Parse(p[4], CultureInfo.InvariantCulture);
                        d.DpcMaxNs = long.Parse(p[5], CultureInfo.InvariantCulture);
                        d.Over500Us = long.Parse(p[6], CultureInfo.InvariantCulture);
                        d.Over1Ms = long.Parse(p[7], CultureInfo.InvariantCulture);
                        d.CpuMask = ulong.Parse(p[8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        d.MaskTruncated = p[9] == "1";
                        d.Buckets = ParseBuckets(p[10]);
                        cur.Drivers.Add(d);
                    }
                }
                catch { }
            }
            return list;
        }

        public static bool Append(IrqSessionRecord rec)
        {
            if (rec == null) return false;
            string path = Path_();
            if (path == null) return false;
            lock (lk) { if (readOnlyFormat) return false; }

            List<IrqSessionRecord> all = Load();
            lock (lk) { if (readOnlyFormat) return false; }
            all.Add(rec);
            while (all.Count > KeepSessions) all.RemoveAt(0);

            var sb = new List<string>();
            sb.Add(Header);
            foreach (IrqSessionRecord s in all)
            {
                sb.Add(string.Join("|", new[]
                {
                    "S",
                    s.StartUtcTicks.ToString(CultureInfo.InvariantCulture),
                    s.DurationSeconds.ToString(CultureInfo.InvariantCulture),
                    B64(s.GameName),
                    s.BootStamp ?? "",
                    s.TopologyStamp ?? "",
                    s.EventsLost.ToString(CultureInfo.InvariantCulture),
                    s.Unmapped.ToString(CultureInfo.InvariantCulture)
                }));
                foreach (IrqDriverRecord d in s.Drivers)
                    sb.Add(string.Join("|", new[]
                    {
                        "D",
                        B64(d.Driver),
                        B64(d.DriverVersion),
                        d.Dpc.ToString(CultureInfo.InvariantCulture),
                        d.DpcTotalNs.ToString(CultureInfo.InvariantCulture),
                        d.DpcMaxNs.ToString(CultureInfo.InvariantCulture),
                        d.Over500Us.ToString(CultureInfo.InvariantCulture),
                        d.Over1Ms.ToString(CultureInfo.InvariantCulture),
                        d.CpuMask.ToString("X", CultureInfo.InvariantCulture),
                        d.MaskTruncated ? "1" : "0",
                        BucketsText(d.Buckets)
                    }));
            }
            try
            {
                File.WriteAllLines(path, sb.ToArray(), Encoding.UTF8);
                return true;
            }
            catch { return false; }
        }

        public static void Clear()
        {
            string path = Path_();
            if (path == null) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
