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
            GpuAdapter primary = GpuInventory.Primary();
            return primary == null ? null : primary.Name;
        }

        private static string GpuFromWmi()
        {
            string best = null;
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPDeviceID FROM Win32_VideoController"))
                using (ManagementObjectCollection results = searcher.Get())
                    foreach (ManagementObject item in results)
                        using (item)
                        {
                            string pnp = item["PNPDeviceID"] as string;
                            if (!GpuInventory.IsPciAdapter(pnp)) continue;
                            string compact = Compact(item["Name"] as string);
                            if (compact.Length == 0) continue;
                            if (GpuInventory.VendorOf(pnp) == GpuVendor.Nvidia) return compact;
                            if (best == null) best = compact;
                        }
            }
            catch { }
            return best;
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
                bool supported, enabled;
                bool capsOk = HagsTweak.TryQueryState(out supported, out enabled);
                if (capsOk && !supported) return Lang.T("v16.device.hags.none");
                int? mode = HagsTweak.ConfiguredMode();
                if (mode.HasValue)
                    return mode.Value == 2 ? Lang.T("v16.device.hags.on") : Lang.T("v16.device.hags.off");
                if (capsOk)
                    return enabled ? Lang.T("v16.device.hags.on") : Lang.T("v16.device.hags.off");
                return Lang.T("v16.device.hags.unknown");
            }
            catch { return Lang.T("v16.device.hags.unknown"); }
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
