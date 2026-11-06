namespace PaviseApp
{
	internal sealed class HardThreadObserved
	{
		internal HardThreadRead Read;

		internal ushort Group;

		internal ulong Mask;

		internal bool CpuSetsEmpty;

		internal bool ThreadPriorityKnown;

		internal int ThreadPriorityLevel;

		internal string Detail;
	}
}
