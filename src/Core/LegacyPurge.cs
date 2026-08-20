// @author bdth 2074055628@qq.com
// 文件用途 清除本机数据 首次启动清旧版本 升级越过数据基线 以及设置页手动清除三条路径共用
using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace PaviseApp
{
    internal static class LegacyPurge
    {
        private const string DoneKey = "PurgeV180Done";
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
            foreach (RetiredFeature f in VersionMigrations.Entries)
                Step(f.Name, f.Restore, failed);
            Step("Game DVR", GameDvr.Restore, failed);
            Step("MMCSS", Mmcss.Restore, failed);
            Step(Lang.T("t.legacypurge.4"), VisualFx.Restore, failed);
            Step(Lang.T("t.legacypurge.5"), DisplayGuard.Restore, failed);
            Step(Lang.T("t.gamemodeenv.5"), DisplayAwake.Restore, failed);
            Step(Lang.T("t.legacypurge.6"), PresenceQos.Restore, failed);
            Step(Lang.T("t.legacypurge.7"), PowerOverlay.Restore, failed);
            Step(Lang.T("t.gamemodeenv.6"), GpuPowerMax.Restore, failed);
            Step(Lang.T("t.gamemodeenv.1"), DoTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.8"), SvcPause.Restore, failed);
            Step(Lang.T("t.legacypurge.9"), SvcYield.Restore, failed);
            Step(Lang.T("t.gamemodeenv.2"), WlanGuard.Restore, failed);
            Step(Lang.T("set.clock"), PlatformClockTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.10"), NetTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.11"), QuantumTweak.Restore, failed);
            Step("MPO", MpoTweak.Restore, failed);
            Step("VBS", VbsTweak.Restore, failed);
            Step("Spectre/Meltdown", SpecMitigationTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.12"), GameModeGuard.Restore, failed);
            Step(Lang.T("t.legacypurge.13"), DevicePowerTweak.Restore, failed);
            StepIf(Lang.T("set.windowedopt"), WindowedOptTweak.HasResidue, WindowedOptTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.14"), AccessibilityKeysTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.15"), HidPowerTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.16"), InputMythTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.18"), NvGlobalTweaks.Restore, failed);
            Step(Lang.T("t.legacypurge.19"), AdlxTweaks.PurgeResidue, failed);
            Step(Lang.T("t.legacypurge.20"), delegate { UploadYield.HealFromCrash(); return !UploadYield.HasResidue(); }, failed);
            Step(Lang.T("t.legacypurge.21"), MsiModeTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.22"), IfeoBoost.RestoreAll, failed);

            StepIf("HAGS", HagsTweak.HasResidue, HagsTweak.Restore, failed);
            StepIf("CFG", delegate { return CfgOffTweak.Enabled || CfgOffTweak.HasResidue(); },
                CfgOffTweak.Disable, failed);
            // 顺序即 LIFO 在役的 IrqRelocate 后写先还 退役台账后还
            // 反过来的话 退役壳刚写回的真原值 会被 IrqRelocate 那份含残留的快照重新覆盖
            StepIf(Lang.T("t.legacypurge.29"), delegate { return IrqRelocate.HasResidue; }, IrqRelocate.Revert, failed);
            StepIf(Lang.T("t.legacypurge.23"), delegate { return RetiredIrqAffinity.HasResidue; }, RetiredIrqAffinity.Disable, failed);
            StepIf(Lang.T("t.legacypurge.24"), delegate { return UsbInterruptAffinityTweak.HasResidue; }, UsbInterruptAffinityTweak.Disable, failed);

            foreach (string kind in new[]
            {
                NvDrsTweaks.KeyPState, NvDrsTweaks.KeyFrl, NvDrsTweaks.KeyPreRender,
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
            foreach (string exeKind in new[] { "gpu", "igpu", "fso" })
            {
                string k = exeKind;
                Step(k == "gpu" ? Lang.T("t.legacypurge.25") : k == "igpu" ? Lang.T("t.legacypurge.26") : Lang.T("t.legacypurge.27"), delegate
                {
                    GameExeTweaks.RestoreKind(k);
                    return !GameExeTweaks.HasKindResidue(k);
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

        public static bool HasInstallFootprint(string dataDir)
        {
            if (Settings.Load(DoneKey, false)) return true;
            string[] marks =
            {
                "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt",
                "Pavise.autoignore.txt", GameProfileStore.FileName,
                "Pavise.freeze.state", SuppressionCore.StateFileName
            };
            foreach (string name in marks)
                try { if (File.Exists(Path.Combine(dataDir, name))) return true; } catch { }
            return false;
        }

        public static void RunOnce(string dataDir)
        {
            if (Settings.Load(DoneKey, false)) return;
            if (!HasInstallFootprint(dataDir))
            {
                Settings.Save(DoneKey, true);
                Logger.Log(Lang.T("log.legacypurge.44"));
                return;
            }

            Logger.Log(Lang.T("log.legacypurge.29"));

            List<string> failed = RestoreOrHook(dataDir);
            if (failed.Count > 0)
            {
                Logger.Log(Lang.T("log.legacypurge.30") + failed.Count + Lang.T("log.legacypurge.31")
                    + string.Join(" ", failed.ToArray()) + Lang.T("log.legacypurge.32"));
                return;
            }

            int files = DeleteDataFiles(dataDir);
            bool regCleared = DeleteRegistryTree();

            Settings.Save(DoneKey, true);

            Logger.Log(Lang.T("log.legacypurge.33") + files + Lang.T("log.legacypurge.34")
                + (regCleared ? Lang.T("log.legacypurge.35") : Lang.T("log.legacypurge.36")));
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
            Settings.Save(DoneKey, true);

            Logger.Log(why + Lang.T("log.legacypurge.40") + files + Lang.T("log.legacypurge.34")
                + (includeSettings ? regCleared ? Lang.T("log.legacypurge.41") : Lang.T("log.legacypurge.42") : Lang.T("log.legacypurge.43")));
            return !includeSettings || regCleared;
        }
    }
}
