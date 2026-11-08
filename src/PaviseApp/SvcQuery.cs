using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class SvcQuery
	{
#pragma warning disable 0649
		private struct SERVICE_STATUS_PROCESS
		{
			public uint ServiceType;

			public uint CurrentState;

			public uint ControlsAccepted;

			public uint Win32ExitCode;

			public uint ServiceSpecificExitCode;

			public uint CheckPoint;

			public uint WaitHint;

			public uint ProcessId;

			public uint ServiceFlags;
		}

		private struct ENUM_SERVICE_STATUS_PROCESS
		{
			public IntPtr ServiceName;

			public IntPtr DisplayName;

			public SERVICE_STATUS_PROCESS Status;
		}
#pragma warning restore 0649

		private const uint SC_MANAGER_ENUMERATE_SERVICE = 4u;

		private const int SC_ENUM_PROCESS_INFO = 0;

		private const uint SERVICE_WIN32 = 48u;

		private const uint SERVICE_ACTIVE = 1u;

		private const int ERROR_MORE_DATA = 234;

		public static List<KeyValuePair<string, int>> RunningServices()
		{
			IntPtr intPtr = OpenSCManagerW(null, null, 4u);
			if (intPtr == IntPtr.Zero)
			{
				return null;
			}
			try
			{
				uint resumeHandle = 0u;
				uint bytesNeeded;
				uint servicesReturned;
				EnumServicesStatusExW(intPtr, 0, 48u, 1u, IntPtr.Zero, 0u, out bytesNeeded, out servicesReturned, ref resumeHandle, null);
				if (Marshal.GetLastWin32Error() != 234 || bytesNeeded == 0)
				{
					return null;
				}
				IntPtr intPtr2 = Marshal.AllocHGlobal((int)bytesNeeded);
				try
				{
					resumeHandle = 0u;
					if (!EnumServicesStatusExW(intPtr, 0, 48u, 1u, intPtr2, bytesNeeded, out bytesNeeded, out servicesReturned, ref resumeHandle, null))
					{
						return null;
					}
					List<KeyValuePair<string, int>> list = new List<KeyValuePair<string, int>>((int)servicesReturned);
					int num = Marshal.SizeOf(typeof(ENUM_SERVICE_STATUS_PROCESS));
					for (uint num2 = 0u; num2 < servicesReturned; num2++)
					{
						ENUM_SERVICE_STATUS_PROCESS eNUM_SERVICE_STATUS_PROCESS = (ENUM_SERVICE_STATUS_PROCESS)Marshal.PtrToStructure(new IntPtr(intPtr2.ToInt64() + num2 * num), typeof(ENUM_SERVICE_STATUS_PROCESS));
						string text = Marshal.PtrToStringUni(eNUM_SERVICE_STATUS_PROCESS.ServiceName);
						if (!string.IsNullOrEmpty(text))
						{
							list.Add(new KeyValuePair<string, int>(text, (int)eNUM_SERVICE_STATUS_PROCESS.Status.ProcessId));
						}
					}
					return list;
				}
				finally
				{
					Marshal.FreeHGlobal(intPtr2);
				}
			}
			catch
			{
				return null;
			}
			finally
			{
				CloseServiceHandle(intPtr);
			}
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr OpenSCManagerW(string machine, string db, uint access);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern bool EnumServicesStatusExW(IntPtr scm, int infoLevel, uint type, uint state, IntPtr buffer, uint bufSize, out uint bytesNeeded, out uint servicesReturned, ref uint resumeHandle, string groupName);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool CloseServiceHandle(IntPtr handle);
	}
}
