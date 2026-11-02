#if PAVISE_SELFTEST
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunPromotionPolicyCatalogueTests()
        {
            RunAutomaticPolicyTests();
            Console.WriteLine("PASS complete Auto policy catalogue and existing mode contracts");
        }
        internal static void RunPerformancePromotionTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            var oldRequested = AutomaticPolicy.RequestedForTest;
            var oldEffective = AutomaticPolicy.EffectiveForTest;
            var oldApproved = AutomaticPolicy.ApprovedForTest;
            bool requested = false, effective = false, approved = false;
            AutomaticPolicy.RequestedForTest = delegate { return requested; };
            AutomaticPolicy.EffectiveForTest = delegate { return requested && effective; };
            AutomaticPolicy.ApprovedForTest = delegate(string key) {
                return approved && (key == PolicyCatalog.KeyBoost || key == PolicyCatalog.KeySuppress
                    || key == PolicyCatalog.KeyNvShaderCache) ? "1" : null;
            };
            try
            {
                Eq(true, AutomaticPolicy.IsRetiredInAuto(PolicyCatalog.KeyRenderLane));
                Eq(false, AutomaticPolicy.IsCandidate(PolicyCatalog.KeyThreadBoost));
                Eq(true, PolicyResolver.Global().RenderLane);
                Eq(true, PolicyResolver.Global().PauseDownloads);
                Eq(false, PolicyResolver.Global().NvMaxPerf);
                Eq(false, PolicyResolver.Global().NvShaderCacheMax);
                Eq(true, AutomaticPolicy.IsRetired(PolicyCatalog.KeyRenderLane));
                foreach (PerformancePreset mode in new[] { PerformancePreset.Standard, PerformancePreset.Competitive })
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)mode).ToString());
                    foreach (bool manualChoice in new[] { false, true })
                    {
                        Settings.Save(PolicyCatalog.KeyRenderLane, manualChoice);
                        Settings.Save(PolicyCatalog.KeyBoost, manualChoice);
                        Settings.Save(PolicyCatalog.KeySuppress, manualChoice);
                        Settings.Save(PolicyCatalog.KeyNvShaderCache, manualChoice);
                        var profile = new GameProfile { Id = "promotion-fixture", Name = "Owned promotion fixture" };
                        profile.Overrides[PolicyCatalog.KeyRenderLane] = "1";
                        requested = false; effective = approved = true;
                        CheckPromotionPolicy(profile, manualChoice, manualChoice, true);
                        requested = true; effective = false;
                        CheckPromotionPolicy(profile, false, false, false);
                        effective = true; approved = false;
                        CheckPromotionPolicy(profile, false, false, false);
                        approved = true;
                        CheckPromotionPolicy(profile, true, false, false);
                        effective = false;
                        CheckPromotionPolicy(profile, false, false, false);
                    }
                }
                Eq(true, typeof(RenderLane).GetMethod("Release") != null);
                Console.WriteLine("PASS 2223 candidate: manual default on and switchable, automatic unbenchmarked strategy refused, download default on, NVIDIA options default off");
            }
            finally
            {
                AutomaticPolicy.RequestedForTest = oldRequested;
                AutomaticPolicy.EffectiveForTest = oldEffective;
                AutomaticPolicy.ApprovedForTest = oldApproved;
                Settings.UseTransientStoreForCurrentProcess();
            }
        }
        private static void CheckPromotionPolicy(GameProfile profile, bool expectedScheduling, bool globalLane, bool profileLane)
        {
            foreach (PolicySnapshot policy in new[] { PolicyResolver.Global(), PolicyResolver.For(profile) })
            {
                Eq(policy.IsGlobal ? globalLane : profileLane, policy.RenderLane);
                Eq(expectedScheduling, policy.BoostGame);
                Eq(expectedScheduling, policy.SuppressBackground);
                Eq(expectedScheduling, policy.NvShaderCacheMax);
            }
            Eq("0", AutomaticPolicy.Resolve(PolicyCatalog.KeyRenderLane, delegate { return "1"; }));
        }
    }
}
#endif
