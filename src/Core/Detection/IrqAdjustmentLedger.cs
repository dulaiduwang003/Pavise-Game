using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PaviseApp
{
    internal sealed class IrqComparisonSample
    {
        internal long Start;
        internal int Seconds;
        internal double SlowPerMinute = -1, P99 = -1, P999 = -1, LongPerMinute = -1,
            TargetLoad = -1, LoadCoverage, FrameCoverage;
        internal bool FramesReliable;
        internal bool Valid
        {
            get
            {
                if (Start <= 0 || Seconds < 60 || SlowPerMinute < 0 || TargetLoad < -1 || TargetLoad > 100
                    || P99 < -1 || P999 < P99 || LongPerMinute < -1
                    || LoadCoverage < 0 || LoadCoverage > 1 || FrameCoverage < 0 || FrameCoverage > 1) return false;
                foreach (double value in new[] { SlowPerMinute,P99,P999,LongPerMinute,TargetLoad,LoadCoverage,FrameCoverage })
                    if (double.IsNaN(value) || double.IsInfinity(value)) return false;
                return !FramesReliable || P99 > 0 && P999 > 0 && LongPerMinute >= 0 && FrameCoverage > 0;
            }
        }
    }

    internal sealed class IrqAdjustment
    {
        internal string Id = Guid.NewGuid().ToString("N"), Device = "", Driver = "", DriverVersion = "",
            DeviceVersion = "", GameId = "", Configuration = "", Scene = "", Topology = "", Boot = "",
            Before = "", Phase = "prepared", Placement = "pending", Performance = "insufficient";
        internal string AffinityResult = "unknown", PriorityResult = "unknown", RollbackResult = "unknown",
            RestoreAffinityResult = "unknown", RestorePriorityResult = "unknown";
        internal long WrittenUtc, RestoredUtc;
        internal ulong Target, GameMask;
        internal bool PriorityRequested, NoDeviceWrite;
        internal readonly List<IrqComparisonSample> Baseline = new List<IrqComparisonSample>();
    }

    // Independent write-ahead adjustment files retain the baseline beyond the bounded session ledger
    internal static class IrqAdjustmentLedger
    {
        private static string directory;
        private static readonly object gate = new object();
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        internal static void Bind(string dir) { lock (gate) directory = dir; }
        internal static string ConfigurationOf(PolicySnapshot policy)
        {
            if (policy == null) return "";
            try
            {
            var values = new List<string>();
            foreach (PolicyItem item in PolicyCatalog.All) values.Add(item.Key + "=" + policy.ValueOf(item.Key));
            values.Sort(StringComparer.Ordinal);
            values.Add("topology=" + CpuTopology.TopologyStamp());
            values.Add("refresh=" + DisplayGuard.CurrentRefreshRate());
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", values.ToArray())))).Replace("-", "");
            }
            catch { return ""; }
        }
        internal static string DeviceConfiguration(IrqDevice d)
        { return d.Policy + ":" + d.Mask.ToString("X", Inv) + ":" + d.PriorityValue + ":" + d.DriverVersion; }
        internal static string ExpectedConfiguration(IrqAdjustment a)
        {
            string[] before = a.Before.Split(':');
            int previous;
            if (before.Length < 4 || !int.TryParse(before[2],out previous)) return "";
            int priority = a.PriorityRequested ? 3 : previous;
            return "4:" + a.Target.ToString("X",Inv) + ":" + priority + ":" + a.DeviceVersion;
        }

        internal static IrqAdjustment Prepare(IrqDevice d, IrqPinSession view, ulong target,
            bool priority, IList<IrqSessionRecord> sessions)
        {
            var a = new IrqAdjustment { Device = d.InstanceId, DeviceVersion = d.DriverVersion,
                Driver = view.Driver == null ? "" : view.Driver.Driver,
                DriverVersion = view.Driver == null ? "" : view.Driver.DriverVersion,
                Target = target, PriorityRequested = priority, Before = DeviceConfiguration(d),
                WrittenUtc = DateTime.UtcNow.Ticks, Boot = IrqAffinityEngine.BootStamp(),
                Topology = CpuTopology.TopologyStamp() };
            var selected = view.Record;
            if (selected != null)
            {
                a.GameId = selected.GameId; a.Configuration = selected.Configuration;
                a.GameMask = selected.GameMask;
                var seen = new HashSet<long>();
                if (!d.SharedStats && !d.FrameworkStats && sessions != null)
                    foreach (var s in sessions)
                    {
                        string reason; var sample = Sample(a, s, out reason);
                        if (sample != null && s.StartUtcTicks < a.WrittenUtc && seen.Add(s.StartUtcTicks)) a.Baseline.Add(sample);
                    }
            }
            return a;
        }

        internal static IrqComparisonSample Sample(IrqAdjustment a, IrqSessionRecord s, out string reason)
        {
            reason = "";
            if (s == null || s.DurationSeconds < 60 || s.EventsLost != 0 || s.Unmapped != 0)
            { reason = "sample"; return null; }
            string actual;
            string expected = s.StartUtcTicks > a.WrittenUtc ? ExpectedConfiguration(a) : a.Before;
            if (!s.DeviceConfigurations.TryGetValue(a.Device,out actual) || actual != expected)
            { reason = "deviceconfig"; return null; }
            if (a.GameId.Length == 0 || s.GameId != a.GameId) { reason = "game"; return null; }
            if (a.Configuration.Length == 0 || s.Configuration != a.Configuration
                || a.Topology.Length == 0 || s.TopologyStamp != a.Topology || s.GameMask != a.GameMask)
            { reason = "config"; return null; }
            IrqDriverRecord driver = null;
            foreach (var d in s.Drivers)
                if (string.Equals(d.Driver, a.Driver, StringComparison.OrdinalIgnoreCase))
                { if (driver != null) { reason = "driver"; return null; } driver = d; }
            if (driver == null || a.DriverVersion.Length == 0 || a.DeviceVersion.Length == 0 || driver.DriverVersion != a.DriverVersion)
            { reason = "driver"; return null; }
            if (s.GameMask == 0 || s.GameMask == s.SystemMask || (s.GameMask & ~s.SystemMask) != 0
                || !driver.ValidCores(s.SystemMask)) { reason = "cores"; return null; }
            var game = driver.OnCores(s.GameMask);
            var r = new IrqComparisonSample { Start = s.StartUtcTicks, Seconds = s.DurationSeconds,
                SlowPerMinute = game.Over500Us * 60.0 / s.DurationSeconds };
            ulong loaded = 0; double sum = 0, coverage = 1; int count = 0;
            if (s.CoreLoadWindowTicks > 0 && IrqSessionLedger.ValidCoreLoads(s))
                foreach (var c in s.CoreLoads)
                    if ((a.Target & (1UL << c.Cpu)) != 0)
                    {
                        loaded |= 1UL << c.Cpu; sum += c.AveragePercent; count++;
                        coverage = Math.Min(coverage, c.ObservedTicks / (double)s.CoreLoadWindowTicks);
                    }
            if (loaded == a.Target && count > 0) { r.TargetLoad = sum / count; r.LoadCoverage = coverage; }
            var f = s.Frames;
            if (f != null && f.Valid)
            {
                r.P99 = f.P99Ms; r.P999 = f.P999Ms; r.LongPerMinute = f.LongFrames * 60.0 / f.Seconds;
                r.FrameCoverage = Math.Min(1, f.Seconds / s.DurationSeconds); r.FramesReliable = f.IdentityReliable;
            }
            return r;
        }

        internal static bool Save(IrqAdjustment a)
        {
            lock (gate)
            {
                if (string.IsNullOrEmpty(directory) || a == null) return false;
                Guid validId;
                if (!Guid.TryParseExact(a.Id,"N",out validId)) return false;
                foreach (var sample in a.Baseline) if (sample == null || !sample.Valid) return false;
                string path = Path.Combine(directory, "irq-adjustment-" + a.Id + ".xml");
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var w = XmlWriter.Create(temp, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
                    {
                        w.WriteStartElement("irq-adjustment"); w.WriteAttributeString("version", "1");
                        Field(w,"id",a.Id); Field(w,"device",a.Device); Field(w,"driver",a.Driver);
                        Field(w,"driverVersion",a.DriverVersion); Field(w,"deviceVersion",a.DeviceVersion);
                        Field(w,"game",a.GameId); Field(w,"configuration",a.Configuration); Field(w,"scene",a.Scene);
                        Field(w,"topology",a.Topology); Field(w,"boot",a.Boot); Field(w,"before",a.Before);
                        Field(w,"affinityResult",a.AffinityResult); Field(w,"priorityResult",a.PriorityResult);
                        Field(w,"rollbackResult",a.RollbackResult); Field(w,"restoreAffinity",a.RestoreAffinityResult);
                        Field(w,"restorePriority",a.RestorePriorityResult);
                        Field(w,"phase",a.Phase); Field(w,"placement",a.Placement); Field(w,"performance",a.Performance);
                        Field(w,"written",a.WrittenUtc); Field(w,"restored",a.RestoredUtc);
                        Field(w,"target",a.Target); Field(w,"gameMask",a.GameMask); Field(w,"priority",a.PriorityRequested ? 1 : 0);
                        Field(w,"noDeviceWrite",a.NoDeviceWrite ? 1 : 0);
                        w.WriteStartElement("baseline");
                        foreach (var s in a.Baseline)
                        {
                            w.WriteStartElement("sample"); Field(w,"start",s.Start); Field(w,"seconds",s.Seconds);
                            Field(w,"slow",s.SlowPerMinute); Field(w,"p99",s.P99); Field(w,"p999",s.P999);
                            Field(w,"long",s.LongPerMinute); Field(w,"load",s.TargetLoad);
                            Field(w,"loadCoverage",s.LoadCoverage); Field(w,"frameCoverage",s.FrameCoverage);
                            Field(w,"reliable",s.FramesReliable ? 1 : 0); w.WriteEndElement();
                        }
                        w.WriteEndElement(); w.WriteEndElement();
                    }
                    if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                    return true;
                }
                catch { return false; }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }
        private static void Field(XmlWriter w, string name, object value)
        { w.WriteElementString(name, Convert.ToString(value, Inv) ?? ""); }
        private static string Optional(XmlNode n,string name)
        { var value = n.SelectSingleNode(name); return value == null ? "unknown" : value.InnerText; }
        private static string Read(XmlNode n, string name)
        { var value = n.SelectSingleNode(name); if (value == null) throw new FormatException(name); return value.InnerText; }

        internal static List<IrqAdjustment> Load(out string issue)
        {
            var result = new List<IrqAdjustment>(); issue = "";
            lock (gate)
            {
                if (string.IsNullOrEmpty(directory)) { issue = Lang.T("irq.ledger.unbound"); return result; }
                try
                {
                    if (!Directory.Exists(directory)) return result;
                    foreach (string path in Directory.GetFiles(directory, "irq-adjustment-*.xml"))
                    {
                        var doc = new XmlDocument { XmlResolver = null };
                        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null })) doc.Load(reader);
                        XmlNode n = doc.DocumentElement;
                        if (n.Name != "irq-adjustment" || n.Attributes["version"].Value != "1") throw new FormatException();
                        var a = new IrqAdjustment { Id = Read(n,"id"), Device = Read(n,"device"), Driver = Read(n,"driver"),
                            DriverVersion = Read(n,"driverVersion"), DeviceVersion = Read(n,"deviceVersion"),
                            GameId = Read(n,"game"), Configuration = Read(n,"configuration"), Scene = Read(n,"scene"),
                            Topology = Read(n,"topology"), Boot = Read(n,"boot"), Before = Read(n,"before"),
                            Phase = Read(n,"phase"), Placement = Read(n,"placement"), Performance = Read(n,"performance"),
                            WrittenUtc = long.Parse(Read(n,"written"),Inv), RestoredUtc = long.Parse(Read(n,"restored"),Inv),
                            Target = ulong.Parse(Read(n,"target"),Inv), GameMask = ulong.Parse(Read(n,"gameMask"),Inv),
                            PriorityRequested = Read(n,"priority") == "1" };
                        a.AffinityResult = Optional(n,"affinityResult"); a.PriorityResult = Optional(n,"priorityResult");
                        a.RollbackResult = Optional(n,"rollbackResult"); a.RestoreAffinityResult = Optional(n,"restoreAffinity");
                        a.RestorePriorityResult = Optional(n,"restorePriority");
                        a.NoDeviceWrite = Optional(n,"noDeviceWrite") == "1";
                        Guid id; if (!Guid.TryParseExact(a.Id,"N",out id) || a.Target == 0 || a.WrittenUtc <= 0) throw new FormatException();
                        foreach (XmlNode b in n.SelectNodes("baseline/sample"))
                            a.Baseline.Add(new IrqComparisonSample { Start = long.Parse(Read(b,"start"),Inv),
                                Seconds = int.Parse(Read(b,"seconds"),Inv), SlowPerMinute = double.Parse(Read(b,"slow"),Inv),
                                P99 = double.Parse(Read(b,"p99"),Inv), P999 = double.Parse(Read(b,"p999"),Inv),
                                LongPerMinute = double.Parse(Read(b,"long"),Inv), TargetLoad = double.Parse(Read(b,"load"),Inv),
                                LoadCoverage = double.Parse(Read(b,"loadCoverage"),Inv), FrameCoverage = double.Parse(Read(b,"frameCoverage"),Inv),
                                FramesReliable = Read(b,"reliable") == "1" });
                        var seen = new HashSet<long>();
                        foreach (var sample in a.Baseline)
                            if (!sample.Valid || !seen.Add(sample.Start)) throw new FormatException("invalid baseline");
                        result.Add(a);
                    }
                }
                catch { issue = Lang.T("irq.adjust.readfailed"); return new List<IrqAdjustment>(); }
            }
            result.Sort(delegate(IrqAdjustment a, IrqAdjustment b) { return a.WrittenUtc.CompareTo(b.WrittenUtc); });
            return result;
        }
    }
}
