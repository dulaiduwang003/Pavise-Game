using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class IrqDeviceInventory
	{
		private const string EnumRoot = "SYSTEM\\CurrentControlSet\\Enum\\";

		private const string AffSuffix = "\\Device Parameters\\Interrupt Management\\Affinity Policy";

		private const string IntrSuffix = "\\Device Parameters\\Interrupt Management";

		private const string MsiSuffix = "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties";

		private const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

		private const int MaxPerBus = 512;

		private static readonly string[] Buses = new string[4] { "PCI", "USB", "HDAUDIO", "ACPI" };

		public static List<IrqDevice> Enumerate()
		{
			List<IrqDevice> list = new List<IrqDevice>();
			string[] buses = Buses;
			foreach (string bus in buses)
			{
				try
				{
					Walk(bus, list);
				}
				catch
				{
				}
			}
			return list;
		}

		private static void Walk(string bus, List<IrqDevice> list)
		{
			using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + bus))
			{
				if (registryKey == null)
				{
					return;
				}
				int walkValue = 0;
				string[] subKeyNames = registryKey.GetSubKeyNames();
				foreach (string entryKey in subKeyNames)
				{
					try
					{
						using (RegistryKey registryKey2 = registryKey.OpenSubKey(entryKey))
						{
							if (registryKey2 == null)
							{
								continue;
							}
							string[] subKeyNames2 = registryKey2.GetSubKeyNames();
							foreach (string entryKey2 in subKeyNames2)
							{
								if (++walkValue > 512)
								{
									return;
								}
								AddDevice(bus + "\\" + entryKey + "\\" + entryKey2, bus, list, Read);
							}
						}
					}
					catch
					{
					}
				}
			}
		}

		internal static void AddDevice(string id, string bus, List<IrqDevice> list, Func<string, string, IrqDevice> read)
		{
			try
			{
				IrqDevice irqDevice = read(id, bus);
				if (irqDevice != null)
				{
					list.Add(irqDevice);
				}
			}
			catch
			{
			}
		}

		private static IrqDevice Read(string instanceId, string bus)
		{
			using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management"))
			{
				if (registryKey == null)
				{
					return null;
				}
			}
			using (RegistryKey registryKey2 = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Control"))
			{
				if (registryKey2 == null)
				{
					return null;
				}
			}
			IrqDevice irqDevice = new IrqDevice();
			irqDevice.InstanceId = instanceId;
			irqDevice.Bus = bus;
			irqDevice.ClassGuid = ReadStr(instanceId, "ClassGUID");
			try
			{
				using (RegistryKey registryKey3 = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId))
				{
					if (registryKey3 != null)
					{
						irqDevice.Name = (registryKey3.GetValue("FriendlyName") as string) ?? (registryKey3.GetValue("DeviceDesc") as string) ?? "";
						int matchIndex = irqDevice.Name.LastIndexOf(';');
						if (matchIndex >= 0 && matchIndex + 1 < irqDevice.Name.Length)
						{
							irqDevice.Name = irqDevice.Name.Substring(matchIndex + 1);
						}
						irqDevice.Service = (registryKey3.GetValue("Service") as string) ?? "";
					}
				}
			}
			catch
			{
			}
			if (irqDevice.Name.Length == 0)
			{
				irqDevice.Name = DriverDeviceResolver.ShortId(instanceId);
			}
			try
			{
				string str = ReadStr(instanceId, "Driver");
				if (str.Length > 0)
				{
					using (RegistryKey registryKey4 = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Control\\Class\\" + str))
					{
						if (registryKey4 != null)
						{
							irqDevice.DriverVersion = (registryKey4.GetValue("DriverVersion") as string) ?? "";
						}
					}
				}
			}
			catch
			{
			}
			irqDevice.MessageCount = ReadMessageCount(instanceId);
			irqDevice.MultiMessageRisk = LooksMultiQueue(irqDevice);
			try
			{
				using (RegistryKey registryKey5 = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\Affinity Policy"))
				{
					irqDevice.ConfigurationKnown = true;
					if (registryKey5 != null)
					{
						object value = registryKey5.GetValue("DevicePolicy");
						if (value != null)
						{
							if (!(value is int))
							{
								irqDevice.ConfigurationKnown = false;
							}
							else
							{
								irqDevice.Policy = (int)value;
							}
						}
						object value2 = registryKey5.GetValue("AssignmentSetOverride");
						byte[] value2Offset = value2 as byte[];
						if (value2 != null && (value2Offset == null || value2Offset.Length != 8))
						{
							irqDevice.ConfigurationKnown = false;
						}
						if (value2Offset != null)
						{
							irqDevice.Mask = IrqAffinityEngine.BytesToMask(value2Offset);
						}
						object value3 = registryKey5.GetValue("DevicePriority");
						if (value3 != null && !(value3 is int))
						{
							irqDevice.ConfigurationKnown = false;
						}
					}
				}
			}
			catch
			{
				irqDevice.ConfigurationKnown = false;
			}
			return irqDevice;
		}

		private static string ReadStr(string instanceId, string name)
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId))
				{
					return (registryKey == null) ? "" : ((registryKey.GetValue(name) as string) ?? "");
				}
			}
			catch
			{
				return "";
			}
		}

		private static int ReadMessageCount(string instanceId)
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId + "\\Device Parameters\\Interrupt Management\\MessageSignaledInterruptProperties"))
				{
					if (registryKey == null)
					{
						return 0;
					}
					object value = registryKey.GetValue("MessageNumberLimit");
					if (value == null)
					{
						return 0;
					}
					return Convert.ToInt32(value);
				}
			}
			catch
			{
				return 0;
			}
		}

		internal static bool LooksMultiQueue(IrqDevice d)
		{
			if (d == null)
			{
				return false;
			}
			if (d.MessageCount > 1)
			{
				return true;
			}
			if (string.Equals(d.ClassGuid, "{4d36e972-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
			{
				return d.MessageCount == 0;
			}
			return false;
		}
	}
}
