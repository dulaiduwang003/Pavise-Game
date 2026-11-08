using System;
using System.Collections.Generic;
using System.Management;
using System.Text;

namespace PaviseApp
{
	internal static class NetworkAffinityTweak
	{
		private const string QosPolicyNamesKey = "NetQosPolicyNames";

		private const string EnabledKey = "NetPriorityOnByPavise";

		private const string PolicyPrefix = "Pavise_";

		private const int GamingDscp = 46;

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
			List<string> list = new List<string>();
			try
			{
				using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT PNPDeviceID FROM Win32_NetworkAdapter WHERE PhysicalAdapter = TRUE"))
				{
					using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
					{
						foreach (ManagementObject item in managementObjectCollection)
						{
							using (item)
							{
								string message = item["PNPDeviceID"] as string;
								if (!string.IsNullOrEmpty(message) && (message.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || message.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)))
								{
									list.Add(message);
								}
							}
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Warn(Lang.T("log.networkaffinitytweak.1") + ex.Message);
			}
			return list;
		}

		internal static string SanitizePolicyName(string gameName, string exePath)
		{
			StringBuilder stringBuilder = new StringBuilder("Pavise_");
			string gameNameOffset = gameName ?? "";
			foreach (char c in gameNameOffset)
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
			int sanitizePolicyNameValue = 17;
			string exePathOffset = exePath ?? "";
			foreach (char c2 in exePathOffset)
			{
				sanitizePolicyNameValue = sanitizePolicyNameValue * 31 + char.ToLowerInvariant(c2);
			}
			StringBuilder stringBuilder2 = stringBuilder.Append('_');
			uint numericValue = (uint)sanitizePolicyNameValue;
			stringBuilder2.Append(numericValue.ToString("X6").Substring(0, 6));
			return stringBuilder.ToString();
		}

		private static List<string> LoadPolicyNames()
		{
			List<string> list = new List<string>();
			string netQosPolicyNamesValue = Settings.LoadStr("NetQosPolicyNames", "");
			string[] netQosPolicyNamesValueParts = netQosPolicyNamesValue.Split(';');
			foreach (string entryText in netQosPolicyNamesValueParts)
			{
				if (entryText.Length > 0)
				{
					list.Add(entryText);
				}
			}
			return list;
		}

		private static bool SavePolicyNames(List<string> names)
		{
			string joinResult = string.Join(";", names.ToArray());
			Settings.SaveStr("NetQosPolicyNames", joinResult);
			return Settings.LoadStr("NetQosPolicyNames", "") == joinResult;
		}

		private static IDictionary<string, string> QosArgs(string name, string exePath)
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>();
			dictionary.Add("PAVISE_QOS_NAME", name ?? "");
			Dictionary<string, string> dictionary2 = dictionary;
			if (exePath != null)
			{
				dictionary2["PAVISE_QOS_PATH"] = exePath;
			}
			return dictionary2;
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
			List<string> list = new List<string>();
			if (games != null)
			{
				foreach (GameProfile game in games)
				{
					if (!string.IsNullOrEmpty(game.ExecutablePath))
					{
						string sanitizePolicyNameResult = SanitizePolicyName(game.Name, game.ExecutablePath);
						if (ApplyQosPolicy(sanitizePolicyNameResult, game.ExecutablePath))
						{
							list.Add(sanitizePolicyNameResult);
						}
						else
						{
							Logger.Warn(Lang.T("log.networkaffinitytweak.3") + game.Name + Lang.T("log.networkaffinitytweak.4"));
						}
					}
				}
			}
			List<string> list2 = LoadPolicyNames();
			List<string> list3 = new List<string>(list);
			foreach (string item in list2)
			{
				if (!list3.Contains(item) && !RemoveQosPolicy(item))
				{
					list3.Add(item);
				}
			}
			if (!SavePolicyNames(list3))
			{
				foreach (string item2 in list)
				{
					RemoveQosPolicy(item2);
				}
				Logger.Log(Lang.T("log.networkaffinitytweak.5"));
				return enableSucceeded;
			}
			bool matchesEnableSucceeded = enableSucceeded || list.Count > 0;
			if (matchesEnableSucceeded)
			{
				Settings.Save("NetPriorityOnByPavise", true);
			}
			return matchesEnableSucceeded;
		}

		public static bool HasResidue()
		{
			return Settings.Load("NetPriorityOnByPavise", false);
		}

		public static bool Disable()
		{
			bool disableSucceeded = irqEngine.Disable(EnumerateNicDeviceIds());
			List<string> list = LoadPolicyNames();
			bool disableReady = true;
			foreach (string item in list)
			{
				if (!RemoveQosPolicy(item))
				{
					disableReady = false;
				}
			}
			if (disableReady && !SavePolicyNames(new List<string>()))
			{
				disableReady = false;
			}
			bool matchesDisableSucceeded = disableSucceeded && disableReady;
			if (matchesDisableSucceeded)
			{
				Settings.Save("NetPriorityOnByPavise", false);
			}
			return matchesDisableSucceeded;
		}
	}
}
