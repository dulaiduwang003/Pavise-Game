// @author bdth 2074055628@qq.com
// 文件用途 游戏时给被压制的后台进程写 QoS 上行限速策略 台架实测 230Mbps 压到 7.5Mbps

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class UploadYield
    {
        public const string ThrottleKbps = "512";
        private const string QosRoot = @"SOFTWARE\Policies\Microsoft\Windows\QoS";
        private const string Prefix = "PaviseYield_";
        private const int MaxPolicies = 48;

        private static readonly object lk = new object();
        private static bool anyApplied;

        public static int Apply(IEnumerable<string> processNames)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in processNames)
            {
                if (string.IsNullOrEmpty(name)) continue;
                string exe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? name : name + ".exe";
                if (wanted.Count < MaxPolicies) wanted.Add(exe);
            }
            if (wanted.Count == 0) return 0;
            int written = 0;
            lock (lk)
            {
                try
                {
                    foreach (string exe in wanted)
                    {
                        string sub = QosRoot + "\\" + Prefix + SafeName(exe);
                        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(sub))
                        {
                            key.SetValue("Version", "1.0", RegistryValueKind.String);
                            key.SetValue("Application Name", exe, RegistryValueKind.String);
                            key.SetValue("Protocol", "*", RegistryValueKind.String);
                            key.SetValue("Local Port", "*", RegistryValueKind.String);
                            key.SetValue("Local IP", "*", RegistryValueKind.String);
                            key.SetValue("Local IP Prefix Length", "*", RegistryValueKind.String);
                            key.SetValue("Remote Port", "*", RegistryValueKind.String);
                            key.SetValue("Remote IP", "*", RegistryValueKind.String);
                            key.SetValue("Remote IP Prefix Length", "*", RegistryValueKind.String);
                            key.SetValue("DSCP Value", "-1", RegistryValueKind.String);
                            key.SetValue("Throttle Rate", ThrottleKbps, RegistryValueKind.String);
                        }
                        written++;
                    }
                    if (written > 0)
                    {
                        anyApplied = true;
                        RefreshPolicyAsync();
                        Logger.Log("上传让位：已对 " + written + " 个被压制的后台进程限制上行（每进程 "
                            + ThrottleKbps + " kbps），退出游戏解除");
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log("上传让位：写入策略失败（" + ex.Message + "）");
                }
            }
            return written;
        }

        public static void Clear()
        {
            lock (lk)
            {
                int removed = 0;
                try
                {
                    using (RegistryKey root = Registry.LocalMachine.OpenSubKey(QosRoot, true))
                    {
                        if (root != null)
                            foreach (string sub in root.GetSubKeyNames())
                                if (sub.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                                {
                                    try { root.DeleteSubKeyTree(sub, false); removed++; }
                                    catch { }
                                }
                    }
                }
                catch { }
                if (removed > 0 || anyApplied)
                {
                    anyApplied = false;
                    RefreshPolicyAsync();
                    if (removed > 0)
                        Logger.Log("上传让位：已解除 " + removed + " 条上行限速");
                }
            }
        }

        public static void HealFromCrash()
        {
            Clear();
        }

        private static string SafeName(string exe)
        {
            var sb = new System.Text.StringBuilder(exe.Length);
            foreach (char c in exe)
                sb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        private static void RefreshPolicyAsync()
        {
            var t = new Thread(delegate ()
            {
                try
                {
                    using (Process p = Process.Start(new ProcessStartInfo("gpupdate.exe", "/target:computer /force")
                    { UseShellExecute = false, CreateNoWindow = true }))
                        p.WaitForExit(90000);
                }
                catch { }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
