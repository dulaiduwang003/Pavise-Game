namespace PaviseApp
{
	internal sealed class HardThreadProcessFact
	{
		internal readonly bool Opened;

		internal readonly bool TimesKnown;

		internal readonly int Pid;

		internal readonly int Error;

		internal readonly long Birth;

		internal readonly uint Wait;

		internal HardThreadProcessFact(bool opened, int pid, long birth, bool timesKnown, uint wait, int error)
		{
			Opened = opened;
			Pid = pid;
			Birth = birth;
			TimesKnown = timesKnown;
			Wait = wait;
			Error = error;
		}
	}
}
