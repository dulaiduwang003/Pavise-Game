using System;
using System.Runtime.InteropServices;

namespace PaviseApp
{
	internal struct StandbyMemoryListInformation
	{
		public UIntPtr ZeroPageCount;

		public UIntPtr FreePageCount;

		public UIntPtr ModifiedPageCount;

		public UIntPtr ModifiedNoWritePageCount;

		public UIntPtr BadPageCount;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
		public UIntPtr[] PageCountByPriority;

		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
		public UIntPtr[] RepurposedPagesByPriority;

		public UIntPtr ModifiedPageCountPageFile;
	}
}
