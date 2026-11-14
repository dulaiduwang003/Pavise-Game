using System.Collections.Generic;
using System.Linq;

namespace PaviseApp
{
    // 每个阶段仅在准入时选核，后续保持目标。
    internal static class RenderIsolationPlan
    {
        // allowed 已是进程实际范围、用户游戏核心范围与其他功能预留核之差的交集。
        // 返回一个逻辑 CPU 位；返回 0 时 reason 说明不固定的原因。
        internal static ulong Choose(ulong allowed, ulong[] physicalCores, ulong performanceMask, ulong favoredMask,
            ThreadRelationMetrics presenter, out string reason)
        {
            reason = null;
            if (allowed == 0 || physicalCores == null || physicalCores.Length == 0)
            { reason = "进程范围或物理拓扑未知"; return 0; }
            ulong union = 0;
            var cores = new List<ulong>();
            foreach (ulong core in physicalCores)
            {
                if (core == 0 || (union & core) != 0) { reason = "物理核心范围为空或重叠"; return 0; }
                union |= core;
                if ((core & allowed) != 0) cores.Add(core);
            }
            if ((allowed & ~union) != 0) { reason = "授权范围包含未知物理核心"; return 0; }
            if (cores.Count == 0) { reason = "授权范围内没有可用物理核"; return 0; }
        // 优先性能核和系统首选核，同等条件避开 CPU 0，再按线程原运行分布减少迁移。
            ulong chosen = cores
                .OrderByDescending(core => performanceMask != 0 && (core & performanceMask) != 0)
                .ThenByDescending(core => favoredMask != 0 && (core & favoredMask) != 0)
                .ThenBy(core => (core & 1UL) != 0)
                .ThenByDescending(core => RunningOn(core, presenter))
                .ThenBy(core => core).First();
            ulong usable = chosen & allowed;
            var logical = new List<ulong>();
            for (int index = 0; index < 64; index++) if ((usable & (1UL << index)) != 0) logical.Add(1UL << index);
            return logical.OrderByDescending(cpu => favoredMask != 0 && (cpu & favoredMask) != 0)
                .ThenByDescending(cpu => RunningOn(cpu, presenter)).ThenBy(cpu => cpu).First();
        }

            // 清核后须至少剩两颗物理核；容量不足时仅保留固定与提优。
        internal static bool LeavesRoomForOthers(ulong allowed, ulong[] physicalCores, ulong reservedCores)
        {
            if (allowed == 0 || physicalCores == null) return false;
            return physicalCores.Count(core => (core & reservedCores) == 0 && (core & allowed) != 0) >= 2;
        }

        private static double RunningOn(ulong mask, ThreadRelationMetrics presenter)
        {
            return presenter == null ? 0 : UnifiedWorkloadPlan.RunningOn(mask, presenter);
        }
    }
}
