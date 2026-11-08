namespace PaviseApp
{
	internal static class SuppressionAffinityPolicy
	{
		public static ulong SqueezeTarget(ulong squeezeMask, ulong originalAffinity, uint[] originalCpuSets, ulong allMask, bool multiGroup)
		{
			if (multiGroup || squeezeMask == 0 || allMask == 0)
			{
				return 0uL;
			}
			if ((squeezeMask & allMask) != squeezeMask)
			{
				return 0uL;
			}
			if (originalCpuSets != null && originalCpuSets.Length > 0)
			{
				return 0uL;
			}
			ulong originalAffinitySnapshot = ((originalAffinity != 0) ? originalAffinity : allMask);
			if ((squeezeMask & originalAffinitySnapshot) != squeezeMask || squeezeMask == originalAffinitySnapshot)
			{
				return 0uL;
			}
			return squeezeMask;
		}

		public static ulong DesiredAffinity(SuppressReason reasons, ulong squeezeAffinity, ulong originalAffinity, ulong allMask)
		{
			ulong result = ((originalAffinity != 0) ? originalAffinity : allMask);
			if (squeezeAffinity == 0)
			{
				return result;
			}
			if ((reasons & (SuppressReason.AntiCheat | SuppressReason.Background)) == 0)
			{
				return result;
			}
			return squeezeAffinity;
		}
	}
}
