namespace PaviseApp
{
	internal sealed class IsolationCpuState
	{
		public ulong All;

		public ulong Allocated;

		public ulong Admitted;

		public ulong[] Physical;
	}
}
