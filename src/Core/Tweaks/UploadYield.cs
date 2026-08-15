// @author bdth 2074055628@qq.com
// 文件用途 上传让位 已退役 仅保留还原能力
// 退役原因 对局中拉 PowerShell 刷 NetQosPolicy 组策略不稳定 顿挫风险大 概念正经但实现不可靠
// 仅保留 Clear/HealFromCrash/HasResidue 清理旧版本写过的 PaviseYield_* QoS 策略

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
                    Logger.Log("上传让位 已解除全部上行限速");
                    return;
                }
                Logger.Log("上传让位 仍有 " + left + " 条限速未能解除 标志保留 下次启动重试");
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
                Logger.Log("上传让位 清理 NetQosPolicy 失败 " + ex.Message);
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
