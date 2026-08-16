// @author bdth 2074055628@qq.com
// 文件用途 上传让位 已退役 仅保留还原能力

using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class UploadYield
    {
        private const string QosRoot = @"SOFTWARE\Policies\Microsoft\Windows\QoS";
        private const string Prefix = "PaviseYield_";
        private const string Flag = "UploadYieldApplied";
        private const int ShellTimeoutMs = 60000;

        private static readonly object lk = new object();

        public static void Clear()
        {
            lock (lk)
            {
                bool had = Settings.LoadStr(Flag, "").Length > 0 || RegistryResidueCount() > 0;
                if (!had) return;

                bool ok = RunShell(
                    "Get-NetQosPolicy -ErrorAction SilentlyContinue"
                    + " | Where-Object { $_.Name -like '" + Prefix + "*' }"
                    + " | Remove-NetQosPolicy -Confirm:$false -ErrorAction SilentlyContinue;");

                int left = RegistryResidueCount();
                if (ok && left == 0)
                {
                    SweepRegistryResidue();
                    Settings.SaveStr(Flag, "");
                    Logger.Log(Lang.T("log.uploadyield.1"));
                    return;
                }
                Logger.Log(Lang.T("log.uploadyield.2") + left + Lang.T("log.uploadyield.3"));
            }
        }

        public static void HealFromCrash()
        {
            if (Settings.LoadStr(Flag, "").Length > 0 || RegistryResidueCount() > 0) Clear();
        }

        public static bool HasResidue()
        {
            return Settings.LoadStr(Flag, "").Length > 0 || RegistryResidueCount() > 0;
        }

        private static bool RunShell(string script)
        {
            try
            {
                var psi = new ProcessStartInfo("powershell.exe",
                    "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    if (!p.WaitForExit(ShellTimeoutMs)) { try { p.Kill(); } catch { } return false; }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log(Lang.T("log.uploadyield.4") + ex.Message);
                return false;
            }
        }

        private static int RegistryResidueCount()
        {
            int n = 0;
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(QosRoot))
                {
                    if (root == null) return 0;
                    foreach (string sub in root.GetSubKeyNames())
                        if (sub.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) n++;
                }
            }
            catch { }
            return n;
        }

        private static void SweepRegistryResidue()
        {
            try
            {
                using (RegistryKey root = Registry.LocalMachine.OpenSubKey(QosRoot, true))
                {
                    if (root == null) return;
                    foreach (string sub in root.GetSubKeyNames())
                        if (sub.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                            try { root.DeleteSubKeyTree(sub, false); } catch { }
                }
            }
            catch { }
        }
    }
}
