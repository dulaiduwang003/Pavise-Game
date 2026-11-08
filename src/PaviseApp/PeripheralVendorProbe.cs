using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class PeripheralVendorProbe
	{
		private const int RefreshMs = 600000;

		private const int MaxTokens = 64;

		private static readonly object sync = new object();

		private static string[] tokens = new string[0];

		private static int stamp;

		private static bool scanned;

		private static volatile int generation;

		private static readonly HashSet<string> StopWords = new HashSet<string>(StringComparer.Ordinal)
		{
			"usb", "usb2", "usb3", "hid", "compliant", "composite", "device", "devices", "input", "output",
			"mouse", "mice", "keyboard", "keyboards", "keypad", "pointing", "pointer", "cursor", "wheel", "button",
			"buttons", "wireless", "wired", "receiver", "dongle", "adapter", "transceiver", "bluetooth", "radio", "gaming",
			"game", "microsoft", "windows", "standard", "system", "generic", "port", "ports", "root", "hub",
			"hubs", "controller", "control", "collection", "consumer", "vendor", "defined", "interface", "class", "filter",
			"driver", "drivers", "firmware", "virtual", "internal", "remote", "desktop", "terminal", "server", "precision",
			"touch", "touchpad", "trackpad", "optical", "laser", "mechanical", "membrane", "ergonomic", "rechargeable", "edition",
			"series", "model", "type", "combo", "intel", "amd", "nvidia", "pro", "max", "mini",
			"lite", "plus", "ultra", "air", "nano", "black", "white", "pink", "blue", "red",
			"green", "gray", "grey", "silver", "gold", "purple", "yellow", "the", "and", "for",
			"with", "ver", "rev", "ghz", "mhz", "khz", "dpi", "rgb", "led", "host",
			"inc", "ltd", "corp", "corporation", "company", "computer", "technology", "electronics", "enhanced", "extensible",
			"compatible", "chipset", "express", "key", "keys", "num", "number", "pad", "media", "audio2",
			"codec"
		};

		internal static int Generation
		{
			get
			{
				return generation;
			}
		}

		internal static int CurrentGeneration()
		{
			Tokens();
			return generation;
		}

		public static string[] Tokens()
		{
			lock (sync)
			{
				int tickCount = Environment.TickCount;
				if (scanned && tickCount - stamp < 600000)
				{
					return tokens;
				}
				stamp = tickCount;
				scanned = true;
			}
			string[] scanResult = Scan();
			lock (sync)
			{
				if (!SameSet(tokens, scanResult))
				{
					tokens = scanResult;
					generation++;
					Logger.Log(Lang.T("log.peripheralvendorprobe.1") + scanResult.Length + Lang.T("log.peripheralvendorprobe.2") + string.Join(" ", scanResult));
				}
				return tokens;
			}
		}

		private static string[] Scan()
		{
			HashSet<string> hashSet = new HashSet<string>(StringComparer.Ordinal);
			try
			{
				foreach (InputDevice item in InputChainProbe.Devices())
				{
					if (item.Transport == InputTransport.Virtual)
					{
						continue;
					}
					CollectNode(item.InstanceId, hashSet);
					if (item.Transport != InputTransport.Usb)
					{
						continue;
					}
					foreach (string item2 in PresentDevices.ParentChain(item.InstanceId))
					{
						string hardware;
						string instance;
						if (item2.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) && InputChainProbe.SplitUsbInstance(item2, out hardware, out instance) && !hardware.StartsWith("ROOT_HUB", StringComparison.OrdinalIgnoreCase))
						{
							CollectNode(item2, hashSet);
						}
					}
				}
			}
			catch (Exception ex)
			{
				Logger.Warn(Lang.T("log.peripheralvendorprobe.3") + ex.Message);
			}
			List<string> list = new List<string>(hashSet);
			list.Sort(StringComparer.Ordinal);
			if (list.Count > 64)
			{
				list.RemoveRange(64, list.Count - 64);
			}
			return list.ToArray();
		}

		private static void CollectNode(string instanceId, HashSet<string> set)
		{
			AddTokens(PresentDevices.BusReportedDesc(instanceId), set);
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId))
				{
					if (registryKey != null)
					{
						AddTokens(InputChainProbe.CleanDesc(registryKey.GetValue("FriendlyName") as string), set);
						AddTokens(InputChainProbe.CleanDesc(registryKey.GetValue("DeviceDesc") as string), set);
						AddTokens(InputChainProbe.CleanDesc(registryKey.GetValue("Mfg") as string), set);
					}
				}
			}
			catch
			{
			}
		}

		internal static void AddTokens(string raw, HashSet<string> set)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return;
			}
			int addTokensValue = -1;
			for (int i = 0; i <= raw.Length; i++)
			{
				char c = ((i < raw.Length) ? raw[i] : ' ');
				if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'))
				{
					if (addTokensValue < 0)
					{
						addTokensValue = i;
					}
				}
				else if (addTokensValue >= 0)
				{
					Take(raw, addTokensValue, i - addTokensValue, set);
					addTokensValue = -1;
				}
			}
		}

		private static void Take(string raw, int start, int length, HashSet<string> set)
		{
			if (length < 3 || length > 24)
			{
				return;
			}
			char c = raw[start];
			if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
			{
				string item = raw.Substring(start, length).ToLowerInvariant();
				if (!StopWords.Contains(item))
				{
					set.Add(item);
				}
			}
		}

		private static bool SameSet(string[] a, string[] b)
		{
			if (a.Length != b.Length)
			{
				return false;
			}
			for (int i = 0; i < a.Length; i++)
			{
				if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
				{
					return false;
				}
			}
			return true;
		}
	}
}
