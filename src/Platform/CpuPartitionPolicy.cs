// @author bdth 2074055628@qq.com
// 文件用途 只负责 CPU 分区决策 不读取硬件也不调用 Windows API

using System;

namespace PaviseApp
{
    internal static class CpuPartitionPolicy
    {
        public static ulong StrictMask(ulong all, ulong background, ulong performance, ulong cache)
        {
            ulong preferred = cache != 0 ? cache
                : (performance != 0 ? performance : (all & ~background));
            preferred &= all;
            return preferred != 0 ? preferred : all;
        }

        public static int BackgroundCoreCount(int physicalCoreCount)
        {
            if (physicalCoreCount <= 6) return 0;
            if (physicalCoreCount <= 10) return 1;
            return Math.Min(4, Math.Max(2, physicalCoreCount / 8));
        }
    }
}
