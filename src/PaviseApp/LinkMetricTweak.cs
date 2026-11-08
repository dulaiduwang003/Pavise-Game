using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class LinkMetricTweak
	{
		private sealed class Probe
		{
			public int WiredIndex = -1;

			public bool DefaultRouteWireless;
		}

		private const string ReceiptKey = "LinkMetricReceipt";

		private const int PreferredMetric = 5;

		private static readonly object lk = new object();

		private static readonly string ApplyScript = string.Join("\r\n", "$ErrorActionPreference = 'SilentlyContinue'", "$i = [int]$env:PAVISE_LM_INDEX", "$m = [int]$env:PAVISE_LM_METRIC", "if ($null -eq (Get-NetAdapter -InterfaceIndex $i -ErrorAction SilentlyContinue)) { Write-Output 'APPLYFAIL'; exit }", "try { Set-NetIPInterface -InterfaceIndex $i -InterfaceMetric $m -ErrorAction Stop } catch { Write-Output 'APPLYFAIL'; exit }", "$chk = Get-NetIPInterface -InterfaceIndex $i -AddressFamily IPv4 -ErrorAction SilentlyContinue", "if ($null -ne $chk -and $chk.InterfaceMetric -eq $m) { Write-Output 'APPLYOK' } else { Write-Output 'APPLYFAIL' }");

		private static readonly string RestoreScript = string.Join("\r\n", "$ErrorActionPreference = 'SilentlyContinue'", "$i = [int]$env:PAVISE_LM_INDEX", "if ($null -eq (Get-NetAdapter -InterfaceIndex $i -ErrorAction SilentlyContinue)) { Write-Output 'RESTOREOK'; exit }", "try { Set-NetIPInterface -InterfaceIndex $i -AutomaticMetric Enabled -ErrorAction Stop; Write-Output 'RESTOREOK' } catch { Write-Output 'RESTOREFAIL' }");

		public static bool RepairedByPavise
		{
			get
			{
				return Settings.LoadStr("LinkMetricReceipt", "").Length > 0;
			}
		}

		internal static bool Decide(bool wiredWithGatewayUp, bool defaultRouteWireless)
		{
			if (wiredWithGatewayUp)
			{
				return defaultRouteWireless;
			}
			return false;
		}

		private static Probe Inspect()
		{
			Probe probe = new Probe();
			int bestInterfaceIndexResult = BestInterfaceIndex();
			NetworkInterface[] allNetworkInterfaces = NetworkInterface.GetAllNetworkInterfaces();
			foreach (NetworkInterface networkInterface in allNetworkInterfaces)
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
				bool inspectReady = false;
				int wiredIndexValue = -1;
				try
				{
					IPInterfaceProperties iPProperties = networkInterface.GetIPProperties();
					foreach (GatewayIPAddressInformation gatewayAddress in iPProperties.GatewayAddresses)
					{
						if (gatewayAddress != null && gatewayAddress.Address != null && !gatewayAddress.Address.Equals(IPAddress.Any))
						{
							inspectReady = true;
							break;
						}
					}
					try
					{
						wiredIndexValue = iPProperties.GetIPv4Properties().Index;
					}
					catch
					{
					}
				}
				catch
				{
				}
				if (!inspectReady)
				{
					continue;
				}
				switch (networkInterfaceType)
				{
				case NetworkInterfaceType.Wireless80211:
					if (wiredIndexValue > 0 && wiredIndexValue == bestInterfaceIndexResult)
					{
						probe.DefaultRouteWireless = true;
					}
					break;
				case NetworkInterfaceType.Ethernet:
				case NetworkInterfaceType.FastEthernetT:
				case NetworkInterfaceType.FastEthernetFx:
				case NetworkInterfaceType.GigabitEthernet:
					if (probe.WiredIndex > 0)
					{
						probe.WiredIndex = -2;
					}
					else
					{
						probe.WiredIndex = wiredIndexValue;
					}
					break;
				}
			}
			return probe;
		}

		[DllImport("iphlpapi.dll")]
		private static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);

		private static int BestInterfaceIndex()
		{
			try
			{
				uint bestIfIndex;
				if (GetBestInterface(134744072u, out bestIfIndex) == 0)
				{
					return (int)bestIfIndex;
				}
			}
			catch
			{
			}
			return -1;
		}

		public static bool NeedsRepair()
		{
			if (RepairedByPavise)
			{
				return false;
			}
			try
			{
				Probe probe = Inspect();
				return probe.WiredIndex > 0 && Decide(true, probe.DefaultRouteWireless);
			}
			catch
			{
				return false;
			}
		}

		public static string Describe()
		{
			if (RepairedByPavise)
			{
				return Lang.T("linkmetric.repaired");
			}
			try
			{
				Probe probe = Inspect();
				if (probe.WiredIndex == -2)
				{
					return Lang.T("linkmetric.ambiguous");
				}
				if (probe.WiredIndex <= 0)
				{
					return Lang.T("linkmetric.nowired");
				}
				return probe.DefaultRouteWireless ? Lang.T("linkmetric.wireless") : Lang.T("linkmetric.ok");
			}
			catch
			{
				return Lang.T("linkmetric.nowired");
			}
		}

		public static bool Repair()
		{
			lock (lk)
			{
				if (RepairedByPavise)
				{
					return true;
				}
				Probe probe;
				try
				{
					probe = Inspect();
				}
				catch
				{
					return false;
				}
				if (probe.WiredIndex <= 0 || !probe.DefaultRouteWireless)
				{
					return true;
				}
				Dictionary<string, string> dictionary = new Dictionary<string, string>();
				dictionary.Add("PAVISE_LM_INDEX", probe.WiredIndex.ToString());
				dictionary.Add("PAVISE_LM_METRIC", 5.ToString());
				Dictionary<string, string> args = dictionary;
				string stdout;
				if (!PsRunner.Run(ApplyScript, "link-metric-apply", 30000, args, out stdout) || stdout.IndexOf("APPLYOK", StringComparison.Ordinal) < 0)
				{
					Logger.Log(Lang.T("log.linkmetric.1"));
					return false;
				}
				Settings.SaveStr("LinkMetricReceipt", probe.WiredIndex.ToString());
				if (Settings.LoadStr("LinkMetricReceipt", "") != probe.WiredIndex.ToString())
				{
					RestoreIndex(probe.WiredIndex);
					return false;
				}
				Logger.Log(Lang.T("log.linkmetric.2"));
				return true;
			}
		}

		public static bool Restore()
		{
			lock (lk)
			{
				string linkMetricReceiptValue = Settings.LoadStr("LinkMetricReceipt", "");
				if (linkMetricReceiptValue.Length == 0)
				{
					return true;
				}
				int result;
				if (int.TryParse(linkMetricReceiptValue, out result) && !RestoreIndex(result))
				{
					Logger.Log(Lang.T("log.linkmetric.3"));
					return false;
				}
				Settings.SaveStr("LinkMetricReceipt", "");
				Logger.Log(Lang.T("log.linkmetric.4"));
				return true;
			}
		}

		private static bool RestoreIndex(int index)
		{
			Dictionary<string, string> dictionary = new Dictionary<string, string>();
			dictionary.Add("PAVISE_LM_INDEX", index.ToString());
			Dictionary<string, string> args = dictionary;
			string stdout;
			if (PsRunner.Run(RestoreScript, "link-metric-restore", 30000, args, out stdout))
			{
				return stdout.IndexOf("RESTOREOK", StringComparison.Ordinal) >= 0;
			}
			return false;
		}
	}
}
