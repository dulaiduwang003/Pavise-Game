using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal static class SvcState
	{
		private struct SERVICE_STATUS
		{
			public int Type;

			public int State;

			public int ControlsAccepted;

			public int Win32ExitCode;

			public int SpecificExitCode;

			public int CheckPoint;

			public int WaitHint;
		}

		public static bool StopTaken(int state)
		{
			if (state != 1)
			{
				return state == 3;
			}
			return true;
		}

		public static int Query(string name)
		{
			try
			{
				IntPtr intPtr = OpenSCManagerW(null, null, 1u);
				if (intPtr == IntPtr.Zero)
				{
					return 0;
				}
				try
				{
					IntPtr intPtr2 = OpenServiceW(intPtr, name, 4u);
					if (intPtr2 == IntPtr.Zero)
					{
						return 0;
					}
					try
					{
						SERVICE_STATUS status;
						return QueryServiceStatus(intPtr2, out status) ? status.State : 0;
					}
					finally
					{
						CloseServiceHandle(intPtr2);
					}
				}
				finally
				{
					CloseServiceHandle(intPtr);
				}
			}
			catch
			{
				return 0;
			}
		}

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr OpenSCManagerW(string machine, string db, uint access);

		[DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		private static extern IntPtr OpenServiceW(IntPtr scm, string name, uint access);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool QueryServiceStatus(IntPtr svc, out SERVICE_STATUS status);

		[DllImport("advapi32.dll", SetLastError = true)]
		private static extern bool CloseServiceHandle(IntPtr handle);
	}
}
