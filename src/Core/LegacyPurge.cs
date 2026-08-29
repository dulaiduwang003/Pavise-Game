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
        internal static Func<bool> DeleteRegistryHook;
#endif

        private static List<string> RestoreOrHook(string dataDir)
        {
            try
            {
                List<string> result;
#if PAVISE_SELFTEST
                if (RestoreHook != null) result = RestoreHook();
                else
#endif
                    result = RestoreEverything(dataDir);
                return result ?? new List<string> { "系统还原未返回确认结果" };
            }
            catch (Exception ex)
            {
                return new List<string> { "系统还原异常: " + ex.GetType().Name };
            }
        }

        public static List<string> RestorePersistent(string dataDir)
        {
            return RestoreOrHook(dataDir);
        }

        private static bool DeleteRegistryTree()
        {
            try
            {
#if PAVISE_SELFTEST
                if (DeleteRegistryHook != null) return DeleteRegistryHook();
                if (SkipRegistryDelete) return true;
#endif
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(@"Software", true))
                {
                    if (parent != null)
                    {
                        bool exists;
                        // A live probe handle can keep a deleted registry key pending.
                        // Close it before deletion, then reopen separately to verify.
                        using (RegistryKey probe = parent.OpenSubKey("Pavise"))
                            exists = probe != null;
                        if (exists) parent.DeleteSubKeyTree("Pavise", false);
                    }
                }
                using (RegistryKey remaining = Registry.CurrentUser.OpenSubKey(RegKey))
                    return remaining == null;
            }
            catch { return false; }
        }

        private static readonly string[] DataFiles =
        {
            IrqSessionLedger.FileName,
            "Pavise.games.txt", "Pavise.whitelist.txt", "Pavise.targets.txt",
            "Pavise.autoignore.txt", GameProfileStore.FileName, RendererObservationStore.FileName,
            "Pavise.log", "Pavise.log.old", "crash.log", "Pavise.preview.log",
            "Pavise.freeze.state", SuppressionCore.StateFileName, "backdrop.img"
        };

        private static readonly string[] AtomicDataFiles =
        {
            "Pavise.whitelist.txt", "Pavise.autoignore.txt", SuppressionCore.StateFileName
        };

        private static readonly string[] UniqueTempDataFiles =
        {
            GameProfileStore.FileName, IrqSessionLedger.FileName, RendererObservationStore.FileName
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
            StepIf(Lang.T("gm.pausesvc"), delegate { return OptionalServicePause.HasResidue; },
                OptionalServicePause.Restore, failed);
            Step(Lang.T("t.gamemodeenv.2"), WlanGuard.Restore, failed);
            StepIf(Lang.T("set.timertick"), delegate { return TimerTickTweak.OwnsState; },
                TimerTickTweak.Restore, failed);
            StepIf(Lang.T("set.gtimer"), delegate { return GlobalTimerResTweak.OwnsState; },
                GlobalTimerResTweak.Restore, failed);
            StepIf(Lang.T("set.gpupref"), delegate { return GpuPrefStage.HasResidue; },
                delegate { return GpuPrefStage.Restore() || GpuPrefStage.AbandonUnprovableForReset(); },
                failed);
            StepIf(Lang.T("set.apppref"), delegate { return AppGpuPreferences.HasResidue; },
                AppGpuPreferences.RestoreAll, failed);
            StepIf(Lang.T("set.intel.lowlatency"), delegate { return IntelGraphicsTweaks.HasResidue; },
                IntelGraphicsTweaks.Restore, failed);
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
            Step(Lang.T("t.legacypurge.19"), AdlxTweaks.PurgeResidue, failed);
            Step(Lang.T("t.legacypurge.21"), MsiModeTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.22"), IfeoBoost.RestoreAll, failed);
            Step("RenderLane", delegate
            {
                RenderLane.HealFromCrash();
                return !RenderLane.HasResidue();
            }, failed);
            Step("VramShield 显存预留未确认还原：请先退出仍在运行的游戏，再重启重试", delegate
            {
                return VramShield.HealFromCrash() && !VramShield.HasResidue();
            }, failed);

            StepIf("HAGS", HagsTweak.HasResidue, HagsTweak.Restore, failed);
            StepIf("FSO", FsoTweak.HasResidue, FsoTweak.RestoreAll, failed);
            StepIf("FTH", delegate { return FthTweak.RepairedByPavise; }, FthTweak.Restore, failed);
            Step("CFG", CfgOffTweak.RestoreAll, failed);
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
                return RestoreProcessResidue(CrashGuard.HealFromCrash,
                    delegate { SuppressionCore.HealFromCrash(journal); }, CrashGuard.HasPending,
                    delegate { return SuppressionCore.HasPendingJournalFile(journal); });
            }, failed);

            return failed;
        }

        private static bool RestoreProcessResidue(Action healBoost, Action healSuppression,
            Func<bool> boostPending, Func<bool> suppressionPending)
        {
            healBoost();
            healSuppression();
            bool boost = boostPending();
            bool suppression = suppressionPending();
            if (!boost && !suppression) return true;
            Logger.Log(Lang.T("log.legacypurge.45"));
            return false;
        }

