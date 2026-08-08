// @author bdth 2074055628@qq.com
// 文件用途 网卡中断亲和优化与游戏流量 QoS 优先级标记 开启 关闭并恢复

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;

namespace PaviseApp
{
    internal static class NetworkAffinityTweak
    {
        private static readonly IrqAffinityEngine irqEngine =
            new IrqAffinityEngine("NicAffinityOnByPavise", "NicAff_", "网卡中断亲和");

        private const string QosPolicyNamesKey = "NetQosPolicyNames";
        private const string EnabledKey = "NetPriorityOnByPavise";
        private const string PolicyPrefix = "Pavise_";
        private const int GamingDscp = 46;

        public static bool EnabledByPavise { get { return Settings.Load(EnabledKey, false); } }

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

        private static bool SavePolicyNames(List<string> names)
        {
            string joined = string.Join(";", names.ToArray());
            Settings.SaveStr(QosPolicyNamesKey, joined);
            return Settings.LoadStr(QosPolicyNamesKey, "") == joined;
        }

        private static IDictionary<string, string> QosArgs(string name, string exePath)
        {
            var d = new Dictionary<string, string> { { "PAVISE_QOS_NAME", name ?? "" } };
            if (exePath != null) d["PAVISE_QOS_PATH"] = exePath;
            return d;
        }

        private static bool ApplyQosPolicy(string policyName, string exePath)
        {
            string script =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "$n = $env:PAVISE_QOS_NAME\r\n" +
                "if (Get-NetQosPolicy -Name $n -ErrorAction SilentlyContinue) {\r\n" +
                "    Remove-NetQosPolicy -Name $n -Confirm:$false\r\n" +
                "}\r\n" +
                "New-NetQosPolicy -Name $n -AppPathNameMatchCondition $env:PAVISE_QOS_PATH" +
                " -DSCPAction " + GamingDscp + " -NetworkProfile All | Out-Null\r\n" +
                "Write-Output DONE\r\n";
            string stdout;
            bool ok = PsRunner.Run(script, "网络优先级", 15000, QosArgs(policyName, exePath), out stdout);
            return ok && stdout.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool RemoveQosPolicy(string policyName)
        {
            string script =
                "$n = $env:PAVISE_QOS_NAME\r\n" +
                "if (Get-NetQosPolicy -Name $n -ErrorAction SilentlyContinue) {\r\n" +
                "    Remove-NetQosPolicy -Name $n -Confirm:$false -ErrorAction Stop\r\n" +
                "}\r\n" +
                "Write-Output DONE\r\n";
            string stdout;
            bool ok = PsRunner.Run(script, "网络优先级", 15000, QosArgs(policyName, null), out stdout);
            return ok && stdout.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) >= 0;
        }

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
            var keptNames = new List<string>(newNames);
            foreach (string old in oldNames)
                if (!keptNames.Contains(old) && !RemoveQosPolicy(old)) keptNames.Add(old);

            if (!SavePolicyNames(keptNames))
            {
                foreach (string name in newNames) RemoveQosPolicy(name);
                Logger.Log("网络优先级：策略名无法持久化，已撤回本轮创建的 QoS 策略");
                return irqOk;
            }

            bool anyOk = irqOk || newNames.Count > 0;
            if (anyOk) Settings.Save(EnabledKey, true);
            return anyOk;
        }

        public static bool HasResidue() { return Settings.Load(EnabledKey, false); }

        public static bool Disable()
        {
            bool irqOk = irqEngine.Disable(EnumerateNicDeviceIds());

            List<string> names = LoadPolicyNames();
            bool qosOk = true;
            foreach (string name in names) if (!RemoveQosPolicy(name)) qosOk = false;

            if (qosOk && !SavePolicyNames(new List<string>())) qosOk = false;

            bool allOk = irqOk && qosOk;
            if (allOk) Settings.Save(EnabledKey, false);
            return allOk;
        }
    }
}
