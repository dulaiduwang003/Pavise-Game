#if PAVISE_SELFTEST
// 文件用途 锁住候选线程提优的本机资格判据 掌机档和低核机器一律不启用
//   用户实测掌机上开着只有 45 到 50 帧 关掉稳定 60
//   被提优的线程跑在 15 游戏其余线程留在 13 核不够时它抢占的正是自己在等的线程
//   CPU 饱和撤回那条保护指望不上 掌机常卡在 GPU 或功耗墙 整机利用率够不到 90
using System;

namespace PaviseApp
{
    internal static partial class SelfTests
    {
        internal static void RunRenderLaneEligibilityTests()
        {
            LaneNeverRunsOnHandheld();
            LaneNeedsEnoughPhysicalCores();
            LaneThresholdMatchesDocumentedValue();
        }

        // 掌机档不看核数 直接不给 掌机核多但功耗墙锁死频率 一样会撞上
        private static void LaneNeverRunsOnHandheld()
        {
            Eq(false, GameMode.LaneSupported(PerformancePreset.Handheld));
        }

        // 其余档位按物理核数判 门槛以下不启用 以上按开关走
        private static void LaneNeedsEnoughPhysicalCores()
        {
            int cores;
            try { cores = CpuTopology.PhysicalCoreCount; }
            catch { return; }

            bool expected = cores >= GameMode.LaneMinPhysicalCores;
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Standard));
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Competitive));
            Eq(expected, GameMode.LaneSupported(PerformancePreset.Custom));

        }

        // 门槛值写进界面文案 改判据时这条会先炸 提醒把文案一起改
        private static void LaneThresholdMatchesDocumentedValue()
        {
            Eq(6, GameMode.LaneMinPhysicalCores);
            string zh = Lang.T("gm.lane.unsupported");
            if (string.IsNullOrEmpty(zh) || zh == "gm.lane.unsupported")
                throw new InvalidOperationException("gm.lane.unsupported 文案缺失");
            if (zh.IndexOf("6", StringComparison.Ordinal) < 0)
                throw new InvalidOperationException("不可用文案没有写出门槛核数");
        }
    }
}
#endif
