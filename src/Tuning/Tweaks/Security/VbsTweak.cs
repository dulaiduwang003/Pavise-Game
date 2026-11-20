using System;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class VbsTweak
    {
        public struct State
        {
            public bool WmiOk;

            public bool VbsRunning;
        }

        private const string DgKey = "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard";

        private const string HvciKey = "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity";

        private static readonly ReversibleReg Vbs = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard", "EnableVirtualizationBasedSecurity", RegistryValueKind.DWord, "PrevVbsEnable");

        private static readonly ReversibleReg Hvci = new ReversibleReg(Registry.LocalMachine, "SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity", "Enabled", RegistryValueKind.DWord, "PrevVbsHvci");

        private static readonly object lk = new object();

        public static bool DisabledByPavise
        {
            get
            {
                return Settings.Load("VbsDisabledByPavise", false);
            }
        }

        internal static bool OwnsState
        {
            get
            {
                if (!DisabledByPavise && !Vbs.HasBackup && !Hvci.HasBackup)
                {
                    return Settings.LoadStr("PrevHvLaunch", "").Length > 0;
                }
                return true;
            }
        }

        internal static bool CurrentlyConfiguredOff()
        {
            if (!Vbs.Matches(0) || !Hvci.Matches(0))
            {
                return false;
            }
            bool ok;
            if (ReadHvLaunch(out ok) == "off")
            {
                return ok;
            }
            return false;
        }

        public static bool BlockedReason(out string reasonKey)
        {
            reasonKey = null;
            if (ReadDword("SOFTWARE\\Policies\\Microsoft\\Windows\\DeviceGuard", "EnableVirtualizationBasedSecurity") == 1)
            {
                reasonKey = "vbs.blocked.policy";
                return true;
            }
            if (ReadDword("SYSTEM\\CurrentControlSet\\Control\\DeviceGuard", "Locked") == 1 || ReadDword("SYSTEM\\CurrentControlSet\\Control\\DeviceGuard\\Scenarios\\HypervisorEnforcedCodeIntegrity", "Locked") == 1)
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
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey(path))
                {
                    if (registryKey == null)
                    {
                        return -1;
                    }
                    object value = registryKey.GetValue(name);
                    return (value is int) ? ((int)value) : (-1);
                }
            }
            catch
            {
                return -1;
            }
        }

        public static State Query()
        {
            State result = default(State);
            try
            {
                using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("root\\Microsoft\\Windows\\DeviceGuard", "SELECT VirtualizationBasedSecurityStatus FROM Win32_DeviceGuard"))
                {
                    using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
                    {
                        using (ManagementObjectCollection.ManagementObjectEnumerator managementObjectEnumerator = managementObjectCollection.GetEnumerator())
                        {
                            if (managementObjectEnumerator.MoveNext())
                            {
                                ManagementObject managementObject = (ManagementObject)managementObjectEnumerator.Current;
                                using (managementObject)
                                {
                                    result.WmiOk = true;
                                    object instance = managementObject["VirtualizationBasedSecurityStatus"];
                                    if (instance != null)
                                    {
                                        result.VbsRunning = Convert.ToInt32(instance) == 2;
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                result.WmiOk = false;
            }
            return result;
        }

#if PAVISE_SELFTEST
        internal delegate string BcdCall(string args, out int code);
        // 自检构建里关闭和恢复 VBS 整个交给替身 没注入就当场拒绝 HKLM 的设备防护键和启动配置都碰不到
        internal static Func<bool> DisableForTest = null;
        internal static Func<bool> RestoreForTest = null;
        // bcdedit 调用交给替身 没注入同样拒绝 本该交给 bcdedit.exe 的参数只会交到启动进程的接缝 接缝没注入也拒绝
        internal static BcdCall BcdForTest = null;
        internal static BcdCall BcdStartForTest = null;
#endif

        public static bool Disable()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
#if PAVISE_SELFTEST
            if (DisableForTest == null) throw new InvalidOperationException("Unmocked VBS disable");
            if (DisableForTest != null) return DisableForTest();
#endif
            return false;
#else
            lock (lk)
            {
                try
                {
                    string reasonKey;
                    if (BlockedReason(out reasonKey))
                    {
                        Logger.Log(() => Lang.T(reasonKey));
                        return false;
                    }
                    if (Settings.LoadStr("PrevHvLaunch", "").Length == 0)
                    {
                        bool ok;
                        string hvLaunch = ReadHvLaunch(out ok);
                        if (!ok)
                        {
                            Logger.Warn(Lang.T("log.vbstweak.1"));
                            return false;
                        }
                        if (!Settings.SaveStrDurable("PrevHvLaunch", hvLaunch))
                        {
                            return false;
                        }
                        if (Settings.LoadStr("PrevHvLaunch", "") != hvLaunch)
                        {
                            return false;
                        }
                    }
                    if (!(Vbs.Apply(0) & Hvci.Apply(0)))
                    {
                        Vbs.Restore();
                        Hvci.Restore();
                        if (!DisabledByPavise)
                        {
                            Settings.SaveStr("PrevHvLaunch", "");
                        }
                        Logger.Log(() => Lang.T("log.vbstweak.2"));
                        return false;
                    }
                    int code;
                    RunBcd("/set hypervisorlaunchtype off", out code);
                    if (code != 0)
                    {
                        Vbs.Restore();
                        Hvci.Restore();
                        Logger.Log(() => Lang.T("log.vbstweak.3") + code + Lang.T("log.vbstweak.4"));
                        return false;
                    }
                    Settings.Save("VbsDisabledByPavise", true);
                    if (!Settings.Load("VbsDisabledByPavise", false))
                    {
                        string normHvLaunchResult = NormHvLaunch(Settings.LoadStr("PrevHvLaunch", "auto"));
                        int code2;
                        RunBcd((normHvLaunchResult == "__pavise_absent__") ? "/deletevalue hypervisorlaunchtype" : ("/set hypervisorlaunchtype " + normHvLaunchResult), out code2);
                        Vbs.Restore();
                        Hvci.Restore();
                        if (code2 == 0)
                        {
                            Settings.SaveStr("PrevHvLaunch", "");
                        }
                        Logger.Log(() => Lang.T("log.vbstweak.5"));
                        return false;
                    }
                    Logger.Log(() => Lang.T("log.vbstweak.6"));
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log(() => Lang.T("log.vbstweak.7") + ex.Message);
                    return false;
                }
            }
#endif
        }

        public static bool Restore()
        {
#if PAVISE_SELFTEST || PAVISE_PERFLAB
#if PAVISE_SELFTEST
            if (RestoreForTest == null) throw new InvalidOperationException("Unmocked VBS restore");
            if (RestoreForTest != null) return RestoreForTest();
#endif
            return !OwnsState;
#else
            lock (lk)
            {
                try
                {
                    string prevHvLaunchValue = Settings.LoadStr("PrevHvLaunch", "");
                    if (!Vbs.HasBackup && !Hvci.HasBackup && prevHvLaunchValue.Length == 0 && !DisabledByPavise)
                    {
                        return true;
                    }
                    bool registryRestored = Vbs.Restore() & Hvci.Restore();
                    bool hasOwnedState = DisabledByPavise || prevHvLaunchValue.Length > 0;
                    string normHvLaunchResult = NormHvLaunch((prevHvLaunchValue.Length == 0) ? "auto" : prevHvLaunchValue);
                    int code = 0;
                    if (hasOwnedState)
                    {
                        bool ok;
                        string current = ReadHvLaunch(out ok);
                        if (!ok)
                        {
                            registryRestored = false;
                        }
                        else if (ShouldRestoreHvLaunch(current, normHvLaunchResult))
                        {
                            RunBcd((normHvLaunchResult == "__pavise_absent__") ? "/deletevalue hypervisorlaunchtype" : ("/set hypervisorlaunchtype " + normHvLaunchResult), out code);
                            if (code != 0)
                            {
                                registryRestored = false;
                            }
                        }
                    }
                    if (registryRestored)
                    {
                        Settings.Save("VbsDisabledByPavise", false);
                        if (Settings.Load("VbsDisabledByPavise", true))
                        {
                            Logger.Log(() => Lang.T("log.vbstweak.8"));
                            return false;
                        }
                        Settings.SaveStr("PrevHvLaunch", "");
                        Logger.Log(() => hasOwnedState ? (Lang.T("log.vbstweak.9") + normHvLaunchResult + Lang.T("log.nettweak.4")) : Lang.T("log.vbstweak.10"));
                    }
                    else
                    {
                        Logger.Log(() => Lang.T("log.vbstweak.11") + code + Lang.T("log.vbstweak.12"));
                    }
                    return registryRestored;
                }
                catch (Exception ex)
                {
                    Logger.Log(() => Lang.T("log.vbstweak.13") + ex.Message);
                    return false;
                }
            }
#endif
        }

        internal static bool ShouldRestoreHvLaunch(string current, string original)
        {
            if (current == "off")
            {
                return current != original;
            }
            return false;
        }

        private static string ReadHvLaunch(out bool ok)
        {
            int code;
            string runBcdResult = RunBcd("/enum {current}", out code);
            ok = code == 0 && !string.IsNullOrEmpty(runBcdResult);
            if (!ok)
            {
                return null;
            }
            string[] runBcdResultParts = runBcdResult.Split('\n');
            foreach (string entryText in runBcdResultParts)
            {
                string entryTextTrimmed = entryText.Trim();
                if (entryTextTrimmed.StartsWith("hypervisorlaunchtype", StringComparison.OrdinalIgnoreCase))
                {
                    string[] entryTextTrimmedParts = entryTextTrimmed.Split(new char[2] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (entryTextTrimmedParts.Length >= 2)
                    {
                        return NormHvLaunch(entryTextTrimmedParts[entryTextTrimmedParts.Length - 1]);
                    }
                }
            }
            return "__pavise_absent__";
        }

        private static string NormHvLaunch(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "auto";
            }
            if (s == "__pavise_absent__")
            {
                return "__pavise_absent__";
            }
            switch (s.Trim().ToLowerInvariant())
            {
            case "off":
                return "off";
            case "on":
                return "on";
            case "optout":
                return "optout";
            default:
                return "auto";
            }
        }

        internal static string RunBcd(string args, out int code)
        {
#if PAVISE_SELFTEST
            if (BcdForTest == null) throw new InvalidOperationException("Unmocked VBS bcdedit call");
            if (BcdForTest != null) return BcdForTest(args, out code);
            return StartBcd(args, out code);
#else
            code = -1;
            try
            {
                ProcessStartInfo processStartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"), args);
                processStartInfo.CreateNoWindow = true;
                processStartInfo.UseShellExecute = false;
                processStartInfo.RedirectStandardOutput = true;
                processStartInfo.RedirectStandardError = true;
                using (Process process = Process.Start(processStartInfo))
                {
                    StringBuilder sb = new StringBuilder();
                    process.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null)
                        {
                            lock (sb)
                            {
                                sb.Append(e.Data).Append('\n');
                            }
                        }
                    };
                    process.ErrorDataReceived += delegate
                    {
                    };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(15000))
                    {
                        try
                        {
                            process.Kill();
                        }
                        catch
                        {
                        }
                        Logger.Log(() => Lang.T("log.vbstweak.14") + args + " ");
                        return "";
                    }
                    process.WaitForExit();
                    code = process.ExitCode;
                    lock (sb)
                    {
                        return sb.ToString();
                    }
                }
            }
            catch
            {
                return "";
            }
#endif
        }
#if PAVISE_SELFTEST
        internal static string StartBcd(string args, out int code)
        {
            if (BcdStartForTest == null) throw new InvalidOperationException("Selftest builds cannot start bcdedit.exe for VBS");
            return BcdStartForTest(args, out code);
        }
#endif
    }
}
