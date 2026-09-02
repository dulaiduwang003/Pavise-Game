// @author bdth 2074055628@qq.com
// 文件用途 有线加无线并存时把有线口的接口跃点压低 让默认路由走有线 记原值可写回
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;

namespace PaviseApp
{
    // Windows 自动跃点按链路速率算 有线 200Mb 到 2Gb 是 25 Wi-Fi 报 2Gb 以上也是 25
    //   Wi-Fi 6E 和 7 常报 2.4Gbps 千兆有线和它打平 默认路由可能走无线
    //   只在"有线口有默认网关且已连通 默认路由却在无线口"时才算需要修
    //   修法是把该有线口 IPv4 和 IPv6 的跃点写成 5 关闭开关时恢复自动跃点
    internal static class LinkMetricTweak
    {
        private const string ReceiptKey = "LinkMetricReceipt";
        private const int PreferredMetric = 5;
        private static readonly object lk = new object();

        public static bool RepairedByPavise { get { return Settings.LoadStr(ReceiptKey, "").Length > 0; } }

        internal static bool Decide(bool wiredWithGatewayUp, bool defaultRouteWireless)
        {
            return wiredWithGatewayUp && defaultRouteWireless;
        }

        private sealed class Probe
        {
            public int WiredIndex = -1;
            public bool DefaultRouteWireless;
        }

        private static Probe Inspect()
        {
            var probe = new Probe();
            int bestIndex = BestInterfaceIndex();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                NetworkInterfaceType kind = ni.NetworkInterfaceType;
                if (kind == NetworkInterfaceType.Loopback || kind == NetworkInterfaceType.Tunnel) continue;
                bool gateway = false;
                int index = -1;
                try
                {
                    IPInterfaceProperties props = ni.GetIPProperties();
                    foreach (GatewayIPAddressInformation gw in props.GatewayAddresses)
                        if (gw != null && gw.Address != null && !gw.Address.Equals(System.Net.IPAddress.Any))
                        { gateway = true; break; }
                    try { index = props.GetIPv4Properties().Index; } catch { }
                }
                catch { }
                if (!gateway) continue;
                if (kind == NetworkInterfaceType.Wireless80211)
                {
                    if (index > 0 && index == bestIndex) probe.DefaultRouteWireless = true;
                }
                else if (kind == NetworkInterfaceType.Ethernet || kind == NetworkInterfaceType.GigabitEthernet
                    || kind == NetworkInterfaceType.FastEthernetT || kind == NetworkInterfaceType.FastEthernetFx)
                {
                    // 多张有线口都带网关时不猜 谁是出口说不清 不修
                    if (probe.WiredIndex > 0) { probe.WiredIndex = -2; continue; }
                    probe.WiredIndex = index;
                }
            }
            return probe;
        }

        [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
        private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

        private static int BestInterfaceIndex()
        {
            try
            {
                uint index;
                // 8.8.8.8 只是拿来问路由表 不发任何包
                if (GetBestInterface(0x08080808u, out index) == 0) return (int)index;
            }
            catch { }
            return -1;
        }

        public static bool NeedsRepair()
        {
            if (RepairedByPavise) return false;
            try
            {
                Probe probe = Inspect();
                return probe.WiredIndex > 0 && Decide(true, probe.DefaultRouteWireless);
            }
            catch { return false; }
        }

        public static string Describe()
        {
            if (RepairedByPavise) return Lang.T("linkmetric.repaired");
            try
            {
                Probe probe = Inspect();
                if (probe.WiredIndex == -2) return Lang.T("linkmetric.ambiguous");
                if (probe.WiredIndex <= 0) return Lang.T("linkmetric.nowired");
                return probe.DefaultRouteWireless ? Lang.T("linkmetric.wireless") : Lang.T("linkmetric.ok");
            }
            catch { return Lang.T("linkmetric.nowired"); }
        }

        public static bool Repair()
        {
            lock (lk)
            {
                if (RepairedByPavise) return true;
                Probe probe;
                try { probe = Inspect(); } catch { return false; }
                if (probe.WiredIndex <= 0 || !probe.DefaultRouteWireless) return true;
                string output;
                var args = new Dictionary<string, string>
                {
                    { "PAVISE_LM_INDEX", probe.WiredIndex.ToString() },
                    { "PAVISE_LM_METRIC", PreferredMetric.ToString() },
                };
                if (!PsRunner.Run(ApplyScript, "link-metric-apply", 30000, args, out output)
                    || output.IndexOf("APPLYOK", StringComparison.Ordinal) < 0)
                { Logger.Log(Lang.T("log.linkmetric.1")); return false; }
                Settings.SaveStr(ReceiptKey, probe.WiredIndex.ToString());
                if (Settings.LoadStr(ReceiptKey, "") != probe.WiredIndex.ToString())
                {
                    RestoreIndex(probe.WiredIndex);
                    return false;
                }
                Logger.Log(Lang.T("log.linkmetric.2"));
                return true;
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                string raw = Settings.LoadStr(ReceiptKey, "");
                if (raw.Length == 0) return true;
                int index;
                if (int.TryParse(raw, out index) && !RestoreIndex(index))
                { Logger.Log(Lang.T("log.linkmetric.3")); return false; }
                Settings.SaveStr(ReceiptKey, "");
                Logger.Log(Lang.T("log.linkmetric.4"));
                return true;
            }
        }

        private static bool RestoreIndex(int index)
        {
            string output;
            var args = new Dictionary<string, string> { { "PAVISE_LM_INDEX", index.ToString() } };
            return PsRunner.Run(RestoreScript, "link-metric-restore", 30000, args, out output)
                && output.IndexOf("RESTOREOK", StringComparison.Ordinal) >= 0;
        }

        private static readonly string ApplyScript = string.Join("\r\n", new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$i = [int]$env:PAVISE_LM_INDEX",
            "$m = [int]$env:PAVISE_LM_METRIC",
            "if ($null -eq (Get-NetAdapter -InterfaceIndex $i -ErrorAction SilentlyContinue)) { Write-Output 'APPLYFAIL'; exit }",
            "try { Set-NetIPInterface -InterfaceIndex $i -InterfaceMetric $m -ErrorAction Stop } catch { Write-Output 'APPLYFAIL'; exit }",
            "$chk = Get-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue",
            "if ($null -ne $chk -and $chk.InterfaceMetric -eq $m) { Write-Output 'APPLYOK' } else { Write-Output 'APPLYFAIL' }",
        });

        private static readonly string RestoreScript = string.Join("\r\n", new[]
        {
            "$ErrorActionPreference = 'SilentlyContinue'",
            "$i = [int]$env:PAVISE_LM_INDEX",
            "if ($null -eq (Get-NetAdapter -InterfaceIndex $i -ErrorAction SilentlyContinue)) { Write-Output 'RESTOREOK'; exit }",
            "try { Set-NetIPInterface -InterfaceIndex $i -AutomaticMetric Enabled -ErrorAction Stop; Write-Output 'RESTOREOK' } catch { Write-Output 'RESTOREFAIL' }",
        });
    }
}
