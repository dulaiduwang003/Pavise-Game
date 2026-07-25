// @author bdth 2074055628@qq.com
// 文件用途 网卡中断亲和优化与游戏流量 QoS 优先级标记 开启 关闭并恢复

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace AegisApp
{
    internal static class NetworkAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine =
            new IrqAffinityEngine("NicAffinityOnByAegis", "NicAff_", "网卡中断亲和");

        private const string QosPolicyNamesKey = "NetQosPolicyNames";
        private const string EnabledKey = "NetPriorityOnByAegis";
        private const string PolicyPrefix = "Aegis_";
        private const int GamingDscp = 46;

        public static bool EnabledByAegis { get { return Settings.Load(EnabledKey, false); } }

        // 只读枚举：真实物理网卡（PhysicalAdapter=TRUE），只留 PCI/USB 总线设备，
        // 排除虚拟网卡（VPN、Hyper-V 虚拟交换机、蓝牙 PAN 等 PhysicalAdapter 已为 FALSE 的情形）。
        internal static List<string> EnumerateNicDeviceIds()
        {
            var ids = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
                using (ManagementObjectCollection results = searcher.Get())
                {
                    foreach (ManagementObject mo in results)
                    {
                        using (mo)
                        {
                            string id = mo["PNPDeviceID"] as string;
                            if (string.IsNullOrEmpty(id)) continue;
                            if (!id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
                                && !id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)) continue;
                            ids.Add(id);
                        }
                    }
                }
            }
            catch (Exception ex) { Logger.Log("枚举网卡设备失败：" + ex.Message); }
            return ids;
        }

        // QoS 策略名 = 前缀 + 净化后的游戏名 + 可执行文件路径的短哈希，
        // 哈希保证同名不同游戏、或名字含有大量非法字符时依然唯一且长度可控。
        internal static string SanitizePolicyName(string gameName, string exePath)
        {
            var sb = new StringBuilder(PolicyPrefix);
            foreach (char c in gameName ?? "")
            {
                if (sb.Length - PolicyPrefix.Length >= 40) break;
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            if (sb.Length == PolicyPrefix.Length) sb.Append("Game");
            int h = 17;
            unchecked { foreach (char c in exePath ?? "") h = h * 31 + char.ToLowerInvariant(c); }
            sb.Append('_').Append(((uint)h).ToString("X6").Substring(0, 6));
            return sb.ToString();
        }

        private static List<string> LoadPolicyNames()
        {
            var list = new List<string>();
            string raw = Settings.LoadStr(QosPolicyNamesKey, "");
            foreach (string s in raw.Split(';')) if (s.Length > 0) list.Add(s);
            return list;
        }

        private static void SavePolicyNames(List<string> names)
        {
            Settings.SaveStr(QosPolicyNamesKey, string.Join(";", names.ToArray()));
        }

        private static string PsQuote(string s)
        {
            return "'" + (s ?? "").Replace("'", "''") + "'";
        }

        private static bool RunPowerShellScript(string script, out string stdout)
        {
            stdout = "";
            string tmp = Path.Combine(Path.GetTempPath(), "Aegis.ps." + Guid.NewGuid().ToString("N") + ".ps1");
            try
            {
                File.WriteAllText(tmp, script, Encoding.UTF8);
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + tmp + "\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (Process p = Process.Start(psi))
                {
                    string outText = p.StandardOutput.ReadToEnd();
                    string errText = p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        Logger.Log("网络优先级：PowerShell 执行超时");
                        return false;
                    }
                    stdout = outText;
                    if (p.ExitCode != 0)
                    {
                        Logger.Log("网络优先级：PowerShell 执行失败(exit=" + p.ExitCode + ")：" + errText.Trim());
                        return false;
                    }
                    return true;
                }
            }
            catch (Exception ex) { Logger.Log("网络优先级：无法执行 PowerShell：" + ex.Message); return false; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        private static bool ApplyQosPolicy(string policyName, string exePath)
        {
            string script =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "if (Get-NetQosPolicy -Name " + PsQuote(policyName) + " -ErrorAction SilentlyContinue) {\r\n" +
                "    Remove-NetQosPolicy -Name " + PsQuote(policyName) + " -Confirm:$false\r\n" +
                "}\r\n" +
                "New-NetQosPolicy -Name " + PsQuote(policyName) + " -AppPathNameMatchCondition " + PsQuote(exePath) +
                " -DSCPAction " + GamingDscp + " -NetworkProfile All | Out-Null\r\n" +
                "Write-Output DONE\r\n";
            string stdout;
            bool ok = RunPowerShellScript(script, out stdout);
            return ok && stdout.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool RemoveQosPolicy(string policyName)
        {
            string script =
                "if (Get-NetQosPolicy -Name " + PsQuote(policyName) + " -ErrorAction SilentlyContinue) {\r\n" +
                "    Remove-NetQosPolicy -Name " + PsQuote(policyName) + " -Confirm:$false -ErrorAction Stop\r\n" +
                "}\r\n" +
                "Write-Output DONE\r\n";
            string stdout;
            bool ok = RunPowerShellScript(script, out stdout);
            return ok && stdout.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 对目录里每个有可执行文件路径的游戏都打上 QoS 优先级标记；
        // 可重复调用来刷新——会自动补上新加的游戏、摘掉已经不在目录里的游戏对应的策略。
        public static bool Enable(List<GameProfile> games)
        {
            bool irqOk = irqEngine.Enable(EnumerateNicDeviceIds());

            var newNames = new List<string>();
            if (games != null)
            {
                foreach (GameProfile g in games)
                {
                    if (string.IsNullOrEmpty(g.ExecutablePath)) continue;
                    string name = SanitizePolicyName(g.Name, g.ExecutablePath);
                    if (ApplyQosPolicy(name, g.ExecutablePath)) newNames.Add(name);
                    else Logger.Log("网络优先级：" + g.Name + " 的 QoS 策略创建失败");
                }
            }

            List<string> oldNames = LoadPolicyNames();
            foreach (string old in oldNames) if (!newNames.Contains(old)) RemoveQosPolicy(old);
            SavePolicyNames(newNames);

            bool anyOk = irqOk || newNames.Count > 0;
            if (anyOk) Settings.Save(EnabledKey, true);
            return anyOk;
        }

        public static bool Disable()
        {
            bool irqOk = irqEngine.Disable(EnumerateNicDeviceIds());

            List<string> names = LoadPolicyNames();
            bool qosOk = true;
            foreach (string name in names) if (!RemoveQosPolicy(name)) qosOk = false;
            if (qosOk) SavePolicyNames(new List<string>());

            bool allOk = irqOk && qosOk;
            if (allOk) Settings.Save(EnabledKey, false);
            return allOk;
        }
    }
}
