// @author bdth 2074055628@qq.com
// 文件用途 集中登记版本迁移 已移除功能的残留清理与设置默认值的一次性重置
// 白名单预置项的收敛依赖白名单加载流程 留在 GameMode 内

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

        private const string DataResetBelow = "";
        private const bool DataResetIncludesSettings = false;

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

        public static void ResetDataOnUpgrade(string dataDir)
        {
            if (DataResetBelow.Length == 0) return;
            string last = PreviousRunVersion;
            if (last.Length == 0) return;
            if (Program.CompareVersions(last, DataResetBelow) >= 0) return;

            int files;
            string unrestored;
            LegacyPurge.WipeAll(dataDir, DataResetIncludesSettings,
                "升级数据重置 上个版本 " + last + " 低于数据基线 " + DataResetBelow,
                out files, out unrestored);
        }

        private static readonly RetiredFeature[] Retired =
        {
            new RetiredFeature("MMCSS 多媒体调度", "1.6.6",
                "实测无可测收益",
                Mmcss.HasResidue, Mmcss.Restore),

            new RetiredFeature("前台调度稳定", "1.6.6",
                "写入值削弱前台时间片",
                FgBoost.HasResidue, FgBoost.Restore),

            new RetiredFeature("MSI 模式", "1.6.8",
                "无法验证设备是否支持消息信号中断",
                MsiModeTweak.HasResidue, MsiModeTweak.Restore),

            new RetiredFeature("网卡中断亲和", "1.6.8",
                "无实测数据",
                NetworkAffinityTweak.HasResidue, NetworkAffinityTweak.Disable),

            new RetiredFeature("Nagle 与延迟 ACK", "1.6.8",
                "现代游戏实时流量走 UDP 与 Nagle 无关",
                NagleTweak.HasResidue, NagleTweak.Restore),

            new RetiredFeature("禁用全屏优化", "1.6.8",
                "禁用会丢失 Auto HDR 与可变刷新率",
                delegate { return GameExeTweaks.HasKindResidue("fso"); },
                delegate { GameExeTweaks.RestoreKind("fso"); return true; }),

            new RetiredFeature("AMD 驱动调优", "1.7",
                "A 卡用户实机反馈存在副作用",
                AdlxTweaks.HasResidue,
                delegate { AdlxTweaks.HealFromCrash(); return !AdlxTweaks.HasResidue(); }),

            new RetiredFeature("硬盘中断避让", "1.7.0.1",
                "只测过 DPC 次数下降 无帧数据",
                delegate { return StorageAffinityTweak.EnabledByPavise; },
                StorageAffinityTweak.Disable),

            new RetiredFeature("禁用 MPO", "1.7.0.5",
                "只有极少数驱动与显示器组合需要 关掉它会让画面全部改走合成 且影响捕获类程序",
                delegate { return MpoTweak.DisabledByPavise; },
                MpoTweak.Restore),

            new RetiredFeature("后台赶去集显", "1.7.0.5",
                "把后台程序的显卡偏好持久写成集显 驱动录屏和视频程序被误伤 且对已在运行的进程无法生效",
                delegate { return GameExeTweaks.HasKindResidue("igpu"); },
                delegate { GameExeTweaks.RestoreKind("igpu"); return !GameExeTweaks.HasKindResidue("igpu"); }),

            new RetiredFeature("视觉效果降级", "1.7.0.6",
                "全屏游戏时桌面本就不合成 关透明与动画对帧率无可测收益 却改动了用户的系统设置",
                VisualFx.HasResidue, VisualFx.Restore),

            new RetiredFeature("后台硬限帧", "1.7.1",
                "写的是 NVIDIA 基础 Profile 的全局项 限的是失去焦点的程序 玩家切出游戏时游戏自己就被限到 20 帧 双屏和挂机玩法受影响最大",
                NvGlobalTweaks.HasResidue, NvGlobalTweaks.Restore),

            new RetiredFeature("窗口化游戏优化", "1.7.1",
                "写的是全局的 DirectX 呈现路径开关 和系统里可变刷新率与 Auto HDR 共用同一个值 收益因游戏而异且无法逐个游戏控制 交回系统设置里由用户自己决定",
                delegate { return WindowedOptTweak.EnabledByPavise; },
                WindowedOptTweak.Restore),

            new RetiredFeature("Pavise 托管电源计划", "1.7.1",
                "自建一份计划再逐项写入 等于替用户改系统设置 且和显卡驱动 厂商工具的电源策略互相打架 改成只切换不改写",
                PowerPlan.HasLegacyManagedResidue, PowerPlan.PurgeLegacyManaged),

            new RetiredFeature("刷新率守护", "1.7.0.4",
                "刷新率是持久设置 大多数机器上每局空转 中途被系统打回也无法察觉 改由体检页只读提示",
                DisplayGuard.HasResidue, DisplayGuard.Restore),
        };

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
                    Logger.Log("v" + r.ResetIn + " 迁移 " + r.Name + " 已重置为默认"
                        + (r.Value ? "开启" : "关闭"));
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
                ? "版本更新到 " + App.Version + " 上一版日志已归档为 " + last + " 保留最近 "
                    + KeepArchivedVersions + " 个版本"
                : "版本更新到 " + App.Version + " 没有需要归档的旧日志");
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
            "WindowedOptOnByPavise", "ContactAutoPopup"
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
                Logger.Log("已清除 " + removed + " 项废弃功能遗留的设置");
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
                    Logger.Log("已清理 v" + f.RemovedIn + " 移除的 " + f.Name + " 残留");
                }
                else
                {
                    failed++;
                    Logger.Log(" " + f.Name + " 残留未能清理 下次启动重试");
                }
            }
            if (cleaned == 0 && failed == 0 && upgraded)
                Logger.Log("版本变化 " + last + " " + App.Version + " 无废弃功能残留需要清理");
            PurgeRetiredSettingKeys();
            Settings.SaveStr(LastRunKey, App.Version);
        }

        public static void RestoreAll()
        {
            foreach (RetiredFeature f in Retired) f.Restore();
        }
    }
}
