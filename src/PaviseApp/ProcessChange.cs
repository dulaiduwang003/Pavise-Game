namespace PaviseApp
{
	internal sealed class ProcessChange
	{
		public int Pid;

		public int ParentPid;

		public long ParentCreation;

		public int Session = -1;

		public string Name;

		public string Path;

		public long Creation;

		public long Sequence;

		public ProcessChangeKind Kind;
	}
}
