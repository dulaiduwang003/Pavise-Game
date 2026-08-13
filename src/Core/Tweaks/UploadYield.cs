// @author bdth 2074055628@qq.com
// 文件用途 游戏时给被压制的后台进程写 QoS 上行限速策略 台架实测 954Mbps 压到 0.5Mbps
// 必须走 NetQosPolicy 官方接口 手写注册表既不生效也解除不掉 详见 --qos-lab 台架

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class UploadYield
    {
        public const int ThrottleKbps = 512;
        private const string QosRoot = @"SOFTWARE\Policies\Microsoft\Windows\QoS";
        private const string Prefix = "PaviseYield_";
        private const string Flag = "UploadYieldApplied";
        private const int MaxPolicies = 48;
        private const int ShellTimeoutMs = 60000;

        private static readonly object lk = new object();

        public static int Apply(IEnumerable<string> processNames)
        {
            var wanted = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in processNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                string exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? name : name + ".exe";
                if (!IsShellSafe(exe) || PolicyNameFor(exe).Length == Prefix.Length) continue;
                if (!seen.Add(exe)) continue;
                wanted.Add(exe);
                if (wanted.Count >= MaxPolicies) break;
            }
            if (wanted.Count == 0) return 0;

            lock (lk)
            {
                var script = new StringBuilder();
                foreach (string exe in wanted)
                    script.Append("New-NetQosPolicy -Name '").Append(PolicyNameFor(exe))
                        .Append("' -AppPathNameMatchCondition '").Append(exe)
                        .Append("' -ThrottleRateActionBitsPerSecond ").Append(ThrottleKbps * 1000)
                        .Append(" -ErrorAction SilentlyContinue | Out-Null;");

                Settings.SaveStr(Flag, "1");
                if (!RunShell(script.ToString()))
                {
                    Logger.Log("上传让位 策略写入失败 本轮不限速");
                    Clear();
                    return 0;
                }
                int live = CountLivePolicies();
                if (live == 0)
                {
                    Settings.SaveStr(Flag, "");
                    Logger.Log("上传让位 策略回读为空 本轮未生效");
                    return 0;
                }
                Logger.Log("上传让位 已对 " + live + " 个被压制的后台进程限制上行 每进程 "
                    + ThrottleKbps + " kbps 退出游戏解除");
                return live;
            }
        }

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

                int left = CountLivePolicies();
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
                Logger.Log("上传让位 调用 NetQosPolicy 失败 " + ex.Message);
                return false;
            }
        }

        private static int CountLivePolicies()
        {
            return RegistryResidueCount();
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

        internal static bool IsShellSafe(string exe)
        {
            if (string.IsNullOrEmpty(exe) || exe.Length > 128) return false;
            foreach (char c in exe)
            {
                if (c < 32 || c == 127) return false;
                if (c == '\'' || c == '"' || c == '`' || c == '$' || c == ';' || c == '|'
                    || c == '&' || c == '<' || c == '>' || c == '(' || c == ')'
                    || c == '{' || c == '}' || c == '%') return false;
            }
            return true;
        }

        internal static string PolicyNameFor(string exe)
        {
            var sb = new StringBuilder(Prefix.Length + 32);
            sb.Append(Prefix);
            foreach (char c in exe ?? "")
                if (c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                    sb.Append(c);
            return sb.ToString();
        }
    }
}
