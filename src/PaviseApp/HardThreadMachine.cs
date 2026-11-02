namespace PaviseApp
{
	internal sealed class HardThreadMachine
	{
		internal HardThreadRead Read;

		internal int GroupCount;

		internal ushort Group;

		internal ulong ProcessMask;

		internal ulong[] PhysicalCores = new ulong[0];

		internal bool CpuSetsEmpty;

		internal string Detail;
	}
}
