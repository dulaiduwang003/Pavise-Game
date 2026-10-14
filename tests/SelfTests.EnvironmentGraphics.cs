#if PAVISE_SELFTEST
using System;
using System.IO;
namespace PaviseApp
{
    internal static partial class SelfTests
    {
        private static void RunEnvironmentGraphicsTests()
        {
            Settings.UseTransientStoreForCurrentProcess();
            string current = "SwapEffectUpgradeEnable=0;Other=keep;";
            bool failWrite = false, failRead = false; int writes = 0;
            DirectXGlobalPreference.ReadForTest = delegate { if (failRead) throw new IOException(); return current; };
            DirectXGlobalPreference.WriteForTest = delegate(string value) { writes++; if (failWrite) throw new IOException(); current = value; };
            DirectXGlobalPreference.OsBuildForTest = 22621;
            try
            {
                var windowed = DirectXGlobalPreference.Windowed; var vrr = DirectXGlobalPreference.Vrr;
                bool on;
                Eq(true, windowed.Enable()); Eq(true, vrr.Enable());
                Eq("1", PrefFieldText.ReadField(current, "SwapEffectUpgradeEnable"));
                Eq("1", PrefFieldText.ReadField(current, "VRROptimizeEnable"));
                current = PrefFieldText.MergeField(current, "External", "keep");
                Eq(true, windowed.Restore());
                Eq("0", PrefFieldText.ReadField(current, "SwapEffectUpgradeEnable"));
                Eq("1", PrefFieldText.ReadField(current, "VRROptimizeEnable"));
                Eq(true, vrr.Restore()); Eq(null, PrefFieldText.ReadField(current, "VRROptimizeEnable"));
                Eq("keep", PrefFieldText.ReadField(current, "Other")); Eq("keep", PrefFieldText.ReadField(current, "External"));
                current = null; int before = writes;
                Eq(true, windowed.TryReadState(out on)); Eq(true, on);
                Eq(true, windowed.Enable()); Eq(before, writes); Eq(false, windowed.HasResidue);
                current = "VRROptimizeEnable=1;";
                Eq(true, vrr.Enable()); Eq(false, vrr.HasResidue); Eq(before, writes);
                current = ""; Eq(true, vrr.Enable());
                current = "VRROptimizeEnable=0;Other=new;";
                before = writes; Eq(true, vrr.Restore()); Eq(before, writes);
                Eq("0", PrefFieldText.ReadField(current, "VRROptimizeEnable"));
                Eq(true, vrr.Enable()); failRead = true;
                before = writes; Eq(false, vrr.Restore()); Eq(before, writes); Eq(true, vrr.HasResidue);
                failRead = false; failWrite = true;
                Eq(false, vrr.Restore()); Eq(true, vrr.HasResidue);
                failWrite = false; Eq(true, vrr.Restore());
                failWrite = true; Eq(false, vrr.Enable()); Eq(true, vrr.HasResidue);
                failWrite = false; Eq(true, vrr.Restore());
                current = null; Eq(true, vrr.Enable()); Eq(true, vrr.Restore()); Eq(null, current);
                DirectXGlobalPreference.OsBuildForTest = 17763;
                Eq(false, vrr.Enable()); Eq(false, windowed.Enable());
                DirectXGlobalPreference.OsBuildForTest = 22621;
                Settings.SuspendWritesForReset(); before = writes;
                Eq(false, vrr.Enable()); Eq(before, writes);
            }
            finally
            {
                DirectXGlobalPreference.ReadForTest = null; DirectXGlobalPreference.WriteForTest = null;
                DirectXGlobalPreference.OsBuildForTest = null; Settings.UseTransientStoreForCurrentProcess();
            }
            uint driver = 1; bool unreadable = false, deny = false;
            NvVrrWindowed.ReadForTest = delegate { return unreadable ? (uint?)null : driver; };
            NvVrrWindowed.WriteForTest = delegate(uint value) { if (deny) return false; driver = value; return true; };
            try
            {
                Eq(true, NvVrrWindowed.Applicable()); Eq(true, NvVrrWindowed.Activate()); Eq(2u, driver);
                unreadable = true; Eq(false, NvVrrWindowed.Restore()); Eq(true, NvVrrWindowed.HasResidue);
                unreadable = false; deny = true; Eq(false, NvVrrWindowed.Restore()); Eq(true, NvVrrWindowed.HasResidue);
                deny = false; Eq(true, NvVrrWindowed.Restore()); Eq(1u, driver);
                Eq(true, NvVrrWindowed.Activate()); driver = 0;
                Eq(true, NvVrrWindowed.Restore()); Eq(0u, driver);
                Eq(true, NvVrrWindowed.Activate()); Eq(false, NvVrrWindowed.HasResidue); Eq(0u, driver);
                driver = 1; deny = true;
                Eq(false, NvVrrWindowed.Activate()); Eq(true, NvVrrWindowed.HasResidue);
                deny = false; Eq(true, NvVrrWindowed.Restore()); Eq(1u, driver);
                var mode = new GameMode(Path.GetTempPath(), new SuppressionCore()); mode.InitializeAutomation();
                mode.NvVrrWindowedEnabled = true;
                foreach (string preset in new[] { "0", "1", "4", "6" })
                {
                    Settings.SaveStr(PolicyCatalog.KeyPreset, preset); mode.RefreshAutomaticPreferences();
                    Eq(true, mode.NvVrrWindowedEnabled);
                    Eq(true, mode.RefreshAutomaticNvPlan(true, true, true, "off", mode.NvVrrWindowedEnabled).WindowedVrr);
                }
                mode.NvVrrWindowedEnabled = false;
                Eq(false, mode.RefreshAutomaticNvPlan(true, true, true, "off", mode.NvVrrWindowedEnabled).WindowedVrr);
            }
            finally { NvVrrWindowed.ReadForTest = null; NvVrrWindowed.WriteForTest = null; Settings.UseTransientStoreForCurrentProcess(); }
        }
    }
}
#endif
