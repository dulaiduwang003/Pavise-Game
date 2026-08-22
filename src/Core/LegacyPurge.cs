// @author bdth 2074055628@qq.com
// 文件用途 设置页那颗清除全部按钮的实现 把所有写过的东西还原再删掉本机数据
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class LegacyPurge
    {
        private const string RegKey = @"Software\Pavise";

#if PAVISE_SELFTEST
        internal static Func<List<string>> RestoreHook;
        internal static bool SkipRegistryDelete;
#endif

        private static List<string> RestoreOrHook(string dataDir)
        {
#if PAVISE_SELFTEST
            if (RestoreHook != null) return RestoreHook();
#endif
            return RestoreEverything(dataDir);
        }

        public static List<string> RestorePersistent(string dataDir)
        {
            return RestoreOrHook(dataDir);
        }

        private static bool DeleteRegistryTree()
        {
#if PAVISE_SELFTEST
            if (SkipRegistryDelete) return true;
#endif
            try
            {
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(@"Software", true))
                    if (parent != null && parent.OpenSubKey("Pavise") != null)
                        parent.DeleteSubKeyTree("Pavise", false);
                return Registry.CurrentUser.OpenSubKey(RegKey) == null;
            }
            catch { return false; }
        }

        private static readonly string[] DataFiles =
        {
            IrqSessionLedger.FileName,
            "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt",
            "Pavise.autoignore.txt", GameProfileStore.FileName,
            "Pavise.log", "Pavise.log.old", "crash.log", "Pavise.preview.log",
            "Pavise.freeze.state", SuppressionCore.StateFileName
        };

        private static void Step(string name, Func<bool> restore, List<string> failed)
        {
            try { if (!restore()) failed.Add(name); }
            catch { failed.Add(name); }
        }

        private static void StepIf(string name, Func<bool> enabled, Func<bool> disable, List<string> failed)
        {
            try { if (enabled() && !disable()) failed.Add(name); }
            catch { failed.Add(name); }
        }

        private static List<string> RestoreEverything(string dataDir)
        {
            var failed = new List<string>();

            Step(Lang.T("t.gamemodeenv.35"), PowerPlan.Restore, failed);
            Step(Lang.T("t.legacypurge.1"), PowerPlan.RemoveCreatedPlan, failed);
            Step(Lang.T("t.legacypurge.2"), PowerPlan.RemoveManagedPlan, failed);
            StepIf(Lang.T("t.legacypurge.3"), PowerPlan.HasParkResidue, PowerPlan.RestoreParkState, failed);
            Step(Lang.T("t.gamemodeenv.3"), UpdatePause.Restore, failed);
            Step("Game DVR", GameDvr.Restore, failed);
            Step("MMCSS", Mmcss.Restore, failed);
            Step(Lang.T("t.gamemodeenv.5"), DisplayAwake.Restore, failed);
            Step(Lang.T("t.legacypurge.6"), PresenceQos.Restore, failed);
            Step(Lang.T("t.legacypurge.7"), PowerOverlay.Restore, failed);
            Step(Lang.T("t.gamemodeenv.6"), GpuPowerMax.Restore, failed);
            Step(Lang.T("t.gamemodeenv.1"), DoTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.8"), SvcPause.Restore, failed);
            Step(Lang.T("t.gamemodeenv.2"), WlanGuard.Restore, failed);
            StepIf(Lang.T("set.timertick"), delegate { return TimerTickTweak.OwnsState; },
                TimerTickTweak.Restore, failed);
            StepIf(Lang.T("set.gtimer"), delegate { return GlobalTimerResTweak.OwnsState; },
                GlobalTimerResTweak.Restore, failed);
            StepIf(Lang.T("set.gpupref"), delegate { return GpuPrefStage.HasResidue; },
                GpuPrefStage.Restore, failed);
            Step(Lang.T("t.legacypurge.10"), NetTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.11"), QuantumTweak.Restore, failed);
            Step("VBS", VbsTweak.Restore, failed);
            Step("Spectre/Meltdown", SpecMitigationTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.12"), GameModeGuard.Restore, failed);
            Step(Lang.T("t.legacypurge.13"), DevicePowerTweak.Restore, failed);
            StepIf(Lang.T("set.windowedopt"), WindowedOptTweak.HasResidue, WindowedOptTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.14"), AccessibilityKeysTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.15"), HidPowerTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.16"), InputMythTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.21"), MsiModeTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.22"), IfeoBoost.RestoreAll, failed);

            StepIf("HAGS", HagsTweak.HasResidue, HagsTweak.Restore, failed);
            StepIf("FTH", delegate { return FthTweak.RepairedByPavise; }, FthTweak.Restore, failed);
            StepIf("CFG", delegate { return CfgOffTweak.Enabled || CfgOffTweak.HasResidue(); },
                CfgOffTweak.Disable, failed);
            StepIf(Lang.T("t.legacypurge.29"), delegate { return IrqRelocate.HasResidue; }, IrqRelocate.Revert, failed);
            StepIf(Lang.T("irqpin.prio.name"), delegate { return IrqPriorityTweak.HasResidue; },
                IrqPriorityTweak.RestoreAll, failed);

            foreach (string kind in new[]
            {
                NvDrsTweaks.KeyPState, NvDrsTweaks.KeyPreRender,
                NvDrsTweaks.KeyLowLatCpl, NvDrsTweaks.KeyUllEnable, NvDrsTweaks.KeySmooth,
                NvDrsTweaks.KeyShaderCache, NvDrsTweaks.KeyAnsel, NvDrsTweaks.KeyRebarFeat,
                NvDrsTweaks.KeyRebarOpt, NvDrsTweaks.KeyRebarSize, NvDrsTweaks.KeyDlssOvr,
                NvDrsTweaks.KeyDlssPreset
            })
            {
                string k = kind;
                Step("NVIDIA Profile:" + k, delegate
                {
                    NvDrsTweaks.RestoreKind(k);
                    return !NvDrsTweaks.HasSnapshotFor(k);
                }, failed);
            }
            Step(Lang.T("t.legacypurge.28"), delegate
            {
                string journal = Path.Combine(dataDir, SuppressionCore.StateFileName);
                try { CrashGuard.HealFromCrash(); } catch { }
                try { SuppressionCore.HealFromCrash(journal); } catch { }
                if (CrashGuard.HasPending() || SuppressionCore.HasPendingJournalFile(journal))
                    Logger.Log(Lang.T("log.legacypurge.45"));
                return true;
            }, failed);

            return failed;
        }

        private static int DeleteDataFiles(string dataDir)
        {
            int files = 0;
            foreach (string name in DataFiles)
            {
                try
                {
                    string p = Path.Combine(dataDir, name);
                    if (File.Exists(p)) { File.Delete(p); files++; }
                }
                catch { }
            }
            foreach (string pattern in new[] { "Pavise.*.log", "crash.*.log" })
            {
                try
                {
                    foreach (string p in Directory.GetFiles(dataDir, pattern))
                    {
                        try { File.Delete(p); files++; } catch { }
                    }
                }
                catch { }
            }
            return files;
        }

        public static bool WipeAll(string dataDir, bool includeSettings, string why,
            out int files, out string unrestored)
        {
            files = 0;
            unrestored = null;
            Logger.Log(why + Lang.T("log.legacypurge.37"));

            List<string> failed = RestoreOrHook(dataDir);
            if (failed.Count > 0)
            {
                unrestored = string.Join(" ", failed.ToArray());
                Logger.Log(why + Lang.T("log.legacypurge.38") + failed.Count + Lang.T("log.legacypurge.31") + unrestored
                    + Lang.T("log.legacypurge.39"));
                return false;
            }

            files = DeleteDataFiles(dataDir);
            bool regCleared = includeSettings && DeleteRegistryTree();

            Logger.Log(why + Lang.T("log.legacypurge.40") + files + Lang.T("log.legacypurge.34")
                + (includeSettings ? regCleared ? Lang.T("log.legacypurge.41") : Lang.T("log.legacypurge.42") : Lang.T("log.legacypurge.43")));
            return !includeSettings || regCleared;
        }
    }
}
