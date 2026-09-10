// @author bdth 2074055628@qq.com
// 文件用途 关闭并恢复 VBS 内存完整性和虚拟机监控程序
using System;
using System.Diagnostics;
using System.Management;
using Microsoft.Win32;

namespace PaviseApp
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

        public static bool DisabledByPavise { get { return Settings.Load("VbsDisabledByPavise", false); } }

        // 关 VBS 等于关虚拟机监控程序 Hyper-V WSL2 Docker 沙盒都跟着停
        //   vmcompute 和 vmms 两个服务任一存在就是有人在用 Credential Guard 开着是企业机
        //   原先只挡极限档的强制 极限下架后仅用于说明 用户手开不经此门
        internal static bool VirtualizationInUse(bool vmcomputeInstalled, bool vmmsInstalled, int lsaCfgFlags)
        {
            return vmcomputeInstalled || vmmsInstalled || lsaCfgFlags > 0;
        }

        public static bool VirtualizationInUse()
        {
            return VirtualizationInUse(ServiceInstalled("vmcompute"), ServiceInstalled("vmms"),
                Math.Max(ReadDword(@"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags"), 0));
        }

        // 只认真正注册且没被禁用的服务 Hyper-V 卸载后常留一个没有 ImagePath 的空壳键 那不算在用
        private static bool ServiceInstalled(string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                {
                    if (k == null) return false;
                    string image = k.GetValue("ImagePath") as string;
                    if (string.IsNullOrEmpty(image)) return false;
                    object start = k.GetValue("Start");
                    return !(start is int) || (int)start != 4;
                }
            }
            catch { return false; }
        }

        public static bool BlockedReason(out string reasonKey)
        {
            reasonKey = null;
            if (ReadDword(@"SOFTWARE\Policies\Microsoft\Windows\DeviceGuard",
                    "EnableVirtualizationBasedSecurity") == 1)
            {
                reasonKey = "vbs.blocked.policy";
                return true;
            }
            if (ReadDword(DgKey, "Locked") == 1 || ReadDword(HvciKey, "Locked") == 1)
            {
                reasonKey = "vbs.blocked.lock";
                return true;
            }
            return false;
        }

        private static int ReadDword(string path, string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (k == null) return -1;
                    object v = k.GetValue(name);
                    return v is int ? (int)v : -1;
                }
            }
            catch { return -1; }
        }

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
                    string blockKey;
                    if (BlockedReason(out blockKey))
                    {
                        Logger.Log(Lang.T(blockKey));
                        return false;
                    }
                    if (Settings.LoadStr("PrevHvLaunch", "").Length == 0)
                    {
                        bool readOk;
                        string previous = ReadHvLaunch(out readOk);
                        if (!readOk)
                        {
                            Logger.Warn(Lang.T("log.vbstweak.1"));
                            return false;
                        }
                        Settings.SaveStr("PrevHvLaunch", previous);
                        if (Settings.LoadStr("PrevHvLaunch", "") != previous) return false;
                    }
                    bool registryOk = Vbs.Apply(0) & Hvci.Apply(0);
                    if (!registryOk)
                    {
                        Vbs.Restore(); Hvci.Restore();
                        if (!DisabledByPavise) Settings.SaveStr("PrevHvLaunch", "");
                        Logger.Log(Lang.T("log.vbstweak.2"));
                        return false;
                    }
                    int code;
                    RunBcd("/set hypervisorlaunchtype off", out code);
                    if (code != 0)
                    {
                        Vbs.Restore(); Hvci.Restore();
                        Logger.Log(Lang.T("log.vbstweak.3") + code + Lang.T("log.vbstweak.4"));
                        return false;
                    }
                    Settings.Save("VbsDisabledByPavise", true);
                    if (!Settings.Load("VbsDisabledByPavise", false))
                    {
                        int rollbackCode;
                        string back = NormHvLaunch(Settings.LoadStr("PrevHvLaunch", "auto"));
                        RunBcd(back == ReversibleReg.Absent
                            ? "/deletevalue hypervisorlaunchtype"
                            : "/set hypervisorlaunchtype " + back, out rollbackCode);
                        Vbs.Restore(); Hvci.Restore();
                        if (rollbackCode == 0) Settings.SaveStr("PrevHvLaunch", "");
                        Logger.Log(Lang.T("log.vbstweak.5"));
                        return false;
                    }
                    Logger.Log(Lang.T("log.vbstweak.6"));
                    return true;
                }
                catch (Exception ex) { Logger.Log(Lang.T("log.vbstweak.7") + ex.Message); return false; }
            }
        }

        public static bool Restore()
        {
            lock (lk)
            {
                try
                {
                    string savedHvLaunch = Settings.LoadStr("PrevHvLaunch", "");
                    if (!Vbs.HasBackup && !Hvci.HasBackup && savedHvLaunch.Length == 0 && !DisabledByPavise)
                        return true;
                    bool ok = Vbs.Restore() & Hvci.Restore();
                    bool bcdOurs = DisabledByPavise || savedHvLaunch.Length > 0;
                    string hv = NormHvLaunch(savedHvLaunch.Length == 0 ? "auto" : savedHvLaunch);
                    int code = 0;
                    if (bcdOurs)
                    {
                        RunBcd(hv == ReversibleReg.Absent
                            ? "/deletevalue hypervisorlaunchtype"
                            : "/set hypervisorlaunchtype " + hv, out code);
                        if (code != 0) ok = false;
                    }
                    if (ok)
                    {
                        Settings.Save("VbsDisabledByPavise", false);
                        if (Settings.Load("VbsDisabledByPavise", true))
                        {
                            Logger.Log(Lang.T("log.vbstweak.8"));
                            return false;
                        }
                        Settings.SaveStr("PrevHvLaunch", "");
                        Logger.Log(bcdOurs
                            ? Lang.T("log.vbstweak.9") + hv + Lang.T("log.nettweak.4")
                            : Lang.T("log.vbstweak.10"));
                    }
                    else Logger.Log(Lang.T("log.vbstweak.11") + code + Lang.T("log.vbstweak.12"));
                    return ok;
                }
                catch (Exception ex) { Logger.Log(Lang.T("log.vbstweak.13") + ex.Message); return false; }
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
            return ReversibleReg.Absent;
        }

        private static string NormHvLaunch(string s)
        {
            if (string.IsNullOrEmpty(s)) return "auto";
            if (s == ReversibleReg.Absent) return ReversibleReg.Absent;
            switch (s.Trim().ToLowerInvariant())
            {
                case "off": return "off";
                case "on": return "on";
                case "optout": return "optout";
                default: return "auto";
            }
        }

        internal static string RunBcd(string args, out int code)
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
                    var sb = new System.Text.StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) { sb.Append(e.Data).Append('\n'); } };
                    p.ErrorDataReceived += (s, e) => { };
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    if (!p.WaitForExit(15000))
                    {
                        try { p.Kill(); } catch { }
                        Logger.Log(Lang.T("log.vbstweak.14") + args + " ");
                        return "";
                    }
                    p.WaitForExit();
                    code = p.ExitCode;
                    lock (sb) { return sb.ToString(); }
                }
            }
            catch { return ""; }
        }
    }
}
