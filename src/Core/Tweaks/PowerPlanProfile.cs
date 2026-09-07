// @author bdth 2074055628@qq.com
// 文件用途 按 CPU 类别决定竞技电源计划的差异项 纯决策不碰硬件
using System;

namespace PaviseApp
{
    internal sealed class PowerPlanProfile
    {
        public readonly string Tag;
        public readonly bool WriteHetero;
        public readonly uint HeteroSched;
        public readonly bool PreserveCoreParking;

        private PowerPlanProfile(string tag, bool hetero, uint sched, bool preserveCoreParking)
        {
            Tag = tag; WriteHetero = hetero; HeteroSched = sched;
            PreserveCoreParking = preserveCoreParking;
        }

        public bool UseArenaCoreParking(bool aggressive)
        {
            return !PreserveCoreParking;
        }

        public static PowerPlanProfile Resolve(bool amdCpu, bool hybrid, bool asymCache,
            string partitionTag)
        {
            // Intel 大小核的长短线程调度策略写 5 自动 交给 Thread Director
            //   曾写 2 偏好性能核 没有实测依据 会把系统短线程和游戏辅助线程一起挤上 P 核
            //   后台已由 EcoQoS 压到小核 不需要再用全局策略抢方向盘
            if (hybrid && !amdCpu) return new PowerPlanProfile(Lang.T("t.powerplanprofile.1"), true, 5, false);
            if (hybrid) return new PowerPlanProfile(Lang.T("t.powerplanprofile.2"), true, 0, false);
            if (asymCache) return new PowerPlanProfile(Lang.T("t.powerplanprofile.3"), false, 0, true);
            if (partitionTag == "symmetric-ccd") return new PowerPlanProfile(Lang.T("t.powerplanprofile.4"), false, 0, false);
            return new PowerPlanProfile(Lang.T("gfx.tab.common"), false, 0, false);
        }
    }
}
