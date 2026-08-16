// @author bdth 2074055628@qq.com
// 文件用途 集中登记版本迁移 已移除功能的残留清理与设置默认值的一次性重置

using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class RetiredFeature
    {
        public readonly string Name;
        public readonly string RemovedIn;
        public readonly string Reason;
        private readonly Func<bool> hasResidue;
        private readonly Func<bool> restore;

        public RetiredFeature(string name, string removedIn, string reason,
            Func<bool> residue, Func<bool> restoreFunc)
        {
            Name = name;
            RemovedIn = removedIn;
            Reason = reason;
            hasResidue = residue;
            restore = restoreFunc;
        }

        public bool HasResidue()
        {
            try { return hasResidue(); }
            catch { return false; }
        }

        public bool Restore()
        {
            try { return restore(); }
            catch { return false; }
        }
    }

    internal sealed class DefaultReset
    {
        public readonly string Name;
        public readonly string ResetIn;
        public readonly string Reason;
        public readonly string SettingKey;
        public readonly bool Value;
        public readonly string DoneKey;

        public DefaultReset(string name, string resetIn, string reason,
            string settingKey, bool value, string doneKey)
        {
            Name = name;
            ResetIn = resetIn;
            Reason = reason;
            SettingKey = settingKey;
            Value = value;
            DoneKey = doneKey;
        }
    }

    internal static class VersionMigrations
    {
        private const string LastRunKey = "LastRunVersion";

        // 1.8.0.3 起收紧数据基线:检测到任何 1.8.0.3 之前版本的数据一律全清 含设置与游戏库
        private const string DataResetBelow = "1.8.0.3";
        private const bool DataResetIncludesSettings = true;

        private static readonly object lk = new object();
        private static bool settingsMigrated;
        private static string previousRun;
        private static bool previousRunRead;

        public static string PreviousRunVersion
        {
            get
            {
                lock (lk)
                {
                    if (!previousRunRead)
                    {
                        previousRun = Settings.LoadStr(LastRunKey, "");
                        previousRunRead = true;
                    }
                    return previousRun;
                }
            }
        }

        public static bool ResetDataOnUpgrade(string dataDir)
        {
            if (DataResetBelow.Length == 0) return true;
            string last = PreviousRunVersion;
            if (last.Length > 0 && Program.CompareVersions(last, DataResetBelow) >= 0) return true;
            // 无版本记录且无旧安装足迹 = 新装机器 没东西可重置 当场盖章
            // 否则首启重置一旦失败未盖章 用户当天的配置会在下一次启动被重置吞掉
            if (last.Length == 0 && !LegacyPurge.HasInstallFootprint(dataDir))
            {
                StampRunVersion();
                Logger.Log(Lang.T("log.versionmigrations.55"));
                return true;
            }

            int files;
            string unrestored;
            bool ok = LegacyPurge.WipeAll(dataDir, DataResetIncludesSettings,
                Lang.T("t.versionmigrations.1") + (last.Length > 0 ? last : Lang.T("t.versionmigrations.2")) + Lang.T("t.versionmigrations.3") + DataResetBelow,
                out files, out unrestored);
            // 升级重置属一次性尽力而为 无论成败当场盖章 绝不让下一次启动对着用户之后的新配置再来一刀
            StampRunVersion();
            return ok;
        }

        private static readonly RetiredFeature[] Retired =
        {
            new RetiredFeature(Lang.T("t.versionmigrations.4"), "1.6.6",
                Lang.T("t.versionmigrations.5"),
                Mmcss.HasResidue, Mmcss.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.6"), "1.6.6",
                Lang.T("t.versionmigrations.7"),
                FgBoost.HasResidue, FgBoost.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.8"), "1.6.8",
                Lang.T("t.versionmigrations.9"),
                NetworkAffinityTweak.HasResidue, NetworkAffinityTweak.Disable),

            new RetiredFeature(Lang.T("t.versionmigrations.10"), "1.6.8",
                Lang.T("t.versionmigrations.11"),
                NagleTweak.HasResidue, NagleTweak.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.12"), "1.6.8",
                Lang.T("t.versionmigrations.13"),
                delegate { return GameExeTweaks.HasKindResidue("fso"); },
                delegate { GameExeTweaks.RestoreKind("fso"); return true; }),

            new RetiredFeature(Lang.T("t.versionmigrations.14"), "1.7.0.1",
                Lang.T("t.versionmigrations.15"),
                delegate { return StorageAffinityTweak.HasResidue; },
                StorageAffinityTweak.Disable),

            new RetiredFeature(Lang.T("t.versionmigrations.42"), "1.8.0.3",
                Lang.T("t.versionmigrations.43"),
                delegate { return UsbInterruptAffinityTweak.HasResidue; },
                UsbInterruptAffinityTweak.Disable),

            new RetiredFeature(Lang.T("t.legacypurge.25"), "1.8.0.3",
                Lang.T("t.versionmigrations.44"),
                delegate { return GameExeTweaks.HasKindResidue("gpu"); },
                delegate { GameExeTweaks.RestoreKind("gpu"); return !GameExeTweaks.HasKindResidue("gpu"); }),

            new RetiredFeature(Lang.T("t.versionmigrations.16"), "1.7.0.5",
                Lang.T("t.versionmigrations.17"),
                delegate { return MpoTweak.DisabledByPavise; },
                MpoTweak.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.18"), "1.7.0.5",
                Lang.T("t.versionmigrations.19"),
                delegate { return GameExeTweaks.HasKindResidue("igpu"); },
                delegate { GameExeTweaks.RestoreKind("igpu"); return !GameExeTweaks.HasKindResidue("igpu"); }),

            new RetiredFeature(Lang.T("t.versionmigrations.20"), "1.7.0.6",
                Lang.T("t.versionmigrations.21"),
                VisualFx.HasResidue, VisualFx.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.22"), "1.7.1",
                Lang.T("t.versionmigrations.23"),
                NvGlobalTweaks.HasResidue, NvGlobalTweaks.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.24"), "1.7.1",
                Lang.T("t.versionmigrations.25"),
                PowerPlan.HasLegacyManagedResidue, PowerPlan.PurgeLegacyManaged),

            new RetiredFeature(Lang.T("t.versionmigrations.26"), "1.7.2",
                Lang.T("t.versionmigrations.27"),
                delegate { return HasSetting("GmBgTrim"); },
                delegate { Settings.Remove("GmBgTrim"); return true; }),

            new RetiredFeature(Lang.T("t.versionmigrations.28"), "1.7.2",
                Lang.T("t.versionmigrations.29"),
                delegate { return HasSetting("GmStandbyGuard"); },
                delegate { Settings.Remove("GmStandbyGuard"); return true; }),

            new RetiredFeature(Lang.T("t.versionmigrations.30"), "1.8.0.2",
                Lang.T("t.versionmigrations.31"),
                Notif.HasResidue, Notif.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.39"), "1.8.0.3",
                Lang.T("t.versionmigrations.41"),
                delegate { return NvDrsTweaks.HasSnapshotFor(NvDrsTweaks.KeyFrl); },
                delegate
                {
                    NvDrsTweaks.RestoreKind(NvDrsTweaks.KeyFrl);
                    return !NvDrsTweaks.HasSnapshotFor(NvDrsTweaks.KeyFrl);
                }),

            new RetiredFeature(Lang.T("t.versionmigrations.40"), "1.8.0.3",
                Lang.T("t.versionmigrations.41"),
                AdlxTweaks.HasFrameLimitResidue,
                delegate
                {
                    AdlxTweaks.RestoreFrameLimit();
                    return !AdlxTweaks.HasFrameLimitResidue();
                }),

            new RetiredFeature(Lang.T("t.legacypurge.5"), "1.7.0.4",
                Lang.T("t.versionmigrations.32"),
                DisplayGuard.HasResidue, DisplayGuard.Restore),

            new RetiredFeature(Lang.T("t.legacypurge.9"), "1.8.0.2",
                Lang.T("t.versionmigrations.33"),
                SvcYield.HasResidue, SvcYield.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.34"), "1.8.0.2",
                Lang.T("t.versionmigrations.35"),
                delegate { return HasSetting("PowerOverlaySnap"); }, PowerOverlay.Restore),

            new RetiredFeature(Lang.T("t.versionmigrations.36"), "1.8.0.2",
                Lang.T("t.versionmigrations.37"),
                SvcPause.HasResidue, SvcPause.Restore),

            new RetiredFeature(Lang.T("t.legacypurge.20"), "1.8.0.2",
                Lang.T("t.versionmigrations.38"),
                UploadYield.HasResidue, delegate { UploadYield.Clear(); return !UploadYield.HasResidue(); }),
        };

        private static bool HasSetting(string name)
        {
            return Settings.LoadStr(name, "").Length > 0;
        }

        private static readonly DefaultReset[] Resets = new DefaultReset[0];

        public static IEnumerable<RetiredFeature> Entries { get { return Retired; } }

        public static void EnsureSettingsMigrated()
        {
            if (settingsMigrated) return;
            lock (lk)
            {
                if (settingsMigrated) return;
                foreach (DefaultReset r in Resets)
                {
                    if (Settings.Load(r.DoneKey, false)) continue;
                    Settings.Save(r.SettingKey, r.Value);
                    Settings.Save(r.DoneKey, true);
                    Logger.Log("v" + r.ResetIn + Lang.T("log.versionmigrations.39") + r.Name + Lang.T("log.versionmigrations.40")
                        + (r.Value ? Lang.T("log.versionmigrations.41") : Lang.T("notes.close")));
                }
                settingsMigrated = true;
            }
        }

        private const int KeepArchivedVersions = 2;

        public static void ClearLogsOnUpgrade(string dataDir)
        {
            string last = PreviousRunVersion;
            if (last.Length == 0 || string.Equals(last, App.Version, StringComparison.OrdinalIgnoreCase)) return;
            int archived = ArchiveLogsFor(dataDir, last);
            Logger.Log(archived > 0
                ? Lang.T("log.versionmigrations.42") + App.Version + Lang.T("log.versionmigrations.43") + last + Lang.T("log.versionmigrations.44")
                    + KeepArchivedVersions + Lang.T("log.versionmigrations.45")
                : Lang.T("log.versionmigrations.42") + App.Version + Lang.T("log.versionmigrations.46"));
        }

        internal static int ArchiveLogsFor(string dataDir, string lastVersion)
        {
            if (string.IsNullOrEmpty(dataDir) || string.IsNullOrEmpty(lastVersion)) return 0;
            int archived = 0;
            foreach (string name in new[] { "Pavise.log", "crash.log" })
            {
                string src = System.IO.Path.Combine(dataDir, name);
                if (!System.IO.File.Exists(src)) continue;
                string target = System.IO.Path.Combine(dataDir,
                    System.IO.Path.GetFileNameWithoutExtension(name) + "." + lastVersion + ".log");
                try
                {
                    if (System.IO.File.Exists(target)) System.IO.File.Delete(target);
                    System.IO.File.Move(src, target);
                    archived++;
                }
                catch { try { System.IO.File.Delete(src); } catch { } }
            }
            foreach (string stale in new[] { "Pavise.log.old", "Pavise.preview.log" })
                try { System.IO.File.Delete(System.IO.Path.Combine(dataDir, stale)); } catch { }
            PruneArchivedLogs(dataDir);
            return archived;
        }

        private static void PruneArchivedLogs(string dataDir)
        {
            foreach (string stem in new[] { "Pavise", "crash" })
            {
                try
                {
                    string[] found = System.IO.Directory.GetFiles(dataDir, stem + ".*.log");
                    if (found.Length <= KeepArchivedVersions) continue;
                    Array.Sort(found, delegate(string a, string b)
                    {
                        return System.IO.File.GetLastWriteTimeUtc(b)
                            .CompareTo(System.IO.File.GetLastWriteTimeUtc(a));
                    });
                    for (int i = KeepArchivedVersions; i < found.Length; i++)
                        try { System.IO.File.Delete(found[i]); } catch { }
                }
                catch { }
            }
        }

        private static readonly string[] RetiredSettingKeys =
        {
            "TrimWS", "HzGuardOn", "EnvFuse_hz", "GmIgpuOffload", "GmMemResidency",
            "GmVisualFx", "EnvFuse_fx", "GmPrewarm", "NotesAutoPopup",
            "NvBgFrl", "EnvFuse_nvbg", "GmBgCoreMask", "ArenaPlanGuid", "UltimatePlanGuid",
            "ContactAutoPopup", "NotifQuiet", "EnvFuse_notif",
            "GmPresenceQos", "EnvFuse_pqos", "GpuHighPerf",
            "GmSvcPause", "EnvFuse_svc", "EnvFuse_overlay",
            "NvFrl", "AmdFrl", "NvFailStreak_frl", "EnvFuse_amdfrtc", "EnvFuse_corepark"
        };

        private static void PurgeRetiredSettingKeys()
        {
            int removed = 0;
            foreach (string key in RetiredSettingKeys)
            {
                if (Settings.LoadStr(key, "").Length == 0 && !Settings.Load(key, false)) continue;
                Settings.Remove(key);
                removed++;
            }
            if (removed > 0)
                Logger.Log(Lang.T("log.versionmigrations.47") + removed + Lang.T("log.versionmigrations.48"));
        }

        public static void PurgeRetired()
        {
            string last = PreviousRunVersion;
            bool upgraded = last.Length > 0
                && !string.Equals(last, App.Version, StringComparison.OrdinalIgnoreCase);

            int cleaned = 0, failed = 0;
            foreach (RetiredFeature f in Retired)
            {
                if (!f.HasResidue()) continue;
                if (f.Restore())
                {
                    cleaned++;
                    Logger.Log(Lang.T("log.versionmigrations.49") + f.RemovedIn + Lang.T("log.versionmigrations.50") + f.Name + Lang.T("log.versionmigrations.51"));
                }
                else
                {
                    failed++;
                    Logger.Log(" " + f.Name + Lang.T("log.versionmigrations.52"));
                }
            }
            if (cleaned == 0 && failed == 0 && upgraded)
                Logger.Log(Lang.T("log.versionmigrations.53") + last + " " + App.Version + Lang.T("log.versionmigrations.54"));
            PurgeRetiredSettingKeys();
        }

        public static void StampRunVersion()
        {
            Settings.SaveStr(LastRunKey, App.Version);
        }

        public static void RestoreAll()
        {
            foreach (RetiredFeature f in Retired) f.Restore();
        }
    }
}
