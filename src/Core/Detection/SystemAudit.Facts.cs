// @author bdth 2074055628@qq.com
// 文件用途 体检事实采集 页面文件与链路类型
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {
        private sealed class Facts
        {
            public bool Nv;
            public GpuAdapter[] Gpus = new GpuAdapter[0];
            public bool NvHardware;
            public bool IntegratedOnly;
            public bool Partition;
            public bool PartitionOn;
            public bool Eco;
            public bool EcoFull;
            public bool Hags;
            public VbsTweak.State Vbs = new VbsTweak.State();
            public SpecMitigationTweak.State SpecMit = new SpecMitigationTweak.State();
            public bool GameMode;
            public bool MpoOff;
            public bool Dvr;
            public bool SuppressOn;
            public string Plan = Lang.T("t.systemaudithardware.3");
            public bool MemOk;
            public double UsedRatio, TotalGb, AvailGb;
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
            public List<double> MemModuleGb;
            public List<BypassIoVerdict> BypassIo;
        }

        public static Func<List<string>> LibraryPaths;

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
            try { f.BypassIo = GatherBypassIo(); } catch { }
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
                }
                f.IntegratedOnly = GpuInventory.IntegratedOnly;
            }
            catch { }
            try { f.Partition = CpuTopology.HasSafeBackgroundPartition(); } catch { }
            try { f.PartitionOn = Settings.Load("GmStrictCores", false); } catch { }
            try { f.Eco = Native.PowerThrottlingSupported; } catch { }
            f.EcoFull = f.Eco && WindowsBuild() >= EcoQosFullBuild;
            try { f.Hags = HagsTweak.CurrentlyOn(); } catch { }
            try { f.Vbs = VbsTweak.Query(); } catch { }
            try { f.SpecMit = SpecMitigationTweak.Query(); } catch { }
            try { f.GameMode = GameModeGuard.CurrentlyOn(); } catch { }
            try { f.MpoOff = MpoTweak.CurrentlyDisabled(); } catch { }
            f.Dvr = GameDvrOn();
            try { f.SuppressOn = Settings.Load("GmSuppress", true); } catch { f.SuppressOn = true; }
            try { f.Plan = PowerPlan.CurrentPlanLabel(); } catch { }
            f.MemOk = TryMemory(out f.UsedRatio, out f.TotalGb, out f.AvailGb);
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

        // TotalPageFile 是物理内存加页面文件 减掉物理内存才是页面文件本身
        //   留半 GB 余量是因为这两个值来自同一次调用但统计口径有零头
        // TotalPageFile 是物理内存加页面文件 减掉物理内存才是页面文件本身
        //   留半 GB 余量是因为这两个值来自同一次调用但统计口径有零头
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
    }
}
