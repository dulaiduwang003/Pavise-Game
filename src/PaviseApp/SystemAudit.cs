using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class SystemAudit
	{
		private struct MemoryStatusEx
		{
			public uint Length;

			public uint MemoryLoad;

			public ulong TotalPhys;

			public ulong AvailPhys;

			public ulong TotalPageFile;

			public ulong AvailPageFile;

			public ulong TotalVirtual;

			public ulong AvailVirtual;

			public ulong AvailExtendedVirtual;
		}

		private struct SystemPowerStatus
		{
			public byte AcLineStatus;

			public byte BatteryFlag;

			public byte BatteryLifePercent;

			public byte SystemStatusFlag;

			public int BatteryLifeTime;

			public int BatteryFullLifeTime;
		}

		private sealed class Facts
		{
			public bool Nv;

			public GpuAdapter[] Gpus = new GpuAdapter[0];

			public bool NvHardware;

			public bool IntegratedOnly;

			public bool Partition;

			public bool Eco;

			public bool EcoFull;

			public bool Hags;

			public bool HagsKnown;

			public bool InstalledMemoryKnown;

			public bool MemoryCompressionKnown;

			public bool CompressionOn;

			public bool CombiningOn;

			public ulong InstalledMemoryKb;

			public bool MpoOff;

			public bool Dvr;

			public bool SuppressOn;

			public string Plan = Lang.T("t.systemaudithardware.3");

			public bool MemOk;

			public double UsedRatio;

			public double TotalGb;

			public double AvailGb;

			public bool PageOk;

			public double PageFileGb;

			public string Link;

			public List<InputDevice> Inputs;

			public AccessibilityState Access;

			public int? ThreadDpc;

			public int OsBuild;

			public bool RyzenCpu;

			public int MemModules;

			public int MemConfiguredMhz;

			public int MemRatedMhz;

			public List<int> AllHz;

			public List<string> RgbSuites;

			public int ThrottleEvents7d;

			public int MsiOffCount;

			public List<double> MemModuleGb;

			public List<BypassIoVerdict> BypassIo;
		}

		public sealed class LoadEntry
		{
			public string Name;

			public double Ratio;
		}

		private sealed class Sample
		{
			public string Name;

			public long Started;

			public TimeSpan Cpu;
		}

		public const string EvMeasuredLocal = "本机实测";

		public const string EvMeasuredBench = "台架实测";

		public const string EvMechanism = "机制明确";

		public const string EvUnverified = "未验证";

		public const byte EnergySaverOn = 1;

		public const int EcoQosFullBuild = 22000;

		private const string X3dServiceKey = "SYSTEM\\CurrentControlSet\\Services\\amd3dvcache";

		public static Func<List<string>> LibraryPaths;

		public static string EvidenceText(string evidence)
		{
			switch (evidence)
			{
			case "本机实测":
				return Lang.T("audit.evidence.local");
			case "台架实测":
				return Lang.T("audit.evidence.bench");
			case "机制明确":
				return Lang.T("audit.evidence.mechanism");
			default:
				return Lang.T("audit.evidence.unverified");
			}
		}

		public static string PercentText(double rate)
		{
			return (rate * 100.0).ToString("F2") + "%";
		}

		[DllImport("kernel32.dll")]
		private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

		[DllImport("kernel32.dll")]
		private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

		public static bool TryEnergySaver(out bool on)
		{
			on = false;
			try
			{
				SystemPowerStatus status;
				if (!GetSystemPowerStatus(out status))
				{
					return false;
				}
				if (status.SystemStatusFlag > 1)
				{
					return false;
				}
				on = status.SystemStatusFlag == 1;
				return true;
			}
			catch
			{
				return false;
			}
		}

		public static bool TryMemory(out double usedRatio, out double totalGb, out double availGb)
		{
			usedRatio = 0.0;
			totalGb = 0.0;
			availGb = 0.0;
			try
			{
				MemoryStatusEx buffer = default(MemoryStatusEx);
				buffer.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
				if (!GlobalMemoryStatusEx(ref buffer) || buffer.TotalPhys == 0)
				{
					return false;
				}
				totalGb = (double)buffer.TotalPhys / 1073741824.0;
				availGb = (double)buffer.AvailPhys / 1073741824.0;
				usedRatio = 1.0 - (double)buffer.AvailPhys / (double)buffer.TotalPhys;
				return true;
			}
			catch
			{
				return false;
			}
		}

		public static bool RefreshRateIsBest(int current, int best)
		{
			if (current > 0 && best > 0)
			{
				return current >= best - 1;
			}
			return true;
		}

		private static string WindowsText()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"))
				{
					if (registryKey == null)
					{
						return Environment.OSVersion.VersionString;
					}
					string text = registryKey.GetValue("ProductName") as string;
					string text2 = registryKey.GetValue("DisplayVersion") as string;
					object value = registryKey.GetValue("CurrentBuildNumber");
					object value2 = registryKey.GetValue("UBR");
					string text3 = (string.IsNullOrEmpty(text) ? "Windows" : text);
					int result;
					if (value != null && int.TryParse(value.ToString(), out result) && result >= 22000)
					{
						text3 = text3.Replace("Windows 10", "Windows 11");
					}
					if (!string.IsNullOrEmpty(text2))
					{
						text3 = text3 + " " + text2;
					}
					if (value != null)
					{
						object obj = text3;
						text3 = string.Concat(obj, " ", value, (value2 != null) ? ("." + value2) : "");
					}
					return text3;
				}
			}
			catch
			{
				return Environment.OSVersion.VersionString;
			}
		}

		public static int WindowsBuild()
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion"))
				{
					if (registryKey != null)
					{
						object value = registryKey.GetValue("CurrentBuildNumber");
						int result;
						if (value != null && int.TryParse(value.ToString(), out result))
						{
							return result;
						}
					}
				}
			}
			catch
			{
			}
			try
			{
				return Environment.OSVersion.Version.Build;
			}
			catch
			{
				return 0;
			}
		}

		private static bool GameDvrOn()
		{
			try
			{
				using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("System\\GameConfigStore"))
				{
					if (registryKey == null)
					{
						return false;
					}
					object value = registryKey.GetValue("GameDVR_Enabled");
					return value == null || Convert.ToInt32(value) != 0;
				}
			}
			catch
			{
				return false;
			}
		}

		private static Facts Gather()
		{
			Facts facts = new Facts();
			try
			{
				facts.OsBuild = Native.OsBuild();
			}
			catch
			{
			}
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0"))
				{
					string text = ((registryKey == null) ? null : (registryKey.GetValue("ProcessorNameString") as string));
					facts.RyzenCpu = text != null && text.IndexOf("AMD Ryzen", StringComparison.OrdinalIgnoreCase) >= 0;
				}
			}
			catch
			{
			}
			try
			{
				GatherMemoryModules(facts);
			}
			catch
			{
			}
			try
			{
				facts.BypassIo = GatherBypassIo();
			}
			catch
			{
			}
			try
			{
				facts.AllHz = DisplayGuard.AllRefreshRates();
			}
			catch
			{
			}
			try
			{
				facts.RgbSuites = ScanRgbSuites();
			}
			catch
			{
			}
			try
			{
				facts.ThrottleEvents7d = CountCpuThrottleEvents();
			}
			catch
			{
			}
			try
			{
				facts.MsiOffCount = MsiModeTweak.Disabled().Count;
			}
			catch
			{
			}
			try
			{
				facts.Nv = NvApi.Available;
			}
			catch
			{
			}
			try
			{
				facts.Gpus = GpuInventory.Adapters();
				GpuAdapter[] gpus = facts.Gpus;
				foreach (GpuAdapter gpuAdapter in gpus)
				{
					if (gpuAdapter.Vendor == GpuVendor.Nvidia)
					{
						facts.NvHardware = true;
					}
				}
				facts.IntegratedOnly = GpuInventory.IntegratedOnly;
			}
			catch
			{
			}
			try
			{
				facts.Partition = CpuTopology.HasSafeBackgroundPartition();
			}
			catch
			{
			}
			try
			{
				facts.Eco = Native.PowerThrottlingSupported;
			}
			catch
			{
			}
			facts.EcoFull = facts.Eco && WindowsBuild() >= 22000;
			try
			{
				bool supported;
				facts.HagsKnown = HagsTweak.TryQueryState(out supported, out facts.Hags);
			}
			catch
			{
				facts.HagsKnown = false;
			}
			try
			{
				facts.InstalledMemoryKnown = MemCompressTweak.TryInstalledMemory(out facts.InstalledMemoryKb);
			}
			catch
			{
			}
			try
			{
				facts.MemoryCompressionKnown = MemCompressTweak.QueryState(out facts.CompressionOn, out facts.CombiningOn);
			}
			catch
			{
			}
			try
			{
				facts.MpoOff = MpoTweak.CurrentlyDisabled();
			}
			catch
			{
			}
			facts.Dvr = GameDvrOn();
			try
			{
				facts.SuppressOn = Settings.Load("GmSuppress", true);
			}
			catch
			{
				facts.SuppressOn = true;
			}
			try
			{
				facts.Plan = PowerPlan.CurrentPlanLabel();
			}
			catch
			{
			}
			facts.MemOk = TryMemory(out facts.UsedRatio, out facts.TotalGb, out facts.AvailGb);
			facts.PageOk = TryPageFile(out facts.PageFileGb);
			try
			{
				facts.Link = LinkKind();
			}
			catch
			{
			}
			try
			{
				facts.Inputs = InputChainProbe.Devices();
			}
			catch
			{
			}
			try
			{
				facts.Access = InputChainProbe.ReadAccessibility();
			}
			catch
			{
			}
			try
			{
				using (RegistryKey key = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Session Manager\\kernel"))
				{
					object value = key == null ? null : key.GetValue("ThreadDpcEnable");
					facts.ThreadDpc = value is int ? (int?)value : null;
				}
			}
			catch
			{
			}
			return facts;
		}

		public static bool TryPageFile(out double pageFileGb)
		{
			pageFileGb = 0.0;
			try
			{
				MemoryStatusEx buffer = default(MemoryStatusEx);
				buffer.Length = (uint)Marshal.SizeOf(typeof(MemoryStatusEx));
				if (!GlobalMemoryStatusEx(ref buffer) || buffer.TotalPhys == 0)
				{
					return false;
				}
				double num = (double)buffer.TotalPageFile / 1073741824.0;
				double num2 = (double)buffer.TotalPhys / 1073741824.0;
				pageFileGb = Math.Max(0.0, num - num2);
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool PageFileLooksDisabled(double pageFileGb)
		{
			return pageFileGb < 0.5;
		}

		private static string LinkKind()
		{
			bool flag = false;
			bool flag2 = false;
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
				bool flag3 = false;
				try
				{
					foreach (GatewayIPAddressInformation gatewayAddress in networkInterface.GetIPProperties().GatewayAddresses)
					{
						if (gatewayAddress != null && gatewayAddress.Address != null && !gatewayAddress.Address.Equals(IPAddress.Any))
						{
							flag3 = true;
							break;
						}
					}
				}
				catch
				{
				}
				if (flag3)
				{
					if (networkInterfaceType == NetworkInterfaceType.Wireless80211)
					{
						flag = true;
					}
					else
					{
						flag2 = true;
					}
				}
			}
			if (flag2 && flag)
			{
				return "both";
			}
			if (flag2)
			{
				return "wired";
			}
			if (flag)
			{
				return "wifi";
			}
			return "none";
		}

		private static void BuildHardwareHealth(AuditReport report, Facts facts)
		{
			bool warn;
			string text = MemChannelText(facts.MemModuleGb, out warn);
			if (text != null)
			{
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.memchannel.1"),
					Value = text,
					Note = (warn ? Lang.T("t.memchannel.6") : Lang.T("t.memchannel.7")),
					Evidence = "机制明确",
					Warn = warn
				});
			}
			if (facts.MemModules > 0)
			{
				bool flag = facts.MemConfiguredMhz > 0 && facts.MemRatedMhz > 0 && facts.MemConfiguredMhz * 10 < facts.MemRatedMhz * 9;
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudithardware.1"),
					Value = ((facts.MemConfiguredMhz > 0) ? (facts.MemConfiguredMhz + " MHz" + ((facts.MemRatedMhz > facts.MemConfiguredMhz) ? (Lang.T("t.systemaudithardware.2") + facts.MemRatedMhz) : "")) : Lang.T("t.systemaudithardware.3")),
					Note = (flag ? Lang.T("t.systemaudithardware.4") : Lang.T("t.systemaudithardware.5")),
					Evidence = (flag ? "台架实测" : "机制明确"),
					Warn = flag
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
				int num = int.MaxValue;
				int num2 = 0;
				List<string> list = new List<string>();
				foreach (int item in facts.AllHz)
				{
					if (item < num)
					{
						num = item;
					}
					if (item > num2)
					{
						num2 = item;
					}
					list.Add(item + "Hz");
				}
				bool flag2 = num2 - num > 5;
				bool flag3 = facts.OsBuild > 0 && facts.OsBuild < 26100;
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudithardware.10"),
					Value = string.Join(" / ", list.ToArray()),
					Note = ((flag2 && flag3) ? Lang.T("t.systemaudithardware.11") : (flag2 ? Lang.T("t.systemaudithardware.12") : Lang.T("t.systemaudithardware.13"))),
					Evidence = "机制明确",
					Warn = (flag2 && flag3)
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
				bool flag4 = facts.OsBuild < 26100;
				report.Persistent.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudithardware.21"),
					Value = (flag4 ? Lang.T("t.systemaudithardware.22") : Lang.T("t.systemaudithardware.23")),
					Note = (flag4 ? Lang.T("t.systemaudithardware.24") : Lang.T("t.systemaudithardware.25")),
					Evidence = "台架实测",
					Warn = flag4
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
				object obj = ((registryKey == null) ? null : registryKey.GetValue("AutoGameModeEnabled"));
				return !(obj is int) || (int)obj != 0;
			}
		}

		private static AuditRow X3dOptimizerRow()
		{
			bool flag = X3dOptimizerPresent();
			bool flag2 = false;
			try
			{
				flag2 = WindowsGameModeOn();
			}
			catch
			{
			}
			AuditRow auditRow = new AuditRow();
			auditRow.Name = Lang.T("t.systemaudithardware.33");
			auditRow.Value = (flag ? Lang.T(flag2 ? "t.systemaudithardware.34" : "t.systemaudithardware.35") : Lang.T("t.systemaudithardware.36"));
			auditRow.Note = (flag ? Lang.T(flag2 ? "t.systemaudithardware.37" : "t.systemaudithardware.38") : Lang.T("t.systemaudithardware.39"));
			auditRow.Evidence = "机制明确";
			auditRow.Warn = flag && !flag2;
			return auditRow;
		}

		private static void GatherMemoryModules(Facts f)
		{
			using (ManagementObjectSearcher managementObjectSearcher = new ManagementObjectSearcher("SELECT Speed, ConfiguredClockSpeed, Capacity FROM Win32_PhysicalMemory"))
			{
				managementObjectSearcher.Options.Timeout = TimeSpan.FromSeconds(10.0);
				f.MemModuleGb = new List<double>();
				using (ManagementObjectCollection managementObjectCollection = managementObjectSearcher.Get())
				{
					foreach (ManagementBaseObject item in managementObjectCollection)
					{
						using (item)
						{
							f.MemModules++;
							int num = ToMhz(item["Speed"]);
							int num2 = ToMhz(item["ConfiguredClockSpeed"]);
							if (num > f.MemRatedMhz)
							{
								f.MemRatedMhz = num;
							}
							if (num2 > f.MemConfiguredMhz)
							{
								f.MemConfiguredMhz = num2;
							}
							try
							{
								object obj = item["Capacity"];
								if (obj != null)
								{
									f.MemModuleGb.Add((double)Convert.ToUInt64(obj) / 1073741824.0);
								}
							}
							catch
							{
							}
						}
					}
				}
			}
		}

		internal static string MemChannelText(List<double> moduleGb, out bool warn)
		{
			warn = false;
			if (moduleGb == null || moduleGb.Count == 0)
			{
				return null;
			}
			List<string> list = new List<string>();
			bool flag = true;
			foreach (double item in moduleGb)
			{
				double num = item;
				list.Add(num.ToString("F0"));
				if (Math.Abs(num - moduleGb[0]) > 0.5)
				{
					flag = false;
				}
			}
			if (moduleGb.Count == 1)
			{
				warn = true;
				return moduleGb[0].ToString("F0") + "GB " + Lang.T("t.memchannel.2");
			}
			string text = string.Join("+", list.ToArray()) + "GB ";
			if (flag)
			{
				return text + Lang.T("t.memchannel.3");
			}
			if (moduleGb.Count == 2)
			{
				double num2 = Math.Min(moduleGb[0], moduleGb[1]) * 2.0;
				double num3 = Math.Abs(moduleGb[0] - moduleGb[1]);
				return text + Lang.F("t.memchannel.4", num2.ToString("F0"), num3.ToString("F0"));
			}
			return text + Lang.T("t.memchannel.5");
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

		private static List<string> ScanRgbSuites()
		{
			List<string> list = new List<string>();
			Process[] processes = Process.GetProcesses();
			foreach (Process process in processes)
			{
				try
				{
					string text = process.ProcessName.ToLowerInvariant();
					string text2 = null;
					if (text.Contains("icue"))
					{
						text2 = "Corsair iCUE";
					}
					else if (text.Contains("lightingservice"))
					{
						text2 = "ASUS Aura";
					}
					else if (text.Contains("armourycrate") || text.Contains("armouryswagent"))
					{
						text2 = "Armoury Crate";
					}
					else if (text.Contains("razerappengine") || text.Contains("rzsynapse") || text.Contains("razer synapse"))
					{
						text2 = "Razer Synapse";
					}
					else if (text.Contains("mysticlight"))
					{
						text2 = "MSI Mystic Light";
					}
					else if (text.Contains("signalrgb"))
					{
						text2 = "SignalRGB";
					}
					if (text2 != null && !list.Contains(text2))
					{
						list.Add(text2);
					}
				}
				catch
				{
				}
				finally
				{
					try
					{
						process.Dispose();
					}
					catch
					{
					}
				}
			}
			return list;
		}

		private static int CountCpuThrottleEvents()
		{
			string query = "*[System[Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37) and TimeCreated[timediff(@SystemTime) <= 604800000]]]";
			EventLogQuery eventQuery = new EventLogQuery("System", PathType.LogName, query);
			int num = 0;
			using (EventLogReader eventLogReader = new EventLogReader(eventQuery))
			{
				EventRecord eventRecord = eventLogReader.ReadEvent();
				while (eventRecord != null && num < 200)
				{
					using (eventRecord)
					{
						num++;
					}
					eventRecord = eventLogReader.ReadEvent();
				}
			}
			return num;
		}

		internal static string InputSummary(List<InputDevice> devices, bool mouse)
		{
			if (devices == null)
			{
				return null;
			}
			bool flag = false;
			foreach (InputDevice device in devices)
			{
				if (device.IsMouse == mouse && device.Transport != InputTransport.Virtual && device.Transport != InputTransport.Ps2)
				{
					flag = true;
				}
			}
			List<string> list = new List<string>();
			foreach (InputDevice device2 in devices)
			{
				if (device2.IsMouse == mouse && device2.Transport != InputTransport.Virtual && (device2.Transport != InputTransport.Ps2 || !flag))
				{
					string item = InputChainProbe.TransportText(device2.Transport);
					if (!list.Contains(item))
					{
						list.Add(item);
					}
				}
			}
			if (list.Count != 0)
			{
				return string.Join(Lang.T("t.systemaudit.101"), list.ToArray());
			}
			return null;
		}

		private static void BuildInputChain(AuditReport report, Facts facts)
		{
			bool flag = false;
			bool flag2 = false;
			try
			{
				flag = InputChainProbe.AnyBluetooth(facts.Inputs);
				flag2 = InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, true) || InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, false);
			}
			catch
			{
			}
			string text = InputSummary(facts.Inputs, true);
			string text2 = InputSummary(facts.Inputs, false);
			if (text != null || text2 != null)
			{
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("audit.input.devices"),
					Value = ((text == null) ? "" : (Lang.T("t.systemaudit.102") + text)) + ((text != null && text2 != null) ? "   " : "") + ((text2 == null) ? "" : (Lang.T("t.systemaudit.103") + text2)),
					Note = (flag2 ? Lang.T("t.systemaudit.104") : (flag ? Lang.T("t.systemaudit.105") : Lang.T("t.systemaudit.106"))),
					Evidence = (flag ? "台架实测" : "本机实测"),
					Warn = flag2
				});
			}
			if (facts.Access != null && facts.Access.Read)
			{
				bool filterKeysSwallowing = facts.Access.FilterKeysSwallowing;
				bool anyNeedsFix = facts.Access.AnyNeedsFix;
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.legacypurge.14"),
					Value = (filterKeysSwallowing ? Lang.T("t.systemaudit.107") : (anyNeedsFix ? Lang.T("t.systemaudit.108") : Lang.T("t.systemaudit.1"))),
					Note = (filterKeysSwallowing ? (Lang.T("t.systemaudit.109") + facts.Access.DelayBeforeAcceptanceMs + Lang.T("t.systemaudit.110")) : (anyNeedsFix ? Lang.T("t.systemaudit.111") : Lang.T("t.systemaudit.112"))),
					Evidence = "机制明确",
					Warn = anyNeedsFix
				});
			}
			if (facts.ThreadDpc.HasValue)
			{
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudit.121"),
					Value = Lang.T("t.systemaudit.120") + facts.ThreadDpc.Value,
					Note = Lang.T("t.systemaudit.124"),
					Evidence = "机制明确",
					Warn = true
				});
			}
		}

		private static void BuildPersistent(AuditReport report, Facts facts)
		{
			report.Persistent.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.125"),
				Value = ((!facts.HagsKnown) ? Lang.T("env.graphics.unknown") : (facts.Hags ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close"))),
				Note = Lang.T("t.systemaudit.126"),
				Evidence = "机制明确",
				Warn = false
			});
			BuildMemoryCompression(report, facts.InstalledMemoryKnown, facts.InstalledMemoryKb, facts.MemoryCompressionKnown, facts.CompressionOn, facts.CombiningOn, MemCompressTweak.OwnsState);
			report.Persistent.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.132"),
				Value = (facts.MpoOff ? Lang.T("t.systemaudit.133") : Lang.T("t.systemaudit.134")),
				Note = (facts.MpoOff ? Lang.T("t.systemaudit.135") : Lang.T("t.systemaudit.136")),
				Evidence = "机制明确",
				Warn = facts.MpoOff
			});
			report.Persistent.Add(new AuditRow
			{
				Name = Lang.T("t.systemauditverdicts.43"),
				Value = (facts.Dvr ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close")),
				Note = (facts.Dvr ? Lang.T("t.systemaudit.137") : Lang.T("t.systemaudit.138")),
				Evidence = "机制明确",
				Warn = facts.Dvr
			});
			report.Persistent.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.139"),
				Value = facts.Plan,
				Note = Lang.T("t.systemaudit.140"),
				Evidence = "机制明确",
				Warn = false
			});
			QuantumAudit.Finding finding = QuantumAudit.Inspect();
			report.Persistent.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.144"),
				Value = finding.Value,
				Note = finding.Note,
				Evidence = "机制明确",
				Warn = finding.Warn,
				FixKey = (finding.OfferSetting ? "quantum" : null)
			});
			bool flag = false;
			string text = "";
			try
			{
				flag = NetTweak.NeedsRepair();
				text = NetTweak.Describe();
			}
			catch
			{
			}
			report.Persistent.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.148"),
				Value = (flag ? Lang.T("t.systemaudit.145") : Lang.T("t.systemaudit.134")),
				Note = (flag ? Lang.T("t.systemaudit.149") : Lang.T("t.systemaudit.150")) + " " + text,
				Evidence = "机制明确",
				Warn = flag,
				FixKey = "net"
			});
			bool flag2 = false;
			string text2 = "";
			try
			{
				flag2 = FthTweak.NeedsRepair();
				text2 = FthTweak.Describe();
			}
			catch
			{
			}
			report.Persistent.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.151"),
				Value = (flag2 ? Lang.T("t.systemaudit.152") : Lang.T("t.systemaudit.134")),
				Note = text2 + " " + Lang.T(flag2 ? "t.systemaudit.153" : "t.systemaudit.154"),
				Evidence = "机制明确",
				Warn = flag2,
				FixKey = "fth"
			});
		}

		internal static void BuildMemoryCompression(AuditReport report, bool memoryKnown, ulong installedKb, bool stateKnown, bool compression, bool combining, bool owned)
		{
			report.MemoryCompressionCanEnable = stateKnown && !compression;
			string value = ((!stateKnown) ? Lang.T("t.systemaudithardware.3") : Lang.T(compression ? "audit.mem.healthy" : "audit.mem.todo"));
			string text = ((!stateKnown) ? Lang.T("audit.mem.unknown") : Lang.T(compression ? "audit.mem.keep" : "audit.mem.policy"));
			if (owned)
			{
				text = text + " " + Lang.T("audit.mem.owned");
			}
			if (memoryKnown)
			{
				text = Lang.F("audit.mem.capacity", ((double)installedKb / 1048576.0).ToString("0.#")) + " " + text;
			}
			if (stateKnown)
			{
				text = text + " " + Lang.F("audit.mem.states", Lang.T(compression ? "audit.mem.on" : "notes.close"), Lang.T(combining ? "audit.mem.on" : "notes.close"));
			}
			report.Persistent.Add(new AuditRow
			{
				Name = Lang.T("audit.mem.title"),
				Value = value,
				Note = text,
				Warn = report.MemoryCompressionCanEnable,
				FixKey = "memcompress",
				Evidence = (stateKnown ? "本机实测" : "未验证")
			});
		}

		internal static void BuildGpuInterrupt(AuditReport report)
		{
			GpuIrqResult gpuIrq = report.GpuIrq;
			if (gpuIrq == null)
			{
				return;
			}
			report.Machine.RemoveAll((AuditRow row) => row.FixKey == "gpuirq" || row.FixKey == "gpuirqdriver");
			bool warn = false;
			string value;
			string text;
			switch (gpuIrq.Outcome)
			{
			case GpuIrqOutcome.NeedsPlacement:
				value = Lang.T("audit.irq.overlap");
				text = Lang.F("audit.irq.proposal", IrqRelocate.MaskText(gpuIrq.Target));
				warn = true;
				break;
			case GpuIrqOutcome.NeedsVerification:
				value = Lang.T("irqcheck.state.verify");
				text = Lang.T("irqcheck.hint.verify");
				warn = true;
				break;
			case GpuIrqOutcome.AwaitingReboot:
				value = Lang.T("irqcheck.state.pending");
				text = Lang.T("irqcheck.hint.pending");
				warn = true;
				break;
			case GpuIrqOutcome.Clear:
				value = Lang.T("irqcheck.state.clear");
				text = Lang.T("irqcheck.hint.clear");
				break;
			case GpuIrqOutcome.NoRegion:
				value = Lang.T("irqcheck.state.noregion");
				text = Lang.T("irqcheck.out.noregion");
				break;
			case GpuIrqOutcome.NoTarget:
				value = Lang.T("irqcheck.reason.notarget");
				text = Lang.T("irqcheck.reason.notarget");
				break;
			case GpuIrqOutcome.Skipped:
				value = Lang.T("audit.irq.skipped");
				text = gpuIrq.Detail;
				break;
			case GpuIrqOutcome.NoEvidence:
				value = Lang.T("audit.irq.insufficient");
				text = Lang.F("audit.irq.check.hint", (report.MeasureWindowMs > 0) ? ((int)Math.Ceiling((double)report.MeasureWindowMs / 1000.0)) : 120);
				break;
			case GpuIrqOutcome.Cancelled:
				value = Lang.T("audit.irq.skipped");
				text = Lang.T("irqcheck.out.cancelled");
				break;
			default:
				value = Lang.T("audit.irq.unavailable");
				text = gpuIrq.Detail;
				break;
			}
			if (gpuIrq.Gpu != null)
			{
				text = text + " " + Lang.F("audit.irq.sample", gpuIrq.Gpu.Name, IrqRelocate.MaskText(gpuIrq.Gpu.Seen), gpuIrq.Gpu.Events, gpuIrq.Gpu.P99Us.ToString("F0"));
			}
			report.Machine.Add(new AuditRow
			{
				Name = Lang.T("irqcheck.gpu.title"),
				Value = value,
				Note = text,
				Warn = warn,
				FixKey = "gpuirq",
				Evidence = ((gpuIrq.Gpu != null && gpuIrq.Gpu.Events >= 300 && gpuIrq.Gpu.Seen != 0) ? "本机实测" : "未验证")
			});
			foreach (SlowDriver item in gpuIrq.Slow)
			{
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("irqcheck.slow.title") + " · " + item.Driver,
					Value = Lang.F("irqcheck.slow.row", item.MaxUs.ToString("F0"), item.P99Us.ToString("F0")),
					Note = item.Device + " " + Lang.T("irqcheck.slow.hint"),
					Warn = true,
					Evidence = "本机实测",
					FixKey = "gpuirqdriver"
				});
			}
		}

		private static void BuildMachine(AuditReport report, Facts facts, double cpuBusy, int hzCur, int hzBest)
		{
			BuildMachineCpu(report, cpuBusy);
			BuildMachineDisplay(report, hzCur, hzBest);
			BuildMachineGpu(report);
			BuildMachinePower(report);
			BuildMachineRebar(report);
			BuildMachineMemory(report, facts);
			BuildMachineNetwork(report, facts);
			BuildMachineBattery(report);
			BuildMachineTopConsumers(report);
		}

		private static void BuildMachineCpu(AuditReport report, double cpuBusy)
		{
			int processorCount = Environment.ProcessorCount;
			int num = 0;
			try
			{
				num = CpuTopology.PhysicalCoreCount;
			}
			catch
			{
			}
			string text = (CpuTopology.Hybrid ? Lang.T("t.systemaudit.28") : (CpuTopology.AsymCache ? Lang.T("t.systemaudit.29") : ((CpuTopology.PartitionTag == "symmetric-ccd") ? Lang.T("t.systemaudit.30") : ((CpuTopology.PartitionTag == "pool-iso-core0") ? Lang.T("t.systemaudit.31") : Lang.T("t.systemaudit.32")))));
			report.Machine.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.33"),
				Value = num + Lang.T("t.systemaudit.34") + processorCount + Lang.T("t.systemaudit.35"),
				Note = text + (CpuTopology.HasSafeBackgroundPartition() ? (Lang.T("t.systemaudit.36") + CpuTopology.StrictBoostMask.ToString("X")) : Lang.T("t.systemaudit.37")),
				Evidence = "本机实测",
				Warn = false
			});
			if (report.MeasureOk)
			{
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudit.38"),
					Value = PercentText(cpuBusy),
					Note = Lang.T("t.systemaudit.39") + ((report.MeasureWindowMs >= 1000) ? (report.MeasureWindowMs / 1000 + Lang.T("t.systemaudit.40")) : (report.MeasureWindowMs + Lang.T("t.systemaudit.41"))) + Lang.T("t.systemaudit.42"),
					Evidence = "本机实测",
					Warn = false
				});
			}
		}

		private static void BuildMachineDisplay(AuditReport report, int hzCur, int hzBest)
		{
			bool flag = hzCur > 0 && hzBest > 0;
			bool flag2 = RefreshRateIsBest(hzCur, hzBest);
			report.Machine.Add(new AuditRow
			{
				Name = Lang.T("t.systemauditverdicts.1"),
				Value = (flag ? (hzCur + " Hz" + (flag2 ? Lang.T("t.systemaudit.54") : (Lang.T("t.systemaudit.55") + hzBest + " Hz"))) : Lang.T("t.systemaudithardware.3")),
				Note = ((!flag) ? Lang.T("t.systemaudit.56") : (flag2 ? Lang.T("t.systemaudit.57") : (Lang.T("t.systemaudit.58") + hzCur + Lang.T("t.systemaudit.59") + hzBest + Lang.T("t.systemaudit.60") + hzBest + Lang.T("t.systemaudit.61")))),
				Evidence = "本机实测",
				Warn = (!flag2 || !flag)
			});
		}

		private static void BuildMachineGpu(AuditReport report)
		{
			string text = null;
			try
			{
				text = GpuThrottleProbe.InstantText();
			}
			catch
			{
			}
			if (text != null)
			{
				bool flag = text != Lang.T("t.gputhrottleprobe.6");
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudit.62"),
					Value = text,
					Note = (flag ? Lang.T("t.systemaudit.63") : Lang.T("t.systemaudit.64")),
					Evidence = "本机实测",
					Warn = flag
				});
			}
		}

		private static void BuildMachinePower(AuditReport report)
		{
			bool on;
			if (TryEnergySaver(out on))
			{
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudit.65"),
					Value = (on ? Lang.T("t.systemaudit.66") : Lang.T("t.systemaudit.67")),
					Note = (on ? Lang.T("t.systemaudit.68") : Lang.T("t.systemaudit.69")),
					Evidence = "本机实测",
					Warn = on
				});
			}
			bool flag = false;
			try
			{
				flag = PresenceQos.CurrentlyDisabled();
			}
			catch
			{
			}
			report.Machine.Add(new AuditRow
			{
				Name = Lang.T("t.legacypurge.6"),
				Value = (flag ? Lang.T("col.ux.closed") : Lang.T("t.systemaudit.70")),
				Note = (flag ? Lang.T("t.systemaudit.71") : Lang.T("t.systemaudit.72")),
				Evidence = "本机实测",
				Warn = false
			});
		}

		private static void BuildMachineRebar(AuditReport report)
		{
			bool enabled = false;
			ulong windowBytes = 0uL;
			string gpuName = null;
			bool nvidia = false;
			bool flag = false;
			try
			{
				flag = RebarProbe.TryDetect(out enabled, out windowBytes, out gpuName, out nvidia);
			}
			catch
			{
			}
			if (flag)
			{
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudit.73"),
					Value = (enabled ? Lang.T("v16.device.hags.on") : Lang.T("gs.noff")) + Lang.T("t.systemaudit.74") + RebarProbe.WindowText(windowBytes),
					Note = (string.IsNullOrEmpty(gpuName) ? "" : (gpuName + " ")) + ((!nvidia) ? (enabled ? Lang.T("t.systemaudit.77") : Lang.T("t.systemaudit.78")) : (enabled ? Lang.T("t.systemaudit.75") : Lang.T("t.systemaudit.76"))),
					Evidence = "本机实测",
					Warn = false
				});
			}
		}

		private static void BuildMachineMemory(AuditReport report, Facts facts)
		{
			if (facts.MemOk)
			{
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemaudit.79"),
					Value = facts.TotalGb.ToString("F1") + Lang.T("t.systemaudit.80") + PercentText(facts.UsedRatio) + Lang.T("t.systemaudit.55") + facts.AvailGb.ToString("F1") + " GB",
					Note = ((facts.UsedRatio >= 0.85) ? Lang.T("t.systemaudit.81") : Lang.T("t.systemaudit.82")),
					Evidence = "本机实测",
					Warn = (facts.UsedRatio >= 0.85)
				});
			}
			if (facts.PageOk)
			{
				bool flag = PageFileLooksDisabled(facts.PageFileGb);
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.53"),
					Value = (flag ? Lang.T("t.systemaudit.83") : (facts.PageFileGb.ToString("F1") + " GB")),
					Note = (flag ? Lang.T("t.systemaudit.84") : Lang.T("t.systemaudit.85")),
					Evidence = "本机实测",
					Warn = flag
				});
			}
		}

		private static void BuildMachineNetwork(AuditReport report, Facts facts)
		{
			if (facts.Link == null || !(facts.Link != "none"))
			{
				return;
			}
			bool flag = facts.Link == "wifi";
			report.Machine.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.86"),
				Value = ((facts.Link == "wired") ? Lang.T("t.systemaudit.87") : (flag ? Lang.T("t.systemaudit.88") : Lang.T("t.systemaudit.89"))),
				Note = (flag ? Lang.T("t.systemaudit.90") : ((facts.Link == "wired") ? Lang.T("t.systemaudit.91") : Lang.T("t.systemaudit.92"))),
				Evidence = "本机实测",
				Warn = flag
			});
			if (facts.Link == "both" || LinkMetricTweak.RepairedByPavise)
			{
				bool flag2 = false;
				string note = "";
				try
				{
					flag2 = LinkMetricTweak.NeedsRepair();
					note = LinkMetricTweak.Describe();
				}
				catch
				{
				}
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.linkmetric.name"),
					Value = (flag2 ? Lang.T("t.linkmetric.bad") : Lang.T("t.linkmetric.good")),
					Note = note,
					Evidence = "机制明确",
					Warn = flag2,
					FixKey = "linkmetric"
				});
			}
		}

		private static void BuildMachineBattery(AuditReport report)
		{
			try
			{
				SystemPowerStatus status;
				if (GetSystemPowerStatus(out status) && status.BatteryFlag != 128 && status.BatteryFlag != byte.MaxValue && status.AcLineStatus != byte.MaxValue)
				{
					bool flag = status.AcLineStatus == 1;
					report.Machine.Add(new AuditRow
					{
						Name = Lang.T("t.systemaudit.93"),
						Value = (flag ? Lang.T("t.systemaudit.94") : Lang.T("t.systemaudit.95")),
						Note = (flag ? Lang.T("t.systemaudit.96") : Lang.T("t.systemaudit.97")),
						Evidence = "机制明确",
						Warn = !flag
					});
				}
			}
			catch
			{
			}
		}

		private static void BuildMachineTopConsumers(AuditReport report)
		{
			int num = Math.Max(600, Math.Min(3000, report.MeasureWindowMs / 10));
			List<LoadEntry> list = TopConsumers(num, 3);
			if (list.Count <= 0)
			{
				return;
			}
			List<string> list2 = new List<string>();
			foreach (LoadEntry item in list)
			{
				list2.Add(item.Name + " " + PercentText(item.Ratio));
			}
			report.Machine.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.98"),
				Value = string.Join("   ", list2.ToArray()),
				Note = Lang.T("t.systemaudit.99") + ((double)num / 1000.0).ToString("0.#") + Lang.T("t.systemaudit.100"),
				Evidence = "本机实测",
				Warn = false
			});
		}

		public static AuditReport Collect(int measureWindowMs, Func<bool> cancelIrq = null, Action<float, string> progress = null)
		{
			if (cancelIrq != null && cancelIrq())
			{
				return null;
			}
			AuditReport auditReport = new AuditReport();
			auditReport.MeasureWindowMs = measureWindowMs;
			object progressGate = new object();
			float cpuProgress = 0f;
			float irqProgress = 0f;
			Action reportSampling = delegate
			{
				if (progress != null)
				{
					float num = Math.Min(cpuProgress, irqProgress);
					int num2 = (int)Math.Ceiling((double)measureWindowMs * (1.0 - (double)num) / 1000.0);
					progress(num * 0.9f, Lang.F("audit.phase.sampling", num2));
				}
			};
			Task<GpuIrqResult> task = Task.Factory.StartNew(delegate
			{
				try
				{
					return GpuIrqCheck.Inspect(measureWindowMs, cancelIrq, delegate(float value)
					{
						lock (progressGate)
						{
							irqProgress = value;
							reportSampling();
						}
					});
				}
				catch (Exception ex)
				{
					return new GpuIrqResult
					{
						Outcome = GpuIrqOutcome.Failed,
						Detail = ex.GetType().Name
					};
				}
				finally
				{
					lock (progressGate)
					{
						irqProgress = 1f;
						reportSampling();
					}
				}
			});
			double cpuBusy = 0.0;
			double[] array = null;
			try
			{
				array = DpcSampler.MeasureLoad(measureWindowMs, out cpuBusy, cancelIrq, delegate(float value)
				{
					lock (progressGate)
					{
						cpuProgress = value;
						reportSampling();
					}
				});
			}
			catch
			{
			}
			auditReport.GpuIrq = task.Result;
			if (cancelIrq != null && cancelIrq())
			{
				return null;
			}
			auditReport.MeasureOk = array != null;
			auditReport.CpuBusy = cpuBusy;
			if (progress != null)
			{
				progress(0.91f, Lang.T("audit.phase.capability"));
			}
			if (!Assemble(auditReport, cancelIrq, progress))
			{
				return null;
			}
			return auditReport;
		}

		public static AuditReport Refresh(AuditReport previous, GpuIrqResult irqFix)
		{
			if (previous == null)
			{
				return null;
			}
			AuditReport auditReport = new AuditReport();
			auditReport.MeasureWindowMs = previous.MeasureWindowMs;
			auditReport.MeasureOk = previous.MeasureOk;
			auditReport.CpuBusy = previous.CpuBusy;
			auditReport.GpuIrq = GpuIrqCheck.AfterFix(previous.GpuIrq, irqFix);
			if (!Assemble(auditReport, null, null))
			{
				return null;
			}
			return auditReport;
		}

		private static bool Assemble(AuditReport report, Func<bool> cancelled, Action<float, string> progress)
		{
			int current;
			int best;
			DisplayGuard.QueryRefreshRates(out current, out best);
			Facts facts = Gather();
			BuildCapability(report, facts);
			BuildMachine(report, facts, report.CpuBusy, current, best);
			BuildBypassIo(report, facts);
			BuildHardwareHealth(report, facts);
			BuildInputChain(report, facts);
			if (cancelled != null && cancelled())
			{
				return false;
			}
			if (progress != null)
			{
				progress(0.96f, Lang.T("audit.phase.persistent"));
			}
			BuildPersistent(report, facts);
			BuildGpuInterrupt(report);
			if (GpuIrqCheck.HasResidue || IrqRelocate.HasResidue || IrqPriorityTweak.HasResidue)
			{
				report.Persistent.Add(new AuditRow
				{
					Name = Lang.T("audit.irq.saved"),
					Value = Lang.T("audit.irq.owned"),
					Note = Lang.T("irqcheck.restore.hint"),
					Evidence = "机制明确",
					FixKey = "gpuirqrestore"
				});
			}
			if (cancelled != null && cancelled())
			{
				return false;
			}
			if (progress != null)
			{
				progress(0.99f, Lang.T("audit.phase.verdict"));
			}
			BuildVerdicts(report, facts, current, best);
			return true;
		}

		private static List<BypassIoVerdict> GatherBypassIo()
		{
			List<BypassIoVerdict> list = new List<BypassIoVerdict>();
			if (Native.OsBuild() > 0 && Native.OsBuild() < 22000)
			{
				return list;
			}
			List<string> list2 = new List<string>();
			try
			{
				Func<List<string>> libraryPaths = LibraryPaths;
				List<string> list3 = ((libraryPaths != null) ? libraryPaths() : null);
				if (list3 != null)
				{
					list2.AddRange(list3);
				}
			}
			catch
			{
			}
			try
			{
				using (Process process = Process.GetCurrentProcess())
				{
					list2.Add(process.MainModule.FileName);
				}
			}
			catch
			{
			}
			HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (string item in list2)
			{
				if (string.IsNullOrEmpty(item))
				{
					continue;
				}
				string pathRoot;
				try
				{
					pathRoot = Path.GetPathRoot(item);
				}
				catch
				{
					continue;
				}
				if (string.IsNullOrEmpty(pathRoot) || !hashSet.Add(pathRoot))
				{
					continue;
				}
				try
				{
					if (!File.Exists(item))
					{
						continue;
					}
				}
				catch
				{
					continue;
				}
				BypassIoVerdict bypassIoVerdict = BypassIoProbe.Query(item);
				if (bypassIoVerdict.Supported)
				{
					list.Add(bypassIoVerdict);
				}
				if (list.Count >= 4)
				{
					break;
				}
			}
			return list;
		}

		private static void BuildBypassIo(AuditReport report, Facts facts)
		{
			if (facts.BypassIo == null)
			{
				return;
			}
			foreach (BypassIoVerdict item in facts.BypassIo)
			{
				string text = "";
				try
				{
					text = Path.GetPathRoot(item.Path) ?? "";
				}
				catch
				{
				}
				report.Machine.Add(new AuditRow
				{
					Name = Lang.T("t.bypassio.1") + " " + text.TrimEnd('\\'),
					Value = (item.Enabled ? Lang.T("t.bypassio.2") : Lang.F("t.bypassio.3", (item.Blocker.Length > 0) ? item.Blocker : "?")),
					Note = (item.Enabled ? Lang.T("t.bypassio.4") : (Lang.T("t.bypassio.5") + ((item.Reason.Length > 0) ? (" " + item.Reason) : ""))),
					Evidence = "机制明确",
					Warn = !item.Enabled
				});
			}
		}

		private static void BuildCapability(AuditReport report, Facts facts)
		{
			if (facts.Gpus.Length > 0)
			{
				report.Capability.Add(new AuditRow
				{
					Name = Lang.T("cfg.group.gpu"),
					Value = GpuInventory.Describe(),
					Note = (facts.IntegratedOnly ? Lang.T("t.systemaudit.3") : Lang.T("t.systemaudit.4")),
					Evidence = "本机实测",
					Warn = false
				});
			}
			report.Capability.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.5"),
				Value = (facts.Nv ? Lang.T("t.systemaudit.6") : Lang.T("t.systemaudit.7")),
				Note = (facts.Nv ? Lang.T("t.systemaudit.8") : (facts.NvHardware ? Lang.T("t.systemaudit.9") : Lang.T("t.systemaudit.10"))),
				Evidence = "本机实测",
				Warn = false
			});
			report.Capability.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.16"),
				Value = (facts.Partition ? Lang.T("t.systemaudit.6") : Lang.T("t.systemaudit.7")),
				Note = (facts.Partition ? Lang.T("t.systemaudit.17") : Lang.T("t.systemaudit.18")),
				Evidence = "本机实测",
				Warn = false
			});
			report.Capability.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.19"),
				Value = ((!facts.Eco) ? Lang.T("t.systemaudit.20") : (facts.EcoFull ? Lang.T("t.systemaudit.21") : Lang.T("t.systemaudit.22"))),
				Note = ((!facts.Eco) ? Lang.T("t.systemaudit.23") : (facts.EcoFull ? Lang.T("t.systemaudit.24") : Lang.T("t.systemaudit.25"))),
				Evidence = "本机实测",
				Warn = !facts.Eco
			});
			report.Capability.Add(new AuditRow
			{
				Name = Lang.T("t.systemaudit.26"),
				Value = WindowsText(),
				Note = Lang.T("t.systemaudit.27"),
				Evidence = "本机实测",
				Warn = false
			});
		}

		public static List<LoadEntry> TopConsumers(int windowMs, int take)
		{
			List<LoadEntry> list = new List<LoadEntry>();
			try
			{
				Dictionary<int, Sample> dictionary = new Dictionary<int, Sample>();
				Process[] processes = Process.GetProcesses();
				foreach (Process process in processes)
				{
					using (process)
					{
						try
						{
							if (process.Id > 4)
							{
								dictionary[process.Id] = new Sample
								{
									Name = process.ProcessName,
									Started = process.StartTime.Ticks,
									Cpu = process.TotalProcessorTime
								};
							}
						}
						catch
						{
						}
					}
				}
				if (dictionary.Count == 0)
				{
					return list;
				}
				Thread.Sleep(windowMs);
				double num = (double)windowMs / 1000.0 * (double)Environment.ProcessorCount;
				if (num <= 0.0)
				{
					return list;
				}
				Process[] processes2 = Process.GetProcesses();
				foreach (Process process3 in processes2)
				{
					using (process3)
					{
						try
						{
							Sample value;
							if (dictionary.TryGetValue(process3.Id, out value) && process3.StartTime.Ticks == value.Started)
							{
								double totalSeconds = (process3.TotalProcessorTime - value.Cpu).TotalSeconds;
								if (!(totalSeconds <= 0.0))
								{
									list.Add(new LoadEntry
									{
										Name = value.Name,
										Ratio = totalSeconds / num
									});
								}
							}
						}
						catch
						{
						}
					}
				}
			}
			catch
			{
				return list;
			}
			list.Sort((LoadEntry a, LoadEntry b) => b.Ratio.CompareTo(a.Ratio));
			if (list.Count > take)
			{
				list.RemoveRange(take, list.Count - take);
			}
			return list;
		}

		private static void BuildVerdicts(AuditReport report, Facts facts, int hzCur, int hzBest)
		{
			if (hzCur > 0 && hzBest > 0 && !RefreshRateIsBest(hzCur, hzBest))
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.1"),
					Value = Lang.T("t.systemauditverdicts.2"),
					Note = Lang.T("t.systemauditverdicts.3") + hzCur + Lang.T("t.systemauditverdicts.4") + hzBest + Lang.T("t.systemauditverdicts.5") + hzBest + Lang.T("t.systemauditverdicts.6"),
					Evidence = "机制明确",
					Warn = true
				});
			}
			if (facts.Access != null && facts.Access.FilterKeysSwallowing)
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.7"),
					Value = Lang.T("t.systemauditverdicts.2"),
					Note = Lang.T("t.systemauditverdicts.8") + facts.Access.DelayBeforeAcceptanceMs + Lang.T("t.systemauditverdicts.9"),
					Evidence = "机制明确",
					Warn = true
				});
			}
			else if (facts.Access != null && facts.Access.AnyNeedsFix)
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.10"),
					Value = Lang.T("t.systemauditverdicts.11"),
					Note = Lang.T("t.systemauditverdicts.12"),
					Evidence = "机制明确",
					Warn = false
				});
			}
			bool flag = false;
			try
			{
				flag = InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, true) || InputChainProbe.BluetoothIsOnlyOption(facts.Inputs, false);
			}
			catch
			{
			}
			if (flag)
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.13"),
					Value = Lang.T("t.systemauditverdicts.14"),
					Note = Lang.T("t.systemauditverdicts.15"),
					Evidence = "台架实测",
					Warn = true
				});
			}
			if (!facts.SuppressOn)
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("cfg.group.bg"),
					Value = Lang.T("t.systemauditverdicts.24"),
					Note = Lang.T("t.systemauditverdicts.25"),
					Evidence = "台架实测",
					Warn = false
				});
			}
			if (facts.Dvr)
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.43"),
					Value = Lang.T("t.systemauditverdicts.11"),
					Note = Lang.T("t.systemauditverdicts.44"),
					Evidence = "机制明确",
					Warn = false
				});
			}
			if (facts.MemOk && facts.UsedRatio >= 0.85)
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.45"),
					Value = Lang.T("t.systemauditverdicts.35"),
					Note = Lang.T("t.systemauditverdicts.46") + facts.AvailGb.ToString("F1") + Lang.T("t.systemauditverdicts.47"),
					Evidence = "机制明确",
					Warn = true
				});
			}
			if (facts.PageOk && PageFileLooksDisabled(facts.PageFileGb))
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.53"),
					Value = Lang.T("t.systemauditverdicts.54"),
					Note = Lang.T("t.systemauditverdicts.55"),
					Evidence = "机制明确",
					Warn = true
				});
			}
			bool unparked;
			if (!CpuTopology.AsymCache && PowerPlan.TryCurrentUnparked(out unparked) && !unparked)
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.legacypurge.3"),
					Value = Lang.T("t.systemauditverdicts.56"),
					Note = Lang.T("t.systemauditverdicts.57"),
					Evidence = "机制明确",
					Warn = false
				});
			}
			if (report.Verdicts.Count == 0)
			{
				report.Verdicts.Add(new AuditRow
				{
					Name = Lang.T("t.systemauditverdicts.60"),
					Value = Lang.T("t.systemauditverdicts.62"),
					Note = Lang.T("t.systemauditverdicts.61"),
					Evidence = "本机实测",
					Warn = false
				});
			}
		}
	}
}
