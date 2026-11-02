using System;

namespace PaviseApp
{
	internal struct ManualMemoryCombineInformation
	{
		internal IntPtr EventHandle;

		internal UIntPtr PagesCombined;

		internal uint Flags;
	}
}
