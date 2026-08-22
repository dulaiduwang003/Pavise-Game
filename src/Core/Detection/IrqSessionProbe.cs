// @author bdth 2074055628@qq.com
// 文件用途 对局期常驻中断观测 打完一局落盘一段 供中断页按真实数据给建议
using System;

namespace PaviseApp
{
    internal sealed class IrqSessionProbe : IDisposable
    {
        internal const string EnabledKey = "IrqSessionProbe";

        private readonly object gate = new object();
        private InterruptAttribution live;
        private long startTicks;
        private string gameName = "";
        private bool completed = true;
        private string pendingSummary;

        public static bool EnabledSetting
        {
            get { return Settings.Load(EnabledKey, false); }
            set { Settings.Save(EnabledKey, value); }
        }

        private bool warnedNoAdmin;

        public void Begin(string game)
        {
            lock (gate) pendingSummary = null;
            if (!EnabledSetting) return;
            if (!Native.IsElevated())
            {
                if (!warnedNoAdmin) { warnedNoAdmin = true; Logger.Log(Lang.T("log.irqsession.4")); }
                return;
            }
            lock (gate)
            {
                if (live != null) return;
                var ia = new InterruptAttribution();
                if (!ia.Start())
                {
                    if (!ia.Busy) Logger.Log(Lang.T("log.irqsession.1"));
                    return;
                }
                live = ia;
                gameName = game ?? "";
                startTicks = DateTime.UtcNow.Ticks;
                completed = false;
                Logger.Log(Lang.T("log.irqsession.2"));
            }
        }

        public void SampleIfDue() { }

        public void CompleteIfRunning() { Run(); }

        public string TakeSummary()
        {
            Run();
            lock (gate)
            {
                string held = pendingSummary;
                pendingSummary = null;
                return held;
            }
        }

        private string Run()
        {
            InterruptAttribution ia;
            long began;
            string game;
            lock (gate)
            {
                if (live == null || completed) return null;
                completed = true;
                ia = live;
                live = null;
                began = startTicks;
                game = gameName;
            }

            InterruptAttributionResult raw;
            try { raw = ia.Stop(); }
            catch { return null; }
            if (raw == null || raw.Drivers == null || raw.Drivers.Count == 0) return null;
            if (raw.Lossy) return null;

            var rec = new IrqSessionRecord();
            rec.StartUtcTicks = began;
            rec.DurationSeconds = (int)Math.Max(0,
                (DateTime.UtcNow.Ticks - began) / TimeSpan.TicksPerSecond);
            rec.GameName = game;
            rec.BootStamp = IrqAffinityEngine.BootStamp();
            rec.TopologyStamp = CpuTopology.TopologyStamp();
            foreach (DriverInterrupt d in raw.Drivers)
            {
                if (d == null || d.Dpc <= 0) continue;
                var r = new IrqDriverRecord();
                r.Driver = d.Driver ?? "?";
                r.DriverVersion = DriverVersionOf(r.Driver);
                r.Buckets = d.DpcBuckets;
                r.Dpc = d.Dpc;
                r.DpcTotalNs = (long)(d.DpcTotalUs * 1000.0);
                r.DpcMaxNs = (long)(d.DpcMaxUs * 1000.0);
                r.Over500Us = d.DpcOver500Us;
                r.Over1Ms = d.DpcOver1Ms;
                r.CpuMask = d.CpuMask;
                r.MaskTruncated = d.CpuMaskTruncated;
                rec.Drivers.Add(r);
            }
            if (rec.Drivers.Count == 0) return null;
            IrqSessionLedger.Append(rec);
            string summary = IrqVerdict.SummarizeSession(rec);
            lock (gate) pendingSummary = summary;
            return summary;
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> versionCache =
            new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static string DriverVersionOf(string moduleName)
        {
            if (string.IsNullOrEmpty(moduleName)) return "";
            lock (versionCache)
            {
                string got;
                if (versionCache.TryGetValue(moduleName, out got)) return got;
            }
            string v = "";
            try
            {
                string sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
                string path = System.IO.Path.Combine(System.IO.Path.Combine(sys, "drivers"), moduleName);
                if (!System.IO.File.Exists(path)) path = System.IO.Path.Combine(sys, moduleName);
                if (System.IO.File.Exists(path))
                {
                    var fi = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                    string ver = fi.FileVersion ?? "";
                    long stamp = System.IO.File.GetLastWriteTimeUtc(path).Ticks / TimeSpan.TicksPerSecond;
                    v = ver.Trim() + "#" + stamp.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { v = ""; }
            lock (versionCache) versionCache[moduleName] = v;
            return v;
        }

        public void Dispose()
        {
            try { Run(); } catch { }
            lock (gate)
            {
                InterruptAttribution ia = live;
                live = null;
                if (ia != null) try { ia.Stop(); } catch { }
            }
        }
    }
}
