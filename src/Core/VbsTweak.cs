// @author bdth 2074055628@qq.com
// 文件用途 关闭并恢复 VBS 内存完整性和虚拟机监控程序

using System;
using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace AegisApp
{
    internal static class VbsTweak
    {
        private const string DgKey   = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
        private const string HvciKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity";

        private static readonly ReversibleReg Vbs = new ReversibleReg(
            Registry.LocalMachine, DgKey, "EnableVirtualizationBasedSecurity", RegistryValueKind.DWord, "PrevVbsEnable");
        private static readonly ReversibleReg Hvci = new ReversibleReg(
            Registry.LocalMachine, HvciKey, "Enabled", RegistryValueKind.DWord, "PrevVbsHvci");

        private static readonly object lk = new object();

        public struct State
        {
            public bool WmiOk;
            public bool VbsRunning;
        }

        public static bool DisabledByAegis { get { return Settings.Load("VbsDisabledByAegis", false); } }

        public static State Query()
        {
            var st = new State();
            try
            {
                using (var s = new ManagementObjectSearcher(
                    @"root\Microsoft\Windows\DeviceGuard",
                    "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard"))
                using (var res = s.Get())
                {
                    foreach (ManagementObject mo in res)
                    {
                        using (mo)
                        {
                            st.WmiOk = true;
                            object vs = mo["VirtualizationBasedSecurityStatus"];
                            if (vs != null) st.VbsRunning = Convert.ToInt32(vs) == 2;
                        }
                        break;
                    }
                }
            }
            catch { st.WmiOk = false; }
            return st;
        }

        public static bool Disable()
        {
            lock (lk)
            {
                try
                {
                    if (Settings.LoadStr("PrevHvLaunch", "").Length == 0)
                    {
                        bool readOk;
                        string previous = ReadHvLaunch(out readOk);
                        if (!readOk)
                        {
                            Logger.Log("无法读取 hypervisorlaunchtype 原状态，已取消修改");
                            return false;
                        }
                        Settings.SaveStr("PrevHvLaunch", previous);
                        if (Settings.LoadStr("PrevHvLaunch", "") != previous) return false;
                    }
                    bool registryOk = Vbs.Apply(0) & Hvci.Apply(0);
                    if (!registryOk)
                    {
                        Vbs.Restore(); Hvci.Restore();
                        Logger.Log("关闭 VBS/内存完整性写入或回读失败，已回滚");
                        return false;
                    }
                    int code;
                    RunBcd("/set hypervisorlaunchtype off", out code);
                    if (code != 0)
                    {
                        if (Vbs.Restore() & Hvci.Restore()) Settings.SaveStr("PrevHvLaunch", "");
                        Logger.Log("停用 hypervisor 失败（bcdedit rc=" + code + "），已回滚注册表改动");
                        return false;
                    }
                    Settings.Save("VbsDisabledByAegis", true);
                    if (!Settings.Load("VbsDisabledByAegis", false))
                    {
                        int rollbackCode;
                        RunBcd("/set hypervisorlaunchtype "
                            + NormHvLaunch(Settings.LoadStr("PrevHvLaunch", "auto")), out rollbackCode);
                        Vbs.Restore(); Hvci.Restore();
                        if (rollbackCode == 0) Settings.SaveStr("PrevHvLaunch", "");
                        Logger.Log("无法持久化 VBS 修改状态，已回滚本轮设置");
                        return false;
                    }
                    Logger.Log("VBS/内存完整性已关，hypervisor 已停用（重启后生效；WSL2/Docker/Hyper-V 将失效）");
                    return true;
                }
                catch (Exception ex) { Logger.Log("关闭 VBS/hypervisor 失败：" + ex.Message); return false; }
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                try
                {
                    string savedHvLaunch = Settings.LoadStr("PrevHvLaunch", "");
                    if (!Vbs.HasBackup && !Hvci.HasBackup && savedHvLaunch.Length == 0 && !DisabledByAegis)
                        return true;
                    bool ok = Vbs.Restore() & Hvci.Restore();
                    string hv = NormHvLaunch(savedHvLaunch.Length == 0 ? "auto" : savedHvLaunch);
                    int code;
                    RunBcd("/set hypervisorlaunchtype " + hv, out code);
                    if (code != 0) ok = false;
                    if (ok)
                    {
                        Settings.Save("VbsDisabledByAegis", false);
                        if (Settings.Load("VbsDisabledByAegis", true))
                        {
                            Logger.Log("系统设置已还原，但状态标志写入失败；下次启动将再次校正");
                            return false;
                        }
                        Settings.SaveStr("PrevHvLaunch", "");
                        Logger.Log("已还原：VBS/内存完整性 + hypervisorlaunchtype → " + hv + "（重启后生效）");
                    }
                    else Logger.Log("还原 VBS/hypervisor 未完全成功（bcdedit rc=" + code + "），快照保留，可再试一次");
                    return ok;
                }
                catch (Exception ex) { Logger.Log("还原 VBS/hypervisor 失败：" + ex.Message); return false; }
            }
        }

        private static string ReadHvLaunch(out bool ok)
        {
            int code;
            string o = RunBcd("/enum {current}", out code);
            ok = code == 0 && !string.IsNullOrEmpty(o);
            if (!ok) return null;
            foreach (string line in o.Split('\n'))
            {
                string t = line.Trim();
                if (t.StartsWith("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = t.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2) return NormHvLaunch(parts[parts.Length - 1]);
                }
            }
            return "auto";
        }

        private static string NormHvLaunch(string s)
        {
            if (string.IsNullOrEmpty(s)) return "auto";
            switch (s.Trim().ToLowerInvariant())
            {
                case "off": return "off";
                case "on": return "on";
                case "optout": return "optout";
                default: return "auto";
            }
        }

        private static string RunBcd(string args, out int code)
        {
            code = -1;
            try
            {
                var psi = new ProcessStartInfo(System.IO.Path.Combine(Environment.SystemDirectory, "bcdedit.exe"), args);
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginErrorReadLine();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    code = p.ExitCode;
                    return o;
                }
            }
            catch { return ""; }
        }
    }
}
