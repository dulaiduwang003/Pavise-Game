// @author bdth 2074055628@qq.com
// 文件用途 对局期间优化器自己让出游戏核心并降低调度权重 状态只写自身 进程消亡即自动清零无残留
using System;

namespace PaviseApp
{
    internal static class SelfYield
    {
        private static readonly object sync = new object();
        private static bool engaged;
        private static uint origPriority;
        private static int origIo = -1;
        private static uint[] origCpuSets;

        private static IntPtr Self { get { return (IntPtr)(-1); } }

        public static bool Engaged { get { lock (sync) return engaged; } }

        public static void Engage()
        {
            lock (sync)
            {
                if (engaged) return;
                IntPtr h = Self;
                uint pri = Native.GetPriorityClass(h);
                if (pri == 0) return;
                origPriority = pri;
                origIo = Native.QueryIoPriority(h);
                origCpuSets = Native.QueryCpuSets(h);
                bool moved = false;
                uint[] yieldIds = CpuTopology.BackgroundYieldCpuSetIds();
                if (yieldIds != null && yieldIds.Length > 0)
                    moved = Native.TrySetCpuSets(h, yieldIds);
                // 挪核成功就不再降优先级 看门人不能比它要抓的人低
                // 2026-08-20 台架 十线程内存带宽压力 NORMAL 优先级占满全核时
                // 挪核加降档的 Pavise 在两个核上被饿到 Sweep 超过热度采样的 30 秒时限
                // 热度只刷基线不累计 重负载反而永远升不了档 六臂全贴 103 fps 一分未收
                // 挪核本身已保证不占游戏核 后台核上以 NORMAL 平分时间片 毫秒级的 Sweep 够跑
                bool lowered = false;
                if (!moved)
                    lowered = pri == Native.BELOW_NORMAL_PRIORITY_CLASS
                        || Native.SetPriorityClass(h, Native.BELOW_NORMAL_PRIORITY_CLASS);
                bool ioLowered = origIo >= 0 && origIo != 1 && Native.TrySetIoPriority(h, 1);
                engaged = lowered || moved || ioLowered;
                if (engaged) Logger.Log(Lang.T("log.selfyield.1"));
            }
        }

        public static void Release()
        {
            lock (sync)
            {
                if (!engaged) return;
                engaged = false;
                IntPtr h = Self;
                Native.RestoreCpuSetsVerified(h, origCpuSets ?? new uint[0]);
                if (origPriority != 0) Native.SetPriorityClass(h, origPriority);
                if (origIo >= 0) Native.TrySetIoPriority(h, origIo);
                Logger.Log(Lang.T("log.selfyield.2"));
            }
        }
    }
}
