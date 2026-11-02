namespace PaviseApp
{
	internal sealed class ProcEntry
	{
		public int Pid;

		public int ParentPid;

		public int Session = -1;

		public int Threads;

		public long Creation;

		public long Cpu;

		public ulong Io;

		public string Name;

		public string Path;
	}
}