#if PAVISE_SELFTEST
        internal static bool ProbeProcessResidueRestore(Action healBoost, Action healSuppression,
            Func<bool> boostPending, Func<bool> suppressionPending)
        {
            return RestoreProcessResidue(healBoost, healSuppression, boostPending, suppressionPending);
        }
#endif

        private static bool IsOwnedDataName(string name)
        {
            foreach (string owned in DataFiles)
                if (string.Equals(name, owned, StringComparison.OrdinalIgnoreCase)) return true;
            foreach (string owned in AtomicDataFiles)
            {
                if (string.Equals(name, owned + ".tmp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, owned + ".stale.bak", StringComparison.OrdinalIgnoreCase)) return true;
            }
            foreach (string owned in UniqueTempDataFiles)
            {
                string prefix = owned + ".";
                if (name.Length != prefix.Length + 32 + 4
                    || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                Guid ignored;
                if (Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out ignored)) return true;
            }
            // TaskHelper owns exactly this temporary startup-task XML format.
            // Do not broaden portable cleanup to user-created XML files.
            if (name.Length == 7 + 32 + 4
                && name.StartsWith("Pavise_", StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                Guid ignored;
                if (Guid.TryParseExact(name.Substring(7, 32), "N", out ignored)) return true;
            }
            return (name.StartsWith("Pavise.", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("crash.", StringComparison.OrdinalIgnoreCase))
                && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
        }

        // 只认 %AppData%\Pavise 这一个目录 别的一概不整树删
        //   便携版 Paths.Data 就是 exe 所在目录 递归删会连 Pavise.exe 一起删掉
        //   所以不问"是不是 Paths.Data" 只问"是不是恰好等于那个路径"
        //   目录本身是重解析点也不碰 junction 会把删除带到别处去
        internal static bool IsRoamingDataDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return false;
            try
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(roaming)) return false;
                if (!Same(Path.Combine(roaming, "Pavise"), dir)) return false;
                if (!Directory.Exists(dir)) return false;
                return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) == 0;
            }
            catch { return false; }
        }

        private static bool Same(string a, string b)
        {
            string x = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string y = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryAttributes(string path, out FileAttributes attributes,
            out bool exists, out string error)
        {
            attributes = 0;
            exists = false;
            error = null;
            try
            {
                attributes = File.GetAttributes(path);
                exists = true;
                return true;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex)
            {
                error = path + " (" + ex.GetType().Name + ")";
                return false;
            }
        }

        private static bool TryDataRoot(string dir, out string root, out bool exists, out string error)
        {
            root = null;
            exists = false;
            error = null;
            try
            {
                bool absoluteDrive = !string.IsNullOrEmpty(dir) && dir.Length >= 3 && dir[1] == ':'
                    && (dir[2] == Path.DirectorySeparatorChar || dir[2] == Path.AltDirectorySeparatorChar);
                bool absoluteUnc = !string.IsNullOrEmpty(dir) && dir.StartsWith(@"\\", StringComparison.Ordinal);
                if (string.IsNullOrWhiteSpace(dir) || (!absoluteDrive && !absoluteUnc))
                {
                    error = "数据目录不是明确的绝对路径";
                    return false;
                }
                root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Same(root, Path.GetPathRoot(root)))
                {
                    error = "拒绝清理磁盘根目录";
                    return false;
                }
                FileAttributes attributes;
                if (!TryAttributes(root, out attributes, out exists, out error)) return false;
                if (!exists) return true;
                if ((attributes & FileAttributes.Directory) == 0)
                {
                    error = "数据路径不是目录: " + root;
                    return false;
                }
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    error = "拒绝清理重解析点数据目录: " + root;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = "数据路径校验失败: " + ex.GetType().Name;
                return false;
            }
        }

        private static void AddFailure(List<string> failures, string detail)
        {
            if (failures.Count < 8) failures.Add(detail);
            else if (failures.Count == 8) failures.Add("另有未能清理的项目");
        }

        // Verify every parent beneath the accepted root before touching a child.
        // Enumeration is one level at a time; directory links are never followed.
        private static bool CheckChildParents(string root, string path, out string error)
        {
            error = null;
            try
            {
                string full = Path.GetFullPath(path);
                if (!Same(full, root)
                    && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    error = "拒绝越界路径: " + path;
                    return false;
                }
                string current = Same(full, root) ? root : Path.GetDirectoryName(full);
                while (!string.IsNullOrEmpty(current))
                {
                    FileAttributes attributes;
                    bool exists;
                    if (!TryAttributes(current, out attributes, out exists, out error)) return false;
                    if (exists && ((attributes & FileAttributes.Directory) == 0
                        || (attributes & FileAttributes.ReparsePoint) != 0))
                    {
                        error = "数据目录结构已改变: " + current;
                        return false;
                    }
                    if (Same(current, root)) return true;
                    current = Path.GetDirectoryName(current);
                }
                error = "无法核对数据目录边界: " + path;
                return false;
            }
            catch (Exception ex)
            {
                error = "数据目录边界校验失败: " + ex.GetType().Name;
                return false;
            }
        }

        private static void DeleteDataFile(string root, string path, ref int files, List<string> failures)
        {
            string error;
            if (!CheckChildParents(root, path, out error)) { AddFailure(failures, error); return; }
            FileAttributes attributes;
            bool exists;
            if (!TryAttributes(path, out attributes, out exists, out error)) { AddFailure(failures, error); return; }
            if (!exists) return;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                AddFailure(failures, "预期文件却发现目录，已保留: " + path);
                return;
            }
            try
            {
                // Setting attributes through a file symlink could modify its target.
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                File.Delete(path);
            }
            catch (Exception ex) { AddFailure(failures, path + " (" + ex.GetType().Name + ")"); return; }
            if (!TryAttributes(path, out attributes, out exists, out error)) AddFailure(failures, error);
            else if (exists) AddFailure(failures, "文件仍存在: " + path);
            else files++;
        }

        private static bool DeleteDataFiles(string root, out int files, out string error)
        {
            files = 0;
            error = null;
            var failures = new List<string>();
            string normalized;
            bool exists;
            if (!TryDataRoot(root, out normalized, out exists, out error)) return false;
            if (!exists) return true;
            try
            {
                foreach (string entry in Directory.GetFileSystemEntries(normalized))
                    if (IsOwnedDataName(Path.GetFileName(entry))) DeleteDataFile(normalized, entry, ref files, failures);
            }
            catch (Exception ex) { AddFailure(failures, normalized + " (" + ex.GetType().Name + ")"); }

            // Do not infer success from Delete calls. Detect locks, denied access,
            // directories using owned filenames, and concurrent recreation.
            string remainingRoot;
            string probeError;
            if (!TryDataRoot(normalized, out remainingRoot, out exists, out probeError)) AddFailure(failures, probeError);
            else if (exists)
            {
                try
                {
                    foreach (string entry in Directory.GetFileSystemEntries(remainingRoot))
                        if (IsOwnedDataName(Path.GetFileName(entry))) AddFailure(failures, "残留: " + entry);
                }
                catch (Exception ex) { AddFailure(failures, remainingRoot + " (" + ex.GetType().Name + ")"); }
            }
            error = failures.Count == 0 ? null : string.Join("; ", failures.ToArray());
            return failures.Count == 0;
        }

        // The caller still decides whether an entire directory is owned. This
        // helper rejects roots/links, and reports residuals rather than pretending
        // that a partial best-effort deletion was a reset.
        internal static bool TryDeleteDataTree(string dir, out int files, out string error)
        {
            files = 0;
            string root;
            bool exists;
            if (!TryDataRoot(dir, out root, out exists, out error)) return false;
            foreach (string protectedRoot in new[]
            {
                Path.GetTempPath(), AppDomain.CurrentDomain.BaseDirectory,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            })
            {
                if (!string.IsNullOrEmpty(protectedRoot) && Same(root, protectedRoot))
                {
                    error = "拒绝整树删除公共目录: " + root;
                    return false;
                }
            }
            if (!exists) return true;
            var failures = new List<string>();
            DeleteDataTreeLevel(root, root, ref files, failures);
            FileAttributes remainingAttributes;
            string probeError;
            if (!TryAttributes(root, out remainingAttributes, out exists, out probeError)) AddFailure(failures, probeError);
            else if (exists) AddFailure(failures, "数据目录仍存在: " + root);
            error = failures.Count == 0 ? null : string.Join("; ", failures.ToArray());
            return failures.Count == 0;
        }

        internal static int DeleteDataTree(string dir)
        {
            int files;
            string error;
            TryDeleteDataTree(dir, out files, out error);
            return files;
        }

        private static void DeleteDataTreeLevel(string root, string dir, ref int files, List<string> failures)
        {
            string error;
            if (!CheckChildParents(root, dir, out error)) { AddFailure(failures, error); return; }
            FileAttributes attributes;
            bool exists;
            if (!TryAttributes(dir, out attributes, out exists, out error)) { AddFailure(failures, error); return; }
            if (!exists) return;
            if ((attributes & FileAttributes.Directory) == 0)
            {
                AddFailure(failures, "数据目录结构已改变: " + dir);
                return;
            }
            // The root was rejected above if it is a link. Child links are only
            // removed themselves, using non-recursive deletion.
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                try
                {
                    foreach (string entry in Directory.GetFileSystemEntries(dir))
                    {
                        FileAttributes childAttributes;
                        if (!TryAttributes(entry, out childAttributes, out exists, out error))
                        { AddFailure(failures, error); continue; }
                        if (!exists) continue;
                        if ((childAttributes & FileAttributes.Directory) != 0)
                            DeleteDataTreeLevel(root, entry, ref files, failures);
                        else DeleteDataFile(root, entry, ref files, failures);
                    }
                }
                catch (Exception ex) { AddFailure(failures, dir + " (" + ex.GetType().Name + ")"); }
            }
            if (!CheckChildParents(root, dir, out error)) { AddFailure(failures, error); return; }
            if (!TryAttributes(dir, out attributes, out exists, out error)) { AddFailure(failures, error); return; }
            if (!exists) return;
            if ((attributes & FileAttributes.Directory) == 0)
            {
                AddFailure(failures, "数据目录结构已改变: " + dir);
                return;
            }
            try
            {
                // Never change attributes on a directory link's external target.
                if ((attributes & (FileAttributes.ReadOnly | FileAttributes.ReparsePoint)) == FileAttributes.ReadOnly)
                    File.SetAttributes(dir, attributes & ~FileAttributes.ReadOnly);
                Directory.Delete(dir, false);
            }
            catch (Exception ex) { AddFailure(failures, dir + " (" + ex.GetType().Name + ")"); }
        }

        public static bool WipeAll(string dataDir, bool includeSettings, string why,
            out int files, out string unrestored)
        {
            files = 0;
            unrestored = null;
            string root;
            bool exists;
            string error;
            if (!TryDataRoot(dataDir, out root, out exists, out error))
            {
                unrestored = "数据目录校验失败: " + error;
                return false;
            }
            Logger.Log(why + Lang.T("log.legacypurge.37"));

            List<string> failed = RestoreOrHook(root);
            if (failed.Count > 0)
            {
                unrestored = "系统还原未完成: " + string.Join(" ", failed.ToArray());
                Logger.Log(why + Lang.T("log.legacypurge.38") + failed.Count + Lang.T("log.legacypurge.31") + unrestored
                    + Lang.T("log.legacypurge.39"));
                return false;
            }

            bool wipeDir = includeSettings && IsRoamingDataDir(root);
            // Last log before deletion. In a portable directory even a single
            // post-delete line would recreate Pavise.log and invalidate the reset.
            Logger.Log(why + " 系统还原已确认，开始清理本机数据");
            if (includeSettings)
            {
                // Successful recovery is the last writer of persistent originals.
                // Late UI callbacks must not recreate either store after deletion.
                Settings.SuspendWritesForReset();
                Logger.SuspendWritesForReset();
            }
            bool cleared = wipeDir ? TryDeleteDataTree(root, out files, out error)
                : DeleteDataFiles(root, out files, out error);
            if (!cleared)
            {
                unrestored = "数据清理未完成，注册表设置已保留: " + error;
                return false;
            }
            if (includeSettings && !DeleteRegistryTree())
            {
                unrestored = @"注册表清理未完成: HKCU\Software\Pavise";
                return false;
            }
            return true;
        }
    }
}
