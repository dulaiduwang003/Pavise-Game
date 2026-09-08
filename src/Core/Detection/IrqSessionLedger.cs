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

    internal enum IrqSessionExclusion
    {
        None, TooShort, MissingSystemMask, LostEvents, NoDrivers, DifferentBoot, DifferentTopology,
        NoDuration
    }

    internal sealed class IrqSessionRecord
    {
        public long StartUtcTicks;
        public int DurationSeconds;
        public string GameName = "";
        public string BootStamp = "";
        public string TopologyStamp = "";
        public ulong GameMask;
        public ulong SystemMask;
        public long EventsLost;
        public long Unmapped;
        public readonly List<IrqDriverRecord> Drivers = new List<IrqDriverRecord>();
        public long CoreLoadWindowTicks;
        public readonly List<IrqCoreLoadRecord> CoreLoads = new List<IrqCoreLoadRecord>();

        public bool UsableForVerdict
        {
            get { return VerdictExclusion(null, null) == IrqSessionExclusion.None; }
        }

        // 判据与页内解释共享同一条路径 测试可传入固定上下文 不查询真实机器
        internal IrqSessionExclusion VerdictExclusion(string currentBoot, string currentTopology)
        {
            if (DurationSeconds < MinUsableSeconds) return IrqSessionExclusion.TooShort;
            return CommonExclusion(currentBoot, currentTopology);
        }

        // 60 秒是建议样本门槛 不是展示门槛 小于 1 秒的记录仍保留在台账
        // 但 V3 只有整数秒 不能为它伪造一个 1 秒分母来算频率
        internal IrqSessionExclusion DisplayExclusion(string currentBoot, string currentTopology)
        {
            if (DurationSeconds <= 0) return IrqSessionExclusion.NoDuration;
            return CommonExclusion(currentBoot, currentTopology);
        }

        private IrqSessionExclusion CommonExclusion(string currentBoot, string currentTopology)
        {
            if (SystemMask == 0) return IrqSessionExclusion.MissingSystemMask;
            if (EventsLost != 0) return IrqSessionExclusion.LostEvents;
            if (Drivers.Count == 0) return IrqSessionExclusion.NoDrivers;
            // IRQ affinity 修改要重启才生效 不拿重启前的观测继续建议挪核
            if (!IrqAffinityEngine.SameBoot(BootStamp, currentBoot ?? IrqAffinityEngine.BootStamp()))
                return IrqSessionExclusion.DifferentBoot;
            if (!string.IsNullOrEmpty(TopologyStamp)
                && !string.Equals(TopologyStamp, currentTopology ?? CpuTopology.TopologyStamp(),
                    StringComparison.Ordinal))
                return IrqSessionExclusion.DifferentTopology;
            return IrqSessionExclusion.None;
        }

        internal const int MinUsableSeconds = 60;
    }

    internal static class IrqSessionLedger
    {
        internal const string FileName = "Pavise.irq-sessions.dat";
        private const string Header = "PAVISE_IRQ_SESSIONS_V4";
        private const string LegacyHeader = "PAVISE_IRQ_SESSIONS_V3";
        private const string ObsoleteHeader = "PAVISE_IRQ_SESSIONS_V2";
        internal const int KeepSessions = 12;
        internal const int VerdictWindow = 5;
        internal const int MinSessionsForVerdict = 3;

        private static readonly object lk = new object();
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static string dir;
        private static bool readOnlyFormat;

        public static void Bind(string dataDir)
        {
            lock (lk)
            {
                if (!string.Equals(dir, dataDir, StringComparison.OrdinalIgnoreCase))
                    readOnlyFormat = false;
                dir = dataDir;
            }
        }

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
            try { return StrictUtf8.GetString(Convert.FromBase64String(s ?? "")); }
            catch { throw new FormatException("invalid base64 field"); }
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

        private static long[] ParseBuckets(string s, out bool valid)
        {
            valid = true;
            if (string.IsNullOrEmpty(s)) return null;
            string[] parts = s.Split(',');
            var b = new long[parts.Length];
            for (int i = 0; i < parts.Length; i++)
                if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out b[i]))
                {
                    valid = false;
                    return null;
                }
            return b;
        }

        public static List<IrqSessionRecord> Load()
        {
            string issue;
            return Load(out issue);
        }

        public static List<IrqSessionRecord> Load(out string issue)
        {
            lock (lk)
            {
                bool safeToRewrite;
                List<IrqSessionRecord> loaded = LoadLocked(out safeToRewrite, out issue);
                // 只要文件有一处解析不完整 就不能把前半截当成可靠历史参与裁决
                // Append 同样会拒绝覆盖 原文件完整保留给诊断或人工恢复
                return safeToRewrite ? loaded : new List<IrqSessionRecord>();
            }
        }

        private static List<IrqSessionRecord> LoadLocked(out bool safeToRewrite, out string issue)
        {
            safeToRewrite = true;
            issue = "";
            var list = new List<IrqSessionRecord>();
            string path;
            try { path = Path_(); }
            catch { safeToRewrite = false; issue = Lang.T("irq.ledger.readfailed"); return list; }
            if (path == null)
            {
                safeToRewrite = false;
                issue = Lang.T("irq.ledger.unbound");
                return list;
            }
            string[] lines;
            try { lines = File.ReadAllLines(path, StrictUtf8); }
            // File.Exists 会把权限错误也当成不存在 只有明确缺文件才算正常空历史
            catch (FileNotFoundException) { readOnlyFormat = false; return list; }
            catch (DirectoryNotFoundException) { readOnlyFormat = false; return list; }
            catch (DecoderFallbackException)
            { safeToRewrite = false; issue = Lang.T("irq.ledger.corrupt"); return list; }
            catch { safeToRewrite = false; issue = Lang.T("irq.ledger.readfailed"); return list; }
            if (lines.Length == 0)
            { safeToRewrite = false; issue = Lang.T("irq.ledger.corrupt"); return list; }
            string header = lines[0].Trim();
            if (string.Equals(header, ObsoleteHeader, StringComparison.Ordinal))
            {
                // V2 缺少安全裁决所需的逐局游戏掩码 直接判为无效
                // 去猜或者迁移这个掩码 会把老观测变成错误建议
                bool removed = false;
                try { File.Delete(path); removed = !File.Exists(path); } catch { }
                readOnlyFormat = !removed;
                safeToRewrite = removed;
                issue = Lang.T("irq.ledger.obsolete");
                return list;
            }
            bool legacy = string.Equals(header, LegacyHeader, StringComparison.Ordinal);
            if (!legacy && !string.Equals(header, Header, StringComparison.Ordinal))
            {
                readOnlyFormat = true;
                safeToRewrite = false;
                issue = Lang.T("irq.ledger.format");
                return list;
            }
            readOnlyFormat = false;

            IrqSessionRecord cur = null;
            for (int i = 1; i < lines.Length; i++)
            {
                string[] p = lines[i].Split('|');
                if (p.Length < 2) { safeToRewrite = false; continue; }
                // 新会话行即使损坏 也必须先切断上一会话 否则紧随其后的 D 行会被
                // 错接到上一局 会造出一个文件里根本没存在过的有效样本
                if (p[0] == "S") cur = null;
                try
                {
                    if (p[0] == "S" && p.Length == 10)
                    {
                        var next = new IrqSessionRecord();
                        next.StartUtcTicks = long.Parse(p[1], CultureInfo.InvariantCulture);
                        next.DurationSeconds = int.Parse(p[2], CultureInfo.InvariantCulture);
                        next.GameName = UnB64(p[3]);
                        next.BootStamp = p[4];
                        next.TopologyStamp = p[5];
                        next.EventsLost = long.Parse(p[6], CultureInfo.InvariantCulture);
                        next.Unmapped = long.Parse(p[7], CultureInfo.InvariantCulture);
                        next.GameMask = ulong.Parse(p[8], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        next.SystemMask = ulong.Parse(p[9], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        cur = next;
                        list.Add(next);
                    }
                    else if (p[0] == "D" && p.Length == 11 && cur != null)
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
                        if (p[9] != "0" && p[9] != "1")
                            throw new FormatException("invalid MaskTruncated flag");
                        d.MaskTruncated = p[9] == "1";
                        bool bucketsValid;
                        d.Buckets = ParseBuckets(p[10], out bucketsValid);
                        if (!bucketsValid) throw new FormatException("invalid DPC buckets");
                        cur.Drivers.Add(d);
                    }
                    else if (!legacy && p[0] == "L" && p.Length == 2 && cur != null
                        && cur.CoreLoadWindowTicks == 0)
                    {
                        cur.CoreLoadWindowTicks = long.Parse(p[1], CultureInfo.InvariantCulture);
                        if (cur.CoreLoadWindowTicks <= 0) throw new FormatException("invalid load window");
                    }
                    else if (!legacy && p[0] == "C" && p.Length == 5 && cur != null
                        && cur.CoreLoadWindowTicks > 0 && cur.CoreLoads.Count < 64)
                    {
                        var load = new IrqCoreLoadRecord {
                            Cpu = int.Parse(p[1], CultureInfo.InvariantCulture),
                            AveragePercent = double.Parse(p[2], CultureInfo.InvariantCulture),
                            ObservedTicks = long.Parse(p[3], CultureInfo.InvariantCulture),
                            Samples = int.Parse(p[4], CultureInfo.InvariantCulture) };
                        cur.CoreLoads.Add(load);
                        if (!ValidCoreLoads(cur)) throw new FormatException("invalid core load");
                    }
                    else safeToRewrite = false;
                }
                catch { safeToRewrite = false; }
            }
            foreach (IrqSessionRecord record in list)
                if (!ValidCoreLoads(record)) safeToRewrite = false;
            if (!safeToRewrite) issue = Lang.T("irq.ledger.corrupt");
            return list;
        }

        public static bool Append(IrqSessionRecord rec)
        {
            if (rec == null || !ValidCoreLoads(rec)) return false;
            lock (lk)
            {
                string path = Path_();
                if (path == null) return false;

                bool safeToRewrite;
                string issue;
                List<IrqSessionRecord> all = LoadLocked(out safeToRewrite, out issue);
                if (!safeToRewrite || readOnlyFormat) return false;
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
                        s.Unmapped.ToString(CultureInfo.InvariantCulture),
                        s.GameMask.ToString("X", CultureInfo.InvariantCulture),
                        s.SystemMask.ToString("X", CultureInfo.InvariantCulture)
                    }));
                    if (s.CoreLoadWindowTicks > 0)
                    {
                        sb.Add("L|" + s.CoreLoadWindowTicks.ToString(CultureInfo.InvariantCulture));
                        foreach (IrqCoreLoadRecord load in s.CoreLoads)
                            sb.Add(string.Join("|", new[] { "C",
                                load.Cpu.ToString(CultureInfo.InvariantCulture),
                                load.AveragePercent.ToString("R", CultureInfo.InvariantCulture),
                                load.ObservedTicks.ToString(CultureInfo.InvariantCulture),
                                load.Samples.ToString(CultureInfo.InvariantCulture) }));
                    }
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
                return WriteAtomically(path, sb.ToArray());
            }
        }

        private static bool WriteAtomically(string path, string[] lines)
        {
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllLines(temp, lines, Encoding.UTF8);
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                return true;
            }
            catch { return false; }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        internal static bool ValidCoreLoads(IrqSessionRecord record)
        {
            if (record == null || record.CoreLoadWindowTicks < 0 || record.CoreLoads.Count > 64) return false;
            if (record.CoreLoadWindowTicks > 0 && Math.Min(int.MaxValue,
                record.CoreLoadWindowTicks / TimeSpan.TicksPerSecond) != record.DurationSeconds) return false;
            ulong seen = 0;
            foreach (IrqCoreLoadRecord load in record.CoreLoads)
            {
                if (load == null || load.Cpu < 0 || load.Cpu >= 64
                    || (record.SystemMask & (1UL << load.Cpu)) == 0 || (seen & (1UL << load.Cpu)) != 0
                    || double.IsNaN(load.AveragePercent) || double.IsInfinity(load.AveragePercent)
                    || load.AveragePercent < 0 || load.AveragePercent > 100 || load.Samples <= 0
                    || load.ObservedTicks <= 0 || load.ObservedTicks > record.CoreLoadWindowTicks) return false;
                seen |= 1UL << load.Cpu;
            }
            return true;
        }

        public static void Clear()
        {
            lock (lk)
            {
                string path = Path_();
                if (path == null) return;
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    if (!File.Exists(path)) readOnlyFormat = false;
                }
                catch { }
            }
        }
    }
}
