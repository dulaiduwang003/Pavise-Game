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
        private static readonly object lk = new object();
        private static bool settingsMigrated;

        private static readonly RetiredFeature[] Retired =
        {
            new RetiredFeature("MMCSS 多媒体调度", "1.6.6",
                "实测无可测收益",
                Mmcss.HasResidue, Mmcss.Restore),

            new RetiredFeature("前台调度稳定", "1.6.6",
                "写入值削弱前台时间片",
                FgBoost.HasResidue, FgBoost.Restore),

            new RetiredFeature("MSI 模式", "1.7",
                "无法验证设备是否支持消息信号中断",
                MsiModeTweak.HasResidue, MsiModeTweak.Restore),

            new RetiredFeature("网卡中断亲和", "1.7",
                "无实测数据",
                NetworkAffinityTweak.HasResidue, NetworkAffinityTweak.Disable),

            new RetiredFeature("Nagle 与延迟 ACK", "1.7",
                "现代游戏实时流量走 UDP 与 Nagle 无关",
                NagleTweak.HasResidue, NagleTweak.Restore),

            new RetiredFeature("禁用全屏优化", "1.7",
                "禁用会丢失 Auto HDR 与可变刷新率",
                delegate { return GameExeTweaks.HasKindResidue("fso"); },
                delegate { GameExeTweaks.RestoreKind("fso"); return true; }),

            new RetiredFeature("AMD 驱动调优", "1.7",
                "A 卡用户实机反馈存在副作用",
                AdlxTweaks.HasResidue,
                delegate { AdlxTweaks.HealFromCrash(); return !AdlxTweaks.HasResidue(); }),
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
                    Logger.Log("v" + r.ResetIn + " 迁移：「" + r.Name + "」已重置为默认"
                        + (r.Value ? "开启" : "关闭"));
                }
                settingsMigrated = true;
            }
        }

        public static void PurgeRetired()
        {
            string last = Settings.LoadStr(LastRunKey, "");
            bool upgraded = last.Length > 0
                && !string.Equals(last, App.Version, StringComparison.OrdinalIgnoreCase);

            int cleaned = 0, failed = 0;
            foreach (RetiredFeature f in Retired)
            {
                if (!f.HasResidue()) continue;
                if (f.Restore())
                {
                    cleaned++;
                    Logger.Log("已清理 v" + f.RemovedIn + " 移除的「" + f.Name + "」残留");
                }
                else
                {
                    failed++;
                    Logger.Log("「" + f.Name + "」残留未能清理，下次启动重试");
                }
            }
            if (cleaned == 0 && failed == 0 && upgraded)
                Logger.Log("版本变化 " + last + " → " + App.Version + "，无废弃功能残留需要清理");
            Settings.SaveStr(LastRunKey, App.Version);
        }

        public static void RestoreAll()
        {
            foreach (RetiredFeature f in Retired) f.Restore();
        }
    }
}
