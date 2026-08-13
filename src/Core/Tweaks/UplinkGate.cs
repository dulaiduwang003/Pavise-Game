// @author bdth 2074055628@qq.com
// 文件用途 采样全网卡上行速率给上传让位当闸门 后台没人上传就不写策略也不刷组策略
// 预热窗口避开开局加载期 那段时间刷组策略的顿挫最容易被当成游戏卡

using System;
using System.Net.NetworkInformation;

namespace PaviseApp
{
    internal static class UplinkGate
    {
        public const double BusyMbps = 1.5;
        public const int WarmupSeconds = 45;
        public const int SampleGapSeconds = 5;

        internal static double Mbps(long deltaBytes, double seconds)
        {
            if (seconds <= 0 || deltaBytes <= 0) return 0;
            return deltaBytes * 8.0 / 1000000.0 / seconds;
        }

        internal static bool Busy(double mbps)
        {
            return mbps >= BusyMbps;
        }

        internal static bool ShouldEngage(long deltaBytes, double seconds, out double mbps)
        {
            mbps = 0;
            if (seconds < SampleGapSeconds) return false;
            mbps = Mbps(deltaBytes, seconds);
            return Busy(mbps);
        }

        private const int TunnelInterfaceType = 53;

        private static readonly string[] VirtualNicWords =
        {
            "tap", "tun", "tunnel", "virtual", "vmware", "hyper-v", "vbox", "virtualbox",
            "clash", "mihomo", "wireguard", "openvpn", "wintun", "zerotier",
            "tailscale", "loopback", "pseudo", "teredo", "vpn"
        };

        internal static bool LooksVirtual(string name, string description, int nicType)
        {
            if (nicType == TunnelInterfaceType) return true;
            string s = ((name ?? "") + " " + (description ?? "")).ToLowerInvariant();
            foreach (string w in VirtualNicWords) if (s.Contains(w)) return true;
            return false;
        }

        internal static bool IsBenchmarkNet(byte[] addr)
        {
            return addr != null && addr.Length == 4
                && addr[0] == 198 && (addr[1] == 18 || addr[1] == 19);
        }

        public static long TotalBytesSent()
        {
            long total = 0;
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up
                        || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (LooksVirtual(nic.Name, nic.Description, (int)nic.NetworkInterfaceType)) continue;
                    total += nic.GetIPv4Statistics().BytesSent;
                }
            }
            catch { return 0; }
            return total;
        }
    }
}
