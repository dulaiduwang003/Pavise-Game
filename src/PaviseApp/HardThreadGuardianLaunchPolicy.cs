using System;
using System.IO;

namespace PaviseApp
{
	internal static class HardThreadGuardianLaunchPolicy
	{
		internal static Exception Failed(int code, bool membershipKnown, bool inJob)
		{
			string codeOffset = "hidden independent guardian could not start: " + code;
			if (code != 5 || !membershipKnown || !inJob)
			{
				return new IOException(codeOffset);
			}
			return new HardThreadGuardianLaunchBlockedException(codeOffset + " (Pavise is running inside a job)");
		}

		internal static void RequireIndependent(bool membershipKnown, bool inJob)
		{
			if (!membershipKnown)
			{
				throw new IOException("independent guardian job membership could not be verified");
			}
			if (inJob)
			{
				throw new HardThreadGuardianLaunchBlockedException("spawned guardian is still inside a job; independent recovery is unavailable");
			}
		}
	}
}
