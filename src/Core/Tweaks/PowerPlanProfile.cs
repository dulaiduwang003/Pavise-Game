// 作者：bdth 2074055628@qq.com
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
            // 混合架构线程策略交给 Thread Director，不全局强制性能核。
            if (hybrid && !amdCpu) return new PowerPlanProfile(Lang.T("t.powerplanprofile.1"), true, 5, false);
            if (hybrid) return new PowerPlanProfile(Lang.T("t.powerplanprofile.2"), true, 0, false);
            if (asymCache) return new PowerPlanProfile(Lang.T("t.powerplanprofile.3"), false, 0, true);
            if (partitionTag == "symmetric-ccd") return new PowerPlanProfile(Lang.T("t.powerplanprofile.4"), false, 0, false);
            return new PowerPlanProfile(Lang.T("gfx.tab.common"), false, 0, false);
        }
    }
}
