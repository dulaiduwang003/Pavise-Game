// @author bdth 2074055628@qq.com
// 文件用途 集中登记历史上移除的功能及其残留清理 新增废弃项只需在此加一条

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

    internal static class RetiredFeatures
    {
        private const string LastRunKey = "LastRunVersion";

        private static readonly RetiredFeature[] All =
        {
            new RetiredFeature("MMCSS 多媒体调度", "1.6.6",
                "实测帧时间 p50/p99/p99.9 全部为 1.00 倍，无可测收益",
                Mmcss.HasResidue, Mmcss.Restore),

            new RetiredFeature("前台调度稳定", "1.6.6",
                "写入值削弱前台三倍时间片，方向与目标相反",
                FgBoost.HasResidue, FgBoost.Restore),

            new RetiredFeature("MSI 模式", "1.7",
                "无法验证设备真的支持消息信号中断，写错可能导致设备不工作或无法开机",
                MsiModeTweak.HasResidue, MsiModeTweak.Restore),
        };

        public static IEnumerable<RetiredFeature> Entries { get { return All; } }

        public static void PurgeAll()
        {
            string last = Settings.LoadStr(LastRunKey, "");
            bool upgraded = last.Length > 0
                && !string.Equals(last, App.Version, StringComparison.OrdinalIgnoreCase);

            int cleaned = 0, failed = 0;
            foreach (RetiredFeature f in All)
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
            foreach (RetiredFeature f in All) f.Restore();
        }
    }
}
