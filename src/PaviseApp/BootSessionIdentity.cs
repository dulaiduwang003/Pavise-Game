using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class BootSessionIdentity
	{
		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);

		internal static string Read()
		{
			try
			{
				byte[] rawBytes = new byte[712];
				UIntPtr read;
				if (!ReadProcessMemory(new IntPtr(-1), new IntPtr(2147352576), rawBytes, new UIntPtr((uint)rawBytes.Length), out read) || read.ToUInt64() != (ulong)rawBytes.Length)
				{
					return null;
				}
				if (BitConverter.ToUInt32(rawBytes, 620) != 10)
				{
					return null;
				}
				Guid result;
				if (!Guid.TryParse(new CoreIsolationNative().BootIdentity(), out result))
				{
					return null;
				}
				return Create(result, BitConverter.ToUInt32(rawBytes, 708));
			}
			catch
			{
				return null;
			}
		}

		internal static string Create(Guid loader, uint sequence)
		{
			if (!(loader == Guid.Empty) && sequence != 0)
			{
				return "boot2:" + loader.ToString("N") + ":" + sequence.ToString(CultureInfo.InvariantCulture);
			}
			return null;
		}

		internal static bool Valid(string identity)
		{
			if (string.IsNullOrEmpty(identity))
			{
				return false;
			}
			string[] identityParts = identity.Split(':');
			Guid result;
			uint result2;
			if (identityParts.Length == 3 && identityParts[0] == "boot2" && Guid.TryParseExact(identityParts[1], "N", out result) && result != Guid.Empty && uint.TryParse(identityParts[2], NumberStyles.None, CultureInfo.InvariantCulture, out result2))
			{
				return result2 != 0;
			}
			return false;
		}

		internal static IrqRebootState Compare(string saved, string current)
		{
			if (!Valid(saved) || !Valid(current))
			{
				return IrqRebootState.Unknown;
			}
			if (!string.Equals(saved, current, StringComparison.OrdinalIgnoreCase))
			{
				return IrqRebootState.Rebooted;
			}
			return IrqRebootState.AwaitingReboot;
		}
	}
}
