using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static partial class SystemAudit
    {

        private static string LinkKind()
        {
            bool hasWireless = false;
            bool hasWired = false;
            foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != OperationalStatus.Up)
                {
                    continue;
                }
                NetworkInterfaceType networkInterfaceType = networkInterface.NetworkInterfaceType;
                if (networkInterfaceType == NetworkInterfaceType.Loopback || networkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }
                bool hasGateway = false;
                try
                {
                    foreach (GatewayIPAddressInformation gatewayAddress in networkInterface.GetIPProperties().GatewayAddresses)
                    {
                        if (gatewayAddress != null && gatewayAddress.Address != null && !gatewayAddress.Address.Equals(IPAddress.Any))
                        {
                            hasGateway = true;
                            break;
                        }
                    }
                }
                catch
                {
                }
                if (hasGateway)
                {
                    if (networkInterfaceType == NetworkInterfaceType.Wireless80211)
                    {
                        hasWireless = true;
                    }
                    else
                    {
                        hasWired = true;
                    }
                }
            }
            if (hasWired && hasWireless)
            {
                return "both";
            }
            if (hasWired)
            {
                return "wired";
            }
            if (hasWireless)
            {
                return "wifi";
            }
            return "none";
        }

        private static void BuildHardwareHealth(AuditReport report, Facts facts)
        {
            bool warn;
            string memChannelTextResult = MemChannelText(facts.MemModuleGb, out warn);
            if (memChannelTextResult != null)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.memchannel.1"),
                    Value = memChannelTextResult,
                    Note = (warn ? Lang.T("t.memchannel.6") : Lang.T("t.memchannel.7")),
                    Evidence = "机制明确",
                    Warn = warn
                });
            }
            if (facts.MemModules > 0)
            {
                bool memoryBelowRatedSpeed = facts.MemConfiguredMhz > 0 && facts.MemRatedMhz > 0 && facts.MemConfiguredMhz * 10 < facts.MemRatedMhz * 9;
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.1"),
                    Value = ((facts.MemConfiguredMhz > 0) ? (facts.MemConfiguredMhz + " MHz" + ((facts.MemRatedMhz > facts.MemConfiguredMhz) ? (Lang.T("t.systemaudithardware.2") + facts.MemRatedMhz) : "")) : Lang.T("t.systemaudithardware.3")),
                    Note = (memoryBelowRatedSpeed ? Lang.T("t.systemaudithardware.4") : Lang.T("t.systemaudithardware.5")),
                    Evidence = (memoryBelowRatedSpeed ? "台架实测" : "机制明确"),
                    Warn = memoryBelowRatedSpeed
                });
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.6"),
                    Value = facts.MemModules + Lang.T("t.systemaudithardware.7"),
                    Note = ((facts.MemModules == 1) ? Lang.T("t.systemaudithardware.8") : Lang.T("t.systemaudithardware.9")),
                    Evidence = "机制明确",
                    Warn = (facts.MemModules == 1)
                });
            }
            if (facts.AllHz != null && facts.AllHz.Count > 1)
            {
                int maxValue = int.MaxValue;
                int hardwareHealthValue = 0;
                List<string> texts = new List<string>();
                foreach (int allHz in facts.AllHz)
                {
                    if (allHz < maxValue)
                    {
                        maxValue = allHz;
                    }
                    if (allHz > hardwareHealthValue)
                    {
                        hardwareHealthValue = allHz;
                    }
                    texts.Add(allHz + "Hz");
                }
                bool valuesMatch = hardwareHealthValue - maxValue > 5;
                bool belowPreferredBuild = facts.OsBuild > 0 && facts.OsBuild < 26100;
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.10"),
                    Value = string.Join(" / ", texts.ToArray()),
                    Note = ((valuesMatch && belowPreferredBuild) ? Lang.T("t.systemaudithardware.11") : (valuesMatch ? Lang.T("t.systemaudithardware.12") : Lang.T("t.systemaudithardware.13"))),
                    Evidence = "机制明确",
                    Warn = (valuesMatch && belowPreferredBuild)
                });
            }
            report.Machine.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudithardware.14"),
                Value = ((facts.ThrottleEvents7d > 0) ? (Lang.T("t.systemaudithardware.15") + facts.ThrottleEvents7d + Lang.T("t.gputhrottleprobe.5")) : Lang.T("t.systemaudithardware.16")),
                Note = ((facts.ThrottleEvents7d > 0) ? Lang.T("t.systemaudithardware.17") : Lang.T("t.systemaudithardware.18")),
                Evidence = "本机实测",
                Warn = (facts.ThrottleEvents7d > 0)
            });
            if (facts.RgbSuites != null && facts.RgbSuites.Count > 0)
            {
                report.Machine.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.19"),
                    Value = string.Join(" ", facts.RgbSuites.ToArray()),
                    Evidence = "机制明确",
                    Warn = false
                });
            }
            if (facts.RyzenCpu && facts.OsBuild > 0)
            {
                bool olderScheduler = facts.OsBuild < 26100;
                report.Persistent.Add(new AuditRow
                {
                    Name = Lang.T("t.systemaudithardware.21"),
                    Value = (olderScheduler ? Lang.T("t.systemaudithardware.22") : Lang.T("t.systemaudithardware.23")),
                    Note = (olderScheduler ? Lang.T("t.systemaudithardware.24") : Lang.T("t.systemaudithardware.25")),
                    Evidence = "台架实测",
                    Warn = olderScheduler
                });
            }
            report.Persistent.Add(new AuditRow
            {
                Name = Lang.T("t.systemaudithardware.28"),
                Value = ((facts.MsiOffCount > 0) ? (facts.MsiOffCount + Lang.T("t.systemaudithardware.29")) : Lang.T("t.systemaudithardware.30")),
                Note = ((facts.MsiOffCount > 0) ? Lang.T("t.systemaudithardware.31") : Lang.T("t.systemaudithardware.32")),
                Evidence = "机制明确",
                Warn = (facts.MsiOffCount > 0),
                FixKey = "msi"
            });
            if (CpuTopology.AsymCache)
            {
                report.Capability.Add(X3dOptimizerRow());
            }
        }

        internal static bool X3dOptimizerPresent()
        {
            try
            {
                using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\amd3dvcache"))
                {
                    return registryKey != null;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool WindowsGameModeOn()
        {
            using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\GameBar"))
            {
                object value = ((registryKey == null) ? null : registryKey.GetValue("AutoGameModeEnabled"));
                return !(value is int) || (int)value != 0;
            }
        }

        private static AuditRow X3dOptimizerRow()
        {
            bool x3dOptimizerPresentSucceeded = X3dOptimizerPresent();
            bool gameModeEnabled = false;
            try
            {
                gameModeEnabled = WindowsGameModeOn();
            }
            catch
            {
            }
            AuditRow auditRow = new AuditRow();
            auditRow.Name = Lang.T("t.systemaudithardware.33");
            auditRow.Value = (x3dOptimizerPresentSucceeded ? Lang.T(gameModeEnabled ? "t.systemaudithardware.34" : "t.systemaudithardware.35") : Lang.T("t.systemaudithardware.36"));
            auditRow.Note = (x3dOptimizerPresentSucceeded ? Lang.T(gameModeEnabled ? "t.systemaudithardware.37" : "t.systemaudithardware.38") : Lang.T("t.systemaudithardware.39"));
            auditRow.Evidence = "机制明确";
            auditRow.Warn = x3dOptimizerPresentSucceeded && !gameModeEnabled;
            return auditRow;
        }

        internal static string MemChannelText(List<double> moduleGb, out bool warn)
        {
            warn = false;
            if (moduleGb == null || moduleGb.Count == 0)
            {
                return null;
            }
            List<string> itemSnapshots = new List<string>();
            bool equalModuleSizes = true;
            foreach (double currentModuleGb in moduleGb)
            {
                double itemSnapshot = currentModuleGb;
                itemSnapshots.Add(itemSnapshot.ToString("F0"));
                if (Math.Abs(itemSnapshot - moduleGb[0]) > 0.5)
                {
                    equalModuleSizes = false;
                }
            }
            if (moduleGb.Count == 1)
            {
                warn = true;
                return moduleGb[0].ToString("F0") + "GB " + Lang.T("t.memchannel.2");
            }
            string joinOffset = string.Join("+", itemSnapshots.ToArray()) + "GB ";
            if (equalModuleSizes)
            {
                return joinOffset + Lang.T("t.memchannel.3");
            }
            if (moduleGb.Count == 2)
            {
                double scaledMin = Math.Min(moduleGb[0], moduleGb[1]) * 2.0;
                double absResult = Math.Abs(moduleGb[0] - moduleGb[1]);
                return joinOffset + Lang.F("t.memchannel.4", scaledMin.ToString("F0"), absResult.ToString("F0"));
            }
            return joinOffset + Lang.T("t.memchannel.5");
        }

        private static int ToMhz(object value)
        {
            try
            {
                return (value != null) ? Convert.ToInt32(value) : 0;
            }
            catch
            {
                return 0;
            }
        }

    }
}
