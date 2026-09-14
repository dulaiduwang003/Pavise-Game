// @author bdth 2074055628@qq.com
// File purpose Health check system fact reads: memory, power, version, and switch states
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
        // Every verdict must carry an evidence level, shown verbatim to the user in the UI
        //   Measured locally is the strongest; bench-measured was measured on a different machine; mechanism-clear is inferred from principle only
        //   Unverified means not measured yet; when writing new verdicts do not lazily stamp everything with the top level
        public const string EvMeasuredLocal = "本机实测";
        public const string EvMeasuredBench = "台架实测";
        public const string EvMechanism = "机制明确";
        public const string EvUnverified = "未验证";

        public static string PercentText(double rate)
        {
            return (rate * 100.0).ToString("F2") + "%";
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
    }
}
