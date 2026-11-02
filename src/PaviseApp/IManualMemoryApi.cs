using System;

namespace PaviseApp
{
	internal interface IManualMemoryApi
	{
		bool QueryPerformance(ref StandbyPerformanceInformation information, uint size);

		int QueryLists(out StandbyMemoryListInformation information, out int returnedBytes);

		int AcquirePrivilege(ref uint privilege, out IntPtr state);

		void ReleasePrivilege(IntPtr state);

		int MemoryCommand(int informationClass, ref int command, int size);

		bool FileCache(UIntPtr minimum, UIntPtr maximum, uint flags, out int error);

		int EmptyInformation(int informationClass);

		int Combine(int informationClass, ref ManualMemoryCombineInformation information, int size);
	}
}
