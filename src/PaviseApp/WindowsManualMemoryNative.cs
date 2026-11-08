using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal sealed class WindowsManualMemoryNative : IManualMemoryNative
	{
		private sealed class WindowsApi : IManualMemoryApi
		{
			private static void RuntimeOnly()
			{
			}

			public bool QueryPerformance(ref StandbyPerformanceInformation information, uint size)
			{
				RuntimeOnly();
				return GetPerformanceInfo(ref information, size);
			}

			public int QueryLists(out StandbyMemoryListInformation information, out int returnedBytes)
			{
				RuntimeOnly();
				return NtQuerySystemInformation(80, out information, Marshal.SizeOf(typeof(StandbyMemoryListInformation)), out returnedBytes);
			}

			public int AcquirePrivilege(ref uint privilege, out IntPtr state)
			{
				RuntimeOnly();
				return RtlAcquirePrivilege(ref privilege, 1u, 0u, out state);
			}

			public void ReleasePrivilege(IntPtr state)
			{
				RuntimeOnly();
				RtlReleasePrivilege(state);
			}

			public int MemoryCommand(int informationClass, ref int command, int size)
			{
				RuntimeOnly();
				return NtSetMemoryInformation(informationClass, ref command, size);
			}

			public bool FileCache(UIntPtr minimum, UIntPtr maximum, uint flags, out int error)
			{
				RuntimeOnly();
				bool setSystemFileCacheSizeSucceeded = SetSystemFileCacheSize(minimum, maximum, flags);
				error = ((!setSystemFileCacheSizeSucceeded) ? Marshal.GetLastWin32Error() : 0);
				return setSystemFileCacheSizeSucceeded;
			}

			public int EmptyInformation(int informationClass)
			{
				RuntimeOnly();
				return NtSetEmptyInformation(informationClass, IntPtr.Zero, 0);
			}

			public int Combine(int informationClass, ref ManualMemoryCombineInformation information, int size)
			{
				RuntimeOnly();
				return NtSetCombineInformation(informationClass, ref information, size);
			}

			[DllImport("psapi.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			private static extern bool GetPerformanceInfo(ref StandbyPerformanceInformation information, uint size);

			[DllImport("ntdll.dll", ExactSpelling = true)]
			private static extern int NtQuerySystemInformation(int informationClass, out StandbyMemoryListInformation information, int size, out int returnedBytes);

			[DllImport("ntdll.dll", ExactSpelling = true)]
			private static extern int RtlAcquirePrivilege(ref uint privileges, uint count, uint flags, out IntPtr state);

			[DllImport("ntdll.dll", ExactSpelling = true)]
			private static extern void RtlReleasePrivilege(IntPtr state);

			[DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation", ExactSpelling = true)]
			private static extern int NtSetMemoryInformation(int informationClass, ref int command, int size);

			[DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation", ExactSpelling = true)]
			private static extern int NtSetEmptyInformation(int informationClass, IntPtr information, int size);

			[DllImport("ntdll.dll", EntryPoint = "NtSetSystemInformation", ExactSpelling = true)]
			private static extern int NtSetCombineInformation(int informationClass, ref ManualMemoryCombineInformation information, int size);

			[DllImport("kernel32.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			private static extern bool SetSystemFileCacheSize(UIntPtr minimum, UIntPtr maximum, uint flags);
		}

		private const int StatusPrivilegeNotHeld = -1073741727;

		private readonly IManualMemoryApi api;
		private readonly Func<bool> gamePresent;

		internal WindowsManualMemoryNative()
			: this(new WindowsApi())
		{
		}
		internal WindowsManualMemoryNative(Func<bool> gamePresent) : this(new WindowsApi(), gamePresent) { }

		internal WindowsManualMemoryNative(IManualMemoryApi api, Func<bool> gamePresent = null)
		{
			if (api == null)
			{
				throw new ArgumentNullException("api");
			}
			this.api = api;
			this.gamePresent = gamePresent;
		}

		public bool TryQuery(out ManualMemorySnapshot snapshot)
		{
			snapshot = null;
			StandbyPerformanceInformation information = default(StandbyPerformanceInformation);
			information.Size = (uint)Marshal.SizeOf(typeof(StandbyPerformanceInformation));
			if (!api.QueryPerformance(ref information, information.Size))
			{
				return false;
			}
			ulong toUInt64Result = information.PageSize.ToUInt64();
			if (toUInt64Result == 0 || (toUInt64Result & (toUInt64Result - 1)) != 0)
			{
				return false;
			}
			checked
			{
				try
				{
					snapshot = new ManualMemorySnapshot
					{
						TotalBytes = information.PhysicalTotal.ToUInt64() * toUInt64Result,
						AvailableBytes = information.PhysicalAvailable.ToUInt64() * toUInt64Result,
						CommittedBytes = information.CommitTotal.ToUInt64() * toUInt64Result,
						CommitLimitBytes = information.CommitLimit.ToUInt64() * toUInt64Result
					};
					if (!snapshot.IsValid)
					{
						snapshot = null;
						return false;
					}
					try
					{
						StandbyMemoryListInformation information2;
						int returnedBytes;
						if (api.QueryLists(out information2, out returnedBytes) >= 0 && returnedBytes >= unchecked(13 * IntPtr.Size) && returnedBytes <= Marshal.SizeOf(typeof(StandbyMemoryListInformation)) && information2.PageCountByPriority != null && information2.PageCountByPriority.Length == 8)
						{
							ulong standbyBytesValue = 0uL;
							UIntPtr[] pageCountByPriority = information2.PageCountByPriority;
							foreach (UIntPtr uIntPtr in pageCountByPriority)
							{
								standbyBytesValue += uIntPtr.ToUInt64();
							}
							standbyBytesValue *= toUInt64Result;
							if (standbyBytesValue <= snapshot.TotalBytes)
							{
								snapshot.StandbyBytes = standbyBytesValue;
							}
						}
					}
					catch
					{
					}
					return true;
				}
				catch (OverflowException)
				{
					snapshot = null;
					return false;
				}
			}
		}

		public int Execute(ManualMemoryStep step)
		{
			if (step == ManualMemoryStep.PaviseMemory)
			{
				// No NtSetSystemInformation, working-set trim or cache flush in a game session.
				GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, false);
				return 0;
			}
			if (!Enum.IsDefined(typeof(ManualMemoryStep), step))
			{
				return -1073741811;
			}
			if (ManualMemoryCleaner.ProtectGame(gamePresent)) return ManualMemoryCleaner.StatusGameProtected;
			uint privilege = ((step == ManualMemoryStep.FileCache) ? 5u : 13u);
			IntPtr state = IntPtr.Zero;
			int acquirePrivilegeResult = api.AcquirePrivilege(ref privilege, out state);
			if (acquirePrivilegeResult < 0 || state == IntPtr.Zero)
			{
				if (state != IntPtr.Zero)
				{
					api.ReleasePrivilege(state);
				}
				if (acquirePrivilegeResult >= 0)
				{
					return -1073741727;
				}
				return acquirePrivilegeResult;
			}
			try
			{
				if (ManualMemoryCleaner.ProtectGame(gamePresent)) return ManualMemoryCleaner.StatusGameProtected;
				int executeValue;
				int command;
				switch (step)
				{
				case ManualMemoryStep.StandbyLists:
					executeValue = 4;
					goto ApplyMemoryCommand;
				case ManualMemoryStep.ModifiedPages:
					executeValue = 3;
					goto ApplyMemoryCommand;
				case ManualMemoryStep.WorkingSets:
					executeValue = 2;
					goto ApplyMemoryCommand;
				case ManualMemoryStep.FileCache:
				{
					UIntPtr uIntPtr = ((UIntPtr.Size == 8) ? new UIntPtr(ulong.MaxValue) : new UIntPtr(uint.MaxValue));
					int error;
					if (api.FileCache(uIntPtr, uIntPtr, 0u, out error))
					{
						return 0;
					}
					return (error > 0) ? (-2147024896 | (error & 0xFFFF)) : (-1073741823);
				}
				case ManualMemoryStep.RegistryCache:
					return api.EmptyInformation(155);
				case ManualMemoryStep.CombinePages:
				{
					ManualMemoryCombineInformation information = default(ManualMemoryCombineInformation);
					return api.Combine(130, ref information, Marshal.SizeOf(typeof(ManualMemoryCombineInformation)));
				}
				default:
					{
						return -1073741811;
					}
					ApplyMemoryCommand:
					command = executeValue;
					return api.MemoryCommand(80, ref command, 4);
				}
			}
			finally
			{
				api.ReleasePrivilege(state);
			}
		}
	}
}
