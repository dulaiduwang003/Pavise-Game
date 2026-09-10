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

        // 四参重载保持旧语义 纯后台条目一律回原值
        //   已退役的重压在账本里留下过后台落点 那些记录只能还原 不能照着再写一遍
        public static ulong DesiredAffinity(SuppressReason reasons, ulong squeezeAffinity,
            ulong originalAffinity, ulong allMask)
        {
            return DesiredAffinity(reasons, squeezeAffinity, originalAffinity, allMask, false);
        }

        // backgroundPinsAllowed 只有硬亲和开关打开时为真 由 SuppressionCore 按当前设置传入
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
