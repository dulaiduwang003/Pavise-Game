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
            Settings.Save(AutomaticPolicy.VersionKey, true);
            Eq(true, AutomaticPolicy.IsRetiredInAuto(PolicyCatalog.KeyRenderLane));
            Eq(false, AutomaticPolicy.IsRetired(PolicyCatalog.KeyRenderLane));
            foreach (PerformancePreset mode in new[] { (PerformancePreset)0,
                (PerformancePreset)1, (PerformancePreset)2,
                (PerformancePreset)4, (PerformancePreset)6 })
            {
                Settings.SaveStr(PolicyCatalog.KeyPreset, ((int)mode).ToString());
                foreach (string old in new[] { "0", "1" })
                {
                    Settings.SaveStr(PolicyCatalog.KeyRenderLane, old);
                    var profile = new GameProfile { Id = "promotion-fixture", Name = "Owned promotion fixture" };
                    profile.Overrides[PolicyCatalog.KeyRenderLane] = "1";
                    Eq("0", AutomaticPolicy.Resolve(PolicyCatalog.KeyRenderLane, delegate(string key) { return key == PolicyCatalog.KeyPreset ? ((int)mode).ToString() : old; }));
                    Eq(false, PolicyResolver.Global().RenderLane);
                    Eq(false, PolicyResolver.For(profile).RenderLane);
                    Eq(true, PolicyResolver.Global().BoostGame);
                    Eq(true, PolicyResolver.Global().SuppressBackground);
                    Eq(true, PolicyResolver.Global().NvShaderCacheMax);
                }
            }
            Eq(true, typeof(RenderLane).GetMethod("Release") != null);
            Console.WriteLine("PASS CPU-time candidate boost disabled in every Auto mode; base game and background scheduling retained");
        }
    }
}
#endif
