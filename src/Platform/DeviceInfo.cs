// @author bdth 2074055628@qq.com
// 文件用途 只读汇总本机处理器 显卡 内存与图形调度信息 供概览页展示

using System;
using System.Collections.Generic;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class DeviceInfo
    {
        private static string[] cached;

        public static string[] Specs()
        {
            return Build(false);
        }

        public static string[] SpecsWithSlowFallback()
        {
            return Build(true);
        }

        private static string[] Build(bool allowSlow)
        {
            if (cached != null) return cached;
            string cpu = CpuName();
            string gpu = GpuFromRegistry();
            if (string.IsNullOrEmpty(gpu) && allowSlow) gpu = GpuFromWmi();
            string mem = MemoryText();
            var specs = new[]
            {
                string.IsNullOrEmpty(cpu) ? "—" : cpu,
                string.IsNullOrEmpty(gpu) ? "—" : gpu,
                string.IsNullOrEmpty(mem) ? "—" : mem,
                HagsText()
            };
            if (specs[1] != "—") cached = specs;
            return specs;
        }

        private static string CpuName()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    if (key == null) return null;
                    string name = key.GetValue("ProcessorNameString") as string;
                    return name == null ? null : Compact(name);
                }
            }
            catch { return null; }
        }

        private static string GpuFromRegistry()
        {
            string best = null;
            long bestScore = -1;
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}"))
                {
                    if (root == null) return null;
                    foreach (string sub in root.GetSubKeyNames())
                    {
                        if (sub.Length != 4) continue;
                        using (RegistryKey key = root.OpenSubKey(sub))
                        {
                            if (key == null) continue;
                            string compact = Compact(key.GetValue("DriverDesc") as string);
                            if (compact.Length == 0 || IsVirtualAdapter(compact)) continue;
                            long score = VideoMemoryBytes(key);
                            if (IsDiscrete(compact)) score += 1L << 60;
                            if (score > bestScore) { bestScore = score; best = compact; }
                        }
                    }
                }
            }
            catch { }
            return best;
        }

        private static long VideoMemoryBytes(RegistryKey key)
        {
            string[] names = { "HardwareInformation.qwMemorySize", "HardwareInformation.MemorySize" };
            foreach (string name in names)
            {
                try
                {
                    object raw = key.GetValue(name);
                    if (raw is long) return (long)raw;
                    if (raw is int) return (uint)(int)raw;
                    var bytes = raw as byte[];
                    if (bytes != null && bytes.Length >= 4)
                    {
                        long value = 0;
                        for (int i = Math.Min(8, bytes.Length) - 1; i >= 0; i--) value = (value << 8) | bytes[i];
                        if (value > 0) return value;
                    }
                }
                catch { }
            }
            return 0;
        }

        private static string GpuFromWmi()
        {
            string best = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name FROM Win32_VideoController"))
                using (ManagementObjectCollection results = searcher.Get())
                    foreach (ManagementObject item in results)
                        using (item)
                        {
                            string compact = Compact(item["Name"] as string);
                            if (compact.Length == 0 || IsVirtualAdapter(compact)) continue;
                            if (IsDiscrete(compact)) return compact;
                            if (best == null) best = compact;
                        }
            }
            catch { }
            return best;
        }

        private static bool IsDiscrete(string name)
        {
            return name.IndexOf("NVIDIA", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("GeForce", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Radeon", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Arc", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static readonly string[] VirtualAdapterHints =
        {
            "Virtual", "Basic", "Remote", "Mirror", "Todesk", "MuMu",
            "GameViewer", "Parsec", "Sunshine", "IddSample", "Meta", "Citrix"
        };

        private static bool IsVirtualAdapter(string name)
        {
            foreach (string hint in VirtualAdapterHints)
                if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint Length, MemoryLoad;
            public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile,
                TotalVirtual, AvailVirtual, AvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        private static string MemoryText()
        {
            try
            {
                var status = new MemoryStatusEx();
                status.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
                if (!GlobalMemoryStatusEx(ref status) || status.TotalPhys == 0) return null;
                double gb = status.TotalPhys / 1073741824.0;
                return Math.Round(gb).ToString("0") + " GB";
            }
            catch { return null; }
        }

        private static string HagsText()
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"))
                {
                    object raw = key == null ? null : key.GetValue("HwSchMode");
                    if (raw == null) return Lang.T("v16.device.hags.none");
                    int mode = Convert.ToInt32(raw);
                    return mode == 2 ? Lang.T("v16.device.hags.on") : Lang.T("v16.device.hags.off");
                }
            }
            catch { return Lang.T("v16.device.hags.none"); }
        }

        private static string Compact(string value)
        {
            string text = (value ?? "").Replace("(R)", "").Replace("(TM)", "").Replace("(C)", "")
                .Replace("®", "").Replace("™", "");
            text = text.Replace(" CPU", "").Replace(" Processor", "");
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
                text = text.Replace("  ", " ");
            return text.Trim();
        }
    }
}
