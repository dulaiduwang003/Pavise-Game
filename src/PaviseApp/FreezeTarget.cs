using System;

namespace PaviseApp
{
	internal sealed class FreezeTarget
	{
		internal int Pid;

		internal long Creation;

		internal string Path;

		internal bool Same(FreezeTarget other)
		{
			if (other != null && Pid == other.Pid && Creation == other.Creation)
			{
				return string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase);
			}
			return false;
		}
	}
}
