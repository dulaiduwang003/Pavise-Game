// 反作弊亲和性落点校验，以及已退役后台重压的原亲和性恢复。
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
            // 只有反作弊仍能使用限制范围；旧后台记录在巡检/恢复时回到原值。
            return squeezeAffinity != 0 && (reasons & SuppressReason.AntiCheat) != 0
                ? squeezeAffinity : original;
        }
    }
}
