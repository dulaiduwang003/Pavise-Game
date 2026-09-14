// Anti-cheat affinity placement validation, plus original-affinity restore for retired heavy-load background pins
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

        // Four-arg overload keeps the old semantics: pure background entries always go back to the original value
        //   Retired heavy-load pins left background placements in the ledger; those records may only be restored, never re-applied
        public static ulong DesiredAffinity(SuppressReason reasons, ulong squeezeAffinity,
            ulong originalAffinity, ulong allMask)
        {
            return DesiredAffinity(reasons, squeezeAffinity, originalAffinity, allMask, false);
        }

        // backgroundPinsAllowed is true only with the hard-affinity switch on; SuppressionCore passes it per current settings
        public static ulong DesiredAffinity(SuppressReason reasons, ulong squeezeAffinity,
            ulong originalAffinity, ulong allMask, bool backgroundPinsAllowed)
        {
            ulong original = originalAffinity != 0 ? originalAffinity : allMask;
            if (squeezeAffinity == 0) return original;
            if ((reasons & SuppressReason.AntiCheat) != 0) return squeezeAffinity;
            return backgroundPinsAllowed && (reasons & SuppressReason.Background) != 0
                ? squeezeAffinity : original;
        }
    }
}
