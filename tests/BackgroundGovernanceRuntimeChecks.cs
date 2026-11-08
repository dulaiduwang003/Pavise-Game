using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal static class BackgroundGovernanceRuntimeChecks
    {
        private static int checks;
        private static void Check(bool pass, string message) { checks++; if (!pass) throw new InvalidOperationException(message); }
        private static void BoostLevels()
        {
            bool saved = SuppressionCore.BackgroundBoostLimit;
            try
            {
                SuppressionCore.BackgroundBoostLimit = false;
                Check(!SuppressionCore.DisableDynamicBoost(SuppressionLevel.Isolated, false), "Normal governance must leave ordinary background boost alone");
                int state = 0, writes = 0;
                Check(SuppressionCore.ApplyBoostPolicy(SuppressionLevel.Isolated, false, 0, () => state,
                    disabled => { state = disabled ? 1 : 0; writes++; return true; }) == "" && state == 0 && writes == 0, "Normal governance must not write the boost state");
                Check(SuppressionCore.DesiredBoostDisabled(SuppressionLevel.Isolated, false, 1) == 1, "Normal governance must keep a captured disabled state");
                Check(SuppressionCore.ApplyBoostPolicy(SuppressionLevel.Isolated, false, -1, () => 0,
                    disabled => { throw new Exception("Normal governance never needs a boost baseline"); }) == "", "Normal governance must not demand a boost snapshot");
                Check(!SuppressionCore.DisableDynamicBoost(SuppressionLevel.Eco, true) && SuppressionCore.DisableDynamicBoost(SuppressionLevel.Restrained, true),
                    "Anti-cheat taming keeps its own boost rule under Normal governance");

                SuppressionCore.BackgroundBoostLimit = true;
                state = 0; writes = 0;
                Check(SuppressionCore.ApplyBoostPolicy(SuppressionLevel.Isolated, false, 0, () => state,
                    disabled => { state = disabled ? 1 : 0; writes++; return true; }) == "" && state == 1, "Aggressive governance must disable ordinary background boost");
                Check(SuppressionCore.ApplyBoostPolicy(SuppressionLevel.None, false, 0, () => state,
                    disabled => { state = disabled ? 1 : 0; writes++; return true; }) == "" && state == 0 && writes == 2, "Release must restore captured enabled state");
                Check(SuppressionCore.DesiredBoostDisabled(SuppressionLevel.None, false, 1) == 1, "Release must retain captured disabled state");
                Check(SuppressionCore.ApplyBoostPolicy(SuppressionLevel.Isolated, false, -1, () => 0,
                    disabled => { throw new Exception("Unknown baseline must prevent writes"); }) == "boost-snapshot", "Unknown original boost cannot be invented");
                Check(SuppressionCore.ApplyBoostPolicy(SuppressionLevel.Isolated, false, 0, () => 0, disabled => false) == "boost-write", "Write refusal must be reported");
                Check(SuppressionCore.ApplyBoostPolicy(SuppressionLevel.Isolated, false, 0, () => 0, disabled => true) == "boost-readback", "Successful setter without readback must fail");
                Check(!SuppressionCore.DisableDynamicBoost(SuppressionLevel.Eco, true) && SuppressionCore.DisableDynamicBoost(SuppressionLevel.Restrained, true),
                    "Anti-cheat taming keeps its own boost rule under Aggressive governance");
                Check(!SuppressionCore.DisableDynamicBoost(SuppressionLevel.Restrained, false), "Aggressive governance applies only to the background tier");
            }
            finally { SuppressionCore.BackgroundBoostLimit = saved; }
        }
        private static void NativeBoostRoundTrip()
        {
            // The fixture itself is the only native target; no Pavise/game handle
            // is opened. Even a failed restore cannot outlive this test process.
            IntPtr self = new IntPtr(-1);
            int original = Native.QueryBoostDisabled(self);
            Check(original == 0 || original == 1, "Own process boost baseline must be readable");
            bool restored = false;
            try
            {
                Check(Native.TrySetBoostDisabled(self, original == 0), "Own process boost write must succeed");
                Check(Native.QueryBoostDisabled(self) == (original == 0 ? 1 : 0), "Own process boost write must read back");
            }
            finally
            {
                for (int attempt = 0; attempt < 3; attempt++)
                    if (Native.TrySetBoostDisabled(self, original == 1) && Native.QueryBoostDisabled(self) == original) { restored = true; break; }
            }
            Check(restored, "Own process boost baseline must be restored within three attempts");
        }
        private static int Main()
        {
            try
            {
                Check(PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "1") == "1" && PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "garbage") == "0", "Governance setting must be canonical with safe default");
                Check(!BackgroundGovernancePolicy.WantsBackgroundBoostLimit(false, 1) && !BackgroundGovernancePolicy.WantsBackgroundBoostLimit(true, 0)
                    && BackgroundGovernancePolicy.WantsBackgroundBoostLimit(true, 1), "Only enabled Aggressive governance may limit background boost");
                Check(RecoveryRecordKeys.Contains(RetiredBackgroundCpuCap.ReceiptKey) && RecoveryRecordKeys.Contains(RetiredBackgroundCpuCap.LegacyReceiptKey),
                    "Retired CPU cap receipts must still be swept by reset and settings archival");
                BoostLevels(); NativeBoostRoundTrip();
                Console.WriteLine("PASS Background governance runtime: " + checks + " checks; native boost writes only to this hidden fixture; no registry/settings changes, no production app start"); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
