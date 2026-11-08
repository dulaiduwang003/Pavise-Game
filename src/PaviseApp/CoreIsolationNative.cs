using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal sealed class CoreIsolationNative : ICoreIsolationPlatform
	{
		private readonly object queryGate = new object();
		private byte[] systemBuffer;
		private byte[] allowedBuffer;
		private const int MaxSystemBytes = 65536;
		private const int MaxSystemReads = 3;

		[DllImport("ntdll.dll")]
		private static extern int NtSetSystemInformation(int c, byte[] b, int n);

		[DllImport("ntdll.dll")]
		private static extern int NtSetInformationProcess(IntPtr h, int c, byte[] b, int n);

		[DllImport("ntdll.dll")]
		private static extern int NtQueryInformationProcess(IntPtr h, int c, byte[] b, int n, out int r);

		[DllImport("ntdll.dll")]
		private static extern int NtQuerySystemInformation(int c, byte[] b, int n, out int r);

		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool GetSystemCpuSetInformation(byte[] b, int n, out int r, IntPtr h, int flags);

		[DllImport("kernel32.dll")]
		private static extern uint WaitForSingleObject(IntPtr h, uint ms);

		public string BootIdentity()
		{
			byte[] array = new byte[32];
			int r;
			if (NtQuerySystemInformation(90, array, array.Length, out r) < 0 || r < 20 || r > array.Length)
			{
				return null;
			}
			byte[] array2 = new byte[16];
			Array.Copy(array, array2, 16);
			Guid guid = new Guid(array2);
			if (!(guid == Guid.Empty))
			{
				return guid.ToString("N");
			}
			return null;
		}

		public IsolationCpuState ReadSystem(IntPtr process)
		{
			lock (queryGate)
			{
				int r;
				if (systemBuffer == null)
				{
					bool sized = GetSystemCpuSetInformation(null, 0, out r, process, 0);
					if ((!sized && Marshal.GetLastWin32Error() != 122) || r < 32 || r > MaxSystemBytes) return null;
					systemBuffer = new byte[r];
				}
				for (int attempt = 0; attempt < MaxSystemReads; attempt++)
				{
					Array.Clear(systemBuffer, 0, systemBuffer.Length);
					if (GetSystemCpuSetInformation(systemBuffer, systemBuffer.Length, out r, process, 0))
					{
						return r >= 32 && r <= systemBuffer.Length ? Decode(systemBuffer, r) : null;
					}
					if (Marshal.GetLastWin32Error() != 122 || r <= systemBuffer.Length || r > MaxSystemBytes || attempt + 1 == MaxSystemReads) return null;
					systemBuffer = new byte[r];
				}
				return null;
			}
		}

		internal static IsolationCpuState Decode(byte[] b, int length)
		{
			if (b == null || length <= 0 || length > b.Length)
			{
				return null;
			}
			IsolationCpuState isolationCpuState = new IsolationCpuState();
			SortedDictionary<int, ulong> sortedDictionary = new SortedDictionary<int, ulong>();
			HashSet<uint> hashSet = new HashSet<uint>();
			uint num;
			for (int i = 0; i < length; i += (int)num)
			{
				if (length - i < 32)
				{
					return null;
				}
				num = BitConverter.ToUInt32(b, i);
				if (num < 32 || num > length - i || BitConverter.ToInt32(b, i + 4) != 0 || BitConverter.ToUInt16(b, i + 12) != 0 || b[i + 14] >= 64 || !hashSet.Add(BitConverter.ToUInt32(b, i + 8)))
				{
					return null;
				}
				ulong num2 = (ulong)(1L << (int)b[i + 14]);
				if ((isolationCpuState.All & num2) != 0)
				{
					return null;
				}
				isolationCpuState.All |= num2;
				if ((b[i + 19] & 2u) != 0)
				{
					isolationCpuState.Allocated |= num2;
				}
				if ((b[i + 19] & 4u) != 0)
				{
					isolationCpuState.Admitted |= num2;
				}
				ulong value;
				sortedDictionary.TryGetValue(b[i + 15], out value);
				sortedDictionary[b[i + 15]] = value | num2;
			}
			isolationCpuState.Physical = new List<ulong>(sortedDictionary.Values).ToArray();
			if (isolationCpuState.All != 0)
			{
				return isolationCpuState;
			}
			return null;
		}

		public bool SetSystemAllowed(ulong mask)
		{
			if (Native.EnsureBoostPrivilege())
			{
				return NtSetSystemInformation(168, BitConverter.GetBytes(mask), 8) == 0;
			}
			return false;
		}

		public IntPtr Open(int pid, long creation)
		{
			IntPtr intPtr = Native.OpenProcess(1060864, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return intPtr;
			}
			long creation2;
			long cpu;
			ulong io;
			if (Native.QueryProcessSample(intPtr, out creation2, out cpu, out io) && creation2 == creation)
			{
				return intPtr;
			}
			Native.CloseHandle(intPtr);
			return IntPtr.Zero;
		}

		public bool Exited(IntPtr h)
		{
			return WaitForSingleObject(h, 0u) == 0;
		}

		public bool Gone(int pid, long creation)
		{
			IntPtr intPtr = Native.OpenProcess(4096, false, pid);
			if (intPtr == IntPtr.Zero)
			{
				return Native.LastOpenProcessFailureWasNoSuchProcess();
			}
			try
			{
				long creation2;
				long cpu;
				ulong io;
				return Native.QueryProcessSample(intPtr, out creation2, out cpu, out io) && (creation2 != creation || !Native.StillActive(intPtr));
			}
			finally
			{
				Native.CloseHandle(intPtr);
			}
		}

		public bool ReadAllowed(IntPtr h, out ulong mask)
		{
			mask = 0uL;
			lock (queryGate)
			{
				if (allowedBuffer == null) allowedBuffer = new byte[160];
				Array.Clear(allowedBuffer, 0, allowedBuffer.Length);
				int r;
				if (NtQueryInformationProcess(h, 67, allowedBuffer, allowedBuffer.Length, out r) != 0 || r != 8)
				{
					return false;
				}
				mask = BitConverter.ToUInt64(allowedBuffer, 0);
				return true;
			}
		}

		public bool SetAllowed(IntPtr h, ulong mask)
		{
			if (Native.EnsureBoostPrivilege())
			{
				return NtSetInformationProcess(h, 67, BitConverter.GetBytes(mask), 8) == 0;
			}
			return false;
		}

		public void Close(IntPtr h)
		{
			if (h != IntPtr.Zero)
			{
				Native.CloseHandle(h);
			}
		}
	}
}
