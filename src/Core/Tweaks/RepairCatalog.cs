// @author bdth 2074055628@qq.com
// 文件用途 「把被教程改坏的系统值改回去」这一族的统一目录 谁能开机自动执行也记在这里
//
// 这一族的共同形态是 NeedsRepair/Repair/Restore/Describe/RepairedByPavise 五件套
// 收进目录的好处是自动执行与体检页取自同一份名单 不会一边加了另一边忘了
//
// AutoApply 的四条门槛 必须全中
//   1 目标是回到系统默认值 不是加一层优化
//   2 幂等 已经是默认值就不动
//   3 不需要重启就生效
//   4 不碰启动配置(bcdedit)与设备驱动参数 失败不会影响开机与硬件
using System;
using System.Collections.Generic;

namespace PaviseApp
{
    internal sealed class RepairTweak
    {
        public readonly string Key;
        public readonly Func<bool> NeedsRepair;
        public readonly Func<bool> Repair;
        public readonly Func<bool> Restore;
        public readonly Func<string> Describe;
        public readonly Func<bool> RepairedByPavise;
        public readonly bool AutoApply;
        public readonly string AutoSkipReason;

        public RepairTweak(string key, Func<bool> needsRepair, Func<bool> repair, Func<bool> restore,
            Func<string> describe, Func<bool> repairedByPavise, bool autoApply, string autoSkipReason)
        {
            Key = key;
            NeedsRepair = needsRepair;
            Repair = repair;
            Restore = restore;
            Describe = describe;
            RepairedByPavise = repairedByPavise;
            AutoApply = autoApply;
            AutoSkipReason = autoSkipReason;
        }
    }

    internal static class RepairCatalog
    {
        private static readonly RepairTweak[] Items =
        {
            new RepairTweak("net",
                delegate { return NetTweak.NeedsRepair(); },
                delegate { return NetTweak.Repair(); },
                delegate { return NetTweak.Restore(); },
                delegate { return NetTweak.Describe(); },
                delegate { return NetTweak.RepairedByPavise; },
                true, null),

            new RepairTweak("inputmyth",
                delegate { return InputMythTweak.NeedsRepair(); },
                delegate { return InputMythTweak.Repair(); },
                delegate { return InputMythTweak.Restore(); },
                delegate { return InputMythTweak.Describe(); },
                delegate { return InputMythTweak.RepairedByPavise; },
                true, null),

            // 只动 Win32PrioritySeparation 有文档的低两位 取到 Maximum 即桌面默认语义
            // 立即生效 不碰启动配置 高四位原样保留
            new RepairTweak("quantum",
                delegate { return QuantumTweak.NeedsRepair(); },
                delegate { return QuantumTweak.Repair(); },
                delegate { return QuantumTweak.Restore(); },
                delegate { return QuantumTweak.Describe(); },
                delegate { return QuantumTweak.RepairedByPavise; },
                true, null),

            // 违反门槛 4：改的是 BCD 启动配置 失败会牵连开机
            new RepairTweak("platformclock",
                delegate { return PlatformClockTweak.NeedsRepair(); },
                delegate { return PlatformClockTweak.Repair(); },
                delegate { return PlatformClockTweak.Restore(); },
                delegate { return PlatformClockTweak.Describe(); },
                delegate { return PlatformClockTweak.RepairedByPavise; },
                false, "writes BCD"),

            // 违反门槛 1：摘掉容错堆是拿稳定性换速度的权衡 不是回默认
            new RepairTweak("fth",
                delegate { return FthTweak.NeedsRepair(); },
                delegate { return FthTweak.Repair(); },
                delegate { return FthTweak.Restore(); },
                delegate { return FthTweak.Describe(); },
                delegate { return FthTweak.RepairedByPavise; },
                false, "trade-off, not a default"),
        };

        public static IEnumerable<RepairTweak> All { get { return Items; } }

        public static List<RepairTweak> Auto()
        {
            var list = new List<RepairTweak>();
            foreach (RepairTweak t in Items) if (t.AutoApply) list.Add(t);
            return list;
        }

        public static RepairTweak Find(string key)
        {
            foreach (RepairTweak t in Items)
                if (string.Equals(t.Key, key, StringComparison.Ordinal)) return t;
            return null;
        }
    }
}
