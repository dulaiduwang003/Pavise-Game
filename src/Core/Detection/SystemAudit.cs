// @author bdth 2074055628@qq.com
// 文件用途 系统体检 聚合本机能力 实测数据与持久设置 输出带依据等级的结论清单

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal sealed class AuditRow
    {
        public string Name;
        public string Value;
        public string Note;
        public string Evidence;
        public bool Warn;
        public string FixKey;
    }

    internal sealed class AuditReport
    {
        public readonly List<AuditRow> Capability = new List<AuditRow>();
        public readonly List<AuditRow> Machine = new List<AuditRow>();
        public readonly List<AuditRow> Persistent = new List<AuditRow>();
        public readonly List<AuditRow> Verdicts = new List<AuditRow>();
        public bool MeasureOk;
        public int MeasureWindowMs;
    }

    internal static partial class SystemAudit
    {
        public const string EvMeasuredLocal = "本机实测";
        public const string EvMeasuredBench = "台架实测";
        public const string EvMechanism = "机制明确";
        public const string EvUnverified = "未验证";

        public static int InterruptTier(double worstRate)
        {
            if (worstRate < 0.01) return 0;
            if (worstRate < 0.05) return 1;
            return 2;
        }

        public static string InterruptTierText(int tier)
        {
            if (tier == 0) return Lang.T("t.systemaudit.1");
            return tier == 1 ? Lang.T("t.systemaudithardware.30") : Lang.T("t.systemaudit.2");
        }

        public static string PercentText(double rate)
        {
            return (rate * 100.0).ToString("F2") + "%";
        }

        private static string CoreLabel(ulong mask)
        {
            var parts = new List<string>();
            for (int i = 0; i < 64; i++) if (((mask >> i) & 1UL) != 0) parts.Add(i.ToString());
            return string.Join(" ", parts.ToArray());
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

        public const byte EnergySaverOn = 1;

        public static bool TryEnergySaver(out bool on)
        {
            on = false;
            try
            {
                SystemPowerStatus status;
                if (!GetSystemPowerStatus(out status)) return false;
                if (status.SystemStatusFlag > 1) return false;
                on = status.SystemStatusFlag == EnergySaverOn;
                return true;
            }
            catch { return false; }
        }

        public static bool TryMemory(out double usedRatio, out double totalGb, out double availGb)
        {
            usedRatio = 0; totalGb = 0; availGb = 0;
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return false;
                totalGb = status.TotalPhys / 1073741824.0;
                availGb = status.AvailPhys / 1073741824.0;
                usedRatio = 1.0 - (status.AvailPhys / (double)status.TotalPhys);
                return true;
            }
            catch { return false; }
        }

        public static bool RefreshRateIsBest(int current, int best)
        {
            return current <= 0 || best <= 0 || current >= best - 1;
        }

        private static string WindowsText()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key == null) return Environment.OSVersion.VersionString;
                    string name = key.GetValue("ProductName") as string;
                    string display = key.GetValue("DisplayVersion") as string;
                    object build = key.GetValue("CurrentBuildNumber");
                    object ubr = key.GetValue("UBR");
                    string text = string.IsNullOrEmpty(name) ? "Windows" : name;
                    int buildNumber;
                    if (build != null && int.TryParse(build.ToString(), out buildNumber) && buildNumber >= 22000)
                        text = text.Replace("Windows 10", "Windows 11");
                    if (!string.IsNullOrEmpty(display)) text += " " + display;
                    if (build != null) text += " " + build + (ubr != null ? "." + ubr : "");
                    return text;
                }
            }
            catch { return Environment.OSVersion.VersionString; }
        }

        public const int EcoQosFullBuild = 22000;

        public static int WindowsBuild()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    if (key != null)
                    {
                        object build = key.GetValue("CurrentBuildNumber");
                        int parsed;
                        if (build != null && int.TryParse(build.ToString(), out parsed)) return parsed;
                    }
                }
            }
            catch { }
            try { return Environment.OSVersion.Version.Build; }
            catch { return 0; }
        }

        private static bool GameDvrOn()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore"))
                {
                    if (key == null) return false;
                    object value = key.GetValue("GameDVR_Enabled");
                    return value == null || Convert.ToInt32(value) != 0;
                }
            }
            catch { return false; }
        }

        public sealed class LoadEntry
        {
            public string Name;
            public double Ratio;
        }

        private sealed class Sample
        {
            public string Name;
            public long Started;
            public TimeSpan Cpu;
        }

        public static List<LoadEntry> TopConsumers(int windowMs, int take)
        {
            var result = new List<LoadEntry>();
            try
            {
                var before = new Dictionary<int, Sample>();
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            if (p.Id <= 4) continue;
                            before[p.Id] = new Sample
                            {
                                Name = p.ProcessName, Started = p.StartTime.Ticks, Cpu = p.TotalProcessorTime
                            };
                        }
                        catch { }
                    }
                }
                if (before.Count == 0) return result;
                System.Threading.Thread.Sleep(windowMs);
                double span = windowMs / 1000.0 * Environment.ProcessorCount;
                if (span <= 0) return result;
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            Sample old;
                            if (!before.TryGetValue(p.Id, out old)) continue;
                            if (p.StartTime.Ticks != old.Started) continue;
                            double delta = (p.TotalProcessorTime - old.Cpu).TotalSeconds;
                            if (delta <= 0) continue;
                            result.Add(new LoadEntry { Name = old.Name, Ratio = delta / span });
                        }
                        catch { }
                    }
                }
            }
            catch { return result; }
            result.Sort(delegate(LoadEntry a, LoadEntry b) { return b.Ratio.CompareTo(a.Ratio); });
            if (result.Count > take) result.RemoveRange(take, result.Count - take);
            return result;
        }

        private sealed class Facts
        {
            public bool Nv;
            public GpuAdapter[] Gpus = new GpuAdapter[0];
            public bool NvHardware;
            public bool AmdHardware;
            public bool IntegratedOnly;
            public bool Partition;
            public bool Eco;
            public bool EcoFull;
            public bool Hags;
            public VbsTweak.State Vbs = new VbsTweak.State();
            public bool GameMode;
            public bool MpoOff;
            public bool Dvr;
            public bool SuppressOn;
            public string Plan = Lang.T("t.systemaudithardware.3");
            public bool MemOk;
            public double UsedRatio, TotalGb, AvailGb;
            public List<string> ClockStale;
            public bool PageOk;
            public double PageFileGb;
            public string Link;
            public List<InputDevice> Inputs;
            public AccessibilityState Access;
            public bool HidPowerSave;
            public bool QueueTampered;
            public int? ThreadDpc;
            public int OsBuild;
            public bool RyzenCpu;
            public int MemModules;
            public int MemConfiguredMhz;
            public int MemRatedMhz;
            public List<int> AllHz;
            public List<string> RgbSuites;
            public int ThrottleEvents7d;
            public bool WindowedOptOn;
            public int MsiOffCount;
        }

        private static Facts Gather()
        {
            var f = new Facts();
            try { f.OsBuild = Native.OsBuild(); } catch { }
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string cpu = k == null ? null : k.GetValue("ProcessorNameString") as string;
                    f.RyzenCpu = cpu != null && cpu.IndexOf("AMD Ryzen", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }
            try { GatherMemoryModules(f); } catch { }
            try { f.AllHz = DisplayGuard.AllRefreshRates(); } catch { }
            try { f.RgbSuites = ScanRgbSuites(); } catch { }
            try { f.ThrottleEvents7d = CountCpuThrottleEvents(); } catch { }
            try { f.WindowedOptOn = WindowedOptTweak.CurrentlyOn(); } catch { }
            try { f.MsiOffCount = MsiModeTweak.Disabled().Count; } catch { }
            try { f.Nv = NvApi.Available; } catch { }
            try
            {
                f.Gpus = GpuInventory.Adapters();
                foreach (GpuAdapter g in f.Gpus)
                {
                    if (g.Vendor == GpuVendor.Nvidia) f.NvHardware = true;
                    if (g.Vendor == GpuVendor.Amd) f.AmdHardware = true;
                }
                f.IntegratedOnly = GpuInventory.IntegratedOnly;
            }
            catch { }
            try { f.Partition = CpuTopology.HasSafeBackgroundPartition(); } catch { }
            try { f.Eco = Native.PowerThrottlingSupported; } catch { }
            f.EcoFull = f.Eco && WindowsBuild() >= EcoQosFullBuild;
            try { f.Hags = HagsTweak.CurrentlyOn(); } catch { }
            try { f.Vbs = VbsTweak.Query(); } catch { }
            try { f.GameMode = GameModeGuard.CurrentlyOn(); } catch { }
            try { f.MpoOff = MpoTweak.CurrentlyDisabled(); } catch { }
            f.Dvr = GameDvrOn();
            try { f.SuppressOn = Settings.Load("GmSuppress", true); } catch { f.SuppressOn = true; }
            try { f.Plan = PowerPlan.CurrentPlanLabel(); } catch { }
            f.MemOk = TryMemory(out f.UsedRatio, out f.TotalGb, out f.AvailGb);
            try { f.ClockStale = PlatformClockTweak.StaleOverrides(); } catch { }
            f.PageOk = TryPageFile(out f.PageFileGb);
            try { f.Link = LinkKind(); } catch { }
            try { f.Inputs = InputChainProbe.Devices(); } catch { }
            try { f.Access = InputChainProbe.ReadAccessibility(); } catch { }
            try { f.HidPowerSave = HidPowerTweak.PowerSaveActive(); } catch { }
            try { f.QueueTampered = InputMythTweak.NeedsRepair(); } catch { }
            try { f.ThreadDpc = InputMythTweak.ThreadDpcOverride(); } catch { }
            return f;
        }

        public static bool TryPageFile(out double pageFileGb)
        {
            pageFileGb = 0;
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return false;
                double limit = status.TotalPageFile / 1073741824.0;
                double phys = status.TotalPhys / 1073741824.0;
                pageFileGb = Math.Max(0, limit - phys);
                return true;
            }
            catch { return false; }
        }

        internal static bool PageFileLooksDisabled(double pageFileGb)
        {
            return pageFileGb < 0.5;
        }

        private static string LinkKind()
        {
            bool wifi = false, wired = false;
            foreach (System.Net.NetworkInformation.NetworkInterface ni
                in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                var kind = ni.NetworkInterfaceType;
                if (kind == System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                    || kind == System.Net.NetworkInformation.NetworkInterfaceType.Tunnel) continue;
                bool gateway = false;
                try
                {
                    foreach (System.Net.NetworkInformation.GatewayIPAddressInformation gw
                        in ni.GetIPProperties().GatewayAddresses)
                        if (gw != null && gw.Address != null
                            && !gw.Address.Equals(System.Net.IPAddress.Any)) { gateway = true; break; }
                }
                catch { }
                if (!gateway) continue;
                if (kind == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211) wifi = true;
                else wired = true;
            }
            if (wired && wifi) return "both";
            if (wired) return "wired";
            if (wifi) return "wifi";
            return "none";
        }

        public static AuditReport Collect(int measureWindowMs)
        {
            var report = new AuditReport();
            report.MeasureWindowMs = measureWindowMs;

            var attrib = new InterruptAttribution();
            bool attribOn = false;
            try { attribOn = attrib.Start(); } catch { }

            double cpuBusy = 0;
            double[] rates = null;
            try { rates = DpcSampler.MeasureLoad(measureWindowMs, out cpuBusy); } catch { }
            report.MeasureOk = rates != null;

            InterruptAttributionResult culprits = null;
            if (attribOn)
            {
                try { culprits = attrib.Stop(); } catch { }
            }

            double worstIrq = 0; ulong worstCore = 0;
            if (rates != null)
            {
                foreach (ulong core in CpuTopology.PhysicalCoreMasks())
                {
                    double r = CpuPartitionPolicy.CoreInterruptRate(rates, core);
                    if (r > worstIrq) { worstIrq = r; worstCore = core; }
                }
            }

            int hzCur, hzBest;
            DisplayGuard.QueryRefreshRates(out hzCur, out hzBest);

            Facts facts = Gather();
            BuildCapability(report, facts);
            BuildMachine(report, facts, cpuBusy, worstIrq, worstCore, hzCur, hzBest, culprits);
            BuildHardwareHealth(report, facts);
            BuildInputChain(report, facts);
            BuildPersistent(report, facts);
            BuildVerdicts(report, facts, worstIrq, hzCur, hzBest, culprits);
            return report;
        }

        private static void BuildCapability(AuditReport report, Facts facts)
        {
            if (facts.Gpus.Length > 0)
            {
                report.Capability.Add(new AuditRow
                {
                    Name = Lang.T("cfg.group.gpu"),
                    Value = GpuInventory.Describe(),
                    Note = facts.IntegratedOnly
                        ? Lang.T("t.systemaudit.3")
                        : Lang.T("t.systemaudit.4"),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.5"),
                Value = facts.Nv ? Lang.T("t.systemaudit.6") : Lang.T("t.systemaudit.7"),
                Note = facts.Nv
                    ? Lang.T("t.systemaudit.8")
                    : facts.NvHardware
                        ? Lang.T("t.systemaudit.9")
                        : Lang.T("t.systemaudit.10"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.11"),
                Value = facts.AmdHardware ? Lang.T("t.systemaudit.12") : Lang.T("t.systemaudit.13"),
                Note = facts.AmdHardware
                    ? Lang.T("t.systemaudit.14")
                    : Lang.T("t.systemaudit.15"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.16"),
                Value = facts.Partition ? Lang.T("t.systemaudit.6") : Lang.T("t.systemaudit.7"),
                Note = facts.Partition ? Lang.T("t.systemaudit.17")
                    : Lang.T("t.systemaudit.18"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.19"),
                Value = !facts.Eco ? Lang.T("t.systemaudit.20") : (facts.EcoFull ? Lang.T("t.systemaudit.21") : Lang.T("t.systemaudit.22")),
                Note = !facts.Eco
                    ? Lang.T("t.systemaudit.23")
                    : (facts.EcoFull
                        ? Lang.T("t.systemaudit.24")
                        : Lang.T("t.systemaudit.25")),
                Evidence = EvMeasuredLocal,
                Warn = !facts.Eco
            });

            report.Capability.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.26"),
                Value = WindowsText(),
                Note = Lang.T("t.systemaudit.27"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });
        }

        internal static string TopDriversText(InterruptAttributionResult culprits, int max)
        {
            if (culprits == null || !culprits.Ok || culprits.Drivers.Count == 0) return null;
            var parts = new List<string>();
            for (int i = 0; i < culprits.Drivers.Count && i < max; i++)
            {
                DriverInterrupt d = culprits.Drivers[i];
                double share = culprits.DpcTotal + culprits.IsrTotal > 0
                    ? (double)(d.Dpc + d.Isr) / (culprits.DpcTotal + culprits.IsrTotal) : 0;
                parts.Add(d.Driver + " " + PercentText(share));
            }
            return string.Join("   ", parts.ToArray());
        }

        private static void BuildMachine(AuditReport report, Facts facts, double cpuBusy, double worstIrq, ulong worstCore,
            int hzCur, int hzBest, InterruptAttributionResult culprits)
        {
            BuildMachineCpu(report, cpuBusy, worstIrq, worstCore, culprits);
            BuildMachineDisplay(report, hzCur, hzBest);
            BuildMachineGpu(report);
            BuildMachinePower(report);
            BuildMachineRebar(report);
            BuildMachineMemory(report, facts);
            BuildMachineNetwork(report, facts);
            BuildMachineBattery(report);
            BuildMachineTopConsumers(report);
        }

        private static void BuildMachineCpu(AuditReport report, double cpuBusy, double worstIrq, ulong worstCore,
            InterruptAttributionResult culprits)
        {
            int logical = Environment.ProcessorCount;
            int physical = 0;
            try { physical = CpuTopology.PhysicalCoreCount; } catch { }
            string arch = CpuTopology.Hybrid ? Lang.T("t.systemaudit.28")
                : CpuTopology.AsymCache ? Lang.T("t.systemaudit.29")
                : CpuTopology.PartitionTag == "symmetric-ccd" ? Lang.T("t.systemaudit.30")
                : CpuTopology.PartitionTag == "pool-iso-core0" ? Lang.T("t.systemaudit.31")
                : Lang.T("t.systemaudit.32");
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.33"),
                Value = physical + Lang.T("t.systemaudit.34") + logical + Lang.T("t.systemaudit.35"),
                Note = arch + (CpuTopology.HasSafeBackgroundPartition()
                    ? Lang.T("t.systemaudit.36") + CpuTopology.StrictBoostMask.ToString("X") : Lang.T("t.systemaudit.37")),
                Evidence = EvMeasuredLocal,
                Warn = false
            });

            if (report.MeasureOk)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.38"),
                    Value = PercentText(cpuBusy),
                    Note = Lang.T("t.systemaudit.39") + (report.MeasureWindowMs >= 1000
                        ? (report.MeasureWindowMs / 1000) + Lang.T("t.systemaudit.40") : report.MeasureWindowMs + Lang.T("t.systemaudit.41")) + Lang.T("t.systemaudit.42"),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });

                int tier = InterruptTier(worstIrq);
                string topDrivers = TopDriversText(culprits, 3);
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.43"),
                    Value = (worstCore != 0 ? Lang.T("t.systemaudit.44") + CoreLabel(worstCore) + " " : "")
                        + Lang.T("t.systemaudit.45") + PercentText(worstIrq) + " " + InterruptTierText(tier),
                    Note = topDrivers != null
                        ? Lang.T("t.systemaudit.46") + topDrivers + (tier == 2
                            ? Lang.T("t.systemaudit.47")
                            : Lang.T("t.systemaudit.48"))
                        : tier == 2
                            ? Lang.T("t.systemaudit.49")
                            : report.MeasureWindowMs < 10000
                                ? Lang.T("t.systemaudit.50")
                                : Lang.T("t.systemaudit.51"),
                    Evidence = EvMeasuredLocal,
                    Warn = tier == 2
                });
            }
            else
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.43"),
                    Value = Lang.T("t.systemaudit.52"),
                    Note = Lang.T("t.systemaudit.53"),
                    Evidence = EvMeasuredLocal,
                    Warn = true
                });
            }
        }

        private static void BuildMachineDisplay(AuditReport report, int hzCur, int hzBest)
        {
            bool hzRead = hzCur > 0 && hzBest > 0;
            bool hzOk = RefreshRateIsBest(hzCur, hzBest);
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.1"),
                Value = hzRead
                    ? hzCur + " Hz" + (hzOk ? Lang.T("t.systemaudit.54") : Lang.T("t.systemaudit.55") + hzBest + " Hz")
                    : Lang.T("t.systemaudithardware.3"),
                Note = !hzRead
                    ? Lang.T("t.systemaudit.56")
                    : (hzOk
                        ? Lang.T("t.systemaudit.57")
                        : Lang.T("t.systemaudit.58") + hzCur + Lang.T("t.systemaudit.59") + hzBest
                            + Lang.T("t.systemaudit.60")
                            + hzBest + Lang.T("t.systemaudit.61")),
                Evidence = EvMeasuredLocal,
                Warn = !hzOk || !hzRead
            });
        }

        private static void BuildMachineGpu(AuditReport report)
        {
            string throttleNow = null;
            try { throttleNow = GpuThrottleProbe.InstantText(); } catch { }
            if (throttleNow != null)
            {
                bool throttled = throttleNow != Lang.T("t.gputhrottleprobe.6");
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.62"),
                    Value = throttleNow,
                    Note = throttled
                        ? Lang.T("t.systemaudit.63")
                        : Lang.T("t.systemaudit.64"),
                    Evidence = EvMeasuredLocal,
                    Warn = throttled
                });
            }
        }

        private static void BuildMachinePower(AuditReport report)
        {
            bool saverOn;
            if (TryEnergySaver(out saverOn))
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.65"),
                    Value = saverOn ? Lang.T("t.systemaudit.66") : Lang.T("t.systemaudit.67"),
                    Note = saverOn
                        ? Lang.T("t.systemaudit.68")
                        : Lang.T("t.systemaudit.69"),
                    Evidence = EvMeasuredLocal,
                    Warn = saverOn
                });
            }

            bool presenceOff = false;
            try { presenceOff = PresenceQos.CurrentlyDisabled(); } catch { }
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.legacypurge.6"),
                Value = presenceOff ? Lang.T("col.ux.closed") : Lang.T("t.systemaudit.70"),
                Note = presenceOff
                    ? Lang.T("t.systemaudit.71")
                    : Lang.T("t.systemaudit.72"),
                Evidence = EvMeasuredLocal,
                Warn = false
            });
        }

        private static void BuildMachineRebar(AuditReport report)
        {
            bool rebarOn = false;
            ulong rebarWindow = 0;
            string rebarGpu = null;
            bool rebarNvidia = false;
            bool rebarRead = false;
            try { rebarRead = RebarProbe.TryDetect(out rebarOn, out rebarWindow, out rebarGpu, out rebarNvidia); } catch { }
            if (rebarRead)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.73"),
                    Value = (rebarOn ? Lang.T("v16.device.hags.on") : Lang.T("gs.noff")) + Lang.T("t.systemaudit.74") + RebarProbe.WindowText(rebarWindow),
                    Note = (string.IsNullOrEmpty(rebarGpu) ? "" : rebarGpu + " ")
                        + (rebarNvidia
                            ? (rebarOn ? Lang.T("t.systemaudit.75") : Lang.T("t.systemaudit.76"))
                            : (rebarOn ? Lang.T("t.systemaudit.77") : Lang.T("t.systemaudit.78"))),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }
        }

        private static void BuildMachineMemory(AuditReport report, Facts facts)
        {
            if (facts.MemOk)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.79"),
                    Value = facts.TotalGb.ToString("F1") + Lang.T("t.systemaudit.80") + PercentText(facts.UsedRatio)
                        + Lang.T("t.systemaudit.55") + facts.AvailGb.ToString("F1") + " GB",
                    Note = facts.UsedRatio >= 0.85
                        ? Lang.T("t.systemaudit.81")
                        : Lang.T("t.systemaudit.82"),
                    Evidence = EvMeasuredLocal,
                    Warn = facts.UsedRatio >= 0.85
                });
            }

            if (facts.PageOk)
            {
                bool pageDisabled = PageFileLooksDisabled(facts.PageFileGb);
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.53"),
                    Value = pageDisabled ? Lang.T("t.systemaudit.83") : facts.PageFileGb.ToString("F1") + " GB",
                    Note = pageDisabled
                        ? Lang.T("t.systemaudit.84")
                        : Lang.T("t.systemaudit.85"),
                    Evidence = EvMeasuredLocal,
                    Warn = pageDisabled
                });
            }
        }

        private static void BuildMachineNetwork(AuditReport report, Facts facts)
        {
            if (facts.Link != null && facts.Link != "none")
            {
                bool wifiOnly = facts.Link == "wifi";
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.86"),
                    Value = facts.Link == "wired" ? Lang.T("t.systemaudit.87") : wifiOnly ? Lang.T("t.systemaudit.88") : Lang.T("t.systemaudit.89"),
                    Note = wifiOnly
                        ? Lang.T("t.systemaudit.90")
                        : facts.Link == "wired" ? Lang.T("t.systemaudit.91")
                        : Lang.T("t.systemaudit.92"),
                    Evidence = EvMeasuredLocal,
                    Warn = wifiOnly
                });
            }
        }

        private static void BuildMachineBattery(AuditReport report)
        {
            try
            {
                SystemPowerStatus power;
                if (GetSystemPowerStatus(out power) && power.BatteryFlag != 128
                    && power.BatteryFlag != 255 && power.AcLineStatus != 255)
                {
                    bool onAc = power.AcLineStatus == 1;
                    report.Machine.Add(new AuditRow
                    {
                        Name = Lang.T("t.systemaudit.93"),
                        Value = onAc ? Lang.T("t.systemaudit.94") : Lang.T("t.systemaudit.95"),
                        Note = onAc ? Lang.T("t.systemaudit.96")
                            : Lang.T("t.systemaudit.97"),
                        Evidence = EvMechanism,
                        Warn = !onAc
                    });
                }
            }
            catch { }
        }

        private static void BuildMachineTopConsumers(AuditReport report)
        {
            int topWindow = Math.Max(600, Math.Min(3000, report.MeasureWindowMs / 10));
            var top = TopConsumers(topWindow, 3);
            if (top.Count > 0)
            {
                var parts = new List<string>();
                foreach (LoadEntry e in top) parts.Add(e.Name + " " + PercentText(e.Ratio));
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.98"),
                    Value = string.Join("   ", parts.ToArray()),
                    Note = Lang.T("t.systemaudit.99") + (topWindow / 1000.0).ToString("0.#")
                        + Lang.T("t.systemaudit.100"),
                    Evidence = EvMeasuredLocal,
                    Warn = false
                });
            }
        }

        internal static string InputSummary(List<InputDevice> devices, bool mouse)
        {
            if (devices == null) return null;
            bool hasNonPs2 = false;
            foreach (InputDevice d in devices)
                if (d.IsMouse == mouse && d.Transport != InputTransport.Virtual
                    && d.Transport != InputTransport.Ps2) hasNonPs2 = true;
            var seen = new List<string>();
            foreach (InputDevice d in devices)
            {
                if (d.IsMouse != mouse) continue;
                if (d.Transport == InputTransport.Virtual) continue;
                if (d.Transport == InputTransport.Ps2 && hasNonPs2) continue;
                string text = InputChainProbe.TransportText(d.Transport);
                if (!seen.Contains(text)) seen.Add(text);
            }
            return seen.Count == 0 ? null : string.Join(Lang.T("t.systemaudit.101"), seen.ToArray());
        }

        private static void BuildInputChain(AuditReport report, Facts facts)
        {
            bool bt = false, btOnly = false;
            try
            {
                bt = InputChainProbe.AnyBluetooth(facts.Inputs);
                btOnly = InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, true)
                    || InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, false);
            }
            catch { }
            string mice = InputSummary(facts.Inputs, true);
            string keys = InputSummary(facts.Inputs, false);
            if (mice != null || keys != null)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("env.tab.input"),
                    Value = (mice == null ? "" : Lang.T("t.systemaudit.102") + mice) + (mice != null && keys != null ? "   " : "")
                        + (keys == null ? "" : Lang.T("t.systemaudit.103") + keys),
                    Note = btOnly
                        ? Lang.T("t.systemaudit.104")
                        : bt
                            ? Lang.T("t.systemaudit.105")
                            : Lang.T("t.systemaudit.106"),
                    Evidence = bt ? EvMeasuredBench : EvMeasuredLocal,
                    Warn = btOnly
                });
            }

            if (facts.Access != null && facts.Access.Read)
            {
                bool swallow = facts.Access.FilterKeysSwallowing;
                bool needFix = facts.Access.AnyNeedsFix;
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.legacypurge.14"),
                    Value = swallow ? Lang.T("t.systemaudit.107") : needFix ? Lang.T("t.systemaudit.108") : Lang.T("t.systemaudit.1"),
                    Note = swallow
                        ? Lang.T("t.systemaudit.109")
                            + facts.Access.DelayBeforeAcceptanceMs
                            + Lang.T("t.systemaudit.110")
                        : needFix
                            ? Lang.T("t.systemaudit.111")
                            : Lang.T("t.systemaudit.112"),
                    Evidence = EvMechanism,
                    Warn = needFix
                });
            }

            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.legacypurge.15"),
                Value = facts.HidPowerSave ? Lang.T("t.systemaudit.113") : Lang.T("t.systemaudit.114"),
                Note = facts.HidPowerSave
                    ? Lang.T("t.systemaudit.115")
                    : Lang.T("t.systemaudit.116"),
                Evidence = EvMechanism,
                Warn = facts.HidPowerSave
            });

            bool dpc = facts.ThreadDpc.HasValue;
            if (facts.QueueTampered || dpc)
            {
                var parts = new List<string>();
                if (facts.QueueTampered) parts.Add(Lang.T("t.systemaudit.119"));
                if (dpc) parts.Add(Lang.T("t.systemaudit.120") + facts.ThreadDpc.Value);
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudit.121"),
                    Value = string.Join("  ", parts.ToArray()),
                    Note = (facts.QueueTampered
                            ? Lang.T("t.systemaudit.122") + InputMythTweak.SystemDefault + Lang.T("t.systemaudit.123")
                            : "")
                        + (dpc
                            ? Lang.T("t.systemaudit.124")
                            : ""),
                    Evidence = EvMechanism,
                    Warn = true
                });
            }
        }

        private static void BuildPersistent(AuditReport report, Facts facts)
        {
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.125"),
                Value = facts.Hags ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close"),
                Note = Lang.T("t.systemaudit.126"),
                Evidence = EvUnverified,
                Warn = false
            });

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.127"),
                Value = !facts.Vbs.WmiOk ? Lang.T("t.systemaudithardware.3") : (facts.Vbs.VbsRunning ? Lang.T("scan.running.tag") : Lang.T("col.proc.none")),
                Note = facts.Vbs.VbsRunning ? Lang.T("t.systemaudit.128")
                    : Lang.T("t.systemaudit.129"),
                Evidence = EvMechanism,
                Warn = false
            });

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.26"),
                Value = facts.GameMode ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close"),
                Note = facts.GameMode ? Lang.T("t.systemaudit.130")
                    : Lang.T("t.systemaudit.131"),
                Evidence = EvMechanism,
                Warn = !facts.GameMode
            });

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.132"),
                Value = facts.MpoOff ? Lang.T("t.systemaudit.133") : Lang.T("t.systemaudit.134"),
                Note = facts.MpoOff
                    ? Lang.T("t.systemaudit.135")
                    : Lang.T("t.systemaudit.136"),
                Evidence = EvMechanism,
                Warn = facts.MpoOff
            });

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemauditverdicts.43"),
                Value = facts.Dvr ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close"),
                Note = facts.Dvr ? Lang.T("t.systemaudit.137")
                    : Lang.T("t.systemaudit.138"),
                Evidence = EvMechanism,
                Warn = facts.Dvr
            });

            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.139"),
                Value = facts.Plan,
                Note = Lang.T("t.systemaudit.140"),
                Evidence = EvMechanism,
                Warn = false
            });

            if (facts.ClockStale != null)
            {
                bool stale = facts.ClockStale.Count > 0;
                report.Persistent.Add(new AuditRow
                {
                    Name = Lang.T("t.systemauditverdicts.50"),
                    Value = stale ? Lang.T("t.systemaudit.141") + string.Join(" ", facts.ClockStale.ToArray()) : Lang.T("t.systemaudit.134"),
                    Note = stale
                        ? Lang.T("t.systemaudit.142")
                        : Lang.T("t.systemaudit.143"),
                    Evidence = EvMechanism,
                    Warn = stale,
                    FixKey = "clock"
                });
            }

            bool quantumTampered = false;
            string quantumState = "";
            try { quantumTampered = QuantumTweak.NeedsRepair(); quantumState = QuantumTweak.Describe(); }
            catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.144"),
                Value = quantumTampered ? Lang.T("t.systemaudit.145") : Lang.T("t.systemaudit.134"),
                Note = (quantumTampered
                    ? Lang.T("t.systemaudit.146")
                    : Lang.T("t.systemaudit.147")) + " " + quantumState,
                Evidence = EvMechanism,
                Warn = quantumTampered,
                FixKey = "quantum"
            });

            bool netTampered = false;
            string netState = "";
            try { netTampered = NetTweak.NeedsRepair(); netState = NetTweak.Describe(); }
            catch { }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudit.148"),
                Value = netTampered ? Lang.T("t.systemaudit.145") : Lang.T("t.systemaudit.134"),
                Note = (netTampered
                    ? Lang.T("t.systemaudit.149")
                    : Lang.T("t.systemaudit.150")) + " " + netState,
                Evidence = EvMechanism,
                Warn = netTampered,
                FixKey = "net"
            });
        }
    }
}
