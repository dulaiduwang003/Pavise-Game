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
            // Multiple processor groups, empty mask, empty full set: never pin
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[0], all, true));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(0, 0, new uint[0], all, false));
            Eq(0UL, SuppressionAffinityPolicy.SqueezeTarget(squeeze, 0, new uint[0], 0, false));
            // Process already narrowed its own affinity without the placement, or the placement is its whole range, or it set CPU Sets itself
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
            // Anti-cheat entries get their placement from Tamer after suppression lands, the anti-cheat reason follows the placement too
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(
                SuppressReason.Background | SuppressReason.AntiCheat, squeeze, 0, all));
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, squeeze, 0, all));
            // No placement returns the original, an empty original returns the full set
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, 0, 0, all));
            Eq(0x0F0UL, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.AntiCheat, 0, 0x0F0, all));
            Eq(0x0F0UL, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.Background, 0, 0x0F0, all));
            // Entries with no suppression reason at all may not carry a placement
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.None, squeeze, 0, all));
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.None, 0, 0, all));

            // Background placement is honored only with hard affinity on, the sole difference between the 4-arg and 5-arg overloads
            //   When off, background placements in the old heavy-squeeze ledger are restored only, never rewritten, that's what the cases above express
            Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(
                SuppressReason.Background, squeeze, 0, all, true));
            Eq(0x0F0UL, SuppressionAffinityPolicy.DesiredAffinity(
                SuppressReason.Background, 0, 0x0F0, all, true));
            // Even when on, entries with no suppression reason get no placement
            Eq(all, SuppressionAffinityPolicy.DesiredAffinity(SuppressReason.None, squeeze, 0, all, true));
            // Anti-cheat ignores this switch, both values follow the placement
            foreach (bool allowed in new[] { false, true })
                Eq(squeeze, SuppressionAffinityPolicy.DesiredAffinity(
                    SuppressReason.AntiCheat, squeeze, 0, all, allowed));
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
                    Eq("", PolicyResolver.For(profile).ValueOf(CoreScheduling.HeavyMaskKey));
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