using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace PaviseApp
{
	internal static class CacheWarmPlatform
	{
		private struct Memory
		{
			internal uint Length;

			internal uint Load;

			internal ulong Total;

			internal ulong Available;

			internal ulong PageTotal;

			internal ulong PageAvailable;

			internal ulong VirtualTotal;

			internal ulong VirtualAvailable;

			internal ulong Extended;
		}

		private struct Power
		{
			internal byte Ac;

			internal byte Flags;

			internal byte Percent;

			internal byte Saver;

			internal uint Life;

			internal uint FullLife;
		}

		[DllImport("kernel32.dll")]
		private static extern bool GlobalMemoryStatusEx(ref Memory value);

		[DllImport("kernel32.dll")]
		private static extern bool GetSystemPowerStatus(out Power value);

		[DllImport("kernel32.dll")]
		private static extern IntPtr GetCurrentThread();

		[DllImport("kernel32.dll")]
		private static extern bool SetThreadPriority(IntPtr thread, int priority);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);

		internal static CacheWarmEnvironment Sample()
		{
			Memory memory = default(Memory);
			memory.Length = (uint)Marshal.SizeOf(typeof(Memory));
			Memory value = memory;
			Power value2;
			if (!GlobalMemoryStatusEx(ref value) || !GetSystemPowerStatus(out value2))
			{
				return new CacheWarmEnvironment();
			}
			CacheWarmEnvironment cacheWarmEnvironment = new CacheWarmEnvironment();
			cacheWarmEnvironment.Ac = value2.Ac == 1 && value2.Saver == 0;
			cacheWarmEnvironment.Total = value.Total;
			cacheWarmEnvironment.Available = value.Available;
			return cacheWarmEnvironment;
		}

		internal static bool Background(bool begin)
		{
			return SetThreadPriority(GetCurrentThread(), begin ? 65536 : 131072);
		}

		internal static bool SolidState(string root)
		{
			try
			{
				string pathRoot = Path.GetPathRoot(root);
				if (pathRoot.Length != 3 || new DriveInfo(pathRoot).DriveType != DriveType.Fixed)
				{
					return false;
				}
				using (SafeFileHandle safeFileHandle = CreateFile("\\\\.\\" + pathRoot.Substring(0, 2), 0u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero))
				{
					if (safeFileHandle.IsInvalid)
					{
						return false;
					}
					byte[] rawBytes = new byte[12];
					byte[] rawBytes2 = new byte[12];
					rawBytes[0] = 7;
					int returned;
					return DeviceIoControl(safeFileHandle, 2954240u, rawBytes, rawBytes.Length, rawBytes2, rawBytes2.Length, out returned, IntPtr.Zero) && returned >= 9 && BitConverter.ToUInt32(rawBytes2, 4) >= 9 && rawBytes2[8] == 0;
				}
			}
			catch
			{
				return false;
			}
		}

		internal static bool SafePath(string path)
		{
			try
			{
				string fullPath = Path.GetFullPath(path);
				while (!string.IsNullOrEmpty(fullPath))
				{
					if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
					{
						return false;
					}
					fullPath = Path.GetDirectoryName(fullPath);
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		internal static bool HandleUnder(FileStream stream, string root)
		{
			StringBuilder stringBuilder = new StringBuilder(32768);
			uint finalPathNameByHandle = GetFinalPathNameByHandle(stream.SafeFileHandle, stringBuilder, (uint)stringBuilder.Capacity, 0u);
			if (finalPathNameByHandle == 0 || finalPathNameByHandle >= stringBuilder.Capacity)
			{
				return false;
			}
			string stringBuilderText = stringBuilder.ToString();
			if (stringBuilderText.StartsWith("\\\\?\\", StringComparison.Ordinal))
			{
				stringBuilderText = stringBuilderText.Substring(4);
			}
			return stringBuilderText.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
		}
	}
}
