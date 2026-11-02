using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PaviseApp
{
	internal static class InputChainProbe
	{
		internal const string EnumRoot = "SYSTEM\\CurrentControlSet\\Enum";

		internal const string AccessRoot = "Control Panel\\Accessibility";

		internal const string FilterKeysPath = "Control Panel\\Accessibility\\Keyboard Response";

		internal const string StickyKeysPath = "Control Panel\\Accessibility\\StickyKeys";

		internal const string ToggleKeysPath = "Control Panel\\Accessibility\\ToggleKeys";

		internal const string MousePath = "Control Panel\\Mouse";

		private const string BtClassicUuid = "{00001124-0000-1000-8000-00805f9b34fb}";

		private const string BtLeUuid = "{00001812-0000-1000-8000-00805f9b34fb}";

		internal const string MouseClassGuid = "{4d36e96f-e325-11ce-bfc1-08002be10318}";

		internal const string KeyboardClassGuid = "{4d36e96b-e325-11ce-bfc1-08002be10318}";

		internal static bool SplitUsbInstance(string instanceId, out string hardware, out string instance)
		{
			hardware = null;
			instance = null;
			if (string.IsNullOrEmpty(instanceId))
			{
				return false;
			}
			if (!instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}
			int length = "USB\\".Length;
			int num = instanceId.IndexOf('\\', length);
			if (num < 0 || num + 1 >= instanceId.Length)
			{
				return false;
			}
			hardware = instanceId.Substring(length, num - length);
			instance = instanceId.Substring(num + 1);
			if (hardware.Length > 0)
			{
				return instance.Length > 0;
			}
			return false;
		}

		internal static int ClassifyDeviceClass(string classGuid, string className)
		{
			if (!string.IsNullOrEmpty(classGuid))
			{
				if (string.Equals(classGuid, "{4d36e96f-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
				{
					return 1;
				}
				if (string.Equals(classGuid, "{4d36e96b-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
				{
					return 0;
				}
				return -1;
			}
			if (string.Equals(className, "Mouse", StringComparison.OrdinalIgnoreCase))
			{
				return 1;
			}
			if (string.Equals(className, "Keyboard", StringComparison.OrdinalIgnoreCase))
			{
				return 0;
			}
			return -1;
		}

		internal static string CleanDesc(string raw)
		{
			if (string.IsNullOrEmpty(raw))
			{
				return null;
			}
			if (raw[0] != '@')
			{
				return raw;
			}
			int num = raw.LastIndexOf(';');
			if (num < 0 || num + 1 >= raw.Length)
			{
				return null;
			}
			string text = raw.Substring(num + 1).Trim();
			if (text.Length != 0)
			{
				return text;
			}
			return null;
		}

		internal static InputTransport ClassifyTransport(string instanceId)
		{
			if (string.IsNullOrEmpty(instanceId))
			{
				return InputTransport.Unknown;
			}
			string text = instanceId.ToUpperInvariant();
			if (text.StartsWith("ROOT\\", StringComparison.Ordinal) || text.StartsWith("SWD\\", StringComparison.Ordinal))
			{
				return InputTransport.Virtual;
			}
			if (text.StartsWith("BTHLEDEVICE", StringComparison.Ordinal))
			{
				return InputTransport.BluetoothLe;
			}
			if (text.StartsWith("BTHENUM", StringComparison.Ordinal))
			{
				return InputTransport.BluetoothClassic;
			}
			if (text.IndexOf("{00001812-0000-1000-8000-00805f9b34fb}".ToUpperInvariant(), StringComparison.Ordinal) >= 0)
			{
				return InputTransport.BluetoothLe;
			}
			if (text.IndexOf("{00001124-0000-1000-8000-00805f9b34fb}".ToUpperInvariant(), StringComparison.Ordinal) >= 0)
			{
				return InputTransport.BluetoothClassic;
			}
			if (text.StartsWith("ACPI\\", StringComparison.Ordinal))
			{
				if (text.IndexOf("PNP0303", StringComparison.Ordinal) >= 0 || text.IndexOf("PNP0F03", StringComparison.Ordinal) >= 0 || text.IndexOf("PNP0F13", StringComparison.Ordinal) >= 0)
				{
					return InputTransport.Ps2;
				}
				return InputTransport.Internal;
			}
			if (text.StartsWith("USB\\", StringComparison.Ordinal))
			{
				return InputTransport.Usb;
			}
			if (text.StartsWith("HID\\VID_", StringComparison.Ordinal))
			{
				return InputTransport.Usb;
			}
			if (text.StartsWith("HID\\", StringComparison.Ordinal))
			{
				return ClassifyBareHid(instanceId);
			}
			return InputTransport.Unknown;
		}

		private static InputTransport ClassifyBareHid(string instanceId)
		{
			foreach (string item in PresentDevices.ParentChain(instanceId))
			{
				string text = item.ToUpperInvariant();
				if (text.StartsWith("ACPI\\", StringComparison.Ordinal))
				{
					return InputTransport.Internal;
				}
				if (text.StartsWith("USB\\", StringComparison.Ordinal))
				{
					return InputTransport.Usb;
				}
				if (text.StartsWith("BTHENUM", StringComparison.Ordinal))
				{
					return InputTransport.BluetoothClassic;
				}
				if (text.StartsWith("BTHLE", StringComparison.Ordinal))
				{
					return InputTransport.BluetoothLe;
				}
				if (text.StartsWith("ROOT\\", StringComparison.Ordinal) || text.StartsWith("SWD\\", StringComparison.Ordinal))
				{
					return InputTransport.Virtual;
				}
			}
			return InputTransport.Virtual;
		}

		internal static string UsbHardwareKeyOf(string hidInstanceId)
		{
			if (string.IsNullOrEmpty(hidInstanceId))
			{
				return null;
			}
			if (!hidInstanceId.StartsWith("HID\\VID_", StringComparison.OrdinalIgnoreCase))
			{
				return null;
			}
			int length = "HID\\".Length;
			int num = hidInstanceId.IndexOf('\\', length);
			string text = ((num < 0) ? hidInstanceId.Substring(length) : hidInstanceId.Substring(length, num - length));
			string[] array = text.Split('&');
			List<string> list = new List<string>();
			string[] array2 = array;
			foreach (string text2 in array2)
			{
				if (!text2.StartsWith("COL", StringComparison.OrdinalIgnoreCase))
				{
					list.Add(text2);
				}
			}
			if (list.Count == 0)
			{
				return null;
			}
			return string.Join("&", list.ToArray());
		}

		public static AccessibilityState ReadAccessibility()
		{
			AccessibilityState accessibilityState = new AccessibilityState();
			try
			{
				int flags;
				bool flag = ReadFlags("Control Panel\\Accessibility\\Keyboard Response", out flags);
				int flags2;
				bool flag2 = ReadFlags("Control Panel\\Accessibility\\StickyKeys", out flags2);
				int flags3;
				bool flag3 = ReadFlags("Control Panel\\Accessibility\\ToggleKeys", out flags3);
				accessibilityState.FilterKeys = flags;
				accessibilityState.StickyKeys = flags2;
				accessibilityState.ToggleKeys = flags3;
				accessibilityState.Read = flag || flag2 || flag3;
				using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey("Control Panel\\Accessibility\\Keyboard Response"))
				{
					int flags4;
					if (registryKey != null && AccessibilityFlags.TryParse(registryKey.GetValue("DelayBeforeAcceptance") as string, out flags4))
					{
						accessibilityState.DelayBeforeAcceptanceMs = flags4;
					}
				}
			}
			catch
			{
			}
			return accessibilityState;
		}

		private static bool ReadFlags(string path, out int flags)
		{
			flags = 0;
			try
			{
				using (RegistryKey registryKey = Registry.CurrentUser.OpenSubKey(path))
				{
					if (registryKey == null)
					{
						return false;
					}
					return AccessibilityFlags.TryParse(registryKey.GetValue("Flags") as string, out flags);
				}
			}
			catch
			{
				return false;
			}
		}

		public static List<InputDevice> Devices()
		{
			List<InputDevice> list = new List<InputDevice>();
			try
			{
				Collect(PresentDevices.ByClass(new Guid("{4d36e96f-e325-11ce-bfc1-08002be10318}")), true, list);
				Collect(PresentDevices.ByClass(new Guid("{4d36e96b-e325-11ce-bfc1-08002be10318}")), false, list);
			}
			catch
			{
			}
			return list;
		}

		private static void Collect(List<string> ids, bool mouse, List<InputDevice> list)
		{
			if (ids == null)
			{
				return;
			}
			foreach (string id in ids)
			{
				if (!string.IsNullOrEmpty(id))
				{
					list.Add(new InputDevice
					{
						InstanceId = id,
						Name = (NameOf(id) ?? id),
						IsMouse = mouse,
						Transport = ClassifyTransport(id),
						UsbHardwareKey = UsbHardwareKeyOf(id)
					});
				}
			}
		}

		private static string NameOf(string instanceId)
		{
			try
			{
				using (RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Enum\\" + instanceId))
				{
					if (registryKey == null)
					{
						return null;
					}
					return CleanDesc(registryKey.GetValue("FriendlyName") as string) ?? CleanDesc(registryKey.GetValue("DeviceDesc") as string);
				}
			}
			catch
			{
				return null;
			}
		}

		public static bool AnyBluetooth(List<InputDevice> devices)
		{
			if (devices == null)
			{
				return false;
			}
			foreach (InputDevice device in devices)
			{
				if (device.Transport == InputTransport.BluetoothClassic || device.Transport == InputTransport.BluetoothLe)
				{
					return true;
				}
			}
			return false;
		}

		public static bool BluetoothIsOnlyOption(List<InputDevice> devices, bool mouse)
		{
			if (devices == null)
			{
				return false;
			}
			bool flag = false;
			bool flag2 = false;
			foreach (InputDevice device in devices)
			{
				if (device.IsMouse == mouse)
				{
					if (device.Transport == InputTransport.BluetoothClassic || device.Transport == InputTransport.BluetoothLe)
					{
						flag = true;
					}
					else if (device.Transport == InputTransport.Usb || device.Transport == InputTransport.Ps2 || device.Transport == InputTransport.Internal)
					{
						flag2 = true;
					}
				}
			}
			if (flag)
			{
				return !flag2;
			}
			return false;
		}

		public static string TransportText(InputTransport transport)
		{
			switch (transport)
			{
			case InputTransport.Ps2:
				return "PS/2";
			case InputTransport.Usb:
				return Lang.T("t.inputchainprobe.1");
			case InputTransport.BluetoothClassic:
				return Lang.T("t.peripheralcatalog.9");
			case InputTransport.BluetoothLe:
				return Lang.T("t.inputchainprobe.2");
			case InputTransport.Virtual:
				return Lang.T("t.inputchainprobe.3");
			case InputTransport.Internal:
				return Lang.T("t.inputchainprobe.4");
			default:
				return Lang.T("t.versionmigrations.2");
			}
		}
	}
}
