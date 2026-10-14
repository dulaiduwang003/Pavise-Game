#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunSuppressionAffinityTests()
        {
            SuppressionTargetRules();
            SuppressionAffinityOwners();
            SuppressionAffinityFailureDetail();
        }

        private static void SuppressionAffinityFailureDetail()
        {
            Eq(true, SuppressionCore.AffinityOnlyFailure("affinity-write,affinity-readback"));
            Eq(true, SuppressionCore.AffinityOnlyFailure("affinity-write"));
            Eq(true, SuppressionCore.AffinityOnlyFailure("affinity-readback"));
            Eq(false, SuppressionCore.AffinityOnlyFailure("affinity-write,priority-write"));
            Eq(false, SuppressionCore.AffinityOnlyFailure("affinity-restore,affinity-restore-readback"));
            Eq(false, SuppressionCore.AffinityOnlyFailure("io-write"));
            Eq(false, SuppressionCore.AffinityOnlyFailure(""));
            Eq(false, SuppressionCore.AffinityOnlyFailure(null));
        }

        private static void SuppressionTargetRules()
        {
            ulong all = 0xFFF, squeeze = 0xC00;
            Eq(squeeze, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[0], all, false));
            Eq(squeeze, SuppressionAffinityPolicy.SqueezeTarget(squeeze, all, null, all, false));
            Eq(squeeze, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0xFF0, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[0], all, true));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(0, 0, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[0], 0, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0x0F0, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0x800, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, squeeze, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[] { 3, 4 }, all, false));
        }

        private static void SuppressionAffinityOwners()
        {
            ulong all = 0xFFF, squeeze = 0xC00;
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.Background, squeeze, 0, all));
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.Background, squeeze, 0x0F0, all));
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(
                SuppressReason.Background | SuppressReason.AntiCheat, squeeze, 0, all));
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, squeeze, 0, all));
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, 0, 0, all));
            Eq(0x0F0UL, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, 0, 0x0F0, all));
            Eq(0x0F0UL, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.Background, 0, 0x0F0, all));
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.None, squeeze, 0, all));
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.None, 0, 0, all));
        }
    }
}
#endif