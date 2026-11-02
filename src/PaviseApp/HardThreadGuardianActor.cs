using System;
using System.IO;

namespace PaviseApp
{
	internal sealed class HardThreadGuardianActor
	{
		internal readonly int Pid;

		internal readonly long Creation;

		internal readonly string Path;

		internal readonly string ImageHash;

		internal HardThreadGuardianActor(int pid, long creation, string path, string imageHash)
		{
			if (pid <= 0 || creation <= 0 || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(imageHash) || imageHash.Length != 64)
			{
				throw new ArgumentException("guardian actor identity");
			}
			Pid = pid;
			Creation = creation;
			Path = System.IO.Path.GetFullPath(path);
			ImageHash = imageHash;
		}

		internal bool Same(HardThreadGuardianActor other)
		{
			if (other != null && Pid == other.Pid && Creation == other.Creation && string.Equals(Path, other.Path, StringComparison.OrdinalIgnoreCase))
			{
				return ImageHash == other.ImageHash;
			}
			return false;
		}
	}
}
