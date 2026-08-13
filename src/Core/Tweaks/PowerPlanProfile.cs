// @author bdth 2074055628@qq.com
// 文件用途 按 CPU 类别决定竞技电源计划的差异项 纯决策不碰硬件
// 每条差异必须有硬件层理由 给不出理由的保持通用值

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
            return aggressive && !PreserveCoreParking;
        }

        public static PowerPlanProfile Resolve(bool amdCpu, bool hybrid, bool asymCache,
            string partitionTag)
        {
            if (hybrid && !amdCpu) return new PowerPlanProfile("Intel大小核", true, 2, false);
            if (hybrid) return new PowerPlanProfile("AMD密集核", true, 0, false);
            if (asymCache) return new PowerPlanProfile("X3D大缓存", false, 0, true);
            if (partitionTag == "symmetric-ccd") return new PowerPlanProfile("双CCD", false, 0, false);
            return new PowerPlanProfile("通用", false, 0, false);
        }
    }
}
