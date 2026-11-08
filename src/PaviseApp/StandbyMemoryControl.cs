using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace PaviseApp
{
	internal sealed class StandbyMemoryControl : IStandbyMemoryControl
	{
		private sealed class WindowsStandbyMemoryNative : IStandbyMemoryNative
		{
			private sealed class PrivilegeLease : IDisposable
			{
				internal IntPtr State;

				public void Dispose()
				{
					RefuseNativeInTests();
					IntPtr state = State;
					State = IntPtr.Zero;
					if (state != IntPtr.Zero)
					{
						RtlReleasePrivilege(state);
					}
				}
			}

			public bool TryGetPerformanceInfo(out StandbyPerformanceInformation information)
			{
				RefuseNativeInTests();
				information = default(StandbyPerformanceInformation);
				information.Size = (uint)Marshal.SizeOf(typeof(StandbyPerformanceInformation));
				return GetPerformanceInfo(ref information, information.Size);
			}

			public int QueryMemoryList(out StandbyMemoryListInformation information, out int returnedBytes)
			{
				RefuseNativeInTests();
				return NtQuerySystemInformation(80, out information, Marshal.SizeOf(typeof(StandbyMemoryListInformation)), out returnedBytes);
			}

			public bool TryAcquirePurgePrivilege(out IDisposable lease)
			{
				RefuseNativeInTests();
				lease = null;
				PrivilegeLease privilegeLease = new PrivilegeLease();
				uint privileges = 13u;
				IntPtr state;
				if (RtlAcquirePrivilege(ref privileges, 1u, 0u, out state) != 0 || state == IntPtr.Zero)
				{
					return false;
				}
				privilegeLease.State = state;
				lease = privilegeLease;
				return true;
			}

			public int SetSystemInformation(int informationClass, ref int command, int length)
			{
				RefuseNativeInTests();
				if (informationClass != 80 || command != 4 || length != 4)
				{
					return -1073741811;
				}
				return NtSetSystemInformation(informationClass, ref command, length);
			}

			private static void RefuseNativeInTests()
			{
			}

			[DllImport("psapi.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			private static extern bool GetPerformanceInfo(ref StandbyPerformanceInformation information, uint size);

			[DllImport("ntdll.dll", ExactSpelling = true)]
			private static extern int NtQuerySystemInformation(int informationClass, out StandbyMemoryListInformation information, int length, out int returnedBytes);

			[DllImport("ntdll.dll", ExactSpelling = true)]
			private static extern int NtSetSystemInformation(int informationClass, ref int command, int length);

			[DllImport("ntdll.dll", ExactSpelling = true)]
			private static extern int RtlAcquirePrivilege(ref uint privileges, uint count, uint flags, out IntPtr state);

			[DllImport("ntdll.dll", ExactSpelling = true)]
			private static extern void RtlReleasePrivilege(IntPtr state);
		}

		internal const int MemoryListInformationClass = 80;

		internal const int PurgeStandbyCommand = 4;

		private const int StatusPrivilegeNotHeld = -1073741727;

		internal static readonly object OperationGate = new object();

		internal static int ManualCleanupActive;

		private readonly IStandbyMemoryNative native;

		private volatile bool unhealthy;

		public StandbyMemoryControl()
			: this(new WindowsStandbyMemoryNative())
		{
		}

		internal StandbyMemoryControl(IStandbyMemoryNative native)
		{
			if (native == null)
			{
				throw new ArgumentNullException("native");
			}
			this.native = native;
		}

		public bool TryQuery(out StandbyMemorySnapshot snapshot)
		{
			snapshot = null;
			if (unhealthy)
			{
				return false;
			}
			try
			{
				StandbyPerformanceInformation information;
				if (!native.TryGetPerformanceInfo(out information))
				{
					return false;
				}
				if (information.Size != (uint)Marshal.SizeOf(typeof(StandbyPerformanceInformation)))
				{
					return false;
				}
				ulong toUInt64Result = information.PageSize.ToUInt64();
				if (toUInt64Result == 0 || (toUInt64Result & (toUInt64Result - 1)) != 0)
				{
					return false;
				}
				StandbyMemoryListInformation information2;
				int returnedBytes;
				int memoryList = native.QueryMemoryList(out information2, out returnedBytes);
				if (memoryList < 0 || returnedBytes < 13 * IntPtr.Size || returnedBytes > Marshal.SizeOf(typeof(StandbyMemoryListInformation)) || information2.PageCountByPriority == null || information2.PageCountByPriority.Length != 8)
				{
					return false;
				}
				ulong numericValue = 0uL;
				UIntPtr[] pageCountByPriority = information2.PageCountByPriority;
				checked
				{
					foreach (UIntPtr uIntPtr in pageCountByPriority)
					{
						numericValue += uIntPtr.ToUInt64();
					}
					snapshot = new StandbyMemorySnapshot
					{
						TotalBytes = information.PhysicalTotal.ToUInt64() * toUInt64Result,
						AvailableBytes = information.PhysicalAvailable.ToUInt64() * toUInt64Result,
						FreeBytes = (information2.ZeroPageCount.ToUInt64() + information2.FreePageCount.ToUInt64()) * toUInt64Result,
						StandbyBytes = numericValue * toUInt64Result,
						ListBytes = information.SystemCache.ToUInt64() * toUInt64Result
					};
					if (StandbyCleanerEngine.ValidSnapshot(snapshot))
					{
						return true;
					}
				}
			}
			catch
			{
			}
			snapshot = null;
			return false;
		}

		public bool TryPurge(Func<bool> mayContinue, out int nativeStatus)
		{
			nativeStatus = -1073741536;
			if (Volatile.Read(ref ManualCleanupActive) != 0)
			{
				return false;
			}
			lock (OperationGate)
			{
				if (Volatile.Read(ref ManualCleanupActive) != 0)
				{
					return false;
				}
				return TryPurgeCore(mayContinue, out nativeStatus);
			}
		}

		private bool TryPurgeCore(Func<bool> mayContinue, out int nativeStatus)
		{
			if (unhealthy)
			{
				nativeStatus = -1073741823;
				return false;
			}
			nativeStatus = -1073741536;
			if (!StandbyCleanerEngine.MayContinue(mayContinue))
			{
				return false;
			}
			IDisposable lease = null;
			bool result = false;
			try
			{
				nativeStatus = -1073741727;
				if (!native.TryAcquirePurgePrivilege(out lease) || lease == null)
				{
					return false;
				}
				if (!StandbyCleanerEngine.MayContinue(mayContinue))
				{
					nativeStatus = -1073741536;
					return false;
				}
				int command = 4;
				nativeStatus = native.SetSystemInformation(80, ref command, 4);
				result = nativeStatus == 0;
			}
			catch
			{
				nativeStatus = -1073741823;
			}
			finally
			{
				if (lease != null)
				{
					try
					{
						lease.Dispose();
					}
					catch
					{
						unhealthy = true;
						nativeStatus = -1073741823;
					}
				}
			}
			return result;
		}
	}
}
