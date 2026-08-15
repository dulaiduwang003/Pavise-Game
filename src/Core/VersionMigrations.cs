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

        // 1.8.0.2 极限档下架 布局大改 明确不做任何旧配置兼容 低于此版本的配置与数据一律清除重建
        private const string DataResetBelow = "1.8.0.2";
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
            // 没盖过版本戳的一律视为旧版本 与首装无法区分 首装时清理本来就是空操作 代价只是首启多两行日志
            if (last.Length > 0 && Program.CompareVersions(last, DataResetBelow) >= 0) return true;

            int files;
            string unrestored;
            return LegacyPurge.WipeAll(dataDir, DataResetIncludesSettings,
                "升级数据重置 上个版本 " + (last.Length > 0 ? last : "未知") + " 低于数据基线 " + DataResetBelow,
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

            new RetiredFeature("Pavise 托管电源计划", "1.7.1",
                "自建一份计划再逐项写入 等于替用户改系统设置 且和显卡驱动 厂商工具的电源策略互相打架 改成只切换不改写",
                PowerPlan.HasLegacyManagedResidue, PowerPlan.PurgeLegacyManaged),

            new RetiredFeature("对局稳定后回收后台工作集", "1.7.2",
                "SetProcessWorkingSetSize 不释放内存 只把页赶去待机列表 脏页还要先写盘 这笔 IO 正好落在对局中 而被压制的后台本来就是内存管理器有压力时首批自然裁剪的对象 进程没挂起 裁完立刻缺页读回 几秒后白做",
                delegate { return HasSetting("GmBgTrim"); },
                delegate { Settings.Remove("GmBgTrim"); return true; }),

            new RetiredFeature("待机列表超阈值时清空", "1.7.2",
                "触发条件成立时清空只换回 382MB 可用内存 代价是 1380 毫秒全系统阻塞加整个文件缓存丢弃 且 5 秒一次检查没有冷却 低配机上会形成清空到读盘到再堆积的反复触发 比它想解决的微卡更糟",
                delegate { return HasSetting("GmStandbyGuard"); },
                delegate { Settings.Remove("GmStandbyGuard"); return true; }),

            new RetiredFeature("游戏时免打扰", "1.8.0.2",
                "只改 ToastEnabled 一个开关 微信 QQ 的弹窗是自绘窗口拦不住 全屏独占时系统横幅本就不抢焦点 实测没有收益",
                Notif.HasResidue, Notif.Restore),

            new RetiredFeature("刷新率守护", "1.7.0.4",
                "刷新率是持久设置 大多数机器上每局空转 中途被系统打回也无法察觉 改由体检页只读提示",
                DisplayGuard.HasResidue, DisplayGuard.Restore),

            new RetiredFeature("服务让路", "1.8.0.2",
                "引流的诊断/更新/打印类服务本就几乎不吃 CPU 挪核收益≈0 而真正的游戏核隔离后台压制系统已覆盖",
                SvcYield.HasResidue, SvcYield.Restore),

            new RetiredFeature("电源滑块最佳性能", "1.8.0.2",
                "散热受限的笔记本上把滑块拉满会更快撞温度墙 持续性能反而下降 台式机上又与高性能电源计划重复",
                delegate { return HasSetting("PowerOverlaySnap"); }, PowerOverlay.Restore),

            new RetiredFeature("暂停搜索索引与预读服务", "1.8.0.2",
                "固态硬盘上测不出差异 机械盘受众极少 停止与恢复服务本身要数秒 收益覆盖不了成本",
                SvcPause.HasResidue, SvcPause.Restore),

            new RetiredFeature("上传让位", "1.8.0.2",
                "对局中拉 PowerShell 刷 NetQos 组策略不稳定 顿挫风险大 概念虽正经但实现不可靠",
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
            "ContactAutoPopup", "NotifQuiet", "EnvFuse_notif",
            "GmPresenceQos", "EnvFuse_pqos", "GpuHighPerf",
            "GmSvcPause", "EnvFuse_svc", "EnvFuse_overlay"
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
