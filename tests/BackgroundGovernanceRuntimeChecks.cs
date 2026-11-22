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
                Check(SuppressionCore.DisableDynamicBoost(SuppressionLevel.Isolated, true), "Anti-cheat suppression keeps its own boost rule");
                Check(!SuppressionCore.DisableDynamicBoost(SuppressionLevel.Restrained, false), "Aggressive governance applies only to the background tier");
            }
            finally { SuppressionCore.BackgroundBoostLimit = saved; }
        }
        private static void NativeBoostRoundTrip()
        {
            // 原生目标仅为测试进程自身 不打开 Pavise 或游戏句柄 即使恢复失败 也不会超出测试进程生命周期

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
                Check(PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "1") == "1" && PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "2") == "2"
                    && PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "3") == "0" && PolicyCatalog.Canonical(PolicyCatalog.KeyBackgroundGovernance, "garbage") == "0",
                    "Governance setting must be canonical with safe default");
                Check(!BackgroundGovernancePolicy.WantsBackgroundBoostLimit(false, 1) && !BackgroundGovernancePolicy.WantsBackgroundBoostLimit(false, 0)
                    && BackgroundGovernancePolicy.WantsBackgroundBoostLimit(true, 0) && BackgroundGovernancePolicy.WantsBackgroundBoostLimit(true, 1)
                    && BackgroundGovernancePolicy.WantsBackgroundBoostLimit(true, 2),
                    "Every enabled governance level must limit background boost, and governance Off must not");
                Check(BackgroundGovernancePolicy.ResourceCpuRate(0, 32) == 625 && BackgroundGovernancePolicy.ResourceCpuRate(0, 8) == 2500
                    && BackgroundGovernancePolicy.ResourceCpuRate(0, 4) == 2500,
                    "Normal CPU caps must be two cores per process and at most 25% on small machines");
                // 激进档按 0.5 个核给 全机万分比随核数变 换算回去每个进程拿到的核时间相同
                Check(BackgroundGovernancePolicy.ResourceCpuRate(1, 32) == 156 && BackgroundGovernancePolicy.ResourceCpuRate(1, 12) == 417
                    && BackgroundGovernancePolicy.ResourceCpuRate(1, 4) == 1250 && BackgroundGovernancePolicy.ResourceCpuRate(1, 1) == 2500,
                    "Aggressive CPU caps must give each process 0.5 of a core on every machine size");
                Check(BackgroundGovernancePolicy.ResourceCpuRate(2, 32) == 1 && BackgroundGovernancePolicy.ResourceCpuRate(2, 4) == 1,
                    "Freeze CPU caps must stay at 0.01% of the machine");
                Check(BackgroundGovernancePolicy.ResourceIoBandwidth(0) == 50L * 1024 * 1024 && BackgroundGovernancePolicy.ResourceIoBandwidth(1) == 10L * 1024 * 1024
                    && BackgroundGovernancePolicy.ResourceIoBandwidth(2) == 1024 * 1024,
                    "Resource Job disk targets must be 50 MiB/s for Normal, 10 MiB/s for Aggressive and 1 MiB/s for Insane");
                Check(RecoveryRecordKeys.Contains(RetiredBackgroundCpuCap.ReceiptKey) && RecoveryRecordKeys.Contains(RetiredBackgroundCpuCap.LegacyReceiptKey),
                    "Retired CPU cap receipts must still be swept by reset and settings archival");
                BoostLevels(); NativeBoostRoundTrip();
                Console.WriteLine("PASS Background governance runtime: " + checks + " checks; native boost writes only to this hidden fixture; no registry/settings changes, no production app start"); return 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        }
    }
}
