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
            RetiredHeavySqueezeSettings();
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
            // 多处理器组 空掩码 空全集 一律不绑
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[0], all, true));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(0, 0, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[0], 0, false));
            // 进程自己收过亲和且不含落点 或落点就是它的全部范围 或它自己设过 CPU Sets
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0x0F0, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0x800, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, squeeze, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[] { 3, 4 }, all, false));
        }

        private static void SuppressionAffinityOwners()
        {
            ulong all = 0xFFF, squeeze = 0xC00;
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.Background, squeeze, 0, all));
            Eq(0x0F0UL, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.Background, squeeze, 0x0F0, all));
            // 反作弊条目的落点由 Tamer 在压制落地后下 带反作弊原因时同样按落点走
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(
                SuppressReason.Background | SuppressReason.AntiCheat, squeeze, 0, all));
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, squeeze, 0, all));
            // 没有落点时回原值 原值为空回全集
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, 0, 0, all));
            Eq(0x0F0UL, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, 0, 0x0F0, all));
            Eq(0x0F0UL, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.Background, 0, 0x0F0, all));
            // 没有任何压制原因的条目不许带落点
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.None, squeeze, 0, all));
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.None, 0, 0, all));
        }

        private static void RetiredHeavySqueezeSettings()
        {
            Eq(null, PolicyCatalog.ItemOf(PolicyCatalog.KeyHeavySqueeze));
            Eq(null, PolicyCatalog.ItemOf(CoreScheduling.HeavyMaskKey));
            Eq(null, PolicyCatalog.Canonical(PolicyCatalog.KeyHeavySqueeze, "1"));
            Settings.UseTransientStoreForCurrentProcess();
            try
            {
                Settings.Save(PolicyCatalog.KeyHeavySqueeze, true);
                Settings.SaveStr(CoreScheduling.HeavyMaskKey, "FF");
                var profile = new GameProfile();
                profile.Overrides[PolicyCatalog.KeyHeavySqueeze] = "1";
                profile.Overrides[CoreScheduling.HeavyMaskKey] = "FF";
                Eq(false, PolicyResolver.SetOverride(profile, PolicyCatalog.KeyHeavySqueeze, "1"));
                for (int preset = 0; preset <= 4; preset++)
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, preset.ToString());
                    Eq("0", PolicyResolver.Read(profile, PolicyCatalog.KeyHeavySqueeze));
                    Eq("", PolicyResolver.Read(profile, CoreScheduling.HeavyMaskKey));
                    Eq("0", PolicyResolver.Global().ValueOf(PolicyCatalog.KeyHeavySqueeze));
                    Eq("0", PolicyResolver.For(profile).ValueOf(PolicyCatalog.KeyHeavySqueeze));
                    Eq("", PolicyResolver.For(profile).OwnValueOf(CoreScheduling.HeavyMaskKey));
                }
                PolicyResolver.Sanitize(profile);
                Eq(false, profile.Overrides.ContainsKey(PolicyCatalog.KeyHeavySqueeze));
                Eq(false, profile.Overrides.ContainsKey(CoreScheduling.HeavyMaskKey));
            }
            finally { Settings.UseTransientStoreForCurrentProcess(); }
        }
    }
}
#endif