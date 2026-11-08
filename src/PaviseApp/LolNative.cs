using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class LolNative
	{
#pragma warning disable 0649
		private struct TcpRowOwnerPid
		{
			public uint State;

			public uint LocalAddr;

			public uint LocalPort;

			public uint RemoteAddr;

			public uint RemotePort;

			public uint OwningPid;
		}
#pragma warning restore 0649

		[DllImport("iphlpapi.dll", SetLastError = true)]
		private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int addressFamily, int tableClass, int reserved);

		public static bool TryGetTcpListenerOwner(int port, out int pid)
		{
			pid = 0;
			if (port <= 0 || port > 65535)
			{
				return false;
			}
			int size = 0;
			GetExtendedTcpTable(IntPtr.Zero, ref size, false, 2, 3, 0);
			if (size <= 0)
			{
				return false;
			}
			IntPtr intPtr = Marshal.AllocHGlobal(size);
			try
			{
				if (GetExtendedTcpTable(intPtr, ref size, false, 2, 3, 0) != 0)
				{
					return false;
				}
				int int32 = Marshal.ReadInt32(intPtr);
				int sizeOfResult = Marshal.SizeOf(typeof(TcpRowOwnerPid));
				long toInt64Offset = intPtr.ToInt64() + 4;
				int processedCount = 0;
				while (processedCount < int32)
				{
					TcpRowOwnerPid tcpRowOwnerPid = (TcpRowOwnerPid)Marshal.PtrToStructure(new IntPtr(toInt64Offset), typeof(TcpRowOwnerPid));
					int combinedMask = (int)(((tcpRowOwnerPid.LocalPort & 0xFF) << 8) | ((tcpRowOwnerPid.LocalPort >> 8) & 0xFF));
					if (combinedMask == port)
					{
						pid = (int)tcpRowOwnerPid.OwningPid;
						return pid > 0;
					}
					processedCount++;
					toInt64Offset += sizeOfResult;
				}
			}
			catch
			{
				return false;
			}
			finally
			{
				Marshal.FreeHGlobal(intPtr);
			}
			return false;
		}
	}
}
