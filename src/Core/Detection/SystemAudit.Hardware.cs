// @author bdth 2074055628@qq.com
// 文件用途 系统体检硬件健康分部 内存模块 灯效常驻 降频事件 机械盘寻道检测
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        private static void BuildHardwareHealth(AuditReport report, Facts facts)
        {
            bool channelWarn;
            string channelText = MemChannelText(facts.MemModuleGb, out channelWarn);
            if (channelText != null)
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.memchannel.1"),
                    Value = channelText,
                    Note = channelWarn ? Lang.T("t.memchannel.6") : Lang.T("t.memchannel.7"),
                    Evidence = EvMechanism,
                    Warn = channelWarn
                });
            if (facts.MemModules > 0)
            {
                bool xmpSuspect = facts.MemConfiguredMhz > 0 && facts.MemRatedMhz > 0
                    && facts.MemConfiguredMhz * 10 < facts.MemRatedMhz * 9;
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.1"),
                    Value = facts.MemConfiguredMhz > 0
                        ? facts.MemConfiguredMhz + " MHz"
                            + (facts.MemRatedMhz > facts.MemConfiguredMhz ? Lang.T("t.systemaudithardware.2") + facts.MemRatedMhz : "")
                        : Lang.T("t.systemaudithardware.3"),
                    Note = xmpSuspect
                        ? Lang.T("t.systemaudithardware.4")
                        : Lang.T("t.systemaudithardware.5"),
                    Evidence = xmpSuspect ? EvMeasuredBench : EvMechanism,
                    Warn = xmpSuspect
                });
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.6"),
                    Value = facts.MemModules + Lang.T("t.systemaudithardware.7"),
                    Note = facts.MemModules == 1
                        ? Lang.T("t.systemaudithardware.8")
                        : Lang.T("t.systemaudithardware.9"),
                    Evidence = EvMechanism,
                    Warn = facts.MemModules == 1
                });
            }

            if (facts.AllHz != null && facts.AllHz.Count > 1)
            {
                int min = int.MaxValue, max = 0;
                var parts = new List<string>();
                foreach (int hz in facts.AllHz)
                {
                    if (hz < min) min = hz;
                    if (hz > max) max = hz;
                    parts.Add(hz + "Hz");
                }
                bool mixed = max - min > 5;
                bool oldOs = facts.OsBuild > 0 && facts.OsBuild < 26100;
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.10"),
                    Value = string.Join(" / ", parts.ToArray()),
                    Note = mixed && oldOs
                        ? Lang.T("t.systemaudithardware.11")
                        : mixed ? Lang.T("t.systemaudithardware.12") : Lang.T("t.systemaudithardware.13"),
                    Evidence = EvMechanism,
                    Warn = mixed && oldOs
                });
            }

            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudithardware.14"),
                Value = facts.ThrottleEvents7d > 0 ? Lang.T("t.systemaudithardware.15") + facts.ThrottleEvents7d + Lang.T("t.gputhrottleprobe.5") : Lang.T("t.systemaudithardware.16"),
                Note = facts.ThrottleEvents7d > 0
                    ? Lang.T("t.systemaudithardware.17")
                    : Lang.T("t.systemaudithardware.18"),
                Evidence = EvMeasuredLocal,
                Warn = facts.ThrottleEvents7d > 0
            });

            if (facts.RgbSuites != null && facts.RgbSuites.Count > 0)
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.19"),
                    Value = string.Join(" ", facts.RgbSuites.ToArray()),
                    Evidence = EvMechanism,
                    Warn = false
                });

            if (facts.RyzenCpu && facts.OsBuild > 0)
            {
                bool old = facts.OsBuild < 26100;
                report.Persistent.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.21"),
                    Value = old ? Lang.T("t.systemaudithardware.22") : Lang.T("t.systemaudithardware.23"),
                    Note = old
                        ? Lang.T("t.systemaudithardware.24")
                        : Lang.T("t.systemaudithardware.25"),
                    Evidence = EvMeasuredBench,
                    Warn = old
                });
            }

            if (facts.OsBuild >= 22000)
                report.Persistent.Add(new AuditRow
                {
                    Name = Lang.T("set.windowedopt"),
                    Value = facts.WindowedOptOn ? Lang.T("log.versionmigrations.41") : Lang.T("gs.noff"),
                    Note = facts.WindowedOptOn
                        ? Lang.T("t.systemaudithardware.26")
                        : Lang.T("t.systemaudithardware.27"),
                    Evidence = EvMechanism,
                    Warn = !facts.WindowedOptOn
                });

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudithardware.28"),
                Value = facts.MsiOffCount > 0 ? facts.MsiOffCount + Lang.T("t.systemaudithardware.29") : Lang.T("t.systemaudithardware.30"),
                Note = facts.MsiOffCount > 0
                    ? Lang.T("t.systemaudithardware.31")
                    : Lang.T("t.systemaudithardware.32"),
                Evidence = EvMechanism,
                Warn = facts.MsiOffCount > 0,
                FixKey = "msi"
            });

            if (CpuTopology.AsymCache) report.Capability.Add(X3dOptimizerRow());
        }

        private const string X3dServiceKey = @"SYSTEM\CurrentControlSet\Services\amd3dvcache";

        internal static bool X3dOptimizerPresent()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(X3dServiceKey))
                    return k != null;
            }
            catch { return false; }
        }

        private static AuditRow X3dOptimizerRow()
        {
            bool driver = X3dOptimizerPresent();
            bool gameMode = false;
            try { gameMode = GameModeGuard.CurrentlyOn(); } catch { }
            return new AuditRow
            {
                Name = Lang.T("t.systemaudithardware.33"),
                Value = driver
                    ? Lang.T(gameMode ? "t.systemaudithardware.34" : "t.systemaudithardware.35")
                    : Lang.T("t.systemaudithardware.36"),
                Note = driver
                    ? Lang.T(gameMode ? "t.systemaudithardware.37" : "t.systemaudithardware.38")
                    : Lang.T("t.systemaudithardware.39"),
                Evidence = EvMechanism,
                Warn = driver && !gameMode
            };
        }

        private static void GatherMemoryModules(Facts f)
        {
            using (var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT Speed, ConfiguredClockSpeed, Capacity FROM Win32_PhysicalMemory"))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(10);
                f.MemModuleGb = new List<double>();
                using (var results = searcher.Get())
                    foreach (System.Management.ManagementBaseObject mo in results)
                        using (mo)
                        {
                            f.MemModules++;
                            int rated = ToMhz(mo["Speed"]);
                            int cfg = ToMhz(mo["ConfiguredClockSpeed"]);
                            if (rated > f.MemRatedMhz) f.MemRatedMhz = rated;
                            if (cfg > f.MemConfiguredMhz) f.MemConfiguredMhz = cfg;
                            try
                            {
                                object cap = mo["Capacity"];
                                if (cap != null)
                                    f.MemModuleGb.Add(Convert.ToUInt64(cap) / 1073741824.0);
                            }
                            catch { }
                        }
            }
        }

        internal static string MemChannelText(List<double> moduleGb, out bool warn)
        {
            warn = false;
            if (moduleGb == null || moduleGb.Count == 0) return null;
            var sizes = new List<string>();
            bool uniform = true;
            foreach (double gb in moduleGb)
            {
                sizes.Add(gb.ToString("F0"));
                if (Math.Abs(gb - moduleGb[0]) > 0.5) uniform = false;
            }
            if (moduleGb.Count == 1)
            {
                warn = true;
                return moduleGb[0].ToString("F0") + "GB " + Lang.T("t.memchannel.2");
            }
            string layout = string.Join("+", sizes.ToArray()) + "GB ";
            if (uniform) return layout + Lang.T("t.memchannel.3");
            if (moduleGb.Count == 2)
            {
                double dual = Math.Min(moduleGb[0], moduleGb[1]) * 2;
                double single = Math.Abs(moduleGb[0] - moduleGb[1]);
                return layout + Lang.F("t.memchannel.4", dual.ToString("F0"), single.ToString("F0"));
            }
            return layout + Lang.T("t.memchannel.5");
        }

        private static int ToMhz(object value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value); }
            catch { return 0; }
        }

        private static List<string> ScanRgbSuites()
        {
            var hits = new List<string>();
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    string n = p.ProcessName.ToLowerInvariant();
                    string label = null;
                    if (n.Contains("icue")) label = "Corsair iCUE";
                    else if (n.Contains("lightingservice")) label = "ASUS Aura";
                    else if (n.Contains("armourycrate") || n.Contains("armouryswagent")) label = "Armoury Crate";
                    else if (n.Contains("razerappengine") || n.Contains("rzsynapse")
                        || n.Contains("razer synapse")) label = "Razer Synapse";
                    else if (n.Contains("mysticlight")) label = "MSI Mystic Light";
                    else if (n.Contains("signalrgb")) label = "SignalRGB";
                    if (label != null && !hits.Contains(label)) hits.Add(label);
                }
                catch { }
                finally { try { p.Dispose(); } catch { } }
            }
            return hits;
        }

        private static int CountCpuThrottleEvents()
        {
            string q = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power']"
                + " and (EventID=37) and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
            var query = new System.Diagnostics.Eventing.Reader.EventLogQuery(
                "System", System.Diagnostics.Eventing.Reader.PathType.LogName, q);
            int n = 0;
            using (var reader = new System.Diagnostics.Eventing.Reader.EventLogReader(query))
                for (System.Diagnostics.Eventing.Reader.EventRecord r = reader.ReadEvent();
                    r != null && n < 200; r = reader.ReadEvent())
                    using (r) n++;
            return n;
        }

    }
}
