using System;
using System.Collections.Generic;
using System.Management;
using System.Text;

namespace PaviseApp
{
    internal static class NetworkAffinityTweak
    {

        private static readonly IrqAffinityEngine irqEngine = new IrqAffinityEngine("NicAffinityOnByPavise", "NicAff_", Lang.T("t.versionmigrations.8"));

        public static bool EnabledByPavise
        {
            get
            {
                return Settings.Load("NetPriorityOnByPavise", false);
            }
        }

        internal static List<string> EnumerateNicDeviceIds()
        {
            List<string> messages = new List<string>();
            try
            {
                using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
                {
                    using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
                    {
                        foreach (ManagementObject currentManagementObjectCollection in managementObjectCollection)
                        {
                            using (currentManagementObjectCollection)
                            {
                                string message = currentManagementObjectCollection["PNPDeviceID"] as string;
                                if (!string.IsNullOrEmpty(message) && (message.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || message.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)))
                                {
                                    messages.Add(message);
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(Lang.T("log.networkaffinitytweak.1", 0) + ex.Message);
            }
            return messages;
        }

        internal static string SanitizePolicyName(string gameName, string exePath)
        {
            StringBuilder stringBuilder = new StringBuilder("Pavise_");
            foreach (char c in gameName ?? "")
            {
                if (stringBuilder.Length - "Pavise_".Length >= 40)
                {
                    break;
                }
                stringBuilder.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            if (stringBuilder.Length == "Pavise_".Length)
            {
                stringBuilder.Append("Game");
            }
            int pathHash = 17;
            foreach (char c2 in exePath ?? "")
            {
                pathHash = pathHash * 31 + char.ToLowerInvariant(c2);
            }
            StringBuilder append = stringBuilder.Append('_');
            uint currentPathHash = (uint)pathHash;
            append.Append(currentPathHash.ToString("X6").Substring(0, 6));
            return stringBuilder.ToString();
        }

        private static List<string> LoadPolicyNames()
        {
            List<string> entryTexts = new List<string>();
            string netQosPolicyNamesValue = Settings.LoadStr("NetQosPolicyNames", "");
            foreach (string entryText in netQosPolicyNamesValue.Split(';'))
            {
                if (entryText.Length > 0)
                {
                    entryTexts.Add(entryText);
                }
            }
            return entryTexts;
        }

        private static bool SavePolicyNames(List<string> names)
        {
            string joinResult = string.Join(";", names.ToArray());
            Settings.SaveStr("NetQosPolicyNames", joinResult);
            return Settings.LoadStr("NetQosPolicyNames", "") == joinResult;
        }

        private static IDictionary<string, string> QosArgs(string name, string exePath)
        {
            Dictionary<string, string> textsByName = new Dictionary<string, string>();
            textsByName.Add("PAVISE_QOS_NAME", name ?? "");
            Dictionary<string, string> exePathsByName = textsByName;
            if (exePath != null)
            {
                exePathsByName["PAVISE_QOS_PATH"] = exePath;
            }
            return exePathsByName;
        }

        private static bool ApplyQosPolicy(string policyName, string exePath)
        {
            string script = "$ErrorActionPreference = 'Stop'\r\n$n = $env:PAVISE_QOS_NAME\r\nif (Get-NetQosPolicy -Name $n -ErrorAction SilentlyContinue) {\r\n    Remove-NetQosPolicy -Name $n -Confirm:$false\r\n}\r\nNew-NetQosPolicy -Name $n -AppPathNameMatchCondition $env:PAVISE_QOS_PATH -DSCPAction " + 46 + " -NetworkProfile All | Out-Null\r\nWrite-Output DONE\r\n";
            string stdout;
            if (PsRunner.Run(script, Lang.T("t.networkaffinitytweak.2"), 15000, QosArgs(policyName, exePath), out stdout))
            {
                return stdout.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return false;
        }

        private static bool RemoveQosPolicy(string policyName)
        {
            string script = "$n = $env:PAVISE_QOS_NAME\r\nif (Get-NetQosPolicy -Name $n -ErrorAction SilentlyContinue) {\r\n    Remove-NetQosPolicy -Name $n -Confirm:$false -ErrorAction Stop\r\n}\r\nWrite-Output DONE\r\n";
            string stdout;
            if (PsRunner.Run(script, Lang.T("t.networkaffinitytweak.2"), 15000, QosArgs(policyName, null), out stdout))
            {
                return stdout.IndexOf("DONE", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return false;
        }

        public static bool Enable(List<GameProfile> games)
        {
            bool enableSucceeded = irqEngine.Enable(EnumerateNicDeviceIds());
            List<string> sanitizePolicyNameResults = new List<string>();
            if (games != null)
            {
                foreach (GameProfile game in games)
                {
                    if (!string.IsNullOrEmpty(game.ExecutablePath))
                    {
                        string sanitizePolicyNameResult = SanitizePolicyName(game.Name, game.ExecutablePath);
                        if (ApplyQosPolicy(sanitizePolicyNameResult, game.ExecutablePath))
                        {
                            sanitizePolicyNameResults.Add(sanitizePolicyNameResult);
                        }
                        else
                        {
                            Logger.Warn(Lang.T("log.networkaffinitytweak.3", 0) + game.Name + Lang.T("log.networkaffinitytweak.4", 0));
                        }
                    }
                }
            }
            List<string> texts = LoadPolicyNames();
            List<string> sanitizePolicyNameResultsSnapshot = new List<string>(sanitizePolicyNameResults);
            foreach (string text in texts)
            {
                if (!sanitizePolicyNameResultsSnapshot.Contains(text) && !RemoveQosPolicy(text))
                {
                    sanitizePolicyNameResultsSnapshot.Add(text);
                }
            }
            if (!SavePolicyNames(sanitizePolicyNameResultsSnapshot))
            {
                foreach (string currentSanitizePolicyNameResult in sanitizePolicyNameResults)
                {
                    RemoveQosPolicy(currentSanitizePolicyNameResult);
                }
                Logger.Log(Lang.T("log.networkaffinitytweak.5", 0));
                return enableSucceeded;
            }
            bool hasManagedPolicy = enableSucceeded || sanitizePolicyNameResults.Count > 0;
            if (hasManagedPolicy)
            {
                Settings.Save("NetPriorityOnByPavise", true);
            }
            return hasManagedPolicy;
        }

        public static bool HasResidue()
        {
            return Settings.Load("NetPriorityOnByPavise", false);
        }

        public static bool Disable()
        {
            bool disableSucceeded = irqEngine.Disable(EnumerateNicDeviceIds());
            List<string> texts = LoadPolicyNames();
            bool policiesRemoved = true;
            foreach (string text in texts)
            {
                if (!RemoveQosPolicy(text))
                {
                    policiesRemoved = false;
                }
            }
            if (policiesRemoved && !SavePolicyNames(new List<string>()))
            {
                policiesRemoved = false;
            }
            bool restored = disableSucceeded && policiesRemoved;
            if (restored)
            {
                Settings.Save("NetPriorityOnByPavise", false);
            }
            return restored;
        }
    }
}
