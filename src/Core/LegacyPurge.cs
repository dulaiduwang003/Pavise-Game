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
                        // 探测用的句柄还开着 删掉的注册表键会一直挂在待删状态
                        // 先关掉再删 然后另开一个句柄去核实
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
            Step(Lang.T("gm.dwmboost"), DwmBoost.Restore, failed);
            StepIf(Lang.T("gm.rsssteer"), delegate { return RssSteer.HasResidue; },
                RssSteer.Restore, failed);
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
            StepIf(Lang.T("set.memcompress"), delegate { return MemCompressTweak.OwnsState; },
                MemCompressTweak.Restore, failed);
            StepIf(Lang.T("set.rescores"), delegate { return ReservedCoresTweak.OwnsState; },
                ReservedCoresTweak.Restore, failed);
            Step(Lang.T("extreme.card.title"), delegate { ExtremeMode.PurgeAll(); return true; }, failed);
            StepIf(Lang.T("set.gpupref"), delegate { return GpuPrefStage.HasResidue; },
                delegate { return GpuPrefStage.Restore() || GpuPrefStage.AbandonUnprovableForReset(); },
                failed);
            StepIf(Lang.T("set.apppref"), delegate { return AppGpuPreferences.HasResidue; },
                delegate { return AppGpuPreferences.RestoreAll()
                    || AppGpuPreferences.AbandonUnprovableForReset(); }, failed);
            StepIf(Lang.T("set.intel.lowlatency"), delegate { return IntelGraphicsTweaks.HasResidue; },
                delegate { return IntelGraphicsTweaks.Restore()
                    || IntelGraphicsTweaks.AbandonUnprovableForReset(); }, failed);
            Step(Lang.T("t.legacypurge.10"), NetTweak.Restore, failed);
            StepIf(Lang.T("set.nicim"), NicModerationTweak.HasResidue,
                delegate { return NicModerationTweak.Restore()
                    || NicModerationTweak.AbandonUnprovableForReset(); }, failed);
            Step(Lang.T("t.legacypurge.11"), QuantumTweak.Restore, failed);
            Step("VBS", VbsTweak.Restore, failed);
            Step("Spectre/Meltdown", SpecMitigationTweak.Restore, failed);
            Step(Lang.T("t.legacypurge.12"), GameModeGuard.Restore, failed);
            Step(Lang.T("t.legacypurge.13"), DevicePowerTweak.Restore, failed);
            StepIf(Lang.T("set.windowedopt"), WindowedOptTweak.HasResidue, WindowedOptTweak.Restore, failed);
            StepIf(Lang.T("set.vrropt"), VrrOptTweak.HasResidue, VrrOptTweak.Restore, failed);
            StepIf(Lang.T("set.eee"), EeeTweak.HasResidue, EeeTweak.Restore, failed);
            StepIf(Lang.T("set.gpuclock"), delegate { return GpuClockLock.HasResidue; }, GpuClockLock.Restore, failed);
            StepIf(Lang.T("set.nvvrr"), delegate { return NvVrrWindowed.HasResidue; }, NvVrrWindowed.Restore, failed);
            StepIf(Lang.T("set.intel.endurance"), delegate { return IntelEndurance.HasResidue; }, IntelEndurance.Restore, failed);
            StepIf(Lang.T("gm.laptopperf"), delegate { return LaptopPerfMode.HasResidue; }, LaptopPerfMode.Restore, failed);
            StepIf(Lang.T("t.linkmetric.name"), delegate { return LinkMetricTweak.RepairedByPavise; }, LinkMetricTweak.Restore, failed);
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
            Step("MemShield 内存驻留未确认还原：请先退出仍在运行的游戏，再重启重试", delegate
            {
                return MemShield.HealFromCrash() && !MemShield.HasResidue();
            }, failed);
            Step("DisplaySolo 显示拓扑未还原", delegate
            {
                if (DisplaySolo.HealFromCrash()) return !DisplaySolo.HasResidue();
                // 解析不出来或不是合法拓扑值的快照永远还原不了 重置流程里放弃这份记录
                uint topology;
                string snap = Settings.LoadStr(DisplaySolo.SnapKey, "");
                if (snap.Length != 0 && (!uint.TryParse(snap, out topology)
                        || (topology != DisplaySolo.TopologyClone
                            && topology != DisplaySolo.TopologyExtend
                            && topology != DisplaySolo.TopologyExternal)))
                    return Settings.SaveStr(DisplaySolo.SnapKey, "");
                return false;
            }, failed);

            StepIf("HAGS", HagsTweak.HasResidue, HagsTweak.Restore, failed);
            StepIf("FSO", FsoTweak.HasResidue, FsoTweak.RestoreAll, failed);
            StepIf("DPI", DpiTweak.HasResidue, DpiTweak.RestoreAll, failed);
            StepIf(Lang.T("gm.pausemaint"), delegate { return MaintenancePause.HasResidue; }, MaintenancePause.Restore, failed);
            StepIf("AMD SAM", AmdSamTweak.HasResidue, AmdSamTweak.Restore, failed);
            StepIf("FTH", delegate { return FthTweak.RepairedByPavise; }, FthTweak.Restore, failed);
            Step("CFG", CfgOffTweak.RestoreAll, failed);
            StepIf(Lang.T("t.legacypurge.29"), delegate { return IrqRelocate.HasResidue; }, IrqRelocate.Revert, failed);
            StepIf("自动中断编排 已下架的注册表钉核清退", delegate { return IrqAutoPilot.HasResidue; }, delegate
            {
                if (!IrqAutoPilot.RevertAll()) return false;
                IrqAutoPilot.ClearForReset();
                return !IrqAutoPilot.HasResidue;
            }, failed);
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
            Logger.Warn(Lang.T("log.legacypurge.45"));
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
            // TaskHelper 管的就是这一种临时启动任务 XML 格式
            // 便携模式的清理不要扩大到用户自己建的 XML
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

        // 动子项之前 先把认可根目录下的每一级父目录都核实一遍
        // 枚举一次只下一层 目录链接一律不跟进
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
                // 隔着文件符号链接改属性 可能改到它指向的目标
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

            // 不要拿 Delete 的返回值当成功 要能识别出被锁 权限拒绝
            // 占用了同名的目录 以及并发重建这几种情况
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

        // 整个目录归不归我们 仍然由调用方决定 这个辅助函数只负责
        // 拒掉根目录和链接 并如实报告残留 而不是把一次尽力而为的
        // 部分删除谎报成重置完成
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
            // 根目录如果是链接 上面已经拒掉了 子链接只删链接本身
            // 用非递归删除
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
                // 绝不修改目录链接所指向的外部目标的属性
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
            // 删除前的最后一条日志 便携目录下 删完哪怕再写一行
            // 都会把 Pavise.log 重新造出来 让这次重置作废
            Logger.Log(why + " 系统还原已确认，开始清理本机数据");
            if (includeSettings)
            {
                // 恢复成功是持久原始值的最后一个写入者
                // 迟到的界面回调不能在删除之后把这两个存储又建回来
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
