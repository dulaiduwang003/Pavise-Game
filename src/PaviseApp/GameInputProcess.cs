using System;

namespace PaviseApp
{
	internal struct GameInputProcess : IEquatable<GameInputProcess>
	{
		internal readonly int Pid;

		internal readonly long Creation;

		internal bool IsValid
		{
			get
			{
				if (Pid > 0)
				{
					return Creation > 0;
				}
				return false;
			}
		}

		internal GameInputProcess(int pid, long creation)
		{
			Pid = pid;
			Creation = creation;
		}

		public bool Equals(GameInputProcess other)
		{
			if (Pid == other.Pid)
			{
				return Creation == other.Creation;
			}
			return false;
		}

		public override bool Equals(object other)
		{
			if (other is GameInputProcess)
			{
				return Equals((GameInputProcess)other);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return Pid ^ Creation.GetHashCode();
		}
	}
}
