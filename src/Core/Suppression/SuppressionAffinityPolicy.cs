namespace PaviseApp
{
    internal static class SuppressionAffinityPolicy
    {
        public static ulong SqueezeTarget(ulong squeezeMask, ulong originalAffinity,
            uint[] originalCpuSets, ulong allMask, bool multiGroup)
        {
            if (multiGroup || squeezeMask == 0 || allMask == 0) return 0;
            if (originalCpuSets != null && originalCpuSets.Length > 0) return 0;
            ulong allowed = originalAffinity != 0 ? originalAffinity : allMask;
            if ((squeezeMask & allowed) != squeezeMask || squeezeMask == allowed) return 0;
            return squeezeMask;
        }

        public static ulong DesiredAffinity(SuppressReason reasons, ulong squeezeAffinity,
            ulong originalAffinity, ulong allMask)
        {
            ulong original = originalAffinity != 0 ? originalAffinity : allMask;
            if (squeezeAffinity == 0) return original;
            return (reasons & (SuppressReason.AntiCheat | SuppressReason.Background)) != 0 ? squeezeAffinity : original;
        }
    }
}
