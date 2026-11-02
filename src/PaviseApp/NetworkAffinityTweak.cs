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
								string text = item["PNPDeviceID"] as string;
								if (!string.IsNullOrEmpty(text) && (text.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase) || text.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)))
								{
									list.Add(text);
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
			string text = gameName ?? "";
			foreach (char c in text)
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
			int num = 17;
			string text2 = exePath ?? "";
			foreach (char c2 in text2)
			{
				num = num * 31 + char.ToLowerInvariant(c2);
			}
			StringBuilder stringBuilder2 = stringBuilder.Append('_');
			uint num2 = (uint)num;
			stringBuilder2.Append(num2.ToString("X6").Substring(0, 6));
			return stringBuilder.ToString();
		}

		private static List<string> LoadPolicyNames()
		{
			List<string> list = new List<string>();
			string text = Settings.LoadStr("NetQosPolicyNames", "");
			string[] array = text.Split(';');
			foreach (string text2 in array)
			{
				if (text2.Length > 0)
				{
					list.Add(text2);
				}
			}
			return list;
		}

		private static bool SavePolicyNames(List<string> names)
		{
			string text = string.Join(";", names.ToArray());
			Settings.SaveStr("NetQosPolicyNames", text);
			return Settings.LoadStr("NetQosPolicyNames", "") == text;
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
			bool flag = irqEngine.Enable(EnumerateNicDeviceIds());
			List<string> list = new List<string>();
			if (games != null)
			{
				foreach (GameProfile game in games)
				{
					if (!string.IsNullOrEmpty(game.ExecutablePath))
					{
						string text = SanitizePolicyName(game.Name, game.ExecutablePath);
						if (ApplyQosPolicy(text, game.ExecutablePath))
						{
							list.Add(text);
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
				return flag;
			}
			bool flag2 = flag || list.Count > 0;
			if (flag2)
			{
				Settings.Save("NetPriorityOnByPavise", true);
			}
			return flag2;
		}

		public static bool HasResidue()
		{
			return Settings.Load("NetPriorityOnByPavise", false);
		}

		public static bool Disable()
		{
			bool flag = irqEngine.Disable(EnumerateNicDeviceIds());
			List<string> list = LoadPolicyNames();
			bool flag2 = true;
			foreach (string item in list)
			{
				if (!RemoveQosPolicy(item))
				{
					flag2 = false;
				}
			}
			if (flag2 && !SavePolicyNames(new List<string>()))
			{
				flag2 = false;
			}
			bool flag3 = flag && flag2;
			if (flag3)
			{
				Settings.Save("NetPriorityOnByPavise", false);
			}
			return flag3;
		}
	}
}
